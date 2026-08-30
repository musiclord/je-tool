using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>P4：artifact catalog 的跨 store、durability、清理與刪案 lease 對抗測試。</summary>
public sealed class ProjectReportArtifactConcurrencyTests
{
    private const string ProjectId = "報告產物並行測試案件";
    private const string ValidationRun1 = "11111111111111111111111111111111";
    private const string ValidationRun2 = "22222222222222222222222222222222";
    private const string ValidationRun3 = "33333333333333333333333333333333";
    private static readonly DateTimeOffset BaseUtc =
        new(2026, 7, 11, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TwoStoreInstances_ConcurrentWrites_LeaveOneCompleteWinningArtifact()
    {
        using var root = new TempProjectRoot();
        var (folder, projectDirectory) = CreateProject(root);
        var first = new ProjectReportArtifactStore(folder, new MutableTimeProvider(BaseUtc));
        var second = new ProjectReportArtifactStore(folder, new MutableTimeProvider(BaseUtc.AddSeconds(1)));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var writes = new[]
        {
            Task.Run(async () =>
            {
                await start.Task;
                return await first.WriteAsync(ProjectId, Request(ValidationRun1, "first"), CancellationToken.None);
            }),
            Task.Run(async () =>
            {
                await start.Task;
                return await second.WriteAsync(ProjectId, Request(ValidationRun1, "second"), CancellationToken.None);
            })
        };
        start.SetResult();

        var written = await Task.WhenAll(writes);
        var catalog = await first.ReadCatalogAsync(ProjectId, CancellationToken.None);

        var current = Assert.Single(catalog.Artifacts);
        Assert.Contains(current.ArtifactId, written.Select(item => item.ArtifactId));
        Assert.Equal($"{ProjectId}_ValidationReport.xlsx", current.RelativeFileName);
        var bytes = await File.ReadAllBytesAsync(
            Path.Combine(projectDirectory, current.RelativeFileName));
        Assert.Equal(current.Sha256, Convert.ToHexString(SHA256.HashData(bytes)));
        Assert.Contains(Encoding.UTF8.GetString(bytes), new[] { "first", "second" });
        Assert.Single(Directory.GetFiles(projectDirectory, "*.xlsx"));
    }

    [Fact]
    public async Task TwoStoreInstances_DifferentValidityWrites_LeaveOnlyLastSerializedArtifact()
    {
        using var root = new TempProjectRoot();
        var (folder, _) = CreateProject(root);
        var seed = new ProjectReportArtifactStore(folder, new MutableTimeProvider(BaseUtc));
        var old = await seed.WriteAsync(
            ProjectId,
            Request(ValidationRun1, "old"),
            CancellationToken.None);
        var first = new ProjectReportArtifactStore(folder, new MutableTimeProvider(BaseUtc.AddSeconds(1)));
        var second = new ProjectReportArtifactStore(folder, new MutableTimeProvider(BaseUtc.AddSeconds(2)));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = new[]
        {
            Task.Run(async () =>
            {
                await start.Task;
                return await first.WriteAsync(ProjectId, Request(ValidationRun2, "new-2"), CancellationToken.None);
            }),
            Task.Run(async () =>
            {
                await start.Task;
                return await second.WriteAsync(ProjectId, Request(ValidationRun3, "new-3"), CancellationToken.None);
            })
        };
        start.SetResult();
        var newer = await Task.WhenAll(tasks);

        var artifacts = (await seed.ReadCatalogAsync(ProjectId, CancellationToken.None)).Artifacts;
        var current = Assert.Single(artifacts);
        Assert.NotEqual(old.ArtifactId, current.ArtifactId);
        Assert.False(current.Stale);
        Assert.Contains(
            current.ArtifactId,
            newer.Select(item => item.ArtifactId));
    }

    [Fact]
    public async Task Cleanup_OldCatalogRevision_RejectsWithoutDeletingFiles()
    {
        using var root = new TempProjectRoot();
        var (folder, projectDirectory) = CreateProject(root);
        var clock = new MutableTimeProvider(BaseUtc);
        var store = new ProjectReportArtifactStore(folder, clock);
        await WriteVersionsAsync(store, projectDirectory, clock, count: 4);
        var previewCatalog = await store.ReadCatalogAsync(ProjectId, CancellationToken.None);
        var candidates = ReportArtifactRetentionPolicy.Plan(previewCatalog.Artifacts);
        clock.Advance();
        await store.WriteAsync(ProjectId, Request(ValidationRun1, "newer"), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<JetActionException>(() => store.CleanupAsync(
            ProjectId,
            previewCatalog.Revision,
            candidates,
            "CONTOSO\\auditor",
            CancellationToken.None));

        Assert.Equal(JetErrorCodes.ArtifactCatalogChanged, exception.Code);
        Assert.Single(Directory.GetFiles(projectDirectory, "*.xlsx"));
        Assert.False(File.Exists(Path.Combine(projectDirectory, ProjectReportArtifactStore.JournalFileName)));
    }

    [Fact]
    public async Task Cleanup_FiveValidVersions_RetainsNewestThreeAndWritesImmutableProjectAudit()
    {
        using var root = new TempProjectRoot();
        var (folder, projectDirectory) = CreateProject(root);
        var clock = new MutableTimeProvider(BaseUtc);
        var store = new ProjectReportArtifactStore(folder, clock);
        var written = await WriteVersionsAsync(store, projectDirectory, clock, count: 5);
        var catalog = await store.ReadCatalogAsync(ProjectId, CancellationToken.None);
        var candidates = ReportArtifactRetentionPolicy.Plan(catalog.Artifacts);

        var result = await store.CleanupAsync(
            ProjectId,
            catalog.Revision,
            candidates,
            "CONTOSO\\auditor",
            CancellationToken.None);

        Assert.Equal(2, result.DeletedCount);
        Assert.Equal(3, result.Catalog.Artifacts.Count);
        Assert.Equal(
            written.Skip(2).Select(item => item.ArtifactId).Order(),
            result.Catalog.Artifacts.Select(item => item.ArtifactId).Order());
        Assert.Equal(3, Directory.GetFiles(projectDirectory, "*.xlsx").Length);
        Assert.False(File.Exists(Path.Combine(projectDirectory, ProjectReportArtifactStore.JournalFileName)));
        Assert.Empty(Directory.GetFiles(projectDirectory, ".report-artifact-quarantine-*"));

        var auditDirectory = Path.Combine(projectDirectory, ProjectReportArtifactStore.AuditDirectoryName);
        var auditPath = Assert.Single(Directory.GetFiles(auditDirectory, "*.json"));
        using var audit = JsonDocument.Parse(await File.ReadAllTextAsync(auditPath));
        Assert.Equal(result.AuditId, audit.RootElement.GetProperty("auditId").GetString());
        Assert.Equal(2, audit.RootElement.GetProperty("deletedCount").GetInt32());
        Assert.Equal("CONTOSO\\auditor", audit.RootElement.GetProperty("requestedBy").GetString());
        Assert.DoesNotContain(root.Path, await File.ReadAllTextAsync(auditPath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cleanup_LaterCandidateLocked_RollsBackWholeBatchAndLeavesNoAudit()
    {
        using var root = new TempProjectRoot();
        var (folder, projectDirectory) = CreateProject(root);
        var clock = new MutableTimeProvider(BaseUtc);
        var store = new ProjectReportArtifactStore(folder, clock);
        await WriteVersionsAsync(store, projectDirectory, clock, count: 5);
        var catalog = await store.ReadCatalogAsync(ProjectId, CancellationToken.None);
        var candidates = ReportArtifactRetentionPolicy.Plan(catalog.Artifacts);
        var laterCandidate = candidates.Last();
        await using var locked = new FileStream(
            Path.Combine(projectDirectory, laterCandidate.Artifact.RelativeFileName),
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

        var exception = await Assert.ThrowsAsync<JetActionException>(() => store.CleanupAsync(
            ProjectId,
            catalog.Revision,
            candidates,
            "CONTOSO\\auditor",
            CancellationToken.None));

        Assert.Equal(JetErrorCodes.ArtifactCleanupFailed, exception.Code);
        Assert.Equal(5, (await store.ListAsync(ProjectId, CancellationToken.None)).Count);
        Assert.Equal(5, Directory.GetFiles(projectDirectory, "*.xlsx").Length);
        Assert.False(File.Exists(Path.Combine(projectDirectory, ProjectReportArtifactStore.JournalFileName)));
        Assert.Empty(Directory.GetFiles(projectDirectory, ".report-artifact-quarantine-*"));
        var auditDirectory = Path.Combine(projectDirectory, ProjectReportArtifactStore.AuditDirectoryName);
        Assert.True(!Directory.Exists(auditDirectory) || Directory.GetFiles(auditDirectory, "*.json").Length == 0);
    }

    [Fact]
    public async Task Cleanup_CancelledBeforeManifestCommit_RestoresAllFiles()
    {
        using var root = new TempProjectRoot();
        var (folder, projectDirectory) = CreateProject(root);
        var clock = new MutableTimeProvider(BaseUtc);
        var seed = new ProjectReportArtifactStore(folder, clock);
        await WriteVersionsAsync(seed, projectDirectory, clock, count: 4);
        var catalog = await seed.ReadCatalogAsync(ProjectId, CancellationToken.None);
        var candidates = ReportArtifactRetentionPolicy.Plan(catalog.Artifacts);
        using var cancellation = new CancellationTokenSource();
        var store = new ProjectReportArtifactStore(
            folder,
            clock,
            new ReportArtifactStoreTestHooks
            {
                OnCheckpoint = checkpoint =>
                {
                    if (checkpoint == ReportArtifactStoreCheckpoint.FileTransitionsApplied)
                    {
                        cancellation.Cancel();
                    }
                }
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CleanupAsync(
            ProjectId,
            catalog.Revision,
            candidates,
            "CONTOSO\\auditor",
            cancellation.Token));

        Assert.Equal(4, (await seed.ListAsync(ProjectId, CancellationToken.None)).Count);
        Assert.Equal(4, Directory.GetFiles(projectDirectory, "*.xlsx").Length);
        Assert.False(File.Exists(Path.Combine(projectDirectory, ProjectReportArtifactStore.JournalFileName)));
        Assert.Empty(Directory.GetFiles(projectDirectory, ".report-artifact-quarantine-*"));
    }

    [Fact]
    public async Task Cleanup_CancelledAfterManifestCommit_CompletesAuditAndReturnsSuccess()
    {
        using var root = new TempProjectRoot();
        var (folder, projectDirectory) = CreateProject(root);
        var clock = new MutableTimeProvider(BaseUtc);
        var seed = new ProjectReportArtifactStore(folder, clock);
        await WriteVersionsAsync(seed, projectDirectory, clock, count: 4);
        var catalog = await seed.ReadCatalogAsync(ProjectId, CancellationToken.None);
        var candidates = ReportArtifactRetentionPolicy.Plan(catalog.Artifacts);
        using var cancellation = new CancellationTokenSource();
        var store = new ProjectReportArtifactStore(
            folder,
            clock,
            new ReportArtifactStoreTestHooks
            {
                OnCheckpoint = checkpoint =>
                {
                    if (checkpoint == ReportArtifactStoreCheckpoint.ManifestCommitted)
                    {
                        cancellation.Cancel();
                    }
                }
            });

        var result = await store.CleanupAsync(
            ProjectId,
            catalog.Revision,
            candidates,
            "CONTOSO\\auditor",
            cancellation.Token);

        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(3, result.Catalog.Artifacts.Count);
        Assert.Single(Directory.GetFiles(
            Path.Combine(projectDirectory, ProjectReportArtifactStore.AuditDirectoryName),
            "*.json"));
        Assert.False(File.Exists(Path.Combine(projectDirectory, ProjectReportArtifactStore.JournalFileName)));
    }

    [Theory]
    [InlineData(0, 4, false)]
    [InlineData(1, 4, false)]
    [InlineData(2, 3, true)]
    [InlineData(3, 3, true)]
    [InlineData(4, 3, true)]
    public async Task Cleanup_ProcessCrash_RecoversByManifestCommitBoundary(
        int crashAtValue,
        int expectedRemaining,
        bool expectAudit)
    {
        var crashAt = (ReportArtifactStoreCheckpoint)crashAtValue;
        using var root = new TempProjectRoot();
        var (folder, projectDirectory) = CreateProject(root);
        var clock = new MutableTimeProvider(BaseUtc);
        var seed = new ProjectReportArtifactStore(folder, clock);
        await WriteVersionsAsync(seed, projectDirectory, clock, count: 4);
        var catalog = await seed.ReadCatalogAsync(ProjectId, CancellationToken.None);
        var candidates = ReportArtifactRetentionPolicy.Plan(catalog.Artifacts);
        var crashing = new ProjectReportArtifactStore(
            folder,
            clock,
            new ReportArtifactStoreTestHooks
            {
                OnCheckpoint = checkpoint =>
                {
                    if (checkpoint == crashAt)
                    {
                        throw new ReportArtifactStoreSimulatedCrashException("simulated process death");
                    }
                }
            });

        await Assert.ThrowsAsync<ReportArtifactStoreSimulatedCrashException>(() => crashing.CleanupAsync(
            ProjectId,
            catalog.Revision,
            candidates,
            "CONTOSO\\auditor",
            CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(projectDirectory, ProjectReportArtifactStore.JournalFileName)));

        var recovered = await new ProjectReportArtifactStore(folder, clock)
            .ListAsync(ProjectId, CancellationToken.None);

        Assert.Equal(expectedRemaining, recovered.Count);
        Assert.Equal(expectedRemaining, Directory.GetFiles(projectDirectory, "*.xlsx").Length);
        Assert.False(File.Exists(Path.Combine(projectDirectory, ProjectReportArtifactStore.JournalFileName)));
        Assert.Empty(Directory.GetFiles(projectDirectory, ".report-artifact-quarantine-*"));
        var auditDirectory = Path.Combine(projectDirectory, ProjectReportArtifactStore.AuditDirectoryName);
        Assert.Equal(
            expectAudit ? 1 : 0,
            Directory.Exists(auditDirectory) ? Directory.GetFiles(auditDirectory, "*.json").Length : 0);
    }

    [Fact]
    public async Task Cleanup_CommittedButQuarantineMissingBeforeAudit_FailsClosed()
    {
        using var root = new TempProjectRoot();
        var (folder, projectDirectory) = CreateProject(root);
        var clock = new MutableTimeProvider(BaseUtc);
        var seed = new ProjectReportArtifactStore(folder, clock);
        await WriteVersionsAsync(seed, projectDirectory, clock, count: 4);
        var catalog = await seed.ReadCatalogAsync(ProjectId, CancellationToken.None);
        var candidates = ReportArtifactRetentionPolicy.Plan(catalog.Artifacts);
        var crashing = CrashAt(folder, clock, ReportArtifactStoreCheckpoint.ManifestCommitted);

        await Assert.ThrowsAsync<ReportArtifactStoreSimulatedCrashException>(() => crashing.CleanupAsync(
            ProjectId,
            catalog.Revision,
            candidates,
            "CONTOSO\\auditor",
            CancellationToken.None));
        File.Delete(Assert.Single(Directory.GetFiles(projectDirectory, ".report-artifact-quarantine-*")));

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            new ProjectReportArtifactStore(folder, clock).ListAsync(ProjectId, CancellationToken.None));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
        Assert.True(File.Exists(Path.Combine(projectDirectory, ProjectReportArtifactStore.JournalFileName)));
        var auditDirectory = Path.Combine(projectDirectory, ProjectReportArtifactStore.AuditDirectoryName);
        Assert.Empty(Directory.GetFiles(auditDirectory, "*.json"));
    }

    [Fact]
    public async Task Cleanup_TamperedAuditWithDuplicateAndMissingArtifact_FailsClosed()
    {
        using var root = new TempProjectRoot();
        var (folder, projectDirectory) = CreateProject(root);
        var clock = new MutableTimeProvider(BaseUtc);
        var seed = new ProjectReportArtifactStore(folder, clock);
        await WriteVersionsAsync(seed, projectDirectory, clock, count: 5);
        var catalog = await seed.ReadCatalogAsync(ProjectId, CancellationToken.None);
        var candidates = ReportArtifactRetentionPolicy.Plan(catalog.Artifacts);
        var crashing = CrashAt(folder, clock, ReportArtifactStoreCheckpoint.ManifestCommitted);

        await Assert.ThrowsAsync<ReportArtifactStoreSimulatedCrashException>(() => crashing.CleanupAsync(
            ProjectId,
            catalog.Revision,
            candidates,
            "CONTOSO\\auditor",
            CancellationToken.None));
        var journalPath = Path.Combine(projectDirectory, ProjectReportArtifactStore.JournalFileName);
        var journal = JsonNode.Parse(await File.ReadAllTextAsync(journalPath))!.AsObject();
        var deleted = journal["audit"]!["deletedArtifacts"]!.AsArray();
        deleted[1] = deleted[0]!.DeepClone();
        await File.WriteAllTextAsync(
            journalPath,
            journal.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            new ProjectReportArtifactStore(folder, clock).ListAsync(ProjectId, CancellationToken.None));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
        Assert.True(File.Exists(journalPath));
        Assert.Equal(2, Directory.GetFiles(projectDirectory, ".report-artifact-quarantine-*").Length);
        Assert.Empty(Directory.GetFiles(
            Path.Combine(projectDirectory, ProjectReportArtifactStore.AuditDirectoryName),
            "*.json"));
    }

    [Fact]
    public async Task Cleanup_CommittedButAuditPathIsDirectory_FailsClosedWithJournalAndQuarantine()
    {
        using var root = new TempProjectRoot();
        var (folder, projectDirectory) = CreateProject(root);
        var clock = new MutableTimeProvider(BaseUtc);
        var seed = new ProjectReportArtifactStore(folder, clock);
        await WriteVersionsAsync(seed, projectDirectory, clock, count: 4);
        var catalog = await seed.ReadCatalogAsync(ProjectId, CancellationToken.None);
        var candidates = ReportArtifactRetentionPolicy.Plan(catalog.Artifacts);
        var crashing = CrashAt(folder, clock, ReportArtifactStoreCheckpoint.ManifestCommitted);

        await Assert.ThrowsAsync<ReportArtifactStoreSimulatedCrashException>(() => crashing.CleanupAsync(
            ProjectId,
            catalog.Revision,
            candidates,
            "CONTOSO\\auditor",
            CancellationToken.None));
        var journalPath = Path.Combine(projectDirectory, ProjectReportArtifactStore.JournalFileName);
        var journal = JsonNode.Parse(await File.ReadAllTextAsync(journalPath))!.AsObject();
        var auditId = journal["audit"]!["auditId"]!.GetValue<string>();
        var auditDirectory = Path.Combine(projectDirectory, ProjectReportArtifactStore.AuditDirectoryName);
        Directory.CreateDirectory(Path.Combine(auditDirectory, $"{auditId}.json"));

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            new ProjectReportArtifactStore(folder, clock).ListAsync(ProjectId, CancellationToken.None));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
        Assert.True(File.Exists(journalPath));
        Assert.Single(Directory.GetFiles(projectDirectory, ".report-artifact-quarantine-*"));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, true)]
    public async Task MarkStale_ProcessCrash_RecoversByManifestCommitBoundary(
        int crashAtValue,
        bool expectedStale)
    {
        using var root = new TempProjectRoot();
        var (folder, _) = CreateProject(root);
        var clock = new MutableTimeProvider(BaseUtc);
        var seed = new ProjectReportArtifactStore(folder, clock);
        await seed.WriteAsync(ProjectId, Request(ValidationRun1, "content"), CancellationToken.None);
        var crashing = CrashAt(folder, clock, (ReportArtifactStoreCheckpoint)crashAtValue);

        await Assert.ThrowsAsync<ReportArtifactStoreSimulatedCrashException>(() => crashing.MarkStaleAsync(
            ProjectId,
            ReportArtifactKind.ValidationReport,
            CancellationToken.None));

        var artifact = Assert.Single(await new ProjectReportArtifactStore(folder, clock)
            .ListAsync(ProjectId, CancellationToken.None));
        Assert.Equal(expectedStale, artifact.Stale);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    public async Task Write_ProcessCrash_RecoversByManifestCommitBoundary(
        int crashAtValue,
        int expectedCount)
    {
        var crashAt = (ReportArtifactStoreCheckpoint)crashAtValue;
        using var root = new TempProjectRoot();
        var (folder, projectDirectory) = CreateProject(root);
        var crashing = new ProjectReportArtifactStore(
            folder,
            new MutableTimeProvider(BaseUtc),
            new ReportArtifactStoreTestHooks
            {
                OnCheckpoint = checkpoint =>
                {
                    if (checkpoint == crashAt)
                    {
                        throw new ReportArtifactStoreSimulatedCrashException("simulated process death");
                    }
                }
            });

        await Assert.ThrowsAsync<ReportArtifactStoreSimulatedCrashException>(() => crashing.WriteAsync(
            ProjectId,
            Request(ValidationRun1, "content"),
            CancellationToken.None));

        var recovered = await new ProjectReportArtifactStore(folder)
            .ListAsync(ProjectId, CancellationToken.None);
        Assert.Equal(expectedCount, recovered.Count);
        Assert.Equal(expectedCount, Directory.GetFiles(projectDirectory, "*.xlsx").Length);
        Assert.False(File.Exists(Path.Combine(projectDirectory, ProjectReportArtifactStore.JournalFileName)));
    }

    [Theory]
    [InlineData(0, "old")]
    [InlineData(1, "old")]
    [InlineData(2, "new")]
    public async Task Write_ReplacementCrash_RecoversOldOrNewArtifactByManifestCommitBoundary(
        int crashAtValue,
        string expectedContent)
    {
        using var root = new TempProjectRoot();
        var (folder, projectDirectory) = CreateProject(root);
        var clock = new MutableTimeProvider(BaseUtc);
        var seed = new ProjectReportArtifactStore(folder, clock);
        var old = await seed.WriteAsync(
            ProjectId,
            Request(ValidationRun1, "old"),
            CancellationToken.None);
        clock.Advance();
        var crashing = CrashAt(folder, clock, (ReportArtifactStoreCheckpoint)crashAtValue);

        await Assert.ThrowsAsync<ReportArtifactStoreSimulatedCrashException>(() => crashing.WriteAsync(
            ProjectId,
            Request(ValidationRun2, "new"),
            CancellationToken.None));

        var recoveredStore = new ProjectReportArtifactStore(folder, clock);
        var recovered = Assert.Single(await recoveredStore.ListAsync(ProjectId, CancellationToken.None));
        Assert.Equal(old.RelativeFileName, recovered.RelativeFileName);
        Assert.Equal(
            expectedContent,
            await File.ReadAllTextAsync(Path.Combine(projectDirectory, recovered.RelativeFileName)));
        Assert.Equal(expectedContent == "old" ? ValidationRun1 : ValidationRun2, recovered.SourceRef.ValidationRunId);
        Assert.Single(Directory.GetFiles(projectDirectory, "*.xlsx"));
        Assert.Empty(Directory.GetFiles(projectDirectory, ".report-artifact-*.tmp"));
        Assert.False(File.Exists(Path.Combine(projectDirectory, ProjectReportArtifactStore.JournalFileName)));
    }

    [Fact]
    public async Task ReadCatalog_MaliciousTraversalJournal_FailsClosedAndDoesNotTouchOutsideFile()
    {
        using var root = new TempProjectRoot();
        var (folder, projectDirectory) = CreateProject(root);
        var store = new ProjectReportArtifactStore(folder);
        var emptyRevision = (await store.ReadCatalogAsync(ProjectId, CancellationToken.None)).Revision;
        var outside = Path.Combine(root.Path, "outside.xlsx");
        await File.WriteAllTextAsync(outside, "outside");
        var operationId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var journal = new
        {
            formatVersion = 1,
            operationId,
            operation = "writeBatch",
            beforeRevision = emptyRevision,
            afterRevision = emptyRevision,
            beforeArtifacts = Array.Empty<object>(),
            afterArtifacts = Array.Empty<object>(),
            transitions = new[]
            {
                new
                {
                    fromFileName = "..\\outside.xlsx",
                    toFileName = "inside.xlsx",
                    bytes = 7,
                    sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("outside"))),
                    artifactId = operationId
                }
            },
            audit = (object?)null
        };
        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory, ProjectReportArtifactStore.JournalFileName),
            JsonSerializer.Serialize(journal, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var exception = await Assert.ThrowsAsync<JetActionException>(() => store.ReadCatalogAsync(
            ProjectId,
            CancellationToken.None));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
        Assert.Equal("outside", await File.ReadAllTextAsync(outside));
        Assert.True(File.Exists(Path.Combine(projectDirectory, ProjectReportArtifactStore.JournalFileName)));
    }

    [Fact]
    public async Task ProjectDeletionLease_BlocksOtherStoreUntilLeaseIsReleased()
    {
        using var root = new TempProjectRoot();
        var (folder, _) = CreateProject(root);
        var first = new ProjectReportArtifactStore(folder);
        var second = new ProjectReportArtifactStore(folder);
        var lease = await first.AcquireProjectDeletionLeaseAsync(ProjectId, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var blockedRead = second.ReadCatalogAsync(ProjectId, timeout.Token);
        await Task.Delay(100, CancellationToken.None);
        Assert.False(blockedRead.IsCompleted);

        await lease.DisposeAsync();
        var catalog = await blockedRead;
        Assert.Empty(catalog.Artifacts);
    }

    [Fact]
    public async Task ProjectDeletionLease_WaitingReadAfterFolderDeletion_DoesNotRecreateProject()
    {
        using var root = new TempProjectRoot();
        var (folder, projectDirectory) = CreateProject(root);
        var first = new ProjectReportArtifactStore(folder);
        var second = new ProjectReportArtifactStore(folder);
        var lease = await first.AcquireProjectDeletionLeaseAsync(ProjectId, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var blockedRead = second.ReadCatalogAsync(ProjectId, timeout.Token);
        await Task.Delay(100, CancellationToken.None);

        Directory.Delete(projectDirectory, recursive: true);
        await lease.DisposeAsync();
        var exception = await Assert.ThrowsAsync<JetActionException>(() => blockedRead);

        Assert.Equal(JetErrorCodes.ProjectNotFound, exception.Code);
        Assert.False(Directory.Exists(projectDirectory));
    }

    [Fact]
    public async Task ReadCatalog_UnknownTemporaryPrefixFile_FailsClosedAndPreservesFile()
    {
        using var root = new TempProjectRoot();
        var (folder, projectDirectory) = CreateProject(root);
        var unknown = Path.Combine(projectDirectory, ".report-artifact-stage-not-a-protocol-name.tmp");
        await File.WriteAllTextAsync(unknown, "user-file");

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            new ProjectReportArtifactStore(folder).ReadCatalogAsync(ProjectId, CancellationToken.None));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
        Assert.Equal("user-file", await File.ReadAllTextAsync(unknown));
    }

    [FileSystemLinksFact]
    public async Task Cleanup_CandidateReplacedBySymbolicLink_RejectsWithoutDeletingTarget()
    {
        using var root = new TempProjectRoot();
        var (folder, projectDirectory) = CreateProject(root);
        var clock = new MutableTimeProvider(BaseUtc);
        var store = new ProjectReportArtifactStore(folder, clock);
        await WriteVersionsAsync(store, projectDirectory, clock, count: 4);
        var catalog = await store.ReadCatalogAsync(ProjectId, CancellationToken.None);
        var candidate = Assert.Single(ReportArtifactRetentionPolicy.Plan(catalog.Artifacts));
        var artifactPath = Path.Combine(projectDirectory, candidate.Artifact.RelativeFileName);
        var outside = Path.Combine(root.Path, "outside-report.xlsx");
        File.Move(artifactPath, outside);
        File.CreateSymbolicLink(artifactPath, outside);

        var exception = await Assert.ThrowsAsync<JetActionException>(() => store.CleanupAsync(
            ProjectId,
            catalog.Revision,
            [candidate],
            "CONTOSO\\auditor",
            CancellationToken.None));

        Assert.Equal(JetErrorCodes.ArtifactCleanupFailed, exception.Code);
        Assert.True(File.Exists(outside));
        Assert.Equal(4, (await store.ListAsync(ProjectId, CancellationToken.None)).Count);
    }

    [FileSystemLinksFact]
    public async Task ReadCatalog_DanglingManifestLink_FailsClosed()
    {
        using var root = new TempProjectRoot();
        var (folder, projectDirectory) = CreateProject(root);
        File.CreateSymbolicLink(
            Path.Combine(projectDirectory, ProjectReportArtifactStore.ManifestFileName),
            Path.Combine(root.Path, "missing-manifest-target.json"));

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            new ProjectReportArtifactStore(folder).ReadCatalogAsync(ProjectId, CancellationToken.None));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
    }

    [FileSystemLinksFact]
    public async Task ReadCatalog_DanglingJournalLink_FailsClosed()
    {
        using var root = new TempProjectRoot();
        var (folder, projectDirectory) = CreateProject(root);
        File.CreateSymbolicLink(
            Path.Combine(projectDirectory, ProjectReportArtifactStore.JournalFileName),
            Path.Combine(root.Path, "missing-journal-target.json"));

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            new ProjectReportArtifactStore(folder).ReadCatalogAsync(ProjectId, CancellationToken.None));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
    }

    [FileSystemLinksFact]
    public async Task CleanupRecovery_DanglingQuarantineLink_FailsClosed()
    {
        using var root = new TempProjectRoot();
        var (folder, projectDirectory) = CreateProject(root);
        var clock = new MutableTimeProvider(BaseUtc);
        var seed = new ProjectReportArtifactStore(folder, clock);
        await WriteVersionsAsync(seed, projectDirectory, clock, count: 4);
        var catalog = await seed.ReadCatalogAsync(ProjectId, CancellationToken.None);
        var candidates = ReportArtifactRetentionPolicy.Plan(catalog.Artifacts);
        var crashing = CrashAt(folder, clock, ReportArtifactStoreCheckpoint.ManifestCommitted);
        await Assert.ThrowsAsync<ReportArtifactStoreSimulatedCrashException>(() => crashing.CleanupAsync(
            ProjectId,
            catalog.Revision,
            candidates,
            "CONTOSO\\auditor",
            CancellationToken.None));
        var quarantine = Assert.Single(Directory.GetFiles(projectDirectory, ".report-artifact-quarantine-*"));
        File.Delete(quarantine);
        File.CreateSymbolicLink(quarantine, Path.Combine(root.Path, "missing-quarantine.xlsx"));

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            new ProjectReportArtifactStore(folder, clock).ReadCatalogAsync(ProjectId, CancellationToken.None));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
        Assert.True(File.Exists(Path.Combine(projectDirectory, ProjectReportArtifactStore.JournalFileName)));
    }

    [FileSystemLinksFact]
    public async Task CleanupRecovery_DanglingAuditReceiptLink_FailsClosed()
    {
        using var root = new TempProjectRoot();
        var (folder, projectDirectory) = CreateProject(root);
        var clock = new MutableTimeProvider(BaseUtc);
        var seed = new ProjectReportArtifactStore(folder, clock);
        await WriteVersionsAsync(seed, projectDirectory, clock, count: 4);
        var catalog = await seed.ReadCatalogAsync(ProjectId, CancellationToken.None);
        var candidates = ReportArtifactRetentionPolicy.Plan(catalog.Artifacts);
        var crashing = CrashAt(folder, clock, ReportArtifactStoreCheckpoint.ManifestCommitted);
        await Assert.ThrowsAsync<ReportArtifactStoreSimulatedCrashException>(() => crashing.CleanupAsync(
            ProjectId,
            catalog.Revision,
            candidates,
            "CONTOSO\\auditor",
            CancellationToken.None));
        var journalPath = Path.Combine(projectDirectory, ProjectReportArtifactStore.JournalFileName);
        var journal = JsonNode.Parse(await File.ReadAllTextAsync(journalPath))!.AsObject();
        var auditId = journal["audit"]!["auditId"]!.GetValue<string>();
        var auditPath = Path.Combine(
            projectDirectory,
            ProjectReportArtifactStore.AuditDirectoryName,
            $"{auditId}.json");
        File.CreateSymbolicLink(auditPath, Path.Combine(root.Path, "missing-audit.json"));

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            new ProjectReportArtifactStore(folder, clock).ReadCatalogAsync(ProjectId, CancellationToken.None));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
        Assert.True(File.Exists(journalPath));
    }

