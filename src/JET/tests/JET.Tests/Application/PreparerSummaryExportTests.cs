using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 不截斷的全編製人員彙總查詢(<see cref="IPreparerSummaryExportRepository.FetchAllAsync"/>,底稿 step1-2 用)。
/// 鏡射既有 prescreen preparer 彙總 SQL,但**去掉 LIMIT 50**:step1-2 需「全名單」。
/// oracle:獨立參數化 SQL recount(<see cref="DemoProjectPipeline.QueryScalarAsync"/>);
/// 斷言鎖「數值＋身分」(每位編製者的筆數、總名單基數),而非弱斷言非空。
/// 直接建 SQLite repo(比照 ProviderParityJourneyTests 直接建 repo),此 task 尚無 handler。
/// </summary>
public sealed class PreparerSummaryExportTests
{
    private static IPreparerSummaryExportRepository SqliteRepo(HandlerTestHost host) =>
        new LocalPreparerSummaryExportRepository(new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot)));

    /// <summary>
    /// 反截斷的決定性證據:刻意造 60 位不同編製者(>舊 LIMIT 50),每位 1 筆。
    /// 全名單必回 60 筆;若仍帶 LIMIT 50 只會回 50 → 紅。這是「不分頁/不截斷」性質的 oracle。
    /// </summary>
    [Fact]
    public async Task FetchAllAsync_DistinctPreparersBeyondLegacyLimit_ReturnsEveryPreparer()
    {
        const int preparerCount = 60; // > 既有 SummaryRowLimit(50)

        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(host, builder =>
        {
            builder.WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "建立人員", "金額", "借方旗標");
            for (var i = 0; i < preparerCount; i++)
            {
                // 每位編製者唯一(P00..P59),各 1 列,借方;科目固定不影響編製者基數。
                builder.AddRow($"JV-{i:000}", "2025-03-05", "1101", "現金", "說明", $"P{i:00}", "100.00", 1);
            }
        });

        var rows = await SqliteRepo(host).FetchAllAsync(
            projectId, "2025-01-01", "2025-12-31", CancellationToken.None);

        // 獨立 recount:distinct created_by 基數。
        var distinct = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId, "SELECT COUNT(DISTINCT created_by) FROM gl;");

        Assert.Equal(preparerCount, distinct); // 母體前置確認(造了 60 位)
        Assert.Equal(preparerCount, rows.Count); // 全名單不截斷:60 筆全回(LIMIT 50 會只回 50 → 紅)
        // 身分完整:P00..P59 全在,無遺漏無重複。
        Assert.Equal(
            Enumerable.Range(0, preparerCount).Select(i => $"P{i:00}").OrderBy(s => s, StringComparer.Ordinal),
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

        // 裁定 2026-11-05-32：示範案件配對了人工傳票欄位，照舊版依編製人員加人工旗標分列，
        // 所以基數改成「人員與人工旗標」的組數，每列的 recount 也多比人工旗標。第一次失敗：Focused 收據 20261105-105841963-25d6f65850974bbda831c58051526192。
        // 全名單基數 == distinct (created_by, is_manual) recount（§2 母體限本期 gl_date；export 已本期界定，recount 同口徑）。
        var distinct = await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId,
            "SELECT COUNT(*) FROM (SELECT DISTINCT created_by, is_manual FROM gl " +
            "WHERE gl_date >= '2025-01-01' AND gl_date <= '2025-12-31');");
        Assert.Equal(distinct, rows.Count);
        Assert.True(rows.Count > 0, "demo 應有編製者");
        Assert.Contains(rows, row => row.IsManual == true);
        Assert.Contains(rows, row => row.IsManual == false);

        foreach (var row in rows)
        {
            object manual = row.IsManual is { } isManual ? (isManual ? 1 : 0) : DBNull.Value;
            var entryCount = await DemoProjectPipeline.QueryScalarAsync(
                host, context.ProjectId,
                "SELECT COUNT(*) FROM gl WHERE created_by = @c AND is_manual IS @m " +
                "AND gl_date >= '2025-01-01' AND gl_date <= '2025-12-31';",
                ("@c", row.CreatedBy), ("@m", manual));
            var debit = await DemoProjectPipeline.QueryScalarAsync(
                host, context.ProjectId,
                "SELECT COALESCE(SUM(debit_amount_scaled), 0) FROM gl WHERE created_by = @c AND is_manual IS @m " +
                "AND gl_date >= '2025-01-01' AND gl_date <= '2025-12-31';",
                ("@c", row.CreatedBy), ("@m", manual));
            var credit = await DemoProjectPipeline.QueryScalarAsync(
                host, context.ProjectId,
                "SELECT COALESCE(SUM(credit_amount_scaled), 0) FROM gl WHERE created_by = @c AND is_manual IS @m " +
                "AND gl_date >= '2025-01-01' AND gl_date <= '2025-12-31';",
                ("@c", row.CreatedBy), ("@m", manual));

            Assert.Equal(entryCount, row.EntryCount);
            Assert.Equal(debit, row.DebitTotalScaled);
            Assert.Equal(credit, row.CreditTotalScaled);
        }
    }

    /// <summary>
    /// 排序:先按這位人員的總筆數降冪、再按 created_by 升冪,同一位人員照舊版先自動、再人工,未判定放最後(裁定 2026-11-05-32)。
    /// 原本一人一列時是「筆數降冪、再按 created_by」;分列後同一位人員的列排在一起,人員之間的順序不變。
    /// 第一次失敗：Focused 收據 20261105-105841963-25d6f65850974bbda831c58051526192。
    /// demo 編製者以 voucherIndex % 5 輪派 → 筆數非全等,可證 DESC 主鍵生效。
    /// </summary>
    [Fact]
    public async Task FetchAllAsync_OrdersByPreparerTotalDescThenCreatedByThenManualFlag()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        var rows = await SqliteRepo(host).FetchAllAsync(
            context.ProjectId, "2025-01-01", "2025-12-31", CancellationToken.None);
        var totals = rows
            .GroupBy(row => row.CreatedBy, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.EntryCount), StringComparer.Ordinal);
        static int ManualRank(PreparerSummaryExportRow row) => row.IsManual switch
        {
            false => 0,
            true => 1,
            null => 2
        };

        Assert.Contains(totals, pair => rows.Count(row => row.CreatedBy == pair.Key) > 1);
        for (var i = 1; i < rows.Count; i++)
        {
            var prev = rows[i - 1];
            var curr = rows[i];
            var samePerson = string.Equals(prev.CreatedBy, curr.CreatedBy, StringComparison.Ordinal);
            var ordered = samePerson
                ? ManualRank(prev) < ManualRank(curr)
                : totals[prev.CreatedBy] > totals[curr.CreatedBy]
                    || (totals[prev.CreatedBy] == totals[curr.CreatedBy]
                        && string.CompareOrdinal(prev.CreatedBy, curr.CreatedBy) < 0);
            Assert.True(ordered,
                $"排序違規:[{prev.CreatedBy}:{prev.IsManual}:{prev.EntryCount}] 在 [{curr.CreatedBy}:{curr.IsManual}:{curr.EntryCount}] 之前。");
        }
        Assert.Equal(
            rows.Count,
            rows.Select((row, index) => (row.CreatedBy, index))
                .GroupBy(item => item.CreatedBy, StringComparer.Ordinal)
                .Sum(group => group.Max(item => item.index) - group.Min(item => item.index) + 1));
    }
}
