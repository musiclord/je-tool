using System.Text.Json;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class PrescreenPageTests
{
    [Fact]
    public async Task WalkAllPages_IsEquivalentAcrossSqliteAndDuckDb()
    {
        using var sqlite = new HandlerTestHost();
        using var duckDb = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(sqlite, databaseProvider: "sqlite");
        await DemoProjectPipeline.SetupAsync(duckDb, databaseProvider: "duckdb");

        var sqliteRows = await WalkRowsAsync(sqlite, PrescreenRuleKeys.BackdatedPosting, pageSize: 3);
        var duckDbRows = await WalkRowsAsync(duckDb, PrescreenRuleKeys.BackdatedPosting, pageSize: 3);

        Assert.NotEmpty(sqliteRows);
        Assert.Equal(sqliteRows, duckDbRows);
    }

    [Fact]
    public async Task WalkAllPages_MatchesPrescreenRunCount_AndEntryIdsAreStrictlyIncreasing()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);
        var run = await host.DispatchAsync("prescreen.run");
        var expected = run.GetProperty("suspiciousKeywords").GetProperty("count").GetInt64();

        string? cursor = null;
        var ids = new List<long>();
        do
        {
            var page = await host.DispatchAsync(
                "query.prescreenPage",
                JsonSerializer.Serialize(new { ruleKey = PrescreenRuleKeys.SuspiciousKeywords, cursor, pageSize = 2 }));
            ids.AddRange(page.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("entryId").GetInt64()));
            cursor = page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null
                ? null
                : page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        Assert.Equal(expected, (long)ids.Count);
        Assert.Equal(ids.OrderBy(id => id).ToArray(), ids);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public async Task UnknownRuleKey_IsRejectedBeforeRepositoryExecution()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);

        using var payload = JsonDocument.Parse("{\"ruleKey\":\"creatorSummary\"}");
        var error = await Assert.ThrowsAsync<JetActionException>(() =>
            host.Dispatcher.DispatchAsync(
                "query.prescreenPage",
                payload.RootElement,
                CancellationToken.None));

        Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
    }

    [Fact]
    public async Task MalformedCursor_IsRejected()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);
        using var payload = JsonDocument.Parse("{\"ruleKey\":\"backdatedPosting\",\"cursor\":\"not-base64\"}");

        var error = await Assert.ThrowsAsync<JetActionException>(() =>
            host.Dispatcher.DispatchAsync(
                "query.prescreenPage", payload.RootElement, CancellationToken.None));

        Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
    }

    private static async Task<List<string>> WalkRowsAsync(
        HandlerTestHost host,
        string ruleKey,
        int pageSize)
    {
        string? cursor = null;
        var rows = new List<string>();
        do
        {
            var page = await host.DispatchAsync(
                "query.prescreenPage",
                JsonSerializer.Serialize(new { ruleKey, cursor, pageSize }));
            rows.AddRange(page.GetProperty("rows").EnumerateArray().Select(row => row.GetRawText()));
            cursor = page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null
                ? null
                : page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        return rows;
    }
}
