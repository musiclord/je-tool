using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// query.accountMappingBlankPage：科目配對檔分類欄留白的科目（已視為 Others）keyset 分頁。
/// 排序鍵 account_code ASC、cursor opaque、pageSize 預設 200／上限 500。只供第四步顯示，不影響篩選。
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

        var pageSize = PayloadReader.GetOptionalInt(payload, "pageSize") ?? PageRequest.DefaultPageSize;
        var page = await Task.Run(
            () => repositories.AccountMappingBlankPages.GetPageAsync(projectId, new PageRequest(cursor, Math.Clamp(pageSize <= 0 ? 200 : pageSize, 1, 200), IncludeTotalCount: string.IsNullOrEmpty(cursor)), cancellationToken),
            cancellationToken);
        return new
        {
            rows = page.Rows.Select(row => (object)new { accountCode = row.AccountCode, accountName = row.AccountName }).ToArray(),
            totalCount = page.TotalCount,
            nextCursor = page.NextCursor
        };
    }
}
