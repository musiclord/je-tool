using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Prescreen typed facts route 只依 project provider 委派同一個 plan，不解讀或改寫 facts。
/// </summary>
public sealed class ProviderRoutingPrescreenFactsPortTests
{
    [Theory]
    [InlineData("sqlite", "sqlite")]
    [InlineData("sqlServer", "sqlServer")]
    [InlineData("duckdb", "duckdb")]
    public async Task ExecuteAsync_RoutesExactPlanToProjectProvider(
        string provider,
        string expected)
    {
        var sqlite = new RecordingFactsPort("sqlite");
        var sqlServer = new RecordingFactsPort("sqlServer");
        var duckDb = new RecordingFactsPort("duckdb");
        var router = new ProviderRoutingPrescreenFactsPort(
            new ProjectProviderResolver(new StubProjectStore(ProjectWith(provider))),
            sqlite,
            sqlServer,
            duckDb);
        var plan = Plan("p1");

        var result = await router.ExecuteAsync(plan, CancellationToken.None);

        var selected = expected switch
        {
            "sqlite" => sqlite,
            "sqlServer" => sqlServer,
            "duckdb" => duckDb,
            _ => throw new InvalidOperationException($"Unexpected provider '{expected}'.")
        };
        Assert.Same(plan, selected.Plan);
        Assert.Same(selected.Result, result);
        Assert.Equal(expected == "sqlite" ? 1 : 0, sqlite.Calls);
        Assert.Equal(expected == "sqlServer" ? 1 : 0, sqlServer.Calls);
        Assert.Equal(expected == "duckdb" ? 1 : 0, duckDb.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownProvider_ThrowsUnsupportedProvider()
    {
        var router = new ProviderRoutingPrescreenFactsPort(
            new ProjectProviderResolver(new StubProjectStore(ProjectWith("postgres"))),
            new RecordingFactsPort("sqlite"),
            new RecordingFactsPort("sqlServer"),
            new RecordingFactsPort("duckdb"));

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            router.ExecuteAsync(Plan("p1"), CancellationToken.None));

        Assert.Equal("unsupported_provider", exception.Code);
    }

    [Fact]
    public async Task ExecuteAsync_MissingProject_ThrowsProjectNotFound()
    {
        var router = new ProviderRoutingPrescreenFactsPort(
            new ProjectProviderResolver(new StubProjectStore(null)),
            new RecordingFactsPort("sqlite"),
            new RecordingFactsPort("sqlServer"),
            new RecordingFactsPort("duckdb"));

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            router.ExecuteAsync(Plan("missing"), CancellationToken.None));

        Assert.Equal(JetErrorCodes.ProjectNotFound, exception.Code);
    }

    private static PrescreenPlan Plan(string projectId) =>
        JetAuditProgram.Plan(new PrescreenRequest(
            ProjectId: projectId,
            HasGlMapping: true,
            PeriodStart: "2025-01-01",
            PeriodEnd: "2025-12-31",
            MoneyScale: 10_000,
            SampleSeed: 48_271,
            RunId: "run",
            GeneratedUtc: DateTimeOffset.UnixEpoch,
            LastPeriodStart: "2025-12-31",
            HasApprovalDate: true,
            HasCreatedBy: true,
            HasHolidays: true,
            HasAccountMapping: true,
            HasRevenue: true,
            HasCounterpart: true,
            HasAuthorizedPreparers: true,
            NonWorkingDays: [0, 6]));

    private static PrescreenFacts EmptyFacts() => new(
        PostPeriodApprovalCount: 0,
        SuspiciousKeywordsCount: 0,
        UnexpectedAccountPairCount: 0,
        TrailingZerosCount: 0,
        Creators: [],
        DistinctAccountCount: 0,
        Accounts: [],
        WeekendPostingCount: 0,
        WeekendApprovalCount: 0,
        HolidayPostingCount: 0,
        HolidayApprovalCount: 0,
        BlankDescriptionCount: 0,
        BackdatedPostingCount: 0,
        NonAuthorizedPreparerCount: 0,
        LowFrequencyPreparerCount: 0,
        LowFrequencyAccountCount: 0,
        RuleVoucherCounts: PrescreenRuleKeys.FilterableKeys.ToDictionary(
            key => key,
            _ => 0L,
            StringComparer.Ordinal));

    private static ProjectDocument ProjectWith(string provider) => new(
        ProjectId: "p1",
        ProjectCode: "C",
        EntityName: "E",
        OperatorId: "o",
        PeriodStart: "2025-01-01",
        PeriodEnd: "2025-12-31",
        LastAccountingPeriodDate: null,
        MoneyScale: 10_000,
        RoundingMode: "AwayFromZero",
        CreatedUtc: DateTimeOffset.UnixEpoch,
        CurrentStep: 0,
        SchemaVersion: 1,
        DatabaseProvider: provider);

    private sealed class RecordingFactsPort(string name) : IPrescreenFactsPort
    {
        public int Calls { get; private set; }

        public PrescreenPlan? Plan { get; private set; }

        public PrescreenFacts Result { get; } = EmptyFacts();

        public Task<PrescreenFacts> ExecuteAsync(
            PrescreenPlan plan,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Plan = plan;
            return Task.FromResult(Result);
        }

        public override string ToString() => name;
    }

    private sealed class StubProjectStore(ProjectDocument? document) : IProjectStore
    {
        public Task CreateAsync(ProjectDocument doc, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<ProjectDocument>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProjectDocument>>([]);

        public Task<ProjectDocument?> FindAsync(string projectId, CancellationToken ct) =>
            Task.FromResult(document);

        public Task SaveAsync(ProjectDocument doc, CancellationToken ct) => Task.CompletedTask;

        public Task DeleteAsync(string projectId, CancellationToken ct) => Task.CompletedTask;
    }
}
