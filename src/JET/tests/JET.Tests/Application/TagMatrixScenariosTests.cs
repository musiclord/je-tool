using System.Linq;
using System.Text.Json;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// query.tagMatrixScenarios 驗收(plan 子專案 D2 Task 2)。
/// 每測試自建 host(filter.commit 改狀態,不可共用唯讀 fixture)。
///
/// 設計技術:狀態轉換(commit 多情境 → 摘要)+ metamorphic recount oracle。
///   - oracle:每個 position 的 voucherHitCount == result_filter_run JOIN target_gl_entry 的
///     COUNT(DISTINCT document_number) 獨立 recount;rowHitCount == result_filter_run 的
///     COUNT(*) 獨立 recount(非以 C# 重實作規則,故為有效 oracle)。
///   - 0 命中情境:commit 一個刻意打不中(amount ≥ 天價)的情境,斷言其仍列出且兩 count 皆 0。
///   - 惰性補算:清空 result_filter_run 後不重 commit 直接呼叫,仍能算出與 recount 相等。
/// </summary>
public sealed class TagMatrixScenariosTests
{
    /// <summary>命中(backdatedPosting)+ 命中(廣域金額)+ 0 命中(天價金額)三情境,position 1/2/3。</summary>
    private static string ThreeScenarioPayload() =>
        JsonSerializer.Serialize(new
        {
            scenarios = new object[]
            {
                new {
                    name = "提前過帳", rationale = "命中情境",
                    groups = new[] { new { join = "and", rules = new object[] {
                        new { join = "and", type = "prescreen", prescreenKey = "backdatedPosting" } } } } },
                new {
                    name = "廣域金額", rationale = "命中情境",
                    groups = new[] { new { join = "and", rules = new object[] {
                        new { join = "and", type = "numRange", field = "amount", from = "0" } } } } },
                new {
                    name = "天價(打不中)", rationale = "0 命中情境",
                    groups = new[] { new { join = "and", rules = new object[] {
                        new { join = "and", type = "numRange", field = "amount", from = "999999999999" } } } } }
            }
        });

    private static long PositionOf(JsonElement scenarios, int position) =>
        scenarios.EnumerateArray().Single(s => s.GetProperty("position").GetInt32() == position).GetProperty("voucherHitCount").GetInt64();

    [Fact]
    public async Task Summary_CountsEqualIndependentRecount_AndZeroHitListed()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("filter.commit", ThreeScenarioPayload());

        var data = await host.DispatchAsync("query.tagMatrixScenarios");
        var scenarios = data.GetProperty("scenarios");

        // 三情境全列出、依 position 升冪。
        var positions = scenarios.EnumerateArray().Select(s => s.GetProperty("position").GetInt32()).ToArray();
        Assert.Equal(new[] { 1, 2, 3 }, positions);
        Assert.Equal("提前過帳", scenarios[0].GetProperty("name").GetString());

        // 每個 position 的兩 count == 獨立 ANSI recount(由 result_filter_run 即時算)。
        foreach (var s in scenarios.EnumerateArray())
        {
            var pos = s.GetProperty("position").GetInt32();
            var voucherRecount = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
                "SELECT COUNT(DISTINCT g.document_number) FROM result_filter_run r " +
                "JOIN target_gl_entry g ON g.entry_id = r.entry_id WHERE r.scenario_position = @pos;",
                ("@pos", pos));
            var rowRecount = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
                "SELECT COUNT(*) FROM result_filter_run WHERE scenario_position = @pos;",
                ("@pos", pos));

            Assert.Equal(voucherRecount, s.GetProperty("voucherHitCount").GetInt64());
            Assert.Equal(rowRecount, s.GetProperty("rowHitCount").GetInt64());
        }

        // 命中情境確有命中(避免空跑假性通過)。
        Assert.True(PositionOf(scenarios, 1) > 0);
        Assert.True(PositionOf(scenarios, 2) > 0);

        // 0 命中情境(position 3)仍列出,兩 count 皆 0。
        var zeroHit = scenarios[2];
        Assert.Equal("天價(打不中)", zeroHit.GetProperty("name").GetString());
        Assert.Equal(0, zeroHit.GetProperty("voucherHitCount").GetInt64());
        Assert.Equal(0, zeroHit.GetProperty("rowHitCount").GetInt64());
    }

    [Fact]
    public async Task LazyMaterialize_WhenResultEmptyButScenariosExist_BackfillsThenCounts()
    {
        // 惰性補算:情境定義存在於 config_filter_scenario,但 result_filter_run 被清空
        // (模擬重投影後尚未重跑 filter.commit),tagMatrixScenarios 取摘要前應重用 materializer 補算後回取。
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("filter.commit", ThreeScenarioPayload());

        // 直接清空命中表(definition_json 仍在),模擬「定義在、命中不在」。
        await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "DELETE FROM result_filter_run; SELECT 0;");
        Assert.Equal(0, await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT COUNT(*) FROM result_filter_run;"));

        var data = await host.DispatchAsync("query.tagMatrixScenarios");
        var scenarios = data.GetProperty("scenarios");

        // 補算後 position 1/2 仍命中、與重取後 recount 相等。
        foreach (var s in scenarios.EnumerateArray())
        {
            var pos = s.GetProperty("position").GetInt32();
            var rowRecount = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
                "SELECT COUNT(*) FROM result_filter_run WHERE scenario_position = @pos;",
                ("@pos", pos));
            Assert.Equal(rowRecount, s.GetProperty("rowHitCount").GetInt64());
        }

        Assert.True(PositionOf(scenarios, 1) > 0, "惰性補算後 backdatedPosting 應仍命中");
    }

    [Fact]
    public async Task NoScenarios_ReturnsEmptyList()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);

        var data = await host.DispatchAsync("query.tagMatrixScenarios");
        Assert.Equal(0, data.GetProperty("scenarios").GetArrayLength());
    }
}
