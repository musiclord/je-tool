using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>合法零筆與搜尋無結果仍是已完成結果；讀取不應再次取得寫入鎖或重算全部情境。</summary>
public sealed class FilterQueryFreshnessTests
{
    public static TheoryData<string, string> Queries => new()
    {
        { "sqlite", "query.filterHitsPage" },
        { "sqlite", "query.tagMatrixScenarios" },
        { "sqlite", "query.tagMatrixVoucherPage" },
        { "sqlite", "query.tagMatrixRowPage" },
        { "duckdb", "query.filterHitsPage" },
        { "duckdb", "query.tagMatrixScenarios" },
        { "duckdb", "query.tagMatrixVoucherPage" },
        { "duckdb", "query.tagMatrixRowPage" }
    };

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task CurrentZeroHitResult_IsReadableRepeatedlyWhileAnotherOperationOwnsWriteGate(
        string provider, string action)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await host.DispatchAsync("filter.commit", ScenarioPayload("999999999999"));
        Assert.Equal(0, await FilterStaleAsync(host, context.ProjectId, provider));

        using var lease = Assert.IsAssignableFrom<IDisposable>(host.Dispatcher.TryAcquireExecutionLease());
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var page = await host.DispatchAsync(action, QueryPayload(action));
            AssertEmptyResult(action, page);
        }
        Assert.Equal(0, await FilterStaleAsync(host, context.ProjectId, provider));
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task InvalidatedZeroHitResult_RecalculatesOnceThenRemainsReadableWithoutWriteGate(
        string provider, string action)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await host.DispatchAsync("filter.commit", ScenarioPayload("999999999999"));
        await host.DispatchAsync("calendar.setNonWorkingDays", """{"days":[6]}""");
        Assert.Equal(1, await FilterStaleAsync(host, context.ProjectId, provider));

        using (var held = Assert.IsAssignableFrom<IDisposable>(host.Dispatcher.TryAcquireExecutionLease()))
        {
            var error = await Assert.ThrowsAsync<JetActionException>(() =>
                host.DispatchAsync(action, QueryPayload(action)));
            Assert.Equal(JetErrorCodes.OperationInProgress, error.Code);
        }
        Assert.Equal(1, await FilterStaleAsync(host, context.ProjectId, provider));

        AssertEmptyResult(action, await host.DispatchAsync(action, QueryPayload(action)));
        Assert.Equal(0, await FilterStaleAsync(host, context.ProjectId, provider));
        using var lease = Assert.IsAssignableFrom<IDisposable>(host.Dispatcher.TryAcquireExecutionLease());
        AssertEmptyResult(action, await host.DispatchAsync(action, QueryPayload(action)));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task SearchWithNoMatchingRows_DoesNotInvalidateOrRecalculateExistingHits(string provider)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await host.DispatchAsync("filter.commit", ScenarioPayload("0"));
        var hitsBefore = await ScalarAsync(host, context.ProjectId, provider,
            "SELECT COUNT(*) FROM result_filter_run;");
        Assert.True(hitsBefore > 0);

        using var lease = Assert.IsAssignableFrom<IDisposable>(host.Dispatcher.TryAcquireExecutionLease());
        foreach (var action in new[] { "query.filterHitsPage", "query.tagMatrixVoucherPage", "query.tagMatrixRowPage" })
        {
            var page = await host.DispatchAsync(action, QueryPayload(action, "SYNTHETIC-NO-SUCH-VOUCHER"));
            AssertEmptyResult(action, page);
        }
        Assert.Equal(hitsBefore, await ScalarAsync(host, context.ProjectId, provider,
            "SELECT COUNT(*) FROM result_filter_run;"));
        Assert.Equal(0, await FilterStaleAsync(host, context.ProjectId, provider));
    }

    private static Task<long> FilterStaleAsync(HandlerTestHost host, string projectId, string provider) =>
        ScalarAsync(host, projectId, provider,
            "SELECT filter_stale FROM config_result_stale_state WHERE singleton=1;");

    private static async Task<long> ScalarAsync(HandlerTestHost host, string projectId, string provider, string sql)
    {
        var folder = new JetProjectFolder(host.ProjectsRoot);
        ILocalProjectDatabase database = provider == "sqlite"
            ? new SqliteProjectDatabase(folder) : new DuckDbProjectDatabase(folder);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static string ScenarioPayload(string minimumAmount) => JsonSerializer.Serialize(new
    {
        scenarios = new[] { new
        {
            name = "查詢結果狀態", rationale = "合成零筆與搜尋測試",
            groups = new[] { new { rules = new[] { new
            {
                type = "numRange", field = "amount", from = minimumAmount
            } } } }
        } }
    });

    private static string QueryPayload(string action, string? search = null) => JsonSerializer.Serialize(new
    {
        scenarioPosition = action == "query.filterHitsPage" ? (int?)1 : null,
        pageSize = 20, search
    });

    private static void AssertEmptyResult(string action, JsonElement data)
    {
        if (action == "query.tagMatrixScenarios")
        {
            var scenario = Assert.Single(data.GetProperty("scenarios").EnumerateArray());
            Assert.Equal(1, scenario.GetProperty("position").GetInt32());
            Assert.Equal(0, scenario.GetProperty("rowHitCount").GetInt64());
            Assert.Equal(0, scenario.GetProperty("voucherHitCount").GetInt64());
            return;
        }
        Assert.Empty(data.GetProperty("rows").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("nextCursor").ValueKind);
    }
}
