using System.Text.Json;
using ClosedXML.Excel;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

// 舊式關鍵字條件停用後，合成情境改用欄位關鍵字，原命中與報表斷言保留；首次失敗：20261014-135805134-9060396119bc4e8f8dae2eecd29f1776。

/// <summary>
/// 「預設審計員可信」的旅程驗收：審計員照設計流程操作，或在 JET 之外動了報告檔，案件都要能繼續。
/// 起因是 2026-09-02 公司測試環境的 artifact_recovery_conflict；第一個測試就是那條事故路徑。
/// </summary>
public sealed class ReportTrustJourneyTests
{
    [Fact]
    public async Task AccountMappingTemplate_OnlyIfMissing_PreservesFilledFileAndReportsDisposition()
    {
        using var host = new HandlerTestHost();
        var projectId = await ReportExportFixture.SetupProjectAsync(host);
        var validation = await host.DispatchAsync("validate.run");
        var resultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString()!;
        var payload = JsonSerializer.Serialize(new { resultId, onlyIfMissing = true });
        var created = await host.DispatchAsync("export.accountMappingTemplate", payload);
        var path = created.GetProperty("filePath").GetString()!;
        Assert.Equal("created", created.GetProperty("disposition").GetString());
        using (var workbook = new XLWorkbook(path))
        {
            workbook.Worksheet("AccountMapping").Cell(4, 3).Value = AccountMappingCategories.All[0];
            workbook.Save();
        }
        var filled = await File.ReadAllBytesAsync(path);
        // 不需要讀取已填工作簿，即使 Excel 佔用也可回報保留。
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var kept = await host.DispatchAsync("export.accountMappingTemplate", payload);
            Assert.Equal("kept", kept.GetProperty("disposition").GetString());
            Assert.Equal(path, kept.GetProperty("filePath").GetString());
            Assert.Equal(JsonValueKind.Null, kept.GetProperty("rowCount").ValueKind);
        }
        Assert.Equal(filled, await File.ReadAllBytesAsync(path));
        var replaced = await host.DispatchAsync("export.accountMappingTemplate", JsonSerializer.Serialize(new { resultId }));
        Assert.Equal("created", replaced.GetProperty("disposition").GetString());
        using var blank = new XLWorkbook(path);
        Assert.True(blank.Worksheet("AccountMapping").Cell(4, 3).IsEmpty());
        Assert.Empty(Directory.GetFiles(Path.Combine(host.ProjectsRoot, projectId), "*.tmp"));
    }

    [Fact]
    public async Task AccountMappingTemplate_OnlyIfMissing_FileAppearsDuringPublication_IsKept()
    {
        var publisher = new TemplateAppearsAtPublication();
        using var host = new HandlerTestHost(eventPublisher: publisher);
        var projectId = await ReportExportFixture.SetupProjectAsync(host);
        var validation = await host.DispatchAsync("validate.run");
        var resultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString()!;
        publisher.Path = Path.Combine(host.ProjectsRoot, projectId, ProjectFileNames.AccountMappingTemplate(projectId));
        var result = await host.DispatchAsync("export.accountMappingTemplate", JsonSerializer.Serialize(new { resultId, onlyIfMissing = true }));
        Assert.True(publisher.Observed);
        Assert.Equal("kept", result.GetProperty("disposition").GetString());
        Assert.Equal(new byte[] { 17, 29, 41 }, await File.ReadAllBytesAsync(publisher.Path));
        Assert.Empty(Directory.GetFiles(Path.Combine(host.ProjectsRoot, projectId), "*.tmp"));
    }

    private sealed class TemplateAppearsAtPublication : IJetEventPublisher
    {
        public string? Path { get; set; }
        public bool Observed { get; private set; }
        public void Publish(string eventName, object payload)
        {
            if (Path is null || eventName != "export.progress") { return; }
            var update = JsonSerializer.SerializeToElement(payload);
            if (update.GetProperty("reportKind").GetString() == "accountMapping"
                && update.GetProperty("phase").GetString() == "publishingReport")
            {
                File.WriteAllBytes(Path, [17, 29, 41]);
                Observed = true;
            }
        }
    }

    [Fact]
    public async Task AccountMappingTemplate_CancelledBeforePublication_KeepsFilledOriginal()
    {
        using var cancellation = new CancellationTokenSource();
        var publisher = new CancelOnTemplatePublication(cancellation);
        using var host = new HandlerTestHost(eventPublisher: publisher);
        var projectId = await ReportExportFixture.SetupProjectAsync(host);
        var validation = await host.DispatchAsync("validate.run");
        var resultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString()!;
        var payload = JsonSerializer.Serialize(new { resultId });
        var exported = await host.DispatchAsync("export.accountMappingTemplate", payload);
        var path = exported.GetProperty("filePath").GetString()!;
        using (var workbook = new XLWorkbook(path))
        {
            workbook.Worksheet("AccountMapping").Cell(4, 3).Value = AccountMappingCategories.All[0];
            workbook.Save();
        }
        var filledOriginal = await File.ReadAllBytesAsync(path);
        publisher.Armed = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.DispatchAsync(
            "export.accountMappingTemplate", payload, cancellation.Token));

        Assert.True(publisher.Observed);
        Assert.Equal(filledOriginal, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(Path.Combine(host.ProjectsRoot, projectId), "*.tmp"));
    }

    private sealed class CancelOnTemplatePublication(CancellationTokenSource cancellation) : IJetEventPublisher
    {
        public bool Armed { get; set; }
        public bool Observed { get; private set; }

        public void Publish(string eventName, object payload)
        {
            if (!Armed || eventName != "export.progress") { return; }
            var update = JsonSerializer.SerializeToElement(payload);
            if (update.GetProperty("reportKind").GetString() == "accountMapping"
                && update.GetProperty("phase").GetString() == "publishingReport")
            {
                Observed = true;
                cancellation.Cancel();
            }
        }
    }

    [Fact]
    public async Task AccountMappingTemplate_EditedInPlaceAndReexported_ProjectStillLoads()
    {
        using var host = new HandlerTestHost();
        var projectId = await ReportExportFixture.SetupProjectAsync(host);
        var projectDirectory = Path.Combine(host.ProjectsRoot, projectId);
        var validation = await host.DispatchAsync("validate.run");
        var resultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString()!;

        await host.DispatchAsync("export.accountMappingTemplate", JsonSerializer.Serialize(new { resultId }));
        var templatePath = Assert.Single(Directory.GetFiles(projectDirectory, "*AccountMapping*.xlsx"));

        // 審計員用 Excel 開範本、填 C 欄、存回原檔：位元組一定會變。
        using (var workbook = new XLWorkbook(templatePath))
        {
            var sheet = workbook.Worksheet("AccountMapping");
            var lastRow = sheet.LastRowUsed()!.RowNumber();
            for (var row = 4; row <= lastRow; row++)
            {
                sheet.Cell(row, 3).Value = AccountMappingCategories.All[0];
            }

            workbook.Save();
        }
        var filledTemplate = await File.ReadAllBytesAsync(templatePath);

        await host.DispatchAsync(
            "import.accountMapping.fromFile",
            JsonSerializer.Serialize(new { filePath = templatePath }));

        // 再匯出報告。舊設計在這一步會因為範本檔和紀錄不符而中止並留下 journal，之後案件就打不開。
        await host.DispatchAsync("export.validationReports", JsonSerializer.Serialize(new { resultId }));
        Assert.Equal(filledTemplate, await File.ReadAllBytesAsync(templatePath));

        // 再次產生範本仍使用相同工作檔位置，不把使用者填過的檔案當成不可變報告。
        var regenerated = await host.DispatchAsync("export.accountMappingTemplate", JsonSerializer.Serialize(new { resultId }));
        Assert.Equal(templatePath, regenerated.GetProperty("filePath").GetString());
        using (var workbook = new XLWorkbook(templatePath))
        {
            Assert.True(workbook.Worksheet("AccountMapping").Cell(4, 3).IsEmpty());
        }

        await host.DispatchAsync("project.releaseLock");
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));

        Assert.True(loaded.TryGetProperty("reports", out _));
        Assert.Equal(new[] { "infReport", "validationReport" }, loaded.GetProperty("reports").EnumerateArray()
            .Select(report => report.GetProperty("kind").GetString()).Order(StringComparer.Ordinal));
        // 原本另外確認舊版暫存檔 .report-artifacts.mutation-* 不在；產品早已不寫這種檔，那一行一定通過，拿掉。
        // 現行三種暫存檔的檢查改用共用的寫法。
        ReportStoreTempFiles.AssertNoneLeft(projectDirectory);
    }

    [Fact]
    public async Task WorkingPaperExport_Twice_KeepsBothVersionFiles()
    {
        using var host = new HandlerTestHost();
        var projectId = await ReportExportFixture.SetupProjectAsync(host);
        var projectDirectory = Path.Combine(host.ProjectsRoot, projectId);
        var (validationResultId, prescreenResultId, revision) = await RunThroughFilterAsync(host);
        await host.DispatchAsync(
            "export.criteriaSelectionReport",
            JsonSerializer.Serialize(new { validationResultId, prescreenResultId, revision }));
        var workingPaperPayload = JsonSerializer.Serialize(new
        {
            validationResultId,
            prescreenResultId,
            scenarioRevision = revision,
            scenarioPositions = new[] { 1 }
        });

        await host.DispatchAsync("export.workingPaperStream", workingPaperPayload);
        var firstPath = Assert.Single(Directory.GetFiles(projectDirectory, "*WorkingPaper*.xlsx"));
        var firstBytes = await File.ReadAllBytesAsync(firstPath);
        await host.DispatchAsync("export.workingPaperStream", workingPaperPayload);

        Assert.Equal(2, Directory.GetFiles(projectDirectory, "*WorkingPaper*.xlsx").Length);
        Assert.Equal(firstBytes, await File.ReadAllBytesAsync(firstPath));
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        var workingPapers = loaded.GetProperty("reports").EnumerateArray()
            .Where(report => report.GetProperty("kind").GetString() == "workingPaper")
            .ToArray();
        Assert.Equal(2, workingPapers.Length);
        Assert.Equal(2, workingPapers.Select(report => report.GetProperty("fileName").GetString()).Distinct().Count());
        Assert.All(workingPapers, report => Assert.Equal("asPublished", report.GetProperty("fileState").GetString()));
        var replacedCounts = await DemoProjectPipeline.QueryStringListAsync(
            host,
            projectId,
            "SELECT CAST(replaced_count AS TEXT) FROM operation_log WHERE target_type = 'reportCatalog' " +
            "AND target_id = 'workingPaper' ORDER BY occurred_utc, event_id;");
        Assert.Equal(new[] { "0", "0" }, replacedCounts);
    }

    [Fact]
    public async Task ValidationExport_Twice_OverwritesSameFileName()
    {
        using var host = new HandlerTestHost();
        var projectId = await ReportExportFixture.SetupProjectAsync(host);
        var projectDirectory = Path.Combine(host.ProjectsRoot, projectId);
        var validation = await host.DispatchAsync("validate.run");
        var resultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString()!;

        await host.DispatchAsync("export.validationReports", JsonSerializer.Serialize(new { resultId }));
        await host.DispatchAsync("export.validationReports", JsonSerializer.Serialize(new { resultId }));

        Assert.Single(Directory.GetFiles(projectDirectory, "*ValidationReport*.xlsx"));
        Assert.Single(Directory.GetFiles(projectDirectory, "*INFReport*.xlsx"));
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        Assert.Single(
            loaded.GetProperty("reports").EnumerateArray(),
            report => report.GetProperty("kind").GetString() == "validationReport");
    }

    [Fact]
    public async Task ReportList_DeletedReport_LoadsAndReexportRestoresFile()
    {
        using var host = new HandlerTestHost();
        var projectId = await ReportExportFixture.SetupProjectAsync(host);
        var projectDirectory = Path.Combine(host.ProjectsRoot, projectId);
        var validation = await host.DispatchAsync("validate.run");
        var resultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString()!;
        await host.DispatchAsync("export.validationReports", JsonSerializer.Serialize(new { resultId }));
        var path = Assert.Single(Directory.GetFiles(projectDirectory, "*ValidationReport*.xlsx"));
        File.Delete(path);
        await host.DispatchAsync("project.releaseLock");

        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        var missing = Assert.Single(loaded.GetProperty("reports").EnumerateArray(),
            report => report.GetProperty("kind").GetString() == "validationReport");
        Assert.Equal("missing", missing.GetProperty("fileState").GetString());

        await host.DispatchAsync("export.validationReports", JsonSerializer.Serialize(new { resultId }));
        Assert.Equal(path, Assert.Single(Directory.GetFiles(projectDirectory, "*ValidationReport*.xlsx")));
        using var workbook = new XLWorkbook(path);
        Assert.NotEmpty(workbook.Worksheets);
        var reloaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        Assert.Equal(2, reloaded.GetProperty("reports").GetArrayLength());
        Assert.All(reloaded.GetProperty("reports").EnumerateArray(),
            report => Assert.Equal("asPublished", report.GetProperty("fileState").GetString()));
    }

    [Fact]
    public async Task ReportList_FileModifiedOutside_ReportsFlagInsteadOfBlocking()
    {
        using var host = new HandlerTestHost();
        var projectId = await ReportExportFixture.SetupProjectAsync(host);
        var projectDirectory = Path.Combine(host.ProjectsRoot, projectId);
        var validation = await host.DispatchAsync("validate.run");
        var resultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString()!;
        await host.DispatchAsync("export.validationReports", JsonSerializer.Serialize(new { resultId }));
        var validationPath = Assert.Single(Directory.GetFiles(projectDirectory, "*ValidationReport*.xlsx"));
        await File.AppendAllTextAsync(validationPath, "edited outside JET");

        await host.DispatchAsync("project.releaseLock");
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));

        var reports = loaded.GetProperty("reports").EnumerateArray().ToArray();
        var edited = Assert.Single(reports, report => report.GetProperty("kind").GetString() == "validationReport");
        Assert.Equal("modifiedOutside", edited.GetProperty("fileState").GetString());
        var untouched = Assert.Single(reports, report => report.GetProperty("kind").GetString() == "infReport");
        Assert.Equal("asPublished", untouched.GetProperty("fileState").GetString());
    }

    [Fact]
    public async Task AccountMappingTemplate_IsNotListedAsReport()
    {
        using var host = new HandlerTestHost();
        var projectId = await ReportExportFixture.SetupProjectAsync(host);
        var projectDirectory = Path.Combine(host.ProjectsRoot, projectId);
        var validation = await host.DispatchAsync("validate.run");
        var resultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString()!;

        var exported = await host.DispatchAsync(
            "export.accountMappingTemplate",
            JsonSerializer.Serialize(new { resultId }));

        var filePath = exported.GetProperty("filePath").GetString()!;
        Assert.True(File.Exists(filePath));
        Assert.Equal(projectDirectory, Path.GetDirectoryName(filePath), StringComparer.OrdinalIgnoreCase);
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        Assert.DoesNotContain(
            loaded.GetProperty("reports").EnumerateArray(),
            report => report.GetProperty("kind").GetString() == "accountMapping");
    }

    [Fact]
    public async Task AccountMappingTemplate_LockedFile_ReportsRetryAndKeepsOriginal()
    {
        using var host = new HandlerTestHost();
        var projectId = await ReportExportFixture.SetupProjectAsync(host);
        var validation = await host.DispatchAsync("validate.run");
        var resultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString()!;
        var payload = JsonSerializer.Serialize(new { resultId });
        var exported = await host.DispatchAsync("export.accountMappingTemplate", payload);
        var path = exported.GetProperty("filePath").GetString()!;
        var original = await File.ReadAllBytesAsync(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("export.accountMappingTemplate", payload));
            Assert.Equal(JetErrorCodes.FileReadError, error.Code);
            Assert.Contains("關閉", error.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(Path.Combine(host.ProjectsRoot, projectId), "*.tmp"));
        }

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        var retried = await host.DispatchAsync("export.accountMappingTemplate", payload);
        Assert.Equal(path, retried.GetProperty("filePath").GetString());
        using var workbook = new XLWorkbook(path);
        Assert.Equal("AccountMapping", workbook.Worksheet(1).Name);
    }

    private static async Task<string> CreateEmptyProjectAsync(HandlerTestHost host)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectCode = $"TRUST-{suffix}",
            clientName = "Trust Journey Test",
            operatorId = "tester",
            periodStart = "2025-01-01",
            periodEnd = "2025-12-31",
            databaseProvider = "sqlite",
        }));
        return created.GetProperty("projectId").GetString()!;
    }

    private static async Task<(string ValidationResultId, string PrescreenResultId, string Revision)> RunThroughFilterAsync(
        HandlerTestHost host)
    {
        var validation = await host.DispatchAsync("validate.run");
        var validationResultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString()!;
        var prescreen = await host.DispatchAsync("prescreen.run");
        var prescreenResultId = prescreen.GetProperty("resultRef").GetProperty("resultId").GetString()!;
        var committed = await host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new
            {
                scenarios = new[]
                {
                    new
                    {
                        name = "信任旅程情境",
                        rationale = "以合成摘要條件驗證版本檔",
                        groups = new[]
                        {
                            new
                            {
                                join = "AND",
                                conditions = new[]
                                {
                                    new { join = "AND", type = "fieldValue", field = "description", @operator = "contains", values = new[] { "調整" } }
                                }
                            }
                        }
                    }
                }
            }));
        var revision = committed.GetProperty("resultRef").GetProperty("revision").GetString()!;
        return (validationResultId, prescreenResultId, revision);
    }
}
