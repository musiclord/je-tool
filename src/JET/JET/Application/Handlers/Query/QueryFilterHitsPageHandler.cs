using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// query.filterHitsPage：已存篩選情境(result_filter_run)命中行層明細的 keyset 分頁(manifest 查詢段)。
/// 請求 <c>scenarioPosition</c> **必填**(缺則 invalid_payload);排序鍵 entry_id ASC、cursor opaque、
/// pageSize 預設 200/上限 500。金額由 scaled 整數換算顯示值((decimal)scaled / moneyScale,沿用 DataPreview)。
///
/// 惰性補算:首頁(無 cursor)取回為空、但該 position 仍存在於 config_filter_scenario 時(例如重投影
/// 清空命中後尚未重跑 filter.commit),重用 filter.commit 同源的 <see cref="IFilterRunMaterializer"/>
/// 對全部已存情境落地後再取一次。解析 definition JSON → spec 屬 Application 層
/// (<see cref="FilterScenarioPayloadParser"/>),materializer 只吃 Domain 型別,維持 Infrastructure
/// 不反向依賴 Application。
/// </summary>
public sealed class QueryFilterHitsPageHandler(
    IFilterHitsPageRepository repository,
    IFilterScenarioStore scenarioStore,
    FilterRunMaterializeService materializeService,
    IMappingStateStore mappingStore,
    IResultPageRdeValuesPort rdeValuesPort,
    IProjectStore projectStore,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "query.filterHitsPage";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();
        var scenarioPosition = PayloadReader.GetOptionalInt(payload, "scenarioPosition")
            ?? throw new JetActionException(
                JetErrorCodes.InvalidPayload, "scenarioPosition 為必填(整數)。");
        var cursor = PayloadReader.GetOptionalString(payload, "cursor");
        if (PageCursor.IsMalformed(cursor))
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload, "cursor 格式不符(無法解碼)。");
        }

        var pageSize = PayloadReader.GetOptionalInt(payload, "pageSize") ?? PageRequest.DefaultPageSize;

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.ProjectNotFound, $"找不到專案 '{projectId}'。");
        var scenarios = await scenarioStore.ListAsync(projectId, cancellationToken);
        var currentRevision = FilterPopulationScopeParser.RequireCurrentRevision(scenarios);
        if (!currentRevision.Positions.Contains(scenarioPosition))
        {
            throw new JetActionException(
                JetErrorCodes.StaleResult,
                "指定的篩選情境不屬於目前 revision，請重新載入進階條件篩選。");
        }

        var scenario = RequireScenario(scenarios, scenarioPosition);
        var mapping = await mappingStore.FindAsync(projectId, DatasetKind.Gl, cancellationToken);
        var columnPlan = ResultPageColumnRegistry.ForFilter(scenario, mapping, document.MoneyScale);

        var request = new PageRequest(cursor, pageSize);

        var page = await Task.Run(
            () => repository.GetPageAsync(projectId, scenarioPosition, document.MoneyScale, request, cancellationToken),
            cancellationToken);

        // 惰性補算:僅在首頁取回為空時嘗試(避免每頁多查);若該 position 有定義則落地後重取一次。
        if (page.Rows.Count == 0 && cursor is null)
        {
            var refreshed = await materializeService.MaterializeForConcurrentQueryAsync(
                projectId,
                cancellationToken);
            document = refreshed.Document;
            scenarios = refreshed.Scenarios;
            currentRevision = FilterPopulationScopeParser.RequireCurrentRevision(scenarios);
            if (!currentRevision.Positions.Contains(scenarioPosition))
            {
                throw new JetActionException(
                    JetErrorCodes.StaleResult,
                    "指定的篩選情境不屬於目前 revision，請重新載入進階條件篩選。");
            }

            scenario = RequireScenario(scenarios, scenarioPosition);
            mapping = await mappingStore.FindAsync(projectId, DatasetKind.Gl, cancellationToken);
            columnPlan = ResultPageColumnRegistry.ForFilter(scenario, mapping, document.MoneyScale);

            page = await Task.Run(
                () => repository.GetPageAsync(projectId, scenarioPosition, document.MoneyScale, request, cancellationToken),
                cancellationToken);
        }

        var scale = document.MoneyScale;
        var entryIds = page.Rows.Select(static row => row.EntryId).ToArray();
        var rdeValues = await rdeValuesPort.ReadAsync(projectId, entryIds, cancellationToken);
        var customValues = ResultPageCustomValueRenderer.Render(entryIds, rdeValues, columnPlan, scale);
        return new
        {
            columns = columnPlan.Columns.Select(static column => new
            {
                key = column.Key,
                label = column.Label,
                valueType = column.ValueType,
                isCustom = column.IsCustom
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
            nextCursor = page.NextCursor
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
