namespace JET.Domain;

/// <summary>規則執行前需要的案件資料種類。</summary>
public enum JetRulePrerequisiteKind
{
    GlField,
    LastPeriodStart,
    AccountMapping,
    AnyAccountCategory,
    RevenueCategory,
    CounterpartCategory,
    AuthorizedPreparers,
    HolidayCalendar
}

/// <summary>一項前置條件。<see cref="GlField"/> 只在種類是 GL 欄位時有值，是欄位目錄的語意欄位。</summary>
public sealed record JetRulePrerequisite(JetRulePrerequisiteKind Kind, string? GlField = null);

/// <summary>
/// 第四步分類設定裡內建分類目前的名稱，依用途對應；審計員改過名稱時原因寫新名稱（使用者 2026-10-13 裁定）。
/// 內建分類不可刪除，沒有帶分類設定時用內建名稱。
/// </summary>
public sealed record AccountCategoryLabels(string Revenue, string Receivables, string Cash, string ReceiptInAdvance)
{
    public static AccountCategoryLabels BuiltIn { get; } = From(null);

    public static AccountCategoryLabels From(AccountTaxonomySnapshot? taxonomy) => new(
        Label(taxonomy, AccountTaxonomyBuiltIns.RevenueRole),
        Label(taxonomy, AccountTaxonomyBuiltIns.ReceivablesRole),
        Label(taxonomy, AccountTaxonomyBuiltIns.CashRole),
        Label(taxonomy, AccountTaxonomyBuiltIns.ReceiptInAdvanceRole));

    private static string Label(AccountTaxonomySnapshot? taxonomy, string semanticRole) =>
        taxonomy?.Categories.FirstOrDefault(category =>
                category.IsBuiltIn && string.Equals(category.SemanticRole, semanticRole, StringComparison.Ordinal))?.Label
        ?? AccountTaxonomyBuiltIns.All.Single(category =>
            string.Equals(category.SemanticRole, semanticRole, StringComparison.Ordinal)).Label;
}

/// <summary>
/// 判斷前置條件用的案件事實。第四步與第五步各自從案件資料組出這一份，再交給同一張表判斷。
/// <see cref="AvailableGlFields"/> 為 null 時不檢查 GL 欄位，只用在沒有配對資料的獨立測試；
/// <see cref="HasHolidayCalendar"/> 為 null 時不檢查假日檔，第五步照使用者 2026-10-04 的裁定不擋。
/// </summary>
internal sealed record JetRuleFacts(
    IReadOnlyCollection<string>? AvailableGlFields,
    bool HasLastPeriodStart,
    bool HasAccountMapping,
    bool HasAnyAccountCategory,
    bool HasRevenueCategory,
    bool HasCounterpartCategory,
    bool HasAuthorizedPreparers,
    bool? HasHolidayCalendar,
    AccountCategoryLabels CategoryLabels);

/// <summary>
/// 每條規則需要什麼：預篩選規則、第四步兩項彙總、資料驗證的「核准日不在期間」與第五步條件各自需要的 GL 語意欄位與案件資料。
/// 第四步的不適用原因、第五步的驗證器與前端的停用說明都讀這一張表，同一條規則給同一個原因
/// （使用者 2026-10-22 裁定第五步不能用 GL 沒有配對的欄位）。前端 ui-core.js 的 PRESCREEN_RULES 與
/// FILTER_RULE_PREREQUISITES 逐項鏡像預篩選規則與第五步條件；資料驗證的原因由後端寫好，前端只顯示。
/// </summary>
internal static class JetRuleRequirements
{
    /// <summary>第四步「依分錄編製者彙總」與「較少使用之科目」的回應鍵；兩者不是第五步可用的預篩選條件。</summary>
    internal const string CreatorSummary = "creatorSummary";

    internal const string RareAccounts = "rareAccounts";

    private static readonly JetRulePrerequisite AnyAccountCategory = new(JetRulePrerequisiteKind.AnyAccountCategory);
    private static readonly JetRulePrerequisite RevenueCategory = new(JetRulePrerequisiteKind.RevenueCategory);
    private static readonly JetRulePrerequisite CounterpartCategory = new(JetRulePrerequisiteKind.CounterpartCategory);

    private static JetRulePrerequisite Field(string semanticIdentity) =>
        new(JetRulePrerequisiteKind.GlField, semanticIdentity);

    /// <summary>
    /// 預篩選規則與第四步兩項彙總需要的前置條件，由規則登錄 <see cref="PrescreenRuleDefinitions"/> 衍生，順序就是原因列出的順序。
    /// </summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<JetRulePrerequisite>> PrescreenRules { get; } =
        PrescreenRuleDefinitions.All.ToDictionary(rule => rule.Key, rule => rule.Requirements, StringComparer.Ordinal);

