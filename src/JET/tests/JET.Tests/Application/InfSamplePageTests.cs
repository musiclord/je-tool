using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// query.infSamplePage 走訪驗收(plan 子專案 D1 Task 5)。
/// 每測試自建 host(validate.run 改狀態,不可共用唯讀 fixture)。
///
/// 設計技術:狀態轉換(validate.run 落地樣本 → 走訪)+ recount oracle。
///   - oracle:走訪到底的筆數 == result_inf_sampling_test_sample 最新 run 的列數
///     == validate.run 回傳 infSamplingTest.sampleSize(demo 母體 ≥59 → 取 59)。
///   - 小 pageSize 逼非首頁分支;斷言借/貸/日期/人員顯示欄存在(manifest 形狀)。
///   - 最新 run 限定:跑兩次 validate.run 後走訪筆數仍 == 單次 sampleSize(不累積跨 run)。
/// </summary>
public sealed class InfSamplePageTests
{
    [Fact]
    public async Task WalkAllPages_EqualsLatestRunSample_WithDisplayColumns()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host, runValidation: false);
        var validate = await host.DispatchAsync("validate.run");
        var sampleSize = validate.GetProperty("infSamplingTest").GetProperty("sampleSize").GetInt64();
        Assert.True(sampleSize > 0); // demo 母體確有樣本,避免空跑

        var keys = new List<string>();
        var firstRow = default(JsonElement);
        var sawRow = false;
        string? cursor = null;
        do
        {
            var page = await host.DispatchAsync("query.infSamplePage",
                JsonSerializer.Serialize(new { cursor, pageSize = 25 }));
            foreach (var r in page.GetProperty("rows").EnumerateArray())
            {
                if (!sawRow) { firstRow = r.Clone(); sawRow = true; }
                keys.Add($"{r.GetProperty("documentNumber").GetString()}|{r.GetProperty("accountCode").GetString()}|{r.GetProperty("postDate").GetString()}");
            }

            var nc = page.GetProperty("nextCursor");
            cursor = nc.ValueKind == JsonValueKind.Null ? null : nc.GetString();
        } while (cursor is not null);

        var sampleRows = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT COUNT(*) FROM result_inf_sampling_test_sample;");

        // 無漏無重由「走訪筆數 == 表列數 == 單次 run sampleSize」鎖住:keyset 以唯一 entry_id 升冪推進,
        // 不可能重訪;故計數三方相等即證明逐頁無漏無重(不另以粗業務鍵驗 distinct——同票多列會共用
        // doc|account|date,該鍵本就非唯一)。
        Assert.Equal(sampleSize, sampleRows);
        Assert.Equal(sampleSize, keys.Count);

        // 借/貸/日期/人員顯示欄存在(拆欄 + ToDisplay)。
        Assert.True(sawRow);
        Assert.True(firstRow.TryGetProperty("debit", out _));
        Assert.True(firstRow.TryGetProperty("credit", out _));
        Assert.True(firstRow.TryGetProperty("approvalDate", out _));
        Assert.True(firstRow.TryGetProperty("createdBy", out _));
        Assert.True(firstRow.TryGetProperty("approvedBy", out _));
    }

    [Fact]
    public async Task WalkAllPages_AfterSecondValidateRun_LimitsToLatestRun()
    {
        // result_inf_sampling_test_sample 以 (run_id, entry_id) 跨 run 累積(validate.run 不清表);
        // infSamplePage 須限定最新一次 validate run → 走訪筆數仍 == 單次 sampleSize,而非累積兩倍。
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host, runValidation: false);
        var first = await host.DispatchAsync("validate.run");
        var sampleSize = first.GetProperty("infSamplingTest").GetProperty("sampleSize").GetInt64();
        await host.DispatchAsync("validate.run"); // 第二次 → 表內累積兩個 run_id

        var accumulated = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT COUNT(*) FROM result_inf_sampling_test_sample;");
        Assert.Equal(sampleSize * 2, accumulated); // 釘住「表確實累積」這個前提

        var walked = 0;
        string? cursor = null;
        do
        {
            var page = await host.DispatchAsync("query.infSamplePage",
                JsonSerializer.Serialize(new { cursor, pageSize = 200 }));
            walked += page.GetProperty("rows").EnumerateArray().Count();
            var nc = page.GetProperty("nextCursor");
            cursor = nc.ValueKind == JsonValueKind.Null ? null : nc.GetString();
        } while (cursor is not null);

        Assert.Equal(sampleSize, walked); // 只回最新 run,不爆兩倍
    }
}
