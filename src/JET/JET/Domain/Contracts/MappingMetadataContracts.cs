namespace JET.Domain;

internal static class ManualAutoPolicyErrors
{
    internal const string OverlapValueKey = "JET.ManualAutoOverlapValue";

    internal static ArgumentException Overlap(string value)
    {
        var error = new ArgumentException($"manualAutoPolicy 的 manual/automatic 代碼不得重疊：'{value}'。", "options");
        error.Data[OverlapValueKey] = value;
        return error;
    }
}

/// <summary>
/// JET 報告內嵌欄位配對 metadata 的固定位置與版本。可見欄位資訊只供人閱讀；
/// machine round-trip 一律以這組 marker/version/payload 為準，不能反推顯示名稱。
/// </summary>
public static class MappingMetadataFormat
{
    public const string WorksheetName = "自動化工具-檔案欄位資訊";
    public const string Marker = "JET_MAPPING_METADATA";
    public const int CurrentVersion = 2;
    public const string MarkerCell = "F1";
    public const string VersionCell = "G1";
    public const string PayloadCell = "H1";
}

public static class ApprovalDateModeNames
{
    public const string Unmapped = "unmapped";
    public const string Mapped = "mapped";
    public const string SameAsPostDate = "sameAsPostDate";

    public static bool IsCanonical(string? value) => value is Unmapped or Mapped or SameAsPostDate;
}

public static class RdeFieldValueTypeNames
{
    public const string Text = "text";
    public const string Date = "date";
    public const string Money = "money";

    public static bool IsCanonical(string? value) => value is Text or Date or Money;
}

/// <summary>
/// RDE 在三個 provider 共用的儲存邊界。SQL Server 的可索引 text_value 是
/// NVARCHAR(450)，因此所有 provider 都在投影前以 .NET UTF-16 code units 套用同一上限；
/// 不截斷、不讓本機 provider 接受 SQL Server 無法保存的值。
/// </summary>
public static class GlRdeStorageLimits
{
    public const int TextValueUtf16CodeUnits = 450;
    public const int LabelUtf16CodeUnits = 400;
}

public sealed record GlPostingStatusPolicy(
    IReadOnlyList<string> AcceptedValues,
    bool IncludeBlank);

