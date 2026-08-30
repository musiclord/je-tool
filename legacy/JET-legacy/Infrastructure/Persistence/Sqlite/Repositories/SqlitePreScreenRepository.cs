using Dapper;
using JET.Domain.Abstractions.Persistence;
using JET.Domain.Abstractions.Repositories;
using JET.Infrastructure.Configuration;
using JET.Infrastructure.Persistence.Schema;
using Microsoft.Data.Sqlite;

namespace JET.Infrastructure.Persistence.Sqlite.Repositories
{
    public sealed class SqlitePreScreenRepository : IPreScreenRepository
    {
        private readonly string _connectionString;
        private readonly ISchemaNames _names;

        public SqlitePreScreenRepository(DatabaseOptions databaseOptions)
            : this(databaseOptions.SqliteConnectionString, new SqliteSchemaNames())
        {
        }

        public SqlitePreScreenRepository(string connectionString, ISchemaNames names)
        {
            _connectionString = connectionString;
            _names = names;
        }

        public async Task<PreScreenRunResult> RunAsync(string projectId, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
            var runId = Guid.NewGuid().ToString("D");
            var gl = _names.Resolve(JetTable.TargetGlEntry);
            var project = _names.Resolve(JetTable.ConfigProject);

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            foreach (var table in ResultTables())
            {
                await connection.ExecuteAsync(new CommandDefinition($"DELETE FROM {table} WHERE project_id = @ProjectId;", new { ProjectId = projectId }, transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            var r1 = await InsertAsync(connection, transaction, _names.Resolve(JetTable.ResultPrescreenR1), runId, projectId, gl,
                $"g.project_id = @ProjectId AND COALESCE(NULLIF(TRIM(g.doc_date), ''), NULLIF(TRIM(g.post_date), '')) >= (SELECT last_period_start FROM {project} WHERE project_id = @ProjectId)",
                "Near period end", cancellationToken).ConfigureAwait(false);

            var r2 = await InsertAsync(connection, transaction, _names.Resolve(JetTable.ResultPrescreenR2), runId, projectId, gl,
                "g.project_id = @ProjectId AND (LOWER(COALESCE(g.description, '')) LIKE '%adj%' OR LOWER(COALESCE(g.description, '')) LIKE '%rev%' OR LOWER(COALESCE(g.description, '')) LIKE '%reclass%' OR LOWER(COALESCE(g.description, '')) LIKE '%suspense%' OR LOWER(COALESCE(g.description, '')) LIKE '%error%' OR LOWER(COALESCE(g.description, '')) LIKE '%wrong%' OR COALESCE(g.description, '') LIKE '%調整%' OR COALESCE(g.description, '') LIKE '%迴轉%' OR COALESCE(g.description, '') LIKE '%沖銷%' OR COALESCE(g.description, '') LIKE '%重分類%' OR COALESCE(g.description, '') LIKE '%避險%' OR COALESCE(g.description, '') LIKE '%重編%' OR COALESCE(g.description, '') LIKE '%錯誤%' OR COALESCE(g.description, '') LIKE '%計畫外%' OR COALESCE(g.description, '') LIKE '%預算外%')",
                "Keyword description", cancellationToken).ConfigureAwait(false);

            var r4Threshold = 3;
            var r4 = await InsertAsync(connection, transaction, _names.Resolve(JetTable.ResultPrescreenR4), runId, projectId, gl,
                "g.project_id = @ProjectId AND ABS(CAST(g.amount AS INTEGER)) >= 1000 AND ABS(CAST(g.amount AS INTEGER)) % 1000 = 0",
                "Rounded amount", cancellationToken).ConfigureAwait(false);

            var r5 = await InsertAsync(connection, transaction, _names.Resolve(JetTable.ResultPrescreenR5), runId, projectId, gl,
                "g.project_id = @ProjectId AND g.create_by IS NOT NULL AND TRIM(g.create_by) <> ''",
                "Creator summary source", cancellationToken).ConfigureAwait(false);

            var r6 = await InsertAsync(connection, transaction, _names.Resolve(JetTable.ResultPrescreenR6), runId, projectId, gl,
                $"""
                g.project_id = @ProjectId AND g.acc_num IN (
                    WITH freq AS (
                        SELECT acc_num, COUNT(1) AS cnt
                        FROM {gl}
                        WHERE project_id = @ProjectId AND acc_num IS NOT NULL AND TRIM(acc_num) <> ''
                        GROUP BY acc_num
                    ), avg_freq AS (
                        SELECT AVG(cnt) AS avg_cnt FROM freq
                    )
                    SELECT freq.acc_num
                    FROM freq, avg_freq
                    WHERE freq.cnt < avg_freq.avg_cnt * 0.25
                )
                """,
                "Rare account", cancellationToken).ConfigureAwait(false);

            var r3 = await InsertAsync(connection, transaction, _names.Resolve(JetTable.ResultPrescreenR3), runId, projectId, gl,
                $"""
                g.project_id = @ProjectId AND g.doc_num IN (
                    SELECT g2.doc_num FROM {gl} g2
                    LEFT JOIN (
                        SELECT TRIM(json_extract(s.payload, '$[0]')) AS acc,
                               LOWER(COALESCE(json_extract(s.payload, '$[2]'), '')) AS cat
                        FROM {_names.Resolve(JetTable.StagingAccountMappingRawRow)} s
                        WHERE s.batch_id = (
                            SELECT batch_id FROM {_names.Resolve(JetTable.ConfigImportBatch)}
                            WHERE project_id = @ProjectId AND dataset_kind = 'accountMapping'
                            ORDER BY imported_utc DESC LIMIT 1
                        )
                    ) m ON m.acc = TRIM(COALESCE(g2.acc_num, ''))
                    WHERE g2.project_id = @ProjectId
                    GROUP BY g2.doc_num
                    HAVING SUM(CASE WHEN (m.cat LIKE '%revenue%' OR m.cat LIKE '%income%' OR m.cat LIKE '%收入%') AND g2.amount < 0 THEN 1 ELSE 0 END) >= 1
                       AND SUM(CASE WHEN (m.cat LIKE '%receivable%' OR m.cat LIKE '%應收%' OR m.cat LIKE '%cash%' OR m.cat LIKE '%bank%' OR m.cat LIKE '%現金%' OR m.cat LIKE '%advance%' OR m.cat LIKE '%deferred%' OR m.cat LIKE '%預收%') AND g2.amount > 0 THEN 1 ELSE 0 END) >= 1
                )
                """,
                "Revenue/AR pair", cancellationToken).ConfigureAwait(false);
            var descNullCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                $"SELECT COUNT(1) FROM {gl} WHERE project_id = @ProjectId AND (description IS NULL OR TRIM(description) = '');",
                new { ProjectId = projectId }, transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            var r5Rows = (await connection.QueryAsync<(string Creator, long Count)>(new CommandDefinition(
                $"""
                SELECT COALESCE(NULLIF(TRIM(create_by), ''), '(未知)') AS Creator, COUNT(1) AS Count
                FROM {gl}
                WHERE project_id = @ProjectId
                GROUP BY COALESCE(NULLIF(TRIM(create_by), ''), '(未知)')
                ORDER BY Count DESC, Creator;
                """, new { ProjectId = projectId }, transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToArray();
            var r5Summary = r5Rows
                .Select(row => new PreScreenCreatorSummary(row.Creator, (int)row.Count))
                .ToArray();

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PreScreenRunResult(runId, r1, r2, r3, r4, r4Threshold, r5Summary, r6, 0, 0, 0, 0, 0, descNullCount);
        }

        public async Task<PreScreenPageResult> QueryPageAsync(string projectId, string kind, long? cursor, int pageSize, CancellationToken cancellationToken)
        {
            var table = ResolveResultTable(kind);
            var effectivePageSize = Math.Clamp(pageSize <= 0 ? 100 : pageSize, 1, 1000);
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var rows = (await connection.QueryAsync<PreScreenDetailRow>(new CommandDefinition(
                $"""
                SELECT row_no AS RowNo, run_id AS RunId, batch_id AS BatchId, doc_num AS DocNum, line_id AS LineId, acc_num AS AccNum, reason AS Reason
                FROM {table}
                WHERE project_id = @ProjectId AND row_no > @Cursor
                ORDER BY row_no
                LIMIT @Limit;
                """, new { ProjectId = projectId, Cursor = cursor ?? 0, Limit = effectivePageSize + 1 }, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
            long? nextCursor = null;
            if (rows.Count > effectivePageSize)
            {
                nextCursor = rows[effectivePageSize - 1].RowNo;
                rows.RemoveAt(rows.Count - 1);
            }
            return new PreScreenPageResult(rows, nextCursor);
        }

        private IEnumerable<string> ResultTables()
        {
            foreach (var table in new[] { "r1", "r2", "r3", "r4", "r5", "r6", "r7", "r8", "a2", "a3", "a4" })
            {
                yield return ResolveResultTable(table);
            }
        }

        private string ResolveResultTable(string kind) => (kind ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "r1" => _names.Resolve(JetTable.ResultPrescreenR1),
            "r2" => _names.Resolve(JetTable.ResultPrescreenR2),
            "r3" => _names.Resolve(JetTable.ResultPrescreenR3),
            "r4" => _names.Resolve(JetTable.ResultPrescreenR4),
            "r5" => _names.Resolve(JetTable.ResultPrescreenR5),
            "r6" => _names.Resolve(JetTable.ResultPrescreenR6),
            "r7" => _names.Resolve(JetTable.ResultPrescreenR7),
            "r8" => _names.Resolve(JetTable.ResultPrescreenR8),
            "a2" => _names.Resolve(JetTable.ResultPrescreenA2),
            "a3" => _names.Resolve(JetTable.ResultPrescreenA3),
            "a4" => _names.Resolve(JetTable.ResultPrescreenA4),
            _ => throw new ArgumentException($"Unsupported prescreen kind: {kind}", nameof(kind))
        };

        private static async Task<int> InsertAsync(SqliteConnection connection, SqliteTransaction transaction, string resultTable, string runId, string projectId, string gl, string predicate, string reason, CancellationToken cancellationToken)
        {
            return await connection.ExecuteAsync(new CommandDefinition(
                $"""
                INSERT INTO {resultTable} (project_id, run_id, row_no, batch_id, doc_num, line_id, acc_num, reason)
                SELECT g.project_id, @RunId, ROW_NUMBER() OVER (ORDER BY g.batch_id, g.doc_num, g.line_id), g.batch_id, g.doc_num, g.line_id, g.acc_num, @Reason
                FROM {gl} g
                WHERE {predicate};
                """, new { ProjectId = projectId, RunId = runId, Reason = reason }, transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }
}
