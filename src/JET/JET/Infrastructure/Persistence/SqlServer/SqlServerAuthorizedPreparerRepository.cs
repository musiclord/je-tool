using System.Data;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// 授權編製人員清單的 SQL Server 實作（對應 <see cref="LocalAuthorizedPreparerRepository"/>;
/// 機械式移植,差連線型別/參數型別/COUNT_BIG）。replace-only;不寫 import_batch;
/// staging 寫入與 target 投影同一交易。
/// </summary>
public sealed class SqlServerAuthorizedPreparerRepository(SqlServerProjectDatabase database)
    : IAuthorizedPreparerStore, IAuthorizedPreparerImportPersistence
{
    private static readonly JsonSerializerOptions JsonOptions = JetJsonStorage.Options;

    public Task<AuthorizedPreparerImportResult> ImportAsync(
        string projectId,
        ImportSourceDescriptor source,
        IReadOnlyList<string> columns,
        IAsyncEnumerable<StagingRow> rows,
        CancellationToken cancellationToken) =>
        ImportCoreAsync(
            projectId,
            source,
            columns,
            projection: null,
            rows: rows,
            cancellationToken: cancellationToken);

    Task<AuthorizedPreparerImportResult> IAuthorizedPreparerImportPersistence.ImportAsync(
        string projectId,
        ImportSourceDescriptor source,
        IReadOnlyList<string> columns,
        AuthorizedPreparerProjection projection,
        IAsyncEnumerable<StagingRow> rows,
        CancellationToken cancellationToken) =>
        ImportCoreAsync(
            projectId,
            source,
            columns,
            projection,
            rows,
            cancellationToken);

    private async Task<AuthorizedPreparerImportResult> ImportCoreAsync(
        string projectId,
        ImportSourceDescriptor source,
        IReadOnlyList<string> columns,
        AuthorizedPreparerProjection? projection,
        IAsyncEnumerable<StagingRow> rows,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        projection ??= JetAuditProgram.PrepareAuthorizedPreparerProjection(columns);
        projection.ResolveColumns();

        var batchId = Guid.NewGuid().ToString("N");
        var importedUtc = DateTimeOffset.UtcNow;

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var cleanup = database.CreateCommand(connection, projectId,
            """
            DELETE FROM {s}.staging_authorized_preparer_raw_row;
            DELETE FROM {s}.target_authorized_preparer;
            """))
        {
            cleanup.Transaction = transaction;
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }

        // 授權清單換版,非授權編製人員等規則結果即失效。
        await RuleRunResultReset.ClearWithinAsync(
            connection,
            transaction,
            cancellationToken,
            AuditMutation.AuthorizedPreparer,
            SqlServerProjectSchema.QualifierFor(projectId));

        var rowCount = 0;

        await using (var insertRow = database.CreateCommand(connection, projectId,
            """
            INSERT INTO {s}.staging_authorized_preparer_raw_row
                (batch_id, row_number, source_no, source_row_number, row_json)
            VALUES (@batchId, @rowNumber, 1, @sourceRowNumber, @rowJson);
            """))
        {
            insertRow.Transaction = transaction;
            insertRow.Parameters.AddWithValue("@batchId", batchId);
            var rowNumberParam = insertRow.Parameters.Add("@rowNumber", SqlDbType.BigInt);
            var sourceRowParam = insertRow.Parameters.Add("@sourceRowNumber", SqlDbType.Int);
            var rowJsonParam = insertRow.Parameters.Add("@rowJson", SqlDbType.NVarChar, -1);

            await foreach (var row in rows.WithCancellation(cancellationToken))
            {
                rowNumberParam.Value = row.SourceRowNumber;
                sourceRowParam.Value = row.SourceRowNumber;
                rowJsonParam.Value = JsonSerializer.Serialize(row.Values, JsonOptions);
                await insertRow.ExecuteNonQueryAsync(cancellationToken);
                rowCount++;
                projection.Observe(row);
            }
        }

        IReadOnlyList<string> names;
        try
        {
            names = projection.Complete(rowCount, source.FileName);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        await using (var insertTarget = database.CreateCommand(connection, projectId,
            "INSERT INTO {s}.target_authorized_preparer (name) VALUES (@name);"))
        {
            insertTarget.Transaction = transaction;
            var nameParam = insertTarget.Parameters.Add("@name", SqlDbType.NVarChar, 450);

            foreach (var name in names)
            {
                nameParam.Value = name;
                await insertTarget.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await AuthorizedPreparerMetadataSql.WriteAsync(connection, transaction, projection.SourceColumn, cancellationToken,
            SqlServerProjectSchema.QualifierFor(projectId));
        await transaction.CommitAsync(cancellationToken);

        return new AuthorizedPreparerImportResult(batchId, names.Count, source.FileName, importedUtc)
        {
            SourceColumn = projection.SourceColumn, SourceRowCount = rowCount,
            BlankRowCount = projection.BlankRowCount, DuplicateRowCount = projection.DuplicateRowCount
        };
    }

    public async Task ClearAsync(string projectId, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqlTransaction)transaction;
        var prefix = SqlServerProjectSchema.QualifierFor(projectId);
        command.CommandText = $"DELETE FROM {prefix}staging_authorized_preparer_raw_row; DELETE FROM {prefix}target_authorized_preparer;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        await AuthorizedPreparerMetadataSql.WriteAsync(connection, transaction, null, cancellationToken, prefix);
        await RuleRunResultReset.ClearWithinAsync(connection, transaction, cancellationToken, AuditMutation.AuthorizedPreparer, prefix);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<long> CountAsync(string projectId, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = database.CreateCommand(connection, projectId,
            "SELECT COUNT_BIG(*) FROM {s}.target_authorized_preparer;");
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }

    public async Task<AuthorizedPreparerState?> FindStateAsync(string projectId, CancellationToken cancellationToken)
    {
        var rowCount = await CountAsync(projectId, cancellationToken);
        if (rowCount == 0) return null;
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        return new AuthorizedPreparerState(rowCount)
        {
            SourceColumn = await AuthorizedPreparerMetadataSql.ReadAsync(connection, cancellationToken,
                SqlServerProjectSchema.QualifierFor(projectId))
        };
    }
}
