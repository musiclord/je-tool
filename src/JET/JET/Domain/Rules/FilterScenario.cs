namespace JET.Domain;

/// <summary>
/// 進階篩選條件 AST。
/// 前端 Query Builder 只組裝 JSON；後端解析成本型別後交由
/// Infrastructure 轉成參數化 SQL，在資料庫內以集合式查詢評估。
/// 規則間與群組間的結合律為左折疊累積括號：((c1 OP c2) OP c3)。
/// </summary>
public enum FilterJoin
{
    And,
    Or
}

public enum FilterConditionType
{
    Prescreen,
    DrCrOnly,
    ManualAuto,
    AccountPair,
    SpecialAccountCategoryPair,
    RevenueDebitNearQuarterEnd,
    RevenueWithoutNormalCounterpart,
    ManualRevenueEntry,
    PreparerEqualsApprover,
    FieldValue,
    AccountSide,
    EntityFrequency,
    Group,
    Voucher
}

/// <summary>
/// 進階篩選 wire 與編譯的有界契約。每條條件的值清單先以畫面看得懂的上限擋下；
/// 整個情境編譯出的 SQL 另以總參數上限 <see cref="FilterScenarioLimits.MaxCompiledParameters"/> 作最後防線。
/// </summary>
public static class FilterScenarioLimits
{
    public const int MaxNestingDepth = 8;

    /// <summary>
    /// 科目配對單側可多選的分類上限。案件的科目分類沒有筆數上限，故雙側各自先以可理解的
    /// UI 上限 fail closed；整個情境仍受 <see cref="MaxCompiledParameters"/> 的最後防線約束。
    /// </summary>
    public const int MaxCategoryIdsPerSide = 100;

    /// <summary>欄位值條件每條值清單的上限：符合清單、包含任一文字、開頭或結尾符合與尾數，每條要有 1 到 100 個值。</summary>
    public const int MaxTypedInValuesPerRule = 100;

    public const int MaxCompiledParameters = 2_000;

    /// <summary>
    /// 每個案件可保存的篩選情境上限。filter.commit 的 handler 與 AuditCore 規劃共用這個值；
    /// 前端 ui-core.js 的 FILTER_MAX_SAVED_SCENARIOS 只是就近提示用的鏡像，由架構測試比對。
    /// </summary>
    public const int MaxSavedScenarios = 10;

    /// <summary>
    /// 超過上限時回給畫面的句子。三個會檢查上限的入口都用這一句；前端 ui-core.js 用 FILTER_MAX_SAVED_SCENARIOS
    /// 拼出相同的句子，由架構測試比對。
    /// </summary>
    public static readonly string MaxSavedScenariosMessage =
        $"最多可儲存 {MaxSavedScenarios} 個篩選情境，請先移除不需要的情境。";

    /// <summary>
    /// 情境名稱上限，儲存時超過就回錯誤。前端 ui-core.js 的
    /// FILTER_MAX_SCENARIO_NAME_LENGTH 是輸入框的鏡像，由架構測試比對。
    /// </summary>
    public const int MaxNameLength = TextStorageLimits.TextUtf16CodeUnits;
}

/// <summary>科目配對分析的三模式。</summary>
public static class AccountPairModes
{
    public const string Exact = "exact";
    public const string DebitAnchor = "debitAnchor";
    public const string CreditAnchor = "creditAnchor";
}

/// <summary>
/// 特殊科目類別配對（specialAccountCategoryPair）的三個模式。每個模式同時表達借方類別 A、貸方類別 B
/// 與「存在」「不存在」的語意（含否定），和 AccountPairModes 的錨定語意分開，是不同的條件，見 GlRulePredicates。
/// </summary>
public static class SpecialAccountCategoryPairModes
{
    /// <summary>借方為 A 且貸方為 B：同傳票同時有 A 借與 B 貸。</summary>
    public const string DrAndCr = "drAndCr";

    /// <summary>借方為 A 且貸方「非」B：同傳票有 A 借、但無任何 B 貸。</summary>
    public const string DrNotCr = "drNotCr";

    /// <summary>借方「非」A 且貸方為 B：同傳票有 B 貸、但無任何 A 借。</summary>
    public const string NotDrCr = "notDrCr";
}

