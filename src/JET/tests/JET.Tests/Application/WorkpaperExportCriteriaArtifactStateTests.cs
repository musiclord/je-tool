using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;
using static JET.Tests.Application.WorkpaperExportTestSupport;

namespace JET.Tests.Application;

/// <summary>WorkingPaper 匯出：條件篩選報告缺少、過期或檔案已刪除時，仍產出目前底稿。</summary>
public sealed class WorkpaperExportCriteriaArtifactStateTests
{
    [Fact]
    public async Task Export_MissingCriteriaArtifact_ProducesCurrentWorkpaper()
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host, publishCriteria: false);

        var response = await host.DispatchAsync("export.workpaperStream", Payload(prepared));
        AssertCurrentWorkpaper(host, prepared, response);
        Assert.Empty(Directory.GetFiles(
            Path.Combine(host.ProjectsRoot, prepared.ProjectId),
            "*_CriteriaSelectionReport.xlsx"));
    }

    [Fact]
    public async Task Export_StaleCriteriaArtifact_ProducesCurrentWorkpaper()
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host);
        var store = ArtifactStore(host);
        await store.MarkStaleAsync(
            prepared.ProjectId,
            ReportArtifactKind.CriteriaSelectionReport,
            CancellationToken.None);

        var response = await host.DispatchAsync("export.workpaperStream", Payload(prepared));
        AssertCurrentWorkpaper(host, prepared, response);
        Assert.True(Assert.Single(await store.ListAsync(prepared.ProjectId, CancellationToken.None),
            item => item.Kind == ReportArtifactKind.CriteriaSelectionReport).Stale);
    }

    [Fact]
    public async Task Export_CriteriaArtifactFileWasDeleted_ProducesCurrentWorkpaperWithoutRecreatingIt()
    {
        using var host = new HandlerTestHost();
        var prepared = await PrepareAsync(host);
        var projectDirectory = Path.Combine(host.ProjectsRoot, prepared.ProjectId);
        var criteriaPath = Assert.Single(Directory.GetFiles(
            projectDirectory,
            "*_CriteriaSelectionReport.xlsx"));
        File.Delete(criteriaPath);

        var response = await host.DispatchAsync("export.workpaperStream", Payload(prepared));
        AssertCurrentWorkpaper(host, prepared, response);
        Assert.False(File.Exists(criteriaPath));
    }
}
