using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// query.highRiskSummaryRowPage:多情境高風險條件彙總的行層 keyset 分頁(底稿方法學 step4-1)。
/// 列集為**命中傳票之所有行**(任一行命中任一情境的傳票,其全部 GL 行皆列出,含該傳票內未命中
/// 任何情境的行);每列 voucherNumber/lineItem/glDate/approvalDate/createdBy/approvedBy/
/// accountNumber/accountName/amount(signed)/description ＋ matchedPositions(該行直接命中的情境
/// 位置 1..N,有序去重;**非命中行為空 []**)。排序鍵 entry_id ASC、排除 NULL 傳票號、cursor opaque、
/// pageSize 預設 200/上限 500。amount 為該行 signed 金額顯示值((decimal)AmountScaled/scale)。
///
/// repo 回的 RowTagRow 不含 entry_id,故另回與 rows 同序同長的 EntryIds;本 handler 以 index 對齊
/// rows[i] ↔ EntryIds[i] → PositionsByEntry,非命中行(不在 dict)補空 []。
///
/// 惰性補算(同 filterHitsPage):首頁只在持久化結果已失效時，用共用服務重算全部已存情境。
/// 零筆命中與搜尋無結果不觸發重算。壞 cursor →
/// invalid_payload(fail loud,不靜默重置為首頁)。
/// </summary>
public sealed class QueryHighRiskSummaryRowPageHandler(
    IProjectStore projectStore,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "query.highRiskSummaryRowPage";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var dataRevision = await repositories.FilterHitMaterializeService.ReadQueryDataRevisionAsync(projectId, cancellationToken);

        var request = PageRequestReader.Read(payload, ResultPageSorting.HighRiskSummaryRow);
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
        var (page, entryIds, positions) = await Task.Run(
            () => repositories.HighRiskSummaryRowPages.GetPageAsync(projectId, populationContext, request, null, cancellationToken),
            cancellationToken);
        await repositories.FilterHitMaterializeService.EnsureQueryCurrentAsync(projectId, query.DataRevision, query.ScenarioRevision, cancellationToken);

        var scale = document.MoneyScale;
        var rows = new object[page.Rows.Count];
        for (var i = 0; i < page.Rows.Count; i++)
        {
            var r = page.Rows[i];
            rows[i] = new
            {
                voucherNumber = r.VoucherNumber,
                lineItem = r.LineItem,
                glDate = r.GlDate,
                approvalDate = r.ApprovalDate,
                createdBy = r.CreatedBy,
                approvedBy = r.ApprovedBy,
                accountNumber = r.AccountNumber,
                accountName = r.AccountName,
                amount = (decimal)r.AmountScaled / scale,
                matchedPositions = positions.GetValueOrDefault(entryIds[i], []),
                description = r.Description
            };
        }

        return new
        {
            rows,
            totalCount = page.TotalCount,
            nextCursor = query.WrapCursor(page.NextCursor)
        };
    }
}
