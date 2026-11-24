using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// query.completenessDiffPage：分頁列出完整性測試的全科目差異，也就是 GL 與 TB 金額不相等（diff≠0）的科目。
/// 沒有指定 sort 時照查詢算出的固定科目順序 page_row 排列，下一頁從上一頁最後一筆的排序鍵接著讀（keyset 分頁）；
/// cursor 的內容前端不解讀，照原樣帶回。pageSize 預設 200，最多也是 200。
/// 資料庫裡的金額是放大成整數的值，回應時除以 moneyScale 換回顯示用的金額，做法和資料預覽相同。
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
                ProjectFolderMessages.ActiveProjectMissing);

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
                accountNumber = r.AccountNumber,
                accountName = r.AccountName,
                tbAmount = MoneyScaling.FromScaled(r.TbAmountScaled, scale),
                glAmount = MoneyScaling.FromScaled(r.GlAmountScaled, scale),
                diff = MoneyScaling.FromScaled(r.DiffScaled, scale),
                notInTb = r.NotInTb
            }).ToArray(),
            totalCount = page.TotalCount,
            nextCursor = page.NextCursor
        };
    }
}
