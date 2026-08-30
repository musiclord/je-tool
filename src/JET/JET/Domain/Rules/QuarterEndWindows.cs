namespace JET.Domain;

/// <summary>
/// 條件 A「季末前 X 天借記收入」(filter type revenueDebitNearQuarterEnd) 的視窗計算（純函式）。
/// 給定查核期間與天數 X，列出各曆年季底（3/31、6/30、9/30、12/31）前 X 天
/// （含季底當日，即季底回推 X−1 天）的日期視窗；只保留與查核期間有交集的視窗。
/// 回傳 ISO yyyy-MM-dd 邊界字串，由 Infrastructure 以參數綁定組成 OR 述詞
/// （SQL 識別字不來自此處，符合 guide §1.5.2 參數化要求）。日期邏輯純 C#、provider 無關。
/// </summary>
public static class QuarterEndWindows
{
    public const int MinWindowDays = 1;

    /// <summary>一季約 92 天；超過即跨季重疊，無審計意義，作為輸入上界。</summary>
    public const int MaxWindowDays = 92;

    private static readonly (int Month, int Day)[] CalendarQuarterEnds =
        [(3, 31), (6, 30), (9, 30), (12, 31)];

    public sealed record Window(string FromIso, string ToIso);

    public static IReadOnlyList<Window> Compute(string periodStart, string periodEnd, int windowDays)
    {
        if (windowDays < MinWindowDays || windowDays > MaxWindowDays
            || !DateOnly.TryParseExact(periodStart, "yyyy-MM-dd", out var start)
            || !DateOnly.TryParseExact(periodEnd, "yyyy-MM-dd", out var end)
            || start > end)
        {
            return [];
        }

        var windows = new List<Window>();
        for (var year = start.Year; year <= end.Year; year++)
        {
            foreach (var (month, day) in CalendarQuarterEnds)
            {
                var quarterEnd = new DateOnly(year, month, day);
                var quarterWindowStart = quarterEnd.AddDays(-(windowDays - 1));

                // 只保留與查核期間 [start, end] 有交集的視窗（否則為永不命中的多餘子句）。
                if (quarterEnd < start || quarterWindowStart > end)
                {
                    continue;
                }

                windows.Add(new Window(
                    quarterWindowStart.ToString("yyyy-MM-dd"),
                    quarterEnd.ToString("yyyy-MM-dd")));
            }
        }

        return windows;
    }
}
