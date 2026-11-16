using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// query.blankRecordsPage：分頁列出科目編號、傳票號碼或摘要空白的分錄，以及核准日不在查核期間的分錄。
/// 請求多一個必填的 <c>category</c>，只收四個固定的值，其他值丟 <see cref="JetActionException"/>。
/// 沒有指定 sort 時依 entry_id 由小到大排列，下一頁從上一頁最後一筆的排序鍵接著讀（keyset 分頁）；
/// cursor 的內容前端不解讀，照原樣帶回。pageSize 預設 200，最多也是 200。
/// approvalDateNotInPeriod 用案件的 PeriodStart 與 PeriodEnd 判斷；handler 從 project.json 讀出來，傳給 repository。
/// 這一頁照目前的總帳逐筆判斷，核准日空白也算不在期間。傳票核准日不可用時這一項不執行，
/// 驗證結果不記筆數、只寫原因；畫面與報告只在驗證結果的筆數大於零時才要明細，所以不會在這時列出每一筆。
/// </summary>
public sealed class QueryBlankRecordsPageHandler(
    IProjectStore projectStore,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "query.blankRecordsPage";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var category = ParseCategory(PayloadReader.GetRequiredString(payload, "category"));
        var request = PageRequestReader.Read(payload, ResultPageSorting.BlankRecords);

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.ProjectNotFound,
                ProjectFolderMessages.ActiveProjectMissing);

        var page = await Task.Run(
            () => repositories.BlankRecordsPages.GetPageAsync(
                projectId, category, document.PeriodStart, document.PeriodEnd,
                request, cancellationToken),
            cancellationToken);

        return new
        {
            rows = page.Rows.Select(r => (object)new
            {
                voucherNumber = r.VoucherNumber,
                accountNumber = r.AccountNumber,
                glDate = r.GlDate,
                approvalDate = r.ApprovalDate,
                description = r.Description
            }).ToArray(),
            totalCount = page.TotalCount,
            nextCursor = page.NextCursor
        };
    }

    /// <summary>白名單 switch:非法值丟 invalid_payload(不接受任意字串)。</summary>
    private static BlankRecordCategory ParseCategory(string category) => category switch
    {
        "blankAccountNumber" => BlankRecordCategory.BlankAccountNumber,
        "blankVoucherNumber" => BlankRecordCategory.BlankVoucherNumber,
        "blankDescription" => BlankRecordCategory.BlankDescription,
        "approvalDateNotInPeriod" => BlankRecordCategory.ApprovalDateNotInPeriod,
        _ => throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "blank_records_category_invalid")
    };
}
