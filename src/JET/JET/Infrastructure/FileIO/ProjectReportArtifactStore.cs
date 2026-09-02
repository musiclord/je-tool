using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JET.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// 專案正式報告的檔案系統 transaction boundary。所有 manifest 讀寫、檔案發布、失效與清理
/// 都先取得 projects-root-local 的跨程序鎖，並先依 durable journal 完成 recovery。
/// </summary>
public sealed class ProjectReportArtifactStore : IReportArtifactStore, IReportArtifactPublishingStore
{
    public const string ManifestFileName = "report-artifacts.json";
    internal const string JournalFileName = ".report-artifacts.mutation-v1.json";
    internal const string AuditDirectoryName = "report-artifact-audit";
    internal const long MaxManifestBytes = 4L * 1024 * 1024;
    internal const int MaxManifestEntries = 4_096;
    internal const long MaxJournalBytes = 12L * 1024 * 1024;
    internal const long MaxAuditBytes = 4L * 1024 * 1024;

    private const int JournalFormatVersion = 1;
    private const int AuditFormatVersion = 1;
    private const string WriteOperation = "writeBatch";
    private const string MarkStaleOperation = "markStale";
    private const string CleanupOperation = "cleanup";
    private const string PublishTransition = "publish";
    private const string RetireTransition = "retire";
    private const string StagePrefix = ".report-artifact-stage-";
    private const string QuarantinePrefix = ".report-artifact-quarantine-";
    private const string ManifestTempPrefix = ".report-artifact-manifest-";
    private const string JournalTempPrefix = ".report-artifact-journal-";

    private static readonly JsonSerializerOptions StorageJsonOptions = CreateStorageJsonOptions();
    private static readonly JsonSerializerOptions CanonicalJsonOptions = CreateCanonicalJsonOptions();
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly JetProjectFolder _folder;
    private readonly TimeProvider _timeProvider;
    private readonly ReportArtifactStoreTestHooks? _testHooks;
    private readonly ILogger<ProjectReportArtifactStore> _logger;

    public ProjectReportArtifactStore(
        JetProjectFolder folder,
        TimeProvider? timeProvider = null,
        ILogger<ProjectReportArtifactStore>? logger = null)
        : this(folder, timeProvider, testHooks: null, logger)
    {
    }

    internal ProjectReportArtifactStore(
        JetProjectFolder folder,
        TimeProvider? timeProvider,
        ReportArtifactStoreTestHooks? testHooks,
        ILogger<ProjectReportArtifactStore>? logger = null)
    {
        _folder = folder ?? throw new ArgumentNullException(nameof(folder));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _testHooks = testHooks;
        _logger = logger ?? NullLogger<ProjectReportArtifactStore>.Instance;
    }

    public async Task<ReportArtifact> WriteAsync(
        string projectId,
        ReportArtifactWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var written = await WriteBatchCoreAsync(
                projectId,
                [request],
                publishingArtifact: null,
                cancellationToken)
            .ConfigureAwait(false);
        return written[0];
    }

    public Task<IReadOnlyList<ReportArtifact>> WriteBatchAsync(
        string projectId,
        IReadOnlyList<ReportArtifactWriteRequest> requests,
        CancellationToken cancellationToken) =>
        WriteBatchCoreAsync(projectId, requests, publishingArtifact: null, cancellationToken);

    async Task<ReportArtifact> IReportArtifactPublishingStore.WriteWithPublishingAsync(
        string projectId,
        ReportArtifactWriteRequest request,
        Action<ReportArtifactKind> publishingArtifact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(publishingArtifact);
        var written = await WriteBatchCoreAsync(
                projectId,
                [request],
                publishingArtifact,
                cancellationToken)
            .ConfigureAwait(false);
        return written[0];
    }

