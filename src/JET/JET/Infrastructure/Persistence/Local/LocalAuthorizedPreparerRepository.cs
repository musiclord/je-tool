using System.Data;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 授權編製人員清單的匯入與計數（import.authorizedPreparer.fromFile）。
/// 授權清單只是一組姓名。寫匯入暫存表 <c>pbc_authorized_preparer</c> 與寫 <c>authorized_preparer</c> 在同一個交易裡完成。
/// 做法和 <see cref="LocalAccountMappingRepository"/> 相同，但不寫 import_batch：
/// import_batch.dataset_kind 的 CHECK 只允許 gl、tb 與 account_mapping。
/// batchId 只寫在匯入暫存表的 batch_id 欄與回應裡，沒有批次紀錄可以查回。只支援取代匯入。
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
        await database.EnsureReadyAsync(projectId, cancellationToken);
        projection ??= JetAuditProgram.PrepareAuthorizedPreparerProjection(
            columns, columns.Count == 1 ? columns[0] : null);
        projection.ResolveColumns();

        var batchId = Guid.NewGuid().ToString("N");
        var importedUtc = DateTimeOffset.UtcNow;

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // 取代匯入：在同一個交易裡清掉舊的匯入暫存表 pbc_authorized_preparer 與 authorized_preparer。
        await using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandText =
                """
                DELETE FROM pbc_authorized_preparer;
                DELETE FROM authorized_preparer;
                """;
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }

        // 授權清單換版,非授權編製人員等規則結果即失效。
        await ResultReset.ClearWithinAsync(
            connection,
            transaction,
            cancellationToken,
            AuditMutation.AuthorizedPreparer);

        // 一邊讀來源一邊寫匯入暫存表，同時把每一列交給 projection 收集姓名：去掉前後空白、略過空白列、重複的只留一個。
        var rowCount = 0;

        await using (var insertRow = connection.CreateCommand())
        {
            insertRow.Transaction = transaction;
            insertRow.CommandText =
                """
                INSERT INTO pbc_authorized_preparer
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
                "INSERT INTO authorized_preparer (name) VALUES (@name);";
            var nameParam = insertTarget.AddParameter("@name", DbType.String);

            foreach (var name in names)
            {
                nameParam.Value = name;
                await insertTarget.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await AuthorizedPreparerMetadataSql.WriteAsync(connection, transaction, projection.SourceColumn, cancellationToken,
            sourceRowCount: rowCount, blankRowCount: projection.BlankRowCount, duplicateRowCount: projection.DuplicateRowCount);
        var matchedPreparerCount = await AuthorizedPreparerMetadataSql.CountMatchedAsync(
            connection, transaction, database.Dialect, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new AuthorizedPreparerImportResult(batchId, names.Count, source.FileName, importedUtc)
        {
            SourceColumn = projection.SourceColumn, SourceRowCount = rowCount,
            BlankRowCount = projection.BlankRowCount, DuplicateRowCount = projection.DuplicateRowCount,
            MatchedPreparerCount = matchedPreparerCount
        };
    }

    public async Task ClearAsync(string projectId, CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM pbc_authorized_preparer; DELETE FROM authorized_preparer;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        await AuthorizedPreparerMetadataSql.WriteAsync(connection, transaction, null, cancellationToken);
        await ResultReset.ClearWithinAsync(connection, transaction, cancellationToken, AuditMutation.AuthorizedPreparer);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<long> CountAsync(string projectId, CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM authorized_preparer;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }

    public async Task<AuthorizedPreparerState?> FindStateAsync(string projectId, CancellationToken cancellationToken)
    {
        var rowCount = await CountAsync(projectId, cancellationToken);
        if (rowCount == 0) return null;
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        var metadata = await AuthorizedPreparerMetadataSql.ReadAsync(connection, cancellationToken);
        return new AuthorizedPreparerState(rowCount)
        {
            SourceColumn = metadata.SourceColumn,
            SourceRowCount = metadata.SourceRowCount,
            BlankRowCount = metadata.BlankRowCount,
            DuplicateRowCount = metadata.DuplicateRowCount,
            MatchedPreparerCount = await AuthorizedPreparerMetadataSql.CountMatchedAsync(
                connection, null, database.Dialect, cancellationToken)
        };
    }
}
