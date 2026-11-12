using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.AuditCore;

/// <summary>
/// 搬遷程式時留下的 golden characterization：鎖住條件左折疊、參數與 null-record SQL，
/// 讓 namespace／檔案位置改變時不會順帶改變 SQL 行為。
/// </summary>
public sealed class FilterCompilationTests
{
    private static readonly FilterConditionContext Context =
        new(10_000, null, "2025-01-01", "2025-12-31");

    [Fact]
    public void BuildWhere_MixedKnownJoins_PreservesLeftFoldSqlAndParameters()
    {
        // Golden oracle：第一個 rule/group join 忽略；其後逐邊左折疊，組內先 OR，組間再 OR、AND。
        var builder = new GlFilterWhereBuilder(
            SqliteDialect.Instance,
            new GlRulePredicates(SqliteDialect.Instance, GlPopulationScopeSql.Predicate));
        var scenario = new FilterScenarioSpec(
            "混合 join",
            "鎖住既有逐邊左折疊",
            [
                new FilterGroupSpec(FilterJoin.And,
                [
                    TextRule("alpha"),
                    AmountRule(100) with { Join = FilterJoin.Or }
                ]),
                new FilterGroupSpec(FilterJoin.Or,
                [
                    ManualRule(true)
                ]),
                new FilterGroupSpec(FilterJoin.And,
                [
                    DrCrRule("debit")
                ])
            ]);

        var plan = builder.BuildPlan(scenario, Context);

        Assert.Equal(
            "(((((instr(UPPER(COALESCE(g.description, '')), @p0) > 0) OR " +
            "(ABS(g.amount_scaled) >= @p1))) OR (g.is_manual = @p2)) AND (g.dr_cr = @p3))",
            plan.Sql);
        Assert.Collection(
            plan.Parameters,
            parameter => AssertParameter(parameter, "@p0", "ALPHA"),
            parameter => AssertParameter(parameter, "@p1", 100L),
            parameter => AssertParameter(parameter, "@p2", 1),
            parameter => AssertParameter(parameter, "@p3", "DEBIT"));
    }

    [Fact]
    public void PopulationScopePredicate_AuditPeriod_ReturnsCanonicalSql()
    {
        Assert.Equal(
            "g.is_excluded = 0",
            GlPopulationScopeSql.Predicate(Context, "g"));
    }

    [Fact]
    public void PopulationScopeParameters_AddedTwice_RemainEmpty()
    {
        var parameters = new FilterSqlParameterPlanBuilder(SqliteDialect.Instance);

        GlPopulationScopeSql.AddParameters(parameters, Context);
        GlPopulationScopeSql.AddParameters(parameters, Context);
        var plan = parameters.Build(GlPopulationScopeSql.Predicate(Context, "g"));

        Assert.Empty(plan.Parameters);
        Assert.Equal("g.is_excluded = 0", plan.Sql);
    }

