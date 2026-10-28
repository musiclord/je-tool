namespace JET.Domain;

/// <summary>
/// 資料預覽 GL 與 GL(未納入測試) 的欄位計畫（預篩選收斂計畫階段五，使用者 2026-10-22 裁定）。
/// 核心欄位在前，接著是已配對的選填欄位與攸關資料元素欄位；未配對的不列，借方與貸方金額不分欄。
/// 兩頁列同一組欄位，GL(未納入測試) 最後多一欄未納入原因。選填欄位算不算已配對，讀第四步、第五步同一份
/// <see cref="JetFieldCatalog.AvailableGlFields"/>，所以傳票核准日選「與總帳日期相同」時也列出；
/// 傳票文件項次是核心欄位，自動編號時照樣列出。
/// 欄名取自欄位目錄，和欄位配對畫面同名；本機與 SQL Server 的查詢都由這份計畫產生。
/// </summary>
internal static class DataPreviewGlColumns
{
    /// <summary>核心欄位，照原本 GL 頁的順序；識別沿用原本的回應鍵。</summary>
    private static readonly (string Field, string Key)[] CoreFields =
    [
        (JetFieldCatalog.GlDocNum, "documentNumber"),
        (JetFieldCatalog.GlLineId, "lineItem"),
        (JetFieldCatalog.GlPostDate, "postDate"),
        (JetFieldCatalog.GlAccNum, "accountCode"),
        (JetFieldCatalog.GlAccName, "accountName"),
        (JetFieldCatalog.GlDescription, "documentDescription"),
        (JetFieldCatalog.GlAmount, "amount")
    ];

    /// <summary>選填欄位的回應鍵；列出的順序依欄位目錄，已配對才列。</summary>
    private static readonly IReadOnlyDictionary<string, string> OptionalFieldKeys =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [JetFieldCatalog.GlDocDate] = "approvalDate",
            [JetFieldCatalog.GlJeSource] = "sourceModule",
            [JetFieldCatalog.GlCreateBy] = "createdBy",
            [JetFieldCatalog.GlApproveBy] = "approvedBy",
            [JetFieldCatalog.GlManual] = "manualAuto",
            [JetFieldCatalog.GlPostingStatus] = "postingStatus"
        };

    /// <summary>借貸別由金額的配對方式決定，每筆分錄都有，接在金額後面；名稱和配對畫面的借貸別欄位相同。</summary>
    private static readonly DataPreviewGlColumn DebitCredit = new(
        "drCr",
        JetFieldCatalog.GlMappingSlots.Single(static slot => slot.Key == JetFieldCatalog.GlDcField).Label,
        RdeFieldValueTypeNames.Text,
        DataPreviewGlValueSource.EntryText,
        "dr_cr");

    private static readonly DataPreviewGlColumn ExclusionReason = new(
        "exclusionReason",
        "未納入原因",
        RdeFieldValueTypeNames.Text,
        DataPreviewGlValueSource.EntryExclusionReason,
        "exclusion_reason");

    /// <param name="mapping">已確認的 GL 配對；還沒確認時是 null，只列核心欄位。</param>
    /// <param name="options">配對的投影設定；沒有時依配對本身推出傳票核准日的模式，也沒有攸關資料元素欄位。</param>
    internal static IReadOnlyList<DataPreviewGlColumn> For(
        DataPreviewDataset dataset,
        IReadOnlyDictionary<string, string>? mapping,
        GlMappingOptions? options)
    {
        if (dataset is not (DataPreviewDataset.Gl or DataPreviewDataset.GlExcluded))
        {
            throw new ArgumentOutOfRangeException(nameof(dataset), dataset, "只有 GL 與 GL(未納入測試) 依配對列欄位。");
        }

        var available = mapping is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : JetFieldCatalog.AvailableGlFields(mapping, options).ToHashSet(StringComparer.Ordinal);

        var columns = CoreFields.Select(static core => EntryColumn(core.Field, core.Key)).ToList();
        columns.Add(DebitCredit);
        columns.AddRange(JetFieldCatalog.GlFields
            .Where(field => OptionalFieldKeys.ContainsKey(field.SemanticIdentity) && available.Contains(field.SemanticIdentity))
            .Select(static field => EntryColumn(field.SemanticIdentity, OptionalFieldKeys[field.SemanticIdentity])));
        if (mapping is not null)
        {
            columns.AddRange((options?.RdeFields ?? []).Select(static field => new DataPreviewGlColumn(
                field.FieldId, field.Label, field.ValueType, DataPreviewGlValueSource.Rde, field.FieldId)));
        }

        if (dataset == DataPreviewDataset.GlExcluded)
        {
            columns.Add(ExclusionReason);
        }

        return columns;
    }

    private static DataPreviewGlColumn EntryColumn(string semanticIdentity, string key)
    {
        var field = JetFieldCatalog.GlFields.Single(field => field.SemanticIdentity == semanticIdentity);
        var (valueType, source) = field.Kind switch
        {
            JetFieldValueKind.Date => (RdeFieldValueTypeNames.Date, DataPreviewGlValueSource.EntryText),
            JetFieldValueKind.Amount => (RdeFieldValueTypeNames.Money, DataPreviewGlValueSource.EntryAmount),
            // 目錄裡唯一的布林欄位是人工或自動分錄，畫面寫「人工」「自動」。
            JetFieldValueKind.Boolean => (RdeFieldValueTypeNames.Text, DataPreviewGlValueSource.EntryManualAuto),
            _ => (RdeFieldValueTypeNames.Text, DataPreviewGlValueSource.EntryText)
        };
        return new DataPreviewGlColumn(
            key,
            JetFieldCatalog.GlSemanticFieldMappingLabel(semanticIdentity),
            valueType,
            source,
            field.SemanticSqlTarget);
    }
}
