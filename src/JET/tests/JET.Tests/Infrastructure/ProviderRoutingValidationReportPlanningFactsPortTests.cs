using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class ProviderRoutingValidationReportPlanningFactsPortTests
{
    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider, "sqlite")]
    [InlineData(ProjectDocument.SqlServerDatabaseProvider, "sqlServer")]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider, "duckdb")]
    public async Task ExecuteAsync_RoutesExactPlanToProjectProvider(
        string provider,
        string expected)
    {
        var sqlite = new RecordingPort("sqlite");
        var sqlServer = new RecordingPort("sqlServer");
        var duckDb = new RecordingPort("duckdb");
        var router = new ProviderRoutingValidationReportPlanningFactsPort(
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
            _ => throw new InvalidOperationException(expected)
        };
        Assert.Same(selected.Result, result);
        Assert.Same(plan, selected.Plan);
        Assert.Equal(expected == "sqlite" ? 1 : 0, sqlite.Calls);
        Assert.Equal(expected == "sqlServer" ? 1 : 0, sqlServer.Calls);
        Assert.Equal(expected == "duckdb" ? 1 : 0, duckDb.Calls);
    }

    [Theory]
    [InlineData("postgres", JetErrorCodes.UnsupportedProvider)]
    [InlineData(null, JetErrorCodes.ProjectNotFound)]
    public async Task ExecuteAsync_InvalidResolution_UsesExistingProviderErrors(
        string? provider,
        string expectedCode)
    {
        var router = new ProviderRoutingValidationReportPlanningFactsPort(
            new ProjectProviderResolver(new StubProjectStore(
                provider is null ? null : ProjectWith(provider))),
            new RecordingPort("sqlite"),
            new RecordingPort("sqlServer"),
            new RecordingPort("duckdb"));

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            router.ExecuteAsync(Plan("p1"), CancellationToken.None));

        Assert.Equal(expectedCode, exception.Code);
    }

    private static ValidationReportPlan Plan(string projectId) =>
        JetAuditProgram.Plan(new ValidationReportRequest(
            ProjectId: projectId,
            PeriodStart: "2025-01-01",
            PeriodEnd: "2025-12-31",
            CompletenessDiffAccountCount: 0,
            NullAccountCount: 0,
            NullDocumentCount: 0,
            NullDescriptionCount: 0,
            OutOfRangeDateCount: 0));

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
        SchemaVersion: ProjectDocument.CurrentSchemaVersion,
        DatabaseProvider: provider);

    private sealed class RecordingPort(string name)
        : IValidationReportPlanningFactsPort
    {
        public int Calls { get; private set; }

        public ValidationReportPlan? Plan { get; private set; }

        public ValidationReportPlanningFacts Result { get; } =
            new(UnbalancedDetailRowCount: name.Length);

        public Task<ValidationReportPlanningFacts> ExecuteAsync(
            ValidationReportPlan plan,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Plan = plan;
            return Task.FromResult(Result);
        }
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
