using System.Data;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// SQLite／DuckDB 共用的 filter.commit 整批發布實作。definitions 與命中集合在同一
/// transaction 內 replace；任何命令失敗或 caller 取消都由 transaction disposal rollback。
/// </summary>
public sealed class LocalFilterCommitRepository(
    ILocalProjectDatabase database,
    ILogger<LocalFilterCommitRepository>? logger = null) : IFilterCommitRepository
{
    private readonly string _provider = database.Dialect.ProviderName;

    private readonly GlFilterWhereBuilder _whereBuilder =
        new(
            database.Dialect,
            new GlRulePredicates(database.Dialect, GlPopulationScopeSql.Predicate));

    private readonly ILogger _log = logger ?? NullLogger<LocalFilterCommitRepository>.Instance;

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
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var clearHits = connection.CreateCommand())
        {
            clearHits.Transaction = transaction;
            clearHits.CommandText = "DELETE FROM result_filter_run;";
            await clearHits.ExecuteNonQueryLoggedAsync(_log, _provider, cancellationToken);
        }

        await using (var clearDefinitions = connection.CreateCommand())
        {
            clearDefinitions.Transaction = transaction;
            clearDefinitions.CommandText = "DELETE FROM config_filter_scenario;";
            await clearDefinitions.ExecuteNonQueryLoggedAsync(
                _log,
                _provider,
                cancellationToken);
        }

        await InsertDefinitionsAsync(
            connection,
            transaction,
            items,
            cancellationToken);
        await InsertHitsAsync(
            connection,
            transaction,
            items,
            context,
            cancellationToken);
        await ResultStaleStateSql.ClearFilterWithinAsync(
            connection,
            transaction,
            cancellationToken,
            schemaPrefix: string.Empty);

        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
    }

    private async Task InsertDefinitionsAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        IReadOnlyList<FilterCommitItem> items,
        CancellationToken cancellationToken)
    {
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO config_filter_scenario (position, name, rationale, definition_json, saved_utc)
            VALUES (@position, @name, @rationale, @definitionJson, @savedUtc);
            """;

        var position = insert.AddParameter("@position", DbType.Int64);
        var name = insert.AddParameter("@name", DbType.String);
        var rationale = insert.AddParameter("@rationale", DbType.String);
        var definitionJson = insert.AddParameter("@definitionJson", DbType.String);
        var savedUtc = insert.AddParameter("@savedUtc", DbType.String);

        foreach (var item in items)
        {
            var definition = item.Definition;
            position.Value = definition.Position;
            name.Value = definition.Name;
            rationale.Value = definition.Rationale;
            definitionJson.Value = definition.DefinitionJson;
            savedUtc.Value = definition.SavedUtc.ToString("O");
            await insert.ExecuteNonQueryLoggedAsync(_log, _provider, cancellationToken);
        }
    }

    private async Task InsertHitsAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        IReadOnlyList<FilterCommitItem> items,
        FilterRuleContext context,
        CancellationToken cancellationToken)
    {
        var zeroModulus = TrailingZeroThreshold.UnitModulus(
            TrailingZeroThreshold.DefaultZerosThreshold);

        foreach (var item in items)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            var plan = _whereBuilder.BuildPlan(item.Spec, context, zeroModulus);
            plan.BindParametersTo(insert);
            insert.AddWithValue("@scenarioPosition", item.Definition.Position);
            insert.CommandText = FilterRunHitInsertSql.Build(
                tablePrefix: string.Empty,
                populationPredicateSql: GlPopulationScopeSql.Predicate(context, "g"),
                scenarioPredicateSql: plan.Sql);
            await insert.ExecuteNonQueryLoggedAsync(_log, _provider, cancellationToken);
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
