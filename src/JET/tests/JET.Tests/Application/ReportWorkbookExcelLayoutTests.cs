using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 資料驗證、預篩選與條件篩選報告從畫面的匯出動作一路寫到案件資料夾，輸出檔的寫法要和 Excel 存檔時相同：
/// 每一張工作表與每一個 XML 項目都套同一組檢查，見 <see cref="ExcelPackageLayoutAssert"/>。
/// 資料都由程式產生：有含換行的摘要、借貸不平衡的傳票、傳票號碼空白的分錄、摘要空白的分錄、
/// 財報準備日之後核准的傳票，以及和 TB 有差異的科目，讓明細頁、續頁以外的固定頁與隱藏的紀錄都寫出來。
/// INF、工作底稿與科目配對範本照原樣寫出、不整理成 Excel 存檔時的寫法，所以不在這裡檢查：
/// INF 與底稿照舊版的寫法交付，科目配對範本是讓審計員填寫後讀回的工作檔。
/// </summary>
public sealed class ReportWorkbookExcelLayoutTests(ReportWorkbookExcelLayoutFixture fixture)
    : IClassFixture<ReportWorkbookExcelLayoutFixture>
{
    [Theory]
    [InlineData("validationReport", ReportTemplateCatalog.Validation)]
    [InlineData("prescreenReport", ReportTemplateCatalog.Prescreen)]
    [InlineData("criteriaSelectionReport", ReportTemplateCatalog.CriteriaSelection)]
    public void ExportedReport_MatchesTheExcelLayout(string kind, string templateName) =>
        Assert.Empty(ExcelPackageLayoutAssert.Problems(fixture.Package(kind), templateName));

    // 前提：合成資料真的讓明細頁與隱藏的紀錄都寫出來，上面的檢查才涵蓋得到。
    [Theory]
    [InlineData("validationReport")]
    [InlineData("prescreenReport")]
    [InlineData("criteriaSelectionReport")]
    public void ExportedReport_WritesTheSheetsTheChecksCover(string kind)
    {
        string[] expected = kind switch
        {
            "validationReport" =>
            [
                "ValidationReport", MappingMetadataFormat.WorksheetName, WorkingPaperSheetCatalog.Step13,
                "V_Report 2", "V_Report 3", "V_Report 4", "V_Report 5", "V_Report 6",
                ReportWorkbookMetadataFormat.WorksheetName
            ],
            "prescreenReport" => ["Pre-screening_Report", "R1", "R5", "R6", "R7", ReportWorkbookMetadataFormat.WorksheetName],
            _ => ["Summary Inforamtion", "#Criteria Select 1", ReportWorkbookMetadataFormat.WorksheetName]
        };

        using var document = SpreadsheetDocument.Open(new MemoryStream(fixture.Package(kind), writable: false), false);
        var names = document.WorkbookPart!.Workbook!.Sheets!.Elements<Sheet>()
            .Select(sheet => sheet.Name!.Value!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Subset(names, expected.ToHashSet(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("validationReport")]
    [InlineData("prescreenReport")]
    [InlineData("criteriaSelectionReport")]
    public void ExportedReport_KeepsTheMetadataRecordInline(string kind) =>
        Assert.Empty(ExcelPackageLayoutAssert.MetadataRecordProblems(fixture.Package(kind)));

    [Theory]
    [InlineData("validationReport")]
    [InlineData("prescreenReport")]
    [InlineData("criteriaSelectionReport")]
    public void ExportedReport_WritesLineBreaksAsLineFeed(string kind) =>
        Assert.Empty(ExcelPackageLayoutAssert.LineBreakProblems(
            fixture.Package(kind),
            ReportWorkbookExcelLayoutFixture.DescriptionWithLineBreak));
}

/// <summary>建一個合成案件，依序執行驗證、預篩選與條件篩選，再從畫面的匯出動作產生三份報告，讀回輸出檔的位元組。</summary>
public sealed class ReportWorkbookExcelLayoutFixture : IAsyncLifetime
{
    internal const string DescriptionWithLineBreak = "合成調整\n第二行";

    private readonly Dictionary<string, byte[]> _packages = new(StringComparer.Ordinal);

    private HandlerTestHost Host { get; set; } = null!;

    internal byte[] Package(string kind) => _packages[kind];

    public async ValueTask InitializeAsync()
    {
        Host = new HandlerTestHost(principalName: "tester");
        var projectId = await InlineWorkbookProject.SetupAsync(
            Host,
            builder => builder
                .WithColumns("傳票號碼", "傳票日期", "核准日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標", "建立人員")
                .AddRow("JV-001", "2025-03-05", "2026-01-02", "1101", "現金", DescriptionWithLineBreak, "2000000.00", 1, "合成編製者甲")
                .AddRow("JV-001", "2025-03-05", "2026-01-02", "4101", "收入", null, "2000000.00", 0, "合成編製者甲")
                .AddRow("JV-002", "2025-06-06", "2025-06-07", "1101", "現金", DescriptionWithLineBreak, "100.00", 1, "合成編製者乙")
                .AddRow("JV-002", "2025-06-06", "2025-06-07", "5101", "待分類科目", "合成一般摘要", "90.00", 0, "合成編製者乙")
                .AddRow("", "2025-07-01", "2025-07-02", "1101", "現金", "合成調整 空白傳票", "50.00", 1, "合成編製者乙")
                .AddRow("", "2025-07-01", "2025-07-02", "4101", "收入", "合成一般 空白傳票", "50.00", 0, "合成編製者乙"),
            prepStartDate: "2025-12-31",
            configureTb: tb => tb
                .AddRow("1101", "現金", 2_000_150)
                .AddRow("4101", "收入", -2_000_050)
                .AddRow("5101", "待分類科目", -100));

        var validation = await Host.DispatchAsync("validate.run");
        var validationResultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString()!;
        await Host.DispatchAsync("export.validationReports", JsonSerializer.Serialize(new { resultId = validationResultId }));

        var prescreen = await Host.DispatchAsync("prescreen.run");
        var prescreenResultId = prescreen.GetProperty("resultRef").GetProperty("resultId").GetString()!;
        await Host.DispatchAsync("export.prescreenReport", JsonSerializer.Serialize(new { resultId = prescreenResultId }));

        var committed = await Host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new
            {
                scenarios = new[]
                {
                    new
                    {
                        name = "合成摘要情境",
                        rationale = "摘要含調整",
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
        await Host.DispatchAsync(
            "export.criteriaSelectionReport",
            JsonSerializer.Serialize(new { validationResultId, prescreenResultId, revision }));

        var loaded = await Host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        foreach (var report in loaded.GetProperty("reports").EnumerateArray())
        {
            var kind = report.GetProperty("kind").GetString()!;
            var fileName = report.GetProperty("fileName").GetString()!;
            _packages[kind] = await File.ReadAllBytesAsync(Path.Combine(Host.ProjectsRoot, projectId, fileName));
        }
    }

    public ValueTask DisposeAsync()
    {
        Host.Dispose();
        return ValueTask.CompletedTask;
    }
}