    [Fact]
    public async Task Cleanup_AtManifestEntryLimit_RemainsAvailable()
    {
        using var root = new TempProjectRoot();
        var (folder, projectDirectory) = CreateProject(root);
        var entries = new List<object>(ProjectReportArtifactStore.MaxManifestEntries);
        var emptySha = Convert.ToHexString(SHA256.HashData([]));
        for (var index = 0; index < ProjectReportArtifactStore.MaxManifestEntries; index++)
        {
            var artifactId = (index + 1).ToString("x32");
            var fileName = $"{ProjectId}_20260711000000000_ValidationReport_{artifactId}.xlsx";
            await File.WriteAllBytesAsync(Path.Combine(projectDirectory, fileName), []);
            entries.Add(new
            {
                artifactId,
                kind = ReportArtifactKindValues.ValidationReport,
                relativeFileName = fileName,
                sourceRef = new { validationRunId = ValidationRun1 },
                generatedUtc = BaseUtc,
                bytes = 0,
                sha256 = emptySha,
                stale = true
            });
        }

        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory, ProjectReportArtifactStore.ManifestFileName),
            JsonSerializer.Serialize(
                entries,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        var store = new ProjectReportArtifactStore(folder, new MutableTimeProvider(BaseUtc));
        var catalog = await store.ReadCatalogAsync(ProjectId, CancellationToken.None);
        var candidates = ReportArtifactRetentionPolicy.Plan(catalog.Artifacts);

        var result = await store.CleanupAsync(
            ProjectId,
            catalog.Revision,
            candidates,
            "CONTOSO\\auditor",
            CancellationToken.None);

        Assert.Equal(ProjectReportArtifactStore.MaxManifestEntries, result.DeletedCount);
        Assert.Empty(result.Catalog.Artifacts);
        Assert.Empty(Directory.GetFiles(projectDirectory, "*.xlsx"));
    }

