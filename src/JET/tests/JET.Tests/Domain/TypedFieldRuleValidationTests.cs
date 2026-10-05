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
    public void UnknownOrRemovedField_IsInvalidWithoutShowingTheInternalFieldId()
    {
        // 2026-10-03 主線裁定 T9：審計員會看到的訊息不顯示 rde. 內部代號；欄位已移除時拿不到顯示名稱，
        // 改成不含代號的白話並保留下一步。原本鎖住訊息含 fieldId，現在改鎖相反方向（第一次失敗：收據 20261003-023349721-0ccefea0a80c412aa8460624eaae563a）。
        const string removed = "rde.dddd0000dddd0000dddd0000dddd0000";
        var errors = Validate(TypedRule(removed, "equals", value: "x"));

        var error = Assert.Single(errors);
        Assert.DoesNotContain(removed, error, StringComparison.Ordinal);
        Assert.DoesNotContain("rde.", error, StringComparison.Ordinal);
        Assert.Contains("這個條件使用的攸關資料元素欄位已不在目前案件的欄位配對中", error, StringComparison.Ordinal);
        // 2026-10-03 主線審查改下一步：回第三步重新勾選會產生新的欄位身分，這個條件仍然對不上，
        // 正確的下一步是在第五步這個條件重新選擇欄位（第一次失敗：收據 20261003-025309380-f5004d2af23d4f7a9c32ae8f17b89367）。
        Assert.Contains("請在這個條件重新選擇欄位，或刪除這個條件", error, StringComparison.Ordinal);
        Assert.DoesNotContain("勾選該欄位", error, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingFieldId_IsInvalid()
    {
        var errors = Validate(TypedRule(fieldId: null, op: "equals", value: "x"));

        // 2026-10-03 主線裁定 T9：沒選欄位是正常操作會看到的訊息，改用白話並寫出下一步（第一次失敗：收據 20261003-023349721-0ccefea0a80c412aa8460624eaae563a）。
        Assert.Contains(errors, error => error.Contains("這個攸關資料元素欄位條件尚未選擇欄位，請選擇欄位後再儲存", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, error => error.Contains("fieldId", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyRegistry_FailsClosedForAnyTypedRule()
    {
        var context = new FilterValidationContext(true, false, false);

        var errors = FilterScenarioValidator.Validate(
            Scenario(TypedRule(TextFieldId, "equals", value: "x")), context);

        // 2026-10-03 主線裁定 T9：仍然失敗關閉，但訊息不再顯示 rde. 內部代號（第一次失敗：收據 20261003-023349721-0ccefea0a80c412aa8460624eaae563a）。
        Assert.Contains(errors, error => error.Contains("已不在目前案件的欄位配對中", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, error => error.Contains(TextFieldId, StringComparison.Ordinal));
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
        // 2026-10-03 主線裁定 T9：指名改用欄位顯示名稱與比較方式名稱，不顯示 rde. 內部代號（第一次失敗：收據 20261003-023349721-0ccefea0a80c412aa8460624eaae563a）。
        var errors = Validate(TypedRule(fieldId, op, value: "2025-01-01", amountBasis: null));
        var label = fieldId == TextFieldId ? "備註" : fieldId == DateFieldId ? "審核日" : "稅額";

        var error = Assert.Single(errors);
        Assert.Contains("攸關資料元素欄位「" + label + "」", error, StringComparison.Ordinal);
        Assert.Contains("比較方式「" + FilterConditionLabels.TypedOperatorLabel(op) + "」", error, StringComparison.Ordinal);
        Assert.DoesNotContain(fieldId, error, StringComparison.Ordinal);
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
            && error.Contains("區間", StringComparison.Ordinal));
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
        // 2026-10-04 第 7 批 L72：清單錯誤改白話，缺值、空清單、額外欄位、空白元素與 100 個邊界仍逐項驗證。
        // 第一次失敗：20261004-085516563-cc5e5a0682e8482a9771daf26cb186d9。
        Assert.Contains(
            Validate(TypedRule(TextFieldId, op)),
            error => error.Contains("請填入清單，至少 1 個值，最多 100 個值。", StringComparison.Ordinal));
        Assert.Contains(
            Validate(TypedRule(TextFieldId, op, values: [])),
            error => error.Contains("清單至少需要 1 個值，最多 100 個值。", StringComparison.Ordinal));
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
    [InlineData("2025/01/01")]
    [InlineData("2025-1-1")]
    [InlineData("20250101")]
    public void DateOperand_GlImportDateAliases_AreValid(string value)
    {
        // 2026-10-04 第 7 批 L70：這三個原案例改用 GL 既有日期定義，移到接受測試而不刪除。
        // 第一次失敗：20261004-085516563-cc5e5a0682e8482a9771daf26cb186d9。
        Assert.Empty(Validate(TypedRule(DateFieldId, "on", value: value)));
    }

    [Theory]
    [InlineData("2025-13-01")]
    [InlineData("")]
    public void DateOperand_InvalidOrBlank_IsInvalid(string value)
    {
        // 同批仍保留真正無效日期與空白的拒絕斷言。
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

        Assert.Contains(errors, error => error.Contains("不可只有空白", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingOperator_IsInvalid()
    {
        var errors = Validate(TypedRule(TextFieldId, op: null, value: "x"));

        Assert.Contains(errors, error => error.Contains("尚未選擇比較方式", StringComparison.Ordinal));
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
