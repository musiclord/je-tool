using System.Text.Json;
using Dapper;
using JET.Domain.Abstractions.Persistence;
using JET.Domain.Abstractions.Repositories;
using JET.Domain.Entities;
using JET.Infrastructure.Configuration;
using JET.Infrastructure.Persistence.Schema;
using Microsoft.Data.Sqlite;

namespace JET.Infrastructure.Persistence.Sqlite.Repositories
{
    /// <summary>
    /// SQLite implementation of <see cref="IGlRepository"/>. Streams raw GL rows
    /// into <c>staging_gl_raw_row</c> in 5,000-row prepared-statement batches
    /// inside a single transaction. Per plan.md §3.1.b mission constraint, this
    /// path NEVER materialises the full row stream in memory.
    /// </summary>
    public sealed class SqliteGlRepository : IGlRepository
    {
        private const int BatchSize = 5_000;
        private const string DatasetKind = "gl";

        private readonly string _connectionString;
        private readonly ISchemaNames _names;

        public SqliteGlRepository(DatabaseOptions databaseOptions)
            : this(databaseOptions.SqliteConnectionString, new SqliteSchemaNames())
        {
        }

        public SqliteGlRepository(string connectionString, ISchemaNames names)
        {
            _connectionString = connectionString;
            _names = names;
        }

        public async Task<BulkImportResult> BulkInsertStagingAsync(
            string projectId,
            string fileName,
            IAsyncEnumerable<GlRawRow> rows,
            string mode,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
            ArgumentNullException.ThrowIfNull(rows);

            var effectiveMode = string.IsNullOrWhiteSpace(mode) ? "replace" : mode.Trim().ToLowerInvariant();
            if (effectiveMode is not ("replace" or "append"))
            {
                throw new ArgumentException($"Unsupported import mode: {mode}", nameof(mode));
            }

            var batchId = Guid.NewGuid().ToString("D");
            var nowUtc = DateTimeOffset.UtcNow.ToString("O");

            var batchTable = _names.Resolve(JetTable.ConfigImportBatch);
            var columnTable = _names.Resolve(JetTable.ConfigImportColumn);
            var rawTable = _names.Resolve(JetTable.StagingGlRawRow);

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            if (effectiveMode == "replace")
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    $"""
                    DELETE FROM {rawTable}
                    WHERE batch_id IN (
                        SELECT batch_id FROM {batchTable}
                        WHERE project_id = @ProjectId AND dataset_kind = @DatasetKind
                    );
                    """,
                    new { ProjectId = projectId, DatasetKind = DatasetKind },
                    transaction: transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

                await connection.ExecuteAsync(new CommandDefinition(
                    $"""
                    DELETE FROM {columnTable}
                    WHERE batch_id IN (
                        SELECT batch_id FROM {batchTable}
                        WHERE project_id = @ProjectId AND dataset_kind = @DatasetKind
                    );
                    """,
                    new { ProjectId = projectId, DatasetKind = DatasetKind },
                    transaction: transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

                await connection.ExecuteAsync(new CommandDefinition(
                    $"DELETE FROM {batchTable} WHERE project_id = @ProjectId AND dataset_kind = @DatasetKind;",
                    new { ProjectId = projectId, DatasetKind = DatasetKind },
                    transaction: transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            // Streaming write: header (RowIndex == 0) → config_import_column;
            // data rows → staging_gl_raw_row in BatchSize chunks.
            IReadOnlyList<string> columns = Array.Empty<string>();
            var rowCount = 0;
            var pending = new List<GlRawRow>(BatchSize);
            var headerSeen = false;

            // Insert the batch row up front; row_count is patched on commit.
            // Using a placeholder of 0 keeps the FK-style invariant that
            // batch row exists before child rows.
            await connection.ExecuteAsync(new CommandDefinition(
                $"""
                INSERT INTO {batchTable}
                    (batch_id, project_id, dataset_kind, file_name, row_count, imported_utc)
                VALUES
                    (@BatchId, @ProjectId, @DatasetKind, @FileName, 0, @ImportedUtc);
                """,
                new
                {
                    BatchId = batchId,
                    ProjectId = projectId,
                    DatasetKind = DatasetKind,
                    FileName = string.IsNullOrWhiteSpace(fileName) ? "(unnamed)" : fileName,
                    ImportedUtc = nowUtc
                },
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (!headerSeen)
                {
                    headerSeen = true;
                    var headerValues = row.Values
                        .Select((v, i) => string.IsNullOrWhiteSpace(v) ? $"col{i + 1}" : v!.Trim())
                        .ToList();
                    columns = headerValues;

                    if (headerValues.Count > 0)
                    {
                        var columnRows = headerValues
                            .Select((name, idx) => new { BatchId = batchId, ColumnIndex = idx, ColumnName = name })
                            .ToArray();

                        await connection.ExecuteAsync(new CommandDefinition(
                            $"INSERT INTO {columnTable} (batch_id, column_index, column_name) VALUES (@BatchId, @ColumnIndex, @ColumnName);",
                            columnRows,
                            transaction: transaction,
                            cancellationToken: cancellationToken)).ConfigureAwait(false);
                    }
                    continue;
                }

                pending.Add(row);
                if (pending.Count >= BatchSize)
                {
                    rowCount += await FlushAsync(connection, transaction, rawTable, batchId, pending, cancellationToken).ConfigureAwait(false);
                    pending.Clear();
                }
            }

            if (!headerSeen)
            {
                throw new InvalidOperationException("GL file has no header row.");
            }

            if (pending.Count > 0)
            {
                rowCount += await FlushAsync(connection, transaction, rawTable, batchId, pending, cancellationToken).ConfigureAwait(false);
                pending.Clear();
            }

            await connection.ExecuteAsync(new CommandDefinition(
                $"UPDATE {batchTable} SET row_count = @RowCount WHERE batch_id = @BatchId;",
                new { RowCount = rowCount, BatchId = batchId },
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new BulkImportResult(batchId, rowCount, columns);
        }

        private static async Task<int> FlushAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string rawTable,
            string batchId,
            List<GlRawRow> pending,
            CancellationToken cancellationToken)
        {
            var rows = pending.Select(r => new
            {
                BatchId = batchId,
                RowIndex = r.RowIndex,
                Payload = JsonSerializer.Serialize(r.Values),
            }).ToArray();

            await connection.ExecuteAsync(new CommandDefinition(
                $"INSERT INTO {rawTable} (batch_id, row_index, payload) VALUES (@BatchId, @RowIndex, @Payload);",
                rows,
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            return rows.Length;
        }
    }
}
