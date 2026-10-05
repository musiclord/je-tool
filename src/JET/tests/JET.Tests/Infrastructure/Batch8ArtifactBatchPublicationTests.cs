using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class Batch8ArtifactBatchPublicationTests
{
    private const string ProjectId = "batch8";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishFailure_RestoresBothPriorReportsAndExactManifest(bool lockManifest)
    {
        using var root = new TempProjectRoot();
        var (store, directory) = Create(root);
        var original = await store.WriteBatchAsync(ProjectId, Requests("old", [1, 2], [3, 4]), CancellationToken.None);
        var validationPath = Path.Combine(directory, original[0].RelativeFileName);
        var infPath = Path.Combine(directory, original[1].RelativeFileName);
        var manifestPath = Path.Combine(directory, ProjectReportArtifactStore.ManifestFileName);
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath);

        using (var locked = new FileStream(lockManifest ? manifestPath : infPath,
                   FileMode.Open, FileAccess.Read, lockManifest ? FileShare.Read : FileShare.None))
        {
            var error = await Assert.ThrowsAsync<JetActionException>(() => store.WriteBatchAsync(
                ProjectId, Requests("new", [5, 6, 7], [8, 9, 10]), CancellationToken.None));
            Assert.Equal(JetErrorCodes.FileReadError, error.Code);
            Assert.Contains("關閉", error.Message, StringComparison.Ordinal);
        }

        Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(validationPath));
        Assert.Equal(new byte[] { 3, 4 }, await File.ReadAllBytesAsync(infPath));
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(manifestPath));
        var listed = await store.ListAsync(ProjectId, CancellationToken.None);
        Assert.Equal(original.Select(item => item.ArtifactId), listed.Select(item => item.ArtifactId));
        Assert.All(listed, item => Assert.Equal(ReportArtifactFileState.AsPublished, item.FileState));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));

        var retried = await store.WriteBatchAsync(ProjectId, Requests("new", [5, 6, 7], [8, 9, 10]), CancellationToken.None);
        Assert.Equal(new byte[] { 5, 6, 7 }, await File.ReadAllBytesAsync(validationPath));
        Assert.Equal(new byte[] { 8, 9, 10 }, await File.ReadAllBytesAsync(infPath));
        Assert.Equal(retried.Select(item => item.ArtifactId),
            (await store.ListAsync(ProjectId, CancellationToken.None)).Select(item => item.ArtifactId));
        Assert.All(retried, item => Assert.Equal("new", item.SourceRef.ValidationRunId));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task FirstPublication_SecondDestinationBlocked_LeavesNoFirstReportOrManifest()
    {
        using var root = new TempProjectRoot();
        var (store, directory) = Create(root);
        var blocked = Path.Combine(directory, "batch8_INFReport.xlsx");
        Directory.CreateDirectory(blocked);

        var error = await Assert.ThrowsAsync<JetActionException>(() => store.WriteBatchAsync(
            ProjectId, Requests("new", [1, 2], [3, 4]), CancellationToken.None));

        Assert.Equal(JetErrorCodes.FileReadError, error.Code);
        Assert.False(File.Exists(Path.Combine(directory, "batch8_ValidationReport.xlsx")));
        Assert.False(File.Exists(Path.Combine(directory, ProjectReportArtifactStore.ManifestFileName)));
        Assert.Empty(await store.ListAsync(ProjectId, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        Directory.Delete(blocked);
        Assert.Equal(2, (await store.WriteBatchAsync(ProjectId, Requests("new", [1, 2], [3, 4]), CancellationToken.None)).Count);
    }

    [Fact]
    public async Task RollbackFailure_RetainsOriginalBackupAndExplainsRecoveryNeeded()
    {
        using var root = new TempProjectRoot();
        var (store, directory) = Create(root);
        var original = await store.WriteBatchAsync(ProjectId, Requests("old", [1, 2], [3, 4]), CancellationToken.None);
        var manifestPath = Path.Combine(directory, ProjectReportArtifactStore.ManifestFileName);
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
        var failingStore = new ProjectReportArtifactStore(new JetProjectFolder(root.Path), (source, destination, overwrite) =>
        {
            if (source.EndsWith(".tmp", StringComparison.Ordinal)
                && destination.EndsWith("_INFReport.xlsx", StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Synthetic second publication failure.");
            if (Path.GetFileName(source).StartsWith(".report-artifact-backup-", StringComparison.Ordinal)
                && destination.EndsWith("_ValidationReport.xlsx", StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Synthetic restore failure.");
            File.Move(source, destination, overwrite);
        });

        var error = await Assert.ThrowsAsync<JetActionException>(() => failingStore.WriteBatchAsync(
            ProjectId, Requests("new", [5, 6, 7], [8, 9, 10]), CancellationToken.None));

        Assert.Equal(JetErrorCodes.FileReadError, error.Code);
        Assert.Contains("檔案還原未完成，請先關閉報告再重試；原檔備份仍保留", error.Message, StringComparison.Ordinal);
        var backup = Assert.Single(Directory.GetFiles(directory, ".report-artifact-backup-*"));
        Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(backup));
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(manifestPath));
        Assert.Equal(new byte[] { 3, 4 }, await File.ReadAllBytesAsync(Path.Combine(directory, original[1].RelativeFileName)));
        _ = await store.ListAsync(ProjectId, CancellationToken.None);
        _ = await store.ListAsync(ProjectId, CancellationToken.None);
        Assert.True(File.Exists(backup)); // 讀取清單的舊暫存檔清理也不得刪除尚未還原的原檔。
        Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(backup));
    }

    [Fact]
    public async Task UnexpectedPublicationException_RestoresBatchAndPreservesOriginalException()
    {
        using var root = new TempProjectRoot();
        var (store, directory) = Create(root);
        var original = await store.WriteBatchAsync(ProjectId, Requests("old", [1, 2], [3, 4]), CancellationToken.None);
        var manifestPath = Path.Combine(directory, ProjectReportArtifactStore.ManifestFileName);
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
        var failure = new InvalidOperationException("Synthetic publication defect.");
        var failingStore = new ProjectReportArtifactStore(new JetProjectFolder(root.Path), (source, destination, overwrite) =>
        {
            if (source.EndsWith(".tmp", StringComparison.Ordinal)
                && destination.EndsWith("_INFReport.xlsx", StringComparison.Ordinal)) throw failure;
            File.Move(source, destination, overwrite);
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => failingStore.WriteBatchAsync(
            ProjectId, Requests("new", [5, 6, 7], [8, 9, 10]), CancellationToken.None));

        Assert.Same(failure, error);
        Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(Path.Combine(directory, original[0].RelativeFileName)));
        Assert.Equal(new byte[] { 3, 4 }, await File.ReadAllBytesAsync(Path.Combine(directory, original[1].RelativeFileName)));
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(manifestPath));
        Assert.All(await store.ListAsync(ProjectId, CancellationToken.None),
            artifact => Assert.Equal(ReportArtifactFileState.AsPublished, artifact.FileState));
        Assert.Empty(Directory.GetFiles(directory, ".report-artifact-backup-*"));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task UnexpectedRollbackException_PreservesBackupAndBothFailureCauses()
    {
        using var root = new TempProjectRoot();
        var (store, directory) = Create(root);
        var original = await store.WriteBatchAsync(ProjectId, Requests("old", [1, 2], [3, 4]), CancellationToken.None);
        var manifestPath = Path.Combine(directory, ProjectReportArtifactStore.ManifestFileName);
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
        var publicationFailure = new UnauthorizedAccessException("Synthetic publication failure.");
        var restoreFailure = new InvalidOperationException("Synthetic restore defect.");
        var failingStore = new ProjectReportArtifactStore(new JetProjectFolder(root.Path), (source, destination, overwrite) =>
        {
            if (source.EndsWith(".tmp", StringComparison.Ordinal)
                && destination.EndsWith("_INFReport.xlsx", StringComparison.Ordinal)) throw publicationFailure;
            if (Path.GetFileName(source).StartsWith(".report-artifact-backup-", StringComparison.Ordinal)
                && destination.EndsWith("_ValidationReport.xlsx", StringComparison.Ordinal)) throw restoreFailure;
            File.Move(source, destination, overwrite);
        });

        var error = await Assert.ThrowsAsync<JetActionException>(() => failingStore.WriteBatchAsync(
            ProjectId, Requests("new", [5, 6, 7], [8, 9, 10]), CancellationToken.None));

        Assert.Equal(JetErrorCodes.FileReadError, error.Code);
        Assert.Contains("檔案還原未完成，請先關閉報告再重試；原檔備份仍保留", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Synthetic", error.Message, StringComparison.Ordinal);
        var causes = Assert.IsType<AggregateException>(error.InnerException).InnerExceptions;
        Assert.Equal(2, causes.Count);
        Assert.Same(publicationFailure, causes[0]);
        Assert.Same(restoreFailure, causes[1]);
        var backup = Assert.Single(Directory.GetFiles(directory, ".report-artifact-backup-*"));
        Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(backup));
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(manifestPath));
        Assert.Equal(new byte[] { 3, 4 }, await File.ReadAllBytesAsync(Path.Combine(directory, original[1].RelativeFileName)));
        _ = await store.ListAsync(ProjectId, CancellationToken.None);
        Assert.True(File.Exists(backup));
    }

    [Fact]
    public async Task CancellationAtPublishingBoundary_PreservesBothPriorReportsAndManifest()
    {
        using var root = new TempProjectRoot();
        var (store, directory) = Create(root);
        var original = await store.WriteBatchAsync(ProjectId, Requests("old", [1, 2], [3, 4]), CancellationToken.None);
        var manifestPath = Path.Combine(directory, ProjectReportArtifactStore.ManifestFileName);
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ((IReportArtifactPublishingStore)store).WriteBatchWithPublishingAsync(ProjectId,
                Requests("new", [5, 6], [7, 8]), _ => cancellation.Cancel(), cancellation.Token));

        Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(Path.Combine(directory, original[0].RelativeFileName)));
        Assert.Equal(new byte[] { 3, 4 }, await File.ReadAllBytesAsync(Path.Combine(directory, original[1].RelativeFileName)));
        Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(manifestPath));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    private static (ProjectReportArtifactStore Store, string Directory) Create(TempProjectRoot root)
    {
        var folder = new JetProjectFolder(root.Path);
        var directory = folder.GetProjectDirectory(ProjectId);
        Directory.CreateDirectory(directory);
        return (new ProjectReportArtifactStore(folder), directory);
    }

    private static ReportArtifactWriteRequest[] Requests(string runId, byte[] validation, byte[] inf) =>
    [
        new(ReportArtifactKind.ValidationReport, new ReportArtifactSourceRefs(ValidationRunId: runId),
            (output, token) => output.WriteAsync(validation, token).AsTask()),
        new(ReportArtifactKind.InfReport, new ReportArtifactSourceRefs(ValidationRunId: runId),
            (output, token) => output.WriteAsync(inf, token).AsTask())
    ];
}
