using System.Text.Json;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// filter.commit 命中落地驗收（plan 子專案 D1 Task 2）：filter.commit 保存情境後，
/// 必須把每個情境的命中 entry_id 落地到 result_filter_run，且筆數等於獨立 SQL recount；
/// 任何重投影（GL re-commit）使該表隨結果失效集一併清空。
///
/// 設計技術：狀態轉換 + metamorphic recount oracle。
///   - oracle：result_filter_run 命中數 == 對 target_gl_entry 的獨立 recount。
///     採 backdatedPosting 情境（過帳日早於傳票日；demo 母體確有命中），其述詞為純 ANSI
///     `voucher_date IS NOT NULL AND post_date &lt; voucher_date`（見 GlRulePredicates.Backdated）。
///     recount 以相同述詞獨立重跑（非以 C# 重實作規則），故為有效 oracle。
///   - 失效不變量：重投影後 result_filter_run 為空（與 result_rule_run/抽樣同失效集）。
/// 每個測試自建 host（會變更狀態，不可共用 DemoProjectFixture）。
/// </summary>
public sealed class FilterRunMaterializeTests
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
                    new { join = "and", type = "prescreen", prescreenKey = "backdatedPosting" } } } }
            } }
        });

    [Fact]
    public async Task FilterCommit_MaterializesHitEntryIds_MatchingRecount()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);

        await host.DispatchAsync("filter.commit", ScenarioPayload("提前過帳"));

        var hits = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT COUNT(*) FROM result_filter_run WHERE scenario_position = 1;");
        var recount = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId, BackdatedRecount);

        Assert.True(hits > 0); // 母體確有命中，避免 0==0 假性通過
        Assert.Equal(recount, hits);
    }

    [Fact]
    public async Task Reprojection_ClearsResultFilterRun()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("filter.commit", ScenarioPayload("提前過帳"));
        Assert.True(await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT COUNT(*) FROM result_filter_run;") > 0);

        // 重投影（GL re-commit，重投影交易；失效集隨之清除 result_filter_run）。
        await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(
                ctx.Demo.GetProperty("gl").GetProperty("mapping").GetRawText()),
            amountMode = ctx.Demo.GetProperty("gl").GetProperty("amountMode").GetString()
        }));

        Assert.Equal(0, await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT COUNT(*) FROM result_filter_run;"));
    }
}
