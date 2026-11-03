using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class ReportRevisionStateTests
{
    [Theory]
    [InlineData(null, "41", false, true)]
    [InlineData("40", "41", false, true)]
    [InlineData("41", null, false, true)]
    [InlineData("41", "41", false, false)]
    [InlineData("41", "41", true, true)]
    public void CriteriaSource_RequiresItsActualDataRevision(string? savedRevision, string? currentRevision, bool filterStale, bool expected)
    {
        var run = new ResultRecord("validation", ResultKind.Validation, DateTimeOffset.UnixEpoch, "{}");
        var report = new Report("report", ReportKind.CriteriaSelectionReport, "synthetic.xlsx",
            new ReportSourceRefs(ValidationResultId: "validation", ScenarioRevision: "scenario", ScenarioPositions: [1], FilterDataRevision: savedRevision),
            DateTimeOffset.UnixEpoch, 0, LastWriteUtc: null, Stale: false);
        Assert.Equal(expected, ReportExportSupport.IsSourceStale(report, run, null, filterStale, "scenario", [1], currentRevision));
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
        { validationResultId = prepared.ValidationResultId, revision = prepared.Revision }));
        Assert.Equal("41", report.GetProperty("publishedReport").GetProperty("sourceRef").GetProperty("filterDataRevision").GetString());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task OldCriteria_RemainsStaleAfterCalendarChangeAndResavingTheSameScenario(string provider)
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host, provider);
        var exported = await host.DispatchAsync("export.criteriaSelectionReport", JsonSerializer.Serialize(new
        { validationResultId = prepared.ValidationResultId, revision = prepared.Revision }));
        var path = exported.GetProperty("publishedReport").GetProperty("fullPath").GetString()!;
        Assert.InRange(new FileInfo(path).Length, 1, 10_000_000);
        var bytes = File.ReadAllBytes(path);
        await host.DispatchAsync("import.holiday", """{"dates":["2025-06-01"]}""");
        // 上游修改會清除下游（裁定 2026-10-07-03）：行事曆修改會清掉已存情境，不再有舊版本可以惰性重算。
        // 改成驗證情境清掉、重新儲存同一情境後，舊報告仍標成先前版本且檔案不被改寫。
        // 第一次失敗收據 20261007-032952783-7147565fec1c42be845dac22acf7263b。
        var cleared = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = prepared.Id }));
        Assert.Empty(cleared.GetProperty("filterScenarios").EnumerateArray());
        Assert.False(cleared.GetProperty("staleState").GetProperty("filter").GetBoolean());
        Assert.True(Assert.Single(cleared.GetProperty("reports").EnumerateArray()).GetProperty("stale").GetBoolean());
        await host.DispatchAsync("filter.commit", """{"scenarios":[{"name":"合成借方","rationale":"固定答案","groups":[{"conditions":[{"type":"drCrOnly","drCr":"debit"}]}]}]}""");
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = prepared.Id }));
        Assert.False(loaded.GetProperty("staleState").GetProperty("filter").GetBoolean());
        Assert.True(Assert.Single(loaded.GetProperty("reports").EnumerateArray()).GetProperty("stale").GetBoolean());
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("sqlite", "export.validationReports")]
    [InlineData("duckdb", "export.validationReports")]
    [InlineData("sqlite", "export.prescreenReport")]
    [InlineData("duckdb", "export.prescreenReport")]
    [InlineData("sqlite", "export.workingPaperStream")]
    [InlineData("duckdb", "export.workingPaperStream")]
    public async Task OtherExports_ReturnARefreshedCatalogWithoutReopeningTheProject(string provider, string action)
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host, provider);
        await host.DispatchAsync("export.criteriaSelectionReport", JsonSerializer.Serialize(new
        { validationResultId = prepared.ValidationResultId, revision = prepared.Revision }));
        await host.DispatchAsync("import.holiday", """{"dates":["2025-06-01"]}""");
        var prescreen = await host.DispatchAsync("prescreen.run");
        // 上游修改會清除下游（裁定 2026-10-07-03）：行事曆修改清掉已存情境，舊版本不能再惰性重算；
        // 改為重新儲存同一情境，再用新版本匯出。第一次失敗收據 20261007-032952783-7147565fec1c42be845dac22acf7263b。
        var resaved = await host.DispatchAsync("filter.commit", """{"scenarios":[{"name":"合成借方","rationale":"固定答案","groups":[{"conditions":[{"type":"drCrOnly","drCr":"debit"}]}]}]}""");
        var revision = resaved.GetProperty("resultRef").GetProperty("revision").GetString();
        var response = await host.DispatchAsync(action, JsonSerializer.Serialize(new
        {
            resultId = action == "export.prescreenReport" ? prescreen.GetProperty("resultRef").GetProperty("resultId").GetString() : prepared.ValidationResultId,
            validationResultId = prepared.ValidationResultId, scenarioRevision = revision, scenarioPositions = new[] { 1 }
        }));
        var old = response.GetProperty("reports").EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "criteriaSelectionReport");
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
            var summary = JsonNode.Parse(loaded.GetProperty(ResultInvalidationTestSupport.LoadedResultProperty(kind)).GetRawText())!.AsObject();
            summary["resultRef"]!["logicVersion"] = "obsolete-synthetic-version";
            await ExecuteAsync(host, prepared.Id, provider, $"UPDATE {ResultInvalidationTestSupport.ResultTable(kind)} SET summary_json=@summary;",
                ("@summary", summary.ToJsonString()));
        }
        var reopened = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = prepared.Id }));
        Assert.Equal(JsonValueKind.Null, reopened.GetProperty("validationResult").ValueKind);
        Assert.Equal(JsonValueKind.Null, reopened.GetProperty("prescreenResult").ValueKind);
        Assert.True(reopened.GetProperty("staleState").GetProperty("validation").GetBoolean());
        Assert.True(reopened.GetProperty("staleState").GetProperty("prescreen").GetBoolean());
    }

    /// <summary>
    /// 同一種資料庫在一個測試程序裡只建一次，之後每個測試複製一份（<see cref="CaseTemplates"/>）。
    /// <paramref name="fromScratch"/> 為 true 時在這個 host 上照順序建案，給要檢查建案者身分或建案過程的測試。
    /// </summary>
    internal static async Task<Prepared> PrepareAsync(HandlerTestHost host, string provider, bool fromScratch = false)
    {
        if (fromScratch || !CaseTemplates.CanCopy(provider))
        {
            return await BuildPreparedAsync(host, provider);
        }

        var copied = await CaseTemplates.CopyIntoAsync(host, $"batch9-artifact-state:{provider}", async templateHost =>
        {
            var prepared = await BuildPreparedAsync(templateHost, provider);
            return (prepared.Id, prepared);
        });
        return copied.State;
    }

    private static async Task<Prepared> BuildPreparedAsync(HandlerTestHost host, string provider)
    {
        var id = await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "核准日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("V1", "2025-06-01", "2025-06-02", "1000", "合成資產", "synthetic", 10, 1)
            .AddRow("V1", "2025-06-01", "2025-06-02", "4000", "合成收入", "synthetic", 10, 0),
            databaseProvider: provider, validateForDownstream: true);
        var validation = await host.DispatchAsync("validate.run");
        await host.DispatchAsync("prescreen.run");
        var saved = await host.DispatchAsync("filter.commit", """{"scenarios":[{"name":"合成借方","rationale":"固定答案","groups":[{"conditions":[{"type":"drCrOnly","drCr":"debit"}]}]}]}""");
        return new Prepared(id, validation.GetProperty("resultRef").GetProperty("resultId").GetString()!, saved.GetProperty("resultRef").GetProperty("revision").GetString()!);
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

    internal sealed record Prepared(string Id, string ValidationResultId, string Revision);
}
