using Dapper;
using JET.Domain.Abstractions.Persistence;
using JET.Domain.Abstractions.Repositories;
using JET.Infrastructure.Configuration;
using JET.Infrastructure.Persistence.Schema;
using Microsoft.Data.Sqlite;

namespace JET.Infrastructure.Persistence.Sqlite.Repositories
{
    public sealed class SqliteGlProjectionRepository : IGlProjectionRepository
    {
        private const string DatasetKind = "gl";

        private readonly string _connectionString;
        private readonly ISchemaNames _names;

        public SqliteGlProjectionRepository(DatabaseOptions databaseOptions)
            : this(databaseOptions.SqliteConnectionString, new SqliteSchemaNames())
        {
        }

        public SqliteGlProjectionRepository(string connectionString, ISchemaNames names)
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
            var doc = SqliteProjectionSupport.JsonValue(mapping, columns, "docNum");
            var line = SqliteProjectionSupport.JsonValue(mapping, columns, "lineID");
            var postDate = SqliteProjectionSupport.JsonValue(mapping, columns, "postDate");
            var docDate = SqliteProjectionSupport.JsonValue(mapping, columns, "docDate");
            var accNum = SqliteProjectionSupport.JsonValue(mapping, columns, "accNum");
            var accName = SqliteProjectionSupport.JsonValue(mapping, columns, "accName");
            var description = SqliteProjectionSupport.JsonValue(mapping, columns, "description");
            var jeSource = SqliteProjectionSupport.JsonValue(mapping, columns, "jeSource");
            var createBy = SqliteProjectionSupport.JsonValue(mapping, columns, "createBy");
            var approveBy = SqliteProjectionSupport.JsonValue(mapping, columns, "approveBy");
            var manual = SqliteProjectionSupport.JsonValue(mapping, columns, "manual");
            var amount = $"CAST({SqliteProjectionSupport.JsonValue(mapping, columns, "amount")} AS REAL)";

            var target = _names.Resolve(JetTable.TargetGlEntry);
            var raw = _names.Resolve(JetTable.StagingGlRawRow);

            await connection.ExecuteAsync(new CommandDefinition(
                $"DELETE FROM {target} WHERE project_id = @ProjectId;",
                new { ProjectId = projectId },
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            var projected = await connection.ExecuteAsync(new CommandDefinition(
                $"""
                INSERT INTO {target}
                    (project_id, batch_id, doc_num, line_id, post_date, doc_date, acc_num, acc_name, description, je_source, create_by, approve_by, manual, dr_amount, cr_amount, amount)
                SELECT
                    @ProjectId,
                    @BatchId,
                    {doc},
                    {line},
                    {postDate},
                    {docDate},
                    {accNum},
                    {accName},
                    {description},
                    {jeSource},
                    {createBy},
                    {approveBy},
                    CAST({manual} AS INTEGER),
                    CASE WHEN {amount} > 0 THEN {amount} ELSE 0 END,
                    CASE WHEN {amount} < 0 THEN -{amount} ELSE 0 END,
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
