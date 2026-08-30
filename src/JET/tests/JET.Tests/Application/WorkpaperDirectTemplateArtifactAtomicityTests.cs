using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class WorkpaperDirectTemplateArtifactAtomicityTests
{
    [Fact]
    public async Task DirectWriter_CancelDuringStep41_PreservesPriorArtifactAndManifest()
    {
        using var prepared = await PrepareAsync();
        using var cancellation = new CancellationTokenSource();
        var reachedStep41 = false;
        var capturingRows = new CapturePreparedSessionRepository(
            new LocalTagMatrixRowPageRepository(prepared.Database));
        var writer = (IWorkpaperPlanWriter)CreateWriter(
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
                        WorkpaperSheetCatalog.Step41,
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
    public async Task DirectWriter_CancelDuringPreparedReader_PreservesPriorArtifactAndCleansSession()
    {
        using var prepared = await PrepareAsync();
        using var cancellation = new CancellationTokenSource();
        var cancellingRows = new CancelAfterFirstPreparedRowRepository(
            new LocalTagMatrixRowPageRepository(prepared.Database),
            cancellation);
        var writer = (IWorkpaperPlanWriter)CreateWriter(
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
    public async Task DirectWriter_CancelImmediatelyAfterPreparedMaterialization_PreservesPriorArtifactAndCleansSession()
    {
        using var prepared = await PrepareAsync();
        using var cancellation = new CancellationTokenSource();
        var cancellingRows = new CancelAfterPreparedMaterializationRepository(
            new LocalTagMatrixRowPageRepository(prepared.Database),
            cancellation);
        var writer = (IWorkpaperPlanWriter)CreateWriter(
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

        var metrics = Assert.IsType<WorkpaperStep41PreparedSessionMetrics>(
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
    public async Task DirectWriter_CancelAtFinalizingBoundary_PreservesPriorArtifactAndCleansSession()
    {
        using var prepared = await PrepareAsync();
        using var cancellation = new CancellationTokenSource();
        var capturingRows = new CapturePreparedSessionRepository(
            new LocalTagMatrixRowPageRepository(prepared.Database));
        var writer = (IWorkpaperPlanWriter)CreateWriter(
            prepared.Database,
            capturingRows);
        var request = new ReportArtifactWriteRequest(
            ReportArtifactKind.WorkingPaper,
            new ReportArtifactSourceRefs(
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
    public async Task DirectWriter_OrderedPreparedReaderThrowsAfterFirstRow_PreservesPriorArtifactAndManifest()
    {
        using var prepared = await PrepareAsync();
        var throwingRows = new ThrowAfterFirstStep41RowRepository(
            new LocalTagMatrixRowPageRepository(prepared.Database));
        var writer = (IWorkpaperPlanWriter)CreateWriter(
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
    public async Task DirectWriter_ExistingWorkingPaperLockedByExcelLikeHandle_PreservesPriorArtifactAndManifest()
    {
        using var prepared = await PrepareAsync();
        var writer = (IWorkpaperPlanWriter)CreateWriter(
            prepared.Database,
            new LocalTagMatrixRowPageRepository(prepared.Database));
        var replacement = ReplacementRequest(
            writer,
            ReplacementContext(prepared.Context),
            prepared.Plan);

        using (var blocker = new FileStream(
                   prepared.Snapshot.ArtifactPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            Assert.True(blocker.Length > 0);
            await Assert.ThrowsAnyAsync<IOException>(() =>
                prepared.Store.WriteAsync(
                    prepared.ProjectId,
                    replacement,
                    CancellationToken.None));
        }

        await AssertCommittedSnapshotUnchangedAsync(
            prepared,
            recoverPendingMutation: true);
    }

    private static async Task<PreparedCase> PrepareAsync()
    {
        var host = new HandlerTestHost();
        try
        {
            var projectId = await ReportArtifactExportFixture.SetupProjectAsync(host);
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
                                    rules = new[]
                                    {
                                        new
                                        {
                                            join = "AND",
                                            type = "customKeywords",
                                            keywords = "調整"
                                        }
                                    }
                                }
                            }
                        }
                    }
                }));
            var validationRunId = validation
                .GetProperty("resultRef")
                .GetProperty("runId")
                .GetString()!;
            var prescreenRunId = prescreen
                .GetProperty("resultRef")
                .GetProperty("runId")
                .GetString()!;
            var revision = filter
                .GetProperty("resultRef")
                .GetProperty("revision")
                .GetString()!;

            var folder = new JetProjectFolder(host.ProjectsRoot);
            var database = new SqliteProjectDatabase(folder);
            var context = new WorkpaperContext(
                projectId,
                "測試自含案件",
                "2025-01-01",
                "2025-12-31",
                "2025-12-31",
                10_000,
                validationRunId,
                revision,
                [1]);
            var plan = await CreatePlanAsync(database, context);
            var store = new ProjectReportArtifactStore(folder);
            var initialWriter = (IWorkpaperPlanWriter)CreateWriter(
                database,
                new LocalTagMatrixRowPageRepository(database));
            var committed = await store.WriteAsync(
                projectId,
                new ReportArtifactWriteRequest(
                    ReportArtifactKind.WorkingPaper,
                    new ReportArtifactSourceRefs(
                        validationRunId,
                        prescreenRunId,
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

    private static async Task<WorkpaperPlan> CreatePlanAsync(
        SqliteProjectDatabase database,
        WorkpaperContext context)
    {
        var savedScenarios = await new LocalFilterScenarioStore(database)
            .ListAsync(context.ProjectId, CancellationToken.None);
        var scenario = Assert.Single(savedScenarios);
        Assert.Equal(1, scenario.Position);
        var initial = JetAuditProgram.Plan(new WorkpaperRequest(
            context.ProjectId,
            context.PeriodStart,
            context.PeriodEnd,
            context.LastPeriodStart,
            context.MoneyScale,
            context.ValidationRunId,
            context.ScenarioRevision,
            [
                new WorkpaperScenarioSelection(
                    scenario.Position,
                    scenario.Name,
                    scenario.Rationale)
            ],
            context.PopulationScope));
        var facts = await JetAuditProgram.ExecuteAsync(
            initial,
            new WorkpaperPlanningFactsPort(
                new LocalCompletenessDiffPageRepository(database),
                new LocalDocBalancePageRepository(database),
                new LocalTagMatrixScenariosRepository(database),
                new LocalFieldDefinitionFactsPort(database),
                new LocalMappingStateStore(database)),
            CancellationToken.None);
        return JetAuditProgram.Finalize(initial, facts);
    }

    private static WorkpaperWriter CreateWriter(
        SqliteProjectDatabase database,
        ITagMatrixRowPageRepository tagMatrixRows,
        int progressRowInterval = 10_000) =>
        new(
            new LocalCompletenessAccountPageRepository(database),
            new LocalCompletenessDiffPageRepository(database),
            new LocalDocBalancePageRepository(database),
            new LocalCreatorSummaryExportRepository(database),
            new LocalInfSamplePageRepository(database),
            new LocalFilterScenarioStore(database),
            new LocalTagMatrixScenariosRepository(database),
            new LocalTagMatrixVoucherPageRepository(database),
            tagMatrixRows,
            new LocalMappingStateStore(database),
            new LocalCalendarExportRepository(database),
            new LocalAccountMappingExportRepository(database),
            new WorkpaperWriterOptions(),
            progressRowInterval,
            new LocalRawGlExportRepository(database));

    private static WorkpaperContext ReplacementContext(
        WorkpaperContext context) =>
        context with
        {
            CompanyName = "WorkingPaper 置換版本公司"
        };

    private static ReportArtifactWriteRequest ReplacementRequest(
        IWorkpaperPlanWriter writer,
        WorkpaperContext context,
        WorkpaperPlan plan,
        Action<WorkpaperProgress>? progress = null) =>
        new(
            ReportArtifactKind.WorkingPaper,
            new ReportArtifactSourceRefs(
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
        PreparedCase prepared,
        bool recoverPendingMutation = false)
    {
        var snapshot = prepared.Snapshot;
        Assert.Equal(
            snapshot.WorkbookBytes,
            await File.ReadAllBytesAsync(
                snapshot.ArtifactPath,
                CancellationToken.None));
        Assert.Equal(
            snapshot.ManifestBytes,
            await File.ReadAllBytesAsync(
                Path.Combine(
                    prepared.ProjectDirectory,
                    ProjectReportArtifactStore.ManifestFileName),
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
        AssertArtifactIdentityEqual(snapshot.Artifact, current);
        AssertNoTemporaryState(prepared.ProjectDirectory);
    }

    private static void AssertNoTemporaryState(string projectDirectory)
    {
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

    private sealed class ThrowAfterFirstStep41RowRepository(
        LocalTagMatrixRowPageRepository inner)
        : ITagMatrixRowPageRepository, IWorkpaperStep41PreparedSessionFactory
    {
        internal int RowsRead { get; private set; }

        internal WorkpaperStep41PreparedSessionMetrics? Metrics { get; private set; }

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

        async Task<IWorkpaperStep41PreparedSession>
            IWorkpaperStep41PreparedSessionFactory.PrepareAsync(
                string projectId,
                GlPopulationContext context,
                IReadOnlyList<int> scenarioPositions,
                LegacyFieldKind lineItemKind,
                CancellationToken cancellationToken)
        {
            var session = await ((IWorkpaperStep41PreparedSessionFactory)inner)
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
            IWorkpaperStep41PreparedSession inner,
            ThrowAfterFirstStep41RowRepository owner)
            : IWorkpaperStep41PreparedSession
        {
            public WorkpaperStep41PreparedSessionMetrics Metrics => inner.Metrics;

            public async IAsyncEnumerable<WorkpaperStep41SourceRow> ReadRowsAsync(
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
        LocalTagMatrixRowPageRepository inner)
        : ITagMatrixRowPageRepository, IWorkpaperStep41PreparedSessionFactory
    {
        internal WorkpaperStep41PreparedSessionMetrics? Metrics { get; private set; }

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

        public async Task<IWorkpaperStep41PreparedSession> PrepareAsync(
            string projectId,
            GlPopulationContext context,
            IReadOnlyList<int> scenarioPositions,
            LegacyFieldKind lineItemKind,
            CancellationToken cancellationToken)
        {
            var session = await ((IWorkpaperStep41PreparedSessionFactory)inner)
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
        LocalTagMatrixRowPageRepository inner,
        CancellationTokenSource cancellation)
        : ITagMatrixRowPageRepository, IWorkpaperStep41PreparedSessionFactory
    {
        internal WorkpaperStep41PreparedSessionMetrics? Metrics { get; private set; }

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

        public async Task<IWorkpaperStep41PreparedSession> PrepareAsync(
            string projectId,
            GlPopulationContext context,
            IReadOnlyList<int> scenarioPositions,
            LegacyFieldKind lineItemKind,
            CancellationToken cancellationToken)
        {
            var session = await ((IWorkpaperStep41PreparedSessionFactory)inner)
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
        LocalTagMatrixRowPageRepository inner,
        CancellationTokenSource cancellation)
        : ITagMatrixRowPageRepository, IWorkpaperStep41PreparedSessionFactory
    {
        internal int RowsRead { get; private set; }

        internal WorkpaperStep41PreparedSessionMetrics? Metrics { get; private set; }

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

        public async Task<IWorkpaperStep41PreparedSession> PrepareAsync(
            string projectId,
            GlPopulationContext context,
            IReadOnlyList<int> scenarioPositions,
            LegacyFieldKind lineItemKind,
            CancellationToken cancellationToken)
        {
            var session = await ((IWorkpaperStep41PreparedSessionFactory)inner)
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
            IWorkpaperStep41PreparedSession inner,
            CancelAfterFirstPreparedRowRepository owner,
            CancellationTokenSource cancellation)
            : IWorkpaperStep41PreparedSession
        {
            public WorkpaperStep41PreparedSessionMetrics Metrics => inner.Metrics;

            public async IAsyncEnumerable<WorkpaperStep41SourceRow> ReadRowsAsync(
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

    private sealed record CommittedArtifactSnapshot(
        ReportArtifact Artifact,
        string ArtifactPath,
        byte[] WorkbookBytes,
        byte[] ManifestBytes,
        string[] WorkbookPaths);

    private sealed class PreparedCase(
        HandlerTestHost host,
        string projectId,
        string projectDirectory,
        SqliteProjectDatabase database,
        ProjectReportArtifactStore store,
        WorkpaperContext context,
        WorkpaperPlan plan,
        CommittedArtifactSnapshot snapshot) : IDisposable
    {
        internal string ProjectId { get; } = projectId;

        internal string ProjectDirectory { get; } = projectDirectory;

        internal SqliteProjectDatabase Database { get; } = database;

        internal ProjectReportArtifactStore Store { get; } = store;

        internal WorkpaperContext Context { get; } = context;

        internal WorkpaperPlan Plan { get; } = plan;

        internal CommittedArtifactSnapshot Snapshot { get; } = snapshot;

        public void Dispose() => host.Dispose();
    }
}
