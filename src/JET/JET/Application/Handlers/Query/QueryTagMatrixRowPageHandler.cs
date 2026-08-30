using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// query.tagMatrixRowPage:多情境 tag 矩陣的行層 keyset 分頁(manifest 查詢段、方法學 step4-1)。
/// 列集為**命中傳票之所有行**(任一行命中任一情境的傳票,其全部 GL 行皆列出,含該傳票內未命中
/// 任何情境的行);每列 documentNumber/lineItem/postDate/approvalDate/createdBy/approvedBy/
/// accountCode/accountName/amount(signed)/description ＋ matchedPositions(該行直接命中的情境
/// 位置 1..N,有序去重;**非命中行為空 []**)。排序鍵 entry_id ASC、排除 NULL 傳票號、cursor opaque、
/// pageSize 預設 200/上限 500。amount 為該行 signed 金額顯示值((decimal)AmountScaled/scale)。
///
/// repo 回的 RowTagRow 不含 entry_id,故另回與 rows 同序同長的 EntryIds;本 handler 以 index 對齊
/// rows[i] ↔ EntryIds[i] → PositionsByEntry,非命中行(不在 dict)補空 []。
///
/// 惰性補算(同 filterHitsPage):首頁(無 cursor)取回為空但 config_filter_scenario 有已存情境時,
/// 先重用 filter.commit 同源的共用 materialize 服務對全部已存情境落地後再取一次。壞 cursor →
/// invalid_payload(fail loud,不靜默重置為首頁)。
/// </summary>
public sealed class QueryTagMatrixRowPageHandler(
    ITagMatrixRowPageRepository repository,
    IFilterScenarioStore scenarioStore,
    FilterRunMaterializeService materializeService,
    IProjectStore projectStore,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "query.tagMatrixRowPage";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();

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
        if (scenarios.Count == 0)
        {
            return new { rows = Array.Empty<object>(), nextCursor = (string?)null };
        }

        var revision = FilterPopulationScopeParser.RequireCurrentRevision(scenarios);
        var populationContext = new GlPopulationContext(
            revision.PopulationScope,
            document.PeriodStart,
            document.PeriodEnd);

        var request = new PageRequest(cursor, pageSize);

        var (page, entryIds, positions) = await Task.Run(
            () => repository.GetPageAsync(projectId, populationContext, request, null, cancellationToken),
            cancellationToken);

        // 惰性補算:僅在首頁取回為空時嘗試(避免每頁多查);若有已存情境則落地後重取一次。
        if (page.Rows.Count == 0 && cursor is null)
        {
            if (scenarios.Count > 0)
            {
                var refreshed = await materializeService.MaterializeForConcurrentQueryAsync(
                    projectId,
                    cancellationToken);
                document = refreshed.Document;
                scenarios = refreshed.Scenarios;
                revision = FilterPopulationScopeParser.RequireCurrentRevision(scenarios);
                populationContext = new GlPopulationContext(
                    revision.PopulationScope,
                    document.PeriodStart,
                    document.PeriodEnd);
                (page, entryIds, positions) = await Task.Run(
                    () => repository.GetPageAsync(projectId, populationContext, request, null, cancellationToken),
                    cancellationToken);
            }
        }

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
            nextCursor = page.NextCursor
        };
    }
}
