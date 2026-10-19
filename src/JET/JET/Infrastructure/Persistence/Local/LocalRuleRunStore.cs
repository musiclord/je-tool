using JET.Domain;

namespace JET.Infrastructure;

public sealed class LocalRuleRunStore(ILocalProjectDatabase database) : IRuleRunStore
{
    public async Task SaveAsync(string projectId, RuleRunRecord record, CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await SaveWithinAsync(connection, transaction, record, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
    }

    internal static async Task SaveWithinAsync(System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction, RuleRunRecord record, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO result_rule_run (run_id, run_kind, generated_utc, summary_json)
            VALUES (@runId, @runKind, @generatedUtc, @summaryJson);
            """;
        command.AddWithValue("@runId", record.RunId);
        command.AddWithValue("@runKind", record.RunKind);
        command.AddWithValue("@generatedUtc", record.GeneratedUtc.ToString("O"));
        command.AddWithValue("@summaryJson", record.SummaryJson);

        await command.ExecuteNonQueryAsync(cancellationToken);
        await ResultStaleStateSql.ClearRuleRunWithinAsync(
            connection,
            transaction,
            record.RunKind,
            cancellationToken,
            schemaPrefix: string.Empty);
    }

    public async Task<RuleRunRecord?> FindLatestAsync(
        string projectId,
        string runKind,
        CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        // generated_utc 記到 100 奈秒，兩次執行幾乎不可能同時；真的同時就以 run_id 當平手鍵，
        // 和 SqlServerRuleRunStore 相同，三種資料庫選到同一筆（2026-10-19 跨資料庫一致性第二階段）。
        command.CommandText =
            """
            SELECT run_id, run_kind, generated_utc, summary_json
            FROM result_rule_run
            WHERE run_kind = @runKind
            ORDER BY generated_utc DESC, run_id DESC
            LIMIT 1;
            """;
        command.AddWithValue("@runKind", runKind);

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
