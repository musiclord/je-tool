using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 不截斷的全編製人員彙總查詢(<see cref="ICreatorSummaryExportRepository.FetchAllAsync"/>,E1 step1-2 用)。
/// 鏡射既有 prescreen creator 彙總 SQL,但**去掉 LIMIT 50**:step1-2 需「全名單」。
/// oracle:獨立參數化 SQL recount(<see cref="DemoProjectPipeline.QueryScalarAsync"/>);
/// 斷言鎖「數值＋身分」(每位編製者的筆數、總名單基數),而非弱斷言非空。
/// 直接建 SQLite repo(比照 ProviderParityJourneyTests 直接建 repo),此 task 尚無 handler。
/// </summary>
public sealed class CreatorSummaryExportTests
{
    private static ICreatorSummaryExportRepository SqliteRepo(HandlerTestHost host) =>
        new LocalCreatorSummaryExportRepository(new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot)));

    /// <summary>
    /// 反截斷的決定性證據:刻意造 60 位不同編製者(>舊 LIMIT 50),每位 1 筆。
    /// 全名單必回 60 筆;若仍帶 LIMIT 50 只會回 50 → 紅。這是「不分頁/不截斷」性質的 oracle。
    /// </summary>
    [Fact]
    public async Task FetchAllAsync_DistinctCreatorsBeyondLegacyLimit_ReturnsEveryCreator()
    {
        const int creatorCount = 60; // > 既有 SummaryRowLimit(50)

        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(host, builder =>
        {
            builder.WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "建立人員", "金額", "借方旗標");
            for (var i = 0; i < creatorCount; i++)
            {
                // 每位編製者唯一(P00..P59),各 1 列,借方;科目固定不影響編製者基數。
                builder.AddRow($"JV-{i:000}", "2025-03-05", "1101", "現金", "說明", $"P{i:00}", "100.00", 1);
            }
        });

        var rows = await SqliteRepo(host).FetchAllAsync(
            projectId, "2025-01-01", "2025-12-31", CancellationToken.None);

        // 獨立 recount:distinct created_by 基數。
        var distinct = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId, "SELECT COUNT(DISTINCT created_by) FROM target_gl_entry;");

        Assert.Equal(creatorCount, distinct); // 母體前置確認(造了 60 位)
        Assert.Equal(creatorCount, rows.Count); // 全名單不截斷:60 筆全回(LIMIT 50 會只回 50 → 紅)
        // 身分完整:P00..P59 全在,無遺漏無重複。
        Assert.Equal(
            Enumerable.Range(0, creatorCount).Select(i => $"P{i:00}").OrderBy(s => s, StringComparer.Ordinal),
            rows.Select(r => r.CreatedBy).OrderBy(s => s, StringComparer.Ordinal));
    }

    /// <summary>
    /// 每列數值正確且鎖身分:demo 五位編製者,逐位以獨立 recount 比對筆數/借方/貸方彙總。
    /// 這鎖住「SUM 不被 COUNT 取代、借貸欄不對調」(mutation 思維:換聚合函式或欄即紅)。
    /// </summary>
    [Fact]
    public async Task FetchAllAsync_DemoPopulation_EachRowMatchesIndependentRecount()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        var rows = await SqliteRepo(host).FetchAllAsync(
            context.ProjectId, "2025-01-01", "2025-12-31", CancellationToken.None);

        // 全名單基數 == distinct created_by recount（§2 母體限本期 post_date；export 已本期界定，recount 同口徑）。
        var distinct = await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId,
            "SELECT COUNT(DISTINCT created_by) FROM target_gl_entry " +
            "WHERE post_date >= '2025-01-01' AND post_date <= '2025-12-31';");
        Assert.Equal(distinct, rows.Count);
        Assert.True(rows.Count > 0, "demo 應有編製者");

        foreach (var row in rows)
        {
            var entryCount = await DemoProjectPipeline.QueryScalarAsync(
                host, context.ProjectId,
                "SELECT COUNT(*) FROM target_gl_entry WHERE created_by = @c " +
                "AND post_date >= '2025-01-01' AND post_date <= '2025-12-31';",
                ("@c", row.CreatedBy));
            var debit = await DemoProjectPipeline.QueryScalarAsync(
                host, context.ProjectId,
                "SELECT COALESCE(SUM(debit_amount_scaled), 0) FROM target_gl_entry WHERE created_by = @c " +
                "AND post_date >= '2025-01-01' AND post_date <= '2025-12-31';",
                ("@c", row.CreatedBy));
            var credit = await DemoProjectPipeline.QueryScalarAsync(
                host, context.ProjectId,
                "SELECT COALESCE(SUM(credit_amount_scaled), 0) FROM target_gl_entry WHERE created_by = @c " +
                "AND post_date >= '2025-01-01' AND post_date <= '2025-12-31';",
                ("@c", row.CreatedBy));

            Assert.Equal(entryCount, row.EntryCount);
            Assert.Equal(debit, row.DebitTotalScaled);
            Assert.Equal(credit, row.CreditTotalScaled);
        }
    }

    /// <summary>
    /// 排序鏡射既有 creator 彙總:COUNT(*) DESC, created_by。逐位筆數遞減(同筆數則 created_by 升冪)。
    /// demo 編製者以 voucherIndex % 5 輪派 → 筆數非全等,可證 DESC 主鍵生效。
    /// </summary>
    [Fact]
    public async Task FetchAllAsync_OrdersByEntryCountDescThenCreatedBy()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        var rows = await SqliteRepo(host).FetchAllAsync(
            context.ProjectId, "2025-01-01", "2025-12-31", CancellationToken.None);

        for (var i = 1; i < rows.Count; i++)
        {
            var prev = rows[i - 1];
            var curr = rows[i];
            var ordered = prev.EntryCount > curr.EntryCount
                || (prev.EntryCount == curr.EntryCount
                    && string.CompareOrdinal(prev.CreatedBy, curr.CreatedBy) <= 0);
            Assert.True(ordered,
                $"排序違規:[{prev.CreatedBy}:{prev.EntryCount}] 在 [{curr.CreatedBy}:{curr.EntryCount}] 之前。");
        }
    }
}
