using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// query.blankRecordsPage：空值/期外日期紀錄的 keyset 分頁。
/// 請求多一個必填 <c>category</c>(白名單四值;非法值丟 <see cref="JetActionException"/>);
/// 排序鍵 entry_id ASC、cursor opaque、pageSize 預設 200/上限 500。
/// approvalDateNotInPeriod 以專案 PeriodStart/End 判定(handler 取自 document 傳入 repo)。
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
