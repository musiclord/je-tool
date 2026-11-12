using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// 結果過期後重算已存情境的本地引擎實作。命中與符合第幾組第幾條的寫法和儲存情境共用
/// <see cref="FilterHitInsertSql"/>；本類只負責連線、交易與清除範圍。
/// 全案重算在同交易替換全部結果；所選重算只替換指定情境，且不清除全案失效旗標。
/// </summary>
public sealed class LocalFilterHitMaterializer(ILocalProjectDatabase database, ILogger<LocalFilterHitMaterializer>? logger = null)
    : IFilterHitMaterializer
{
    private readonly string _provider = database.Dialect.ProviderName;

    private readonly ILogger _log = logger ?? NullLogger<LocalFilterHitMaterializer>.Instance;

    public async Task MaterializeAsync(
        string projectId,
        IReadOnlyList<MaterializableScenario> scenarios,
        FilterConditionContext context,
        CancellationToken cancellationToken,
        bool replaceAll = true)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        if (replaceAll)
        {
            foreach (var statement in FilterHitInsertSql.DeleteAll(string.Empty))
            {
                await using var clear = connection.CreateCommand();
                clear.Transaction = transaction;
                clear.CommandText = statement;
                await clear.ExecuteNonQueryLoggedAsync(_log, _provider, cancellationToken);
            }
        }

        foreach (var saved in scenarios)
        {
            if (!replaceAll)
            {
                foreach (var statement in FilterHitInsertSql.DeleteOne(string.Empty))
                {
                    await using var clear = connection.CreateCommand();
                    clear.Transaction = transaction;
                    clear.CommandText = statement;
                    clear.AddWithValue("@scenarioId", saved.ScenarioId);
                    await clear.ExecuteNonQueryLoggedAsync(_log, _provider, cancellationToken);
                }
            }

            await FilterHitInsertSql.InsertOneAsync(
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
                saved.ScenarioId,
                saved.Spec,
                context,
                _log,
                _provider,
                cancellationToken);
        }

        if (replaceAll)
        {
            await ResultStaleStateSql.ClearFilterWithinAsync(
                connection, transaction, cancellationToken, schemaPrefix: string.Empty);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
    }
}
