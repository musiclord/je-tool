using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class UsageScenarioJourneyTests
{
    private const string Scenario = """{"name":"合成借方","rationale":"固定答案","groups":[{"rules":[{"type":"drCrOnly","drCr":"debit"}]}]}""";
    public static TheoryData<string, string> ClearingCases() => UpstreamMutationClearsFilterScenariosTests.ClearingCases();
    public static IEnumerable<object[]> FailureCases() =>
        from provider in new[] { "sqlite", "duckdb" }
        from failure in new[] { "gl-read", "gl-mapping", "holiday", "authorized", "taxonomy-conflict", "cancel" }
        select new object[] { provider, failure };

    [Theory]
    [MemberData(nameof(ClearingCases))]
    public async Task ClearingUpstreamAction_MarksBothReportsStaleWithoutChangingTheirFiles(string provider, string action)
    {
        using var host = new HandlerTestHost();
        var prepared = await Batch9ArtifactStateTests.PrepareAsync(host, provider);
        if (action == "accountMapping.save")
        {
            var file = Path.Combine(host.ProjectsRoot, "synthetic-mapping.csv");
            await File.WriteAllTextAsync(file, "科目代號,科目名稱,標準化分類\n1000,合成資產,Cash\n");
            await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new { filePath = file }));
            await host.DispatchAsync("prescreen.run");
            var saved = await host.DispatchAsync("filter.commit", "{\"scenarios\":[" + Scenario + "]}");
            prepared = prepared with { Revision = saved.GetProperty("resultRef").GetProperty("revision").GetString()! };
        }
        var reports = await ExportBothAsync(host, prepared);
        var response = action == "accountMapping.save"
            ? await host.DispatchAsync(action, """{"changes":[{"accountCode":"1000","categoryId":"builtin.others"}]}""")
            : await Batch9MutationStateTests.MutateAsync(host, prepared.Id, action);
        Assert.True(response.GetProperty("invalidatedResults").GetProperty("filterScenarios").GetBoolean());
        AssertReports(reports, response);
        var reopened = await LoadAsync(host, prepared.Id);
        Assert.Empty(reopened.GetProperty("filterScenarios").EnumerateArray());
        AssertReports(reports, reopened);
    }

    [Theory]
    [MemberData(nameof(FailureCases))]
    public async Task FailedOrCancelledUpstreamChange_PreservesDefinitionsHitsAndSource(string provider, string failure)
    {
        var events = new CancelImportEvents();
        using var host = new HandlerTestHost(eventPublisher: events);
        var prepared = await Batch9ArtifactStateTests.PrepareAsync(host, provider);
        var before = await LoadAsync(host, prepared.Id);
        var hits = await CountHitsAsync(host, prepared.Id, provider);
        Assert.True(hits > 0);
        var file = Path.Combine(host.ProjectsRoot, "synthetic-input.csv");
        if (failure is "gl-read" or "cancel")
        {
            var bytes = Encoding.UTF8.GetBytes("Code,Amount\n" + string.Concat(Enumerable.Repeat("1000,10\n", 21000)));
            await File.WriteAllBytesAsync(file, failure == "gl-read" ? bytes.Concat(new byte[] { 0xff, 0xff }).ToArray() : bytes);
        }
        string action = "", payload = "", code = "";
        switch (failure)
        {
            case "gl-read":
                action = "import.gl.fromFile"; payload = JsonSerializer.Serialize(new { filePath = file, encoding = "utf-8", delimiter = "," }); code = "file_read_error"; break;
            case "gl-mapping":
                var mapping = JsonNode.Parse(before.GetProperty("mapping").GetProperty("gl").GetRawText())!;
                mapping["mapping"]!["amount"] = "摘要";
                action = "mapping.commit.gl"; payload = mapping.ToJsonString(); code = "projection_failed"; break;
            case "holiday":
                file = TestWorkbookBuilder.WriteWorkbook(sheet =>
                {
                    sheet.Cell(1, 1).Value = "Synthetic calendar";
                    sheet.Cell(2, 1).Value = "Date_of_Holiday"; sheet.Cell(2, 2).Value = "Holiday_Name"; sheet.Cell(2, 3).Value = "IS_Holiday";
                    sheet.Cell(3, 1).Value = "2025/01/01"; sheet.Cell(3, 2).Value = "合成假日"; sheet.Cell(3, 3).Value = "Y";
                });
                action = "import.holiday.fromFile"; payload = JsonSerializer.Serialize(new { filePath = file }); code = "projection_failed"; break;
            case "authorized":
                file = TestWorkbookBuilder.WriteWorkbook(sheet =>
                { sheet.Cell(1, 1).Value = "Personnel"; sheet.Cell(1, 2).Value = "Other";
                  sheet.Cell(2, 1).Value = "   "; sheet.Cell(2, 2).Value = "synthetic"; });
                action = "import.authorizedPreparer.fromFile"; payload = JsonSerializer.Serialize(new { filePath = file, sourceColumn = "Personnel" }); code = "invalid_payload"; break;
            case "taxonomy-conflict":
                var taxonomy = before.GetProperty("taxonomy");
                action = "accountTaxonomy.save"; payload = JsonSerializer.Serialize(new { revision = taxonomy.GetProperty("revision").GetInt32() + 1, categories = taxonomy.GetProperty("categories") });
                code = JetErrorCodes.TaxonomyRevisionConflict; break;
        }
        try
        {
            if (failure == "cancel")
            {
                using var request = host.Dispatcher.CancellationRegistry.Begin("synthetic-import");
                events.Cancel = () => host.DispatchAsync("operation.cancel", """{"requestId":"synthetic-import"}""").GetAwaiter().GetResult();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.DispatchAsync("import.gl.fromFile",
                    JsonSerializer.Serialize(new { filePath = file, encoding = "utf-8", delimiter = "," }), request.Token));
                Assert.True(events.Requested); Assert.True(request.Token.IsCancellationRequested);
            }
            else
            {
                var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(action, payload));
                Assert.Equal(code, error.Code);
            }
        }
        finally { if (failure is "holiday" or "authorized") TestWorkbookBuilder.Delete(file); }
        var after = await LoadAsync(host, prepared.Id);
        Assert.Equal(before.GetProperty("filterScenarios").GetRawText(), after.GetProperty("filterScenarios").GetRawText());
        Assert.Equal(hits, await CountHitsAsync(host, prepared.Id, provider));
        Assert.Equal(before.GetProperty("importState").GetProperty("gl").GetRawText(), after.GetProperty("importState").GetProperty("gl").GetRawText());
        Assert.Equal(before.GetProperty("mapping").GetRawText(), after.GetProperty("mapping").GetRawText());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task TrialBalanceReplacement_ClearsScenariosThenRequiresResavingBeforeAnotherExport(string provider)
    {
        using var host = new HandlerTestHost();
        var prepared = await Batch9ArtifactStateTests.PrepareAsync(host, provider);
        var reports = await ExportBothAsync(host, prepared);
        var file = new InlineTbWorkbookBuilder().AddRow("1000", "合成資產", 10m).AddRow("4000", "合成收入", -10m).WriteWorkbook();
        try
        {
            var imported = await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new { filePath = file }));
            Assert.True(imported.GetProperty("invalidatedResults").GetProperty("filterScenarios").GetBoolean());
            var mapped = await host.DispatchAsync("mapping.commit.tb", JsonSerializer.Serialize(new { mapping = InlineTbWorkbookBuilder.BuildDirectModeMapping(), changeMode = "direct" }));
            Assert.True(mapped.GetProperty("invalidatedResults").GetProperty("filterScenarios").GetBoolean());
            var after = await LoadAsync(host, prepared.Id);
            Assert.Empty(after.GetProperty("filterScenarios").EnumerateArray());
            Assert.Equal(0, await CountHitsAsync(host, prepared.Id, provider));
            var error = await Assert.ThrowsAsync<JetActionException>(() => ExportPaperAsync(host, prepared));
            Assert.Equal("completeness_prerequisite_failed", error.Code);
            AssertReports(reports, after);
            var validation = await host.DispatchAsync("validate.run");
            prepared = prepared with { ValidationRunId = validation.GetProperty("resultRef").GetProperty("runId").GetString()! };
            var noScenario = await Assert.ThrowsAsync<JetActionException>(() => ExportPaperAsync(host, prepared));
            Assert.Equal("stale_result", noScenario.Code);
            Assert.Contains("目前沒有已儲存的篩選情境。", noScenario.Message, StringComparison.Ordinal);
            var saved = await host.DispatchAsync("filter.commit", "{\"scenarios\":[" + Scenario + "]}");
            prepared = prepared with { Revision = saved.GetProperty("resultRef").GetProperty("revision").GetString()! };
            var exported = await ExportPaperAsync(host, prepared);
            Assert.False(exported.GetProperty("artifact").GetProperty("stale").GetBoolean());
            AssertReports(reports, exported);
        }
        finally { TestWorkbookBuilder.Delete(file); }
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ScenarioNames_TrimWhitespaceButRemainOrdinalAndDoNotDeduplicateIdenticalConditions(string provider)
    {
        using var host = new HandlerTestHost();
        var prepared = await Batch9ArtifactStateTests.PrepareAsync(host, provider);
        string Payload(string second) => JsonSerializer.Serialize(new { scenarios = new[] { JsonNode.Parse(Scenario)!, JsonNode.Parse(Scenario)! }
            .Select((scenario, index) => { scenario["name"] = index == 0 ? "G" : second; return scenario; }) });
        var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("filter.commit", Payload(" G ")));
        Assert.Equal("invalid_scenario", error.Code); Assert.Contains("情境名稱重複：「G」。", error.Message, StringComparison.Ordinal);
        await host.DispatchAsync("filter.commit", Payload("g"));
        var matrix = await host.DispatchAsync("query.tagMatrixScenarios");
        Assert.Equal(new[] { "G", "g" }, matrix.GetProperty("scenarios").EnumerateArray().Select(x => x.GetProperty("name").GetString()));
        Assert.All(matrix.GetProperty("scenarios").EnumerateArray(), row => { Assert.Equal(1, row.GetProperty("rowHitCount").GetInt32()); Assert.Equal(1, row.GetProperty("voucherHitCount").GetInt32()); });
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task NoSavedScenario_RejectsWorkpaperBeforeWritingAnyFile(string provider)
    {
        using var host = new HandlerTestHost();
        var prepared = await Batch9ArtifactStateTests.PrepareAsync(host, provider);
        await host.DispatchAsync("filter.commit", """{"scenarios":[]}""");
        var error = await Assert.ThrowsAsync<JetActionException>(() => ExportPaperAsync(host, prepared));
        Assert.Equal("stale_result", error.Code); Assert.Contains("目前沒有已儲存的篩選情境。", error.Message, StringComparison.Ordinal);
        Assert.Empty(WorkpaperExportTestSupport.FindWorkpapers(host.ProjectsRoot));
    }

    private static Task<JsonElement> LoadAsync(HandlerTestHost host, string id) => host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = id }));
    private static Task<JsonElement> ExportPaperAsync(HandlerTestHost host, Batch9ArtifactStateTests.Prepared p) => host.DispatchAsync("export.workpaperStream",
        JsonSerializer.Serialize(new { validationRunId = p.ValidationRunId, scenarioRevision = p.Revision, scenarioPositions = new[] { 1 } }));
    private sealed record Exported(string Id, string Path, byte[] Bytes);
    private static async Task<Exported[]> ExportBothAsync(HandlerTestHost host, Batch9ArtifactStateTests.Prepared p)
    {
        var report = await host.DispatchAsync("export.criteriaSelectionReport", JsonSerializer.Serialize(new { validationRunId = p.ValidationRunId, revision = p.Revision }));
        var paper = await ExportPaperAsync(host, p);
        return new[] { report, paper }.Select(response =>
        {
            var artifact = response.GetProperty("artifact"); var path = artifact.GetProperty("fullPath").GetString()!;
            Assert.InRange(new FileInfo(path).Length, 1, 10_000_000);
            return new Exported(artifact.GetProperty("artifactId").GetString()!, path, File.ReadAllBytes(path));
        }).ToArray();
    }
    private static void AssertReports(Exported[] reports, JsonElement response)
    {
        foreach (var report in reports)
        {
            var entry = response.GetProperty("reportArtifacts").EnumerateArray().Single(x => x.GetProperty("artifactId").GetString() == report.Id);
            Assert.True(entry.GetProperty("stale").GetBoolean());
            Assert.Equal(report.Bytes, File.ReadAllBytes(report.Path));
        }
    }
    private static async Task<long> CountHitsAsync(HandlerTestHost host, string id, string provider)
    {
        var folder = new JetProjectFolder(host.ProjectsRoot);
        ILocalProjectDatabase database = provider == "duckdb" ? new DuckDbProjectDatabase(folder) : new SqliteProjectDatabase(folder);
        await using var connection = database.CreateConnection(id); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM result_filter_run;";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
    private sealed class CancelImportEvents : IJetEventPublisher
    {
        public Func<JsonElement>? Cancel { get; set; }
        public bool Requested { get; private set; }
        public void Publish(string eventName, object payload)
        {
            if (eventName != "import.progress" || Cancel is null || Requested) return;
            Requested = Cancel().GetProperty("requested").GetBoolean();
        }
    }
}
