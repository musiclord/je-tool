using System.Text.Json;
using ClosedXML.Excel;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 「預設審計員可信」的旅程驗收：審計員照設計流程操作，或在 JET 之外動了報告檔，案件都要能繼續。
/// 起因是 2026-09-02 公司測試環境的 artifact_recovery_conflict；第一個測試就是那條事故路徑。
/// </summary>
public sealed class ReportArtifactTrustJourneyTests
{
    [Fact]
    public async Task AccountMappingTemplate_EditedInPlaceAndReexported_ProjectStillLoads()
    {
        using var host = new HandlerTestHost();
        var projectId = await ReportArtifactExportFixture.SetupProjectAsync(host);
        var projectDirectory = Path.Combine(host.ProjectsRoot, projectId);
        var validation = await host.DispatchAsync("validate.run");
        var runId = validation.GetProperty("resultRef").GetProperty("runId").GetString()!;

        await host.DispatchAsync("export.accountMappingTemplate", JsonSerializer.Serialize(new { runId }));
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
        await host.DispatchAsync("export.validationArtifacts", JsonSerializer.Serialize(new { runId }));
        Assert.Equal(filledTemplate, await File.ReadAllBytesAsync(templatePath));

        // 再次產生範本仍使用相同工作檔位置，不把使用者填過的檔案當成不可變報告。
        var regenerated = await host.DispatchAsync("export.accountMappingTemplate", JsonSerializer.Serialize(new { runId }));
        Assert.Equal(templatePath, regenerated.GetProperty("filePath").GetString());
        using (var workbook = new XLWorkbook(templatePath))
        {
            Assert.True(workbook.Worksheet("AccountMapping").Cell(4, 3).IsEmpty());
        }

        await host.DispatchAsync("project.releaseLock");
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));

        Assert.True(loaded.TryGetProperty("reportArtifacts", out _));
        Assert.Equal(new[] { "infReport", "validationReport" }, loaded.GetProperty("reportArtifacts").EnumerateArray()
            .Select(artifact => artifact.GetProperty("kind").GetString()).Order(StringComparer.Ordinal));
        Assert.Empty(Directory.GetFiles(projectDirectory, ".report-artifacts.mutation-*"));
        Assert.Empty(Directory.GetFiles(projectDirectory, ".report-artifact-*"));
    }

    [Fact]
    public async Task ProjectLoad_LeftoverMutationJournal_IsDiscardedAndLogged()
    {
        using var host = new HandlerTestHost();
        var projectId = await CreateEmptyProjectAsync(host);
        var projectDirectory = Path.Combine(host.ProjectsRoot, projectId);
        var journalPath = Path.Combine(projectDirectory, ".report-artifacts.mutation-v1.json");
        await File.WriteAllTextAsync(journalPath, """{ "formatVersion": 1, "operation": "writeBatch" }""");
        await host.DispatchAsync("project.releaseLock");

        const string correlationId = "leftover-journal-load";
        var loaded = await host.DispatchAsync(
            "project.load",
            JsonSerializer.Serialize(new { projectId }),
            CancellationToken.None,
            correlationId);

        Assert.True(loaded.TryGetProperty("reportArtifacts", out _));
        Assert.False(File.Exists(journalPath));
        var export = await host.DispatchAsync(
            "support.log.export",
            JsonSerializer.Serialize(new { projectId, correlationId }));
        var text = await File.ReadAllTextAsync(export.GetProperty("filePath").GetString()!);
        Assert.Contains("artifact.journal.discarded", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkpaperExport_Twice_KeepsBothVersionFiles()
    {
        using var host = new HandlerTestHost();
        var projectId = await ReportArtifactExportFixture.SetupProjectAsync(host);
        var projectDirectory = Path.Combine(host.ProjectsRoot, projectId);
        var (validationRunId, prescreenRunId, revision) = await RunThroughFilterAsync(host);
        await host.DispatchAsync(
            "export.criteriaSelectionReport",
            JsonSerializer.Serialize(new { validationRunId, prescreenRunId, revision }));
        var workpaperPayload = JsonSerializer.Serialize(new
        {
            validationRunId,
            prescreenRunId,
            scenarioRevision = revision,
            scenarioPositions = new[] { 1 }
        });

        await host.DispatchAsync("export.workpaperStream", workpaperPayload);
        var firstPath = Assert.Single(Directory.GetFiles(projectDirectory, "*WorkingPaper*.xlsx"));
        var firstBytes = await File.ReadAllBytesAsync(firstPath);
        await host.DispatchAsync("export.workpaperStream", workpaperPayload);

        Assert.Equal(2, Directory.GetFiles(projectDirectory, "*WorkingPaper*.xlsx").Length);
        Assert.Equal(firstBytes, await File.ReadAllBytesAsync(firstPath));
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        var workingPapers = loaded.GetProperty("reportArtifacts").EnumerateArray()
            .Where(artifact => artifact.GetProperty("kind").GetString() == "workingPaper")
            .ToArray();
        Assert.Equal(2, workingPapers.Length);
        Assert.Equal(2, workingPapers.Select(artifact => artifact.GetProperty("fileName").GetString()).Distinct().Count());
        Assert.All(workingPapers, artifact => Assert.Equal("asPublished", artifact.GetProperty("fileState").GetString()));
        var replacedCounts = await DemoProjectPipeline.QueryStringListAsync(
            host,
            projectId,
            "SELECT CAST(replaced_count AS TEXT) FROM audit_event_log WHERE target_type = 'reportCatalog' " +
            "AND target_id = 'workingPaper' ORDER BY occurred_utc, event_id;");
        Assert.Equal(new[] { "0", "0" }, replacedCounts);
    }

    [Fact]
    public async Task ValidationExport_Twice_OverwritesSameFileName()
    {
        using var host = new HandlerTestHost();
        var projectId = await ReportArtifactExportFixture.SetupProjectAsync(host);
        var projectDirectory = Path.Combine(host.ProjectsRoot, projectId);
        var validation = await host.DispatchAsync("validate.run");
        var runId = validation.GetProperty("resultRef").GetProperty("runId").GetString()!;

        await host.DispatchAsync("export.validationArtifacts", JsonSerializer.Serialize(new { runId }));
        await host.DispatchAsync("export.validationArtifacts", JsonSerializer.Serialize(new { runId }));

        Assert.Single(Directory.GetFiles(projectDirectory, "*ValidationReport*.xlsx"));
        Assert.Single(Directory.GetFiles(projectDirectory, "*INFReport*.xlsx"));
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        Assert.Single(
            loaded.GetProperty("reportArtifacts").EnumerateArray(),
            artifact => artifact.GetProperty("kind").GetString() == "validationReport");
    }

    [Fact]
    public async Task ReportList_DeletedReport_LoadsAndReexportRestoresFile()
    {
        using var host = new HandlerTestHost();
        var projectId = await ReportArtifactExportFixture.SetupProjectAsync(host);
        var projectDirectory = Path.Combine(host.ProjectsRoot, projectId);
        var validation = await host.DispatchAsync("validate.run");
        var runId = validation.GetProperty("resultRef").GetProperty("runId").GetString()!;
        await host.DispatchAsync("export.validationArtifacts", JsonSerializer.Serialize(new { runId }));
        var path = Assert.Single(Directory.GetFiles(projectDirectory, "*ValidationReport*.xlsx"));
        File.Delete(path);
        await host.DispatchAsync("project.releaseLock");

        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        var missing = Assert.Single(loaded.GetProperty("reportArtifacts").EnumerateArray(),
            artifact => artifact.GetProperty("kind").GetString() == "validationReport");
        Assert.Equal("missing", missing.GetProperty("fileState").GetString());

        await host.DispatchAsync("export.validationArtifacts", JsonSerializer.Serialize(new { runId }));
        Assert.Equal(path, Assert.Single(Directory.GetFiles(projectDirectory, "*ValidationReport*.xlsx")));
        using var workbook = new XLWorkbook(path);
        Assert.NotEmpty(workbook.Worksheets);
        var reloaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        Assert.Equal(2, reloaded.GetProperty("reportArtifacts").GetArrayLength());
        Assert.All(reloaded.GetProperty("reportArtifacts").EnumerateArray(),
            artifact => Assert.Equal("asPublished", artifact.GetProperty("fileState").GetString()));
    }

    [Fact]
    public async Task ReportList_FileModifiedOutside_ReportsFlagInsteadOfBlocking()
    {
        using var host = new HandlerTestHost();
        var projectId = await ReportArtifactExportFixture.SetupProjectAsync(host);
        var projectDirectory = Path.Combine(host.ProjectsRoot, projectId);
        var validation = await host.DispatchAsync("validate.run");
        var runId = validation.GetProperty("resultRef").GetProperty("runId").GetString()!;
        await host.DispatchAsync("export.validationArtifacts", JsonSerializer.Serialize(new { runId }));
        var validationPath = Assert.Single(Directory.GetFiles(projectDirectory, "*ValidationReport*.xlsx"));
        await File.AppendAllTextAsync(validationPath, "edited outside JET");

        await host.DispatchAsync("project.releaseLock");
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));

        var artifacts = loaded.GetProperty("reportArtifacts").EnumerateArray().ToArray();
        var edited = Assert.Single(artifacts, artifact => artifact.GetProperty("kind").GetString() == "validationReport");
        Assert.Equal("modifiedOutside", edited.GetProperty("fileState").GetString());
        var untouched = Assert.Single(artifacts, artifact => artifact.GetProperty("kind").GetString() == "infReport");
        Assert.Equal("asPublished", untouched.GetProperty("fileState").GetString());
    }

    [Fact]
    public async Task AccountMappingTemplate_IsNotListedAsReportArtifact()
    {
        using var host = new HandlerTestHost();
        var projectId = await ReportArtifactExportFixture.SetupProjectAsync(host);
        var projectDirectory = Path.Combine(host.ProjectsRoot, projectId);
        var validation = await host.DispatchAsync("validate.run");
        var runId = validation.GetProperty("resultRef").GetProperty("runId").GetString()!;

        var exported = await host.DispatchAsync(
            "export.accountMappingTemplate",
            JsonSerializer.Serialize(new { runId }));

        var filePath = exported.GetProperty("filePath").GetString()!;
        Assert.True(File.Exists(filePath));
        Assert.Equal(projectDirectory, Path.GetDirectoryName(filePath), StringComparer.OrdinalIgnoreCase);
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        Assert.DoesNotContain(
            loaded.GetProperty("reportArtifacts").EnumerateArray(),
            artifact => artifact.GetProperty("kind").GetString() == "accountMapping");
    }

    [Fact]
    public async Task AccountMappingTemplate_LockedFile_ReportsRetryAndKeepsOriginal()
    {
        using var host = new HandlerTestHost();
        var projectId = await ReportArtifactExportFixture.SetupProjectAsync(host);
        var validation = await host.DispatchAsync("validate.run");
        var runId = validation.GetProperty("resultRef").GetProperty("runId").GetString()!;
        var payload = JsonSerializer.Serialize(new { runId });
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
            entityName = "Trust Journey Test",
            operatorId = "tester",
            periodStart = "2025-01-01",
            periodEnd = "2025-12-31",
            databaseProvider = "sqlite",
        }));
        return created.GetProperty("projectId").GetString()!;
    }

    private static async Task<(string ValidationRunId, string PrescreenRunId, string Revision)> RunThroughFilterAsync(
        HandlerTestHost host)
    {
        var validation = await host.DispatchAsync("validate.run");
        var validationRunId = validation.GetProperty("resultRef").GetProperty("runId").GetString()!;
        var prescreen = await host.DispatchAsync("prescreen.run");
        var prescreenRunId = prescreen.GetProperty("resultRef").GetProperty("runId").GetString()!;
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
                                rules = new[]
                                {
                                    new { join = "AND", type = "customKeywords", keywords = "調整" }
                                }
                            }
                        }
                    }
                }
            }));
        var revision = committed.GetProperty("resultRef").GetProperty("revision").GetString()!;
        return (validationRunId, prescreenRunId, revision);
    }
}
