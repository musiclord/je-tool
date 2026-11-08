using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

// 舊式關鍵字條件停用後，合成情境改用欄位關鍵字，原命中與報表斷言保留；首次失敗：20261014-135805134-9060396119bc4e8f8dae2eecd29f1776。

public sealed class WorkingPaperDirectTemplateReportAtomicityTests
{
    [Fact]
    public async Task DirectWriter_CancelDuringStep41_PreservesPriorReportAndManifest()
    {
        using var prepared = await PrepareAsync();
        using var cancellation = new CancellationTokenSource();
        var reachedStep41 = false;
        var capturingRows = new CapturePreparedSessionRepository(
            new LocalHighRiskSummaryRowPageRepository(prepared.Database));
        var writer = (IWorkingPaperPlanWriter)CreateWriter(
            prepared.Database,
            capturingRows,
            progressRowInterval: 1);
        var replacement = ReplacementRequest(
            writer,
            ReplacementContext(prepared.Context),
            prepared.Plan,
            progress =>
            {
                if (string.Equals(
                        progress.SheetName,
                        WorkingPaperSheetCatalog.Step41,
                        StringComparison.Ordinal)
                    && progress.RowsWritten == 1)
                {
                    reachedStep41 = true;
                    cancellation.Cancel();
                }
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            prepared.Store.WriteAsync(
                prepared.ProjectId,
                replacement,
                cancellation.Token));

        Assert.True(reachedStep41);
        Assert.NotNull(capturingRows.Metrics);
        Assert.Equal(1, capturingRows.Metrics!.CleanupCommands);
        Assert.True(capturingRows.Metrics.Disposed);
        Assert.True(capturingRows.Metrics.TemporaryObjectsCleared);
        await AssertCommittedSnapshotUnchangedAsync(prepared);
    }

    [Fact]
    public async Task DirectWriter_CancelDuringPreparedReader_PreservesPriorReportAndCleansSession()
    {
        using var prepared = await PrepareAsync();
        using var cancellation = new CancellationTokenSource();
        var cancellingRows = new CancelAfterFirstPreparedRowRepository(
            new LocalHighRiskSummaryRowPageRepository(prepared.Database),
            cancellation);
        var writer = (IWorkingPaperPlanWriter)CreateWriter(
            prepared.Database,
            cancellingRows);
        var replacement = ReplacementRequest(
            writer,
            ReplacementContext(prepared.Context),
            prepared.Plan);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            prepared.Store.WriteAsync(
                prepared.ProjectId,
                replacement,
                cancellation.Token));

        Assert.Equal(1, cancellingRows.RowsRead);
        Assert.NotNull(cancellingRows.Metrics);
        Assert.Equal(1, cancellingRows.Metrics!.OrderedReaderCommands);
        Assert.Equal(1, cancellingRows.Metrics.CleanupCommands);
        Assert.True(cancellingRows.Metrics.TemporaryObjectsCleared);
        await AssertCommittedSnapshotUnchangedAsync(prepared);
    }

    [Fact]
    public async Task DirectWriter_CancelImmediatelyAfterPreparedMaterialization_PreservesPriorReportAndCleansSession()
    {
        using var prepared = await PrepareAsync();
        using var cancellation = new CancellationTokenSource();
        var cancellingRows = new CancelAfterPreparedMaterializationRepository(
            new LocalHighRiskSummaryRowPageRepository(prepared.Database),
            cancellation);
        var writer = (IWorkingPaperPlanWriter)CreateWriter(
            prepared.Database,
            cancellingRows);
        var replacement = ReplacementRequest(
            writer,
            ReplacementContext(prepared.Context),
            prepared.Plan);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            prepared.Store.WriteAsync(
                prepared.ProjectId,
                replacement,
                cancellation.Token));

