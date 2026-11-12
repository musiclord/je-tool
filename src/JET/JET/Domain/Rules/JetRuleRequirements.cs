namespace JET.Domain;

/// <summary>規則執行前需要的案件資料種類。</summary>
public enum JetRulePrerequisiteKind
{
    GlField,
    PrepStartDate,
    AccountMapping,
    AnyAccountCategory,
    RevenueCategory,
    CounterpartCategory,
    AuthorizedPreparer,
    HolidayCalendar,
    MakeupCalendar
}

/// <summary>一項前置條件。<see cref="GlField"/> 只在種類是 GL 欄位時有值，是欄位目錄的語意欄位。</summary>
public sealed record JetRulePrerequisite(JetRulePrerequisiteKind Kind, string? GlField = null);

/// <summary>
/// 第四步分類設定裡內建分類目前的名稱，依用途對應；審計員改過名稱時原因寫新名稱。
/// 內建分類不可刪除，沒有帶分類設定時用內建名稱。
/// </summary>
public sealed record AccountCategoryLabels(string Revenue, string Receivables, string Cash, string ReceiptInAdvance)
{
    public static AccountCategoryLabels BuiltIn { get; } = From(null);

    public static AccountCategoryLabels From(AccountCategorySnapshot? categorySnapshot) => new(
        Label(categorySnapshot, AccountCategoryBuiltIns.RevenueRole),
        Label(categorySnapshot, AccountCategoryBuiltIns.ReceivablesRole),
        Label(categorySnapshot, AccountCategoryBuiltIns.CashRole),
        Label(categorySnapshot, AccountCategoryBuiltIns.ReceiptInAdvanceRole));

    private static string Label(AccountCategorySnapshot? categorySnapshot, string semanticRole) =>
        categorySnapshot?.Categories.FirstOrDefault(category =>
                category.IsBuiltIn && string.Equals(category.SemanticRole, semanticRole, StringComparison.Ordinal))?.Label
        ?? AccountCategoryBuiltIns.All.Single(category =>
            string.Equals(category.SemanticRole, semanticRole, StringComparison.Ordinal)).Label;
}

/// <summary>
/// 判斷前置條件用的案件事實。第四步與第五步各自從案件資料組出這一份，再交給同一張表判斷。
/// <see cref="AvailableGlFields"/> 為 null 時不檢查 GL 欄位，只用在沒有配對資料的獨立測試；
/// <see cref="HasHolidayCalendar"/> 為 null 時不檢查假日檔，只用在沒有案件資料的獨立測試；第四步與第五步都帶入實際狀態
/// （沒有假日清單時，第五步的假日條件停用並寫原因）。<see cref="HasMakeupCalendar"/> 是補班日清單，只有第五步的
/// 補班日條件需要；第四步沒有規則用到補班日清單，不帶這項事實。
/// </summary>
internal sealed record JetRuleFacts(
    IReadOnlyCollection<string>? AvailableGlFields,
    bool HasPrepStartDate,
    bool HasAccountMapping,
    bool HasAnyAccountCategory,
    bool HasRevenueCategory,
    bool HasCounterpartCategory,
    bool HasAuthorizedPreparers,
    bool? HasHolidayCalendar,
    AccountCategoryLabels CategoryLabels,
    bool? HasMakeupCalendar = null);

/// <summary>
/// 每條規則需要什麼：預篩選規則、第四步兩項彙總、資料驗證的「核准日不在期間」與第五步條件各自需要的 GL 語意欄位與案件資料。
/// 第四步的不適用原因、第五步的驗證器與前端的停用說明都讀這一張表，同一條規則給同一個原因
/// （第五步不能用 GL 沒有配對的欄位）。前端 ui-core.js 的 PRESCREEN_RULES 與
/// FILTER_RULE_PREREQUISITES 逐項鏡像預篩選規則與第五步條件；資料驗證的原因由後端寫好，前端只顯示。
/// </summary>
internal static class JetRuleRequirements
{
    /// <summary>第四步「依分錄編製者彙總」與「較少使用之科目」的回應鍵；兩者不是第五步可用的預篩選條件。</summary>
    internal const string PreparerSummary = "preparerSummary";

    internal const string RareAccounts = "rareAccounts";

    private static readonly JetRulePrerequisite AnyAccountCategory = new(JetRulePrerequisiteKind.AnyAccountCategory);
    private static readonly JetRulePrerequisite RevenueCategory = new(JetRulePrerequisiteKind.RevenueCategory);
    private static readonly JetRulePrerequisite CounterpartCategory = new(JetRulePrerequisiteKind.CounterpartCategory);
    private static readonly JetRulePrerequisite HolidayCalendar = new(JetRulePrerequisiteKind.HolidayCalendar);
    private static readonly JetRulePrerequisite MakeupCalendar = new(JetRulePrerequisiteKind.MakeupCalendar);

