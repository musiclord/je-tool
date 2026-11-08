using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class WorkingPaperExportCapacityJourneyTests
{
    // jet-guide section 7; action contract export.workingPaperStream.
    [Theory]
    [InlineData("sqlite")]
    public async Task ExportAtCapacity_ReturnsCompletePrunedCatalogAndKeepsExistingFiles(string provider)
    {
        using var host = new HandlerTestHost();
        var prepared = await ReportRevisionStateTests.PrepareAsync(host, provider);
        var folder = Path.Combine(host.ProjectsRoot, prepared.Id);
        await WriteHistoryAsync(folder, materialize: false);
        byte[] oldBytes = [5, 6, 7];
        await File.WriteAllBytesAsync(Path.Combine(folder, "history-0.xlsx"), oldBytes);
        Directory.CreateDirectory(Path.Combine(folder, "history-1.xlsx"));

        var response = await ExportAsync(host, prepared);
        var reports = response.GetProperty("reports").EnumerateArray().ToArray();
        Assert.Equal(3, reports.Length);
        Assert.Contains(reports, report => report.GetProperty("fileName").GetString() == "history-0.xlsx");
        Assert.Contains(reports, report => report.GetProperty("fileName").GetString() == "history-1.xlsx");
        var newId = response.GetProperty("publishedReport").GetProperty("reportId").GetString();
        Assert.Contains(reports, report => report.GetProperty("reportId").GetString() == newId);
        Assert.DoesNotContain(reports, report => report.GetProperty("fileName").GetString() == "history-2.xlsx");
        Assert.Equal(oldBytes, await File.ReadAllBytesAsync(Path.Combine(folder, "history-0.xlsx")));
        Assert.True(Directory.Exists(Path.Combine(folder, "history-1.xlsx")));
        Assert.Single(WorkingPaperExportTestSupport.FindWorkingPapers(folder));
        var reopened = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = prepared.Id }));
        Assert.Equal(reports.Select(Id).Order(), reopened.GetProperty("reports").EnumerateArray().Select(Id).Order());
    }

    [Theory]
    [InlineData("sqlite")]
    public async Task ExportAtCapacityWithExistingFiles_GuidesCleanupAndPreservesManifest(string provider)
    {
        using var host = new HandlerTestHost();
        var prepared = await ReportRevisionStateTests.PrepareAsync(host, provider);
        var folder = Path.Combine(host.ProjectsRoot, prepared.Id);
        var before = await WriteHistoryAsync(folder, materialize: true);

        var error = await Assert.ThrowsAsync<JetActionException>(() => ExportAsync(host, prepared));
        Assert.Equal(JetErrorCodes.FileReadError, error.Code);
        Assert.Contains("不需要的舊底稿", error.Message, StringComparison.Ordinal);
        // The spec requires an actionable retry instruction, not the literal word 重試.
        Assert.Contains("重新匯出", error.Message, StringComparison.Ordinal);
        Assert.Empty(WorkingPaperExportTestSupport.FindWorkingPapers(folder));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp", SearchOption.AllDirectories));
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(folder, ProjectReportStore.ManifestFileName)));
        Assert.Equal(4_096, Directory.GetFiles(folder, "history-*.xlsx").Length);
    }

    private static string? Id(JsonElement report) => report.GetProperty("reportId").GetString();
    private static Task<JsonElement> ExportAsync(HandlerTestHost host, ReportRevisionStateTests.Prepared prepared) =>
        host.DispatchAsync("export.workingPaperStream", JsonSerializer.Serialize(new
        { validationResultId = prepared.ValidationResultId, scenarioRevision = prepared.Revision, scenarioPositions = new[] { 1 } }));

    private static async Task<byte[]> WriteHistoryAsync(string folder, bool materialize)
    {
        var entries = Enumerable.Range(0, 4_096).Select(index => new
        {
            reportId = index.ToString("x32"), kind = "workingPaper", relativeFileName = $"history-{index}.xlsx",
            generatedUtc = "2026-09-01T00:00:00Z", bytes = 0, sourceRef = new { validationResultId = "old-run" }, stale = true
        }).ToArray();
        // Only empty files are needed; this does not generate thousands of workbooks.
        if (materialize) foreach (var entry in entries) File.WriteAllBytes(Path.Combine(folder, entry.relativeFileName), []);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entries);
        await File.WriteAllBytesAsync(Path.Combine(folder, ProjectReportStore.ManifestFileName), bytes);
        return bytes;
    }
}
