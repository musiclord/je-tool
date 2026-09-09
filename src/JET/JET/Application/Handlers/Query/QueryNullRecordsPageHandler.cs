using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// query.nullRecordsPage：空值/期外日期紀錄的 keyset 分頁(manifest 查詢段)。
/// 請求多一個必填 <c>category</c>(白名單四值;非法值丟 <see cref="JetActionException"/>);
/// 排序鍵 entry_id ASC、cursor opaque、pageSize 預設 200/上限 500。
/// outOfRangeDate 以專案 PeriodStart/End 判定(handler 取自 document 傳入 repo)。
/// </summary>
public sealed class QueryNullRecordsPageHandler(
    INullRecordsPageRepository repository,
    IProjectStore projectStore,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "query.nullRecordsPage";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();
        var category = ParseCategory(PayloadReader.GetRequiredString(payload, "category"));
        var request = PageRequestReader.Read(payload, ResultPageSorting.NullRecords);

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.ProjectNotFound,
                $"找不到專案 '{projectId}'。");

        var page = await Task.Run(
            () => repository.GetPageAsync(
                projectId, category, document.PeriodStart, document.PeriodEnd,
                request, cancellationToken),
            cancellationToken);

        return new
        {
            rows = page.Rows.Select(r => (object)new
            {
                documentNumber = r.DocumentNumber,
                accountCode = r.AccountCode,
                postDate = r.PostDate,
                description = r.Description
            }).ToArray(),
            nextCursor = page.NextCursor
        };
    }

    /// <summary>白名單 switch:非法值丟 invalid_payload(不接受任意字串)。</summary>
    private static NullRecordCategory ParseCategory(string category) => category switch
    {
        "nullAccount" => NullRecordCategory.NullAccount,
        "nullDocument" => NullRecordCategory.NullDocument,
        "nullDescription" => NullRecordCategory.NullDescription,
        "outOfRangeDate" => NullRecordCategory.OutOfRangeDate,
        _ => throw new JetActionException(
            JetErrorCodes.InvalidPayload,
            $"category '{category}' 非白名單值(nullAccount|nullDocument|nullDescription|outOfRangeDate)。")
    };
}
