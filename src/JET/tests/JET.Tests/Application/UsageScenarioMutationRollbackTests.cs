using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class UsageScenarioMutationRollbackTests
{
    private const string Scenario = """{"scenarios":[{"name":"合成借方","rationale":"固定答案","groups":[{"conditions":[{"type":"drCrOnly","drCr":"debit"}]}]}]}""";
    private static readonly string[] SnapshotTables =
    [
        "filter_scenario", "filter_hit", "validation_result", "prescreen_result", "result_stale_state", "schema_info",
        "calendar_day", "account_mapping", "pbc_account_mapping",
        "import_batch", "import_batch_source", "authorized_preparer", "pbc_authorized_preparer",
        "account_category", "account_category_path"
    ];

    public static TheoryData<string, string> Cases()
    {
        var result = new TheoryData<string, string>();
        foreach (var provider in new[] { "sqlite" })
        foreach (var action in new[] { "calendar.setNonWorkingDays", "import.makeupDay.fromFile", "accountMapping.save", "import.authorizedPreparer.clear", "accountCategory.save" })
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
        var prepared = await ReportRevisionStateTests.PrepareAsync(host, provider);
        await MutationInvalidationStateTests.MutateAsync(host, prepared.Id, "import.accountMapping.fromFile");
        await MutationInvalidationStateTests.MutateAsync(host, prepared.Id, "import.authorizedPreparer.fromFile");
        await host.DispatchAsync("import.makeupDay", """{"dates":["2025-02-08"]}""");
        await host.DispatchAsync("prescreen.run");
        await host.DispatchAsync("filter.commit", Scenario);
        var before = await LoadAsync(host, prepared.Id);
        Assert.Single(before.GetProperty("filterScenarios").EnumerateArray());
        Assert.True(await WriteFailureRollbackTests.ScalarAsync(host, prepared.Id, provider, "SELECT COUNT(*) FROM filter_hit;") > 0);
        var tables = new Dictionary<string, string[]>();
        foreach (var table in SnapshotTables) tables[table] = await ReadTableAsync(host, prepared.Id, provider, table);
        var projectFile = Path.Combine(host.ProjectsRoot, prepared.Id, "project.json");
        var projectBytes = await File.ReadAllBytesAsync(projectFile);
        var revision = await WriteFailureRollbackTests.ScalarAsync(host, prepared.Id, provider,
            "SELECT CAST(value AS BIGINT) FROM schema_info WHERE key='filter_data_revision';");
        await ReportRevisionStateTests.ExecuteAsync(host, prepared.Id, provider, $$"""
            ALTER TABLE schema_info RENAME TO synthetic_saved_schema_info;
            CREATE TABLE schema_info (key TEXT PRIMARY KEY, value TEXT NOT NULL,
                CONSTRAINT synthetic_revision_failure CHECK (key <> 'filter_data_revision' OR value = '{{revision}}'));
            INSERT INTO schema_info SELECT * FROM synthetic_saved_schema_info;
            """);

        var categorySnapshot = before.GetProperty("accountCategory");
        var categories = JsonNode.Parse(categorySnapshot.GetProperty("categories").GetRawText())!.AsArray();
        categories[0]!["label"] = "合成改名";
        var error = await Record.ExceptionAsync(() => action switch
        {
            "accountMapping.save" => host.DispatchAsync(action, """{"changes":[{"accountNumber":"1000","categoryId":"builtin.others"}]}"""),
            "accountCategory.save" => host.DispatchAsync(action, JsonSerializer.Serialize(new
                { revision = categorySnapshot.GetProperty("revision").GetInt32(), categories })),
            _ => MutationInvalidationStateTests.MutateAsync(host, prepared.Id, action)
        });
        Assert.NotNull(error);
        Assert.Contains("CHECK", error.ToString(), StringComparison.OrdinalIgnoreCase);
        foreach (var table in SnapshotTables)
            Assert.Equal(tables[table], await ReadTableAsync(host, prepared.Id, provider, table));
        Assert.Equal(projectBytes, await File.ReadAllBytesAsync(projectFile));

        // The transaction assertions are complete. Remove the test-only fault before opening
        // the project, whose idempotent schema bootstrap also inserts the default revision.
        await ReportRevisionStateTests.ExecuteAsync(host, prepared.Id, provider, """
            DROP TABLE schema_info;
            ALTER TABLE synthetic_saved_schema_info RENAME TO schema_info;
            """);

        var after = await LoadAsync(host, prepared.Id);
        foreach (var key in new[] { "filterScenarios", "staleState", "importState", "accountCategory", "mapping" })
            Assert.Equal(before.GetProperty(key).GetRawText(), after.GetProperty(key).GetRawText());
    }

    internal static async Task<string[]> ReadTableAsync(HandlerTestHost host, string projectId, string provider, string table)
    {
        var folder = new JetProjectFolder(host.ProjectsRoot);
        ILocalProjectDatabase database = new SqliteProjectDatabase(folder);
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
