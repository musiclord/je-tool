using System.Data;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// SQL Server 的情境發布實作（對應 <see cref="LocalFilterCommitRepository"/>）。整批發布與儲存一個情境都在同一條連線及
/// transaction 裡完成，只有 caller token 在 commit 邊界前仍有效；真正 commit 使用不可取消 token。
/// </summary>
public sealed class SqlServerFilterCommitRepository(
    SqlServerProjectDatabase database,
    ILogger<SqlServerFilterCommitRepository>? logger = null) : IFilterCommitRepository
{
    private const string Provider = "sqlServer";

    private readonly ILogger _log = logger ?? NullLogger<SqlServerFilterCommitRepository>.Instance;

    public async Task CommitAsync(
        string projectId,
        IReadOnlyList<FilterCommitItem> items,
        FilterConditionContext context,
        CancellationToken cancellationToken)
    {
        ValidateBatch(items, context);

        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        foreach (var statement in FilterHitInsertSql.DeleteAll("{s}.").Append("DELETE FROM {s}.filter_scenario;"))
        {
            await using var clear = database.CreateCommand(connection, projectId, statement);
            clear.Transaction = transaction;
            await clear.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
        }

        foreach (var item in items)
        {
            await InsertDefinitionAsync(connection, transaction, projectId, item.Definition, cancellationToken);
        }

        foreach (var item in items)
        {
            await InsertResultsAsync(connection, transaction, projectId, item, context, cancellationToken);
        }

        await ResultStaleStateSql.ClearFilterWithinAsync(
            connection,
            transaction,
            cancellationToken,
            SqlServerProjectSchema.QualifierFor(projectId));

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

        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        foreach (var statement in FilterHitInsertSql.DeleteOne("{s}.")
            .Append("DELETE FROM {s}.filter_scenario WHERE scenario_id = @scenarioId;"))
        {
            await using var clear = database.CreateCommand(connection, projectId, statement);
            clear.Transaction = transaction;
            clear.Parameters.AddWithValue("@scenarioId", item.Definition.ScenarioId);
            await clear.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
        }

        await InsertDefinitionAsync(connection, transaction, projectId, item.Definition, cancellationToken);
        await InsertResultsAsync(connection, transaction, projectId, item, context, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
    }

    private async Task InsertDefinitionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string projectId,
        SavedFilterScenario definition,
        CancellationToken cancellationToken)
    {
        await using var insert = database.CreateCommand(
            connection,
            projectId,
            """
            INSERT INTO {s}.filter_scenario (scenario_id, position, name, rationale, definition_json, saved_utc)
            VALUES (@scenarioId, @position, @name, @rationale, @definitionJson, @savedUtc);
            """);
        insert.Transaction = transaction;
        insert.Parameters.Add("@scenarioId", SqlDbType.Int).Value = definition.ScenarioId;
        insert.Parameters.Add("@position", SqlDbType.Int).Value = definition.Position;
        insert.Parameters.Add("@name", SqlDbType.NVarChar, FilterScenarioLimits.MaxNameLength).Value = definition.Name;
        insert.Parameters.Add("@rationale", SqlDbType.NVarChar, -1).Value = definition.Rationale;
        insert.Parameters.Add("@definitionJson", SqlDbType.NVarChar, -1).Value = definition.DefinitionJson;
        insert.Parameters.Add("@savedUtc", SqlDbType.NVarChar, 40).Value = definition.SavedUtc.ToString("O");
        await insert.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
    }

    private Task InsertResultsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string projectId,
        FilterCommitItem item,
        FilterConditionContext context,
        CancellationToken cancellationToken) =>
        FilterHitInsertSql.InsertOneAsync(
            sql => database.CreateCommand(connection, projectId, sql),
            transaction,
            SqlServerDialect.Instance,
            tablePrefix: "{s}.",
            schemaPrefix: SqlServerProjectSchema.QualifierFor(projectId),
            item.Definition.ScenarioId,
            item.Spec,
            context,
            _log,
            Provider,
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