    /// <summary>
    /// 第四步資料驗證「核准日不在期間」需要的前置條件：以傳票核准日判斷，傳票核准日不可用時這一項無法執行，
    /// 驗證報告照同一個結果寫 N/A（使用者 2026-10-23 裁定）。
    /// </summary>
    internal static IReadOnlyList<JetRulePrerequisite> OutOfPeriodApproval { get; } = [Field(JetFieldCatalog.GlDocDate)];

    /// <summary>
    /// 第五步一條條件需要的前置條件；括號與整張傳票條件由子條件各自檢查。凡條件引用核心 GL 欄位，不論比較方式都要求欄位可用；
    /// 欄位已不在欄位目錄（例如已移除的傳票日期）或是攸關資料元素欄位時，由各型別自己的檢查說明。
    /// </summary>
    internal static IReadOnlyList<JetRulePrerequisite> ForFilterRule(FilterRuleSpec rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.Type switch
        {
            FilterRuleType.Prescreen => rule.PrescreenKey is { } key && PrescreenRuleKeys.FilterableKeys.Contains(key)
                ? PrescreenRules[key]
                : [],
            FilterRuleType.FieldValue => rule.FieldId is null ? CatalogField(rule.Field) : [],
            FilterRuleType.Text or FilterRuleType.TextSet or FilterRuleType.DateRange or FilterRuleType.NumRange
                or FilterRuleType.EntityFrequency => CatalogField(rule.Field),
            FilterRuleType.DrCrOnly => [Field(JetFieldCatalog.GlAmount)],
            FilterRuleType.ManualAuto => [Field(JetFieldCatalog.GlManual)],
            FilterRuleType.AccountSide or FilterRuleType.AccountPair or FilterRuleType.SpecialAccountCategoryPair =>
                [AnyAccountCategory],
            FilterRuleType.RevenueDebitNearQuarterEnd => [RevenueCategory],
            FilterRuleType.RevenueWithoutNormalCounterpart => [RevenueCategory, CounterpartCategory],
            FilterRuleType.ManualRevenueEntry => [RevenueCategory, Field(JetFieldCatalog.GlManual)],
            FilterRuleType.PreparerEqualsApprover => [Field(JetFieldCatalog.GlCreateBy), Field(JetFieldCatalog.GlApproveBy)],
            // 已停用的型別（FilterSinglePurposeRules.RetiredTypes）沒有需求：驗證器在查需求前就回報停用原因與重建方式。
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
    /// 第五步與前端停用說明用的原因，不含句號（使用者 2026-10-13 勾選頁確認的第五步句子）。第五步的下一步由畫面在清單最後寫一次，
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
            JetRulePrerequisiteKind.LastPeriodStart => "尚未填寫期末財報準備日，請先在第一步按「修改案件資料」填寫",
            JetRulePrerequisiteKind.AccountMapping or JetRulePrerequisiteKind.AnyAccountCategory =>
                "科目配對中尚無任何科目設定分類，請先在第四步設定",
            JetRulePrerequisiteKind.RevenueCategory => $"科目配對中尚無歸類為「{labels.Revenue}」的科目，請先在第四步設定",
            JetRulePrerequisiteKind.CounterpartCategory =>
                $"科目配對中尚無歸類為「{labels.Receivables}」「{labels.Cash}」或「{labels.ReceiptInAdvance}」的科目，請先在第四步設定",
            JetRulePrerequisiteKind.AuthorizedPreparers => "尚未匯入授權編製人員清單，請先在第二步匯入",
            JetRulePrerequisiteKind.HolidayCalendar => "尚未上傳假日檔，請先在第二步「假日與補班日」上傳",
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
        JetRulePrerequisiteKind.LastPeriodStart => facts.HasLastPeriodStart,
        JetRulePrerequisiteKind.AccountMapping => facts.HasAccountMapping,
        JetRulePrerequisiteKind.AnyAccountCategory => facts.HasAnyAccountCategory,
        JetRulePrerequisiteKind.RevenueCategory => facts.HasRevenueCategory,
        JetRulePrerequisiteKind.CounterpartCategory => facts.HasCounterpartCategory,
        JetRulePrerequisiteKind.AuthorizedPreparers => facts.HasAuthorizedPreparers,
        JetRulePrerequisiteKind.HolidayCalendar => facts.HasHolidayCalendar ?? true,
        _ => throw new ArgumentOutOfRangeException(nameof(prerequisite), prerequisite.Kind, null)
    };
}
