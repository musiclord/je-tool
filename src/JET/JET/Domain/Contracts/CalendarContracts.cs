using System.Globalization;

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
/// 假日／補班日儲存（staging_calendar_raw_day）。
/// Replace 語意：同 day_type 先清空再寫入；兩種 type 互不影響。
/// </summary>
public interface ICalendarStore
{
    Task ReplaceDaysAsync(
        string projectId,
        CalendarDayType type,
        IReadOnlyList<CalendarDayEntry> days,
        CancellationToken cancellationToken);

    Task InvalidateDependentResultsAsync(string projectId, CancellationToken cancellationToken);

    Task<int> CountAsync(string projectId, CalendarDayType type, CancellationToken cancellationToken);
}

/// <summary>事務所行事曆檔的標準欄名(關鍵字命中以 Contains 不分大小寫)。</summary>
public static class CalendarImportColumns
{
    public const string HolidayDate = "Date_of_Holiday";
    public const string HolidayName = "Holiday_Name";
    public const string HolidayFlag = "IS_Holiday";
    public const string MakeupDate = "Date_of_MakeUpday";
    public const string MakeupDesc = "MakeUpDay_Desc";
}

/// <summary>解析出的行事曆欄位(日期必有;名稱/IS_Holiday 可為 null=缺席)。</summary>
public sealed record CalendarImportColumnResolution(string DateColumn, string? NameColumn, string? IsHolidayColumn);

/// <summary>列級投影結果。</summary>
public enum CalendarRowOutcome
{
    Included,
    Skipped,
    Failed
}

/// <summary>列級投影錯誤(列號 + 訊息)。</summary>
public sealed record CalendarImportRowError(int SourceRowNumber, string Message);

/// <summary>
/// 行事曆檔欄位辨識:日期欄以標準名關鍵字命中(必有,否則 projection_failed);
/// 名稱/IS_Holiday 欄選用(缺席回 null)。鏡射 <see cref="AccountMappingColumnResolver"/>。
/// </summary>
public static class CalendarImportColumnResolver
{
    public static CalendarImportColumnResolution Resolve(IReadOnlyList<string> columns, CalendarDayType type)
    {
        var (dateKey, nameKey, flagKey) = type == CalendarDayType.Holiday
            ? (CalendarImportColumns.HolidayDate, CalendarImportColumns.HolidayName, (string?)CalendarImportColumns.HolidayFlag)
            : (CalendarImportColumns.MakeupDate, CalendarImportColumns.MakeupDesc, null);

        var date = FindByKeyword(columns, dateKey);
        if (date is null)
        {
            throw new JetActionException(
                JetErrorCodes.ProjectionFailed,
                $"行事曆檔需含「{dateKey}」欄。");
        }

        var name = FindByKeyword(columns, nameKey);
        var flag = flagKey is null ? null : FindByKeyword(columns, flagKey);
        return new CalendarImportColumnResolution(date, name, flag);
    }

    private static string? FindByKeyword(IReadOnlyList<string> columns, string keyword)
    {
        foreach (var column in columns)
        {
            if (column.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return column;
            }
        }

        return null;
    }
}

/// <summary>
/// 暫存列 → 行事曆日的純函式投影:驗日期(yyyy-MM-dd);holiday 在 IS_Holiday 欄存在時只收 Y;
/// 名稱欄缺/空 → null。Included/Skipped/Failed 三態。
/// </summary>
public static class CalendarDayProjector
{
    public static CalendarRowOutcome Project(
        StagingRow row,
        CalendarDayType type,
        CalendarImportColumnResolution resolution,
        out CalendarDayEntry entry,
        out CalendarImportRowError error)
    {
        entry = null!;
        error = null!;

        row.Values.TryGetValue(resolution.DateColumn, out var rawDate);
        var date = rawDate?.Trim();
        if (string.IsNullOrEmpty(date))
        {
            error = new CalendarImportRowError(row.SourceRowNumber, $"第 {row.SourceRowNumber} 列:日期空白。");
            return CalendarRowOutcome.Failed;
        }

        if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            error = new CalendarImportRowError(
                row.SourceRowNumber, $"第 {row.SourceRowNumber} 列:日期「{date}」非 yyyy-MM-dd 格式。");
            return CalendarRowOutcome.Failed;
        }

        if (type == CalendarDayType.Holiday && resolution.IsHolidayColumn is not null)
        {
            row.Values.TryGetValue(resolution.IsHolidayColumn, out var rawFlag);
            if (!string.Equals(rawFlag?.Trim(), "Y", StringComparison.OrdinalIgnoreCase))
            {
                return CalendarRowOutcome.Skipped;
            }
        }

        string? name = null;
        if (resolution.NameColumn is not null)
        {
            row.Values.TryGetValue(resolution.NameColumn, out var rawName);
            name = string.IsNullOrWhiteSpace(rawName) ? null : rawName.Trim();
        }

        entry = new CalendarDayEntry(date, name);
        return CalendarRowOutcome.Included;
    }
}