    private static (JetProjectFolder Folder, string ProjectDirectory) CreateProject(TempProjectRoot root)
    {
        var folder = new JetProjectFolder(root.Path);
        var projectDirectory = Path.GetFullPath(folder.GetProjectDirectory(ProjectId));
        Directory.CreateDirectory(projectDirectory);
        return (folder, projectDirectory);
    }

    private static ReportArtifactWriteRequest Request(string validationRunId, string content)
        => new(
            ReportArtifactKind.ValidationReport,
            new ReportArtifactSourceRefs(ValidationRunId: validationRunId),
            (output, cancellationToken) => output.WriteAsync(
                Encoding.UTF8.GetBytes(content),
                cancellationToken).AsTask());

    private static async Task<IReadOnlyList<ReportArtifact>> WriteVersionsAsync(
        ProjectReportArtifactStore store,
        string projectDirectory,
        MutableTimeProvider clock,
        int count)
    {
        var entries = new List<object>(count);
        for (var index = 0; index < count; index++)
        {
            var artifactId = (index + 1).ToString("x32", CultureInfo.InvariantCulture);
            var generatedUtc = clock.GetUtcNow().ToUniversalTime();
            var fileName = $"{ProjectId}_{generatedUtc:yyyyMMddHHmmssfff}_ValidationReport_{artifactId}.xlsx";
            var content = Encoding.UTF8.GetBytes($"version-{index}");
            await File.WriteAllBytesAsync(Path.Combine(projectDirectory, fileName), content);
            entries.Add(new
            {
                artifactId,
                kind = ReportArtifactKindValues.ValidationReport,
                relativeFileName = fileName,
                sourceRef = new { validationRunId = ValidationRun1 },
                generatedUtc,
                bytes = content.LongLength,
                sha256 = Convert.ToHexString(SHA256.HashData(content)),
                stale = false
            });
            clock.Advance();
        }

        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory, ProjectReportArtifactStore.ManifestFileName),
            JsonSerializer.Serialize(
                entries,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        return await store.ListAsync(ProjectId, CancellationToken.None);
    }

    private static ProjectReportArtifactStore CrashAt(
        JetProjectFolder folder,
        TimeProvider clock,
        ReportArtifactStoreCheckpoint crashAt)
        => new(
            folder,
            clock,
            new ReportArtifactStoreTestHooks
            {
                OnCheckpoint = checkpoint =>
                {
                    if (checkpoint == crashAt)
                    {
                        throw new ReportArtifactStoreSimulatedCrashException("simulated process death");
                    }
                }
            });

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance() => _utcNow = _utcNow.AddSeconds(1);
    }
}
