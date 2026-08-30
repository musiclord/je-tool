using Dapper;
using JET.Application.Commands.FilterScenario.Rules;
using JET.Domain.Abstractions.Persistence;
using JET.Domain.Abstractions.Repositories;
using JET.Infrastructure.Configuration;
using JET.Infrastructure.Persistence.Schema;
using Microsoft.Data.Sqlite;

namespace JET.Infrastructure.Persistence.Sqlite.Repositories
{
    /// <summary>
    /// SQLite implementation of <see cref="IScenarioRepository"/>. Composes per-rule
    /// SQL fragments (via <see cref="ScenarioGroupComposer"/>) into a single key-set
    /// query against <c>target_gl_entry</c>, set-based inserts matched rows into
    /// <c>result_filter_run</c>, and returns summary counts plus the first ≤1000
    /// preview rows. Mission-critical pushdown (plan §1.5.2 / §3.3.d).
    /// </summary>
    public sealed class SqliteScenarioRepository : IScenarioRepository
    {
        private const int PreviewLimit = 1000;

        private readonly string _connectionString;
        private readonly ISchemaNames _names;
        private readonly ScenarioGroupComposer _composer;

        public SqliteScenarioRepository(DatabaseOptions databaseOptions)
            : this(databaseOptions.SqliteConnectionString, new SqliteSchemaNames(), ScenarioGroupComposer.Default())
        {
        }

        public SqliteScenarioRepository(string connectionString, ISchemaNames names, ScenarioGroupComposer composer)
        {
            _connectionString = connectionString;
            _names = names;
            _composer = composer;
        }

        public async Task<ScenarioPreviewResult> PreviewAsync(string projectId, ScenarioDefinition scenario, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
            ArgumentNullException.ThrowIfNull(scenario);

            var runId = Guid.NewGuid().ToString("D");
            var label = string.IsNullOrWhiteSpace(scenario.Name) ? "未命名情境" : scenario.Name;
            var summary = _composer.Describe(scenario);

            var context = new ScenarioContext(
                projectId,
                _names.Resolve(JetTable.TargetGlEntry),
                _names.Resolve(JetTable.StagingAccountMappingRawRow),
                _names.Resolve(JetTable.ConfigImportBatch),
                _names.Resolve(JetTable.ConfigProject));

            var composed = _composer.Compose(scenario, context);
            if (!composed.HasRules)
            {
                return new ScenarioPreviewResult(runId, label, 0, 0, summary, Array.Empty<ScenarioFilterRow>());
            }

            var resultTable = _names.Resolve(JetTable.ResultFilterRun);
            var glTable = context.GlTable;

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            // Trim history to keep table bounded; only retain the most recent 5 runs per project.
            await connection.ExecuteAsync(new CommandDefinition(
                $"DELETE FROM {resultTable} WHERE project_id = @ProjectId AND run_id NOT IN (SELECT DISTINCT run_id FROM {resultTable} WHERE project_id = @ProjectId ORDER BY run_id DESC LIMIT 4);",
                new { ProjectId = projectId },
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            var parameters = new DynamicParameters();
            foreach (var (key, value) in composed.Parameters)
            {
                parameters.Add(key, value);
            }
            parameters.Add("ProjectId", projectId);
            parameters.Add("RunId", runId);
            parameters.Add("Label", label);

            var insertSql = $@"
INSERT INTO {resultTable}
    (project_id, run_id, row_no, scenario_label, batch_id, doc_num, line_id, post_date, doc_date, acc_num, acc_name, description, amount)
SELECT
    g.project_id,
    @RunId,
    ROW_NUMBER() OVER (ORDER BY g.batch_id, g.doc_num, g.line_id),
    @Label,
    g.batch_id,
    g.doc_num,
    g.line_id,
    g.post_date,
    g.doc_date,
    g.acc_num,
    g.acc_name,
    g.description,
    g.amount
FROM {glTable} g
WHERE g.project_id = @ProjectId AND ({composed.Predicate});";

            await connection.ExecuteAsync(new CommandDefinition(insertSql, parameters, transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

            var count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                $"SELECT COUNT(1) FROM {resultTable} WHERE project_id = @ProjectId AND run_id = @RunId;",
                new { ProjectId = projectId, RunId = runId },
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            var voucherCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                $"SELECT COUNT(DISTINCT doc_num) FROM {resultTable} WHERE project_id = @ProjectId AND run_id = @RunId AND doc_num IS NOT NULL AND TRIM(doc_num) <> '';",
                new { ProjectId = projectId, RunId = runId },
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            var preview = (await connection.QueryAsync<ScenarioFilterRow>(new CommandDefinition(
                $@"SELECT row_no AS RowNo, run_id AS RunId, batch_id AS BatchId,
                          doc_num AS DocNum, line_id AS LineId, post_date AS PostDate, doc_date AS DocDate,
                          acc_num AS AccNum, acc_name AS AccName, description AS Description, amount AS Amount
                  FROM {resultTable}
                  WHERE project_id = @ProjectId AND run_id = @RunId
                  ORDER BY row_no
                  LIMIT @Limit;",
                new { ProjectId = projectId, RunId = runId, Limit = PreviewLimit },
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new ScenarioPreviewResult(runId, label, count, voucherCount, summary, preview);
        }

        public async Task<ScenarioPageResult> QueryPageAsync(string projectId, string? runId, long? cursor, int pageSize, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
            var resultTable = _names.Resolve(JetTable.ResultFilterRun);
            var effectivePageSize = Math.Clamp(pageSize <= 0 ? 100 : pageSize, 1, PreviewLimit);

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var resolvedRunId = runId;
            if (string.IsNullOrWhiteSpace(resolvedRunId))
            {
                resolvedRunId = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                    $"SELECT run_id FROM {resultTable} WHERE project_id = @ProjectId ORDER BY run_id DESC LIMIT 1;",
                    new { ProjectId = projectId },
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
            if (string.IsNullOrWhiteSpace(resolvedRunId))
            {
                return new ScenarioPageResult(Array.Empty<ScenarioFilterRow>(), null);
            }

            var rows = (await connection.QueryAsync<ScenarioFilterRow>(new CommandDefinition(
                $@"SELECT row_no AS RowNo, run_id AS RunId, batch_id AS BatchId,
                          doc_num AS DocNum, line_id AS LineId, post_date AS PostDate, doc_date AS DocDate,
                          acc_num AS AccNum, acc_name AS AccName, description AS Description, amount AS Amount
                  FROM {resultTable}
                  WHERE project_id = @ProjectId AND run_id = @RunId AND row_no > @Cursor
                  ORDER BY row_no
                  LIMIT @Limit;",
                new { ProjectId = projectId, RunId = resolvedRunId, Cursor = cursor ?? 0, Limit = effectivePageSize + 1 },
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

            long? nextCursor = null;
            if (rows.Count > effectivePageSize)
            {
                nextCursor = rows[effectivePageSize - 1].RowNo;
                rows.RemoveAt(rows.Count - 1);
            }
            return new ScenarioPageResult(rows, nextCursor);
        }
    }
}
