namespace JET.Domain;

/// <summary>
/// 進階篩選條件 AST（manifest「Filter / Criteria」章節）。
/// 前端 Query Builder 只組裝 JSON；後端解析成本型別後交由
/// Infrastructure 轉成參數化 SQL（guide §1.5.2 set-based pushdown）。
/// 規則間與群組間的結合律為左折疊累積括號：((c1 OP c2) OP c3)。
/// </summary>
public enum FilterJoin
{
    And,
    Or
}

public enum FilterRuleType
{
    Prescreen,
    Text,
    DateRange,
    NumRange,
    DrCrOnly,
    ManualAuto,
    AccountPair,
    SpecialAccountCategoryPair,
    CustomKeywords,
    CustomTrailingZeros,
    CustomPreparerEntryCount,
    CustomAccountEntryCount,
    RevenueDebitNearQuarterEnd,
    RevenueWithoutNormalCounterpart,
    ManualRevenueEntry,
    TrailingDigits,
    PreparerEqualsApprover,
    TextSet,

    /// <summary>
    /// typed dynamic rule（wire <c>type:"typed"</c>，2026-08-14 契約凍結）：目前案件 committed
    /// 的 RDE 欄位（text／date／money）依 closed operator menu 參與進階條件。fieldId 只作
    /// registry lookup 與參數綁定，永不成為 SQL identifier。
    /// </summary>
    TypedField,
    FieldValue,
    AccountSide,
    EntityFrequency,
    Group,
    Voucher
}

/// <summary>
/// 群組內規則的評估範圍。Row 維持既有同列左折疊；SameVoucher 以第一條規則
/// 錨定輸出列，後續規則只要求同一傳票在查核期間內另有符合列。
/// </summary>
public enum FilterGroupMatchScope
{
    Row,
    SameVoucher
}

/// <summary>textSet 輸入值的封閉正規化集合；資料欄本身永不改寫。</summary>
public enum TextSetNormalization
{
    Preserve,
    RemoveAsciiSpaces
}

/// <summary>
/// 進階篩選 wire／編譯的 provider-neutral 有界契約。每條文字集合先以可理解的 UI 上限
/// fail closed；實際 SQL plan 另以總參數預算作最後防線，避免合法 AST 在 provider 間因
/// command 大小差異產生不一致。
/// </summary>
public static class FilterScenarioLimits
{
    public const int MaxNestingDepth = 8;
    public const int MaxTextSetValuesPerRule = 100;

    /// <summary>
    /// 科目配對單側可多選的分類上限。專案 taxonomy 沒有筆數上限，故雙側各自先以可理解的
    /// UI 上限 fail closed；整個情境仍受 <see cref="MaxCompiledParameters"/> 的最後防線約束。
    /// </summary>
    public const int MaxCategoryIdsPerSide = 100;

    /// <summary>typed 條件 <c>in</c>／<c>notIn</c> 的 values 上限（凍結契約：1–100 個字串）。</summary>
    public const int MaxTypedInValuesPerRule = 100;

    public const int MaxCompiledParameters = 2_000;
}

/// <summary>科目配對分析的三模式（guide §6.1）。</summary>
public static class AccountPairModes
{
    public const string Exact = "exact";
    public const string DebitAnchor = "debitAnchor";
    public const string CreditAnchor = "creditAnchor";
}

/// <summary>
/// 考量特殊科目類別配對（specialAccountCategoryPair）的三模式。命名說「為何不只是配對」——
/// 每個模式同時表達借方類別 A／貸方類別 B 與「存在 / 不存在」的語意（含否定），
/// 與 AccountPairModes 的錨定語意刻意分離（不同的使用者面向條件，見 GlRulePredicates）。
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

public enum TextMatchMode
{
    Contains,
    Exact,
    NotContains,
    NotExact
}

