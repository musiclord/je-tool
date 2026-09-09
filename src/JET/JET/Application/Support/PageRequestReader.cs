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
            throw new JetActionException(JetErrorCodes.InvalidPayload, "cursor 格式不符(無法解碼)。");
        }

        var pageSize = PayloadReader.GetOptionalInt(payload, "pageSize") ?? PageRequest.DefaultPageSize;
        return new PageRequest(cursor, pageSize, ReadSort(payload, catalog), ReadSearch(payload, catalog));
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
            throw new JetActionException(JetErrorCodes.InvalidPayload, "sort 必須是 { key, direction } 物件。");
        }

        var key = PayloadReader.GetOptionalString(sort, "key")
            ?? throw new JetActionException(JetErrorCodes.InvalidPayload, "sort.key 必填。");
        if (catalog.Find(key) is null)
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                $"排序鍵「{key}」無效，允許：{string.Join("、", catalog.Keys)}。");
        }

        var direction = PayloadReader.GetOptionalString(sort, "direction") ?? "asc";
        return direction switch
        {
            "asc" => new PageSort(key, PageSortDirection.Ascending),
            "desc" => new PageSort(key, PageSortDirection.Descending),
            _ => throw new JetActionException(JetErrorCodes.InvalidPayload, "sort.direction 只能是 asc 或 desc。")
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
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                $"search 最多 {PageRequest.MaxSearchLength} 個字。");
        }

        return search;
    }
}