        var metrics = Assert.IsType<WorkingPaperStep41PreparedSessionMetrics>(
            cancellingRows.Metrics);
        Assert.Equal(1, metrics.SchemaReadinessCommands);
        Assert.Equal(1, metrics.TemporaryTableInitializationCommands);
        Assert.Equal(1, metrics.HitVoucherMaterializationCommands);
        Assert.Equal(1, metrics.RowTagMaterializationCommands);
        Assert.Equal(1, metrics.OrderedReaderCommands);
        Assert.Equal(0, metrics.RowsRead);
        Assert.False(metrics.ReaderCompleted);
        Assert.True(metrics.TransactionRolledBack);
        Assert.True(metrics.CleanupCommandSucceeded);
        Assert.True(metrics.ConnectionDisposed);
        Assert.True(metrics.TemporaryObjectsCleared);
        await AssertCommittedSnapshotUnchangedAsync(prepared);
    }

    [Fact]
    public async Task DirectWriter_CancelAtFinalizingBoundary_PreservesPriorReportAndCleansSession()
    {
        using var prepared = await PrepareAsync();
        using var cancellation = new CancellationTokenSource();
        var capturingRows = new CapturePreparedSessionRepository(
            new LocalHighRiskSummaryRowPageRepository(prepared.Database));
        var writer = (IWorkingPaperPlanWriter)CreateWriter(
            prepared.Database,
            capturingRows);
        var request = new ReportWriteRequest(
            ReportKind.WorkingPaper,
            new ReportSourceRefs(
                Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"),
                "cancel-finalizing",
                [1]),
            async (output, cancellationToken) =>
            {
                _ = await writer.WriteAsync(
                    output,
                    ReplacementContext(prepared.Context),
                    prepared.Plan,
                    cancellationToken);
                Assert.NotNull(capturingRows.Metrics);
                Assert.True(capturingRows.Metrics!.Disposed);
                Assert.True(capturingRows.Metrics.TemporaryObjectsCleared);
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            prepared.Store.WriteAsync(
                prepared.ProjectId,
                request,
                cancellation.Token));

        Assert.NotNull(capturingRows.Metrics);
        Assert.Equal(1, capturingRows.Metrics!.CleanupCommands);
        Assert.True(capturingRows.Metrics.Disposed);
        await AssertCommittedSnapshotUnchangedAsync(prepared);
    }

    [Fact]
    public async Task DirectWriter_OrderedPreparedReaderThrowsAfterFirstRow_PreservesPriorReportAndManifest()
    {
        using var prepared = await PrepareAsync();
        var throwingRows = new ThrowAfterFirstStep41RowRepository(
            new LocalHighRiskSummaryRowPageRepository(prepared.Database));
        var writer = (IWorkingPaperPlanWriter)CreateWriter(
            prepared.Database,
            throwingRows);
        var replacement = ReplacementRequest(
            writer,
            ReplacementContext(prepared.Context),
            prepared.Plan);

        await Assert.ThrowsAsync<InjectedStep41ReaderException>(() =>
            prepared.Store.WriteAsync(
                prepared.ProjectId,
                replacement,
                CancellationToken.None));

        Assert.Equal(2, throwingRows.RowsRead);
        Assert.NotNull(throwingRows.Metrics);
        Assert.Equal(1, throwingRows.Metrics!.OrderedReaderCommands);
        Assert.Equal(1, throwingRows.Metrics.CleanupCommands);
        Assert.True(throwingRows.Metrics.Disposed);
        await AssertCommittedSnapshotUnchangedAsync(prepared);
    }

    [Fact]
    public async Task DirectWriter_ExistingWorkingPaperLockedByExcelLikeHandle_WritesNewVersionAndLeavesLockedFileAlone()
    {
        // 工作底稿每次匯出都是新版本檔，正式底稿不得被默默改寫（裁定 2026-09-02-03）。舊版被 Excel 開著不會擋住匯出，也不會被改寫；
        // 修改前這個測試預期 IOException，因為舊設計會嘗試覆蓋同名檔。
        using var prepared = await PrepareAsync();
        var writer = (IWorkingPaperPlanWriter)CreateWriter(
            prepared.Database,
            new LocalHighRiskSummaryRowPageRepository(prepared.Database));
        var replacement = ReplacementRequest(
            writer,
            ReplacementContext(prepared.Context),
            prepared.Plan);

        Report published;
        using (var blocker = new FileStream(
                   prepared.Snapshot.ReportPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            Assert.True(blocker.Length > 0);
            published = await prepared.Store.WriteAsync(
                prepared.ProjectId,
                replacement,
                CancellationToken.None);
        }

        Assert.Equal(ReportKind.WorkingPaper, published.Kind);
        Assert.NotEqual(prepared.Snapshot.Report.RelativeFileName, published.RelativeFileName);
        var publishedPath = Path.GetFullPath(Path.Combine(prepared.ProjectDirectory, published.RelativeFileName));
        Assert.True(File.Exists(publishedPath));
        Assert.Equal(
            prepared.Snapshot.WorkbookBytes,
            await File.ReadAllBytesAsync(prepared.Snapshot.ReportPath, CancellationToken.None));
        Assert.Equal(
            prepared.Snapshot.WorkbookPaths.Append(publishedPath).Order(StringComparer.Ordinal).ToArray(),
            CurrentWorkbookPaths(prepared.ProjectDirectory));

        var listed = await prepared.Store.ListAsync(prepared.ProjectId, CancellationToken.None);
        Assert.Equal(2, listed.Count(report => report.Kind == ReportKind.WorkingPaper));
        Assert.Contains(listed, report => report.ReportId == prepared.Snapshot.Report.ReportId);
        Assert.Contains(listed, report => report.ReportId == published.ReportId);
        AssertNoTemporaryState(prepared.ProjectDirectory);
    }

    private static async Task<PreparedCase> PrepareAsync()
    {
        var host = new HandlerTestHost();
        try
        {
            var projectId = await ReportExportFixture.SetupProjectAsync(host);
            var validation = await host.DispatchAsync("validate.run");
            var prescreen = await host.DispatchAsync("prescreen.run");
            var filter = await host.DispatchAsync(
                "filter.commit",
                JsonSerializer.Serialize(new
                {
                    scenarios = new[]
                    {
                        new
                        {
                            name = "WorkingPaper atomicity 情境",
                            rationale = "原始摘要包含調整",
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
                                            type = "fieldValue", field = "description", @operator = "contains", values = new[] { "調整" }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }));
            var validationResultId = validation
                .GetProperty("resultRef")
                .GetProperty("resultId")
                .GetString()!;
            var prescreenResultId = prescreen
                .GetProperty("resultRef")
                .GetProperty("resultId")
                .GetString()!;
            var revision = filter
                .GetProperty("resultRef")
                .GetProperty("revision")
                .GetString()!;

            var folder = new JetProjectFolder(host.ProjectsRoot);
            var database = new SqliteProjectDatabase(folder);
            var context = new WorkingPaperContext(
                projectId,
                "測試自含案件",
                "2025-01-01",
                "2025-12-31",
                "2025-12-31",
                10_000,
                validationResultId,
                revision,
                [1]);
            var plan = await CreatePlanAsync(database, context);
            var store = new ProjectReportStore(folder);
            var initialWriter = (IWorkingPaperPlanWriter)CreateWriter(
                database,
                new LocalHighRiskSummaryRowPageRepository(database));
            var committed = await store.WriteAsync(
                projectId,
                new ReportWriteRequest(
                    ReportKind.WorkingPaper,
                    new ReportSourceRefs(
                        validationResultId,
                        prescreenResultId,
                        revision,
                        [1]),
                    async (output, cancellationToken) =>
                    {
                        _ = await initialWriter.WriteAsync(
                            output,
                            context,
                            plan,
                            cancellationToken);
                    }),
                CancellationToken.None);
            var projectDirectory = Path.GetFullPath(
                folder.GetProjectDirectory(projectId));
            var snapshot = await CaptureCommittedSnapshotAsync(
                projectDirectory,
                committed);
            return new PreparedCase(
                host,
                projectId,
                projectDirectory,
                database,
                store,
                context,
                plan,
                snapshot);
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    private static async Task<WorkingPaperPlan> CreatePlanAsync(
        SqliteProjectDatabase database,
        WorkingPaperContext context)
    {
        var savedScenarios = await new LocalFilterScenarioStore(database)
            .ListAsync(context.ProjectId, CancellationToken.None);
        var scenario = Assert.Single(savedScenarios);
        Assert.Equal(1, scenario.Position);
        var initial = JetAuditProgram.Plan(new WorkingPaperRequest(
            context.ProjectId,
            context.PeriodStart,
            context.PeriodEnd,
            context.PrepStartDate,
            context.MoneyScale,
            context.ValidationResultId,
            context.ScenarioRevision,
            [
                new WorkingPaperScenarioSelection(
                    scenario.Position,
                    scenario.Name,
                    scenario.Rationale)
            ],
            context.PopulationScope));
        var facts = await new WorkingPaperPlanningFactsPort(
                new LocalCompletenessDiffPageRepository(database),
                new LocalVoucherBalancePageRepository(database),
                new LocalHighRiskSummaryScenariosRepository(database),
                new LocalFieldInfoFactsPort(database))
            .ExecuteAsync(initial, CancellationToken.None);
        return JetAuditProgram.Finalize(initial, facts);
    }

    private static WorkingPaperWriter CreateWriter(
        SqliteProjectDatabase database,
        IHighRiskSummaryRowPageRepository highRiskSummaryRows,
        int progressRowInterval = 10_000) =>
        new(
            new LocalCompletenessAccountPageRepository(database),
            new LocalCompletenessDiffPageRepository(database),
            new LocalVoucherBalancePageRepository(database),
            new LocalPreparerSummaryExportRepository(database),
            new LocalInfSamplePageRepository(database),
            new LocalHighRiskSummaryVoucherPageRepository(database),
            highRiskSummaryRows,
            new LocalMappingStateStore(database),
            new LocalCalendarExportRepository(database),
            new LocalAccountMappingExportRepository(database),
            new WorkingPaperWriterOptions(),
            progressRowInterval,
            new LocalRawGlExportRepository(database));

    private static WorkingPaperContext ReplacementContext(
        WorkingPaperContext context) =>
        context with
        {
            CompanyName = "WorkingPaper 置換版本公司"
        };

    private static ReportWriteRequest ReplacementRequest(
        IWorkingPaperPlanWriter writer,
        WorkingPaperContext context,
        WorkingPaperPlan plan,
        Action<WorkingPaperProgress>? progress = null) =>
        new(
            ReportKind.WorkingPaper,
            new ReportSourceRefs(
                Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"),
                "replacement-revision",
                [1]),
            async (output, cancellationToken) =>
            {
                _ = await writer.WriteAsync(
                    output,
                    context,
                    plan,
                    cancellationToken,
                    progress);
            });

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
        PreparedCase prepared,
        bool recoverPendingMutation = false)
    {
        var snapshot = prepared.Snapshot;
        Assert.Equal(
            snapshot.WorkbookBytes,
            await File.ReadAllBytesAsync(
                snapshot.ReportPath,
                CancellationToken.None));
        Assert.Equal(
            snapshot.ManifestBytes,
            await File.ReadAllBytesAsync(
                Path.Combine(
                    prepared.ProjectDirectory,
                    ProjectReportStore.ManifestFileName),
                CancellationToken.None));
        Assert.Equal(
            snapshot.WorkbookPaths,
            CurrentWorkbookPaths(prepared.ProjectDirectory));

        if (!recoverPendingMutation)
        {
            AssertNoTemporaryState(prepared.ProjectDirectory);
        }
        var current = Assert.Single(await prepared.Store.ListAsync(
            prepared.ProjectId,
            CancellationToken.None));
        AssertReportIdentityEqual(snapshot.Report, current);
        AssertNoTemporaryState(prepared.ProjectDirectory);
    }

    private static void AssertNoTemporaryState(string projectDirectory)
    {
        Assert.Empty(Directory.GetFiles(
            projectDirectory,
            "*.tmp",
            SearchOption.AllDirectories));
        // 原本只確認舊版的暫存檔 .report-artifacts.mutation-v1.json 不在；產品早已不寫這個檔，斷言一定通過。改成確認現行的暫存檔都沒有留下。
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

    private sealed class ThrowAfterFirstStep41RowRepository(
        LocalHighRiskSummaryRowPageRepository inner)
        : IHighRiskSummaryRowPageRepository, IWorkingPaperStep41PreparedSessionFactory
    {
        internal int RowsRead { get; private set; }

        internal WorkingPaperStep41PreparedSessionMetrics? Metrics { get; private set; }

        public Task<(
            PageResult<RowTagRow> Page,
            IReadOnlyList<long> EntryIds,
            IReadOnlyDictionary<long, IReadOnlyList<int>> PositionsByEntry)>
            GetPageAsync(
                string projectId,
                GlPopulationContext context,
                PageRequest request,
                IReadOnlyList<int>? scenarioPositions,
                CancellationToken cancellationToken) =>
            inner.GetPageAsync(
                projectId,
                context,
                request,
                scenarioPositions,
                cancellationToken);

        async Task<IWorkingPaperStep41PreparedSession>
            IWorkingPaperStep41PreparedSessionFactory.PrepareAsync(
                string projectId,
                GlPopulationContext context,
                IReadOnlyList<int> scenarioPositions,
                LegacyFieldKind lineItemKind,
                CancellationToken cancellationToken)
        {
            var session = await ((IWorkingPaperStep41PreparedSessionFactory)inner)
                .PrepareAsync(
                    projectId,
                    context,
                    scenarioPositions,
                    lineItemKind,
                    cancellationToken);
            Metrics = session.Metrics;
            return new ThrowAfterFirstRowSession(session, this);
        }

        private sealed class ThrowAfterFirstRowSession(
            IWorkingPaperStep41PreparedSession inner,
            ThrowAfterFirstStep41RowRepository owner)
            : IWorkingPaperStep41PreparedSession
        {
            public WorkingPaperStep41PreparedSessionMetrics Metrics => inner.Metrics;

            public async IAsyncEnumerable<WorkingPaperStep41SourceRow> ReadRowsAsync(
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken)
            {
                await foreach (var row in inner.ReadRowsAsync(cancellationToken))
                {
                    owner.RowsRead++;
                    if (owner.RowsRead > 1)
                    {
                        throw new InjectedStep41ReaderException();
                    }
                    yield return row;
                }
            }

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private sealed class InjectedStep41ReaderException()
        : Exception("Injected failure after the first WorkingPaper Step4-1 prepared row.");

    private sealed class CapturePreparedSessionRepository(
        LocalHighRiskSummaryRowPageRepository inner)
        : IHighRiskSummaryRowPageRepository, IWorkingPaperStep41PreparedSessionFactory
    {
        internal WorkingPaperStep41PreparedSessionMetrics? Metrics { get; private set; }

        public Task<(
            PageResult<RowTagRow> Page,
            IReadOnlyList<long> EntryIds,
            IReadOnlyDictionary<long, IReadOnlyList<int>> PositionsByEntry)>
            GetPageAsync(
                string projectId,
                GlPopulationContext context,
                PageRequest request,
                IReadOnlyList<int>? scenarioPositions,
                CancellationToken cancellationToken) =>
            inner.GetPageAsync(
                projectId,
                context,
                request,
                scenarioPositions,
                cancellationToken);

        public async Task<IWorkingPaperStep41PreparedSession> PrepareAsync(
            string projectId,
            GlPopulationContext context,
            IReadOnlyList<int> scenarioPositions,
            LegacyFieldKind lineItemKind,
            CancellationToken cancellationToken)
        {
            var session = await ((IWorkingPaperStep41PreparedSessionFactory)inner)
                .PrepareAsync(
                    projectId,
                    context,
                    scenarioPositions,
                    lineItemKind,
                    cancellationToken);
            Metrics = session.Metrics;
            return session;
        }
    }

    private sealed class CancelAfterPreparedMaterializationRepository(
        LocalHighRiskSummaryRowPageRepository inner,
        CancellationTokenSource cancellation)
        : IHighRiskSummaryRowPageRepository, IWorkingPaperStep41PreparedSessionFactory
    {
        internal WorkingPaperStep41PreparedSessionMetrics? Metrics { get; private set; }

        public Task<(
            PageResult<RowTagRow> Page,
            IReadOnlyList<long> EntryIds,
            IReadOnlyDictionary<long, IReadOnlyList<int>> PositionsByEntry)>
            GetPageAsync(
                string projectId,
                GlPopulationContext context,
                PageRequest request,
                IReadOnlyList<int>? scenarioPositions,
                CancellationToken cancellationToken) =>
            inner.GetPageAsync(
                projectId,
                context,
                request,
                scenarioPositions,
                cancellationToken);

        public async Task<IWorkingPaperStep41PreparedSession> PrepareAsync(
            string projectId,
            GlPopulationContext context,
            IReadOnlyList<int> scenarioPositions,
            LegacyFieldKind lineItemKind,
            CancellationToken cancellationToken)
        {
            var session = await ((IWorkingPaperStep41PreparedSessionFactory)inner)
                .PrepareAsync(
                    projectId,
                    context,
                    scenarioPositions,
                    lineItemKind,
                    cancellationToken);
            Metrics = session.Metrics;
            cancellation.Cancel();
            return session;
        }
    }

    private sealed class CancelAfterFirstPreparedRowRepository(
        LocalHighRiskSummaryRowPageRepository inner,
        CancellationTokenSource cancellation)
        : IHighRiskSummaryRowPageRepository, IWorkingPaperStep41PreparedSessionFactory
    {
        internal int RowsRead { get; private set; }

        internal WorkingPaperStep41PreparedSessionMetrics? Metrics { get; private set; }

        public Task<(
            PageResult<RowTagRow> Page,
            IReadOnlyList<long> EntryIds,
            IReadOnlyDictionary<long, IReadOnlyList<int>> PositionsByEntry)>
            GetPageAsync(
                string projectId,
                GlPopulationContext context,
                PageRequest request,
                IReadOnlyList<int>? scenarioPositions,
                CancellationToken cancellationToken) =>
            inner.GetPageAsync(
                projectId,
                context,
                request,
                scenarioPositions,
                cancellationToken);

        public async Task<IWorkingPaperStep41PreparedSession> PrepareAsync(
            string projectId,
            GlPopulationContext context,
            IReadOnlyList<int> scenarioPositions,
            LegacyFieldKind lineItemKind,
            CancellationToken cancellationToken)
        {
            var session = await ((IWorkingPaperStep41PreparedSessionFactory)inner)
                .PrepareAsync(
                    projectId,
                    context,
                    scenarioPositions,
                    lineItemKind,
                    cancellationToken);
            Metrics = session.Metrics;
            return new CancelAfterFirstRowSession(session, this, cancellation);
        }

        private sealed class CancelAfterFirstRowSession(
            IWorkingPaperStep41PreparedSession inner,
            CancelAfterFirstPreparedRowRepository owner,
            CancellationTokenSource cancellation)
            : IWorkingPaperStep41PreparedSession
        {
            public WorkingPaperStep41PreparedSessionMetrics Metrics => inner.Metrics;

            public async IAsyncEnumerable<WorkingPaperStep41SourceRow> ReadRowsAsync(
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken)
            {
                await foreach (var row in inner.ReadRowsAsync(cancellationToken))
                {
                    owner.RowsRead++;
                    cancellation.Cancel();
                    yield return row;
                }
            }

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private sealed record CommittedReportSnapshot(
        Report Report,
        string ReportPath,
        byte[] WorkbookBytes,
        byte[] ManifestBytes,
        string[] WorkbookPaths);

    private sealed class PreparedCase(
        HandlerTestHost host,
        string projectId,
        string projectDirectory,
        SqliteProjectDatabase database,
        ProjectReportStore store,
        WorkingPaperContext context,
        WorkingPaperPlan plan,
        CommittedReportSnapshot snapshot) : IDisposable
    {
        internal string ProjectId { get; } = projectId;

        internal string ProjectDirectory { get; } = projectDirectory;

        internal SqliteProjectDatabase Database { get; } = database;

        internal ProjectReportStore Store { get; } = store;

        internal WorkingPaperContext Context { get; } = context;

        internal WorkingPaperPlan Plan { get; } = plan;

        internal CommittedReportSnapshot Snapshot { get; } = snapshot;

        public void Dispose() => host.Dispose();
    }
}
