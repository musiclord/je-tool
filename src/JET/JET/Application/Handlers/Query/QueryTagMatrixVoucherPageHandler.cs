using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// query.tagMatrixVoucherPage:多情境 tag 矩陣的傳票層 keyset 分頁(manifest 查詢段、方法學 step4)。
/// 每列為一張命中傳票(documentNumber/postDate/createdBy/voucherTotal)＋ matchedPositions
/// (命中的情境位置 1..N,有序去重);排序鍵 document_number ASC、排除 NULL 傳票號、cursor opaque、
/// pageSize 預設 200/上限 500。voucherTotal 為該傳票借方總額顯示值((decimal)SUM(debit_amount_scaled)/scale)。
///
/// 惰性補算(同 filterHitsPage):首頁(無 cursor)取回為空但 config_filter_scenario 有已存情境
/// (例如重投影清空命中後尚未重跑 filter.commit),先重用 filter.commit 同源的共用 materialize 服務
/// 對全部已存情境落地後再取一次。壞 cursor → invalid_payload(fail loud,不靜默重置為首頁)。
/// </summary>
public sealed class QueryTagMatrixVoucherPageHandler(
    ITagMatrixVoucherPageRepository repository,
    IFilterScenarioStore scenarioStore,
    FilterRunMaterializeService materializeService,
    IProjectStore projectStore,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "query.tagMatrixVoucherPage";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();

        var request = PageRequestReader.Read(payload, ResultPageSorting.TagMatrixVoucher);
        var cursor = request.Cursor;

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

        var (page, positions) = await Task.Run(
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
                (page, positions) = await Task.Run(
                    () => repository.GetPageAsync(projectId, populationContext, request, null, cancellationToken),
                    cancellationToken);
            }
        }

        var scale = document.MoneyScale;
        return new
        {
            rows = page.Rows.Select(r => (object)new
            {
                documentNumber = r.DocumentNumber,
                postDate = r.PostDate,
                createdBy = r.CreatedBy,
                voucherTotal = (decimal)r.VoucherTotalScaled / scale,
                matchedPositions = r.DocumentNumber is not null
                    ? positions.GetValueOrDefault(r.DocumentNumber, [])
                    : (IReadOnlyList<int>)[]
            }).ToArray(),
            nextCursor = page.NextCursor
        };
    }
}
