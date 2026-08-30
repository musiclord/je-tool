using System.Data;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// SQL Server 的 filter.commit 整批發布實作。definitions 與命中集合共用一條連線及
/// transaction，只有 caller token 在 commit 邊界前仍有效；真正 commit 使用不可取消 token。
/// </summary>
public sealed class SqlServerFilterCommitRepository(
    SqlServerProjectDatabase database,
    ILogger<SqlServerFilterCommitRepository>? logger = null) : IFilterCommitRepository
{
    private const string Provider = "sqlServer";

    private static readonly GlFilterWhereBuilder WhereBuilder =
        new(
            SqlServerDialect.Instance,
            new GlRulePredicates(SqlServerDialect.Instance, GlPopulationScopeSql.Predicate));

    private readonly ILogger _log = logger ?? NullLogger<SqlServerFilterCommitRepository>.Instance;

    public async Task CommitAsync(
        string projectId,
        IReadOnlyList<FilterCommitItem> items,
        FilterRuleContext context,
        CancellationToken cancellationToken)
    {
        ValidateBatch(items, context);

        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var clearHits = database.CreateCommand(
            connection,
            projectId,
            "DELETE FROM {s}.result_filter_run;"))
        {
            clearHits.Transaction = transaction;
            await clearHits.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
        }

        await using (var clearDefinitions = database.CreateCommand(
            connection,
            projectId,
            "DELETE FROM {s}.config_filter_scenario;"))
        {
            clearDefinitions.Transaction = transaction;
            await clearDefinitions.ExecuteNonQueryLoggedAsync(
                _log,
                Provider,
                cancellationToken);
        }

        await InsertDefinitionsAsync(
            connection,
            transaction,
            projectId,
            items,
            cancellationToken);
        await InsertHitsAsync(
            connection,
            transaction,
            projectId,
            items,
            context,
            cancellationToken);
        await ResultStaleStateSql.ClearFilterWithinAsync(
            connection,
            transaction,
            cancellationToken,
            SqlServerProjectSchema.QualifierFor(projectId));

        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
    }

    private async Task InsertDefinitionsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string projectId,
        IReadOnlyList<FilterCommitItem> items,
        CancellationToken cancellationToken)
    {
        await using var insert = database.CreateCommand(
            connection,
            projectId,
            """
            INSERT INTO {s}.config_filter_scenario (position, name, rationale, definition_json, saved_utc)
            VALUES (@position, @name, @rationale, @definitionJson, @savedUtc);
            """);
        insert.Transaction = transaction;

        var position = insert.Parameters.Add("@position", SqlDbType.Int);
        var name = insert.Parameters.Add("@name", SqlDbType.NVarChar, 400);
        var rationale = insert.Parameters.Add("@rationale", SqlDbType.NVarChar, -1);
        var definitionJson = insert.Parameters.Add("@definitionJson", SqlDbType.NVarChar, -1);
        var savedUtc = insert.Parameters.Add("@savedUtc", SqlDbType.NVarChar, 40);

        foreach (var item in items)
        {
            var definition = item.Definition;
            position.Value = definition.Position;
            name.Value = definition.Name;
            rationale.Value = definition.Rationale;
            definitionJson.Value = definition.DefinitionJson;
            savedUtc.Value = definition.SavedUtc.ToString("O");
            await insert.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
        }
    }

    private async Task InsertHitsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string projectId,
        IReadOnlyList<FilterCommitItem> items,
        FilterRuleContext context,
        CancellationToken cancellationToken)
    {
        var zeroModulus = TrailingZeroThreshold.UnitModulus(
            TrailingZeroThreshold.DefaultZerosThreshold);
        var schemaPrefix = SqlServerProjectSchema.QualifierFor(projectId);

        foreach (var item in items)
        {
            var plan = WhereBuilder.BuildPlan(
                item.Spec,
                context,
                zeroModulus,
                schemaPrefix);
            await using var insert = database.CreateCommand(
                connection,
                projectId,
                FilterRunHitInsertSql.Build(
                    tablePrefix: "{s}.",
                    populationPredicateSql: GlPopulationScopeSql.Predicate(context, "g"),
                    scenarioPredicateSql: plan.Sql));
            insert.Transaction = transaction;
            plan.BindParametersTo(insert);
            insert.Parameters.AddWithValue("@scenarioPosition", item.Definition.Position);
            await insert.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
        }
    }

    private static void ValidateBatch(
        IReadOnlyList<FilterCommitItem> items,
        FilterRuleContext context)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(context);

        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(item.Definition);
            ArgumentNullException.ThrowIfNull(item.Spec);
        }
    }
}
