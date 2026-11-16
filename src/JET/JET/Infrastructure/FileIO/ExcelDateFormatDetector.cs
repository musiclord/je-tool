namespace JET.Infrastructure;

/// <summary>數值儲存格的數字格式代表哪一種值：一般數值、日期（含日期加時間），或只有時間。</summary>
public enum ExcelNumberKind
{
    None,
    Date,
    Time
}

/// <summary>
/// 判斷 Excel 的數字格式是日期還是時間。
/// 內建格式的 id 照 ECMA-376 §18.8.30 的表寫死在程式裡。自訂格式碼則逐字找日期與時間的記號；
/// 引號裡的文字、中括號裡的條件、顏色與地區設定、反斜線後面的字元，以及 _ 與 * 後面的填充字元都不算記號。
/// 這個類別不保存狀態。讀取器解析 styles.xml 之後，用它建立樣式索引對應 <see cref="ExcelNumberKind"/> 的對照表。
/// </summary>
public static class ExcelDateFormatDetector
{
    public static ExcelNumberKind Classify(int numFmtId, IReadOnlyDictionary<int, string> customFormats)
    {
        switch (numFmtId)
        {
            case >= 14 and <= 17:
            case 22:
            case >= 27 and <= 36:
            case >= 50 and <= 58:
                return ExcelNumberKind.Date;

            case >= 18 and <= 21:
            case >= 45 and <= 47:
                return ExcelNumberKind.Time;
        }

        return customFormats.TryGetValue(numFmtId, out var formatCode)
            ? ClassifyFormatCode(formatCode)
            : ExcelNumberKind.None;
    }

    public static ExcelNumberKind ClassifyFormatCode(string formatCode)
    {
        var hasYearOrDay = false;
        var hasMonth = false;
        var hasHourOrSecond = false;

        for (var i = 0; i < formatCode.Length; i++)
        {
            var ch = formatCode[i];

            switch (ch)
            {
                case '"':
                {
                    // 引號字面值：跳到對應的關閉引號
                    var close = formatCode.IndexOf('"', i + 1);
                    i = close < 0 ? formatCode.Length : close;
                    continue;
                }

                case '[':
                {
                    // 中括號裡通常是顏色、地區或條件，例如 [Red]、[$-404]、[<=100]，不算記號。
                    // 唯一的例外是內容全是 h、m、s 的經過時間寫法，例如 [h]、[mm]、[s]，算時間記號。
                    var close = formatCode.IndexOf(']', i + 1);
                    if (close < 0)
                    {
                        i = formatCode.Length;
                        continue;
                    }

                    var section = formatCode.AsSpan(i + 1, close - i - 1);
                    var isElapsedTimeToken = section.Length > 0;
                    foreach (var c in section)
                    {
                        if (char.ToLowerInvariant(c) is not ('h' or 'm' or 's'))
                        {
                            isElapsedTimeToken = false;
                            break;
                        }
                    }

                    if (isElapsedTimeToken)
                    {
                        hasHourOrSecond = true;
                    }

                    i = close;
                    continue;
                }

                case '\\':
                case '_':
                case '*':
                    // 反斜線是跳脫，_ 與 * 是對齊與填充；它們後面的那個字元是一般文字，跳過不看。
                    i++;
                    continue;
            }

            switch (char.ToLowerInvariant(ch))
            {
                // 不含地區年代記號 'e'：會與科學記號 0.00E+00 的 E 衝突，且 y/d 已涵蓋實務日期格式
                case 'y' or 'd':
                    hasYearOrDay = true;
                    break;
                case 'm':
                    hasMonth = true;
                    break;
                case 'h' or 's':
                    hasHourOrSecond = true;
                    break;
            }
        }

        if (hasYearOrDay)
        {
            return ExcelNumberKind.Date;
        }

        if (hasHourOrSecond)
        {
            return ExcelNumberKind.Time; // m 與 h/s 同現時是「分」，不是「月」
        }

        return hasMonth ? ExcelNumberKind.Date : ExcelNumberKind.None;
    }
}
