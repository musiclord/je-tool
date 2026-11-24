namespace JET.Domain;

public enum CalendarDayType
{
    Holiday,
    Makeup
}

public static class CalendarDayTypeNames
{
    public static string ToStorageName(this CalendarDayType type) => type switch
    {
        CalendarDayType.Holiday => "holiday",
        CalendarDayType.Makeup => "makeup",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };
}

/// <summary>一筆行事曆日:日期(yyyy-MM-dd)+ 可選名稱(假日名稱/補班說明)。</summary>
public sealed record CalendarDayEntry(string Date, string? Name);

/// <summary>
/// 假日／補班日儲存（calendar_day）。
/// Replace 語意：同 day_type 先清空再寫入；兩種 type 互不影響。
/// </summary>
public interface ICalendarStore
{
    Task ReplaceDaysAsync(
        string projectId,
        CalendarDayType type,
        IReadOnlyList<CalendarDayEntry> days,
        CancellationToken cancellationToken);

    /// <summary>
    /// 改每週休息日：同一個交易裡清除依休息日算出的結果，並把 <paramref name="nonWorkingDays"/> 記進案件資料庫；
    /// 提交前先執行 <paramref name="saveSettings"/>（寫入設定檔），它失敗時交易回復，資料庫不變。
    /// </summary>
    Task InvalidateDependentResultsAsync(
        string projectId,
        IReadOnlyList<int> nonWorkingDays,
        Func<CancellationToken, Task> saveSettings,
        CancellationToken cancellationToken);

    Task<int> CountAsync(string projectId, CalendarDayType type, CancellationToken cancellationToken);
}

/// <summary>國定假日清單與補班日清單的標準欄名。檔案的欄名包含它就算，不分大小寫，也不計較空白、底線與連字號。</summary>
public static class CalendarImportColumns
{
    public const string HolidayDate = "Date_of_Holiday";
    public const string HolidayName = "Holiday_Name";
    public const string HolidayFlag = "IS_Holiday";
    public const string MakeupDate = "Date_of_MakeUpday";
    public const string MakeupDesc = "MakeUpDay_Desc";
}
