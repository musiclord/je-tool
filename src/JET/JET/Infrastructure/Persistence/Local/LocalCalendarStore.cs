using System.Data;
using JET.Domain;

namespace JET.Infrastructure;

public sealed class LocalCalendarStore(ILocalProjectDatabase database) : ICalendarStore
{
    public async Task ReplaceDaysAsync(
        string projectId,
        CalendarDayType type,
        IReadOnlyList<CalendarDayEntry> days,
        CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM calendar_day WHERE day_type = @type;";
            delete.AddWithValue("@type", type.ToStorageName());
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT OR IGNORE INTO calendar_day (day_type, date, day_name)
                VALUES (@type, @date, @name);
                """;

            var typeParam = insert.AddParameter("@type", DbType.String);
            var dateParam = insert.AddParameter("@date", DbType.String);
            var nameParam = insert.AddParameter("@name", DbType.String);
            typeParam.Value = type.ToStorageName();

            foreach (var day in days)
            {
                dateParam.Value = day.Date;
                nameParam.Value = (object?)day.Name ?? DBNull.Value;
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        // 國定假日與補班日換了，會影響週末與假日的預篩選規則，已有的規則結果跟著失效，在同一個交易裡清除。
        await ResultReset.ClearWithinAsync(
            connection,
            transaction,
            cancellationToken,
            AuditMutation.Calendar);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<int> CountAsync(string projectId, CalendarDayType type, CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM calendar_day WHERE day_type = @type;";
        command.AddWithValue("@type", type.ToStorageName());

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task InvalidateDependentResultsAsync(
        string projectId,
        IReadOnlyList<int> nonWorkingDays,
        Func<CancellationToken, Task> saveSettings,
        CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await ResultReset.ClearWithinAsync(
            connection,
            transaction,
            cancellationToken,
            AuditMutation.Calendar);
        await LocalProjectSettingSql.UpdateNonWorkingDaysAsync(connection, transaction, nonWorkingDays, cancellationToken);
        // 設定檔存不進去時直接離開，交易在釋放時回復。
        await saveSettings(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }
}
