using System.Text.Json;
using FsCheck.Xunit;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.AuditCore;

/// <summary>
/// typed dynamic rule（2026-08-14 契約凍結）的 provider-neutral SQL plan 契約：
/// blank＝missing value row、負向 operator 是 value row 自身述詞（EXISTS 內取負，不重讀成
/// voucher-level NOT EXISTS）、fieldId 與 operand 一律參數綁定、text trim＋UPPER、money 依
/// amountBasis 於 scaled integer 域比較、in/notIn 依型別正規化去重、sameVoucher 同構不變、
/// 2,000 參數預算不變。
/// </summary>
public sealed class TypedFieldCompilationTests
{
    private const string TextFieldId = "rde.aaaa0000aaaa0000aaaa0000aaaa0000";
    private const string DateFieldId = "rde.bbbb0000bbbb0000bbbb0000bbbb0000";
    private const string MoneyFieldId = "rde.cccc0000cccc0000cccc0000cccc0000";

    private static readonly FilterRuleContext Context =
        new(100, null, "2025-01-01", "2025-12-31", PopulationScope: GlPopulationScope.AuditPeriod)
        {
            RdeFields =
            [
                new GlRdeFieldMetadata(TextFieldId, "備註欄", "備註", RdeFieldValueTypeNames.Text),
                new GlRdeFieldMetadata(DateFieldId, "審核日欄", "審核日", RdeFieldValueTypeNames.Date),
                new GlRdeFieldMetadata(MoneyFieldId, "稅額欄", "稅額", RdeFieldValueTypeNames.Money)
            ]
        };

