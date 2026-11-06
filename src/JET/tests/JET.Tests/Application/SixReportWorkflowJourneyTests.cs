using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

// 舊式關鍵字條件停用後，合成情境改用欄位關鍵字，原命中與報表斷言保留；首次失敗：20261014-135805134-9060396119bc4e8f8dae2eecd29f1776。

/// <summary>
/// 五份報告與科目配對工作檔的完整旅程：驗證、填回範本原檔、重新匯出驗證報告，再完成篩選與底稿。
/// 類別名稱與 JET_SIX_REPORT_EVIDENCE_DIR 為既有篩選及證據介面保留；六份工作簿只有五份進報告清單。
/// 全程使用自含合成資料，不讀外部範本或真實帳務資料。
/// </summary>
public sealed class SixReportWorkflowJourneyTests
{
    [Fact]
    public async Task AccountMappingHandoff_PreservesValidationResult_AndPublishesFiveReportsAndTemplate()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(host,
            builder => builder.WithColumns("傳票號碼", "傳票日期", "核准日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
                .AddRow("JV-001","2025-03-05","2026-01-02","1101","現金","調整分錄","2000000.00",1)
                .AddRow("JV-001","2025-03-05","2026-01-02","4101","收入",null,"2000000.00",0)
                .AddRow("JV-002","2025-06-06","2026-01-02","1101","現金","調整分錄","100.00",1)
                .AddRow("JV-002","2025-06-06","2026-01-02","5101","待分類科目",null,"100.00",0),
            prepStartDate: "2025-12-31", configureTb: tb => tb.AddRow("1101","現金",2_000_100)
                .AddRow("4101","收入",-2_000_000).AddRow("5101","待分類科目",-100));

        var validation = await host.DispatchAsync("validate.run");
        var validationResultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString()!;
        var validationBatch = await host.DispatchAsync(
            "export.validationReports",
            JsonSerializer.Serialize(new { resultId = validationResultId }));

        Assert.Equal(2, validationBatch.GetProperty("publishedReports").GetArrayLength());
        var projectDirectory = Path.Combine(host.ProjectsRoot, projectId);
        // 科目配對範本是工作檔，由第四步卡片單獨產生，不在驗證報告批次裡。
        var template = await host.DispatchAsync(
            "export.accountMappingTemplate",
            JsonSerializer.Serialize(new { resultId = validationResultId }));
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
            validationResultId,
            afterMapping.GetProperty("validationResult")
                .GetProperty("resultRef").GetProperty("resultId").GetString());
        Assert.Equal(JsonValueKind.Null, afterMapping.GetProperty("prescreenResult").ValueKind);

        var regenerated = await host.DispatchAsync(
            "export.validationReports", JsonSerializer.Serialize(new { resultId = validationResultId }));
        Assert.Equal(new[] { "infReport", "validationReport" }, regenerated.GetProperty("publishedReports").EnumerateArray()
            .Select(report => report.GetProperty("kind").GetString()).Order(StringComparer.Ordinal));
        Assert.Equal(filledTemplate, await File.ReadAllBytesAsync(mappingPath));
        Assert.DoesNotContain(afterMapping.GetProperty("reports").EnumerateArray(),
            report => report.GetProperty("kind").GetString() == "accountMapping");

        var prescreen = await host.DispatchAsync("prescreen.run");
        var prescreenResultId = prescreen.GetProperty("resultRef").GetProperty("resultId").GetString()!;
        await host.DispatchAsync(
            "export.prescreenReport",
            JsonSerializer.Serialize(new { resultId = prescreenResultId }));

