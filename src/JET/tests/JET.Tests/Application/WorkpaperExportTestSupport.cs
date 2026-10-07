using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>WorkpaperExportHandler 各測試類別共用的輔助方法與事件發布器。</summary>
internal static class WorkpaperExportTestSupport
{
    internal static IEnumerable<string> FindWorkpapers(string folder) => Directory.EnumerateFiles(folder, "*WorkingPaper*.xlsx", SearchOption.AllDirectories);
    internal sealed class CancelOnFirstWorkpaperSheetProgress(CancellationTokenSource source) : IJetEventPublisher
    {
        private int _cancelled;

        public List<JsonElement> WorkpaperEvents { get; } = [];

        public void Publish(string eventName, object? payload)
        {
            if (eventName != "export.progress")
            {
                return;
            }

            var update = JsonSerializer.SerializeToElement(
                payload,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (!string.Equals(
                    update.GetProperty("artifactKind").GetString(),
                    ReportArtifactKindValues.WorkingPaper,
                    StringComparison.Ordinal))
            {
                return;
            }

            WorkpaperEvents.Add(update);
            if (string.Equals(
                    update.GetProperty("phase").GetString(),
                    "writingSheet",
                    StringComparison.Ordinal)
                && Interlocked.Exchange(ref _cancelled, 1) == 0)
            {
                source.Cancel();
            }
        }
    }

    internal sealed class ArmableCancelOnWorkpaperPhase(
        CancellationTokenSource source,
        string targetPhase) : IJetEventPublisher
    {
        private int _armed;
        private int _cancelled;

        public List<JsonElement> WorkpaperEvents { get; } = [];

        public void Arm()
        {
            Interlocked.Exchange(ref _cancelled, 0);
            Interlocked.Exchange(ref _armed, 1);
        }

        public void Publish(string eventName, object? payload)
        {
            if (eventName != "export.progress")
            {
                return;
            }

            var update = JsonSerializer.SerializeToElement(
                payload,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (!string.Equals(
                    update.GetProperty("artifactKind").GetString(),
                    ReportArtifactKindValues.WorkingPaper,
                    StringComparison.Ordinal))
            {
                return;
            }

            WorkpaperEvents.Add(update);
            if (Volatile.Read(ref _armed) != 0
                && string.Equals(
                    update.GetProperty("phase").GetString(),
                    targetPhase,
                    StringComparison.Ordinal)
                && Interlocked.Exchange(ref _cancelled, 1) == 0)
            {
                source.Cancel();
            }
        }
    }

    internal sealed record Prepared(
        string ProjectId,
        string ValidationRunId,
        string PrescreenRunId,
        string Revision,
        int[] Positions);

    internal static string ScenarioPayload() => JsonSerializer.Serialize(new
    {
        scenarios = new object[]
        {
            new
            {
                name = "回溯過帳",
                rationale = "測試第一個情境",
                groups = new[] { new { join = "and", rules = new object[] {
                    new { join = "and", type = "prescreen", prescreenKey = "backdatedPosting" }
                } } }
            },
            new
            {
                name = "借方行",
                rationale = "測試第二個情境",
                groups = new[] { new { join = "and", rules = new object[] {
                    new { join = "and", type = "drCrOnly", drCr = "debit" }
                } } }
            }
        }
    });

    internal static async Task<Prepared> PrepareAsync(
        HandlerTestHost host,
        bool publishCriteria = true)
    {
        var project = await DemoProjectPipeline.SetupAsync(host);
        var validation = await host.DispatchAsync("validate.run");
        var prescreen = await host.DispatchAsync("prescreen.run");
        var filter = await host.DispatchAsync("filter.commit", ScenarioPayload());
        var prepared = new Prepared(
            project.ProjectId,
            validation.GetProperty("resultRef").GetProperty("runId").GetString()!,
            prescreen.GetProperty("resultRef").GetProperty("runId").GetString()!,
            filter.GetProperty("resultRef").GetProperty("revision").GetString()!,
            [1, 2]);

        if (publishCriteria)
        {
            await host.DispatchAsync(
                "export.criteriaSelectionReport",
                JsonSerializer.Serialize(new
                {
                    validationRunId = prepared.ValidationRunId,
                    revision = prepared.Revision
                }));
        }

        return prepared;
    }

    internal static string Payload(Prepared prepared, IReadOnlyList<int>? positions = null) =>
        JsonSerializer.Serialize(new
        {
            validationRunId = prepared.ValidationRunId,
            scenarioRevision = prepared.Revision,
            scenarioPositions = positions ?? prepared.Positions
        });

    internal static async Task ReplaceValidationNullSummaryAsync(
        HandlerTestHost host,
        string projectId,
        string runId,
        IReadOnlyList<int> counts)
    {
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();

        await using var read = connection.CreateCommand();
        read.CommandText =
            "SELECT summary_json FROM result_rule_run WHERE run_id = @runId;";
        read.AddWithValue("@runId", runId);
        var raw = Assert.IsType<string>(await read.ExecuteScalarAsync());
        var summary = Assert.IsType<JsonObject>(JsonNode.Parse(raw));
        var nullRecords = Assert.IsType<JsonObject>(summary["nullRecordsTest"]);
        nullRecords["nullAccountCount"] = counts[0];
        nullRecords["nullDocumentCount"] = counts[1];
        nullRecords["nullDescriptionCount"] = counts[2];
        nullRecords["outOfRangeDateCount"] = counts[3];
        var sourceQuality = Assert.IsType<JsonObject>(summary["sourceQuality"]);
        sourceQuality["findingCount"] = counts[4];

        await using var update = connection.CreateCommand();
        update.CommandText =
            "UPDATE result_rule_run SET summary_json = @summary WHERE run_id = @runId;";
        update.AddWithValue("@summary", summary.ToJsonString());
        update.AddWithValue("@runId", runId);
        Assert.Equal(1, await update.ExecuteNonQueryAsync());
    }

    internal static ProjectReportArtifactStore ArtifactStore(HandlerTestHost host) =>
        new(new JetProjectFolder(host.ProjectsRoot));

    internal static void AssertCurrentWorkpaper(HandlerTestHost host, Prepared prepared, JsonElement response)
    {
        var artifact = response.GetProperty("artifact");
        Assert.Equal("workingPaper", artifact.GetProperty("kind").GetString());
        Assert.False(artifact.GetProperty("stale").GetBoolean());
        Assert.Equal(prepared.ValidationRunId, artifact.GetProperty("sourceRef").GetProperty("validationRunId").GetString());
        Assert.Equal(prepared.Revision, artifact.GetProperty("sourceRef").GetProperty("scenarioRevision").GetString());
        Assert.Equal(prepared.Positions, artifact.GetProperty("sourceRef").GetProperty("scenarioPositions").EnumerateArray().Select(p => p.GetInt32()));
        using var book = new XLWorkbook(Path.Combine(host.ProjectsRoot, prepared.ProjectId, artifact.GetProperty("fileName").GetString()!));
        // This fixed demo has no completeness differences: Step 1-3 is omitted, metadata is last.
        Assert.Equal(WorkpaperSheetCatalog.All.Where(name => name != WorkpaperSheetCatalog.Step13)
            .Append(ReportWorkbookMetadataFormat.WorksheetName), book.Worksheets.Select(sheet => sheet.Name));
        Assert.Equal(XLWorksheetVisibility.VeryHidden, book.Worksheet(ReportWorkbookMetadataFormat.WorksheetName).Visibility);
        Assert.True(book.Worksheet(WorkpaperSheetCatalog.Step41).RowsUsed().Count() > 1);
    }
}
