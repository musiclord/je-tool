using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 行事曆(假日/補班)逐日讀回(本地引擎,匯出底稿 sheet 14)。自 <c>staging_calendar_raw_day</c>
/// 取指定 day_type 的每一日(日期 + 名稱),日期升冪。日數有界(一年百列上下),全載入、不分頁。
/// 讀回的是匯入時 staging 的原樣(<see cref="ICalendarStore.ReplaceDaysAsync"/> 寫入),與計數 <see cref="ICalendarStore.CountAsync"/> 同表。
/// </summary>
public sealed class LocalCalendarExportRepository(ILocalProjectDatabase database)
    : ICalendarExportRepository
{
    public async Task<IReadOnlyList<CalendarDayEntry>> FetchDaysAsync(
        string projectId, CalendarDayType type, CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT date, day_name FROM staging_calendar_raw_day WHERE day_type = @type ORDER BY date;";
        command.AddWithValue("@type", type.ToStorageName());

        var rows = new List<CalendarDayEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CalendarDayEntry(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return rows;
    }
}