    Task<IReadOnlyList<ReportArtifact>> IReportArtifactPublishingStore.WriteBatchWithPublishingAsync(
        string projectId,
        IReadOnlyList<ReportArtifactWriteRequest> requests,
        Action<ReportArtifactKind> publishingArtifact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publishingArtifact);
        return WriteBatchCoreAsync(projectId, requests, publishingArtifact, cancellationToken);
    }

    private async Task<IReadOnlyList<ReportArtifact>> WriteBatchCoreAsync(
        string projectId,
        IReadOnlyList<ReportArtifactWriteRequest> requests,
        Action<ReportArtifactKind>? publishingArtifact,
        CancellationToken cancellationToken)
    {
        ValidateWriteRequests(requests);

        var paths = GetProjectPaths(projectId, requireProjectDirectory: true);
        await using var lease = await AcquireLeaseAsync(paths, cancellationToken).ConfigureAwait(false);
        await RecoverPendingMutationAsync(paths).ConfigureAwait(false);
        CleanupUnjournaledTemporaryFiles(paths);
        EnsureManifestTargetWritable(paths.ManifestPath);

        var beforeArtifacts = await ReadManifestAsync(paths, cancellationToken).ConfigureAwait(false);
        var operationId = Guid.NewGuid().ToString("N");
        var generatedUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        var staged = new List<StagedArtifact>(requests.Count);
        var temporaryPaths = new List<string>(requests.Count);
        MutationJournal? journal = null;
        var journalPublished = false;

        try
        {
            for (var index = 0; index < requests.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = requests[index];
                var artifactId = Guid.NewGuid().ToString("N");
                var relativeFileName = BuildFileName(projectId, request.Kind);
                var finalPath = ResolveContainedPath(paths.ProjectDirectory, relativeFileName);
                var stageFileName = $"{StagePrefix}{operationId}-{artifactId}.tmp";
                var stagePath = ResolveContainedPath(paths.ProjectDirectory, stageFileName);
                temporaryPaths.Add(stagePath);

                await WriteContentAsync(stagePath, request.WriteContentAsync, cancellationToken)
                    .ConfigureAwait(false);
                EnsureSafeExistingFileChain(stagePath);
                var bytes = new FileInfo(stagePath).Length;
                var sha256 = await ComputeSha256Async(stagePath, cancellationToken).ConfigureAwait(false);
                var artifact = new ReportArtifact(
                    artifactId,
                    request.Kind,
                    relativeFileName,
                    CloneSourceRefs(request.SourceRef),
                    generatedUtc,
                    bytes,
                    sha256,
                    Stale: false);

                staged.Add(new StagedArtifact(stageFileName, stagePath, finalPath, artifact));
            }

            var replacedKinds = staged.Select(item => item.Artifact.Kind).ToHashSet();
            var retired = beforeArtifacts.Where(item => replacedKinds.Contains(item.Kind)).ToArray();
            var afterArtifacts = beforeArtifacts
                .Where(item => !replacedKinds.Contains(item.Kind))
                .Concat(staged.Select(item => item.Artifact))
                .ToArray();
            if (afterArtifacts.Length > MaxManifestEntries)
            {
                throw InvalidManifestLimit();
            }

            var transitions = retired.Select(item => new FileTransition(
                    item.RelativeFileName,
                    $"{QuarantinePrefix}{operationId}-{item.ArtifactId}.tmp",
                    item.Bytes,
                    item.Sha256,
                    item.ArtifactId,
                    RetireTransition))
                .Concat(staged.Select(item => new FileTransition(
                    item.StageFileName,
                    item.Artifact.RelativeFileName,
                    item.Artifact.Bytes,
                    item.Artifact.Sha256,
                    item.Artifact.ArtifactId,
                    PublishTransition)))
                .ToArray();

            if (publishingArtifact is not null)
            {
                // Every staged stream is closed and durably flushed by
                // WriteContentAsync, and every artifact has been hashed above.
                // Emit the whole batch at this one pre-journal boundary; a
                // cancellation requested by an observer prevents the journal
                // and every subsequent artifact event from being published.
                foreach (var item in staged)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    publishingArtifact(item.Artifact.Kind);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            journal = CreateJournal(
                operationId,
                WriteOperation,
                beforeArtifacts,
                afterArtifacts,
                transitions,
                audit: null);
            await PublishJournalAsync(paths, journal, cancellationToken).ConfigureAwait(false);
            journalPublished = true;
            Checkpoint(ReportArtifactStoreCheckpoint.JournalPublished);

            foreach (var transition in transitions.Where(IsRetireTransition))
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureProjectDirectorySafe(paths.ProjectDirectory);
                var sourcePath = ResolveContainedPath(paths.ProjectDirectory, transition.FromFileName);
                var quarantinePath = ResolveContainedPath(paths.ProjectDirectory, transition.ToFileName);
                await VerifyExpectedFileAsync(sourcePath, transition).ConfigureAwait(false);
                File.Move(sourcePath, quarantinePath);
                EnsureSafeExistingFileChain(quarantinePath);
            }

            foreach (var item in staged)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureProjectDirectorySafe(paths.ProjectDirectory);
                File.Move(item.StagePath, item.FinalPath);
                EnsureSafeExistingFileChain(item.FinalPath);
            }

            Checkpoint(ReportArtifactStoreCheckpoint.FileTransitionsApplied);
            cancellationToken.ThrowIfCancellationRequested();
            await PublishManifestAsync(paths, afterArtifacts, operationId, cancellationToken)
                .ConfigureAwait(false);
            Checkpoint(ReportArtifactStoreCheckpoint.ManifestCommitted);

            await CompleteCommittedMutationAsync(paths, journal).ConfigureAwait(false);
            return Array.AsReadOnly(staged.Select(item => CloneArtifact(item.Artifact)).ToArray());
        }
        catch (ReportArtifactStoreSimulatedCrashException)
        {
            throw;
        }
        catch
        {
            if (journalPublished && journal is not null)
            {
                var recovery = await RecoverPendingMutationAsync(paths).ConfigureAwait(false);
                if (recovery == RecoveryDisposition.Committed)
                {
                    return Array.AsReadOnly(staged.Select(item => CloneArtifact(item.Artifact)).ToArray());
                }
            }

            throw;
        }
        finally
        {
            foreach (var item in staged)
            {
                DeleteFileBestEffort(item.StagePath);
            }

            foreach (var temporaryPath in temporaryPaths)
            {
                DeleteFileBestEffort(temporaryPath);
            }
        }
    }

    public async Task<IReadOnlyList<ReportArtifact>> ListAsync(
        string projectId,
        CancellationToken cancellationToken)
        => (await ReadCatalogAsync(projectId, cancellationToken).ConfigureAwait(false)).Artifacts;

    public async Task<ReportArtifactCatalog> ReadCatalogAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var paths = GetProjectPaths(projectId, requireProjectDirectory: true);
        await using var lease = await AcquireLeaseAsync(paths, cancellationToken).ConfigureAwait(false);
        await RecoverPendingMutationAsync(paths).ConfigureAwait(false);
        CleanupUnjournaledTemporaryFiles(paths);
        var artifacts = await ReadManifestAsync(paths, cancellationToken).ConfigureAwait(false);
        return CreateCatalog(artifacts);
    }

    public async Task<string> ResolvePathAsync(
        string projectId,
        string artifactId,
        CancellationToken cancellationToken)
    {
        if (!IsValidArtifactId(artifactId))
        {
            throw new JetActionException(JetErrorCodes.InvalidPayload, "artifactId 格式無效。");
        }

        var paths = GetProjectPaths(projectId, requireProjectDirectory: true);
        await using var lease = await AcquireLeaseAsync(paths, cancellationToken).ConfigureAwait(false);
        await RecoverPendingMutationAsync(paths).ConfigureAwait(false);
        CleanupUnjournaledTemporaryFiles(paths);
        var artifacts = await ReadManifestAsync(paths, cancellationToken).ConfigureAwait(false);
        var artifact = artifacts.SingleOrDefault(
            item => string.Equals(item.ArtifactId, artifactId, StringComparison.Ordinal));
        if (artifact is null)
        {
            throw new JetActionException(JetErrorCodes.FileNotFound, "找不到指定的報告產物。");
        }

        var path = ResolveContainedPath(paths.ProjectDirectory, artifact.RelativeFileName);
        EnsureSafeExistingFileChain(path);
        return path;
    }

    public Task<int> MarkStaleAsync(
        string projectId,
        ReportArtifactKind kind,
        CancellationToken cancellationToken)
    {
        ValidateKind(kind);
        return MarkStaleAsync(projectId, artifact => artifact.Kind == kind, cancellationToken);
    }

    public async Task<int> MarkStaleAsync(
        string projectId,
        Func<ReportArtifact, bool> predicate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var paths = GetProjectPaths(projectId, requireProjectDirectory: true);
        await using var lease = await AcquireLeaseAsync(paths, cancellationToken).ConfigureAwait(false);
        await RecoverPendingMutationAsync(paths).ConfigureAwait(false);
        CleanupUnjournaledTemporaryFiles(paths);
        EnsureManifestTargetWritable(paths.ManifestPath);

        var beforeArtifacts = await ReadManifestAsync(paths, cancellationToken).ConfigureAwait(false);
        var afterArtifacts = new ReportArtifact[beforeArtifacts.Count];
        var changedCount = 0;
        for (var index = 0; index < beforeArtifacts.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var artifact = beforeArtifacts[index];
            if (!artifact.Stale && predicate(CloneArtifact(artifact)))
            {
                artifact = artifact with { Stale = true };
                changedCount++;
            }

            afterArtifacts[index] = artifact;
        }

        if (changedCount == 0)
        {
            return 0;
        }

        var operationId = Guid.NewGuid().ToString("N");
        var journal = CreateJournal(
            operationId,
            MarkStaleOperation,
            beforeArtifacts,
            afterArtifacts,
            [],
            audit: null);
        var journalPublished = false;
        try
        {
            await PublishJournalAsync(paths, journal, cancellationToken).ConfigureAwait(false);
            journalPublished = true;
            Checkpoint(ReportArtifactStoreCheckpoint.JournalPublished);
            cancellationToken.ThrowIfCancellationRequested();
            await PublishManifestAsync(paths, afterArtifacts, operationId, cancellationToken)
                .ConfigureAwait(false);
            Checkpoint(ReportArtifactStoreCheckpoint.ManifestCommitted);
            await CompleteCommittedMutationAsync(paths, journal).ConfigureAwait(false);
            return changedCount;
        }
        catch (ReportArtifactStoreSimulatedCrashException)
        {
            throw;
        }
        catch
        {
            if (journalPublished)
            {
                var recovery = await RecoverPendingMutationAsync(paths).ConfigureAwait(false);
                if (recovery == RecoveryDisposition.Committed)
                {
                    return changedCount;
                }
            }

            throw;
        }
    }

    public async Task<ReportArtifactCleanupResult> CleanupAsync(
        string projectId,
        string expectedCatalogRevision,
        IReadOnlyList<ReportArtifactCleanupCandidate> candidates,
        string requestedBy,
        CancellationToken cancellationToken)
    {
        ValidateCleanupRequest(expectedCatalogRevision, candidates, requestedBy);
        var paths = GetProjectPaths(projectId, requireProjectDirectory: true);
        await using var lease = await AcquireLeaseAsync(paths, cancellationToken).ConfigureAwait(false);

        MutationJournal? journal = null;
        var journalPublished = false;
        var opened = new List<FileStream>();
        string? auditTemporaryPath = null;
        try
        {
            await RecoverPendingMutationAsync(paths).ConfigureAwait(false);
            CleanupUnjournaledTemporaryFiles(paths);
            EnsureManifestTargetWritable(paths.ManifestPath);

            var beforeArtifacts = await ReadManifestAsync(paths, cancellationToken).ConfigureAwait(false);
            var beforeCatalog = CreateCatalog(beforeArtifacts);
            if (!string.Equals(
                    beforeCatalog.Revision,
                    expectedCatalogRevision,
                    StringComparison.Ordinal))
            {
                throw CatalogChanged();
            }

            var authoritativeCandidates = ReportArtifactRetentionPolicy.Plan(beforeArtifacts);
            if (!CleanupCandidatesEqual(authoritativeCandidates, candidates))
            {
                throw CatalogChanged();
            }

            var candidateIds = candidates
                .Select(candidate => candidate.Artifact.ArtifactId)
                .ToHashSet(StringComparer.Ordinal);
            var afterArtifacts = beforeArtifacts
                .Where(artifact => !candidateIds.Contains(artifact.ArtifactId))
                .ToArray();
            var afterCatalog = CreateCatalog(afterArtifacts);
            var operationId = Guid.NewGuid().ToString("N");
            var auditId = Guid.NewGuid().ToString("N");

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var artifact = beforeArtifacts.Single(item => string.Equals(
                    item.ArtifactId,
                    candidate.Artifact.ArtifactId,
                    StringComparison.Ordinal));
                var path = ResolveContainedPath(paths.ProjectDirectory, artifact.RelativeFileName);
                EnsureSafeExistingFileChain(path);
                FileStream stream;
                try
                {
                    stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read | FileShare.Delete,
                        bufferSize: 128 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw CleanupFailed("報告檔案目前被其他程式占用，未進行任何清理。", exception);
                }

                opened.Add(stream);
                if (stream.Length != artifact.Bytes)
                {
                    throw CleanupFailed("報告檔案內容與索引不一致，未進行任何清理。");
                }

                var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(Convert.ToHexString(hash), artifact.Sha256, StringComparison.Ordinal))
                {
                    throw CleanupFailed("報告檔案內容與索引不一致，未進行任何清理。");
                }

                stream.Position = 0;
            }

            var audit = new CleanupAuditReceipt(
                AuditFormatVersion,
                auditId,
                _timeProvider.GetUtcNow().ToUniversalTime(),
                requestedBy,
                beforeCatalog.Revision,
                afterCatalog.Revision,
                candidates.Count,
                SumBytes(candidates),
                candidates.Select(candidate => new CleanupAuditArtifact(
                    candidate.Artifact.ArtifactId,
                    ReportArtifactKindValues.ToValue(candidate.Artifact.Kind),
                    candidate.Artifact.RelativeFileName,
                    candidate.Artifact.GeneratedUtc.ToUniversalTime(),
                    candidate.Artifact.Bytes,
                    ReportArtifactCleanupReasonValues.ToValue(candidate.Reason))).ToArray());
            auditTemporaryPath = await WriteAuditTemporaryAsync(paths, audit, cancellationToken)
                .ConfigureAwait(false);

            var transitions = candidates.Select(candidate => new FileTransition(
                candidate.Artifact.RelativeFileName,
                $"{QuarantinePrefix}{operationId}-{candidate.Artifact.ArtifactId}.tmp",
                candidate.Artifact.Bytes,
                candidate.Artifact.Sha256,
                candidate.Artifact.ArtifactId)).ToArray();
            journal = CreateJournal(
                operationId,
                CleanupOperation,
                beforeArtifacts,
                afterArtifacts,
                transitions,
                audit);
            await PublishJournalAsync(paths, journal, cancellationToken).ConfigureAwait(false);
            journalPublished = true;
            Checkpoint(ReportArtifactStoreCheckpoint.JournalPublished);

            foreach (var transition in transitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = ResolveContainedPath(paths.ProjectDirectory, transition.FromFileName);
                var quarantinePath = ResolveContainedPath(paths.ProjectDirectory, transition.ToFileName);
                if (InspectPathEntry(quarantinePath) != PathEntryKind.Missing)
                {
                    throw CleanupFailed("報告清理的暫存名稱已被占用，未進行任何清理。");
                }

                File.Move(sourcePath, quarantinePath);
                EnsureSafeExistingFileChain(quarantinePath);
            }

            Checkpoint(ReportArtifactStoreCheckpoint.FileTransitionsApplied);
            cancellationToken.ThrowIfCancellationRequested();
            await PublishManifestAsync(paths, afterArtifacts, operationId, cancellationToken)
                .ConfigureAwait(false);
            Checkpoint(ReportArtifactStoreCheckpoint.ManifestCommitted);

            DisposeStreams(opened);
            await CompleteCommittedMutationAsync(paths, journal).ConfigureAwait(false);
            auditTemporaryPath = null;
            return new ReportArtifactCleanupResult(
                auditId,
                candidates.Count,
                audit.DeletedBytes,
                afterCatalog);
        }
        catch (ReportArtifactStoreSimulatedCrashException)
        {
            throw;
        }
        catch (Exception exception)
        {
            DisposeStreams(opened);
            if (journalPublished && journal is not null)
            {
                try
                {
                    var recovery = await RecoverPendingMutationAsync(paths).ConfigureAwait(false);
                    if (recovery == RecoveryDisposition.Committed)
                    {
                        var catalog = CreateCatalog(journal.AfterArtifacts
                            .Select(entry => FromManifestEntry(entry, projectId))
                            .ToArray());
                        return new ReportArtifactCleanupResult(
                            journal.Audit!.AuditId,
                            journal.Audit.DeletedCount,
                            journal.Audit.DeletedBytes,
                            catalog);
                    }
                }
                catch (Exception recoveryException) when (
                    recoveryException is not ReportArtifactStoreSimulatedCrashException)
                {
                    throw CleanupFailed(
                        "報告清理未能完成安全復原；已保留 journal，請重試。",
                        recoveryException);
                }
            }

            if (exception is OperationCanceledException
                or JetActionException { Code: JetErrorCodes.ArtifactCatalogChanged }
                or JetActionException { Code: JetErrorCodes.InvalidPayload }
                or JetActionException { Code: JetErrorCodes.NoActiveProject })
            {
                throw;
            }

            if (exception is JetActionException { Code: JetErrorCodes.ArtifactCleanupFailed })
            {
                throw;
            }

            throw CleanupFailed("報告清理無法安全完成，未刪除任何已發布版本。", exception);
        }
        finally
        {
            DisposeStreams(opened);
            if (!journalPublished && auditTemporaryPath is not null)
            {
                DeleteFileBestEffort(auditTemporaryPath);
            }
        }
    }

    public async Task<IAsyncDisposable> AcquireProjectDeletionLeaseAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var paths = GetProjectPaths(projectId, requireProjectDirectory: true);
        // 刪案只需要排除其他 artifact reader/writer；即將永久刪除整案時，不先要求損壞的 journal
        // 成功 recovery。此方法只取得案件外 lease，不讀取或改動任何案件內檔案。
        return await AcquireLeaseAsync(paths, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RecoveryDisposition> RecoverPendingMutationAsync(ProjectPaths paths)
    {
        var journalEntry = InspectPathEntry(paths.JournalPath);
        if (journalEntry == PathEntryKind.Directory)
        {
            throw RecoveryFailed("報告產物 journal 路徑不是 regular file。");
        }

        if (journalEntry == PathEntryKind.Missing)
        {
            return RecoveryDisposition.None;
        }

        var journal = await ReadJournalAsync(paths).ConfigureAwait(false);
        var current = await ReadManifestAsync(paths, CancellationToken.None).ConfigureAwait(false);
        var currentRevision = ComputeCatalogRevision(current);
        if (string.Equals(currentRevision, journal.BeforeRevision, StringComparison.Ordinal))
        {
            await RollbackUncommittedMutationAsync(paths, journal).ConfigureAwait(false);
            DeleteControlFile(paths.JournalPath);
            return RecoveryDisposition.RolledBack;
        }

        if (string.Equals(currentRevision, journal.AfterRevision, StringComparison.Ordinal))
        {
            await CompleteCommittedMutationAsync(paths, journal).ConfigureAwait(false);
            return RecoveryDisposition.Committed;
        }

        ReportArtifactStoreDiagnostics.RecoveryConflict(
            _logger,
            journal.Operation,
            "neither",
            "none",
            "unknown",
            "unknown",
            "unknown",
            "unknown",
            0,
            0,
            false,
            false,
            JetErrorCodes.ArtifactRecoveryConflict);
        throw RecoveryConflict("報告產物 journal 與目前 manifest 皆不相符；已保留現場，未猜測復原。");
    }

    private async Task RollbackUncommittedMutationAsync(
        ProjectPaths paths,
        MutationJournal journal)
    {
        if (journal.Operation == WriteOperation)
        {
            foreach (var transition in journal.Transitions.Where(IsPublishTransition))
            {
                var stagePath = ResolveContainedPath(paths.ProjectDirectory, transition.FromFileName);
                var finalPath = ResolveContainedPath(paths.ProjectDirectory, transition.ToFileName);
                var finalEntry = InspectPathEntry(finalPath);
                if (finalEntry == PathEntryKind.Directory)
                {
                    throw RecoveryFailed("報告覆寫回滾的正式路徑被資料夾占用。");
                }

                if (finalEntry == PathEntryKind.RegularFile)
                {
                    if (await FileMatchesExpectedAsync(finalPath, transition).ConfigureAwait(false))
                    {
                        File.Delete(finalPath);
                    }
                    else
                    {
                        var retiredAtSamePath = journal.Transitions.FirstOrDefault(candidate =>
                            IsRetireTransition(candidate)
                            && string.Equals(
                                candidate.FromFileName,
                                transition.ToFileName,
                                StringComparison.OrdinalIgnoreCase));
                        var quarantineMissing = retiredAtSamePath is not null
                            && InspectPathEntry(ResolveContainedPath(
                                paths.ProjectDirectory,
                                retiredAtSamePath.ToFileName)) == PathEntryKind.Missing;
                        var oldFallbackMatches = quarantineMissing
                            && await FileMatchesExpectedAsync(finalPath, retiredAtSamePath!)
                                .ConfigureAwait(false);
                        if (!oldFallbackMatches)
                        {
                            var quarantineState = retiredAtSamePath is null
                                ? "notApplicable"
                                : InspectPathEntry(ResolveContainedPath(
                                    paths.ProjectDirectory,
                                    retiredAtSamePath.ToFileName)).ToString();
                            var artifactKind = journal.AfterArtifacts
                                .FirstOrDefault(item => string.Equals(
                                    item.ArtifactId,
                                    transition.ArtifactId,
                                    StringComparison.Ordinal))?.Kind
                                ?? "unknown";
                            ReportArtifactStoreDiagnostics.RecoveryConflict(
                                _logger,
                                journal.Operation,
                                "before",
                                transition.Role ?? "unknown",
                                artifactKind,
                                InspectPathEntry(stagePath).ToString(),
                                finalEntry.ToString(),
                                quarantineState,
                                transition.Bytes,
                                new FileInfo(finalPath).Length,
                                false,
                                oldFallbackMatches,
                                JetErrorCodes.ArtifactRecoveryConflict);
                            throw RecoveryConflict("報告覆寫回滾的正式檔內容不符合 journal。");
                        }
                    }
                }

                await DeleteExpectedFileIfPresentAsync(stagePath, transition).ConfigureAwait(false);
            }

            foreach (var transition in journal.Transitions.Where(IsRetireTransition).Reverse())
            {
                var originalPath = ResolveContainedPath(paths.ProjectDirectory, transition.FromFileName);
                var quarantinePath = ResolveContainedPath(paths.ProjectDirectory, transition.ToFileName);
                var originalEntry = InspectPathEntry(originalPath);
                var quarantineEntry = InspectPathEntry(quarantinePath);
                if (originalEntry == PathEntryKind.Directory
                    || quarantineEntry == PathEntryKind.Directory
                    || (originalEntry == PathEntryKind.RegularFile
                        && quarantineEntry == PathEntryKind.RegularFile))
                {
                    throw RecoveryFailed("報告覆寫回滾路徑狀態無效。");
                }

                if (quarantineEntry == PathEntryKind.RegularFile)
                {
                    await VerifyExpectedFileAsync(quarantinePath, transition).ConfigureAwait(false);
                    File.Move(quarantinePath, originalPath);
                }

                await VerifyExpectedFileAsync(originalPath, transition).ConfigureAwait(false);
            }
        }
        else if (journal.Operation == CleanupOperation)
        {
            foreach (var transition in journal.Transitions.Reverse())
            {
                var originalPath = ResolveContainedPath(paths.ProjectDirectory, transition.FromFileName);
                var quarantinePath = ResolveContainedPath(paths.ProjectDirectory, transition.ToFileName);
                var originalEntry = InspectPathEntry(originalPath);
                var quarantineEntry = InspectPathEntry(quarantinePath);
                if (originalEntry == PathEntryKind.Directory
                    || quarantineEntry == PathEntryKind.Directory)
                {
                    throw RecoveryFailed("清理回滾路徑被資料夾占用。");
                }

                var originalExists = originalEntry == PathEntryKind.RegularFile;
                var quarantineExists = quarantineEntry == PathEntryKind.RegularFile;
                if (originalExists && quarantineExists)
                {
                    throw RecoveryFailed("清理回滾時原檔與 quarantine 同時存在。");
                }

                if (!originalExists && !quarantineExists)
                {
                    throw RecoveryFailed("清理回滾缺少原檔與 quarantine，無法安全補猜。");
                }

                if (quarantineExists)
                {
                    await VerifyExpectedFileAsync(quarantinePath, transition).ConfigureAwait(false);
                    File.Move(quarantinePath, originalPath);
                }

                await VerifyExpectedFileAsync(originalPath, transition).ConfigureAwait(false);
            }

            if (journal.Audit is not null)
            {
                var auditPath = GetAuditPath(paths, journal.Audit.AuditId);
                if (InspectPathEntry(auditPath) != PathEntryKind.Missing)
                {
                    throw RecoveryFailed("manifest commit 前不應存在 cleanup audit receipt。");
                }

                DeleteFileBestEffort(GetAuditTemporaryPath(paths, journal.Audit.AuditId));
            }
        }
    }

    private async Task CompleteCommittedMutationAsync(ProjectPaths paths, MutationJournal journal)
    {
        if (journal.Operation == WriteOperation)
        {
            foreach (var transition in journal.Transitions.Where(IsPublishTransition))
            {
                var finalPath = ResolveContainedPath(paths.ProjectDirectory, transition.ToFileName);
                await VerifyExpectedFileAsync(finalPath, transition).ConfigureAwait(false);
                DeleteFileBestEffort(ResolveContainedPath(paths.ProjectDirectory, transition.FromFileName));
            }

            var publishedFileNames = journal.Transitions
                .Where(IsPublishTransition)
                .Select(transition => transition.ToFileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var transition in journal.Transitions.Where(IsRetireTransition))
            {
                var originalPath = ResolveContainedPath(paths.ProjectDirectory, transition.FromFileName);
                var quarantinePath = ResolveContainedPath(paths.ProjectDirectory, transition.ToFileName);
                var originalEntry = InspectPathEntry(originalPath);
                var quarantineEntry = InspectPathEntry(quarantinePath);
                if (originalEntry == PathEntryKind.Directory || quarantineEntry == PathEntryKind.Directory)
                {
                    throw RecoveryFailed("報告覆寫完成路徑被資料夾占用。");
                }

                if (originalEntry == PathEntryKind.RegularFile
                    && !publishedFileNames.Contains(transition.FromFileName))
                {
                    throw RecoveryFailed("報告覆寫 manifest 已 commit，但 legacy 原檔仍存在。");
                }

                if (quarantineEntry == PathEntryKind.RegularFile)
                {
                    await VerifyExpectedFileAsync(quarantinePath, transition).ConfigureAwait(false);
                    File.Delete(quarantinePath);
                }
            }
        }
        else if (journal.Operation == CleanupOperation)
        {
            var audit = journal.Audit
                ?? throw RecoveryFailed("cleanup journal 缺少 audit receipt。");
            var auditAlreadyPublished = await IsAuditReceiptPublishedAsync(paths, audit)
                .ConfigureAwait(false);
            foreach (var transition in journal.Transitions)
            {
                var originalPath = ResolveContainedPath(paths.ProjectDirectory, transition.FromFileName);
                var quarantinePath = ResolveContainedPath(paths.ProjectDirectory, transition.ToFileName);
                var originalEntry = InspectPathEntry(originalPath);
                var quarantineEntry = InspectPathEntry(quarantinePath);
                if (originalEntry == PathEntryKind.Directory
                    || quarantineEntry == PathEntryKind.Directory)
                {
                    throw RecoveryFailed("cleanup recovery 的原檔或 quarantine 被資料夾占用。");
                }

                if (originalEntry == PathEntryKind.RegularFile)
                {
                    throw RecoveryFailed("cleanup manifest 已 commit，但原檔仍存在。");
                }

                if (quarantineEntry == PathEntryKind.RegularFile)
                {
                    await VerifyExpectedFileAsync(quarantinePath, transition).ConfigureAwait(false);
                }
                else if (!auditAlreadyPublished)
                {
                    throw RecoveryFailed(
                        "cleanup manifest 已 commit、audit 尚未發布，但 quarantine 已遺失；已保留 journal。");
                }
            }

            await PublishAuditReceiptAsync(paths, audit).ConfigureAwait(false);
            Checkpoint(ReportArtifactStoreCheckpoint.AuditPublished);
            foreach (var transition in journal.Transitions)
            {
                var quarantinePath = ResolveContainedPath(paths.ProjectDirectory, transition.ToFileName);
                var quarantineEntry = InspectPathEntry(quarantinePath);
                if (quarantineEntry == PathEntryKind.Directory)
                {
                    throw RecoveryFailed("cleanup quarantine 被資料夾占用。");
                }

                if (quarantineEntry == PathEntryKind.RegularFile)
                {
                    File.Delete(quarantinePath);
                    Checkpoint(ReportArtifactStoreCheckpoint.QuarantineFileDeleted);
                }
            }

            DeleteFileBestEffort(GetAuditTemporaryPath(paths, audit.AuditId));
        }

        DeleteControlFile(paths.JournalPath);
    }

    private static async Task DeleteExpectedFileIfPresentAsync(string path, FileTransition transition)
    {
        var entry = InspectPathEntry(path);
        if (entry == PathEntryKind.Missing)
        {
            return;
        }

        if (entry == PathEntryKind.Directory)
        {
            throw RecoveryFailed("待復原檔案路徑被資料夾占用。");
        }

        await VerifyExpectedFileAsync(path, transition).ConfigureAwait(false);
        File.Delete(path);
    }

    private static async Task VerifyExpectedFileAsync(string path, FileTransition transition)
    {
        EnsureSafeExistingFileChain(path);
        var info = new FileInfo(path);
        if (info.Length != transition.Bytes)
        {
            throw RecoveryFailed("待復原檔案的 bytes 與 journal 不符。");
        }

        var sha256 = await ComputeSha256Async(path, CancellationToken.None).ConfigureAwait(false);
        if (!string.Equals(sha256, transition.Sha256, StringComparison.Ordinal))
        {
            throw RecoveryFailed("待復原檔案的 SHA-256 與 journal 不符。");
        }
    }

    private static async Task<bool> FileMatchesExpectedAsync(string path, FileTransition transition)
    {
        EnsureSafeExistingFileChain(path);
        var info = new FileInfo(path);
        if (info.Length != transition.Bytes)
        {
            return false;
        }

        var sha256 = await ComputeSha256Async(path, CancellationToken.None).ConfigureAwait(false);
        return string.Equals(sha256, transition.Sha256, StringComparison.Ordinal);
    }

    private static bool IsPublishTransition(FileTransition transition)
        => transition.Role is null or PublishTransition;

    private static bool IsRetireTransition(FileTransition transition)
        => transition.Role == RetireTransition;

    private static MutationJournal CreateJournal(
        string operationId,
        string operation,
        IReadOnlyList<ReportArtifact> beforeArtifacts,
        IReadOnlyList<ReportArtifact> afterArtifacts,
        FileTransition[] transitions,
        CleanupAuditReceipt? audit)
    {
        var beforeEntries = beforeArtifacts.Select(ToManifestEntry).ToArray();
        var afterEntries = afterArtifacts.Select(ToManifestEntry).ToArray();
        return new MutationJournal(
            JournalFormatVersion,
            operationId,
            operation,
            ComputeCatalogRevision(beforeArtifacts),
            ComputeCatalogRevision(afterArtifacts),
            beforeEntries,
            afterEntries,
            transitions,
            audit);
    }

    private static ReportArtifactCatalog CreateCatalog(IReadOnlyList<ReportArtifact> artifacts)
        => new(
            ComputeCatalogRevision(artifacts),
            Array.AsReadOnly(artifacts.Select(CloneArtifact).ToArray()));

    private static string ComputeCatalogRevision(IReadOnlyList<ReportArtifact> artifacts)
    {
        var canonical = artifacts
            .OrderBy(artifact => artifact.ArtifactId, StringComparer.Ordinal)
            .Select(artifact => new CanonicalArtifact(
                artifact.ArtifactId,
                ReportArtifactKindValues.ToValue(artifact.Kind),
                artifact.RelativeFileName,
                artifact.SourceRef.ValidationRunId,
                artifact.SourceRef.PrescreenRunId,
                artifact.SourceRef.ScenarioRevision,
                artifact.SourceRef.ScenarioPositions?.Order().ToArray(),
                artifact.GeneratedUtc.ToUniversalTime(),
                artifact.Bytes,
                artifact.Sha256,
                artifact.Stale))
            .ToArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, CanonicalJsonOptions);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static bool CleanupCandidatesEqual(
        IReadOnlyList<ReportArtifactCleanupCandidate> left,
        IReadOnlyList<ReportArtifactCleanupCandidate> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var rightById = right.ToDictionary(
            candidate => candidate.Artifact.ArtifactId,
            StringComparer.Ordinal);
        foreach (var candidate in left)
        {
            if (!rightById.TryGetValue(candidate.Artifact.ArtifactId, out var other)
                || candidate.Reason != other.Reason
                || !ArtifactsSemanticallyEqual(candidate.Artifact, other.Artifact))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ArtifactsSemanticallyEqual(ReportArtifact left, ReportArtifact right)
        => string.Equals(left.ArtifactId, right.ArtifactId, StringComparison.Ordinal)
            && left.Kind == right.Kind
            && string.Equals(left.RelativeFileName, right.RelativeFileName, StringComparison.Ordinal)
            && SourceRefsEqual(left.SourceRef, right.SourceRef)
            && left.GeneratedUtc.ToUniversalTime() == right.GeneratedUtc.ToUniversalTime()
            && left.Bytes == right.Bytes
            && string.Equals(left.Sha256, right.Sha256, StringComparison.Ordinal)
            && left.Stale == right.Stale;

    private static bool SourceRefsEqual(ReportArtifactSourceRefs left, ReportArtifactSourceRefs right)
        => string.Equals(left.ValidationRunId, right.ValidationRunId, StringComparison.Ordinal)
            && string.Equals(left.PrescreenRunId, right.PrescreenRunId, StringComparison.Ordinal)
            && string.Equals(left.ScenarioRevision, right.ScenarioRevision, StringComparison.Ordinal)
            && (left.ScenarioPositions ?? []).SequenceEqual(right.ScenarioPositions ?? []);

    private static long SumBytes(IReadOnlyList<ReportArtifactCleanupCandidate> candidates)
    {
        try
        {
            return candidates.Aggregate(
                0L,
                static (total, candidate) => checked(total + candidate.Artifact.Bytes));
        }
        catch (OverflowException exception)
        {
            throw CleanupFailed("報告清理候選的檔案大小總和超出安全範圍。", exception);
        }
    }

    private static void ValidateCleanupRequest(
        string expectedCatalogRevision,
        IReadOnlyList<ReportArtifactCleanupCandidate> candidates,
        string requestedBy)
    {
        if (!IsValidSha256(expectedCatalogRevision))
        {
            throw new JetActionException(JetErrorCodes.InvalidPayload, "catalogRevision 格式無效。");
        }

        if (candidates is null || candidates.Count == 0 || candidates.Count > MaxManifestEntries)
        {
            throw new JetActionException(JetErrorCodes.InvalidPayload, "沒有可確認清理的報告版本。");
        }

        if (candidates.Any(candidate => candidate is null || candidate.Artifact is null)
            || candidates.Select(candidate => candidate.Artifact.ArtifactId)
                .Distinct(StringComparer.Ordinal).Count() != candidates.Count)
        {
            throw new JetActionException(JetErrorCodes.InvalidPayload, "報告清理候選格式無效。");
        }

        if (string.IsNullOrWhiteSpace(requestedBy)
            || requestedBy.Length > 256
            || !string.Equals(requestedBy, requestedBy.Trim(), StringComparison.Ordinal)
            || requestedBy.Contains('\r')
            || requestedBy.Contains('\n'))
        {
            throw new JetActionException(JetErrorCodes.InvalidPayload, "報告清理的 requestedBy 格式無效。");
        }
    }

    private static void ValidateWriteRequests(IReadOnlyList<ReportArtifactWriteRequest> requests)
    {
        if (requests is null || requests.Count == 0)
        {
            throw new JetActionException(JetErrorCodes.InvalidPayload, "報告產物批次不可為空白。");
        }

        foreach (var request in requests)
        {
            if (request is null)
            {
                throw new JetActionException(JetErrorCodes.InvalidPayload, "報告產物寫入要求不可為 null。");
            }

            ValidateKind(request.Kind);
            if (request.SourceRef is null)
            {
                throw new JetActionException(JetErrorCodes.InvalidPayload, "報告產物的來源參照不可為 null。");
            }

            ValidateSourceRefs(request.SourceRef, JetErrorCodes.InvalidPayload);
            ValidateRequiredSourceRefs(request.Kind, request.SourceRef);
            if (request.WriteContentAsync is null)
            {
                throw new JetActionException(JetErrorCodes.InvalidPayload, "報告產物缺少內容寫入器。");
            }
        }

        if (requests.Select(request => request.Kind).Distinct().Count() != requests.Count)
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                "同一批報告產物不可包含重複種類。");
        }
    }

    private static void ValidateKind(ReportArtifactKind kind)
    {
        if (!Enum.IsDefined(typeof(ReportArtifactKind), kind))
        {
            throw new JetActionException(JetErrorCodes.InvalidPayload, "報告產物種類無效。");
        }
    }

    private static void ValidateSourceRefs(ReportArtifactSourceRefs sourceRefs, string errorCode)
    {
        ValidateRunId(sourceRefs.ValidationRunId, "validationRunId", errorCode);
        ValidateRunId(sourceRefs.PrescreenRunId, "prescreenRunId", errorCode);
        if (sourceRefs.ScenarioRevision is { } revision
            && (string.IsNullOrWhiteSpace(revision)
                || revision.Length > 128
                || !string.Equals(revision, revision.Trim(), StringComparison.Ordinal)
                || revision.Contains('\r')
                || revision.Contains('\n')))
        {
            throw new JetActionException(errorCode, "報告產物的 scenarioRevision 格式無效。");
        }

        if (sourceRefs.ScenarioPositions is { } positions
            && (positions.Count > 10
                || positions.Any(position => position <= 0)
                || positions.Distinct().Count() != positions.Count))
        {
            throw new JetActionException(errorCode, "報告產物的 scenarioPositions 格式無效。");
        }
    }

    private static void ValidateRequiredSourceRefs(ReportArtifactKind kind, ReportArtifactSourceRefs sourceRef)
    {
        if (!HasRequiredSourceRefs(kind, sourceRef))
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                "報告產物缺少該種類必要的來源參照。");
        }
    }

    private static bool HasRequiredSourceRefs(ReportArtifactKind kind, ReportArtifactSourceRefs sourceRef)
        => kind switch
        {
            ReportArtifactKind.ValidationReport
                or ReportArtifactKind.AccountMapping
                or ReportArtifactKind.InfReport
                => sourceRef.ValidationRunId is not null,
            ReportArtifactKind.PrescreenReport
                => sourceRef.PrescreenRunId is not null,
            ReportArtifactKind.CriteriaSelectionReport
                or ReportArtifactKind.WorkingPaper
                => sourceRef.ValidationRunId is not null
                    && sourceRef.ScenarioRevision is not null
                    && sourceRef.ScenarioPositions is { Count: > 0 },
            _ => false
        };

    private static void ValidateRunId(string? runId, string fieldName, string errorCode)
    {
        if (runId is not null
            && (runId.Length != 32
                || !string.Equals(runId, runId.ToLowerInvariant(), StringComparison.Ordinal)
                || !Guid.TryParseExact(runId, "N", out _)))
        {
            throw new JetActionException(errorCode, $"報告產物的 {fieldName} 格式無效。");
        }
    }

    private ProjectPaths GetProjectPaths(string projectId, bool requireProjectDirectory)
    {
        string root;
        string projectDirectory;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_folder.RootPath));
            projectDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(_folder.GetProjectDirectory(projectId)));
        }
        catch (JetActionException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new JetActionException(JetErrorCodes.FileReadError, "專案資料夾路徑格式無效。");
        }

        var rootEntry = InspectPathEntry(root);
        if (rootEntry == PathEntryKind.Missing)
        {
            throw new JetActionException(JetErrorCodes.ProjectNotFound, "找不到指定的專案資料夾。");
        }
        if (rootEntry != PathEntryKind.Directory)
        {
            throw new JetActionException(JetErrorCodes.FileReadError, "專案根目錄不是受控資料夾。");
        }

        EnsureNoReparseDirectoryChain(new DirectoryInfo(root));
        var requiredPrefix = AppendDirectorySeparator(root);
        if (!projectDirectory.StartsWith(requiredPrefix, PathComparison))
        {
            throw new JetActionException(JetErrorCodes.FileReadError, "專案資料夾路徑超出受控的專案根目錄。");
        }

        if (requireProjectDirectory)
        {
            EnsureProjectDirectorySafe(projectDirectory);
        }

        var normalizedForHash = OperatingSystem.IsWindows()
            ? projectDirectory.ToUpperInvariant()
            : projectDirectory;
        var lockHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedForHash)));
        var lockPath = Path.Combine(root, $".report-artifacts-{lockHash}.lock");
        return new ProjectPaths(
            root,
            projectDirectory,
            lockPath,
            Path.Combine(projectDirectory, ManifestFileName),
            Path.Combine(projectDirectory, JournalFileName),
            Path.Combine(projectDirectory, AuditDirectoryName));
    }

    private static async Task<ArtifactProjectLease> AcquireLeaseAsync(
        ProjectPaths paths,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                EnsureNoReparseDirectoryChain(new DirectoryInfo(paths.RootDirectory));
                if (InspectPathEntry(paths.LockPath) == PathEntryKind.Directory)
                {
                    throw new JetActionException(JetErrorCodes.FileReadError, "報告產物鎖路徑不是 regular file。");
                }

                var stream = new FileStream(
                    paths.LockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                try
                {
                    EnsureSafeExistingFileChain(paths.LockPath);
                    EnsureProjectDirectorySafe(paths.ProjectDirectory);
                    return new ArtifactProjectLease(stream);
                }
                catch
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new JetActionException(
                    JetErrorCodes.FileReadError,
                    $"無法取得報告產物的跨程序鎖：{exception.GetType().Name}。");
            }
        }
    }

    private static bool IsSharingViolation(IOException exception)
    {
        var code = exception.HResult & 0xFFFF;
        return code is 32 or 33;
    }

    private static async Task WriteContentAsync(
        string temporaryPath,
        ReportArtifactContentWriter writer,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await writer(output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static async Task PublishManifestAsync(
        ProjectPaths paths,
        IReadOnlyList<ReportArtifact> artifacts,
        string operationId,
        CancellationToken cancellationToken)
    {
        if (artifacts.Count > MaxManifestEntries)
        {
            throw InvalidManifestLimit();
        }

        var temporaryPath = ResolveContainedPath(
            paths.ProjectDirectory,
            $"{ManifestTempPrefix}{operationId}.tmp");
        try
        {
            await WriteJsonFileAsync(
                temporaryPath,
                artifacts.Select(ToManifestEntry).ToArray(),
                MaxManifestBytes,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            PublishControlFile(temporaryPath, paths.ManifestPath);
        }
        finally
        {
            DeleteFileBestEffort(temporaryPath);
        }
    }

    private static async Task PublishJournalAsync(
        ProjectPaths paths,
        MutationJournal journal,
        CancellationToken cancellationToken)
    {
        if (InspectPathEntry(paths.JournalPath) != PathEntryKind.Missing)
        {
            throw RecoveryFailed("已有待復原的報告產物 journal。");
        }

        var temporaryPath = ResolveContainedPath(
            paths.ProjectDirectory,
            $"{JournalTempPrefix}{journal.OperationId}.tmp");
        try
        {
            await WriteJsonFileAsync(
                temporaryPath,
                journal,
                MaxJournalBytes,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, paths.JournalPath);
            EnsureSafeExistingFileChain(paths.JournalPath);
        }
        finally
        {
            DeleteFileBestEffort(temporaryPath);
        }
    }

    private static async Task<string> WriteAuditTemporaryAsync(
        ProjectPaths paths,
        CleanupAuditReceipt audit,
        CancellationToken cancellationToken)
    {
        EnsureAuditDirectory(paths);
        var temporaryPath = GetAuditTemporaryPath(paths, audit.AuditId);
        var finalPath = GetAuditPath(paths, audit.AuditId);
        if (InspectPathEntry(finalPath) != PathEntryKind.Missing)
        {
            throw CleanupFailed("cleanup auditId 已存在，未進行清理。");
        }

        await WriteJsonFileAsync(
            temporaryPath,
            audit,
            MaxAuditBytes,
            cancellationToken).ConfigureAwait(false);
        return temporaryPath;
    }

    private static async Task PublishAuditReceiptAsync(ProjectPaths paths, CleanupAuditReceipt audit)
    {
        EnsureAuditDirectory(paths);
        var finalPath = GetAuditPath(paths, audit.AuditId);
        var expectedBytes = JsonSerializer.SerializeToUtf8Bytes(audit, StorageJsonOptions);
        if (expectedBytes.LongLength > MaxAuditBytes)
        {
            throw RecoveryFailed("cleanup audit receipt 超過安全大小上限。");
        }

        if (await IsAuditReceiptPublishedAsync(paths, audit).ConfigureAwait(false))
        {
            return;
        }

        var temporaryPath = GetAuditTemporaryPath(paths, audit.AuditId);
        try
        {
            var temporaryEntry = InspectPathEntry(temporaryPath);
            if (temporaryEntry == PathEntryKind.Directory)
            {
                throw RecoveryFailed("cleanup audit 暫存路徑被資料夾占用。");
            }

            if (temporaryEntry == PathEntryKind.RegularFile)
            {
                EnsureSafeExistingFileChain(temporaryPath);
                var existing = await File.ReadAllBytesAsync(temporaryPath, CancellationToken.None)
                    .ConfigureAwait(false);
                if (!existing.AsSpan().SequenceEqual(expectedBytes))
                {
                    throw RecoveryFailed("cleanup audit 暫存檔與 journal 不一致。");
                }
            }
            else
            {
                await WriteJsonFileAsync(
                    temporaryPath,
                    audit,
                    MaxAuditBytes,
                    CancellationToken.None).ConfigureAwait(false);
            }

            File.Move(temporaryPath, finalPath);
            EnsureSafeExistingFileChain(finalPath);
        }
        finally
        {
            DeleteFileBestEffort(temporaryPath);
        }
    }

    private static async Task<bool> IsAuditReceiptPublishedAsync(
        ProjectPaths paths,
        CleanupAuditReceipt audit)
    {
        EnsureAuditDirectory(paths);
        var finalPath = GetAuditPath(paths, audit.AuditId);
        var finalEntry = InspectPathEntry(finalPath);
        if (finalEntry == PathEntryKind.Directory)
        {
            throw RecoveryFailed("cleanup audit receipt 路徑不是 regular file。");
        }

        if (finalEntry == PathEntryKind.Missing)
        {
            return false;
        }

        EnsureSafeExistingFileChain(finalPath);
        var expectedBytes = JsonSerializer.SerializeToUtf8Bytes(audit, StorageJsonOptions);
        if (expectedBytes.LongLength > MaxAuditBytes)
        {
            throw RecoveryFailed("cleanup audit receipt 超過安全大小上限。");
        }

        var actualBytes = await File.ReadAllBytesAsync(finalPath, CancellationToken.None)
            .ConfigureAwait(false);
        if (!actualBytes.AsSpan().SequenceEqual(expectedBytes))
        {
            throw RecoveryFailed("既有 cleanup audit receipt 與 journal 不一致。");
        }

        return true;
    }

    private static async Task WriteJsonFileAsync<T>(
        string path,
        T value,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        EnsureSafeFileParentChain(path);
        await using var output = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await JsonSerializer.SerializeAsync(output, value, StorageJsonOptions, cancellationToken)
            .ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (output.Length > maxBytes)
        {
            throw new JetActionException(JetErrorCodes.FileReadError, "報告產物控制檔超過安全大小上限。");
        }

        output.Flush(flushToDisk: true);
        EnsureSafeExistingFileChain(path);
    }

    private static void PublishControlFile(string temporaryPath, string destinationPath)
    {
        EnsureSafeExistingFileChain(temporaryPath);
        EnsureSafeFileParentChain(destinationPath);
        var destinationEntry = InspectPathEntry(destinationPath);
        if (destinationEntry == PathEntryKind.Directory)
        {
            throw new IOException("報告產物控制檔目標被資料夾占用。");
        }

        if (destinationEntry == PathEntryKind.RegularFile)
        {
            EnsureSafeExistingFileChain(destinationPath);
            File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null);
            return;
        }

        File.Move(temporaryPath, destinationPath);
    }

    private static async Task<IReadOnlyList<ReportArtifact>> ReadManifestAsync(
        ProjectPaths paths,
        CancellationToken cancellationToken)
    {
        var manifestEntry = InspectPathEntry(paths.ManifestPath);
        if (manifestEntry == PathEntryKind.Directory)
        {
            throw new JetActionException(JetErrorCodes.FileReadError, "報告產物索引路徑不是 regular file。");
        }

        if (manifestEntry == PathEntryKind.Missing)
        {
            return [];
        }

        var entries = await ReadJsonFileAsync<ManifestEntry[]>(
            paths.ManifestPath,
            MaxManifestBytes,
            "報告產物索引無法讀取。",
            cancellationToken).ConfigureAwait(false);
        if (entries is null || entries.Length > MaxManifestEntries)
        {
            throw InvalidManifestLimit();
        }

        return FromManifestEntries(entries, Path.GetFileName(paths.ProjectDirectory));
    }

    private static async Task<MutationJournal> ReadJournalAsync(ProjectPaths paths)
    {
        var journal = await ReadJsonFileAsync<MutationJournal>(
            paths.JournalPath,
            MaxJournalBytes,
            "報告產物 journal 無法讀取。",
            CancellationToken.None).ConfigureAwait(false)
            ?? throw RecoveryFailed("報告產物 journal 格式無效。");
        ValidateJournal(journal, Path.GetFileName(paths.ProjectDirectory));
        return journal;
    }

    private static async Task<T?> ReadJsonFileAsync<T>(
        string path,
        long maxBytes,
        string message,
        CancellationToken cancellationToken)
    {
        try
        {
            EnsureSafeExistingFileChain(path);
            await using var input = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 32 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (input.Length > maxBytes)
            {
                throw new JetActionException(JetErrorCodes.FileReadError, message);
            }

            return await JsonSerializer.DeserializeAsync<T>(input, StorageJsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JetActionException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new JetActionException(JetErrorCodes.FileReadError, message);
        }
    }

    private static IReadOnlyList<ReportArtifact> FromManifestEntries(
        IReadOnlyList<ManifestEntry?> entries,
        string projectId)
    {
        if (entries.Count > MaxManifestEntries)
        {
            throw InvalidManifestLimit();
        }

        var artifactIds = new HashSet<string>(StringComparer.Ordinal);
        var relativeFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var artifacts = new ReportArtifact[entries.Count];
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index]
                ?? throw new JetActionException(JetErrorCodes.FileReadError, "報告產物索引含空白項目。");
            var artifact = FromManifestEntry(entry, projectId);
            if (!artifactIds.Add(artifact.ArtifactId)
                || !relativeFileNames.Add(artifact.RelativeFileName))
            {
                throw new JetActionException(
                    JetErrorCodes.FileReadError,
                    "報告產物索引含重複識別碼或檔名。");
            }

            artifacts[index] = artifact;
        }

        return artifacts;
    }

    private static void ValidateJournal(MutationJournal journal, string projectId)
    {
        if (journal.FormatVersion != JournalFormatVersion
            || !IsValidArtifactId(journal.OperationId)
            || journal.Operation is not (WriteOperation or MarkStaleOperation or CleanupOperation)
            || !IsValidSha256(journal.BeforeRevision)
            || !IsValidSha256(journal.AfterRevision)
            || journal.BeforeArtifacts is null
            || journal.AfterArtifacts is null
            || journal.Transitions is null
            || journal.Transitions.Length > MaxManifestEntries + Enum.GetValues<ReportArtifactKind>().Length)
        {
            throw RecoveryFailed("報告產物 journal 欄位格式無效。");
        }

        var before = FromManifestEntries(journal.BeforeArtifacts, projectId);
        var after = FromManifestEntries(journal.AfterArtifacts, projectId);
        if (!string.Equals(ComputeCatalogRevision(before), journal.BeforeRevision, StringComparison.Ordinal)
            || !string.Equals(ComputeCatalogRevision(after), journal.AfterRevision, StringComparison.Ordinal))
        {
            throw RecoveryFailed("報告產物 journal digest 無效。");
        }

        if (journal.Transitions.Any(transition => transition is null
                || !IsValidArtifactId(transition.ArtifactId)
                || transition.Bytes < 0
                || !IsValidSha256(transition.Sha256)
                || !IsDirectChildName(transition.FromFileName)
                || !IsDirectChildName(transition.ToFileName)))
        {
            throw RecoveryFailed("報告產物 journal 含不安全的檔案轉移。");
        }

        var beforeById = before.ToDictionary(item => item.ArtifactId, StringComparer.Ordinal);
        var afterById = after.ToDictionary(item => item.ArtifactId, StringComparer.Ordinal);
        if (journal.Operation == WriteOperation)
        {
            var added = after.Where(item => !beforeById.ContainsKey(item.ArtifactId)).ToArray();
            var removed = before.Where(item => !afterById.ContainsKey(item.ArtifactId)).ToArray();
            if (journal.Audit is not null
                || journal.Transitions.Any(transition =>
                    transition.Role is not (null or PublishTransition or RetireTransition))
                || journal.Transitions.Length != added.Length + removed.Length
                || before.Where(item => afterById.ContainsKey(item.ArtifactId)).Any(item =>
                    !SameExceptAllowedStaleFlip(item, afterById[item.ArtifactId]))
                || added.Any(item => !journal.Transitions.Any(transition =>
                    IsPublishTransition(transition)
                    &&
                    transition.ArtifactId == item.ArtifactId
                    && transition.ToFileName == item.RelativeFileName
                    && transition.FromFileName == $"{StagePrefix}{journal.OperationId}-{item.ArtifactId}.tmp"
                    && transition.Bytes == item.Bytes
                    && transition.Sha256 == item.Sha256))
                || removed.Any(item => !journal.Transitions.Any(transition =>
                    IsRetireTransition(transition)
                    && transition.ArtifactId == item.ArtifactId
                    && transition.FromFileName == item.RelativeFileName
                    && transition.ToFileName == $"{QuarantinePrefix}{journal.OperationId}-{item.ArtifactId}.tmp"
                    && transition.Bytes == item.Bytes
                    && transition.Sha256 == item.Sha256)))
            {
                throw RecoveryFailed("write journal 的 before／after 差異無效。");
            }
        }
        else if (journal.Operation == MarkStaleOperation)
        {
            if (journal.Audit is not null
                || journal.Transitions.Length != 0
                || before.Count != after.Count
                || before.Any(item => !afterById.TryGetValue(item.ArtifactId, out var rewritten)
                    || !SameExceptAllowedStaleFlip(item, rewritten)))
            {
                throw RecoveryFailed("markStale journal 的 before／after 差異無效。");
            }
        }
        else
        {
            var removed = before.Where(item => !afterById.ContainsKey(item.ArtifactId)).ToArray();
            if (journal.Audit is null
                || journal.Transitions.Any(transition => transition.Role is not null)
                || journal.Transitions.Length != removed.Length
                || after.Any(item => !beforeById.TryGetValue(item.ArtifactId, out var original)
                    || !ArtifactsSemanticallyEqual(item, original))
                || removed.Any(item => !journal.Transitions.Any(transition =>
                    transition.ArtifactId == item.ArtifactId
                    && transition.FromFileName == item.RelativeFileName
                    && transition.ToFileName == $"{QuarantinePrefix}{journal.OperationId}-{item.ArtifactId}.tmp"
                    && transition.Bytes == item.Bytes
                    && transition.Sha256 == item.Sha256))
                || !AuditMatchesRemoved(
                    journal.Audit,
                    journal.BeforeRevision,
                    journal.AfterRevision,
                    before,
                    removed))
            {
                throw RecoveryFailed("cleanup journal 的 before／after 差異無效。");
            }
        }
    }

    private static bool SameExceptAllowedStaleFlip(ReportArtifact before, ReportArtifact after)
        => ArtifactsSemanticallyEqual(before with { Stale = after.Stale }, after)
            && (!before.Stale || after.Stale);

    private static bool AuditMatchesRemoved(
        CleanupAuditReceipt audit,
        string beforeRevision,
        string afterRevision,
        IReadOnlyList<ReportArtifact> before,
        IReadOnlyList<ReportArtifact> removed)
    {
        long removedBytes;
        try
        {
            removedBytes = removed.Aggregate(0L, (total, item) => checked(total + item.Bytes));
        }
        catch (OverflowException)
        {
            return false;
        }

        if (audit.FormatVersion != AuditFormatVersion
            || !IsValidArtifactId(audit.AuditId)
            || string.IsNullOrWhiteSpace(audit.RequestedBy)
            || audit.RequestedBy.Length > 256
            || !string.Equals(audit.RequestedBy, audit.RequestedBy.Trim(), StringComparison.Ordinal)
            || audit.RequestedBy.Contains('\r')
            || audit.RequestedBy.Contains('\n')
            || audit.DeletedUtc == default
            || audit.DeletedUtc.Offset != TimeSpan.Zero
            || audit.CatalogRevisionBefore != beforeRevision
            || audit.CatalogRevisionAfter != afterRevision
            || audit.DeletedCount != removed.Count
            || audit.DeletedArtifacts is null
            || audit.DeletedArtifacts.Length != removed.Count
            || audit.DeletedBytes != removedBytes
            || audit.DeletedArtifacts.Any(item => item is null || !IsValidArtifactId(item.ArtifactId))
            || audit.DeletedArtifacts.Select(item => item.ArtifactId)
                .Distinct(StringComparer.Ordinal).Count() != removed.Count)
        {
            return false;
        }

        var removedById = removed.ToDictionary(item => item.ArtifactId, StringComparer.Ordinal);
        var reasonsById = ReportArtifactRetentionPolicy.Plan(before).ToDictionary(
            candidate => candidate.Artifact.ArtifactId,
            candidate => ReportArtifactCleanupReasonValues.ToValue(candidate.Reason),
            StringComparer.Ordinal);
        if (reasonsById.Count != removed.Count
            || reasonsById.Keys.Any(artifactId => !removedById.ContainsKey(artifactId)))
        {
            return false;
        }

        var auditById = audit.DeletedArtifacts.ToDictionary(item => item.ArtifactId, StringComparer.Ordinal);
        return removed.All(artifact =>
            auditById.TryGetValue(artifact.ArtifactId, out var item)
            && reasonsById.TryGetValue(artifact.ArtifactId, out var expectedReason)
            && item.Kind == ReportArtifactKindValues.ToValue(artifact.Kind)
            && item.FileName == artifact.RelativeFileName
            && item.GeneratedUtc.ToUniversalTime() == artifact.GeneratedUtc.ToUniversalTime()
            && item.Bytes == artifact.Bytes
            && item.Reason == expectedReason);
    }

    private static ManifestEntry ToManifestEntry(ReportArtifact artifact) => new(
        artifact.ArtifactId,
        ReportArtifactKindValues.ToValue(artifact.Kind),
        artifact.RelativeFileName,
        CloneSourceRefs(artifact.SourceRef),
        artifact.GeneratedUtc.ToUniversalTime(),
        artifact.Bytes,
        artifact.Sha256,
        artifact.Stale);

    private static ReportArtifact FromManifestEntry(ManifestEntry entry, string projectId)
    {
        var sourceRef = ResolveManifestSourceRef(entry);
        var generatedUtc = ResolveManifestGeneratedUtc(entry);
        if (!IsValidArtifactId(entry.ArtifactId)
            || !ReportArtifactKindValues.TryParse(entry.Kind, out var kind)
            || sourceRef is null
            || generatedUtc is null
            || entry.Bytes < 0
            || !IsValidSha256(entry.Sha256))
        {
            throw new JetActionException(JetErrorCodes.FileReadError, "報告產物索引欄位格式無效。");
        }

        ValidateSourceRefs(sourceRef, JetErrorCodes.FileReadError);
        var normalizedGeneratedUtc = generatedUtc.Value.ToUniversalTime();
        var expectedFileName = BuildFileName(projectId, kind);
        var legacyFileName = BuildLegacyFileName(
            projectId,
            normalizedGeneratedUtc,
            kind,
            entry.ArtifactId);
        var preRenameFileName = BuildPreRenameFileName(projectId, kind);
        if (!string.Equals(entry.RelativeFileName, expectedFileName, StringComparison.Ordinal)
            && !string.Equals(entry.RelativeFileName, legacyFileName, StringComparison.Ordinal)
            && !string.Equals(entry.RelativeFileName, preRenameFileName, StringComparison.Ordinal))
        {
            throw new JetActionException(JetErrorCodes.FileReadError, "報告產物索引的相對檔名無效。");
        }

        return new ReportArtifact(
            entry.ArtifactId,
            kind,
            entry.RelativeFileName,
            CloneSourceRefs(sourceRef),
            normalizedGeneratedUtc,
            entry.Bytes,
            entry.Sha256,
            entry.Stale || !HasRequiredSourceRefs(kind, sourceRef));
    }

    private static ReportArtifactSourceRefs? ResolveManifestSourceRef(ManifestEntry entry)
    {
        if (entry.SourceRef is not null && entry.LegacySourceRefs is not null)
        {
            throw new JetActionException(JetErrorCodes.FileReadError, "報告產物索引同時含新舊來源欄位。");
        }

        return entry.SourceRef ?? entry.LegacySourceRefs;
    }

    private static DateTimeOffset? ResolveManifestGeneratedUtc(ManifestEntry entry)
    {
        if (entry.GeneratedUtc is not null && entry.LegacyCreatedUtc is not null)
        {
            throw new JetActionException(JetErrorCodes.FileReadError, "報告產物索引同時含新舊產生時間欄位。");
        }

        return entry.GeneratedUtc ?? entry.LegacyCreatedUtc;
    }

    private static string BuildFileName(string projectId, ReportArtifactKind kind)
        => $"{BuildSafeProjectFilePrefix(projectId)}_{GetReportFileSuffix(kind)}.xlsx";

    private static string BuildLegacyFileName(
        string projectId,
        DateTimeOffset generatedUtc,
        ReportArtifactKind kind,
        string artifactId)
        => $"{BuildSafeProjectFilePrefix(projectId)}_" +
            $"{generatedUtc.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)}_" +
            $"{GetReportFileSuffix(kind)}_{artifactId}.xlsx";

    private static string? BuildPreRenameFileName(string projectId, ReportArtifactKind kind)
    {
        var suffix = kind switch
        {
            ReportArtifactKind.InfReport => "INF_Report",
            ReportArtifactKind.PrescreenReport => "Pre-screeningReport",
            _ => null
        };

        return suffix is null
            ? null
            : $"{BuildSafeProjectFilePrefix(projectId)}_{suffix}.xlsx";
    }

    private static string GetReportFileSuffix(ReportArtifactKind kind)
    {
        return kind switch
        {
            ReportArtifactKind.ValidationReport => "ValidationReport",
            ReportArtifactKind.AccountMapping => "AccountMapping",
            ReportArtifactKind.InfReport => "INFReport",
            ReportArtifactKind.PrescreenReport => "PrescreeningReport",
            ReportArtifactKind.CriteriaSelectionReport => "CriteriaSelectionReport",
            ReportArtifactKind.WorkingPaper => "WorkingPaper",
            _ => throw new JetActionException(JetErrorCodes.InvalidPayload, "報告產物種類無效。")
        };
    }

    private static string BuildSafeProjectFilePrefix(string projectId)
    {
        var safeProjectId = string.Concat(projectId.Select(character =>
            char.IsControl(character) || character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*'
                ? '_'
                : character));
        safeProjectId = safeProjectId.Trim(' ', '.');
        if (safeProjectId.Length == 0)
        {
            safeProjectId = "JET";
        }

        return safeProjectId.Length <= 48 ? safeProjectId : safeProjectId[..48];
    }

    private static void EnsureManifestTargetWritable(string manifestPath)
    {
        EnsureSafeFileParentChain(manifestPath);
        var entry = InspectPathEntry(manifestPath);
        if (entry == PathEntryKind.Directory)
        {
            throw new IOException("報告產物索引目標被資料夾占用。");
        }

        if (entry == PathEntryKind.RegularFile)
        {
            EnsureSafeExistingFileChain(manifestPath);
        }
    }

    private static void EnsureAuditDirectory(ProjectPaths paths)
    {
        EnsureProjectDirectorySafe(paths.ProjectDirectory);
        var entry = InspectPathEntry(paths.AuditDirectory);
        if (entry == PathEntryKind.RegularFile)
        {
            throw CleanupFailed("報告清理稽核目錄被檔案占用。");
        }

        if (entry == PathEntryKind.Missing)
        {
            Directory.CreateDirectory(paths.AuditDirectory);
        }

        EnsureNoReparseDirectoryChain(new DirectoryInfo(paths.AuditDirectory));
    }

    private static string GetAuditPath(ProjectPaths paths, string auditId)
    {
        if (!IsValidArtifactId(auditId))
        {
            throw RecoveryFailed("cleanup auditId 格式無效。");
        }

        EnsureAuditDirectory(paths);
        return ResolveContainedPath(paths.AuditDirectory, $"{auditId}.json");
    }

    private static string GetAuditTemporaryPath(ProjectPaths paths, string auditId)
    {
        if (!IsValidArtifactId(auditId))
        {
            throw RecoveryFailed("cleanup auditId 格式無效。");
        }

        EnsureAuditDirectory(paths);
        return ResolveContainedPath(paths.AuditDirectory, $".{auditId}.tmp");
    }

    private static void CleanupUnjournaledTemporaryFiles(ProjectPaths paths)
    {
        EnsureProjectDirectorySafe(paths.ProjectDirectory);
        foreach (var path in Directory.EnumerateFiles(paths.ProjectDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (IsControlledStageName(name)
                || IsControlledSingleIdTemporaryName(name, ManifestTempPrefix)
                || IsControlledSingleIdTemporaryName(name, JournalTempPrefix))
            {
                EnsureSafeExistingFileChain(path);
                File.Delete(path);
            }
            else if (name.StartsWith(StagePrefix, StringComparison.Ordinal)
                || name.StartsWith(ManifestTempPrefix, StringComparison.Ordinal)
                || name.StartsWith(JournalTempPrefix, StringComparison.Ordinal))
            {
                throw RecoveryFailed("找到名稱不符合協定的 report temporary file；已保留現場。");
            }
            else if (name.StartsWith(QuarantinePrefix, StringComparison.Ordinal))
            {
                throw RecoveryFailed("找到沒有 journal 的 report quarantine；已保留現場。");
            }
        }

        var auditDirectoryEntry = InspectPathEntry(paths.AuditDirectory);
        if (auditDirectoryEntry == PathEntryKind.Missing)
        {
            return;
        }
        if (auditDirectoryEntry != PathEntryKind.Directory)
        {
            throw RecoveryFailed("cleanup audit 目錄不是受控資料夾。");
        }

        EnsureNoReparseDirectoryChain(new DirectoryInfo(paths.AuditDirectory));
        foreach (var path in Directory.EnumerateFiles(paths.AuditDirectory, ".*.tmp", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (!IsControlledAuditTemporaryName(name))
            {
                throw RecoveryFailed("找到名稱不符合協定的 cleanup audit temporary file；已保留現場。");
            }

            EnsureSafeExistingFileChain(path);
            File.Delete(path);
        }
    }

    private static bool IsControlledStageName(string name)
    {
        if (!name.StartsWith(StagePrefix, StringComparison.Ordinal)
            || !name.EndsWith(".tmp", StringComparison.Ordinal))
        {
            return false;
        }

        var body = name[StagePrefix.Length..^4];
        return body.Length == 65
            && body[32] == '-'
            && IsValidArtifactId(body[..32])
            && IsValidArtifactId(body[33..]);
    }

    private static bool IsControlledSingleIdTemporaryName(string name, string prefix)
    {
        if (!name.StartsWith(prefix, StringComparison.Ordinal)
            || !name.EndsWith(".tmp", StringComparison.Ordinal))
        {
            return false;
        }

        return IsValidArtifactId(name[prefix.Length..^4]);
    }

    private static bool IsControlledAuditTemporaryName(string name)
        => name.Length == 37
            && name[0] == '.'
            && name.EndsWith(".tmp", StringComparison.Ordinal)
            && IsValidArtifactId(name[1..^4]);

    /// <summary>
    /// File.Exists／Directory.Exists 會把 dangling symlink 當成不存在；控制檔與 recovery 路徑
    /// 必須先讀 link metadata，再區分真正 missing、regular file 與 directory。
    /// </summary>
    private static PathEntryKind InspectPathEntry(string path)
    {
        try
        {
            EnsureSafeFileParentChain(path);
            var file = new FileInfo(path);
            var directory = new DirectoryInfo(path);
            if (file.LinkTarget is not null || directory.LinkTarget is not null)
            {
                throw UnsafeFileSystemLink();
            }

            file.Refresh();
            if (file.Exists)
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw UnsafeFileSystemLink();
                }

                return PathEntryKind.RegularFile;
            }

            directory.Refresh();
            if (directory.Exists)
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw UnsafeFileSystemLink();
                }

                return PathEntryKind.Directory;
            }

            return PathEntryKind.Missing;
        }
        catch (JetActionException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or NotSupportedException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            throw new JetActionException(
                JetErrorCodes.FileReadError,
                "無法安全判定報告產物路徑項目。");
        }
    }

    private static string ResolveContainedPath(string directory, string relativeFileName)
    {
        if (!IsDirectChildName(relativeFileName))
        {
            throw new JetActionException(JetErrorCodes.FileReadError, "報告產物的相對檔名無效。");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var candidate = Path.GetFullPath(Path.Combine(root, relativeFileName));
        if (!candidate.StartsWith(AppendDirectorySeparator(root), PathComparison))
        {
            throw new JetActionException(JetErrorCodes.FileReadError, "報告產物路徑超出受控資料夾。");
        }

        return candidate;
    }

    private static bool IsDirectChildName(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && !Path.IsPathRooted(value)
            && string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal)
            && value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0;

    private static string AppendDirectorySeparator(string path)
        => Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;

    private static void EnsureProjectDirectorySafe(string projectDirectory)
        => EnsureNoReparseDirectoryChain(
            new DirectoryInfo(projectDirectory),
            missingLeafIsProjectNotFound: true);

    private static void EnsureSafeExistingFileChain(string path)
    {
        try
        {
            var file = new FileInfo(path);
            EnsureNoReparseDirectoryChain(file.Directory ?? throw new IOException("檔案缺少父資料夾。"));
            if (file.LinkTarget is not null)
            {
                throw UnsafeFileSystemLink();
            }

            file.Refresh();
            if (!file.Exists)
            {
                throw new JetActionException(JetErrorCodes.FileNotFound, "指定的報告產物檔案不存在。");
            }

            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw UnsafeFileSystemLink();
            }
        }
        catch (JetActionException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or NotSupportedException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            throw new JetActionException(JetErrorCodes.FileReadError, "無法驗證報告產物的本機路徑安全性。");
        }
    }

    private static void EnsureSafeFileParentChain(string path)
    {
        try
        {
            var parent = new FileInfo(path).Directory ?? throw new IOException("檔案缺少父資料夾。");
            EnsureNoReparseDirectoryChain(parent);
        }
        catch (JetActionException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or NotSupportedException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            throw new JetActionException(JetErrorCodes.FileReadError, "無法驗證報告產物的父路徑安全性。");
        }
    }

    private static void EnsureNoReparseDirectoryChain(
        DirectoryInfo directory,
        bool missingLeafIsProjectNotFound = false)
    {
        try
        {
            var chain = new Stack<DirectoryInfo>();
            for (DirectoryInfo? current = directory; current is not null; current = current.Parent)
            {
                chain.Push(current);
            }

            foreach (var current in chain)
            {
                if (current.LinkTarget is not null)
                {
                    throw UnsafeFileSystemLink();
                }

                current.Refresh();
                if (!current.Exists)
                {
                    if (missingLeafIsProjectNotFound
                        && string.Equals(current.FullName, directory.FullName, PathComparison))
                    {
                        throw new JetActionException(JetErrorCodes.ProjectNotFound, "找不到指定的專案資料夾。");
                    }

                    throw new IOException("路徑祖先不存在。");
                }

                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw UnsafeFileSystemLink();
                }
            }
        }
        catch (JetActionException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or NotSupportedException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            throw new JetActionException(JetErrorCodes.FileReadError, "無法驗證專案資料夾的本機路徑安全性。");
        }
    }

    private static JetActionException UnsafeFileSystemLink() => new(
        JetErrorCodes.FileReadError,
        "專案報告路徑不可包含檔案系統連結或 junction。");

    private static bool IsValidArtifactId(string? artifactId)
        => artifactId is { Length: 32 }
            && string.Equals(artifactId, artifactId.ToLowerInvariant(), StringComparison.Ordinal)
            && Guid.TryParseExact(artifactId, "N", out _);

    private static bool IsValidSha256(string? sha256)
        => sha256 is { Length: 64 }
            && sha256.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static ReportArtifact CloneArtifact(ReportArtifact artifact)
        => artifact with { SourceRef = CloneSourceRefs(artifact.SourceRef) };

    private static ReportArtifactSourceRefs CloneSourceRefs(ReportArtifactSourceRefs sourceRefs)
        => sourceRefs with
        {
            ScenarioPositions = sourceRefs.ScenarioPositions is null
                ? null
                : Array.AsReadOnly(sourceRefs.ScenarioPositions.ToArray())
        };

    private static JetActionException InvalidManifestLimit() => new(
        JetErrorCodes.FileReadError,
        "報告產物索引超過安全大小或項目上限。");

    private static JetActionException CatalogChanged() => new(
        JetErrorCodes.ArtifactCatalogChanged,
        "報告版本清理預覽已過期，請重新檢查後再確認。");

    private static JetActionException CleanupFailed(string message, Exception? inner = null)
        => inner is null
            ? new JetActionException(JetErrorCodes.ArtifactCleanupFailed, message)
            : new JetActionException(
                JetErrorCodes.ArtifactCleanupFailed,
                $"{message}（{inner.GetType().Name}）");

    private static JetActionException RecoveryFailed(string message)
        => new(JetErrorCodes.FileReadError, message);

    private static JetActionException RecoveryConflict(string message)
        => new(JetErrorCodes.ArtifactRecoveryConflict, message);

    private static void DeleteControlFile(string path)
    {
        var entry = InspectPathEntry(path);
        if (entry == PathEntryKind.Missing)
        {
            return;
        }
        if (entry == PathEntryKind.Directory)
        {
            throw RecoveryFailed("報告產物控制檔路徑被資料夾占用。");
        }

        EnsureSafeExistingFileChain(path);
        File.Delete(path);
    }

    private static void DeleteFileBestEffort(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 不遮蔽原始失敗；有 fixed journal 的 mutation 會由下一個持鎖者完成 recovery。
        }
    }

    private void Checkpoint(ReportArtifactStoreCheckpoint checkpoint)
        => _testHooks?.OnCheckpoint?.Invoke(checkpoint);

    private static void DisposeStreams(List<FileStream> streams)
    {
        foreach (var stream in streams)
        {
            stream.Dispose();
        }

        streams.Clear();
    }

    private static JsonSerializerOptions CreateStorageJsonOptions()
        => new(JetJsonStorage.IndentedOptions)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

    private static JsonSerializerOptions CreateCanonicalJsonOptions()
        => new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = false
        };

    private sealed record ProjectPaths(
        string RootDirectory,
        string ProjectDirectory,
        string LockPath,
        string ManifestPath,
        string JournalPath,
        string AuditDirectory);

    private sealed record ManifestEntry(
        string ArtifactId,
        string Kind,
        string RelativeFileName,
        ReportArtifactSourceRefs? SourceRef,
        DateTimeOffset? GeneratedUtc,
        long Bytes,
        string Sha256,
        bool Stale)
    {
        [JsonPropertyName("sourceRefs")]
        public ReportArtifactSourceRefs? LegacySourceRefs { get; init; }

        [JsonPropertyName("createdUtc")]
        public DateTimeOffset? LegacyCreatedUtc { get; init; }
    }

    private sealed record MutationJournal(
        int FormatVersion,
        string OperationId,
        string Operation,
        string BeforeRevision,
        string AfterRevision,
        ManifestEntry[] BeforeArtifacts,
        ManifestEntry[] AfterArtifacts,
        FileTransition[] Transitions,
        CleanupAuditReceipt? Audit);

    private sealed record FileTransition(
        string FromFileName,
        string ToFileName,
        long Bytes,
        string Sha256,
        string ArtifactId,
        string? Role = null);

    private sealed record CleanupAuditReceipt(
        int FormatVersion,
        string AuditId,
        DateTimeOffset DeletedUtc,
        string RequestedBy,
        string CatalogRevisionBefore,
        string CatalogRevisionAfter,
        int DeletedCount,
        long DeletedBytes,
        CleanupAuditArtifact[] DeletedArtifacts);

    private sealed record CleanupAuditArtifact(
        string ArtifactId,
        string Kind,
        string FileName,
        DateTimeOffset GeneratedUtc,
        long Bytes,
        string Reason);

    private sealed record CanonicalArtifact(
        string ArtifactId,
        string Kind,
        string RelativeFileName,
        string? ValidationRunId,
        string? PrescreenRunId,
        string? ScenarioRevision,
        int[]? ScenarioPositions,
        DateTimeOffset GeneratedUtc,
        long Bytes,
        string Sha256,
        bool Stale);

    private sealed record StagedArtifact(
        string StageFileName,
        string StagePath,
        string FinalPath,
        ReportArtifact Artifact);

    private sealed class ArtifactProjectLease(FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }

    private enum RecoveryDisposition
    {
        None,
        RolledBack,
        Committed
    }

    private enum PathEntryKind
    {
        Missing,
        RegularFile,
        Directory
    }
}

