namespace JET.Infrastructure;

/// <summary>數值 cell 的數字格式語意：一般數值 / 日期（含日期時間）/ 純時間。</summary>
public enum ExcelNumberKind
{
    None,
    Date,
    Time
}

/// <summary>
/// Excel 數字格式的日期/時間判定（guide §3.1.5）。
/// 內建 id 依 ECMA-376 §18.8.30 登錄表凍結；自訂格式碼掃描日期/時間記號，
/// 引號字面值、[..] 條件/色彩/地區區段、反斜線跳脫與 _/* 填充字元都不算記號。
/// 純函式：reader 解析 styles.xml 後以本類別建 style index → kind 對照表。
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
                    // [..] 區段通常是條件/色彩/地區（[Red]、[$-404]、[<=100]），不算記號；
                    // 唯一例外：內容全為 h/m/s 的經過時間 token（[h]、[mm]、[s]）
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
                    // 跳脫字元 / 對齊填充：下一個字元是字面值
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
