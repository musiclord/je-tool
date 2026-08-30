using System.Text;

namespace JET.Domain;

/// <summary>
/// keyset 分頁 opaque 游標:編碼「上一頁最後一列的排序鍵」。
/// 採 Base64(UTF-8) 以避免鍵內字元(科目代號含 *、傳票號含符號)污染 wire 字串;
/// 純函式、provider 中立。
/// </summary>
public static class PageCursor
{
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
}

/// <summary>分頁請求:opaque 游標 + 頁大小(夾擠 1..MaxPageSize,預設 DefaultPageSize)。</summary>
public sealed record PageRequest(string? Cursor, int PageSize)
{
    public const int DefaultPageSize = 200;
    public const int MaxPageSize = 500;

    public int ClampedPageSize =>
        PageSize <= 0 ? DefaultPageSize : Math.Min(PageSize, MaxPageSize);
}

/// <summary>一頁結果 + 下一頁游標(null 表已到底)。</summary>
public sealed record PageResult<T>(IReadOnlyList<T> Rows, string? NextCursor);
