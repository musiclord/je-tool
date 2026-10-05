using FsCheck.Xunit;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.AuditCore;

/// <summary>
/// 借貸組合雙側多選的編譯契約：查核員選取的分類身分只用來 lookup semantic role、一律綁定為參數，
/// 商業比較仍只看 role；同一組分類的任何排列或重複都編譯出逐字相同的 SQL 與參數序列。
/// legacy scalar 只是「單元素集合」的相容輸入，不另走一條 SQL。
/// 空集合必須 fail closed——否定模式的 NOT EXISTS 遇到空集合會反轉成全命中。
/// </summary>
public sealed class AccountPairCategorySelectionCompilationTests
{
    private static readonly FilterRuleContext Context =
        new(100, null, "2025-01-01", "2025-12-31");

    private const string CustomCashId = "custom.0123456789abcdef0123456789abcdef";

    [Fact]
    public void AccountPair_MultiSelectDebitSide_BindsEveryCategoryIdAsParameter()
    {
        var plan = Compile(PairRule(
            FilterRuleType.AccountPair,
            AccountPairModes.Exact,
            [AccountTaxonomyBuiltIns.CashId, AccountTaxonomyBuiltIns.ReceivablesId],
            [AccountTaxonomyBuiltIns.RevenueId]));

        // 借方兩類、貸方一類：每個身分各自成為一個綁定參數，SQL 內沒有任何 category 常值，
        // 而且比較的是 taxonomy 解析出來的 semantic role，不是身分本身。
        Assert.Contains(
            "td.semantic_role IN (SELECT ts.semantic_role FROM config_account_taxonomy ts "
            + "WHERE ts.category_id IN (@p0, @p1))",
            plan.Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "tc.semantic_role IN (SELECT ts.semantic_role FROM config_account_taxonomy ts "
            + "WHERE ts.category_id IN (@p2))",
            plan.Sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain(AccountTaxonomyBuiltIns.CashId + "'", plan.Sql, StringComparison.Ordinal);
        Assert.Equal(
            [
                AccountTaxonomyBuiltIns.CashId,
                AccountTaxonomyBuiltIns.ReceivablesId,
                AccountTaxonomyBuiltIns.RevenueId,
                AccountTaxonomyBuiltIns.CashId,
                AccountTaxonomyBuiltIns.ReceivablesId,
                AccountTaxonomyBuiltIns.RevenueId
            ],
            plan.Parameters.Select(static parameter => (string)parameter.Value!).ToArray());
    }

    [Fact]
    public void AccountPair_SelectedCategoryIdentitiesResolveToSemanticRoles()
    {
        // 自訂分類同樣只以身分 lookup role；SQL 永遠不把身分或顯示 label 當成比較對象，
        // 因此改名不改命中，而相同 role 的分類會得到同一個商業結果。
        var plan = Compile(PairRule(
            FilterRuleType.AccountPair,
            AccountPairModes.DebitAnchor,
            [CustomCashId],
            []));

        Assert.Contains("semantic_role IN (SELECT ts.semantic_role", plan.Sql, StringComparison.Ordinal);
        // 顯示 label 只出現在「尚未 backfill 的 legacy row」那條 join 分支，永遠不與使用者輸入比較。
        Assert.DoesNotContain("label = @", plan.Sql, StringComparison.Ordinal);
        Assert.All(
            plan.Parameters.Select(static parameter => (string)parameter.Value!),
            value => Assert.Equal(CustomCashId, value));
    }

    [Theory]
    [InlineData(FilterRuleType.AccountPair, AccountPairModes.Exact)]
    [InlineData(FilterRuleType.AccountPair, AccountPairModes.DebitAnchor)]
    [InlineData(FilterRuleType.AccountPair, AccountPairModes.CreditAnchor)]
    [InlineData(FilterRuleType.SpecialAccountCategoryPair, SpecialAccountCategoryPairModes.DrAndCr)]
    [InlineData(FilterRuleType.SpecialAccountCategoryPair, SpecialAccountCategoryPairModes.DrNotCr)]
    [InlineData(FilterRuleType.SpecialAccountCategoryPair, SpecialAccountCategoryPairModes.NotDrCr)]
    public void PairRule_CategoryOrderAndDuplicates_ProduceIdenticalPlan(
        FilterRuleType type,
        string pairMode)
    {
        var canonical = Compile(PairRule(
            type,
            pairMode,
            [AccountTaxonomyBuiltIns.CashId, AccountTaxonomyBuiltIns.ReceivablesId],
            [AccountTaxonomyBuiltIns.RevenueId, CustomCashId]));
        var shuffled = Compile(PairRule(
            type,
            pairMode,
            [AccountTaxonomyBuiltIns.ReceivablesId, AccountTaxonomyBuiltIns.CashId, AccountTaxonomyBuiltIns.CashId],
            [CustomCashId, AccountTaxonomyBuiltIns.RevenueId, CustomCashId]));

        Assert.Equal(canonical.Sql, shuffled.Sql);
        Assert.Equal(
            canonical.Parameters.Select(static parameter => (string)parameter.Value!).ToArray(),
            shuffled.Parameters.Select(static parameter => (string)parameter.Value!).ToArray());
    }

    [Theory]
    [InlineData(SpecialAccountCategoryPairModes.DrAndCr)]
    [InlineData(SpecialAccountCategoryPairModes.DrNotCr)]
    [InlineData(SpecialAccountCategoryPairModes.NotDrCr)]
    public void SpecialPair_EmptyCategorySelection_FailsClosedInsteadOfMatchingEveryVoucher(string pairMode)
    {
        var rule = PairRule(
            FilterRuleType.SpecialAccountCategoryPair,
            pairMode,
            [],
            []);

        var exception = Assert.Throws<InvalidOperationException>(() => Compile(rule));

        Assert.Contains("分類集合為空", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Property：分類選擇是集合語意。任何重複與排列都必須得到同一份 SQL 與參數序列，
    /// 否則同一情境會因使用者的點選順序產生不同的可稽核 SQL。失敗時 seed 由固定 Replay 重現。
    /// </summary>
    [Property(Replay = "20260814,7", MaxTest = 128)]
    public bool PairSelection_IsSetSemantics_UnderShufflingAndDuplication(
        int[] debitPicks,
        int[] creditPicks,
        int rotation)
    {
        var debit = Selection(debitPicks);
        var credit = Selection(creditPicks);
        var canonical = Compile(PairRule(
            FilterRuleType.AccountPair,
            AccountPairModes.Exact,
            debit,
            credit));
        var rearranged = Compile(PairRule(
            FilterRuleType.AccountPair,
            AccountPairModes.Exact,
            Rearrange(debit, rotation),
            Rearrange(credit, rotation)));

        return string.Equals(canonical.Sql, rearranged.Sql, StringComparison.Ordinal)
            && canonical.Parameters
                .Select(static parameter => (string)parameter.Value!)
                .SequenceEqual(
                    rearranged.Parameters.Select(static parameter => (string)parameter.Value!),
                    StringComparer.Ordinal);
    }

    private static readonly string[] CategoryPool =
    [
        AccountTaxonomyBuiltIns.RevenueId,
        AccountTaxonomyBuiltIns.ReceivablesId,
        AccountTaxonomyBuiltIns.CashId,
        AccountTaxonomyBuiltIns.ReceiptInAdvanceId,
        AccountTaxonomyBuiltIns.OthersId,
        CustomCashId
    ];

    /// <summary>把任意產生的整數投影成非空的分類身分集合（生成器不決定業務語意）。</summary>
    private static string[] Selection(int[] picks)
    {
        var selected = (picks ?? [])
            .Select(pick => CategoryPool[Math.Abs(pick % CategoryPool.Length)])
            .ToArray();
        return selected.Length == 0 ? [CategoryPool[0]] : selected;
    }

    private static string[] Rearrange(IReadOnlyList<string> values, int rotation)
    {
        var offset = Math.Abs(rotation % values.Count);
        var rotated = values.Skip(offset).Concat(values.Take(offset)).ToList();
        rotated.Add(values[^1]);
        return [.. rotated];
    }

    private static FilterRuleSpec PairRule(
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

    private static FilterSqlFragmentPlan Compile(FilterRuleSpec rule)
    {
        var builder = new GlFilterWhereBuilder(
            SqliteDialect.Instance,
            new GlRulePredicates(SqliteDialect.Instance, GlPopulationScopeSql.Predicate));
        var scenario = new FilterScenarioSpec(
            "借貸組合多選",
            "編譯契約",
            [new FilterGroupSpec(FilterJoin.And, [rule])]);
        // 母體參數與本測試無關，排除後參數索引即為分類綁定順序。
        return builder.BuildPlan(
            scenario,
            Context,
            zeroModulus: 1_000_000,
            includePopulationParameters: false);
    }
}
