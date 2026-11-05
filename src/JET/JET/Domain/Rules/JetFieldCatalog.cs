namespace JET.Domain;

/// <summary>
/// 一個來源配對位置。配對必填性描述的是某個 mapping mode 是否要求使用者提供位置，
/// 與正規化後資料庫欄位能否為 NULL 是兩個不同維度。
/// </summary>
internal sealed record JetMappingSlot(
    string Key,
    string Label,
    int Order,
    bool IsAlwaysRequired,
    IReadOnlyList<string> RequiredModes,
    bool IsLiteral,
    bool IncludeInFieldInfo,
    bool IncludeInMappingUi);

/// <summary>正規化後 semantic field 的領域值型態；internal 以免擴張既有 public filter kind。</summary>
internal enum JetFieldValueKind
{
    Text,
    Date,
    Boolean,
    Amount
}

/// <summary>
/// 欄位沒有來源欄時，系統另外產生值的方式。傳票文件項次自動編號、
/// 傳票核准日選「與總帳日期相同」，都算已配對。
/// </summary>
internal enum JetFieldDerivedSource
{
    None,

    /// <summary>沒有來源欄時，投影後逐張傳票自動編號。</summary>
    AutoNumber,

    /// <summary>傳票核准日選「與總帳日期相同」時，取總帳日期。</summary>
    SameAsGlDate
}

/// <summary>
/// 正規化後的 GL／TB 語意欄。多個來源位置可以共同產生同一個語意欄；
/// 例如 TB 的各種金額表示法最後都產生 <c>changeAmount</c>。
/// </summary>
internal sealed record JetFieldDefinition(
    DatasetKind Dataset,
    string SemanticIdentity,
    JetFieldValueKind Kind,
    int Order,
    bool StorageNullable,
    string SemanticSqlTarget,
    bool IsGenericFilterField,
    IReadOnlyList<JetMappingSlot> MappingSlots)
{
    /// <summary>
    /// 報告與底稿上這個欄位的輸出名稱，照舊版範本加 _JE、_TB（裁定 2026-10-29-15）。欄位資訊、底稿第四步之一與
    /// 資料可靠性抽樣清單都從這裡取名，不另寫字面字串；過帳狀態的輸出欄是照來源欄複製出來的「過帳碼_JE」。
    /// </summary>
    internal string? LegacyFieldName { get; init; }

    /// <summary>沒有來源欄時，系統是否另外產生值；有的話這個欄位在條件與規則裡算已配對。</summary>
    internal JetFieldDerivedSource DerivedSource { get; init; }
}

/// <summary>
/// GL／TB 欄位語意的 Domain 權威目錄。既有 public mapping keys 與 filter whitelist 都只由這份目錄投影；
/// 目錄不產生 DDL、wire 或前端程式碼。
/// </summary>
internal static class JetFieldCatalog
{
    internal const string GlVoucherNumber = "voucherNumber";
    internal const string GlLineItem = "lineItem";
    internal const string GlDate = "glDate";
    // 歷史 action 鍵 approvalDate 在本案表示「傳票核准日」，不是 Document Date；儲存欄為 approval_date。
    internal const string GlApprovalDate = "approvalDate";
    internal const string GlAccountNumber = "accountNumber";
    internal const string GlAccountName = "accountName";
    internal const string GlDescription = "description";
    internal const string GlSourceModule = "sourceModule";
    internal const string GlCreatedBy = "createdBy";
    internal const string GlApprovedBy = "approvedBy";
    internal const string GlManual = "isManual";
    internal const string GlPostingStatus = "postingStatus";
    internal const string GlAmount = "amount";
    internal const string GlDebitAmount = "debitAmount";
    internal const string GlCreditAmount = "creditAmount";
    internal const string GlDrCr = "drCr";
    internal const string GlDrCrDebitCode = "drCrDebitCode";
    internal const string GlDrCrCreditCode = "drCrCreditCode";

    internal const string TbAccountNumber = "accountNumber";
    internal const string TbAccountName = "accountName";
    internal const string TbChangeAmount = "changeAmount";
    internal const string TbDebitAmount = "debitAmount";
    internal const string TbCreditAmount = "creditAmount";
    internal const string TbOpeningBalance = "openingBalance";
    internal const string TbClosingBalance = "closingBalance";
    internal const string TbOpeningDebitBalance = "openingDebitBalance";
    internal const string TbOpeningCreditBalance = "openingCreditBalance";
    internal const string TbClosingDebitBalance = "closingDebitBalance";
    internal const string TbClosingCreditBalance = "closingCreditBalance";

