using System.Text.Json;
using ClosedXML.Excel;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class SelectedScenarioWorkpaperTests
{
    private const string Scenarios = """
        {"scenarios":[
          {"name":"借方分錄","rationale":"合成選取範圍","groups":[{"rules":[{"type":"drCrOnly","drCr":"debit"}]}]},
          {"name":"編製筆數","rationale":"合成未選範圍","groups":[{"rules":[{"type":"prescreen","prescreenKey":"lowFrequencyPreparer"}]}]}
        ]}
        """;

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Workpaper_RemapClearsScenarios_ThenOnlyResavedValidScenarioExports(string provider)
    {
        // 使用者 2026-10-07 裁定上游修改清除下游：重新配對會清掉全部已存情境，不再出現「保留下來但缺來源的未選情境」。
        // 原本驗證未選的缺欄情境不擋所選底稿；改成驗證重新配對後情境清空、缺欄情境無法重新儲存、
        // 只重新儲存有效情境即可匯出，且之後再改資料，已產出的底稿仍標示過期。
        // 第一次失敗收據 20261007-032952783-7147565fec1c42be845dac22acf7263b。
        using var host = new HandlerTestHost();
        var setup = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await host.DispatchAsync("filter.commit", Scenarios);
        var mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(
            setup.Demo.GetProperty("gl").GetProperty("mapping").GetRawText())!;
        Assert.True(mapping.Remove(GlMappingKeys.CreateBy));
        var remapped = await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping,
            amountMode = setup.Demo.GetProperty("gl").GetProperty("amountMode").GetString()
        }));
        Assert.True(remapped.GetProperty("invalidatedResults").GetProperty("filterScenarios").GetBoolean());
        var validation = await host.DispatchAsync("validate.run");
        var validationRunId = validation.GetProperty("resultRef").GetProperty("runId").GetString();
        var cleared = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = setup.ProjectId }));
        Assert.Empty(cleared.GetProperty("filterScenarios").EnumerateArray());

        var invalid = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("filter.commit", Scenarios));
        Assert.Equal(JetErrorCodes.InvalidScenario, invalid.Code);
        Assert.Empty(Directory.GetFiles(Path.Combine(host.ProjectsRoot, setup.ProjectId), "*WorkingPaper*.xlsx"));

        var saved = await host.DispatchAsync("filter.commit", """
            {"scenarios":[{"name":"借方分錄","rationale":"合成選取範圍","groups":[{"rules":[{"type":"drCrOnly","drCr":"debit"}]}]}]}
            """);
        var revision = saved.GetProperty("resultRef").GetProperty("revision").GetString();
        var response = await host.DispatchAsync("export.workpaperStream", JsonSerializer.Serialize(new
        {
            validationRunId, scenarioRevision = revision, scenarioPositions = new[] { 1 }
        }));
        var artifact = response.GetProperty("artifact");
        Assert.True(response.GetProperty("filterResultsCurrent").GetBoolean());
        Assert.False(artifact.GetProperty("stale").GetBoolean());
        Assert.Equal(new[] { 1 }, artifact.GetProperty("sourceRef").GetProperty("scenarioPositions")
            .EnumerateArray().Select(item => item.GetInt32()));
        Assert.False(string.IsNullOrWhiteSpace(artifact.GetProperty("sourceRef").GetProperty("filterDataRevision").GetString()));
        using (var book = new XLWorkbook(Path.Combine(host.ProjectsRoot, setup.ProjectId,
            artifact.GetProperty("fileName").GetString()!)))
        {
            Assert.True(book.Worksheet(WorkpaperSheetCatalog.Step41).RowsUsed().Count() > 1);
        }

        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = setup.ProjectId }));
        Assert.False(loaded.GetProperty("staleState").GetProperty("filter").GetBoolean());
        Assert.Equal(new[] { "借方分錄" }, loaded.GetProperty("filterScenarios")
            .EnumerateArray().Select(item => item.GetProperty("name").GetString()));
        Assert.False(Assert.Single(loaded.GetProperty("reportArtifacts").EnumerateArray())
            .GetProperty("stale").GetBoolean());
        Assert.True(await ScalarAsync(host, setup.ProjectId, provider,
            "SELECT COUNT(*) FROM result_filter_run WHERE scenario_position=1;") > 0);

        // 之後真的變更資料，已產出的所選底稿仍必須標示過期。
        await host.DispatchAsync("calendar.setNonWorkingDays", """{"days":[6]}""");
        var changed = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = setup.ProjectId }));
        Assert.True(Assert.Single(changed.GetProperty("reportArtifacts").EnumerateArray())
            .GetProperty("stale").GetBoolean());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Workpaper_SelectedRefreshPreservesCurrentUnselectedHits(string provider)
    {
        using var host = new HandlerTestHost();
        var setup = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        var saved = await host.DispatchAsync("filter.commit", Scenarios);
        var unselectedHits = await ScalarAsync(host, setup.ProjectId, provider,
            "SELECT COUNT(*) FROM result_filter_run WHERE scenario_position=2;");
        Assert.True(unselectedHits > 0);
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = setup.ProjectId }));
        await host.DispatchAsync("export.workpaperStream", JsonSerializer.Serialize(new
        {
            validationRunId = loaded.GetProperty("latestRuns").GetProperty("validate")
                .GetProperty("resultRef").GetProperty("runId").GetString(),
            scenarioRevision = saved.GetProperty("resultRef").GetProperty("revision").GetString(),
            scenarioPositions = new[] { 1 }
        }));
        Assert.Equal(unselectedHits, await ScalarAsync(host, setup.ProjectId, provider,
            "SELECT COUNT(*) FROM result_filter_run WHERE scenario_position=2;"));
        Assert.Equal(0, await ScalarAsync(host, setup.ProjectId, provider,
            "SELECT filter_stale FROM config_result_stale_state WHERE singleton=1;"));
    }

    private static async Task<long> ScalarAsync(HandlerTestHost host, string projectId, string provider, string sql)
    {
        var folder = new JetProjectFolder(host.ProjectsRoot);
        ILocalProjectDatabase database = provider == "sqlite"
            ? new SqliteProjectDatabase(folder) : new DuckDbProjectDatabase(folder);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
