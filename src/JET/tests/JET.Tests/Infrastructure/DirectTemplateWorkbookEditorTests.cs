using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class DirectTemplateWorkbookEditorTests
{
    /// <summary>
    /// 帶 localSheetId 的定義名稱屬於一張工作表，值是那張工作表的位置，從 0 起算。
    /// 移除第二張工作表後，屬於它的名稱一起拿掉；第三張往前移到第二個位置，它的名稱改成 1；
    /// 第一張的名稱與不屬於任何工作表的名稱不變。
    /// </summary>
    [Fact]
    public void RemoveWorksheet_DropsItsLocalNamesAndMovesLaterOwnersUp()
    {
        using var stream = new MemoryStream();
        using var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        string[] names = ["合成一", "合成二", "合成三"];
        for (var index = 0; index < names.Length; index++)
        {
            var part = workbookPart.AddNewPart<WorksheetPart>();
            part.Worksheet = new Worksheet(new SheetData());
            part.Worksheet.Save();
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(part),
                SheetId = (uint)index + 1,
                Name = names[index]
            });
        }
        workbookPart.Workbook.Append(new DefinedNames(
            new DefinedName("'合成一'!$A$1:$B$1") { Name = "_xlnm._FilterDatabase", LocalSheetId = 0, Hidden = true },
            new DefinedName("'合成二'!$A$1:$B$1") { Name = "_xlnm._FilterDatabase", LocalSheetId = 1, Hidden = true },
            new DefinedName("'合成三'!$A$1:$C$3") { Name = "_xlnm.Print_Area", LocalSheetId = 2 },
            new DefinedName("'合成一'!$A$1") { Name = "合成合計" }));
        workbookPart.Workbook.Save();

        new DirectTemplateWorkbookEditor(document, ["合成二"]).RemoveWorksheet("合成二", CancellationToken.None);

        var workbook = workbookPart.RequiredWorkbook();
        Assert.Equal(["合成一", "合成三"], workbook.Sheets!.Elements<Sheet>().Select(sheet => sheet.Name?.Value));
        Assert.Equal(
            new (string?, uint?, string)[]
            {
                ("_xlnm._FilterDatabase", 0U, "'合成一'!$A$1:$B$1"),
                ("_xlnm.Print_Area", 1U, "'合成三'!$A$1:$C$3"),
                ("合成合計", null, "'合成一'!$A$1")
            },
            workbook.DefinedNames!.Elements<DefinedName>()
                .Select(name => (name.Name?.Value, name.LocalSheetId?.Value, name.Text)));
    }
}