/// <summary>
/// 單一條件規則。各 Type 只使用對應欄位：
/// Prescreen → PrescreenKey；FieldValue → Field 或 FieldId、TypedOperator 與比較值；
/// DrCrOnly → DrCr（"debit"|"credit"）；ManualAuto → IsManual；
/// AccountPair / SpecialAccountCategoryPair → PairMode + DebitCategoryIds/CreditCategoryIds；
/// 常用篩選條件專用的型別：RevenueDebitNearQuarterEnd → WindowDays（季末視窗天數）；
/// RevenueWithoutNormalCounterpart / ManualRevenueEntry / PreparerEqualsApprover → 無參數。
/// UnknownJoin：wire 的 join 有值卻不是 AND/OR 時，解析層把原始值標記在這裡而不擲錯
/// （materialize 重新解析已存 JSON 不經驗證，解析層必須容忍），由 FilterScenarioValidator
/// 擋成 invalid_scenario；未知 join 若被默默當成 AND，前端組錯條件時就不會被發現。
/// </summary>
public sealed record FilterConditionSpec(
    FilterJoin Join,
    FilterConditionType Type,
    string? PrescreenKey,
    string? Field,
    IReadOnlyList<string> Keywords,
    string? DrCr,
    bool? IsManual,
    string? PairMode = null,
    int? WindowDays = null,
    string? UnknownJoin = null)
{
    public string? CountUnit { get; init; }
    public string? CountOperator { get; init; }
    public int? CountFrom { get; init; }
    public int? CountTo { get; init; }

    /// <summary>
    /// 科目配對借方側的多選分類身分（`debitCategoryIds`）。空集合代表 wire 沒有帶陣列。
    /// </summary>
    public IReadOnlyList<string> DebitCategoryIds { get; init; } = [];

    /// <summary>科目配對貸方側的多選分類身分（`creditCategoryIds`）；語意同借方側。</summary>
    public IReadOnlyList<string> CreditCategoryIds { get; init; } = [];

    /// <summary>驗證與編譯都以這份借方側分類集合為準（已去重、排序）。</summary>
    internal IReadOnlyList<string> EffectiveDebitCategoryIds =>
        AccountPairCategorySelection.Canonicalize(DebitCategoryIds);

    /// <summary>驗證與編譯都以這份貸方側分類集合為準（已去重、排序）。</summary>
    internal IReadOnlyList<string> EffectiveCreditCategoryIds =>
        AccountPairCategorySelection.Canonicalize(CreditCategoryIds);

    /// <summary>欄位值條件選的攸關資料元素欄位身分（`rde.&lt;32 lowercase hex&gt;`）；核心 GL 欄位用 Field。</summary>
    public string? FieldId { get; init; }

    /// <summary>欄位值條件的比較方式代號（各欄位種類能用的見 <see cref="FieldValueConditions.Operators"/>）。</summary>
    public string? TypedOperator { get; init; }

    /// <summary>單一比較值 `value` 的原始 wire 字串；null＝wire 沒帶。</summary>
    public string? TypedValue { get; init; }

    /// <summary>介於與不介於的 `from` 原始 wire 字串；null＝wire 沒帶。</summary>
    public string? TypedFrom { get; init; }

    /// <summary>介於與不介於的 `to` 原始 wire 字串；null＝wire 沒帶。</summary>
    public string? TypedTo { get; init; }

    /// <summary>值清單 `values`；null＝wire 沒帶陣列，空陣列由驗證擋下。</summary>
    public IReadOnlyList<string>? TypedValues { get; init; }

    /// <summary>金額欄位的欄位值條件必填的 `amountBasis` 原始 wire 字串；其他欄位不得帶它。</summary>
    public string? AmountBasis { get; init; }

    // 空白值的處理方式。null 表示情境存檔時沒有這個欄位，比對時當成不含空白。
    public bool? IncludeBlank { get; init; }
    public string? CategoryMode { get; init; }
    public IReadOnlyList<string> CategoryIds { get; init; } = [];
    public string? CategorySelection { get; init; }
    public IReadOnlyList<FilterConditionSpec> Conditions { get; init; } = [];
    public string? Quantifier { get; init; }
    public string? Side { get; init; }

    public IEnumerable<FilterConditionSpec> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Conditions)
            foreach (var descendant in child.DescendantsAndSelf()) yield return descendant;
    }

    public bool IsVoucherCondition => Type == FilterConditionType.Voucher
        || Type == FilterConditionType.Group && Conditions.Count > 0 && Conditions.All(rule => rule.IsVoucherCondition);
}

