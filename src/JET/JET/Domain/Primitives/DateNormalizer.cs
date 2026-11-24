using System.Globalization;
using System.Text.RegularExpressions;

namespace JET.Domain;

/// <summary>
/// 日期解析選項。RocYearEnabled 對應案件設定的 rocDateEnabled（預設啟用），作業用案件資料庫記的值。
/// </summary>
public sealed record DateParseOptions(bool RocYearEnabled)
{
    public static DateParseOptions Default { get; } = new(RocYearEnabled: true);
}

/// <summary>無法辨識的日期是哪一種問題。</summary>
public enum DateProblemKind
{
    /// <summary>只有數字，分不出是年份、序列值還是日期。</summary>
    Number,

    /// <summary>月或日不存在，例如 13 月或 2 月 30 日。</summary>
    NoSuchDate,

    /// <summary>年份只有兩位數或寫在最後，分不出年月日的順序。</summary>
    AmbiguousOrder,

    /// <summary>年份寫成五位數以上，例如 99999/02/01，多半是年份多打了幾位數。</summary>
    YearTooLong,

    /// <summary>其他讀不成日期的內容。</summary>
    NotDate
}

/// <summary>無法辨識的日期的問題種類，以及要標出的那一段在去掉頭尾空白的原文裡的起點與長度。</summary>
public readonly record struct DateProblem(DateProblemKind Kind, int MarkStart, int MarkLength);

