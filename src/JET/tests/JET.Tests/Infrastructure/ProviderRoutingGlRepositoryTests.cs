using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// ProviderRoutingGlRepository 的路由語意(無 DB,純委派驗證)。依專案 databaseProvider
/// 把 GL 投影導向對應實作;決策表:sqlite / sqlServer / duckdb / 未知 / 找不到專案。
/// </summary>
public sealed class ProviderRoutingGlRepositoryTests
{
    private static readonly GlMappingSpec AnySpec = new(new Dictionary<string, string>(), GlAmountMode.SignedAmount);

    private static Task<ProjectionResult> ProjectAsync(
        IGlRepository repository,
        string projectId) =>
        repository.ProjectStagingToTargetAsync(
            projectId,
            "b1",
            AnySpec,
            10_000,
            DateParseOptions.Default,
            new DateOnly(2025, 1, 1),
            new DateOnly(2025, 12, 31),
            postingStatusMapped: false,
            postingStatusPolicy: null,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);

    [Theory]
    [InlineData("sqlite", "sqlite")]
    [InlineData("sqlServer", "sqlServer")]
    [InlineData("duckdb", "duckdb")]
    public async Task Routes_ToProviderMatchingProjectDocument(string provider, string expected)
    {
        var sqlite = new RecordingGlRepository("sqlite");
        var sqlServer = new RecordingGlRepository("sqlServer");
        var duckDb = new RecordingGlRepository("duckdb");
        var router = new ProviderRoutingGlRepository(
            new ProjectProviderResolver(new StubProjectStore(ProjectWith(provider))), sqlite, sqlServer, duckDb);

        await ProjectAsync(router, "p1");

        Assert.Equal(expected == "sqlite" ? 1 : 0, sqlite.Calls);
        Assert.Equal(expected == "sqlServer" ? 1 : 0, sqlServer.Calls);
        Assert.Equal(expected == "duckdb" ? 1 : 0, duckDb.Calls);
    }

    [Fact]
    public async Task UnknownProvider_ThrowsUnsupportedProvider()
    {
        var router = new ProviderRoutingGlRepository(
            new ProjectProviderResolver(new StubProjectStore(ProjectWith("postgres"))),
            new RecordingGlRepository("sqlite"),
            new RecordingGlRepository("sqlServer"),
            new RecordingGlRepository("duckdb"));

        var ex = await Assert.ThrowsAsync<JetActionException>(() =>
            ProjectAsync(router, "p1"));

        Assert.Equal("unsupported_provider", ex.Code);
    }

    [Fact]
    public async Task MissingProject_ThrowsProjectNotFound()
    {
        var router = new ProviderRoutingGlRepository(
            new ProjectProviderResolver(new StubProjectStore(null)),
            new RecordingGlRepository("sqlite"),
            new RecordingGlRepository("sqlServer"),
            new RecordingGlRepository("duckdb"));

        var ex = await Assert.ThrowsAsync<JetActionException>(() =>
            ProjectAsync(router, "missing"));

        Assert.Equal(JetErrorCodes.ProjectNotFound, ex.Code);
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
        SchemaVersion: 1,
        DatabaseProvider: provider);

    private sealed class RecordingGlRepository(string name) : IGlRepository
    {
        public int Calls { get; private set; }

        public string Name => name;

        public Task<ProjectionResult> ProjectStagingToTargetAsync(
            string projectId, string batchId, GlMappingSpec spec, int moneyScale,
            DateParseOptions dateOptions, DateOnly periodStart, DateOnly periodEnd,
            bool postingStatusMapped, GlPostingStatusPolicy? postingStatusPolicy,
            DateTimeOffset committedUtc, CancellationToken cancellationToken,
            Action<ProjectionProgress>? progress = null)
        {
            Calls++;
            return Task.FromResult(new ProjectionResult(0, []));
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
