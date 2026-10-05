using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 借貸組合雙側多選在 SQLite、DuckDB 與受閘控 SQL Server 上的實際執行矩陣。
/// Fixture 以手算 oracle 鎖定傳票歸屬與逐列命中身分，涵蓋：多選、零元借方列、
/// 自訂分類（同 role 者一併參與、自訂 role 者自成一組）、否定模式，以及排列／重複不變性。
///
/// 傳票（皆在查核期間、is_effective = 1）：
///   M1 借 Receivables 10000 ／貸 Revenue 10000
///   M2 借 Cash 20000        ／貸 Revenue 20000
///   M3 借 零用金（custom，role = cash，與內建 Cash 同 role）30000 ／貸 Revenue 30000
///   M4 借 Others 40000      ／貸 Revenue 40000
///   M5 借 Receivables 0（零元，仍屬借方側）／貸 Revenue 50000
///   M6 借 Receivables 60000 ／貸 Cash 60000（整張傳票沒有 Revenue 貸方）
///   M7 借 履約保證金（custom，role = deposits，自訂 role）70000 ／貸 Revenue 70000
/// </summary>
public sealed class AccountPairCategorySelectionPredicateTests
{
    private const string CustomPettyCashId = "custom.0123456789abcdef0123456789abcdef";

    private const string CustomDepositsId = "custom.fedcba9876543210fedcba9876543210";

    private const string FixtureSql =
        """
        INSERT INTO config_account_taxonomy
            (category_id, label, ordinal, semantic_role, is_builtin, revision)
        VALUES
            ('custom.0123456789abcdef0123456789abcdef', 'Petty cash', 5, 'cash', 0, 2),
            ('custom.fedcba9876543210fedcba9876543210', 'Deposits', 6, 'deposits', 0, 2);

        INSERT INTO target_gl_entry
            (batch_id, source_row_number, document_number, line_item, post_date, approval_date,
             account_code, account_name, document_description, source_module, created_by, approved_by,
             is_manual, is_effective, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
        VALUES
            ('pair', 1,  'M1', '1', '2025-03-01', '2025-03-02', '1131', 'Receivables', 'receivable debit', NULL, 'P', NULL, 0, 1,  10000, 10000, 0,     'DEBIT'),
            ('pair', 2,  'M1', '2', '2025-03-01', '2025-03-02', '4101', 'Revenue',     'revenue credit',   NULL, 'P', NULL, 0, 1, -10000, 0,     10000, 'CREDIT'),
            ('pair', 3,  'M2', '1', '2025-03-03', '2025-03-04', '1101', 'Cash',        'cash debit',       NULL, 'P', NULL, 0, 1,  20000, 20000, 0,     'DEBIT'),
            ('pair', 4,  'M2', '2', '2025-03-03', '2025-03-04', '4101', 'Revenue',     'revenue credit',   NULL, 'P', NULL, 0, 1, -20000, 0,     20000, 'CREDIT'),
            ('pair', 5,  'M3', '1', '2025-03-05', '2025-03-06', '1102', 'Petty cash',  'custom debit',     NULL, 'P', NULL, 0, 1,  30000, 30000, 0,     'DEBIT'),
            ('pair', 6,  'M3', '2', '2025-03-05', '2025-03-06', '4101', 'Revenue',     'revenue credit',   NULL, 'P', NULL, 0, 1, -30000, 0,     30000, 'CREDIT'),
            ('pair', 7,  'M4', '1', '2025-03-07', '2025-03-08', '5101', 'Others',      'others debit',     NULL, 'P', NULL, 0, 1,  40000, 40000, 0,     'DEBIT'),
            ('pair', 8,  'M4', '2', '2025-03-07', '2025-03-08', '4101', 'Revenue',     'revenue credit',   NULL, 'P', NULL, 0, 1, -40000, 0,     40000, 'CREDIT'),
            ('pair', 9,  'M5', '1', '2025-03-09', '2025-03-10', '1131', 'Receivables', 'zero debit',       NULL, 'P', NULL, 0, 1,      0, 0,     0,     'DEBIT'),
            ('pair', 10, 'M5', '2', '2025-03-09', '2025-03-10', '4101', 'Revenue',     'revenue credit',   NULL, 'P', NULL, 0, 1, -50000, 0,     50000, 'CREDIT'),
            ('pair', 11, 'M6', '1', '2025-03-11', '2025-03-12', '1131', 'Receivables', 'receivable debit', NULL, 'P', NULL, 0, 1,  60000, 60000, 0,     'DEBIT'),
            ('pair', 12, 'M6', '2', '2025-03-11', '2025-03-12', '1101', 'Cash',        'cash credit',      NULL, 'P', NULL, 0, 1, -60000, 0,     60000, 'CREDIT'),
            ('pair', 13, 'M7', '1', '2025-03-13', '2025-03-14', '1103', 'Deposits',    'deposit debit',    NULL, 'P', NULL, 0, 1,  70000, 70000, 0,     'DEBIT'),
            ('pair', 14, 'M7', '2', '2025-03-13', '2025-03-14', '4101', 'Revenue',     'revenue credit',   NULL, 'P', NULL, 0, 1, -70000, 0,     70000, 'CREDIT');

        INSERT INTO target_account_mapping
            (batch_id, source_row_number, account_code, account_name, standardized_category, category_id)
        VALUES
            ('mapping', 1, '4101', 'Revenue',     'Revenue',     'builtin.revenue'),
            ('mapping', 2, '1131', 'Receivables', 'Receivables', 'builtin.receivables'),
            ('mapping', 3, '1101', 'Cash',        'Cash',        'builtin.cash'),
            ('mapping', 4, '1102', 'Petty cash',  'Cash',        'custom.0123456789abcdef0123456789abcdef'),
            ('mapping', 5, '5101', 'Others',      'Others',      'builtin.others'),
            ('mapping', 6, '1103', 'Deposits',    'Others',      'custom.fedcba9876543210fedcba9876543210');
        """;