/// <summary>
/// 科目配對單側選擇的正規化（驗證與編譯的唯一收斂點）。
/// 只認 `debitCategoryIds`／`creditCategoryIds` 陣列；沒有帶陣列時是空集合，
/// 由 validator 依配對模式要求必填。
/// 去重後依 ordinal 排序，因此同一組分類的任何排列都編譯出逐字相同的 SQL。
/// </summary>
internal static class AccountPairCategorySelection
{
    internal static IReadOnlyList<string> Canonicalize(IReadOnlyList<string> categoryIds) =>
        categoryIds
            .Select(static value => value?.Trim() ?? string.Empty)
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
}

/// <summary>UnknownJoin 的語意同 FilterConditionSpec：未知 join 的原始值標記，由驗證層 fail-loud。</summary>
public sealed record FilterGroupSpec(
    FilterJoin Join,
    IReadOnlyList<FilterConditionSpec> Conditions,
    string? UnknownJoin = null)
{
    /// <summary>wire 的 matchScope 寫了 row 以外的值時保留原值，交由 validator 當成無法讀取的內容。</summary>
    public string? UnknownMatchScope { get; init; }
}

/// <summary>
/// 篩選情境來源標記（wire 欄位 scenario.source）：標明此情境是使用者手寫，
/// 還是用了「常用篩選條件」的卡片。空值、null 與未知值一律視為使用者手寫。
/// </summary>
public static class FilterScenarioSources
{
    /// <summary>常用篩選條件：用了卡片的情境。名稱與動機留白時的自動文字見 Application 的 FilterScenarioAutomaticMetadata。</summary>
    public const string Common = "common";

    public static bool IsCommon(string? source) =>
        string.Equals(source?.Trim(), Common, StringComparison.Ordinal);
}

public sealed record FilterScenarioSpec(
    string Name,
    string Rationale,
    IReadOnlyList<FilterGroupSpec> Groups,
    string? Source = null);

