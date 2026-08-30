using System.Globalization;
using System.Text.RegularExpressions;

namespace JET.Domain;

/// <summary>
/// 日期解析選項。RocYearEnabled 對應 project.json 的 rocDateEnabled（預設啟用，guide §3.1.3）。
/// </summary>
public sealed record DateParseOptions(bool RocYearEnabled)
{
    public static DateParseOptions Default { get; } = new(RocYearEnabled: true);
}

/// <summary>
/// 日期字串正規化（guide §3.1.3 的權威實作）。純函式、provider 無關；輸出一律 "yyyy-MM-dd"。
/// 判定順序固定：ISO → 顯式西元（yyyy/M/d、yyyy.M.d、yyyyMMdd）→ 民國年（開關）→
/// Excel 序列值 → 寬鬆 fallback（年份 1900–2100 sanity guard）。
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
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var serial)
            && serial >= 1 && serial <= 2_958_465) // 9999-12-31 的 OADate 上限
        {
            isoDate = DateTime.FromOADate((double)serial).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }

        // 兩位數年等短年份三段式（11/05/06）無法分辨民國/西元/日月序，一律拒絕（guide §3.1.3）。
        // 民國年關閉時，3 位數年三段式也落入此處被拒絕，而非被吞成西元 0114。
        if (AmbiguousShortYearPattern().IsMatch(text))
        {
            return false;
        }

        // 5. 寬鬆 fallback + 年份 sanity guard
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            && parsed.Year is >= 1900 and <= 2100)
        {
            isoDate = parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }

        return false;
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
