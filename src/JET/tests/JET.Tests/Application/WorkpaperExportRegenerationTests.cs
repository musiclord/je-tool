using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;
using static JET.Tests.Application.WorkpaperExportTestSupport;

namespace JET.Tests.Application;

/// <summary>WorkingPaper 匯出：驗證摘要筆數改變時目錄與指紋不變，並在寫出前重建情境矩陣。</summary>
public sealed class WorkpaperExportRegenerationTests
{
    [Fact]
    public async Task ValidationNullSummary_1234289_PreservesWorkpaperCatalogAndNormalizedFingerprint()
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host);

        var baselineResponse = await host.DispatchAsync(
            "export.workpaperStream",
            Payload(prepared));
        var baselinePath = Path.Combine(
            host.ProjectsRoot,
            prepared.ProjectId,
            baselineResponse.GetProperty("artifact").GetProperty("fileName").GetString()!);
        var baseline = NormalizedOpenXmlWorkbookSnapshot.Capture(baselinePath);
        using var baselineWorkbook = new XLWorkbook(baselinePath);
        var baselineCatalog = baselineWorkbook.Worksheets
            .Select(sheet => sheet.Name)
            .ToArray();

        var counts = new[] { 1_234_000, 100, 100, 50, 39 };
        Assert.Equal(1_234_289, counts.Sum());
        await ReplaceValidationNullSummaryAsync(
            host,
            prepared.ProjectId,
            prepared.ValidationRunId,
            counts);

        var hugeResponse = await host.DispatchAsync(
            "export.workpaperStream",
            Payload(prepared));
        var hugePath = Path.Combine(
            host.ProjectsRoot,
            prepared.ProjectId,
            hugeResponse.GetProperty("artifact").GetProperty("fileName").GetString()!);
        var huge = NormalizedOpenXmlWorkbookSnapshot.Capture(hugePath);
        using var hugeWorkbook = new XLWorkbook(hugePath);

        Assert.Equal(baseline.Fingerprint, huge.Fingerprint);
        Assert.Equal(
            baselineCatalog,
            hugeWorkbook.Worksheets.Select(sheet => sheet.Name).ToArray());
    }

    [Fact]
    public async Task Export_RematerializesMatrixBeforeWriting()
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host);
        await DemoProjectPipeline.QueryScalarAsync(
            host, prepared.ProjectId, "DELETE FROM result_filter_run; SELECT changes();");

        await host.DispatchAsync("export.workpaperStream", Payload(prepared));

        var count = await DemoProjectPipeline.QueryScalarAsync(
            host, prepared.ProjectId, "SELECT COUNT(*) FROM result_filter_run;");
        Assert.True(count > 0);
    }
}
