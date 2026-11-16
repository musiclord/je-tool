using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 逐日讀出國定假日或補班日，供工作底稿的「自動化工具-假期假日資訊」工作表（第 14 張）使用。從 <c>calendar_day</c>
/// 取指定 day_type 的每一天（日期與名稱），依日期升冪。天數不多（一年一百列上下），一次全部讀進來，不分頁。
/// 讀到的就是匯入時 <see cref="ICalendarStore.ReplaceDaysAsync"/> 寫進去的內容，和 <see cref="ICalendarStore.CountAsync"/> 計數的是同一張表。
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
            "SELECT date, day_name FROM calendar_day WHERE day_type = @type ORDER BY date;";
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
