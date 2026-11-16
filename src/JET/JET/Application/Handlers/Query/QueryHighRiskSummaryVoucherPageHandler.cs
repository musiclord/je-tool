using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// query.highRiskSummaryVoucherPage：高風險條件彙總的傳票分頁，對應工作底稿的 step4。
/// 每列是一張命中的傳票，有 voucherNumber、glDate、createdBy、voucherTotal 與 matchedPositions。
/// matchedPositions 是這張傳票命中的情境位置，從 1 起算、由小到大、不重複。
/// voucherTotal 是這張傳票的借方總額，由 SUM(debit_amount_scaled) 除以 moneyScale 得到。
/// 沒有指定 sort 時依 voucher_number 由小到大排列，傳票號碼是 NULL 的分錄不列入。下一頁從上一頁最後一筆的排序鍵接著讀（keyset 分頁）；
/// cursor 的內容前端不解讀，照原樣帶回。pageSize 預設 200，最多也是 200。
///
/// 和 query.filterHitsPage 一樣，讀第一頁時只有在存下的命中結果已經過期，才重算全部已儲存情境；
/// 命中零筆或搜尋不到結果不當成過期。cursor 格式不對時回 invalid_payload，不會當成第一頁重讀。
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
