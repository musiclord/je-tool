using System.Globalization;

namespace JET.Domain;

/// <summary>案件期間檢查與財報準備日提醒；提醒不改變審計條件的日期語意。</summary>
public static class ProjectMetadataRules
{
    /// <summary>準備日偏離查核期間時的提醒；建案畫面輸入時的即時提醒照抄這一句。</summary>
    public const string PreparationDateWarning = "期末財報準備日與查核期間不相符，請確認年份；不影響後續操作。";

    /// <summary>查核起始日的年份不在 1900 到 2100 時的訊息；建案畫面送出前的檢查照抄這一句。</summary>
    public const string PeriodStartYearOutOfRange = "查核起始日的年份須在 1900 到 2100 之間。請修正年份後再建立案件。";

    /// <summary>查核截止日的年份不在 1900 到 2100 時的訊息；建案畫面送出前的檢查照抄這一句。</summary>
    public const string PeriodEndYearOutOfRange = "查核截止日的年份須在 1900 到 2100 之間。請修正年份後再建立案件。";

    /// <summary>
    /// 建立案件前檢查查核期間：兩個日期的年份都要在 1900 到 2100，起始日不晚於截止日。
    /// 傳入的日期已經是 yyyy-MM-dd；年份先看，打錯年份時指出的是那一欄，不會被當成起訖順序的問題。
    /// </summary>
    public static void RequireValidPeriod(string periodStart, string periodEnd)
    {
        if (!IsSupportedPeriodYear(periodStart))
            throw new JetActionException(JetErrorCodes.InvalidPayload, PeriodStartYearOutOfRange, "periodStart");
        if (!IsSupportedPeriodYear(periodEnd))
            throw new JetActionException(JetErrorCodes.InvalidPayload, PeriodEndYearOutOfRange, "periodEnd");
        if (string.CompareOrdinal(periodStart, periodEnd) > 0)
            throw new JetActionException(JetErrorCodes.InvalidPayload,
                "查核起始日不可晚於查核截止日。請調整起始日或截止日後再建立案件。", "periodStart");
    }

    // 查核期間建案後不能修改，所以年份明顯打錯時在建案當下擋下。範圍和來源資料的寬鬆日期解析、
    // Excel 日期型儲存格接受的年份相同。
    private static bool IsSupportedPeriodYear(string date)
        => DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture).Year is >= 1900 and <= 2100;

    public static IReadOnlyList<string> GetWarnings(string periodStart, string periodEnd, string? prepStartDate)
    {
        if (prepStartDate is null) return [];
        var start = DateOnly.ParseExact(periodStart, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = DateOnly.ParseExact(periodEnd, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var preparation = DateOnly.ParseExact(prepStartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        // DateOnly.AddYears 保留月日，閏日落在非閏年時用二月最後一天；最大年份不再加一年。
        if (preparation < start || (end.Year < 9999 && preparation > end.AddYears(1)))
            return [PreparationDateWarning];
        return [];
    }
}
