using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// 有界查詢共用的請求解析：<c>cursor</c>、<c>pageSize</c>、<c>sort</c>（{ key, direction }）與 <c>search</c>。
/// 排序鍵對照該查詢的 <see cref="PageSortCatalog"/> 白名單，不在白名單就報 invalid_payload 並列出允許的鍵；
/// 搜尋文字只做長度上限，比對哪一欄由查詢決定。
/// </summary>
internal static class PageRequestReader
{
    public static PageRequest Read(JsonElement payload, PageSortCatalog catalog)
    {
        var cursor = PayloadReader.GetOptionalString(payload, "cursor");
        if (PageCursor.IsMalformed(cursor))
        {
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "cursor_invalid");
        }

        var pageSize = PayloadReader.GetOptionalInt(payload, "pageSize") ?? PageRequest.DefaultPageSize;
        // 總筆數只在第一頁（沒有游標）計算；換頁沿用第一頁的數字，不再每頁重掃完整查詢範圍。
        return new PageRequest(cursor, Math.Clamp(pageSize <= 0 ? 200 : pageSize, 1, 200),
            ReadSort(payload, catalog), ReadSearch(payload, catalog), IncludeTotalCount: string.IsNullOrEmpty(cursor));
    }

    public static PageSort? ReadSort(JsonElement payload, PageSortCatalog catalog)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("sort", out var sort)
            || sort.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (sort.ValueKind != JsonValueKind.Object)
        {
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "sort_not_object");
        }

        var key = PayloadReader.GetOptionalString(sort, "key")
            ?? throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "sort_key_missing");
        if (catalog.Find(key) is null)
        {
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "sort_key_invalid");
        }

        var direction = PayloadReader.GetOptionalString(sort, "direction") ?? "asc";
        return direction switch
        {
            "asc" => new PageSort(key, PageSortDirection.Ascending),
            "desc" => new PageSort(key, PageSortDirection.Descending),
            _ => throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "sort_direction_invalid")
        };
    }

    public static string? ReadSearch(JsonElement payload, PageSortCatalog catalog)
    {
        var search = PayloadReader.GetOptionalString(payload, "search");
        if (search is null)
        {
            return null;
        }

        if (catalog.SearchSql is null)
        {
            throw new JetActionException(JetErrorCodes.InvalidPayload, "這個查詢不提供搜尋。");
        }

        if (search.Length > PageRequest.MaxSearchLength)
        {
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "search_too_long");
        }

        return search;
    }
}