internal static partial class ReportArtifactStoreDiagnostics
{
    [LoggerMessage(
        EventId = 2200,
        EventName = "artifact.recovery.conflict",
        Level = LogLevel.Error,
        Message = "artifact recovery conflict operation={operation} manifest={manifest_state} role={transition_role} kind={artifact_kind} stage={stage_state} final={final_state} quarantine={quarantine_state} expectedBytes={expected_bytes} actualBytes={actual_bytes} newMatch={new_content_matches} oldFallback={old_fallback_matches} code={error_code}")]
    public static partial void RecoveryConflict(
        ILogger logger,
        string operation,
        string manifest_state,
        string transition_role,
        string artifact_kind,
        string stage_state,
        string final_state,
        string quarantine_state,
        long expected_bytes,
        long actual_bytes,
        bool new_content_matches,
        bool old_fallback_matches,
        string error_code);
}

internal enum ReportArtifactStoreCheckpoint
{
    JournalPublished,
    FileTransitionsApplied,
    ManifestCommitted,
    AuditPublished,
    QuarantineFileDeleted
}

internal sealed class ReportArtifactStoreTestHooks
{
    public Action<ReportArtifactStoreCheckpoint>? OnCheckpoint { get; init; }
}

/// <summary>測試專用：模擬 process 在 checkpoint 直接終止，store 不執行同 process recovery。</summary>
internal sealed class ReportArtifactStoreSimulatedCrashException(string message) : Exception(message);
