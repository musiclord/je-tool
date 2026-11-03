using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// query.infSamplePage：INF 抽樣(inf_sample,目前有效 validate.run)的
/// 行層明細 keyset 分頁。排序鍵 entry_id ASC、cursor opaque、
/// pageSize 預設 200/上限 500(夾擠在 Domain)。借/貸由 scaled 整數換算顯示值
/// ((decimal)scaled / moneyScale,沿用 DataPreview)。
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
                debit = (decimal)r.DebitScaled / scale,
                credit = (decimal)r.CreditScaled / scale,
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