/// <summary>
/// 單一條件規則。各 Type 只使用對應欄位：
/// Prescreen → PrescreenKey；Text → Field/Keywords/Mode；
/// DateRange → Field/FromDate/ToDate；NumRange → Field/FromAmountScaled/ToAmountScaled；
/// DrCrOnly → DrCr（"debit"|"credit"）；ManualAuto → IsManual；
/// AccountPair / SpecialAccountCategoryPair → PairMode + DebitCategoryIds/CreditCategoryIds
/// （wire 未帶陣列時回退 legacy scalar DebitCategory/CreditCategory）；
/// CustomKeywords → Keywords；CustomTrailingZeros → Digits；CustomPreparerEntryCount / CustomAccountEntryCount → MaxEntries。
/// KCT 小組條件（清單 A/C/D/H/J）：RevenueDebitNearQuarterEnd → WindowDays（季末視窗天數）；
/// TrailingDigits → Keywords（尾數樣態清單，重用同欄）；
/// RevenueWithoutNormalCounterpart / ManualRevenueEntry / PreparerEqualsApprover → 無參數。
/// UnknownJoin：wire 的 join 有值卻不是 AND/OR 時，解析層把原始值標記在這裡而不擲錯
/// （materialize 重新解析已存 JSON 不經驗證，解析層必須容忍），由 FilterScenarioValidator
/// 擋成 invalid_scenario——2026-07-06 收緊前未知 join 被靜默當 AND，曾掩護前端回歸。
/// </summary>
public sealed record FilterRuleSpec(
    FilterJoin Join,
    FilterRuleType Type,
    string? PrescreenKey,
    string? Field,
    IReadOnlyList<string> Keywords,
    TextMatchMode Mode,
    string? FromDate,
    string? ToDate,
    long? FromAmountScaled,
    long? ToAmountScaled,
    string? DrCr,
    bool? IsManual,
    string? PairMode = null,
    string? DebitCategory = null,
    string? CreditCategory = null,
    int? Digits = null,
    int? MaxEntries = null,
    int? WindowDays = null,
    string? UnknownJoin = null)
{
    public string? CountUnit { get; init; }
    public string? CountOperator { get; init; }
    public int? CountFrom { get; init; }
    public int? CountTo { get; init; }

    /// <summary>textSet 的結構化值；與既有逗號分隔 Keywords 分離。</summary>
    public IReadOnlyList<string> Values { get; init; } = [];

    public TextSetNormalization Normalization { get; init; } = TextSetNormalization.Preserve;

    /// <summary>wire 明示未知 normalization 時保留原值，交由 validator fail-loud。</summary>
    public string? UnknownNormalization { get; init; }

    /// <summary>
    /// 科目配對借方側的多選分類身分（`debitCategoryIds`）。空集合代表 wire 沒有帶陣列，
    /// 此時由 <see cref="EffectiveDebitCategoryIds"/> 回退到 legacy scalar 單選。
    /// </summary>
    public IReadOnlyList<string> DebitCategoryIds { get; init; } = [];

    /// <summary>科目配對貸方側的多選分類身分（`creditCategoryIds`）；語意同借方側。</summary>
    public IReadOnlyList<string> CreditCategoryIds { get; init; } = [];

    /// <summary>驗證與編譯共用的借方側權威分類集合（已去重、排序）。</summary>
    internal IReadOnlyList<string> EffectiveDebitCategoryIds =>
        AccountPairCategorySelection.Resolve(DebitCategoryIds, DebitCategory);

    /// <summary>驗證與編譯共用的貸方側權威分類集合（已去重、排序）。</summary>
    internal IReadOnlyList<string> EffectiveCreditCategoryIds =>
        AccountPairCategorySelection.Resolve(CreditCategoryIds, CreditCategory);

    /// <summary>typed 條件（type:"typed"）：committed RDE 欄位身分（`rde.&lt;32 lowercase hex&gt;`）。</summary>
    public string? FieldId { get; init; }

    /// <summary>typed 條件的 closed operator token（per-type 集合見 <see cref="TypedFieldOperatorSets"/>）。</summary>
    public string? TypedOperator { get; init; }

    /// <summary>typed single-value operand carrier `value` 的原始 wire 字串；null＝wire 沒帶。</summary>
    public string? TypedValue { get; init; }

    /// <summary>typed `between` 的 `from` 原始 wire 字串；null＝wire 沒帶。</summary>
    public string? TypedFrom { get; init; }

    /// <summary>typed `between` 的 `to` 原始 wire 字串；null＝wire 沒帶。</summary>
    public string? TypedTo { get; init; }

    /// <summary>typed `in`／`notIn` 的 `values`；null＝wire 沒帶陣列、空陣列由 validator fail-loud。</summary>
    public IReadOnlyList<string>? TypedValues { get; init; }

    /// <summary>typed money 條件必填的 `amountBasis` 原始 wire 字串（僅 money 允許）。</summary>
    public string? AmountBasis { get; init; }

    // New rules carry explicit blank handling; null on old rules preserves their original behavior.
    public bool? IncludeBlank { get; init; }
    public string? CategoryMode { get; init; }
    public IReadOnlyList<string> CategoryIds { get; init; } = [];
    public string? CategorySelection { get; init; }
    public IReadOnlyList<FilterRuleSpec> Rules { get; init; } = [];
    public string? Quantifier { get; init; }
    public string? Side { get; init; }

    public IEnumerable<FilterRuleSpec> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Rules)
            foreach (var descendant in child.DescendantsAndSelf()) yield return descendant;
    }

    public bool IsVoucherCondition => Type == FilterRuleType.Voucher
        || Type == FilterRuleType.AccountSide && CategoryMode == "absent"
        || Type == FilterRuleType.Group && Rules.Count > 0 && Rules.All(rule => rule.IsVoucherCondition);

    // FieldValue also uses DrCr when present: the value and side must match the same entry.
}

/// <summary>
/// 科目配對單側選擇的正規化（驗證、編譯與 legacy 相容的唯一收斂點）。
/// wire 帶 `debitCategoryIds`／`creditCategoryIds` 時以陣列為權威；沒有帶陣列的
/// legacy scalar 才投影為對應內建分類的單元素集合，無法辨識的 scalar 原樣保留，
/// 交由 validator 以「不存在於目前專案的科目分類」fail-loud（不得靜默成空集合，
/// 否定模式的 NOT EXISTS 會因空集合反轉成全命中）。
/// 去重後依 ordinal 排序，因此同一組分類的任何排列都編譯出逐字相同的 SQL。
/// </summary>
internal static class AccountPairCategorySelection
{
    internal static IReadOnlyList<string> Resolve(IReadOnlyList<string> categoryIds, string? legacyScalar)
    {
        if (categoryIds.Count > 0)
        {
            return Canonicalize(categoryIds);
        }

        var scalar = legacyScalar?.Trim();
        if (string.IsNullOrEmpty(scalar))
        {
            return [];
        }

        return AccountTaxonomyBuiltIns.TryResolveLegacyLabel(scalar, out var category)
            ? [category.CategoryId]
            : [scalar];
    }

    internal static IReadOnlyList<string> Canonicalize(IReadOnlyList<string> categoryIds) =>
        categoryIds
            .Select(static value => value?.Trim() ?? string.Empty)
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
}

