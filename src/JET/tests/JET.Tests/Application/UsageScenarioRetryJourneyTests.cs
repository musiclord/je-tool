using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class UsageScenarioRetryJourneyTests
{
    // Failed work leaves no partial new results.
    // Retrying after removing a synthetic I/O/SQL fault must produce a fresh complete result.
    [Theory]
    [InlineData("sqlite", "validate")]
    [InlineData("sqlite", "prescreen")]
    public async Task ResultWriteFailure_PreservesPreviousResultsAndRetryPublishesNewRun(string provider, string kind)
    {
        using var host = new HandlerTestHost();
        var prepared = await ReportRevisionStateTests.PrepareAsync(host, provider);
        await ReportRevisionStateTests.ExecuteAsync(host, prepared.Id, provider,
            "DELETE FROM validation_result WHERE result_id<>@id; DELETE FROM inf_sample WHERE result_id<>@id;",
            ("@id", prepared.ValidationResultId));
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = prepared.Id }));
        var oldRun = loaded.GetProperty(ResultInvalidationTestSupport.LoadedResultProperty(kind)).GetProperty("resultRef").GetProperty("resultId").GetString()!;
        var staleColumn = kind == "validate" ? "validation_stale" : "prescreen_stale";
        var resultTable = ResultInvalidationTestSupport.ResultTable(kind);
        await ReportRevisionStateTests.ExecuteAsync(host, prepared.Id, provider,
            $"UPDATE result_stale_state SET {staleColumn}=1 WHERE singleton=1;");
        var tables = new[] { "validation_result", "prescreen_result", "inf_sample", "result_stale_state", "filter_scenario", "filter_hit" };
        var before = new Dictionary<string, string[]>();
        foreach (var table in tables) before[table] = await UsageScenarioMutationRollbackTests.ReadTableAsync(host, prepared.Id, provider, table);
        await ReportRevisionStateTests.ExecuteAsync(host, prepared.Id, provider, $$"""
            ALTER TABLE {{resultTable}} RENAME TO synthetic_saved_result;
            CREATE TABLE {{resultTable}} (result_id TEXT PRIMARY KEY, generated_utc TEXT NOT NULL, summary_json TEXT NOT NULL,
                CHECK (result_id = '{{oldRun}}'));
            INSERT INTO {{resultTable}} SELECT * FROM synthetic_saved_result;
            """);

        var error = await Record.ExceptionAsync(() => host.DispatchAsync(kind + ".run"));
        Assert.NotNull(error);
        Assert.Contains("CHECK", error.ToString(), StringComparison.OrdinalIgnoreCase);
        foreach (var table in tables)
            Assert.Equal(before[table], await UsageScenarioMutationRollbackTests.ReadTableAsync(host, prepared.Id, provider, table));
        await ReportRevisionStateTests.ExecuteAsync(host, prepared.Id, provider, $"""
            DROP TABLE {resultTable};
            ALTER TABLE synthetic_saved_result RENAME TO {resultTable};
            """);

        var retry = await host.DispatchAsync(kind + ".run");
        var newRun = retry.GetProperty("resultRef").GetProperty("resultId").GetString()!;
        Assert.NotEqual(oldRun, newRun);
        Assert.Equal(2, await WriteFailureRollbackTests.ScalarAsync(host, prepared.Id, provider,
            $"SELECT COUNT(*) FROM {resultTable};"));
        Assert.Equal(0, await WriteFailureRollbackTests.ScalarAsync(host, prepared.Id, provider,
            $"SELECT {staleColumn} FROM result_stale_state WHERE singleton=1;"));
        Assert.Equal(0, await WriteFailureRollbackTests.ScalarAsync(host, prepared.Id, provider,
            "SELECT COUNT(*) FROM inf_sample s WHERE NOT EXISTS (SELECT 1 FROM validation_result r WHERE r.result_id=s.result_id);"));
        var reopened = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = prepared.Id }));
        Assert.Equal(newRun, reopened.GetProperty(ResultInvalidationTestSupport.LoadedResultProperty(kind)).GetProperty("resultRef").GetProperty("resultId").GetString());
        Assert.Equal(loaded.GetProperty("filterScenarios").GetRawText(), reopened.GetProperty("filterScenarios").GetRawText());
    }

    [Theory]
    [InlineData("sqlite")]
    public async Task WorkingPaperWriteFailure_PreservesPublishedVersionAndRetryPublishesAnother(string provider)
    {
        var events = new FailWorkingPaperWriteOnce();
        using var host = new HandlerTestHost(eventPublisher: events);
        var prepared = await ReportRevisionStateTests.PrepareAsync(host, provider);
        var payload = JsonSerializer.Serialize(new
        { validationResultId = prepared.ValidationResultId, scenarioRevision = prepared.Revision, scenarioPositions = new[] { 1 } });
        var first = await host.DispatchAsync("export.workingPaperStream", payload);
        var oldReport = first.GetProperty("publishedReport");
        var path = oldReport.GetProperty("fullPath").GetString()!;
        var folder = Path.Combine(host.ProjectsRoot, prepared.Id);
        var manifest = Path.Combine(folder, ProjectReportStore.ManifestFileName);
        Assert.InRange(new FileInfo(path).Length, 1, 10_000_000);
        var oldFile = await File.ReadAllBytesAsync(path);
        var oldManifest = await File.ReadAllBytesAsync(manifest);
        events.Armed = true;

        var error = await Record.ExceptionAsync(() => host.DispatchAsync("export.workingPaperStream", payload));
        Assert.NotNull(error);
        // The public export boundary sanitizes I/O exceptions; the armed publisher proves
        // the injected failure occurred during writing instead of depending on leaked text.
        Assert.IsType<JetActionException>(error);
        Assert.True(events.FailedDuringWriting);
        Assert.Equal(oldFile, await File.ReadAllBytesAsync(path));
        Assert.Equal(oldManifest, await File.ReadAllBytesAsync(manifest));
        Assert.Single(WorkingPaperExportTestSupport.FindWorkingPapers(folder));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp", SearchOption.AllDirectories));
        // 確認現行的暫存檔都沒有留下。
        ReportStoreTempFiles.AssertNoneLeft(folder);

        var retry = await host.DispatchAsync("export.workingPaperStream", payload);
        Assert.False(retry.GetProperty("publishedReport").GetProperty("stale").GetBoolean());
        Assert.NotEqual(oldReport.GetProperty("reportId").GetString(), retry.GetProperty("publishedReport").GetProperty("reportId").GetString());
        Assert.Equal(2, retry.GetProperty("reports").GetArrayLength());
        Assert.Equal(2, WorkingPaperExportTestSupport.FindWorkingPapers(folder).Count());
        Assert.Equal(oldFile, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp", SearchOption.AllDirectories));
    }

    private sealed class FailWorkingPaperWriteOnce : IJetEventPublisher
    {
        public bool Armed { get; set; }
        public bool FailedDuringWriting { get; private set; }
        public void Publish(string eventName, object payload)
        {
            if (!Armed || eventName != "export.progress") return;
            var progress = JsonSerializer.SerializeToElement(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (progress.GetProperty("reportKind").GetString() != "workingPaper"
                || progress.GetProperty("phase").GetString() != "writingSheet") return;
            Armed = false;
            FailedDuringWriting = true;
            throw new IOException("synthetic-working-paper-write-failure");
        }
    }
}
