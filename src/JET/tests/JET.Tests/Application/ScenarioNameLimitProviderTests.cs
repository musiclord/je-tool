using System.Text.Json;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 情境名稱上限 400 字。
/// 剛好 400 字時完整存回；超過時儲存回錯誤，寫出上限，不默默截斷成 400 字。
/// </summary>
public sealed class ScenarioNameLimitProviderTests
{
    [Theory]
    [InlineData("sqlite")]
    public async Task ScenarioName_AtLimitRoundTrips_AndOverLimitIsRejected(string provider)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await AssertNameLimitAsync(host, context.ProjectId);
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
                        conditions = new[] { new { join = "and", type = "prescreen", prescreenKey = "nonAuthorizedPreparer" } }
                    }
                }
            }
        }
    });
}