    internal static IReadOnlyList<JetFieldDefinition> GlFields { get; } =
    [
        Field(
            DatasetKind.Gl, GlVoucherNumber, JetFieldValueKind.Text, 0, storageNullable: true, "voucher_number",
            "傳票號碼_JE", isGenericFilterField: true,
            Slot(GlVoucherNumber, "傳票號碼", 0, always: true, fieldInfo: true)),
        Field(
            DatasetKind.Gl, GlLineItem, JetFieldValueKind.Text, 1, storageNullable: true, "line_item",
            "傳票文件項次_JE_S", isGenericFilterField: true,
            Slot(GlLineItem, "傳票文件項次", 1, fieldInfo: true)) with
        {
            DerivedSource = JetFieldDerivedSource.AutoNumber
        },
        Field(
            DatasetKind.Gl, GlDate, JetFieldValueKind.Date, 2, storageNullable: true, "gl_date",
            "總帳日期_JE", isGenericFilterField: true,
            Slot(GlDate, "總帳日期", 2, always: true, fieldInfo: true)),
        Field(
            DatasetKind.Gl, GlApprovalDate, JetFieldValueKind.Date, 3, storageNullable: true, "approval_date",
            "傳票核准日_JE", isGenericFilterField: true,
            Slot(GlApprovalDate, "傳票核准日", 3, fieldInfo: true)) with
        {
            DerivedSource = JetFieldDerivedSource.SameAsGlDate
        },
        // 裁定 2026-10-08-03 移除新版自行加入的傳票日期（voucherDate）；其餘欄位的順序值不重排。
        Field(
            DatasetKind.Gl, GlAccountNumber, JetFieldValueKind.Text, 5, storageNullable: true, "account_number",
            "會計科目編號_JE", isGenericFilterField: true,
            Slot(GlAccountNumber, "會計科目編號", 5, always: true, fieldInfo: true)),
        Field(
            DatasetKind.Gl, GlAccountName, JetFieldValueKind.Text, 6, storageNullable: true, "account_name",
            "會計科目名稱_JE", isGenericFilterField: true,
            Slot(GlAccountName, "會計科目名稱", 6, always: true, fieldInfo: true)),
        Field(
            DatasetKind.Gl, GlDescription, JetFieldValueKind.Text, 7, storageNullable: true, "description",
            "傳票摘要_JE", isGenericFilterField: true,
            Slot(GlDescription, "傳票摘要", 7, always: true, fieldInfo: true)),
        Field(
            DatasetKind.Gl, GlSourceModule, JetFieldValueKind.Text, 8, storageNullable: true, "source_module",
            "分錄來源模組_JE", isGenericFilterField: true,
            Slot(GlSourceModule, "分錄來源模組", 8, fieldInfo: true)),
        Field(
            DatasetKind.Gl, GlCreatedBy, JetFieldValueKind.Text, 9, storageNullable: true, "created_by",
            "傳票建立人員_JE", isGenericFilterField: true,
            Slot(GlCreatedBy, "傳票建立人員", 9, fieldInfo: true)),
        Field(
            DatasetKind.Gl, GlApprovedBy, JetFieldValueKind.Text, 10, storageNullable: true, "approved_by",
            "傳票核准人員_JE", isGenericFilterField: true,
            Slot(GlApprovedBy, "傳票核准人員", 10, fieldInfo: true)),
        Field(
            DatasetKind.Gl, GlManual, JetFieldValueKind.Boolean, 11, storageNullable: true, "is_manual",
            "人工傳票否_JE_S", isGenericFilterField: false,
            Slot(GlManual, "人工/自動分錄", 11)),
        Field(
            DatasetKind.Gl, GlAmount, JetFieldValueKind.Amount, 12, storageNullable: false, "amount_scaled",
            "傳票金額_JE", isGenericFilterField: true,
            Slot(GlAmount, "金額", 12,
                modes: [GlAmountModeNames.Signed, GlAmountModeNames.Side, GlAmountModeNames.Flag],
                fieldInfo: true),
            Slot(GlDebitAmount, "借方金額", 13, modes: [GlAmountModeNames.Dual]),
            Slot(GlCreditAmount, "貸方金額", 14, modes: [GlAmountModeNames.Dual]),
            Slot(GlDrCr, "借貸別", 15,
                modes: [GlAmountModeNames.Side, GlAmountModeNames.Flag]),
            Slot(GlDrCrDebitCode, "借方代碼", 16,
                modes: [GlAmountModeNames.Side, GlAmountModeNames.Flag], literal: true),
            Slot(GlDrCrCreditCode, "貸方代碼", 17,
                modes: [GlAmountModeNames.Side, GlAmountModeNames.Flag], literal: true)),
        Field(
            DatasetKind.Gl, GlPostingStatus, JetFieldValueKind.Text, 13, storageNullable: true,
            "posting_status", "過帳碼_JE", isGenericFilterField: false,
            Slot(GlPostingStatus, "過帳狀態", 18))
    ];