/// <summary>
/// 日期字串正規化的權威實作。純函式、provider 無關；輸出一律 "yyyy-MM-dd"。
/// 判定順序固定：補零成四位數的民國年（開關）→ ISO → 顯式西元（yyyy/M/d、yyyy.M.d、yyyyMMdd）→ 民國年（開關）→
/// 純數字一律拒絕 → 寬鬆 fallback（年份 1900 到 2100 的 sanity guard，且原文要寫出四位數年份與日）。
/// 純數字不當成 Excel 序列值：日期型儲存格由讀取器依儲存格格式換成 ISO，
/// 走到這裡的數字是沒套日期格式的儲存格或文字檔裡的數字，分不出是年份還是序列值，一律視為無法辨識。
/// 前端 filter-values.js 的日期清單鏡像同一套規則；兩邊共用 tests/JET.Tests/Fixtures/date-normalization.json 的固定答案。
/// </summary>
public static partial class DateNormalizer
{
    // 樣式裡的數字一律寫成 [0-9]。.NET 的 \d 也認全形數字與其他文字的數字，後面換成整數時卻只收半形，
    // 全形的值會在判斷途中出錯。全形數字不是常見的日期寫法，照無法辨識的日期處理，和前端的日期清單相同。
    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}$")]
    private static partial Regex IsoPattern();

    [GeneratedRegex(@"^([0-9]{4})[/.]([0-9]{1,2})[/.]([0-9]{1,2})$")]
    private static partial Regex ExplicitWesternPattern();

    [GeneratedRegex(@"^(1[0-9]{2})[/.]([0-9]{1,2})[/.]([0-9]{1,2})$")]
    private static partial Regex RocSeparatedPattern();

    [GeneratedRegex(@"^(1[0-9]{2})([0-9]{2})([0-9]{2})$")]
    private static partial Regex RocCompactPattern();

    // 民國年前面補一個 0 寫成四位數：0114/06/11、0114-06-11、01140611。
    [GeneratedRegex(@"^0(1[0-9]{2})[-/.]([0-9]{1,2})[-/.]([0-9]{1,2})$")]
    private static partial Regex RocPaddedSeparatedPattern();

    [GeneratedRegex(@"^0(1[0-9]{2})([0-9]{2})([0-9]{2})$")]
    private static partial Regex RocPaddedCompactPattern();

    [GeneratedRegex(@"^[0-9]{1,3}[/.][0-9]{1,2}[/.][0-9]{1,2}$")]
    private static partial Regex AmbiguousShortYearPattern();

    // 日期旁邊即使帶時間或星期文字，也不能交給 fallback 猜月日順序。
    // 數字邊界避免從 2025/01/02 的年份中間開始配對。
    [GeneratedRegex(@"(?<![0-9])[0-9]{1,2}\s*[-/.]\s*[0-9]{1,2}\s*[-/.]\s*[0-9]{1,4}(?![0-9])")]
    private static partial Regex NumericYearLastPattern();

    // 原文裡寫出來的數字；和冒號相鄰的是時間，不算日期的一部分。
    [GeneratedRegex(@"(?<![0-9:])[0-9]+(?![0-9:])")]
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

        // 0. 補零的民國年（開關）：有些系統把民國年補成四位數，0114 是民國 114 年。要排在西元的判定前面，
        // 否則 0114/06/11 會被當成西元 114 年存進資料庫。只認 0100 到 0199，和三位數民國年的範圍相同；
        // 其他 0 開頭的年份照寫出來的年份處理。月日不合法時直接視為無法辨識，不再往下猜。
        if (options.RocYearEnabled)
        {
            var rocPadded = RocPaddedSeparatedPattern().Match(text);
            if (!rocPadded.Success)
            {
                rocPadded = RocPaddedCompactPattern().Match(text);
            }

            if (rocPadded.Success)
            {
                return TryBuildIsoDate(
                    int.Parse(rocPadded.Groups[1].Value, CultureInfo.InvariantCulture) + 1911,
                    int.Parse(rocPadded.Groups[2].Value, CultureInfo.InvariantCulture),
                    int.Parse(rocPadded.Groups[3].Value, CultureInfo.InvariantCulture),
                    out isoDate);
            }
        }

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

        // 3. 民國年（開關關閉時，7 位數 1140611 落到下面的純數字而拒絕）
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

        // 4. 純數字一律拒絕：把 1 到 2,958,465 的數字當 Excel 序列值換算的話，
        // 2025 會變成 1905-07-17、2025.12 也是 1905 年。沒套日期格式的數字儲存格與文字檔裡的數字，
        // 都在確認配對時列為無法辨識的日期，不當成日期使用。
        // 帶千分位的數字與逗號不在三位一組位置的數字（「12,31」「1,2024」）同樣在這裡拒絕，
        // 不能交給下面的寬鬆解析讀成今年的月日或某年的 1 月 1 日。讀取器對換算不出日期的日期型儲存格
        // 交給投影的序列文字（例如 -5.0）也在這裡拒絕。前端 filter-values.js 的日期清單照同一規則。
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
    // 這些都視為無法辨識的日期，所以解析出的年份要是原文另寫的四位數，
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

    // 只給 .xls 讀取器檢查 ExcelDataReader 換好的日期：1900 年以後的日期直接交出 yyyy-MM-dd，最晚到 9999-12-31。
    // 更早的日期由呼叫端處理，1899-12-31 存成 Excel 顯示的 1900-01-00，其他交出序列值的文字。
    internal static bool IsReadableExcelDate(DateTime date) => date.Year >= 1900;

    // 讀取器對換算不出日期的日期型儲存格交給投影的文字，例如負的序列值。小數點保留數值身分，避免七位數的序列值被讀成民國年月日文字；
    // TryNormalize 把它當成純數字，確認配對時這個值列為無法辨識的日期。
    internal static string ExcelSerialText(double serial) => serial.ToString("0.0################", CultureInfo.InvariantCulture);

    /// <summary>yyyy-MM-dd 的年份不在 1900 到 2100 年之間。這種日期照樣保留，只在確認配對後提醒審計員。</summary>
    internal static bool HasUnusualYear(string isoDate) =>
        string.CompareOrdinal(isoDate, "1900-01-01") < 0 || string.CompareOrdinal(isoDate, "2100-12-31") > 0;

    [GeneratedRegex(@"^([0-9]{3,4})[-/.]([0-9]{1,2})[-/.]([0-9]{1,2})$")]
    private static partial Regex YearFirstPattern();

    [GeneratedRegex(@"^([0-9]{3,4})([0-9]{2})([0-9]{2})$")]
    private static partial Regex YearFirstCompactPattern();

    // 年份寫成五位數以上，後面接著用 -、/ 或 . 寫月日，可以再接時間，例如 99999/02/01、99999-02-01T08:30:00。
    [GeneratedRegex(@"^([0-9]{5,})[-/.][0-9]{1,2}[-/.][0-9]{1,2}(?:[ T][0-9]{1,2}:[0-9]{2}(?::[0-9]{2}(?:\.[0-9]+)?)?)?$")]
    private static partial Regex LongYearFirstPattern();

    /// <summary>
    /// 無法辨識的日期哪裡有問題，給畫面標示用：問題的種類，以及要標出的那一段在去掉頭尾空白的原文裡的位置。
    /// 只在 <see cref="TryNormalize"/> 回 false 之後呼叫。年份寫成五位數以上、後面用 -、/ 或 . 寫月日時標出年份，不猜是哪一年；
    /// 年份寫在前面、月或日不存在時標出月或日；其他情形標出整個值。
    /// </summary>
    internal static DateProblem DescribeUnrecognized(string raw)
    {
        var text = raw.Trim();
        var longYear = LongYearFirstPattern().Match(text);
        if (longYear.Success)
        {
            return new DateProblem(DateProblemKind.YearTooLong, longYear.Groups[1].Index, longYear.Groups[1].Length);
        }

        var separated = YearFirstPattern().Match(text);
        var parts = separated.Success ? separated : YearFirstCompactPattern().Match(text);
        if (parts.Success)
        {
            var month = int.Parse(parts.Groups[2].Value, CultureInfo.InvariantCulture);
            var day = int.Parse(parts.Groups[3].Value, CultureInfo.InvariantCulture);
            if (month is < 1 or > 12)
            {
                return new DateProblem(DateProblemKind.NoSuchDate, parts.Groups[2].Index, parts.Groups[2].Length);
            }

            // 有分隔符號時，月份對了還讀不成日期，就是那個月沒有這一天，例如 2 月 30 日。
            // 沒有分隔符號的一串數字只在日超過 31 時才這樣判斷，其餘照純數字處理。
            if (separated.Success || day is < 1 or > 31)
            {
                return new DateProblem(DateProblemKind.NoSuchDate, parts.Groups[3].Index, parts.Groups[3].Length);
            }
        }

        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
        {
            return new DateProblem(DateProblemKind.Number, 0, text.Length);
        }

        if (AmbiguousShortYearPattern().IsMatch(text) || NumericYearLastPattern().IsMatch(text))
        {
            return new DateProblem(DateProblemKind.AmbiguousOrder, 0, text.Length);
        }

        return new DateProblem(DateProblemKind.NotDate, 0, text.Length);
    }

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
