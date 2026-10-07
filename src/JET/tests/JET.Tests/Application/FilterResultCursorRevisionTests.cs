using System.Text.Json;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class FilterResultCursorRevisionTests
{
    private const string Definitions = """
        {"scenarios":[
          {"name":"借方","rationale":"合成游標版本","groups":[{"rules":[{"type":"drCrOnly","drCr":"debit"}]}]},
          {"name":"貸方","rationale":"合成游標版本","groups":[{"rules":[{"type":"drCrOnly","drCr":"credit"}]}]}
        ]}
        """;

    [Theory]
    [InlineData("sqlite", "selected")]
    [InlineData("duckdb", "selected")]
    [InlineData("sqlite", "source")]
    [InlineData("duckdb", "source")]
    [InlineData("sqlite", "definition")]
    [InlineData("duckdb", "definition")]
    public async Task Continuation_DoesNotCombinePreviousPageWithChangedResults(string provider, string change)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        var saved = await host.DispatchAsync("filter.commit", Definitions);
        var cursors = new Dictionary<string, string>();
        foreach (var action in new[] { "query.filterHitsPage", "query.tagMatrixVoucherPage", "query.tagMatrixRowPage" })
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
            // 使用者 2026-10-07 裁定上游修改清除下游：行事曆修改會清掉已存情境，審計員要重新儲存同一組情境；
            // 重新儲存會重算全部情境，因此不再有「只重算選定情境、全案仍待重跑」的狀態，filter 一律不是待重跑。
            // 仍要確認上游修改前取得的游標不能接到新結果。第一次失敗收據 20261007-032952783-7147565fec1c42be845dac22acf7263b。
            await host.DispatchAsync("calendar.setNonWorkingDays", """{"days":[6]}""");
            saved = await host.DispatchAsync("filter.commit", Definitions);
            if (change == "selected")
            {
                var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));
                await host.DispatchAsync("export.workpaperStream", JsonSerializer.Serialize(new
                {
                    validationRunId = loaded.GetProperty("latestRuns").GetProperty("validate")
                        .GetProperty("resultRef").GetProperty("runId").GetString(),
                    scenarioRevision = saved.GetProperty("resultRef").GetProperty("revision").GetString(),
                    scenarioPositions = new[] { 1 }
                }));
            }
            else
            {
                await host.DispatchAsync("query.tagMatrixScenarios");
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
    [InlineData("duckdb")]
    public async Task MatrixContinuation_AfterLastScenarioIsRemoved_RequiresFirstPage(string provider)
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await host.DispatchAsync("filter.commit", Definitions);
        var cursors = new Dictionary<string, string>();
        foreach (var action in new[] { "query.tagMatrixVoucherPage", "query.tagMatrixRowPage" })
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
