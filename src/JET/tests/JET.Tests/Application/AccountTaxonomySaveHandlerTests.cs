using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class AccountTaxonomySaveHandlerTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public Task Save_CustomCategory_RoundTripsWithStableBackendId_AndRejectsStaleRevision(
        string provider) =>
        VerifySaveRoundTripAsync(provider);

    [SqlServerFact]
    public async Task SqlServer_Save_CustomCategory_RoundTripsWithStableBackendId_AndRejectsStaleRevision()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync()
            ?? throw new InvalidOperationException(
                "SqlServerFact 已判定可用，但執行期無法取得 SQL Server 連線。");

        await VerifySaveRoundTripAsync("sqlServer", connectionString);
    }

    [Fact]
    public async Task Save_RejectsBuiltInRoleChange()
    {
        using var host = new HandlerTestHost();
        await CreateProjectAsync(host, "sqlite");
        var payload = SavePayload(
            1,
            AccountTaxonomyBuiltIns.All.Select(item => item.CategoryId == AccountTaxonomyBuiltIns.RevenueId
                ? Item(item.CategoryId, item.Label, item.Ordinal, AccountTaxonomyBuiltIns.OthersRole)
                : Item(item.CategoryId, item.Label, item.Ordinal, item.SemanticRole)));

        var error = await Assert.ThrowsAsync<JetActionException>(() =>
            host.DispatchAsync("accountTaxonomy.save", payload));

        Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
    }

    [Theory]
    [InlineData("mapping")]
    [InlineData("scenario")]
    public async Task Save_RejectsDeletingCategoryUsedByMapping_ButAllowsCategoryUsedOnlyByScenario(string useKind)
    {
        using var host = new HandlerTestHost();
        var projectId = await CreateProjectAsync(host, "sqlite");
        var saved = await host.DispatchAsync(
            "accountTaxonomy.save",
            SavePayload(1, AddCustom("Contract assets", AccountTaxonomyBuiltIns.ReceivablesRole)));
        var custom = saved.GetProperty("categories").EnumerateArray()
            .Single(item => !item.GetProperty("isBuiltIn").GetBoolean());
        var customId = custom.GetProperty("categoryId").GetString()!;

        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        await using (var connection = database.CreateConnection(projectId))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            if (useKind == "mapping")
            {
                command.CommandText =
                    """
                    INSERT INTO target_account_mapping
                        (batch_id, source_row_number, account_code, account_name, standardized_category, category_id)
                    VALUES ('taxonomy-test', 1, '1000', 'Test', 'Receivables', @categoryId);
                    """;
                command.AddWithValue("@categoryId", customId);
            }
            else
            {
                command.CommandText =
                    """
                    INSERT INTO config_filter_scenario
                        (position, name, rationale, definition_json, saved_utc)
                    VALUES (1, 'taxonomy-test', 'taxonomy-test', @definition, '2026-08-14T00:00:00Z');
                    """;
                command.AddWithValue(
                    "@definition",
                    JsonSerializer.Serialize(new { debitCategoryIds = new[] { customId } }));
            }
            await command.ExecuteNonQueryAsync();
        }

        var deleteCustom = SavePayload(2, AccountTaxonomyBuiltIns.All.Select(item =>
            Item(item.CategoryId, item.Label, item.Ordinal, item.SemanticRole)));
        if (useKind == "scenario")
        {
            // 使用者 2026-10-07 裁定上游修改清除下游：分類設定的修改會清掉全部已存情境，
            // 所以只被情境使用的分類改為可以刪除，情境一併清除。第一次失敗收據
            // 20261007-032952783-7147565fec1c42be845dac22acf7263b。
            var saved2 = await host.DispatchAsync("accountTaxonomy.save", deleteCustom);
            Assert.Equal(3, saved2.GetProperty("revision").GetInt32());
            Assert.DoesNotContain(
                (await new LocalAccountTaxonomyStore(database).ReadAsync(projectId, CancellationToken.None)).Categories,
                item => item.CategoryId == customId);
            Assert.Equal(0, await DemoProjectPipeline.QueryScalarAsync(
                host, projectId, "SELECT COUNT(*) FROM config_filter_scenario;"));
            return;
        }

        var error = await Assert.ThrowsAsync<JetActionException>(() =>
            host.DispatchAsync("accountTaxonomy.save", deleteCustom));

        Assert.Equal(JetErrorCodes.TaxonomyCategoryInUse, error.Code);
        Assert.Contains("科目配對", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("篩選情境", error.Message, StringComparison.Ordinal);
        var loaded = await new LocalAccountTaxonomyStore(database)
            .ReadAsync(projectId, CancellationToken.None);
        Assert.Equal(2, loaded.Revision);
        Assert.Contains(
            loaded.Categories,
            item => item.CategoryId == customId);
    }

    [Fact]
    public async Task Save_InvalidatesPrescreenAndFilterResults_ButPreservesValidation()
    {
        using var host = new HandlerTestHost();
        var projectId = await CreateProjectAsync(host, "sqlite");
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        await using (var connection = database.CreateConnection(projectId))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                INSERT INTO result_rule_run (run_id, run_kind, generated_utc, summary_json)
                VALUES ('taxonomy-validation', 'validate', '2026-08-14T00:00:00Z', '{}'),
                       ('taxonomy-prescreen', 'prescreen', '2026-08-14T00:00:00Z', '{}');
                INSERT INTO result_filter_run (scenario_position, entry_id) VALUES (1, 101);
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await host.DispatchAsync(
            "accountTaxonomy.save",
            SavePayload(1, AddCustom("Contract assets", AccountTaxonomyBuiltIns.ReceivablesRole)));

        Assert.Equal(1, await DemoProjectPipeline.QueryScalarAsync(
            host, projectId, "SELECT COUNT(*) FROM result_rule_run WHERE run_kind = 'validate';"));
        Assert.Equal(0, await DemoProjectPipeline.QueryScalarAsync(
            host, projectId, "SELECT COUNT(*) FROM result_rule_run WHERE run_kind = 'prescreen';"));
        Assert.Equal(0, await DemoProjectPipeline.QueryScalarAsync(
            host, projectId, "SELECT COUNT(*) FROM result_filter_run;"));
    }

    private static async Task VerifySaveRoundTripAsync(
        string provider,
        string? sqlServerConnectionString = null)
    {
        using var host = new HandlerTestHost(sqlServerConnectionString: sqlServerConnectionString);
        var projectId = await CreateProjectAsync(host, provider);
        try
        {
            var saved = await host.DispatchAsync(
                "accountTaxonomy.save",
                SavePayload(1, AddCustom("Contract assets", AccountTaxonomyBuiltIns.ReceivablesRole)));

            Assert.Equal(2, saved.GetProperty("revision").GetInt32());
            var custom = saved.GetProperty("categories").EnumerateArray()
                .Single(item => !item.GetProperty("isBuiltIn").GetBoolean());
            var customId = custom.GetProperty("categoryId").GetString()!;
            Assert.Matches("^custom\\.[0-9a-f]{32}$", customId);

            var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
            Assert.Equal(
                customId,
                loaded.GetProperty("taxonomy").GetProperty("categories").EnumerateArray()
                    .Single(item => !item.GetProperty("isBuiltIn").GetBoolean())
                    .GetProperty("categoryId").GetString());

            var stale = await Assert.ThrowsAsync<JetActionException>(() =>
                host.DispatchAsync(
                    "accountTaxonomy.save",
                    SavePayload(1, AccountTaxonomyBuiltIns.All.Select(item =>
                        Item(item.CategoryId, item.Label, item.Ordinal, item.SemanticRole)))));
            Assert.Equal(JetErrorCodes.TaxonomyRevisionConflict, stale.Code);
        }
        finally
        {
            if (sqlServerConnectionString is not null)
            {
                await TempSqlServerProject.DropDatabaseAsync(sqlServerConnectionString, projectId);
            }
        }
    }

    private static IEnumerable<object> AddCustom(string label, string semanticRole) =>
        AccountTaxonomyBuiltIns.All
            .Select(item => Item(item.CategoryId, item.Label, item.Ordinal, item.SemanticRole))
            .Append(new { label, ordinal = 5, semanticRole });

    private static object Item(string categoryId, string label, int ordinal, string semanticRole) =>
        new { categoryId, label, ordinal, semanticRole };

    private static string SavePayload(int revision, IEnumerable<object> categories) =>
        JsonSerializer.Serialize(new { revision, categories });

    private static async Task<string> CreateProjectAsync(HandlerTestHost host, string databaseProvider)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectCode = $"TAX-{suffix}",
            entityName = "Taxonomy Test",
            operatorId = "tester",
            periodStart = "2025-01-01",
            periodEnd = "2025-12-31",
            databaseProvider
        }));
        return created.GetProperty("projectId").GetString()!;
    }
}
