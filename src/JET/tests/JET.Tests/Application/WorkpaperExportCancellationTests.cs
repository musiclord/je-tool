using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;
using static JET.Tests.Application.WorkpaperExportTestSupport;

namespace JET.Tests.Application;

/// <summary>WorkingPaper 匯出：取消時清除暫存檔，並保留先前的 artifact 與 manifest。</summary>
public sealed class WorkpaperExportCancellationTests
{
    [Fact]
    public async Task Cancellation_CleansAllTemporaryAndUnindexedFiles()
    {
        using var source = new CancellationTokenSource();
        var publisher = new CancelOnFirstWorkpaperSheetProgress(source);
        using var host = new HandlerTestHost(eventPublisher: publisher);
        var prepared = await PrepareAsync(host);
        var folder = Path.Combine(host.ProjectsRoot, prepared.ProjectId);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host.DispatchAsync("export.workpaperStream", Payload(prepared), source.Token));

        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        Assert.Empty(Directory.GetFiles(folder, "*_WorkingPaper.xlsx"));
        Assert.NotEmpty(publisher.WorkpaperEvents);
        Assert.Equal("preparingData", publisher.WorkpaperEvents[0].GetProperty("phase").GetString());
        Assert.Equal("writingSheet", publisher.WorkpaperEvents[^1].GetProperty("phase").GetString());
        Assert.DoesNotContain(
            publisher.WorkpaperEvents,
            update => update.GetProperty("phase").GetString() is "finalizingWorkbook" or "publishingArtifact");
    }

    [Fact]
    public async Task CancellationAtFinalizingProgress_PreservesPriorArtifactAndManifest()
    {
        using var source = new CancellationTokenSource();
        var publisher = new ArmableCancelOnWorkpaperPhase(
            source,
            "finalizingWorkbook");
        using var host = new HandlerTestHost(eventPublisher: publisher);
        var prepared = await PrepareAsync(host);
        var folder = Path.Combine(host.ProjectsRoot, prepared.ProjectId);
        var first = await host.DispatchAsync(
            "export.workpaperStream",
            Payload(prepared));
        var fileName = first.GetProperty("artifact").GetProperty("fileName").GetString()!;
        var artifactPath = Path.Combine(folder, fileName);
        var manifestPath = Path.Combine(folder, ProjectReportArtifactStore.ManifestFileName);
        var artifactBytes = await File.ReadAllBytesAsync(artifactPath);
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
        var eventStart = publisher.WorkpaperEvents.Count;
        publisher.Arm();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host.DispatchAsync(
                "export.workpaperStream",
                Payload(prepared),
                source.Token));

        Assert.Equal(artifactBytes, await File.ReadAllBytesAsync(artifactPath));
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(manifestPath));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(
            folder,
            ProjectReportArtifactStore.JournalFileName)));
        var cancelledEvents = publisher.WorkpaperEvents.Skip(eventStart).ToArray();
        Assert.Contains(
            cancelledEvents,
            update => update.GetProperty("phase").GetString() == "finalizingWorkbook");
        Assert.DoesNotContain(
            cancelledEvents,
            update => update.GetProperty("phase").GetString() == "publishingArtifact");
    }
}
