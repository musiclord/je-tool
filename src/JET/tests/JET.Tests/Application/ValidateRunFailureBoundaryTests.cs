using System.Text.Json;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// Characterizes the existing validate.run persistence boundary. Provider work,
/// summary persistence, and milestone persistence are deliberately three stages;
/// these tests must not be interpreted as a cross-store atomicity guarantee.
/// </summary>
public sealed class ValidateRunFailureBoundaryTests
{
    [Fact]
    public async Task MissingGlMapping_WinsBeforeStaleSessionProjectLookup()
    {
        const string projectId = "missing-validation-project";
        var session = new ProjectSession();
        session.Enter(projectId);
        var projectStore = new MissingProjectStore();
        var handler = new ValidateRunHandler(
            new RecordingFactsPort(failure: null),
            new EmptySourceQualityPageRepository(),
            new MissingMappingStore(projectId),
            new RecordingRunStore(failure: null),
            projectStore,
            session);

        var error = await Assert.ThrowsAsync<JetActionException>(
            () => handler.HandleAsync(default, CancellationToken.None));

        Assert.Equal(JetErrorCodes.NoTargetData, error.Code);
        Assert.Equal(
            "尚未提交 GL 欄位配對（無投影資料），請先完成欄位配對步驟。",
            error.Message);
        Assert.Equal(0, projectStore.FindCalls);
    }

    [Fact]
    public async Task FactsFailure_DoesNotAttemptSummaryOrMilestoneSave()
    {
        var boundary = Boundary(factsFailure: new BoundaryFailure("facts"));

        var error = await Assert.ThrowsAsync<BoundaryFailure>(
            () => boundary.Handler.HandleAsync(default, CancellationToken.None));

        Assert.Equal("facts", error.Stage);
        Assert.Equal(1, boundary.FactsPort.Calls);
        Assert.Equal(0, boundary.RunStore.SaveCalls);
        Assert.Equal(0, boundary.ProjectStore.SaveCalls);
    }

    [Fact]
    public async Task SummaryFailure_HappensAfterFactsAndBeforeMilestoneSave()
    {
        var boundary = Boundary(summaryFailure: new BoundaryFailure("summary"));

        var error = await Assert.ThrowsAsync<BoundaryFailure>(
            () => boundary.Handler.HandleAsync(default, CancellationToken.None));

        Assert.Equal("summary", error.Stage);
        Assert.Equal(1, boundary.FactsPort.Calls);
        Assert.Equal(1, boundary.RunStore.SaveCalls);
        Assert.Equal(0, boundary.ProjectStore.SaveCalls);
    }

    [Fact]
    public async Task MilestoneFailure_LeavesAlreadySavedSummaryObservable()
    {
        var boundary = Boundary(milestoneFailure: new BoundaryFailure("milestone"));

        var error = await Assert.ThrowsAsync<BoundaryFailure>(
            () => boundary.Handler.HandleAsync(default, CancellationToken.None));

        Assert.Equal("milestone", error.Stage);
        Assert.Equal(1, boundary.FactsPort.Calls);
        Assert.Equal(1, boundary.RunStore.SaveCalls);
        Assert.NotNull(boundary.RunStore.LastRecord);
        Assert.Equal(RuleRunKinds.Validate, boundary.RunStore.LastRecord!.RunKind);
        Assert.Equal(1, boundary.ProjectStore.SaveCalls);
        Assert.Equal(3, boundary.ProjectStore.Document.CurrentStep);
    }

    [Fact]
    public async Task MissingControlTotals_RendersBothPartAPopulationsAndMatchesAsNull()
    {
        var boundary = Boundary();

        var response = Assert.IsType<JsonElement>(
            await boundary.Handler.HandleAsync(default, CancellationToken.None));
        var partA = response.GetProperty("completenessTest").GetProperty("partA");

        Assert.Equal(JsonValueKind.Null, partA.GetProperty("eligibleSource").ValueKind);
        Assert.Equal(JsonValueKind.Null, partA.GetProperty("effectiveTarget").ValueKind);
        Assert.Equal(JsonValueKind.Null, partA.GetProperty("rowCountMatch").ValueKind);
        Assert.Equal(JsonValueKind.Null, partA.GetProperty("amountMatch").ValueKind);
    }

    private static BoundaryContext Boundary(
        BoundaryFailure? factsFailure = null,
        BoundaryFailure? summaryFailure = null,
        BoundaryFailure? milestoneFailure = null)
    {
        var document = new ProjectDocument(
            ProjectId: "validation-boundary",
            ProjectCode: "VALIDATION-BOUNDARY",
            EntityName: "驗證失敗邊界公司",
            OperatorId: "tester",
            PeriodStart: "2025-01-01",
            PeriodEnd: "2025-12-31",
            LastAccountingPeriodDate: null,
            MoneyScale: ProjectDocument.DefaultMoneyScale,
            RoundingMode: ProjectDocument.DefaultRoundingMode,
            CreatedUtc: DateTimeOffset.UnixEpoch,
            CurrentStep: 3,
            SchemaVersion: ProjectDocument.CurrentSchemaVersion,
            SampleSeed: 7);
        var session = new ProjectSession();
        session.Enter(document.ProjectId);

        var factsPort = new RecordingFactsPort(factsFailure);
        var runStore = new RecordingRunStore(summaryFailure);
        var projectStore = new RecordingProjectStore(document, milestoneFailure);
        var handler = new ValidateRunHandler(
            factsPort,
            new EmptySourceQualityPageRepository(),
            new FixedMappingStore(document.ProjectId),
            runStore,
            projectStore,
            session);

        return new BoundaryContext(handler, factsPort, runStore, projectStore);
    }

