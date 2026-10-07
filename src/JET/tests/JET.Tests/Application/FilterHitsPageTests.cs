using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// query.filterHitsPage 走訪驗收(plan 子專案 D1 Task 5)。
/// 每測試自建 host(filter.commit 改狀態,不可共用唯讀 fixture)。
///
/// 設計技術:狀態轉換(commit → 走訪)+ metamorphic recount oracle。
///   - oracle:走訪到底的行層筆數 == result_filter_run WHERE scenario_position=1 == demo 對
///     target_gl_entry 的獨立 ANSI recount(backdatedPosting 述詞:post_date &lt; voucher_date)。
///     非以 C# 重實作規則,故為有效 oracle。
///   - 保證非空母體:採 backdatedPosting(demo 母體確有命中),避免空跑 0==0 假性通過。
/// </summary>
public sealed class FilterHitsPageTests
{
    private const string BackdatedRecount =
        "SELECT COUNT(*) FROM target_gl_entry g " +
        "WHERE g.voucher_date IS NOT NULL AND g.post_date < g.voucher_date;";

    private static string ScenarioPayload(string name) =>
        JsonSerializer.Serialize(new
        {
            scenarios = new[] { new {
                name, rationale = "test",
                groups = new[] { new { join = "and", rules = new[] {
                    new { join = "and", type = "prescreen", prescreenKey = "backdatedPosting" } } } } } }
        });

    [Fact]
    public async Task WalkAllPages_EqualsHitSet_NoGapNoDup()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("filter.commit", ScenarioPayload("提前過帳"));

        // 小 pageSize 逼非首頁分支(命中數 > 7 → 跨多頁),走訪到底串接 doc|lineItem 驗無重複。
        var keys = new List<string>();
        string? cursor = null;
        do
        {
            var page = await host.DispatchAsync("query.filterHitsPage",
                JsonSerializer.Serialize(new { scenarioPosition = 1, cursor, pageSize = 7 }));
            foreach (var r in page.GetProperty("rows").EnumerateArray())
            {
                keys.Add($"{r.GetProperty("documentNumber").GetString()}|{r.GetProperty("lineItem").GetString()}");
            }

            var nc = page.GetProperty("nextCursor");
            cursor = nc.ValueKind == JsonValueKind.Null ? null : nc.GetString();
        } while (cursor is not null);

        var hits = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT COUNT(*) FROM result_filter_run WHERE scenario_position = 1;");
        var recount = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId, BackdatedRecount);

        Assert.True(hits > 0); // 母體確有命中,避免空跑
        Assert.Equal(recount, hits);
        Assert.Equal(hits, keys.Count);                       // 走訪無漏無多
        Assert.Equal(keys.Count, keys.Distinct().Count());    // 每列唯一(entry_id 游標序穩)
    }

    [Fact]
    public async Task LazyMaterialize_WhenResultEmptyButScenarioExists_BackfillsThenReturns()
    {
        // 惰性補算:情境定義存在於 config_filter_scenario,但 result_filter_run 被清空(模擬重投影後尚未重跑),
        // filterHitsPage 取頁前應重用 materializer 補算後回取——筆數仍等於 recount。
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("filter.commit", ScenarioPayload("提前過帳"));

        // 透過正式流程使結果失效；情境定義保留，不把手動刪表當作產品失效流程。
        // 使用者 2026-10-07 裁定上游修改清除下游後，行事曆修改會連情境一起清掉；改用開案規則升版的情境整批取代。
        // 第一次失敗收據 20261007-032952783-7147565fec1c42be845dac22acf7263b。
        await SavedScenarioReplay.ReplaceLikeRuleUpgradeAsync(host, ctx.ProjectId);
        Assert.Equal(0, await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT COUNT(*) FROM result_filter_run;"));

        var page = await host.DispatchAsync("query.filterHitsPage",
            JsonSerializer.Serialize(new { scenarioPosition = 1, pageSize = 500 }));
        var rowCount = page.GetProperty("rows").EnumerateArray().Count();

        var recount = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId, BackdatedRecount);
        Assert.True(recount > 0);
        Assert.Equal(recount, rowCount);
    }

    [Fact]
    public async Task MissingScenarioPosition_ThrowsActionError()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);

        await Assert.ThrowsAnyAsync<System.Exception>(() => host.DispatchAsync(
            "query.filterHitsPage", JsonSerializer.Serialize(new { pageSize = 200 })));
    }
}
