using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

public sealed class QueryAccountMappingPageHandler(
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "query.accountMappingPage";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var cursor = PayloadReader.GetOptionalString(payload, "cursor");
        if (PageCursor.IsMalformed(cursor))
            throw new JetActionException(JetErrorCodes.InvalidPayload, "科目清單已變更，請重新載入。");
        var search = PayloadReader.GetOptionalString(payload, "search")?.Trim();
        if (search?.Length > 400)
            throw new JetActionException(JetErrorCodes.InvalidPayload, "搜尋文字最多 400 字。");
        var categoryId = PayloadReader.GetOptionalString(payload, "categoryId")?.Trim();
        if (categoryId?.Length > 64)
            throw new JetActionException(JetErrorCodes.InvalidPayload, "分類識別無效，請重新選擇分類。");
        var request = new PageRequest(cursor, Math.Clamp(PayloadReader.GetOptionalInt(payload, "pageSize") ?? 200, 1, 200), IncludeTotalCount: string.IsNullOrEmpty(cursor));
        var page = await Task.Run(() => repositories.AccountMappingEditor.GetPageAsync(
            projectId, request, search, cancellationToken, categoryId), cancellationToken);
        return new { rows = page.Rows.Select(row => new
            { accountCode = row.AccountCode, accountName = row.AccountName, categoryId = row.CategoryId }).ToArray(),
            totalCount = page.TotalCount,
            nextCursor = page.NextCursor };
    }
}

public sealed class AccountMappingSaveHandler(
    ProjectSession session, ILogger? logger = null) : IApplicationActionHandler
{
    // 使用者 2026-10-19 在視窗內選擇題確認的句子，句型比照讀不回結果狀態時的提醒。
    internal const string SummaryUnavailableWarning =
        "變更已儲存，但畫面上的科目配對摘要沒有更新；重新開啟案件就會更新，不必重做變更。";

    public string Action => "accountMapping.save";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        if (!payload.TryGetProperty("changes", out var array) || array.ValueKind != JsonValueKind.Array
            || array.GetArrayLength() is < 1 or > 500)
            throw new JetActionException(JetErrorCodes.InvalidPayload, "每次可儲存 1 至 500 個科目的分類。");
        var changes = new List<AccountMappingChange>();
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new JetActionException(JetErrorCodes.InvalidPayload, "科目分類資料格式不正確。");
            var code = PayloadReader.GetRequiredString(item, "accountCode");
            var categoryId = PayloadReader.GetRequiredString(item, "categoryId");
            if (!codes.Add(code))
                throw new JetActionException(JetErrorCodes.InvalidPayload, "同一科目只能指定一個分類。");
            changes.Add(new AccountMappingChange(code, categoryId));
        }
        await Task.Run(() => repositories.AccountMappingEditor.SaveAsync(projectId, changes, cancellationToken), cancellationToken);
        // 分類已經存好，之後的讀取不受取消影響。讀不回科目配對摘要時回報成功並提醒，回應不帶摘要欄位，
        // 前端保留畫面上原本的摘要（使用者 2026-10-19 裁定）；支援日誌記下錯誤類型，不記訊息內容。
        AccountMappingState? state = null;
        try
        {
            state = await repositories.AccountMappings.FindStateAsync(projectId, CancellationToken.None)
                ?? throw new InvalidOperationException("科目分類儲存後找不到狀態。");
        }
        catch (Exception error)
        {
            if (logger is not null) WorkflowResultStateSupport.StateReadFailed(logger, "account_mapping_state", error.GetType().Name);
        }

        var mutationState = await WorkflowResultStateSupport.AfterMutationAsync(
            projectId, repositories.RuleRuns, repositories.ResultStaleStates,
            repositories.FilterScenarios, repositories.ReportArtifactStore, AuditMutationEffects.For(AuditMutation.AccountMapping), logger);
        if (state is null)
        {
            return new { warnings = new[] { SummaryUnavailableWarning },
                invalidatedResults = mutationState.InvalidatedResults,
                staleState = mutationState.StaleState,
                reportArtifacts = mutationState.ReportArtifacts,
                reportArtifactWarning = mutationState.ReportArtifactWarning };
        }

        return new { batchId = state.BatchId, rowCount = state.RowCount, fileName = state.FileName,
            importedUtc = state.ImportedUtc, hasAnyCategory = state.HasAnyCategory,
            hasRevenue = state.HasRevenue, hasCounterpart = state.HasCounterpart,
            blankCategoryCount = state.BlankCategoryCount,
            warnings = Array.Empty<string>(),
            invalidatedResults = mutationState.InvalidatedResults,
            staleState = mutationState.StaleState,
            reportArtifacts = mutationState.ReportArtifacts,
            reportArtifactWarning = mutationState.ReportArtifactWarning };
    }
}
