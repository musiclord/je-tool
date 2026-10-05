using System.Text.Json;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// Characterizes facts, summary publication, and the post-commit navigation milestone.
/// The production port now commits facts-related samples and summary together; these handler doubles
/// preserve exception ordering, while Batch9TransactionBoundaryTests verifies real database rollback.
/// </summary>
public sealed class ValidateRunFailureBoundaryTests
{
    [Fact]
    public async Task MissingGlMapping_WinsBeforeStaleSessionProjectLookup()
    {
        const string projectId = "missing-validation-project";
        // handler 從作用中案件的資料庫組取 repository，替身放進資料庫組後再進入 session。
        var session = new ProjectSession();
        session.Enter(
            projectId,
            TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider) with
            {
                ValidationFacts = new RecordingFactsPort(failure: null),
                SourceQualityPages = new EmptySourceQualityPageRepository(),
                MappingStates = new MissingMappingStore(projectId),
                RuleRuns = new RecordingRunStore(failure: null),
            });
        var projectStore = new MissingProjectStore();
        var handler = new ValidateRunHandler(projectStore, session);

        var error = await Assert.ThrowsAsync<JetActionException>(
            () => handler.HandleAsync(default, CancellationToken.None));

        Assert.Equal(JetErrorCodes.NoTargetData, error.Code);
        // 2026-10-02 整體複審 T4：畫面不再說「提交」欄位配對，改用「完成」；斷言改鎖新句子。
        Assert.Equal(
            // 2026-10-04 第 8 批 Q8：只更新完整提示句，仍驗證 missing mapping 先於案件查找失敗。
            // 第一次失敗：20261004-092023464-13b0a6928d5e400492fbe8a6a24eb69d。
            "尚未確認 GL 欄位配對，請先到第三步按「確認配對」。",
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
    public async Task MilestoneFailure_ReturnsSavedSummaryAndLeavesThePreviousNavigationStep()
    {
        var boundary = Boundary(milestoneFailure: new BoundaryFailure("milestone"));

        // 第9批中低9；Public首敗105344715-0a3f9684995a4a20bcdcb2fb954649f6：已commit後的導航戳記失敗只能記日誌，不能改報資料作業失敗。
        // 原facts、summary及milestone次數與已保存record/舊step斷言全部保留。
        var response = Assert.IsType<JsonElement>(await boundary.Handler.HandleAsync(default, CancellationToken.None));
        Assert.Equal(1, boundary.FactsPort.Calls);
        Assert.Equal(1, boundary.RunStore.SaveCalls);
        Assert.NotNull(boundary.RunStore.LastRecord);
        Assert.Equal(RuleRunKinds.Validate, boundary.RunStore.LastRecord!.RunKind);
        Assert.Equal(boundary.RunStore.LastRecord.RunId, response.GetProperty("resultRef").GetProperty("runId").GetString());
        Assert.Equal(boundary.RunStore.LastRecord.SummaryJson, response.GetRawText());
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
            SampleSeed: 7,
            // 目前版本建案一定寫入 INF 抽樣種子與版本；缺欄位的文件會被當成舊版案件拒絕。
            SampleSeedVersion: JetAuditProgram.CurrentInfSamplingAlgorithmVersion);
        var runStore = new RecordingRunStore(summaryFailure);
        // 第9批中低9：summary保存改由正式facts port在callback之後執行；原各階段例外與呼叫次數斷言保留。
        var factsPort = new RecordingFactsPort(factsFailure, runStore);
        var projectStore = new RecordingProjectStore(document, milestoneFailure);
        // handler 從作用中案件的資料庫組取 repository，替身放進資料庫組後再進入 session。
        var session = new ProjectSession();
        session.Enter(
            document.ProjectId,
            TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider) with
            {
                ValidationFacts = factsPort,
                SourceQualityPages = new EmptySourceQualityPageRepository(),
                MappingStates = new FixedMappingStore(document.ProjectId),
                RuleRuns = runStore,
            });
        var handler = new ValidateRunHandler(projectStore, session);

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

    private sealed class RecordingFactsPort(BoundaryFailure? failure, IRuleRunStore? runStore = null) : IValidationFactsPort
    {
        internal int Calls { get; private set; }

        public async Task<RuleRunRecord> ExecuteAsync(
            ValidationPlan plan,
            Func<ValidationFacts, RuleRunRecord> finalize,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (failure is not null)
            {
                throw failure;
            }

            var facts = new ValidationFacts(
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
                // 第9批R10；Public首敗100911120：空母體替身必須明示已測得0/0，讓原本summary及milestone失敗邊界仍被測到。
                AmountBinCounts: [], DocumentDateReuse: new DocumentDateReuseCounts(0, 0), SourceQualitySampleRows: []);
            var record = finalize(facts);
            await (runStore ?? throw new InvalidOperationException("Missing summary store.")).SaveAsync(
                plan.Request.ProjectId, record, cancellationToken);
            return record;
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
        public Task<IReadOnlyList<ProjectStoreEntry>> ListEntriesAsync(CancellationToken cancellationToken) =>
            ProjectStoreTestEntries.FromAsync(ListAsync(cancellationToken));
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
        public Task<IReadOnlyList<ProjectStoreEntry>> ListEntriesAsync(CancellationToken cancellationToken) =>
            ProjectStoreTestEntries.FromAsync(ListAsync(cancellationToken));
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
