using System.Text.Json;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 從建立案件、匯入 GL 一路做到匯出底稿，核對底稿「自動化工具-檔案欄位資訊」工作表。
/// 配對前的欄名很長時，名稱欄維持寬度上限、欄名換行，列高跟著行數加高；沒有匯入 TB 時，TB 那一段沒有任何欄位列。
/// 寫出程式的單元測試已經測過換行加高，這裡補上照審計員操作順序走正式流程匯出的底稿。
/// 新增的測試，程式本來就這樣處理，第一次就通過，沒有第一次失敗的收據。
/// </summary>
public sealed class WorkingPaperFieldInfoJourneyTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task LongSourceColumnName_WrapsAndRaisesRowHeight_WhileTbSectionStaysEmpty(string provider)
    {
        // 摘要配到一個 60 個全形字的欄名，寬度算 120。名稱欄寬度上限 40，扣掉留白與安全係數後每行放得下約 33，
        // 所以換成 4 行；底稿內文 11 點，每行 16.5 點，列高 66 點。
        var longColumn = new string('長', 60);
        using var host = new HandlerTestHost();
        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectCode = "FIELD-INFO-001",
            clientName = "合成欄位資訊案件",
            operatorId = "tester",
            periodStart = "2025-01-01",
            periodEnd = "2025-12-31",
            databaseProvider = provider
        }));
        var projectId = created.GetProperty("projectId").GetString()!;
        var builder = new InlineGlWorkbookBuilder()
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", longColumn, "金額", "借方旗標")
            .AddRow("JV-001", "2025-03-05", "1101", "合成現金", "合成借方", 100, 1)
            .AddRow("JV-001", "2025-03-05", "4101", "合成收入", "合成貸方", 100, 0);
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

        var mapping = builder.BuildFlagModeMapping();
        mapping["description"] = longColumn;
        await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping,
            amountMode = "flag",
            manualAutoPolicy = new { manualValues = new[] { "1" }, automaticValues = new[] { "0" } }
        }));

        var validation = await host.DispatchAsync("validate.run");
        var prescreen = await host.DispatchAsync("prescreen.run");
        var saved = await host.DispatchAsync("filter.commit", """
            {
              "scenarios": [
                {
                  "name": "借方分錄",
                  "rationale": "合成案件",
                  "groups": [
                    { "join": "AND", "conditions": [ { "join": "AND", "type": "drCrOnly", "drCr": "debit" } ] }
                  ]
                }
              ]
            }
            """);
        var response = await host.DispatchAsync("export.workingPaperStream", JsonSerializer.Serialize(new
        {
            validationResultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString(),
            prescreenResultId = prescreen.GetProperty("resultRef").GetProperty("resultId").GetString(),
            scenarioRevision = saved.GetProperty("resultRef").GetProperty("revision").GetString(),
            scenarioPositions = new[] { 1 }
        }));
        var path = Path.Combine(
            host.ProjectsRoot,
            projectId,
            response.GetProperty("publishedReport").GetProperty("fileName").GetString()!);

        uint longRowNumber;
        using (var workbook = new XLWorkbook(path))
        {
            var sheet = workbook.Worksheet(WorkingPaperSheetCatalog.FieldInfo);
            Assert.Equal("TB檔案配對前後欄位對照表", sheet.Cell("A2").GetString());
            // 沒有 TB：表頭下面直接是兩列空白，接著是 GL 的標題與表頭。
            Assert.All(sheet.Range("A4:E5").Cells(), cell => Assert.True(cell.IsEmpty()));
            Assert.Equal("GL檔案配對前後欄位對照表", sheet.Cell("A6").GetString());
            // 改名欄位的「配對前欄位名稱」照裁定 2026-11-03-17 加【】。第一次失敗：Focused 收據 20261103-065006568-d4478bbc5fd8447f82aa77ce32345943。
            var longCell = Assert.Single(sheet.Column(1).CellsUsed(), cell => cell.GetString() == $"【{longColumn}】");
            Assert.True(longCell.Address.RowNumber > 7);
            Assert.True(longCell.Style.Alignment.WrapText);
            longRowNumber = (uint)longCell.Address.RowNumber;
        }

        using var document = SpreadsheetDocument.Open(path, false);
        var workbookPart = document.WorkbookPart!;
        var sheetEntry = workbookPart.Workbook.Descendants<Sheet>()
            .Single(entry => entry.Name == WorkingPaperSheetCatalog.FieldInfo);
        var worksheet = ((WorksheetPart)workbookPart.GetPartById(sheetEntry.Id!)).Worksheet;
        var nameColumn = worksheet.GetFirstChild<Columns>()!.Elements<Column>()
            .Single(column => column.Min!.Value <= 1U && column.Max!.Value >= 1U);
        Assert.Equal(40D, nameColumn.Width!.Value);
        var longRow = worksheet.GetFirstChild<SheetData>()!.Elements<Row>()
            .Single(row => row.RowIndex!.Value == longRowNumber);
        Assert.Equal(66D, longRow.Height!.Value);
        Assert.True(longRow.CustomHeight!.Value);
    }
}