public sealed record GlManualAutoPolicy(
    IReadOnlyList<string> ManualValues,
    IReadOnlyList<string> AutomaticValues)
{
    // 缺少設定的舊案件維持逐值判定；補集與空白必須由審計員分別指定。
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? UnlistedValueKind { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? BlankValueKind { get; init; }
}

public static class ManualAutoValueKindNames
{
    public const string Reject = "reject";
    public const string Manual = "manual";
    public const string Automatic = "automatic";
    public const string Unclassified = "unclassified";
    public static bool IsUnlisted(string? value) => value is null or Reject or Manual or Automatic;
    public static bool IsBlank(string? value) => IsUnlisted(value) || value == Unclassified;
    public static bool HasRequiredCodes(GlManualAutoPolicy policy) =>
        (policy.ManualValues.Count > 0 || policy.UnlistedValueKind == Manual)
        && (policy.AutomaticValues.Count > 0 || policy.UnlistedValueKind == Automatic);
}

public sealed record GlRdeFieldMetadata(
    string FieldId,
    string SourceColumn,
    string Label,
    string ValueType);

/// <summary>
/// GL mapping v2 的正準投影設定。posting、approval、manual/automatic 與 RDE
/// 均由同一份 options 驗證、投影並保存，避免 provider 或 metadata reader 各自解讀。
/// </summary>
public sealed record GlMappingOptions(
    string ApprovalDateMode,
    GlPostingStatusPolicy? PostingStatusPolicy,
    GlManualAutoPolicy ManualAutoPolicy,
    IReadOnlyList<GlRdeFieldMetadata> RdeFields)
{
    public static GlMappingOptions NormalizeLegacy(IReadOnlyDictionary<string, string> mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var approvalDateMode = mapping.TryGetValue(GlMappingKeys.DocDate, out var docDate)
                               && !string.IsNullOrWhiteSpace(docDate)
            ? ApprovalDateModeNames.Mapped
            : ApprovalDateModeNames.Unmapped;

        return new GlMappingOptions(
            approvalDateMode,
            null,
            new GlManualAutoPolicy(["1"], ["0"]),
            []);
    }
}

/// <summary>GL mapping v2 options 的 provider-neutral 正規化與完整驗證。</summary>
public static class GlMappingOptionsRules
{
    public static GlMappingOptions NormalizeAndValidate(
        IReadOnlyDictionary<string, string> mapping,
        IReadOnlyList<string> sourceColumns,
        GlMappingOptions options)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(sourceColumns);
        ArgumentNullException.ThrowIfNull(options);

        if (!ApprovalDateModeNames.IsCanonical(options.ApprovalDateMode))
        {
            throw new ArgumentException(
                "approvalDateMode 必須是 unmapped、mapped 或 sameAsPostDate。",
                nameof(options));
        }

        var approvalMapped = HasMappedColumn(mapping, GlMappingKeys.DocDate);
        if (options.ApprovalDateMode == ApprovalDateModeNames.Mapped && !approvalMapped)
        {
            throw new ArgumentException(
                "approvalDateMode 為 mapped 時必須配對 docDate。",
                nameof(options));
        }

        if (options.ApprovalDateMode != ApprovalDateModeNames.Mapped && approvalMapped)
        {
            throw new ArgumentException(
                $"approvalDateMode 為 {options.ApprovalDateMode} 時不得配對 docDate。",
                nameof(options));
        }

        var manualValues = NormalizeCodes(options.ManualAutoPolicy.ManualValues, "manualValues");
        var automaticValues = NormalizeCodes(options.ManualAutoPolicy.AutomaticValues, "automaticValues");
        var normalizedManualPolicy = options.ManualAutoPolicy with { ManualValues = manualValues, AutomaticValues = automaticValues };
        if (!ManualAutoValueKindNames.IsUnlisted(normalizedManualPolicy.UnlistedValueKind)
            || !ManualAutoValueKindNames.IsBlank(normalizedManualPolicy.BlankValueKind))
            throw new ArgumentException("人工與自動分錄的補集或空白處理設定不正確。", nameof(options));
        if (!ManualAutoValueKindNames.HasRequiredCodes(normalizedManualPolicy))
        {
            throw new ArgumentException(
                "人工與自動代碼各需至少一個非空白值；使用單側清單時，另一側須明確指定為補集。",
                nameof(options));
        }

        var automaticSet = automaticValues.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var overlap = manualValues.FirstOrDefault(automaticSet.Contains);
        if (overlap is not null)
        {
            throw ManualAutoPolicyErrors.Overlap(overlap);
        }

        var sourceSet = sourceColumns.ToHashSet(StringComparer.Ordinal);
        var mappedSourceSet = mapping
            .Where(static pair => !JetFieldCatalog.IsGlLiteralMappingKey(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .Select(static pair => pair.Value)
            .ToHashSet(StringComparer.Ordinal);
        var fieldIds = new HashSet<string>(StringComparer.Ordinal);
        var rdeSources = new HashSet<string>(StringComparer.Ordinal);
        var canonicalRde = new List<GlRdeFieldMetadata>(options.RdeFields.Count);
        foreach (var field in options.RdeFields)
        {
            if (!IsCanonicalFieldId(field.FieldId))
            {
                throw new ArgumentException(
                    "rdeFields.fieldId 必須是後端產生的 rde.<32 lowercase hex> stable ID。",
                    nameof(options));
            }

            if (!fieldIds.Add(field.FieldId))
            {
                throw new ArgumentException($"rdeFields.fieldId 重複：'{field.FieldId}'。", nameof(options));
            }

            if (string.IsNullOrWhiteSpace(field.SourceColumn) || !sourceSet.Contains(field.SourceColumn))
            {
                throw new ArgumentException(
                    $"rdeFields.sourceColumn 不存在於目前來源批次：'{field.SourceColumn}'。",
                    nameof(options));
            }

            if (!rdeSources.Add(field.SourceColumn))
            {
                throw new ArgumentException(
                    $"rdeFields.sourceColumn 重複：'{field.SourceColumn}'。",
                    nameof(options));
            }

            if (mappedSourceSet.Contains(field.SourceColumn))
            {
                throw new ArgumentException(
                    $"RDE 來源欄不得與核心 mapping 重複：'{field.SourceColumn}'。",
                    nameof(options));
            }

            var label = field.Label?.Trim() ?? string.Empty;
            if (label.Length == 0 || label.Length > GlRdeStorageLimits.LabelUtf16CodeUnits)
            {
                throw new ArgumentException(
                    $"rdeFields.label 必須是 1–{GlRdeStorageLimits.LabelUtf16CodeUnits} 個 UTF-16 code units。",
                    nameof(options));
            }

            if (!RdeFieldValueTypeNames.IsCanonical(field.ValueType))
            {
                throw new ArgumentException(
                    "rdeFields.valueType 必須是 text、date 或 money。",
                    nameof(options));
            }

            canonicalRde.Add(field with { Label = label });
        }

        return options with
        {
            ManualAutoPolicy = normalizedManualPolicy,
            RdeFields = canonicalRde
        };
    }

    public static bool IsCanonicalFieldId(string? fieldId) =>
        fieldId is not null
        && fieldId.Length == 36
        && fieldId.StartsWith("rde.", StringComparison.Ordinal)
        && fieldId.AsSpan(4).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static IReadOnlyList<string> NormalizeCodes(
        IReadOnlyList<string> values,
        string fieldName)
    {
        ArgumentNullException.ThrowIfNull(values);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(values.Count);
        foreach (var raw in values)
        {
            var value = raw?.Trim() ?? string.Empty;
            if (value.Length == 0)
            {
                throw new ArgumentException(
                    $"manualAutoPolicy.{fieldName} 不得包含空白代碼。",
                    nameof(values));
            }

            if (seen.Add(value))
            {
                result.Add(value);
            }
        }

        return result;
    }

    private static bool HasMappedColumn(IReadOnlyDictionary<string, string> mapping, string key) =>
        mapping.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);
}

public sealed record GlMappingDraftMetadata(
    IReadOnlyDictionary<string, string> Mapping,
    string AmountMode,
    string ApprovalDateMode,
    GlPostingStatusPolicy? PostingStatusPolicy,
    GlManualAutoPolicy ManualAutoPolicy,
    IReadOnlyList<GlRdeFieldMetadata> RdeFields)
{
    public GlMappingOptions ToOptions() => new(
        ApprovalDateMode,
        PostingStatusPolicy,
        ManualAutoPolicy,
        RdeFields);
}

public sealed record TbMappingDraftMetadata(
    IReadOnlyDictionary<string, string> Mapping,
    string ChangeMode);

public sealed record MappingDraftMetadata(
    int FormatVersion,
    GlMappingDraftMetadata Gl,
    TbMappingDraftMetadata Tb);
