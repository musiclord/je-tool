using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

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
        var request = new PageRequest(cursor, PayloadReader.GetOptionalInt(payload, "pageSize") ?? 100);
        var page = await Task.Run(() => repositories.AccountMappingEditor.GetPageAsync(
            projectId, request, search, cancellationToken, categoryId), cancellationToken);
        return new { rows = page.Rows.Select(row => new
            { accountCode = row.AccountCode, accountName = row.AccountName, categoryId = row.CategoryId }).ToArray(),
            nextCursor = page.NextCursor };
    }
}

public sealed class AccountMappingSaveHandler(
    ProjectSession session) : IApplicationActionHandler
{
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
        var state = await repositories.AccountMappings.FindStateAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException("科目分類儲存後找不到狀態。");
        var mutationState = await WorkflowResultStateSupport.AfterMutationAsync(
            projectId, repositories.RuleRuns, repositories.ResultStaleStates,
            repositories.FilterScenarios, repositories.ReportArtifactStore, AuditMutationEffects.For(AuditMutation.AccountMapping));
        return new { batchId = state.BatchId, rowCount = state.RowCount, fileName = state.FileName,
            importedUtc = state.ImportedUtc, hasAnyCategory = state.HasAnyCategory,
            hasRevenue = state.HasRevenue, hasCounterpart = state.HasCounterpart,
            blankCategoryCount = state.BlankCategoryCount,
            invalidatedResults = mutationState.InvalidatedResults,
            staleState = mutationState.StaleState,
            reportArtifacts = mutationState.ReportArtifacts,
            reportArtifactWarning = mutationState.ReportArtifactWarning };
    }
}
