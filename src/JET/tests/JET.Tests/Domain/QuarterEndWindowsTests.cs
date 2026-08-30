using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

/// <summary>
/// 季末視窗計算（QuarterEndWindows，KCT 清單 A）的純函式測試。
/// oracle：手算曆年四季底（3/31、6/30、9/30、12/31）前 X 天（含季底當日）的視窗，
/// 並只保留與查核期間有交集者。斷言鎖視窗邊界字串集合（值＋身分），非僅筆數。
/// </summary>
public sealed class QuarterEndWindowsTests
{
    private static IReadOnlyList<string> WindowStrings(string start, string end, int days) =>
        QuarterEndWindows.Compute(start, end, days)
            .Select(w => $"{w.FromIso}..{w.ToIso}")
            .ToList();

    [Fact]
    public void Compute_FullYear_ThreeDayWindow_EnumeratesFourCalendarQuarterEnds()
    {
        // X=3 → 含季底當日的最後 3 天 [qEnd-2, qEnd]。
        Assert.Equal(
            ["2025-03-29..2025-03-31", "2025-06-28..2025-06-30",
             "2025-09-28..2025-09-30", "2025-12-29..2025-12-31"],
            WindowStrings("2025-01-01", "2025-12-31", 3));
    }

    [Fact]
    public void Compute_OneDayWindow_IsQuarterEndDayOnly()
    {
        // BVA：X=1（含季底當日）→ 每個視窗退化為季底單日。
        Assert.Equal(
            ["2025-03-31..2025-03-31", "2025-06-30..2025-06-30",
             "2025-09-30..2025-09-30", "2025-12-31..2025-12-31"],
            WindowStrings("2025-01-01", "2025-12-31", 1));
    }

    [Theory]
    [InlineData(0)]   // 下鄰：X<1 → 視窗計算回空（驗證層另以 invalid_scenario 擋下）
    [InlineData(93)]  // 上鄰：X>92 → 空
    public void Compute_WindowDaysOutOfRange_ReturnsEmpty(int days)
    {
        Assert.Empty(QuarterEndWindows.Compute("2025-01-01", "2025-12-31", days));
    }

    [Fact]
    public void Compute_MaxWindowDays_IsAccepted()
    {
        // BVA：X=92（上界）→ 仍產出四個視窗（不空）。
        Assert.Equal(4, QuarterEndWindows.Compute("2025-01-01", "2025-12-31", 92).Count);
    }

    [Fact]
    public void Compute_PeriodSpanningYearEnd_KeepsOnlyIntersectingQuarterEnds()
    {
        // 期間 2025-12-01～2026-02-28、X=2：只有 2025-12-31 的視窗 [12-30,12-31] 與期間相交；
        // 2026-03-31 視窗起點 03-30 已超出期末、2025 前三季底在期初之前 → 排除。
        Assert.Equal(
            ["2025-12-30..2025-12-31"],
            WindowStrings("2025-12-01", "2026-02-28", 2));
    }

    [Fact]
    public void Compute_PeriodBetweenQuarterEnds_ReturnsEmpty()
    {
        // 期間 2025-04-01～2025-05-31 不含任何季底視窗（03-31 在期初前、06-30 視窗在期末後）。
        Assert.Empty(QuarterEndWindows.Compute("2025-04-01", "2025-05-31", 3));
    }

    [Fact]
    public void Compute_InvalidPeriod_ReturnsEmpty()
    {
        // start > end 或日期格式錯誤 → 空（防呆，不丟例外）。
        Assert.Empty(QuarterEndWindows.Compute("2025-12-31", "2025-01-01", 3));
        Assert.Empty(QuarterEndWindows.Compute("2025/01/01", "2025-12-31", 3));
    }
}
