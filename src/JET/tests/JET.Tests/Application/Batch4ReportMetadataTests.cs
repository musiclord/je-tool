using System.Text.Json;
using ClosedXML.Excel;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>第 4 批：合成案件的報告公司表頭、案件資料修改與既有輸出保留。</summary>
public sealed class Batch4ReportMetadataTests
{
    private const string CaseName = "第4批合成案件";
    private const string OriginalEntity = "修改前合成客戶";
    private const string UpdatedEntity = "修改後合成客戶";
    private static readonly string[] ReportKinds =
        ["criteriaSelectionReport", "infReport", "prescreenReport", "validationReport", "workingPaper"];

    [Theory]
    [InlineData("sqlite", "")]
    [InlineData("sqlite", "　 ")]
    [InlineData("duckdb", "")]
    [InlineData("duckdb", "　 ")]
    public async Task BlankEntityName_FiveReportsUseCaseName(string provider, string entityName)
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host, provider, entityName);
        var reports = await ExportFiveAsync(host, prepared);

        AssertHeaders(reports, CaseName);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task UpdateEntityName_PreservesOldFilesAndStaleState_ThenNewExportsUseNewName(string provider)
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host, provider, OriginalEntity);
        var reports = await ExportFiveAsync(host, prepared);
        AssertHeaders(reports, OriginalEntity);
        var originalBytes = reports.ToDictionary(item => item.Key, item => ReadSmallSyntheticFile(item.Value));

        await host.DispatchAsync("project.update", JsonSerializer.Serialize(new
        {
            projectId = CaseName,
            projectCode = "BATCH4-UPDATED",
            entityName = UpdatedEntity,
            lastPeriodStart = "2025-12-31"
        }));

        AssertFilesUnchanged(reports, originalBytes);
        var afterUpdate = await LoadAsync(host);
        AssertAllFiveStale(afterUpdate);
        Assert.Equal(prepared.ValidationRunId, afterUpdate.GetProperty("latestRuns").GetProperty("validate")
            .GetProperty("resultRef").GetProperty("runId").GetString());
        Assert.Equal(prepared.PrescreenRunId, afterUpdate.GetProperty("latestRuns").GetProperty("prescreen")
            .GetProperty("resultRef").GetProperty("runId").GetString());

        await host.DispatchAsync("project.releaseLock");
        using var reopened = new HandlerTestHost(projectsRootPath: host.ProjectsRoot);
        var loaded = await LoadAsync(reopened);
        AssertAllFiveStale(loaded);
        AssertFilesUnchanged(reports, originalBytes);

        var freshReports = await ExportFiveAsync(reopened, prepared);
        AssertHeaders(freshReports, UpdatedEntity);
        Assert.NotEqual(reports["workingPaper"], freshReports["workingPaper"]);
        Assert.Equal(originalBytes["workingPaper"], ReadSmallSyntheticFile(reports["workingPaper"]));

        var final = await LoadAsync(reopened);
        var artifacts = final.GetProperty("reportArtifacts").EnumerateArray().ToArray();
        Assert.Equal(6, artifacts.Length);
        var oldPaperName = Path.GetFileName(reports["workingPaper"]);
        Assert.True(artifacts.Single(item => item.GetProperty("fileName").GetString() == oldPaperName)
            .GetProperty("stale").GetBoolean());
        Assert.All(artifacts.Where(item => item.GetProperty("fileName").GetString() != oldPaperName),
            item => Assert.False(item.GetProperty("stale").GetBoolean()));
    }

    private static async Task<Prepared> PrepareAsync(HandlerTestHost host, string provider, string entityName)
    {
        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            caseName = CaseName,
            projectCode = "BATCH4-ORIGINAL",
            entityName,
            periodStart = "2025-01-01",
            periodEnd = "2025-12-31",
            lastPeriodStart = "2025-12-31",
            databaseProvider = provider
        }));
        Assert.Equal(CaseName, created.GetProperty("projectId").GetString());

        var gl = new InlineGlWorkbookBuilder()
            .WithColumns("傳票號碼", "傳票日期", "核准日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("JV-001", "2025-03-05", "2026-01-02", "1101", "合成資產", "調整分錄", "100.00", 1)
            .AddRow("JV-001", "2025-03-05", "2026-01-02", "4101", "合成收入", "調整分錄", "100.00", 0);
        var tb = new InlineTbWorkbookBuilder()
            .AddRow("1101", "合成資產", 100)
            .AddRow("4101", "合成收入", -100);
        await ImportAsync(host, "gl", gl.WriteWorkbook());
        await ImportAsync(host, "tb", tb.WriteWorkbook());
        await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping = gl.BuildFlagModeMapping(),
            amountMode = "flag"
        }));
        await host.DispatchAsync("mapping.commit.tb", JsonSerializer.Serialize(new
        {
            mapping = InlineTbWorkbookBuilder.BuildDirectModeMapping(),
            changeMode = "direct"
        }));

        var validation = await host.DispatchAsync("validate.run");
        Assert.True(validation.GetProperty("completenessTest").GetProperty("eligibility")
            .GetProperty("isEligible").GetBoolean());
        var prescreen = await host.DispatchAsync("prescreen.run");
        var committed = await host.DispatchAsync("filter.commit", """
            {"scenarios":[{"name":"合成表頭檢查","rationale":"只核對報告中繼資料","groups":[{"rules":[
                {"type":"customKeywords","keywords":"調整"}
            ]}]}]}
            """);
        return new Prepared(
            validation.GetProperty("resultRef").GetProperty("runId").GetString()!,
            prescreen.GetProperty("resultRef").GetProperty("runId").GetString()!,
            committed.GetProperty("resultRef").GetProperty("revision").GetString()!);
    }

    private static async Task ImportAsync(HandlerTestHost host, string dataset, string filePath)
    {
        try
        {
            await host.DispatchAsync($"import.{dataset}.fromFile", JsonSerializer.Serialize(new { filePath }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(filePath);
        }
    }

    private static async Task<Dictionary<string, string>> ExportFiveAsync(HandlerTestHost host, Prepared prepared)
    {
        await host.DispatchAsync("export.validationArtifacts", JsonSerializer.Serialize(new
        {
            runId = prepared.ValidationRunId
        }));
        await host.DispatchAsync("export.prescreenReport", JsonSerializer.Serialize(new
        {
            runId = prepared.PrescreenRunId
        }));
        await host.DispatchAsync("export.criteriaSelectionReport", JsonSerializer.Serialize(new
        {
            validationRunId = prepared.ValidationRunId,
            revision = prepared.ScenarioRevision
        }));
        await host.DispatchAsync("export.workpaperStream", JsonSerializer.Serialize(new
        {
            validationRunId = prepared.ValidationRunId,
            scenarioRevision = prepared.ScenarioRevision,
            scenarioPositions = new[] { 1 }
        }));
        var loaded = await LoadAsync(host);
        var artifacts = loaded.GetProperty("reportArtifacts").EnumerateArray()
            .Where(item => !item.GetProperty("stale").GetBoolean()).ToArray();
        Assert.Equal(ReportKinds, artifacts.Select(item => item.GetProperty("kind").GetString()!)
            .Order(StringComparer.Ordinal).ToArray());
        return artifacts.ToDictionary(
            item => item.GetProperty("kind").GetString()!,
            item => Path.Combine(host.ProjectsRoot, CaseName, item.GetProperty("fileName").GetString()!));
    }

    private static void AssertHeaders(IReadOnlyDictionary<string, string> reports, string companyName)
    {
        // 固定工作表、儲存格與顯示格式，不呼叫產品 fallback 來計算預期值。
        var cells = new (string Kind, string Sheet, string Cell, string Expected)[]
        {
            ("validationReport", "ValidationReport", "E1", $"Client: {companyName}"),
            ("infReport", "INF Testing 可靠性測試", "A1", $"公司名稱 [{companyName}]"),
            ("prescreenReport", "Pre-screening_Report", "E1", $"Client: {companyName}"),
            ("criteriaSelectionReport", "Summary Inforamtion", "C1", $"Client: {companyName}"),
            ("workingPaper", "step1 完整性測試", "A1", $"公司名稱 : {companyName}")
        };
        Assert.All(cells, item =>
        {
            using var workbook = new XLWorkbook(reports[item.Kind]);
            Assert.Equal(item.Expected, workbook.Worksheet(item.Sheet).Cell(item.Cell).GetString());
        });
    }

    private static void AssertAllFiveStale(JsonElement loaded)
    {
        var artifacts = loaded.GetProperty("reportArtifacts").EnumerateArray().ToArray();
        Assert.Equal(ReportKinds, artifacts.Select(item => item.GetProperty("kind").GetString()!)
            .Order(StringComparer.Ordinal).ToArray());
        Assert.All(artifacts, item => Assert.True(item.GetProperty("stale").GetBoolean()));
    }

    private static void AssertFilesUnchanged(
        IReadOnlyDictionary<string, string> reports, IReadOnlyDictionary<string, byte[]> originalBytes) =>
        Assert.All(reports, item => Assert.Equal(originalBytes[item.Key], ReadSmallSyntheticFile(item.Value)));

    private static byte[] ReadSmallSyntheticFile(string path)
    {
        Assert.InRange(new FileInfo(path).Length, 1, 2 * 1024 * 1024);
        return File.ReadAllBytes(path);
    }

    private static Task<JsonElement> LoadAsync(HandlerTestHost host) =>
        host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = CaseName }));

    private sealed record Prepared(string ValidationRunId, string PrescreenRunId, string ScenarioRevision);
}
