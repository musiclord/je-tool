using System.Data;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 授權編製人員清單的匯入與計數（manifest import.authorizedPreparer.fromFile）。
/// 授權清單就是一個 name 集合——staging 寫入與 target 投影在**同一 transaction**完成。
/// 鏡射 <see cref="LocalAccountMappingRepository"/> 但不寫 import_batch（不入 dataset_kind 體系，
/// 避開 CHECK 升版）；batchId 僅供 response、不持久化。replace-only。
/// </summary>
public sealed class LocalAuthorizedPreparerRepository(ILocalProjectDatabase database)
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
        source = ImportSourceFileName.Normalize(source);
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        projection ??= JetAuditProgram.PrepareAuthorizedPreparerProjection(columns);
        projection.ResolveColumns();

        var batchId = Guid.NewGuid().ToString("N");
        var importedUtc = DateTimeOffset.UtcNow;

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // replace 語意：同一 transaction 清舊 staging 與 target。
        await using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandText =
                """
                DELETE FROM staging_authorized_preparer_raw_row;
                DELETE FROM target_authorized_preparer;
                """;
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }

        // 授權清單換版,非授權編製人員等規則結果即失效。
        await RuleRunResultReset.ClearWithinAsync(
            connection,
            transaction,
            cancellationToken,
            AuditMutation.AuthorizedPreparer);

        // 串流寫 staging,同步收集去重後姓名集合（TRIM、空白略過）。
        var rowCount = 0;

        await using (var insertRow = connection.CreateCommand())
        {
            insertRow.Transaction = transaction;
            insertRow.CommandText =
                """
                INSERT INTO staging_authorized_preparer_raw_row
                    (batch_id, row_number, source_no, source_row_number, row_json)
                VALUES (@batchId, @rowNumber, 1, @sourceRowNumber, @rowJson);
                """;
            insertRow.AddWithValue("@batchId", batchId);
            var rowNumberParam = insertRow.AddParameter("@rowNumber", DbType.Int64);
            var sourceRowParam = insertRow.AddParameter("@sourceRowNumber", DbType.Int64);
            var rowJsonParam = insertRow.AddParameter("@rowJson", DbType.String);

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

        await using (var insertTarget = connection.CreateCommand())
        {
            insertTarget.Transaction = transaction;
            insertTarget.CommandText =
                "INSERT INTO target_authorized_preparer (name) VALUES (@name);";
            var nameParam = insertTarget.AddParameter("@name", DbType.String);

            foreach (var name in names)
            {
                nameParam.Value = name;
                await insertTarget.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await AuthorizedPreparerMetadataSql.WriteAsync(connection, transaction, projection.SourceColumn, cancellationToken);
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
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM staging_authorized_preparer_raw_row; DELETE FROM target_authorized_preparer;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        await AuthorizedPreparerMetadataSql.WriteAsync(connection, transaction, null, cancellationToken);
        await RuleRunResultReset.ClearWithinAsync(connection, transaction, cancellationToken, AuditMutation.AuthorizedPreparer);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<long> CountAsync(string projectId, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM target_authorized_preparer;";
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
            SourceColumn = await AuthorizedPreparerMetadataSql.ReadAsync(connection, cancellationToken)
        };
    }
}
