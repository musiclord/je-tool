using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// query.highRiskSummaryScenarios：高風險條件彙總的情境摘要，對應工作底稿 step4 的概觀。
/// 回傳全部已儲存情境的 position 與 name，依 position 由小到大排列。每個情境附兩個數字，都是查詢當下從 filter_hit 算出來的：
/// 命中的傳票數是 COUNT(DISTINCT voucher_number)，命中的分錄數是 COUNT(*)。沒有命中的情境照樣列出，數字是 0。
/// summaryRowCount 是畫面上的「符合條件的分錄筆數」，口徑與 Criteria Selection 摘要相同；
/// 只有一條「未預期借貸組合」條件的情境用整張命中傳票的分錄數。
///
/// 和 query.filterHitsPage 一樣，只有在存下的命中結果已經過期，才用共用的服務重算全部已儲存情境。
/// 全部情境都命中零筆也是目前資料的有效結果，不會因為反覆開啟摘要而再算一次。
/// </summary>
public sealed class QueryHighRiskSummaryScenariosHandler(
    IProjectStore projectStore,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "query.highRiskSummaryScenarios";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var dataRevision = await repositories.FilterHitMaterializeService.ReadQueryDataRevisionAsync(projectId, cancellationToken);

        var project = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.ProjectNotFound, ProjectFolderMessages.ActiveProjectMissing);

        var scenarios = await repositories.FilterScenarios.ListAsync(projectId, cancellationToken);
        if (scenarios.Count == 0)
        {
            return new { scenarios = Array.Empty<object>() };
        }

        var revision = FilterPopulationScopeParser.RequireCurrentRevision(scenarios);

        var refreshed = await repositories.FilterHitMaterializeService.MaterializeForConcurrentQueryAsync(
            projectId,
            cancellationToken);
        if (refreshed is not null)
        {
            scenarios = refreshed.Scenarios;
            revision = FilterPopulationScopeParser.RequireCurrentRevision(scenarios);
        }
        await repositories.FilterHitMaterializeService.EnsureQueryCurrentAsync(projectId, dataRevision, revision.Revision, cancellationToken);
        // 只用「未預期借貸組合」一個條件的情境，Criteria Selection 摘要以整張命中傳票計算分錄筆數；
        // 畫面的 summaryRowCount 用同一個判斷，rowHitCount 仍是直接符合的分錄數。
        var wholeVoucherPositions = scenarios
            .Where(s =>
            {
                using var definition = JsonDocument.Parse(s.DefinitionJson);
                return JetAuditProgram.UsesLegacyWholeVoucherRows(
                    FilterScenarioPayloadParser.Parse(definition.RootElement, project.MoneyScale));
            })
            .Select(s => s.Position)
            .ToArray();
        var counts = await Task.Run(
            () => repositories.HighRiskSummaryScenarios.GetCountsAsync(projectId, cancellationToken), cancellationToken);
        var wholeVoucherRowCounts = await Task.Run(
            () => repositories.HighRiskSummaryScenarios.GetWholeVoucherRowCountsAsync(
                projectId, wholeVoucherPositions, cancellationToken),
            cancellationToken);
        await repositories.FilterHitMaterializeService.EnsureQueryCurrentAsync(projectId, dataRevision, revision.Revision, cancellationToken);

        return new
        {
            scenarios = scenarios.OrderBy(s => s.Position).Select(s =>
            {
                var (voucherHitCount, rowHitCount) = counts.GetValueOrDefault(s.Position);
                var countsWholeVoucherRows = wholeVoucherPositions.Contains(s.Position);
                return (object)new
                {
                    position = s.Position,
                    name = s.Name,
                    voucherHitCount,
                    rowHitCount,
                    summaryRowCount = countsWholeVoucherRows
                        ? wholeVoucherRowCounts.GetValueOrDefault(s.Position)
                        : rowHitCount,
                    countsWholeVoucherRows
                };
            }).ToArray()
        };
    }
}
