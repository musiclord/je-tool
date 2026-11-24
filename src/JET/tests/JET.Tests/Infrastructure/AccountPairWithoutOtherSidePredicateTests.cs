using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 借貸科目組合的兩個「沒有」模式：「借方是 A，而且整張傳票沒有 B 類貸方」與「貸方是 B，而且整張傳票沒有 A 類借方」。
/// 「沒有」是指整張傳票完全沒有那一類分錄，所以只有單邊分錄、借貸不平的傳票也算；
/// 一側選兩個以上分類時，「沒有」是指每一個所選分類都沒有。答案逐列手算。
///
/// 傳票（都在查核期間內、都納入測試）：
///   U1 只有一列：借 Cash 500（沒有任何貸方列，借貸不平）
///   U2 只有一列：貸 Revenue 800（沒有任何借方列，借貸不平）
///   B1 借 Cash 1000        ／貸 Revenue 1000
///   B2 借 Receivables 2000 ／貸 Cash 2000
///   B3 借 Others 3000      ／貸 Revenue 3000
///   B4 借 Receivables 4000 ／貸 Revenue 4000
/// </summary>
public sealed class AccountPairWithoutOtherSidePredicateTests
{
    private const string FixtureSql =
        """
        INSERT INTO gl
            (batch_id, source_row_number, voucher_number, line_item, gl_date, approval_date,
             account_number, account_name, description, source_module, created_by, approved_by,
             is_manual, is_excluded, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
        VALUES
            ('pair', 1,  'U1', '1', '2025-03-01', '2025-03-02', '1101', 'Cash',        'cash debit only',     NULL, 'P', NULL, 0, 0,    500,  500, 0,    'DEBIT'),
            ('pair', 2,  'U2', '1', '2025-03-02', '2025-03-03', '4101', 'Revenue',     'revenue credit only', NULL, 'P', NULL, 0, 0,   -800,    0, 800,  'CREDIT'),
            ('pair', 3,  'B1', '1', '2025-03-03', '2025-03-04', '1101', 'Cash',        'cash debit',          NULL, 'P', NULL, 0, 0,   1000, 1000, 0,    'DEBIT'),
            ('pair', 4,  'B1', '2', '2025-03-03', '2025-03-04', '4101', 'Revenue',     'revenue credit',      NULL, 'P', NULL, 0, 0,  -1000,    0, 1000, 'CREDIT'),
            ('pair', 5,  'B2', '1', '2025-03-05', '2025-03-06', '1131', 'Receivables', 'receivable debit',    NULL, 'P', NULL, 0, 0,   2000, 2000, 0,    'DEBIT'),
            ('pair', 6,  'B2', '2', '2025-03-05', '2025-03-06', '1101', 'Cash',        'cash credit',         NULL, 'P', NULL, 0, 0,  -2000,    0, 2000, 'CREDIT'),
            ('pair', 7,  'B3', '1', '2025-03-07', '2025-03-08', '5101', 'Others',      'others debit',        NULL, 'P', NULL, 0, 0,   3000, 3000, 0,    'DEBIT'),
            ('pair', 8,  'B3', '2', '2025-03-07', '2025-03-08', '4101', 'Revenue',     'revenue credit',      NULL, 'P', NULL, 0, 0,  -3000,    0, 3000, 'CREDIT'),
            ('pair', 9,  'B4', '1', '2025-03-09', '2025-03-10', '1131', 'Receivables', 'receivable debit',    NULL, 'P', NULL, 0, 0,   4000, 4000, 0,    'DEBIT'),
            ('pair', 10, 'B4', '2', '2025-03-09', '2025-03-10', '4101', 'Revenue',     'revenue credit',      NULL, 'P', NULL, 0, 0,  -4000,    0, 4000, 'CREDIT');

        INSERT INTO account_mapping
            (batch_id, source_row_number, account_number, account_name, standardized_category, category_id)
        VALUES
            ('mapping', 1, '4101', 'Revenue',     'Revenue',     'builtin.revenue'),
            ('mapping', 2, '1131', 'Receivables', 'Receivables', 'builtin.receivables'),
            ('mapping', 3, '1101', 'Cash',        'Cash',        'builtin.cash'),
            ('mapping', 4, '5101', 'Others',      'Others',      'builtin.others');
        """;

    private static readonly FilterConditionContext Context =
        new(100, "2025-12-31", "2025-01-01", "2025-12-31");

    // 借方是 Cash，而且整張傳票沒有 Revenue 貸方。U1 只有一列 Cash 借方，沒有任何貸方列，照字面應該選入；
    // B1 有 Revenue 貸方，不選。
    [Fact]
    public async Task DebitWithoutCreditCategory_SelectsAVoucherThatHasNoCreditRowsAtAll()
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync("sqlite", FixtureSql);

