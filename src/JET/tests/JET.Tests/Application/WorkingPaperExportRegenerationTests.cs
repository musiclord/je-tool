using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;
using static JET.Tests.Application.WorkingPaperExportTestSupport;

namespace JET.Tests.Application;

/// <summary>WorkingPaper 匯出：驗證摘要筆數改變時目錄與指紋不變，並在勾稽結果過期時於寫出前重建情境矩陣。</summary>
public sealed class WorkingPaperExportRegenerationTests
{
    [Fact]
    public async Task ValidationBlankSummary_1234289_PreservesWorkingPaperCatalogAndNormalizedFingerprint()
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host);

        var baselineResponse = await host.DispatchAsync(
            "export.workingPaperStream",
            Payload(prepared));
        var baselinePath = Path.Combine(
            host.ProjectsRoot,
            prepared.ProjectId,
            baselineResponse.GetProperty("publishedReport").GetProperty("fileName").GetString()!);
        var baseline = NormalizedOpenXmlWorkbookSnapshot.Capture(baselinePath);
        using var baselineWorkbook = new XLWorkbook(baselinePath);
        var baselineCatalog = baselineWorkbook.Worksheets
            .Select(sheet => sheet.Name)
            .ToArray();

        var counts = new[] { 1_234_000, 100, 100, 50, 39 };
        Assert.Equal(1_234_289, counts.Sum());
        await ReplaceValidationBlankSummaryAsync(
            host,
            prepared.ProjectId,
            prepared.ValidationResultId,
            counts);

        var hugeResponse = await host.DispatchAsync(
            "export.workingPaperStream",
            Payload(prepared));
        var hugePath = Path.Combine(
            host.ProjectsRoot,
            prepared.ProjectId,
            hugeResponse.GetProperty("publishedReport").GetProperty("fileName").GetString()!);
        var huge = NormalizedOpenXmlWorkbookSnapshot.Capture(hugePath);
        using var hugeWorkbook = new XLWorkbook(hugePath);

        Assert.Equal(baseline.Fingerprint, huge.Fingerprint);
        Assert.Equal(
            baselineCatalog,
            hugeWorkbook.Worksheets.Select(sheet => sheet.Name).ToArray());
    }

    [Fact]
    public async Task Export_RecomputesFilterHitsWhenResultsAreStale()
    {
        // 每個條件組儲存時就存好結果，匯出直接讀；只有勾稽結果標成過期時才重算，
        // 所以這裡除了刪掉儲存的結果，也同時標記過期。不重算的情形見 FilterHitPerScenarioTests。
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host);
        await DemoProjectPipeline.QueryScalarAsync(
            host, prepared.ProjectId,
            "DELETE FROM filter_hit; UPDATE result_stale_state SET filter_stale = 1 WHERE singleton = 1; SELECT changes();");

        await host.DispatchAsync("export.workingPaperStream", Payload(prepared));

        var count = await DemoProjectPipeline.QueryScalarAsync(
            host, prepared.ProjectId, "SELECT COUNT(*) FROM filter_hit;");
        Assert.True(count > 0);
    }
}
