using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// query.tagMatrixVoucherPage:多情境 tag 矩陣的傳票層 keyset 分頁(底稿方法學 step4)。
/// 每列為一張命中傳票(documentNumber/postDate/createdBy/voucherTotal)＋ matchedPositions
/// (命中的情境位置 1..N,有序去重);排序鍵 document_number ASC、排除 NULL 傳票號、cursor opaque、
/// pageSize 預設 200/上限 500。voucherTotal 為該傳票借方總額顯示值((decimal)SUM(debit_amount_scaled)/scale)。
///
/// 惰性補算(同 filterHitsPage):首頁只在持久化結果已失效時重算全部已存情境，
/// 不以零筆命中或搜尋無結果推定失效。壞 cursor → invalid_payload，不靜默重置為首頁。
/// </summary>
public sealed class QueryTagMatrixVoucherPageHandler(
    IProjectStore projectStore,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "query.tagMatrixVoucherPage";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var dataRevision = await repositories.FilterRunMaterializeService.ReadQueryDataRevisionAsync(projectId, cancellationToken);

        var request = PageRequestReader.Read(payload, ResultPageSorting.TagMatrixVoucher);
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
        var (page, positions) = await Task.Run(
            () => repositories.TagMatrixVoucherPages.GetPageAsync(projectId, populationContext, request, null, cancellationToken),
            cancellationToken);
        await repositories.FilterRunMaterializeService.EnsureQueryCurrentAsync(projectId, query.DataRevision, query.ScenarioRevision, cancellationToken);

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
            nextCursor = query.WrapCursor(page.NextCursor)
        };
    }
}
