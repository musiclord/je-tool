using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// query.sourceQualityPage：分頁讀回目前成功 GL generation 的 source-quality findings。
/// 現階段 category closed 為 nullPostDate；hard projection errors 仍由 projection_failed 回傳並 rollback。
/// </summary>
public sealed class QuerySourceQualityPageHandler(
    ISourceQualityPageRepository repository,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "query.sourceQualityPage";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();
        string? cursor = null;
        if (payload.TryGetProperty("cursor", out var cursorElement)
            && cursorElement.ValueKind != JsonValueKind.Null)
        {
            if (cursorElement.ValueKind != JsonValueKind.String)
            {
                throw new JetActionException(JetErrorCodes.InvalidPayload, "cursor 必須是 opaque 字串或 null。");
            }
            cursor = cursorElement.GetString();
        }
        if (PageCursor.IsMalformed(cursor))
        {
            throw new JetActionException(JetErrorCodes.InvalidPayload, "cursor 格式不符(無法解碼)。");
        }
        var pageSize = PayloadReader.GetOptionalInt(payload, "pageSize") ?? PageRequest.DefaultPageSize;
        var page = await repository.GetPageAsync(
            projectId,
            new PageRequest(cursor, pageSize),
            cancellationToken);

        return new
        {
            rows = page.Rows.Select(row => (object)new
            {
                category = row.Category,
                sourceRowNumber = row.SourceRowNumber,
                sourceLabel = row.SourceLabel,
                documentNumber = row.DocumentNumber,
                accountCode = row.AccountCode,
                postDate = row.PostDate,
                description = row.Description
            }).ToArray(),
            nextCursor = page.NextCursor
        };
    }
}
