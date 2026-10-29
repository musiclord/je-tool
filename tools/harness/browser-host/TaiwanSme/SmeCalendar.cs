using System.Globalization;

namespace Jet.Harness.TaiwanSme;

/// <summary>
/// 民國 114 年（2025）行政機關辦公日曆：16 個落在平日的放假日與 2 月 8 日補班。
/// 已含同年修法新增的教師節補假（9/29）、光復節補假（10/24）與行憲紀念日（12/25）。
/// </summary>
internal static class SmeCalendar
{
    public static readonly DateOnly PeriodStart = new(2025, 1, 1);
    public static readonly DateOnly PeriodEnd = new(2025, 12, 31);

    /// <summary>期末財報準備日：核准日在這天或之後的分錄屬於「財報準備日起核准」。</summary>
    public static readonly DateOnly PreparationDate = new(2026, 1, 12);

    public static readonly IReadOnlyList<DateOnly> Holidays =
    [
        new(2025, 1, 1), // 開國紀念日
        new(2025, 1, 27), // 春節彈性放假，2/8 補班
        new(2025, 1, 28), // 農曆除夕
        new(2025, 1, 29), // 春節
        new(2025, 1, 30),
        new(2025, 1, 31),
        new(2025, 2, 28), // 和平紀念日
        new(2025, 4, 3), // 兒童節補假
        new(2025, 4, 4), // 兒童節及民族掃墓節
        new(2025, 5, 1), // 勞動節
        new(2025, 5, 30), // 端午節補假
        new(2025, 9, 29), // 教師節補假
        new(2025, 10, 6), // 中秋節
        new(2025, 10, 10), // 國慶日
        new(2025, 10, 24), // 光復節補假
        new(2025, 12, 25), // 行憲紀念日
    ];

    public static readonly IReadOnlyList<DateOnly> MakeupDays = [new(2025, 2, 8)];

    // 2026 年只需要一月到二月上旬的核准日；元旦與春節前後的放假日不當成上班日。
    private static readonly HashSet<DateOnly> Holidays2026 =
    [
        new(2026, 1, 1), new(2026, 2, 16), new(2026, 2, 17), new(2026, 2, 18), new(2026, 2, 19), new(2026, 2, 20),
    ];

    private static readonly HashSet<DateOnly> HolidaySet = [.. Holidays];
    private static readonly HashSet<DateOnly> MakeupSet = [.. MakeupDays];

    public static bool IsWeekend(DateOnly date) => date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    public static bool IsHoliday(DateOnly date) => HolidaySet.Contains(date);

    public static bool IsMakeupDay(DateOnly date) => MakeupSet.Contains(date);

    /// <summary>非營業日：週末或假日，但補班日照常上班。非營業日的卡片用這個定義。</summary>
    public static bool IsNonBusinessDay(DateOnly date) =>
        (IsWeekend(date) || IsHoliday(date)) && !IsMakeupDay(date);

    public static bool IsWorkingDay(DateOnly date) =>
        IsMakeupDay(date) || !(IsWeekend(date) || IsHoliday(date) || Holidays2026.Contains(date));

    public static IReadOnlyList<DateOnly> WorkingDays(int year, int month)
    {
        var days = new List<DateOnly>();
        for (var date = new DateOnly(year, month, 1); date.Month == month; date = date.AddDays(1))
        {
            if (IsWorkingDay(date))
            {
                days.Add(date);
            }
        }

        return days;
    }

    /// <summary>往後數第 n 個上班日；n 為 0 時，當天是上班日就回當天，否則回下一個上班日。</summary>
    public static DateOnly AddWorkingDays(DateOnly date, int n)
    {
        var current = date;
        while (!IsWorkingDay(current))
        {
            current = current.AddDays(1);
        }

        for (var i = 0; i < n; i++)
        {
            current = current.AddDays(1);
            while (!IsWorkingDay(current))
            {
                current = current.AddDays(1);
            }
        }

        return current;
    }

    public static DateOnly MonthEnd(int year, int month) => new(year, month, DateTime.DaysInMonth(year, month));

    public static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
