using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.AuditCore;

/// <summary>
/// 第三輪搬遷的 golden characterization：鎖住條件左折疊、參數與 null-record SQL，
/// 讓 namespace／檔案位置改變時不會順帶改變 SQL 行為。
/// </summary>
public sealed class FilterCompilationTests
{
    private static readonly FilterRuleContext Context =
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
            "(((((instr(UPPER(COALESCE(g.document_description, '')), @p0) > 0) OR " +
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
            "g.is_effective = 1",
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
        Assert.Equal("g.is_effective = 1", plan.Sql);
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
    public void NullRecordCategories_AllOrder_IsLocked()
    {
        Assert.Equal(
            [
                NullRecordCategory.NullAccount,
                NullRecordCategory.NullDocument,
                NullRecordCategory.NullDescription,
                NullRecordCategory.OutOfRangeDate
            ],
            NullRecordsCategoryPredicate.All);
    }

    // 2026-10-04 第二遍回饋審閱第 2 批（C3）：空白判定改走方言的 Trim，去掉的字元集合和 .NET 相同，三個 provider 一致。
    // 原本斷言的是引擎原生 TRIM 與 LTRIM(RTRIM)（第一次失敗：收據 20261004-045437671），這裡改成以方言組出預期字串。
    [Theory]
    [InlineData(NullRecordCategory.NullAccount, "account_code")]
    [InlineData(NullRecordCategory.NullDocument, "document_number")]
    [InlineData(NullRecordCategory.NullDescription, "document_description")]
    public void NullRecordCategoryPredicates_ProviderVariants_UseDialectTrim(NullRecordCategory category, string column)
    {
        // 等價分割：三種空白欄位各取一個代表；期外核准日另測。
        foreach (ISqlDialect dialect in new ISqlDialect[] { SqliteDialect.Instance, DuckDbDialect.Instance, SqlServerDialect.Instance })
        {
            Assert.Equal(
                $"({column} IS NULL OR {dialect.Trim(column)} = '')",
                NullRecordsCategoryPredicate.For(category, dialect));
        }

        Assert.Contains("char(9,10,11,12,13,32,", SqliteDialect.Instance.Trim(column), StringComparison.Ordinal);
        Assert.Contains("chr(12288)", DuckDbDialect.Instance.Trim(column), StringComparison.Ordinal);
        Assert.Contains("NCHAR(160)", SqlServerDialect.Instance.Trim(column), StringComparison.Ordinal);
    }

    [Fact]
    public void NullRecordCategoryPredicates_OutOfRangeDate_IsTheSameOnEveryProvider()
    {
        const string expected = "(approval_date IS NOT NULL AND (approval_date < @periodStart OR approval_date > @periodEnd))";
        Assert.Equal(expected, NullRecordsCategoryPredicate.For(NullRecordCategory.OutOfRangeDate, SqliteDialect.Instance));
        Assert.Equal(expected, NullRecordsCategoryPredicate.For(NullRecordCategory.OutOfRangeDate, SqlServerDialect.Instance));
    }

    [Fact]
    public void NullRecordCategoryScope_RegularCategory_UsesEffectivePopulation()
    {
        Assert.Equal(
            "(is_effective = 1) AND " +
            $"((account_code IS NULL OR {SqliteDialect.Instance.Trim("account_code")} = ''))",
            NullRecordsCategoryPredicate.Scoped(NullRecordCategory.NullAccount, SqliteDialect.Instance));
    }

    [Fact]
    public void NullRecordCategoryScope_SqlServerRegularCategory_UsesEffectivePopulation()
    {
        Assert.Equal(
            "(is_effective = 1) AND " +
            $"((account_code IS NULL OR {SqlServerDialect.Instance.Trim("account_code")} = ''))",
            NullRecordsCategoryPredicate.Scoped(NullRecordCategory.NullAccount, SqlServerDialect.Instance));
    }

    // 2026-10-19 跨資料庫一致性計畫第三節 D：轉大寫改走方言。SQLite 與 DuckDB 的 SQL 文字不變；
    // SQL Server 改用 Latin1_General_100_BIN2 的大小寫對照表，欄位本身的比較與排序仍是 Latin1_General_BIN2。
    // 第一次失敗：20261019-025219572-8db61eaea8e248329c0efa0328ca3212，SQL Server 的一項在方言修改前失敗。
    [Fact]
    public void PreparerEqualsApprover_LocalProviders_KeepPlainUpper()
    {
        foreach (ISqlDialect dialect in new ISqlDialect[] { SqliteDialect.Instance, DuckDbDialect.Instance })
        {
            var creator = dialect.Trim("g.created_by");
            var approver = dialect.Trim("g.approved_by");
            // 2026-10-22 技術債一輪：「不是空白」改走 SqlBlankText，自帶一層括號（第一次失敗：Public 收據 20261022-075335566）。
            Assert.Equal(
                $"((g.created_by IS NOT NULL AND {creator} <> '') AND g.approved_by IS NOT NULL "
                + $"AND UPPER({creator}) = UPPER({approver}))",
                new GlRulePredicates(dialect, GlPopulationScopeSql.Predicate).PreparerEqualsApprover());
        }
    }

    [Fact]
    public void PreparerEqualsApprover_SqlServer_UppercasesWithTheNewerBinaryCollation()
    {
        var dialect = SqlServerDialect.Instance;
        var creator = dialect.Trim("g.created_by");
        var approver = dialect.Trim("g.approved_by");

        Assert.Equal(
            $"((g.created_by IS NOT NULL AND {creator} <> '') AND g.approved_by IS NOT NULL "
            + $"AND UPPER(({creator}) COLLATE Latin1_General_100_BIN2) = UPPER(({approver}) COLLATE Latin1_General_100_BIN2))",
            new GlRulePredicates(dialect, GlPopulationScopeSql.Predicate).PreparerEqualsApprover());
    }

    private static FilterRuleSpec TextRule(string keyword) =>
        new(FilterJoin.And, FilterRuleType.Text, null, "description", [keyword], TextMatchMode.Contains,
            null, null, null, null, null, null);

    private static FilterRuleSpec AmountRule(long fromScaled) =>
        new(FilterJoin.And, FilterRuleType.NumRange, null, "amount", [], TextMatchMode.Contains,
            null, null, fromScaled, null, null, null);

    private static FilterRuleSpec ManualRule(bool isManual) =>
        new(FilterJoin.And, FilterRuleType.ManualAuto, null, null, [], TextMatchMode.Contains,
            null, null, null, null, null, isManual);

    private static FilterRuleSpec DrCrRule(string drCr) =>
        new(FilterJoin.And, FilterRuleType.DrCrOnly, null, null, [], TextMatchMode.Contains,
            null, null, null, null, drCr, null);

    private static void AssertParameter(FilterSqlParameter parameter, string name, object value)
    {
        Assert.Equal(name, parameter.Name);
        Assert.Equal(value, parameter.Value);
    }
}
