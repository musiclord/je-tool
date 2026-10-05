using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class Batch9MutationStateTests
{
    public static TheoryData<string, string, bool, bool, bool> Cases()
    {
        var data = new TheoryData<string, string, bool, bool, bool>();
        foreach (var provider in new[] { "sqlite", "duckdb" })
        {
            data.Add(provider, "import.gl.fromFile", true, true, true);
            data.Add(provider, "import.tb.fromFile", true, false, false);
            data.Add(provider, "mapping.commit.gl", true, true, true);
            data.Add(provider, "mapping.commit.tb", true, false, false);
            foreach (var action in new[]
            {
                "import.accountMapping.fromFile", "accountMapping.save", "accountTaxonomy.save",
                "import.authorizedPreparer.fromFile", "import.authorizedPreparer.clear",
                "import.holiday", "import.makeupDay", "import.holiday.fromFile", "import.makeupDay.fromFile",
                "calendar.setNonWorkingDays", "project.update.date"
            }) data.Add(provider, action, false, true, true);
            data.Add(provider, "calendar.sameNonWorkingDays", false, false, false);
            data.Add(provider, "project.update.metadata", false, false, false);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task MutationReplies_PublishExplicitImpactPersistedStateAndCatalog(string provider, string action,
        bool validation, bool prescreen, bool filter)
    {
        using var host = new HandlerTestHost();
        var prepared = await Batch9ArtifactStateTests.PrepareAsync(host, provider);
        var response = await MutateAsync(host, prepared.Id, action);
        AssertFlags(response.GetProperty("invalidatedResults"), validation, prescreen, filter);
        AssertFlags(response.GetProperty("staleState"), validation, prescreen, filter);
        Assert.Equal(JsonValueKind.Array, response.GetProperty("reportArtifacts").ValueKind);
        Assert.Empty(response.GetProperty("reportArtifacts").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("reportArtifactWarning").ValueKind);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task MutationWithoutPriorRuns_StillInvalidatesAnUnsavedPreviewButDoesNotInventStaleResults(string provider)
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            caseName = "Batch9-Empty", periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider
        }));
        var response = await host.DispatchAsync("import.holiday", """{"dates":["2025-01-01"]}""");
        AssertFlags(response.GetProperty("invalidatedResults"), false, true, true);
        AssertFlags(response.GetProperty("staleState"), false, false, false);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task LockedCatalog_DoesNotTurnACommittedMutationIntoFailure(string provider)
    {
        using var host = new HandlerTestHost();
        var prepared = await Batch9ArtifactStateTests.PrepareAsync(host, provider);
        var report = await host.DispatchAsync("export.criteriaSelectionReport", JsonSerializer.Serialize(new
        { validationRunId = prepared.ValidationRunId, revision = prepared.Revision }));
        var reportPath = report.GetProperty("artifact").GetProperty("fullPath").GetString()!;
        var manifest = Path.Combine(Path.GetDirectoryName(reportPath)!, ProjectReportArtifactStore.ManifestFileName);
        using (var locked = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var stopwatch = Stopwatch.StartNew();
            var response = await host.DispatchAsync("import.holiday", """{"dates":["2025-06-01"]}""")
                .WaitAsync(TimeSpan.FromSeconds(6));
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
            Assert.Equal(1, response.GetProperty("count").GetInt32());
            AssertFlags(response.GetProperty("invalidatedResults"), false, true, true);
            AssertFlags(response.GetProperty("staleState"), false, true, true);
            Assert.Equal(JsonValueKind.Null, response.GetProperty("reportArtifacts").ValueKind);
            Assert.Equal("變更已儲存，報告清單暫時無法更新。請稍後重新開啟案件查看，無須重做變更。",
                response.GetProperty("reportArtifactWarning").GetString());
        }
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = prepared.Id }));
        Assert.Equal(1, loaded.GetProperty("importState").GetProperty("calendar").GetProperty("holidayCount").GetInt32());
        AssertFlags(loaded.GetProperty("staleState"), false, true, true);
    }

    private static async Task<JsonElement> MutateAsync(HandlerTestHost host, string id, string action)
    {
        if (action.StartsWith("mapping.commit.", StringComparison.Ordinal))
        {
            var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = id }));
            return await host.DispatchAsync(action, loaded.GetProperty("mapping").GetProperty(action.EndsWith("gl", StringComparison.Ordinal) ? "gl" : "tb").GetRawText());
        }
        if (action == "accountTaxonomy.save")
        {
            var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = id }));
            var taxonomy = loaded.GetProperty("taxonomy");
            var categories = JsonNode.Parse(taxonomy.GetProperty("categories").GetRawText())!.AsArray();
            categories[0]!["label"] = "合成改名";
            return await host.DispatchAsync(action, JsonSerializer.Serialize(new { revision = taxonomy.GetProperty("revision").GetInt32(), categories }));
        }
        if (action == "project.update.metadata") return await host.DispatchAsync("project.update", """{"entityName":"合成改名"}""");
        if (action == "project.update.date") return await host.DispatchAsync("project.update", """{"lastPeriodStart":"2025-12-31"}""");
        if (action == "calendar.setNonWorkingDays") return await host.DispatchAsync(action, """{"days":[1]}""");
        if (action == "calendar.sameNonWorkingDays") return await host.DispatchAsync("calendar.setNonWorkingDays", """{"days":[0,6]}""");
        if (action is "import.holiday" or "import.makeupDay") return await host.DispatchAsync(action, """{"dates":["2025-06-01"]}""");
        if (action == "import.authorizedPreparer.clear") return await host.DispatchAsync(action);
        if (action == "accountMapping.save")
        {
            await ImportFileAsync(host, "import.accountMapping.fromFile");
            await host.DispatchAsync("prescreen.run");
            await host.DispatchAsync("filter.commit", """{"scenarios":[{"name":"合成借方","rationale":"固定答案","groups":[{"rules":[{"type":"drCrOnly","drCr":"debit"}]}]}]}""");
            return await host.DispatchAsync(action, """{"changes":[{"accountCode":"1000","categoryId":"builtin.others"}]}""");
        }
        return await ImportFileAsync(host, action);
    }

    private static async Task<JsonElement> ImportFileAsync(HandlerTestHost host, string action)
    {
        var path = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            string[] headers;
            object[] values;
            var headerRow = 1;
            if (action == "import.gl.fromFile")
            {
                headers = ["Doc", "Date", "Account", "Amount"];
                values = ["V1", "2025-06-01", "1000", 10];
            }
            else if (action == "import.tb.fromFile")
            {
                headers = ["Account", "Change"]; values = ["1000", 10];
            }
            else if (action == "import.accountMapping.fromFile")
            {
                headers = ["科目代號", "科目名稱", "標準化分類"]; values = ["1000", "合成資產", "Cash"];
            }
            else if (action == "import.authorizedPreparer.fromFile")
            {
                headers = ["Personnel"]; values = ["SYNTHETIC"];
            }
            else
            {
                headerRow = 2;
                headers = action == "import.holiday.fromFile" ? ["Date_of_Holiday", "Holiday_Name", "IS_Holiday"] : ["Date_of_MakeUpday"];
                values = action == "import.holiday.fromFile" ? ["2025-06-01", "合成假日", "Y"] : ["2025-06-01"];
                sheet.Cell(1, 1).Value = "Synthetic calendar";
            }
            for (var index = 0; index < headers.Length; index++) sheet.Cell(headerRow, index + 1).Value = headers[index];
            for (var index = 0; index < values.Length; index++) sheet.Cell(headerRow + 1, index + 1).Value = values[index].ToString();
        });
        try
        {
            return await host.DispatchAsync(action, JsonSerializer.Serialize(new { filePath = path, sourceColumn = "Personnel" }));
        }
        finally { TestWorkbookBuilder.Delete(path); }
    }

    private static void AssertFlags(JsonElement value, bool validation, bool prescreen, bool filter)
    {
        Assert.Equal(new[] { "filter", "prescreen", "validation" }, value.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal));
        Assert.Equal(validation, value.GetProperty("validation").GetBoolean());
        Assert.Equal(prescreen, value.GetProperty("prescreen").GetBoolean());
        Assert.Equal(filter, value.GetProperty("filter").GetBoolean());
    }
}
