using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// query.filterHitsPage：分頁列出一個已儲存情境命中的分錄，資料來自 filter_hit。
/// 請求必須帶 <c>scenarioPosition</c>，沒帶就回 invalid_payload。沒有指定 sort 時依 entry_id 由小到大排列，
/// 下一頁從上一頁最後一筆的排序鍵接著讀（keyset 分頁）；cursor 的內容前端不解讀，照原樣帶回。pageSize 預設 200，最多也是 200。
/// 資料庫裡的金額是放大成整數的值，回應時除以 moneyScale 換回顯示用的金額，做法和資料預覽相同。
///
/// 讀第一頁（沒有 cursor）時，如果存下的命中結果已經過期，例如重新確認 GL 配對清掉了命中、之後還沒重新儲存情境，
/// 就先用和 filter.commit 相同的 <see cref="IFilterHitMaterializer"/> 把全部已儲存情境的命中重新寫進 filter_hit，再讀這一頁。
/// 把情境的 definition JSON 解析成規格是 Application 的工作（<see cref="FilterScenarioPayloadParser"/>）；
/// <see cref="IFilterHitMaterializer"/> 只收 Domain 的型別，這樣 Infrastructure 不必反過來依賴 Application。
/// </summary>
public sealed class QueryFilterHitsPageHandler(
    IProjectStore projectStore,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "query.filterHitsPage";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var dataRevision = await repositories.FilterHitMaterializeService.ReadQueryDataRevisionAsync(projectId, cancellationToken);
        var scenarioPosition = PayloadReader.GetOptionalInt(payload, "scenarioPosition")
            ?? throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "scenario_position_missing");
        var request = PageRequestReader.Read(payload, ResultPageSorting.GlEntryRows);
        var cursor = request.Cursor;

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.ProjectNotFound, ProjectFolderMessages.ActiveProjectMissing);
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
            var refreshed = await repositories.FilterHitMaterializeService.MaterializeForConcurrentQueryAsync(
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
        await repositories.FilterHitMaterializeService.EnsureQueryCurrentAsync(projectId, query.DataRevision, query.ScenarioRevision, cancellationToken);
        var page = await Task.Run(
            () => repositories.FilterHitsPages.GetPageAsync(projectId, scenarioPosition, document.MoneyScale, request, cancellationToken),
            cancellationToken);

        var scale = document.MoneyScale;
        var entryIds = page.Rows.Select(static row => row.EntryId).ToArray();
        var rdeValues = await repositories.ResultPageRdeValues.ReadAsync(projectId, entryIds, cancellationToken);
        var customValues = ResultPageCustomValueRenderer.Render(entryIds, rdeValues, columnPlan, scale);
        await repositories.FilterHitMaterializeService.EnsureQueryCurrentAsync(projectId, query.DataRevision, query.ScenarioRevision, cancellationToken);
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
                voucherNumber = r.VoucherNumber,
                lineItem = r.LineItem,
                glDate = r.GlDate,
                accountNumber = r.AccountNumber,
                accountName = r.AccountName,
                amount = MoneyScaling.FromScaled(r.AmountScaled, scale),
                drCr = r.DrCr,
                description = r.Description,
                customValues = customValues[r.EntryId]
            }).ToArray(),
            totalCount = page.TotalCount,
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
