using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;
using static JET.Tests.Application.WorkingPaperExportTestSupport;

namespace JET.Tests.Application;

/// <summary>WorkingPaper 匯出：取消時清除暫存檔，並保留先前的報告檔與 manifest。</summary>
public sealed class WorkingPaperExportCancellationTests
{
    [Fact]
    public async Task Cancellation_CleansAllTemporaryAndUnindexedFiles()
    {
        using var source = new CancellationTokenSource();
        var publisher = new CancelOnFirstWorkingPaperSheetProgress(source);
        using var host = new HandlerTestHost(eventPublisher: publisher);
        var prepared = await PrepareAsync(host);
        var folder = Path.Combine(host.ProjectsRoot, prepared.ProjectId);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host.DispatchAsync("export.workingPaperStream", Payload(prepared), source.Token));

        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        Assert.Empty(FindWorkingPapers(folder));
        Assert.NotEmpty(publisher.WorkingPaperEvents);
        Assert.Equal("preparingData", publisher.WorkingPaperEvents[0].GetProperty("phase").GetString());
        Assert.Equal("writingSheet", publisher.WorkingPaperEvents[^1].GetProperty("phase").GetString());
        Assert.DoesNotContain(
            publisher.WorkingPaperEvents,
            update => update.GetProperty("phase").GetString() is "finalizingWorkbook" or "publishingReport");
    }

    [Fact]
    public async Task CancellationAtFinalizingProgress_PreservesPriorReportAndManifest()
    {
        using var source = new CancellationTokenSource();
        var publisher = new ArmableCancelOnWorkingPaperPhase(
            source,
            "finalizingWorkbook");
        using var host = new HandlerTestHost(eventPublisher: publisher);
        var prepared = await PrepareAsync(host);
        var folder = Path.Combine(host.ProjectsRoot, prepared.ProjectId);
        var first = await host.DispatchAsync(
            "export.workingPaperStream",
            Payload(prepared));
        var fileName = first.GetProperty("publishedReport").GetProperty("fileName").GetString()!;
        var reportPath = Path.Combine(folder, fileName);
        var manifestPath = Path.Combine(folder, ProjectReportStore.ManifestFileName);
        var reportBytes = await File.ReadAllBytesAsync(reportPath);
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
        var eventStart = publisher.WorkingPaperEvents.Count;
        publisher.Arm();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host.DispatchAsync(
                "export.workingPaperStream",
                Payload(prepared),
                source.Token));

        Assert.Equal(reportBytes, await File.ReadAllBytesAsync(reportPath));
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(manifestPath));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp", SearchOption.AllDirectories));
        // 確認現行的暫存檔都沒有留下。
        ReportStoreTempFiles.AssertNoneLeft(folder);
        var cancelledEvents = publisher.WorkingPaperEvents.Skip(eventStart).ToArray();
        Assert.Contains(
            cancelledEvents,
            update => update.GetProperty("phase").GetString() == "finalizingWorkbook");
        Assert.DoesNotContain(
            cancelledEvents,
            update => update.GetProperty("phase").GetString() == "publishingReport");
    }
}
