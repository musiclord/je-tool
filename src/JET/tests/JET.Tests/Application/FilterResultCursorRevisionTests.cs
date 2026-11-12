using System.Text.Json;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class FilterResultCursorRevisionTests
{
    private const string Definitions = """
        {"scenarios":[
          {"name":"借方","rationale":"合成游標版本","groups":[{"conditions":[{"type":"drCrOnly","drCr":"debit"}]}]},
          {"name":"貸方","rationale":"合成游標版本","groups":[{"conditions":[{"type":"drCrOnly","drCr":"credit"}]}]}
        ]}
        """;

    [Theory]
    [InlineData("sqlite", "selected")]
    [InlineData("sqlite", "source")]
    [InlineData("sqlite", "definition")]
    public async Task Continuation_DoesNotCombinePreviousPageWithChangedResults(string provider, string change)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        var saved = await host.DispatchAsync("filter.commit", Definitions);
        var cursors = new Dictionary<string, string>();
        foreach (var action in new[] { "query.filterHitsPage", "query.highRiskSummaryVoucherPage", "query.highRiskSummaryRowPage" })
        {
            var first = await host.DispatchAsync(action, Request(action));
            var cursor = first.GetProperty("nextCursor").GetString();
            Assert.False(string.IsNullOrWhiteSpace(cursor));
            cursors.Add(action, cursor!);
        }

        if (change == "definition")
        {
            await host.DispatchAsync("filter.commit", Definitions.Replace("借方", "借方更新", StringComparison.Ordinal));
        }
        else
        {
            // 上游修改會清除下游：行事曆修改會清掉已存情境，審計員要重新儲存同一組情境；
            // 重新儲存會重算全部情境，因此沒有「只重算選定情境、全案仍待重跑」的狀態，filter 一律不是待重跑。
            // 仍要確認上游修改前取得的游標不能接到新結果。
            await host.DispatchAsync("calendar.setNonWorkingDays", """{"days":[6]}""");
            saved = await host.DispatchAsync("filter.commit", Definitions);
            if (change == "selected")
            {
                var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));
                await host.DispatchAsync("export.workingPaperStream", JsonSerializer.Serialize(new
                {
                    validationResultId = loaded.GetProperty("validationResult")
                        .GetProperty("resultRef").GetProperty("resultId").GetString(),
                    scenarioRevision = saved.GetProperty("resultRef").GetProperty("revision").GetString(),
                    scenarioPositions = new[] { 1 }
                }));
            }
            else
            {
                await host.DispatchAsync("query.highRiskSummaryScenarios");
            }
        }
        var after = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));
        Assert.False(after.GetProperty("staleState").GetProperty("filter").GetBoolean());

        foreach (var (action, cursor) in cursors)
        {
            var exception = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(action, Request(action, cursor)));
            Assert.Equal(JetErrorCodes.StaleResult, exception.Code);
            Assert.Contains("第一頁", exception.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("sqlite")]
    public async Task HighRiskSummaryContinuation_AfterLastScenarioIsRemoved_RequiresFirstPage(string provider)
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await host.DispatchAsync("filter.commit", Definitions);
        var cursors = new Dictionary<string, string>();
        foreach (var action in new[] { "query.highRiskSummaryVoucherPage", "query.highRiskSummaryRowPage" })
        {
            var first = await host.DispatchAsync(action, Request(action));
            var cursor = first.GetProperty("nextCursor").GetString();
            Assert.False(string.IsNullOrWhiteSpace(cursor));
            cursors.Add(action, cursor!);
        }
        await host.DispatchAsync("filter.commit", """{"scenarios":[]}""");

        foreach (var (action, cursor) in cursors)
        {
            var exception = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(action, Request(action, cursor)));
            Assert.Equal(JetErrorCodes.StaleResult, exception.Code);
            Assert.Contains("第一頁", exception.Message, StringComparison.Ordinal);
            var first = await host.DispatchAsync(action, Request(action));
            Assert.Equal(0, first.GetProperty("rows").GetArrayLength());
            Assert.Equal(JsonValueKind.Null, first.GetProperty("nextCursor").ValueKind);
        }
    }

    private static string Request(string action, string? cursor = null) => JsonSerializer.Serialize(new
    {
        scenarioPosition = action == "query.filterHitsPage" ? (int?)1 : null,
        pageSize = 1,
        cursor
    });
}
