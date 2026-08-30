using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// app_message_log 留存修剪（manifest：每專案保留最近 500 則）。
/// BVA：恰滿上限不修剪、超出一則即剔除最舊一則。真 SQLite，不 mock。
/// </summary>
public sealed class SqliteMessageLogStoreTests
{
    [Fact]
    public async Task Append_BeyondRetainedCount_TrimsOldestKeepsNewest()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var database = new SqliteProjectDatabase(folder);
        var store = new LocalMessageLogStore(database);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        var total = LocalMessageLogStore.RetainedCount + 3;
        for (var i = 1; i <= total; i++)
        {
            await store.AppendAsync(projectId, "info", $"訊息 {i}", CancellationToken.None);
        }

        var recent = await store.GetRecentAsync(projectId, limit: LocalMessageLogStore.RetainedCount + 10, CancellationToken.None);

        // 僅留最近 500 則：最新為「訊息 503」、最舊為「訊息 4」（1–3 被修剪）
        Assert.Equal(LocalMessageLogStore.RetainedCount, recent.Count);
        Assert.Equal($"訊息 {total}", recent[0].Text);
        Assert.Equal("訊息 4", recent[^1].Text);
    }
}
