using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// query.voucherBalancePage：分頁列出借貸不平的傳票，也就是 SUM(amount_scaled)≠0 的傳票。
/// 沒有指定 sort 時依 voucher_number 由小到大排列，下一頁從上一頁最後一筆的排序鍵接著讀（keyset 分頁）；
/// cursor 的內容前端不解讀，照原樣帶回。pageSize 預設 200，最多也是 200。
/// 資料庫裡的金額是放大成整數的值，回應時除以 moneyScale 換回顯示用的金額，做法和資料預覽相同。
/// </summary>
public sealed class QueryVoucherBalancePageHandler(
    IProjectStore projectStore,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "query.voucherBalancePage";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var request = PageRequestReader.Read(payload, ResultPageSorting.VoucherBalance);

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.ProjectNotFound,
                ProjectFolderMessages.ActiveProjectMissing);

        var page = await Task.Run(
            () => repositories.VoucherBalancePages.GetPageAsync(
                projectId, document.MoneyScale, document.PeriodStart, document.PeriodEnd,
                request, cancellationToken),
            cancellationToken);

        var scale = document.MoneyScale;
        return new
        {
            rows = page.Rows.Select(r => (object)new
            {
                voucherNumber = r.VoucherNumber,
                debit = (decimal)r.DebitScaled / scale,
                credit = (decimal)r.CreditScaled / scale,
                diff = (decimal)r.DiffScaled / scale
            }).ToArray(),
            totalCount = page.TotalCount,
            nextCursor = page.NextCursor
        };
    }
}