/// <summary>UnknownJoin 的語意同 FilterRuleSpec：未知 join 的原始值標記，由驗證層 fail-loud。</summary>
public sealed record FilterGroupSpec(
    FilterJoin Join,
    IReadOnlyList<FilterRuleSpec> Rules,
    string? UnknownJoin = null)
{
    public FilterGroupMatchScope MatchScope { get; init; } = FilterGroupMatchScope.Row;

    /// <summary>wire 明示未知 matchScope 時保留原值，交由 validator fail-loud。</summary>
    public string? UnknownMatchScope { get; init; }
}

/// <summary>textSet 輸入正規化的單一事實來源，供 Domain 驗證與 AuditCore 編譯共用。</summary>
internal static class TextSetValueNormalizer
{
    public static string Normalize(string? value, TextSetNormalization normalization)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalization == TextSetNormalization.RemoveAsciiSpaces
            ? normalized.Replace(" ", string.Empty, StringComparison.Ordinal)
            : normalized;
    }
}

/// <summary>
/// 篩選情境來源標記（manifest「scenario.source」）：標明此情境是查核員手寫，
/// 還是來自固定方法論清單。空／null／未知值一律視為查核員手寫（原行為）。
/// 目前唯一具名來源為 KCT 小組方法論檢核清單。
/// </summary>
public static class FilterScenarioSources
{
    /// <summary>KCT 小組方法論檢核條件：固定清單，非查核員自擬，故豁免名稱/動機必填。</summary>
    public const string Kct = "kct";

    /// <summary>KCT 來源情境留痕用的預設動機（使用者不被要求填寫時的非空替補）。</summary>
    public const string KctDefaultRationale = "KCT 小組方法論檢核條件";

    /// <summary>KCT 來源情境名稱留空時的穩定替補前綴（儲存層 name NOT NULL）。</summary>
    public const string KctDefaultName = "KCT 小組方法論檢核條件";

    public static bool IsKct(string? source) =>
        string.Equals(source?.Trim(), Kct, StringComparison.Ordinal);

    /// <summary>
    /// 落地前的留痕替補：唯一收斂點。KCT 來源且名稱/動機留白時補上穩定非空值，
    /// 其餘來源原樣保留（替補只在 KCT 豁免必填的前提下才有意義）。
    /// 回傳將寫入 config_filter_scenario.name / .rationale 的有效值。
    /// </summary>
    public static (string Name, string Rationale) ResolvePersistable(
        FilterScenarioSpec scenario,
        int position)
    {
        if (!IsKct(scenario.Source))
        {
            return (scenario.Name, scenario.Rationale);
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(position, 1);
        var name = string.IsNullOrWhiteSpace(scenario.Name)
            ? $"{KctDefaultName}（第 {position} 項）"
            : scenario.Name;
        var rationale = string.IsNullOrWhiteSpace(scenario.Rationale)
            ? KctDefaultRationale
            : scenario.Rationale;
        return (name, rationale);
    }
}

public sealed record FilterScenarioSpec(
    string Name,
    string Rationale,
    IReadOnlyList<FilterGroupSpec> Groups,
    string? Source = null);

/// <summary>
/// 驗證所需的專案前置條件：期末財報準備日（postPeriodApproval 條件）、
/// 科目配對 presence（unexpectedAccountPair / accountPair 條件）、
/// 授權編製人員清單 presence（nonAuthorizedPreparer 條件）。
/// </summary>
public sealed record FilterValidationContext(
    bool HasLastPeriodStart,
    bool HasAccountMapping,
    bool HasAuthorizedPreparers,
    GlPopulationScope PopulationScope = GlPopulationScope.AuditPeriod,
    bool HasAnyAccountCategory = false,
    bool HasRevenueCategory = false,
    bool HasCounterpartCategory = false)
{
    public bool HasManualFlag { get; init; }

    public bool HasCreatedBy { get; init; }

    public bool HasApprovedBy { get; init; }

    public bool HasDescription { get; init; }

    /// <summary>正式查詢由已提交配對提供；null 只供沒有配對資料的獨立述詞驗證。</summary>
    public IReadOnlyList<string>? AvailableGlFields { get; init; }

    /// <summary>
    /// 目前專案 taxonomy 的全部分類身分，供科目配對多選驗證。預設只含五個內建 ID——
    /// 它們由 <see cref="AccountTaxonomyInvariant"/> 保證不可刪除，因此沒有帶 taxonomy 的
    /// 呼叫端仍會擋掉未知 custom ID，不會 fail open。
    /// </summary>
    public IReadOnlyList<string> TaxonomyCategoryIds { get; init; } =
        AccountTaxonomyBuiltIns.All.Select(static category => category.CategoryId).ToArray();

    /// <summary>
    /// 目前案件 committed 的 RDE 欄位（typed 條件的唯一 registry）。預設空集合＝任何 typed
    /// 條件都因欄位不存在而 invalid（fail closed），不會誤放行。
    /// </summary>
    public IReadOnlyList<GlRdeFieldMetadata> RdeFields { get; init; } = [];

    /// <summary>typed money operand 解析所需的專案 MoneyScale（沿 guide §1.5.3）。</summary>
    public int MoneyScale { get; init; } = 1;
}

/// <summary>
/// 條件 AST 的領域驗證：回傳所有錯誤訊息（空集合 = 合法）。
/// 識別字安全的第一道防線：Field / PrescreenKey 必須在白名單內。
/// </summary>
public static partial class FilterScenarioValidator
{
    public static IReadOnlyList<string> Validate(FilterScenarioSpec scenario, FilterValidationContext context,
        bool forSave = true)
    {
        var errors = new List<string>();

        // KCT 來源是固定方法論清單（非查核員自擬），不向使用者索取名稱/動機；
        // 留痕的非空替補在落地時由 FilterScenarioSources.ResolvePersistable 補上。
        var isKct = FilterScenarioSources.IsKct(scenario.Source);
        var requiresAuthoredMetadata = forSave && !isKct;

        if (requiresAuthoredMetadata && string.IsNullOrWhiteSpace(scenario.Name))
        {
            errors.Add("情境名稱必填。");
        }

        if (requiresAuthoredMetadata && string.IsNullOrWhiteSpace(scenario.Rationale))
        {
            errors.Add("篩選動機說明必填。");
        }

        if (scenario.Groups.Count == 0)
        {
            errors.Add("至少需要一個條件群組。");
            return errors;
        }

        for (var groupIndex = 0; groupIndex < scenario.Groups.Count; groupIndex++)
        {
            var group = scenario.Groups[groupIndex];
            var groupLabel = $"條件群組 {groupIndex + 1}";

            // 未知 join fail-loud（含左折疊時被忽略的第一個群組——亂值代表前端組裝有誤）。
            if (group.UnknownJoin is not null)
            {
                errors.Add($"{groupLabel}：join 結合方式「{group.UnknownJoin}」無效，允許值：AND、OR。");
            }

            if (group.UnknownMatchScope is not null)
            {
                errors.Add($"{groupLabel}：matchScope 無效，允許值：row、sameVoucher。");
            }

            if (group.Rules.Count == 0)
            {
                errors.Add($"{groupLabel} 沒有任何規則。");
                continue;
            }

            if (group.MatchScope == FilterGroupMatchScope.SameVoucher)
            {
                if (group.Rules.Count < 2)
                {
                    errors.Add($"{groupLabel}：sameVoucher 至少需要兩條規則（第一條為輸出錨點）。");
                }

                if (group.Rules.Any(static rule => rule.Join == FilterJoin.Or))
                {
                    errors.Add($"{groupLabel}：sameVoucher 群組內只允許 AND，不接受 OR。");
                }
            }

            for (var ruleIndex = 0; ruleIndex < group.Rules.Count; ruleIndex++)
            {
                ValidateRule(
                    group.Rules[ruleIndex],
                    $"{groupLabel} 規則 {ruleIndex + 1}",
                    context,
                    isKct,
                    errors);
            }
        }

        return errors;
    }

