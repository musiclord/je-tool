using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

/// <summary>
/// GL 中一定有值的欄位，以及第五步對這些欄位選「空白」時的檢查。
/// 金額來源空白時存成 0；總帳日期空白或讀不成日期的分錄不納入 GL；傳票核准日沿用總帳日期、
/// 傳票文件項次自動編號時，每一筆也都有值。攸關資料元素欄位空白時不存值，不在這份清單裡。
/// </summary>
public sealed class NeverBlankGlFieldsTests
{
    private const string AmountReason = "GL 中的金額不會空白，來源空白的金額已當成 0；要找 0 元的分錄，請改用「指定金額」填 0";

    private static readonly IReadOnlyDictionary<string, string> RequiredOnlyMapping =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [JetFieldCatalog.GlVoucherNumber] = "傳票號碼",
            [JetFieldCatalog.GlDate] = "傳票日期",
            [JetFieldCatalog.GlAccountNumber] = "科目代號",
            [JetFieldCatalog.GlAccountName] = "科目名稱",
            [JetFieldCatalog.GlDescription] = "摘要",
            [JetFieldCatalog.GlAmount] = "金額"
        };

    private static GlMappingOptions Options(string approvalDateMode) =>
        new(approvalDateMode, null, new GlManualAutoPolicy(["1"], ["0"]), []);

    private static IReadOnlyDictionary<string, string> With(params (string Key, string Column)[] extra)
    {
        var mapping = new Dictionary<string, string>(RequiredOnlyMapping, StringComparer.Ordinal);
        foreach (var (key, column) in extra) mapping[key] = column;
        return mapping;
    }

    [Fact]
    public void NeverBlankGlFields_SourceColumnsForLineItemAndApprovalDate_ListsOnlyGlDateAndAmount()
    {
        var mapping = With((JetFieldCatalog.GlLineItem, "項次"), (JetFieldCatalog.GlApprovalDate, "核准日期"));

        Assert.Equal(
            [JetFieldCatalog.GlDate, JetFieldCatalog.GlAmount],
            JetFieldCatalog.NeverBlankGlFields(mapping, Options(ApprovalDateModeNames.Mapped)));
    }

    [Fact]
    public void NeverBlankGlFields_AutoNumberedLineItemAndApprovalDateSameAsGlDate_AreListedToo()
    {
        Assert.Equal(
            [JetFieldCatalog.GlLineItem, JetFieldCatalog.GlDate, JetFieldCatalog.GlApprovalDate, JetFieldCatalog.GlAmount],
            JetFieldCatalog.NeverBlankGlFields(RequiredOnlyMapping, Options(ApprovalDateModeNames.SameAsGlDate)));
    }

    [Fact]
    public void NeverBlankGlFields_ApprovalDateWithoutSource_IsNotListed()
    {
        Assert.Equal(
            [JetFieldCatalog.GlLineItem, JetFieldCatalog.GlDate, JetFieldCatalog.GlAmount],
            JetFieldCatalog.NeverBlankGlFields(RequiredOnlyMapping, Options(ApprovalDateModeNames.Unmapped)));
    }

    [Fact]
    public void NeverBlankGlFields_AreAlwaysAmongTheAvailableFields()
    {
        foreach (var mode in new[] { ApprovalDateModeNames.Unmapped, ApprovalDateModeNames.Mapped, ApprovalDateModeNames.SameAsGlDate })
        {
            var mapping = mode == ApprovalDateModeNames.Mapped ? With((JetFieldCatalog.GlApprovalDate, "核准日期")) : RequiredOnlyMapping;
            var available = JetFieldCatalog.AvailableGlFields(mapping, Options(mode));

            Assert.All(JetFieldCatalog.NeverBlankGlFields(mapping, Options(mode)), field => Assert.Contains(field, available));
        }
    }

    private static FilterConditionSpec FieldValue(string field, string op, bool includeBlank = false) =>
        new(FilterJoin.And, FilterConditionType.FieldValue, null, field, [], null, null)
        {
            TypedOperator = op,
            IncludeBlank = includeBlank,
            AmountBasis = field == JetFieldCatalog.GlAmount ? TypedAmountBasisNames.Absolute : null
        };

    private static FilterScenarioSpec Scenario(FilterConditionSpec rule) =>
        new("合成情境", "合成動機", [new FilterGroupSpec(FilterJoin.And, [rule])]);

    private static FilterValidationContext Context(IReadOnlyDictionary<string, string> mapping, string approvalDateMode) =>
        new(HasPrepStartDate: true, HasAccountMapping: false, HasAuthorizedPreparers: false)
        {
            AvailableGlFields = JetFieldCatalog.AvailableGlFields(mapping, Options(approvalDateMode)),
            NeverBlankGlFields = JetFieldCatalog.NeverBlankGlFields(mapping, Options(approvalDateMode)),
            MoneyScale = 100
        };

    [Fact]
    public void Validate_AmountIsBlank_TellsTheAuditorToUseSpecifiedAmountZero()
    {
        var errors = FilterScenarioValidator.Validate(
            Scenario(FieldValue(JetFieldCatalog.GlAmount, "isBlank")),
            Context(RequiredOnlyMapping, ApprovalDateModeNames.Unmapped));

        Assert.Equal([$"第 1 組第 1 條：{AmountReason}"], errors);
    }

    [Theory]
    [InlineData(JetFieldCatalog.GlDate, "總帳日期")]
    [InlineData(JetFieldCatalog.GlApprovalDate, "傳票核准日")]
    [InlineData(JetFieldCatalog.GlLineItem, "傳票文件項次")]
    public void Validate_IsBlankOnAFieldThatIsNeverBlank_NamesTheFieldAndSaysWhatToDo(string field, string label)
    {
        var errors = FilterScenarioValidator.Validate(
            Scenario(FieldValue(field, "isBlank")),
            Context(RequiredOnlyMapping, ApprovalDateModeNames.SameAsGlDate));

        Assert.Equal([$"第 1 組第 1 條：GL 中的{label}不會空白，請移除這條條件"], errors);
    }

    [Theory]
    [InlineData(JetFieldCatalog.GlApprovalDate)]
    [InlineData(JetFieldCatalog.GlLineItem)]
    public void Validate_IsBlankOnAFieldReadFromASourceColumn_IsStillAllowed(string field)
    {
        var mapping = With((JetFieldCatalog.GlLineItem, "項次"), (JetFieldCatalog.GlApprovalDate, "核准日期"));

        Assert.Empty(FilterScenarioValidator.Validate(
            Scenario(FieldValue(field, "isBlank")),
            Context(mapping, ApprovalDateModeNames.Mapped)));
    }

    // 「非空白」與「空白也列入」在一定有值的欄位上不改變結果，已存的情境照樣能用。
    [Theory]
    [InlineData(JetFieldCatalog.GlAmount)]
    [InlineData(JetFieldCatalog.GlDate)]
    public void Validate_IsNotBlankOnAFieldThatIsNeverBlank_IsNotBlocked(string field)
    {
        Assert.Empty(FilterScenarioValidator.Validate(
            Scenario(FieldValue(field, "isNotBlank", includeBlank: true)),
            Context(RequiredOnlyMapping, ApprovalDateModeNames.Unmapped)));
    }

    [Fact]
    public void Validate_IsBlankOnARelevantDataElementAmount_IsStillAllowed()
    {
        const string fieldId = "rde.0123456789abcdef0123456789abcdef";
        var rule = FieldValue(JetFieldCatalog.GlAmount, "isBlank") with { Field = null, FieldId = fieldId };
        var context = Context(RequiredOnlyMapping, ApprovalDateModeNames.Unmapped) with
        {
            RdeFields = [new GlRdeFieldMetadata(fieldId, "合成金額欄", "合成金額", "amount")]
        };

        Assert.Empty(FilterScenarioValidator.Validate(Scenario(rule), context));
    }

    // 沒有帶案件配對的獨立檢查不知道哪些欄位一定有值，所以不檢查。
    [Fact]
    public void Validate_WithoutCaseFacts_DoesNotCheckBlankOptions()
    {
        Assert.Empty(FilterScenarioValidator.Validate(
            Scenario(FieldValue(JetFieldCatalog.GlAmount, "isBlank")),
            new FilterValidationContext(true, false, false) { MoneyScale = 100 }));
    }
}
