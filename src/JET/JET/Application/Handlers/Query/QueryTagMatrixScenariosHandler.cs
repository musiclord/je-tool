using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// query.tagMatrixScenarios:多情境 tag 矩陣的情境摘要(底稿方法學 step4 概觀)。
/// 回全部已存情境(position/name,依 position 升冪),每個附傳票層命中數
/// (COUNT(DISTINCT document_number))與行層命中數(COUNT(*)),即時從 result_filter_run 算;
/// 無命中的情境仍列出、count=0。summaryRowCount 是畫面上的「符合條件的分錄筆數」，
/// 口徑與 Criteria Selection 摘要相同；只有一條「未預期借貸組合」條件的情境用整張命中傳票的分錄數。
///
/// 惰性補算(同 filterHitsPage):只有持久化結果已失效時，才用共用服務重算全部已存情境。
/// 全部情境零筆命中仍是目前資料的有效結果，不會因反覆開啟摘要而再次重算。
/// </summary>
public sealed class QueryTagMatrixScenariosHandler(
    IProjectStore projectStore,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "query.tagMatrixScenarios";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var dataRevision = await repositories.FilterRunMaterializeService.ReadQueryDataRevisionAsync(projectId, cancellationToken);

        var project = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.ProjectNotFound, ProjectFolderMessages.ActiveProjectMissing);

        var scenarios = await repositories.FilterScenarios.ListAsync(projectId, cancellationToken);
        if (scenarios.Count == 0)
        {
            return new { scenarios = Array.Empty<object>() };
        }

        var revision = FilterPopulationScopeParser.RequireCurrentRevision(scenarios);

        var refreshed = await repositories.FilterRunMaterializeService.MaterializeForConcurrentQueryAsync(
            projectId,
            cancellationToken);
        if (refreshed is not null)
        {
            scenarios = refreshed.Scenarios;
            revision = FilterPopulationScopeParser.RequireCurrentRevision(scenarios);
        }
        await repositories.FilterRunMaterializeService.EnsureQueryCurrentAsync(projectId, dataRevision, revision.Revision, cancellationToken);
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
            () => repositories.TagMatrixScenarios.GetCountsAsync(projectId, cancellationToken), cancellationToken);
        var wholeVoucherRowCounts = await Task.Run(
            () => repositories.TagMatrixScenarios.GetWholeVoucherRowCountsAsync(
                projectId, wholeVoucherPositions, cancellationToken),
            cancellationToken);
        await repositories.FilterRunMaterializeService.EnsureQueryCurrentAsync(projectId, dataRevision, revision.Revision, cancellationToken);

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
