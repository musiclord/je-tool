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
    string? CanonicalName,
    int? CanonicalOrder,
    bool IsGenericFilterField,
    IReadOnlyList<JetMappingSlot> MappingSlots)
{
    /// <summary>
    /// Legacy TableDef 在 mapping 後的實際欄名。預設沿用既有 compatibility CanonicalName；
    /// 兩者刻意分離，避免本階段提前改變既有 Field Info writer 的可見輸出。
    /// </summary>
    internal string? LegacyFieldName { get; init; }
}

/// <summary>
/// GL／TB 欄位語意的 Domain 權威目錄。既有 public mapping keys、filter whitelist 與
/// canonical-name dictionaries 都只由這份目錄投影；目錄不產生 DDL、wire 或前端程式碼。
/// </summary>
internal static class JetFieldCatalog
{
    internal const string GlDocNum = "docNum";
    internal const string GlLineId = "lineID";
    internal const string GlPostDate = "postDate";
    internal const string GlDocDate = "docDate";
    internal const string GlVoucherDate = "voucherDate";
    internal const string GlAccNum = "accNum";
    internal const string GlAccName = "accName";
    internal const string GlDescription = "description";
    internal const string GlJeSource = "jeSource";
    internal const string GlCreateBy = "createBy";
    internal const string GlApproveBy = "approveBy";
    internal const string GlManual = "manual";
    internal const string GlPostingStatus = "postingStatus";
    internal const string GlAmount = "amount";
    internal const string GlDebitAmount = "debitAmount";
    internal const string GlCreditAmount = "creditAmount";
    internal const string GlDcField = "dcField";
    internal const string GlDcDebitCode = "dcDebitCode";

    internal const string TbAccNum = "accNum";
    internal const string TbAccName = "accName";
    internal const string TbAmount = "amount";
    internal const string TbDebitAmount = "debitAmt";
    internal const string TbCreditAmount = "creditAmt";
    internal const string TbOpeningBalance = "openingBalance";
    internal const string TbClosingBalance = "closingBalance";
    internal const string TbOpeningDebit = "openingDebit";
    internal const string TbOpeningCredit = "openingCredit";
    internal const string TbClosingDebit = "closingDebit";
    internal const string TbClosingCredit = "closingCredit";

    internal const string TbChangeAmount = "changeAmount";

    internal static IReadOnlyList<JetFieldDefinition> GlFields { get; } =
    [
        Field(
            DatasetKind.Gl, GlDocNum, JetFieldValueKind.Text, 0, storageNullable: true, "document_number",
            "傳票號碼_JE", canonicalOrder: 0, isGenericFilterField: true,
            Slot(GlDocNum, "傳票號碼", 0, always: true, fieldInfo: true)),
        Field(
            DatasetKind.Gl, GlLineId, JetFieldValueKind.Text, 1, storageNullable: true, "line_item",
            "傳票文件項次_JE_S", canonicalOrder: 1, isGenericFilterField: true,
            Slot(GlLineId, "傳票文件項次", 1, fieldInfo: true)),
        Field(
            DatasetKind.Gl, GlPostDate, JetFieldValueKind.Date, 2, storageNullable: true, "post_date",
            "總帳日期_JE", canonicalOrder: 2, isGenericFilterField: true,
            Slot(GlPostDate, "總帳日期", 2, always: true, fieldInfo: true)),
        Field(
            DatasetKind.Gl, GlDocDate, JetFieldValueKind.Date, 3, storageNullable: true, "approval_date",
            canonicalName: null, canonicalOrder: null, isGenericFilterField: true,
            Slot(GlDocDate, "傳票核准日", 3, fieldInfo: true)) with
        {
            LegacyFieldName = "傳票核准日_JE"
        },
        Field(
            DatasetKind.Gl, GlVoucherDate, JetFieldValueKind.Date, 4, storageNullable: true, "voucher_date",
            canonicalName: null, canonicalOrder: null, isGenericFilterField: true,
            Slot(GlVoucherDate, "傳票日期", 4, fieldInfo: true)),
        Field(
            DatasetKind.Gl, GlAccNum, JetFieldValueKind.Text, 5, storageNullable: true, "account_code",
            "會計科目編號_JE", canonicalOrder: 5, isGenericFilterField: true,
            Slot(GlAccNum, "會計科目編號", 5, always: true, fieldInfo: true)),
        Field(
            DatasetKind.Gl, GlAccName, JetFieldValueKind.Text, 6, storageNullable: true, "account_name",
            "會計科目名稱_JE", canonicalOrder: 6, isGenericFilterField: true,
            Slot(GlAccName, "會計科目名稱", 6, always: true, fieldInfo: true)),
        Field(
            DatasetKind.Gl, GlDescription, JetFieldValueKind.Text, 7, storageNullable: true, "document_description",
            "傳票摘要_JE", canonicalOrder: 8, isGenericFilterField: true,
            Slot(GlDescription, "傳票摘要", 7, always: true, fieldInfo: true)),
        Field(
            DatasetKind.Gl, GlJeSource, JetFieldValueKind.Text, 8, storageNullable: true, "source_module",
            canonicalName: null, canonicalOrder: null, isGenericFilterField: true,
            Slot(GlJeSource, "分錄來源模組", 8, fieldInfo: true)) with
        {
            LegacyFieldName = "分錄來源模組_JE"
        },
        Field(
            DatasetKind.Gl, GlCreateBy, JetFieldValueKind.Text, 9, storageNullable: true, "created_by",
            "傳票建立人員_JE", canonicalOrder: 3, isGenericFilterField: true,
            Slot(GlCreateBy, "傳票建立人員", 9, fieldInfo: true)),
        Field(
            DatasetKind.Gl, GlApproveBy, JetFieldValueKind.Text, 10, storageNullable: true, "approved_by",
            "傳票核准人員_JE", canonicalOrder: 4, isGenericFilterField: true,
            Slot(GlApproveBy, "傳票核准人員", 10, fieldInfo: true)),
        Field(
            DatasetKind.Gl, GlManual, JetFieldValueKind.Boolean, 11, storageNullable: true, "is_manual",
            canonicalName: null, canonicalOrder: null, isGenericFilterField: false,
            Slot(GlManual, "人工/自動分錄", 11)) with
        {
            LegacyFieldName = "人工傳票否_JE_S"
        },
        Field(
            DatasetKind.Gl, GlAmount, JetFieldValueKind.Amount, 12, storageNullable: false, "amount_scaled",
            "傳票金額_JE", canonicalOrder: 7, isGenericFilterField: true,
            Slot(GlAmount, "傳票金額（單欄）", 12,
                modes: [GlAmountModeNames.Signed, GlAmountModeNames.Side, GlAmountModeNames.Flag],
                fieldInfo: true),
            Slot(GlDebitAmount, "借方金額", 13, modes: [GlAmountModeNames.Dual]),
            Slot(GlCreditAmount, "貸方金額", 14, modes: [GlAmountModeNames.Dual]),
            Slot(GlDcField, "借貸別欄位", 15,
                modes: [GlAmountModeNames.Side, GlAmountModeNames.Flag]),
            Slot(GlDcDebitCode, "借方標識代碼", 16,
                modes: [GlAmountModeNames.Side, GlAmountModeNames.Flag], literal: true)),
        Field(
            DatasetKind.Gl, GlPostingStatus, JetFieldValueKind.Text, 13, storageNullable: true,
            "posting_status", canonicalName: null, canonicalOrder: null, isGenericFilterField: false,
            Slot(GlPostingStatus, "過帳狀態", 17))
    ];