    private static void ValidateCategorySelectionMode(FilterRuleSpec rule, string label, List<string> errors)
    {
        if (rule.CategorySelection is not (null or "role" or "node" or "subtree"))
            errors.Add($"{label}：請選審計角色、分類本身或分類及下層。");
    }

    private static void ValidateRule(
        FilterRuleSpec rule,
        string label,
        FilterValidationContext context,
        bool isKct,
        List<string> errors,
        int depth = 0,
        bool insideVoucher = false)
    {
        if (depth > FilterScenarioLimits.MaxNestingDepth)
        {
            errors.Add($"{label}：條件最多可巢狀八層，請減少括號層數。");
            return;
        }
        // 未知 join fail-loud（含左折疊時被忽略的第一條規則），與型別專屬錯誤合併列出。
        if (rule.UnknownJoin is not null)
        {
            errors.Add($"{label}：join 結合方式「{rule.UnknownJoin}」無效，允許值：AND、OR。");
        }

        switch (rule.Type)
        {
            case FilterRuleType.Group:
            case FilterRuleType.Voucher:
                if (rule.Rules.Count == 0) errors.Add($"{label}：請加入至少一條子條件。");
                if (rule.Type == FilterRuleType.Voucher)
                {
                    if (insideVoucher) errors.Add($"{label}：傳票內的條件請設定在同一筆分錄，勿再次加入傳票量詞。");
                    if (rule.Side is not ("all" or "debit" or "credit")) errors.Add($"{label}：請選整張傳票、借方或貸方。");
                    if (rule.Quantifier is not ("any" or "all" or "none")) errors.Add($"{label}：請選至少一筆、全部符合或不存在符合。");
                }
                for (var i = 0; i < rule.Rules.Count; i++)
                    ValidateRule(rule.Rules[i], $"{label} 子條件 {i + 1}", context, isKct, errors,
                        depth + 1, insideVoucher || rule.Type == FilterRuleType.Voucher);
                break;
            case FilterRuleType.FieldValue:
                ValidateFieldValue(rule, label, context, errors);
                break;
            case FilterRuleType.AccountSide:
                ValidateCategorySelectionMode(rule, label, errors);
                if (rule.DrCr is not ("debit" or "credit"))
                    errors.Add($"{label}：請選擇借方或貸方。");
                if (rule.CategoryMode is not ("is" or "isNot" or "absent"))
                    errors.Add($"{label}：請選擇科目分類的判斷方式。");
                if (!context.HasAnyAccountCategory)
                    errors.Add($"{label}：借貸科目分類條件需要先在「資料驗證與測試」匯入科目配對。");
                ValidateCategorySelection(rule.CategoryIds, "指定", label, context, errors);
                break;
            case FilterRuleType.Prescreen:
                ValidatePrescreen(rule, label, context, isKct, errors);
                break;
            case FilterRuleType.Text:
                ValidateText(rule, label, errors);
                if (isKct
                    && string.Equals(rule.Field, JetFieldCatalog.GlCreateBy, StringComparison.Ordinal)
                    && rule.Mode == TextMatchMode.Exact)
                {
                    RequireMappedField(
                        context.HasCreatedBy,
                        JetFieldCatalog.GlCreateBy,
                        label,
                        errors);
                }
                break;
            case FilterRuleType.TextSet:
                ValidateTextSet(rule, label, errors);
                break;
            case FilterRuleType.DateRange:
                ValidateDateRange(rule, label, errors);
                break;
            case FilterRuleType.NumRange:
                ValidateNumRange(rule, label, errors);
                break;
            case FilterRuleType.DrCrOnly:
                if (rule.DrCr is not ("debit" or "credit"))
                {
                    errors.Add($"{label}：借貸限定必須是 debit 或 credit。");
                }
                break;
            case FilterRuleType.ManualAuto:
                if (rule.IsManual is null)
                {
                    errors.Add($"{label}：人工/自動條件需指定 isManual。");
                }
                break;
            case FilterRuleType.AccountPair:
                ValidateCategorySelectionMode(rule, label, errors);
                ValidateAccountPair(rule, label, context, errors);
                break;
            case FilterRuleType.SpecialAccountCategoryPair:
                ValidateCategorySelectionMode(rule, label, errors);
                ValidateSpecialAccountCategoryPair(rule, label, context, errors);
                break;
            case FilterRuleType.CustomKeywords:
                if (rule.Keywords.All(string.IsNullOrWhiteSpace))
                {
                    errors.Add($"{label}：自訂關鍵字條件至少需要一個關鍵字。");
                }
                break;
            case FilterRuleType.CustomTrailingZeros:
                if (rule.Digits is not (>= TrailingZeroThreshold.MinCustomDigits
                    and <= TrailingZeroThreshold.MaxCustomDigits))
                {
                    errors.Add($"{label}：自訂尾數位數必須是 {TrailingZeroThreshold.MinCustomDigits}–"
                        + $"{TrailingZeroThreshold.MaxCustomDigits} 的整數。");
                }
                break;
            case FilterRuleType.CustomPreparerEntryCount:
                if (rule.MaxEntries is not (>= 1))
                {
                    errors.Add($"{label}：自訂編製人員分錄筆數門檻必須是 ≥ 1 的整數。");
                }
                break;
            case FilterRuleType.CustomAccountEntryCount:
                if (rule.MaxEntries is not (>= 1))
                {
                    errors.Add($"{label}：自訂科目分錄筆數門檻必須是 ≥ 1 的整數。");
                }
                break;
            case FilterRuleType.EntityFrequency:
                EntityFrequencyConditions.Validate(rule, context, label, errors);
                break;
            case FilterRuleType.RevenueDebitNearQuarterEnd:
                if (!context.HasRevenueCategory)
                {
                    errors.Add($"{label}：季末前借記收入需要科目配對 target 含 Revenue 分類。");
                }

                if (rule.WindowDays is not (>= QuarterEndWindows.MinWindowDays
                    and <= QuarterEndWindows.MaxWindowDays))
                {
                    errors.Add($"{label}：季末前天數須為 {QuarterEndWindows.MinWindowDays}–"
                        + $"{QuarterEndWindows.MaxWindowDays} 的整數。");
                }
                break;
            case FilterRuleType.RevenueWithoutNormalCounterpart:
                if (!context.HasRevenueCategory || !context.HasCounterpartCategory)
                {
                    errors.Add($"{label}：收入無一般對方科目需要科目配對 target 同時含 Revenue，"
                        + "以及 Receivables、Cash、Receipt in advance 至少一類。");
                }
                break;
            case FilterRuleType.ManualRevenueEntry:
                if (!context.HasRevenueCategory)
                {
                    errors.Add($"{label}：收入之人工分錄需要科目配對 target 含 Revenue 分類。");
                }
                RequireMappedField(
                    context.HasManualFlag,
                    JetFieldCatalog.GlManual,
                    label,
                    errors);
                break;
            case FilterRuleType.TrailingDigits:
                ValidateTrailingDigits(rule, label, errors);
                break;
            case FilterRuleType.PreparerEqualsApprover:
                RequireMappedField(
                    context.HasCreatedBy,
                    JetFieldCatalog.GlCreateBy,
                    label,
                    errors);
                RequireMappedField(
                    context.HasApprovedBy,
                    JetFieldCatalog.GlApproveBy,
                    label,
                    errors);
                break;
            case FilterRuleType.TypedField:
                ValidateTypedField(rule, label, context, errors);
                break;
            default:
                errors.Add($"{label}：不支援的規則型別。");
                break;
        }
    }

