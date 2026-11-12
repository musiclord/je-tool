using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

/// <summary>
/// typed dynamic rule 的 Domain 驗證矩陣：field registry、per-type
/// operator 集合、operand carrier 精確匹配、amountBasis、值格式與 RDE lifecycle 的
/// 「invalid_scenario 並指名 field」。
/// </summary>
public sealed class TypedFieldRuleValidationTests
{
    private const string TextFieldId = "rde.aaaa0000aaaa0000aaaa0000aaaa0000";
    private const string DateFieldId = "rde.bbbb0000bbbb0000bbbb0000bbbb0000";
    private const string MoneyFieldId = "rde.cccc0000cccc0000cccc0000cccc0000";

    private static readonly FilterValidationContext Context = new(
        HasPrepStartDate: true,
        HasAccountMapping: false,
        HasAuthorizedPreparers: false)
    {
        RdeFields =
        [
            new GlRdeFieldMetadata(TextFieldId, "備註欄", "備註", RdeFieldValueTypeNames.Text),
            new GlRdeFieldMetadata(DateFieldId, "審核日欄", "審核日", RdeFieldValueTypeNames.Date),
            new GlRdeFieldMetadata(MoneyFieldId, "稅額欄", "稅額", RdeFieldValueTypeNames.Amount)
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
            // money：9 operators（比較 operator 必填 amountBasis；blank 家族亦必填——
            // 每條 money 規則都要明示 amountBasis，不分 operator 種類）
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
        // 審計員會看到的訊息不顯示 rde. 內部代號；欄位已移除時拿不到顯示名稱，
        // 訊息用不含代號的白話並保留下一步。
        const string removed = "rde.dddd0000dddd0000dddd0000dddd0000";
        var errors = Validate(TypedRule(removed, "equals", value: "x"));

        var error = Assert.Single(errors);
        Assert.DoesNotContain(removed, error, StringComparison.Ordinal);
        Assert.DoesNotContain("rde.", error, StringComparison.Ordinal);
        // 回第三步重新勾選會產生新的欄位身分，這個條件仍然對不上，
        // 正確的下一步是在第五步這個條件重新選擇欄位。
        // 這是保留下一步的少數句子之一；這句的用字已定案，核對整句。
        Assert.Equal("第 1 組第 1 條：所用的攸關資料元素欄位已不在欄位配對中，請重新選擇欄位或刪除此條件", error);
        Assert.DoesNotContain("勾選該欄位", error, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingFieldId_IsInvalid()
    {
        var errors = Validate(TypedRule(fieldId: null, op: "equals", value: "x"));

        // 沒選欄位是正常操作會看到的訊息，用白話寫出原因。
        Assert.Contains("第 1 組第 1 條：尚未選擇攸關資料元素欄位", errors);
        Assert.DoesNotContain(errors, error => error.Contains("fieldId", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyRegistry_FailsClosedForAnyTypedRule()
    {
        var context = new FilterValidationContext(true, false, false);

        var errors = FilterScenarioValidator.Validate(
            Scenario(TypedRule(TextFieldId, "equals", value: "x")), context);

        // 仍然失敗關閉，但訊息不顯示 rde. 內部代號。
        Assert.Contains(errors, error => error.Contains("已不在欄位配對中", StringComparison.Ordinal));
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
        // 指名用欄位顯示名稱與比較方式名稱，不顯示 rde. 內部代號。
        var errors = Validate(TypedRule(fieldId, op, value: "2025-01-01", amountBasis: null));
        var label = fieldId == TextFieldId ? "備註" : fieldId == DateFieldId ? "審核日" : "稅額";
        var setting = fieldId == TextFieldId ? "文字" : fieldId == DateFieldId ? "日期" : "金額";

        var error = Assert.Single(errors);
        // 訊息用具體說法：寫出欄位目前設定成文字、日期或金額，不用「型別」。
        // 核對整句，指名欄位、設定與比較方式。
        Assert.Equal(
            "第 1 組第 1 條：「" + label + "」目前設定為" + setting + "，不能用「" + FilterConditionLabels.TypedOperatorLabel(op) + "」比較",
            error);
        Assert.DoesNotContain(fieldId, error, StringComparison.Ordinal);
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
        // 只有存檔損毀或程式送錯才會出現的錯誤，畫面只寫通用句，原句與英文代碼改記支援日誌；
        // 改鎖通用句與程式寫死的原因代號，檢查仍逐項區分。
        Assert.True(HasCorruptReason(Validate(TypedRule(TextFieldId, "equals")), "typed_single_value_missing"));
        Assert.True(HasCorruptReason(
            Validate(TypedRule(TextFieldId, "equals", value: "x", from: "y")), "typed_single_extra_operand"));
        Assert.True(HasCorruptReason(
            Validate(TypedRule(TextFieldId, "equals", value: "x", values: ["y"])), "typed_single_extra_operand"));
        Assert.Contains("第 1 組第 1 條：此條件內容有誤，無法儲存。請刪除此條件後重新設定。", Validate(TypedRule(TextFieldId, "equals")));
    }

    [Fact]
    public void Between_RequiresBothBoundsAndForbidsOtherCarriers()
    {
        // 只有存檔損毀或程式送錯才會出現的錯誤，畫面只寫通用句，原句與英文代碼改記支援日誌；
        // 改鎖通用句與程式寫死的原因代號，檢查仍逐項區分。
        Assert.True(HasCorruptReason(
            Validate(TypedRule(DateFieldId, "between", from: "2025-01-01")), "typed_between_bound_missing"));
        Assert.True(HasCorruptReason(
            Validate(TypedRule(DateFieldId, "between", to: "2025-01-01")), "typed_between_bound_missing"));
        Assert.True(HasCorruptReason(
            Validate(TypedRule(
                DateFieldId, "between", value: "x", from: "2025-01-01", to: "2025-12-31")),
            "typed_between_extra_operand"));
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

        // 這句的用字已定案，核對整句。
        Assert.Contains(
            fieldId == DateFieldId ? "第 1 組第 1 條：起日晚於迄日" : "第 1 組第 1 條：下限金額大於上限金額",
            errors);
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
        // 清單錯誤用白話，缺值、空清單、攸關資料元素欄位、空白元素與 100 個邊界逐項驗證。
        // 清單個數的錯誤都用同一句；多帶 value 只會是內容損毀，核對原因代號。
        Assert.Contains("第 1 組第 1 條：請填 1 到 100 個值", Validate(TypedRule(TextFieldId, op)));
        Assert.Contains("第 1 組第 1 條：請填 1 到 100 個值", Validate(TypedRule(TextFieldId, op, values: [])));
        Assert.True(HasCorruptReason(
            Validate(TypedRule(TextFieldId, op, value: "x", values: ["y"])), "typed_set_extra_operand"));
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

        // 只有存檔損毀或程式送錯才會出現的錯誤，畫面只寫通用句，原句與英文代碼改記支援日誌；
        // 改鎖通用句與程式寫死的原因代號，檢查仍逐項區分。
        Assert.True(HasCorruptReason(errors, "typed_amount_basis_missing"));
        Assert.DoesNotContain(errors, error => error.Contains("amountBasis", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(TextFieldId, "equals", "x")]
    [InlineData(DateFieldId, "on", "2025-01-01")]
    public void NonAmountRule_WithAmountBasis_IsInvalid(string fieldId, string op, string value)
    {
        var errors = Validate(TypedRule(fieldId, op, value: value, amountBasis: "signed"));

        // 只有存檔損毀或程式送錯才會出現的錯誤，畫面只寫通用句，原句與英文代碼改記支援日誌；
        // 改鎖通用句與程式寫死的原因代號，檢查仍逐項區分。
        Assert.True(HasCorruptReason(errors, "typed_amount_basis_not_amount"));
    }

    // ── 值格式（malformed operands）───────────────────────────────────────

    [Theory]
    [InlineData("2025/01/01")]
    [InlineData("2025-1-1")]
    [InlineData("20250101")]
    public void DateOperand_GlImportDateAliases_AreValid(string value)
    {
        // 這三個案例照 GL 既有日期定義，都是有效日期。
        Assert.Empty(Validate(TypedRule(DateFieldId, "on", value: value)));
    }

    [Theory]
    [InlineData("2025-13-01")]
    [InlineData("")]
    public void DateOperand_InvalidOrBlank_IsInvalid(string value)
    {
        // 同批仍保留真正無效日期與空白的拒絕斷言。
        var errors = Validate(TypedRule(DateFieldId, "on", value: value));

        // 這句的用字已定案，核對整句。
        Assert.Contains($"第 1 組第 1 條：日期「{value}」無法辨識，請以 2026-12-31 的格式輸入", errors);
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

        // 這句的用字已定案，核對整句。
        Assert.Contains($"第 1 組第 1 條：金額「{value}」不是數字", errors);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TextOperand_BlankAfterTrim_IsInvalid(string value)
    {
        var errors = Validate(TypedRule(TextFieldId, "equals", value: value));

        // 這句的用字已定案，核對整句。
        Assert.Contains("第 1 組第 1 條：比對文字不得為空白", errors);
    }

    [Fact]
    public void MissingOperator_IsInvalid()
    {
        var errors = Validate(TypedRule(TextFieldId, op: null, value: "x"));

        Assert.Contains(errors, error => error.Contains("尚未選擇比較方式", StringComparison.Ordinal));
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> Validate(FilterConditionSpec rule) =>
        FilterScenarioValidator.Validate(Scenario(rule), Context);

    private static bool HasCorruptReason(IReadOnlyList<string> errors, string code) =>
        FilterScenarioCorruption.ReasonsOf(errors).Any(reason => reason.Code == code);

    private static FilterScenarioSpec Scenario(FilterConditionSpec rule) =>
        new("typed", "validation matrix", [new FilterGroupSpec(FilterJoin.And, [rule])]);

    private static FilterConditionSpec TypedRule(
        string? fieldId,
        string? op,
        string? value = null,
        string? from = null,
        string? to = null,
        string[]? values = null,
        string? amountBasis = null) =>
        new(
            FilterJoin.And,
            FilterConditionType.TypedField,
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
