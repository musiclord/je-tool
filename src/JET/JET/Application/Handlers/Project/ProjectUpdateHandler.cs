using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

/// <summary>修改目前案件的顯示資料與期末財報準備日，不改名稱、查核期間或已匯出檔案。</summary>
public sealed class ProjectUpdateHandler(
    IProjectStore projectStore,
    IProjectRegistry projectRegistry,
    ProjectSession session,
    ILogger? logger = null) : IApplicationActionHandler
{
    public string Action => "project.update";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, ProjectFolderMessages.ActiveProjectMissing);
        var clientName = ReadText(payload, "clientName", "客戶名稱", document.ClientName) ?? string.Empty;
        var projectCode = ReadText(payload, "projectCode", "案件編號", document.ProjectCode) ?? string.Empty;
        var prepStartDate = document.LastAccountingPeriodDate;
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("prepStartDate", out _))
        {
            _ = ReadText(payload, "prepStartDate", "期末財報準備日", prepStartDate);
            prepStartDate = PayloadReader.GetOptionalDate(payload, "prepStartDate");
        }

        var dateChanged = !string.Equals(prepStartDate, document.LastAccountingPeriodDate, StringComparison.Ordinal);
        var effects = dateChanged ? AuditMutationEffects.For(AuditMutation.PreparationDate) : null;
        var changed = dateChanged
            || !string.Equals(clientName, document.ClientName, StringComparison.Ordinal)
            || !string.Equals(projectCode, document.ProjectCode, StringComparison.Ordinal);
        var updated = document with
        {
            ClientName = clientName,
            ProjectCode = projectCode,
            LastAccountingPeriodDate = prepStartDate
        };

        // project.json 與案件資料庫無法共用交易，所以改日期時把存檔放進清除結果的交易裡：
        // 先清除不提交，報告索引標過期、存好新設定後才提交。存檔失敗時交易回復，情境與結果都還在，重送即可；
        // 提交失敗時把舊設定寫回，不留下新日期搭配舊結果的狀態。報告索引寧可多標過期，不能漏標。
        if (dateChanged)
        {
            var settingsSaved = false;
            try
            {
                await repositories.ResultStaleStates.InvalidateForPreparationDateChangeAsync(projectId, async token =>
                {
                    await repositories.ReportStore.MarkStaleAsync(projectId, _ => true, token);
                    await projectStore.SaveAsync(updated, token);
                    settingsSaved = true;
                }, cancellationToken);
            }
            catch when (settingsSaved)
            {
                await projectStore.SaveAsync(document, CancellationToken.None);
                throw;
            }
        }
        else if (changed)
        {
            await repositories.ReportStore.MarkStaleAsync(projectId, _ => true, cancellationToken);
            await projectStore.SaveAsync(updated, cancellationToken);
        }

        if (updated.DatabaseProvider == ProjectDocument.SqlServerDatabaseProvider)
        {
            // 即使本機已是同值，也同步登記簿，讓先前同步失敗可以安全重試。
            await projectRegistry.UpdateDocumentAsync(updated, CancellationToken.None);
        }

        var mutationState = await WorkflowResultStateSupport.AfterMutationAsync(projectId, repositories.Results, repositories.ResultStaleStates,
            repositories.FilterScenarios, repositories.ReportStore, effects, logger);
        return new
        {
            project = new
            {
                projectId = updated.ProjectId,
                projectCode = updated.ProjectCode,
                clientName = updated.ClientName,
                operatorId = updated.OperatorId,
                periodStart = updated.PeriodStart,
                periodEnd = updated.PeriodEnd,
                prepStartDate = updated.LastAccountingPeriodDate,
                moneyScale = updated.MoneyScale,
                roundingMode = updated.RoundingMode,
                databaseProvider = updated.DatabaseProvider,
                rocDateEnabled = updated.RocDateEnabled,
                createdUtc = updated.CreatedUtc,
                currentStep = updated.CurrentStep
            },
            warnings = ProjectMetadataRules.GetWarnings(updated.PeriodStart, updated.PeriodEnd, prepStartDate),
            staleState = mutationState.StaleState,
            invalidatedResults = mutationState.InvalidatedResults,
            reports = mutationState.Reports,
            reportWarning = mutationState.ReportWarning
        };
    }

    private static string? ReadText(JsonElement payload, string key, string label, string? current)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(key, out var value)) return current;
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new JetActionException(JetErrorCodes.InvalidPayload, $"{label}必須填文字；要清除時請留空。", field: key);
        var text = value.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
