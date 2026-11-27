namespace JET.Domain;

/// <summary>
/// 來源文字的整理規則，只有這一處定義。來源內容照匯入時的樣子保存，這裡只做兩件事：去掉頭尾空白，
/// 以及在比對時不分大小寫。全形半形、字元中間的空白與其他內容一律不改。
/// 去掉的空白字元是 <see cref="TextWhitespace"/> 列出的那一組，資料庫端的 TRIM 用同一組字元。
/// </summary>
public static class SourceText
{
    /// <summary>去掉頭尾空白，其餘內容不變。</summary>
    public static string Trim(string value) => value.Trim();

    /// <summary>
    /// 文字的比較鍵：去掉頭尾空白後用 ToUpperInvariant 轉成大寫。資料庫端對欄位取 UPPER(TRIM(...))，
    /// 條件值與搜尋文字用這個方法，兩邊才會算出相同的鍵。只用來比對，不拿來顯示或存回資料。
    /// </summary>
    public static string ComparisonKey(string value) => Trim(value).ToUpperInvariant();
}
