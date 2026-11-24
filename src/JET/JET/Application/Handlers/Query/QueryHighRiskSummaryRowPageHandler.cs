using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// query.highRiskSummaryRowPage：高風險條件彙總的分錄明細分頁，對應工作底稿的 step4-1。
/// 列出命中傳票的全部分錄：一張傳票只要有任何一筆分錄命中任何一個情境，它的每一筆 GL 分錄都列出來，
/// 包含沒有命中任何情境的分錄。每列有 voucherNumber、lineItem、glDate、approvalDate、createdBy、approvedBy、
/// accountNumber、accountName、amount、description 與 matchedPositions。
/// matchedPositions 是這一筆分錄直接命中的情境位置，從 1 起算、由小到大、不重複；沒有命中的分錄是空陣列。
/// amount 是帶正負號的金額，由 AmountScaled 除以 moneyScale 得到。
/// 沒有指定 sort 時依 entry_id 由小到大排列，不含傳票號碼是 NULL 的分錄。下一頁從上一頁最後一筆的排序鍵接著讀（keyset 分頁）；
/// cursor 的內容前端不解讀，照原樣帶回。pageSize 預設 200，最多也是 200。
///
/// repository 回的每一列不含 entry_id，所以另外回一份和 rows 順序、長度都相同的 EntryIds。
/// 這個 handler 用同一個索引把 rows[i] 對到 EntryIds[i]，再查出這筆分錄命中的情境位置；查不到就是沒有命中，補空陣列。
///
/// 和 query.filterHitsPage 一樣，讀第一頁時只有在存下的命中結果已經過期，才用共用的服務重算全部已儲存情境；
/// 命中零筆或搜尋不到結果不會引發重算。cursor 格式不對時回 invalid_payload，不會當成第一頁重讀。
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
                amount = MoneyScaling.FromScaled(r.AmountScaled, scale),
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
