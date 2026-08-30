using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

/// <summary>
/// typed dynamic rule（2026-08-14 契約凍結）的 Domain 驗證矩陣：field registry、per-type
/// operator 集合、operand carrier 精確匹配、amountBasis、值格式與 RDE lifecycle 的
/// 「invalid_scenario 並指名 field」。oracle 是 manifest「Typed dynamic rule」凍結條目。
/// </summary>
public sealed class TypedFieldRuleValidationTests
{
    private const string TextFieldId = "rde.aaaa0000aaaa0000aaaa0000aaaa0000";
    private const string DateFieldId = "rde.bbbb0000bbbb0000bbbb0000bbbb0000";
    private const string MoneyFieldId = "rde.cccc0000cccc0000cccc0000cccc0000";

    private static readonly FilterValidationContext Context = new(
        HasLastPeriodStart: true,
        HasAccountMapping: false,
        HasAuthorizedPreparers: false)
    {
        RdeFields =
        [
            new GlRdeFieldMetadata(TextFieldId, "備註欄", "備註", RdeFieldValueTypeNames.Text),
            new GlRdeFieldMetadata(DateFieldId, "審核日欄", "審核日", RdeFieldValueTypeNames.Date),
            new GlRdeFieldMetadata(MoneyFieldId, "稅額欄", "稅額", RdeFieldValueTypeNames.Money)
        ],
        MoneyScale = 100
    };

    // ── operator × 型別矩陣：全部合法組合 ─────────────────────────────────────

    public static TheoryData<string, string, string?, string?, string?, string[]?, string?> ValidRules()
    {
        var data = new TheoryData<string, string, string?, string?, string?, string[]?, string?>
        {
            // text：8 operators
            { TextFieldId, "equals", "Alpha", null, null, null, null },
            { TextFieldId, "notEquals", "Alpha", null, null, null, null },
            { TextFieldId, "contains", "Al", null, null, null, null },
            { TextFieldId, "notContains", "Al", null, null, null, null },
            { TextFieldId, "in", null, null, null, new[] { "A", "B" }, null },
            { TextFieldId, "notIn", null, null, null, new[] { "A", "B" }, null },
            { TextFieldId, "isBlank", null, null, null, null, null },
            { TextFieldId, "isNotBlank", null, null, null, null, null },
            // date：8 operators
            { DateFieldId, "on", "2025-06-30", null, null, null, null },
            { DateFieldId, "before", "2025-06-30", null, null, null, null },
            { DateFieldId, "onOrBefore", "2025-06-30", null, null, null, null },
            { DateFieldId, "after", "2025-06-30", null, null, null, null },
            { DateFieldId, "onOrAfter", "2025-06-30", null, null, null, null },
            { DateFieldId, "between", null, "2025-01-01", "2025-12-31", null, null },
            { DateFieldId, "isBlank", null, null, null, null, null },
            { DateFieldId, "isNotBlank", null, null, null, null, null },
            // money：9 operators（比較 operator 必填 amountBasis；blank 家族亦必填——凍結
            // 契約要求「每條 money 規則明示 amountBasis」，不分 operator 種類）
            { MoneyFieldId, "equals", "100.50", null, null, null, "signed" },
            { MoneyFieldId, "notEquals", "100.50", null, null, null, "absolute" },
            { MoneyFieldId, "greaterThan", "-3", null, null, null, "signed" },
            { MoneyFieldId, "greaterThanOrEqual", "0", null, null, null, "absolute" },
            { MoneyFieldId, "lessThan", "1000000", null, null, null, "signed" },
            { MoneyFieldId, "lessThanOrEqual", "-0.01", null, null, null, "signed" },
            { MoneyFieldId, "between", null, "-10", "10", null, "signed" },
            { MoneyFieldId, "isBlank", null, null, null, null, "signed" },
            { MoneyFieldId, "isNotBlank", null, null, null, null, "absolute" },
        };
        return data;
    }

    [Theory]
    [MemberData(nameof(ValidRules))]
    public void FullOperatorMatrix_CanonicalShapes_AreValid(
        string fieldId,
        string op,
        string? value,
        string? from,
        string? to,
        string[]? values,
        string? amountBasis)
    {
        var errors = Validate(TypedRule(fieldId, op, value, from, to, values, amountBasis));

        Assert.Empty(errors);
    }

