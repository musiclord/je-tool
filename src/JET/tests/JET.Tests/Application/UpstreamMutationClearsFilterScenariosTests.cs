using System.Data.Common;
using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 使用者 2026-10-07 裁定上游修改清除下游：會讓篩選結果過期的上游修改，要在同一筆交易內清掉全部已存篩選情境與命中，
/// 回應以 <c>invalidatedResults.filterScenarios</c> 告知畫面；試算表與總帳的修改使用同一條規則。
/// </summary>
public sealed class UpstreamMutationClearsFilterScenariosTests
{
    private static readonly string[] ClearingActions =
    [
        "import.gl.fromFile", "mapping.commit.gl", "import.tb.fromFile", "mapping.commit.tb", "import.accountMapping.fromFile", "accountMapping.save",
        "accountTaxonomy.save", "import.authorizedPreparer.fromFile", "import.authorizedPreparer.clear",
        "import.holiday", "import.makeupDay", "import.holiday.fromFile", "import.makeupDay.fromFile",
        "calendar.setNonWorkingDays", "project.update.date"
    ];

    private static readonly string[] KeepingActions =
    [
        "calendar.sameNonWorkingDays", "project.update.metadata"
    ];

    public static TheoryData<string, string> ClearingCases() => Cross(ClearingActions);

    public static TheoryData<string, string> KeepingCases() => Cross(KeepingActions);

    public static IEnumerable<object[]> MutationCases() =>
        from provider in new[] { ProjectDocument.DefaultDatabaseProvider, ProjectDocument.DuckDbDatabaseProvider }
        from mutation in Enum.GetValues<AuditMutation>()
        select new object[] { provider, mutation };

    [Fact]
    public void Policy_ClearsScenarioDefinitionsExactlyWhenFilterHitsBecomeStale()
    {
        foreach (var mutation in Enum.GetValues<AuditMutation>())
        {
            var impact = AuditDependencyPolicy.For(mutation);
            Assert.Equal(impact.InvalidateFilterHits, impact.InvalidateFilterScenarioDefinitions);
        }
        Assert.True(AuditDependencyPolicy.For(AuditMutation.TbImport).InvalidateFilterScenarioDefinitions);
        Assert.True(AuditDependencyPolicy.For(AuditMutation.TbProjection).InvalidateFilterScenarioDefinitions);
        Assert.True(AuditDependencyPolicy.For(AuditMutation.AccountTaxonomy).InvalidateFilterScenarioDefinitions);
    }

