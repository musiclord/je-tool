using System.Text.Json;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 情境名稱在三種資料庫共用 400 字上限（使用者 2026-10-19 選「三種共用 400 字上限 (Recommended)」）。
/// 剛好 400 字時三種資料庫都完整存回；超過時儲存回錯誤，寫出上限，不在 SQL Server 默默截斷成 400 字。
/// </summary>
public sealed class ScenarioNameLimitProviderTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ScenarioName_AtLimitRoundTrips_AndOverLimitIsRejected(string provider)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await AssertNameLimitAsync(host, context.ProjectId);
    }

    [SqlServerFact]
    public async Task ScenarioName_AtLimitRoundTrips_AndOverLimitIsRejected_SqlServer()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return;
        }

        using var host = new HandlerTestHost(sqlServerConnectionString: connectionString);
        string? projectId = null;
        try
        {
            projectId = (await DemoProjectPipeline.SetupAsync(host, databaseProvider: "sqlServer")).ProjectId;
            await AssertNameLimitAsync(host, projectId);
        }
        finally
        {
            if (projectId is not null)
            {
                await TempSqlServerProject.DropDatabaseAsync(connectionString, projectId);
            }
        }
    }

    private static async Task AssertNameLimitAsync(HandlerTestHost host, string projectId)
    {
        // 400 字，最後一個字不同，截斷時一定看得出來。
        var atLimit = new string('名', 399) + "尾";
        await host.DispatchAsync("filter.commit", Commit(atLimit));
        await host.DispatchAsync("project.releaseLock");
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        var saved = Assert.Single(loaded.GetProperty("filterScenarios").EnumerateArray());
        Assert.Equal(atLimit, saved.GetProperty("name").GetString());

        var overLimit = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("filter.commit", Commit(atLimit + "多")));
        Assert.Equal(JetErrorCodes.InvalidScenario, overLimit.Code);
        Assert.Contains("情境名稱不可超過 400 字", overLimit.Message, StringComparison.Ordinal);
    }

    private static string Commit(string name) => JsonSerializer.Serialize(new
    {
        scenarios = new[]
        {
            new
            {
                name,
                rationale = "合成",
                groups = new[]
                {
                    new
                    {
                        join = "and",
                        rules = new[] { new { join = "and", type = "prescreen", prescreenKey = "nonAuthorizedPreparer" } }
                    }
                }
            }
        }
    });
}
