using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// query.docBalancePage：借貸不平傳票(SUM(amount_scaled)≠0)的 keyset 分頁。
/// 排序鍵 document_number ASC、cursor opaque、pageSize 預設 200/上限 500(夾擠在 Domain)。
/// 金額由 scaled 整數換算顯示值((decimal)scaled / moneyScale,沿用 DataPreview)。
/// </summary>
public sealed class QueryDocBalancePageHandler(
    IProjectStore projectStore,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "query.docBalancePage";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var request = PageRequestReader.Read(payload, ResultPageSorting.DocBalance);

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.ProjectNotFound,
                $"找不到專案 '{projectId}'。");

        var page = await Task.Run(
            () => repositories.DocBalancePages.GetPageAsync(
                projectId, document.MoneyScale, document.PeriodStart, document.PeriodEnd,
                request, cancellationToken),
            cancellationToken);

        var scale = document.MoneyScale;
        return new
        {
            rows = page.Rows.Select(r => (object)new
            {
                documentNumber = r.DocumentNumber,
                debit = (decimal)r.DebitScaled / scale,
                credit = (decimal)r.CreditScaled / scale,
                diff = (decimal)r.DiffScaled / scale
            }).ToArray(),
            nextCursor = page.NextCursor
        };
    }
}
