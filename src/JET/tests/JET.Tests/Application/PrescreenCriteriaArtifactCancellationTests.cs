using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class PrescreenCriteriaArtifactCancellationTests
{
    private const string InitialValidationRunId =
        "11111111111111111111111111111111";
    private const string InitialPrescreenRunId =
        "22222222222222222222222222222222";
    private const string ReplacementValidationRunId =
        "33333333333333333333333333333333";
    private const string ReplacementPrescreenRunId =
        "44444444444444444444444444444444";

    [Fact]
    public async Task PrescreenDirectWriter_CancelDuringFirstDetailPage_PreservesPriorArtifactAndManifest()
    {
        using var host = new HandlerTestHost();
        var projectId = await ReportArtifactExportFixture.SetupProjectAsync(
            host);
        var folder = new JetProjectFolder(host.ProjectsRoot);
        var projectDirectory = Path.GetFullPath(
            folder.GetProjectDirectory(projectId));
        var store = new ProjectReportArtifactStore(folder);
        var database = new SqliteProjectDatabase(folder);
        var projection = PrescreenProjection(rowCount: 2);
        var plan = PrescreenPlan(projectId, projection, rowCount: 2);
        var context = PrescreenContext(projectId);
        var committed = await store.WriteAsync(
            projectId,
            new ReportArtifactWriteRequest(
                ReportArtifactKind.PrescreenReport,
                new ReportArtifactSourceRefs(
                    PrescreenRunId: InitialPrescreenRunId),
                async (output, cancellationToken) =>
                {
                    _ = await ((IPlannedPrescreenReportWriter)CreateWriter(
                            database,
                            new LocalPrescreenPageRepository(database)))
                        .WritePlannedAsync(
                            output,
                            context,
                            projection,
                            plan,
                            "tester",
                            cancellationToken);
                }),
            CancellationToken.None);
        var snapshot = await CaptureCommittedSnapshotAsync(
            projectDirectory,
            committed);

        using var cancellation = new CancellationTokenSource();
        var cancelingPages = new CancelAfterFirstPrescreenPageRepository(
            new LocalPrescreenPageRepository(database),
            cancellation);
        var replacementWriter = CreateWriter(database, cancelingPages);
        var replacement = new ReportArtifactWriteRequest(
            ReportArtifactKind.PrescreenReport,
            new ReportArtifactSourceRefs(
                PrescreenRunId: ReplacementPrescreenRunId),
            async (output, cancellationToken) =>
            {
                _ = await ((IPlannedPrescreenReportWriter)replacementWriter)
                    .WritePlannedAsync(
                        output,
                        context,
                        projection,
                        plan,
                        "replacement-operator",
                        cancellationToken);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.WriteAsync(projectId, replacement, cancellation.Token));

        Assert.Equal(1, cancelingPages.PageCalls);
        await AssertCommittedSnapshotUnchangedAsync(
            store,
            projectId,
            projectDirectory,
            snapshot);
    }

    [Fact]
    public async Task CriteriaDirectWriter_CancelAtContinuationBoundary_PreservesPriorArtifactAndManifest()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            builder =>
            {
                builder.WithColumns(
                    "傳票號碼",
                    "傳票日期",
                    "核准日期",
                    "科目代號",
                    "科目名稱",
                    "摘要",
                    "金額",
                    "借方旗標");
                for (var index = 1; index <= 9; index++)
                {
                    builder.AddRow(
                        $"JV-{index:000}",
                        "2025-03-05",
                        "2025-03-06",
                        "1101",
                        "現金",
                        $"調整列 {index}",
                        "100.00",
                        1);
                    builder.AddRow(
                        $"JV-{index:000}",
                        "2025-03-05",
                        "2025-03-06",
                        "4101",
                        "收入",
                        $"正常列 {index}",
                        "100.00",
                        0);
                }
            },
            validateForDownstream: true);
        var committedScenario = await host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new
            {
                scenarios = new[]
                {
                    KeywordScenario("調整情境", "調整")
                }
            }));
        var revision = committedScenario.GetProperty("resultRef")
            .GetProperty("revision")
            .GetString()!;
        var folder = new JetProjectFolder(host.ProjectsRoot);
        var projectDirectory = Path.GetFullPath(
            folder.GetProjectDirectory(projectId));
        var store = new ProjectReportArtifactStore(folder);
        var database = new SqliteProjectDatabase(folder);
        var scenarios = await new LocalFilterScenarioStore(database)
            .ListAsync(projectId, CancellationToken.None);
        var context = new CriteriaSelectionReportContext(
            new ReportDocumentContext(
                projectId,
                "Criteria 取消驗收公司",
                "2025-01-01",
                "2025-12-31",
                null,
                10_000),
            revision,
            Assert.Single(scenarios).SavedUtc,
            scenarios,
            new Dictionary<int, string>
            {
                [1] = "摘要包含「調整」"
            });
        var writer = CreateWriter(
            database,
            new LocalPrescreenPageRepository(database),
            new LegacyReportWriterOptions(17));
        var committed = await store.WriteAsync(
            projectId,
            new ReportArtifactWriteRequest(
                ReportArtifactKind.CriteriaSelectionReport,
                new ReportArtifactSourceRefs(
                    InitialValidationRunId,
                    InitialPrescreenRunId,
                    revision,
                    [1]),
                async (output, cancellationToken) =>
                {
                    _ = await ((ITypedCriteriaSelectionReportWriter)writer)
                        .WriteTypedAsync(
                            output,
                            context,
                            "tester",
                            cancellationToken);
                }),
            CancellationToken.None);
        var snapshot = await CaptureCommittedSnapshotAsync(
            projectDirectory,
            committed);

        using var cancellation = new CancellationTokenSource();
        var continuationBoundaryReached = false;
        var replacement = new ReportArtifactWriteRequest(
            ReportArtifactKind.CriteriaSelectionReport,
            new ReportArtifactSourceRefs(
                ReplacementValidationRunId,
                ReplacementPrescreenRunId,
                "replacement-revision",
                [2]),
            async (output, cancellationToken) =>
            {
                _ = await ((ITypedCriteriaSelectionReportWriter)writer)
                    .WriteTypedAsync(
                        output,
                        context,
                        "replacement-operator",
                        cancellationToken,
                        progress =>
                        {
                            if (string.Equals(
                                    progress.SheetName,
                                    "#Criteria Select 1",
                                    StringComparison.Ordinal))
                            {
                                continuationBoundaryReached = true;
                                cancellation.Cancel();
                            }
                        });
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.WriteAsync(projectId, replacement, cancellation.Token));

        Assert.True(continuationBoundaryReached);
        await AssertCommittedSnapshotUnchangedAsync(
            store,
            projectId,
            projectDirectory,
            snapshot);
    }

    private static LegacyReportWriter CreateWriter(
        SqliteProjectDatabase database,
        IPrescreenPageRepository prescreenPages,
        LegacyReportWriterOptions? options = null) =>
        new(
            new LocalCompletenessAccountPageRepository(database),
            new LocalCompletenessDiffPageRepository(database),
            new LocalUnbalancedGlEntryPageRepository(database),
            new LocalNullRecordsPageRepository(database),
            new LocalInfSamplePageRepository(database),
            prescreenPages,
            new LocalRawGlExportRepository(database),
            new LocalImportRepository(database),
            new LocalMappingStateStore(database),
            new LocalCreatorSummaryExportRepository(database),
            new LocalAccountUsageExportRepository(database),
            new LocalTagMatrixScenariosRepository(database),
            new LocalTagMatrixRowPageRepository(database),
            options ?? new LegacyReportWriterOptions());

    private static PrescreenReportProjection PrescreenProjection(
        long rowCount)
    {
        var notApplicable = new PrescreenReportRuleProjection(
            "notApplicable",
            "測試未執行");
        return new PrescreenReportProjection(
            PostPeriodApproval: new("completed", null, rowCount),
            SuspiciousKeywords: notApplicable,
            UnexpectedAccountPair: notApplicable,
            TrailingZeros: notApplicable,
            CreatorSummary: new("completed", null),
            RareAccounts: new("completed", null),
            BlankDescription: notApplicable);
    }

    private static PrescreenReportPlan PrescreenPlan(
        string projectId,
        PrescreenReportProjection projection,
        long rowCount)
    {
        var unfinalized = JetAuditProgram.Plan(new PrescreenReportRequest(
            projectId,
            new FilterRuleContext(
                10_000,
                "2025-12-31",
                "2025-01-01",
                "2025-12-31"),
            projection.PostPeriodApproval.NaReason,
            projection.SuspiciousKeywords.NaReason,
            projection.UnexpectedAccountPair.NaReason,
            projection.TrailingZeros.NaReason,
            projection.BlankDescription.NaReason));
        return JetAuditProgram.Finalize(
            unfinalized,
            new PrescreenReportPlanningFacts(
                new Dictionary<
                    PrescreenReportDetailKind,
                    PrescreenHitCounts>
                {
                    [PrescreenReportDetailKind.PostPeriodApproval] =
                        new(VoucherHitCount: 1, RowHitCount: rowCount)
                }));
    }

    private static PrescreenReportContext PrescreenContext(
        string projectId) =>
        new(
            new ReportDocumentContext(
                projectId,
                "Pre-screening 取消驗收公司",
                "2025-01-01",
                "2025-12-31",
                "2025-12-31",
                10_000),
            InitialPrescreenRunId,
            new DateTimeOffset(
                2025,
                12,
                31,
                10,
                30,
                0,
                TimeSpan.Zero),
            "{}");

    private static object KeywordScenario(string name, string keyword) => new
    {
        name,
        rationale = $"測試 {name}",
        groups = new[]
        {
            new
            {
                join = "AND",
                rules = new[]
                {
                    new
                    {
                        join = "AND",
                        type = "customKeywords",
                        keywords = keyword
                    }
                }
            }
        }
    };

    private static async Task<CommittedArtifactSnapshot>
        CaptureCommittedSnapshotAsync(
            string projectDirectory,
            ReportArtifact artifact)
    {
        var artifactPath = Path.Combine(
            projectDirectory,
            artifact.RelativeFileName);
        var manifestPath = Path.Combine(
            projectDirectory,
            ProjectReportArtifactStore.ManifestFileName);
        return new CommittedArtifactSnapshot(
            artifact,
            artifactPath,
            await File.ReadAllBytesAsync(
                artifactPath,
                CancellationToken.None),
            await File.ReadAllBytesAsync(
                manifestPath,
                CancellationToken.None),
            CurrentWorkbookPaths(projectDirectory));
    }

    private static async Task AssertCommittedSnapshotUnchangedAsync(
        ProjectReportArtifactStore store,
        string projectId,
        string projectDirectory,
        CommittedArtifactSnapshot snapshot)
    {
        Assert.Equal(
            snapshot.WorkbookBytes,
            await File.ReadAllBytesAsync(
                snapshot.ArtifactPath,
                CancellationToken.None));
        Assert.Equal(
            snapshot.ManifestBytes,
            await File.ReadAllBytesAsync(
                Path.Combine(
                    projectDirectory,
                    ProjectReportArtifactStore.ManifestFileName),
                CancellationToken.None));
        Assert.Equal(
            snapshot.WorkbookPaths,
            CurrentWorkbookPaths(projectDirectory));

        var current = Assert.Single(await store.ListAsync(
            projectId,
            CancellationToken.None));
        AssertArtifactIdentityEqual(snapshot.Artifact, current);
        Assert.Empty(Directory.GetFiles(
            projectDirectory,
            "*.tmp",
            SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(
            projectDirectory,
            ProjectReportArtifactStore.JournalFileName)));
    }

    private static string[] CurrentWorkbookPaths(
        string projectDirectory) =>
        Directory.GetFiles(
                projectDirectory,
                "*.xlsx",
                SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static void AssertArtifactIdentityEqual(
        ReportArtifact expected,
        ReportArtifact actual)
    {
        Assert.Equal(expected.ArtifactId, actual.ArtifactId);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.RelativeFileName, actual.RelativeFileName);
        Assert.Equal(expected.GeneratedUtc, actual.GeneratedUtc);
        Assert.Equal(expected.Bytes, actual.Bytes);
        Assert.Equal(expected.Sha256, actual.Sha256);
        Assert.Equal(expected.Stale, actual.Stale);
        Assert.Equal(
            expected.SourceRef.ValidationRunId,
            actual.SourceRef.ValidationRunId);
        Assert.Equal(
            expected.SourceRef.PrescreenRunId,
            actual.SourceRef.PrescreenRunId);
        Assert.Equal(
            expected.SourceRef.ScenarioRevision,
            actual.SourceRef.ScenarioRevision);
        if (expected.SourceRef.ScenarioPositions is null)
        {
            Assert.Null(actual.SourceRef.ScenarioPositions);
        }
        else
        {
            Assert.NotNull(actual.SourceRef.ScenarioPositions);
            Assert.Equal(
                expected.SourceRef.ScenarioPositions,
                actual.SourceRef.ScenarioPositions);
        }
    }

    private sealed record CommittedArtifactSnapshot(
        ReportArtifact Artifact,
        string ArtifactPath,
        byte[] WorkbookBytes,
        byte[] ManifestBytes,
        string[] WorkbookPaths);

    private sealed class CancelAfterFirstPrescreenPageRepository(
        IPrescreenPageRepository inner,
        CancellationTokenSource cancellation) : IPrescreenPageRepository
    {
        internal int PageCalls { get; private set; }

        public async Task<PageResult<PrescreenHitRow>> GetPageAsync(
            string projectId,
            string ruleKey,
            FilterRuleContext context,
            PageRequest request,
            CancellationToken cancellationToken)
        {
            PageCalls++;
            var page = await inner.GetPageAsync(
                projectId,
                ruleKey,
                context,
                request,
                cancellationToken);
            if (PageCalls == 1)
            {
                cancellation.Cancel();
            }

            return page;
        }

        public Task<PrescreenHitCounts> GetCountsAsync(
            string projectId,
            string ruleKey,
            FilterRuleContext context,
            CancellationToken cancellationToken) =>
            inner.GetCountsAsync(
                projectId,
                ruleKey,
                context,
                cancellationToken);
    }
}
