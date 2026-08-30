using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// 行事曆(假日/補班日)的 SQL Server 實作(對應 <see cref="LocalCalendarStore"/>)。
/// SQLite 的 INSERT OR IGNORE 在 T-SQL 改為「輸入先去重再 INSERT」(ReplaceDays 已先 DELETE 同型別)。
/// 換版後清規則結果(行事曆影響週末/假日預篩選),與 SQLite 一致。
/// </summary>
public sealed class SqlServerCalendarStore(SqlServerProjectDatabase database) : ICalendarStore
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
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var delete = database.CreateCommand(connection, projectId,
            "DELETE FROM {s}.staging_calendar_raw_day WHERE day_type = @type;"))
        {
            delete.Transaction = transaction;
            delete.Parameters.AddWithValue("@type", type.ToStorageName());
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insert = database.CreateCommand(connection, projectId,
            """
            INSERT INTO {s}.staging_calendar_raw_day (day_type, date, day_name)
            VALUES (@type, @date, @name);
            """))
        {
            insert.Transaction = transaction;

            var typeParam = insert.Parameters.Add("@type", System.Data.SqlDbType.NVarChar, 10);
            var dateParam = insert.Parameters.Add("@date", System.Data.SqlDbType.NVarChar, 32);
            var nameParam = insert.Parameters.Add("@name", System.Data.SqlDbType.NVarChar, 256);
            typeParam.Value = type.ToStorageName();

            // INSERT OR IGNORE 等價:先 DELETE 全清,故只需對輸入以日期去重避免本批 (day_type,date) PK 衝突。
            foreach (var day in days.DistinctBy(d => d.Date))
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
            AuditMutation.Calendar,
            SqlServerProjectSchema.QualifierFor(projectId));

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<int> CountAsync(string projectId, CalendarDayType type, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = database.CreateCommand(connection, projectId,
            "SELECT COUNT(*) FROM {s}.staging_calendar_raw_day WHERE day_type = @type;");
        command.Parameters.AddWithValue("@type", type.ToStorageName());

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task InvalidateDependentResultsAsync(string projectId, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await RuleRunResultReset.ClearWithinAsync(
            connection,
            transaction,
            cancellationToken,
            AuditMutation.Calendar,
            SqlServerProjectSchema.QualifierFor(projectId));

        await transaction.CommitAsync(cancellationToken);
    }
}
