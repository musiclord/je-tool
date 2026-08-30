using Dapper;
using JET.Domain.Abstractions.Persistence;
using JET.Domain.Abstractions.Repositories;
using JET.Infrastructure.Configuration;
using JET.Infrastructure.Persistence.Schema;
using Microsoft.Data.Sqlite;

namespace JET.Infrastructure.Persistence.Sqlite.Repositories
{
    public sealed class SqliteTbProjectionRepository : ITbProjectionRepository
    {
        private const string DatasetKind = "tb";

        private readonly string _connectionString;
        private readonly ISchemaNames _names;

        public SqliteTbProjectionRepository(DatabaseOptions databaseOptions)
            : this(databaseOptions.SqliteConnectionString, new SqliteSchemaNames())
        {
        }

        public SqliteTbProjectionRepository(string connectionString, ISchemaNames names)
        {
            _connectionString = connectionString;
            _names = names;
        }

        public async Task<ProjectionResult> ProjectLatestBatchAsync(
            string projectId,
            IReadOnlyDictionary<string, string> mapping,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
            ArgumentNullException.ThrowIfNull(mapping);

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            var batchId = await SqliteProjectionSupport.FindLatestBatchIdAsync(connection, transaction, _names, projectId, DatasetKind, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(batchId))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ProjectionResult(null, 0);
            }

            var columns = await SqliteProjectionSupport.LoadColumnIndexesAsync(connection, transaction, _names, batchId, cancellationToken).ConfigureAwait(false);
            var accNum = SqliteProjectionSupport.JsonValue(mapping, columns, "accNum");
            var accName = SqliteProjectionSupport.JsonValue(mapping, columns, "accName");
            var amount = $"CAST({SqliteProjectionSupport.JsonValue(mapping, columns, "amount")} AS REAL)";

            var target = _names.Resolve(JetTable.TargetTbBalance);
            var raw = _names.Resolve(JetTable.StagingTbRawRow);

            await connection.ExecuteAsync(new CommandDefinition(
                $"DELETE FROM {target} WHERE project_id = @ProjectId;",
                new { ProjectId = projectId },
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            var projected = await connection.ExecuteAsync(new CommandDefinition(
                $"""
                INSERT INTO {target}
                    (project_id, batch_id, acc_num, acc_name, change_amount)
                SELECT
                    @ProjectId,
                    @BatchId,
                    {accNum},
                    {accName},
                    {amount}
                FROM {raw}
                WHERE batch_id = @BatchId;
                """,
                new { ProjectId = projectId, BatchId = batchId },
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ProjectionResult(batchId, projected);
        }
    }
}