        var originalScenarios = JsonDocument.Parse("""
          [{"name":"完整旅程情境","rationale":"以合成摘要條件驗證報告與工作檔流程","groups":[{"conditions":[{"type":"fieldValue","field":"description","operator":"contains","values":["調整"]}]}]},
           {"name":"分類與指定日期","rationale":"分類留白的貸方科目視為 Others，兩張都命中","groups":[
             {"conditions":[
               {"type":"group","conditions":[{"type":"drCrOnly","drCr":"debit"},{"type":"accountSide","categoryMode":"is","categoryIds":["builtin.cash"]}]},
               {"type":"voucher","quantifier":"any","conditions":[{"type":"drCrOnly","drCr":"credit"},
                 {"type":"accountSide","categoryMode":"isNot","categoryIds":["builtin.cash"],"categorySelection":"node"},
                 {"type":"fieldValue","field":"description","operator":"isBlank"}]}]},
             {"join":"AND","conditions":[
               {"type":"fieldValue","field":"glDate","operator":"in","values":["2025-03-05","2025-06-06"]},
               {"type":"fieldValue","field":"amount","operator":"between","from":"100","to":"2000000","amountBasis":"absolute"},
               {"type":"fieldValue","field":"description","operator":"notContains","value":"NO_MATCH%_"}]},
             {"join":"AND","conditions":[{"type":"voucher","quantifier":"any","conditions":[{"type":"drCrOnly","drCr":"debit"}]},
               {"type":"voucher","quantifier":"all","conditions":[{"type":"drCrOnly","drCr":"credit"},
                 {"type":"accountSide","join":"OR","categoryMode":"is","categoryIds":["builtin.cash"],"categorySelection":"subtree"}]}]}]}]
          """).RootElement;
        // Keep both existing independent answers; add A and D from the retired entry catalogue
        // so the native Excel round-trip also exercises the formerly omitted prescreen descriptions.
        // 裁定 2026-10-08-06 重新設計了自訂篩選條件：目錄改放在測試夾具，條件內容不變，固定答案照舊。
        var scenarios = JsonNode.Parse(originalScenarios.GetRawText())!.AsArray();
        var legacy = Architecture.LegacyFormCatalogTests.ReadCatalog()["conditions"]!.AsArray();
        foreach (var letter in new[] { "A", "D" })
        {
            var item = legacy.Single(item => item!["letter"]!.GetValue<string>() == letter)!;
            var scenario = new JsonObject
            {
                ["name"] = "舊表 " + letter, ["rationale"] = "合成案例驗證原生 Excel 條件說明",
                ["groups"] = new JsonArray(new JsonObject { ["conditions"] = item["conditions"]!.DeepClone() })
            };
            var preview = await host.DispatchAsync("filter.preview", new JsonObject { ["scenario"] = scenario.DeepClone() }.ToJsonString());
            Assert.Equal(letter == "A" ? 4 : 2, preview.GetProperty("scenario").GetProperty("count").GetInt64());
            scenarios.Add(scenario);
        }
        var selectionPreview = await host.DispatchAsync("filter.preview",JsonSerializer.Serialize(new { scenario = scenarios[1] }));
        Assert.Equal(0, await DemoProjectPipeline.QueryScalarAsync(host, projectId,
            "SELECT classification_explicit FROM account_mapping WHERE account_number='5101';"));
        // 兩值語意：5101 分類留白視為 Others，原本「等待補分類」的那張傳票直接命中。
        Assert.Equal(2,selectionPreview.GetProperty("scenario").GetProperty("count").GetInt64());
        Assert.Equal(2,selectionPreview.GetProperty("scenario").GetProperty("voucherCount").GetInt64());
        var committed = await host.DispatchAsync("filter.commit",JsonSerializer.Serialize(new { scenarios }));
        var revision = committed.GetProperty("resultRef").GetProperty("revision").GetString()!;

        await host.DispatchAsync(
            "export.criteriaSelectionReport",
            JsonSerializer.Serialize(new { validationResultId, prescreenResultId, revision }));
        await host.DispatchAsync(
            "export.workingPaperStream",
            JsonSerializer.Serialize(new
            {
                validationResultId,
                prescreenResultId,
                scenarioRevision = revision,
                scenarioPositions = new[] { 1, 2, 3, 4 }
            }));

