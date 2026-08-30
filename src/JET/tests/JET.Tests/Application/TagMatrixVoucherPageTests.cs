using System.Linq;
using System.Text.Json;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// query.tagMatrixVoucherPage 驗收(plan 子專案 D2 Task 3)。
/// 每測試自建 host(filter.commit 改狀態,不可共用唯讀 fixture)。
///
/// 設計技術:狀態轉換(commit 多情境 → 走訪傳票矩陣)+ 獨立 recount oracle(以 result_filter_run
/// JOIN target_gl_entry 的 ANSI 聚合重算,非以 C# 重實作規則,故為有效 oracle)。
///   - 命中傳票集 oracle:COUNT(DISTINCT document_number) / 升冪 document_number 清單(排除 NULL)。
///   - matchedPositions oracle:每傳票的 DISTINCT scenario_position 升冪清單。
///   - voucherTotal oracle:每傳票 SUM(debit_amount_scaled);demo moneyScale=10000(同 ValidateRun 測試慣例)。
///   - 兩段查詢正確性:小 pageSize 跨頁走訪,斷言走訪集 == 全集 recount(不漏)、單一升冪無重複(不溢)、
///     且每傳票位置與 recount 逐筆相等(查詢 2 鍵範圍與查詢 1 一致)。
/// </summary>
public sealed class TagMatrixVoucherPageTests
{
    private const int DemoMoneyScale = 10_000;

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

    private sealed record WalkedRow(string DocumentNumber, decimal VoucherTotal, int[] MatchedPositions);

    /// <summary>逐頁帶 nextCursor 走訪 query.tagMatrixVoucherPage 到底,回傳串接後的列。</summary>
    private static async Task<List<WalkedRow>> WalkAsync(HandlerTestHost host, int pageSize)
    {
        var all = new List<WalkedRow>();
        string? cursor = null;
        do
        {
            var page = await host.DispatchAsync("query.tagMatrixVoucherPage",
                JsonSerializer.Serialize(new { cursor, pageSize }));
            foreach (var r in page.GetProperty("rows").EnumerateArray())
            {
                all.Add(new WalkedRow(
                    r.GetProperty("documentNumber").GetString()!,
                    r.GetProperty("voucherTotal").GetDecimal(),
                    r.GetProperty("matchedPositions").EnumerateArray().Select(p => p.GetInt32()).ToArray()));
            }

            var nextCursor = page.GetProperty("nextCursor");
            cursor = nextCursor.ValueKind == JsonValueKind.Null ? null : nextCursor.GetString();
        } while (cursor is not null);

        return all;
    }

    [Fact]
    public async Task Walk_HitVoucherSet_PositionsAndTotal_EqualIndependentRecount()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("filter.commit", ThreeScenarioPayload());

        // 小 pageSize 強制跨頁(走到查詢 1 keyset 非首頁分支 + 查詢 2 的 (@lo,@hi] 範圍)。
        const int pageSize = 7;
        var walked = await WalkAsync(host, pageSize);

        // 命中傳票集 oracle:DISTINCT document_number(排除 NULL)升冪。
        var expectedDocs = (await DemoProjectPipeline.QueryStringListAsync(host, ctx.ProjectId,
            "SELECT DISTINCT g.document_number FROM result_filter_run r " +
            "JOIN target_gl_entry g ON g.entry_id = r.entry_id " +
            "WHERE g.document_number IS NOT NULL ORDER BY g.document_number;"))
            .Select(d => d!).ToList();

        var walkedDocs = walked.Select(w => w.DocumentNumber).ToList();

        // 跨頁須真有多頁(>pageSize)才證明 keyset 兩段查詢分頁,而非單頁偽綠。
        Assert.True(walkedDocs.Count > pageSize,
            $"母體須跨頁(>{pageSize} 傳票)才能證明 keyset 分頁,實得 {walkedDocs.Count}。");

        // 不漏:走訪集 == 全集 recount(值＋順序)。
        Assert.Equal(expectedDocs, walkedDocs);

        // 不溢:單一升冪、無重複。
        Assert.Equal(walkedDocs.OrderBy(d => d, StringComparer.Ordinal).ToList(), walkedDocs);
        Assert.Equal(walkedDocs.Distinct().Count(), walkedDocs.Count);

        // 每傳票:matchedPositions == DISTINCT scenario_position recount;voucherTotal == SUM(debit) recount。
        foreach (var row in walked)
        {
            var expectedPositions = (await DemoProjectPipeline.QueryStringListAsync(host, ctx.ProjectId,
                    "SELECT DISTINCT r.scenario_position FROM result_filter_run r " +
                    "JOIN target_gl_entry g ON g.entry_id = r.entry_id " +
                    "WHERE g.document_number = @doc ORDER BY r.scenario_position;",
                    ("@doc", row.DocumentNumber)))
                .Select(s => int.Parse(s!)).ToArray();
            Assert.Equal(expectedPositions, row.MatchedPositions);

            var totalScaled = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
                "SELECT COALESCE(SUM(g.debit_amount_scaled), 0) FROM target_gl_entry g " +
                "WHERE g.document_number = @doc;",
                ("@doc", row.DocumentNumber));
            Assert.Equal((decimal)totalScaled / DemoMoneyScale, row.VoucherTotal);
        }

        // 至少有一張傳票同時命中 position 1 與 2(廣域金額涵蓋全部 → 與 backdatedPosting 交集非空),
        // 證明 matchedPositions 確實是「多情境集合」而非單一值。
        Assert.Contains(walked, w => w.MatchedPositions.Length >= 2);
        // 0 命中情境(position 3)不應出現在任何傳票的 matchedPositions。
        Assert.DoesNotContain(walked, w => w.MatchedPositions.Contains(3));
    }

    [Fact]
    public async Task NoHits_ReturnsEmptyPage_AndNullCursor()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);

        // 未 commit 任何情境 → result_filter_run 空 → 傳票矩陣空頁、無位置。
        var page = await host.DispatchAsync("query.tagMatrixVoucherPage");
        Assert.Equal(0, page.GetProperty("rows").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task LazyMaterialize_WhenResultEmptyButScenariosExist_BackfillsThenPages()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("filter.commit", ThreeScenarioPayload());

        // 清空命中表(definition_json 仍在)→ 模擬「定義在、命中不在」。
        await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "DELETE FROM result_filter_run; SELECT 0;");
        Assert.Equal(0, await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT COUNT(*) FROM result_filter_run;"));

        // 首頁取回應觸發惰性補算 → 回有命中傳票。
        var walked = await WalkAsync(host, 200);
        Assert.True(walked.Count > 0, "惰性補算後應有命中傳票");

        var expectedCount = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT COUNT(DISTINCT g.document_number) FROM result_filter_run r " +
            "JOIN target_gl_entry g ON g.entry_id = r.entry_id WHERE g.document_number IS NOT NULL;");
        Assert.Equal(expectedCount, walked.Count);
    }

    [Fact]
    public async Task MalformedCursor_ThrowsInvalidPayload()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);

        var ex = await Assert.ThrowsAsync<JetActionException>(() =>
            host.DispatchAsync("query.tagMatrixVoucherPage",
                JsonSerializer.Serialize(new { cursor = "!!!not-base64!!!" })));
        Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
    }
}
