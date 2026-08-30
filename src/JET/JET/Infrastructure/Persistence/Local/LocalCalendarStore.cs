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
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM staging_calendar_raw_day WHERE day_type = @type;";
            delete.AddWithValue("@type", type.ToStorageName());
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT OR IGNORE INTO staging_calendar_raw_day (day_type, date, day_name)
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

        // 行事曆換版影響週末/假日預篩選,既有規則結果失效(plan Phase 1)。
        await RuleRunResultReset.ClearWithinAsync(
            connection,
            transaction,
            cancellationToken,
            AuditMutation.Calendar);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<int> CountAsync(string projectId, CalendarDayType type, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM staging_calendar_raw_day WHERE day_type = @type;";
        command.AddWithValue("@type", type.ToStorageName());

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task InvalidateDependentResultsAsync(string projectId, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await RuleRunResultReset.ClearWithinAsync(
            connection,
            transaction,
            cancellationToken,
            AuditMutation.Calendar);

        await transaction.CommitAsync(cancellationToken);
    }
}
