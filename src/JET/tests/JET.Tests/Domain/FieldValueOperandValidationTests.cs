using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

/// <summary>
/// 欄位值條件選攸關資料元素欄位時，比較值的檢查：單一值、上下限與清單要和比較方式相符，
/// 金額要寫比較基準，值的格式與清單個數要合格。核心欄位的欄位值條件也走同一套檢查。
/// </summary>
public sealed class FieldValueOperandValidationTests
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

        Assert.Contains(
            fieldId == DateFieldId ? "第 1 組第 1 條：起日晚於迄日" : "第 1 組第 1 條：下限金額大於上限金額",
            errors);
    }

    // 金額區間的上下限照前後端共用的固定答案 tests/JET.Tests/Fixtures/amount-parsing.json：
    // 先各自讀成放大後的整數，讀不成的各寫一句，兩端都讀得成才比大小。前端第五步的區間測試讀同一份。
    public static TheoryData<string, string, string> SharedAmountFixtureRanges()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var range in SharedAmountFixture.Ranges) data.Add(range.From, range.To, range.Result);
        return data;
    }

    [Theory]
    [MemberData(nameof(SharedAmountFixtureRanges))]
    public void Between_SharedAmountFixtureRanges_MatchTheFixedAnswer(string from, string to, string result)
    {
        var errors = FilterScenarioValidator.Validate(
            Scenario(TypedRule(MoneyFieldId, "between", from: from, to: to, amountBasis: "signed")),
            Context with { MoneyScale = SharedAmountFixture.MoneyScale });

        var expected = new List<string>();
        if (result is "invalidFrom" or "invalidBoth") expected.Add($"第 1 組第 1 條：金額「{from}」不是數字");
        if (result is "invalidTo" or "invalidBoth") expected.Add($"第 1 組第 1 條：金額「{to}」不是數字");
        if (result == "reversed") expected.Add("第 1 組第 1 條：下限金額大於上限金額");
        Assert.Contains(result, new[] { "ok", "reversed", "invalidFrom", "invalidTo", "invalidBoth" });
        Assert.Equal(expected, errors);
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
        Assert.True(HasCorruptReason(errors, "field_value_amount_basis_missing"));
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
        Assert.True(HasCorruptReason(errors, "field_value_amount_basis_not_amount"));
    }

    // ── 值格式（malformed operands）───────────────────────────────────────

    [Theory]
    [InlineData("2025/01/01")]
    [InlineData("2025-1-1")]
    [InlineData("20250101")]
    public void DateOperand_GlImportDateAliases_AreValid(string value)
    {
        // 這三個寫法和 GL 匯入時的日期讀法相同，都是有效日期。
        Assert.Empty(Validate(TypedRule(DateFieldId, "on", value: value)));
    }

    [Theory]
    [InlineData("2025-13-01")]
    [InlineData("")]
    public void DateOperand_InvalidOrBlank_IsInvalid(string value)
    {
        // 無法辨識的日期與空白都要拒絕。
        var errors = Validate(TypedRule(DateFieldId, "on", value: value));

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

        Assert.Contains($"第 1 組第 1 條：金額「{value}」不是數字", errors);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TextOperand_BlankAfterTrim_IsInvalid(string value)
    {
        var errors = Validate(TypedRule(TextFieldId, "equals", value: value));

        Assert.Contains("第 1 組第 1 條：比對文字不得為空白", errors);
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
            FilterConditionType.FieldValue,
            PrescreenKey: null,
            Field: null,
            Keywords: [],
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
