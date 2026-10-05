using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;
using static JET.Tests.Application.WorkpaperExportTestSupport;

namespace JET.Tests.Application;

/// <summary>WorkingPaper 匯出：條件篩選報告與目前來源不符時，不提供底稿資料。</summary>
public sealed class WorkpaperExportCriteriaSourceMismatchTests
{
    [Theory]
    [InlineData("validationRun")]
    [InlineData("revision")]
    [InlineData("positions")]
    public async Task Export_CriteriaArtifactDoesNotMatchCompleteCurrentSource_DoesNotSupplyWorkpaperData(
        string variant)
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host, publishCriteria: false);
        var source = new ReportArtifactSourceRefs(
            variant == "validationRun" ? new string('a', 32) : prepared.ValidationRunId,
            prepared.PrescreenRunId,
            variant == "revision" ? "outdated-revision" : prepared.Revision,
            variant == "positions" ? [1] : prepared.Positions);
        await ArtifactStore(host).WriteAsync(
            prepared.ProjectId,
            new ReportArtifactWriteRequest(
                ReportArtifactKind.CriteriaSelectionReport,
                source,
                (output, cancellationToken) => output.WriteAsync(
                    new byte[] { 1 },
                    cancellationToken).AsTask()),
            CancellationToken.None);

        var response = await host.DispatchAsync("export.workpaperStream", Payload(prepared));
        AssertCurrentWorkpaper(host, prepared, response);
        var unchanged = Assert.Single(Directory.GetFiles(Path.Combine(host.ProjectsRoot, prepared.ProjectId), "*_CriteriaSelectionReport.xlsx"));
        Assert.Equal(new byte[] { 1 }, await File.ReadAllBytesAsync(unchanged));
    }
}