    private static JetRulePrerequisite Field(string semanticIdentity) =>
        new(JetRulePrerequisiteKind.GlField, semanticIdentity);

    /// <summary>
    /// 預篩選規則與第四步兩項彙總需要的前置條件，由規則登錄 <see cref="PrescreenRuleDefinitions"/> 衍生，順序就是原因列出的順序。
    /// </summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<JetRulePrerequisite>> PrescreenRules { get; } =
        PrescreenRuleDefinitions.All.ToDictionary(rule => rule.Key, rule => rule.Requirements, StringComparer.Ordinal);

    /// <summary>
    /// 第四步資料驗證「核准日不在期間」需要的前置條件：以傳票核准日判斷，傳票核准日不可用時這一項無法執行，
    /// 驗證報告照同一個結果寫 N/A。
    /// </summary>
    internal static IReadOnlyList<JetRulePrerequisite> OutOfPeriodApproval { get; } = [Field(JetFieldCatalog.GlApprovalDate)];

    /// <summary>
    /// 第五步一條條件需要的前置條件；括號與整張傳票條件由子條件各自檢查。凡條件引用核心 GL 欄位，不論比較方式都要求欄位可用；
    /// 欄位已不在欄位目錄（例如傳票日期）或是攸關資料元素欄位時，由各型別自己的檢查說明。
    /// </summary>
    internal static IReadOnlyList<JetRulePrerequisite> ForFilterCondition(FilterConditionSpec rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.Type switch
        {
            FilterConditionType.Prescreen => rule.PrescreenKey is { } key && PrescreenRuleKeys.FilterableKeys.Contains(key)
                ? PrescreenRules[key]
                : [],
            // 國定假日與補班日的比較方式另外需要各自的清單，排在欄位之後。
            FilterConditionType.FieldValue => [
                .. rule.FieldId is null ? CatalogField(rule.Field) : [],
                .. FieldValueConditions.IsHoliday(rule.TypedOperator) ? [HolidayCalendar] : Array.Empty<JetRulePrerequisite>(),
                .. FieldValueConditions.IsMakeupDay(rule.TypedOperator) ? [MakeupCalendar] : Array.Empty<JetRulePrerequisite>()
            ],
            FilterConditionType.Text or FilterConditionType.TextSet or FilterConditionType.DateRange or FilterConditionType.NumRange
                or FilterConditionType.EntityFrequency => CatalogField(rule.Field),
            FilterConditionType.DrCrOnly => [Field(JetFieldCatalog.GlAmount)],
            FilterConditionType.ManualAuto => [Field(JetFieldCatalog.GlManual)],
            FilterConditionType.AccountSide or FilterConditionType.AccountPair or FilterConditionType.SpecialAccountCategoryPair =>
                [AnyAccountCategory],
            FilterConditionType.RevenueDebitNearQuarterEnd => [RevenueCategory],
            FilterConditionType.RevenueWithoutNormalCounterpart => [RevenueCategory, CounterpartCategory],
            FilterConditionType.ManualRevenueEntry => [RevenueCategory, Field(JetFieldCatalog.GlManual)],
            FilterConditionType.PreparerEqualsApprover => [Field(JetFieldCatalog.GlCreatedBy), Field(JetFieldCatalog.GlApprovedBy)],
            _ => []
        };
    }

    /// <summary>
    /// 沒有滿足的前置條件，依表列順序。科目配對還沒匯入時只列這一項，不再列分類的原因；
    /// 事實是 null 的項目不檢查（見 <see cref="JetRuleFacts"/>）。
    /// </summary>
    internal static IReadOnlyList<JetRulePrerequisite> Unmet(
        IReadOnlyList<JetRulePrerequisite> prerequisites,
        JetRuleFacts facts)
    {
        ArgumentNullException.ThrowIfNull(prerequisites);
        ArgumentNullException.ThrowIfNull(facts);
        var unmet = new List<JetRulePrerequisite>();
        var accountMappingMissing = false;
        foreach (var prerequisite in prerequisites)
        {
            if ((accountMappingMissing && IsCategory(prerequisite.Kind)) || IsSatisfied(prerequisite, facts))
            {
                continue;
            }

            unmet.Add(prerequisite);
            accountMappingMissing |= prerequisite.Kind == JetRulePrerequisiteKind.AccountMapping;
        }

        return unmet;
    }

