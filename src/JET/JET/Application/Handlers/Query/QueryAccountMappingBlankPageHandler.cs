using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// query.accountMappingBlankPage：分頁列出科目配對檔裡分類欄留白的科目，這些科目已經當成 Others。
/// 依 account_number 由小到大排列，下一頁從上一頁最後一筆的排序鍵接著讀（keyset 分頁）；cursor 的內容前端不解讀，照原樣帶回。
/// pageSize 預設 200，最多也是 200。只給第四步顯示用，不影響篩選。
/// </summary>
public sealed class QueryAccountMappingBlankPageHandler(
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "query.accountMappingBlankPage";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var cursor = PayloadReader.GetOptionalString(payload, "cursor");
        if (PageCursor.IsMalformed(cursor))
        {
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "cursor_invalid");
        }

        var pageSize = PageRequestReader.ReadPageSize(payload);
        var page = await Task.Run(
            () => repositories.AccountMappingBlankPages.GetPageAsync(projectId, new PageRequest(cursor, pageSize, IncludeTotalCount: string.IsNullOrEmpty(cursor)), cancellationToken),
            cancellationToken);
        return new
        {
            rows = page.Rows.Select(row => (object)new { accountNumber = row.AccountNumber, accountName = row.AccountName }).ToArray(),
            totalCount = page.TotalCount,
            nextCursor = page.NextCursor
        };
    }
}
