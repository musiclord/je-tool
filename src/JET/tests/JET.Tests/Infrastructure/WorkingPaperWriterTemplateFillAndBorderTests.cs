using System.Globalization;
using System.Text.Json;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Application;
using Xunit;
using static JET.Tests.Infrastructure.WorkingPaperWriterTestSupport;

namespace JET.Tests.Infrastructure;

/// <summary>底稿寫出器：資料列保留範本的底色與框線。</summary>
public sealed class WorkingPaperWriterTemplateFillAndBorderTests
{
    [Fact]
    public async Task InfSamplingToHighRiskVoucher_DynamicDataCells_PreserveTemplateFillAndBorders()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("validate.run");
        await host.DispatchAsync("filter.commit", ConditionColumnScenarioPayload());
        var outputBytes = await WriteToBytesAsync(host, ctx.ProjectId);
        var templatePath = Path.Combine(
            AppContext.BaseDirectory,
            "Templates",
            "WorkingPaper.xlsx");

        using var template = SpreadsheetDocument.Open(templatePath, false);
        using var output = SpreadsheetDocument.Open(
            new MemoryStream(outputBytes, writable: false),
            false);

        AssertTemplateFillAndBorder(
            template,
            output,
            InfSamplingSheet,
            ["A53", "F53", "M53", "T53"]);
        AssertTemplateFillAndBorder(
            template,
            output,
            HighRiskSummarySheet,
            ["B19", "C19", "D19", "E19"]);
        AssertTemplateFillAndBorder(
            template,
            output,
            HighRiskVoucherSheet,
            ["A13", "E13", "F13", "O13"]);
    }

    [Fact]
    public async Task HighRiskVoucher_ContinuationFirstDataRow_PreservesTemplateFillAndBorders()
    {
        const int voucherCount = 9;
        const uint continuationRowLimit = 20;
        using var host = new HandlerTestHost();
        var projectId = await SetupContinuationProjectAsync(host, voucherCount);
        await host.DispatchAsync("filter.commit", ContinuationScenarioPayload());

        await using var stream = new MemoryStream();
        var context = await CurrentContextForAsync(host, projectId);
        var plan = await CurrentPlanForAsync(host, context);
        var writer = (IWorkingPaperPlanWriter)BuildWriter(
            host,
            new WorkingPaperWriterOptions(continuationRowLimit));
        var stats = await writer.WriteAsync(
            stream,
            context,
            plan,
            CancellationToken.None);
        var continuationSheetName = ExcelWorksheetConstraints.ContinuationSheetName(HighRiskVoucherSheet, 2);
        var continuationStats = Assert.Single(
            stats.SheetStats,
            stat => string.Equals(
                stat.SheetName,
                continuationSheetName,
                StringComparison.Ordinal));
        Assert.Equal(1L, continuationStats.RowsWritten);

        var templatePath = Path.Combine(
            AppContext.BaseDirectory,
            "Templates",
            "WorkingPaper.xlsx");
        using var template = SpreadsheetDocument.Open(templatePath, false);
        stream.Position = 0;
        using var output = SpreadsheetDocument.Open(stream, false);

        AssertTemplateFillAndBorder(
            template,
            output,
            HighRiskVoucherSheet,
            ["A13", "E13", "F13", "O13", "P13", "U13"],
            continuationSheetName);
        AssertTemplateCellFormats(
            template,
            output,
            HighRiskVoucherSheet,
            ["A1", "A5", "B5", "A11", "E11"],
            continuationSheetName);
    }

    [Fact]
    public async Task HighRiskVoucher_FirstPageRowsBeyondTemplateExtent_PreserveTemplateFillAndBorders()
    {
        const int voucherCount = 989;
        using var host = new HandlerTestHost();
        var projectId = await SetupContinuationProjectAsync(host, voucherCount);
        await host.DispatchAsync("filter.commit", ContinuationScenarioPayload());
        var outputBytes = await WriteToBytesAsync(host, projectId);
        var templatePath = Path.Combine(
            AppContext.BaseDirectory,
            "Templates",
            "WorkingPaper.xlsx");

        using var template = SpreadsheetDocument.Open(templatePath, false);
        using var output = SpreadsheetDocument.Open(
            new MemoryStream(outputBytes, writable: false),
            false);
        foreach (var column in new[] { "A", "E", "F", "P", "U" })
        {
            AssertTemplateFillAndBorderAt(
                template,
                output,
                HighRiskVoucherSheet,
                expectedReference: $"{column}13",
                outputReference: $"{column}1001");
        }
    }
}
