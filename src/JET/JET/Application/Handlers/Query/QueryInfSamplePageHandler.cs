using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// query.infSamplePage：分頁列出 INF 抽樣抽到的分錄，資料來自 inf_sample 裡屬於目前這次資料驗證結果的樣本。
/// 沒有指定 sort 時依 entry_id 由小到大排列，下一頁從上一頁最後一筆的排序鍵接著讀（keyset 分頁）；
/// cursor 的內容前端不解讀，照原樣帶回。pageSize 預設 200，最多也是 200。
/// 資料庫裡的借方與貸方金額是放大成整數的值，回應時除以 moneyScale 換回顯示用的金額，做法和資料預覽相同。
/// </summary>
public sealed class QueryInfSamplePageHandler(
    IProjectStore projectStore,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "query.infSamplePage";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var request = PageRequestReader.Read(payload, ResultPageSorting.InfSample);

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.ProjectNotFound,
                ProjectFolderMessages.ActiveProjectMissing);
        var mapping = await repositories.MappingStates.FindAsync(projectId, DatasetKind.Gl, cancellationToken);
        var columnPlan = ResultPageColumnRegistry.ForInf(mapping);

        var latestResult = await repositories.Results.FindLatestAsync(
            projectId, ResultKind.Validation, cancellationToken);
        if (latestResult is null)
        {
            return new
            {
                columns = RenderColumns(columnPlan),
                rows = Array.Empty<object>(),
                totalCount = 0L, nextCursor = (string?)null
            };
        }

        if (!RuleLogicVersions.IsCurrent(latestResult))
        {
            throw new JetActionException(
                JetErrorCodes.StaleResult,
                "資料驗證結果是用舊版規則產生的，請重新執行資料驗證。");
        }

        var page = await Task.Run(
            () => repositories.InfSamplePages.GetPageAsync(
                projectId, latestResult.ResultId, document.MoneyScale,
                request, cancellationToken),
            cancellationToken);

        var scale = document.MoneyScale;
        var entryIds = page.Rows.Select(static row => row.EntryId).ToArray();
        var rdeValues = await repositories.ResultPageRdeValues.ReadAsync(projectId, entryIds, cancellationToken);
        var customValues = ResultPageCustomValueRenderer.Render(entryIds, rdeValues, columnPlan, scale);
        return new
        {
            columns = RenderColumns(columnPlan),
            rows = page.Rows.Select(r => (object)new
            {
                voucherNumber = r.VoucherNumber,
                accountNumber = r.AccountNumber,
                accountName = r.AccountName,
                debit = MoneyScaling.FromScaled(r.DebitScaled, scale),
                credit = MoneyScaling.FromScaled(r.CreditScaled, scale),
                glDate = r.GlDate,
                approvalDate = r.ApprovalDate,
                createdBy = r.CreatedBy,
                approvedBy = r.ApprovedBy,
                description = r.Description,
                customValues = customValues[r.EntryId]
            }).ToArray(),
            totalCount = page.TotalCount,
            nextCursor = page.NextCursor
        };
    }

    private static object[] RenderColumns(ResultPageColumnPlan plan) =>
        plan.Columns.Select(static column => (object)new
        {
            key = column.Key,
            label = column.Label,
            valueType = column.ValueType,
            isCustom = column.IsCustom,
            sortable = ResultPageSorting.InfSample.Find(column.Key) is not null
        }).ToArray();
}
