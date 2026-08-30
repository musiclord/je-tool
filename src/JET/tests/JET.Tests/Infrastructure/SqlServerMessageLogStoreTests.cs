using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// SQL Server app_message_log 留存修剪（manifest：每專案保留最近 500 則）。
/// BVA：超出上限即剔除最舊資料。真 SQL Server，不 mock DB。
/// </summary>
public sealed class SqlServerMessageLogStoreTests
{
    [SqlServerFact]
    public async Task AppendAsync_BeyondRetainedCount_TrimsOldestKeepsNewest()
    {
        await using var sql = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(sql);

        var store = new SqlServerMessageLogStore(sql.Database);
        var total = SqlServerMessageLogStore.RetainedCount + 3;
        for (var i = 1; i <= total; i++)
        {
            await store.AppendAsync(sql.ProjectId, "info", $"訊息 {i}", CancellationToken.None);
        }

        var recent = await store.GetRecentAsync(
            sql.ProjectId, SqlServerMessageLogStore.RetainedCount + 10, CancellationToken.None);

        // 僅留最近 500 則：最新為「訊息 503」、最舊為「訊息 4」（1–3 被修剪）。
        Assert.Equal(SqlServerMessageLogStore.RetainedCount, recent.Count);
        Assert.Equal($"訊息 {total}", recent[0].Text);
        Assert.Equal("訊息 4", recent[^1].Text);
    }

    [SqlServerFact]
    public async Task GetRecentAsync_MultipleEntries_ReturnsNewestFirstWithinLimitAndFields()
    {
        await using var sql = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(sql);

        var store = new SqlServerMessageLogStore(sql.Database);
        await store.AppendAsync(sql.ProjectId, "info", "第一則", CancellationToken.None);
        await store.AppendAsync(sql.ProjectId, "warn", "第二則", CancellationToken.None);

        var recent = await store.GetRecentAsync(sql.ProjectId, limit: 1, CancellationToken.None);

        Assert.Single(recent);
        Assert.Equal("warn", recent[0].Level);
        Assert.Equal("第二則", recent[0].Text);
        Assert.True(recent[0].OccurredUtc <= DateTimeOffset.UtcNow);
    }

    [SqlServerFact]
    public async Task GetRecentAsync_NoEntries_ReturnsEmpty()
    {
        await using var sql = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(sql);

        var store = new SqlServerMessageLogStore(sql.Database);

        var recent = await store.GetRecentAsync(sql.ProjectId, limit: 10, CancellationToken.None);

        Assert.Empty(recent);
    }
}
