using System.Globalization;
using System.Text.RegularExpressions;

namespace JET.Domain;

/// <summary>
/// 日期解析選項。RocYearEnabled 對應 project.json 的 rocDateEnabled（預設啟用）。
/// </summary>
public sealed record DateParseOptions(bool RocYearEnabled)
{
    public static DateParseOptions Default { get; } = new(RocYearEnabled: true);
}

/// <summary>
/// 日期字串正規化的權威實作。純函式、provider 無關；輸出一律 "yyyy-MM-dd"。
/// 判定順序固定：ISO → 顯式西元（yyyy/M/d、yyyy.M.d、yyyyMMdd）→ 民國年（開關）→
/// Excel 序列值 → 寬鬆 fallback（年份 1900–2100 sanity guard，且原文要寫出四位數年份與日）。
/// 7 位數民國年與 Excel 序列值範圍重疊，民國年判定必須在序列值之前。
/// </summary>
public static partial class DateNormalizer
{
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex IsoPattern();

    [GeneratedRegex(@"^(\d{4})[/.](\d{1,2})[/.](\d{1,2})$")]
    private static partial Regex ExplicitWesternPattern();

    [GeneratedRegex(@"^(1\d{2})[/.](\d{1,2})[/.](\d{1,2})$")]
    private static partial Regex RocSeparatedPattern();

    [GeneratedRegex(@"^(1\d{2})(\d{2})(\d{2})$")]
    private static partial Regex RocCompactPattern();

    [GeneratedRegex(@"^\d{1,3}[/.]\d{1,2}[/.]\d{1,2}$")]
    private static partial Regex AmbiguousShortYearPattern();

    // 日期旁邊即使帶時間或星期文字，也不能交給 fallback 猜月日順序。
    // 數字邊界避免從 2025/01/02 的年份中間開始配對。
    [GeneratedRegex(@"(?<!\d)\d{1,2}\s*[-/.]\s*\d{1,2}\s*[-/.]\s*\d{1,4}(?!\d)")]
    private static partial Regex NumericYearLastPattern();

    // 原文裡寫出來的數字；和冒號相鄰的是時間，不算日期的一部分。
    [GeneratedRegex(@"(?<![\d:])\d+(?![\d:])")]
    private static partial Regex DatePartNumberPattern();