    private sealed record BoundaryContext(
        ValidateRunHandler Handler,
        RecordingFactsPort FactsPort,
        RecordingRunStore RunStore,
        RecordingProjectStore ProjectStore);

    private sealed class BoundaryFailure(string stage) : Exception(stage)
    {
        internal string Stage { get; } = stage;
    }

    private sealed class RecordingFactsPort(BoundaryFailure? failure) : IValidationFactsPort
    {
        internal int Calls { get; private set; }

        public Task<ValidationFacts> ExecuteAsync(
            ValidationPlan plan,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (failure is not null)
            {
                throw failure;
            }

            return Task.FromResult(new ValidationFacts(
                new GlPopulationSummary(
                    new GlRawPopulationTotals(0, 0, 0),
                    new ValidationEffectivePopulationTotals(0, 0, 0, 0, 0),
                    new GlExcludedPopulationTotals(0, 0, 0)),
                CompletenessDiffAccountCount: 0,
                CompletenessDiffAccounts: [],
                UnbalancedDocumentCount: 0,
                InfSampleCount: 0,
                NullAccountCount: 0,
                NullDocumentCount: 0,
                NullDescriptionCount: 0,
                OutOfRangeDateCount: 0,
                SourceQualityFindingCount: 0,
                UnbalancedDocuments: [],
                NullRecordRows: [],
                ControlTotals: null,
                AmountBinCounts: []));
        }
    }

    private sealed class EmptySourceQualityPageRepository : ISourceQualityPageRepository
    {
        public Task<PageResult<SourceQualityFindingRow>> GetPageAsync(
            string projectId,
            PageRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PageResult<SourceQualityFindingRow>([], null));
    }

    private sealed class FixedMappingStore(string projectId) : IMappingStateStore
    {
        private static readonly IReadOnlyDictionary<string, string> EmptyMapping =
            new Dictionary<string, string>();

        public Task SaveAsync(
            string projectId,
            CommittedMapping mapping,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CommittedMapping?> FindAsync(
            string requestedProjectId,
            DatasetKind kind,
            CancellationToken cancellationToken)
        {
            Assert.Equal(projectId, requestedProjectId);
            return Task.FromResult<CommittedMapping?>(new CommittedMapping(
                kind,
                EmptyMapping,
                "test",
                "batch",
                DateTimeOffset.UnixEpoch));
        }
    }

    private sealed class MissingMappingStore(string projectId) : IMappingStateStore
    {
        public Task SaveAsync(
            string projectId,
            CommittedMapping mapping,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CommittedMapping?> FindAsync(
            string requestedProjectId,
            DatasetKind kind,
            CancellationToken cancellationToken)
        {
            Assert.Equal(projectId, requestedProjectId);
            return Task.FromResult<CommittedMapping?>(null);
        }
    }

    private sealed class RecordingRunStore(BoundaryFailure? failure) : IRuleRunStore
    {
        internal int SaveCalls { get; private set; }

        internal RuleRunRecord? LastRecord { get; private set; }

        public Task SaveAsync(
            string projectId,
            RuleRunRecord record,
            CancellationToken cancellationToken)
        {
            SaveCalls++;
            if (failure is not null)
            {
                throw failure;
            }

            LastRecord = record;
            return Task.CompletedTask;
        }

        public Task<RuleRunRecord?> FindLatestAsync(
            string projectId,
            string runKind,
            CancellationToken cancellationToken) =>
            Task.FromResult(LastRecord);
    }

    private sealed class RecordingProjectStore(
        ProjectDocument document,
        BoundaryFailure? failure) : IProjectStore
    {
        internal ProjectDocument Document { get; private set; } = document;

        internal int SaveCalls { get; private set; }

        public Task CreateAsync(
            ProjectDocument document,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ProjectDocument>> ListAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProjectDocument>>([Document]);

        public Task<ProjectDocument?> FindAsync(
            string projectId,
            CancellationToken cancellationToken) =>
            Task.FromResult<ProjectDocument?>(Document);

        public Task SaveAsync(
            ProjectDocument document,
            CancellationToken cancellationToken)
        {
            SaveCalls++;
            if (failure is not null)
            {
                throw failure;
            }

            Document = document;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            string projectId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class MissingProjectStore : IProjectStore
    {
        internal int FindCalls { get; private set; }

        public Task CreateAsync(
            ProjectDocument document,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ProjectDocument>> ListAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProjectDocument?> FindAsync(
            string projectId,
            CancellationToken cancellationToken)
        {
            FindCalls++;
            return Task.FromResult<ProjectDocument?>(null);
        }

        public Task SaveAsync(
            ProjectDocument document,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(
            string projectId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
