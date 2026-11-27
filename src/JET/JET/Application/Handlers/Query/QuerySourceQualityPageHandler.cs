using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// query.sourceQualityPage：分頁列出目前這一份 GL 的來源資料品質問題；這一份 GL 是最近一次成功確認配對時轉入的。
/// 只有 blankGlDate 一類，也就是總帳日期空白的分錄。會讓配對無法完成的錯誤不列在這裡，
/// 而是回 projection_failed，那次配對的寫入全部回復。
/// </summary>
public sealed class QuerySourceQualityPageHandler(
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "query.sourceQualityPage";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        if (payload.TryGetProperty("cursor", out var cursorElement)
            && cursorElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
        {
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "cursor_not_string");
        }

        var request = PageRequestReader.Read(payload, ResultPageSorting.SourceQuality);
        var page = await repositories.SourceQualityPages.GetPageAsync(
            projectId,
            request,
            cancellationToken);

        return new
        {
            rows = page.Rows.Select(row => (object)new
            {
                category = row.Category,
                sourceRowNumber = row.SourceRowNumber,
                sourceLabel = row.SourceLabel,
                voucherNumber = row.VoucherNumber,
                accountNumber = row.AccountNumber,
                glDate = row.GlDate,
                description = row.Description
            }).ToArray(),
            totalCount = page.TotalCount,
            nextCursor = page.NextCursor
        };
    }
}
