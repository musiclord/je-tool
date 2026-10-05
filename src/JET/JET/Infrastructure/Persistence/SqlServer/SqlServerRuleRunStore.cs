using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// 規則執行結果持久化的 SQL Server 實作(對應 <see cref="LocalRuleRunStore"/>)。
/// SQLite 的 rowid 平手鍵在 T-SQL 無對應,改以 run_id 為次序鍵(generated_utc 為 "O" 100ns 精度,
/// 實務不會平手);LIMIT 1 → TOP 1。
/// </summary>
public sealed class SqlServerRuleRunStore(SqlServerProjectDatabase database) : IRuleRunStore
{
    public async Task SaveAsync(string projectId, RuleRunRecord record, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await SaveWithinAsync(database, connection, transaction, projectId, record, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
    }

    internal static async Task SaveWithinAsync(SqlServerProjectDatabase database, SqlConnection connection,
        SqlTransaction transaction, string projectId, RuleRunRecord record, CancellationToken cancellationToken)
    {
        await using var command = database.CreateCommand(connection, projectId,
            """
            INSERT INTO {s}.result_rule_run (run_id, run_kind, generated_utc, summary_json)
            VALUES (@runId, @runKind, @generatedUtc, @summaryJson);
            """);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@runId", record.RunId);
        command.Parameters.AddWithValue("@runKind", record.RunKind);
        command.Parameters.AddWithValue("@generatedUtc", record.GeneratedUtc.ToString("O"));
        command.Parameters.AddWithValue("@summaryJson", record.SummaryJson);

        await command.ExecuteNonQueryAsync(cancellationToken);
        await ResultStaleStateSql.ClearRuleRunWithinAsync(
            connection,
            transaction,
            record.RunKind,
            cancellationToken,
            SqlServerProjectSchema.QualifierFor(projectId));

    }

    public async Task<RuleRunRecord?> FindLatestAsync(
        string projectId,
        string runKind,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = database.CreateCommand(connection, projectId,
            """
            SELECT TOP 1 run_id, run_kind, generated_utc, summary_json
            FROM {s}.result_rule_run
            WHERE run_kind = @runKind
            ORDER BY generated_utc DESC, run_id DESC;
            """);
        command.Parameters.AddWithValue("@runKind", runKind);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new RuleRunRecord(
            reader.GetString(0),
            reader.GetString(1),
            DateTimeOffset.Parse(reader.GetString(2)),
            reader.GetString(3));
    }
}
