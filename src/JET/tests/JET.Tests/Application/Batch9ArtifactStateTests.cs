using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class Batch9ArtifactStateTests
{
    [Theory]
    [InlineData(null, "41", false, true)]
    [InlineData("40", "41", false, true)]
    [InlineData("41", null, false, true)]
    [InlineData("41", "41", false, false)]
    [InlineData("41", "41", true, true)]
    public void CriteriaSource_RequiresItsActualDataRevision(string? savedRevision, string? currentRevision, bool filterStale, bool expected)
    {
        var run = new RuleRunRecord("validation", RuleRunKinds.Validate, DateTimeOffset.UnixEpoch, "{}");
        var artifact = new ReportArtifact("artifact", ReportArtifactKind.CriteriaSelectionReport, "synthetic.xlsx",
            new ReportArtifactSourceRefs(ValidationRunId: "validation", ScenarioRevision: "scenario", ScenarioPositions: [1], FilterDataRevision: savedRevision),
            DateTimeOffset.UnixEpoch, 0, LastWriteUtc: null, Stale: false);
        Assert.Equal(expected, ReportExportSupport.IsSourceStale(artifact, run, null, filterStale, "scenario", [1], currentRevision));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task CriteriaExport_RecordsThePersistedFilterDataRevision(string provider)
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host, provider);
        await ExecuteAsync(host, prepared.Id, provider, "UPDATE schema_info SET value='41' WHERE key='filter_data_revision';");
        var report = await host.DispatchAsync("export.criteriaSelectionReport", JsonSerializer.Serialize(new
        { validationRunId = prepared.ValidationRunId, revision = prepared.Revision }));
        Assert.Equal("41", report.GetProperty("artifact").GetProperty("sourceRef").GetProperty("filterDataRevision").GetString());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task OldCriteria_RemainsStaleAfterCalendarChangeAndLazyRecalculation(string provider)
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host, provider);
        var exported = await host.DispatchAsync("export.criteriaSelectionReport", JsonSerializer.Serialize(new
        { validationRunId = prepared.ValidationRunId, revision = prepared.Revision }));
        var path = exported.GetProperty("artifact").GetProperty("fullPath").GetString()!;
        Assert.InRange(new FileInfo(path).Length, 1, 10_000_000);
        var bytes = File.ReadAllBytes(path);
        await host.DispatchAsync("import.holiday", """{"dates":["2025-06-01"]}""");
        var hits = await host.DispatchAsync("query.filterHitsPage", JsonSerializer.Serialize(new
        { scenarioPosition = 1, scenarioRevision = prepared.Revision }));
        Assert.Single(hits.GetProperty("rows").EnumerateArray());
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = prepared.Id }));
        Assert.False(loaded.GetProperty("staleState").GetProperty("filter").GetBoolean());
        Assert.True(Assert.Single(loaded.GetProperty("reportArtifacts").EnumerateArray()).GetProperty("stale").GetBoolean());
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("sqlite", "export.validationArtifacts")]
    [InlineData("duckdb", "export.validationArtifacts")]
    [InlineData("sqlite", "export.prescreenReport")]
    [InlineData("duckdb", "export.prescreenReport")]
    [InlineData("sqlite", "export.workpaperStream")]
    [InlineData("duckdb", "export.workpaperStream")]
    public async Task OtherExports_ReturnARefreshedCatalogWithoutReopeningTheProject(string provider, string action)
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host, provider);
        await host.DispatchAsync("export.criteriaSelectionReport", JsonSerializer.Serialize(new
        { validationRunId = prepared.ValidationRunId, revision = prepared.Revision }));
        await host.DispatchAsync("import.holiday", """{"dates":["2025-06-01"]}""");
        var prescreen = await host.DispatchAsync("prescreen.run");
        await host.DispatchAsync("query.filterHitsPage", JsonSerializer.Serialize(new
        { scenarioPosition = 1, scenarioRevision = prepared.Revision }));
        var response = await host.DispatchAsync(action, JsonSerializer.Serialize(new
        {
            runId = action == "export.prescreenReport" ? prescreen.GetProperty("resultRef").GetProperty("runId").GetString() : prepared.ValidationRunId,
            validationRunId = prepared.ValidationRunId, scenarioRevision = prepared.Revision, scenarioPositions = new[] { 1 }
        }));
        var old = response.GetProperty("reportArtifacts").EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "criteriaSelectionReport");
        Assert.True(old.GetProperty("stale").GetBoolean());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ProjectLoad_ReportsObsoleteRunVersionsAsStaleWithoutInventingResults(string provider)
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host, provider);
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = prepared.Id }));
        foreach (var kind in new[] { "validate", "prescreen" })
        {
            var summary = JsonNode.Parse(loaded.GetProperty("latestRuns").GetProperty(kind).GetRawText())!.AsObject();
            summary["resultRef"]!["logicVersion"] = "obsolete-synthetic-version";
            await ExecuteAsync(host, prepared.Id, provider, "UPDATE result_rule_run SET summary_json=@summary WHERE run_kind=@kind;",
                ("@summary", summary.ToJsonString()), ("@kind", kind));
        }
        var reopened = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = prepared.Id }));
        Assert.Equal(JsonValueKind.Null, reopened.GetProperty("latestRuns").GetProperty("validate").ValueKind);
        Assert.Equal(JsonValueKind.Null, reopened.GetProperty("latestRuns").GetProperty("prescreen").ValueKind);
        Assert.True(reopened.GetProperty("staleState").GetProperty("validation").GetBoolean());
        Assert.True(reopened.GetProperty("staleState").GetProperty("prescreen").GetBoolean());
    }

    internal static async Task<Prepared> PrepareAsync(HandlerTestHost host, string provider)
    {
        var id = await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "核准日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("V1", "2025-06-01", "2025-06-02", "1000", "合成資產", "synthetic", 10, 1)
            .AddRow("V1", "2025-06-01", "2025-06-02", "4000", "合成收入", "synthetic", 10, 0),
            databaseProvider: provider, validateForDownstream: true);
        var validation = await host.DispatchAsync("validate.run");
        await host.DispatchAsync("prescreen.run");
        var saved = await host.DispatchAsync("filter.commit", """{"scenarios":[{"name":"合成借方","rationale":"固定答案","groups":[{"rules":[{"type":"drCrOnly","drCr":"debit"}]}]}]}""");
        return new Prepared(id, validation.GetProperty("resultRef").GetProperty("runId").GetString()!, saved.GetProperty("resultRef").GetProperty("revision").GetString()!);
    }

    internal static async Task ExecuteAsync(HandlerTestHost host, string id, string provider, string sql, params (string Name, object Value)[] parameters)
    {
        var folder = new JetProjectFolder(host.ProjectsRoot);
        ILocalProjectDatabase database = provider == "duckdb" ? new DuckDbProjectDatabase(folder) : new SqliteProjectDatabase(folder);
        await using var connection = database.CreateConnection(id);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter);
        }
        await command.ExecuteNonQueryAsync();
    }

    internal sealed record Prepared(string Id, string ValidationRunId, string Revision);
}