    internal static IReadOnlyList<JetFieldDefinition> TbFields { get; } =
    [
        Field(
            DatasetKind.Tb, TbAccountNumber, JetFieldValueKind.Text, 0, storageNullable: true, "account_number",
            "會計科目編號_TB", isGenericFilterField: false,
            Slot(TbAccountNumber, "會計科目編號", 0, always: true, fieldInfo: true)),
        Field(
            DatasetKind.Tb, TbAccountName, JetFieldValueKind.Text, 1, storageNullable: true, "account_name",
            "會計科目名稱_TB", isGenericFilterField: false,
            Slot(TbAccountName, "會計科目名稱", 1, always: true, fieldInfo: true)),
        Field(
            DatasetKind.Tb, TbChangeAmount, JetFieldValueKind.Amount, 2, storageNullable: false,
            "change_amount_scaled", "試算表變動金額_TB", isGenericFilterField: false,
            Slot(TbChangeAmount, "年度變動金額", 2, modes: [TbChangeModeNames.Direct], fieldInfo: true),
            Slot(TbDebitAmount, "借方金額", 3, modes: [TbChangeModeNames.DebitCredit], fieldInfo: true),
            Slot(TbCreditAmount, "貸方金額", 4, modes: [TbChangeModeNames.DebitCredit], fieldInfo: true),
            Slot(TbOpeningBalance, "期初餘額", 5, modes: [TbChangeModeNames.OpenClose], fieldInfo: true),
            Slot(TbClosingBalance, "期末餘額", 6, modes: [TbChangeModeNames.OpenClose], fieldInfo: true),
            Slot(TbOpeningDebitBalance, "期初借方", 7, modes: [TbChangeModeNames.OpenCloseBySide], fieldInfo: true),
            Slot(TbOpeningCreditBalance, "期初貸方", 8, modes: [TbChangeModeNames.OpenCloseBySide], fieldInfo: true),
            Slot(TbClosingDebitBalance, "期末借方", 9, modes: [TbChangeModeNames.OpenCloseBySide], fieldInfo: true),
            Slot(TbClosingCreditBalance, "期末貸方", 10, modes: [TbChangeModeNames.OpenCloseBySide], fieldInfo: true))
    ];

    internal static IReadOnlyList<JetMappingSlot> GlMappingSlots { get; } =
        FlattenSlots(GlFields);

    internal static IReadOnlyList<JetMappingSlot> TbMappingSlots { get; } =
        FlattenSlots(TbFields);

    /// <summary>
    /// runtime mapping UI 目前可見的相容投影。Contract-only 欄位仍可進 All/metadata，
    /// 但在對應前端控制階段完成前不得藉 catalog mirror 提前出現在 UI；
    /// 過帳狀態已於「前端工作流整合」接上配對控制項，故不再排除。
    /// </summary>
    internal static IReadOnlyList<JetMappingSlot> GlMappingUiSlots { get; } =
        GlMappingSlots.Where(static slot => slot.IncludeInMappingUi).ToArray();

    internal static IReadOnlyList<JetMappingSlot> TbMappingUiSlots { get; } =
        TbMappingSlots.Where(static slot => slot.IncludeInMappingUi).ToArray();

    internal static IReadOnlyList<string> GlMappingKeys { get; } =
        GlMappingSlots.Select(static slot => slot.Key).ToArray();

    internal static IReadOnlyList<string> TbMappingKeys { get; } =
        TbMappingSlots.Select(static slot => slot.Key).ToArray();