    // ── field registry 與 lifecycle：指名 field ──────────────────────────────

    [Fact]
    public void UnknownOrRemovedField_IsInvalidAndNamesTheField()
    {
        const string removed = "rde.dddd0000dddd0000dddd0000dddd0000";
        var errors = Validate(TypedRule(removed, "equals", value: "x"));

        var error = Assert.Single(errors);
        Assert.Contains(removed, error, StringComparison.Ordinal);
        Assert.Contains("不存在", error, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingFieldId_IsInvalid()
    {
        var errors = Validate(TypedRule(fieldId: null, op: "equals", value: "x"));

        Assert.Contains(errors, error => error.Contains("fieldId", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyRegistry_FailsClosedForAnyTypedRule()
    {
        var context = new FilterValidationContext(true, false, false);

        var errors = FilterScenarioValidator.Validate(
            Scenario(TypedRule(TextFieldId, "equals", value: "x")), context);

        Assert.Contains(errors, error => error.Contains(TextFieldId, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(TextFieldId, "on")]
    [InlineData(TextFieldId, "greaterThan")]
    [InlineData(DateFieldId, "contains")]
    [InlineData(DateFieldId, "greaterThan")]
    [InlineData(MoneyFieldId, "contains")]
    [InlineData(MoneyFieldId, "in")]
    [InlineData(MoneyFieldId, "on")]
    public void OperatorIncompatibleWithFieldType_IsInvalidAndNamesTheField(string fieldId, string op)
    {
        // type-changed lifecycle 的核心：operator 在封閉聯集內、但與目前欄位型別不相容，
        // 必須指名 field 要求使用者修正，不得靜默改讀或刪 definition。
        var errors = Validate(TypedRule(fieldId, op, value: "2025-01-01", amountBasis: null));

        var error = Assert.Single(errors);
        Assert.Contains(fieldId, error, StringComparison.Ordinal);
        Assert.Contains("不相容", error, StringComparison.Ordinal);
    }

    // ── operand carrier 精確匹配 ────────────────────────────────────────────

    [Theory]
    [InlineData("isBlank")]
    [InlineData("isNotBlank")]
    public void BlankOperators_WithAnyOperandCarrier_AreInvalid(string op)
    {
        Assert.NotEmpty(Validate(TypedRule(TextFieldId, op, value: "x")));
        Assert.NotEmpty(Validate(TypedRule(DateFieldId, op, from: "2025-01-01")));
        Assert.NotEmpty(Validate(TypedRule(DateFieldId, op, to: "2025-01-01")));
        Assert.NotEmpty(Validate(TypedRule(TextFieldId, op, values: ["x"])));
    }

    [Fact]
    public void SingleValueOperator_MissingValueOrExtraCarrier_IsInvalid()
    {
        Assert.Contains(
            Validate(TypedRule(TextFieldId, "equals")),
            error => error.Contains("必須提供 value", StringComparison.Ordinal));
        Assert.Contains(
            Validate(TypedRule(TextFieldId, "equals", value: "x", from: "y")),
            error => error.Contains("不得帶 from", StringComparison.Ordinal));
        Assert.Contains(
            Validate(TypedRule(TextFieldId, "equals", value: "x", values: ["y"])),
            error => error.Contains("不得帶 from、to 或 values", StringComparison.Ordinal));
    }

    [Fact]
    public void Between_RequiresBothBoundsAndForbidsOtherCarriers()
    {
        Assert.Contains(
            Validate(TypedRule(DateFieldId, "between", from: "2025-01-01")),
            error => error.Contains("同時提供 from 與 to", StringComparison.Ordinal));
        Assert.Contains(
            Validate(TypedRule(DateFieldId, "between", to: "2025-01-01")),
            error => error.Contains("同時提供 from 與 to", StringComparison.Ordinal));
        Assert.Contains(
            Validate(TypedRule(
                DateFieldId, "between", value: "x", from: "2025-01-01", to: "2025-12-31")),
            error => error.Contains("不得帶 value", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(DateFieldId, "2025-12-31", "2025-01-01", null)]
    [InlineData(MoneyFieldId, "10.01", "10.00", "signed")]
    public void Between_FromGreaterThanTo_IsInvalid(
        string fieldId,
        string from,
        string to,
        string? amountBasis)
    {
        var errors = Validate(TypedRule(fieldId, "between", from: from, to: to, amountBasis: amountBasis));

        Assert.Contains(errors, error => error.Contains("不得", StringComparison.Ordinal)
            && error.Contains("from", StringComparison.Ordinal));
    }

    [Fact]
    public void Between_EqualBounds_IsValid()
    {
        Assert.Empty(Validate(TypedRule(
            DateFieldId, "between", from: "2025-06-30", to: "2025-06-30")));
        Assert.Empty(Validate(TypedRule(
            MoneyFieldId, "between", from: "5.00", to: "5", amountBasis: "signed")));
    }

    [Theory]
    [InlineData("in")]
    [InlineData("notIn")]
    public void SetOperators_RequireValuesWithinBounds(string op)
    {
        Assert.Contains(
            Validate(TypedRule(TextFieldId, op)),
            error => error.Contains("必須提供 values", StringComparison.Ordinal));
        Assert.Contains(
            Validate(TypedRule(TextFieldId, op, values: [])),
            error => error.Contains("1–100", StringComparison.Ordinal));
        Assert.Contains(
            Validate(TypedRule(TextFieldId, op, value: "x", values: ["y"])),
            error => error.Contains("不得帶 value", StringComparison.Ordinal));
        Assert.Contains(
            Validate(TypedRule(TextFieldId, op, values: ["ok", "   "])),
            error => error.Contains("第 2 個值", StringComparison.Ordinal));
        Assert.Empty(Validate(TypedRule(
            TextFieldId,
            op,
            values: Enumerable.Range(0, 100).Select(i => $"v{i}").ToArray())));
    }

    // ── amountBasis ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("Signed")]
    [InlineData(" signed")]
    [InlineData("abs")]
    public void MoneyRule_MissingOrNonCanonicalAmountBasis_IsInvalid(string? amountBasis)
    {
        var errors = Validate(TypedRule(
            MoneyFieldId, "equals", value: "1", amountBasis: amountBasis));

        Assert.Contains(errors, error => error.Contains("amountBasis", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(TextFieldId, "equals", "x")]
    [InlineData(DateFieldId, "on", "2025-01-01")]
    public void NonMoneyRule_WithAmountBasis_IsInvalid(string fieldId, string op, string value)
    {
        var errors = Validate(TypedRule(fieldId, op, value: value, amountBasis: "signed"));

        Assert.Contains(errors, error =>
            error.Contains("amountBasis 僅 money", StringComparison.Ordinal));
    }

    // ── 值格式（malformed operands）───────────────────────────────────────

    [Theory]
    [InlineData("2025-13-01")]
    [InlineData("2025/01/01")]
    [InlineData("2025-1-1")]
    [InlineData("20250101")]
    [InlineData("")]
    public void DateOperand_NotExactIsoDate_IsInvalid(string value)
    {
        var errors = Validate(TypedRule(DateFieldId, "on", value: value));

        Assert.Contains(errors, error => error.Contains("yyyy-MM-dd", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1,2,3x")]
    [InlineData("")]
    [InlineData("99999999999999999999999999")]
    public void MoneyOperand_NotInvariantDecimal_IsInvalid(string value)
    {
        var errors = Validate(TypedRule(
            MoneyFieldId, "equals", value: value, amountBasis: "signed"));

        Assert.Contains(errors, error => error.Contains("格式無效", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TextOperand_BlankAfterTrim_IsInvalid(string value)
    {
        var errors = Validate(TypedRule(TextFieldId, "equals", value: value));

        Assert.Contains(errors, error => error.Contains("不可為空", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingOperator_IsInvalid()
    {
        var errors = Validate(TypedRule(TextFieldId, op: null, value: "x"));

        Assert.Contains(errors, error => error.Contains("operator", StringComparison.Ordinal));
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> Validate(FilterRuleSpec rule) =>
        FilterScenarioValidator.Validate(Scenario(rule), Context);

    private static FilterScenarioSpec Scenario(FilterRuleSpec rule) =>
        new("typed", "validation matrix", [new FilterGroupSpec(FilterJoin.And, [rule])]);

    private static FilterRuleSpec TypedRule(
        string? fieldId,
        string? op,
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