/// <summary>
/// 驗證所需的案件事實：期末財報準備日、科目配對與分類、授權編製人員清單，以及能使用的 GL 欄位。
/// 每條條件需要哪些事實由 <see cref="JetRuleRequirements"/> 決定。
/// </summary>
public sealed record FilterValidationContext(
    bool HasPrepStartDate,
    bool HasAccountMapping,
    bool HasAuthorizedPreparers,
    GlPopulationScope PopulationScope = GlPopulationScope.AuditPeriod,
    bool HasAnyAccountCategory = false,
    bool HasRevenueCategory = false,
    bool HasCounterpartCategory = false)
{
    /// <summary>
    /// 條件能使用的 GL 語意欄位；正式查詢由 <see cref="JetFieldCatalog.AvailableGlFields"/> 依已確認的配對算出。
    /// null 只供沒有配對資料的獨立述詞驗證，不檢查 GL 欄位。
    /// </summary>
    public IReadOnlyList<string>? AvailableGlFields { get; init; }

    /// <summary>
    /// GL 中每一筆分錄都有值的欄位；正式查詢由 <see cref="JetFieldCatalog.NeverBlankGlFields"/> 依已確認的配對算出。
    /// 對這些欄位選「空白」一定是 0 筆，檢查時擋下並寫出改法。null 的用法和 <see cref="AvailableGlFields"/> 相同，不檢查。
    /// </summary>
    public IReadOnlyList<string>? NeverBlankGlFields { get; init; }

    /// <summary>原因裡的分類名稱；沒有帶分類設定的呼叫端用內建名稱。</summary>
    public AccountCategoryLabels CategoryLabels { get; init; } = AccountCategoryLabels.BuiltIn;

    /// <summary>
    /// 案件有沒有上傳國定假日清單。沒有清單時第五步停用假日條件並寫原因。
    /// null 只供沒有案件資料的獨立述詞驗證，不檢查假日檔；正式查詢由 <c>FilterValidationContextFactory</c> 帶入。
    /// </summary>
    public bool? HasHolidayCalendar { get; init; }

    /// <summary>案件有沒有上傳補班日清單；沒有清單時第五步比照國定假日停用補班日條件並寫原因。null 的用法同上。</summary>
    public bool? HasMakeupCalendar { get; init; }

    /// <summary>交給規則需求表的案件事實。</summary>
    internal JetRuleFacts RuleFacts => new(
        AvailableGlFields,
        HasPrepStartDate,
        HasAccountMapping,
        HasAnyAccountCategory,
        HasRevenueCategory,
        HasCounterpartCategory,
        HasAuthorizedPreparers,
        HasHolidayCalendar,
        CategoryLabels,
        HasMakeupCalendar);

    /// <summary>
    /// 目前案件科目分類的全部分類身分，供科目配對多選驗證。預設只含五個內建 ID——
    /// 它們由 <see cref="AccountCategoryInvariant"/> 保證不可刪除，因此沒有帶科目分類的
    /// 呼叫端仍會擋掉未知 custom ID，不會 fail open。
    /// </summary>
    public IReadOnlyList<string> CategoryIds { get; init; } =
        AccountCategoryBuiltIns.All.Select(static category => category.CategoryId).ToArray();

    /// <summary>
    /// 目前案件已確認配對的攸關資料元素欄位，欄位值條件只能選這裡的欄位。預設空集合＝任何用攸關資料元素欄位的
    /// 條件都因欄位不存在而擋下，不會誤放行。
    /// </summary>
    public IReadOnlyList<GlRdeFieldMetadata> RdeFields { get; init; } = [];

    /// <summary>金額比較值解析所需的案件 MoneyScale。</summary>
    public int MoneyScale { get; init; } = 1;

    public DateParseOptions DateParseOptions { get; init; } = DateParseOptions.Default;
}

/// <summary>
/// 條件 AST 的領域驗證：回傳所有錯誤訊息（空集合 = 合法）。
/// 識別字安全的第一道防線：Field / PrescreenKey 必須在白名單內。
/// </summary>
public static partial class FilterScenarioValidator
{
    // 已停用的預篩選鍵，只用來辨認用到它的已儲存情境，並說明要刪除這條條件，不是可用條件。
    private const string RetiredBackdatedPostingKey = "backdatedPosting";

    public static IReadOnlyList<string> Validate(FilterScenarioSpec scenario, FilterValidationContext context,
        bool forSave = true)
    {
        var errors = new FilterValidationErrors();

        // 情境名稱與篩選動機不是必填，留白時儲存前已依條件補上自動文字。
        if (forSave && scenario.Name.Length > FilterScenarioLimits.MaxNameLength)
        {
            errors.Add($"情境名稱不可超過 {FilterScenarioLimits.MaxNameLength} 字，請縮短名稱後再儲存。");
        }

        if (scenario.Groups.Count == 0)
        {
            errors.Add("尚未設定任何條件");
            return errors;
        }

        for (var groupIndex = 0; groupIndex < scenario.Groups.Count; groupIndex++)
        {
            var group = scenario.Groups[groupIndex];
            var groupLabel = $"第 {groupIndex + 1} 組";

            // 未知 join fail-loud（含左折疊時被忽略的第一個群組——亂值代表前端組裝有誤）。
            if (group.UnknownJoin is not null)
            {
                FilterScenarioCorruption.Add(errors, groupLabel, "group_join_invalid");
            }

            if (group.UnknownMatchScope is not null)
            {
                FilterScenarioCorruption.Add(errors, groupLabel, "group_match_scope_invalid");
            }

            if (group.Conditions.Count == 0)
            {
                errors.Add($"{groupLabel}：此條件組尚未設定條件");
                continue;
            }

            for (var ruleIndex = 0; ruleIndex < group.Conditions.Count; ruleIndex++)
            {
                ValidateRule(
                    group.Conditions[ruleIndex],
                    $"{groupLabel}第 {ruleIndex + 1} 條",
                    context,
                    errors);
            }
        }

        return errors;
    }

