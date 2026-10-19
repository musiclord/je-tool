using System.Globalization;

namespace JET.Domain;

/// <summary>已裁定的案件期間檢查與財報準備日提醒；提醒不改變審計條件的日期語意。</summary>
public static class ProjectMetadataRules
{
    /// <summary>準備日偏離查核期間時的提醒（使用者 2026-10-13 勾選頁確認）；建案畫面輸入時的即時提醒照抄這一句。</summary>
    public const string PreparationDateWarning = "期末財報準備日與查核期間不相符，請確認年份；不影響後續操作。";

    public static void RequireValidPeriod(string periodStart, string periodEnd)
    {
        if (string.CompareOrdinal(periodStart, periodEnd) > 0)
            throw new JetActionException(JetErrorCodes.InvalidPayload,
                "查核起始日不可晚於查核截止日。請調整起始日或截止日後再建立案件。", "periodStart");
    }

    public static IReadOnlyList<string> GetWarnings(string periodStart, string periodEnd, string? lastPeriodStart)
    {
        if (lastPeriodStart is null) return [];
        var start = DateOnly.ParseExact(periodStart, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = DateOnly.ParseExact(periodEnd, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var preparation = DateOnly.ParseExact(lastPeriodStart, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        // DateOnly.AddYears 保留月日，閏日落在非閏年時用二月最後一天；最大年份不再加一年。
        if (preparation < start || (end.Year < 9999 && preparation > end.AddYears(1)))
            return [PreparationDateWarning];
        return [];
    }
}
