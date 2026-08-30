using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class ProviderRoutingFieldDefinitionFactsPortTests
{
    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider, "sqlite")]
    [InlineData(ProjectDocument.SqlServerDatabaseProvider, "sqlServer")]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider, "duckdb")]
    public async Task ReadAsync_RoutesExactRequestToProjectProvider(string provider, string expected)
    {
        var sqlite = new RecordingPort("sqlite");
        var sqlServer = new RecordingPort("sqlServer");
        var duckDb = new RecordingPort("duckdb");
        var router = new ProviderRoutingFieldDefinitionFactsPort(
            new ProjectProviderResolver(new StubProjectStore(ProjectWith(provider))),
            sqlite,
            sqlServer,
            duckDb);

        var result = await router.ReadAsync(
            "p1", DatasetKind.Tb, LegacyFieldDefinitionScope.Target, CancellationToken.None);

        var selected = expected switch
        {
            "sqlite" => sqlite,
            "sqlServer" => sqlServer,
            "duckdb" => duckDb,
            _ => throw new InvalidOperationException(expected)
        };
        Assert.Same(selected.Result, result);
        Assert.Equal(("p1", DatasetKind.Tb, LegacyFieldDefinitionScope.Target), selected.Request);
        Assert.Equal(expected == "sqlite" ? 1 : 0, sqlite.Calls);
        Assert.Equal(expected == "sqlServer" ? 1 : 0, sqlServer.Calls);
        Assert.Equal(expected == "duckdb" ? 1 : 0, duckDb.Calls);
    }

    [Theory]
    [InlineData("postgres", JetErrorCodes.UnsupportedProvider)]
    [InlineData(null, JetErrorCodes.ProjectNotFound)]
    public async Task ReadAsync_InvalidResolution_UsesExistingProviderErrors(
        string? provider,
        string expectedCode)
    {
        var router = new ProviderRoutingFieldDefinitionFactsPort(
            new ProjectProviderResolver(new StubProjectStore(provider is null ? null : ProjectWith(provider))),
            new RecordingPort("sqlite"),
            new RecordingPort("sqlServer"),
            new RecordingPort("duckdb"));

        var exception = await Assert.ThrowsAsync<JetActionException>(() => router.ReadAsync(
            "p1", DatasetKind.Gl, LegacyFieldDefinitionScope.Source, CancellationToken.None));

        Assert.Equal(expectedCode, exception.Code);
    }

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

    private sealed class RecordingPort(string name) : ILegacyFieldDefinitionFactsPort
    {
        public int Calls { get; private set; }
        public (string ProjectId, DatasetKind Kind, LegacyFieldDefinitionScope Scope)? Request { get; private set; }
        public IReadOnlyList<LegacyFieldDefinition> Result { get; } =
            [new(1, name, null, LegacyFieldKind.Text, 0, null)];

        public Task<IReadOnlyList<LegacyFieldDefinition>> ReadAsync(
            string projectId,
            DatasetKind kind,
            LegacyFieldDefinitionScope scope,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Request = (projectId, kind, scope);
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
