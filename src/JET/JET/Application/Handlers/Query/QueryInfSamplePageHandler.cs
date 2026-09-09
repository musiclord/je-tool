using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// query.infSamplePage：INF 抽樣(result_inf_sampling_test_sample,目前有效 validate.run)的
/// 行層明細 keyset 分頁(manifest 查詢段)。排序鍵 entry_id ASC、cursor opaque、
/// pageSize 預設 200/上限 500(夾擠在 Domain)。借/貸由 scaled 整數換算顯示值
/// ((decimal)scaled / moneyScale,沿用 DataPreview)。
/// </summary>
public sealed class QueryInfSamplePageHandler(
    IInfSamplePageRepository repository,
    IRuleRunStore ruleRunStore,
    IMappingStateStore mappingStore,
    IResultPageRdeValuesPort rdeValuesPort,
    IProjectStore projectStore,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "query.infSamplePage";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();
        var request = PageRequestReader.Read(payload, ResultPageSorting.InfSample);

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.ProjectNotFound,
                $"找不到專案 '{projectId}'。");
        var mapping = await mappingStore.FindAsync(projectId, DatasetKind.Gl, cancellationToken);
        var columnPlan = ResultPageColumnRegistry.ForInf(mapping);

        var latestRun = await ruleRunStore.FindLatestAsync(
            projectId, RuleRunKinds.Validate, cancellationToken);
        if (latestRun is null)
        {
            return new
            {
                columns = RenderColumns(columnPlan),
                rows = Array.Empty<object>(),
                nextCursor = (string?)null
            };
        }

        if (!RuleLogicVersions.IsCurrent(latestRun))
        {
            throw new JetActionException(
                JetErrorCodes.StaleResult,
                "最新 Validation 結果來自舊版規則，請重新執行資料驗證。");
        }

        var page = await Task.Run(
            () => repository.GetPageAsync(
                projectId, latestRun.RunId, document.MoneyScale,
                request, cancellationToken),
            cancellationToken);

        var scale = document.MoneyScale;
        var entryIds = page.Rows.Select(static row => row.EntryId).ToArray();
        var rdeValues = await rdeValuesPort.ReadAsync(projectId, entryIds, cancellationToken);
        var customValues = ResultPageCustomValueRenderer.Render(entryIds, rdeValues, columnPlan, scale);
        return new
        {
            columns = RenderColumns(columnPlan),
            rows = page.Rows.Select(r => (object)new
            {
                documentNumber = r.DocumentNumber,
                accountCode = r.AccountCode,
                accountName = r.AccountName,
                debit = (decimal)r.DebitScaled / scale,
                credit = (decimal)r.CreditScaled / scale,
                postDate = r.PostDate,
                approvalDate = r.ApprovalDate,
                createdBy = r.CreatedBy,
                approvedBy = r.ApprovedBy,
                description = r.Description,
                customValues = customValues[r.EntryId]
            }).ToArray(),
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