    private static void ValidateCategorySelectionMode(FilterConditionSpec rule, string label, List<string> errors)
    {
        if (rule.CategorySelection is not (null or "role" or "node" or "subtree"))
            FilterScenarioCorruption.Add(errors, label, "category_selection_mode_invalid");
    }

    private static void ValidateRule(
        FilterConditionSpec rule,
        string label,
        FilterValidationContext context,
        List<string> errors,
        int depth = 0,
        bool insideVoucher = false)
    {
        if (depth > FilterScenarioLimits.MaxNestingDepth)
        {
            FilterScenarioCorruption.Add(errors, label, "nesting_too_deep");
            return;
        }
        // 未知 join fail-loud（含左折疊時被忽略的第一條規則），與型別專屬錯誤合併列出。
        if (rule.UnknownJoin is not null)
        {
            FilterScenarioCorruption.Add(errors, label, "condition_join_invalid");
        }

        // 條件需要的案件資料只看規則需求表，第四步與前端停用說明讀同一張表（第五步不能用 GL 沒有配對的欄位）。
        var facts = context.RuleFacts;
        var unmet = JetRuleRequirements.Unmet(JetRuleRequirements.ForFilterCondition(rule), facts);
        foreach (var prerequisite in unmet)
        {
            errors.Add($"{label}：{JetRuleRequirements.Reason(prerequisite, facts.CategoryLabels)}");
        }

        switch (rule.Type)
        {
            case FilterConditionType.Group:
            case FilterConditionType.Voucher:
                if (rule.Conditions.Count == 0) errors.Add($"{label}：請加入至少一條子條件。");
                if (rule.Type == FilterConditionType.Voucher)
                {
                    if (insideVoucher) errors.Add($"{label}：整張傳票條件中不可再加入整張傳票條件");
                    // 整張傳票條件不接受借貸範圍，讀到時當成無法讀取的內容，已儲存情境裡的這種寫法也不讀回。
                    if (rule.Side is not null) FilterScenarioCorruption.Add(errors, label, "voucher_side_invalid");
                    if (rule.Quantifier is not ("any" or "all" or "none")) FilterScenarioCorruption.Add(errors, label, "voucher_quantifier_invalid");
                }
                for (var i = 0; i < rule.Conditions.Count; i++)
                    ValidateRule(rule.Conditions[i], $"{label} 子條件 {i + 1}", context, errors,
                        depth + 1, insideVoucher || rule.Type == FilterConditionType.Voucher);
                break;
            case FilterConditionType.FieldValue:
                ValidateFieldValue(rule, label, context, errors, fieldUnavailable: unmet.Count > 0);
                break;
            case FilterConditionType.AccountSide:
                ValidateCategorySelectionMode(rule, label, errors);
                // 借貸科目分類條件不接受借貸方向與「這一側都不屬於」，讀到時當成無法讀取的內容。
                if (rule.DrCr is not null)
                    FilterScenarioCorruption.Add(errors, label, "account_side_drcr_invalid");
                if (rule.CategoryMode is not ("is" or "isNot"))
                    FilterScenarioCorruption.Add(errors, label, "account_side_category_mode_invalid");
                ValidateCategorySelection(rule.CategoryIds, "指定", label, context, errors);
                break;
            case FilterConditionType.Prescreen:
                ValidatePrescreen(rule, label, errors);
                break;
            case FilterConditionType.DrCrOnly:
                if (rule.DrCr is not ("debit" or "credit"))
                {
                    FilterScenarioCorruption.Add(errors, label, "drcr_only_invalid");
                }
                break;
            case FilterConditionType.ManualAuto:
                if (rule.IsManual is null)
                {
                    FilterScenarioCorruption.Add(errors, label, "manual_auto_missing");
                }
                break;
            case FilterConditionType.AccountPair:
                ValidateCategorySelectionMode(rule, label, errors);
                ValidateAccountPair(rule, label, context, errors);
                break;
            case FilterConditionType.SpecialAccountCategoryPair:
                ValidateCategorySelectionMode(rule, label, errors);
                ValidateSpecialAccountCategoryPair(rule, label, context, errors);
                break;
            case FilterConditionType.EntityFrequency:
                EntityFrequencyConditions.Validate(rule, label, errors);
                break;
            case FilterConditionType.RevenueDebitNearQuarterEnd:
                if (rule.WindowDays is not (>= QuarterEndWindows.MinWindowDays
                    and <= QuarterEndWindows.MaxWindowDays))
                {
                    errors.Add($"{label}：季末前天數須為 {QuarterEndWindows.MinWindowDays} 到 "
                        + $"{QuarterEndWindows.MaxWindowDays} 的整數");
                }
                break;
            case FilterConditionType.RevenueWithoutNormalCounterpart:
            case FilterConditionType.ManualRevenueEntry:
            case FilterConditionType.PreparerEqualsApprover:
                // 需要的分類與欄位都在規則需求表；缺收入科目與缺對方科目各寫一句。
                break;
            default:
                FilterScenarioCorruption.Add(errors, label, "condition_type_unsupported");
                break;
        }
    }

