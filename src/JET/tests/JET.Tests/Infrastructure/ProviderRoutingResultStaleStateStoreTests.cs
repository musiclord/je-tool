using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class ProviderRoutingResultStaleStateStoreTests
{
    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider, false, false, true)]
    [InlineData(ProjectDocument.SqlServerDatabaseProvider, false, true, false)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider, true, false, false)]
    public async Task ReadAsync_RoutesToProjectProvider(
        string provider,
        bool expectedValidation,
        bool expectedPrescreen,
        bool expectedFilter)
    {
        var router = new ProviderRoutingResultStaleStateStore(
            new ProjectProviderResolver(new StubProjectStore(ProjectWith(provider))),
            new FixedStore(new AuditResultStaleState(false, false, true)),
            new FixedStore(new AuditResultStaleState(false, true, false)),
            new FixedStore(new AuditResultStaleState(true, false, false)));

        var result = await router.ReadAsync("p1", CancellationToken.None);

        Assert.Equal(
            new AuditResultStaleState(expectedValidation, expectedPrescreen, expectedFilter),
            result);
    }

    private static ProjectDocument ProjectWith(string provider) => new(
        ProjectId: "p1",
        ProjectCode: "C",
        EntityName: "E",
        OperatorId: "o",
        PeriodStart: "2025-01-01",
        PeriodEnd: "2025-12-31",
        LastAccountingPeriodDate: null,
        MoneyScale: ProjectDocument.DefaultMoneyScale,
        RoundingMode: "AwayFromZero",
        CreatedUtc: DateTimeOffset.UnixEpoch,
        CurrentStep: 0,
        SchemaVersion: 1,
        DatabaseProvider: provider);

    private sealed class FixedStore(AuditResultStaleState state) : IResultStaleStateStore
    {
        public Task<AuditResultStaleState> ReadAsync(
            string projectId,
            CancellationToken cancellationToken) => Task.FromResult(state);
    }

    private sealed class StubProjectStore(ProjectDocument document) : IProjectStore
    {
        public Task CreateAsync(ProjectDocument doc, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ProjectDocument>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProjectDocument>>([document]);

        public Task<ProjectDocument?> FindAsync(string projectId, CancellationToken cancellationToken) =>
            Task.FromResult<ProjectDocument?>(document);

        public Task SaveAsync(ProjectDocument doc, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeleteAsync(string projectId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