    /// <summary>
    /// typed 條件（2026-08-14 凍結）的完整驗證：fieldId 必須是目前案件 committed 的 RDE 欄位、
    /// operator 必須屬於該欄位型別的 closed 集合、operand carrier 依 operator 種類精確匹配
    /// （single→value；between→from＋to 且 from≤to；in/notIn→values 1–100；blank 家族不得帶
    /// 任何 carrier）、money 必填正準 amountBasis 且僅 money 允許。所有錯誤指名 fieldId，
    /// 符合 RDE lifecycle「invalid_scenario 並指名 field」的凍結裁決。
    /// </summary>
    private static void ValidateTypedField(
        FilterRuleSpec rule,
        string label,
        FilterValidationContext context,
        List<string> errors)
    {
        if (string.IsNullOrEmpty(rule.FieldId))
        {
            errors.Add($"{label}：typed 條件必須指定 fieldId。");
            return;
        }

        var field = context.RdeFields.FirstOrDefault(candidate =>
            string.Equals(candidate.FieldId, rule.FieldId, StringComparison.Ordinal));
        if (field is null)
        {
            errors.Add($"{label}：RDE 欄位「{rule.FieldId}」不存在於目前案件已提交的欄位配對。");
            return;
        }

        var allowedOperators = TypedFieldOperatorSets.ForValueType(field.ValueType);
        if (rule.TypedOperator is null)
        {
            errors.Add($"{label}：typed 條件必須指定 operator。");
            return;
        }

        if (!allowedOperators.Contains(rule.TypedOperator, StringComparer.Ordinal))
        {
            errors.Add(TypedFieldOperatorSets.All.Contains(rule.TypedOperator)
                ? $"{label}：operator「{rule.TypedOperator}」與 RDE 欄位「{field.Label}」"
                    + $"（{field.FieldId}）的型別 {field.ValueType} 不相容。"
                : $"{label}：不支援的 typed operator「{rule.TypedOperator}」。");
            return;
        }

        if (field.ValueType == RdeFieldValueTypeNames.Money)
        {
            if (!TypedAmountBasisNames.IsCanonical(rule.AmountBasis))
            {
                errors.Add($"{label}：RDE 欄位「{field.FieldId}」的 money 條件必須明示 "
                    + "amountBasis（signed 或 absolute）。");
            }
        }
        else if (rule.AmountBasis is not null)
        {
            errors.Add($"{label}：amountBasis 僅 money 型別的 RDE 欄位允許。");
        }

        ValidateTypedOperands(rule, field, label, context, errors);
    }