    private static void ValidateTypedOperands(
        FilterConditionSpec rule,
        GlRdeFieldMetadata field,
        string label,
        FilterValidationContext context,
        List<string> errors)
    {
        var op = rule.TypedOperator!;
        var hasValue = rule.TypedValue is not null;
        var hasFrom = rule.TypedFrom is not null;
        var hasTo = rule.TypedTo is not null;
        var hasValues = rule.TypedValues is not null;

        if (TypedFieldOperatorSets.IsBlankFamily(op))
        {
            if (hasValue || hasFrom || hasTo || hasValues)
            {
                FilterScenarioCorruption.Add(errors, label, "typed_blank_operand_present");
            }

            return;
        }

        if (TypedFieldOperatorSets.IsBetweenOperator(op))
        {
            if (hasValue || hasValues)
            {
                FilterScenarioCorruption.Add(errors, label, "typed_between_extra_operand");
            }

            if (!hasFrom || !hasTo)
            {
                FilterScenarioCorruption.Add(errors, label, "typed_between_bound_missing");
                return;
            }

            ValidateTypedBetweenBounds(rule, field, label, context, errors);
            return;
        }

        if (TypedFieldOperatorSets.IsSetOperator(op))
        {
            if (hasValue || hasFrom || hasTo)
            {
                FilterScenarioCorruption.Add(errors, label, "typed_set_extra_operand");
            }

            if (!hasValues)
            {
                errors.Add($"{label}：{ValueCountReason(FilterScenarioLimits.MaxTypedInValuesPerRule)}");
                return;
            }

            var values = rule.TypedValues!;
            if (values.Count is 0 or > FilterScenarioLimits.MaxTypedInValuesPerRule)
            {
                errors.Add($"{label}：{ValueCountReason(FilterScenarioLimits.MaxTypedInValuesPerRule)}");
                return;
            }

            for (var index = 0; index < values.Count; index++)
            {
                ValidateTypedOperandValue(
                    values[index], field, $"{label}：清單第 {index + 1} 個值", context, errors);
            }

            return;
        }

        // single-value operators
        if (hasFrom || hasTo || hasValues)
        {
            FilterScenarioCorruption.Add(errors, label, "typed_single_extra_operand");
        }

        if (!hasValue)
        {
            FilterScenarioCorruption.Add(errors, label, "typed_single_value_missing");
            return;
        }

        ValidateTypedOperandValue(rule.TypedValue, field, label, context, errors);
    }

