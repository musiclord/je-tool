using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// <c>specialAccountCategoryPair</c> execution matrix over real SQLite, DuckDB, and gated
/// SQL Server databases. A = Revenue debit; B = Cash credit. The hand-calculated fixture
/// locks both voucher membership and exact tagged-row identity for all three modes.
/// </summary>
public sealed class SpecialAccountCategoryPairPredicateTests
{
    private const string FixtureSql =
        """
        INSERT INTO gl
            (batch_id, source_row_number, voucher_number, line_item, gl_date, approval_date,
             account_number, account_name, description, source_module, created_by, approved_by,
             is_manual, is_excluded, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
        VALUES
            -- S1: A debit + B credit.
            ('pair', 1,  'S1', '1', '2025-03-01', '2025-03-02', '4101', 'Revenue',     'A debit B credit',       NULL, 'P', NULL, 0, 0,  50000, 50000, 0,     'DEBIT'),
            ('pair', 2,  'S1', '2', '2025-03-01', '2025-03-02', '1101', 'Cash',        'A debit B credit',       NULL, 'P', NULL, 0, 0, -50000, 0,     50000, 'CREDIT'),
            -- S2: A debit + a non-B credit.
            ('pair', 3,  'S2', '1', '2025-04-01', '2025-04-02', '4101', 'Revenue',     'A debit without B',      NULL, 'P', NULL, 0, 0,  60000, 60000, 0,     'DEBIT'),
            ('pair', 4,  'S2', '2', '2025-04-01', '2025-04-02', '1131', 'Receivables', 'A debit without B',      NULL, 'P', NULL, 0, 0, -60000, 0,     60000, 'CREDIT'),
            -- S3: B credit + a non-A debit.
            ('pair', 5,  'S3', '1', '2025-05-01', '2025-05-02', '1131', 'Receivables', 'B credit without A',     NULL, 'P', NULL, 0, 0,  70000, 70000, 0,     'DEBIT'),
            ('pair', 6,  'S3', '2', '2025-05-01', '2025-05-02', '1101', 'Cash',        'B credit without A',     NULL, 'P', NULL, 0, 0, -70000, 0,     70000, 'CREDIT'),
            -- S4: neither A debit nor B credit.
            ('pair', 7,  'S4', '1', '2025-06-01', '2025-06-02', '5101', 'Others',      'neither',                NULL, 'P', NULL, 0, 0,  80000, 80000, 0,     'DEBIT'),
            ('pair', 8,  'S4', '2', '2025-06-01', '2025-06-02', '1131', 'Receivables', 'neither',                NULL, 'P', NULL, 0, 0, -80000, 0,     80000, 'CREDIT'),
            -- S5: A debit with both a B credit and a non-B credit. This is the decisive
            --     counterexample: drNotCr means no B credit anywhere in the voucher,
            --     not merely that some non-B credit exists.
            ('pair', 9,  'S5', '1', '2025-07-01', '2025-07-02', '4101', 'Revenue',     'mixed credit categories',NULL, 'P', NULL, 0, 0,  100000,100000,0,     'DEBIT'),
            ('pair', 10, 'S5', '2', '2025-07-01', '2025-07-02', '1101', 'Cash',        'mixed credit categories',NULL, 'P', NULL, 0, 0, -40000, 0,     40000, 'CREDIT'),
            ('pair', 11, 'S5', '3', '2025-07-01', '2025-07-02', '1131', 'Receivables', 'mixed credit categories',NULL, 'P', NULL, 0, 0, -60000, 0,     60000, 'CREDIT');

        -- 科目配對一律以 category_id 連到分類表（2026-10-02 起刪除依分類名稱對應的退路），測試資料補上 category_id。
        INSERT INTO account_mapping
            (batch_id, source_row_number, account_number, account_name, standardized_category, category_id)
        VALUES
            ('mapping', 1, '4101', 'Revenue',     'Revenue', 'builtin.revenue'),
            ('mapping', 2, '1131', 'Receivables', 'Receivables', 'builtin.receivables'),
            ('mapping', 3, '1101', 'Cash',        'Cash', 'builtin.cash'),
            ('mapping', 4, '5101', 'Others',      'Others', 'builtin.others');
        """;

