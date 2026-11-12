using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.AuditCore;
using JET.Infrastructure;
using JET.Tests.Application;
using Xunit;
using static JET.Tests.Infrastructure.WorkingPaperWriterTestSupport;

namespace JET.Tests.Infrastructure;

/// <summary>底稿續頁的工作表保護：續頁只鎖和第一頁相同的資料區，其他沒寫值的格子和第一頁一樣可以填。</summary>
public sealed class WorkingPaperWriterContinuationProtectionTests
{
    private const uint ContinuationRowLimit = 25;

    // 列上限 25 時，第 2 頁的資料列如下：
    // step1 是第 20 到 25 列，step1-1 與 step1-3 是第 17 到 25 列，
    // step1-2 是第 12 到 23 列，step4 是第 13 到 25 列。
    // 下面的格子都在鎖定資料區外，或在最後一筆資料下面；第一頁這兩類位置都可以填。
    [Fact]
    public async Task ProtectedContinuationPages_UnwrittenCellsOutsideLockedArea_AreFillable()
    {
        using var host = new HandlerTestHost();
        using var document = await WriteContinuationWorkingPaperAsync(host);
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        var expectations = new (string Sheet, string[] References)[]
        {
            (Step1Sheet, ["A5", "B8", "A16", "G15", "A20", "G20", "A26", "B26", "F26", "XFD20"]),
            (Step11Sheet, ["A5", "A13", "F13", "G16", "A17", "G17", "A26", "B26", "XFD17"]),
            (Step12Sheet, ["A5", "A12", "I12", "A24", "B24", "XFD12"]),
            (Step13Sheet, ["A5", "A17", "H17", "A26", "B26", "XFD17"]),
            (Step4Sheet, ["A4", "A6", "A8", "A10", "G10", "G11", "V11", "B12", "V13", "A26", "O26", "XFD13"])
        };

        foreach (var (baseName, references) in expectations)
        {
            var name = ExcelWorksheetConstraints.ContinuationSheetName(baseName, 2);
            var worksheet = WorksheetFor(document, name);
            Assert.NotNull(worksheet.Elements<SheetProtection>().SingleOrDefault());
            Assert.All(references, reference =>
                Assert.False(IsCellLocked(workbookPart, worksheet, reference), $"{name}!{reference} 應可填。"));
        }
    }

    // 第一頁這兩段是範本原有的格子，照鎖定資料區鎖住；續頁沒有範本格，沒寫值的位置也要一樣鎖住。
    [Fact]
    public async Task ProtectedContinuationPages_Step1AndStep11HeaderRanges_StayLockedLikeFirstPage()
    {
        using var host = new HandlerTestHost();
        using var document = await WriteContinuationWorkingPaperAsync(host);
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        var headerRanges = new (string Sheet, string[] References)[]
        {
            (Step1Sheet, Cells(15, 19, 2, 6)),
            (Step11Sheet, Cells(12, 16, 2, 5))
        };

        foreach (var (baseName, references) in headerRanges)
        {
            foreach (var name in new[] { baseName, ExcelWorksheetConstraints.ContinuationSheetName(baseName, 2) })
            {
                var worksheet = WorksheetFor(document, name);
                Assert.All(references, reference =>
                    Assert.True(IsCellLocked(workbookPart, worksheet, reference), $"{name}!{reference} 應鎖定。"));
            }
        }
    }

    private static async Task<SpreadsheetDocument> WriteContinuationWorkingPaperAsync(HandlerTestHost host)
    {
        var projectId = await SetupContinuationProjectAsync(host);
        await ImportContinuationReferencesAsync(host);
        await host.DispatchAsync("filter.commit", ContinuationScenarioPayload());
        var completenessDiffs = DifferenceRows(26);
        var context = await CurrentContextForAsync(host, projectId);
        var plan = await CurrentPlanForAsync(host, context, completenessDiffs);
        await using var stream = new MemoryStream();
        await ((IWorkingPaperPlanWriter)BuildWriter(
                host,
                new WorkingPaperWriterOptions(ContinuationRowLimit),
                completenessDiffsOverride: completenessDiffs))
            .WriteAsync(stream, context, plan, CancellationToken.None);
        return OpenWorkingPaper(stream.ToArray());
    }
}
