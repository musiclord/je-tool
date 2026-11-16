using System.Text.Json;
using ClosedXML.Excel;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 底稿與條件篩選報告寫出的欄位名稱不加任何符號。條件說明裡的欄位名稱照原樣寫，值照舊用「」；
/// 底稿「自動化工具-檔案欄位資訊」的配對前欄位名稱和來源檔的欄名一字不差，欄名含空白、連字號、頓號、全形逗號或全形括號也一樣。
/// 用合成案件走正式流程匯出兩份文件後讀檔核對。畫面讀回的寫法由 FilterConditionRendererTests 守住。
/// </summary>
public sealed class DocumentFieldNameJourneyTests
{
    private const string ExpectedConditionText =
        "傳票摘要 包含「調整」 且 金額（絕對值） ≥ 1000 且 總帳日期 每月幾日屬於「5、31」；空白不列入";

    private const string VoucherColumn = "傳票 號碼";
    private const string GlDateColumn = "傳票日期";
    private const string AccountNumberColumn = "科目-代號";
    private const string AccountNameColumn = "科目名稱";
    private const string DescriptionColumn = "摘要（說明）";
    private const string AmountColumn = "金額、原幣";
    private const string DebitFlagColumn = "借方，旗標";

    [Theory]
    [InlineData("sqlite")]
    public async Task ExportedDocuments_WriteFieldNamesWithoutAddedSymbols_AndSourceColumnNamesExactly(string provider)
    {
        using var host = new HandlerTestHost();
        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectCode = "FIELD-NAME-001",
            clientName = "合成欄位名稱案件",
            operatorId = "tester",
            periodStart = "2025-01-01",
            periodEnd = "2025-12-31",
            databaseProvider = provider
        }));
        var projectId = created.GetProperty("projectId").GetString()!;

        var builder = new InlineGlWorkbookBuilder()
            .WithColumns(VoucherColumn, GlDateColumn, AccountNumberColumn, AccountNameColumn, DescriptionColumn, AmountColumn, DebitFlagColumn)
            .AddRow("JV-001", "2025-03-05", "1101", "合成現金", "合成調整", 1500, 1)
            .AddRow("JV-001", "2025-03-05", "4101", "合成收入", "合成調整", 1500, 0);
        var workbookPath = builder.WriteWorkbook();
        try
        {
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new
            {
                filePath = workbookPath,
                fileName = "合成總帳.xlsx"
            }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(workbookPath);
        }

        await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["mapping"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["voucherNumber"] = VoucherColumn,
                ["glDate"] = GlDateColumn,
                ["accountNumber"] = AccountNumberColumn,
                ["accountName"] = AccountNameColumn,
                ["description"] = DescriptionColumn,
                ["amount"] = AmountColumn,
                ["drCr"] = DebitFlagColumn,
                ["drCrDebitCode"] = "1",
                ["drCrCreditCode"] = "0"
            },
            ["amountMode"] = "flag",
            ["manualAutoPolicy"] = new { manualValues = new[] { "1" }, automaticValues = new[] { "0" } }
        }));
        var validation = await host.DispatchAsync("validate.run");
        var prescreen = await host.DispatchAsync("prescreen.run");
        var validationResultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString();
        var prescreenResultId = prescreen.GetProperty("resultRef").GetProperty("resultId").GetString();
        var saved = await host.DispatchAsync("filter.commit", """
            {
              "scenarios": [
                {
                  "name": "調整分錄",
                  "rationale": "合成案件",
                  "groups": [
                    { "join": "AND", "conditions": [
                      { "join": "AND", "type": "text", "field": "description", "mode": "contains", "keywords": "調整" },
                      { "join": "AND", "type": "numRange", "field": "amount", "from": "1000", "to": "" },
                      { "join": "AND", "type": "fieldValue", "field": "glDate", "operator": "dayOfMonthIn", "values": ["5", "31"] }
                    ] }
                  ]
                }
              ]
            }
            """);
        var revision = saved.GetProperty("resultRef").GetProperty("revision").GetString();

        await host.DispatchAsync("export.criteriaSelectionReport", JsonSerializer.Serialize(new
        {
            validationResultId,
            prescreenResultId,
            revision
        }));
        await host.DispatchAsync("export.workingPaperStream", JsonSerializer.Serialize(new
        {
            validationResultId,
            prescreenResultId,
            scenarioRevision = revision,
            scenarioPositions = new[] { 1 }
        }));
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        var reports = loaded.GetProperty("reports").EnumerateArray().ToArray();
        var projectDirectory = Path.Combine(host.ProjectsRoot, projectId);

        using (var workingPaper = new XLWorkbook(ReportPath(reports, projectDirectory, "workingPaper")))
        {
            var row = workingPaper.Worksheet(WorkingPaperSheetCatalog.Step3).RowsUsed()
                .Single(candidate => candidate.Cell(2).GetString() == "C1");
            var description = row.Cell(3).GetString();
            // 儲存格裡的換行寫成 CRLF，比對前統一成 LF。
            Assert.Equal("調整分錄\n篩選條件：" + ExpectedConditionText, description.Replace("\r\n", "\n", StringComparison.Ordinal));
            AssertNoAddedSymbols(description);

            // 欄位資訊的配對前欄位名稱：改名欄位寫來源欄名本身，金額說明句裡的來源欄名也一字不差。
            var beforeNames = workingPaper.Worksheet(WorkingPaperSheetCatalog.FieldInfo).Column(1).CellsUsed()
                .Select(static cell => cell.GetString())
                .ToArray();
            Assert.All(beforeNames, AssertNoAddedSymbols);
            Assert.Contains(VoucherColumn, beforeNames);
            Assert.Contains(GlDateColumn, beforeNames);
            Assert.Contains(AccountNumberColumn, beforeNames);
            Assert.Contains(AccountNameColumn, beforeNames);
            Assert.Contains(DescriptionColumn, beforeNames);
            Assert.Contains(
                $"傳票金額_JE 由系統產生 : 金額欄位 {AmountColumn}、借貸方判斷欄位 {DebitFlagColumn}，借方為「1」，貸方為「0」",
                beforeNames);
        }

        using (var criteriaReport = new XLWorkbook(ReportPath(reports, projectDirectory, "criteriaSelectionReport")))
        {
            var sheet = criteriaReport.Worksheets.First();
            Assert.Equal("Criteria Selection 1", sheet.Cell("A5").GetString());
            var conditionText = sheet.Cell("B5").GetString();
            Assert.Equal(ExpectedConditionText, conditionText);
            AssertNoAddedSymbols(conditionText);
        }
    }

    private static void AssertNoAddedSymbols(string text)
    {
        Assert.DoesNotContain("【", text, StringComparison.Ordinal);
        Assert.DoesNotContain("】", text, StringComparison.Ordinal);
    }

    private static string ReportPath(JsonElement[] reports, string projectDirectory, string kind)
    {
        var fileName = reports.Single(report => report.GetProperty("kind").GetString() == kind)
            .GetProperty("fileName").GetString()!;
        return Path.Combine(projectDirectory, fileName);
    }
}
