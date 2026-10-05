using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// query.completenessDiffPage：完整性全科目差異(diff≠0)的 keyset 分頁。
/// 排序鍵 account_code ASC、cursor opaque、pageSize 預設 200/上限 500(夾擠在 Domain)。
/// 金額由 scaled 整數換算顯示值((decimal)scaled / moneyScale,沿用 DataPreview)。
/// </summary>
public sealed class QueryCompletenessDiffPageHandler(
    IProjectStore projectStore,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "query.completenessDiffPage";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var request = PageRequestReader.Read(payload, ResultPageSorting.CompletenessDiff);

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.ProjectNotFound,
                $"找不到專案 '{projectId}'。");

        var page = await Task.Run(
            () => repositories.CompletenessDiffPages.GetPageAsync(
                projectId, document.MoneyScale, document.PeriodStart, document.PeriodEnd,
                request, cancellationToken),
            cancellationToken);

        var scale = document.MoneyScale;
        return new
        {
            rows = page.Rows.Select(r => (object)new
            {
                accountCode = r.AccountCode,
                accountName = r.AccountName,
                tbAmount = (decimal)r.TbAmountScaled / scale,
                glAmount = (decimal)r.GlAmountScaled / scale,
                diff = (decimal)r.DiffScaled / scale,
                notInTb = r.NotInTb
            }).ToArray(),
            nextCursor = page.NextCursor
        };
    }
}
