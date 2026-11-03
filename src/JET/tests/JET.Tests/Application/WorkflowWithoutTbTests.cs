using System.Text.Json;
using ClosedXML.Excel;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

// 裁定 2026-10-29-16：沒有 TB 也能往下做，逐科目比對寫無法執行和原因，由審計員在底稿說明；
// 句子照裁定 2026-10-30-16。已匯入 TB、但配對還沒確認時的報告與底稿句子是 Claude 照同一組句子改寫，待使用者確認。
// 第一次失敗：Focused 收據 20261030-232301388-a5c8cf28039f4bd7916e7e13f46c3c7a。

/// <summary>
/// 沒有 TB，或已匯入 TB 但還沒確認配對的案件，照審計員的順序走完資料驗證、預篩選、條件篩選、
/// 匯出驗證報告與工作底稿。逐科目比對寫無法執行與原因，其他檢查照常；全部用程式生成的合成資料。
/// </summary>
public sealed class WorkflowWithoutTbTests
{
    private const string NotImportedReason = "沒有匯入 TB，無法執行 GL 與 TB 逐科目比對。";
    // 裁定 2026-11-01-06 把這句的「還沒確認」改成「尚未確認」，其餘字不動；第一次失敗：Focused 收據 20261101-062343383-804cee29d9d9428f9e5f502ea4e7ae94。
    private const string NotConfirmedReason = "TB 欄位配對尚未確認，無法執行 GL 與 TB 逐科目比對。";
    private const string NotImportedListNote = "#本案件未匯入試算表，無法執行試算表科目金額本期異動與會計分錄(JE)之推滾比對。";
    private const string NotConfirmedListNote = "#本案件試算表欄位配對尚未確認，無法執行試算表科目金額本期異動與會計分錄(JE)之推滾比對。";
    private const string Step1Conclusion = "基於上述程序，查核團隊對於JE測試母體之完整性，尚需另行說明以取得足夠的查核證據。";

    [Theory]
    [InlineData("sqlite", false)]
    [InlineData("sqlite", true)]
    [InlineData("duckdb", false)]
    [InlineData("duckdb", true)]
    public async Task CaseWithoutConfirmedTb_RunsEveryStepAndWritesWhyAccountComparisonCannotRun(
        string databaseProvider,
        bool tbImported)
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(host, ConfigureGl, databaseProvider: databaseProvider);
        if (tbImported)
        {
            await ImportTbWithoutMappingAsync(host);
        }

        var reason = tbImported ? NotConfirmedReason : NotImportedReason;
        var validation = await host.DispatchAsync("validate.run");
        var completeness = validation.GetProperty("completenessTest");
        Assert.Equal(reason, completeness.GetProperty("naReason").GetString());
        Assert.Equal(0, completeness.GetProperty("diffAccountCount").GetInt64());
        var eligibility = completeness.GetProperty("eligibility");
        Assert.True(eligibility.GetProperty("isEligible").GetBoolean());
        Assert.StartsWith(reason, eligibility.GetProperty("warning").GetString());
        // 其他檢查照常：借貸不平的傳票照樣找到。
        Assert.Equal(1, validation.GetProperty("voucherBalanceTest").GetProperty("unbalancedVoucherCount").GetInt64());

        var validationResultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString()!;
        var prescreen = await host.DispatchAsync("prescreen.run");
        Assert.True(prescreen.TryGetProperty("resultRef", out _));
        var committed = await host.DispatchAsync("filter.commit", ScenarioPayload());
        var revision = committed.GetProperty("resultRef").GetProperty("revision").GetString()!;

        var reports = await host.DispatchAsync(
            "export.validationReports",
            JsonSerializer.Serialize(new { resultId = validationResultId }));
        var validationReport = reports.GetProperty("publishedReports").EnumerateArray()
            .Single(report => report.GetProperty("kind").GetString() == "validationReport");
        using (var book = new XLWorkbook(ReportPath(host, projectId, validationReport)))
        {
            var summary = book.Worksheet("ValidationReport");
            Assert.Equal("N/A", summary.Cell("D24").GetString());
            Assert.Equal(tbImported ? "TB 欄位配對未確認" : "未匯入 TB", summary.Cell("E24").GetString());
            // 逐科目的調節表只留表頭。
            var reconciliation = book.Worksheet("V_Report 5");
            Assert.Equal("ACCOUNT_NUM_ALL", reconciliation.Cell(1, 1).GetString());
            Assert.Equal(1, reconciliation.LastRowUsed()!.RowNumber());
        }

        var workingPaper = await host.DispatchAsync("export.workingPaperStream", JsonSerializer.Serialize(new
        {
            validationResultId,
            scenarioRevision = revision,
            scenarioPositions = new[] { 1 }
        }));
        var report = workingPaper.GetProperty("publishedReport");
        Assert.Equal("workingPaper", report.GetProperty("kind").GetString());
        using (var book = new XLWorkbook(ReportPath(host, projectId, report)))
        {
            var step1 = book.Worksheet(WorkingPaperSheetCatalog.Step1);
            Assert.Equal(Step1Conclusion, step1.Cell("B15").GetString());
            Assert.Equal(tbImported ? NotConfirmedListNote : NotImportedListNote, step1.Cell("B17").GetString());
            // 科目清單留空：表頭照舊，表頭下面沒有科目。
            Assert.Contains("試算表科目", step1.Cell("B19").GetString());
            Assert.True(step1.Row(20).IsEmpty());
            Assert.False(book.Worksheets.Contains(WorkingPaperSheetCatalog.Step13));
            // 其他步驟照常。
            Assert.True(book.Worksheets.Contains(WorkingPaperSheetCatalog.Step11));
            Assert.True(book.Worksheet(WorkingPaperSheetCatalog.Step41).RowsUsed().Count() > 1);
        }
    }

    private static void ConfigureGl(InlineGlWorkbookBuilder builder) =>
        builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("JV-001", "2025-03-05", "1101", "現金", "合成沒有試算表借方", "100.00", 1)
            .AddRow("JV-001", "2025-03-05", "4101", "收入", "合成沒有試算表貸方", "100.00", 0)
            .AddRow("JV-002", "2025-04-08", "1101", "現金", "合成借貸不平", "50.00", 1);

    private static async Task ImportTbWithoutMappingAsync(HandlerTestHost host)
    {
        var builder = new InlineTbWorkbookBuilder()
            .AddRow("1101", "現金", "150.00")
            .AddRow("4101", "收入", "-100.00");
        var filePath = builder.WriteWorkbook();
        try
        {
            await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new
            {
                filePath,
                fileName = "without-mapping-tb.xlsx"
            }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(filePath);
        }
    }

    private static string ScenarioPayload() =>
        JsonSerializer.Serialize(new
        {
            scenarios = new[]
            {
                new
                {
                    name = "合成摘要條件",
                    rationale = "合成沒有試算表的條件篩選",
                    groups = new[]
                    {
                        new
                        {
                            join = "AND",
                            conditions = new[]
                            {
                                new
                                {
                                    join = "AND",
                                    type = "fieldValue",
                                    field = "description",
                                    @operator = "contains",
                                    values = new[] { "合成" }
                                }
                            }
                        }
                    }
                }
            }
        });

    private static string ReportPath(HandlerTestHost host, string projectId, JsonElement report) =>
        Path.Combine(host.ProjectsRoot, projectId, report.GetProperty("fileName").GetString()!);
}
