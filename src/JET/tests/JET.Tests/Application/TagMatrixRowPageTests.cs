using System.Globalization;
using System.Linq;
using System.Text.Json;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// query.tagMatrixRowPage 驗收(plan 子專案 D2 Task 4)。
/// 每測試自建 host(filter.commit 改狀態,不可共用唯讀 fixture)。
///
/// 設計技術:狀態轉換(commit 多情境 → 走訪行層矩陣)+ 獨立 recount oracle(以 SQL 重算,
/// 非以 C# 重實作規則,故為有效 oracle)。
///   - 列集 oracle:命中傳票(任一行入 result_filter_run)之**所有行**(含非命中行)以 entry_id 升冪。
///     即 SELECT entry_id FROM target_gl_entry WHERE document_number IN (命中傳票集),不限於命中行。
///   - matchedPositions oracle:每行(以 entry_id)的 DISTINCT scenario_position 升冪;非命中行為空 []。
///   - 重點:命中傳票內**未命中任何情境的行**必須列出且 matchedPositions==[](獨立 recount 證實)。
///   - 兩段查詢正確性:小 pageSize 跨頁走訪,斷言走訪集 == 全集 recount(不漏)、單一升冪 entry_id
///     無重複(不溢)、且每行位置與 recount 逐筆相等(查詢 2 鍵範圍與查詢 1 一致)。
/// </summary>
public sealed class TagMatrixRowPageTests
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

    /// <summary>
    /// 只命中「借方行」的單一情境(drCrOnly=debit):每張平衡傳票含借/貸兩側,故命中傳票內的
    /// **貸方行必為非命中行**——可靠製造「命中傳票內存在非命中行」,讓非命中行斷言不依賴 demo 巧合
    /// (相對於 amount>=0 這種會命中全部行的情境)。
    /// </summary>
    private static string DebitOnlyPayload() =>
        JsonSerializer.Serialize(new
        {
            scenarios = new object[]
            {
                new {
                    name = "借方行", rationale = "行層子集命中情境",
                    groups = new[] { new { join = "and", rules = new object[] {
                        new { join = "and", type = "drCrOnly", drCr = "debit" } } } } }
            }
        });

    private sealed record WalkedRow(long EntryId, decimal Amount, int[] MatchedPositions);

    /// <summary>逐頁帶 nextCursor 走訪 query.tagMatrixRowPage 到底,回傳串接後的列(含本頁回算 entry_id)。</summary>
    private static async Task<List<WalkedRow>> WalkAsync(
        HandlerTestHost host, string projectId, int pageSize)
    {
        // wire 不含 entry_id,但測試需以 entry_id 做 oracle 對位。改以「文件號+列號」唯一定位回查 entry_id。
        var all = new List<WalkedRow>();
        string? cursor = null;
        do
        {
            var page = await host.DispatchAsync("query.tagMatrixRowPage",
                JsonSerializer.Serialize(new { cursor, pageSize }));
            foreach (var r in page.GetProperty("rows").EnumerateArray())
            {
                var doc = r.GetProperty("documentNumber").GetString();
                var lineItem = r.GetProperty("lineItem").GetString();
                var entryId = await ResolveEntryIdAsync(host, projectId, doc, lineItem);
                all.Add(new WalkedRow(
                    entryId,
                    r.GetProperty("amount").GetDecimal(),
                    r.GetProperty("matchedPositions").EnumerateArray().Select(p => p.GetInt32()).ToArray()));
            }

            var nextCursor = page.GetProperty("nextCursor");
            cursor = nextCursor.ValueKind == JsonValueKind.Null ? null : nextCursor.GetString();
        } while (cursor is not null);

        return all;
    }

    /// <summary>以 (document_number, line_item) 回查唯一 entry_id(demo GL 每傳票各列號唯一)。</summary>
    private static async Task<long> ResolveEntryIdAsync(
        HandlerTestHost host, string projectId, string? doc, string? lineItem)
    {
        var ids = await DemoProjectPipeline.QueryStringListAsync(host, projectId,
            "SELECT entry_id FROM target_gl_entry " +
            "WHERE document_number IS @doc AND line_item IS @line ORDER BY entry_id;",
            ("@doc", (object?)doc ?? DBNull.Value), ("@line", (object?)lineItem ?? DBNull.Value));
        Assert.Single(ids);
        return long.Parse(ids[0]!, CultureInfo.InvariantCulture);
    }

    private static async Task<List<long>> QueryLongListAsync(
        HandlerTestHost host, string projectId, string sql, params (string, object)[] parameters)
    {
        var raw = await DemoProjectPipeline.QueryStringListAsync(host, projectId, sql, parameters);
        return raw.Select(s => long.Parse(s!, CultureInfo.InvariantCulture)).ToList();
    }

    [Fact]
    public async Task Walk_HitVoucherAllRows_IncludingNonMatchedRows_EqualIndependentRecount()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("filter.commit", ThreeScenarioPayload());

        // 小 pageSize 強制跨頁(走到查詢 1 keyset 非首頁分支 + 查詢 2 的 (@lo,@hi] 範圍)。
        const int pageSize = 9;
        var walked = await WalkAsync(host, ctx.ProjectId, pageSize);

        // 列集 oracle:命中傳票之所有行(含非命中行)= entry_id IN (傳票屬命中傳票集) 升冪。
        var expectedEntryIds = await QueryLongListAsync(host, ctx.ProjectId,
            "SELECT g.entry_id FROM target_gl_entry g " +
            "WHERE g.document_number IN ( " +
            "    SELECT DISTINCT g2.document_number FROM result_filter_run r " +
            "    JOIN target_gl_entry g2 ON g2.entry_id = r.entry_id " +
            "    WHERE g2.document_number IS NOT NULL) " +
            "ORDER BY g.entry_id;");

        var walkedEntryIds = walked.Select(w => w.EntryId).ToList();

        // 跨頁須真有多頁(>pageSize)才證明 keyset 兩段查詢分頁,而非單頁偽綠。
        Assert.True(walkedEntryIds.Count > pageSize,
            $"母體須跨頁(>{pageSize} 行)才能證明 keyset 分頁,實得 {walkedEntryIds.Count}。");

        // 不漏:走訪集 == 全集 recount(值＋順序)。
        Assert.Equal(expectedEntryIds, walkedEntryIds);

        // 不溢:單一升冪、無重複。
        Assert.Equal(walkedEntryIds.OrderBy(id => id).ToList(), walkedEntryIds);
        Assert.Equal(walkedEntryIds.Distinct().Count(), walkedEntryIds.Count);

        // 每行:matchedPositions == DISTINCT scenario_position recount;amount == amount_scaled recount。
        foreach (var row in walked)
        {
            var expectedPositions = (await QueryLongListAsync(host, ctx.ProjectId,
                    "SELECT scenario_position FROM result_filter_run " +
                    "WHERE entry_id = @id ORDER BY scenario_position;",
                    ("@id", row.EntryId)))
                .Select(p => (int)p).ToArray();
            Assert.Equal(expectedPositions, row.MatchedPositions);

            var amountScaled = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
                "SELECT amount_scaled FROM target_gl_entry WHERE entry_id = @id;",
                ("@id", row.EntryId));
            Assert.Equal((decimal)amountScaled / DemoMoneyScale, row.Amount);
        }

        // 0 命中情境(position 3)不應出現在任何行的 matchedPositions。
        Assert.DoesNotContain(walked, w => w.MatchedPositions.Contains(3));
    }

    [Fact]
    public async Task Walk_NonMatchedRowsInHitVouchers_AreListed_WithEmptyPositions()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);

        // 只 commit 行層子集情境(借方行),確保命中傳票內存在「未命中該情境」的貸方行。
        await host.DispatchAsync("filter.commit", DebitOnlyPayload());

        var walked = await WalkAsync(host, ctx.ProjectId, 200);
        Assert.NotEmpty(walked);

        // 獨立 recount:命中傳票之所有行裡,哪些 entry_id 不在 result_filter_run(=非命中行)。
        var nonMatchedEntryIds = (await QueryLongListAsync(host, ctx.ProjectId,
            "SELECT g.entry_id FROM target_gl_entry g " +
            "WHERE g.document_number IN ( " +
            "    SELECT DISTINCT g2.document_number FROM result_filter_run r " +
            "    JOIN target_gl_entry g2 ON g2.entry_id = r.entry_id " +
            "    WHERE g2.document_number IS NOT NULL) " +
            "  AND NOT EXISTS (SELECT 1 FROM result_filter_run rr WHERE rr.entry_id = g.entry_id) " +
            "ORDER BY g.entry_id;")).ToHashSet();

        // 本測試的價值前提:命中傳票內確實存在非命中行(否則「非命中行」斷言空轉)。
        Assert.True(nonMatchedEntryIds.Count > 0,
            "demo 命中傳票內應存在未命中該情境的行,否則非命中行覆蓋無效。");

        var walkedByEntry = walked.ToDictionary(w => w.EntryId);

        // 每個非命中行:必須列出(在走訪集),且 matchedPositions 必為空 []。
        foreach (var entryId in nonMatchedEntryIds)
        {
            Assert.True(walkedByEntry.ContainsKey(entryId),
                $"命中傳票內的非命中行 entry_id={entryId} 必須列出。");
            Assert.Empty(walkedByEntry[entryId].MatchedPositions);
        }

        // 反向:命中行的 matchedPositions 必非空(證明 [] 非全域行為)。
        var matchedEntryIds = (await QueryLongListAsync(host, ctx.ProjectId,
            "SELECT DISTINCT entry_id FROM result_filter_run ORDER BY entry_id;")).ToHashSet();
        Assert.True(matchedEntryIds.Count > 0);
        foreach (var entryId in matchedEntryIds)
        {
            Assert.True(walkedByEntry.ContainsKey(entryId));
            Assert.NotEmpty(walkedByEntry[entryId].MatchedPositions);
        }
    }

    [Fact]
    public async Task NoHits_ReturnsEmptyPage_AndNullCursor()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);

        // 未 commit 任何情境 → result_filter_run 空 → 行層矩陣空頁、無位置。
        var page = await host.DispatchAsync("query.tagMatrixRowPage");
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

        // 首頁取回應觸發惰性補算 → 回命中傳票之所有行。
        var walked = await WalkAsync(host, ctx.ProjectId, 200);
        Assert.True(walked.Count > 0, "惰性補算後應有命中傳票之行");

        var expectedCount = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry g WHERE g.document_number IN ( " +
            "    SELECT DISTINCT g2.document_number FROM result_filter_run r " +
            "    JOIN target_gl_entry g2 ON g2.entry_id = r.entry_id " +
            "    WHERE g2.document_number IS NOT NULL);");
        Assert.Equal(expectedCount, walked.Count);
    }

    [Fact]
    public async Task MalformedCursor_ThrowsInvalidPayload()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);

        var ex = await Assert.ThrowsAsync<JetActionException>(() =>
            host.DispatchAsync("query.tagMatrixRowPage",
                JsonSerializer.Serialize(new { cursor = "!!!not-base64!!!" })));
        Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
    }
}
