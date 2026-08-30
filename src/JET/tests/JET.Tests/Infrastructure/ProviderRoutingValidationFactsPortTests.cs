using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Validation typed facts port 的 provider route 只做精確委派：同一個 plan 必須傳到
/// project.json 指定的 provider，且 router 不解讀或改寫 raw facts。
/// </summary>
public sealed class ProviderRoutingValidationFactsPortTests
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
        var router = new ProviderRoutingValidationFactsPort(
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
        var router = new ProviderRoutingValidationFactsPort(
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
        var router = new ProviderRoutingValidationFactsPort(
            new ProjectProviderResolver(new StubProjectStore(null)),
            new RecordingFactsPort("sqlite"),
            new RecordingFactsPort("sqlServer"),
            new RecordingFactsPort("duckdb"));

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            router.ExecuteAsync(Plan("missing"), CancellationToken.None));

        Assert.Equal(JetErrorCodes.ProjectNotFound, exception.Code);
    }

    private static ValidationPlan Plan(string projectId) =>
        JetAuditProgram.Plan(new ValidationRequest(
            ProjectId: projectId,
            HasGlMapping: true,
            HasTbMapping: true,
            PeriodStart: "2025-01-01",
            PeriodEnd: "2025-12-31",
            MoneyScale: 10_000,
            SampleSeed: 48_271,
            RunId: "run",
            GeneratedUtc: DateTimeOffset.UnixEpoch,
            SampleSize: 59));

    private static ValidationFacts EmptyFacts() => new(
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
        AmountBinCounts: []);

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

    private sealed class RecordingFactsPort(string name) : IValidationFactsPort
    {
        public int Calls { get; private set; }

        public ValidationPlan? Plan { get; private set; }

        public ValidationFacts Result { get; } = EmptyFacts();

        public Task<ValidationFacts> ExecuteAsync(
            ValidationPlan plan,
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
