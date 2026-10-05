using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// query.tagMatrixRowPage:多情境 tag 矩陣的行層 keyset 分頁(底稿方法學 step4-1)。
/// 列集為**命中傳票之所有行**(任一行命中任一情境的傳票,其全部 GL 行皆列出,含該傳票內未命中
/// 任何情境的行);每列 documentNumber/lineItem/postDate/approvalDate/createdBy/approvedBy/
/// accountCode/accountName/amount(signed)/description ＋ matchedPositions(該行直接命中的情境
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
public sealed class QueryTagMatrixRowPageHandler(
    IProjectStore projectStore,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "query.tagMatrixRowPage";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var dataRevision = await repositories.FilterRunMaterializeService.ReadQueryDataRevisionAsync(projectId, cancellationToken);

        var request = PageRequestReader.Read(payload, ResultPageSorting.TagMatrixRow);
        var cursor = request.Cursor;

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.ProjectNotFound, $"找不到專案 '{projectId}'。");
        var scenarios = await repositories.FilterScenarios.ListAsync(projectId, cancellationToken);
        if (scenarios.Count == 0)
        {
            if (cursor is not null) { throw FilterResultQuerySnapshot.Stale(); }
            return new { rows = Array.Empty<object>(), nextCursor = (string?)null };
        }

        var revision = FilterPopulationScopeParser.RequireCurrentRevision(scenarios);
        var populationContext = new GlPopulationContext(
            revision.PopulationScope,
            document.PeriodStart,
            document.PeriodEnd);

        if (cursor is null)
        {
            var refreshed = await repositories.FilterRunMaterializeService.MaterializeForConcurrentQueryAsync(
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
        await repositories.FilterRunMaterializeService.EnsureQueryCurrentAsync(projectId, query.DataRevision, query.ScenarioRevision, cancellationToken);
        var (page, entryIds, positions) = await Task.Run(
            () => repositories.TagMatrixRowPages.GetPageAsync(projectId, populationContext, request, null, cancellationToken),
            cancellationToken);
        await repositories.FilterRunMaterializeService.EnsureQueryCurrentAsync(projectId, query.DataRevision, query.ScenarioRevision, cancellationToken);

        var scale = document.MoneyScale;
        var rows = new object[page.Rows.Count];
        for (var i = 0; i < page.Rows.Count; i++)
        {
            var r = page.Rows[i];
            rows[i] = new
            {
                documentNumber = r.DocumentNumber,
                lineItem = r.LineItem,
                postDate = r.PostDate,
                approvalDate = r.ApprovalDate,
                createdBy = r.CreatedBy,
                approvedBy = r.ApprovedBy,
                accountCode = r.AccountCode,
                accountName = r.AccountName,
                amount = (decimal)r.AmountScaled / scale,
                matchedPositions = positions.GetValueOrDefault(entryIds[i], []),
                description = r.Description
            };
        }

        return new
        {
            rows,
            nextCursor = query.WrapCursor(page.NextCursor)
        };
    }
}