    /// <summary>
    /// 第五步與前端停用說明用的原因，不含句號。第五步的下一步由畫面在清單最後寫一次，
    /// 所以 GL 欄位的句子只寫原因；其他句子的下一步和通用說法不同，自己帶下一步。
    /// </summary>
    internal static string Reason(JetRulePrerequisite prerequisite, AccountCategoryLabels labels)
    {
        ArgumentNullException.ThrowIfNull(prerequisite);
        ArgumentNullException.ThrowIfNull(labels);
        return prerequisite.Kind switch
        {
            JetRulePrerequisiteKind.GlField =>
                $"「{JetFieldCatalog.GlSemanticFieldMappingLabel(prerequisite.GlField!)}」尚未選擇來源欄",
            JetRulePrerequisiteKind.PrepStartDate => "尚未填寫期末財報準備日，請先在第一步按「修改案件資料」填寫",
            JetRulePrerequisiteKind.AccountMapping or JetRulePrerequisiteKind.AnyAccountCategory =>
                "科目配對中尚無任何科目設定分類，請先在第四步設定",
            JetRulePrerequisiteKind.RevenueCategory => $"科目配對中尚無歸類為「{labels.Revenue}」的科目，請先在第四步設定",
            JetRulePrerequisiteKind.CounterpartCategory =>
                $"科目配對中尚無歸類為「{labels.Receivables}」「{labels.Cash}」或「{labels.ReceiptInAdvance}」的科目，請先在第四步設定",
            JetRulePrerequisiteKind.AuthorizedPreparer => "尚未匯入授權編製人員清單，請先在第二步匯入",
            JetRulePrerequisiteKind.HolidayCalendar => "尚未上傳國定假日清單，請先在第二步「國定假日與補班日」上傳",
            JetRulePrerequisiteKind.MakeupCalendar => "尚未上傳補班日清單，請先在第二步「國定假日與補班日」上傳",
            _ => throw new ArgumentOutOfRangeException(nameof(prerequisite), prerequisite.Kind, null)
        };
    }

    /// <summary>
    /// 第四步的原因句：一個原因一句，寫出缺什麼、到哪裡補。第四步沒有第五步清單最後那句下一步，GL 欄位的句子另接到哪裡補。
    /// </summary>
    internal static string PrescreenSentence(JetRulePrerequisite prerequisite, AccountCategoryLabels labels) =>
        prerequisite.Kind == JetRulePrerequisiteKind.GlField
            ? Reason(prerequisite, labels) + "，請回第三步欄位配對補上。"
            : Reason(prerequisite, labels) + "。";

    /// <summary>第四步一項檢查的原因：沒有滿足的前置條件依序各寫一句，接成一段。</summary>
    internal static string PrescreenSentences(IReadOnlyList<JetRulePrerequisite> unmet, AccountCategoryLabels labels)
    {
        ArgumentNullException.ThrowIfNull(unmet);
        return string.Concat(unmet.Select(prerequisite => PrescreenSentence(prerequisite, labels)));
    }

    private static IReadOnlyList<JetRulePrerequisite> CatalogField(string? field) =>
        field is not null && JetFieldCatalog.GlFields.Any(candidate =>
            string.Equals(candidate.SemanticIdentity, field, StringComparison.Ordinal))
            ? [Field(field)]
            : [];

    private static bool IsCategory(JetRulePrerequisiteKind kind) =>
        kind is JetRulePrerequisiteKind.AnyAccountCategory
            or JetRulePrerequisiteKind.RevenueCategory
            or JetRulePrerequisiteKind.CounterpartCategory;

    private static bool IsSatisfied(JetRulePrerequisite prerequisite, JetRuleFacts facts) => prerequisite.Kind switch
    {
        JetRulePrerequisiteKind.GlField => facts.AvailableGlFields is not { } available
            || available.Contains(prerequisite.GlField!, StringComparer.Ordinal),
        JetRulePrerequisiteKind.PrepStartDate => facts.HasPrepStartDate,
        JetRulePrerequisiteKind.AccountMapping => facts.HasAccountMapping,
        JetRulePrerequisiteKind.AnyAccountCategory => facts.HasAnyAccountCategory,
        JetRulePrerequisiteKind.RevenueCategory => facts.HasRevenueCategory,
        JetRulePrerequisiteKind.CounterpartCategory => facts.HasCounterpartCategory,
        JetRulePrerequisiteKind.AuthorizedPreparer => facts.HasAuthorizedPreparers,
        JetRulePrerequisiteKind.HolidayCalendar => facts.HasHolidayCalendar ?? true,
        JetRulePrerequisiteKind.MakeupCalendar => facts.HasMakeupCalendar ?? true,
        _ => throw new ArgumentOutOfRangeException(nameof(prerequisite), prerequisite.Kind, null)
    };
}
