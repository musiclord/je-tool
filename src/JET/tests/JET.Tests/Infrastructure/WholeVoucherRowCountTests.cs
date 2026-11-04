using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 只用「未預期借貸組合」一個條件時，Criteria Selection 摘要與工作底稿以整張命中傳票計算分錄筆數
/// （<see cref="JetAuditProgram.UsesLegacyWholeVoucherRows"/>）。依裁定 2026-10-16-08，第五步分開顯示
/// 「符合條件的傳票數量」與「符合條件的分錄筆數」，數字要和過往底稿相同。這裡核對預覽、傳票清單與已存情境摘要
/// 三條查詢，和報告寫出整張傳票列的分頁查詢是同一個集合。預期值由下面的合成資料直接數出：
///
///   PAIR：兩列收入貸方、三列營業成本借方，另有一列無效分錄 → 直接符合 2 列，整張有效分錄 5 列
///   NONE：一列營業成本借方、一列收入貸方 → 直接符合 1 列，整張 2 列
///   CS  ：賒銷，借應收、貸收入 → 不符合
/// </summary>
public sealed class WholeVoucherRowCountTests
{
    private const string FixtureSql =
        """
        INSERT INTO account_mapping
            (batch_id, source_row_number, account_number, account_name, standardized_category, category_id)
        VALUES
            ('am1', 1, '1131', '應收帳款', 'Receivables', 'builtin.receivables'),
            ('am1', 2, '5101', '營業成本', 'Others', 'builtin.others'),
            ('am1', 3, '4101', '銷貨收入', 'Revenue', 'builtin.revenue');
        INSERT INTO gl
            (batch_id, source_row_number, voucher_number, line_item, gl_date, approval_date, account_number, account_name,
             description, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr, is_excluded)
        VALUES
            ('b1', 1, 'PAIR', '1', '2025-03-05', '2025-03-06', '4101', '銷貨收入', '合成', -10000, 0, 10000, 'CREDIT', 0),
            ('b1', 2, 'PAIR', '2', '2025-03-05', '2025-03-06', '4101', '銷貨收入', '合成', -10000, 0, 10000, 'CREDIT', 0),
            ('b1', 3, 'PAIR', '3', '2025-03-05', '2025-03-06', '5101', '營業成本', '合成', 5000, 5000, 0, 'DEBIT', 0),
            ('b1', 4, 'PAIR', '4', '2025-03-05', '2025-03-06', '5101', '營業成本', '合成', 5000, 5000, 0, 'DEBIT', 0),
            ('b1', 5, 'PAIR', '5', '2025-03-05', '2025-03-06', '5101', '營業成本', '合成', 10000, 10000, 0, 'DEBIT', 0),
            ('b1', 6, 'PAIR', '6', '2025-03-05', '2025-03-06', '5101', '營業成本', '合成', 7000, 7000, 0, 'DEBIT', 1),
            ('b1', 7, 'NONE', '1', '2025-03-05', '2025-03-06', '5101', '營業成本', '合成', 30000, 30000, 0, 'DEBIT', 0),
            ('b1', 8, 'NONE', '2', '2025-03-05', '2025-03-06', '4101', '銷貨收入', '合成', -30000, 0, 30000, 'CREDIT', 0),
            ('b1', 9, 'CS', '1', '2025-03-05', '2025-03-06', '1131', '應收帳款', '合成', 10000, 10000, 0, 'DEBIT', 0),
            ('b1', 10, 'CS', '2', '2025-03-05', '2025-03-06', '4101', '銷貨收入', '合成', -10000, 0, 10000, 'CREDIT', 0);
        """;

    private static readonly FilterConditionContext Context = new(100, "2025-12-31", "2025-01-01", "2025-12-31");

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task UnexpectedAccountPairAlone_CountsEveryEffectiveRowOfHitVouchers_SameAsTheReportRows(string provider)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = provider == "sqlite" ? new SqliteProjectDatabase(folder) : new DuckDbProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await ExecuteAsync(database, projectId, FixtureSql);
        var scenario = new FilterScenarioSpec("未預期借貸組合", "整張傳票計數",
            [new FilterGroupSpec(FilterJoin.And, [new FilterConditionSpec(FilterJoin.And, FilterConditionType.Prescreen,
                PrescreenRuleKeys.UnexpectedAccountPair, null, [], TextMatchMode.Contains, null, null, null, null, null, null)])]);
        Assert.True(JetAuditProgram.UsesLegacyWholeVoucherRows(scenario));

