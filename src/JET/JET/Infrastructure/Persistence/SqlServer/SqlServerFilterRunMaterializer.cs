using JET.AuditCore;
using JET.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// filter.commit 命中落地的 SQL Server 實作（對應 <see cref="LocalFilterRunMaterializer"/>）。
/// WHERE 組譯共用 provider 中立的 <see cref="GlFilterWhereBuilder"/>（述詞 + <see cref="SqlServerDialect"/>），
/// 與 filter.preview 同源；本類只負責連線、交易與 INSERT…SELECT 骨架。
/// 單交易先 DELETE 全表再逐情境插入（冪等）。
/// </summary>
public sealed class SqlServerFilterRunMaterializer(SqlServerProjectDatabase database, ILogger<SqlServerFilterRunMaterializer>? logger = null)
    : IFilterRunMaterializer
{
    private const string Provider = "sqlServer";

    private static readonly GlFilterWhereBuilder WhereBuilder =
        new(
            SqlServerDialect.Instance,
            new GlRulePredicates(SqlServerDialect.Instance, GlPopulationScopeSql.Predicate));

    private readonly ILogger _log = logger ?? NullLogger<SqlServerFilterRunMaterializer>.Instance;

    public async Task MaterializeAsync(
        string projectId,
        IReadOnlyList<MaterializableScenario> scenarios,
        FilterRuleContext context,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var clear = database.CreateCommand(connection, projectId,
            "DELETE FROM {s}.result_filter_run;"))
        {
            clear.Transaction = transaction;
            await clear.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
        }

        // 連續零尾數條件的模數與 filter.preview / prescreen.run 同源（固定預設 Domain 門檻）。
        var zeroModulus = TrailingZeroThreshold.UnitModulus(
            TrailingZeroThreshold.DefaultZerosThreshold);

        foreach (var saved in scenarios)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            var plan = WhereBuilder.BuildPlan(
                saved.Spec,
                context,
                zeroModulus,
                SqlServerProjectSchema.QualifierFor(projectId));
            plan.BindParametersTo(insert);
            insert.Parameters.AddWithValue("@scenarioPosition", saved.Position);
            // {s} 由命令工廠收斂(單一替換點);WhereBuilder 需先綁到 insert,故借工廠展開 token 後回填本命令。
            await using (var expand = database.CreateCommand(connection, projectId,
                FilterRunHitInsertSql.Build(
                    tablePrefix: "{s}.",
                    populationPredicateSql: GlPopulationScopeSql.Predicate(context, "g"),
                    scenarioPredicateSql: plan.Sql)))
            {
                insert.CommandText = expand.CommandText;
            }
            await insert.ExecuteNonQueryLoggedAsync(_log, Provider, cancellationToken);
        }

        await ResultStaleStateSql.ClearFilterWithinAsync(
            connection,
            transaction,
            cancellationToken,
            SqlServerProjectSchema.QualifierFor(projectId));

        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
    }
}