    private static void ValidateTypedOperands(
        FilterRuleSpec rule,
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
                errors.Add($"{label}：operator「{op}」不得帶任何 operand（value、from、to、values）。");
            }

            return;
        }

        if (TypedFieldOperatorSets.IsBetweenOperator(op))
        {
            if (hasValue || hasValues)
            {
                errors.Add($"{label}：operator「between」只接受 from 與 to，不得帶 value 或 values。");
            }

            if (!hasFrom || !hasTo)
            {
                errors.Add($"{label}：operator「between」必須同時提供 from 與 to。");
                return;
            }

            ValidateTypedBetweenBounds(rule, field, label, context, errors);
            return;
        }

        if (TypedFieldOperatorSets.IsSetOperator(op))
        {
            if (hasValue || hasFrom || hasTo)
            {
                errors.Add($"{label}：operator「{op}」只接受 values，不得帶 value、from 或 to。");
            }

            if (!hasValues)
            {
                errors.Add($"{label}：operator「{op}」必須提供 values（1–"
                    + $"{FilterScenarioLimits.MaxTypedInValuesPerRule} 個字串）。");
                return;
            }

            var values = rule.TypedValues!;
            if (values.Count is 0 or > FilterScenarioLimits.MaxTypedInValuesPerRule)
            {
                errors.Add($"{label}：values 必須是 1–"
                    + $"{FilterScenarioLimits.MaxTypedInValuesPerRule} 個字串的陣列。");
                return;
            }

            for (var index = 0; index < values.Count; index++)
            {
                ValidateTypedOperandValue(
                    values[index], field, $"{label}：values 第 {index + 1} 個值", context, errors);
            }

            return;
        }

        // single-value operators
        if (hasFrom || hasTo || hasValues)
        {
            errors.Add($"{label}：operator「{op}」只接受 value，不得帶 from、to 或 values。");
        }

        if (!hasValue)
        {
            errors.Add($"{label}：operator「{op}」必須提供 value。");
            return;
        }

