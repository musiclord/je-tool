using Dapper;
using JET.Domain.Abstractions.Persistence;
using JET.Domain.Abstractions.Repositories;
using JET.Infrastructure.Configuration;
using JET.Infrastructure.Persistence.Schema;
using Microsoft.Data.Sqlite;

namespace JET.Infrastructure.Persistence.Sqlite.Repositories
{
    public sealed class SqliteValidationRepository : IValidationRepository
    {
        private readonly string _connectionString;
        private readonly ISchemaNames _names;

        public SqliteValidationRepository(DatabaseOptions databaseOptions)
            : this(databaseOptions.SqliteConnectionString, new SqliteSchemaNames())
        {
        }

        public SqliteValidationRepository(string connectionString, ISchemaNames names)
        {
            _connectionString = connectionString;
            _names = names;
        }

        public async Task<ValidationRunResult> RunAsync(string projectId, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

            var runId = Guid.NewGuid().ToString("D");
            var gl = _names.Resolve(JetTable.TargetGlEntry);
            var tb = _names.Resolve(JetTable.TargetTbBalance);
            var project = _names.Resolve(JetTable.ConfigProject);

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            foreach (var resultTable in ValidationTables())
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    $"DELETE FROM {resultTable} WHERE project_id = @ProjectId;",
                    new { ProjectId = projectId },
                    transaction: transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            var statsRow = await connection.QuerySingleAsync<(long Total, long Docs, double TotalDebit, double TotalCredit, double Net)>(new CommandDefinition(
                $"""
                SELECT
                    COUNT(1) AS Total,
                    0 AS Docs,
                    COALESCE(SUM(dr_amount), 0) AS TotalDebit,
                    COALESCE(SUM(cr_amount), 0) AS TotalCredit,
                    COALESCE(SUM(amount), 0) AS Net
                FROM {gl}
                WHERE project_id = @ProjectId;
                """,
                new { ProjectId = projectId },
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            var stats = new ValidationStats(
                (int)statsRow.Total,
                (int)statsRow.Docs,
                Convert.ToDecimal(statsRow.TotalDebit),
                Convert.ToDecimal(statsRow.TotalCredit),
                Convert.ToDecimal(statsRow.Net));

            var v1 = await InsertResultAsync(connection, transaction, _names.Resolve(JetTable.ResultValidationV1), runId, projectId,
                gl, $"project_id = @ProjectId AND (acc_num IS NULL OR TRIM(acc_num) = '')", "Missing account number", cancellationToken).ConfigureAwait(false);
            var v2 = await InsertResultAsync(connection, transaction, _names.Resolve(JetTable.ResultValidationV2), runId, projectId,
                gl, $"project_id = @ProjectId AND (doc_num IS NULL OR TRIM(doc_num) = '')", "Missing document number", cancellationToken).ConfigureAwait(false);
            var v3 = await InsertResultAsync(connection, transaction, _names.Resolve(JetTable.ResultValidationV3), runId, projectId,
                gl, $"project_id = @ProjectId AND (description IS NULL OR TRIM(description) = '')", "Missing description", cancellationToken).ConfigureAwait(false);
            var v4 = await InsertV4Async(connection, transaction, _names.Resolve(JetTable.ResultValidationV4), runId, projectId, gl, project, cancellationToken).ConfigureAwait(false);

            var diffRows = await QueryDiffRowsAsync(connection, transaction, tb, gl, projectId, cancellationToken).ConfigureAwait(false);
            var diffAccounts = diffRows
                .Select(row => new ValidationDiffAccount(row.Acc, Convert.ToDecimal(row.TbAmt), Convert.ToDecimal(row.GlAmt), Convert.ToDecimal(row.Diff)))
                .ToArray();

            var docStats = await connection.QuerySingleAsync<(long Docs, int OutOfBalanceDocs)>(new CommandDefinition(
                $"""
                SELECT COUNT(1) AS Docs,
                       COALESCE(SUM(CASE WHEN ABS(balance) > 0.01 THEN 1 ELSE 0 END), 0) AS OutOfBalanceDocs
                FROM (
                    SELECT doc_num, SUM(amount) AS balance
                    FROM {gl}
                    WHERE project_id = @ProjectId AND doc_num IS NOT NULL AND TRIM(doc_num) <> ''
                    GROUP BY doc_num
                );
                """,
                new { ProjectId = projectId },
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            stats = stats with { Docs = (int)docStats.Docs };
            var outOfBalanceDocs = docStats.OutOfBalanceDocs;

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            var summary = new ValidationSummary(
                diffAccounts.Length,
                outOfBalanceDocs,
                Math.Min(60, stats.Total),
                v1 + v2 + v3 + v4);

            return new ValidationRunResult(runId, stats, summary, v1, v2, v3, v4, diffAccounts);
        }

        public async Task<ValidationDetailsPageResult> QueryDetailsPageAsync(
            string projectId,
            string kind,
            long? cursor,
            int pageSize,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
            var table = ResolveValidationResultTable(kind);
            var effectivePageSize = Math.Clamp(pageSize <= 0 ? 100 : pageSize, 1, 1000);
            var effectiveCursor = cursor ?? 0;

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var rows = (await connection.QueryAsync<ValidationDetailRow>(new CommandDefinition(
                $"""
                SELECT
                    row_no AS RowNo,
                    run_id AS RunId,
                    batch_id AS BatchId,
                    doc_num AS DocNum,
                    line_id AS LineId,
                    acc_num AS AccNum,
                    reason AS Reason
                FROM {table}
                WHERE project_id = @ProjectId AND row_no > @Cursor
                ORDER BY row_no
                LIMIT @PageSize;
                """,
                new { ProjectId = projectId, Cursor = effectiveCursor, PageSize = effectivePageSize + 1 },
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

            long? nextCursor = null;
            if (rows.Count > effectivePageSize)
            {
                nextCursor = rows[effectivePageSize - 1].RowNo;
                rows.RemoveAt(rows.Count - 1);
            }

            return new ValidationDetailsPageResult(rows, nextCursor);
        }

        private IEnumerable<string> ValidationTables()
        {
            yield return _names.Resolve(JetTable.ResultValidationV1);
            yield return _names.Resolve(JetTable.ResultValidationV2);
            yield return _names.Resolve(JetTable.ResultValidationV3);
            yield return _names.Resolve(JetTable.ResultValidationV4);
        }

        private string ResolveValidationResultTable(string kind)
        {
            return (kind ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "v1" => _names.Resolve(JetTable.ResultValidationV1),
                "v2" => _names.Resolve(JetTable.ResultValidationV2),
                "v3" => _names.Resolve(JetTable.ResultValidationV3),
                "v4" => _names.Resolve(JetTable.ResultValidationV4),
                _ => throw new ArgumentException($"Unsupported validation detail kind: {kind}", nameof(kind))
            };
        }

        private static async Task<(string Acc, double TbAmt, double GlAmt, double Diff)[]> QueryDiffRowsAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string tb,
            string gl,
            string projectId,
            CancellationToken cancellationToken)
        {
            var tbRows = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                $"SELECT COUNT(1) FROM {tb} WHERE project_id = @ProjectId;",
                new { ProjectId = projectId },
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (tbRows == 0)
            {
                return Array.Empty<(string Acc, double TbAmt, double GlAmt, double Diff)>();
            }

            return (await connection.QueryAsync<(string Acc, double TbAmt, double GlAmt, double Diff)>(new CommandDefinition(
                $"""
                WITH gl_sum AS (
                    SELECT acc_num, SUM(amount) AS GlAmt
                    FROM {gl}
                    WHERE project_id = @ProjectId AND acc_num IS NOT NULL AND TRIM(acc_num) <> ''
                    GROUP BY acc_num
                ),
                tb_sum AS (
                    SELECT acc_num, SUM(change_amount) AS TbAmt
                    FROM {tb}
                    WHERE project_id = @ProjectId AND acc_num IS NOT NULL AND TRIM(acc_num) <> ''
                    GROUP BY acc_num
                )
                SELECT
                    tb_sum.acc_num AS Acc,
                    COALESCE(tb_sum.TbAmt, 0) AS TbAmt,
                    COALESCE(gl_sum.GlAmt, 0) AS GlAmt,
                    COALESCE(tb_sum.TbAmt, 0) - COALESCE(gl_sum.GlAmt, 0) AS Diff
                FROM tb_sum
                LEFT JOIN gl_sum ON gl_sum.acc_num = tb_sum.acc_num
                WHERE ABS(COALESCE(tb_sum.TbAmt, 0) - COALESCE(gl_sum.GlAmt, 0)) > 0.01
                ORDER BY tb_sum.acc_num;
                """,
                new { ProjectId = projectId },
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToArray();
        }

        private static async Task<int> InsertResultAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string resultTable,
            string runId,
            string projectId,
            string gl,
            string predicate,
            string reason,
            CancellationToken cancellationToken)
        {
            return await connection.ExecuteAsync(new CommandDefinition(
                $"""
                INSERT INTO {resultTable}
                    (project_id, run_id, row_no, batch_id, doc_num, line_id, acc_num, reason)
                SELECT
                    project_id,
                    @RunId,
                    ROW_NUMBER() OVER (ORDER BY batch_id, doc_num, line_id),
                    batch_id,
                    doc_num,
                    line_id,
                    acc_num,
                    @Reason
                FROM {gl}
                WHERE {predicate};
                """,
                new { ProjectId = projectId, RunId = runId, Reason = reason },
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        private static async Task<int> InsertV4Async(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string resultTable,
            string runId,
            string projectId,
            string gl,
            string project,
            CancellationToken cancellationToken)
        {
            return await connection.ExecuteAsync(new CommandDefinition(
                $"""
                INSERT INTO {resultTable}
                    (project_id, run_id, row_no, batch_id, doc_num, line_id, acc_num, reason)
                WITH invalid AS (
                    SELECT g.project_id, g.batch_id, g.doc_num, g.line_id, g.acc_num
                    FROM {gl} g
                    JOIN {project} p ON p.project_id = g.project_id
                    WHERE g.project_id = @ProjectId AND g.doc_date IS NOT NULL AND g.doc_date <> '' AND g.doc_date < p.period_start
                    UNION ALL
                    SELECT g.project_id, g.batch_id, g.doc_num, g.line_id, g.acc_num
                    FROM {gl} g
                    JOIN {project} p ON p.project_id = g.project_id
                    WHERE g.project_id = @ProjectId AND g.doc_date IS NOT NULL AND g.doc_date <> '' AND g.doc_date > p.period_end
                    UNION ALL
                    SELECT g.project_id, g.batch_id, g.doc_num, g.line_id, g.acc_num
                    FROM {gl} g
                    JOIN {project} p ON p.project_id = g.project_id
                    WHERE g.project_id = @ProjectId AND (g.doc_date IS NULL OR g.doc_date = '') AND g.post_date IS NOT NULL AND g.post_date <> '' AND g.post_date < p.period_start
                    UNION ALL
                    SELECT g.project_id, g.batch_id, g.doc_num, g.line_id, g.acc_num
                    FROM {gl} g
                    JOIN {project} p ON p.project_id = g.project_id
                    WHERE g.project_id = @ProjectId AND (g.doc_date IS NULL OR g.doc_date = '') AND g.post_date IS NOT NULL AND g.post_date <> '' AND g.post_date > p.period_end
                )
                SELECT project_id, @RunId, ROW_NUMBER() OVER (ORDER BY batch_id, doc_num, line_id), batch_id, doc_num, line_id, acc_num, @Reason
                FROM invalid;
                """,
                new { ProjectId = projectId, RunId = runId, Reason = "Out of period" },
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }
}
