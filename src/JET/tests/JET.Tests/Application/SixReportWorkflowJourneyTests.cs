using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 五份報告與科目配對工作檔的完整旅程：驗證、填回範本原檔、重新匯出驗證報告，再完成篩選與底稿。
/// 類別名稱與 JET_SIX_REPORT_EVIDENCE_DIR 為既有篩選及證據介面保留；六份工作簿只有五份進報告清單。
/// 全程使用自含合成資料，不讀外部範本或真實帳務資料。
/// </summary>
public sealed class SixReportWorkflowJourneyTests
{
    [Fact]
    public async Task AccountMappingHandoff_PreservesValidationRun_AndPublishesFiveReportsAndTemplate()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(host,
            builder => builder.WithColumns("傳票號碼", "傳票日期", "核准日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
                .AddRow("JV-001","2025-03-05","2026-01-02","1101","現金","調整分錄","2000000.00",1)
                .AddRow("JV-001","2025-03-05","2026-01-02","4101","收入",null,"2000000.00",0)
                .AddRow("JV-002","2025-06-06","2026-01-02","1101","現金","調整分錄","100.00",1)
                .AddRow("JV-002","2025-06-06","2026-01-02","5101","待分類科目",null,"100.00",0),
            lastPeriodStart: "2025-12-31", configureTb: tb => tb.AddRow("1101","現金",2_000_100)
                .AddRow("4101","收入",-2_000_000).AddRow("5101","待分類科目",-100));

        var validation = await host.DispatchAsync("validate.run");
        var validationRunId = validation.GetProperty("resultRef").GetProperty("runId").GetString()!;
        var validationBatch = await host.DispatchAsync(
            "export.validationArtifacts",
            JsonSerializer.Serialize(new { runId = validationRunId }));

        Assert.Equal(2, validationBatch.GetProperty("artifacts").GetArrayLength());
        var projectDirectory = Path.Combine(host.ProjectsRoot, projectId);
        // 科目配對範本是工作檔，由第四步卡片單獨產生，不在驗證報告批次裡。
        var template = await host.DispatchAsync(
            "export.accountMappingTemplate",
            JsonSerializer.Serialize(new { runId = validationRunId }));
        var mappingPath = template.GetProperty("filePath").GetString()!;
        using (var workbook = new XLWorkbook(mappingPath))
        {
            var sheet = workbook.Worksheet("AccountMapping");
            var lastRow = sheet.LastRowUsed()!.RowNumber();
            for (var row = 4; row <= lastRow; row++)
            {
                sheet.Cell(row, 3).Value = sheet.Cell(row,1).GetString() switch
                { "1101" => "Cash", "4101" => "Revenue", _ => "" };
            }
            workbook.Save();
        }
        var filledTemplate = await File.ReadAllBytesAsync(mappingPath);
        await host.DispatchAsync(
            "import.accountMapping.fromFile",
            JsonSerializer.Serialize(new { filePath = mappingPath }));
        await host.DispatchAsync("project.releaseLock");

        var afterMapping = await host.DispatchAsync(
            "project.load",
            JsonSerializer.Serialize(new { projectId }));
        Assert.Equal(
            validationRunId,
            afterMapping.GetProperty("latestRuns").GetProperty("validate")
                .GetProperty("resultRef").GetProperty("runId").GetString());
        Assert.Equal(JsonValueKind.Null, afterMapping.GetProperty("latestRuns").GetProperty("prescreen").ValueKind);

        var regenerated = await host.DispatchAsync(
            "export.validationArtifacts", JsonSerializer.Serialize(new { runId = validationRunId }));
        Assert.Equal(new[] { "infReport", "validationReport" }, regenerated.GetProperty("artifacts").EnumerateArray()
            .Select(artifact => artifact.GetProperty("kind").GetString()).Order(StringComparer.Ordinal));
        Assert.Equal(filledTemplate, await File.ReadAllBytesAsync(mappingPath));
        Assert.DoesNotContain(afterMapping.GetProperty("reportArtifacts").EnumerateArray(),
            artifact => artifact.GetProperty("kind").GetString() == "accountMapping");

        var prescreen = await host.DispatchAsync("prescreen.run");
        var prescreenRunId = prescreen.GetProperty("resultRef").GetProperty("runId").GetString()!;
        await host.DispatchAsync(
            "export.prescreenReport",
            JsonSerializer.Serialize(new { runId = prescreenRunId }));

        var originalScenarios = JsonDocument.Parse("""
          [{"name":"完整旅程情境","rationale":"以合成摘要條件驗證報告與工作檔流程","groups":[{"rules":[{"type":"customKeywords","keywords":"調整"}]}]},
           {"name":"分類與指定日期","rationale":"分類留白的貸方科目視為 Others，兩張都命中","groups":[
             {"matchScope":"sameVoucher","rules":[
               {"type":"accountSide","drCr":"debit","categoryMode":"is","categoryIds":["builtin.cash"]},
               {"type":"group","rules":[
                 {"type":"accountSide","drCr":"credit","categoryMode":"isNot","categoryIds":["builtin.cash"],"categorySelection":"node"},
                 {"type":"fieldValue","field":"description","operator":"isBlank"}]}]},
             {"join":"AND","rules":[
               {"type":"fieldValue","field":"postDate","operator":"in","values":["2025-03-05","2025-06-06"]},
               {"type":"fieldValue","field":"amount","operator":"between","from":"100","to":"2000000","amountBasis":"absolute"},
               {"type":"fieldValue","field":"description","operator":"notContains","value":"NO_MATCH%_"}]},
             {"join":"AND","rules":[{"type":"voucher","side":"debit","quantifier":"all","rules":[
               {"type":"accountSide","drCr":"debit","categoryMode":"is","categoryIds":["builtin.cash"],"categorySelection":"subtree"}]}]}]}]
          """).RootElement;
        // Keep both existing independent answers; add A and D from the production entry catalogue
        // so the native Excel round-trip also exercises the formerly omitted prescreen descriptions.
        var scenarios = JsonNode.Parse(originalScenarios.GetRawText())!.AsArray();
        var legacy = Architecture.LegacyFormCatalogTests.ReadCatalog()["conditions"]!.AsArray();
        foreach (var letter in new[] { "A", "D" })
        {
            var item = legacy.Single(item => item!["letter"]!.GetValue<string>() == letter)!;
            var scenario = new JsonObject
            {
                ["name"] = "舊表 " + letter, ["rationale"] = "合成案例驗證原生 Excel 條件說明",
                ["groups"] = new JsonArray(new JsonObject { ["rules"] = item["rules"]!.DeepClone() })
            };
            var preview = await host.DispatchAsync("filter.preview", new JsonObject { ["scenario"] = scenario.DeepClone() }.ToJsonString());
            Assert.Equal(letter == "A" ? 4 : 2, preview.GetProperty("scenario").GetProperty("count").GetInt64());
            scenarios.Add(scenario);
        }
        var selectionPreview = await host.DispatchAsync("filter.preview",JsonSerializer.Serialize(new { scenario = scenarios[1] }));
        Assert.Equal(0, await DemoProjectPipeline.QueryScalarAsync(host, projectId,
            "SELECT classification_explicit FROM target_account_mapping WHERE account_code='5101';"));
        // 兩值語意：5101 分類留白視為 Others，原本「等待補分類」的那張傳票直接命中。
        Assert.Equal(2,selectionPreview.GetProperty("scenario").GetProperty("count").GetInt64());
        Assert.Equal(2,selectionPreview.GetProperty("scenario").GetProperty("voucherCount").GetInt64());
        var committed = await host.DispatchAsync("filter.commit",JsonSerializer.Serialize(new { scenarios }));
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
                scenarioPositions = new[] { 1, 2, 3, 4 }
            }));

        var completed = await host.DispatchAsync(
            "project.load",
            JsonSerializer.Serialize(new { projectId }));
        var artifacts = completed.GetProperty("reportArtifacts").EnumerateArray().ToArray();
        Assert.Equal(
            new[]
            {
                "criteriaSelectionReport", "infReport",
                "prescreenReport", "validationReport", "workingPaper"
            },
            artifacts.Select(artifact => artifact.GetProperty("kind").GetString())
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.True(File.Exists(mappingPath), "科目配對範本工作檔應留在案件資料夾。");
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
        foreach (var kind in new[] { "criteriaSelectionReport", "workingPaper" })
        {
            using var selectionWorkbook = new XLWorkbook(ArtifactPath(artifacts, projectDirectory, kind));
            var texts = selectionWorkbook.Worksheets.Where(sheet => sheet.Visibility == XLWorksheetVisibility.Visible)
                .SelectMany(sheet => sheet.CellsUsed()).Select(cell => cell.GetFormattedString()).ToArray();
            Assert.DoesNotContain(texts, text => text.Contains("待判定", StringComparison.Ordinal));
            Assert.Contains(texts, text => text.Contains("2025-03-05", StringComparison.Ordinal) && text.Contains("2025-06-06", StringComparison.Ordinal));
            Assert.Contains(texts, text => text.Contains("全部符合（至少有一筆）", StringComparison.Ordinal));
            Assert.Contains(texts, text => text.Contains("包含下層分類", StringComparison.Ordinal));
            Assert.Contains(texts, text => text.Contains("財報準備日起核准", StringComparison.Ordinal));
            // 2026-10-04 第 8 批 L59：固定預篩選名稱明示 6 位；仍核對 Criteria 與 WorkingPaper 的實際內容。
            // 第一次失敗：20261004-092023464-13b0a6928d5e400492fbe8a6a24eb69d，確定停在舊名稱斷言。
            Assert.Contains(texts, text => text.Contains("金額尾數連續 6 個 0", StringComparison.Ordinal));
        }
        AssertHiddenMappingMetadata(validationPath);
        AssertHiddenMappingMetadata(workingPaperPath);
        using (var workpaper = new XLWorkbook(workingPaperPath))
        {
            var selected = workpaper.Worksheet(WorkpaperSheetCatalog.Step3).RowsUsed()
                .Single(row => row.Cell(2).GetString() == "C2");
            // 兩值語意：分類留白的貸方視為 Others，C2 命中兩張傳票（與上方預覽的 voucherCount 一致）。
            Assert.Equal(2, selected.Cell(5).GetValue<long>());
        }

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

        await ExportEvidenceIfRequestedAsync(projectId, artifacts, projectDirectory, mappingPath);
        // 2026-09-17 使用者要求：路徑、查核期間與 V_Report 5 可讀性須沿完整旅程核對。
        Assert.All(artifacts, artifact =>
        {
            Assert.Contains("_20250101-20251231_", artifact.GetProperty("fileName").GetString());
            Assert.Equal(Path.Combine(projectDirectory, artifact.GetProperty("fileName").GetString()!),
                artifact.GetProperty("fullPath").GetString());
        });
        using var readableValidation = new XLWorkbook(validationPath);
        Assert.Equal(18D, readableValidation.Worksheet("V_Report 5").RowHeight);
        Assert.All(readableValidation.Worksheet("V_Report 5").RowsUsed(), row => Assert.Equal(18D, row.Height));

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

    private static async Task ExportEvidenceIfRequestedAsync(
        string projectId,
        JsonElement[] artifacts,
        string projectDirectory,
        string accountMappingTemplatePath)
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

        await NativeAccessImportAcceptance.VerifyAsync(receiptDirectory);
        var workbookDirectory = Path.Combine(receiptDirectory, "workbooks");
        if (Directory.Exists(workbookDirectory) || File.Exists(workbookDirectory))
        {
            throw new InvalidOperationException(
                $"Evidence workbook directory already exists: {workbookDirectory}");
        }

        Directory.CreateDirectory(workbookDirectory);
        // Excel 路線仍要開六份工作簿：五份報告加上科目配對範本工作檔，範本以 accountMapping 為種類名。
        var sources = artifacts
            .Select(artifact => (
                Kind: artifact.GetProperty("kind").GetString()!,
                FileName: artifact.GetProperty("fileName").GetString()!,
                SourcePath: Path.Combine(projectDirectory, artifact.GetProperty("fileName").GetString()!)))
            .Append((
                Kind: "accountMapping",
                FileName: Path.GetFileName(accountMappingTemplatePath),
                SourcePath: accountMappingTemplatePath))
            .OrderBy(source => source.Kind, StringComparer.Ordinal);
        var evidence = sources
            .Select(source =>
            {
                var kind = source.Kind;
                var fileName = source.FileName;
                var sourcePath = source.SourcePath;
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
