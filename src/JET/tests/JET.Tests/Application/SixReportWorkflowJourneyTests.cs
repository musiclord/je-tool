using System.Security.Cryptography;
using System.Text.Json;
using ClosedXML.Excel;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 六份正式報告的 legacy 順序旅程：驗證三檔 → 填回 AccountMapping → 預篩選 →
/// Criteria → WorkingPaper。全程使用自含合成資料，不讀外部範本或真實帳務資料。
/// </summary>
public sealed class SixReportWorkflowJourneyTests
{
    [Fact]
    public async Task AccountMappingHandoff_PreservesValidationRun_AndPublishesSixCurrentArtifacts()
    {
        using var host = new HandlerTestHost();
        var projectId = await ReportArtifactExportFixture.SetupProjectAsync(host);

        var validation = await host.DispatchAsync("validate.run");
        var validationRunId = validation.GetProperty("resultRef").GetProperty("runId").GetString()!;
        var validationBatch = await host.DispatchAsync(
            "export.validationArtifacts",
            JsonSerializer.Serialize(new { runId = validationRunId }));

        var mappingArtifact = validationBatch.GetProperty("artifacts").EnumerateArray()
            .Single(artifact => artifact.GetProperty("kind").GetString() == "accountMapping");
        var projectDirectory = Path.Combine(host.ProjectsRoot, projectId);
        var mappingPath = Path.Combine(projectDirectory, mappingArtifact.GetProperty("fileName").GetString()!);
        var filledPath = Path.Combine(projectDirectory, $"filled-account-mapping-{Guid.NewGuid():N}.xlsx");
        File.Copy(mappingPath, filledPath);
        try
        {
            using (var workbook = new XLWorkbook(filledPath))
            {
                var sheet = workbook.Worksheet("AccountMapping");
                var lastRow = sheet.LastRowUsed()!.RowNumber();
                for (var row = 4; row <= lastRow; row++)
                {
                    sheet.Cell(row, 3).Value = AccountMappingCategories.All[0];
                }
                workbook.Save();
            }

            await host.DispatchAsync(
                "import.accountMapping.fromFile",
                JsonSerializer.Serialize(new { filePath = filledPath }));
        }
        finally
        {
            File.Delete(filledPath);
        }

        var afterMapping = await host.DispatchAsync(
            "project.load",
            JsonSerializer.Serialize(new { projectId }));
        Assert.Equal(
            validationRunId,
            afterMapping.GetProperty("latestRuns").GetProperty("validate")
                .GetProperty("resultRef").GetProperty("runId").GetString());
        Assert.Equal(JsonValueKind.Null, afterMapping.GetProperty("latestRuns").GetProperty("prescreen").ValueKind);

        var prescreen = await host.DispatchAsync("prescreen.run");
        var prescreenRunId = prescreen.GetProperty("resultRef").GetProperty("runId").GetString()!;
        await host.DispatchAsync(
            "export.prescreenReport",
            JsonSerializer.Serialize(new { runId = prescreenRunId }));

        var committed = await host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new
            {
                scenarios = new[]
                {
                    new
                    {
                        name = "完整旅程情境",
                        rationale = "以合成摘要條件驗證六報表順序",
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

        await host.DispatchAsync(
            "export.criteriaSelectionReport",
            JsonSerializer.Serialize(new { validationRunId, prescreenRunId, revision }));
        await host.DispatchAsync(
            "export.workpaperStream",
            JsonSerializer.Serialize(new
            {
                validationRunId,
                prescreenRunId,
                scenarioRevision = revision,
                scenarioPositions = new[] { 1 }
            }));

        var completed = await host.DispatchAsync(
            "project.load",
            JsonSerializer.Serialize(new { projectId }));
        var artifacts = completed.GetProperty("reportArtifacts").EnumerateArray().ToArray();
        Assert.Equal(
            new[]
            {
                "accountMapping", "criteriaSelectionReport", "infReport",
                "prescreenReport", "validationReport", "workingPaper"
            },
            artifacts.Select(artifact => artifact.GetProperty("kind").GetString())
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.All(artifacts, artifact => Assert.False(artifact.GetProperty("stale").GetBoolean()));
        Assert.All(artifacts, artifact =>
        {
            var fileName = artifact.GetProperty("fileName").GetString()!;
            Assert.Equal(Path.GetFileName(fileName), fileName);
            var artifactPath = Path.Combine(projectDirectory, fileName);
            Assert.True(File.Exists(artifactPath));
            AssertCanonicalFormalWorkbookMetadata(artifactPath);
        });

        var validationPath = ArtifactPath(artifacts, projectDirectory, "validationReport");
        var workingPaperPath = ArtifactPath(artifacts, projectDirectory, "workingPaper");
        AssertHiddenMappingMetadata(validationPath);
        AssertHiddenMappingMetadata(workingPaperPath);

        var glRowsBefore = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId, "SELECT COUNT(*) FROM target_gl_entry;");
        var tbRowsBefore = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId, "SELECT COUNT(*) FROM target_tb_balance;");

        var fromValidation = await host.DispatchAsync(
            "mapping.restoreDraft",
            JsonSerializer.Serialize(new { filePath = validationPath }));
        var fromWorkingPaper = await host.DispatchAsync(
            "mapping.restoreDraft",
            JsonSerializer.Serialize(new { filePath = workingPaperPath }));

        Assert.Equal(MappingMetadataFormat.CurrentVersion, fromValidation.GetProperty("formatVersion").GetInt32());
        Assert.Equal(
            ["formatVersion", "gl", "tb"],
            fromValidation.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(
            [
                "mapping", "amountMode", "approvalDateMode", "postingStatusPolicy",
                "manualAutoPolicy", "rdeFields"
            ],
            fromValidation.GetProperty("gl").EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(
            ["mapping", "changeMode"],
            fromValidation.GetProperty("tb").EnumerateObject().Select(property => property.Name).ToArray());
        AssertDraftMatchesCommitted(completed, fromValidation);
        AssertDraftMatchesCommitted(completed, fromWorkingPaper);
        Assert.Equal(
            fromValidation.GetProperty("gl").GetRawText(),
            fromWorkingPaper.GetProperty("gl").GetRawText());
        Assert.Equal(
            fromValidation.GetProperty("tb").GetRawText(),
            fromWorkingPaper.GetProperty("tb").GetRawText());

        var afterRestore = await host.DispatchAsync(
            "project.load",
            JsonSerializer.Serialize(new { projectId }));
        Assert.Equal(
            completed.GetProperty("mapping").GetRawText(),
            afterRestore.GetProperty("mapping").GetRawText());
        Assert.Equal(glRowsBefore, await DemoProjectPipeline.QueryScalarAsync(
            host, projectId, "SELECT COUNT(*) FROM target_gl_entry;"));
        Assert.Equal(tbRowsBefore, await DemoProjectPipeline.QueryScalarAsync(
            host, projectId, "SELECT COUNT(*) FROM target_tb_balance;"));

        ExportEvidenceIfRequested(projectId, artifacts, projectDirectory);
    }

    private static string ArtifactPath(JsonElement[] artifacts, string projectDirectory, string kind)
    {
        var fileName = artifacts.Single(artifact => artifact.GetProperty("kind").GetString() == kind)
            .GetProperty("fileName").GetString()!;
        return Path.Combine(projectDirectory, fileName);
    }

    private static void AssertHiddenMappingMetadata(string path)
    {
        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet("自動化工具-檔案欄位資訊");
        Assert.True(sheet.Column(6).IsHidden);
        Assert.True(sheet.Column(7).IsHidden);
        Assert.True(sheet.Column(8).IsHidden);
        Assert.Equal("JET_MAPPING_METADATA", sheet.Cell("F1").GetString());
        Assert.Equal(
            MappingMetadataFormat.CurrentVersion.ToString(),
            sheet.Cell("G1").GetString());
        Assert.StartsWith("{", sheet.Cell("H1").GetString(), StringComparison.Ordinal);
    }

    private static void AssertCanonicalFormalWorkbookMetadata(string path)
    {
        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet("JET_Metadata");
        Assert.Equal(XLWorksheetVisibility.VeryHidden, sheet.Visibility);
        Assert.Equal("JET_REPORT_METADATA", sheet.Cell("A1").GetString());
        Assert.Equal(1, sheet.Cell("B1").GetValue<int>());

        var chunkCount = sheet.Cell("C1").GetValue<int>();
        Assert.True(chunkCount > 0);
        var json = string.Concat(Enumerable.Range(0, chunkCount)
            .Select(index => sheet.Cell(index + 2, 2).GetString()));
        using var metadata = JsonDocument.Parse(json);
        var root = metadata.RootElement;
        Assert.Equal(
            ["formatVersion", "approvalDateMode", "populationPolicy", "taxonomyRevision", "mapping"],
            root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(1, root.GetProperty("formatVersion").GetInt32());
        Assert.Equal("mapped", root.GetProperty("approvalDateMode").GetString());

        var population = root.GetProperty("populationPolicy");
        Assert.Equal(
            ["periodStart", "periodEnd", "postingStatus"],
            population.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("2025-01-01", population.GetProperty("periodStart").GetString());
        Assert.Equal("2025-12-31", population.GetProperty("periodEnd").GetString());
        var postingStatus = population.GetProperty("postingStatus");
        Assert.False(postingStatus.GetProperty("isMapped").GetBoolean());
        Assert.Empty(postingStatus.GetProperty("acceptedValues").EnumerateArray());
        Assert.False(postingStatus.GetProperty("includeBlank").GetBoolean());

        Assert.True(root.GetProperty("taxonomyRevision").GetInt32() >= 1);
        var mapping = root.GetProperty("mapping");
        Assert.Equal(MappingMetadataFormat.CurrentVersion, mapping.GetProperty("formatVersion").GetInt32());
        Assert.Equal("gl", mapping.GetProperty("gl").GetProperty("kind").GetString());
        Assert.Equal("tb", mapping.GetProperty("tb").GetProperty("kind").GetString());
    }

    private static void AssertDraftMatchesCommitted(JsonElement loadedProject, JsonElement restored)
    {
        var committed = loadedProject.GetProperty("mapping");
        Assert.Equal(
            committed.GetProperty("gl").GetProperty("mapping").GetRawText(),
            restored.GetProperty("gl").GetProperty("mapping").GetRawText());
        Assert.Equal(
            committed.GetProperty("gl").GetProperty("amountMode").GetString(),
            restored.GetProperty("gl").GetProperty("amountMode").GetString());
        Assert.Equal(
            committed.GetProperty("tb").GetProperty("mapping").GetRawText(),
            restored.GetProperty("tb").GetProperty("mapping").GetRawText());
        Assert.Equal(
            committed.GetProperty("tb").GetProperty("changeMode").GetString(),
            restored.GetProperty("tb").GetProperty("changeMode").GetString());
    }

    private static void ExportEvidenceIfRequested(
        string projectId,
        JsonElement[] artifacts,
        string projectDirectory)
    {
        var receiptDirectory = Environment.GetEnvironmentVariable("JET_SIX_REPORT_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(receiptDirectory))
        {
            return;
        }

        if (!Path.IsPathFullyQualified(receiptDirectory))
        {
            throw new InvalidOperationException(
                "JET_SIX_REPORT_EVIDENCE_DIR must be an absolute path.");
        }

        var workbookDirectory = Path.Combine(receiptDirectory, "workbooks");
        if (Directory.Exists(workbookDirectory) || File.Exists(workbookDirectory))
        {
            throw new InvalidOperationException(
                $"Evidence workbook directory already exists: {workbookDirectory}");
        }

        Directory.CreateDirectory(workbookDirectory);
        var evidence = artifacts
            .OrderBy(artifact => artifact.GetProperty("kind").GetString(), StringComparer.Ordinal)
            .Select(artifact =>
            {
                var kind = artifact.GetProperty("kind").GetString()!;
                var fileName = artifact.GetProperty("fileName").GetString()!;
                var sourcePath = Path.Combine(projectDirectory, fileName);
                var evidenceFileName = $"{kind}.xlsx";
                var evidencePath = Path.Combine(workbookDirectory, evidenceFileName);
                File.Copy(sourcePath, evidencePath);

                using var stream = File.OpenRead(evidencePath);
                var sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                using var workbook = new XLWorkbook(evidencePath);
                return new
                {
                    kind,
                    sourceFileName = fileName,
                    evidenceFileName,
                    bytes = stream.Length,
                    sha256,
                    sheets = workbook.Worksheets.Select(sheet => sheet.Name).ToArray()
                };
            })
            .ToArray();

        Directory.CreateDirectory(receiptDirectory);
        File.WriteAllText(
            Path.Combine(receiptDirectory, "evidence-manifest.json"),
            JsonSerializer.Serialize(
                new
                {
                    generatedAtUtc = DateTimeOffset.UtcNow,
                    projectId,
                    artifacts = evidence
                },
                new JsonSerializerOptions { WriteIndented = true }));
    }
}
