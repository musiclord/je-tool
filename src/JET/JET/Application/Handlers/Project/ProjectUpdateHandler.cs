using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>修改目前案件的顯示資料與期末財報準備日，不改名稱、查核期間或已匯出檔案。</summary>
public sealed class ProjectUpdateHandler(
    IProjectStore projectStore,
    IProjectRegistry projectRegistry,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "project.update";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, $"找不到案件『{projectId}』。");
        var entityName = ReadText(payload, "entityName", "客戶名稱", document.EntityName) ?? string.Empty;
        var projectCode = ReadText(payload, "projectCode", "案件編號", document.ProjectCode) ?? string.Empty;
        var lastPeriodStart = document.LastAccountingPeriodDate;
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("lastPeriodStart", out _))
        {
            _ = ReadText(payload, "lastPeriodStart", "期末財報準備日", lastPeriodStart);
            lastPeriodStart = PayloadReader.GetOptionalDate(payload, "lastPeriodStart");
        }

        var dateChanged = !string.Equals(lastPeriodStart, document.LastAccountingPeriodDate, StringComparison.Ordinal);
        var effects = dateChanged ? AuditMutationEffects.For(AuditMutation.PreparationDate) : null;
        var changed = dateChanged
            || !string.Equals(entityName, document.EntityName, StringComparison.Ordinal)
            || !string.Equals(projectCode, document.ProjectCode, StringComparison.Ordinal);
        var updated = document with
        {
            EntityName = entityName,
            ProjectCode = projectCode,
            LastAccountingPeriodDate = lastPeriodStart
        };

        // project.json 與案件資料庫無法共用交易。先使舊結果與報告索引失效，再存新設定；
        // 失敗重試仍會看到舊值並再次處理，不留下新設定搭配舊結果的狀態。
        if (dateChanged)
            await repositories.ResultStaleStates.InvalidateForPreparationDateChangeAsync(projectId, cancellationToken);
        if (changed)
        {
            await repositories.ReportArtifactStore.MarkStaleAsync(projectId, _ => true, cancellationToken);
            await projectStore.SaveAsync(updated, cancellationToken);
        }

        if (updated.DatabaseProvider == ProjectDocument.SqlServerDatabaseProvider)
        {
            // 即使本機已是同值，也同步登記簿，讓先前同步失敗可以安全重試。
            await projectRegistry.UpdateDocumentAsync(updated, CancellationToken.None);
        }

        var mutationState = await WorkflowResultStateSupport.AfterMutationAsync(projectId, repositories.RuleRuns, repositories.ResultStaleStates,
            repositories.FilterScenarios, repositories.ReportArtifactStore, effects);
        return new
        {
            project = new
            {
                projectId = updated.ProjectId,
                projectCode = updated.ProjectCode,
                entityName = updated.EntityName,
                operatorId = updated.OperatorId,
                periodStart = updated.PeriodStart,
                periodEnd = updated.PeriodEnd,
                lastPeriodStart = updated.LastAccountingPeriodDate,
                moneyScale = updated.MoneyScale,
                roundingMode = updated.RoundingMode,
                databaseProvider = updated.DatabaseProvider,
                rocDateEnabled = updated.RocDateEnabled,
                createdUtc = updated.CreatedUtc,
                currentStep = updated.CurrentStep
            },
            warnings = ProjectMetadataRules.GetWarnings(updated.PeriodStart, updated.PeriodEnd, lastPeriodStart),
            staleState = mutationState.StaleState,
            invalidatedResults = mutationState.InvalidatedResults,
            reportArtifacts = mutationState.ReportArtifacts,
            reportArtifactWarning = mutationState.ReportArtifactWarning,
            artifacts = mutationState.ReportArtifacts
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
