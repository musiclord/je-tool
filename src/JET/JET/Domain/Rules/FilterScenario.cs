using System.Globalization;

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
    Text,
    DateRange,
    NumRange,
    DrCrOnly,
    ManualAuto,
    AccountPair,
    SpecialAccountCategoryPair,
    RevenueDebitNearQuarterEnd,
    RevenueWithoutNormalCounterpart,
    ManualRevenueEntry,
    PreparerEqualsApprover,
    TextSet,

    /// <summary>
    /// typed dynamic rule（wire <c>type:"typed"</c>，2026-08-14 契約凍結）：目前案件 committed
    /// 的 RDE 欄位（text／date／amount）依 closed operator menu 參與進階條件。fieldId 只作
    /// registry lookup 與參數綁定，永不成為 SQL identifier。
    /// </summary>
    TypedField,
    FieldValue,
    AccountSide,
    EntityFrequency,
    Group,
    Voucher
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
    /// 科目配對單側可多選的分類上限。專案的科目分類沒有筆數上限，故雙側各自先以可理解的
    /// UI 上限 fail closed；整個情境仍受 <see cref="MaxCompiledParameters"/> 的最後防線約束。
    /// </summary>
    public const int MaxCategoryIdsPerSide = 100;

    /// <summary>typed 條件 <c>in</c>／<c>notIn</c> 的 values 上限（凍結契約：1–100 個字串）。</summary>
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
    /// 情境名稱上限，和 SQL Server 的 <c>filter_scenario.name NVARCHAR(400)</c> 同寬；三種資料庫共用，儲存時超過就回錯誤，
    /// 不讓 SQL Server 默默截斷（裁定 2026-10-19-26）。前端 ui-core.js 的
    /// FILTER_MAX_SCENARIO_NAME_LENGTH 是輸入框的鏡像，由架構測試比對。
    /// </summary>
    public const int MaxNameLength = TextStorageLimits.TextUtf16CodeUnits;
}

/// <summary>科目配對分析的三模式（見 docs/jet-guide.md 第 6 節「進階條件篩選」）。</summary>
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
/// AccountPair / SpecialAccountCategoryPair → PairMode + DebitCategoryIds/CreditCategoryIds；
/// 常用篩選條件專用的型別：RevenueDebitNearQuarterEnd → WindowDays（季末視窗天數）；
/// RevenueWithoutNormalCounterpart / ManualRevenueEntry / PreparerEqualsApprover → 無參數。
/// UnknownJoin：wire 的 join 有值卻不是 AND/OR 時，解析層把原始值標記在這裡而不擲錯
/// （materialize 重新解析已存 JSON 不經驗證，解析層必須容忍），由 FilterScenarioValidator
/// 擋成 invalid_scenario——2026-07-06 收緊前未知 join 被靜默當 AND，曾掩護前端回歸。
/// </summary>
public sealed record FilterConditionSpec(
    FilterJoin Join,
    FilterConditionType Type,
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
    /// 科目配對借方側的多選分類身分（`debitCategoryIds`）。空集合代表 wire 沒有帶陣列。
    /// </summary>
    public IReadOnlyList<string> DebitCategoryIds { get; init; } = [];

    /// <summary>科目配對貸方側的多選分類身分（`creditCategoryIds`）；語意同借方側。</summary>
    public IReadOnlyList<string> CreditCategoryIds { get; init; } = [];

    /// <summary>驗證與編譯共用的借方側權威分類集合（已去重、排序）。</summary>
    internal IReadOnlyList<string> EffectiveDebitCategoryIds =>
        AccountPairCategorySelection.Canonicalize(DebitCategoryIds);

    /// <summary>驗證與編譯共用的貸方側權威分類集合（已去重、排序）。</summary>
    internal IReadOnlyList<string> EffectiveCreditCategoryIds =>
        AccountPairCategorySelection.Canonicalize(CreditCategoryIds);

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

    /// <summary>typed amount 條件必填的 `amountBasis` 原始 wire 字串（僅 amount 允許）。</summary>
    public string? AmountBasis { get; init; }

    // New rules carry explicit blank handling; null on old rules preserves their original behavior.
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
/// 篩選情境來源標記（wire 欄位 scenario.source）：標明此情境是查核員手寫，
/// 還是用了「常用篩選條件」的卡片。空／null／未知值一律視為查核員手寫（原行為）；
/// 2026-10-29 以前的案件記的是另一個來源代碼，這一版不認得，也當成查核員手寫。
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

    /// <summary>原因裡的分類名稱；沒有帶分類設定的呼叫端用內建名稱。</summary>
    public AccountCategoryLabels CategoryLabels { get; init; } = AccountCategoryLabels.BuiltIn;

    /// <summary>交給規則需求表的案件事實。第五步照裁定 2026-10-04-01 不檢查假日檔。</summary>
    internal JetRuleFacts RuleFacts => new(
        AvailableGlFields,
        HasPrepStartDate,
        HasAccountMapping,
        HasAnyAccountCategory,
        HasRevenueCategory,
        HasCounterpartCategory,
        HasAuthorizedPreparers,
        HasHolidayCalendar: null,
        CategoryLabels);

    /// <summary>
    /// 目前專案科目分類的全部分類身分，供科目配對多選驗證。預設只含五個內建 ID——
    /// 它們由 <see cref="AccountCategoryInvariant"/> 保證不可刪除，因此沒有帶科目分類的
    /// 呼叫端仍會擋掉未知 custom ID，不會 fail open。
    /// </summary>
    public IReadOnlyList<string> CategoryIds { get; init; } =
        AccountCategoryBuiltIns.All.Select(static category => category.CategoryId).ToArray();

    /// <summary>
    /// 目前案件 committed 的 RDE 欄位（typed 條件的唯一 registry）。預設空集合＝任何 typed
    /// 條件都因欄位不存在而 invalid（fail closed），不會誤放行。
    /// </summary>
    public IReadOnlyList<GlRdeFieldMetadata> RdeFields { get; init; } = [];

    /// <summary>typed amount operand 解析所需的專案 MoneyScale。</summary>
    public int MoneyScale { get; init; } = 1;

    public DateParseOptions DateParseOptions { get; init; } = DateParseOptions.Default;
}

