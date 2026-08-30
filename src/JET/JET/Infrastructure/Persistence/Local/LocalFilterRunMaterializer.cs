using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// filter.commit 命中落地的本地引擎實作（plan 子專案 D1 Task 2）。
/// WHERE 組譯共用 provider 中立的 <see cref="GlFilterWhereBuilder"/>（述詞 + 注入的 <see cref="ISqlDialect"/>），
/// 與 filter.preview 同源；本類只負責連線、交易與 INSERT…SELECT 骨架。
/// 單交易先 DELETE 全表再逐情境插入（冪等）；識別字皆常數，scenario_position 與 where 參數綁定。
/// </summary>
public sealed class LocalFilterRunMaterializer(ILocalProjectDatabase database, ILogger<LocalFilterRunMaterializer>? logger = null)
    : IFilterRunMaterializer
{
    // 診斷 provider 標籤由方言注入（sqlite／duckdb），不再寫死。
    private readonly string _provider = database.Dialect.ProviderName;

    private readonly GlFilterWhereBuilder WhereBuilder =
        new(
            database.Dialect,
            new GlRulePredicates(database.Dialect, GlPopulationScopeSql.Predicate));

    private readonly ILogger _log = logger ?? NullLogger<LocalFilterRunMaterializer>.Instance;

    public async Task MaterializeAsync(
        string projectId,
        IReadOnlyList<MaterializableScenario> scenarios,
        FilterRuleContext context,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM result_filter_run;";
            await clear.ExecuteNonQueryLoggedAsync(_log, _provider, cancellationToken);
        }

        // 連續零尾數條件的模數與 filter.preview / prescreen.run 同源（固定預設 Domain 門檻）。
        var zeroModulus = TrailingZeroThreshold.UnitModulus(
            TrailingZeroThreshold.DefaultZerosThreshold);

        foreach (var saved in scenarios)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            var plan = WhereBuilder.BuildPlan(saved.Spec, context, zeroModulus);
            plan.BindParametersTo(insert);
            insert.AddWithValue("@scenarioPosition", saved.Position);
            insert.CommandText = FilterRunHitInsertSql.Build(
                tablePrefix: string.Empty,
                populationPredicateSql: GlPopulationScopeSql.Predicate(context, "g"),
                scenarioPredicateSql: plan.Sql);
            await insert.ExecuteNonQueryLoggedAsync(_log, _provider, cancellationToken);
        }

        await ResultStaleStateSql.ClearFilterWithinAsync(
            connection,
            transaction,
            cancellationToken,
            schemaPrefix: string.Empty);

        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
    }
}