        // 預覽：直接符合 3 列、2 張傳票；整張傳票的有效分錄是 5 + 2 = 7 列，無效分錄不算。
        var previewRepository = new LocalFilterHitRepository(database);
        var preview = await previewRepository.PreviewAsync(projectId, scenario, Context, CancellationToken.None);
        Assert.Equal(3, preview.Count);
        Assert.Equal(2, preview.VoucherCount);
        Assert.Equal(7, await previewRepository.CountWholeVoucherRowsAsync(projectId, scenario, Context, CancellationToken.None));

        // 傳票清單：整張傳票計數時，符合分錄數等於整張傳票的分錄數；一般計數維持直接符合的列。
        var vouchers = new LocalFilterVoucherRepository(database);
        var whole = await vouchers.ReadAsync(projectId, scenario, Context, null, new PageRequest(null, 10),
            countWholeVoucherRows: true, CancellationToken.None);
        Assert.Equal([("NONE", 2L, 2L), ("PAIR", 5L, 5L)],
            whole.Vouchers.Select(row => (row.VoucherNumber, row.HitRowCount, row.TotalRowCount)).OrderBy(row => row.VoucherNumber));
        var direct = await vouchers.ReadAsync(projectId, scenario, Context, null, new PageRequest(null, 10),
            countWholeVoucherRows: false, CancellationToken.None);
        Assert.Equal([("NONE", 1L, 2L), ("PAIR", 2L, 5L)],
            direct.Vouchers.Select(row => (row.VoucherNumber, row.HitRowCount, row.TotalRowCount)).OrderBy(row => row.VoucherNumber));

        // 已存情境：命中結果表只記直接符合的 3 列。摘要的整張傳票分錄數，和報告寫出的整張傳票列一樣是 7。
        await ExecuteAsync(database, projectId,
            "INSERT INTO filter_scenario (scenario_id, position, name, rationale, definition_json, saved_utc) "
            + "VALUES (1, 1, 'synthetic', 'synthetic', '{}', '2026-10-31T00:00:00Z');");
        await ExecuteAsync(database, projectId,
            "INSERT INTO filter_hit (scenario_id, entry_id) SELECT 1, entry_id FROM gl "
            + "WHERE account_number = '4101' AND voucher_number IN ('PAIR', 'NONE');");
        var counts = new LocalHighRiskSummaryScenariosRepository(database);
        var wholeCounts = await counts.GetWholeVoucherRowCountsAsync(projectId, [1], CancellationToken.None);
        Assert.Equal(7, Assert.Single(wholeCounts, item => item.Key == 1).Value);
        Assert.Empty(await counts.GetWholeVoucherRowCountsAsync(projectId, [], CancellationToken.None));
        Assert.Equal(7, await CountReportRowsAsync(new LocalHighRiskSummaryRowPageRepository(database), projectId));
    }

    // 與 Criteria Selection 報告寫出「#Criteria Select 1」相同的分頁讀法：只看第 1 個情境，逐頁讀到底。
    private static async Task<long> CountReportRowsAsync(IHighRiskSummaryRowPageRepository rows, string projectId)
    {
        var population = new GlPopulationContext(GlPopulationScope.AuditPeriod, Context.PeriodStart, Context.PeriodEnd);
        long total = 0;
        string? cursor = null;
        do
        {
            var page = await rows.GetPageAsync(projectId, population, new PageRequest(cursor, 2), [1], CancellationToken.None);
            total += page.EntryIds.Count;
            cursor = page.Page.NextCursor;
        } while (cursor is not null);
        return total;
    }

    private static async Task ExecuteAsync(ILocalProjectDatabase database, string projectId, string sql)
    {
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
