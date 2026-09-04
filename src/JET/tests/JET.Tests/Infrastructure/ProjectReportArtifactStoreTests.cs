using System.Text.RegularExpressions;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Application;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 報告檔儲存的最小單元測試（2026-09-02 重寫後）。只涵蓋這個 store 自己的承諾：寫壞或取消不留暫存檔、
/// Working Paper 每次新檔、其餘覆蓋同名檔、舊 manifest 仍能列出、檔案狀態如實回報、殘留的舊控制檔
/// 直接清掉。不核對內容、不擋載入，那些是它刻意不做的事。
/// </summary>
public sealed class ProjectReportArtifactStoreTests
{
    private const string ProjectId = "store-unit-project";

    [Fact]
    public async Task WriteAsync_ContentWriterThrows_LeavesNoTemporaryFileAndNoManifest()
    {
        using var root = new TempProjectRoot();
        var (store, directory) = Create(root);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(
            ProjectId,
            Request(ReportArtifactKind.ValidationReport, async (output, cancellationToken) =>
            {
                await output.WriteAsync(new byte[] { 1, 2, 3 }, cancellationToken);
                throw new InvalidOperationException("writer failed halfway");
            }),
            CancellationToken.None));

        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        Assert.Empty(Directory.GetFiles(directory, "*.xlsx"));
        Assert.False(File.Exists(Path.Combine(directory, ProjectReportArtifactStore.ManifestFileName)));
        Assert.Empty(await store.ListAsync(ProjectId, CancellationToken.None));
    }

    [Fact]
    public async Task WriteAsync_CancelledInsideWriter_LeavesNoTemporaryFileAndKeepsPriorArtifact()
    {
        using var root = new TempProjectRoot();
        var (store, directory) = Create(root);
        var prior = await store.WriteAsync(
            ProjectId,
            Request(ReportArtifactKind.InfReport, Bytes(7, 7, 7)),
            CancellationToken.None);
        var priorPath = Path.Combine(directory, prior.RelativeFileName);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync(
            ProjectId,
            Request(ReportArtifactKind.InfReport, async (output, cancellationToken) =>
            {
                await output.WriteAsync(new byte[] { 9 }, cancellationToken);
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }),
            cancellation.Token));

        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        Assert.Equal(new byte[] { 7, 7, 7 }, await File.ReadAllBytesAsync(priorPath));
        var listed = Assert.Single(await store.ListAsync(ProjectId, CancellationToken.None));
        Assert.Equal(prior.ArtifactId, listed.ArtifactId);
        Assert.Equal(ReportArtifactFileState.AsPublished, listed.FileState);
    }

    [Fact]
    public async Task WriteAsync_WorkingPaper_WritesNewVersionFileEachTimeAndKeepsOldVersions()
    {
        using var root = new TempProjectRoot();
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 9, 2, 1, 2, 3, TimeSpan.Zero));
        var (store, directory) = Create(root, time);

        var first = await store.WriteAsync(ProjectId, Request(ReportArtifactKind.WorkingPaper, Bytes(1)), CancellationToken.None);
        var second = await store.WriteAsync(ProjectId, Request(ReportArtifactKind.WorkingPaper, Bytes(2)), CancellationToken.None);
        time.Now = time.Now.AddMinutes(1);
        var third = await store.WriteAsync(ProjectId, Request(ReportArtifactKind.WorkingPaper, Bytes(3)), CancellationToken.None);

        var prefix = ProjectFileNames.SafePrefix(ProjectId);
        var pattern = new Regex($"^{Regex.Escape(prefix)}_WorkingPaper_\\d{{8}}-\\d{{6}}(-\\d+)?\\.xlsx$");
        foreach (var artifact in new[] { first, second, third })
        {
            Assert.Matches(pattern, artifact.RelativeFileName);
            Assert.True(File.Exists(Path.Combine(directory, artifact.RelativeFileName)));
        }

        // 同一秒內第二次匯出補流水號；時間走了以後又回到純時間戳。
        Assert.NotEqual(first.RelativeFileName, second.RelativeFileName);
        Assert.NotEqual(second.RelativeFileName, third.RelativeFileName);
        Assert.Equal(3, Directory.GetFiles(directory, "*_WorkingPaper_*.xlsx").Length);
        Assert.Equal(new byte[] { 1 }, await File.ReadAllBytesAsync(Path.Combine(directory, first.RelativeFileName)));

        var listed = await store.ListAsync(ProjectId, CancellationToken.None);
        Assert.Equal(
            new[] { first.ArtifactId, second.ArtifactId, third.ArtifactId }.Order(StringComparer.Ordinal),
            listed.Select(artifact => artifact.ArtifactId).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(ReportArtifactKind.ValidationReport, "ValidationReport")]
    [InlineData(ReportArtifactKind.InfReport, "INFReport")]
    [InlineData(ReportArtifactKind.PrescreenReport, "PrescreeningReport")]
    [InlineData(ReportArtifactKind.CriteriaSelectionReport, "CriteriaSelectionReport")]
    public async Task WriteAsync_OtherReportKinds_OverwriteSameFileNameAndKeepOneEntryPerKind(
        ReportArtifactKind kind, string fileSuffix)
    {
        using var root = new TempProjectRoot();
        var (store, directory) = Create(root);

        var first = await store.WriteAsync(ProjectId, Request(kind, Bytes(1)), CancellationToken.None);
        var second = await store.WriteAsync(ProjectId, Request(kind, Bytes(2, 2)), CancellationToken.None);

        Assert.Equal(first.RelativeFileName, second.RelativeFileName);
        Assert.Equal($"{ProjectFileNames.SafePrefix(ProjectId)}_{fileSuffix}.xlsx", second.RelativeFileName);
        Assert.Single(Directory.GetFiles(directory, "*.xlsx"));
        Assert.Equal(new byte[] { 2, 2 }, await File.ReadAllBytesAsync(Path.Combine(directory, second.RelativeFileName)));

        var listed = Assert.Single(await store.ListAsync(ProjectId, CancellationToken.None));
        Assert.Equal(second.ArtifactId, listed.ArtifactId);
        Assert.Equal(2, listed.Bytes);
    }

    [Fact]
    public async Task WriteAsync_LockedReport_KeepsPriorFileAndSucceedsAfterRelease()
    {
        using var root = new TempProjectRoot();
        var (store, directory) = Create(root);
        var first = await store.WriteAsync(ProjectId, Request(ReportArtifactKind.ValidationReport, Bytes(1, 2)), CancellationToken.None);
        var path = Path.Combine(directory, first.RelativeFileName);

        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = await Assert.ThrowsAsync<JetActionException>(() => store.WriteAsync(
                ProjectId, Request(ReportArtifactKind.ValidationReport, Bytes(3, 4)), CancellationToken.None));
            Assert.Equal(JetErrorCodes.FileReadError, error.Code);
            Assert.Contains("關閉", error.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }

        Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(path));
        Assert.Equal(first.ArtifactId, Assert.Single(await store.ListAsync(ProjectId, CancellationToken.None)).ArtifactId);
        var retried = await store.WriteAsync(ProjectId, Request(ReportArtifactKind.ValidationReport, Bytes(3, 4)), CancellationToken.None);
        Assert.Equal(first.RelativeFileName, retried.RelativeFileName);
        Assert.Equal(new byte[] { 3, 4 }, await File.ReadAllBytesAsync(path));
        Assert.Equal(ReportArtifactFileState.AsPublished, Assert.Single(await store.ListAsync(ProjectId, CancellationToken.None)).FileState);
    }

    [Fact]
    public async Task ListAsync_SameLengthEdit_UsesWriteTimeAndReexportClearsWarning()
    {
        using var root = new TempProjectRoot();
        var (store, directory) = Create(root);
        var first = await store.WriteAsync(ProjectId, Request(ReportArtifactKind.InfReport, Bytes(1, 2)), CancellationToken.None);
        var path = Path.Combine(directory, first.RelativeFileName);
        await File.WriteAllBytesAsync(path, new byte[] { 3, 4 });
        File.SetLastWriteTimeUtc(path, first.LastWriteUtc!.Value.UtcDateTime.AddMinutes(1));
        Assert.Equal(first.Bytes, new FileInfo(path).Length);
        Assert.Equal(ReportArtifactFileState.ModifiedOutside, Assert.Single(await store.ListAsync(ProjectId, CancellationToken.None)).FileState);

        await store.WriteAsync(ProjectId, Request(ReportArtifactKind.InfReport, Bytes(5, 6)), CancellationToken.None);
        Assert.Equal(new byte[] { 5, 6 }, await File.ReadAllBytesAsync(path));
        Assert.Equal(ReportArtifactFileState.AsPublished, Assert.Single(await store.ListAsync(ProjectId, CancellationToken.None)).FileState);
    }

    [Fact]
    public async Task ListAsync_ReportsFileStateWithoutBlocking()
    {
        using var root = new TempProjectRoot();
        var (store, directory) = Create(root);
        var untouched = await store.WriteAsync(ProjectId, Request(ReportArtifactKind.ValidationReport, Bytes(1)), CancellationToken.None);
        var edited = await store.WriteAsync(ProjectId, Request(ReportArtifactKind.InfReport, Bytes(1)), CancellationToken.None);
        var deleted = await store.WriteAsync(ProjectId, Request(ReportArtifactKind.PrescreenReport, Bytes(1)), CancellationToken.None);

        // 模擬審計員用 Excel 存檔（內容變長）與直接刪檔。
        await File.AppendAllTextAsync(Path.Combine(directory, edited.RelativeFileName), "edited outside JET");
        File.Delete(Path.Combine(directory, deleted.RelativeFileName));

        var listed = (await store.ListAsync(ProjectId, CancellationToken.None))
            .ToDictionary(artifact => artifact.ArtifactId);
        Assert.Equal(ReportArtifactFileState.AsPublished, listed[untouched.ArtifactId].FileState);
        Assert.Equal(ReportArtifactFileState.ModifiedOutside, listed[edited.ArtifactId].FileState);
        Assert.Equal(ReportArtifactFileState.Missing, listed[deleted.ArtifactId].FileState);
        Assert.Equal(3, listed.Count);
    }

    [Fact]
    public async Task ListAsync_LegacyManifest_IgnoresSha256AndAccountMappingEntries()
    {
        using var root = new TempProjectRoot();
        var (store, directory) = Create(root);
        var validationFile = "legacy_ValidationReport.xlsx";
        await File.WriteAllBytesAsync(Path.Combine(directory, validationFile), new byte[] { 5, 5, 5, 5 });
        await File.WriteAllTextAsync(
            Path.Combine(directory, ProjectReportArtifactStore.ManifestFileName),
            $$"""
            [
              {
                "artifactId": "0123456789abcdef0123456789abcdef",
                "kind": "validationReport",
                "relativeFileName": "{{validationFile}}",
                "sourceRefs": { "validationRunId": "run-1" },
                "createdUtc": "2026-08-01T00:00:00+00:00",
                "bytes": 4,
                "sha256": "{{new string('a', 64)}}",
                "stale": false
              },
              {
                "artifactId": "fedcba9876543210fedcba9876543210",
                "kind": "accountMapping",
                "relativeFileName": "legacy_AccountMapping.xlsx",
                "sourceRefs": { "validationRunId": "run-1" },
                "createdUtc": "2026-08-01T00:00:00+00:00",
                "bytes": 10,
                "sha256": "{{new string('b', 64)}}",
                "stale": false
              }
            ]
            """);

        var listed = Assert.Single(await store.ListAsync(ProjectId, CancellationToken.None));

        Assert.Equal("0123456789abcdef0123456789abcdef", listed.ArtifactId);
        Assert.Equal(ReportArtifactKind.ValidationReport, listed.Kind);
        Assert.Equal("run-1", listed.SourceRef.ValidationRunId);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), listed.GeneratedUtc);
        Assert.NotEqual(ReportArtifactFileState.Missing, listed.FileState);
    }

    [Fact]
    public async Task ListAsync_LeftoverLegacyJournal_IsDeletedAndLogged()
    {
        using var root = new TempProjectRoot();
        var logger = new RecordingLogger();
        var (store, directory) = Create(root, logger: logger);
        var journalPath = Path.Combine(directory, ProjectReportArtifactStore.JournalFileName);
        await File.WriteAllTextAsync(journalPath, """{ "formatVersion": 1, "operation": "writeBatch" }""");

        var listed = await store.ListAsync(ProjectId, CancellationToken.None);

        Assert.Empty(listed);
        Assert.False(File.Exists(journalPath));
        Assert.Contains("artifact.journal.discarded", logger.EventNames);
    }

    [Fact]
    public async Task ListAsync_UnreadableManifest_IsSetAsideAndListingContinuesEmpty()
    {
        using var root = new TempProjectRoot();
        var logger = new RecordingLogger();
        var (store, directory) = Create(root, logger: logger);
        var manifestPath = Path.Combine(directory, ProjectReportArtifactStore.ManifestFileName);
        await File.WriteAllTextAsync(manifestPath, "{");

        var listed = await store.ListAsync(ProjectId, CancellationToken.None);

        Assert.Empty(listed);
        Assert.False(File.Exists(manifestPath));
        Assert.Single(Directory.GetFiles(directory, "report-artifacts.unreadable-*.json"));
        Assert.Contains("artifact.manifest.reset", logger.EventNames);

        // 之後照常寫入，清單從空的重新開始。
        var written = await store.WriteAsync(ProjectId, Request(ReportArtifactKind.InfReport, Bytes(1)), CancellationToken.None);
        Assert.Equal(written.ArtifactId, Assert.Single(await store.ListAsync(ProjectId, CancellationToken.None)).ArtifactId);
    }

    private static (ProjectReportArtifactStore Store, string ProjectDirectory) Create(
        TempProjectRoot root,
        TimeProvider? timeProvider = null,
        ILogger<ProjectReportArtifactStore>? logger = null)
    {
        var folder = new JetProjectFolder(root.Path);
        var directory = folder.GetProjectDirectory(ProjectId);
        Directory.CreateDirectory(directory);
        return (new ProjectReportArtifactStore(folder, timeProvider, logger), directory);
    }

    private static ReportArtifactWriteRequest Request(
        ReportArtifactKind kind,
        ReportArtifactContentWriter writer) =>
        new(kind, new ReportArtifactSourceRefs(ValidationRunId: "run-1", PrescreenRunId: "prescreen-1", ScenarioRevision: "rev-1", ScenarioPositions: [1]), writer);

    private static ReportArtifactContentWriter Bytes(params byte[] content) =>
        (output, cancellationToken) => output.WriteAsync(content, cancellationToken).AsTask();

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingLogger : ILogger<ProjectReportArtifactStore>
    {
        public List<string> EventNames { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Name is not null)
            {
                EventNames.Add(eventId.Name);
            }
        }
    }
}