    private static void ValidateTypedBetweenBounds(
        FilterConditionSpec rule,
        GlRdeFieldMetadata field,
        string label,
        FilterValidationContext context,
        List<string> errors)
    {
        switch (field.ValueType)
        {
            case RdeFieldValueTypeNames.Date:
            {
                var fromValid = TypedFieldOperandRules.TryNormalizeDate(rule.TypedFrom, context.DateParseOptions, out var fromIso);
                var toValid = TypedFieldOperandRules.TryNormalizeDate(rule.TypedTo, context.DateParseOptions, out var toIso);
                if (!fromValid)
                {
                    errors.Add($"{label}：{DateFormatReason(rule.TypedFrom)}");
                }

                if (!toValid)
                {
                    errors.Add($"{label}：{DateFormatReason(rule.TypedTo)}");
                }

                if (fromValid && toValid && string.CompareOrdinal(fromIso, toIso) > 0)
                {
                    errors.Add($"{label}：起日晚於迄日");
                }

                break;
            }

            case RdeFieldValueTypeNames.Amount:
            {
                var fromValid = TypedFieldOperandRules.TryNormalizeMoney(
                    rule.TypedFrom, context.MoneyScale, out var fromScaled);
                var toValid = TypedFieldOperandRules.TryNormalizeMoney(
                    rule.TypedTo, context.MoneyScale, out var toScaled);
                if (!fromValid)
                {
                    errors.Add($"{label}：{AmountFormatReason(rule.TypedFrom)}");
                }

                if (!toValid)
                {
                    errors.Add($"{label}：{AmountFormatReason(rule.TypedTo)}");
                }

                if (fromValid && toValid && fromScaled > toScaled)
                {
                    errors.Add($"{label}：下限金額大於上限金額");
                }

                break;
            }

            default:
                // 文字欄位沒有介於；比較方式在前面已依欄位種類檢查。
                break;
        }
    }

    private static void ValidateTypedOperandValue(
        string? raw,
        GlRdeFieldMetadata field,
        string label,
        FilterValidationContext context,
        List<string> errors)
    {
        switch (field.ValueType)
        {
            case RdeFieldValueTypeNames.Text:
                if (!TypedFieldOperandRules.TryNormalizeText(raw, out _))
                {
                    errors.Add($"{label}：比對文字不得為空白");
                }

                break;
            case RdeFieldValueTypeNames.Date:
                if (!TypedFieldOperandRules.TryNormalizeDate(raw, context.DateParseOptions, out _))
                {
                    errors.Add($"{label}：{DateFormatReason(raw)}");
                }

                break;
            case RdeFieldValueTypeNames.Amount:
                if (!TypedFieldOperandRules.TryNormalizeMoney(raw, context.MoneyScale, out _))
                {
                    errors.Add($"{label}：{AmountFormatReason(raw)}");
                }

                break;
        }
    }

    /// <summary>
    /// 科目配對分析：需科目配對已匯入；模式決定必填分類——
    /// exact 需借貸雙方、debitAnchor 只需借方、creditAnchor 只需貸方。
    /// 每一側都是案件科目分類的分類身分的多選集合；單選分類欄位不讀。
    /// </summary>
    private static void ValidateAccountPair(FilterConditionSpec rule, string label, FilterValidationContext context, List<string> errors)
    {
        var needDebit = rule.PairMode is AccountPairModes.Exact or AccountPairModes.DebitAnchor;
        var needCredit = rule.PairMode is AccountPairModes.Exact or AccountPairModes.CreditAnchor;

        if (rule.PairMode is not (AccountPairModes.Exact or AccountPairModes.DebitAnchor or AccountPairModes.CreditAnchor))
        {
            FilterScenarioCorruption.Add(errors, label, "account_pair_mode_invalid");
            return;
        }

        if (needDebit)
        {
            ValidateCategorySelection(rule.EffectiveDebitCategoryIds, "借方", label, context, errors);
        }

        if (needCredit)
        {
            ValidateCategorySelection(rule.EffectiveCreditCategoryIds, "貸方", label, context, errors);
        }
    }

    /// <summary>
    /// 單側多選分類的共同驗證：至少一個、不超過上限、每個身分都存在於目前案件的科目分類。
    /// 空集合必須擋在這裡——否定模式的 NOT EXISTS 遇到空集合會反轉成全命中（同非授權編製人員的空名單檢查）。
    /// </summary>
    private static void ValidateCategorySelection(
        IReadOnlyList<string> categoryIds,
        string sideLabel,
        string label,
        FilterValidationContext context,
        List<string> errors)
    {
        if (categoryIds.Count == 0)
        {
            errors.Add($"{label}：尚未選擇{sideLabel}分類");
            return;
        }

        if (categoryIds.Count > FilterScenarioLimits.MaxCategoryIdsPerSide)
        {
            FilterScenarioCorruption.Add(errors, label, "category_selection_too_many");
            return;
        }

        // 分類身分是內部代號；已刪除的分類拿不到顯示名稱，因此只說明有分類不存在並給出下一步。
        if (categoryIds.Any(categoryId => !context.CategoryIds.Contains(categoryId, StringComparer.Ordinal)))
        {
            errors.Add($"{label}：所選{sideLabel}分類中含有已刪除的分類");
        }
    }

