using System.Text.Json;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 用其他工具產生的活頁簿，公式可能沒有存計算結果。JET 不重算公式，這些格子照舊匯入成空白，
/// 每個用同一個讀取器的匯入入口都在既有的提醒裡寫出格數、第一格與下一步。活頁簿與值都是合成的。
/// </summary>
public sealed class FormulaWithoutResultImportTests
{
    private const string SheetName = "合成工作表";

    [Fact]
    public async Task GlImport_TwoSources_WarnsOnlyForTheWorkbookWithFormulas()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectName = "合成公式提醒", periodStart = "2025-01-01", periodEnd = "2025-12-31"
        }));
        var csv = Path.Combine(host.ProjectsRoot, "plain.csv");
        await File.WriteAllTextAsync(csv, "doc,amount\nV9,9\n");
        var workbook = Workbook(
            """
            <row r="1"><c r="A1" t="inlineStr"><is><t>doc</t></is></c><c r="B1" t="inlineStr"><is><t>amount</t></is></c></row>
            <row r="2"><c r="A2" t="inlineStr"><is><t>V1</t></is></c><c r="B2"><f>10*2</f></c></row>
            <row r="3"><c r="A3" t="inlineStr"><is><t>V2</t></is></c><c r="B3"><f>B2</f></c></row>
            <row r="4"><c r="A4" t="inlineStr"><is><t>V3</t></is></c><c r="B4"><f>3</f><v>3</v></c></row>
            """);
        try
        {
            var imported = await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new
            {
                sources = new object[]
                {
                    new { filePath = workbook, fileName = "formula-gl.xlsx", sheetName = SheetName },
                    new { filePath = csv }
                }
            }));

            Assert.Equal(4, imported.GetProperty("rowCount").GetInt32());
            Assert.Equal([ImportWarning("formula-gl.xlsx", 2, "B2")], Warnings(imported));
        }
        finally
        {
            File.Delete(workbook);
        }
    }

    [Fact]
    public async Task AccountMappingImport_FormulaWithoutResult_Warns()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);
        var workbook = Workbook(
            """
            <row r="1"><c r="A1" t="inlineStr"><is><t>科目代號</t></is></c><c r="B1" t="inlineStr"><is><t>科目名稱</t></is></c><c r="C1" t="inlineStr"><is><t>標準化分類</t></is></c></row>
            <row r="2"><c r="A2" t="inlineStr"><is><t>1000</t></is></c><c r="B2" t="inlineStr"><is><t>合成資產</t></is></c><c r="C2" t="inlineStr"><is><t>Cash</t></is></c></row>
            <row r="3"><c r="A3" t="inlineStr"><is><t>4000</t></is></c><c r="B3" t="str"><f>"合成收入"</f></c><c r="C3" t="inlineStr"><is><t>Revenue</t></is></c></row>
            """);
        try
        {
            var imported = await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new
            {
                filePath = workbook, fileName = "formula-mapping.xlsx", sheetName = SheetName
            }));

            Assert.Equal(2, imported.GetProperty("rowCount").GetInt32());
            Assert.Equal([ImportWarning("formula-mapping.xlsx", 1, "B3")], Warnings(imported));
        }
        finally
        {
            File.Delete(workbook);
        }
    }

    [Fact]
    public async Task AuthorizedPreparerImport_FormulaWithoutResult_Warns()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);
        var workbook = Workbook(
            """
            <row r="1"><c r="A1" t="inlineStr"><is><t>AUTHORIZED_PREPARER</t></is></c></row>
            <row r="2"><c r="A2" t="inlineStr"><is><t>王小明</t></is></c></row>
            <row r="3"><c r="A3" t="str"><f>"李大華"</f></c></row>
            """);
        try
        {
            var imported = await host.DispatchAsync("import.authorizedPreparer.fromFile", JsonSerializer.Serialize(new
            {
                filePath = workbook,
                fileName = "formula-preparers.xlsx",
                sheetName = SheetName,
                sourceColumn = "AUTHORIZED_PREPARER"
            }));

            Assert.Equal([ImportWarning("formula-preparers.xlsx", 1, "A3")], Warnings(imported));
        }
        finally
        {
            File.Delete(workbook);
        }
    }

    [Fact]
    public async Task HolidayImport_FormulaInTitleRowIsSkipped_WarnsForTheDataRow()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);
        var workbook = Workbook(
            """
            <row r="1"><c r="B1" t="str"><f>"合成行事曆"</f></c></row>
            <row r="2"><c r="A2" t="inlineStr"><is><t>Date_of_Holiday</t></is></c><c r="B2" t="inlineStr"><is><t>Holiday_Name</t></is></c><c r="C2" t="inlineStr"><is><t>IS_Holiday</t></is></c></row>
            <row r="3"><c r="A3" t="inlineStr"><is><t>2025-01-01</t></is></c><c r="B3" t="str"><f>"元旦"</f></c><c r="C3" t="inlineStr"><is><t>Y</t></is></c></row>
            """);
        try
        {
            var imported = await host.DispatchAsync("import.holiday.fromFile", JsonSerializer.Serialize(new
            {
                filePath = workbook, fileName = "formula-holiday.xlsx", sheetName = SheetName
            }));

            Assert.Equal(1, imported.GetProperty("count").GetInt32());
            Assert.Equal([ImportWarning("formula-holiday.xlsx", 1, "B3")], Warnings(imported));
        }
        finally
        {
            File.Delete(workbook);
        }
    }

    [Fact]
    public async Task Preview_FormulaWithoutResult_AddsANotice()
    {
        using var host = new HandlerTestHost();
        var workbook = Workbook(
            """
            <row r="1"><c r="A1" t="inlineStr"><is><t>doc</t></is></c><c r="B1" t="inlineStr"><is><t>amount</t></is></c></row>
            <row r="2"><c r="A2" t="inlineStr"><is><t>V1</t></is></c><c r="B2"><f>10*2</f></c></row>
            """);
        try
        {
            var preview = await host.DispatchAsync("import.previewFile", JsonSerializer.Serialize(new
            {
                filePath = workbook, sheetName = SheetName
            }));

            var row = Assert.Single(preview.GetProperty("sampleRows").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, row[1].ValueKind);
            Assert.Equal(JsonValueKind.Array, preview.GetProperty("notices").ValueKind);
            Assert.Equal(
                [$"預覽讀到的列裡有 1 個儲存格有公式、卻沒有存計算結果，第一格是工作表「{SheetName}」的 B2。"
                    + "JET 不會重新計算公式，這些儲存格會讀成空白。請用 Excel 開啟這個檔案並存檔後再匯入。"],
                preview.GetProperty("notices").EnumerateArray().Select(notice => notice.GetString()!));
        }
        finally
        {
            File.Delete(workbook);
        }
    }

    /// <summary>給審計員看的句子逐字寫在這裡，當成獨立的預期值。</summary>
    private static string ImportWarning(string fileName, int count, string firstCell) =>
        $"檔案「{fileName}」工作表「{SheetName}」有 {count} 個儲存格有公式、卻沒有存計算結果，第一格是 {firstCell}。"
        + "JET 不會重新計算公式，這些儲存格匯入後是空白。請用 Excel 開啟這個檔案並存檔，再重新匯入。";

    private static string Workbook(string rows) =>
        new RawXlsxBuilder().AddSheet(SheetName, $"<sheetData>{rows}</sheetData>").Save();

    private static string[] Warnings(JsonElement response) =>
        response.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString()!).ToArray();
}
