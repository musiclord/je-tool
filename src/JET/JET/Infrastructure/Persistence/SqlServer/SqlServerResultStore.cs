using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// 驗證結果與預篩選結果的 SQL Server 存放(對應 <see cref="LocalResultStore"/>)，兩種結果各寫一張表。
/// 同一時間的平手以 result_id 決定(generated_utc 為 "O" 100ns 精度,實務不會平手);LIMIT 1 → TOP 1。
/// </summary>
public sealed class SqlServerResultStore(SqlServerProjectDatabase database) : IResultStore
{
    public async Task SaveAsync(string projectId, ResultRecord record, CancellationToken cancellationToken)
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
        SqlTransaction transaction, string projectId, ResultRecord record, CancellationToken cancellationToken)
    {
        await using var command = database.CreateCommand(connection, projectId,
            $$"""
            INSERT INTO {s}.{{ResultTables.For(record.Kind)}} (result_id, generated_utc, summary_json)
            VALUES (@resultId, @generatedUtc, @summaryJson);
            """);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@resultId", record.ResultId);
        command.Parameters.AddWithValue("@generatedUtc", record.GeneratedUtc.ToString("O"));
        command.Parameters.AddWithValue("@summaryJson", record.SummaryJson);

        await command.ExecuteNonQueryAsync(cancellationToken);
        await ResultStaleStateSql.ClearResultWithinAsync(
            connection,
            transaction,
            record.Kind,
            cancellationToken,
            SqlServerProjectSchema.QualifierFor(projectId));

    }

    public async Task<ResultRecord?> FindLatestAsync(
        string projectId,
        ResultKind kind,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = database.CreateCommand(connection, projectId,
            $$"""
            SELECT TOP 1 result_id, generated_utc, summary_json
            FROM {s}.{{ResultTables.For(kind)}}
            ORDER BY generated_utc DESC, result_id DESC;
            """);

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
