using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class UsageScenarioMutationRollbackTests
{
    private const string Scenario = """{"scenarios":[{"name":"合成借方","rationale":"固定答案","groups":[{"rules":[{"type":"drCrOnly","drCr":"debit"}]}]}]}""";
    private static readonly string[] SnapshotTables =
    [
        "config_filter_scenario", "result_filter_run", "result_rule_run", "config_result_stale_state", "schema_info",
        "staging_calendar_raw_day", "target_account_mapping", "staging_account_mapping_raw_row",
        "import_batch", "import_batch_source", "target_authorized_preparer", "staging_authorized_preparer_raw_row",
        "config_account_taxonomy", "config_account_taxonomy_path"
    ];

    public static TheoryData<string, string> Cases()
    {
        var result = new TheoryData<string, string>();
        foreach (var provider in new[] { "sqlite", "duckdb" })
        foreach (var action in new[] { "calendar.setNonWorkingDays", "import.makeupDay.fromFile", "accountMapping.save", "import.authorizedPreparer.clear", "accountTaxonomy.save" })
            result.Add(provider, action);
        return result;
    }

    // X-03: jet-guide section 6 requires the whole database mutation to roll back,
    // including definitions and hits. The fault follows scenario deletion and database source
    // writes. Weekly-day settings live in project.json and must not be saved after this failure.
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task DatabaseWriteFailure_RollsBackSourcesDefinitionsHitsAndStaleFlags(string provider, string action)
    {
        using var host = new HandlerTestHost();
        var prepared = await Batch9ArtifactStateTests.PrepareAsync(host, provider);
        await Batch9MutationStateTests.MutateAsync(host, prepared.Id, "import.accountMapping.fromFile");
        await Batch9MutationStateTests.MutateAsync(host, prepared.Id, "import.authorizedPreparer.fromFile");
        await host.DispatchAsync("import.makeupDay", """{"dates":["2025-02-08"]}""");
        await host.DispatchAsync("prescreen.run");
        await host.DispatchAsync("filter.commit", Scenario);
        var before = await LoadAsync(host, prepared.Id);
        Assert.Single(before.GetProperty("filterScenarios").EnumerateArray());
        Assert.True(await Batch9TransactionBoundaryTests.ScalarAsync(host, prepared.Id, provider, "SELECT COUNT(*) FROM result_filter_run;") > 0);
        var tables = new Dictionary<string, string[]>();
        foreach (var table in SnapshotTables) tables[table] = await ReadTableAsync(host, prepared.Id, provider, table);
        var projectFile = Path.Combine(host.ProjectsRoot, prepared.Id, "project.json");
        var projectBytes = await File.ReadAllBytesAsync(projectFile);
        var revision = await Batch9TransactionBoundaryTests.ScalarAsync(host, prepared.Id, provider,
            "SELECT CAST(value AS BIGINT) FROM schema_info WHERE key='filter_data_revision';");
        await Batch9ArtifactStateTests.ExecuteAsync(host, prepared.Id, provider, $$"""
            ALTER TABLE schema_info RENAME TO synthetic_saved_schema_info;
            CREATE TABLE schema_info (key TEXT PRIMARY KEY, value TEXT NOT NULL,
                CONSTRAINT synthetic_revision_failure CHECK (key <> 'filter_data_revision' OR value = '{{revision}}'));
            INSERT INTO schema_info SELECT * FROM synthetic_saved_schema_info;
            """);

        var taxonomy = before.GetProperty("taxonomy");
        var categories = JsonNode.Parse(taxonomy.GetProperty("categories").GetRawText())!.AsArray();
        categories[0]!["label"] = "合成改名";
        var error = await Record.ExceptionAsync(() => action switch
        {
            "accountMapping.save" => host.DispatchAsync(action, """{"changes":[{"accountCode":"1000","categoryId":"builtin.others"}]}"""),
            "accountTaxonomy.save" => host.DispatchAsync(action, JsonSerializer.Serialize(new
                { revision = taxonomy.GetProperty("revision").GetInt32(), categories })),
            _ => Batch9MutationStateTests.MutateAsync(host, prepared.Id, action)
        });
        Assert.NotNull(error);
        Assert.Contains("CHECK", error.ToString(), StringComparison.OrdinalIgnoreCase);
        foreach (var table in SnapshotTables)
            Assert.Equal(tables[table], await ReadTableAsync(host, prepared.Id, provider, table));
        Assert.Equal(projectBytes, await File.ReadAllBytesAsync(projectFile));

        // The transaction assertions are complete. Remove the test-only fault before opening
        // the project, whose idempotent schema bootstrap also inserts the default revision.
        await Batch9ArtifactStateTests.ExecuteAsync(host, prepared.Id, provider, """
            DROP TABLE schema_info;
            ALTER TABLE synthetic_saved_schema_info RENAME TO schema_info;
            """);

        var after = await LoadAsync(host, prepared.Id);
        foreach (var key in new[] { "filterScenarios", "staleState", "importState", "taxonomy", "mapping" })
            Assert.Equal(before.GetProperty(key).GetRawText(), after.GetProperty(key).GetRawText());
    }

    internal static async Task<string[]> ReadTableAsync(HandlerTestHost host, string projectId, string provider, string table)
    {
        var folder = new JetProjectFolder(host.ProjectsRoot);
        ILocalProjectDatabase database = provider == "duckdb" ? new DuckDbProjectDatabase(folder) : new SqliteProjectDatabase(folder);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        // All callers pass fixed test-owned identifiers, never payload data.
        command.CommandText = $"SELECT * FROM {table};";
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount)
                .Select(index => reader.IsDBNull(index) ? null : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture)).ToArray()));
        return rows.Order(StringComparer.Ordinal).ToArray();
    }

    private static Task<JsonElement> LoadAsync(HandlerTestHost host, string projectId) =>
        host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
}
