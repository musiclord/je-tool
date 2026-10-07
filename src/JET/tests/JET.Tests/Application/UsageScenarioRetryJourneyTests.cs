using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class UsageScenarioRetryJourneyTests
{
    // X-16: jet-guide sections 4 and 7: failed work leaves no partial new results.
    // Retrying after removing a synthetic I/O/SQL fault must produce a fresh complete result.
    [Theory]
    [InlineData("sqlite", "validate")]
    [InlineData("duckdb", "validate")]
    [InlineData("sqlite", "prescreen")]
    [InlineData("duckdb", "prescreen")]
    public async Task RuleRunWriteFailure_PreservesPreviousResultsAndRetryPublishesNewRun(string provider, string kind)
    {
        using var host = new HandlerTestHost();
        var prepared = await Batch9ArtifactStateTests.PrepareAsync(host, provider);
        await Batch9ArtifactStateTests.ExecuteAsync(host, prepared.Id, provider,
            "DELETE FROM result_rule_run WHERE run_kind='validate' AND run_id<>@id; DELETE FROM result_inf_sampling_test_sample WHERE run_id<>@id;",
            ("@id", prepared.ValidationRunId));
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = prepared.Id }));
        var oldRun = loaded.GetProperty("latestRuns").GetProperty(kind).GetProperty("resultRef").GetProperty("runId").GetString()!;
        var staleColumn = kind == "validate" ? "validation_stale" : "prescreen_stale";
        await Batch9ArtifactStateTests.ExecuteAsync(host, prepared.Id, provider,
            $"UPDATE config_result_stale_state SET {staleColumn}=1 WHERE singleton=1;");
        var tables = new[] { "result_rule_run", "result_inf_sampling_test_sample", "config_result_stale_state", "config_filter_scenario", "result_filter_run" };
        var before = new Dictionary<string, string[]>();
        foreach (var table in tables) before[table] = await UsageScenarioMutationRollbackTests.ReadTableAsync(host, prepared.Id, provider, table);
        await Batch9ArtifactStateTests.ExecuteAsync(host, prepared.Id, provider, $$"""
            DROP INDEX IF EXISTS ix_result_rule_run_kind;
            ALTER TABLE result_rule_run RENAME TO synthetic_saved_rule_run;
            CREATE TABLE result_rule_run (run_id TEXT PRIMARY KEY, run_kind TEXT NOT NULL, generated_utc TEXT NOT NULL, summary_json TEXT NOT NULL,
                CHECK (run_kind <> '{{kind}}' OR run_id = '{{oldRun}}'));
            INSERT INTO result_rule_run SELECT * FROM synthetic_saved_rule_run;
            CREATE INDEX ix_result_rule_run_kind ON result_rule_run (run_kind, generated_utc);
            """);

        var error = await Record.ExceptionAsync(() => host.DispatchAsync(kind + ".run"));
        Assert.NotNull(error);
        Assert.Contains("CHECK", error.ToString(), StringComparison.OrdinalIgnoreCase);
        foreach (var table in tables)
            Assert.Equal(before[table], await UsageScenarioMutationRollbackTests.ReadTableAsync(host, prepared.Id, provider, table));
        await Batch9ArtifactStateTests.ExecuteAsync(host, prepared.Id, provider, """
            DROP TABLE result_rule_run;
            ALTER TABLE synthetic_saved_rule_run RENAME TO result_rule_run;
            CREATE INDEX ix_result_rule_run_kind ON result_rule_run (run_kind, generated_utc);
            """);

        var retry = await host.DispatchAsync(kind + ".run");
        var newRun = retry.GetProperty("resultRef").GetProperty("runId").GetString()!;
        Assert.NotEqual(oldRun, newRun);
        Assert.Equal(2, await Batch9TransactionBoundaryTests.ScalarAsync(host, prepared.Id, provider,
            $"SELECT COUNT(*) FROM result_rule_run WHERE run_kind='{kind}';"));
        Assert.Equal(0, await Batch9TransactionBoundaryTests.ScalarAsync(host, prepared.Id, provider,
            $"SELECT {staleColumn} FROM config_result_stale_state WHERE singleton=1;"));
        Assert.Equal(0, await Batch9TransactionBoundaryTests.ScalarAsync(host, prepared.Id, provider,
            "SELECT COUNT(*) FROM result_inf_sampling_test_sample s WHERE NOT EXISTS (SELECT 1 FROM result_rule_run r WHERE r.run_id=s.run_id AND r.run_kind='validate');"));
        var reopened = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = prepared.Id }));
        Assert.Equal(newRun, reopened.GetProperty("latestRuns").GetProperty(kind).GetProperty("resultRef").GetProperty("runId").GetString());
        Assert.Equal(loaded.GetProperty("filterScenarios").GetRawText(), reopened.GetProperty("filterScenarios").GetRawText());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task WorkpaperWriteFailure_PreservesPublishedVersionAndRetryPublishesAnother(string provider)
    {
        var events = new FailWorkpaperWriteOnce();
        using var host = new HandlerTestHost(eventPublisher: events);
        var prepared = await Batch9ArtifactStateTests.PrepareAsync(host, provider);
        var payload = JsonSerializer.Serialize(new
        { validationRunId = prepared.ValidationRunId, scenarioRevision = prepared.Revision, scenarioPositions = new[] { 1 } });
        var first = await host.DispatchAsync("export.workpaperStream", payload);
        var oldArtifact = first.GetProperty("artifact");
        var path = oldArtifact.GetProperty("fullPath").GetString()!;
        var folder = Path.Combine(host.ProjectsRoot, prepared.Id);
        var manifest = Path.Combine(folder, ProjectReportArtifactStore.ManifestFileName);
        Assert.InRange(new FileInfo(path).Length, 1, 10_000_000);
        var oldFile = await File.ReadAllBytesAsync(path);
        var oldManifest = await File.ReadAllBytesAsync(manifest);
        events.Armed = true;

        var error = await Record.ExceptionAsync(() => host.DispatchAsync("export.workpaperStream", payload));
        Assert.NotNull(error);
        // The public export boundary sanitizes I/O exceptions; the armed publisher proves
        // the injected failure occurred during writing instead of depending on leaked text.
        Assert.IsType<JetActionException>(error);
        Assert.True(events.FailedDuringWriting);
        Assert.Equal(oldFile, await File.ReadAllBytesAsync(path));
        Assert.Equal(oldManifest, await File.ReadAllBytesAsync(manifest));
        Assert.Single(WorkpaperExportTestSupport.FindWorkpapers(folder));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(folder, ProjectReportArtifactStore.JournalFileName)));

        var retry = await host.DispatchAsync("export.workpaperStream", payload);
        Assert.False(retry.GetProperty("artifact").GetProperty("stale").GetBoolean());
        Assert.NotEqual(oldArtifact.GetProperty("artifactId").GetString(), retry.GetProperty("artifact").GetProperty("artifactId").GetString());
        Assert.Equal(2, retry.GetProperty("reportArtifacts").GetArrayLength());
        Assert.Equal(2, WorkpaperExportTestSupport.FindWorkpapers(folder).Count());
        Assert.Equal(oldFile, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp", SearchOption.AllDirectories));
    }

    private sealed class FailWorkpaperWriteOnce : IJetEventPublisher
    {
        public bool Armed { get; set; }
        public bool FailedDuringWriting { get; private set; }
        public void Publish(string eventName, object payload)
        {
            if (!Armed || eventName != "export.progress") return;
            var progress = JsonSerializer.SerializeToElement(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (progress.GetProperty("artifactKind").GetString() != "workingPaper"
                || progress.GetProperty("phase").GetString() != "writingSheet") return;
            Armed = false;
            FailedDuringWriting = true;
            throw new IOException("synthetic-workpaper-write-failure");
        }
    }
}