        await AssertHitsAsync(fixture,
            Rule(SpecialAccountCategoryPairModes.DrNotCr, [AccountCategoryBuiltIns.CashId], [AccountCategoryBuiltIns.RevenueId]),
            ["U1|1"]);
    }

    // 貸方是 Revenue，而且整張傳票沒有 Cash 借方。U2 只有一列 Revenue 貸方，沒有任何借方列，照字面應該選入；
    // B3、B4 的借方不是 Cash，也選入，並連同傳票的借方列一起列出；B1 有 Cash 借方，不選。
    [Fact]
    public async Task CreditWithoutDebitCategory_SelectsAVoucherThatHasNoDebitRowsAtAll()
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync("sqlite", FixtureSql);

        await AssertHitsAsync(fixture,
            Rule(SpecialAccountCategoryPairModes.NotDrCr, [AccountCategoryBuiltIns.CashId], [AccountCategoryBuiltIns.RevenueId]),
            ["B3|1", "B3|2", "B4|1", "B4|2", "U2|1"]);
    }

    // 借方選 Cash 與 Receivables 兩個分類：有任一分類的借方、而且沒有 Revenue 貸方的傳票是 U1 與 B2。
    // 結果等於兩個分類分開選再合起來，排在前面的分類不會被漏掉。
    [Fact]
    public async Task DebitWithoutCreditCategory_TwoDebitCategories_EqualsTheUnionOfSingleSelections()
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync("sqlite", FixtureSql);

        await AssertHitsAsync(fixture,
            Rule(SpecialAccountCategoryPairModes.DrNotCr, [AccountCategoryBuiltIns.CashId], [AccountCategoryBuiltIns.RevenueId]),
            ["U1|1"]);
        await AssertHitsAsync(fixture,
            Rule(SpecialAccountCategoryPairModes.DrNotCr, [AccountCategoryBuiltIns.ReceivablesId], [AccountCategoryBuiltIns.RevenueId]),
            ["B2|1", "B2|2"]);
        await AssertHitsAsync(fixture,
            Rule(SpecialAccountCategoryPairModes.DrNotCr,
                [AccountCategoryBuiltIns.CashId, AccountCategoryBuiltIns.ReceivablesId], [AccountCategoryBuiltIns.RevenueId]),
            ["B2|1", "B2|2", "U1|1"]);
    }

    // 「沒有 A 類借方」的 A 選 Cash 與 Receivables 兩個分類：兩個分類的借方都沒有才算。
    // 只選 Cash 時是 U2、B3、B4，只選 Receivables 時是 U2、B1、B3；兩個都選只剩 U2 與 B3。
    // 選取的順序不影響結果，排在前面的分類不會被漏掉。
    [Fact]
    public async Task CreditWithoutDebitCategory_TwoDebitCategories_KeepsOnlyVouchersWithNeither()
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync("sqlite", FixtureSql);

        await AssertHitsAsync(fixture,
            Rule(SpecialAccountCategoryPairModes.NotDrCr, [AccountCategoryBuiltIns.ReceivablesId], [AccountCategoryBuiltIns.RevenueId]),
            ["B1|1", "B1|2", "B3|1", "B3|2", "U2|1"]);
        await AssertHitsAsync(fixture,
            Rule(SpecialAccountCategoryPairModes.NotDrCr,
                [AccountCategoryBuiltIns.CashId, AccountCategoryBuiltIns.ReceivablesId], [AccountCategoryBuiltIns.RevenueId]),
            ["B3|1", "B3|2", "U2|1"]);
        await AssertHitsAsync(fixture,
            Rule(SpecialAccountCategoryPairModes.NotDrCr,
                [AccountCategoryBuiltIns.ReceivablesId, AccountCategoryBuiltIns.CashId], [AccountCategoryBuiltIns.RevenueId]),
            ["B3|1", "B3|2", "U2|1"]);
    }

    private static async Task AssertHitsAsync(
        FilterPredicateProviderFixture fixture, FilterConditionSpec rule, string[] expectedRows)
    {
        var result = await fixture.Repository.PreviewAsync(
            fixture.ProjectId,
            new FilterScenarioSpec("合成情境", "逐列手算的答案", [new FilterGroupSpec(FilterJoin.And, [rule])]),
            Context,
            CancellationToken.None);
        var actualRows = result.PreviewRows
            .Select(row => $"{row.VoucherNumber}|{row.LineItem}")
            .OrderBy(static identity => identity, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedRows, actualRows);
        Assert.Equal((long)expectedRows.Length, result.Count);
        Assert.Equal(
            expectedRows.Select(static identity => identity[..identity.IndexOf('|')]).Distinct(StringComparer.Ordinal).LongCount(),
            result.VoucherCount);
    }

    private static FilterConditionSpec Rule(
        string pairMode, IReadOnlyList<string> debitCategoryIds, IReadOnlyList<string> creditCategoryIds) =>
        new(FilterJoin.And, FilterConditionType.SpecialAccountCategoryPair, null, null, [], TextMatchMode.Contains,
            null, null, null, null, null, null, PairMode: pairMode)
        {
            DebitCategoryIds = debitCategoryIds,
            CreditCategoryIds = creditCategoryIds
        };
}