    [Fact]
    public void PopulationScopePredicate_UnknownScope_FailsClosed()
    {
        var unknown = Context with { PopulationScope = (GlPopulationScope)int.MaxValue };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => GlPopulationScopeSql.Predicate(unknown, "g"));
    }

    [Fact]
    public void BuildPlan_WhenScopeParametersAreBoundByCaller_PreservesPredicateParameterZero()
    {
        var builder = new GlFilterWhereBuilder(
            SqliteDialect.Instance,
            new GlRulePredicates(SqliteDialect.Instance, GlPopulationScopeSql.Predicate));
        var scenario = new FilterScenarioSpec(
            "prescreen page",
            "parameter order",
            [
                new FilterGroupSpec(
                    FilterJoin.And,
                    [TextRule("alpha")])
            ]);

        var plan = builder.BuildPlan(
            scenario,
            Context,
            includePopulationParameters: false);

        Assert.Contains("@p0", plan.Sql, StringComparison.Ordinal);
        Assert.Collection(
            plan.Parameters,
            parameter => AssertParameter(parameter, "@p0", "ALPHA"));
    }

    [Fact]
    public void BlankRecordCategories_AllOrder_IsLocked()
    {
        Assert.Equal(
            [
                BlankRecordCategory.BlankAccountNumber,
                BlankRecordCategory.BlankVoucherNumber,
                BlankRecordCategory.BlankDescription,
                BlankRecordCategory.ApprovalDateNotInPeriod
            ],
            BlankRecordsCategoryPredicate.All);
    }

    // 空白判定走 SQLite 方言的 Trim，去掉的字元集合和 .NET 相同。
    [Theory]
    [InlineData(BlankRecordCategory.BlankAccountNumber, "account_number")]
    [InlineData(BlankRecordCategory.BlankVoucherNumber, "voucher_number")]
    [InlineData(BlankRecordCategory.BlankDescription, "description")]
    public void BlankRecordCategoryPredicates_ProviderVariants_UseDialectTrim(BlankRecordCategory category, string column)
    {
        // 等價分割：三種空白欄位各取一個代表；期外核准日另測。
        foreach (ISqlDialect dialect in new ISqlDialect[] { SqliteDialect.Instance })
        {
            Assert.Equal(
                $"({column} IS NULL OR {dialect.Trim(column)} = '')",
                BlankRecordsCategoryPredicate.For(category, dialect));
        }

        Assert.Contains("char(9,10,11,12,13,32,", SqliteDialect.Instance.Trim(column), StringComparison.Ordinal);
    }

    [Fact]
    public void BlankRecordCategoryPredicates_ApprovalDateNotInPeriod_IsTheSameOnEveryProvider()
    {
        // 照舊版 JET 的作法，核准日空白也算成不在期間。
        const string expected = "(approval_date IS NULL OR approval_date < @periodStart OR approval_date > @periodEnd)";
        Assert.Equal(expected, BlankRecordsCategoryPredicate.For(BlankRecordCategory.ApprovalDateNotInPeriod, SqliteDialect.Instance));
    }

    [Fact]
    public void BlankRecordCategoryScopedWhenChecked_UnavailableApprovalDate_NeverMatches_OtherCategoriesUnchanged()
    {
        // 傳票核准日不可用時「核准日不在期間」不執行；核准日空白會算成不在期間，
        // 所以這時計數與明細用永遠不成立的條件。其他三類與核准日可用時照原本的條件。
        foreach (ISqlDialect dialect in new ISqlDialect[] { SqliteDialect.Instance })
        {
            Assert.Equal(
                "(1 = 0)",
                BlankRecordsCategoryPredicate.ScopedWhenChecked(BlankRecordCategory.ApprovalDateNotInPeriod, dialect, approvalDateAvailable: false));
            Assert.Equal(
                BlankRecordsCategoryPredicate.Scoped(BlankRecordCategory.ApprovalDateNotInPeriod, dialect),
                BlankRecordsCategoryPredicate.ScopedWhenChecked(BlankRecordCategory.ApprovalDateNotInPeriod, dialect, approvalDateAvailable: true));
            foreach (var category in BlankRecordsCategoryPredicate.All.Where(static c => c != BlankRecordCategory.ApprovalDateNotInPeriod))
            {
                Assert.Equal(
                    BlankRecordsCategoryPredicate.Scoped(category, dialect),
                    BlankRecordsCategoryPredicate.ScopedWhenChecked(category, dialect, approvalDateAvailable: false));
            }
        }
    }

    [Fact]
    public void BlankRecordCategoryScope_RegularCategory_UsesGl()
    {
        Assert.Equal(
            "(is_excluded = 0) AND " +
            $"((account_number IS NULL OR {SqliteDialect.Instance.Trim("account_number")} = ''))",
            BlankRecordsCategoryPredicate.Scoped(BlankRecordCategory.BlankAccountNumber, SqliteDialect.Instance));
    }

    [Fact]
    public void PreparerEqualsApprover_LocalProviders_KeepPlainUpper()
    {
        foreach (ISqlDialect dialect in new ISqlDialect[] { SqliteDialect.Instance })
        {
            var preparer = dialect.Trim("g.created_by");
            var approver = dialect.Trim("g.approved_by");
            // 「不是空白」走 SqlBlankText，自帶一層括號。
            Assert.Equal(
                $"((g.created_by IS NOT NULL AND {preparer} <> '') AND g.approved_by IS NOT NULL "
                + $"AND UPPER({preparer}) = UPPER({approver}))",
                new GlRulePredicates(dialect, GlPopulationScopeSql.Predicate).PreparerEqualsApprover());
        }
    }

    private static FilterConditionSpec TextRule(string keyword) =>
        new(FilterJoin.And, FilterConditionType.Text, null, "description", [keyword], TextMatchMode.Contains,
            null, null, null, null, null, null);

    private static FilterConditionSpec AmountRule(long fromScaled) =>
        new(FilterJoin.And, FilterConditionType.NumRange, null, "amount", [], TextMatchMode.Contains,
            null, null, fromScaled, null, null, null);

    private static FilterConditionSpec ManualRule(bool isManual) =>
        new(FilterJoin.And, FilterConditionType.ManualAuto, null, null, [], TextMatchMode.Contains,
            null, null, null, null, null, isManual);

    private static FilterConditionSpec DrCrRule(string drCr) =>
        new(FilterJoin.And, FilterConditionType.DrCrOnly, null, null, [], TextMatchMode.Contains,
            null, null, null, null, drCr, null);

    private static void AssertParameter(FilterSqlParameter parameter, string name, object value)
    {
        Assert.Equal(name, parameter.Name);
        Assert.Equal(value, parameter.Value);
    }
}
