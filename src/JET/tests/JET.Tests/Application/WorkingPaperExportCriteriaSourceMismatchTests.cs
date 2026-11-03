using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;
using static JET.Tests.Application.WorkingPaperExportTestSupport;

namespace JET.Tests.Application;

/// <summary>WorkingPaper 匯出：條件篩選報告與目前來源不符時，不提供底稿資料。</summary>
public sealed class WorkingPaperExportCriteriaSourceMismatchTests
{
    [Theory]
    [InlineData("validationResult")]
    [InlineData("revision")]
    [InlineData("positions")]
    public async Task Export_CriteriaReportDoesNotMatchCompleteCurrentSource_DoesNotSupplyWorkingPaperData(
        string variant)
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host, publishCriteria: false);
        var source = new ReportSourceRefs(
            variant == "validationResult" ? new string('a', 32) : prepared.ValidationResultId,
            prepared.PrescreenResultId,
            variant == "revision" ? "outdated-revision" : prepared.Revision,
            variant == "positions" ? [1] : prepared.Positions);
        await ReportStore(host).WriteAsync(
            prepared.ProjectId,
            new ReportWriteRequest(
                ReportKind.CriteriaSelectionReport,
                source,
                (output, cancellationToken) => output.WriteAsync(
                    new byte[] { 1 },
                    cancellationToken).AsTask()),
            CancellationToken.None);

        var response = await host.DispatchAsync("export.workingPaperStream", Payload(prepared));
        AssertCurrentWorkingPaper(host, prepared, response);
        var unchanged = Assert.Single(Directory.GetFiles(Path.Combine(host.ProjectsRoot, prepared.ProjectId), "*_CriteriaSelectionReport.xlsx"));
        Assert.Equal(new byte[] { 1 }, await File.ReadAllBytesAsync(unchanged));
    }
}
