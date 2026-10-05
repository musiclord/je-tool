using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// query.filterHitsPage：已存篩選情境(result_filter_run)命中行層明細的 keyset 分頁。
/// 請求 <c>scenarioPosition</c> **必填**(缺則 invalid_payload);排序鍵 entry_id ASC、cursor opaque、
/// pageSize 預設 200/上限 500。金額由 scaled 整數換算顯示值((decimal)scaled / moneyScale,沿用 DataPreview)。
///
/// 惰性補算:首頁(無 cursor)的持久化結果已失效時(例如重投影清空命中後尚未重跑 filter.commit),
/// 重用 filter.commit 同源的 <see cref="IFilterRunMaterializer"/>
/// 對全部已存情境落地後再取一次。解析 definition JSON → spec 屬 Application 層
/// (<see cref="FilterScenarioPayloadParser"/>),materializer 只吃 Domain 型別,維持 Infrastructure
/// 不反向依賴 Application。
/// </summary>
public sealed class QueryFilterHitsPageHandler(
    IProjectStore projectStore,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "query.filterHitsPage";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var dataRevision = await repositories.FilterRunMaterializeService.ReadQueryDataRevisionAsync(projectId, cancellationToken);
        var scenarioPosition = PayloadReader.GetOptionalInt(payload, "scenarioPosition")
            ?? throw new JetActionException(
                JetErrorCodes.InvalidPayload, "scenarioPosition 為必填(整數)。");
        var request = PageRequestReader.Read(payload, ResultPageSorting.GlEntryRows);
        var cursor = request.Cursor;

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.ProjectNotFound, $"找不到專案 '{projectId}'。");
        var scenarios = await repositories.FilterScenarios.ListAsync(projectId, cancellationToken);
        var currentRevision = FilterPopulationScopeParser.RequireCurrentRevision(scenarios);
        if (!currentRevision.Positions.Contains(scenarioPosition))
        {
            throw new JetActionException(
                JetErrorCodes.StaleResult,
                "篩選情境已更新，請重新開啟第五步進階條件篩選。");
        }

        // 搜尋無結果和零筆命中都是有效答案；只有持久化失效旗標要求重算。
        if (cursor is null)
        {
            var refreshed = await repositories.FilterRunMaterializeService.MaterializeForConcurrentQueryAsync(
                projectId,
                cancellationToken);
            if (refreshed is not null)
            {
                document = refreshed.Document;
                scenarios = refreshed.Scenarios;
                currentRevision = FilterPopulationScopeParser.RequireCurrentRevision(scenarios);
                if (!currentRevision.Positions.Contains(scenarioPosition))
                {
                    throw new JetActionException(
                        JetErrorCodes.StaleResult,
                        "篩選情境已更新，請重新開啟第五步進階條件篩選。");
                }
            }
        }

        var scenario = RequireScenario(scenarios, scenarioPosition);
        var mapping = await repositories.MappingStates.FindAsync(projectId, DatasetKind.Gl, cancellationToken);
        var columnPlan = ResultPageColumnRegistry.ForFilter(scenario, mapping, document.MoneyScale);
        var query = FilterResultQuerySnapshot.Bind(projectId, Action, scenarioPosition,
            dataRevision, currentRevision.Revision, request);
        request = query.Request;
        await repositories.FilterRunMaterializeService.EnsureQueryCurrentAsync(projectId, query.DataRevision, query.ScenarioRevision, cancellationToken);
        var page = await Task.Run(
            () => repositories.FilterHitsPages.GetPageAsync(projectId, scenarioPosition, document.MoneyScale, request, cancellationToken),
            cancellationToken);

        var scale = document.MoneyScale;
        var entryIds = page.Rows.Select(static row => row.EntryId).ToArray();
        var rdeValues = await repositories.ResultPageRdeValues.ReadAsync(projectId, entryIds, cancellationToken);
        var customValues = ResultPageCustomValueRenderer.Render(entryIds, rdeValues, columnPlan, scale);
        await repositories.FilterRunMaterializeService.EnsureQueryCurrentAsync(projectId, query.DataRevision, query.ScenarioRevision, cancellationToken);
        return new
        {
            columns = columnPlan.Columns.Select(static column => new
            {
                key = column.Key,
                label = column.Label,
                valueType = column.ValueType,
                isCustom = column.IsCustom,
                sortable = ResultPageSorting.GlEntryRows.Find(column.Key) is not null
            }).ToArray(),
            rows = page.Rows.Select(r => (object)new
            {
                documentNumber = r.DocumentNumber,
                lineItem = r.LineItem,
                postDate = r.PostDate,
                accountCode = r.AccountCode,
                accountName = r.AccountName,
                amount = (decimal)r.AmountScaled / scale,
                drCr = r.DrCr,
                description = r.Description,
                customValues = customValues[r.EntryId]
            }).ToArray(),
            nextCursor = query.WrapCursor(page.NextCursor)
        };
    }

    private static SavedFilterScenario RequireScenario(
        IReadOnlyList<SavedFilterScenario> scenarios,
        int scenarioPosition)
    {
        var matches = scenarios.Where(scenario => scenario.Position == scenarioPosition).Take(2).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new JetActionException(
                JetErrorCodes.StaleResult,
                "指定的篩選情境定義不存在或重複，請重新載入進階條件篩選。");
    }
}
