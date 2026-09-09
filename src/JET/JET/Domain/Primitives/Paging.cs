using System.Text;

namespace JET.Domain;

/// <summary>
/// keyset 分頁 opaque 游標:編碼「上一頁最後一列的排序鍵」。
/// 採 Base64(UTF-8) 以避免鍵內字元(科目代號含 *、傳票號含符號)污染 wire 字串;
/// 純函式、provider 中立。
///
/// 沒有排序時游標只裝穩定鍵（傳票號碼、科目編號或 entry_id）。有排序時用複合游標：排序鍵名、方向、
/// 排序值是否空、排序值、穩定鍵，用不可見的分隔字元接起來再 Base64；解碼時核對鍵名與方向，
/// 換了排序就不能沿用舊游標。
/// </summary>
public static class PageCursor
{
    private const char Separator = '\u001f';
    private const string CompositeTag = "c";

    public static string Encode(string key) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(key));

    /// <summary>null/空 → false(首頁,無游標述詞);格式不符 → false(由 handler 視為首頁或報參數錯,不靜默崩潰)。</summary>
    public static bool TryDecode(string? cursor, out string key)
    {
        key = string.Empty;
        if (string.IsNullOrEmpty(cursor))
        {
            return false;
        }

        try
        {
            key = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// 壞游標判定:有傳 cursor(非 null、非空)但無法解碼。
    /// 首頁(null/空)不算壞。handler 據此 fail loud(invalid_payload),不靜默重置為首頁
    /// (對齊 manifest/jet-guide:游標格式不符讓 handler 報參數錯)。
    /// </summary>
    public static bool IsMalformed(string? cursor) =>
        !string.IsNullOrEmpty(cursor) && !TryDecode(cursor, out _);

    public static string EncodeComposite(PageCursorKey key) =>
        Encode(string.Join(
            Separator,
            CompositeTag,
            key.SortKey,
            key.Direction == PageSortDirection.Descending ? "desc" : "asc",
            key.SortIsNull ? "1" : "0",
            key.SortValue,
            key.StableKey));

    /// <summary>解碼後的字串是不是複合游標（帶排序）。</summary>
    public static bool IsComposite(string decoded) =>
        decoded.StartsWith(CompositeTag + Separator, StringComparison.Ordinal);

    public static bool TryDecodeComposite(string? cursor, out PageCursorKey key)
    {
        key = null!;
        if (!TryDecode(cursor, out var decoded) || !IsComposite(decoded))
        {
            return false;
        }

        var parts = decoded.Split(Separator, 6);
        if (parts.Length != 6)
        {
            return false;
        }

        PageSortDirection direction;
        switch (parts[2])
        {
            case "asc": direction = PageSortDirection.Ascending; break;
            case "desc": direction = PageSortDirection.Descending; break;
            default: return false;
        }

        key = new PageCursorKey(parts[1], direction, parts[3] == "1", parts[4], parts[5]);
        return true;
    }
}

/// <summary>複合游標的內容：排序鍵名、方向、上一頁末列的排序值（是否空、值）與穩定鍵。</summary>
public sealed record PageCursorKey(
    string SortKey,
    PageSortDirection Direction,
    bool SortIsNull,
    string SortValue,
    string StableKey);

public enum PageSortDirection
{
    Ascending,
    Descending
}

/// <summary>排序要求：<paramref name="Key"/> 是各查詢白名單裡的鍵名，只用來查表，永遠不會拼進 SQL。</summary>
public sealed record PageSort(string Key, PageSortDirection Direction);

/// <summary>
/// 分頁請求:opaque 游標 + 頁大小(夾擠 1..MaxPageSize,預設 DefaultPageSize)。
/// <paramref name="Sort"/> 省略時用該查詢的穩定鍵升冪；<paramref name="Search"/> 是「依傳票號碼查看」或
/// 「依科目編號查看」的文字，由各查詢決定比對哪一欄，省略或空白表示不過濾。
/// </summary>
public sealed record PageRequest(string? Cursor, int PageSize, PageSort? Sort = null, string? Search = null)
{
    public const int DefaultPageSize = 200;
    public const int MaxPageSize = 500;
    public const int MaxSearchLength = 200;

    public int ClampedPageSize =>
        PageSize <= 0 ? DefaultPageSize : Math.Min(PageSize, MaxPageSize);
}

/// <summary>一頁結果 + 下一頁游標(null 表已到底)。</summary>
public sealed record PageResult<T>(IReadOnlyList<T> Rows, string? NextCursor);
