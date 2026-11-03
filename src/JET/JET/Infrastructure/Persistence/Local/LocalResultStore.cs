using JET.Domain;

namespace JET.Infrastructure;

/// <summary>驗證結果與預篩選結果的本機存放；兩種結果各寫一張表，表名由 <see cref="ResultTables"/> 決定。</summary>
public sealed class LocalResultStore(ILocalProjectDatabase database) : IResultStore
{
    public async Task SaveAsync(string projectId, ResultRecord record, CancellationToken cancellationToken)
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
        System.Data.Common.DbTransaction transaction, ResultRecord record, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            INSERT INTO {ResultTables.For(record.Kind)} (result_id, generated_utc, summary_json)
            VALUES (@resultId, @generatedUtc, @summaryJson);
            """;
        command.AddWithValue("@resultId", record.ResultId);
        command.AddWithValue("@generatedUtc", record.GeneratedUtc.ToString("O"));
        command.AddWithValue("@summaryJson", record.SummaryJson);

        await command.ExecuteNonQueryAsync(cancellationToken);
        await ResultStaleStateSql.ClearResultWithinAsync(
            connection,
            transaction,
            record.Kind,
            cancellationToken,
            schemaPrefix: string.Empty);
    }

    public async Task<ResultRecord?> FindLatestAsync(
        string projectId,
        ResultKind kind,
        CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        // generated_utc 記到 100 奈秒，兩次執行幾乎不可能同時；真的同時就以 result_id 當平手鍵，
        // 和 SqlServerResultStore 相同，三種資料庫選到同一筆。
        command.CommandText =
            $"""
            SELECT result_id, generated_utc, summary_json
            FROM {ResultTables.For(kind)}
            ORDER BY generated_utc DESC, result_id DESC
            LIMIT 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ResultRecord(
            reader.GetString(0),
            kind,
            DateTimeOffset.Parse(reader.GetString(1)),
            reader.GetString(2));
    }
}
