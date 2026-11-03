using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// query.highRiskSummaryVoucherPage:多情境高風險條件彙總的傳票層 keyset 分頁(底稿方法學 step4)。
/// 每列為一張命中傳票(voucherNumber/glDate/createdBy/voucherTotal)＋ matchedPositions
/// (命中的情境位置 1..N,有序去重);排序鍵 voucher_number ASC、排除 NULL 傳票號、cursor opaque、
/// pageSize 預設 200/上限 500。voucherTotal 為該傳票借方總額顯示值((decimal)SUM(debit_amount_scaled)/scale)。
///
/// 惰性補算(同 filterHitsPage):首頁只在持久化結果已失效時重算全部已存情境，
/// 不以零筆命中或搜尋無結果推定失效。壞 cursor → invalid_payload，不靜默重置為首頁。
/// </summary>
public sealed class QueryHighRiskSummaryVoucherPageHandler(
    IProjectStore projectStore,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "query.highRiskSummaryVoucherPage";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var dataRevision = await repositories.FilterHitMaterializeService.ReadQueryDataRevisionAsync(projectId, cancellationToken);

        var request = PageRequestReader.Read(payload, ResultPageSorting.HighRiskSummaryVoucher);
        var cursor = request.Cursor;

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.ProjectNotFound, ProjectFolderMessages.ActiveProjectMissing);
        var scenarios = await repositories.FilterScenarios.ListAsync(projectId, cancellationToken);
        if (scenarios.Count == 0)
        {
            if (cursor is not null) { throw FilterResultQuerySnapshot.Stale(); }
            return new { rows = Array.Empty<object>(), totalCount = 0L, nextCursor = (string?)null };
        }

        var revision = FilterPopulationScopeParser.RequireCurrentRevision(scenarios);
        var populationContext = new GlPopulationContext(
            revision.PopulationScope,
            document.PeriodStart,
            document.PeriodEnd);

        if (cursor is null)
        {
            var refreshed = await repositories.FilterHitMaterializeService.MaterializeForConcurrentQueryAsync(
                projectId,
                cancellationToken);
            if (refreshed is not null)
            {
                document = refreshed.Document;
                scenarios = refreshed.Scenarios;
                revision = FilterPopulationScopeParser.RequireCurrentRevision(scenarios);
                populationContext = new GlPopulationContext(
                    revision.PopulationScope,
                    document.PeriodStart,
                    document.PeriodEnd);
            }
        }
        var query = FilterResultQuerySnapshot.Bind(projectId, Action, null,
            dataRevision, revision.Revision, request);
        request = query.Request;
        await repositories.FilterHitMaterializeService.EnsureQueryCurrentAsync(projectId, query.DataRevision, query.ScenarioRevision, cancellationToken);
        var (page, positions) = await Task.Run(
            () => repositories.HighRiskSummaryVoucherPages.GetPageAsync(projectId, populationContext, request, null, cancellationToken),
            cancellationToken);
        await repositories.FilterHitMaterializeService.EnsureQueryCurrentAsync(projectId, query.DataRevision, query.ScenarioRevision, cancellationToken);

        var scale = document.MoneyScale;
        return new
        {
            rows = page.Rows.Select(r => (object)new
            {
                voucherNumber = r.VoucherNumber,
                glDate = r.GlDate,
                createdBy = r.CreatedBy,
                voucherTotal = (decimal)r.VoucherTotalScaled / scale,
                matchedPositions = r.VoucherNumber is not null
                    ? positions.GetValueOrDefault(r.VoucherNumber, [])
                    : (IReadOnlyList<int>)[]
            }).ToArray(),
            totalCount = page.TotalCount,
            nextCursor = query.WrapCursor(page.NextCursor)
        };
    }
}
