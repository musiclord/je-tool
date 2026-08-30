using Dapper;
using JET.Domain.Abstractions.Persistence;
using JET.Domain.Abstractions.Repositories;
using JET.Infrastructure.Configuration;
using JET.Infrastructure.Persistence.Schema;
using Microsoft.Data.Sqlite;

namespace JET.Infrastructure.Persistence.Sqlite.Repositories
{
    /// <summary>
    /// SQLite implementation of <see cref="IDateDimensionRepository"/>. Replace semantics
    /// are atomic: a single transaction deletes prior rows for the (project, kind) pair
    /// and inserts the new batch + day rows.
    /// </summary>
    public sealed class SqliteDateDimensionRepository : IDateDimensionRepository
    {
        private readonly string _connectionString;
        private readonly ISchemaNames _names;

        public SqliteDateDimensionRepository(DatabaseOptions databaseOptions)
            : this(databaseOptions.SqliteConnectionString, new SqliteSchemaNames())
        {
        }

        public SqliteDateDimensionRepository(string connectionString, ISchemaNames names)
        {
            _connectionString = connectionString;
            _names = names;
        }

        public async Task<string> ReplaceCalendarInputAsync(string projectId, string kind, IReadOnlyList<string> dates, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
            ArgumentException.ThrowIfNullOrWhiteSpace(kind);
            ArgumentNullException.ThrowIfNull(dates);

            var datasetKind = $"calendar:{kind}";
            var batchId = Guid.NewGuid().ToString("D");
            var nowUtc = DateTimeOffset.UtcNow.ToString("O");

            var batchTable = _names.Resolve(JetTable.ConfigImportBatch);
            var dayTable = _names.Resolve(JetTable.StagingCalendarRawDay);

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            // 1) Delete day rows linked to prior batches of the same (project, kind).
            await connection.ExecuteAsync(new CommandDefinition(
                $"""
                DELETE FROM {dayTable}
                WHERE batch_id IN (
                    SELECT batch_id FROM {batchTable}
                    WHERE project_id = @ProjectId AND dataset_kind = @DatasetKind
                );
                """,
                new { ProjectId = projectId, DatasetKind = datasetKind },
                transaction: transaction,
                cancellationToken: cancellationToken));

            // 2) Delete the prior batch rows.
            await connection.ExecuteAsync(new CommandDefinition(
                $"DELETE FROM {batchTable} WHERE project_id = @ProjectId AND dataset_kind = @DatasetKind;",
                new { ProjectId = projectId, DatasetKind = datasetKind },
                transaction: transaction,
                cancellationToken: cancellationToken));

            // 3) Insert the new batch row.
            await connection.ExecuteAsync(new CommandDefinition(
                $"""
                INSERT INTO {batchTable}
                    (batch_id, project_id, dataset_kind, file_name, row_count, imported_utc)
                VALUES
                    (@BatchId, @ProjectId, @DatasetKind, @FileName, @RowCount, @ImportedUtc);
                """,
                new
                {
                    BatchId = batchId,
                    ProjectId = projectId,
                    DatasetKind = datasetKind,
                    FileName = $"inline:{kind}",
                    RowCount = dates.Count,
                    ImportedUtc = nowUtc
                },
                transaction: transaction,
                cancellationToken: cancellationToken));

            // 4) Insert deduplicated day rows. PRIMARY KEY (batch_id, date_iso, kind) so
            //    duplicates within the same call are filtered out client-side.
            if (dates.Count > 0)
            {
                var distinctDates = dates
                    .Where(d => !string.IsNullOrWhiteSpace(d))
                    .Select(d => d.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                var rows = distinctDates.Select(d => new
                {
                    BatchId = batchId,
                    DateIso = d,
                    Kind = kind
                }).ToArray();

                if (rows.Length > 0)
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        $"""
                        INSERT INTO {dayTable} (batch_id, date_iso, kind)
                        VALUES (@BatchId, @DateIso, @Kind);
                        """,
                        rows,
                        transaction: transaction,
                        cancellationToken: cancellationToken));
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return batchId;
        }
    }
}
