using JET.AuditCore;
using JET.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// 結果過期後重算已存情境的 SQL Server 實作（對應 <see cref="LocalFilterHitMaterializer"/>）。
/// 命中與符合第幾組第幾條的寫法和儲存情境共用 <see cref="FilterHitInsertSql"/>；本類只負責連線、交易與清除範圍。
/// 全案重算替換全部結果；所選重算只替換指定情境，不將其他情境標成已更新。
/// </summary>
public sealed class SqlServerFilterHitMaterializer(SqlServerProjectDatabase database, ILogger<SqlServerFilterHitMaterializer>? logger = null)
    : IFilterHitMaterializer
{
    private const string Provider = "sqlServer";

    private readonly ILogger _log = logger ?? NullLogger<SqlServerFilterHitMaterializer>.Instance;

    public async Task MaterializeAsync(
        string projectId,
        IReadOnlyList<MaterializableScenario> scenarios,
        FilterConditionContext context,
        CancellationToken cancellationToken,
        bool replaceAll = true)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        if (replaceAll)
        {
            foreach (var statement in FilterHitInsertSql.DeleteAll("{s}."))
            {
                await using var clear = database.CreateCommand(connection, projectId, statement);
                clear.Transaction = transaction;
                await clear.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
            }
        }

        foreach (var saved in scenarios)
        {
            if (!replaceAll)
            {
                foreach (var statement in FilterHitInsertSql.DeleteOne("{s}."))
                {
                    await using var clear = database.CreateCommand(connection, projectId, statement);
                    clear.Transaction = transaction;
                    clear.Parameters.AddWithValue("@scenarioId", saved.ScenarioId);
                    await clear.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
                }
            }

            await FilterHitInsertSql.InsertOneAsync(
                sql => database.CreateCommand(connection, projectId, sql),
                transaction,
                SqlServerDialect.Instance,
                tablePrefix: "{s}.",
                schemaPrefix: SqlServerProjectSchema.QualifierFor(projectId),
                saved.ScenarioId,
                saved.Spec,
                context,
                _log,
                Provider,
                cancellationToken);
        }

        if (replaceAll)
        {
            await ResultStaleStateSql.ClearFilterWithinAsync(
                connection, transaction, cancellationToken, SqlServerProjectSchema.QualifierFor(projectId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
    }
}
