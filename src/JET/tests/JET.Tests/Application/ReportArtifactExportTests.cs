using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 五份正式報告與科目配對範本工作檔的 application 驗收測試。Oracle 來自 action manifest、
/// 六種工作簿的本地格式盤點與一個自含的兩列 GL/TB 母體；不讀取外部參考檔或真實帳務資料。
/// </summary>
public sealed class ReportArtifactExportTests(ReportArtifactExportFixture fixture)
    : IClassFixture<ReportArtifactExportFixture>
{
    private const string CompletenessDiffSheet = "step1-3 完整性測試之差異說明";

    private static readonly string[] EmbeddedTemplateNames =
    [
        "AccountMapping.xlsx",
        "CriteriaSelectionReport.xlsx",
        "INFReport.xlsx",
        "PrescreeningReport.xlsx",
        "ValidationReport.xlsx",
        "WorkingPaper.xlsx",
        "Holiday2025TW.xlsx",
        "MakeUpDay2025TW.xlsx"
    ];

    private static readonly HashSet<string> TemplateHygieneExceptions =
    [
        "INFReport.xlsx",
        "WorkingPaper.xlsx"
    ];

    private static readonly string[] OriginalGlHeaders =
    [
        "傳票號碼", "傳票日期", "核准日期", "科目代號",
        "科目名稱", "摘要", "金額", "借方旗標"
    ];

    [Theory]
    [InlineData("INF", "_", "Report.xlsx")]
    [InlineData("Pre", "-", "screeningReport.xlsx")]
    [InlineData("_Holiday", "_", "2025_TW.xlsx")]
    [InlineData("_MakeUpDay", "_", "2025_TW.xlsx")]
    public async Task ReportTemplateCatalog_PreRenameNames_AreRejected(
        string prefix,
        string separator,
        string suffix)
    {
        var catalog = new ReportTemplateCatalog(Path.GetTempPath());
        using var output = new MemoryStream();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.CopyToAsync(
                string.Concat(prefix, separator, suffix),
                output,
                CancellationToken.None));
    }

    private sealed class ArmableCancelOnValidationFinalizing(
        CancellationTokenSource cancellation) : IJetEventPublisher
    {
        private int _armed;
        private int _cancelled;

        internal List<JsonElement> ValidationEvents { get; } = [];

        internal void Arm()
        {
            Interlocked.Exchange(ref _cancelled, 0);
            Interlocked.Exchange(ref _armed, 1);
        }

        public void Publish(string eventName, object payload)
        {
            if (!string.Equals(eventName, "export.progress", StringComparison.Ordinal))
            {
                return;
            }

            var update = JsonSerializer.SerializeToElement(
                payload,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (!string.Equals(
                    update.GetProperty("artifactKind").GetString(),
                    ReportArtifactKindValues.ValidationReport,
                    StringComparison.Ordinal))
            {
                return;
            }

            ValidationEvents.Add(update);
            if (Volatile.Read(ref _armed) != 0
                && string.Equals(
                    update.GetProperty("phase").GetString(),
                    "finalizingWorkbook",
                    StringComparison.Ordinal)
                && Interlocked.Exchange(ref _cancelled, 1) == 0)
            {
                cancellation.Cancel();
            }
        }
    }

    [Fact]
    public async Task ExportValidationArtifacts_CurrentRun_PublishesAtomicProjectLocalThreeFileBatch()
    {
        using var host = new HandlerTestHost();
        var projectId = await ReportArtifactExportFixture.SetupProjectAsync(host);
        var validation = await host.DispatchAsync("validate.run");
        var runId = validation.GetProperty("resultRef").GetProperty("runId").GetString()!;

        var response = await host.DispatchAsync(
            "export.validationArtifacts",
            JsonSerializer.Serialize(new { runId }));

        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.False(response.TryGetProperty("outputPath", out _));
        Assert.False(response.TryGetProperty("filePath", out _));

        // 2026-09-02 起科目配對範本是工作檔，由 export.accountMappingTemplate 單獨產生；驗證批次只剩兩份。
        var artifacts = response.GetProperty("artifacts");
        Assert.Equal(2, artifacts.GetArrayLength());
        Assert.Equal(
            new[] { "infReport", "validationReport" },
            artifacts.EnumerateArray()
                .Select(artifact => artifact.GetProperty("kind").GetString())
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.All(artifacts.EnumerateArray(), artifact => AssertProjectLocalArtifact(host, projectId, artifact, runId));

        var projectDirectory = Path.Combine(host.ProjectsRoot, projectId);
        Assert.Equal(2, Directory.GetFiles(projectDirectory, "*.xlsx").Length);
        using var manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(projectDirectory, "report-artifacts.json")));
        Assert.Equal(2, manifest.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task ExportValidationArtifacts_CancelAfterFormalValidationWriter_PreservesPriorThreeFileBatch()
    {
        using var cancellation = new CancellationTokenSource();
        var publisher = new ArmableCancelOnValidationFinalizing(cancellation);
        using var host = new HandlerTestHost(eventPublisher: publisher);
        var projectId = await ReportArtifactExportFixture.SetupProjectAsync(host);
        var validation = await host.DispatchAsync("validate.run");
        var runId = validation.GetProperty("resultRef").GetProperty("runId").GetString()!;
        var payload = JsonSerializer.Serialize(new { runId });
        var initial = await host.DispatchAsync("export.validationArtifacts", payload);

        var projectDirectory = Path.Combine(host.ProjectsRoot, projectId);
        var manifestPath = Path.Combine(
            projectDirectory,
            ProjectReportArtifactStore.ManifestFileName);
        var store = new ProjectReportArtifactStore(new JetProjectFolder(host.ProjectsRoot));
        var priorArtifacts = await store.ListAsync(projectId, CancellationToken.None);
        Assert.Equal(2, priorArtifacts.Count);
        Assert.Equal(
            new[]
            {
                ReportArtifactKind.ValidationReport,
                ReportArtifactKind.InfReport
            },
            priorArtifacts.Select(artifact => artifact.Kind).ToArray());
        var priorWorkbookPaths = Directory.GetFiles(projectDirectory, "*.xlsx")
            .Select(Path.GetFullPath)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, priorWorkbookPaths.Length);
        var priorWorkbookBytes = priorWorkbookPaths.ToDictionary(
            path => path,
            File.ReadAllBytes,
            StringComparer.OrdinalIgnoreCase);
        var priorManifestBytes = await File.ReadAllBytesAsync(
            manifestPath,
            CancellationToken.None);

        var validationPath = Path.Combine(
            projectDirectory,
            FindArtifact(initial, ReportArtifactKindValues.ValidationReport)
                .GetProperty("fileName")
                .GetString()!);
        using (var workbook = new XLWorkbook(validationPath))
        {
            Assert.Contains("Source_Quality", workbook.Worksheets.Select(sheet => sheet.Name));
            Assert.Equal(
                XLWorksheetVisibility.VeryHidden,
                workbook.Worksheet(ReportWorkbookMetadataFormat.WorksheetName).Visibility);
        }

        var eventStart = publisher.ValidationEvents.Count;
        publisher.Arm();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.DispatchAsync(
                "export.validationArtifacts",
                payload,
                cancellation.Token));

        var currentWorkbookPaths = Directory.GetFiles(projectDirectory, "*.xlsx")
            .Select(Path.GetFullPath)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(priorWorkbookPaths, currentWorkbookPaths);
        foreach (var path in currentWorkbookPaths)
        {
            Assert.Equal(priorWorkbookBytes[path], File.ReadAllBytes(path));
        }
        Assert.Equal(
            priorManifestBytes,
            await File.ReadAllBytesAsync(manifestPath, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(
            projectDirectory,
            "*.tmp",
            SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(
            projectDirectory,
            ProjectReportArtifactStore.JournalFileName)));

        var currentArtifacts = await store.ListAsync(projectId, CancellationToken.None);
        AssertReportArtifactListEqual(priorArtifacts, currentArtifacts);

        var cancelledEvents = publisher.ValidationEvents.Skip(eventStart).ToArray();
        Assert.Contains(
            cancelledEvents,
            update => update.GetProperty("phase").GetString() == "writingSheet"
                      && update.GetProperty("sheetName").GetString() == "Source_Quality");
        Assert.Contains(
            cancelledEvents,
            update => update.GetProperty("phase").GetString() == "finalizingWorkbook");
        Assert.DoesNotContain(
            cancelledEvents,
            update => update.GetProperty("phase").GetString() == "publishingArtifact");
    }

    [Fact]
    public async Task ValidationAndInfReports_ControlledHits_WriteConditionalSheetsAndReliabilityLayout()
    {
        var response = await fixture.ExportValidationArtifactsAsync();
        var validationPath = fixture.ArtifactPath(FindArtifact(response, "validationReport"));
        var infPath = fixture.ArtifactPath(FindArtifact(response, "infReport"));

        using (var workbook = new XLWorkbook(validationPath))
        {
            Assert.Equal(
                new[]
                {
                    "ValidationReport",
                    "自動化工具-檔案欄位資訊",
                    "完整性測試出現差異時之指引",
                    "V_Report 3",
                    "V_Report 4",
                    "V_Report 5",
                    "Source_Quality",
                    ReportWorkbookMetadataFormat.WorksheetName
                },
                workbook.Worksheets.Select(sheet => sheet.Name).ToArray());
            Assert.Equal(
                XLWorksheetVisibility.VeryHidden,
                workbook.Worksheet(ReportWorkbookMetadataFormat.WorksheetName).Visibility);
            Assert.DoesNotContain(CompletenessDiffSheet, workbook.Worksheets.Select(sheet => sheet.Name));
            Assert.Equal(OriginalGlHeaders, ReadRow(workbook.Worksheet("V_Report 3"), 1, OriginalGlHeaders.Length));
            Assert.Equal(OriginalGlHeaders, ReadRow(workbook.Worksheet("V_Report 4"), 1, OriginalGlHeaders.Length));
            Assert.Equal(
                new[] { "ACCOUNT_NUM_ALL", "會計科目名稱_TB", "試算表變動金額_TB", "傳票金額_JE_SUM", "DIFF" },
                ReadRow(workbook.Worksheet("V_Report 5"), 1, 5));
            Assert.Equal(3, workbook.Worksheet("V_Report 5").LastRowUsed()!.RowNumber());

            var summary = workbook.Worksheet("ValidationReport");
            Assert.Equal("Validation Report", summary.Cell("B1").GetString());
            Assert.Equal("Basic Information", summary.Cell("A7").GetString());
            Assert.StartsWith("Client: ", summary.Cell("E1").GetString(), StringComparison.Ordinal);
            Assert.StartsWith("Year End: ", summary.Cell("E2").GetString(), StringComparison.Ordinal);
            Assert.True(summary.Cell("E3").IsEmpty());
            Assert.Equal("Prepared by: tester", summary.Cell("E4").GetString());
            Assert.Equal(Path.GetFileName(summary.Cell("E9").GetString()), summary.Cell("E9").GetString());
            Assert.Equal(0m, summary.Cell("E10").GetValue<decimal>());
            Assert.Equal(2_000_000m, summary.Cell("E11").GetValue<decimal>());
            Assert.Equal(2_000_000m, summary.Cell("E12").GetValue<decimal>());
            Assert.Equal(2, summary.Cell("E13").GetValue<int>());
            Assert.Equal(2, summary.Cell("E15").GetValue<int>());
            Assert.Equal(new[] { 0, 0, 1, 2, 0, 0 },
                summary.Range("D20:D25").Cells().Select(cell => cell.GetValue<int>()).ToArray());
            Assert.Contains("A1:A5", summary.MergedRanges.Select(range => range.RangeAddress.ToString()));
            Assert.Contains("B1:D5", summary.MergedRanges.Select(range => range.RangeAddress.ToString()));
            Assert.Contains("A18:E18", summary.MergedRanges.Select(range => range.RangeAddress.ToString()));
        }

        using (var workbook = new XLWorkbook(infPath))
        {
            Assert.Equal(
                new[]
                {
                    "INF Testing 可靠性測試",
                    "可靠性樣本_所有欄位",
                    ReportWorkbookMetadataFormat.WorksheetName
                },
                workbook.Worksheets.Select(sheet => sheet.Name).ToArray());
            Assert.Equal(
                XLWorksheetVisibility.VeryHidden,
                workbook.Worksheet(ReportWorkbookMetadataFormat.WorksheetName).Visibility);

            var main = workbook.Worksheet("INF Testing 可靠性測試");
            Assert.Equal(
                "證實測試 - 會計分錄及其他調整",
                main.Cell("A3").GetString());
            Assert.StartsWith(
                "1. 依照KAEG-I",
                main.Cell("C6").GetString(),
                StringComparison.Ordinal);
            Assert.Equal("1.", main.Cell("A53").GetString());
            Assert.Equal("2.", main.Cell("A54").GetString());
            Assert.Equal("3.", main.Cell("A55").GetString());
            Assert.Equal("59.", main.Cell("A111").GetString());
            Assert.True(main.Cell("A112").IsEmpty());
            Assert.False(main.Cell("M53").Style.Protection.Locked);
            Assert.False(main.Cell("T111").Style.Protection.Locked);
            Assert.Single(main.DataValidations.GetAllInRange(main.Cell("M53").AsRange().RangeAddress));
            Assert.Single(main.DataValidations.GetAllInRange(main.Cell("S111").AsRange().RangeAddress));
            Assert.Empty(main.DataValidations.GetAllInRange(main.Cell("S112").AsRange().RangeAddress));
            Assert.Contains("A49:A52", main.MergedRanges.Select(range => range.RangeAddress.ToString()));
            Assert.Contains("C49:L49", main.MergedRanges.Select(range => range.RangeAddress.ToString()));
            Assert.Contains("M49:S50", main.MergedRanges.Select(range => range.RangeAddress.ToString()));
            Assert.Contains("T49:T50", main.MergedRanges.Select(range => range.RangeAddress.ToString()));

            var allFields = workbook.Worksheet("可靠性樣本_所有欄位");
            Assert.Equal(OriginalGlHeaders, ReadRow(allFields, 1, OriginalGlHeaders.Length));
            Assert.Equal(3, allFields.LastRowUsed()!.RowNumber());
        }
    }

    [Fact]
    public async Task ValidationReport_BlankPostDate_EmitsFixedSourceQualitySheetWithoutVReport7()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            builder => builder
                .WithColumns(OriginalGlHeaders)
                .AddRow("JV-BLANK", "", "2025-03-06", "1101", "現金", "空白過帳日", "100.00", 1)
                .AddRow("JV-001", "2025-03-05", "2025-03-06", "4101", "收入", "有效列", "100.00", 0),
            configureTb: tb => tb
                .AddRow("1101", "現金", 100)
                .AddRow("4101", "收入", -100));
        var validation = await host.DispatchAsync("validate.run");
        var runId = validation.GetProperty("resultRef").GetProperty("runId").GetString()!;
        var response = await host.DispatchAsync(
            "export.validationArtifacts",
            JsonSerializer.Serialize(new { runId }));
        var artifact = FindArtifact(response, "validationReport");
        var path = Path.Combine(host.ProjectsRoot, projectId, artifact.GetProperty("fileName").GetString()!);

        using var workbook = new XLWorkbook(path);
        Assert.Contains("V_Report 5", workbook.Worksheets.Select(sheet => sheet.Name));
        Assert.DoesNotContain("V_Report 7", workbook.Worksheets.Select(sheet => sheet.Name));
        Assert.Contains("Source_Quality", workbook.Worksheets.Select(sheet => sheet.Name));
        var sourceQuality = workbook.Worksheet("Source_Quality");
        Assert.Equal(
            new[]
            {
                "CATEGORY", "SOURCE_ROW_NUMBER", "SOURCE_LABEL", "DOCUMENT_NUMBER",
                "ACCOUNT_CODE", "POST_DATE", "DESCRIPTION"
            },
            ReadRow(sourceQuality, 1, 7));
        Assert.Equal("nullPostDate", sourceQuality.Cell("A2").GetString());
        Assert.Equal("JV-BLANK", sourceQuality.Cell("D2").GetString());
        var summary = workbook.Worksheet("ValidationReport");
        Assert.True(summary.Cell("A26").IsEmpty());
        Assert.True(summary.Cell("B26").IsEmpty());
        Assert.True(summary.Cell("C26").IsEmpty());
        Assert.True(summary.Cell("D26").IsEmpty());
        Assert.True(summary.Cell("E26").IsEmpty());
    }

    [Fact]
    public async Task InfAndWorkingPaper_UseLegacyDirectionAmountSourceAndApproverColumns()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            builder => builder
                .WithColumns(
                    "傳票號碼",
                    "傳票日期",
                    "核准日期",
                    "科目代號",
                    "科目名稱",
                    "摘要",
                    "來源模組",
                    "建立人員",
                    "核准人員",
                    "金額",
                    "借方旗標")
                .AddRow(
                    "JV-001", "2025-03-05", "2025-03-06", "1101", "現金", "借方列",
                    "SAP-FI", "編製甲", "覆核甲", "100.00", 1)
                .AddRow(
                    "JV-001", "2025-03-05", "2025-03-06", "4101", "收入", "貸方列",
                    "SAP-FI", "編製乙", "覆核乙", "100.00", 0),
            configureTb: tb => tb
                .AddRow("1101", "現金", 100)
                .AddRow("4101", "收入", -100));
        var validation = await host.DispatchAsync("validate.run");
        var runId = validation.GetProperty("resultRef").GetProperty("runId").GetString()!;
        var response = await host.DispatchAsync(
            "export.validationArtifacts",
            JsonSerializer.Serialize(new { runId }));
        var artifact = FindArtifact(response, "infReport");
        var path = Path.Combine(
            host.ProjectsRoot,
            projectId,
            artifact.GetProperty("fileName").GetString()!);

        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet("INF Testing 可靠性測試");
        Assert.Equal("1", sheet.Cell("E53").GetString());
        Assert.Equal(100d, sheet.Cell("F53").GetDouble());
        Assert.Equal("SAP-FI", sheet.Cell("J53").GetString());
        Assert.Equal("覆核甲", sheet.Cell("L53").GetString());
        Assert.Equal("0", sheet.Cell("E54").GetString());
        Assert.Equal(-100d, sheet.Cell("F54").GetDouble());
        Assert.Equal("SAP-FI", sheet.Cell("J54").GetString());
        Assert.Equal("覆核乙", sheet.Cell("L54").GetString());

        var prescreen = await host.DispatchAsync("prescreen.run");
        var committed = await host.DispatchAsync(
            "filter.commit",
            """
            {
              "scenarios": [
                {
                  "name": "借方樣本",
                  "rationale": "驗證底稿 legacy 欄位落點",
                  "groups": [
                    {
                      "join": "AND",
                      "rules": [
                        { "join": "AND", "type": "drCrOnly", "drCr": "debit" }
                      ]
                    }
                  ]
                }
              ]
            }
            """);
        var prescreenRunId = prescreen.GetProperty("resultRef").GetProperty("runId").GetString()!;
        var scenarioRevision = committed.GetProperty("resultRef").GetProperty("revision").GetString()!;
        _ = await host.DispatchAsync(
            "export.criteriaSelectionReport",
            JsonSerializer.Serialize(new
            {
                validationRunId = runId,
                prescreenRunId,
                revision = scenarioRevision
            }));
        var workpaper = await host.DispatchAsync(
            "export.workpaperStream",
            JsonSerializer.Serialize(new
            {
                validationRunId = runId,
                prescreenRunId,
                scenarioRevision,
                scenarioPositions = new[] { 1 }
            }));
        var workpaperPath = Path.Combine(
            host.ProjectsRoot,
            projectId,
            workpaper.GetProperty("artifact").GetProperty("fileName").GetString()!);
        using var workingPaper = new XLWorkbook(workpaperPath);
        var step2 = workingPaper.Worksheet("step2 可靠性測試");
        Assert.Equal("1", step2.Cell("E53").GetString());
        Assert.Equal(100d, step2.Cell("F53").GetDouble());
        Assert.Equal("SAP-FI", step2.Cell("J53").GetString());
        Assert.Equal("覆核甲", step2.Cell("L53").GetString());
        Assert.Equal("傳票核准人員", step2.Cell("L51").GetString());
    }

    [Fact]
    public async Task ValidationReport_CompletenessDiffSheet_EmitsLegacyLayoutAndProtectionOnlyWhenDiffExists()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            builder => builder
                .WithColumns(OriginalGlHeaders)
                .AddRow("JV-001", "2025-03-05", "2025-03-06", "1101", "現金", "借方列", "100.00", 1)
                .AddRow("JV-001", "2025-03-05", "2025-03-06", "4101", "收入", "貸方列", "100.00", 0),
            lastPeriodStart: "2024-01-01",
            configureTb: tb => tb
                .AddRow("1101", "現金", 125)
                .AddRow("4101", "收入", -100));
        var validation = await host.DispatchAsync("validate.run");
        var runId = validation.GetProperty("resultRef").GetProperty("runId").GetString()!;
        var response = await host.DispatchAsync(
            "export.validationArtifacts",
            JsonSerializer.Serialize(new { runId }));
        var artifact = FindArtifact(response, "validationReport");
        var path = Path.Combine(host.ProjectsRoot, projectId, artifact.GetProperty("fileName").GetString()!);

        using (var document = SpreadsheetDocument.Open(path, false))
        {
            Assert.Empty(new OpenXmlValidator().Validate(document));
        }
        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet(CompletenessDiffSheet);

        Assert.Equal("公司名稱 : 測試自含案件", sheet.Cell("A1").GetString());
        Assert.Equal("測試資料期間 :  2025-01-01 ~ 2025-12-31", sheet.Cell("A2").GetString());
        Assert.Equal("財務報表準備期間 - 開始日 : 20240101", sheet.Cell("A3").GetString());
        Assert.Equal(
            new[]
            {
                "試算表科目編號", "試算表科目名稱", "於Step1之差異金額",
                "差異原因之說明",
                "說明如何進行調節以降低該差異\n"
                + "(可參考Validation Report中的指引，於IDEA篩選JE測試母體檔之相關科目傳票明細來確認調節內容。)",
                "調節後之差異金額或剩餘差異之說明"
            },
            ReadRow(sheet, 16, 7).Skip(1).ToArray());

        Assert.Equal("1101", sheet.Cell("B17").GetString());
        Assert.Equal("現金", sheet.Cell("C17").GetString());
        Assert.Equal(25m, sheet.Cell("D17").GetValue<decimal>());
        Assert.True(sheet.Cell("B18").IsEmpty());
        Assert.All(sheet.Range("E17:G17").Cells(), cell => Assert.True(cell.IsEmpty()));

        Assert.True(sheet.Protection.IsProtected);
        Assert.False(sheet.Cell("A1").Style.Protection.Locked);
        Assert.False(sheet.Cell("B16").Style.Protection.Locked);
        Assert.False(sheet.Cell("A17").Style.Protection.Locked);
        Assert.False(sheet.Cell("H17").Style.Protection.Locked);
        Assert.All(sheet.Range("B17:D17").Cells(), cell => Assert.True(cell.Style.Protection.Locked));
        Assert.All(sheet.Range("E17:G17").Cells(), cell => Assert.False(cell.Style.Protection.Locked));
    }

    [Fact]
    public async Task ValidationReport_CompletenessDiffSheet_ContinuesWithRepeatedSkeletonAndExactRowCounts()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            builder => builder
                .WithColumns(OriginalGlHeaders)
                .AddRow("JV-001", "2025-03-05", "2025-03-06", "1101", "現金", "借方一", "100.00", 1)
                .AddRow("JV-001", "2025-03-05", "2025-03-06", "4101", "收入一", "貸方一", "100.00", 0)
                .AddRow("JV-002", "2025-03-06", "2025-03-07", "1201", "應收帳款", "借方二", "50.00", 1)
                .AddRow("JV-002", "2025-03-06", "2025-03-07", "4201", "收入二", "貸方二", "50.00", 0),
            lastPeriodStart: "2024-01-01",
            configureTb: tb => tb
                .AddRow("1101", "現金", 101)
                .AddRow("4101", "收入一", -100)
                .AddRow("1201", "應收帳款", 52)
                .AddRow("4201", "收入二", -53));
        await host.DispatchAsync("validate.run");

        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        var run = Assert.IsType<RuleRunRecord>(await new LocalRuleRunStore(database).FindLatestAsync(
            projectId, RuleRunKinds.Validate, CancellationToken.None));
        var writer = BuildLegacyReportWriter(host, new LegacyReportWriterOptions(18));
        await using var stream = new MemoryStream();
        var stats = await writer.WriteAsync(
            stream,
            new ValidationReportContext(
                new ReportDocumentContext(
                    projectId, "測試自含案件", "2025-01-01", "2025-12-31", "2024-01-01", 10_000),
                run.RunId,
                run.GeneratedUtc,
                run.SummaryJson),
            CancellationToken.None);

        var continuedName = LegacyReportWriter.SeriesName(CompletenessDiffSheet, 2);
        Assert.Equal(
            new long[] { 2, 1 },
            stats.SheetStats
                .Where(item => item.SheetName is CompletenessDiffSheet || item.SheetName == continuedName)
                .Select(item => item.RowsWritten)
                .ToArray());

        stream.Position = 0;
        using (var document = SpreadsheetDocument.Open(stream, false))
        {
            Assert.Empty(new OpenXmlValidator().Validate(document));
        }
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        Assert.Contains(CompletenessDiffSheet, workbook.Worksheets.Select(sheet => sheet.Name));
        Assert.Contains(continuedName, workbook.Worksheets.Select(sheet => sheet.Name));
        Assert.Equal(new[] { "1101", "1201" },
            workbook.Worksheet(CompletenessDiffSheet).Range("B17:B18").Cells().Select(cell => cell.GetString()).ToArray());
        Assert.Equal("4201", workbook.Worksheet(continuedName).Cell("B17").GetString());

        foreach (var name in new[] { CompletenessDiffSheet, continuedName })
        {
            var sheet = workbook.Worksheet(name);
            Assert.Equal("公司名稱 : 測試自含案件", sheet.Cell("A1").GetString());
            Assert.Equal("試算表科目編號", sheet.Cell("B16").GetString());
            Assert.True(sheet.Protection.IsProtected);
            Assert.True(sheet.Cell("B17").Style.Protection.Locked);
            Assert.False(sheet.Cell("E17").Style.Protection.Locked);
        }
    }

    [Fact]
    public async Task ValidationReport_TypedWriter_UsesProjectionWithoutReadingLegacySummaryJson()
    {
        using var host = new HandlerTestHost();
        var projectId = await ReportArtifactExportFixture.SetupProjectAsync(host);
        var writer = BuildLegacyReportWriter(host, new LegacyReportWriterOptions());
        var projection = new ValidationReportProjection(
            Net: 12.34m,
            TotalDebit: 56.78m,
            TotalCredit: 44.44m,
            GlRowCount: 987,
            CompletenessDiffAccountCount: 0,
            UnbalancedDocumentCount: 0,
            NullAccountCount: 0,
            NullDocumentCount: 0,
            NullDescriptionCount: 0,
            OutOfRangeDateCount: 0,
            SourceQualityFindingCount: 0,
            SourceQualitySampleRows: []);
        var context = new ValidationReportContext(
            new ReportDocumentContext(
                projectId,
                "Typed Projection Entity",
                "2025-01-01",
                "2025-12-31",
                "2024-12-31",
                10_000),
            "typed-run",
            new DateTimeOffset(2025, 12, 31, 10, 30, 0, TimeSpan.Zero),
            "{ this is deliberately invalid JSON");
        await using var stream = new MemoryStream();

        var export = await ((ITypedValidationReportWriter)writer).WriteTypedAsync(
            stream,
            context,
            projection,
            "typed-operator",
            CancellationToken.None);

        Assert.True(export.BytesWritten > 0);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var summary = workbook.Worksheet("ValidationReport");
        Assert.Equal(12.34m, summary.Cell("E10").GetValue<decimal>());
        Assert.Equal(56.78m, summary.Cell("E11").GetValue<decimal>());
        Assert.Equal(44.44m, summary.Cell("E12").GetValue<decimal>());
        Assert.Equal(987, summary.Cell("E13").GetValue<int>());
    }

    [Fact]
    public async Task PrescreenReport_TypedWriter_UsesProjectionWithoutReadingLegacySummaryJson()
    {
        using var host = new HandlerTestHost();
        var projectId = await ReportArtifactExportFixture.SetupProjectAsync(host);
        var writer = BuildLegacyReportWriter(host, new LegacyReportWriterOptions());
        var skipped = new PrescreenReportRuleProjection("na", "typed projection skip");
        var projection = new PrescreenReportProjection(
            PostPeriodApproval: skipped,
            SuspiciousKeywords: skipped,
            UnexpectedAccountPair: skipped,
            TrailingZeros: skipped,
            CreatorSummary: new("V", null, 7),
            RareAccounts: new("V", null, 9),
            BlankDescription: skipped);
        var context = new PrescreenReportContext(
            new ReportDocumentContext(
                projectId,
                "Typed Projection Entity",
                "2025-01-01",
                "2025-12-31",
                "2024-12-31",
                10_000),
            "typed-run",
            new DateTimeOffset(2025, 12, 31, 10, 30, 0, TimeSpan.Zero),
            "{ this is deliberately invalid JSON");
        await using var stream = new MemoryStream();

        var export = await ((ITypedPrescreenReportWriter)writer).WriteTypedAsync(
            stream,
            context,
            projection,
            "typed-operator",
            CancellationToken.None);

        Assert.True(export.BytesWritten > 0);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var summary = workbook.Worksheet("Pre-screening_Report");
        Assert.Contains("彙總 7 項", summary.Cell("D12").GetString(), StringComparison.Ordinal);
        Assert.Contains("彙總 9 項", summary.Cell("D13").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrescreenReport_PublicWriter_PreservesSummaryJsonCompatibilityEntryPoint()
    {
        using var host = new HandlerTestHost();
        var projectId = await ReportArtifactExportFixture.SetupProjectAsync(host);
        var writer = BuildLegacyReportWriter(host, new LegacyReportWriterOptions());
        var summaryJson = JsonSerializer.Serialize(new
        {
            postPeriodApproval = new { status = "na", naReason = "compatibility skip" },
            suspiciousKeywords = new { status = "na", naReason = "compatibility skip" },
            unexpectedAccountPair = new { status = "na", naReason = "compatibility skip" },
            trailingZeros = new { status = "na", naReason = "compatibility skip" },
            creatorSummary = new
            {
                status = "V",
                naReason = (string?)null,
                creators = new[] { new { createdBy = "A" }, new { createdBy = "B" } }
            },
            rareAccounts = new
            {
                status = "V",
                distinctAccountCount = 6L,
                accounts = Array.Empty<object>()
            },
            blankDescription = new { status = "na", naReason = "compatibility skip" }
        });
        var context = new PrescreenReportContext(
            new ReportDocumentContext(
                projectId,
                "Public Compatibility Entity",
                "2025-01-01",
                "2025-12-31",
                "2024-12-31",
                10_000),
            "public-run",
            new DateTimeOffset(2025, 12, 31, 10, 30, 0, TimeSpan.Zero),
            summaryJson);
        await using var stream = new MemoryStream();

        var export = await writer.WriteAsync(
            stream,
            context,
            CancellationToken.None);

        Assert.True(export.BytesWritten > 0);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var summary = workbook.Worksheet("Pre-screening_Report");
        Assert.Contains("彙總 2 項", summary.Cell("D12").GetString(), StringComparison.Ordinal);
        Assert.Contains("彙總 6 項", summary.Cell("D13").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidationReport_UnbalancedVoucher_WritesEveryOriginalGlLineNotAggregate()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            builder => builder
                .WithColumns(OriginalGlHeaders)
                .AddRow("JV-UNBALANCED", "2025-03-05", "2025-03-06", "1101", "現金", "借方列", "100.00", 1)
                .AddRow("JV-UNBALANCED", "2025-03-05", "2025-03-06", "4101", "收入", "貸方列", "90.00", 0),
            lastPeriodStart: "2025-12-31",
            configureTb: tb => tb
                .AddRow("1101", "現金", 100)
                .AddRow("4101", "收入", -90));
        var validation = await host.DispatchAsync("validate.run");
        var runId = validation.GetProperty("resultRef").GetProperty("runId").GetString()!;
        var response = await host.DispatchAsync(
            "export.validationArtifacts",
            JsonSerializer.Serialize(new { runId }));
        var artifact = FindArtifact(response, "validationReport");
        var path = Path.Combine(host.ProjectsRoot, projectId, artifact.GetProperty("fileName").GetString()!);

        using var workbook = new XLWorkbook(path);
        var detail = workbook.Worksheet("V_Report 6");
        Assert.Equal(OriginalGlHeaders, ReadRow(detail, 1, OriginalGlHeaders.Length));
        Assert.Equal(3, detail.LastRowUsed()!.RowNumber());
        Assert.Equal(new[] { "JV-UNBALANCED", "JV-UNBALANCED" }, detail.Range("A2:A3").Cells().Select(cell => cell.GetString()).ToArray());
    }

    [Fact]
    public async Task ExportValidationArtifacts_MissingOriginalGlRow_FailsClosedWithoutPartialBatch()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            builder => builder
                .WithColumns(OriginalGlHeaders)
                .AddRow("JV-UNBALANCED", "2025-03-05", "2025-03-06", "1101", "現金", "借方列", "100.00", 1)
                .AddRow("JV-UNBALANCED", "2025-03-05", "2025-03-06", "4101", "收入", "貸方列", "90.00", 0),
            lastPeriodStart: "2025-12-31",
            configureTb: tb => tb
                .AddRow("1101", "現金", 100)
                .AddRow("4101", "收入", -90));
        var validation = await host.DispatchAsync("validate.run");
        var runId = validation.GetProperty("resultRef").GetProperty("runId").GetString()!;

        // 模擬本機 project DB 中目標列仍在，但其 staging 原始列已損壞．
        // 審計報告不得以 entry_id 或空欄靜默代替。
        await DemoProjectPipeline.QueryScalarAsync(
            host,
            projectId,
            """
            DELETE FROM staging_gl_raw_row
            WHERE row_number = (SELECT MIN(source_row_number) FROM target_gl_entry);
            SELECT 0;
            """);

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            host.DispatchAsync(
                "export.validationArtifacts",
                JsonSerializer.Serialize(new { runId })));

        Assert.Equal(JetErrorCodes.StaleResult, exception.Code);
        var projectDirectory = Path.Combine(host.ProjectsRoot, projectId);
        Assert.Empty(Directory.GetFiles(projectDirectory, "*.xlsx"));
        Assert.False(File.Exists(Path.Combine(projectDirectory, "report-artifacts.json")));
    }

    [Fact]
    public async Task ValidationReport_WithoutApprovalDate_WritesLegacyNaAtD23AndOmitsVReport4()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            builder => builder
                .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
                .AddRow("JV-001", "2025-03-05", "1101", "現金", "借方", "100.00", 1)
                .AddRow("JV-001", "2025-03-05", "4101", "收入", "貸方", "100.00", 0),
            configureTb: tb => tb
                .AddRow("1101", "現金", 100)
                .AddRow("4101", "收入", -100));
        var validation = await host.DispatchAsync("validate.run");
        var runId = validation.GetProperty("resultRef").GetProperty("runId").GetString()!;
        var response = await host.DispatchAsync(
            "export.validationArtifacts",
            JsonSerializer.Serialize(new { runId }));
        var artifact = FindArtifact(response, "validationReport");
        var path = Path.Combine(host.ProjectsRoot, projectId, artifact.GetProperty("fileName").GetString()!);

        using var workbook = new XLWorkbook(path);
        var summary = workbook.Worksheet("ValidationReport");
        Assert.Equal("N/A", summary.Cell("D23").GetString());
        Assert.Contains("核准日", summary.Cell("E23").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("V_Report 4", workbook.Worksheets.Select(sheet => sheet.Name));
    }

    [Fact]
    public async Task AccountMappingReport_WritesLegacyHeadersAndContinuousEditableCategoryRange()
    {
        var path = await fixture.ExportAccountMappingTemplatePathAsync();

        using var workbook = new XLWorkbook(path);
        Assert.Equal(
            new[] { "AccountMapping", "List", ReportWorkbookMetadataFormat.WorksheetName },
            workbook.Worksheets.Select(sheet => sheet.Name).ToArray());
        Assert.Equal(
            XLWorksheetVisibility.VeryHidden,
            workbook.Worksheet(ReportWorkbookMetadataFormat.WorksheetName).Visibility);

        var sheet = workbook.Worksheet("AccountMapping");
        Assert.Equal(
            new[] { "GL_Number", "GL_Name", "Standardized Account Name*" },
            ReadRow(sheet, 3, 3));
        Assert.False(sheet.Cell("A4").IsEmpty());
        Assert.False(sheet.Cell("B4").IsEmpty());
        Assert.True(sheet.Cell("C4").IsEmpty());
        Assert.True(sheet.Protection.IsProtected);
        Assert.True(sheet.Cell("A4").Style.Protection.Locked);
        Assert.False(sheet.Cell("C4").Style.Protection.Locked);
        Assert.False(sheet.Cell("C5").Style.Protection.Locked);
        Assert.Single(sheet.DataValidations.GetAllInRange(sheet.Cell("C4").AsRange().RangeAddress));
        Assert.Single(sheet.DataValidations.GetAllInRange(sheet.Cell("C5").AsRange().RangeAddress));
        Assert.Equal(
            AccountMappingCategories.All,
            workbook.Worksheet("List").Column(1).Cells(2, 6).Select(cell => cell.GetString()).ToArray());
    }

    [Fact]
    public async Task ExportPrescreenReport_ControlledHits_WritesSummaryRowsAndHitDetailPages()
    {
        var response = await fixture.ExportPrescreenReportAsync();
        var path = fixture.ArtifactPath(response.GetProperty("artifact"));

        using var workbook = new XLWorkbook(path);
        Assert.Equal(
            new[]
            {
                "Pre-screening_Report", "R1", "R2", "R4", "R5", "R6", "R7",
                ReportWorkbookMetadataFormat.WorksheetName
            },
            workbook.Worksheets.Select(sheet => sheet.Name).ToArray());
        Assert.Equal(
            XLWorksheetVisibility.VeryHidden,
            workbook.Worksheet(ReportWorkbookMetadataFormat.WorksheetName).Visibility);

        var summary = workbook.Worksheet("Pre-screening_Report");
        Assert.Equal(
            "# 1. 於期末財務報表準備期間核准並入到查核年度總帳之分錄",
            summary.Cell("B8").GetString());
        Assert.StartsWith(
            "將篩選期末關帳開始日",
            summary.Cell("C8").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(
            new[] { "A2", "A3", "A4" },
            summary.Range("B14:B16").Cells().Select(cell => cell.GetString()).ToArray());
        Assert.Equal("# 分錄無摘要描述(即空白摘要)", summary.Cell("B17").GetString());
        Assert.Equal("Prepared by: tester", summary.Cell("E4").GetString());
        Assert.Equal(17, summary.Range("A8:F17").LastRow().RowNumber());
        Assert.Equal(1, summary.Cell("E8").GetValue<long>());
        Assert.Equal(2, summary.Cell("F8").GetValue<long>());
        Assert.Equal("N/A", summary.Cell("E12").GetString());
        Assert.Equal("N/A", summary.Cell("F12").GetString());
        Assert.Equal("N/A", summary.Cell("E13").GetString());
        Assert.Equal("N/A", summary.Cell("F13").GetString());
        Assert.StartsWith("N/A", summary.Cell("D10").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("N/A", summary.Cell("D12").GetString(), StringComparison.Ordinal);
        Assert.Contains("彙總", summary.Cell("D13").GetString(), StringComparison.Ordinal);
        Assert.All(summary.Range("D14:D16").Cells(),
            cell => Assert.Contains("Step 4", cell.GetString(), StringComparison.Ordinal));
        Assert.Contains("A1:A5", summary.MergedRanges.Select(range => range.RangeAddress.ToString()));
        Assert.Contains("B1:D5", summary.MergedRanges.Select(range => range.RangeAddress.ToString()));
        Assert.Contains("E5:F5", summary.MergedRanges.Select(range => range.RangeAddress.ToString()));
        Assert.Equal(OriginalGlHeaders, ReadRow(workbook.Worksheet("R2"), 1, OriginalGlHeaders.Length));
        Assert.DoesNotContain("A2", workbook.Worksheets.Select(sheet => sheet.Name));
        Assert.DoesNotContain("A3", workbook.Worksheets.Select(sheet => sheet.Name));
        Assert.DoesNotContain("A4", workbook.Worksheets.Select(sheet => sheet.Name));
    }

    [Fact]
    public async Task ExportCriteriaSelectionReport_CurrentRevision_WritesSummaryAndOriginalFieldDetail()
    {
        var response = await fixture.ExportCriteriaSelectionReportAsync();
        var artifact = response.GetProperty("artifact");
        var path = fixture.ArtifactPath(artifact);

        Assert.Equal(
            fixture.ValidationRunId,
            artifact.GetProperty("sourceRef").GetProperty("validationRunId").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            artifact.GetProperty("sourceRef").GetProperty("prescreenRunId").ValueKind);

        using var workbook = new XLWorkbook(path);
        Assert.Equal(
            new[]
            {
                "Summary Inforamtion",
                "#Criteria Select 1",
                ReportWorkbookMetadataFormat.WorksheetName
            },
            workbook.Worksheets.Select(sheet => sheet.Name).ToArray());
        Assert.Equal(
            XLWorksheetVisibility.VeryHidden,
            workbook.Worksheet(ReportWorkbookMetadataFormat.WorksheetName).Visibility);

        var summary = workbook.Worksheet("Summary Inforamtion");
        Assert.All(summary.Range("A3:D3").Cells(), cell => Assert.True(cell.IsEmpty()));
        Assert.True(summary.Cell("A4").IsEmpty());
        Assert.Equal("條件的內容", summary.Cell("B4").GetString());
        Assert.True(summary.Cell("C4").IsEmpty());
        Assert.True(summary.Cell("D4").IsEmpty());
        Assert.Equal("Prepared by: tester", summary.Cell("D1").GetString());
        Assert.Equal("Criteria Selection 1", summary.Cell("A5").GetString());
        Assert.Contains("調整", summary.Cell("B5").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, summary.Cell("C5").GetValue<long>());
        // 舊 IDEA 的 D 欄是直接符合條件的明細數；整張傳票帶出的其他列不計入。
        Assert.Equal(1, summary.Cell("D5").GetValue<long>());
        Assert.Equal(
            OriginalGlHeaders,
            ReadRow(workbook.Worksheet("#Criteria Select 1"), 1, OriginalGlHeaders.Length));
        Assert.Equal(3, workbook.Worksheet("#Criteria Select 1").LastRowUsed()!.RowNumber());
        Assert.Equal(
            new[] { "JV-001", "JV-001" },
            workbook.Worksheet("#Criteria Select 1")
                .Range("A2:A3")
                .Cells()
                .Select(cell => cell.GetString())
                .ToArray());
    }

    [Fact]
    public async Task CriteriaUsesApprovedConditionText_WhileWorkpaperStep3EndsAtColumnE()
    {
        using var host = new HandlerTestHost();
        var projectId = await ReportArtifactExportFixture.SetupProjectAsync(host);
        var validation = await host.DispatchAsync("validate.run");
        var prescreen = await host.DispatchAsync("prescreen.run");
        var committed = await host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new
            {
                scenarios = new object[]
                {
                    new
                    {
                        name = "核准文字情境",
                        rationale = "鎖定 Phase 6 枚舉式可見差異",
                        groups = new object[]
                        {
                            new
                            {
                                join = "AND",
                                rules = new object[]
                                {
                                    new
                                    {
                                        join = "AND",
                                        type = "customPreparerEntryCount",
                                        maxEntries = "11"
                                    },
                                    new
                                    {
                                        join = "OR",
                                        type = "customAccountEntryCount",
                                        maxEntries = "11"
                                    },
                                    new { join = "AND", type = "drCrOnly", drCr = "debit" }
                                }
                            }
                        }
                    }
                }
            }));

        var validationRunId = validation.GetProperty("resultRef").GetProperty("runId").GetString()!;
        var prescreenRunId = prescreen.GetProperty("resultRef").GetProperty("runId").GetString()!;
        var revision = committed.GetProperty("resultRef").GetProperty("revision").GetString()!;
        var criteria = await host.DispatchAsync(
            "export.criteriaSelectionReport",
            JsonSerializer.Serialize(new
            {
                validationRunId,
                prescreenRunId,
                revision
            }));
        var workpaper = await host.DispatchAsync(
            "export.workpaperStream",
            JsonSerializer.Serialize(new
            {
                validationRunId,
                prescreenRunId,
                scenarioRevision = revision,
                scenarioPositions = new[] { 1 }
            }));

        const string expected =
            "（（所選母體內編製人員張數 ≤ 11 或 所選母體內科目張數 ≤ 11） 且 僅借方）";
        var criteriaPath = Path.Combine(
            host.ProjectsRoot,
            projectId,
            criteria.GetProperty("artifact").GetProperty("fileName").GetString()!);
        var workpaperPath = Path.Combine(
            host.ProjectsRoot,
            projectId,
            workpaper.GetProperty("artifact").GetProperty("fileName").GetString()!);

        using var criteriaWorkbook = new XLWorkbook(criteriaPath);
        using var workpaperWorkbook = new XLWorkbook(workpaperPath);
        Assert.Equal(expected, criteriaWorkbook.Worksheet("Summary Inforamtion").Cell("B5").GetString());
        var step3 = workpaperWorkbook.Worksheet("step3 高風險條件彙總");
        Assert.True(step3.Cell("F18").IsEmpty());
        Assert.True(step3.Cell("F19").IsEmpty());
    }

    [Fact]
    public async Task WorkingPaper_UsesEmbeddedTemplateStaticContentAndFinalLegacyDetailColumns()
    {
        _ = await fixture.ExportCriteriaSelectionReportAsync();
        var response = await fixture.ExportWorkingPaperAsync();
        var path = fixture.ArtifactPath(response.GetProperty("artifact"));

        using var workbook = new XLWorkbook(path);
        Assert.StartsWith(
            "IT相關考量：",
            workbook.Worksheet("JE WorkingPaper說明").Cell("A2").GetString(),
            StringComparison.Ordinal);

        var details = workbook.Worksheet("step4-1 符合高風險條件傳票明細");
        Assert.Equal(
            new[]
            {
                "傳票號碼_JE",
                "傳票文件項次_JE_S",
                "傳票核准日_JE",
                "總帳日期_JE",
                "會計科目編號_JE",
                "會計科目名稱_JE",
                "傳票金額_JE",
                "傳票摘要_JE",
                "C1_TAG"
            },
            Enumerable.Range(1, 9)
                .Select(column => details.Cell(5, column).GetString())
                .ToArray());
        Assert.True(details.Cell("J5").IsEmpty());
        Assert.True(details.Cell("Q5").IsEmpty());
        Assert.Equal(2_000_000d, details.Cell("G6").GetDouble());
        Assert.Equal(-2_000_000d, details.Cell("G7").GetDouble());
        Assert.Equal("Y", details.Cell("I6").GetString());
        Assert.True(details.Cell("I7").IsEmpty());
        Assert.All(
            Enumerable.Range(1, 9),
            column => Assert.InRange(details.Column(column).Width, 7d, 255d));
    }

    [Fact]
    public async Task WorkingPaper_ReferenceSheets_PreserveTemplatePrinterLinksAndCurrentColumns()
    {
        _ = await fixture.ExportCriteriaSelectionReportAsync();
        var response = await fixture.ExportWorkingPaperAsync();
        var outputPath = fixture.ArtifactPath(response.GetProperty("artifact"));
        var templatePath = Path.Combine(
            FindRepositoryRoot(),
            "src", "JET", "JET", "Templates", "WorkingPaper.xlsx");

        using var output = SpreadsheetDocument.Open(outputPath, false);
        using var template = SpreadsheetDocument.Open(templatePath, false);
        foreach (var sheetName in new[]
                 {
                     "自動化工具-檔案欄位資訊",
                     "自動化工具-假期假日資訊",
                     "自動化工具-科目配對資訊"
                 })
        {
            var outputPart = WorksheetPartFor(output, sheetName);
            var templatePart = WorksheetPartFor(template, sheetName);
            var outputSetup = Assert.Single(outputPart.Worksheet.Elements<PageSetup>());
            var templateSetup = Assert.Single(templatePart.Worksheet.Elements<PageSetup>());
            Assert.False(string.IsNullOrWhiteSpace(outputSetup.Id?.Value));
            Assert.Equal(templateSetup.Id?.Value, outputSetup.Id?.Value);
            Assert.Equal(
                ReadPartBytes(templatePart.GetPartById(templateSetup.Id!.Value!)),
                ReadPartBytes(outputPart.GetPartById(outputSetup.Id!.Value!)));
        }

        var fieldColumns = Assert.IsType<Columns>(
            WorksheetPartFor(output, "自動化工具-檔案欄位資訊")
                .Worksheet.GetFirstChild<Columns>());
        Assert.Contains(fieldColumns.Elements<Column>(), column =>
            column.Min?.Value <= 6
            && column.Max?.Value >= 8
            && column.Hidden?.Value == true);
        Assert.NotNull(WorksheetPartFor(output, "自動化工具-假期假日資訊")
            .Worksheet.GetFirstChild<Columns>());
        Assert.NotNull(WorksheetPartFor(output, "自動化工具-科目配對資訊")
            .Worksheet.GetFirstChild<Columns>());
    }

    [Fact]
    public async Task OfficialReportSummarySheets_HaveProvenWorksheetViewPrintAndFixedRowTopology()
    {
        var validation = await fixture.ExportValidationArtifactsAsync();
        var prescreen = await fixture.ExportPrescreenReportAsync();
        var criteria = await fixture.ExportCriteriaSelectionReportAsync();

        var validationPath = fixture.ArtifactPath(FindArtifact(validation, "validationReport"));
        var infPath = fixture.ArtifactPath(FindArtifact(validation, "infReport"));
        var prescreenPath = fixture.ArtifactPath(prescreen.GetProperty("artifact"));
        var criteriaPath = fixture.ArtifactPath(criteria.GetProperty("artifact"));

        using (var document = SpreadsheetDocument.Open(validationPath, false))
        {
            var worksheet = ReadWorksheet(document, "ValidationReport");
            Assert.Equal(90U, worksheet.Descendants<SheetView>().Single().ZoomScale?.Value);
            Assert.Equal(OrientationValues.Portrait, worksheet.GetFirstChild<PageSetup>()?.Orientation?.Value);
            Assert.Equal(9U, worksheet.GetFirstChild<PageSetup>()?.PaperSize?.Value);
            Assert.NotNull(worksheet.GetFirstChild<PageMargins>());
            Assert.Equal(5.42578125, ColumnWidth(worksheet, 1), 6);
            Assert.Equal(32.28515625, ColumnWidth(worksheet, 5), 6);
            Assert.Equal(25U, worksheet.GetFirstChild<SheetData>()!.Elements<Row>().Last().RowIndex?.Value);
            Assert.Empty(new OpenXmlValidator().Validate(document));
        }

        using (var document = SpreadsheetDocument.Open(infPath, false))
        {
            var worksheet = ReadWorksheet(document, "INF Testing 可靠性測試");
            Assert.Equal(80U, worksheet.Descendants<SheetView>().Single().ZoomScale?.Value);
            var setup = Assert.Single(worksheet.Elements<PageSetup>());
            Assert.Equal(OrientationValues.Landscape, setup.Orientation?.Value);
            Assert.Equal(38U, setup.Scale?.Value);
            Assert.Equal(6.08984375, ColumnWidth(worksheet, 1), 6);
            Assert.True(ColumnWidth(worksheet, 6) >= 15D);
            Assert.Equal(22.453125D, ColumnWidth(worksheet, 20), 6);
            Assert.Equal(55.5D, worksheet.GetFirstChild<SheetData>()!.Elements<Row>()
                .Single(row => row.RowIndex?.Value == 52).Height!.Value, 6);
            Assert.Null(worksheet.GetFirstChild<SheetData>()!.Elements<Row>()
                .Single(row => row.RowIndex?.Value == 53).Height?.Value);
            Assert.Equal(111U, worksheet.GetFirstChild<SheetData>()!.Elements<Row>().Last().RowIndex?.Value);
            Assert.Empty(new OpenXmlValidator().Validate(document));
        }

        using (var document = SpreadsheetDocument.Open(prescreenPath, false))
        {
            var worksheet = ReadWorksheet(document, "Pre-screening_Report");
            Assert.Equal(90U, worksheet.Descendants<SheetView>().Single().ZoomScale?.Value);
            Assert.Equal(71.140625, ColumnWidth(worksheet, 2), 6);
            Assert.Equal(26.1D, worksheet.GetFirstChild<SheetData>()!.Elements<Row>()
                .Single(row => row.RowIndex?.Value == 8).Height?.Value);
            Assert.Equal(17U, worksheet.GetFirstChild<SheetData>()!.Elements<Row>().Last().RowIndex?.Value);
            Assert.Empty(new OpenXmlValidator().Validate(document));
        }

        using (var document = SpreadsheetDocument.Open(criteriaPath, false))
        {
            var worksheet = ReadWorksheet(document, "Summary Inforamtion");
            Assert.Equal(
                Enumerable.Range(5, 10).Select(value => (uint)value).ToArray(),
                worksheet.GetFirstChild<SheetData>()!.Elements<Row>()
                    .Where(row => row.RowIndex?.Value is >= 5 and <= 14)
                    .Select(row => row.RowIndex!.Value).ToArray());
            Assert.Equal(OrientationValues.Portrait, worksheet.GetFirstChild<PageSetup>()?.Orientation?.Value);
            Assert.Equal(95D, ColumnWidth(worksheet, 2), 6);
            Assert.Empty(new OpenXmlValidator().Validate(document));
        }
    }

    [Fact]
    public async Task EveryOfficialReport_PreservesTemplateOwnedPackagePartsWithoutAddingExecutableContent()
    {
        var validation = await fixture.ExportValidationArtifactsAsync();
        var prescreen = await fixture.ExportPrescreenReportAsync();
        var criteria = await fixture.ExportCriteriaSelectionReportAsync();
        var workpaper = await fixture.ExportWorkingPaperAsync();

        var reportTemplates = new[]
        {
            (
                OutputPath: await fixture.ExportAccountMappingTemplatePathAsync(),
                TemplateName: "AccountMapping.xlsx",
                AllowCoverEmbeddedDocumentRemoval: false),
            (
                OutputPath: fixture.ArtifactPath(FindArtifact(validation, "infReport")),
                TemplateName: "INFReport.xlsx",
                AllowCoverEmbeddedDocumentRemoval: false),
            (
                OutputPath: fixture.ArtifactPath(FindArtifact(validation, "validationReport")),
                TemplateName: "ValidationReport.xlsx",
                AllowCoverEmbeddedDocumentRemoval: false),
            (
                OutputPath: fixture.ArtifactPath(prescreen.GetProperty("artifact")),
                TemplateName: "PrescreeningReport.xlsx",
                AllowCoverEmbeddedDocumentRemoval: false),
            (
                OutputPath: fixture.ArtifactPath(criteria.GetProperty("artifact")),
                TemplateName: "CriteriaSelectionReport.xlsx",
                AllowCoverEmbeddedDocumentRemoval: false),
            (
                OutputPath: fixture.ArtifactPath(workpaper.GetProperty("artifact")),
                TemplateName: "WorkingPaper.xlsx",
                AllowCoverEmbeddedDocumentRemoval: true)
        };

        Assert.Equal(6, reportTemplates.Length);
        var templateDirectory = Path.Combine(FindRepositoryRoot(), "src", "JET", "JET", "Templates");
        Assert.All(reportTemplates, pair => AssertTemplatePackageFidelity(
            pair.OutputPath,
            Path.Combine(templateDirectory, pair.TemplateName),
            pair.AllowCoverEmbeddedDocumentRemoval));
    }

    [Fact]
    public void RuntimeTemplateAssets_MatchSourceAnchors_ExceptClosedHygienePair()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceDirectory = Path.Combine(repositoryRoot, "data");
        var embeddedDirectory = Path.Combine(repositoryRoot, "src", "JET", "JET", "Templates");

        Assert.Equal(2, TemplateHygieneExceptions.Count);
        Assert.All(EmbeddedTemplateNames.Where(fileName => !TemplateHygieneExceptions.Contains(fileName)), fileName =>
        {
            var sourcePath = Path.Combine(sourceDirectory, fileName);
            var embeddedPath = Path.Combine(embeddedDirectory, fileName);
            Assert.True(File.Exists(sourcePath), $"Missing repository template source: data/{fileName}");
            Assert.True(
                File.Exists(embeddedPath),
                $"Missing embedded template asset: src/JET/JET/Templates/{fileName}");
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath))),
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(embeddedPath))));
        });
    }

    [Theory]
    [InlineData("export.validationArtifacts")]
    [InlineData("export.prescreenReport")]
    public async Task ExportRunBoundReport_StaleRun_ThrowsStaleResult(string action)
    {
        var exception = await Assert.ThrowsAsync<JetActionException>(() => fixture.Host.DispatchAsync(
            action,
            JsonSerializer.Serialize(new { runId = "00000000000000000000000000000000" })));

        Assert.Equal(JetErrorCodes.StaleResult, exception.Code);
    }

    [Fact]
    public async Task ExportCriteriaSelectionReport_StaleRevision_ThrowsStaleResult()
    {
        var exception = await Assert.ThrowsAsync<JetActionException>(() => fixture.Host.DispatchAsync(
            "export.criteriaSelectionReport",
            JsonSerializer.Serialize(new
            {
                validationRunId = fixture.ValidationRunId,
                revision = "2000-01-01T00:00:00.0000000Z"
            })));

        Assert.Equal(JetErrorCodes.StaleResult, exception.Code);
    }

    [Fact]
    public async Task ExportCriteriaSelectionReport_StaleValidationRun_ThrowsStaleResult()
    {
        var exception = await Assert.ThrowsAsync<JetActionException>(() => fixture.Host.DispatchAsync(
            "export.criteriaSelectionReport",
            JsonSerializer.Serialize(new
            {
                validationRunId = "00000000000000000000000000000000",
                revision = fixture.ScenarioRevision
            })));

        Assert.Equal(JetErrorCodes.StaleResult, exception.Code);
        Assert.Equal(
            "指定的 validate 執行結果已不是目前版本，請重新執行並使用最新結果。",
            exception.Message);
    }

    private static JsonElement FindArtifact(JsonElement validationResponse, string kind) =>
        validationResponse.GetProperty("artifacts").EnumerateArray()
            .Single(artifact => artifact.GetProperty("kind").GetString() == kind);

    private static void AssertReportArtifactListEqual(
        IReadOnlyList<ReportArtifact> expected,
        IReadOnlyList<ReportArtifact> actual)
    {
        var expectedArtifacts = expected
            .OrderBy(artifact => artifact.Kind)
            .ThenBy(artifact => artifact.ArtifactId, StringComparer.Ordinal)
            .ToArray();
        var actualArtifacts = actual
            .OrderBy(artifact => artifact.Kind)
            .ThenBy(artifact => artifact.ArtifactId, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedArtifacts.Length, actualArtifacts.Length);
        for (var index = 0; index < expectedArtifacts.Length; index++)
        {
            var expectedArtifact = expectedArtifacts[index];
            var actualArtifact = actualArtifacts[index];
            Assert.Equal(expectedArtifact.ArtifactId, actualArtifact.ArtifactId);
            Assert.Equal(expectedArtifact.Kind, actualArtifact.Kind);
            Assert.Equal(expectedArtifact.RelativeFileName, actualArtifact.RelativeFileName);
            Assert.Equal(expectedArtifact.GeneratedUtc, actualArtifact.GeneratedUtc);
            Assert.Equal(expectedArtifact.Bytes, actualArtifact.Bytes);
            Assert.Equal(expectedArtifact.LastWriteUtc, actualArtifact.LastWriteUtc);
            Assert.Equal(expectedArtifact.FileState, actualArtifact.FileState);
            Assert.Equal(expectedArtifact.Stale, actualArtifact.Stale);
            Assert.Equal(
                expectedArtifact.SourceRef.ValidationRunId,
                actualArtifact.SourceRef.ValidationRunId);
            Assert.Equal(
                expectedArtifact.SourceRef.PrescreenRunId,
                actualArtifact.SourceRef.PrescreenRunId);
            Assert.Equal(
                expectedArtifact.SourceRef.ScenarioRevision,
                actualArtifact.SourceRef.ScenarioRevision);
            Assert.Equal(
                (expectedArtifact.SourceRef.ScenarioPositions ?? []).ToArray(),
                (actualArtifact.SourceRef.ScenarioPositions ?? []).ToArray());
        }
    }

    private static LegacyReportWriter BuildLegacyReportWriter(
        HandlerTestHost host,
        LegacyReportWriterOptions options)
    {
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        return new LegacyReportWriter(
            new LocalCompletenessAccountPageRepository(database),
            new LocalCompletenessDiffPageRepository(database),
            new LocalUnbalancedGlEntryPageRepository(database),
            new LocalNullRecordsPageRepository(database),
            new LocalInfSamplePageRepository(database),
            new LocalPrescreenPageRepository(database),
            new LocalRawGlExportRepository(database),
            new LocalImportRepository(database),
            new LocalMappingStateStore(database),
            new LocalCreatorSummaryExportRepository(database),
            new LocalAccountUsageExportRepository(database),
            new LocalTagMatrixScenariosRepository(database),
            new LocalTagMatrixRowPageRepository(database),
            options);
    }

    private static string[] ReadRow(IXLWorksheet sheet, int row, int columnCount) =>
        Enumerable.Range(1, columnCount).Select(column => sheet.Cell(row, column).GetString()).ToArray();

    private static Worksheet ReadWorksheet(SpreadsheetDocument document, string name)
    {
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        var sheet = workbookPart.Workbook.Sheets!.Elements<Sheet>()
            .Single(item => item.Name?.Value == name);
        return Assert.IsType<WorksheetPart>(workbookPart.GetPartById(sheet.Id!.Value!)).Worksheet;
    }

    private static double ColumnWidth(Worksheet worksheet, uint column) =>
        worksheet.GetFirstChild<Columns>()!.Elements<Column>()
            .Single(item => item.Min!.Value <= column && item.Max!.Value >= column)
            .Width!.Value;

    private static void AssertProjectLocalArtifact(
        HandlerTestHost host,
        string projectId,
        JsonElement artifact,
        string runId)
    {
        Assert.Equal(
            new[] { "artifactId", "bytes", "fileName", "fileState", "generatedUtc", "kind", "sourceRef", "stale" },
            artifact.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());

        var fileName = artifact.GetProperty("fileName").GetString()!;
        Assert.Equal(Path.GetFileName(fileName), fileName);
        Assert.Equal(".xlsx", Path.GetExtension(fileName));
        Assert.False(Path.IsPathFullyQualified(fileName));
        Assert.DoesNotContain('/', fileName);
        Assert.DoesNotContain('\\', fileName);
        Assert.Equal(runId, artifact.GetProperty("sourceRef").GetProperty("validationRunId").GetString());

        var fullPath = Path.GetFullPath(Path.Combine(host.ProjectsRoot, projectId, fileName));
        var expectedRoot = Path.GetFullPath(Path.Combine(host.ProjectsRoot, projectId)) + Path.DirectorySeparatorChar;
        Assert.StartsWith(expectedRoot, fullPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(fullPath));
        Assert.DoesNotContain(host.ProjectsRoot, artifact.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertTemplatePackageFidelity(
        string outputPath,
        string templatePath,
        bool allowCoverEmbeddedDocumentRemoval)
    {
        Assert.Equal(".xlsx", Path.GetExtension(outputPath));
        Assert.True(File.Exists(templatePath), $"Missing embedded report template: {templatePath}");
        using var outputArchive = ZipFile.OpenRead(outputPath);
        using var templateArchive = ZipFile.OpenRead(templatePath);

        var allowedRemovedEntryNames = allowCoverEmbeddedDocumentRemoval
            ? ReadCoverEmbeddedDocumentPartClosure(templateArchive)
            : new HashSet<string>(StringComparer.Ordinal);
        Assert.All(allowedRemovedEntryNames, entryName => Assert.True(
            IsTemplateOwnedPackagePart(entryName),
            $"Cover embedded-document closure escaped template-owned package parts: {entryName}"));

        var templateOwnedEntryNames = templateArchive.Entries
            .Where(entry => IsTemplateOwnedPackagePart(entry.FullName))
            .Select(entry => entry.FullName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expectedOutputOwnedEntryNames = templateOwnedEntryNames
            .Except(allowedRemovedEntryNames, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var outputOwnedEntryNames = outputArchive.Entries
            .Where(entry => IsTemplateOwnedPackagePart(entry.FullName))
            .Select(entry => entry.FullName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedOutputOwnedEntryNames, outputOwnedEntryNames);

        Assert.All(expectedOutputOwnedEntryNames, entryName =>
        {
            var templateEntry = Assert.IsType<ZipArchiveEntry>(templateArchive.GetEntry(entryName));
            var outputEntry = Assert.IsType<ZipArchiveEntry>(outputArchive.GetEntry(entryName));
            Assert.True(
                ReadEntryBytes(templateEntry).SequenceEqual(ReadEntryBytes(outputEntry)),
                $"Template-owned package part changed: {entryName}");
        });

        Assert.Equal(
            ReadExternalRelationshipTargets(templateArchive),
            ReadExternalRelationshipTargets(outputArchive));
        Assert.Equal(
            ReadWorksheetHyperlinks(templateArchive),
            ReadWorksheetHyperlinks(outputArchive));

        var names = outputArchive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.DoesNotContain(names, name => name.Contains("vbaProject", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("macro", StringComparison.OrdinalIgnoreCase));

        var contentTypes = ReadEntryText(outputArchive.GetEntry("[Content_Types].xml")!);
        Assert.DoesNotContain("macro", contentTypes, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("macroEnabled", contentTypes, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vbaProject", contentTypes, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTemplateOwnedPackagePart(string entryName) =>
        entryName.StartsWith("xl/media/", StringComparison.OrdinalIgnoreCase)
        || entryName.StartsWith("xl/drawings/", StringComparison.OrdinalIgnoreCase)
        || entryName.StartsWith("xl/externalLinks/", StringComparison.OrdinalIgnoreCase)
        || entryName.StartsWith("xl/embeddings/", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlySet<string> ReadCoverEmbeddedDocumentPartClosure(
        ZipArchive archive)
    {
        XNamespace officeRelationships =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var coverWorksheetEntryName = ReadWorksheetNames(archive)
            .Single(pair => string.Equals(
                pair.Value,
                WorkpaperSheetCatalog.Cover,
                StringComparison.Ordinal))
            .Key;
        var coverWorksheetEntry = Assert.IsType<ZipArchiveEntry>(
            archive.GetEntry(coverWorksheetEntryName));
        var removableElementNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "drawing",
            "legacyDrawing",
            "oleObjects"
        };
        var relationshipIds = LoadXml(coverWorksheetEntry)
            .Descendants()
            .Where(element => removableElementNames.Contains(element.Name.LocalName))
            .SelectMany(element => element.DescendantsAndSelf())
            .Attributes(officeRelationships + "id")
            .Select(attribute => attribute.Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(relationshipIds);

        var relationships = ReadRelationships(
            archive,
            RelationshipEntryName(coverWorksheetEntryName));
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var relationshipId in relationshipIds)
        {
            if (!relationships.TryGetValue(relationshipId, out var relationship)
                || string.Equals(
                    relationship.TargetMode,
                    "External",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "WorkingPaper cover embedded-document relationship is invalid.");
            }

            AddInternalRelationshipClosure(
                archive,
                coverWorksheetEntryName,
                relationship.Target,
                result);
        }
        return result;
    }

    private static void AddInternalRelationshipClosure(
        ZipArchive archive,
        string sourceEntryName,
        string target,
        HashSet<string> result)
    {
        var pending = new Queue<(string SourceEntryName, string Target)>();
        pending.Enqueue((sourceEntryName, target));
        while (pending.TryDequeue(out var relationshipTarget))
        {
            var targetEntryName = ResolvePackagePath(
                relationshipTarget.SourceEntryName,
                relationshipTarget.Target);
            if (!result.Add(targetEntryName))
            {
                continue;
            }
            if (archive.GetEntry(targetEntryName) is null)
            {
                throw new InvalidDataException(
                    "Embedded-document relationship target is missing.");
            }

            var relationshipEntryName = RelationshipEntryName(targetEntryName);
            var relationshipEntry = archive.GetEntry(relationshipEntryName);
            if (relationshipEntry is null)
            {
                continue;
            }
            result.Add(relationshipEntryName);

            foreach (var relationship in ReadRelationships(archive, relationshipEntryName)
                         .Values
                         .Where(relationship => !string.Equals(
                             relationship.TargetMode,
                             "External",
                             StringComparison.OrdinalIgnoreCase)))
            {
                pending.Enqueue((targetEntryName, relationship.Target));
            }
        }
    }

    private static WorksheetPart WorksheetPartFor(SpreadsheetDocument document, string sheetName)
    {
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        var sheet = workbookPart.Workbook.Sheets!.Elements<Sheet>()
            .Single(item => string.Equals(item.Name?.Value, sheetName, StringComparison.Ordinal));
        return Assert.IsType<WorksheetPart>(workbookPart.GetPartById(sheet.Id!.Value!));
    }

    private static byte[] ReadPartBytes(OpenXmlPart part)
    {
        using var input = part.GetStream(FileMode.Open, FileAccess.Read);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static string[] ReadExternalRelationshipTargets(ZipArchive archive) =>
        archive.Entries
            .Where(entry => entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
            .SelectMany(entry => LoadXml(entry).Descendants()
                .Where(element => element.Name.LocalName == "Relationship"))
            .Where(relationship => string.Equals(
                (string?)relationship.Attribute("TargetMode"),
                "External",
                StringComparison.OrdinalIgnoreCase))
            .Select(relationship =>
                $"{(string?)relationship.Attribute("Type")}\u001f{(string?)relationship.Attribute("Target")}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] ReadWorksheetHyperlinks(ZipArchive archive)
    {
        XNamespace officeRelationships =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var worksheetNames = ReadWorksheetNames(archive);
        var hyperlinks = new List<string>();

        foreach (var worksheetEntry in archive.Entries
            .Where(entry => entry.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)
                && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            var relationships = ReadRelationships(archive, RelationshipEntryName(worksheetEntry.FullName));
            var worksheetName = worksheetNames.GetValueOrDefault(
                worksheetEntry.FullName,
                worksheetEntry.FullName);

            foreach (var hyperlink in LoadXml(worksheetEntry).Descendants()
                .Where(element => element.Name.LocalName == "hyperlink"))
            {
                var relationshipId = (string?)hyperlink.Attribute(officeRelationships + "id");
                relationships.TryGetValue(relationshipId ?? string.Empty, out var relationship);
                var attributes = hyperlink.Attributes()
                    .Where(attribute => attribute.Name != officeRelationships + "id"
                        && attribute.Name.LocalName != "uid")
                    .Select(attribute => $"{attribute.Name}={attribute.Value}")
                    .Order(StringComparer.Ordinal);
                hyperlinks.Add(
                    $"{worksheetName}\u001f{string.Join('\u001e', attributes)}"
                    + $"\u001f{relationship.Type}\u001f{relationship.Target}");
            }
        }

        return hyperlinks.Order(StringComparer.Ordinal).ToArray();
    }

    private static Dictionary<string, string> ReadWorksheetNames(ZipArchive archive)
    {
        XNamespace officeRelationships =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var workbook = LoadXml(Assert.IsType<ZipArchiveEntry>(archive.GetEntry("xl/workbook.xml")));
        var relationships = ReadRelationships(archive, "xl/_rels/workbook.xml.rels");

        return workbook.Descendants()
            .Where(element => element.Name.LocalName == "sheet")
            .Select(sheet =>
            {
                var relationshipId = (string?)sheet.Attribute(officeRelationships + "id") ?? string.Empty;
                var relationship = relationships.GetValueOrDefault(relationshipId);
                return (
                    Path: ResolvePackagePath("xl/workbook.xml", relationship.Target),
                    Name: (string?)sheet.Attribute("name") ?? string.Empty);
            })
            .Where(sheet => !string.IsNullOrWhiteSpace(sheet.Path))
            .ToDictionary(sheet => sheet.Path, sheet => sheet.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, (string Type, string Target, string? TargetMode)>
        ReadRelationships(
            ZipArchive archive,
            string entryName)
    {
        var entry = archive.GetEntry(entryName);
        if (entry is null)
        {
            return new Dictionary<string, (string Type, string Target, string? TargetMode)>(
                StringComparer.Ordinal);
        }

        return LoadXml(entry).Descendants()
            .Where(element => element.Name.LocalName == "Relationship")
            .ToDictionary(
                relationship => (string?)relationship.Attribute("Id") ?? string.Empty,
                relationship => (
                    (string?)relationship.Attribute("Type") ?? string.Empty,
                    (string?)relationship.Attribute("Target") ?? string.Empty,
                    (string?)relationship.Attribute("TargetMode")),
                StringComparer.Ordinal);
    }

    private static string RelationshipEntryName(string sourceEntryName)
    {
        var separator = sourceEntryName.LastIndexOf('/');
        var directory = separator < 0 ? string.Empty : sourceEntryName[..separator];
        var fileName = separator < 0 ? sourceEntryName : sourceEntryName[(separator + 1)..];
        return $"{directory}/_rels/{fileName}.rels";
    }

    private static string ResolvePackagePath(string sourceEntryName, string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return string.Empty;
        }

        var source = new Uri($"https://package.invalid/{sourceEntryName}", UriKind.Absolute);
        return new Uri(source, target).AbsolutePath.TrimStart('/');
    }

    private static byte[] ReadEntryBytes(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static XDocument LoadXml(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return XDocument.Load(stream, System.Xml.Linq.LoadOptions.None);
    }

    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "data"))
                && Directory.Exists(Path.Combine(directory.FullName, "src", "JET", "JET", "Templates")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate repository root containing data/ and src/JET/JET/Templates/.");
    }
}

public sealed class ReportArtifactExportFixture : IAsyncLifetime
{
    internal HandlerTestHost Host { get; private set; } = null!;

    internal string ProjectId { get; private set; } = string.Empty;

    internal string ValidationRunId { get; private set; } = string.Empty;

    internal string PrescreenRunId { get; private set; } = string.Empty;

    internal string ScenarioRevision { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        Host = new HandlerTestHost();
        ProjectId = await SetupProjectAsync(Host);

        var validation = await Host.DispatchAsync("validate.run");
        ValidationRunId = validation.GetProperty("resultRef").GetProperty("runId").GetString()!;

        var prescreen = await Host.DispatchAsync("prescreen.run");
        PrescreenRunId = prescreen.GetProperty("resultRef").GetProperty("runId").GetString()!;

        var committed = await Host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new
            {
                scenarios = new[]
                {
                    new
                    {
                        name = "受控測試情境",
                        rationale = "原始摘要含調整",
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
        ScenarioRevision = committed.GetProperty("resultRef").GetProperty("revision").GetString()!;
    }

    public ValueTask DisposeAsync()
    {
        Host.Dispose();
        return ValueTask.CompletedTask;
    }

    internal static Task<string> SetupProjectAsync(HandlerTestHost host) =>
        InlineWorkbookProject.SetupAsync(
            host,
            builder => builder
                .WithColumns(OriginalHeaders())
                .AddRow("JV-001", "2025-03-05", "2026-01-02", "1101", "現金", "調整分錄", "2000000.00", 1)
                .AddRow("JV-001", "2025-03-05", "2026-01-02", "4101", "收入", null, "2000000.00", 0),
            lastPeriodStart: "2025-12-31",
            configureTb: tb => tb
                .AddRow("1101", "現金", 2_000_000)
                .AddRow("4101", "收入", -2_000_000));

    internal Task<JsonElement> ExportValidationArtifactsAsync() => Host.DispatchAsync(
        "export.validationArtifacts",
        JsonSerializer.Serialize(new { runId = ValidationRunId }));

    /// <summary>科目配對範本是工作檔：回傳完整路徑，不在 reportArtifacts 裡。</summary>
    internal async Task<string> ExportAccountMappingTemplatePathAsync()
    {
        var response = await Host.DispatchAsync(
            "export.accountMappingTemplate",
            JsonSerializer.Serialize(new { runId = ValidationRunId }));
        return response.GetProperty("filePath").GetString()!;
    }

    internal Task<JsonElement> ExportPrescreenReportAsync() => Host.DispatchAsync(
        "export.prescreenReport",
        JsonSerializer.Serialize(new { runId = PrescreenRunId }));

    internal Task<JsonElement> ExportCriteriaSelectionReportAsync() => Host.DispatchAsync(
        "export.criteriaSelectionReport",
        JsonSerializer.Serialize(new
        {
            validationRunId = ValidationRunId,
            revision = ScenarioRevision
        }));

    internal Task<JsonElement> ExportWorkingPaperAsync() => Host.DispatchAsync(
        "export.workpaperStream",
        JsonSerializer.Serialize(new
        {
            validationRunId = ValidationRunId,
            scenarioRevision = ScenarioRevision,
            scenarioPositions = new[] { 1 }
        }));

    internal string ArtifactPath(JsonElement artifact) => Path.Combine(
        Host.ProjectsRoot,
        ProjectId,
        artifact.GetProperty("fileName").GetString()!);

    private static string[] OriginalHeaders() =>
    [
        "傳票號碼", "傳票日期", "核准日期", "科目代號",
        "科目名稱", "摘要", "金額", "借方旗標"
    ];
}
