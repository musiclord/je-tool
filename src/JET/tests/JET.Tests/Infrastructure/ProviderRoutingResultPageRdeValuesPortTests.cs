using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class ProviderRoutingResultPageRdeValuesPortTests
{
    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider, "sqlite")]
    [InlineData(ProjectDocument.SqlServerDatabaseProvider, "sqlServer")]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider, "duckdb")]
    public async Task ReadAsync_RoutesOneBoundedBatchToProjectProvider(string provider, string expected)
    {
        var sqlite = new RecordingPort("sqlite");
        var sqlServer = new RecordingPort("sqlServer");
        var duckDb = new RecordingPort("duckdb");
        var router = new ProviderRoutingResultPageRdeValuesPort(
            new ProjectProviderResolver(new StubProjectStore(ProjectWith(provider))),
            sqlite,
            sqlServer,
            duckDb);

        var result = await router.ReadAsync("p1", [10, 20], CancellationToken.None);

        var selected = expected switch
        {
            "sqlite" => sqlite,
            "sqlServer" => sqlServer,
            "duckdb" => duckDb,
            _ => throw new InvalidOperationException(expected)
        };
        Assert.Same(selected.Result, result);
        Assert.Equal([10L, 20L], selected.EntryIds!);
        Assert.Equal(expected == "sqlite" ? 1 : 0, sqlite.Calls);
        Assert.Equal(expected == "sqlServer" ? 1 : 0, sqlServer.Calls);
        Assert.Equal(expected == "duckdb" ? 1 : 0, duckDb.Calls);
    }

    [Fact]
    public async Task ReadAsync_OverPageSize_FailsBeforeProviderResolution()
    {
        var router = new ProviderRoutingResultPageRdeValuesPort(
            new ProjectProviderResolver(new StubProjectStore(null)),
            new RecordingPort("sqlite"),
            new RecordingPort("sqlServer"),
            new RecordingPort("duckdb"));
        var ids = Enumerable.Range(1, PageRequest.MaxPageSize + 1)
            .Select(static value => (long)value)
            .ToArray();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            router.ReadAsync("p1", ids, CancellationToken.None));
    }

    private static ProjectDocument ProjectWith(string provider) => new(
        ProjectId: "p1",
        ProjectCode: "C",
        EntityName: "E",
        OperatorId: "o",
        PeriodStart: "2025-01-01",
        PeriodEnd: "2025-12-31",
        LastAccountingPeriodDate: null,
        MoneyScale: 100,
        RoundingMode: "AwayFromZero",
        CreatedUtc: DateTimeOffset.UnixEpoch,
        CurrentStep: 0,
        SchemaVersion: ProjectDocument.CurrentSchemaVersion,
        DatabaseProvider: provider);

    private sealed class RecordingPort(string name) : IResultPageRdeValuesPort
    {
        public int Calls { get; private set; }
        public IReadOnlyList<long>? EntryIds { get; private set; }
        public IReadOnlyList<ResultPageRdeValue> Result { get; } =
            [new(10, $"rde.{new string('0', 32)}", "text", name, null, null)];

        public Task<IReadOnlyList<ResultPageRdeValue>> ReadAsync(
            string projectId,
            IReadOnlyList<long> entryIds,
            CancellationToken cancellationToken)
        {
            Calls++;
            EntryIds = entryIds;
            return Task.FromResult(Result);
        }
    }

    private sealed class StubProjectStore(ProjectDocument? document) : IProjectStore
    {
        public Task CreateAsync(ProjectDocument doc, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<ProjectDocument>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProjectDocument>>([]);
        public Task<ProjectDocument?> FindAsync(string projectId, CancellationToken cancellationToken) =>
            Task.FromResult(document);
        public Task SaveAsync(ProjectDocument doc, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(string projectId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
