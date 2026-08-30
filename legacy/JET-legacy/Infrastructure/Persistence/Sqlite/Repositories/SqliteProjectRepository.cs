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
    /// SQLite implementation of <see cref="IProjectRepository"/>. Opens a short-lived
    /// connection per call and writes <c>config_project</c> + <c>config_project_state</c>
    /// inside a single transaction. Physical table names resolved through
    /// <see cref="ISchemaNames"/> so the SQL stays provider-agnostic at the call site.
    /// </summary>
    public sealed class SqliteProjectRepository : IProjectRepository
    {
        private readonly string _connectionString;
        private readonly ISchemaNames _names;

        public SqliteProjectRepository(DatabaseOptions databaseOptions)
            : this(databaseOptions.SqliteConnectionString, new SqliteSchemaNames())
        {
        }

        public SqliteProjectRepository(string connectionString, ISchemaNames names)
        {
            _connectionString = connectionString;
            _names = names;
        }

        public async Task<string> CreateAsync(ProjectInfo project, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(project);

            var projectId = Guid.NewGuid().ToString("D");
            var nowUtc = DateTimeOffset.UtcNow.ToString("O");

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            var insertProjectSql = $"""
                INSERT INTO {_names.Resolve(JetTable.ConfigProject)}
                    (project_id, project_code, entity_name, operator_id, industry,
                     period_start, period_end, last_period_start, created_utc)
                VALUES
                    (@ProjectId, @ProjectCode, @EntityName, @OperatorId, @Industry,
                     @PeriodStart, @PeriodEnd, @LastPeriodStart, @CreatedUtc);
                """;

            var insertStateSql = $"""
                INSERT INTO {_names.Resolve(JetTable.ConfigProjectState)}
                    (project_id, current_step, updated_utc)
                VALUES
                    (@ProjectId, @CurrentStep, @UpdatedUtc);
                """;

            await connection.ExecuteAsync(new CommandDefinition(
                insertProjectSql,
                new
                {
                    ProjectId = projectId,
                    project.ProjectCode,
                    project.EntityName,
                    project.OperatorId,
                    project.Industry,
                    project.PeriodStart,
                    project.PeriodEnd,
                    project.LastPeriodStart,
                    CreatedUtc = nowUtc
                },
                transaction: transaction,
                cancellationToken: cancellationToken));

            await connection.ExecuteAsync(new CommandDefinition(
                insertStateSql,
                new
                {
                    ProjectId = projectId,
                    CurrentStep = 1,
                    UpdatedUtc = nowUtc
                },
                transaction: transaction,
                cancellationToken: cancellationToken));

            await transaction.CommitAsync(cancellationToken);

            return projectId;
        }

        public async Task<ProjectStateSnapshot?> LoadAsync(string projectId, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var project = await connection.QuerySingleOrDefaultAsync<ProjectRow?>(new CommandDefinition(
                $"""
                SELECT project_code AS ProjectCode, entity_name AS EntityName, operator_id AS OperatorId,
                       industry AS Industry, period_start AS PeriodStart, period_end AS PeriodEnd,
                       last_period_start AS LastPeriodStart
                FROM {_names.Resolve(JetTable.ConfigProject)}
                WHERE project_id = @ProjectId;
                """,
                new { ProjectId = projectId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (project is null) return null;

            var latestValidation = await LatestRunIdAsync(connection, _names.Resolve(JetTable.ResultValidationV1), projectId, cancellationToken).ConfigureAwait(false);
            var latestPrescreen = await LatestRunIdAsync(connection, _names.Resolve(JetTable.ResultPrescreenR5), projectId, cancellationToken).ConfigureAwait(false);
            var latestFilter = await LatestRunIdAsync(connection, _names.Resolve(JetTable.ResultFilterRun), projectId, cancellationToken).ConfigureAwait(false);

            return new ProjectStateSnapshot(
                projectId,
                new ProjectInfo
                {
                    ProjectCode = project.ProjectCode,
                    EntityName = project.EntityName,
                    OperatorId = project.OperatorId,
                    Industry = project.Industry,
                    PeriodStart = project.PeriodStart,
                    PeriodEnd = project.PeriodEnd,
                    LastPeriodStart = project.LastPeriodStart
                },
                latestValidation,
                latestPrescreen,
                latestFilter);
        }

        private static async Task<string?> LatestRunIdAsync(SqliteConnection connection, string table, string projectId, CancellationToken cancellationToken)
        {
            return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                $"SELECT run_id FROM {table} WHERE project_id = @ProjectId ORDER BY rowid DESC LIMIT 1;",
                new { ProjectId = projectId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        private sealed record ProjectRow(
            string ProjectCode,
            string EntityName,
            string OperatorId,
            string Industry,
            string PeriodStart,
            string PeriodEnd,
            string LastPeriodStart);
    }
}
