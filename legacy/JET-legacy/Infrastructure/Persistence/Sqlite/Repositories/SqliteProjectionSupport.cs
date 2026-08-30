using Dapper;
using JET.Domain.Abstractions.Persistence;
using JET.Infrastructure.Persistence.Schema;
using Microsoft.Data.Sqlite;

namespace JET.Infrastructure.Persistence.Sqlite.Repositories
{
    internal static class SqliteProjectionSupport
    {
        public static async Task<string?> FindLatestBatchIdAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            ISchemaNames names,
            string projectId,
            string datasetKind,
            CancellationToken cancellationToken)
        {
            return await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
                $"""
                SELECT batch_id
                FROM {names.Resolve(JetTable.ConfigImportBatch)}
                WHERE project_id = @ProjectId AND dataset_kind = @DatasetKind
                ORDER BY imported_utc DESC, batch_id DESC
                LIMIT 1;
                """,
                new { ProjectId = projectId, DatasetKind = datasetKind },
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        public static async Task<IReadOnlyDictionary<string, int>> LoadColumnIndexesAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            ISchemaNames names,
            string batchId,
            CancellationToken cancellationToken)
        {
            var rows = await connection.QueryAsync<(int column_index, string column_name)>(new CommandDefinition(
                $"""
                SELECT column_index, column_name
                FROM {names.Resolve(JetTable.ConfigImportColumn)}
                WHERE batch_id = @BatchId;
                """,
                new { BatchId = batchId },
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            return rows.ToDictionary(static row => row.column_name, static row => row.column_index, StringComparer.OrdinalIgnoreCase);
        }

        public static string JsonValue(
            IReadOnlyDictionary<string, string> mapping,
            IReadOnlyDictionary<string, int> columns,
            string logicalName)
        {
            if (!mapping.TryGetValue(logicalName, out var columnName) || string.IsNullOrWhiteSpace(columnName))
            {
                throw new InvalidOperationException($"Mapping does not define required field '{logicalName}'.");
            }

            if (!columns.TryGetValue(columnName, out var columnIndex))
            {
                throw new InvalidOperationException($"Mapped column '{columnName}' was not found in the latest import batch.");
            }

            return $"json_extract(payload, '$[{columnIndex}]')";
        }
    }
}
