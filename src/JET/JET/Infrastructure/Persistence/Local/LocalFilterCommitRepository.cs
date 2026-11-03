using System.Data;
using System.Data.Common;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// SQLite／DuckDB 共用的情境發布實作。整批發布在同一個交易裡取代全部定義與結果；儲存一個情境只換掉
/// 這個情境的定義與結果。任何命令失敗或 caller 取消都由 transaction disposal rollback。
/// </summary>
public sealed class LocalFilterCommitRepository(
    ILocalProjectDatabase database,
    ILogger<LocalFilterCommitRepository>? logger = null) : IFilterCommitRepository
{
    private readonly string _provider = database.Dialect.ProviderName;

    private readonly ILogger _log = logger ?? NullLogger<LocalFilterCommitRepository>.Instance;

    public async Task CommitAsync(
        string projectId,
        IReadOnlyList<FilterCommitItem> items,
        FilterConditionContext context,
        CancellationToken cancellationToken)
    {
        ValidateBatch(items, context);

        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var statement in FilterHitInsertSql.DeleteAll(string.Empty).Append("DELETE FROM filter_scenario;"))
        {
            await using var clear = connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = statement;
            await clear.ExecuteNonQueryLoggedAsync(_log, _provider, cancellationToken);
        }

        foreach (var item in items)
        {
            await InsertDefinitionAsync(connection, transaction, item.Definition, cancellationToken);
        }

        foreach (var item in items)
        {
            await InsertResultsAsync(connection, transaction, item, context, cancellationToken);
        }

        await ResultStaleStateSql.ClearFilterWithinAsync(
            connection,
            transaction,
            cancellationToken,
            schemaPrefix: string.Empty);

        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
    }

    public async Task SaveOneAsync(
        string projectId,
        FilterCommitItem item,
        FilterConditionContext context,
        CancellationToken cancellationToken)
    {
        ValidateBatch([item], context);

        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var statement in FilterHitInsertSql.DeleteOne(string.Empty)
            .Append("DELETE FROM filter_scenario WHERE scenario_id = @scenarioId;"))
        {
            await using var clear = connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = statement;
            clear.AddWithValue("@scenarioId", item.Definition.ScenarioId);
            await clear.ExecuteNonQueryLoggedAsync(_log, _provider, cancellationToken);
        }

        await InsertDefinitionAsync(connection, transaction, item.Definition, cancellationToken);
        await InsertResultsAsync(connection, transaction, item, context, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
    }

    private async Task InsertDefinitionAsync(
        DbConnection connection,
        DbTransaction transaction,
        SavedFilterScenario definition,
        CancellationToken cancellationToken)
    {
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO filter_scenario (scenario_id, position, name, rationale, definition_json, saved_utc)
            VALUES (@scenarioId, @position, @name, @rationale, @definitionJson, @savedUtc);
            """;
        insert.AddParameter("@scenarioId", DbType.Int64).Value = definition.ScenarioId;
        insert.AddParameter("@position", DbType.Int64).Value = definition.Position;
        insert.AddParameter("@name", DbType.String).Value = definition.Name;
        insert.AddParameter("@rationale", DbType.String).Value = definition.Rationale;
        insert.AddParameter("@definitionJson", DbType.String).Value = definition.DefinitionJson;
        insert.AddParameter("@savedUtc", DbType.String).Value = definition.SavedUtc.ToString("O");
        await insert.ExecuteNonQueryLoggedAsync(_log, _provider, cancellationToken);
    }

    private Task InsertResultsAsync(
        DbConnection connection,
        DbTransaction transaction,
        FilterCommitItem item,
        FilterConditionContext context,
        CancellationToken cancellationToken) =>
        FilterHitInsertSql.InsertOneAsync(
            sql =>
            {
                var command = connection.CreateCommand();
                command.CommandText = sql;
                return command;
            },
            transaction,
            database.Dialect,
            tablePrefix: string.Empty,
            schemaPrefix: string.Empty,
            item.Definition.ScenarioId,
            item.Spec,
            context,
            _log,
            _provider,
            cancellationToken);

    private static void ValidateBatch(
        IReadOnlyList<FilterCommitItem> items,
        FilterConditionContext context)
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
