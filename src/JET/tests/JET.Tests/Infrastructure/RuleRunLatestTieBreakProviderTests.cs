using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 兩次執行的 generated_utc 相同時，三種資料庫都以 run_id 較大的一筆當最新（2026-10-19 跨資料庫一致性第二階段）。
/// 寫入順序刻意和 run_id 順序相反，依寫入順序挑選的實作會選到另一筆。
/// </summary>
public sealed class RuleRunLatestTieBreakProviderTests
{
    private const string SmallerRunId = "00000000000000000000000000000001";
    private const string LargerRunId = "00000000000000000000000000000002";

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task SameGeneratedUtc_PicksLargerRunId_LocalProviders(string provider)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = provider == ProjectDocument.DuckDbDatabaseProvider
            ? new DuckDbProjectDatabase(folder)
            : new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        await AssertLargerRunIdWinsAsync(new LocalRuleRunStore(database), projectId);
    }

    [SqlServerFact]
    public async Task SameGeneratedUtc_PicksLargerRunId_SqlServer()
    {
        await using var project = Assert.IsType<TempSqlServerProject>(
            await TempSqlServerProject.TryCreateAsync());

        await AssertLargerRunIdWinsAsync(new SqlServerRuleRunStore(project.Database), project.ProjectId);
    }

    private static async Task AssertLargerRunIdWinsAsync(IRuleRunStore store, string projectId)
    {
        var generatedUtc = new DateTimeOffset(2025, 12, 31, 8, 0, 0, TimeSpan.Zero);
        await store.SaveAsync(projectId,
            new RuleRunRecord(LargerRunId, RuleRunKinds.Validate, generatedUtc, """{"run":"larger"}"""),
            CancellationToken.None);
        await store.SaveAsync(projectId,
            new RuleRunRecord(SmallerRunId, RuleRunKinds.Validate, generatedUtc, """{"run":"smaller"}"""),
            CancellationToken.None);

        var latest = await store.FindLatestAsync(projectId, RuleRunKinds.Validate, CancellationToken.None);

        Assert.NotNull(latest);
        Assert.Equal(LargerRunId, latest.RunId);
        Assert.Equal("""{"run":"larger"}""", latest.SummaryJson);
    }
}