    /// <summary>
    /// 考量特殊科目類別配對：顯式雙類別 + 否定。AccountPair 的姊妹條件，三模式
    /// （drAndCr / drNotCr / notDrCr）皆需科目配對已匯入、且借方類別 A 與貸方類別 B
    /// 兩者皆在白名單內（否定模式同樣需要 B 或 A 才能判定「不存在」，故雙方一律必填）。
    /// </summary>
    private static void ValidateSpecialAccountCategoryPair(FilterConditionSpec rule, string label, FilterValidationContext context, List<string> errors)
    {
        if (rule.PairMode is not (SpecialAccountCategoryPairModes.DrAndCr
            or SpecialAccountCategoryPairModes.DrNotCr
            or SpecialAccountCategoryPairModes.NotDrCr))
        {
            FilterScenarioCorruption.Add(errors, label, "special_pair_mode_invalid");
            return;
        }

        ValidateCategorySelection(rule.EffectiveDebitCategoryIds, "借方", label, context, errors);
        ValidateCategorySelection(rule.EffectiveCreditCategoryIds, "貸方", label, context, errors);
    }

    /// <summary>
    /// 預篩選條件本身的檢查：已停用或不能當條件的鍵。需要的配對、名單與分類由規則需求表檢查；
    /// 授權編製人員清單沒有匯入時，空名單會讓 NOT IN 述詞反轉成全命中，所以需求表一定要列這一項。
    /// </summary>
    private static void ValidatePrescreen(FilterConditionSpec rule, string label, List<string> errors)
    {
        // 傳票日期與回溯過帳是已停用的條件；已儲存情境用到時，明確說明這條條件要刪除。
        if (string.Equals(rule.PrescreenKey, RetiredBackdatedPostingKey, StringComparison.Ordinal))
        {
            errors.Add($"{label}：「回溯過帳」條件已停用，請刪除此條件");
            return;
        }

        if (rule.PrescreenKey is null || !PrescreenRuleKeys.FilterableKeys.Contains(rule.PrescreenKey))
        {
            FilterScenarioCorruption.Add(errors, label, "prescreen_key_not_filterable");
        }
    }

    /// <summary>條件用到目前已沒有的欄位；不印內部代號。</summary>
    internal static string MissingFieldReason(string? field) => "此條件使用的欄位已不存在";

    private static string DateFormatReason(string? value) => $"日期「{value}」無法辨識，請以 2026-12-31 的格式輸入";

    /// <summary>金額不是數字；解析情境時讀到的金額不是數字，也用這一句。</summary>
    internal static string AmountFormatReason(string? value) => $"金額「{value}」不是數字";

    internal static string ValueCountReason(int max) => $"請填 1 到 {max} 個值";

    /// <summary>
    /// 金額的「整數尾數符合」：尾數樣態放在 Keywords，
    /// 每組須為 1–12 位純數字（位數上限沿用 trailing zeros 的 long 溢位防線）。
    /// </summary>
    /// <param name="noun">原因裡怎麼稱呼這些數字：整數尾數寫「尾數」，整數部分包含寫「數字」。</param>
    private static void ValidateTrailingDigits(FilterConditionSpec rule, string label, List<string> errors, string noun = "尾數")
    {
        var patterns = rule.Keywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .ToList();

        if (patterns.Count == 0)
        {
            errors.Add($"{label}：尚未輸入{noun}");
            return;
        }

        foreach (var pattern in patterns)
        {
            if (pattern.Length < TrailingZeroThreshold.MinCustomDigits
                || pattern.Length > TrailingZeroThreshold.MaxCustomDigits
                || !pattern.All(character => character is >= '0' and <= '9'))
            {
                errors.Add($"{label}：{noun}「{pattern}」須為 "
                    + $"{TrailingZeroThreshold.MinCustomDigits} 到 {TrailingZeroThreshold.MaxCustomDigits} 位數字");
            }
        }
    }
}