        ValidateTypedOperandValue(rule.TypedValue, field, label, context, errors);
    }

    private static void ValidateTypedBetweenBounds(
        FilterRuleSpec rule,
        GlRdeFieldMetadata field,
        string label,
        FilterValidationContext context,
        List<string> errors)
    {
        switch (field.ValueType)
        {
            case RdeFieldValueTypeNames.Date:
            {
                var fromValid = TypedFieldOperandRules.TryNormalizeDate(rule.TypedFrom, out var fromIso);
                var toValid = TypedFieldOperandRules.TryNormalizeDate(rule.TypedTo, out var toIso);
                if (!fromValid)
                {
                    errors.Add($"{label}：日期「{rule.TypedFrom}」格式須為 yyyy-MM-dd。");
                }

                if (!toValid)
                {
                    errors.Add($"{label}：日期「{rule.TypedTo}」格式須為 yyyy-MM-dd。");
                }

                if (fromValid && toValid && string.CompareOrdinal(fromIso, toIso) > 0)
                {
                    errors.Add($"{label}：between 的 from 不得晚於 to。");
                }

                break;
            }

            case RdeFieldValueTypeNames.Money:
            {
                var fromValid = TypedFieldOperandRules.TryNormalizeMoney(
                    rule.TypedFrom, context.MoneyScale, out var fromScaled);
                var toValid = TypedFieldOperandRules.TryNormalizeMoney(
                    rule.TypedTo, context.MoneyScale, out var toScaled);
                if (!fromValid)
                {
                    errors.Add($"{label}：金額「{rule.TypedFrom}」格式無效。");
                }

                if (!toValid)
                {
                    errors.Add($"{label}：金額「{rule.TypedTo}」格式無效。");
                }

                if (fromValid && toValid && fromScaled > toScaled)
                {
                    errors.Add($"{label}：between 的 from 不得大於 to。");
                }

                break;
            }

            default:
                // text 沒有 between operator；per-type 集合已於 operator 檢查擋下。
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
                    errors.Add($"{label}：文字值 trim 後不可為空。");
                }

                break;
            case RdeFieldValueTypeNames.Date:
                if (!TypedFieldOperandRules.TryNormalizeDate(raw, out _))
                {
                    errors.Add($"{label}：日期「{raw}」格式須為 yyyy-MM-dd。");
                }

                break;
            case RdeFieldValueTypeNames.Money:
                if (!TypedFieldOperandRules.TryNormalizeMoney(raw, context.MoneyScale, out _))
                {
                    errors.Add($"{label}：金額「{raw}」格式無效。");
                }

                break;
        }
    }

    /// <summary>
    /// 科目配對分析（guide §6.1）：需科目配對已匯入；模式決定必填分類——
    /// exact 需借貸雙方、debitAnchor 只需借方、creditAnchor 只需貸方。
    /// 每一側都是專案 taxonomy 分類身分的多選集合，legacy scalar 視為單元素集合。
    /// </summary>
    private static void ValidateAccountPair(FilterRuleSpec rule, string label, FilterValidationContext context, List<string> errors)
    {
        if (!context.HasAnyAccountCategory)
        {
            errors.Add($"{label}：科目配對分析需要科目配對 target 至少一筆非空白分類。");
        }

        var needDebit = rule.PairMode is AccountPairModes.Exact or AccountPairModes.DebitAnchor;
        var needCredit = rule.PairMode is AccountPairModes.Exact or AccountPairModes.CreditAnchor;

        if (rule.PairMode is not (AccountPairModes.Exact or AccountPairModes.DebitAnchor or AccountPairModes.CreditAnchor))
        {
            errors.Add($"{label}：配對模式「{rule.PairMode}」無效，允許值：exact、debitAnchor、creditAnchor。");
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
    /// 單側多選分類的共同驗證：至少一個、不超過上限、每個身分都存在於目前專案 taxonomy。
    /// 空集合必須擋在這裡——否定模式的 NOT EXISTS 遇到空集合會反轉成全命中（同非授權編製人員的空名單閘控）。
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
            errors.Add($"{label}：{sideLabel}分類至少需選擇一項。");
            return;
        }

        if (categoryIds.Count > FilterScenarioLimits.MaxCategoryIdsPerSide)
        {
            errors.Add(
                $"{label}：{sideLabel}分類最多 {FilterScenarioLimits.MaxCategoryIdsPerSide} 項。");
            return;
        }

        foreach (var categoryId in categoryIds)
        {
            if (!context.TaxonomyCategoryIds.Contains(categoryId, StringComparer.Ordinal))
            {
                errors.Add($"{label}：{sideLabel}分類「{categoryId}」不存在於目前專案的科目分類。");
            }
        }
    }

    /// <summary>
    /// 考量特殊科目類別配對：顯式雙類別 + 否定。AccountPair 的姊妹條件，三模式
    /// （drAndCr / drNotCr / notDrCr）皆需科目配對已匯入、且借方類別 A 與貸方類別 B
    /// 兩者皆在白名單內（否定模式同樣需要 B 或 A 才能判定「不存在」，故雙方一律必填）。
    /// </summary>
    private static void ValidateSpecialAccountCategoryPair(FilterRuleSpec rule, string label, FilterValidationContext context, List<string> errors)
    {
        if (!context.HasAnyAccountCategory)
        {
            errors.Add($"{label}：特殊科目類別配對需要科目配對 target 至少一筆非空白分類。");
        }

        if (rule.PairMode is not (SpecialAccountCategoryPairModes.DrAndCr
            or SpecialAccountCategoryPairModes.DrNotCr
            or SpecialAccountCategoryPairModes.NotDrCr))
        {
            errors.Add($"{label}：配對模式「{rule.PairMode}」無效，允許值：drAndCr、drNotCr、notDrCr。");
            return;
        }

        ValidateCategorySelection(rule.EffectiveDebitCategoryIds, "借方", label, context, errors);
        ValidateCategorySelection(rule.EffectiveCreditCategoryIds, "貸方", label, context, errors);
    }

    private static void ValidatePrescreen(
        FilterRuleSpec rule,
        string label,
        FilterValidationContext context,
        bool isKct,
        List<string> errors)
    {
        if (rule.PrescreenKey is null || !PrescreenRuleKeys.FilterableKeys.Contains(rule.PrescreenKey))
        {
            errors.Add($"{label}：預篩選鍵「{rule.PrescreenKey}」不可作為列述詞（彙總規則或未知鍵）。");
            return;
        }

        if (rule.PrescreenKey == PrescreenRuleKeys.PostPeriodApproval && !context.HasLastPeriodStart)
        {
            errors.Add($"{label}：財報準備日起核准條件需要專案設定期末財報準備日（lastPeriodStart）。");
        }

        if (rule.PrescreenKey == PrescreenRuleKeys.UnexpectedAccountPair && !context.HasAccountMapping)
        {
            errors.Add($"{label}：未預期借貸組合條件需先匯入科目配對。");
        }

        // 非授權編製人員閘控（鏡射 unexpectedAccountPair）：授權編製人員清單未匯入時，
        // 空名單會讓 NOT IN 述詞反轉成全命中，故在驗證層直接擋下。
        if (rule.PrescreenKey == PrescreenRuleKeys.NonAuthorizedPreparer && !context.HasAuthorizedPreparers)
        {
            errors.Add($"{label}：非授權編製人員條件需先匯入授權編製人員清單。");
        }

        // 與預篩選的選用來源一致；只指出本條件缺欄，不限制其他可計算的情境。
        // null 僅供沒有案件配對資訊的獨立述詞測試；正式 action 一律提供完整欄位清單。
        if (context.AvailableGlFields is { } available)
        {
            var required = rule.PrescreenKey switch
            {
                PrescreenRuleKeys.BackdatedPosting => JetFieldCatalog.GlVoucherDate,
                PrescreenRuleKeys.LowFrequencyPreparer or PrescreenRuleKeys.NonAuthorizedPreparer => JetFieldCatalog.GlCreateBy,
                _ => null
            };
            if (required is not null)
                RequireMappedField(available.Contains(required, StringComparer.Ordinal), required, label, errors);
        }

        if (isKct && rule.PrescreenKey == PrescreenRuleKeys.BlankDescription)
        {
            RequireMappedField(
                context.HasDescription,
                JetFieldCatalog.GlDescription,
                label,
                errors);
        }
    }

    private static void RequireMappedField(
        bool isAvailable,
        string semanticIdentity,
        string label,
        List<string> errors)
    {
        if (isAvailable)
        {
            return;
        }

        var fieldLabel = JetFieldCatalog.GlSemanticFieldMappingLabel(semanticIdentity);
        errors.Add($"{label}：缺少前置資料：GL 欄位「{fieldLabel}」尚未配對。");
    }

    private static void ValidateText(FilterRuleSpec rule, string label, List<string> errors)
    {
        if (!GlFieldWhitelist.TryResolve(rule.Field, out var column) || column.Kind != GlFieldKind.Text)
        {
            errors.Add($"{label}：文字條件的欄位「{rule.Field}」不在可篩選欄位白名單。");
        }

        if (rule.Keywords.All(string.IsNullOrWhiteSpace))
        {
            errors.Add($"{label}：文字條件至少需要一個關鍵字。");
        }
    }

    private static void ValidateTextSet(FilterRuleSpec rule, string label, List<string> errors)
    {
        if (!GlFieldWhitelist.TryResolve(rule.Field, out var column) || column.Kind != GlFieldKind.Text)
        {
            errors.Add($"{label}：文字值集合的欄位「{rule.Field}」不在可篩選文字欄位白名單。");
        }

        if (rule.Mode is not (TextMatchMode.Contains or TextMatchMode.Exact))
        {
            errors.Add($"{label}：文字值集合只允許 contains 或 exact。");
        }

        if (rule.UnknownNormalization is not null)
        {
            errors.Add($"{label}：normalization 無效，允許值：preserve、removeAsciiSpaces。");
        }

        if (rule.Values.Count == 0)
        {
            errors.Add($"{label}：文字值集合至少需要一個值。");
            return;
        }

        if (rule.Values.Count > FilterScenarioLimits.MaxTextSetValuesPerRule)
        {
            errors.Add(
                $"{label}：文字值集合最多 {FilterScenarioLimits.MaxTextSetValuesPerRule} 個值。");
            return;
        }

        for (var index = 0; index < rule.Values.Count; index++)
        {
            if (string.IsNullOrEmpty(TextSetValueNormalizer.Normalize(rule.Values[index], rule.Normalization)))
            {
                errors.Add($"{label}：第 {index + 1} 個文字值正規化後不可為空。");
            }
        }
    }

    private static void ValidateDateRange(FilterRuleSpec rule, string label, List<string> errors)
    {
        if (!GlFieldWhitelist.TryResolve(rule.Field, out var column) || column.Kind != GlFieldKind.Date)
        {
            errors.Add($"{label}：日期條件的欄位「{rule.Field}」不是日期欄位。");
        }

        if (rule.FromDate is null && rule.ToDate is null)
        {
            errors.Add($"{label}：日期區間至少需填一個邊界。");
            return;
        }

        foreach (var bound in new[] { rule.FromDate, rule.ToDate })
        {
            if (bound is not null && !DateOnly.TryParseExact(bound, "yyyy-MM-dd", out _))
            {
                errors.Add($"{label}：日期「{bound}」格式須為 yyyy-MM-dd。");
            }
        }
    }

    private static void ValidateNumRange(FilterRuleSpec rule, string label, List<string> errors)
    {
        if (!GlFieldWhitelist.TryResolve(rule.Field, out var column) || column.Kind != GlFieldKind.Amount)
        {
            errors.Add($"{label}：數值條件的欄位「{rule.Field}」不是金額欄位。");
        }

        if (rule.FromAmountScaled is null && rule.ToAmountScaled is null)
        {
            errors.Add($"{label}：數值區間至少需填一個邊界。");
        }
    }

    /// <summary>
    /// 特定金額尾數（KCT 清單 H，filter type trailingDigits）：尾數樣態重用 Keywords，
    /// 每組須為 1–12 位純數字（位數上限沿用 trailing zeros 的 long 溢位防線）。
    /// </summary>
    private static void ValidateTrailingDigits(FilterRuleSpec rule, string label, List<string> errors)
    {
        var patterns = rule.Keywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .ToList();

        if (patterns.Count == 0)
        {
            errors.Add($"{label}：特定金額尾數至少需要一組尾數樣態。");
            return;
        }

        foreach (var pattern in patterns)
        {
            if (pattern.Length < TrailingZeroThreshold.MinCustomDigits
                || pattern.Length > TrailingZeroThreshold.MaxCustomDigits
                || !pattern.All(character => character is >= '0' and <= '9'))
            {
                errors.Add($"{label}：尾數樣態「{pattern}」須為 "
                    + $"{TrailingZeroThreshold.MinCustomDigits}–{TrailingZeroThreshold.MaxCustomDigits} 位純數字。");
            }
        }
    }
}
