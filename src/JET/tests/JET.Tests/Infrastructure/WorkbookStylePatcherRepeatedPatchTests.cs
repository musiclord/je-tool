using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 底稿與報告每寫一格都會問一次樣式。同一個範本樣式配同一組修改，第二次起要直接回答，
/// 不能每一格都把整張樣式表列舉一遍；否則寫出時間會跟著「列數乘以樣式數」成長。
/// </summary>
public sealed class WorkbookStylePatcherRepeatedPatchTests
{
    private const int TemplateStyleCount = 400;
    private const int RepeatedCalls = 20_000;

    [Fact]
    public void Patch_RepeatedForTheSameStyleAndPatch_AnswersWithoutWalkingTheStyleTable()
    {
        using var stream = new MemoryStream();
        using var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook);
        var patcher = new WorkbookStylePatcher(CreateStylesPart(document));
        var borderless = new WorkbookStylePatch(Borderless: true);
        var unchanged = new WorkbookStylePatch();

        var derived = patcher.Patch(7U, borderless);
        var same = patcher.Patch(9U, unchanged);
        Assert.Equal((uint)TemplateStyleCount, derived);
        Assert.Equal(9U, same);

        var mismatches = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < RepeatedCalls; index++)
        {
            if (patcher.Patch(7U, borderless) != derived || patcher.Patch(9U, unchanged) != same)
            {
                mismatches++;
            }
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, mismatches);
        // 每次都列舉 400 個樣式的話，四萬次呼叫會配置超過一億位元組。
        Assert.True(
            allocated < 1_000_000,
            $"重複詢問同一個樣式 {RepeatedCalls * 2} 次配置了 {allocated} 位元組。");
    }

    [Fact]
    public void Patch_StillRejectsAStyleTheTemplateDoesNotHave_AfterOtherStylesWereAnswered()
    {
        using var stream = new MemoryStream();
        using var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook);
        var patcher = new WorkbookStylePatcher(CreateStylesPart(document));
        patcher.Patch(7U, new WorkbookStylePatch(Borderless: true));

        Assert.Throws<InvalidDataException>(
            () => patcher.Patch(5_000U, new WorkbookStylePatch(Borderless: true)));
        Assert.Throws<InvalidDataException>(
            () => patcher.Patch(5_000U, new WorkbookStylePatch()));
        Assert.Throws<ArgumentException>(
            () => patcher.Patch(7U, new WorkbookStylePatch(FontName: " ")));
    }

    private static WorkbookStylesPart CreateStylesPart(SpreadsheetDocument document)
    {
        var stylesPart = document.AddWorkbookPart().AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = new Stylesheet(
            new Fonts(new Font()),
            new Fills(new Fill()),
            new Borders(new Border()),
            new CellStyleFormats(new CellFormat()),
            new CellFormats(
                Enumerable.Range(0, TemplateStyleCount)
                    .Select(_ => (OpenXmlElement)new CellFormat { FontId = 0, FillId = 0, BorderId = 0 })));
        stylesPart.Stylesheet.Save();
        return stylesPart;
    }
}
