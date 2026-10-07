using System.Data.Common;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Schema v7 result stale state is durable lifecycle state, distinct from the absence of a result.
/// The lowest provider seam locks the mutation -> stale -> successful regeneration transitions and
/// the transaction boundary used by rule and filter result publication.
/// </summary>
public sealed class ResultStaleStateLifecycleTests
{
    private static readonly FilterRuleContext FilterContext = new(
        ProjectDocument.DefaultMoneyScale,
        LastPeriodStart: null,
        PeriodStart: "2025-01-01",
        PeriodEnd: "2025-12-31",
        PopulationScope: GlPopulationScope.AuditPeriod);

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task LocalProvider_ResultLifecycle_PreservesExactStaleTransitions(string provider)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = provider == ProjectDocument.DuckDbDatabaseProvider
            ? new DuckDbProjectDatabase(folder)
            : new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);

        await AssertLifecycleAsync(
            projectId,
            new LocalResultStaleStateStore(database),
            new LocalRuleRunStore(database),
            new LocalFilterScenarioStore(database),
            new LocalFilterRunMaterializer(database),
            new LocalFilterCommitRepository(database),
            database.CreateConnection,
            schemaPrefix: string.Empty);
    }

    [SqlServerFact]
    public async Task SqlServer_ResultLifecycle_PreservesExactStaleTransitions()
    {
        await using var project = Assert.IsType<TempSqlServerProject>(
            await TempSqlServerProject.TryCreateAsync());

        await AssertLifecycleAsync(
            project.ProjectId,
            new SqlServerResultStaleStateStore(project.Database),
            new SqlServerRuleRunStore(project.Database),
            new SqlServerFilterScenarioStore(project.Database),
            new SqlServerFilterRunMaterializer(project.Database),
            new SqlServerFilterCommitRepository(project.Database),
            project.Database.CreateConnection,
            SqlServerProjectSchema.QualifierFor(project.ProjectId));
    }

    private static async Task AssertLifecycleAsync(
        string projectId,
        IResultStaleStateStore staleStore,
        IRuleRunStore ruleRunStore,
        IFilterScenarioStore scenarioStore,
        IFilterRunMaterializer materializer,
        IFilterCommitRepository commitRepository,
        Func<string, DbConnection> connectionFactory,
        string schemaPrefix)
    {
        Assert.Equal(
            new AuditResultStaleState(false, false, false),
            await staleStore.ReadAsync(projectId, CancellationToken.None));

        // No prior result means "never run", not stale, even for a migration-wide mutation.
        await ResetAsync(
            connectionFactory(projectId),
            schemaPrefix,
            AuditMutation.SchemaV7Migration);
        Assert.Equal(
            new AuditResultStaleState(false, false, false),
            await staleStore.ReadAsync(projectId, CancellationToken.None));

        await SeedAllResultKindsAsync(connectionFactory(projectId), schemaPrefix);

        // Taxonomy affects prescreen/filter only. Validation summary and INF rows remain current.
        // 使用者 2026-10-07 裁定上游修改清除下游：篩選情境與命中一起清掉，沒有可重跑的篩選，filter 回到從未執行。
        // 第一次失敗收據 20261007-033240978-87b22715a057481a849ce3013975e5c1。
        await ResetAsync(
            connectionFactory(projectId),
            schemaPrefix,
            AuditMutation.AccountTaxonomy);
        Assert.Equal(
            new AuditResultStaleState(false, true, false),
            await staleStore.ReadAsync(projectId, CancellationToken.None));
        Assert.Equal(0, await ScalarAsync(
            connectionFactory(projectId),
            $"SELECT COUNT(*) FROM {schemaPrefix}result_filter_run;"));
        Assert.Equal(1, await ScalarAsync(
            connectionFactory(projectId),
            $"SELECT COUNT(*) FROM {schemaPrefix}result_rule_run WHERE run_kind = 'validate';"));
        Assert.Equal(1, await ScalarAsync(
            connectionFactory(projectId),
            $"SELECT COUNT(*) FROM {schemaPrefix}result_inf_sampling_test_sample;"));

        // TB projection invalidates all families; only validation still exists after the earlier reset.
        // filter 在上一步分類修改時已回到從未執行，以下 filter 期望值隨之改為 false（同一次第一次失敗收據）。
        await ResetAsync(
            connectionFactory(projectId),
            schemaPrefix,
            AuditMutation.TbProjection);
        Assert.Equal(
            new AuditResultStaleState(true, true, false),
            await staleStore.ReadAsync(projectId, CancellationToken.None));

        await ruleRunStore.SaveAsync(
            projectId,
            Run("new-validation", RuleRunKinds.Validate),
            CancellationToken.None);
        Assert.Equal(
            new AuditResultStaleState(false, true, false),
            await staleStore.ReadAsync(projectId, CancellationToken.None));

        await SetStaleAsync(
            connectionFactory(projectId),
            schemaPrefix,
            "validation_stale");
        await Assert.ThrowsAnyAsync<Exception>(() => ruleRunStore.SaveAsync(
            projectId,
            Run("new-validation", RuleRunKinds.Validate),
            CancellationToken.None));
        Assert.True((await staleStore.ReadAsync(projectId, CancellationToken.None)).Validation);
        await ruleRunStore.SaveAsync(
            projectId,
            Run("new-validation-retry", RuleRunKinds.Validate),
            CancellationToken.None);
        Assert.False((await staleStore.ReadAsync(projectId, CancellationToken.None)).Validation);

        await ruleRunStore.SaveAsync(
            projectId,
            Run("new-prescreen", RuleRunKinds.Prescreen),
            CancellationToken.None);
        Assert.Equal(
            new AuditResultStaleState(false, false, false),
            await staleStore.ReadAsync(projectId, CancellationToken.None));

        // Replacing authoring definitions is not successful result materialization.
        // 使用者 2026-10-07 裁定上游修改清除下游：前面的分類修改已清掉情境與命中，這裡是從未執行的狀態，
        // 寫入定義不會憑空產生待重跑；原本斷言 true，第一次失敗收據 20261007-034754014-ce0d7c7e932f4d58b70af5629c1da8c2。
        await scenarioStore.ReplaceAllAsync(
            projectId,
            [new SavedFilterScenario(1, "definition", "rationale", "{}", DateTimeOffset.UnixEpoch)],
            CancellationToken.None);
        Assert.False((await staleStore.ReadAsync(projectId, CancellationToken.None)).Filter);

        // A successfully materialized revision may legitimately have zero hits. Its saved definition/revision
        // is therefore the durable completion marker: replacing it invalidates that result just like nonzero hits.
        await materializer.MaterializeAsync(
            projectId,
            [],
            FilterContext,
            CancellationToken.None);
        Assert.False((await staleStore.ReadAsync(projectId, CancellationToken.None)).Filter);

        await scenarioStore.ReplaceAllAsync(
            projectId,
            [new SavedFilterScenario(1, "no-hit-definition", "rationale", "{}", DateTimeOffset.UnixEpoch)],
            CancellationToken.None);
        Assert.True((await staleStore.ReadAsync(projectId, CancellationToken.None)).Filter);

        await materializer.MaterializeAsync(
            projectId,
            [],
            FilterContext,
            CancellationToken.None);
        Assert.False((await staleStore.ReadAsync(projectId, CancellationToken.None)).Filter);

        // 使用者 2026-10-07 裁定上游修改清除下游：上游修改不再把零命中的已存版本標成待重跑，而是連同定義一起清掉，
        // filter 回到從未執行。原本「零命中版本也要標過期」的保護改由上方 ReplaceAllAsync 的檢查涵蓋。
        // 第一次失敗收據 20261007-033240978-87b22715a057481a849ce3013975e5c1。
        await ResetAsync(
            connectionFactory(projectId),
            schemaPrefix,
            AuditMutation.AccountTaxonomy);
        Assert.False((await staleStore.ReadAsync(projectId, CancellationToken.None)).Filter);
        Assert.Equal(0, await ScalarAsync(
            connectionFactory(projectId),
            $"SELECT COUNT(*) FROM {schemaPrefix}config_filter_scenario;"));

        await SeedFilterHitAsync(connectionFactory(projectId), schemaPrefix);
        await scenarioStore.ReplaceAllAsync(
            projectId,
            [new SavedFilterScenario(1, "hit-definition", "rationale", "{}", DateTimeOffset.UnixEpoch)],
            CancellationToken.None);
        Assert.True((await staleStore.ReadAsync(projectId, CancellationToken.None)).Filter);

        await commitRepository.CommitAsync(
            projectId,
            [],
            FilterContext,
            CancellationToken.None);
        Assert.False((await staleStore.ReadAsync(projectId, CancellationToken.None)).Filter);
    }

    private static RuleRunRecord Run(string runId, string runKind) =>
        new(runId, runKind, DateTimeOffset.UnixEpoch, "{}");

    private static async Task ResetAsync(
        DbConnection connection,
        string schemaPrefix,
        AuditMutation mutation)
    {
        await using (connection)
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await RuleRunResultReset.ClearWithinAsync(
                connection,
                transaction,
                CancellationToken.None,
                mutation,
                schemaPrefix);
            await transaction.CommitAsync();
        }
    }

    private static async Task SeedAllResultKindsAsync(
        DbConnection connection,
        string schemaPrefix)
    {
        await using (connection)
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                $$"""
                INSERT INTO {{schemaPrefix}}result_rule_run
                    (run_id, run_kind, generated_utc, summary_json)
                VALUES
                    ('old-validation', 'validate', '2026-08-13T00:00:00.0000000+00:00', '{}'),
                    ('old-prescreen', 'prescreen', '2026-08-13T00:00:00.0000000+00:00', '{}');
                INSERT INTO {{schemaPrefix}}result_inf_sampling_test_sample
                    (run_id, entry_id, document_number, line_item)
                VALUES ('old-validation', 101, NULL, NULL);
                INSERT INTO {{schemaPrefix}}result_filter_run (scenario_position, entry_id)
                VALUES (1, 101);
                """;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task SeedFilterHitAsync(
        DbConnection connection,
        string schemaPrefix)
    {
        await using (connection)
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"INSERT INTO {schemaPrefix}result_filter_run (scenario_position, entry_id) VALUES (1, 202);";
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
    }

    private static async Task SetStaleAsync(
        DbConnection connection,
        string schemaPrefix,
        string column)
    {
        await using (connection)
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"UPDATE {schemaPrefix}config_result_stale_state SET {column} = 1 WHERE singleton = 1;";
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
    }

    private static async Task<long> ScalarAsync(DbConnection connection, string sql)
    {
        await using (connection)
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
    }
}
