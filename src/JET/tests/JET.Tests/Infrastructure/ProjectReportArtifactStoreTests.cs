using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.TestInfrastructure;
using Xunit;
using Xunit.v3;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 專案內報告 artifact store 的真實檔案系統測試。
/// oracle：使用者裁決的本地資料邊界、固定 manifest 欄位與 temp→正式檔批次發布規則。
/// </summary>
public sealed class ProjectReportArtifactStoreTests
{
    private static readonly DateTimeOffset FixedUtc =
        new(2026, 7, 10, 1, 2, 3, 456, TimeSpan.Zero);

    [Fact]
    public async Task WriteAsync_ValidReport_PersistsContentAndHashInsideProjectDirectory()
    {
        using var root = new TempProjectRoot();
        var (store, projectDirectory) = CreateStore(root);
        var content = Encoding.UTF8.GetBytes("local-only-report");

        var artifact = await store.WriteAsync(
            ProjectId,
            Request(ReportArtifactKind.ValidationReport, ValidationRefs(), content),
            CancellationToken.None);

        var expectedPath = Path.Combine(projectDirectory, artifact.RelativeFileName);
        Assert.Equal(content, await File.ReadAllBytesAsync(expectedPath, CancellationToken.None));
        Assert.Equal(content.LongLength, artifact.Bytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)), artifact.Sha256);
        Assert.Equal(Path.GetFileName(expectedPath), artifact.RelativeFileName);
    }

    [Fact]
    public async Task PublishingBoundary_SeesClosedStageBeforeJournalOrFinalArtifact()
    {
        using var root = new TempProjectRoot();
        var (store, projectDirectory) = CreateStore(root);
        var content = Encoding.UTF8.GetBytes("publishing-boundary");
        var observed = false;

        var artifact = await ((IReportArtifactPublishingStore)store).WriteWithPublishingAsync(
            ProjectId,
            Request(ReportArtifactKind.ValidationReport, ValidationRefs(), content),
            kind =>
            {
                observed = true;
                Assert.Equal(ReportArtifactKind.ValidationReport, kind);
                Assert.False(File.Exists(Path.Combine(
                    projectDirectory,
                    ProjectReportArtifactStore.ManifestFileName)));
                Assert.False(File.Exists(Path.Combine(
                    projectDirectory,
                    ProjectReportArtifactStore.JournalFileName)));
                Assert.Empty(Directory.GetFiles(projectDirectory, "*.xlsx"));

                var stagePath = Assert.Single(Directory.GetFiles(
                    projectDirectory,
                    ".report-artifact-stage-*.tmp"));
                using var stage = File.Open(
                    stagePath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None);
                var stagedContent = new byte[content.Length];
                stage.ReadExactly(stagedContent);
                Assert.Equal(content, stagedContent);
            },
            CancellationToken.None);

        Assert.True(observed);
        Assert.True(File.Exists(Path.Combine(projectDirectory, artifact.RelativeFileName)));
    }

    [Fact]
    public async Task PublishingBoundary_CancellationStopsRemainingBatchAndLeavesNoArtifact()
    {
        using var root = new TempProjectRoot();
        var (store, projectDirectory) = CreateStore(root);
        using var cancellation = new CancellationTokenSource();
        var observed = new List<ReportArtifactKind>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ((IReportArtifactPublishingStore)store).WriteBatchWithPublishingAsync(
                ProjectId,
                [
                    Request(ReportArtifactKind.ValidationReport, ValidationRefs(), [1]),
                    Request(ReportArtifactKind.AccountMapping, ValidationRefs(), [2])
                ],
                kind =>
                {
                    observed.Add(kind);
                    Assert.Equal(2, Directory.GetFiles(
                        projectDirectory,
                        ".report-artifact-stage-*.tmp").Length);
                    Assert.Empty(Directory.GetFiles(projectDirectory, "*.xlsx"));
                    Assert.False(File.Exists(Path.Combine(
                        projectDirectory,
                        ProjectReportArtifactStore.JournalFileName)));
                    cancellation.Cancel();
                },
                cancellation.Token));

        Assert.Equal([ReportArtifactKind.ValidationReport], observed);
        Assert.Empty(Directory.GetFiles(projectDirectory, "*.xlsx"));
        Assert.Empty(Directory.GetFiles(projectDirectory, "*.tmp"));
        Assert.False(File.Exists(Path.Combine(
            projectDirectory,
            ProjectReportArtifactStore.ManifestFileName)));
        Assert.False(File.Exists(Path.Combine(
            projectDirectory,
            ProjectReportArtifactStore.JournalFileName)));
    }

    [Fact]
    public async Task WriteAsync_ValidReport_GeneratesBackendOwnedFileName()
    {
        using var root = new TempProjectRoot();
        var (store, _) = CreateStore(root);

        var artifact = await store.WriteAsync(
            ProjectId,
            Request(ReportArtifactKind.InfReport, ValidationRefs(), [1, 2, 3]),
            CancellationToken.None);

        Assert.Equal(
            $"{ProjectId}_INFReport.xlsx",
            artifact.RelativeFileName);
        Assert.DoesNotContain(artifact.ArtifactId, artifact.RelativeFileName, StringComparison.Ordinal);
        Assert.DoesNotContain("20260710010203456", artifact.RelativeFileName, StringComparison.Ordinal);
        Assert.DoesNotContain('/', artifact.RelativeFileName);
        Assert.DoesNotContain('\\', artifact.RelativeFileName);
    }

    [Fact]
    public async Task WriteAsync_SameKindAgain_AtomicallyReplacesCatalogAndStableFile()
    {
        using var root = new TempProjectRoot();
        var (store, projectDirectory) = CreateStore(root);
        var first = await store.WriteAsync(
            ProjectId,
            Request(ReportArtifactKind.WorkingPaper, ScenarioRefs("revision-1"), [1, 2, 3]),
            CancellationToken.None);

        var second = await store.WriteAsync(
            ProjectId,
            Request(ReportArtifactKind.WorkingPaper, ScenarioRefs("revision-2"), [4, 5]),
            CancellationToken.None);

        Assert.Equal($"{ProjectId}_WorkingPaper.xlsx", first.RelativeFileName);
        Assert.Equal(first.RelativeFileName, second.RelativeFileName);
        Assert.NotEqual(first.ArtifactId, second.ArtifactId);
        Assert.Equal([4, 5], await File.ReadAllBytesAsync(
            Path.Combine(projectDirectory, second.RelativeFileName),
            CancellationToken.None));
        Assert.Equal(
            second.RelativeFileName,
            Path.GetFileName(Assert.Single(Directory.GetFiles(projectDirectory, "*.xlsx"))));

        var current = Assert.Single(await store.ListAsync(ProjectId, CancellationToken.None));
        Assert.Equal(second.ArtifactId, current.ArtifactId);
        Assert.Equal("revision-2", current.SourceRef.ScenarioRevision);
        Assert.False(current.Stale);
    }

    [Fact]
    public async Task WriteBatchAsync_DuplicateKinds_RejectsBeforeWriting()
    {
        using var root = new TempProjectRoot();
        var (store, projectDirectory) = CreateStore(root);

        var exception = await Assert.ThrowsAsync<JetActionException>(() => store.WriteBatchAsync(
            ProjectId,
            [
                Request(ReportArtifactKind.ValidationReport, ValidationRefs(), [1]),
                Request(ReportArtifactKind.ValidationReport, ValidationRefs(), [2])
            ],
            CancellationToken.None));

        Assert.Equal(JetErrorCodes.InvalidPayload, exception.Code);
        Assert.Empty(Directory.GetFiles(projectDirectory));
    }

    [Fact]
    public async Task WriteAsync_NewCriteriaMissingValidationRun_RejectsInvalidPayload()
    {
        using var root = new TempProjectRoot();
        var (store, _) = CreateStore(root);

        var exception = await Assert.ThrowsAsync<JetActionException>(() => store.WriteAsync(
            ProjectId,
            Request(
                ReportArtifactKind.CriteriaSelectionReport,
                new ReportArtifactSourceRefs(
                    ScenarioRevision: "revision-1",
                    ScenarioPositions: [1]),
                [1]),
            CancellationToken.None));

        Assert.Equal(JetErrorCodes.InvalidPayload, exception.Code);
    }

    [Theory]
    [InlineData(ReportArtifactKind.CriteriaSelectionReport)]
    [InlineData(ReportArtifactKind.WorkingPaper)]
    public async Task WriteAsync_NewScenarioReportWithoutPrescreenRun_Persists(
        ReportArtifactKind kind)
    {
        using var root = new TempProjectRoot();
        var (store, _) = CreateStore(root);

        var artifact = await store.WriteAsync(
            ProjectId,
            Request(
                kind,
                new ReportArtifactSourceRefs(
                    ValidationRunId: ValidationRunId,
                    ScenarioRevision: "revision-1",
                    ScenarioPositions: [1]),
                [1]),
            CancellationToken.None);

        Assert.Null(artifact.SourceRef.PrescreenRunId);
    }

    [Fact]
    public async Task WriteAsync_ManifestEntry_ContainsOnlyAllowlistedFields()
    {
        using var root = new TempProjectRoot();
        var (store, projectDirectory) = CreateStore(root);
        var refs = new ReportArtifactSourceRefs(
            ValidationRunId: ValidationRunId,
            ScenarioRevision: "2026-07-10T01:02:03.4560000Z",
            ScenarioPositions: [1, 3]);

        await store.WriteAsync(
            ProjectId,
            Request(ReportArtifactKind.WorkingPaper, refs, [4, 5, 6]),
            CancellationToken.None);

        await using var manifest = File.OpenRead(
            Path.Combine(projectDirectory, ProjectReportArtifactStore.ManifestFileName));
        using var document = await JsonDocument.ParseAsync(
            manifest,
            cancellationToken: CancellationToken.None);
        var entry = document.RootElement[0];
        var fieldNames = entry.EnumerateObject().Select(property => property.Name).Order().ToArray();

        Assert.Equal(
            new[] { "artifactId", "bytes", "generatedUtc", "kind", "relativeFileName", "sha256", "sourceRef", "stale" },
            fieldNames);
        Assert.Equal("workingPaper", entry.GetProperty("kind").GetString());
        Assert.Equal(
            new[] { "scenarioPositions", "scenarioRevision", "validationRunId" },
            entry.GetProperty("sourceRef").EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.False(entry.GetProperty("sourceRef").TryGetProperty("prescreenRunId", out _));
    }

    [Theory]
    [InlineData("infReport", "INF", "_", "Report", "validationRunId")]
    [InlineData("prescreenReport", "Pre", "-", "screeningReport", "prescreenRunId")]
    public async Task ListAsync_PreRenameArtifactFileName_RemainsReadable(
        string kind,
        string prefix,
        string separator,
        string suffix,
        string sourceRefProperty)
    {
        using var root = new TempProjectRoot();
        var (store, projectDirectory) = CreateStore(root);
        var artifactId = "23232323232323232323232323232323";
        var fileName = string.Concat(ProjectId, "_", prefix, separator, suffix, ".xlsx");
        var json = $$"""
            [
              {
                "artifactId": "{{artifactId}}",
                "kind": "{{kind}}",
                "relativeFileName": "{{fileName}}",
                "sourceRef": { "{{sourceRefProperty}}": "{{ValidationRunId}}" },
                "generatedUtc": "2026-07-10T01:02:03.456Z",
                "bytes": 7,
                "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
                "stale": false
              }
            ]
            """;
        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory, ProjectReportArtifactStore.ManifestFileName),
            json,
            CancellationToken.None);

        var artifact = Assert.Single(await store.ListAsync(ProjectId, CancellationToken.None));

        Assert.Equal(fileName, artifact.RelativeFileName);
        Assert.Equal(kind, ReportArtifactKindValues.ToValue(artifact.Kind));
    }

    [Theory]
    [InlineData("infReport", "Pre-screeningReport", "validationRunId")]
    [InlineData("infReport", "INF-Report", "validationRunId")]
    [InlineData("prescreenReport", "Pre_screeningReport", "prescreenRunId")]
    public async Task ListAsync_PreRenameArtifactFileName_NearMissOrWrongKind_Rejects(
        string kind,
        string suffix,
        string sourceRefProperty)
    {
        using var root = new TempProjectRoot();
        var (store, projectDirectory) = CreateStore(root);
        var fileName = $"{ProjectId}_{suffix}.xlsx";
        var json = $$"""
            [
              {
                "artifactId": "23232323232323232323232323232323",
                "kind": "{{kind}}",
                "relativeFileName": "{{fileName}}",
                "sourceRef": { "{{sourceRefProperty}}": "{{ValidationRunId}}" },
                "generatedUtc": "2026-07-10T01:02:03.456Z",
                "bytes": 7,
                "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
                "stale": false
              }
            ]
            """;
        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory, ProjectReportArtifactStore.ManifestFileName),
            json,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            store.ListAsync(ProjectId, CancellationToken.None));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
    }

    [Fact]
    public async Task ListAsync_LegacyCriteriaMissingRequiredRunTuple_ReadsAsStaleAndMigratesOnNextRewrite()
    {
        using var root = new TempProjectRoot();
        var (store, projectDirectory) = CreateStore(root);
        var artifactId = "33333333333333333333333333333333";
        var fileName = $"{ProjectId}_20260710010203456_CriteriaSelectionReport_{artifactId}.xlsx";
        var json = $$"""
            [
              {
                "artifactId": "{{artifactId}}",
                "kind": "criteriaSelectionReport",
                "relativeFileName": "{{fileName}}",
                "sourceRefs": { "scenarioRevision": "revision-legacy" },
                "createdUtc": "2026-07-10T01:02:03.456Z",
                "bytes": 7,
                "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
                "stale": false
              }
            ]
            """;
        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory, ProjectReportArtifactStore.ManifestFileName),
            json,
            CancellationToken.None);

        var artifact = Assert.Single(await store.ListAsync(ProjectId, CancellationToken.None));

        Assert.Equal(artifactId, artifact.ArtifactId);
        Assert.Equal(FixedUtc, artifact.GeneratedUtc);
        Assert.Equal("revision-legacy", artifact.SourceRef.ScenarioRevision);
        Assert.Null(artifact.SourceRef.ValidationRunId);
        Assert.Null(artifact.SourceRef.PrescreenRunId);
        Assert.Equal(fileName, artifact.RelativeFileName);
        Assert.True(artifact.Stale);

        await store.WriteAsync(
            ProjectId,
            Request(ReportArtifactKind.ValidationReport, ValidationRefs(), [1]),
            CancellationToken.None);
        using var migrated = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(projectDirectory, ProjectReportArtifactStore.ManifestFileName),
            CancellationToken.None));
        var migratedEntry = migrated.RootElement.EnumerateArray()
            .Single(entry => entry.GetProperty("artifactId").GetString() == artifactId);
        Assert.True(migratedEntry.TryGetProperty("sourceRef", out _));
        Assert.True(migratedEntry.TryGetProperty("generatedUtc", out _));
        Assert.False(migratedEntry.TryGetProperty("sourceRefs", out _));
        Assert.False(migratedEntry.TryGetProperty("createdUtc", out _));
    }

    [Fact]
    public async Task ListAsync_ManifestExceedsByteLimit_RejectsBeforeDeserialization()
    {
        using var root = new TempProjectRoot();
        var (store, projectDirectory) = CreateStore(root);
        await File.WriteAllBytesAsync(
            Path.Combine(projectDirectory, ProjectReportArtifactStore.ManifestFileName),
            new byte[checked((int)ProjectReportArtifactStore.MaxManifestBytes) + 1],
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<JetActionException>(() => store.ListAsync(
            ProjectId,
            CancellationToken.None));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
    }

    [Fact]
    public async Task ListAsync_ManifestExceedsEntryLimit_RejectsBeforeValidatingEntries()
    {
        using var root = new TempProjectRoot();
        var (store, projectDirectory) = CreateStore(root);
        var json = "[" + string.Join(
            ',',
            Enumerable.Repeat("{}", ProjectReportArtifactStore.MaxManifestEntries + 1)) + "]";
        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory, ProjectReportArtifactStore.ManifestFileName),
            json,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<JetActionException>(() => store.ListAsync(
            ProjectId,
            CancellationToken.None));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
    }

    [Fact]
    public async Task WriteBatchAsync_AllWritersSucceed_PublishesWholeBatch()
    {
        using var root = new TempProjectRoot();
        var (store, projectDirectory) = CreateStore(root);
        var requests = new[]
        {
            Request(ReportArtifactKind.ValidationReport, ValidationRefs(), [1]),
            Request(ReportArtifactKind.AccountMapping, ValidationRefs(), [2]),
            Request(ReportArtifactKind.InfReport, ValidationRefs(), [3])
        };

        var artifacts = await store.WriteBatchAsync(
            ProjectId,
            requests,
            CancellationToken.None);

        Assert.Equal(3, artifacts.Count);
        Assert.Equal(3, Directory.GetFiles(projectDirectory, "*.xlsx").Length);
        Assert.Equal(3, (await store.ListAsync(ProjectId, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task WriteBatchAsync_LaterWriterFails_RemovesWholeBatchAndTemporaryFiles()
    {
        using var root = new TempProjectRoot();
        var (store, projectDirectory) = CreateStore(root);
        var requests = new[]
        {
            Request(ReportArtifactKind.ValidationReport, ValidationRefs(), [1]),
            new ReportArtifactWriteRequest(
                ReportArtifactKind.AccountMapping,
                ValidationRefs(),
                (_, _) => throw new InvalidOperationException("fixture failure"))
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteBatchAsync(
            ProjectId,
            requests,
            CancellationToken.None));

        Assert.Empty(Directory.GetFiles(projectDirectory, "*.xlsx"));
        Assert.Empty(Directory.GetFiles(projectDirectory, "*.tmp"));
        Assert.False(File.Exists(Path.Combine(projectDirectory, ProjectReportArtifactStore.ManifestFileName)));
    }

    [Fact]
    public async Task WriteBatchAsync_CancelledDuringStaging_RemovesWholeBatchAndTemporaryFiles()
    {
        using var root = new TempProjectRoot();
        var (store, projectDirectory) = CreateStore(root);
        using var cancellation = new CancellationTokenSource();
        var requests = new[]
        {
            Request(ReportArtifactKind.ValidationReport, ValidationRefs(), [1]),
            new ReportArtifactWriteRequest(
                ReportArtifactKind.AccountMapping,
                ValidationRefs(),
                (_, token) =>
                {
                    cancellation.Cancel();
                    return Task.FromCanceled(token);
                })
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteBatchAsync(
            ProjectId,
            requests,
            cancellation.Token));

        Assert.Empty(Directory.GetFiles(projectDirectory, "*.xlsx"));
        Assert.Empty(Directory.GetFiles(projectDirectory, "*.tmp"));
        Assert.False(File.Exists(Path.Combine(projectDirectory, ProjectReportArtifactStore.ManifestFileName)));
    }

    [Fact]
    public async Task WriteBatchAsync_ManifestPublishFails_RollsBackPublishedFiles()
    {
        using var root = new TempProjectRoot();
        var (store, projectDirectory) = CreateStore(root);
        Directory.CreateDirectory(Path.Combine(projectDirectory, ProjectReportArtifactStore.ManifestFileName));

        await Assert.ThrowsAsync<IOException>(() => store.WriteAsync(
            ProjectId,
            Request(ReportArtifactKind.ValidationReport, ValidationRefs(), [1]),
            CancellationToken.None));

        Assert.Empty(Directory.GetFiles(projectDirectory, "*.xlsx"));
        Assert.Empty(Directory.GetFiles(projectDirectory, "*.tmp"));
    }

    [Fact]
    public async Task WriteBatchAsync_NewBatchFails_PreservesPreviouslyCommittedArtifact()
    {
        using var root = new TempProjectRoot();
        var (store, projectDirectory) = CreateStore(root);
        var committed = await store.WriteAsync(
            ProjectId,
            Request(ReportArtifactKind.ValidationReport, ValidationRefs(), [1]),
            CancellationToken.None);
        var failing = new ReportArtifactWriteRequest(
            ReportArtifactKind.PrescreenReport,
            new ReportArtifactSourceRefs(PrescreenRunId: PrescreenRunId),
            (_, _) => throw new InvalidOperationException("fixture failure"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(
            ProjectId,
            failing,
            CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(projectDirectory, committed.RelativeFileName)));
        Assert.Equal(committed.ArtifactId, Assert.Single(
            await store.ListAsync(ProjectId, CancellationToken.None)).ArtifactId);
    }

    [Fact]
    public async Task AccountMappingDirectWriter_CancelDuringFirstSheet_PreservesPriorArtifactAndManifest()
    {
        using var root = new TempProjectRoot();
        var (store, projectDirectory) = CreateStore(root);
        var writer = new AccountMappingTemplateWriter();
        var initialRows = new AccountMappingTemplateRow[]
        {
            new("1101", "Cash"),
            new("4100", "Revenue")
        };
        var committed = await store.WriteAsync(
            ProjectId,
            new ReportArtifactWriteRequest(
                ReportArtifactKind.AccountMapping,
                ValidationRefs(),
                (output, cancellationToken) =>
                    writer.WriteAsync(output, initialRows, cancellationToken)),
            CancellationToken.None);
        var artifactPath = Path.Combine(projectDirectory, committed.RelativeFileName);
        var manifestPath = Path.Combine(
            projectDirectory,
            ProjectReportArtifactStore.ManifestFileName);
        var committedBytes = await File.ReadAllBytesAsync(artifactPath);
        var committedManifest = await File.ReadAllBytesAsync(manifestPath);

        using var cancellation = new CancellationTokenSource();
        var progress = new List<WorkpaperProgress>();
        var progressWriter = Assert.IsAssignableFrom<IAccountMappingTemplateProgressWriter>(writer);
        var replacementRows = new CancelingAccountMappingRows(cancellation);
        var replacement = new ReportArtifactWriteRequest(
            ReportArtifactKind.AccountMapping,
            ValidationRefs(),
            (output, cancellationToken) => progressWriter.WriteAsync(
                output,
                replacementRows,
                cancellationToken,
                snapshot =>
                {
                    progress.Add(snapshot);
                }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.WriteAsync(ProjectId, replacement, cancellation.Token));

        Assert.Empty(progress);
        Assert.Equal(committedBytes, await File.ReadAllBytesAsync(artifactPath));
        Assert.Equal(committedManifest, await File.ReadAllBytesAsync(manifestPath));
        var current = Assert.Single(await store.ListAsync(ProjectId, CancellationToken.None));
        Assert.Equal(committed.ArtifactId, current.ArtifactId);
        Assert.Equal(committed.Sha256, current.Sha256);
        Assert.Empty(Directory.GetFiles(projectDirectory, ".report-artifact-stage-*.tmp"));
        Assert.False(File.Exists(Path.Combine(
            projectDirectory,
            ProjectReportArtifactStore.JournalFileName)));
    }

    [Fact]
    public async Task ResolvePathAsync_KnownArtifact_ReturnsAbsoluteProjectLocalPath()
    {
        using var root = new TempProjectRoot();
        var (store, projectDirectory) = CreateStore(root);
        var artifact = await store.WriteAsync(
            ProjectId,
            Request(ReportArtifactKind.CriteriaSelectionReport, ScenarioRefs(), [1]),
            CancellationToken.None);

        var resolved = await store.ResolvePathAsync(
            ProjectId,
            artifact.ArtifactId,
            CancellationToken.None);

        Assert.Equal(Path.Combine(projectDirectory, artifact.RelativeFileName), resolved);
        Assert.True(Path.IsPathFullyQualified(resolved));
    }

    [Fact]
    public async Task ResolvePathAsync_TraversalLikeArtifactId_RejectsInvalidPayload()
    {
        using var root = new TempProjectRoot();
        var (store, _) = CreateStore(root);

        var exception = await Assert.ThrowsAsync<JetActionException>(() => store.ResolvePathAsync(
            ProjectId,
            @"..\..\outside.xlsx",
            CancellationToken.None));

        Assert.Equal(JetErrorCodes.InvalidPayload, exception.Code);
    }

    [FileSystemLinksFact]
    public async Task WriteAsync_ReparsePointInProjectAncestor_RejectsBeforeCreatingArtifact()
    {
        using var root = new TempProjectRoot();
        var actualRoot = Path.Combine(root.Path, "actual-root");
        var linkedRoot = Path.Combine(root.Path, "linked-root");
        Directory.CreateDirectory(Path.Combine(actualRoot, ProjectId));
        CreateDirectoryLink(linkedRoot, actualRoot);
        var store = new ProjectReportArtifactStore(
            new JetProjectFolder(linkedRoot),
            new FixedTimeProvider(FixedUtc));

        var exception = await Assert.ThrowsAsync<JetActionException>(() => store.WriteAsync(
            ProjectId,
            Request(ReportArtifactKind.ValidationReport, ValidationRefs(), [1]),
            CancellationToken.None));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
        Assert.Empty(Directory.GetFiles(Path.Combine(actualRoot, ProjectId), "*.xlsx"));
    }

    [FileSystemLinksFact]
    public async Task ResolvePathAsync_ArtifactFileIsSymbolicLink_RejectsLinkedTarget()
    {
        using var root = new TempProjectRoot();
        var (store, projectDirectory) = CreateStore(root);
        var artifact = await store.WriteAsync(
            ProjectId,
            Request(ReportArtifactKind.ValidationReport, ValidationRefs(), [1]),
            CancellationToken.None);
        var artifactPath = Path.Combine(projectDirectory, artifact.RelativeFileName);
        var linkedTarget = Path.Combine(root.Path, "outside-report.xlsx");
        File.Move(artifactPath, linkedTarget);
        CreateFileLink(artifactPath, linkedTarget);

        var exception = await Assert.ThrowsAsync<JetActionException>(() => store.ResolvePathAsync(
            ProjectId,
            artifact.ArtifactId,
            CancellationToken.None));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
    }

    [Fact]
    public async Task ListAsync_TamperedTraversalFileName_RejectsManifest()
    {
        using var root = new TempProjectRoot();
        var (store, projectDirectory) = CreateStore(root);
        var json = $$"""
            [
              {
                "artifactId": "{{ValidationRunId}}",
                "kind": "validationReport",
                "relativeFileName": "..\\outside.xlsx",
                "sourceRefs": { "validationRunId": "{{ValidationRunId}}" },
                "createdUtc": "2026-07-10T01:02:03.456Z",
                "bytes": 0,
                "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
                "stale": false
              }
            ]
            """;
        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory, ProjectReportArtifactStore.ManifestFileName),
            json,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<JetActionException>(() => store.ListAsync(
            ProjectId,
            CancellationToken.None));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
    }

    [Fact]
    public async Task ListAsync_TamperedRootedFileName_RejectsManifest()
    {
        using var root = new TempProjectRoot();
        var (store, projectDirectory) = CreateStore(root);
        var json = $$"""
            [
              {
                "artifactId": "{{ValidationRunId}}",
                "kind": "validationReport",
                "relativeFileName": "C:\\legacy-machine\\outside.xlsx",
                "sourceRefs": { "validationRunId": "{{ValidationRunId}}" },
                "createdUtc": "2026-07-10T01:02:03.456Z",
                "bytes": 0,
                "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
                "stale": false
              }
            ]
            """;
        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory, ProjectReportArtifactStore.ManifestFileName),
            json,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<JetActionException>(() => store.ListAsync(
            ProjectId,
            CancellationToken.None));

        Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
    }

    [Fact]
    public async Task MarkStaleAsync_Kind_MarksOnlyMatchingArtifacts()
    {
        using var root = new TempProjectRoot();
        var (store, _) = CreateStore(root);
        await store.WriteBatchAsync(
            ProjectId,
            [
                Request(ReportArtifactKind.ValidationReport, ValidationRefs(), [1]),
                Request(ReportArtifactKind.PrescreenReport, new ReportArtifactSourceRefs(PrescreenRunId: PrescreenRunId), [2])
            ],
            CancellationToken.None);

        var changed = await store.MarkStaleAsync(
            ProjectId,
            ReportArtifactKind.ValidationReport,
            CancellationToken.None);
        var artifacts = await store.ListAsync(ProjectId, CancellationToken.None);

        Assert.Equal(1, changed);
        Assert.True(artifacts.Single(artifact => artifact.Kind == ReportArtifactKind.ValidationReport).Stale);
        Assert.False(artifacts.Single(artifact => artifact.Kind == ReportArtifactKind.PrescreenReport).Stale);
    }

    [Fact]
    public async Task MarkStaleAsync_SourcePredicate_MarksOnlyOlderRevision()
    {
        using var root = new TempProjectRoot();
        var (store, _) = CreateStore(root);
        await store.WriteBatchAsync(
            ProjectId,
            [
                Request(ReportArtifactKind.CriteriaSelectionReport, ScenarioRefs("revision-1"), [1]),
                Request(ReportArtifactKind.WorkingPaper, ScenarioRefs("revision-2"), [2])
            ],
            CancellationToken.None);

        var changed = await store.MarkStaleAsync(
            ProjectId,
            artifact => artifact.SourceRef.ScenarioRevision == "revision-1",
            CancellationToken.None);
        var artifacts = await new ProjectReportArtifactStore(
                new JetProjectFolder(root.Path),
                new FixedTimeProvider(FixedUtc))
            .ListAsync(ProjectId, CancellationToken.None);

        Assert.Equal(1, changed);
        Assert.True(artifacts.Single(artifact => artifact.SourceRef.ScenarioRevision == "revision-1").Stale);
        Assert.False(artifacts.Single(artifact => artifact.SourceRef.ScenarioRevision == "revision-2").Stale);
    }

    [Fact]
    public async Task WriteAsync_MissingProject_DoesNotCreateProjectDirectory()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var store = new ProjectReportArtifactStore(folder, new FixedTimeProvider(FixedUtc));

        var exception = await Assert.ThrowsAsync<JetActionException>(() => store.WriteAsync(
            ProjectId,
            Request(ReportArtifactKind.ValidationReport, ValidationRefs(), [1]),
            CancellationToken.None));

        Assert.Equal(JetErrorCodes.ProjectNotFound, exception.Code);
        Assert.False(Directory.Exists(folder.GetProjectDirectory(ProjectId)));
    }

    private const string ProjectId = "報告產物測試案件";
    private const string ValidationRunId = "11111111111111111111111111111111";
    private const string PrescreenRunId = "22222222222222222222222222222222";

    private static (ProjectReportArtifactStore Store, string ProjectDirectory) CreateStore(TempProjectRoot root)
    {
        var folder = new JetProjectFolder(root.Path);
        var projectDirectory = Path.GetFullPath(folder.GetProjectDirectory(ProjectId));
        Directory.CreateDirectory(projectDirectory);
        return (new ProjectReportArtifactStore(folder, new FixedTimeProvider(FixedUtc)), projectDirectory);
    }

    private static ReportArtifactWriteRequest Request(
        ReportArtifactKind kind,
        ReportArtifactSourceRefs sourceRefs,
        byte[] content)
        => new(
            kind,
            sourceRefs,
            (output, cancellationToken) => output.WriteAsync(content, cancellationToken).AsTask());

    private static ReportArtifactSourceRefs ValidationRefs()
        => new(ValidationRunId: ValidationRunId);

    private static ReportArtifactSourceRefs ScenarioRefs(string revision = "revision-1")
        => new(
            ValidationRunId: ValidationRunId,
            ScenarioRevision: revision,
            ScenarioPositions: [1, 2]);

    private static void CreateDirectoryLink(string linkPath, string targetPath)
        => Directory.CreateSymbolicLink(linkPath, targetPath);

    private static void CreateFileLink(string linkPath, string targetPath)
        => File.CreateSymbolicLink(linkPath, targetPath);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class CancelingAccountMappingRows(CancellationTokenSource cancellation)
        : IReadOnlyList<AccountMappingTemplateRow>
    {
        private readonly AccountMappingTemplateRow[] _rows =
        [
            new("1101", "Cash changed"),
            new("2160", "Deposits"),
            new("4100", "Revenue changed")
        ];
        private int _accesses;

        public int Count => _rows.Length;

        public AccountMappingTemplateRow this[int index]
        {
            get
            {
                if (Interlocked.Increment(ref _accesses) == 3)
                {
                    cancellation.Cancel();
                }
                return _rows[index];
            }
        }

        public IEnumerator<AccountMappingTemplateRow> GetEnumerator()
        {
            for (var index = 0; index < Count; index++)
            {
                yield return this[index];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

/// <summary>只有測試主機能建立真實 filesystem link 時才執行連結攻擊測試。</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class FileSystemLinksFactAttribute : FactAttribute, ITraitAttribute
{
    private static readonly Lazy<bool> Available = new(Probe);

    public const string SkipReason = "目前測試主機無法建立真實 filesystem link。";

    public static bool IsAvailable => Available.Value;

    public FileSystemLinksFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = SkipReason;
        SkipType = typeof(FileSystemLinksFactAttribute);
        SkipUnless = nameof(IsAvailable);
    }

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() =>
        TestProfileTraits.FileSystemLinks;

    private static bool Probe()
    {
        var probeRoot = Path.Combine(Path.GetTempPath(), $"jet-link-probe-{Guid.NewGuid():N}");
        var directoryTarget = Path.Combine(probeRoot, "directory-target");
        var directoryLink = Path.Combine(probeRoot, "directory-link");
        var fileTarget = Path.Combine(probeRoot, "file-target.tmp");
        var fileLink = Path.Combine(probeRoot, "file-link.tmp");

        try
        {
            Directory.CreateDirectory(directoryTarget);
            Directory.CreateSymbolicLink(directoryLink, directoryTarget);
            File.WriteAllBytes(fileTarget, [1]);
            File.CreateSymbolicLink(fileLink, fileTarget);
            return true;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
                or PlatformNotSupportedException
                or IOException)
        {
            return false;
        }
        finally
        {
            DeleteProbeFile(fileLink);
            DeleteProbeDirectory(directoryLink);
            DeleteProbeDirectory(probeRoot, recursive: true);
        }
    }

    private static void DeleteProbeFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 探測清理由 OS best effort 處理，不改變此測試是否可執行的判斷。
        }
    }

    private static void DeleteProbeDirectory(string path, bool recursive = false)
    {
        try
        {
            Directory.Delete(path, recursive);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // 同上；測試本體另用自己的 temp root，不依賴 probe 殘留。
        }
    }
}