    private static readonly FilterRuleContext Context =
        new(100, "2025-12-31", "2025-01-01", "2025-12-31");

    public static TheoryData<string, string> LocalScenarios()
    {
        var data = new TheoryData<string, string>();
        foreach (var provider in new[] { "sqlite", "duckdb" })
        {
            foreach (var scenario in ScenarioKeys)
            {
                data.Add(provider, scenario);
            }
        }

        return data;
    }

    public static TheoryData<string> SqlServerScenarios()
    {
        var data = new TheoryData<string>();
        foreach (var scenario in ScenarioKeys)
        {
            data.Add(scenario);
        }

        return data;
    }

    private static readonly string[] ScenarioKeys =
    [
        "exactBuiltInPair",
        "exactWithSameRoleCustomCategory",
        "exactWithCustomRoleCategory",
        "debitAnchorMultiSelect",
        "creditAnchorSingle",
        "drAndCrMultiSelect",
        "drNotCrMultiSelect",
        "notDrCrSingle"
    ];

    [Theory]
    [MemberData(nameof(LocalScenarios))]
    public async Task CategorySelection_LocalProvider_HitsExactRows(string provider, string scenario)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, FixtureSql);

        await AssertScenarioAsync(fixture, scenario);
    }

    [SqlServerTheory]
    [MemberData(nameof(SqlServerScenarios))]
    public async Task CategorySelection_SqlServer_HitsExactRows(string scenario)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateSqlServerAsync(FixtureSql);

        await AssertScenarioAsync(fixture, scenario);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task CategoryOrderAndDuplicates_DoNotChangeHits(string provider)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, FixtureSql);

        var canonical = await HitsAsync(fixture, Rule(
            FilterRuleType.AccountPair,
            AccountPairModes.Exact,
            [AccountTaxonomyBuiltIns.ReceivablesId, AccountTaxonomyBuiltIns.CashId],
            [AccountTaxonomyBuiltIns.RevenueId]));
        var shuffledWithDuplicates = await HitsAsync(fixture, Rule(
            FilterRuleType.AccountPair,
            AccountPairModes.Exact,
            [AccountTaxonomyBuiltIns.CashId, AccountTaxonomyBuiltIns.ReceivablesId, AccountTaxonomyBuiltIns.CashId],
            [AccountTaxonomyBuiltIns.RevenueId, AccountTaxonomyBuiltIns.RevenueId]));

        Assert.Equal(canonical, shuffledWithDuplicates);
        // 多選不得產生 category 笛卡兒積：同一 GL 列最多輸出一次。
        Assert.Equal(canonical.Distinct(StringComparer.Ordinal).ToArray(), canonical);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task MultiSelect_PositiveModes_EqualUnionOfSingleSelections(string provider)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, FixtureSql);

        var receivablesOnly = await HitsAsync(fixture, Rule(
            FilterRuleType.AccountPair,
            AccountPairModes.Exact,
            [AccountTaxonomyBuiltIns.ReceivablesId],
            [AccountTaxonomyBuiltIns.RevenueId]));
        var cashOnly = await HitsAsync(fixture, Rule(
            FilterRuleType.AccountPair,
            AccountPairModes.Exact,
            [AccountTaxonomyBuiltIns.CashId],
            [AccountTaxonomyBuiltIns.RevenueId]));
        var both = await HitsAsync(fixture, Rule(
            FilterRuleType.AccountPair,
            AccountPairModes.Exact,
            [AccountTaxonomyBuiltIns.ReceivablesId, AccountTaxonomyBuiltIns.CashId],
            [AccountTaxonomyBuiltIns.RevenueId]));

        Assert.Equal(
            receivablesOnly.Concat(cashOnly)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static identity => identity, StringComparer.Ordinal)
                .ToArray(),
            both);
    }

    private static async Task AssertScenarioAsync(FilterPredicateProviderFixture fixture, string scenario)
    {
        var (rule, expectedRows) = Scenario(scenario);
        var result = await fixture.Repository.PreviewAsync(
            fixture.ProjectId,
            SingleRule(rule),
            Context,
            CancellationToken.None);
        var actualRows = result.PreviewRows
            .Select(row => $"{row.DocumentNumber}|{row.LineItem}")
            .OrderBy(static identity => identity, StringComparer.Ordinal)
            .ToArray();
        var expectedVoucherCount = expectedRows
            .Select(static identity => identity[..identity.IndexOf('|')])
            .Distinct(StringComparer.Ordinal)
            .LongCount();

        Assert.Equal(expectedRows, actualRows);
        Assert.Equal((long)expectedRows.Length, result.Count);
        Assert.Equal(expectedVoucherCount, result.VoucherCount);
    }

    private static async Task<string[]> HitsAsync(
        FilterPredicateProviderFixture fixture,
        FilterRuleSpec rule)
    {
        var result = await fixture.Repository.PreviewAsync(
            fixture.ProjectId,
            SingleRule(rule),
            Context,
            CancellationToken.None);
        return result.PreviewRows
            .Select(row => $"{row.DocumentNumber}|{row.LineItem}")
            .OrderBy(static identity => identity, StringComparer.Ordinal)
            .ToArray();
    }

    private static (FilterRuleSpec Rule, string[] ExpectedRows) Scenario(string scenario) => scenario switch
    {
        // 借方多選 {Receivables, Cash} × 貸方 {Revenue}：判定看 semantic role，
        // 因此與內建 Cash 同 role 的零用金（M3）一併參與；M5 的零元借方列仍屬借方側。
        // M7 的履約保證金是自訂 role，未被選到 → 不命中。
        "exactBuiltInPair" => (
            Rule(
                FilterRuleType.AccountPair,
                AccountPairModes.Exact,
                [AccountTaxonomyBuiltIns.ReceivablesId, AccountTaxonomyBuiltIns.CashId],
                [AccountTaxonomyBuiltIns.RevenueId]),
            ["M1|1", "M1|2", "M2|1", "M2|2", "M3|1", "M3|2", "M5|1", "M5|2"]),

        // 只選自訂的零用金：它與內建 Cash 同 role，因此兩者一起參與同一商業結果。
        "exactWithSameRoleCustomCategory" => (
            Rule(
                FilterRuleType.AccountPair,
                AccountPairModes.Exact,
                [CustomPettyCashId],
                [AccountTaxonomyBuiltIns.RevenueId]),
            ["M2|1", "M2|2", "M3|1", "M3|2"]),

        // 自訂 role 的分類自成一組語意：只命中它自己，內建分類不受影響。
        "exactWithCustomRoleCategory" => (
            Rule(
                FilterRuleType.AccountPair,
                AccountPairModes.Exact,
                [CustomDepositsId],
                [AccountTaxonomyBuiltIns.RevenueId]),
            ["M7|1", "M7|2"]),

        // 借方錨定：輸出錨定借方列與同傳票全部貸方列（貸方不看分類）。
        "debitAnchorMultiSelect" => (
            Rule(
                FilterRuleType.AccountPair,
                AccountPairModes.DebitAnchor,
                [AccountTaxonomyBuiltIns.ReceivablesId, AccountTaxonomyBuiltIns.CashId],
                []),
            ["M1|1", "M1|2", "M2|1", "M2|2", "M3|1", "M3|2", "M5|1", "M5|2", "M6|1", "M6|2"]),

        // 貸方錨定：輸出錨定貸方列與同傳票全部借方列（含零元借方）。
        "creditAnchorSingle" => (
            Rule(
                FilterRuleType.AccountPair,
                AccountPairModes.CreditAnchor,
                [],
                [AccountTaxonomyBuiltIns.RevenueId]),
            [
                "M1|1", "M1|2", "M2|1", "M2|2", "M3|1", "M3|2", "M4|1", "M4|2",
                "M5|1", "M5|2", "M7|1", "M7|2"
            ]),

        "drAndCrMultiSelect" => (
            Rule(
                FilterRuleType.SpecialAccountCategoryPair,
                SpecialAccountCategoryPairModes.DrAndCr,
                [AccountTaxonomyBuiltIns.ReceivablesId, AccountTaxonomyBuiltIns.CashId],
                [AccountTaxonomyBuiltIns.RevenueId]),
            ["M1|1", "M1|2", "M2|1", "M2|2", "M3|1", "M3|2", "M5|1", "M5|2"]),

        // 借 A（多選）且整張傳票沒有任何 B 貸方：只有 M6 沒有 Revenue 貸方。
        "drNotCrMultiSelect" => (
            Rule(
                FilterRuleType.SpecialAccountCategoryPair,
                SpecialAccountCategoryPairModes.DrNotCr,
                [AccountTaxonomyBuiltIns.ReceivablesId, AccountTaxonomyBuiltIns.CashId],
                [AccountTaxonomyBuiltIns.RevenueId]),
            ["M6|1"]),

        // 貸 B 且整張傳票沒有 A 借方：M2（Cash 借）、M3（零用金借）、M4（Others 借）、
        // M7（履約保證金借）只輸出 B 貸方列。
        "notDrCrSingle" => (
            Rule(
                FilterRuleType.SpecialAccountCategoryPair,
                SpecialAccountCategoryPairModes.NotDrCr,
                [AccountTaxonomyBuiltIns.ReceivablesId],
                [AccountTaxonomyBuiltIns.RevenueId]),
            ["M2|2", "M3|2", "M4|2", "M7|2"]),

        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown pair scenario.")
    };

    private static FilterRuleSpec Rule(
        FilterRuleType type,
        string pairMode,
        IReadOnlyList<string> debitCategoryIds,
        IReadOnlyList<string> creditCategoryIds) =>
        new FilterRuleSpec(
            FilterJoin.And,
            type,
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
            DebitCategoryIds = debitCategoryIds,
            CreditCategoryIds = creditCategoryIds
        };

    private static FilterScenarioSpec SingleRule(FilterRuleSpec rule) =>
        new(
            "account pair category selection matrix",
            "hand-calculated row identity oracle",
            [new FilterGroupSpec(FilterJoin.And, [rule])]);
}
