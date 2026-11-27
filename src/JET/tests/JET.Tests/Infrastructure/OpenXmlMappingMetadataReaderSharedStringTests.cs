using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 由底稿匯入欄位時，共用字串格的編號找不到對應的文字，表示檔案已經損壞。讀取器停下來並寫出工作表與儲存格位址，
/// 不把這一格當成空白，免得隱藏紀錄被當成不存在，或對照表少了一列卻沒有人發現。活頁簿與欄名都是合成的。
/// </summary>
public sealed class OpenXmlMappingMetadataReaderSharedStringTests
{
    [Fact]
    public async Task ReadAsync_FieldInfoTableCellNotInTheSharedStringTable_FailsNamingSheetAndCell()
    {
        var path = new RawXlsxBuilder()
            .WithSharedStrings("<si><t>Doc No</t></si>")
            .AddSheet(MappingMetadataFormat.WorksheetName,
                """
                <sheetData>
                <row r="2"><c r="A2" t="inlineStr"><is><t>TB檔案配對前後欄位對照表</t></is></c></row>
                <row r="3"><c r="A3" t="inlineStr"><is><t>Acct</t></is></c><c r="B3" t="inlineStr"><is><t>文字型態</t></is></c><c r="E3" t="inlineStr"><is><t>會計科目編號_TB</t></is></c></row>
                <row r="5"><c r="A5" t="inlineStr"><is><t>GL檔案配對前後欄位對照表</t></is></c></row>
                <row r="6"><c r="A6" t="s"><v>0</v></c><c r="B6" t="inlineStr"><is><t>文字型態</t></is></c><c r="E6" t="inlineStr"><is><t>傳票號碼_JE</t></is></c></row>
                <row r="7"><c r="A7" t="s"><v>3</v></c><c r="B7" t="inlineStr"><is><t>數字型態</t></is></c><c r="E7" t="inlineStr"><is><t>傳票金額_JE</t></is></c></row>
                </sheetData>
                """)
            .Save();
        try
        {
            var error = await Assert.ThrowsAsync<JetActionException>(() =>
                new OpenXmlMappingMetadataReader().ReadAsync(path, CancellationToken.None));

            Assert.Equal(JetErrorCodes.FileReadError, error.Code);
            Assert.Equal(ExpectedMessage(MappingMetadataFormat.WorksheetName, "A7"), error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_HiddenMarkerCellNotInTheSharedStringTable_FailsNamingSheetAndCell()
    {
        var path = new RawXlsxBuilder()
            .WithSharedStrings("<si><t>JET_MAPPING_METADATA</t></si>")
            .AddSheet(MappingMetadataFormat.WorksheetName,
                """
                <sheetData>
                <row r="1"><c r="F1" t="s"><v>4</v></c><c r="G1"><v>2</v></c><c r="H1" t="inlineStr"><is><t>{}</t></is></c></row>
                </sheetData>
                """)
            .Save();
        try
        {
            var error = await Assert.ThrowsAsync<JetActionException>(() =>
                new OpenXmlMappingMetadataReader().ReadAsync(path, CancellationToken.None));

            Assert.Equal(JetErrorCodes.FileReadError, error.Code);
            Assert.Equal(ExpectedMessage(MappingMetadataFormat.WorksheetName, "F1"), error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_ReportMetadataCellNotInTheSharedStringTable_FailsNamingSheetAndCell()
    {
        var path = new RawXlsxBuilder()
            .AddSheet(ReportWorkbookMetadataFormat.WorksheetName,
                """
                <sheetData>
                <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1"><v>1</v></c><c r="C1"><v>1</v></c></row>
                </sheetData>
                """)
            .Save();
        try
        {
            var error = await Assert.ThrowsAsync<JetActionException>(() =>
                new OpenXmlMappingMetadataReader().ReadAsync(path, CancellationToken.None));

            Assert.Equal(JetErrorCodes.FileReadError, error.Code);
            Assert.Equal(ExpectedMessage(ReportWorkbookMetadataFormat.WorksheetName, "A1"), error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>給使用者看的句子逐字寫在這裡，當成獨立的預期值。</summary>
    private static string ExpectedMessage(string sheet, string cell) =>
        $"這份檔案的工作表「{sheet}」儲存格 {cell} 讀不到文字內容，檔案可能已損壞。"
        + "請用 Excel 開啟這個檔案確認內容，另存新檔後再選一次，或直接在下方選擇欄位。";
}