    internal static IReadOnlyDictionary<string, GlFieldColumn> GlFilterFields { get; } =
        GlFields
            .Where(static field => field.IsGenericFilterField)
            .ToDictionary(
                static field => field.MappingSlots.Single(static slot => slot.IncludeInFieldInfo).Key,
                static field => new GlFieldColumn(
                    field.SemanticSqlTarget,
                    ToPublicFilterKind(field.Kind)),
                StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, JetFieldDefinition> GlFieldsByMappingKey =
        IndexFields(GlFields);

    private static readonly IReadOnlyDictionary<string, JetFieldDefinition> TbFieldsByMappingKey =
        IndexFields(TbFields);

    private static readonly IReadOnlyDictionary<string, JetFieldDefinition> GlFieldsBySemanticIdentity =
        GlFields.ToDictionary(static field => field.SemanticIdentity, StringComparer.Ordinal);

    /// <summary>
    /// 取得指定來源位置所產生的 GL semantic field。回傳欄位的 SQL target 與 filterability
    /// 描述 semantic output，不表示該來源位置可直接拿去查詢 SQL 欄。
    /// </summary>
    internal static JetFieldDefinition FindGlSemanticFieldByMappingKey(string key) =>
        GlFieldsByMappingKey[key];

    /// <summary>
    /// 取得指定來源位置所產生的 TB semantic field。回傳欄位的 SQL target 描述
    /// semantic output；例如各種金額來源位置都產生 <c>changeAmount</c>。
    /// </summary>
    internal static JetFieldDefinition FindTbSemanticFieldByMappingKey(string key) =>
        TbFieldsByMappingKey[key];

    /// <summary>
    /// 來源欄配到這些位置時，裡面是金額。資料預覽的 PBC-GL、PBC-TB 與第三步的來源表，只把配到這些位置的來源欄靠右（裁定 2026-11-05-04）；
    /// 前端 ui-core.js 的 GL_FIELDS、TB_FIELDS 用 amount: true 鏡射這一份。GL 的金額欄位底下另有借貸別與借貸代碼，它們不是金額，所以逐一列出。
    /// </summary>
    internal static IReadOnlySet<string> AmountMappingKeys(DatasetKind dataset) => dataset switch
    {
        DatasetKind.Gl => GlAmountMappingKeys,
        DatasetKind.Tb => TbAmountMappingKeys,
        _ => throw new ArgumentOutOfRangeException(nameof(dataset), dataset, "只有 GL 與 TB 有欄位配對。")
    };

    private static readonly IReadOnlySet<string> GlAmountMappingKeys =
        new HashSet<string>([GlAmount, GlDebitAmount, GlCreditAmount], StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> TbAmountMappingKeys =
        new HashSet<string>(
            TbFields.Single(static field => field.SemanticIdentity == TbChangeAmount).MappingSlots.Select(static slot => slot.Key),
            StringComparer.Ordinal);

    /// <summary>依語意欄名稱找 GL 欄位；欄位資訊記下的 JetField 不是標準欄（例如攸關資料元素的 fieldId）時回 null。</summary>
    internal static JetFieldDefinition? FindGlFieldBySemanticIdentity(string semanticIdentity) =>
        GlFieldsBySemanticIdentity.GetValueOrDefault(semanticIdentity);

    /// <summary>
    /// 判斷目前已提交 mapping 是否真的提供指定 GL semantic field 的來源位置。
    /// 以目錄中的 semantic field → mapping slots 關係解析，且空白 legacy 值一律 fail closed；
    /// 不以 target 欄位存在或投影後全 NULL 猜測可用性。
    /// </summary>
    internal static bool HasMappedGlSemanticField(
        IReadOnlyDictionary<string, string> mapping,
        string semanticIdentity)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var field = GlFieldsBySemanticIdentity[semanticIdentity];
        return field.MappingSlots
            .Where(static slot => !slot.IsLiteral)
            .Any(slot => mapping.TryGetValue(slot.Key, out var value)
                && !string.IsNullOrWhiteSpace(value));
    }

    /// <summary>
    /// 已確認的 GL 配對下，條件與規則能使用的語意欄位，依目錄順序：欄位有來源欄，或目錄標示的衍生來源成立。
    /// 第四步、第五步與前端停用說明都讀這一份。沒有帶投影設定的舊配對，依配對本身推出傳票核准日的模式。
    /// </summary>
    internal static IReadOnlyList<string> AvailableGlFields(
        IReadOnlyDictionary<string, string> mapping,
        GlMappingOptions? options)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var approvalDateMode = (options ?? GlMappingOptions.NormalizeLegacy(mapping)).ApprovalDateMode;
        return GlFields
            .Where(field => HasMappedGlSemanticField(mapping, field.SemanticIdentity) || field.DerivedSource switch
            {
                JetFieldDerivedSource.AutoNumber => true,
                JetFieldDerivedSource.SameAsGlDate => approvalDateMode == ApprovalDateModeNames.SameAsGlDate,
                _ => false
            })
            .Select(static field => field.SemanticIdentity)
            .ToArray();
    }

    /// <summary>取得 semantic field 的主要使用者可見 mapping label。</summary>
    internal static string GlSemanticFieldMappingLabel(string semanticIdentity) =>
        GlFieldsBySemanticIdentity[semanticIdentity]
            .MappingSlots
            .Where(static slot => !slot.IsLiteral)
            .OrderBy(static slot => slot.Order)
            .Select(static slot => slot.Label)
            .First();

