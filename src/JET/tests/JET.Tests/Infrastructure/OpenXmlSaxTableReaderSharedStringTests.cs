using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 共用字串格記的是共用字串表的編號。編號找不到對應的文字時，檔案已經損壞，讀取器停下來並寫出工作表與儲存格位址，
/// 不把這一格當成空白讀進來。活頁簿用 RawXlsxBuilder 手刻，欄名與值都是合成的。
/// </summary>
public sealed class OpenXmlSaxTableReaderSharedStringTests
{
    private const string SheetName = "合成總帳";

    [Theory]
    [InlineData("7")]
    [InlineData("-1")]
    [InlineData("x")]
    public async Task ReadRows_ValueNotInTheSharedStringTable_FailsNamingSheetAndCell(string value)
    {
        var path = new RawXlsxBuilder()
            .WithSharedStrings("<si><t>科目代號</t></si><si><t>現金</t></si>")
            .AddSheet(SheetName,
                $"""
                <sheetData>
                <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="inlineStr"><is><t>摘要</t></is></c><c r="C1" t="inlineStr"><is><t>備註</t></is></c></row>
                <row r="2"><c r="A2" t="s"><v>1</v></c><c r="B2" t="inlineStr"><is><t>第一筆</t></is></c></row>
                <row r="3"><c r="B3" t="inlineStr"><is><t>第二筆</t></is></c><c r="C3" t="s"><v>{value}</v></c></row>
                </sheetData>
                """)
            .Save();
        try
        {
            var error = await Assert.ThrowsAsync<JetActionException>(() => ReadAllRowsAsync(path));

            Assert.Equal(JetErrorCodes.FileReadError, error.Code);
            Assert.Equal(ExpectedMessage(path, "C3"), error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadColumnsAndInspect_HeaderCellNotInTheSharedStringTable_FailNamingSheetAndCell()
    {
        var path = new RawXlsxBuilder()
            .WithSharedStrings("<si><t>科目代號</t></si>")
            .AddSheet(SheetName,
                """
                <sheetData>
                <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>5</v></c></row>
                <row r="2"><c r="A2" t="inlineStr"><is><t>1000</t></is></c></row>
                </sheetData>
                """)
            .Save();
        try
        {
            var reader = new OpenXmlSaxTableReader();

            var columns = await Assert.ThrowsAsync<JetActionException>(
                () => reader.ReadColumnsAsync(new TabularSourceRequest(path), CancellationToken.None));
            var inspect = await Assert.ThrowsAsync<JetActionException>(
                () => reader.InspectAsync(path, CancellationToken.None));

            Assert.Equal(JetErrorCodes.FileReadError, columns.Code);
            Assert.Equal(ExpectedMessage(path, "B1"), columns.Message);
            Assert.Equal(JetErrorCodes.FileReadError, inspect.Code);
            Assert.Equal(ExpectedMessage(path, "B1"), inspect.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadRows_WorkbookWithoutSharedStringTable_FailsNamingSheetAndCell()
    {
        var path = new RawXlsxBuilder()
            .AddSheet(SheetName,
                """
                <sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>科目代號</t></is></c></row>
                <row r="2"><c r="A2" t="s"><v>0</v></c></row>
                </sheetData>
                """)
            .Save();
        try
        {
            var error = await Assert.ThrowsAsync<JetActionException>(() => ReadAllRowsAsync(path));

            Assert.Equal(JetErrorCodes.FileReadError, error.Code);
            Assert.Equal(ExpectedMessage(path, "A2"), error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadRows_RowsAndCellsWithoutReferences_NameTheCountedAddress()
    {
        var path = new RawXlsxBuilder()
            .WithSharedStrings("<si><t>科目代號</t></si><si><t>摘要</t></si>")
            .AddSheet(SheetName,
                """
                <sheetData>
                <row><c t="s"><v>0</v></c><c t="s"><v>1</v></c></row>
                <row><c t="inlineStr"><is><t>1000</t></is></c><c t="s"><v>9</v></c></row>
                </sheetData>
                """)
            .Save();
        try
        {
            var error = await Assert.ThrowsAsync<JetActionException>(() => ReadAllRowsAsync(path));

            Assert.Equal(ExpectedMessage(path, "B2"), error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadRows_SharedStringCellWithoutValue_StillReadsBlank()
    {
        var path = new RawXlsxBuilder()
            .WithSharedStrings("<si><t>科目代號</t></si><si><t>摘要</t></si><si><t>1000</t></si>")
            .AddSheet(SheetName,
                """
                <sheetData>
                <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c></row>
                <row r="2"><c r="A2" t="s"><v>2</v></c><c r="B2" t="s"/></row>
                </sheetData>
                """)
            .Save();
        try
        {
            var row = Assert.Single(await ReadAllRowsAsync(path));

            Assert.Equal("1000", row.Values["科目代號"]);
            Assert.False(row.Values.ContainsKey("摘要"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>給審計員看的句子逐字寫在這裡，當成獨立的預期值。</summary>
    private static string ExpectedMessage(string path, string cell) =>
        $"檔案「{Path.GetFileName(path)}」工作表「{SheetName}」的儲存格 {cell} 讀不到文字內容，檔案可能已損壞。"
        + $"請用 Excel 開啟這個檔案確認 {cell} 的內容，另存新檔後再匯入。";

    private static async Task<List<StagingRow>> ReadAllRowsAsync(string path)
    {
        var rows = new List<StagingRow>();
        await foreach (var row in new OpenXmlSaxTableReader().ReadRowsAsync(
            new TabularSourceRequest(path), CancellationToken.None))
        {
            rows.Add(row);
        }

        return rows;
    }
}
