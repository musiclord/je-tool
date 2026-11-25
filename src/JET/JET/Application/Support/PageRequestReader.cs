using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// 分頁查詢共用的請求解析：<c>cursor</c>、<c>pageSize</c>、<c>sort</c>（{ key, direction }）與 <c>search</c>。
/// 排序鍵對照該查詢的 <see cref="PageSortCatalog"/> 白名單，不在白名單就報 invalid_payload 並列出允許的鍵；
/// 搜尋文字只做長度上限，比對哪一欄由查詢決定。
/// </summary>
internal static class PageRequestReader
{
    /// <summary>畫面的查詢一頁最多 200 筆。</summary>
    private const int MaxScreenPageSize = 200;

    public static PageRequest Read(JsonElement payload, PageSortCatalog catalog)
    {
        var cursor = PayloadReader.GetOptionalString(payload, "cursor");
        if (PageCursor.IsMalformed(cursor))
        {
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "cursor_invalid");
        }

        // 總筆數只在第一頁（沒有游標）計算；換頁沿用第一頁的數字，不再每頁重掃完整查詢範圍。
        return new PageRequest(cursor, ReadPageSize(payload),
            ReadSort(payload, catalog), ReadSearch(payload, catalog), IncludeTotalCount: string.IsNullOrEmpty(cursor));
    }

    /// <summary>
    /// 一頁要讀幾筆：沒有帶、0 或負數時用 <paramref name="defaultSize"/>，超過畫面一頁的上限時以上限查詢。
    /// 預覽與開發工具的資料表用 <c>limit</c> 欄位，所以欄名與預設筆數由呼叫端指定。
    /// </summary>
    public static int ReadPageSize(JsonElement payload, string name = "pageSize", int defaultSize = PageRequest.DefaultPageSize)
    {
        var requested = PayloadReader.GetOptionalInt(payload, name);
        return requested is > 0 ? Math.Min(requested.Value, MaxScreenPageSize) : defaultSize;
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
