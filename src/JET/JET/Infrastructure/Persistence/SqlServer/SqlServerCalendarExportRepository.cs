using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// 行事曆(假日/補班)逐日讀回(SQL Server,鏡像 <see cref="LocalCalendarExportRepository"/>;匯出底稿 sheet 14)。
/// 自 <c>staging_calendar_raw_day</c> 取指定 day_type 的每一日,日期升冪。日數有界,全載入、不分頁。
/// </summary>
public sealed class SqlServerCalendarExportRepository(SqlServerProjectDatabase database)
    : ICalendarExportRepository
{
    public async Task<IReadOnlyList<CalendarDayEntry>> FetchDaysAsync(
        string projectId, CalendarDayType type, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = database.CreateCommand(connection, projectId,
            "SELECT date, day_name FROM {s}.staging_calendar_raw_day WHERE day_type = @type ORDER BY date;");
        command.Parameters.AddWithValue("@type", type.ToStorageName());

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
