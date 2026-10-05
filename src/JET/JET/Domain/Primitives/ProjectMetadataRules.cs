using System.Globalization;

namespace JET.Domain;

/// <summary>已裁定的案件期間檢查與財報準備日提醒；提醒不改變審計條件的日期語意。</summary>
public static class ProjectMetadataRules
{
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
            return ["期末財報準備日明顯偏離查核期間，請確認年份是否正確。此提醒不影響儲存或後續操作。"];
        return [];
    }
}
