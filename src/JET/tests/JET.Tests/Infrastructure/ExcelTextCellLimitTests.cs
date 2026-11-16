using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Infrastructure;
using JET.Tests.Application;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Excel 一格最多 32,767 個字元。文字寫進格子時，跳脫前與跳脫後的長度都不超過這個上限；超過時寫到上限，
/// 結尾加上已截斷的註明，註明也算在上限內，並記下這張工作表截斷了幾格。成對的 surrogate 不會被切開。
/// 讀回寫出的文字用測試自己的還原方式，不用被測的 ExcelText.Decode。文字都是合成的。
/// </summary>
public sealed class ExcelTextCellLimitTests
{
    private const int Limit = 32_767;
    private const string Notice = "（JET 已截斷：超過 Excel 每格 32,767 字的上限）";
    private static readonly int Budget = Limit - Notice.Length;

    [Fact]
    public void ShortText_IsWrittenAsItIsAndNotCounted()
    {
        WithWorksheet(part =>
        {
            var written = ExcelText.InlineString("合成摘要\r\n第二行", part).InnerText;

            Assert.Equal("合成摘要\r\n第二行", ReportTextEncodingTests.Decode(written));
            Assert.Equal(0, ExcelText.TruncatedCellCount(part));
        });
    }

    [Fact]
    public void TextExactlyAtTheLimit_IsWrittenAsItIs()
    {
        WithWorksheet(part =>
        {
            var value = new string('甲', Limit);

            var written = ExcelText.InlineString(value, part).InnerText;

            Assert.Equal(value, written);
            Assert.Equal(0, ExcelText.TruncatedCellCount(part));
        });
    }

    [Fact]
    public void LongText_IsWrittenUpToTheLimitAndEndsWithTheNotice()
    {
        WithWorksheet(part =>
        {
            var value = new string('甲', 40_000);

            var written = ExcelText.InlineString(value, part).InnerText;

            Assert.Equal(Limit, written.Length);
            Assert.Equal(new string('甲', Budget) + Notice, ReportTextEncodingTests.Decode(written));
            Assert.Equal(1, ExcelText.TruncatedCellCount(part));
        });
    }

    [Fact]
    public void TextThatOnlyExceedsAfterEscaping_IsCutByItsWrittenLength()
    {
        WithWorksheet(part =>
        {
            // 5,000 個 CR 跳脫前只有 5,000 字，每個寫成 _x000D_ 後是 35,000 字。
            var value = new string('\r', 5_000);

            var written = ExcelText.InlineString(value, part).InnerText;

            Assert.True(written.Length <= Limit);
            Assert.Equal(new string('\r', Budget / 7) + Notice, ReportTextEncodingTests.Decode(written));
            Assert.Equal(1, ExcelText.TruncatedCellCount(part));
        });
    }

    [Fact]
    public void SurrogatePairCrossingTheCut_IsLeftOutWhole()
    {
        WithWorksheet(part =>
        {
            var value = new string('甲', Budget - 1) + "😀" + new string('乙', 10_000);

            var decoded = ReportTextEncodingTests.Decode(ExcelText.InlineString(value, part).InnerText);

            Assert.Equal(new string('甲', Budget - 1) + Notice, decoded);
        });
    }

    [Fact]
    public void SurrogatePairEndingAtTheCut_IsKept()
    {
        WithWorksheet(part =>
        {
            var value = new string('甲', Budget - 2) + "😀" + new string('乙', 10_000);

            var decoded = ReportTextEncodingTests.Decode(ExcelText.InlineString(value, part).InnerText);

            Assert.Equal(new string('甲', Budget - 2) + "😀" + Notice, decoded);
        });
    }

    [Fact]
    public void TextThatLooksLikeAnEscape_IsCutByItsWrittenLength()
    {
        WithWorksheet(part =>
        {
            // 每段 _x0041_ 開頭的底線寫成 _x005F_，7 個字寫出 13 個字。
            var value = string.Concat(Enumerable.Repeat("_x0041_", 6_000));

            var written = ExcelText.InlineString(value, part).InnerText;
            var decoded = ReportTextEncodingTests.Decode(written);

            Assert.True(written.Length <= Limit);
            Assert.EndsWith(Notice, decoded, StringComparison.Ordinal);
            Assert.StartsWith(decoded[..^Notice.Length], value, StringComparison.Ordinal);
            Assert.True(decoded.Length - Notice.Length > Budget / 13 * 7 - 7);
        });
    }

    [Fact]
    public void InlineStringElement_CutsAndCountsTheSameWay()
    {
        WithWorksheet(part =>
        {
            XNamespace spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var value = new string('甲', 40_000);

            var element = ExcelText.InlineStringElement(spreadsheet, value, part);

            Assert.Equal(new string('甲', Budget) + Notice, ReportTextEncodingTests.Decode(element.Value));
            Assert.Equal(1, ExcelText.TruncatedCellCount(part));
        });
    }

    [Fact]
    public void EachWorksheetKeepsItsOwnCount()
    {
        WithWorksheet(first =>
        {
            var second = first.GetParentParts().OfType<WorkbookPart>().Single().AddNewPart<WorksheetPart>();
            var value = new string('甲', 40_000);

            ExcelText.InlineString(value, first);
            ExcelText.InlineString(value, first);
            ExcelText.InlineString(value, second);

            Assert.Equal(2, ExcelText.TruncatedCellCount(first));
            Assert.Equal(1, ExcelText.TruncatedCellCount(second));
        });
    }

    [Fact]
    public void VerbatimText_IsWrittenWholeWithoutTheNotice()
    {
        var value = new string('甲', 40_000);

        Assert.Equal(value, ExcelText.VerbatimInlineString(value).InnerText);
    }

    private static void WithWorksheet(Action<WorksheetPart> check)
    {
        using var stream = new MemoryStream();
        using var document = SpreadsheetDocument.Create(stream, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook(new Sheets());
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        worksheetPart.Worksheet = new Worksheet(new SheetData());
        check(worksheetPart);
    }
}