        var completed = await host.DispatchAsync(
            "project.load",
            JsonSerializer.Serialize(new { projectId }));
        var reports = completed.GetProperty("reports").EnumerateArray().ToArray();
        Assert.Equal(
            new[]
            {
                "criteriaSelectionReport", "infReport",
                "prescreenReport", "validationReport", "workingPaper"
            },
            reports.Select(report => report.GetProperty("kind").GetString())
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.True(File.Exists(mappingPath), "科目配對範本工作檔應留在案件資料夾。");
        Assert.All(reports, report => Assert.False(report.GetProperty("stale").GetBoolean()));
        Assert.All(reports, report =>
        {
            var fileName = report.GetProperty("fileName").GetString()!;
            Assert.Equal(Path.GetFileName(fileName), fileName);
            var reportPath = Path.Combine(projectDirectory, fileName);
            Assert.True(File.Exists(reportPath));
            AssertCanonicalFormalWorkbookMetadata(reportPath);
        });

        var validationPath = ReportPath(reports, projectDirectory, "validationReport");
        var workingPaperPath = ReportPath(reports, projectDirectory, "workingPaper");
        foreach (var kind in new[] { "criteriaSelectionReport", "workingPaper" })
        {
            using var selectionWorkbook = new XLWorkbook(ReportPath(reports, projectDirectory, kind));
            var texts = selectionWorkbook.Worksheets.Where(sheet => sheet.Visibility == XLWorksheetVisibility.Visible)
                .SelectMany(sheet => sheet.CellsUsed()).Select(cell => cell.GetFormattedString()).ToArray();
            Assert.DoesNotContain(texts, text => text.Contains("待判定", StringComparison.Ordinal));
            Assert.Contains(texts, text => text.Contains("2025-03-05", StringComparison.Ordinal) && text.Contains("2025-06-06", StringComparison.Ordinal));
            Assert.Contains(texts, text => text.Contains("全部符合（至少有一筆）", StringComparison.Ordinal));
            // 裁定 2026-11-03-27 起，選的分類都沒有下層時讀回不寫選取依據；這個案件的現金沒有下層分類，報告不再寫「包含下層分類」。
            // 原本核對報告寫出包含下層分類，改核對同一份報告仍寫出會納入其他分類的「相同分類用途」，並確認不寫包含下層分類。
            // 第一次失敗：Focused 收據 20261103-102428077-8a3e24e015e24d57835a2b136ff8e486。
            Assert.Contains(texts, text => text.Contains("相同分類用途", StringComparison.Ordinal));
            Assert.DoesNotContain(texts, text => text.Contains("包含下層分類", StringComparison.Ordinal));
            Assert.Contains(texts, text => text.Contains("財報準備日起核准", StringComparison.Ordinal));
            // 固定預篩選名稱明示 6 位；仍核對 Criteria 與 WorkingPaper 的實際內容。
            // 第一次失敗：20261004-092023464-13b0a6928d5e400492fbe8a6a24eb69d，確定停在舊名稱斷言。
            // 0 的個數改照 IDEA 版由 GL 算出（裁定 2026-11-05-25）：借方 2,000,000 與 100 合計 2,000,100，除以 4 列，平均 500,025 是 6 位數，
            // 所以是 5 個 0。第一次失敗：Public 收據 20261105-084802022-bb026abaa79c492799abea536c518413。
            Assert.Contains(texts, text => text.Contains("金額尾數連續 5 個 0", StringComparison.Ordinal));
        }
        AssertHiddenMappingMetadata(validationPath);
        AssertHiddenMappingMetadata(workingPaperPath);
        using (var workingPaper = new XLWorkbook(workingPaperPath))
        {
            var selected = workingPaper.Worksheet(WorkingPaperSheetCatalog.Step3).RowsUsed()
                .Single(row => row.Cell(2).GetString() == "C2");
            // 兩值語意：分類留白的貸方視為 Others，C2 命中兩張傳票（與上方預覽的 voucherCount 一致）。
            Assert.Equal(2, selected.Cell(5).GetValue<long>());
        }

        var glRowsBefore = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId, "SELECT COUNT(*) FROM gl;");
        var tbRowsBefore = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId, "SELECT COUNT(*) FROM tb;");

        var fromValidation = await host.DispatchAsync(
            "mapping.restoreDraft",
            JsonSerializer.Serialize(new { filePath = validationPath }));
        var fromWorkingPaper = await host.DispatchAsync(
            "mapping.restoreDraft",
            JsonSerializer.Serialize(new { filePath = workingPaperPath }));

        Assert.Equal(MappingMetadataFormat.CurrentVersion, fromValidation.GetProperty("formatVersion").GetInt32());
        // 回應多了 unmatchedFields，列出報告裡選了、目前資料找不到的來源欄；這份報告和目前資料一致，所以是空的（裁定 2026-11-05-03）。
        // 第一次失敗收據 20261105-025407244-d7215948d9d640c4b0e1c33ae1ea7048（Focused）。
        Assert.Equal(
            ["formatVersion", "gl", "tb", "unmatchedFields"],
            fromValidation.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Empty(fromValidation.GetProperty("unmatchedFields").EnumerateArray());
        // 報告的 GL 配對資訊多寫往下填補的欄位 fillDownFields（裁定 2026-10-22-16）。
        // 第一次失敗收據 20261024-033416453-dac33b94f62345b59d864aac174b51c2（Public）。
        Assert.Equal(
            [
                "mapping", "amountMode", "approvalDateMode", "postingStatusPolicy",
                "manualAutoPolicy", "rdeFields", "fillDownFields"
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
            host, projectId, "SELECT COUNT(*) FROM gl;"));
        Assert.Equal(tbRowsBefore, await DemoProjectPipeline.QueryScalarAsync(
            host, projectId, "SELECT COUNT(*) FROM tb;"));

        await ExportEvidenceIfRequestedAsync(projectId, reports, projectDirectory, mappingPath);
        // 裁定 2026-09-17-05：路徑、查核期間與 V_Report 5 可讀性須沿完整旅程核對。
        Assert.All(reports, report =>
        {
            Assert.Contains("_20250101-20251231_", report.GetProperty("fileName").GetString());
            Assert.Equal(Path.Combine(projectDirectory, report.GetProperty("fileName").GetString()!),
                report.GetProperty("fullPath").GetString());
        });
        // V_Report 5 原本因為字被切到而加高到 18 點。裁定 2026-10-29-08 把所有 10 點明細頁統一
        // 為 15 點，V_Report 5 跟著改；15 點仍不會切到微軟正黑體 10 點。第一次失敗：Public 收據 20261029-032810606-bddbed6a55ca4be090640f8ba8efba67。
        using var readableValidation = new XLWorkbook(validationPath);
        Assert.Equal(15D, readableValidation.Worksheet("V_Report 5").RowHeight);
        Assert.All(readableValidation.Worksheet("V_Report 5").RowsUsed(), row => Assert.Equal(15D, row.Height));

    }

    private static string ReportPath(JsonElement[] reports, string projectDirectory, string kind)
    {
        var fileName = reports.Single(report => report.GetProperty("kind").GetString() == kind)
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
            ["formatVersion", "approvalDateMode", "populationPolicy", "categoryRevision", "mapping"],
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

        Assert.True(root.GetProperty("categoryRevision").GetInt32() >= 1);
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
        JsonElement[] reports,
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
        var sources = reports
            .Select(report => (
                Kind: report.GetProperty("kind").GetString()!,
                FileName: report.GetProperty("fileName").GetString()!,
                SourcePath: Path.Combine(projectDirectory, report.GetProperty("fileName").GetString()!)))
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
