using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

/// <summary>
/// 欄位可用性與「規則需要什麼」表的固定答案（預篩選收斂計畫階段四）。第四步、第五步與前端停用說明都讀這一張表，
/// 所以表的內容、判斷順序與原因句在這裡逐字鎖住。第一次失敗：Focused 收據 20261023-031210977-4e167568cda74f58851d32f8088917e8
/// （46 則失敗，例如只配必填欄時，可用欄位清單沒有自動編號的傳票文件項次）。
/// </summary>
public sealed class JetRuleRequirementsTests
{
    private static readonly IReadOnlyDictionary<string, string> RequiredOnlyMapping =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [JetFieldCatalog.GlDocNum] = "傳票號碼",
            [JetFieldCatalog.GlPostDate] = "傳票日期",
            [JetFieldCatalog.GlAccNum] = "科目代號",
            [JetFieldCatalog.GlAccName] = "科目名稱",
            [JetFieldCatalog.GlDescription] = "摘要",
            [JetFieldCatalog.GlAmount] = "金額",
            [JetFieldCatalog.GlDcField] = "借方旗標",
            [JetFieldCatalog.GlDcDebitCode] = "1",
            [JetFieldCatalog.GlDcCreditCode] = "0"
        };

    private static GlMappingOptions Options(string approvalDateMode) =>
        new(approvalDateMode, null, new GlManualAutoPolicy(["1"], ["0"]), []);

    private static JetRulePrerequisite Field(string semanticIdentity) =>
        new(JetRulePrerequisiteKind.GlField, semanticIdentity);

    private static JetRulePrerequisite Kind(JetRulePrerequisiteKind kind) => new(kind);

    private static FilterRuleSpec Rule(FilterRuleType type, string? field = null, string? prescreenKey = null) =>
        new(FilterJoin.And, type, prescreenKey, field, [], TextMatchMode.Contains,
            null, null, null, null, null, null);

    private static JetRuleFacts Facts(
        IReadOnlyCollection<string>? availableGlFields = null,
        bool hasLastPeriodStart = true,
        bool hasAccountMapping = true,
        bool hasAnyAccountCategory = true,
        bool hasRevenueCategory = true,
        bool hasCounterpartCategory = true,
        bool hasAuthorizedPreparers = true,
        bool? hasHolidayCalendar = true,
        AccountCategoryLabels? labels = null) =>
        new(availableGlFields, hasLastPeriodStart, hasAccountMapping, hasAnyAccountCategory, hasRevenueCategory,
            hasCounterpartCategory, hasAuthorizedPreparers, hasHolidayCalendar, labels ?? AccountCategoryLabels.BuiltIn);

    [Fact]
    public void AvailableGlFields_LineItemWithoutSource_IsAutoNumberedAndAvailable()
    {
        // 使用者 2026-10-22 裁定傳票文件項次自動編號算已配對。
        var available = JetFieldCatalog.AvailableGlFields(RequiredOnlyMapping, Options(ApprovalDateModeNames.Unmapped));

        Assert.Equal(
            [
                JetFieldCatalog.GlDocNum, JetFieldCatalog.GlLineId, JetFieldCatalog.GlPostDate, JetFieldCatalog.GlAccNum,
                JetFieldCatalog.GlAccName, JetFieldCatalog.GlDescription, JetFieldCatalog.GlAmount
            ],
            available);
    }

    [Theory]
    [InlineData(ApprovalDateModeNames.Mapped, true)]
    [InlineData(ApprovalDateModeNames.SameAsPostDate, true)]
    [InlineData(ApprovalDateModeNames.Unmapped, false)]
    public void AvailableGlFields_ApprovalDate_FollowsApprovalDateMode(string approvalDateMode, bool expected)
    {
        var mapping = new Dictionary<string, string>(RequiredOnlyMapping, StringComparer.Ordinal);
        if (approvalDateMode == ApprovalDateModeNames.Mapped)
        {
            mapping[JetFieldCatalog.GlDocDate] = "核准日期";
        }

        var available = JetFieldCatalog.AvailableGlFields(mapping, Options(approvalDateMode));

        Assert.Equal(expected, available.Contains(JetFieldCatalog.GlDocDate));
    }

    [Fact]
    public void AvailableGlFields_OptionalFieldWithoutSource_IsUnavailableUntilMapped()
    {
        var withoutCreator = JetFieldCatalog.AvailableGlFields(RequiredOnlyMapping, null);
        var mapping = new Dictionary<string, string>(RequiredOnlyMapping, StringComparer.Ordinal)
        {
            [JetFieldCatalog.GlCreateBy] = "建立人員"
        };
        var withCreator = JetFieldCatalog.AvailableGlFields(mapping, null);

        Assert.DoesNotContain(JetFieldCatalog.GlCreateBy, withoutCreator);
        Assert.Contains(JetFieldCatalog.GlCreateBy, withCreator);
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
        { PrescreenRuleKeys.PostPeriodApproval, [Field(JetFieldCatalog.GlDocDate), Kind(JetRulePrerequisiteKind.LastPeriodStart)] },
        { PrescreenRuleKeys.SuspiciousKeywords, [Field(JetFieldCatalog.GlDescription)] },
        {
            PrescreenRuleKeys.UnexpectedAccountPair,
            [
                Kind(JetRulePrerequisiteKind.AccountMapping), Kind(JetRulePrerequisiteKind.RevenueCategory),
                Kind(JetRulePrerequisiteKind.CounterpartCategory)
            ]
        },
        { PrescreenRuleKeys.TrailingZeros, [Field(JetFieldCatalog.GlAmount)] },
        { JetRuleRequirements.CreatorSummary, [Field(JetFieldCatalog.GlCreateBy)] },
        { JetRuleRequirements.RareAccounts, [Field(JetFieldCatalog.GlAccNum)] },
        { PrescreenRuleKeys.WeekendPosting, [Field(JetFieldCatalog.GlPostDate)] },
        { PrescreenRuleKeys.WeekendApproval, [Field(JetFieldCatalog.GlDocDate)] },
        { PrescreenRuleKeys.HolidayPosting, [Kind(JetRulePrerequisiteKind.HolidayCalendar), Field(JetFieldCatalog.GlPostDate)] },
        { PrescreenRuleKeys.HolidayApproval, [Kind(JetRulePrerequisiteKind.HolidayCalendar), Field(JetFieldCatalog.GlDocDate)] },
        { PrescreenRuleKeys.BlankDescription, [Field(JetFieldCatalog.GlDescription)] },
        {
            PrescreenRuleKeys.NonAuthorizedPreparer,
            [Kind(JetRulePrerequisiteKind.AuthorizedPreparers), Field(JetFieldCatalog.GlCreateBy)]
        },
        { PrescreenRuleKeys.LowFrequencyPreparer, [Field(JetFieldCatalog.GlCreateBy)] },
        { PrescreenRuleKeys.LowFrequencyAccount, [Field(JetFieldCatalog.GlAccNum)] }
    };

    [Theory]
    [MemberData(nameof(PrescreenRuleCases))]
    public void PrescreenRules_ListPrerequisitesInReasonOrder(string key, JetRulePrerequisite[] expected)
    {
        Assert.Equal(expected, JetRuleRequirements.PrescreenRules[key]);
    }

    public static TheoryData<string, FilterRuleSpec, JetRulePrerequisite[]> FilterRuleCases => new()
    {
        { "欄位值比較：建立人員", Rule(FilterRuleType.FieldValue, JetFieldCatalog.GlCreateBy), [Field(JetFieldCatalog.GlCreateBy)] },
        { "欄位值比較：傳票文件項次", Rule(FilterRuleType.FieldValue, JetFieldCatalog.GlLineId), [Field(JetFieldCatalog.GlLineId)] },
        { "欄位值比較：已移除的傳票日期", Rule(FilterRuleType.FieldValue, "voucherDate"), [] },
        { "欄位值比較：攸關資料元素欄位", Rule(FilterRuleType.FieldValue) with { FieldId = "rde.0123456789abcdef0123456789abcdef" }, [] },
        { "文字條件", Rule(FilterRuleType.Text, JetFieldCatalog.GlCreateBy), [Field(JetFieldCatalog.GlCreateBy)] },
        { "文字值清單", Rule(FilterRuleType.TextSet, JetFieldCatalog.GlApproveBy), [Field(JetFieldCatalog.GlApproveBy)] },
        { "日期區間", Rule(FilterRuleType.DateRange, JetFieldCatalog.GlDocDate), [Field(JetFieldCatalog.GlDocDate)] },
        { "金額區間", Rule(FilterRuleType.NumRange, JetFieldCatalog.GlAmount), [Field(JetFieldCatalog.GlAmount)] },
        { "科目與人員統計", Rule(FilterRuleType.EntityFrequency, JetFieldCatalog.GlApproveBy), [Field(JetFieldCatalog.GlApproveBy)] },
        { "借貸限定", Rule(FilterRuleType.DrCrOnly), [Field(JetFieldCatalog.GlAmount)] },
        { "人工或自動", Rule(FilterRuleType.ManualAuto), [Field(JetFieldCatalog.GlManual)] },
        { "借貸科目分類", Rule(FilterRuleType.AccountSide), [Kind(JetRulePrerequisiteKind.AnyAccountCategory)] },
        { "借貸科目組合（看對方科目）", Rule(FilterRuleType.AccountPair), [Kind(JetRulePrerequisiteKind.AnyAccountCategory)] },
        { "借貸科目組合", Rule(FilterRuleType.SpecialAccountCategoryPair), [Kind(JetRulePrerequisiteKind.AnyAccountCategory)] },
        { "季末前借記收入", Rule(FilterRuleType.RevenueDebitNearQuarterEnd), [Kind(JetRulePrerequisiteKind.RevenueCategory)] },
        {
            "收入無一般對方科目", Rule(FilterRuleType.RevenueWithoutNormalCounterpart),
            [Kind(JetRulePrerequisiteKind.RevenueCategory), Kind(JetRulePrerequisiteKind.CounterpartCategory)]
        },
        {
            "收入之人工分錄", Rule(FilterRuleType.ManualRevenueEntry),
            [Kind(JetRulePrerequisiteKind.RevenueCategory), Field(JetFieldCatalog.GlManual)]
        },
        {
            "編製與核准同一人", Rule(FilterRuleType.PreparerEqualsApprover),
            [Field(JetFieldCatalog.GlCreateBy), Field(JetFieldCatalog.GlApproveBy)]
        },
        // 預篩選收斂計畫階段九：兩種舊式筆數條件已停用，驗證器在查需求前就回報停用原因與重建方式，需求表不再替它們列 GL 欄位；
        // 預期值從原本的傳票建立人員、會計科目編號改成空。第一次失敗：Focused 收據 20261024-202823198-3bfffc5b8b824a9b9dbe0bc84e87383f。
        { "自訂編製人員分錄筆數（已停用）", Rule(FilterRuleType.CustomPreparerEntryCount), [] },
        { "自訂科目分錄筆數（已停用）", Rule(FilterRuleType.CustomAccountEntryCount), [] },
        {
            "預篩選：財報準備日起核准", Rule(FilterRuleType.Prescreen, prescreenKey: PrescreenRuleKeys.PostPeriodApproval),
            [Field(JetFieldCatalog.GlDocDate), Kind(JetRulePrerequisiteKind.LastPeriodStart)]
        },
        { "預篩選：已停用的回溯過帳", Rule(FilterRuleType.Prescreen, prescreenKey: "backdatedPosting"), [] },
        { "條件括號", Rule(FilterRuleType.Group), [] },
        { "傳票分錄條件", Rule(FilterRuleType.Voucher), [] },
        { "攸關資料元素條件", Rule(FilterRuleType.TypedField) with { FieldId = "rde.0123456789abcdef0123456789abcdef" }, [] }
    };

    [Theory]
    [MemberData(nameof(FilterRuleCases))]
    public void ForFilterRule_ListsWhatTheConditionUses(string name, FilterRuleSpec rule, JetRulePrerequisite[] expected)
    {
        Assert.False(string.IsNullOrEmpty(name));
        Assert.Equal(expected, JetRuleRequirements.ForFilterRule(rule));
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
            Facts(availableGlFields: [JetFieldCatalog.GlDocNum], hasAuthorizedPreparers: false));

        Assert.Equal(
            [Kind(JetRulePrerequisiteKind.RevenueCategory), Kind(JetRulePrerequisiteKind.CounterpartCategory)],
            unmet);
        Assert.Equal([Kind(JetRulePrerequisiteKind.AuthorizedPreparers), Field(JetFieldCatalog.GlCreateBy)], preparer);
    }

    [Fact]
    public void Unmet_NullFacts_AreNotChecked()
    {
        // GL 欄位清單是 null 只出現在沒有配對資料的獨立測試；假日檔是 null 表示第五步照 2026-10-04 的裁定不擋。
        var unmet = JetRuleRequirements.Unmet(
            JetRuleRequirements.PrescreenRules[PrescreenRuleKeys.HolidayApproval],
            Facts(availableGlFields: null, hasHolidayCalendar: null));
        var missingCalendar = JetRuleRequirements.Unmet(
            JetRuleRequirements.PrescreenRules[PrescreenRuleKeys.HolidayApproval],
            Facts(availableGlFields: [JetFieldCatalog.GlPostDate], hasHolidayCalendar: false));

        Assert.Empty(unmet);
        Assert.Equal([Kind(JetRulePrerequisiteKind.HolidayCalendar), Field(JetFieldCatalog.GlDocDate)], missingCalendar);
    }

    public static TheoryData<JetRulePrerequisite, string, string> SentenceCases => new()
    {
        {
            Field(JetFieldCatalog.GlCreateBy),
            "「傳票建立人員」尚未選擇來源欄",
            "「傳票建立人員」尚未選擇來源欄，請回第三步欄位配對補上。"
        },
        {
            Kind(JetRulePrerequisiteKind.LastPeriodStart),
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
            Kind(JetRulePrerequisiteKind.AuthorizedPreparers),
            "尚未匯入授權編製人員清單，請先在第二步匯入",
            "尚未匯入授權編製人員清單，請先在第二步匯入。"
        },
        {
            Kind(JetRulePrerequisiteKind.HolidayCalendar),
            "尚未上傳假日檔，請先在第二步「假日與補班日」上傳",
            "尚未上傳假日檔，請先在第二步「假日與補班日」上傳。"
        }
    };

    [Theory]
    [MemberData(nameof(SentenceCases))]
    public void Sentences_FilterReasonAndPrescreenSentence(
        JetRulePrerequisite prerequisite, string filterReason, string prescreenSentence)
    {
        // 審計員改過的分類名稱寫新名稱（使用者 2026-10-13 裁定）；第四步沒有清單最後那句下一步，GL 欄位的句子另接到哪裡補。
        var labels = AccountCategoryLabels.BuiltIn with { Revenue = "營業收入", Receivables = "應收帳款" };

        Assert.Equal(filterReason, JetRuleRequirements.Reason(prerequisite, labels));
        Assert.Equal(prescreenSentence, JetRuleRequirements.PrescreenSentence(prerequisite, labels));
    }
}
