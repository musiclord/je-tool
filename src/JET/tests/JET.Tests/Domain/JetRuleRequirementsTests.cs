using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

/// <summary>
/// 欄位可用性與「規則需要什麼」表的固定答案。第四步、第五步與前端停用說明都讀這一張表，
/// 所以表的內容、判斷順序與原因句在這裡逐字鎖住。
/// （46 則失敗，例如只配必填欄時，可用欄位清單沒有自動編號的傳票文件項次）。
/// </summary>
public sealed class JetRuleRequirementsTests
{
    private static readonly IReadOnlyDictionary<string, string> RequiredOnlyMapping =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [JetFieldCatalog.GlVoucherNumber] = "傳票號碼",
            [JetFieldCatalog.GlDate] = "傳票日期",
            [JetFieldCatalog.GlAccountNumber] = "科目代號",
            [JetFieldCatalog.GlAccountName] = "科目名稱",
            [JetFieldCatalog.GlDescription] = "摘要",
            [JetFieldCatalog.GlAmount] = "金額",
            [JetFieldCatalog.GlDrCr] = "借方旗標",
            [JetFieldCatalog.GlDrCrDebitCode] = "1",
            [JetFieldCatalog.GlDrCrCreditCode] = "0"
        };

    private static GlMappingOptions Options(string approvalDateMode) =>
        new(approvalDateMode, null, new GlManualAutoPolicy(["1"], ["0"]), []);

    private static JetRulePrerequisite Field(string semanticIdentity) =>
        new(JetRulePrerequisiteKind.GlField, semanticIdentity);

    private static JetRulePrerequisite Kind(JetRulePrerequisiteKind kind) => new(kind);

    private static FilterConditionSpec Rule(FilterConditionType type, string? field = null, string? prescreenKey = null) =>
        new(FilterJoin.And, type, prescreenKey, field, [], TextMatchMode.Contains,
            null, null, null, null, null, null);

    private static JetRuleFacts Facts(
        IReadOnlyCollection<string>? availableGlFields = null,
        bool hasPrepStartDate = true,
        bool hasAccountMapping = true,
        bool hasAnyAccountCategory = true,
        bool hasRevenueCategory = true,
        bool hasCounterpartCategory = true,
        bool hasAuthorizedPreparers = true,
        bool? hasHolidayCalendar = true,
        AccountCategoryLabels? labels = null) =>
        new(availableGlFields, hasPrepStartDate, hasAccountMapping, hasAnyAccountCategory, hasRevenueCategory,
            hasCounterpartCategory, hasAuthorizedPreparers, hasHolidayCalendar, labels ?? AccountCategoryLabels.BuiltIn);

    [Fact]
    public void AvailableGlFields_LineItemWithoutSource_IsAutoNumberedAndAvailable()
    {
        // 傳票文件項次自動編號時一定有值，所以算已配對。
        var available = JetFieldCatalog.AvailableGlFields(RequiredOnlyMapping, Options(ApprovalDateModeNames.Unmapped));

        Assert.Equal(
            [
                JetFieldCatalog.GlVoucherNumber, JetFieldCatalog.GlLineItem, JetFieldCatalog.GlDate, JetFieldCatalog.GlAccountNumber,
                JetFieldCatalog.GlAccountName, JetFieldCatalog.GlDescription, JetFieldCatalog.GlAmount
            ],
            available);
    }

    [Theory]
    [InlineData(ApprovalDateModeNames.Mapped, true)]
    [InlineData(ApprovalDateModeNames.SameAsGlDate, true)]
    [InlineData(ApprovalDateModeNames.Unmapped, false)]
    public void AvailableGlFields_ApprovalDate_FollowsApprovalDateMode(string approvalDateMode, bool expected)
    {
        var mapping = new Dictionary<string, string>(RequiredOnlyMapping, StringComparer.Ordinal);
        if (approvalDateMode == ApprovalDateModeNames.Mapped)
        {
            mapping[JetFieldCatalog.GlApprovalDate] = "核准日期";
        }

        var available = JetFieldCatalog.AvailableGlFields(mapping, Options(approvalDateMode));

        Assert.Equal(expected, available.Contains(JetFieldCatalog.GlApprovalDate));
    }

    [Fact]
    public void AvailableGlFields_OptionalFieldWithoutSource_IsUnavailableUntilMapped()
    {
        var withoutPreparer = JetFieldCatalog.AvailableGlFields(RequiredOnlyMapping, null);
        var mapping = new Dictionary<string, string>(RequiredOnlyMapping, StringComparer.Ordinal)
        {
            [JetFieldCatalog.GlCreatedBy] = "建立人員"
        };
        var withPreparer = JetFieldCatalog.AvailableGlFields(mapping, null);

        Assert.DoesNotContain(JetFieldCatalog.GlCreatedBy, withoutPreparer);
        Assert.Contains(JetFieldCatalog.GlCreatedBy, withPreparer);
    }

    [Fact]
    public void PrescreenRules_CoverEveryPrescreenProcedure()
    {
        var wireKeys = RuleCatalog.All
            .Where(static rule => rule.Shape is RuleShape.RowTag or RuleShape.Aggregate)
            .Select(static rule => rule.WireKey)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(wireKeys, JetRuleRequirements.PrescreenRules.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    public static TheoryData<string, JetRulePrerequisite[]> PrescreenRuleCases => new()
    {
        { PrescreenRuleKeys.PrepPeriodApproval, [Field(JetFieldCatalog.GlApprovalDate), Kind(JetRulePrerequisiteKind.PrepStartDate)] },
        { PrescreenRuleKeys.SuspiciousKeywords, [Field(JetFieldCatalog.GlDescription)] },
        {
            PrescreenRuleKeys.UnexpectedAccountPair,
            [
                Kind(JetRulePrerequisiteKind.AccountMapping), Kind(JetRulePrerequisiteKind.RevenueCategory),
                Kind(JetRulePrerequisiteKind.CounterpartCategory)
            ]
        },
        { PrescreenRuleKeys.TrailingZeros, [Field(JetFieldCatalog.GlAmount)] },
        { JetRuleRequirements.PreparerSummary, [Field(JetFieldCatalog.GlCreatedBy)] },
        { JetRuleRequirements.RareAccounts, [Field(JetFieldCatalog.GlAccountNumber)] },
        { PrescreenRuleKeys.WeekendPosting, [Field(JetFieldCatalog.GlDate)] },
        { PrescreenRuleKeys.WeekendApproval, [Field(JetFieldCatalog.GlApprovalDate)] },
        { PrescreenRuleKeys.HolidayPosting, [Kind(JetRulePrerequisiteKind.HolidayCalendar), Field(JetFieldCatalog.GlDate)] },
        { PrescreenRuleKeys.HolidayApproval, [Kind(JetRulePrerequisiteKind.HolidayCalendar), Field(JetFieldCatalog.GlApprovalDate)] },
        { PrescreenRuleKeys.BlankDescription, [Field(JetFieldCatalog.GlDescription)] },
        {
            PrescreenRuleKeys.NonAuthorizedPreparer,
            [Kind(JetRulePrerequisiteKind.AuthorizedPreparer), Field(JetFieldCatalog.GlCreatedBy)]
        }
        // 照舊版，預篩選沒有編製分錄較少的人員與使用較少的科目，這裡也沒有這兩列。
    };

    [Theory]
    [MemberData(nameof(PrescreenRuleCases))]
    public void PrescreenRules_ListPrerequisitesInReasonOrder(string key, JetRulePrerequisite[] expected)
    {
        Assert.Equal(expected, JetRuleRequirements.PrescreenRules[key]);
    }

    public static TheoryData<string, FilterConditionSpec, JetRulePrerequisite[]> FilterConditionCases => new()
    {
        { "欄位值比較：建立人員", Rule(FilterConditionType.FieldValue, JetFieldCatalog.GlCreatedBy), [Field(JetFieldCatalog.GlCreatedBy)] },
        { "欄位值比較：傳票文件項次", Rule(FilterConditionType.FieldValue, JetFieldCatalog.GlLineItem), [Field(JetFieldCatalog.GlLineItem)] },
        { "欄位值比較：已移除的傳票日期", Rule(FilterConditionType.FieldValue, "voucherDate"), [] },
        { "欄位值比較：攸關資料元素欄位", Rule(FilterConditionType.FieldValue) with { FieldId = "rde.0123456789abcdef0123456789abcdef" }, [] },
        { "文字條件", Rule(FilterConditionType.Text, JetFieldCatalog.GlCreatedBy), [Field(JetFieldCatalog.GlCreatedBy)] },
        { "文字值清單", Rule(FilterConditionType.TextSet, JetFieldCatalog.GlApprovedBy), [Field(JetFieldCatalog.GlApprovedBy)] },
        { "日期區間", Rule(FilterConditionType.DateRange, JetFieldCatalog.GlApprovalDate), [Field(JetFieldCatalog.GlApprovalDate)] },
        { "金額區間", Rule(FilterConditionType.NumRange, JetFieldCatalog.GlAmount), [Field(JetFieldCatalog.GlAmount)] },
        { "科目與人員統計", Rule(FilterConditionType.EntityFrequency, JetFieldCatalog.GlApprovedBy), [Field(JetFieldCatalog.GlApprovedBy)] },
        { "借貸限定", Rule(FilterConditionType.DrCrOnly), [Field(JetFieldCatalog.GlAmount)] },
        { "人工或自動", Rule(FilterConditionType.ManualAuto), [Field(JetFieldCatalog.GlManual)] },
        { "借貸科目分類", Rule(FilterConditionType.AccountSide), [Kind(JetRulePrerequisiteKind.AnyAccountCategory)] },
        { "借貸科目組合（看對方科目）", Rule(FilterConditionType.AccountPair), [Kind(JetRulePrerequisiteKind.AnyAccountCategory)] },
        { "借貸科目組合", Rule(FilterConditionType.SpecialAccountCategoryPair), [Kind(JetRulePrerequisiteKind.AnyAccountCategory)] },
        { "季末前借記收入", Rule(FilterConditionType.RevenueDebitNearQuarterEnd), [Kind(JetRulePrerequisiteKind.RevenueCategory)] },
        {
            "收入無一般對方科目", Rule(FilterConditionType.RevenueWithoutNormalCounterpart),
            [Kind(JetRulePrerequisiteKind.RevenueCategory), Kind(JetRulePrerequisiteKind.CounterpartCategory)]
        },
        {
            "收入之人工分錄", Rule(FilterConditionType.ManualRevenueEntry),
            [Kind(JetRulePrerequisiteKind.RevenueCategory), Field(JetFieldCatalog.GlManual)]
        },
        {
            "編製與核准同一人", Rule(FilterConditionType.PreparerEqualsApprover),
            [Field(JetFieldCatalog.GlCreatedBy), Field(JetFieldCatalog.GlApprovedBy)]
        },
        {
            "預篩選：財報準備日起核准", Rule(FilterConditionType.Prescreen, prescreenKey: PrescreenRuleKeys.PrepPeriodApproval),
            [Field(JetFieldCatalog.GlApprovalDate), Kind(JetRulePrerequisiteKind.PrepStartDate)]
        },
        { "預篩選：已停用的回溯過帳", Rule(FilterConditionType.Prescreen, prescreenKey: "backdatedPosting"), [] },
        { "子條件組", Rule(FilterConditionType.Group), [] },
        { "整張傳票條件", Rule(FilterConditionType.Voucher), [] },
        { "攸關資料元素條件", Rule(FilterConditionType.TypedField) with { FieldId = "rde.0123456789abcdef0123456789abcdef" }, [] }
    };

    [Theory]
    [MemberData(nameof(FilterConditionCases))]
    public void ForFilterCondition_ListsWhatTheConditionUses(string name, FilterConditionSpec rule, JetRulePrerequisite[] expected)
    {
        Assert.False(string.IsNullOrEmpty(name));
        Assert.Equal(expected, JetRuleRequirements.ForFilterCondition(rule));
    }

    [Fact]
    public void Unmet_AccountMappingNotImported_ListsOnlyThatReason()
    {
        var unmet = JetRuleRequirements.Unmet(
            JetRuleRequirements.PrescreenRules[PrescreenRuleKeys.UnexpectedAccountPair],
            Facts(hasAccountMapping: false, hasAnyAccountCategory: false, hasRevenueCategory: false,
                hasCounterpartCategory: false));

        Assert.Equal([Kind(JetRulePrerequisiteKind.AccountMapping)], unmet);
    }

    [Fact]
    public void Unmet_ListsEveryMissingPrerequisiteInTableOrder()
    {
        var unmet = JetRuleRequirements.Unmet(
            JetRuleRequirements.PrescreenRules[PrescreenRuleKeys.UnexpectedAccountPair],
            Facts(hasRevenueCategory: false, hasCounterpartCategory: false));
        var preparer = JetRuleRequirements.Unmet(
            JetRuleRequirements.PrescreenRules[PrescreenRuleKeys.NonAuthorizedPreparer],
            Facts(availableGlFields: [JetFieldCatalog.GlVoucherNumber], hasAuthorizedPreparers: false));

        Assert.Equal(
            [Kind(JetRulePrerequisiteKind.RevenueCategory), Kind(JetRulePrerequisiteKind.CounterpartCategory)],
            unmet);
        Assert.Equal([Kind(JetRulePrerequisiteKind.AuthorizedPreparer), Field(JetFieldCatalog.GlCreatedBy)], preparer);
    }

    [Fact]
    public void Unmet_NullFacts_AreNotChecked()
    {
        // GL 欄位清單是 null 只出現在沒有配對資料的獨立測試；假日檔是 null 表示第五步不擋。
        var unmet = JetRuleRequirements.Unmet(
            JetRuleRequirements.PrescreenRules[PrescreenRuleKeys.HolidayApproval],
            Facts(availableGlFields: null, hasHolidayCalendar: null));
        var missingCalendar = JetRuleRequirements.Unmet(
            JetRuleRequirements.PrescreenRules[PrescreenRuleKeys.HolidayApproval],
            Facts(availableGlFields: [JetFieldCatalog.GlDate], hasHolidayCalendar: false));

        Assert.Empty(unmet);
        Assert.Equal([Kind(JetRulePrerequisiteKind.HolidayCalendar), Field(JetFieldCatalog.GlApprovalDate)], missingCalendar);
    }

    public static TheoryData<JetRulePrerequisite, string, string> SentenceCases => new()
    {
        {
            Field(JetFieldCatalog.GlCreatedBy),
            "「傳票建立人員」尚未選擇來源欄",
            "「傳票建立人員」尚未選擇來源欄，請回第三步欄位配對補上。"
        },
        {
            Kind(JetRulePrerequisiteKind.PrepStartDate),
            "尚未填寫期末財報準備日，請先在第一步按「修改案件資料」填寫",
            "尚未填寫期末財報準備日，請先在第一步按「修改案件資料」填寫。"
        },
        {
            Kind(JetRulePrerequisiteKind.AccountMapping),
            "科目配對中尚無任何科目設定分類，請先在第四步設定",
            "科目配對中尚無任何科目設定分類，請先在第四步設定。"
        },
        {
            Kind(JetRulePrerequisiteKind.AnyAccountCategory),
            "科目配對中尚無任何科目設定分類，請先在第四步設定",
            "科目配對中尚無任何科目設定分類，請先在第四步設定。"
        },
        {
            Kind(JetRulePrerequisiteKind.RevenueCategory),
            "科目配對中尚無歸類為「營業收入」的科目，請先在第四步設定",
            "科目配對中尚無歸類為「營業收入」的科目，請先在第四步設定。"
        },
        {
            Kind(JetRulePrerequisiteKind.CounterpartCategory),
            "科目配對中尚無歸類為「應收帳款」「Cash」或「Receipt in advance」的科目，請先在第四步設定",
            "科目配對中尚無歸類為「應收帳款」「Cash」或「Receipt in advance」的科目，請先在第四步設定。"
        },
        {
            Kind(JetRulePrerequisiteKind.AuthorizedPreparer),
            "尚未匯入授權編製人員清單，請先在第二步匯入",
            "尚未匯入授權編製人員清單，請先在第二步匯入。"
        },
        {
            Kind(JetRulePrerequisiteKind.HolidayCalendar),
            // 這句的用字已定案。
            "尚未上傳國定假日清單，請先在第二步「國定假日與補班日」上傳",
            "尚未上傳國定假日清單，請先在第二步「國定假日與補班日」上傳。"
        }
    };

    [Theory]
    [MemberData(nameof(SentenceCases))]
    public void Sentences_FilterReasonAndPrescreenSentence(
        JetRulePrerequisite prerequisite, string filterReason, string prescreenSentence)
    {
        // 審計員改過的分類名稱寫新名稱；第四步沒有清單最後那句下一步，GL 欄位的句子另接到哪裡補。
        var labels = AccountCategoryLabels.BuiltIn with { Revenue = "營業收入", Receivables = "應收帳款" };

        Assert.Equal(filterReason, JetRuleRequirements.Reason(prerequisite, labels));
        Assert.Equal(prescreenSentence, JetRuleRequirements.PrescreenSentence(prerequisite, labels));
    }
}