/// <summary>
/// 條件 AST 的領域驗證：回傳所有錯誤訊息（空集合 = 合法）。
/// 識別字安全的第一道防線：Field / PrescreenKey 必須在白名單內。
/// </summary>
public static partial class FilterScenarioValidator
{
    // 已移除的預篩選鍵，只用來辨認舊情境並給出刪除條件的說明；不再是可用條件。
    private const string RetiredBackdatedPostingKey = "backdatedPosting";

    public static IReadOnlyList<string> Validate(FilterScenarioSpec scenario, FilterValidationContext context,
        bool forSave = true)
    {
        var errors = new FilterValidationErrors();

        // 裁定 2026-10-29-16：情境名稱與篩選動機不再必填，留白時儲存前已依條件補上自動文字。
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

        // 條件需要的案件資料只看規則需求表，第四步與前端停用說明讀同一張表（第五步不能用 GL 沒有配對的欄位，裁定 2026-10-22-05）。
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
                    // 借貸範圍是已拿掉的舊寫法，讀到時當成無法讀取的內容（裁定 2026-10-30-25：已儲存情境的舊寫法不再讀回）。
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
                // 借貸方向與「這一側都不屬於」是已拿掉的舊寫法，讀到時當成無法讀取的內容（裁定 2026-10-30-25）。
                if (rule.DrCr is not null)
                    FilterScenarioCorruption.Add(errors, label, "account_side_drcr_invalid");
                if (rule.CategoryMode is not ("is" or "isNot"))
                    FilterScenarioCorruption.Add(errors, label, "account_side_category_mode_invalid");
                ValidateCategorySelection(rule.CategoryIds, "指定", label, context, errors);
                break;
            case FilterConditionType.Prescreen:
                ValidatePrescreen(rule, label, errors);
                break;
            case FilterConditionType.Text:
                ValidateText(rule, label, errors);
                break;
            case FilterConditionType.TextSet:
                ValidateTextSet(rule, label, errors);
                break;
            case FilterConditionType.DateRange:
                ValidateDateRange(rule, label, errors);
                break;
            case FilterConditionType.NumRange:
                ValidateNumRange(rule, label, errors);
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
                // 需要的分類與欄位都在規則需求表；缺收入科目與缺對方科目各寫一句（用字依裁定 2026-10-13-28）。
                break;
            case FilterConditionType.TypedField:
                ValidateTypedField(rule, label, context, errors);
                break;
            default:
                FilterScenarioCorruption.Add(errors, label, "condition_type_unsupported");
                break;
        }
    }

    /// <summary>
    /// typed 條件（2026-08-14 凍結）的完整驗證：fieldId 必須是目前案件 committed 的 RDE 欄位、
    /// operator 必須屬於該欄位型別的 closed 集合、operand carrier 依 operator 種類精確匹配
    /// （single→value；between→from＋to 且 from≤to；in/notIn→values 1–100；blank 家族不得帶
    /// 任何 carrier）、amount 必填正準 amountBasis 且僅 amount 允許。錯誤一律 invalid_scenario；
    /// 欄位仍在配對中時以顯示名稱指名，已移除時拿不到名稱，只說明欄位已不在配對中並寫出下一步，
    /// 不把 rde. 內部代號顯示給審計員。
    /// </summary>
    private static void ValidateTypedField(
        FilterConditionSpec rule,
        string label,
        FilterValidationContext context,
        List<string> errors)
    {
        if (string.IsNullOrEmpty(rule.FieldId))
        {
            errors.Add($"{label}：尚未選擇攸關資料元素欄位");
            return;
        }

        var field = context.RdeFields.FirstOrDefault(candidate =>
            string.Equals(candidate.FieldId, rule.FieldId, StringComparison.Ordinal));
        if (field is null)
        {
            // 回第三步重新勾選會產生新的欄位身分（MappingHandlers 只沿用目前已提交的身分），這個條件仍然對不上；
            // 所以下一步寫在第五步這個條件上重新選擇欄位，不建議回第三步勾選。
            errors.Add($"{label}：所用的攸關資料元素欄位已不在欄位配對中，請重新選擇欄位或刪除此條件");
            return;
        }

        var allowedOperators = TypedFieldOperatorSets.ForValueType(field.ValueType);
        if (rule.TypedOperator is null)
        {
            errors.Add($"{label}：尚未選擇比較方式");
            return;
        }

        if (!allowedOperators.Contains(rule.TypedOperator, StringComparer.Ordinal))
        {
            if (TypedFieldOperatorSets.All.Contains(rule.TypedOperator))
            {
                // 裁定 2026-10-19-14 改用具體說法：寫出欄位目前設定成文字、日期或金額，不用「型別」。
                errors.Add($"{label}：「{field.Label}」目前設定為{RdeFieldValueTypeNames.DisplayName(field.ValueType)}，不能用「{FilterConditionLabels.TypedOperatorLabel(rule.TypedOperator)}」比較");
            }
            else
            {
                FilterScenarioCorruption.Add(errors, label, "typed_operator_unusable");
            }

            return;
        }

        if (field.ValueType == RdeFieldValueTypeNames.Amount)
        {
            if (!TypedAmountBasisNames.IsCanonical(rule.AmountBasis))
            {
                FilterScenarioCorruption.Add(errors, label, "typed_amount_basis_missing");
            }
        }
        else if (rule.AmountBasis is not null)
        {
            FilterScenarioCorruption.Add(errors, label, "typed_amount_basis_not_amount");
        }

        ValidateTypedOperands(rule, field, label, context, errors);
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
    /// 每一側都是專案科目分類的分類身分的多選集合，legacy scalar 視為單元素集合。
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
    /// 單側多選分類的共同驗證：至少一個、不超過上限、每個身分都存在於目前專案的科目分類。
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
        // 裁定 2026-10-08-03 移除傳票日期與回溯過帳；舊情境重開時明確說明這條條件要刪除。
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

    /// <summary>金額不是數字；解析情境時的舊式金額條件也用這一句。</summary>
    internal static string AmountFormatReason(string? value) => $"金額「{value}」不是數字";

    internal static string ValueCountReason(int max) => $"請填 1 到 {max} 個值";

    /// <summary>舊式文字、日期與數值條件的欄位：已不存在時說明欄位已移除；欄位仍在但種類不符只會是內容損毀。</summary>
    private static void ValidateLegacyField(FilterConditionSpec rule, GlFieldKind kind, string code, string label, List<string> errors)
    {
        if (!GlFieldWhitelist.TryResolve(rule.Field, out var column))
        {
            errors.Add($"{label}：{MissingFieldReason(rule.Field)}");
        }
        else if (column.Kind != kind)
        {
            FilterScenarioCorruption.Add(errors, label, code);
        }
    }

    private static void ValidateText(FilterConditionSpec rule, string label, List<string> errors)
    {
        ValidateLegacyField(rule, GlFieldKind.Text, "text_field_kind_mismatch", label, errors);

        if (rule.Keywords.All(string.IsNullOrWhiteSpace))
        {
            errors.Add($"{label}：尚未輸入比對文字");
        }
    }

    private static void ValidateTextSet(FilterConditionSpec rule, string label, List<string> errors)
    {
        ValidateLegacyField(rule, GlFieldKind.Text, "text_set_field_kind_mismatch", label, errors);

        if (rule.Mode is not (TextMatchMode.Contains or TextMatchMode.Exact))
        {
            FilterScenarioCorruption.Add(errors, label, "text_set_mode_invalid");
        }

        if (rule.UnknownNormalization is not null)
        {
            FilterScenarioCorruption.Add(errors, label, "text_set_normalization_invalid");
        }

        if (rule.Values.Count == 0)
        {
            FilterScenarioCorruption.Add(errors, label, "text_set_values_missing");
            return;
        }

        if (rule.Values.Count > FilterScenarioLimits.MaxTextSetValuesPerRule)
        {
            FilterScenarioCorruption.Add(errors, label, "text_set_values_too_many");
            return;
        }

        for (var index = 0; index < rule.Values.Count; index++)
        {
            if (string.IsNullOrEmpty(TextSetValueNormalizer.Normalize(rule.Values[index], rule.Normalization)))
            {
                FilterScenarioCorruption.Add(errors, label, "text_set_value_blank");
            }
        }
    }

    private static void ValidateDateRange(FilterConditionSpec rule, string label, List<string> errors)
    {
        ValidateLegacyField(rule, GlFieldKind.Date, "date_range_field_kind_mismatch", label, errors);

        if (rule.FromDate is null && rule.ToDate is null)
        {
            errors.Add($"{label}：起日與迄日至少須填寫一個");
            return;
        }

        foreach (var bound in new[] { rule.FromDate, rule.ToDate })
        {
            if (bound is not null && !DateOnly.TryParseExact(bound, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                errors.Add($"{label}：{DateFormatReason(bound)}");
            }
        }
    }

    private static void ValidateNumRange(FilterConditionSpec rule, string label, List<string> errors)
    {
        ValidateLegacyField(rule, GlFieldKind.Amount, "num_range_field_kind_mismatch", label, errors);

        if (rule.FromAmountScaled is null && rule.ToAmountScaled is null)
        {
            errors.Add($"{label}：下限與上限至少須填寫一個");
        }
    }

    /// <summary>
    /// 金額的「整數尾數符合」：尾數樣態放在 Keywords，
    /// 每組須為 1–12 位純數字（位數上限沿用 trailing zeros 的 long 溢位防線）。
    /// </summary>
    private static void ValidateTrailingDigits(FilterConditionSpec rule, string label, List<string> errors)
    {
        var patterns = rule.Keywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .ToList();

        if (patterns.Count == 0)
        {
            errors.Add($"{label}：尚未輸入尾數");
            return;
        }

        foreach (var pattern in patterns)
        {
            if (pattern.Length < TrailingZeroThreshold.MinCustomDigits
                || pattern.Length > TrailingZeroThreshold.MaxCustomDigits
                || !pattern.All(character => character is >= '0' and <= '9'))
            {
                errors.Add($"{label}：尾數「{pattern}」須為 "
                    + $"{TrailingZeroThreshold.MinCustomDigits} 到 {TrailingZeroThreshold.MaxCustomDigits} 位數字");
            }
        }
    }
}