    // 寬鬆解析認得的英文月份全名與縮寫。
    [GeneratedRegex(@"(?<![A-Za-z])(?:jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|june?|july?|aug(?:ust)?|sep(?:tember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)(?![A-Za-z])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MonthNamePattern();

    /// <summary>空白視為「無日期」（合法，isoDate = null）；非空但無法判定 → false。</summary>
    public static bool TryNormalize(string? raw, DateParseOptions options, out string? isoDate)
    {
        isoDate = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var text = raw.Trim();

        // 1. ISO 直通
        if (IsoPattern().IsMatch(text)
            && DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            isoDate = text;
            return true;
        }

        // 2. 顯式西元：yyyy/M/d、yyyy.M.d
        var western = ExplicitWesternPattern().Match(text);
        if (western.Success)
        {
            return TryBuildIsoDate(
                int.Parse(western.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(western.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(western.Groups[3].Value, CultureInfo.InvariantCulture),
                out isoDate);
        }

        // 2. 顯式西元：8 位數 yyyyMMdd
        if (text.Length == 8
            && DateTime.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var compact))
        {
            isoDate = compact.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }

        // 3. 民國年（必須先於 Excel 序列值：7 位數 1140611 落在 OADate 範圍內）
        if (options.RocYearEnabled)
        {
            var rocSeparated = RocSeparatedPattern().Match(text);
            if (rocSeparated.Success)
            {
                return TryBuildIsoDate(
                    int.Parse(rocSeparated.Groups[1].Value, CultureInfo.InvariantCulture) + 1911,
                    int.Parse(rocSeparated.Groups[2].Value, CultureInfo.InvariantCulture),
                    int.Parse(rocSeparated.Groups[3].Value, CultureInfo.InvariantCulture),
                    out isoDate);
            }

            var rocCompact = RocCompactPattern().Match(text);
            if (rocCompact.Success)
            {
                return TryBuildIsoDate(
                    int.Parse(rocCompact.Groups[1].Value, CultureInfo.InvariantCulture) + 1911,
                    int.Parse(rocCompact.Groups[2].Value, CultureInfo.InvariantCulture),
                    int.Parse(rocCompact.Groups[3].Value, CultureInfo.InvariantCulture),
                    out isoDate);
            }
        }

        // 4. Excel 序列值（reader 通常已轉 ISO，這裡涵蓋未套日期格式的數字 cell）
        // 帶千分位的數字不當序列值（使用者 2026-10-19 裁定）：「1,234」以前會換成 1903 年再以期間外排除，
        // 現在和其他無法辨識的日期一樣，在確認配對時列出列號。前端 filter-values.js 的日期清單照同一規則。
        if (decimal.TryParse(text, NumberStyles.Number & ~NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var serial))
        {
            if (serial < 1 || serial > 2_958_465) return false;
            var date = DateTime.FromOADate((double)serial);
            if (!IsSupportedExcelDate(date)) return false;
            isoDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }

        // 同一裁定：逗號不在三位一組位置的數字（「12,31」「1,2024」）也是帶千分位的數字，
        // 不能交給下面的寬鬆解析讀成今年的月日或某年的 1 月 1 日。
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
        {
            return false;
        }

        // 兩位數年等短年份三段式（11/05/06）無法分辨民國/西元/日月序，一律拒絕。
        // 民國年關閉時，3 位數年三段式也落入此處被拒絕，而非被吞成西元 0114。
        if (AmbiguousShortYearPattern().IsMatch(text) || NumericYearLastPattern().IsMatch(text))
        {
            return false;
        }

        // 5. 寬鬆 fallback + 年份 sanity guard
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            && parsed.Year is >= 1900 and <= 2100
            && WritesYearAndDay(text, parsed))
        {
            isoDate = parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }

    // 寬鬆解析會自行補上缺少的部分：沒寫年份補成執行當年，沒寫日補成 1 日，一兩位數也會被讀成年份。
    // 使用者 2026-10-19 裁定這些都視為無法辨識的日期，所以解析出的年份要是原文另寫的四位數，
    // 日也要是原文另寫的數字；月份用數字寫時，日不能和月份共用同一個數字。
    private static bool WritesYearAndDay(string text, DateTime parsed)
    {
        var numbers = new List<string>();
        foreach (Match match in DatePartNumberPattern().Matches(text))
        {
            numbers.Add(match.Value);
        }

        var yearIndex = numbers.FindIndex(number =>
            number.Length == 4 && int.Parse(number, CultureInfo.InvariantCulture) == parsed.Year);
        if (yearIndex < 0)
        {
            return false;
        }

        numbers.RemoveAt(yearIndex);
        if (!MonthNamePattern().IsMatch(text))
        {
            var monthIndex = numbers.FindIndex(number => IsWritten(number, parsed.Month));
            if (monthIndex < 0)
            {
                return false;
            }

            numbers.RemoveAt(monthIndex);
        }

        return numbers.Exists(number => IsWritten(number, parsed.Day));

        static bool IsWritten(string number, int value) =>
            number.Length <= 2 && int.Parse(number, CultureInfo.InvariantCulture) == value;
    }

    // 僅限制 Excel 序列值；明確西元與民國文字日期仍依上面的格式規則處理。
    internal static bool IsSupportedExcelDate(DateTime date) => date.Year is >= 1900 and <= 2100;

    // 小數點保留數值身分，避免超限的七位序列在下一層被誤讀成民國年月日文字。
    internal static string ExcelSerialText(double serial) => serial.ToString("0.0################", CultureInfo.InvariantCulture);

    private static bool TryBuildIsoDate(int year, int month, int day, out string? isoDate)
    {
        isoDate = null;

        if (year < 1 || year > 9999 || month < 1 || month > 12
            || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        isoDate = new DateTime(year, month, day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return true;
    }
}
