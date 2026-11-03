using System.Text.Json;
using ClosedXML.Excel;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class SelectedScenarioWorkingPaperTests
{
    // 第四步照舊版拿掉編製分錄較少的人員與使用較少的科目（裁定 2026-10-29-27）：第二個情境原本用預篩選條件 rarePreparer，改成第五步「依審計目的」的「編製分錄較少的人員」同一個條件，
    // 一樣需要傳票建立人員；第一次失敗：Focused 收據 20261030-163342206-c1b391e5be2e46bfa36b85fdbe6b59ad。
    private const string Scenarios = """
        {"scenarios":[
          {"name":"借方分錄","rationale":"合成選取範圍","groups":[{"conditions":[{"type":"drCrOnly","drCr":"debit"}]}]},
          {"name":"編製筆數","rationale":"合成未選範圍","groups":[{"conditions":[{"type":"entityFrequency","field":"createdBy","countUnit":"entries","countOperator":"lessThanOrEqual","countFrom":"11"}]}]}
        ]}
        """;

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task WorkingPaper_RemapClearsScenarios_ThenOnlyResavedValidScenarioExports(string provider)
    {
        // 上游修改會清除下游（裁定 2026-10-07-03）：重新配對會清掉全部已存情境，不再出現「保留下來但缺來源的未選情境」。
        // 原本驗證未選的缺欄情境不擋所選底稿；改成驗證重新配對後情境清空、缺欄情境無法重新儲存、
        // 只重新儲存有效情境即可匯出，且之後再改資料，已產出的底稿仍標示過期。
        // 第一次失敗收據 20261007-032952783-7147565fec1c42be845dac22acf7263b。
        using var host = new HandlerTestHost();
        var setup = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await host.DispatchAsync("filter.commit", Scenarios);
        var mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(
            setup.Demo.GetProperty("gl").GetProperty("mapping").GetRawText())!;
        Assert.True(mapping.Remove(GlMappingKeys.CreatedBy));
        var remapped = await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping,
            amountMode = setup.Demo.GetProperty("gl").GetProperty("amountMode").GetString()
        }));
        Assert.True(remapped.GetProperty("invalidatedResults").GetProperty("filterScenarios").GetBoolean());
        var validation = await host.DispatchAsync("validate.run");
        var validationResultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString();
        var cleared = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = setup.ProjectId }));
        Assert.Empty(cleared.GetProperty("filterScenarios").EnumerateArray());

        var invalid = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("filter.commit", Scenarios));
        Assert.Equal(JetErrorCodes.InvalidScenario, invalid.Code);
        Assert.Empty(Directory.GetFiles(Path.Combine(host.ProjectsRoot, setup.ProjectId), "*WorkingPaper*.xlsx"));

        var saved = await host.DispatchAsync("filter.commit", """
            {"scenarios":[{"name":"借方分錄","rationale":"合成選取範圍","groups":[{"conditions":[{"type":"drCrOnly","drCr":"debit"}]}]}]}
            """);
        var revision = saved.GetProperty("resultRef").GetProperty("revision").GetString();
        var response = await host.DispatchAsync("export.workingPaperStream", JsonSerializer.Serialize(new
        {
            validationResultId, scenarioRevision = revision, scenarioPositions = new[] { 1 }
        }));
        var report = response.GetProperty("publishedReport");
        Assert.True(response.GetProperty("filterResultsCurrent").GetBoolean());
        Assert.False(report.GetProperty("stale").GetBoolean());
        Assert.Equal(new[] { 1 }, report.GetProperty("sourceRef").GetProperty("scenarioPositions")
            .EnumerateArray().Select(item => item.GetInt32()));
        Assert.False(string.IsNullOrWhiteSpace(report.GetProperty("sourceRef").GetProperty("filterDataRevision").GetString()));
        using (var book = new XLWorkbook(Path.Combine(host.ProjectsRoot, setup.ProjectId,
            report.GetProperty("fileName").GetString()!)))
        {
            Assert.True(book.Worksheet(WorkingPaperSheetCatalog.Step41).RowsUsed().Count() > 1);
        }

        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = setup.ProjectId }));
        Assert.False(loaded.GetProperty("staleState").GetProperty("filter").GetBoolean());
        Assert.Equal(new[] { "借方分錄" }, loaded.GetProperty("filterScenarios")
            .EnumerateArray().Select(item => item.GetProperty("name").GetString()));
        Assert.False(Assert.Single(loaded.GetProperty("reports").EnumerateArray())
            .GetProperty("stale").GetBoolean());
        Assert.True(await ScalarAsync(host, setup.ProjectId, provider,
            "SELECT COUNT(*) FROM filter_hit WHERE scenario_id=1;") > 0);

        // 之後真的變更資料，已產出的所選底稿仍必須標示過期。
        await host.DispatchAsync("calendar.setNonWorkingDays", """{"days":[6]}""");
        var changed = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = setup.ProjectId }));
        Assert.True(Assert.Single(changed.GetProperty("reports").EnumerateArray())
            .GetProperty("stale").GetBoolean());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task WorkingPaper_SelectedRefreshPreservesCurrentUnselectedHits(string provider)
    {
        using var host = new HandlerTestHost();
        var setup = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        var saved = await host.DispatchAsync("filter.commit", Scenarios);
        var unselectedHits = await ScalarAsync(host, setup.ProjectId, provider,
            "SELECT COUNT(*) FROM filter_hit WHERE scenario_id=2;");
        Assert.True(unselectedHits > 0);
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = setup.ProjectId }));
        await host.DispatchAsync("export.workingPaperStream", JsonSerializer.Serialize(new
        {
            validationResultId = loaded.GetProperty("validationResult")
                .GetProperty("resultRef").GetProperty("resultId").GetString(),
            scenarioRevision = saved.GetProperty("resultRef").GetProperty("revision").GetString(),
            scenarioPositions = new[] { 1 }
        }));
        Assert.Equal(unselectedHits, await ScalarAsync(host, setup.ProjectId, provider,
            "SELECT COUNT(*) FROM filter_hit WHERE scenario_id=2;"));
        Assert.Equal(0, await ScalarAsync(host, setup.ProjectId, provider,
            "SELECT filter_stale FROM result_stale_state WHERE singleton=1;"));
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