    [Fact]
    public void TextEquals_ComparesTrimmedUpperOnBothSides_AndBindsFieldIdAsParameter()
    {
        var plan = Compile(Rule(TextFieldId, "equals", value: "  Alpha  "));

        Assert.Contains(
            "EXISTS (SELECT 1 FROM target_gl_rde_value v "
            + "WHERE v.entry_id = g.entry_id AND v.field_id = @p0 "
            + "AND v.value_type = @p1 AND UPPER(TRIM(v.text_value)) = @p2)",
            plan.Sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain(TextFieldId, plan.Sql, StringComparison.Ordinal);
        Assert.Equal(
            [TextFieldId, "text", "ALPHA"],
            plan.Parameters.Select(static parameter => parameter.Value).ToArray());
    }

    [Fact]
    public void NegativeOperators_StayInsideExists_SoBlankRowsNeverMatch()
    {
        // (entry_id, field_id) 是主鍵：EXISTS + 內部取負 = 「有值且值不符」，
        // blank（missing row）不命中負向 operator——不得編成外層 NOT EXISTS。
        var notEquals = Compile(Rule(TextFieldId, "notEquals", value: "Alpha"));
        var notContains = Compile(Rule(TextFieldId, "notContains", value: "Alpha"));
        var notIn = Compile(Rule(TextFieldId, "notIn", values: ["A", "B"]));

        foreach (var plan in new[] { notEquals, notContains, notIn })
        {
            Assert.StartsWith(
                "EXISTS (SELECT 1 FROM target_gl_rde_value v",
                TrimOuter(plan.Sql),
                StringComparison.Ordinal);
            Assert.DoesNotContain("NOT EXISTS", plan.Sql, StringComparison.Ordinal);
        }

        Assert.Contains("UPPER(TRIM(v.text_value)) <> @p2", notEquals.Sql, StringComparison.Ordinal);
        Assert.Contains(
            "NOT instr(UPPER(COALESCE(TRIM(v.text_value), '')), @p2) > 0",
            notContains.Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "UPPER(TRIM(v.text_value)) NOT IN (@p2, @p3)",
            notIn.Sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BlankOperators_CompileToRowExistence_WithFieldIdParameterOnly()
    {
        var isBlank = Compile(Rule(TextFieldId, "isBlank"));
        var isNotBlank = Compile(Rule(MoneyFieldId, "isNotBlank", amountBasis: "signed"));

        Assert.Contains(
            "NOT EXISTS (SELECT 1 FROM target_gl_rde_value v "
            + "WHERE v.entry_id = g.entry_id AND v.field_id = @p0)",
            isBlank.Sql,
            StringComparison.Ordinal);
        Assert.Equal([TextFieldId], isBlank.Parameters.Select(static p => p.Value).ToArray());

        Assert.Contains(
            "EXISTS (SELECT 1 FROM target_gl_rde_value v "
            + "WHERE v.entry_id = g.entry_id AND v.field_id = @p0)",
            isNotBlank.Sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain("NOT EXISTS", isNotBlank.Sql, StringComparison.Ordinal);
        Assert.Equal([MoneyFieldId], isNotBlank.Parameters.Select(static p => p.Value).ToArray());
    }

    [Fact]
    public void DateBetween_UsesNormalizedIsoBoundsInclusive()
    {
        var plan = Compile(Rule(DateFieldId, "between", from: "2025-01-01", to: "2025-06-30"));

        Assert.Contains(
            "(v.date_value >= @p2 AND v.date_value <= @p3)",
            plan.Sql,
            StringComparison.Ordinal);
        Assert.Equal(
            [DateFieldId, "date", "2025-01-01", "2025-06-30"],
            plan.Parameters.Select(static parameter => parameter.Value).ToArray());
    }

    [Theory]
    [InlineData("signed", "v.amount_scaled")]
    [InlineData("absolute", "ABS(v.amount_scaled)")]
    public void MoneyComparison_RespectsAmountBasisInScaledIntegerDomain(
        string amountBasis,
        string expectedColumn)
    {
        var plan = Compile(Rule(
            MoneyFieldId, "greaterThanOrEqual", value: "-12.34", amountBasis: amountBasis));

        Assert.Contains($"{expectedColumn} >= @p2", plan.Sql, StringComparison.Ordinal);
        Assert.Equal(
            [MoneyFieldId, "money", -1234L],
            plan.Parameters.Select(static parameter => parameter.Value).ToArray());
    }

    [Fact]
    public void TextInValues_DeduplicateByTrimmedCaseInsensitiveKey_PreservingFirstOrder()
    {
        var plan = Compile(Rule(
            TextFieldId, "in", values: ["  Alpha ", "BETA", "alpha", "beta ", "Gamma"]));

        Assert.Contains(
            "UPPER(TRIM(v.text_value)) IN (@p2, @p3, @p4)",
            plan.Sql,
            StringComparison.Ordinal);
        Assert.Equal(
            [TextFieldId, "text", "ALPHA", "BETA", "GAMMA"],
            plan.Parameters.Select(static parameter => parameter.Value).ToArray());
    }

    [Fact]
    public void InjectionShapedOperands_OnlyEverBecomeParameters()
    {
        const string injection = "x'; DROP TABLE target_gl_entry; --";
        var text = Compile(Rule(TextFieldId, "contains", value: injection));

        Assert.DoesNotContain("DROP TABLE", text.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            injection.Trim().ToUpperInvariant(),
            text.Parameters.Select(static parameter => parameter.Value?.ToString()));
    }

    [Fact]
    public void SameVoucherEvidence_ReusesTypedPredicateInsideVoucherSubquery()
    {
        var scenario = Parse(
            $$"""
            {"name":"typed-sv","rationale":"compile","groups":[{"matchScope":"sameVoucher","rules":[
              {"join":"AND","type":"drCrOnly","drCr":"credit"},
              {"join":"AND","type":"typed","fieldId":"{{TextFieldId}}","operator":"equals","value":"EVIDENCE"}
            ]}]}
            """);

        var plan = Builder().BuildPlan(scenario, Context, zeroModulus: 1_000_000);

        // 第一條仍是輸出列錨點；typed 佐證規則落在同傳票有效母體子查詢內，
        // 且 typed 述詞的 EXISTS 相關到子查詢的 g（沿用既有 alias 重用同構）。
        Assert.Contains("g.document_number IN (", plan.Sql, StringComparison.Ordinal);
        Assert.Contains("FROM target_gl_entry g", plan.Sql, StringComparison.Ordinal);
        Assert.Contains("g.is_effective = 1", plan.Sql, StringComparison.Ordinal);
        Assert.Contains(
            "EXISTS (SELECT 1 FROM target_gl_rde_value v WHERE v.entry_id = g.entry_id",
            plan.Sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownField_FailsLoudNamingTheField_EvenWithoutValidation()
    {
        const string removed = "rde.dddd0000dddd0000dddd0000dddd0000";
        var exception = Assert.Throws<JetActionException>(
            () => Compile(Rule(removed, "equals", value: "x")));

        Assert.Equal(JetErrorCodes.InvalidScenario, exception.Code);
        Assert.Contains(removed, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OperatorIncompatibleWithCurrentType_FailsLoudNamingTheField()
    {
        // type-changed lifecycle 的編譯端最後防線：validator 被繞過（materialize 重新解析
        // 已存 JSON）時仍不得編出錯型別 SQL。
        var exception = Assert.Throws<JetActionException>(
            () => Compile(Rule(MoneyFieldId, "contains", value: "x", amountBasis: "signed")));

        Assert.Equal(JetErrorCodes.InvalidScenario, exception.Code);
        Assert.Contains(MoneyFieldId, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyRegistry_FailsLoudInsteadOfCompilingSql()
    {
        var context = new FilterRuleContext(
            100, null, "2025-01-01", "2025-12-31", PopulationScope: GlPopulationScope.AuditPeriod);

        var exception = Assert.Throws<JetActionException>(() => Builder().BuildPlan(
            Scenario(Rule(TextFieldId, "equals", value: "x")), context, zeroModulus: 1_000_000));

        Assert.Equal(JetErrorCodes.InvalidScenario, exception.Code);
    }

    [Fact]
    public void CompiledParameterBudget_StillCapsTypedScenariosAtTwoThousand()
    {
        // 20 條 in×100 值 = 2,000 operand 參數，加上每條 field/type 參數必然超限。
        var rules = Enumerable.Range(0, 20)
            .Select(ruleIndex => Rule(
                TextFieldId,
                "in",
                values: Enumerable.Range(0, 100)
                    .Select(valueIndex => $"v{ruleIndex}_{valueIndex}")
                    .ToArray()))
            .ToArray();
        var scenario = new FilterScenarioSpec(
            "typed-budget", "budget", [new FilterGroupSpec(FilterJoin.And, rules)]);

        var exception = Assert.Throws<JetActionException>(
            () => Builder().BuildPlan(scenario, Context, zeroModulus: 1_000_000));

        Assert.Equal(JetErrorCodes.InvalidScenario, exception.Code);
        Assert.Contains(
            FilterScenarioLimits.MaxCompiledParameters.ToString(),
            exception.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Property：in/notIn 的 values 是集合語意——任何重複與大小寫／trim 變形都必須得到
    /// 同一份 SQL 與參數序列；money 另在 scaled 域判定（"5" 與 "5.00" 等價）。
    /// </summary>
    [Property(Replay = "20260814,11", MaxTest = 128)]
    public bool TextInValues_AreSetSemantics_UnderDuplicationAndCaseNoise(int[] picks, int rotation)
    {
        string[] pool = ["Alpha", "beta", "GAMMA", "delta value", "épsilon"];
        var selected = (picks ?? [])
            .Select(pick => pool[Math.Abs(pick % pool.Length)])
            .ToArray();
        if (selected.Length == 0)
        {
            selected = [pool[0]];
        }

        var noisy = selected
            .Select((value, index) => index % 2 == 0 ? $"  {value.ToUpperInvariant()} " : value)
            .Concat(selected.Take(Math.Abs(rotation % (selected.Length + 1))))
            .ToArray();

        var canonical = Compile(Rule(
            TextFieldId, "in", values: selected.Select(static v => v.ToUpperInvariant()).ToArray()));
        var perturbed = Compile(Rule(TextFieldId, "in", values: noisy));

        return string.Equals(canonical.Sql, perturbed.Sql, StringComparison.Ordinal)
            && canonical.Parameters.Select(static p => p.Value)
                .SequenceEqual(perturbed.Parameters.Select(static p => p.Value));
    }

    [Fact]
    public void MoneyEquivalentLexemes_BindIdenticalScaledParameters()
    {
        var plain = Compile(Rule(MoneyFieldId, "equals", value: "5", amountBasis: "signed"));
        var decorated = Compile(Rule(MoneyFieldId, "equals", value: "5.00", amountBasis: "signed"));

        Assert.Equal(plain.Sql, decorated.Sql);
        Assert.Equal(
            plain.Parameters.Select(static p => p.Value).ToArray(),
            decorated.Parameters.Select(static p => p.Value).ToArray());
    }

    private static GlFilterWhereBuilder Builder() =>
        new(
            SqliteDialect.Instance,
            new GlRulePredicates(SqliteDialect.Instance, GlPopulationScopeSql.Predicate));

    private static FilterSqlFragmentPlan Compile(FilterRuleSpec rule) =>
        Builder().BuildPlan(Scenario(rule), Context, zeroModulus: 1_000_000);

    private static FilterScenarioSpec Scenario(FilterRuleSpec rule) =>
        new("typed", "compile", [new FilterGroupSpec(FilterJoin.And, [rule])]);

    private static FilterScenarioSpec Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return FilterScenarioPayloadParser.Parse(document.RootElement, moneyScale: 100);
    }

    private static string TrimOuter(string sql) => sql.TrimStart('(');

    private static FilterRuleSpec Rule(
        string fieldId,
        string op,
        string? value = null,
        string? from = null,
        string? to = null,
        string[]? values = null,
        string? amountBasis = null) =>
        new(
            FilterJoin.And,
            FilterRuleType.TypedField,
            PrescreenKey: null,
            Field: null,
            Keywords: [],
            Mode: TextMatchMode.Contains,
            FromDate: null,
            ToDate: null,
            FromAmountScaled: null,
            ToAmountScaled: null,
            DrCr: null,
            IsManual: null)
        {
            FieldId = fieldId,
            TypedOperator = op,
            TypedValue = value,
            TypedFrom = from,
            TypedTo = to,
            TypedValues = values,
            AmountBasis = amountBasis
        };
}
