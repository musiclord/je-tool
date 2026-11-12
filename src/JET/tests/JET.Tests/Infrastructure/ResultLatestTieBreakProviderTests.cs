using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 兩次執行的 generated_utc 相同時，以 result_id 較大的一筆當最新。
/// 寫入順序刻意和 result_id 順序相反，依寫入順序挑選的實作會選到另一筆。
/// </summary>
public sealed class ResultLatestTieBreakProviderTests
{
    private const string SmallerResultId = "00000000000000000000000000000001";
    private const string LargerResultId = "00000000000000000000000000000002";

    [Fact]
    public async Task SameGeneratedUtc_PicksLargerResultId_LocalProviders()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        // 只有建案會建立案件資料庫，讀寫前的檢查不順便建表，所以照建案的做法先建表。
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);

        await AssertLargerResultIdWinsAsync(new LocalResultStore(database), projectId);
    }

    private static async Task AssertLargerResultIdWinsAsync(IResultStore store, string projectId)
    {
        var generatedUtc = new DateTimeOffset(2025, 12, 31, 8, 0, 0, TimeSpan.Zero);
        await store.SaveAsync(projectId,
            new ResultRecord(LargerResultId, ResultKind.Validation, generatedUtc, """{"run":"larger"}"""),
            CancellationToken.None);
        await store.SaveAsync(projectId,
            new ResultRecord(SmallerResultId, ResultKind.Validation, generatedUtc, """{"run":"smaller"}"""),
            CancellationToken.None);

        var latest = await store.FindLatestAsync(projectId, ResultKind.Validation, CancellationToken.None);

        Assert.NotNull(latest);
        Assert.Equal(LargerResultId, latest.ResultId);
        Assert.Equal("""{"run":"larger"}""", latest.SummaryJson);
    }
}