    private static readonly FilterConditionContext Context =
        new(100, "2025-12-31", "2025-01-01", "2025-12-31");

    [Theory]
    [InlineData("sqlite", SpecialAccountCategoryPairModes.DrAndCr)]
    [InlineData("sqlite", SpecialAccountCategoryPairModes.DrNotCr)]
    [InlineData("sqlite", SpecialAccountCategoryPairModes.NotDrCr)]
    [InlineData("duckdb", SpecialAccountCategoryPairModes.DrAndCr)]
    [InlineData("duckdb", SpecialAccountCategoryPairModes.DrNotCr)]
    [InlineData("duckdb", SpecialAccountCategoryPairModes.NotDrCr)]
    public async Task PairMode_LocalProvider_HitsExactRows(string provider, string pairMode)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, FixtureSql);

        await AssertModeAsync(fixture, pairMode);
    }

    [SqlServerTheory]
    [InlineData(SpecialAccountCategoryPairModes.DrAndCr)]
    [InlineData(SpecialAccountCategoryPairModes.DrNotCr)]
    [InlineData(SpecialAccountCategoryPairModes.NotDrCr)]
    public async Task PairMode_SqlServer_HitsExactRows(string pairMode)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateSqlServerAsync(FixtureSql);

        await AssertModeAsync(fixture, pairMode);
    }

    private static async Task AssertModeAsync(
        FilterPredicateProviderFixture fixture,
        string pairMode)
    {
        var expectedRows = ExpectedRows(pairMode);
        var result = await fixture.Repository.PreviewAsync(
            fixture.ProjectId,
            SingleRule(Rule(pairMode)),
            Context,
            CancellationToken.None);
        var actualRows = result.PreviewRows
            .Select(row => $"{row.VoucherNumber}|{row.LineItem}")
            .OrderBy(identity => identity, StringComparer.Ordinal)
            .ToArray();
        var expectedVoucherCount = expectedRows
            .Select(identity => identity[..identity.IndexOf('|')])
            .Distinct(StringComparer.Ordinal)
            .LongCount();

        Assert.Equal((long)expectedRows.Length, result.Count);
        Assert.Equal(expectedVoucherCount, result.VoucherCount);
        Assert.Equal(expectedRows, actualRows);
    }

    private static FilterConditionSpec Rule(string pairMode) =>
        new(
            FilterJoin.And,
            FilterConditionType.SpecialAccountCategoryPair,
            null,
            null,
            [],
            TextMatchMode.Contains,
            null,
            null,
            null,
            null,
            null,
            null,
            PairMode: pairMode)
        {
            // 2026-10-02 起單選分類欄位已刪除，改用分類身分陣列表達同一組分類。
            DebitCategoryIds = [AccountCategoryBuiltIns.RevenueId],
            CreditCategoryIds = [AccountCategoryBuiltIns.CashId]
        };

    private static FilterScenarioSpec SingleRule(FilterConditionSpec rule) =>
        new(
            "special account category pair matrix",
            "hand-calculated row identity oracle",
            [new FilterGroupSpec(FilterJoin.And, [rule])]);

    // 裁定 2026-11-05-45 照舊版，兩個「沒有」模式也標記同一張傳票對方那一側的全部分錄：
    // S2 多了 Receivables 貸方 S2|2，S3 多了 Receivables 借方 S3|1；原本只有 S2|1 與 S3|2。
    // 第一次失敗：Focused 收據 20261105-120743283-660b6625fa1a47119b3e76ef188dcf3e（預期 1 筆，實際 2 筆）。
    private static string[] ExpectedRows(string pairMode) => pairMode switch
    {
        SpecialAccountCategoryPairModes.DrAndCr => ["S1|1", "S1|2", "S5|1", "S5|2"],
        SpecialAccountCategoryPairModes.DrNotCr => ["S2|1", "S2|2"],
        SpecialAccountCategoryPairModes.NotDrCr => ["S3|1", "S3|2"],
        _ => throw new ArgumentOutOfRangeException(nameof(pairMode), pairMode, "Unknown pair mode.")
    };
}