    [Theory]
    [MemberData(nameof(ClearingCases))]
    public async Task ClearingAction_RemovesEverySavedScenarioAndHit(string provider, string action)
    {
        using var host = new HandlerTestHost();
        var prepared = await Batch9ArtifactStateTests.PrepareAsync(host, provider);
        Assert.Equal(1, await CountAsync(host, prepared.Id, provider, "config_filter_scenario"));
        Assert.True(await CountAsync(host, prepared.Id, provider, "result_filter_run") > 0);

        var response = await Batch9MutationStateTests.MutateAsync(host, prepared.Id, action);

        var invalidated = response.GetProperty("invalidatedResults");
        Assert.Equal(
            new[] { "filter", "filterScenarios", "prescreen", "validation" },
            invalidated.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal));
        Assert.True(invalidated.GetProperty("filter").GetBoolean());
        Assert.True(invalidated.GetProperty("filterScenarios").GetBoolean());
        // 情境已清掉，沒有可以重跑的篩選；第五步回到從未執行的預設狀態。
        Assert.False(response.GetProperty("staleState").GetProperty("filter").GetBoolean());
        Assert.Equal(0, await CountAsync(host, prepared.Id, provider, "config_filter_scenario"));
        Assert.Equal(0, await CountAsync(host, prepared.Id, provider, "result_filter_run"));

        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = prepared.Id }));
        Assert.Empty(loaded.GetProperty("filterScenarios").EnumerateArray());
        Assert.False(loaded.GetProperty("staleState").GetProperty("filter").GetBoolean());
    }

    [Theory]
    [MemberData(nameof(KeepingCases))]
    public async Task UnchangedAction_KeepsSavedScenariosAndHits(string provider, string action)
    {
        using var host = new HandlerTestHost();
        var prepared = await Batch9ArtifactStateTests.PrepareAsync(host, provider);
        var hitsBefore = await CountAsync(host, prepared.Id, provider, "result_filter_run");
        Assert.True(hitsBefore > 0);

        var response = await Batch9MutationStateTests.MutateAsync(host, prepared.Id, action);

        var invalidated = response.GetProperty("invalidatedResults");
        Assert.False(invalidated.GetProperty("filter").GetBoolean());
        Assert.False(invalidated.GetProperty("filterScenarios").GetBoolean());
        Assert.Equal(1, await CountAsync(host, prepared.Id, provider, "config_filter_scenario"));
        Assert.Equal(hitsBefore, await CountAsync(host, prepared.Id, provider, "result_filter_run"));
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = prepared.Id }));
        Assert.Single(loaded.GetProperty("filterScenarios").EnumerateArray());
    }

    [Theory]
    [MemberData(nameof(MutationCases))]
    internal async Task CommittedReset_ClearsDefinitionsOnlyWhenPolicySaysSo(string provider, AuditMutation mutation)
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await CreateDatabaseAsync(root, provider);
        await SeedScenarioAndHitAsync(database.CreateConnection(projectId));

        await using (var connection = database.CreateConnection(projectId))
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await RuleRunResultReset.ClearWithinAsync(connection, transaction, CancellationToken.None, mutation);
            await transaction.CommitAsync();
        }

        var cleared = AuditDependencyPolicy.For(mutation).InvalidateFilterHits;
        Assert.Equal(cleared ? 0 : 1, await ScalarAsync(database.CreateConnection(projectId), "SELECT COUNT(*) FROM config_filter_scenario;"));
        Assert.Equal(cleared ? 0 : 1, await ScalarAsync(database.CreateConnection(projectId), "SELECT COUNT(*) FROM result_filter_run;"));
        if (cleared)
        {
            Assert.False((await new LocalResultStaleStateStore(database).ReadAsync(projectId, CancellationToken.None)).Filter);
        }
    }

    [Theory]
    [MemberData(nameof(MutationCases))]
    internal async Task RolledBackReset_KeepsSavedScenariosAndHits(string provider, AuditMutation mutation)
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await CreateDatabaseAsync(root, provider);
        await SeedScenarioAndHitAsync(database.CreateConnection(projectId));
        var staleBefore = await new LocalResultStaleStateStore(database).ReadAsync(projectId, CancellationToken.None);

        await using (var connection = database.CreateConnection(projectId))
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await RuleRunResultReset.ClearWithinAsync(connection, transaction, CancellationToken.None, mutation);
            await using (var inside = connection.CreateCommand())
            {
                inside.Transaction = transaction;
                inside.CommandText = "SELECT COUNT(*) FROM config_filter_scenario;";
                var expectedInside = AuditDependencyPolicy.For(mutation).InvalidateFilterHits ? 0 : 1;
                Assert.Equal(expectedInside, Convert.ToInt64(await inside.ExecuteScalarAsync()));
            }
            await transaction.RollbackAsync();
        }

        Assert.Equal(1, await ScalarAsync(database.CreateConnection(projectId), "SELECT COUNT(*) FROM config_filter_scenario;"));
        Assert.Equal(1, await ScalarAsync(database.CreateConnection(projectId), "SELECT COUNT(*) FROM result_filter_run;"));
        Assert.Equal(staleBefore, await new LocalResultStaleStateStore(database).ReadAsync(projectId, CancellationToken.None));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task FailedAccountMappingImport_RollsBackAndKeepsSavedScenariosAndHits(string provider)
    {
        using var host = new HandlerTestHost();
        var prepared = await Batch9ArtifactStateTests.PrepareAsync(host, provider);
        var hitsBefore = await CountAsync(host, prepared.Id, provider, "result_filter_run");
        var badPath = Path.Combine(Path.GetTempPath(), "jet-upstream-clear-tests", Guid.NewGuid().ToString("N") + ".csv");
        Directory.CreateDirectory(Path.GetDirectoryName(badPath)!);
        await File.WriteAllTextAsync(badPath, "科目代號,科目名稱,標準化分類\n1000,合成資產,Cash\n9999,合成科目,NotACategory\n");
        try
        {
            var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
                "import.accountMapping.fromFile", JsonSerializer.Serialize(new { filePath = badPath })));
            Assert.Equal("projection_failed", error.Code);
        }
        finally { File.Delete(badPath); }

        Assert.Equal(1, await CountAsync(host, prepared.Id, provider, "config_filter_scenario"));
        Assert.Equal(hitsBefore, await CountAsync(host, prepared.Id, provider, "result_filter_run"));
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = prepared.Id }));
        Assert.Single(loaded.GetProperty("filterScenarios").EnumerateArray());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task TaxonomySave_DeletesCategoryUsedOnlyByScenario_AndClearsScenarios(string provider)
    {
        using var host = new HandlerTestHost();
        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            caseName = "合成分類刪除", periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider
        }));
        var projectId = created.GetProperty("projectId").GetString()!;
        var builtIns = AccountTaxonomyBuiltIns.All
            .Select(item => (object)new { categoryId = item.CategoryId, label = item.Label, ordinal = item.Ordinal, semanticRole = item.SemanticRole })
            .ToArray();
        var saved = await host.DispatchAsync("accountTaxonomy.save", JsonSerializer.Serialize(new
        {
            revision = 1,
            categories = builtIns.Append(new { label = "合成合約資產", ordinal = 5, semanticRole = AccountTaxonomyBuiltIns.ReceivablesRole })
        }));
        var customId = saved.GetProperty("categories").EnumerateArray()
            .Single(item => !item.GetProperty("isBuiltIn").GetBoolean())
            .GetProperty("categoryId").GetString()!;
        await Batch9ArtifactStateTests.ExecuteAsync(host, projectId, provider,
            "INSERT INTO config_filter_scenario (position, name, rationale, definition_json, saved_utc) "
            + "VALUES (1, '合成情境', '合成理由', @definition, '2026-10-07T00:00:00Z');",
            ("@definition", JsonSerializer.Serialize(new { debitCategoryIds = new[] { customId } })));

        var response = await host.DispatchAsync("accountTaxonomy.save", JsonSerializer.Serialize(new
        {
            revision = saved.GetProperty("revision").GetInt32(),
            categories = builtIns
        }));

        Assert.DoesNotContain(response.GetProperty("categories").EnumerateArray(),
            item => item.GetProperty("categoryId").GetString() == customId);
        Assert.True(response.GetProperty("invalidatedResults").GetProperty("filterScenarios").GetBoolean());
        Assert.Equal(0, await CountAsync(host, projectId, provider, "config_filter_scenario"));
    }

    private static TheoryData<string, string> Cross(IEnumerable<string> actions)
    {
        var data = new TheoryData<string, string>();
        foreach (var provider in new[] { "sqlite", "duckdb" })
        {
            foreach (var action in actions)
            {
                data.Add(provider, action);
            }
        }
        return data;
    }

    private static async Task<long> CountAsync(HandlerTestHost host, string projectId, string provider, string table)
    {
        var folder = new JetProjectFolder(host.ProjectsRoot);
        ILocalProjectDatabase database = provider == "duckdb" ? new DuckDbProjectDatabase(folder) : new SqliteProjectDatabase(folder);
        return await ScalarAsync(database.CreateConnection(projectId), $"SELECT COUNT(*) FROM {table};");
    }

    private static async Task<(ILocalProjectDatabase Database, string ProjectId)> CreateDatabaseAsync(TempProjectRoot root, string provider)
    {
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = provider == ProjectDocument.DuckDbDatabaseProvider
            ? new DuckDbProjectDatabase(folder)
            : new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        return (database, projectId);
    }

    private static async Task SeedScenarioAndHitAsync(DbConnection connection)
    {
        await using (connection)
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO config_filter_scenario (position, name, rationale, definition_json, saved_utc)
                VALUES (1, 'synthetic', 'synthetic', '{}', '2026-10-07T00:00:00Z');
                INSERT INTO result_filter_run (scenario_position, entry_id) VALUES (1, 101);
                """;
            await command.ExecuteNonQueryAsync();
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
