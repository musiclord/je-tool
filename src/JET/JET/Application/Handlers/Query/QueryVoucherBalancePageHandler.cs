using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// query.voucherBalancePage：借貸不平傳票(SUM(amount_scaled)≠0)的 keyset 分頁。
/// 排序鍵 voucher_number ASC、cursor opaque、pageSize 預設 200/上限 500(夾擠在 Domain)。
/// 金額由 scaled 整數換算顯示值((decimal)scaled / moneyScale,沿用 DataPreview)。
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