    internal static bool TryResolveGlFilterField(string? key, out GlFieldColumn column)
    {
        if (key is not null && GlFilterFields.TryGetValue(key, out var resolved))
        {
            column = resolved;
            return true;
        }

        column = null!;
        return false;
    }

    internal static IReadOnlyList<string> RequiredGlMappingKeys(GlAmountMode mode) =>
        RequiredMappingKeys(GlMappingSlots, GlModeName(mode));

    internal static IReadOnlyList<string> RequiredTbMappingKeys(TbChangeMode mode) =>
        RequiredMappingKeys(TbMappingSlots, TbModeName(mode));

    private static readonly IReadOnlyDictionary<string, JetMappingSlot> GlSlotsByKey =
        GlMappingSlots.ToDictionary(static slot => slot.Key, StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, JetMappingSlot> TbSlotsByKey =
        TbMappingSlots.ToDictionary(static slot => slot.Key, StringComparer.Ordinal);

    internal static bool IsGlMappingKey(string key) => GlSlotsByKey.ContainsKey(key);

    internal static bool IsGlLiteralMappingKey(string key) =>
        GlSlotsByKey.TryGetValue(key, out var slot) && slot.IsLiteral;

    internal static bool IsTbLiteralMappingKey(string key) =>
        TbSlotsByKey.TryGetValue(key, out var slot) && slot.IsLiteral;

    private static JetFieldDefinition Field(
        DatasetKind dataset,
        string semanticIdentity,
        JetFieldValueKind kind,
        int order,
        bool storageNullable,
        string semanticSqlTarget,
        string? legacyFieldName,
        bool isGenericFilterField,
        params JetMappingSlot[] mappingSlots) =>
        new(
            dataset,
            semanticIdentity,
            kind,
            order,
            storageNullable,
            semanticSqlTarget,
            isGenericFilterField,
            mappingSlots)
        {
            LegacyFieldName = legacyFieldName
        };

    private static JetMappingSlot Slot(
        string key,
        string label,
        int order,
        bool always = false,
        IReadOnlyList<string>? modes = null,
        bool literal = false,
        bool fieldInfo = false,
        bool mappingUi = true) =>
        new(key, label, order, always, modes ?? [], literal, fieldInfo, mappingUi);

    private static IReadOnlyList<JetMappingSlot> FlattenSlots(IReadOnlyList<JetFieldDefinition> fields) =>
        fields
            .SelectMany(static field => field.MappingSlots)
            .OrderBy(static slot => slot.Order)
            .ToArray();

    private static IReadOnlyDictionary<string, JetFieldDefinition> IndexFields(
        IReadOnlyList<JetFieldDefinition> fields) =>
        fields
            .SelectMany(static field => field.MappingSlots.Select(slot => (slot.Key, Field: field)))
            .ToDictionary(static item => item.Key, static item => item.Field, StringComparer.Ordinal);

    private static IReadOnlyList<string> RequiredMappingKeys(
        IReadOnlyList<JetMappingSlot> slots,
        string? modeName) =>
        slots
            .Where(slot =>
                slot.IsAlwaysRequired
                || (modeName is not null
                    && slot.RequiredModes.Contains(modeName, StringComparer.Ordinal)))
            .Select(static slot => slot.Key)
            .ToArray();

    private static string? GlModeName(GlAmountMode mode) => mode switch
    {
        GlAmountMode.SignedAmount => GlAmountModeNames.Signed,
        GlAmountMode.AmountWithSide => GlAmountModeNames.Side,
        GlAmountMode.AmountWithFlag => GlAmountModeNames.Flag,
        GlAmountMode.DualAmount => GlAmountModeNames.Dual,
        _ => null
    };

    private static string? TbModeName(TbChangeMode mode) => mode switch
    {
        TbChangeMode.DirectChange => TbChangeModeNames.Direct,
        TbChangeMode.DebitCredit => TbChangeModeNames.DebitCredit,
        TbChangeMode.OpenClose => TbChangeModeNames.OpenClose,
        TbChangeMode.OpenCloseBySide => TbChangeModeNames.OpenCloseBySide,
        _ => null
    };

    private static GlFieldKind ToPublicFilterKind(JetFieldValueKind kind) => kind switch
    {
        JetFieldValueKind.Text => GlFieldKind.Text,
        JetFieldValueKind.Date => GlFieldKind.Date,
        JetFieldValueKind.Amount => GlFieldKind.Amount,
        _ => throw new InvalidOperationException(
            $"Semantic field kind '{kind}' 不能投影成 public generic filter kind。")
    };
}
