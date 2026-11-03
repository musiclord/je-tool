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
    private static readonly FilterConditionContext FilterContext = new(
        ProjectDocument.DefaultMoneyScale,
        PrepStartDate: null,
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
            new LocalResultStore(database),
            new LocalFilterScenarioStore(database),
            new LocalFilterHitMaterializer(database),
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
            new SqlServerResultStore(project.Database),
            new SqlServerFilterScenarioStore(project.Database),
            new SqlServerFilterHitMaterializer(project.Database),
            new SqlServerFilterCommitRepository(project.Database),
            project.Database.CreateConnection,
            SqlServerProjectSchema.QualifierFor(project.ProjectId));
    }

    private static async Task AssertLifecycleAsync(
        string projectId,
        IResultStaleStateStore staleStore,
        IResultStore resultStore,
        IFilterScenarioStore scenarioStore,
        IFilterHitMaterializer materializer,
        IFilterCommitRepository commitRepository,
        Func<string, DbConnection> connectionFactory,
        string schemaPrefix)
    {
        Assert.Equal(
            new AuditResultStaleState(false, false, false),
            await staleStore.ReadAsync(projectId, CancellationToken.None));

        // No prior result means "never run", not stale, even for a mutation that invalidates every result family.
        // 案件資料庫的升版程式拿掉後沒有升版用的 mutation，改用同樣三種結果都失效的 GL 投影。
        await ResetAsync(
            connectionFactory(projectId),
            schemaPrefix,
            AuditMutation.GlProjection);
        Assert.Equal(
            new AuditResultStaleState(false, false, false),
            await staleStore.ReadAsync(projectId, CancellationToken.None));

        await SeedAllResultKindsAsync(connectionFactory(projectId), schemaPrefix);

        // Account category changes affect prescreen/filter only. Validation summary and INF rows remain current.
        // 上游修改會清除下游（裁定 2026-10-07-03）：篩選情境與命中一起清掉，沒有可重跑的篩選，filter 回到從未執行。
        // 第一次失敗收據 20261007-033240978-87b22715a057481a849ce3013975e5c1。
        await ResetAsync(
            connectionFactory(projectId),
            schemaPrefix,
            AuditMutation.AccountCategory);
        Assert.Equal(
            new AuditResultStaleState(false, true, false),
            await staleStore.ReadAsync(projectId, CancellationToken.None));
        Assert.Equal(0, await ScalarAsync(
            connectionFactory(projectId),
            $"SELECT COUNT(*) FROM {schemaPrefix}filter_hit;"));
        Assert.Equal(1, await ScalarAsync(
            connectionFactory(projectId),
            $"SELECT COUNT(*) FROM {schemaPrefix}validation_result;"));
        Assert.Equal(1, await ScalarAsync(
            connectionFactory(projectId),
            $"SELECT COUNT(*) FROM {schemaPrefix}inf_sample;"));

        // TB projection invalidates all families; only validation still exists after the earlier reset.
        // filter 在上一步分類修改時已回到從未執行，以下 filter 期望值隨之改為 false（同一次第一次失敗收據）。
        await ResetAsync(
            connectionFactory(projectId),
            schemaPrefix,
            AuditMutation.TbProjection);
        Assert.Equal(
            new AuditResultStaleState(true, true, false),
            await staleStore.ReadAsync(projectId, CancellationToken.None));

        await resultStore.SaveAsync(
            projectId,
            Run("new-validation", ResultKind.Validation),
            CancellationToken.None);
        Assert.Equal(
            new AuditResultStaleState(false, true, false),
            await staleStore.ReadAsync(projectId, CancellationToken.None));

        await SetStaleAsync(
            connectionFactory(projectId),
            schemaPrefix,
            "validation_stale");
        await Assert.ThrowsAnyAsync<Exception>(() => resultStore.SaveAsync(
            projectId,
            Run("new-validation", ResultKind.Validation),
            CancellationToken.None));
        Assert.True((await staleStore.ReadAsync(projectId, CancellationToken.None)).Validation);
        await resultStore.SaveAsync(
            projectId,
            Run("new-validation-retry", ResultKind.Validation),
            CancellationToken.None);
        Assert.False((await staleStore.ReadAsync(projectId, CancellationToken.None)).Validation);

        await resultStore.SaveAsync(
            projectId,
            Run("new-prescreen", ResultKind.Prescreen),
            CancellationToken.None);
        Assert.Equal(
            new AuditResultStaleState(false, false, false),
            await staleStore.ReadAsync(projectId, CancellationToken.None));

        // Replacing authoring definitions is not successful result materialization.
        // 上游修改會清除下游（裁定 2026-10-07-03）：前面的分類修改已清掉情境與命中，這裡是從未執行的狀態，
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

        // 上游修改會清除下游（裁定 2026-10-07-03）：上游修改不再把零命中的已存版本標成待重跑，而是連同定義一起清掉，
        // filter 回到從未執行。原本「零命中版本也要標過期」的保護改由上方 ReplaceAllAsync 的檢查涵蓋。
        // 第一次失敗收據 20261007-033240978-87b22715a057481a849ce3013975e5c1。
        await ResetAsync(
            connectionFactory(projectId),
            schemaPrefix,
            AuditMutation.AccountCategory);
        Assert.False((await staleStore.ReadAsync(projectId, CancellationToken.None)).Filter);
        Assert.Equal(0, await ScalarAsync(
            connectionFactory(projectId),
            $"SELECT COUNT(*) FROM {schemaPrefix}filter_scenario;"));

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

    private static ResultRecord Run(string resultId, ResultKind kind) =>
        new(resultId, kind, DateTimeOffset.UnixEpoch, "{}");

    private static async Task ResetAsync(
        DbConnection connection,
        string schemaPrefix,
        AuditMutation mutation)
    {
        await using (connection)
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await ResultReset.ClearWithinAsync(
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
                INSERT INTO {{schemaPrefix}}validation_result
                    (result_id, generated_utc, summary_json)
                VALUES ('old-validation', '2026-08-13T00:00:00.0000000+00:00', '{}');
                INSERT INTO {{schemaPrefix}}prescreen_result
                    (result_id, generated_utc, summary_json)
                VALUES ('old-prescreen', '2026-08-13T00:00:00.0000000+00:00', '{}');
                INSERT INTO {{schemaPrefix}}inf_sample
                    (result_id, entry_id, voucher_number, line_item)
                VALUES ('old-validation', 101, NULL, NULL);
                INSERT INTO {{schemaPrefix}}filter_hit (scenario_id, entry_id)
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
                $"INSERT INTO {schemaPrefix}filter_hit (scenario_id, entry_id) VALUES (1, 202);";
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
                $"UPDATE {schemaPrefix}result_stale_state SET {column} = 1 WHERE singleton = 1;";
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
