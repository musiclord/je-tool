using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// filter.commit 的整批 commit 邊界：caller token 必須原樣傳入原子 commit port；
/// commit port 回報取消或錯誤時不得把案件步驟推進到 Step 5，也不得改寫原始例外。
/// 真實三 provider 的 definitions + hits rollback 由 Infrastructure transaction tests 證明。
/// </summary>
public sealed class FilterCommitCancellationTests
{
    [Fact]
    public async Task HandleAsync_AtomicCommitCancellation_PropagatesCallerTokenAndDoesNotAdvanceStep()
    {
        using var cancellation = new CancellationTokenSource();
        var document = Document();
        var projectStore = new RecordingProjectStore(document);
        var commitRepository = new RecordingFilterCommitRepository(token =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        var session = new ProjectSession();
        session.Enter(document.ProjectId);
        var handler = new FilterCommitHandler(
            commitRepository,
            new FixedMappingStore(document.ProjectId),
            new EmptyAccountMappingStore(),
            new EmptyAuthorizedPreparerStore(),
            new BuiltInAccountTaxonomyStore(),
            new EligibleValidationRunStore(),
            projectStore,
            session);
        using var payload = ValidPayload();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handler.HandleAsync(payload.RootElement, cancellation.Token));

        Assert.Equal(cancellation.Token, commitRepository.ReceivedToken);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, projectStore.SaveCalls);
        Assert.Equal(4, projectStore.Document.CurrentStep);
    }

    [Fact]
    public async Task HandleAsync_AtomicCommitFailure_PreservesOriginalExceptionAndDoesNotAdvanceStep()
    {
        using var cancellation = new CancellationTokenSource();
        var document = Document();
        var projectStore = new RecordingProjectStore(document);
        var injected = new InjectedFilterCommitException("sentinel commit failure");
        var commitRepository = new RecordingFilterCommitRepository(
            _ => Task.FromException(injected));
        var session = new ProjectSession();
        session.Enter(document.ProjectId);
        var handler = new FilterCommitHandler(
            commitRepository,
            new FixedMappingStore(document.ProjectId),
            new EmptyAccountMappingStore(),
            new EmptyAuthorizedPreparerStore(),
            new BuiltInAccountTaxonomyStore(),
            new EligibleValidationRunStore(),
            projectStore,
            session);
        using var payload = ValidPayload();

        var exception = await Assert.ThrowsAsync<InjectedFilterCommitException>(
            () => handler.HandleAsync(payload.RootElement, cancellation.Token));

        Assert.Same(injected, exception);
        Assert.Equal(cancellation.Token, commitRepository.ReceivedToken);
        Assert.Equal(0, projectStore.SaveCalls);
        Assert.Equal(4, projectStore.Document.CurrentStep);
    }

    private static ProjectDocument Document() => new(
        ProjectId: "cancel-filter-project",
        ProjectCode: "CANCEL-001",
        EntityName: "取消測試公司",
        OperatorId: "tester",
        PeriodStart: "2025-01-01",
        PeriodEnd: "2025-12-31",
        LastAccountingPeriodDate: "2024-12-31",
        MoneyScale: ProjectDocument.DefaultMoneyScale,
        RoundingMode: ProjectDocument.DefaultRoundingMode,
        CreatedUtc: new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero),
        CurrentStep: 4,
        SchemaVersion: ProjectDocument.CurrentSchemaVersion);

    private static JsonDocument ValidPayload() => JsonDocument.Parse(
        """
        {
          "scenarios": [
            {
              "name": "取消測試情境",
              "rationale": "驗證 caller token 傳遞",
              "groups": [
                {
                  "join": "AND",
                  "rules": [
                    { "join": "AND", "type": "drCrOnly", "drCr": "debit" }
                  ]
                }
              ]
            }
          ]
        }
        """);

    private sealed class RecordingFilterCommitRepository(
        Func<CancellationToken, Task> behavior) : IFilterCommitRepository
    {
        public CancellationToken ReceivedToken { get; private set; }

        public Task CommitAsync(
            string projectId,
            IReadOnlyList<FilterCommitItem> items,
            FilterRuleContext context,
            CancellationToken cancellationToken)
        {
            ReceivedToken = cancellationToken;
            return behavior(cancellationToken);
        }
    }

    private sealed class InjectedFilterCommitException(string message) : Exception(message);

    private sealed class EligibleValidationRunStore : IRuleRunStore
    {
        private static readonly RuleRunRecord ValidationRun = new(
            "eligible-validation",
            RuleRunKinds.Validate,
            new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero),
            CurrentValidationSummaryTestData.Create(
                "eligible-validation",
                new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero)));

        public Task SaveAsync(
            string projectId,
            RuleRunRecord record,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RuleRunRecord?> FindLatestAsync(
            string projectId,
            string runKind,
            CancellationToken cancellationToken) =>
            Task.FromResult<RuleRunRecord?>(ValidationRun);
    }

    private sealed class RecordingProjectStore(ProjectDocument document) : IProjectStore
    {
        public ProjectDocument Document { get; private set; } = document;
        public int SaveCalls { get; private set; }

        public Task CreateAsync(ProjectDocument document, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ProjectDocument>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProjectDocument>>([Document]);

        public Task<ProjectDocument?> FindAsync(string projectId, CancellationToken cancellationToken) =>
            Task.FromResult<ProjectDocument?>(Document);

        public Task SaveAsync(ProjectDocument document, CancellationToken cancellationToken)
        {
            Document = document;
            SaveCalls++;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string projectId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>只回內建五類的 taxonomy store：本測試不涉及 custom 分類。</summary>
    private sealed class BuiltInAccountTaxonomyStore : IAccountTaxonomyStore
    {
        public Task<AccountTaxonomySnapshot> ReadAsync(
            string projectId,
            CancellationToken cancellationToken) =>
            Task.FromResult(AccountTaxonomyCatalog.BuiltInSnapshot);

        public Task<AccountTaxonomySnapshot> SaveAsync(
            string projectId,
            int expectedRevision,
            IReadOnlyList<AccountTaxonomyCategory> categories,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class EmptyAccountMappingStore : IAccountMappingStore
    {
        public Task<AccountMappingImportResult> ImportAsync(
            string projectId,
            ImportSourceDescriptor source,
            IReadOnlyList<string> columns,
            IAsyncEnumerable<StagingRow> rows,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AccountMappingState?> FindStateAsync(
            string projectId,
            CancellationToken cancellationToken) =>
            Task.FromResult<AccountMappingState?>(null);
    }

    private sealed class FixedMappingStore(string projectId) : IMappingStateStore
    {
        private readonly CommittedMapping mapping = new(
            DatasetKind.Gl,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GlMappingKeys.DocNum] = "傳票號碼",
                [GlMappingKeys.PostDate] = "傳票日期",
                [GlMappingKeys.AccNum] = "科目代號",
                [GlMappingKeys.AccName] = "科目名稱",
                [GlMappingKeys.Description] = "摘要",
                [GlMappingKeys.Amount] = "金額"
            },
            GlAmountModeNames.Signed,
            "cancel-batch",
            new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero));

        public Task SaveAsync(
            string requestedProjectId,
            CommittedMapping committedMapping,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CommittedMapping?> FindAsync(
            string requestedProjectId,
            DatasetKind kind,
            CancellationToken cancellationToken) =>
            Task.FromResult<CommittedMapping?>(
                string.Equals(requestedProjectId, projectId, StringComparison.Ordinal)
                    && kind == DatasetKind.Gl
                        ? mapping
                        : null);
    }

    private sealed class EmptyAuthorizedPreparerStore : IAuthorizedPreparerStore
    {
        public Task ClearAsync(string projectId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AuthorizedPreparerImportResult> ImportAsync(
            string projectId,
            ImportSourceDescriptor source,
            IReadOnlyList<string> columns,
            IAsyncEnumerable<StagingRow> rows,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<long> CountAsync(string projectId, CancellationToken cancellationToken) =>
            Task.FromResult(0L);

        public Task<AuthorizedPreparerState?> FindStateAsync(
            string projectId,
            CancellationToken cancellationToken) =>
            Task.FromResult<AuthorizedPreparerState?>(null);
    }
}
