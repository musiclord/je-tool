using System.Text.Json;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// Characterizes facts, summary publication, and the post-commit navigation milestone.
/// The production port now commits facts-related samples and summary together; these handler doubles
/// preserve exception ordering, while WriteFailureRollbackTests verifies real database rollback.
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
                Results = new RecordingRunStore(failure: null),
            });
        var projectStore = new MissingProjectStore();
        var handler = new ValidateRunHandler(projectStore, session);

        var error = await Assert.ThrowsAsync<JetActionException>(
            () => handler.HandleAsync(default, CancellationToken.None));

        Assert.Equal(JetErrorCodes.GlMappingNotConfirmed, error.Code);
        Assert.Equal(
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

        // 已commit後的導航戳記失敗只能記日誌，不能改報資料作業失敗。
        var response = Assert.IsType<JsonElement>(await boundary.Handler.HandleAsync(default, CancellationToken.None));
        Assert.Equal(1, boundary.FactsPort.Calls);
        Assert.Equal(1, boundary.RunStore.SaveCalls);
        Assert.NotNull(boundary.RunStore.LastRecord);
        Assert.Equal(ResultKind.Validation, boundary.RunStore.LastRecord!.Kind);
        Assert.Equal(boundary.RunStore.LastRecord.ResultId, response.GetProperty("resultRef").GetProperty("resultId").GetString());
        Assert.Equal(boundary.RunStore.LastRecord.SummaryJson, response.GetRawText());
        Assert.Equal(1, boundary.ProjectStore.SaveCalls);
        Assert.Equal(3, boundary.ProjectStore.Document.CurrentStep);
    }

    [Fact]
    public async Task MissingControlTotals_RendersBothStoredTotalsPopulationsAndMatchesAsNull()
    {
        var boundary = Boundary();

        var response = Assert.IsType<JsonElement>(
            await boundary.Handler.HandleAsync(default, CancellationToken.None));
        var storedTotals = response.GetProperty("completenessTest").GetProperty("storedTotals");

        Assert.Equal(JsonValueKind.Null, storedTotals.GetProperty("eligibleSource").ValueKind);
        Assert.Equal(JsonValueKind.Null, storedTotals.GetProperty("gl").ValueKind);
        Assert.Equal(JsonValueKind.Null, storedTotals.GetProperty("rowCountMatch").ValueKind);
        Assert.Equal(JsonValueKind.Null, storedTotals.GetProperty("amountMatch").ValueKind);
    }

    private static BoundaryContext Boundary(
        BoundaryFailure? factsFailure = null,
        BoundaryFailure? summaryFailure = null,
        BoundaryFailure? milestoneFailure = null)
    {
        var document = new ProjectDocument(
            ProjectId: "validation-boundary",
            ProjectCode: "VALIDATION-BOUNDARY",
            ClientName: "驗證失敗邊界公司",
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
            // 目前版本建案一定寫入 INF 抽樣種子與版本；缺這兩個欄位的設定檔會被拒絕。
            SampleSeedVersion: JetAuditProgram.CurrentInfSamplingAlgorithmVersion);
        var resultStore = new RecordingRunStore(summaryFailure);
        // summary保存由正式facts port在callback之後執行。
        var factsPort = new RecordingFactsPort(factsFailure, resultStore);
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
                // 驗證會讀 TB 的匯入紀錄來選逐科目比對的原因句，替身組用沒有匯入 TB 的匯入紀錄。
                Imports = new NoTbImportRepository(),
                Results = resultStore,
            });
        var handler = new ValidateRunHandler(projectStore, session);

        return new BoundaryContext(handler, factsPort, resultStore, projectStore);
    }

    private sealed record BoundaryContext(
        ValidateRunHandler Handler,
        RecordingFactsPort FactsPort,
        RecordingRunStore RunStore,
        RecordingProjectStore ProjectStore);

    private sealed class NoTbImportRepository : IImportRepository
    {
        public Task<ImportBatchResult> ReplaceBatchAsync(string projectId, DatasetKind kind,
            IReadOnlyList<ImportSourceInput> sources, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("驗證不應匯入資料。");

        public Task<ImportBatchResult> AppendToBatchAsync(string projectId, DatasetKind kind,
            IReadOnlyList<ImportSourceInput> sources, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("驗證不應匯入資料。");

        public Task<ImportBatchInfo?> GetLatestBatchAsync(string projectId, DatasetKind kind,
            CancellationToken cancellationToken) => Task.FromResult<ImportBatchInfo?>(null);
    }

    private sealed class BoundaryFailure(string stage) : Exception(stage)
    {
        internal string Stage { get; } = stage;
    }

    private sealed class RecordingFactsPort(BoundaryFailure? failure, IResultStore? resultStore = null) : IValidationFactsPort
    {
        internal int Calls { get; private set; }

        public async Task<ResultRecord> ExecuteAsync(
            ValidationPlan plan,
            Func<ValidationFacts, ResultRecord> finalize,
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
                    new ValidationGlTotals(0, 0, 0, 0, 0),
                    new GlExcludedPopulationTotals(0, 0, 0)),
                CompletenessDiffAccountCount: 0,
                CompletenessDiffAccounts: [],
                UnbalancedVoucherCount: 0,
                InfSampleCount: 0,
                BlankAccountNumberCount: 0,
                BlankVoucherNumberCount: 0,
                BlankDescriptionCount: 0,
                ApprovalDateNotInPeriodCount: 0,
                SourceQualityFindingCount: 0,
                UnbalancedVouchers: [],
                BlankRecordRows: [],
                ControlTotals: null,
                // 空母體替身必須明示已測得0/0，讓summary及milestone失敗邊界被測到。
                AmountBinCounts: [], VoucherNumberAcrossGlDates: new VoucherNumberAcrossGlDatesCounts(0, 0), SourceQualitySampleRows: []);
            var record = finalize(facts);
            await (resultStore ?? throw new InvalidOperationException("Missing summary store.")).SaveAsync(
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

    private sealed class RecordingRunStore(BoundaryFailure? failure) : IResultStore
    {
        internal int SaveCalls { get; private set; }

        internal ResultRecord? LastRecord { get; private set; }

        public Task SaveAsync(
            string projectId,
            ResultRecord record,
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

        public Task<ResultRecord?> FindLatestAsync(
            string projectId,
            ResultKind kind,
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
