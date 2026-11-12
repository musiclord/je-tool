using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class PrescreenCriteriaReportCancellationTests
{
    private const string InitialValidationResultId =
        "11111111111111111111111111111111";
    private const string InitialPrescreenResultId =
        "22222222222222222222222222222222";
    private const string ReplacementValidationResultId =
        "33333333333333333333333333333333";
    private const string ReplacementPrescreenResultId =
        "44444444444444444444444444444444";

    [Fact]
    public async Task PrescreenDirectWriter_CancelDuringFirstDetailPage_PreservesPriorReportAndManifest()
    {
        using var host = new HandlerTestHost();
        var projectId = await ReportExportFixture.SetupProjectAsync(
            host);
        var folder = new JetProjectFolder(host.ProjectsRoot);
        var projectDirectory = Path.GetFullPath(
            folder.GetProjectDirectory(projectId));
        var store = new ProjectReportStore(folder);
        var database = new SqliteProjectDatabase(folder);
        var projection = PrescreenProjection(rowCount: 2);
        var plan = PrescreenPlan(projectId, projection, rowCount: 2);
        var context = PrescreenContext(projectId);
        var committed = await store.WriteAsync(
            projectId,
            new ReportWriteRequest(
                ReportKind.PrescreenReport,
                new ReportSourceRefs(
                    PrescreenResultId: InitialPrescreenResultId),
                async (output, cancellationToken) =>
                {
                    _ = await ((IPrescreenReportWriter)CreateWriter(
                            database,
                            new LocalPrescreenPageRepository(database)))
                        .WriteAsync(
                            output,
                            context,
                            projection,
                            plan,
                            ReportWriterTestSupport.SyntheticMetadata(),
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
        var replacement = new ReportWriteRequest(
            ReportKind.PrescreenReport,
            new ReportSourceRefs(
                PrescreenResultId: ReplacementPrescreenResultId),
            async (output, cancellationToken) =>
            {
                _ = await ((IPrescreenReportWriter)replacementWriter)
                    .WriteAsync(
                        output,
                        context,
                        projection,
                        plan,
                        ReportWriterTestSupport.SyntheticMetadata(),
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
    public async Task CriteriaDirectWriter_CancelAtContinuationBoundary_PreservesPriorReportAndManifest()
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
        var store = new ProjectReportStore(folder);
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
            DateOnly.FromDateTime(Assert.Single(scenarios).SavedUtc.UtcDateTime),
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
            new ReportWriteRequest(
                ReportKind.CriteriaSelectionReport,
                new ReportSourceRefs(
                    InitialValidationResultId,
                    InitialPrescreenResultId,
                    revision,
                    [1]),
                async (output, cancellationToken) =>
                {
                    _ = await ((ICriteriaSelectionReportWriter)writer)
                        .WriteAsync(
                            output,
                            context,
                            ReportWriterTestSupport.SyntheticMetadata(),
                            [],
                            "tester",
                            cancellationToken);
                }),
            CancellationToken.None);
        var snapshot = await CaptureCommittedSnapshotAsync(
            projectDirectory,
            committed);

        using var cancellation = new CancellationTokenSource();
        var continuationBoundaryReached = false;
        var replacement = new ReportWriteRequest(
            ReportKind.CriteriaSelectionReport,
            new ReportSourceRefs(
                ReplacementValidationResultId,
                ReplacementPrescreenResultId,
                "replacement-revision",
                [2]),
            async (output, cancellationToken) =>
            {
                _ = await ((ICriteriaSelectionReportWriter)writer)
                    .WriteAsync(
                        output,
                        context,
                        ReportWriterTestSupport.SyntheticMetadata(),
                        [],
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
            new LocalUnbalancedVoucherPageRepository(database),
            new LocalBlankRecordsPageRepository(database),
            new LocalInfSamplePageRepository(database),
            prescreenPages,
            new LocalRawGlExportRepository(database),
            new LocalImportRepository(database),
            new LocalMappingStateStore(database),
            new LocalPreparerSummaryExportRepository(database),
            new LocalAccountUsageExportRepository(database),
            new LocalHighRiskSummaryScenariosRepository(database),
            new LocalSourceQualityPageRepository(database),
            new LocalHighRiskSummaryRowPageRepository(database),
            options ?? new LegacyReportWriterOptions());

    private static PrescreenReportProjection PrescreenProjection(
        long rowCount)
    {
        var notApplicable = new PrescreenReportRuleProjection(
            "notApplicable",
            "測試未執行");
        return new PrescreenReportProjection(
            PrepPeriodApproval: new("completed", null, rowCount),
            SuspiciousKeywords: notApplicable,
            UnexpectedAccountPair: notApplicable,
            TrailingZeros: notApplicable,
            PreparerSummary: new("completed", null),
            RareAccounts: new("completed", null),
            BlankDescription: notApplicable)
        {
            // 0 的個數照 IDEA 版由 GL 算出，投影帶那次預篩選的個數；這裡的尾零規則不執行，帶 6 只為了組出完整的投影。
            ZerosThreshold = 6
        };
    }

    private static PrescreenReportPlan PrescreenPlan(
        string projectId,
        PrescreenReportProjection projection,
        long rowCount)
    {
        var unfinalized = JetAuditProgram.Plan(new PrescreenReportRequest(
            projectId,
            new FilterConditionContext(
                10_000,
                "2025-12-31",
                "2025-01-01",
                "2025-12-31"),
            projection.PrepPeriodApproval.NaReason,
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
                    [PrescreenReportDetailKind.PrepPeriodApproval] =
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
            InitialPrescreenResultId,
            new DateOnly(2025, 12, 31),
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
                conditions = new[]
                {
                    new
                    {
                        join = "AND",
                        type = "fieldValue", field = "description", @operator = "contains",
                        values = new[] { keyword }
                    }
                }
            }
        }
    };

    private static async Task<CommittedReportSnapshot>
        CaptureCommittedSnapshotAsync(
            string projectDirectory,
            Report report)
    {
        var reportPath = Path.Combine(
            projectDirectory,
            report.RelativeFileName);
        var manifestPath = Path.Combine(
            projectDirectory,
            ProjectReportStore.ManifestFileName);
        return new CommittedReportSnapshot(
            report,
            reportPath,
            await File.ReadAllBytesAsync(
                reportPath,
                CancellationToken.None),
            await File.ReadAllBytesAsync(
                manifestPath,
                CancellationToken.None),
            CurrentWorkbookPaths(projectDirectory));
    }

    private static async Task AssertCommittedSnapshotUnchangedAsync(
        ProjectReportStore store,
        string projectId,
        string projectDirectory,
        CommittedReportSnapshot snapshot)
    {
        Assert.Equal(
            snapshot.WorkbookBytes,
            await File.ReadAllBytesAsync(
                snapshot.ReportPath,
                CancellationToken.None));
        Assert.Equal(
            snapshot.ManifestBytes,
            await File.ReadAllBytesAsync(
                Path.Combine(
                    projectDirectory,
                    ProjectReportStore.ManifestFileName),
                CancellationToken.None));
        Assert.Equal(
            snapshot.WorkbookPaths,
            CurrentWorkbookPaths(projectDirectory));

        var current = Assert.Single(await store.ListAsync(
            projectId,
            CancellationToken.None));
        AssertReportIdentityEqual(snapshot.Report, current);
        Assert.Empty(Directory.GetFiles(
            projectDirectory,
            "*.tmp",
            SearchOption.AllDirectories));
        // 確認現行的暫存檔都沒有留下。
        ReportStoreTempFiles.AssertNoneLeft(projectDirectory);
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

    private static void AssertReportIdentityEqual(
        Report expected,
        Report actual)
    {
        Assert.Equal(expected.ReportId, actual.ReportId);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.RelativeFileName, actual.RelativeFileName);
        Assert.Equal(expected.GeneratedUtc, actual.GeneratedUtc);
        Assert.Equal(expected.Bytes, actual.Bytes);
        Assert.Equal(expected.Stale, actual.Stale);
        Assert.Equal(
            expected.SourceRef.ValidationResultId,
            actual.SourceRef.ValidationResultId);
        Assert.Equal(
            expected.SourceRef.PrescreenResultId,
            actual.SourceRef.PrescreenResultId);
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

    private sealed record CommittedReportSnapshot(
        Report Report,
        string ReportPath,
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
            FilterConditionContext context,
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
            FilterConditionContext context,
            CancellationToken cancellationToken) =>
            inner.GetCountsAsync(
                projectId,
                ruleKey,
                context,
                cancellationToken);
    }
}