    internal static IReadOnlyList<JetFieldDefinition> TbFields { get; } =
    [
        Field(
            DatasetKind.Tb, TbAccNum, JetFieldValueKind.Text, 0, storageNullable: true, "account_code",
            "會計科目編號_TB", canonicalOrder: 0, isGenericFilterField: false,
            Slot(TbAccNum, "會計科目編號", 0, always: true, fieldInfo: true)),
        Field(
            DatasetKind.Tb, TbAccName, JetFieldValueKind.Text, 1, storageNullable: true, "account_name",
            "會計科目名稱_TB", canonicalOrder: 1, isGenericFilterField: false,
            Slot(TbAccName, "會計科目名稱", 1, always: true, fieldInfo: true)),
        Field(
            DatasetKind.Tb, TbChangeAmount, JetFieldValueKind.Amount, 2, storageNullable: false,
            "change_amount_scaled", "試算表變動金額_TB", canonicalOrder: 2, isGenericFilterField: false,
            Slot(TbAmount, "年度變動金額", 2, modes: [TbChangeModeNames.Direct], fieldInfo: true),
            Slot(TbDebitAmount, "借方金額", 3, modes: [TbChangeModeNames.DebitCredit], fieldInfo: true),
            Slot(TbCreditAmount, "貸方金額", 4, modes: [TbChangeModeNames.DebitCredit], fieldInfo: true),
            Slot(TbOpeningBalance, "期初餘額", 5, modes: [TbChangeModeNames.OpenClose], fieldInfo: true),
            Slot(TbClosingBalance, "期末餘額", 6, modes: [TbChangeModeNames.OpenClose], fieldInfo: true),
            Slot(TbOpeningDebit, "期初借方", 7, modes: [TbChangeModeNames.OpenCloseBySide], fieldInfo: true),
            Slot(TbOpeningCredit, "期初貸方", 8, modes: [TbChangeModeNames.OpenCloseBySide], fieldInfo: true),
            Slot(TbClosingDebit, "期末借方", 9, modes: [TbChangeModeNames.OpenCloseBySide], fieldInfo: true),
            Slot(TbClosingCredit, "期末貸方", 10, modes: [TbChangeModeNames.OpenCloseBySide], fieldInfo: true))
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

    internal static IReadOnlyDictionary<string, string> GlCanonicalNames { get; } =
        CreateCanonicalNames(GlFields);

    internal static IReadOnlyDictionary<string, string> TbCanonicalNames { get; } =
        CreateCanonicalNames(TbFields);

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

    private static readonly IReadOnlyDictionary<string, JetMappingSlot> GlSlotsByKey =
        GlMappingSlots.ToDictionary(static slot => slot.Key, StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, JetMappingSlot> TbSlotsByKey =
        TbMappingSlots.ToDictionary(static slot => slot.Key, StringComparer.Ordinal);

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

    internal static JetMappingSlot FindGlMappingSlot(string key) =>
        GlSlotsByKey[key];

    internal static JetMappingSlot FindTbMappingSlot(string key) =>
        TbSlotsByKey[key];

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
        string? canonicalName,
        int? canonicalOrder,
        bool isGenericFilterField,
        params JetMappingSlot[] mappingSlots) =>
        new(
            dataset,
            semanticIdentity,
            kind,
            order,
            storageNullable,
            semanticSqlTarget,
            canonicalName,
            canonicalOrder,
            isGenericFilterField,
            mappingSlots)
        {
            LegacyFieldName = canonicalName
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

    private static IReadOnlyDictionary<string, string> CreateCanonicalNames(
        IReadOnlyList<JetFieldDefinition> fields) =>
        fields
            .Where(static field => field.CanonicalName is not null)
            .OrderBy(static field => field.CanonicalOrder)
            .ToDictionary(
                static field => field.SemanticIdentity,
                static field => field.CanonicalName!,
                StringComparer.Ordinal);

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
