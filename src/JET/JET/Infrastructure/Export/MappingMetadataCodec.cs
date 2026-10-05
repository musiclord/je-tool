using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 兩套報告 writer 與 OpenXML reader 共用的 mapping metadata JSON codec。
/// 固定 marker/version/cell 位置在 Domain；writer 與 reader 都只接受目前的第 2 版。
/// </summary>
internal static class MappingMetadataCodec
{
    private const int ExcelCellTextLimit = 32_767;

    public static string Encode(CommittedMapping gl, CommittedMapping tb)
    {
        EnsureCommittedMapping<GlAmountMode>(
            gl,
            DatasetKind.Gl,
            GlMappingKeys.All,
            GlAmountModeNames.TryParse,
            GlAmountModeNames.ToWireName);
        EnsureCommittedMapping<TbChangeMode>(
            tb,
            DatasetKind.Tb,
            TbMappingKeys.All,
            TbChangeModeNames.TryParse,
            TbChangeModeNames.ToWireName);

        var glOptions = gl.GlOptions ?? GlMappingOptions.NormalizeLegacy(gl.Mapping);
        GlMappingOptionsJsonCodec.Validate(glOptions, gl.Mapping);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("gl");
            writer.WriteStartObject();
            WriteMapping(writer, gl.Mapping, GlMappingKeys.All);
            writer.WriteString("amountMode", gl.ModeName);
            GlMappingOptionsJsonCodec.WriteProperties(writer, glOptions);
            writer.WriteEndObject();
            writer.WritePropertyName("tb");
            writer.WriteStartObject();
            WriteMapping(writer, tb.Mapping, TbMappingKeys.All);
            writer.WriteString("changeMode", tb.ModeName);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        var json = Encoding.UTF8.GetString(stream.ToArray());
        if (json.Length > ExcelCellTextLimit)
        {
            throw new InvalidOperationException("欄位配對 metadata 超過 Excel 單一儲存格上限。");
        }

        return json;
    }

    public static MappingDraftMetadata Decode(int version, string json)
    {
        if (version != MappingMetadataFormat.CurrentVersion)
        {
            throw new MappingMetadataFormatException("mapping metadata 版本不受支援。");
        }

        try
        {
            using var document = JsonDocument.Parse(json, StrictDocumentOptions);
            var root = JsonContractReader.RequireObject(document.RootElement, "root", ["gl", "tb"]);
            var gl = ReadCurrentGl(root.GetProperty("gl"));
            var tb = ReadTb(root.GetProperty("tb"));
            return new MappingDraftMetadata(MappingMetadataFormat.CurrentVersion, gl, tb);
        }
        catch (MappingMetadataFormatException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new MappingMetadataFormatException("mapping metadata JSON 無法解析。", ex);
        }
    }

    private static GlMappingDraftMetadata ReadCurrentGl(JsonElement element)
    {
        var value = JsonContractReader.RequireObject(
            element,
            "gl",
            [
                "mapping",
                "amountMode",
                "approvalDateMode",
                "postingStatusPolicy",
                "manualAutoPolicy",
                "rdeFields"
            ]);
        var mapping = JsonContractReader.ReadMapping(
            value.GetProperty("mapping"),
            "gl.mapping",
            GlMappingKeys.All);
        var amountMode = ReadGlAmountMode(value.GetProperty("amountMode"));
        var options = GlMappingOptionsJsonCodec.ReadProperties(value, mapping);
        return new GlMappingDraftMetadata(
            mapping,
            amountMode,
            options.ApprovalDateMode,
            options.PostingStatusPolicy,
            options.ManualAutoPolicy,
            options.RdeFields);
    }

    private static string ReadGlAmountMode(JsonElement element)
    {
        var mode = JsonContractReader.RequireString(element, "gl.amountMode");
        if (!GlAmountModeNames.TryParse(mode, out var parsed)
            || !string.Equals(mode, GlAmountModeNames.ToWireName(parsed), StringComparison.Ordinal))
        {
            throw new MappingMetadataFormatException("gl.amountMode 不是正準值。");
        }

        return mode;
    }

    private static TbMappingDraftMetadata ReadTb(JsonElement element)
    {
        var value = JsonContractReader.RequireObject(element, "tb", ["mapping", "changeMode"]);
        var mode = JsonContractReader.RequireString(value.GetProperty("changeMode"), "tb.changeMode");
        if (!TbChangeModeNames.TryParse(mode, out var parsed)
            || !string.Equals(mode, TbChangeModeNames.ToWireName(parsed), StringComparison.Ordinal))
        {
            throw new MappingMetadataFormatException("tb.changeMode 不是正準值。");
        }

        return new TbMappingDraftMetadata(
            JsonContractReader.ReadMapping(value.GetProperty("mapping"), "tb.mapping", TbMappingKeys.All),
            mode);
    }

    private static void WriteMapping(
        Utf8JsonWriter writer,
        IReadOnlyDictionary<string, string> mapping,
        IReadOnlyList<string> orderedKeys)
    {
        writer.WritePropertyName("mapping");
        writer.WriteStartObject();
        foreach (var key in orderedKeys)
        {
            if (mapping.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                writer.WriteString(key, value);
            }
        }
        writer.WriteEndObject();
    }

    private static void EnsureCommittedMapping<TMode>(
        CommittedMapping mapping,
        DatasetKind expectedKind,
        IReadOnlyList<string> knownKeys,
        TryParseMode<TMode> tryParse,
        Func<TMode, string> toWireName)
    {
        if (mapping.Kind != expectedKind)
        {
            throw new InvalidOperationException($"mapping metadata dataset kind 應為 {expectedKind}。");
        }
        if (!tryParse(mapping.ModeName, out var parsed)
            || !string.Equals(mapping.ModeName, toWireName(parsed), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{expectedKind} mapping mode 不是正準值。");
        }

        var known = new HashSet<string>(knownKeys, StringComparer.Ordinal);
        foreach (var (key, value) in mapping.Mapping)
        {
            if (!known.Contains(key) || string.IsNullOrWhiteSpace(value)
                || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"{expectedKind} committed mapping 不符合 metadata v2 契約。");
            }
        }
    }

    private static readonly JsonDocumentOptions StrictDocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8
    };

    internal static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private delegate bool TryParseMode<TMode>(string? name, out TMode mode);
}

/// <summary>DB <c>options_json</c> 與 metadata v2 GL options 共用的嚴格 codec。</summary>
internal static class GlMappingOptionsJsonCodec
{
    private static readonly JsonDocumentOptions StrictDocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8
    };

    public static string Encode(GlMappingOptions options, IReadOnlyDictionary<string, string> mapping)
    {
        Validate(options, mapping);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, MappingMetadataCodec.WriterOptions))
        {
            writer.WriteStartObject();
            WriteProperties(writer, options);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static GlMappingOptions Decode(string json, IReadOnlyDictionary<string, string> mapping)
    {
        try
        {
            using var document = JsonDocument.Parse(json, StrictDocumentOptions);
            var root = JsonContractReader.RequireObject(
                document.RootElement,
                "options",
                ["approvalDateMode", "postingStatusPolicy", "manualAutoPolicy", "rdeFields"]);
            return ReadProperties(root, mapping, "options.");
        }
        catch (MappingMetadataFormatException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new MappingMetadataFormatException("mapping options JSON 無法解析。", ex);
        }
    }

    internal static GlMappingOptions ReadProperties(
        JsonElement owner,
        IReadOnlyDictionary<string, string> mapping,
        string pathPrefix = "gl.")
    {
        var approvalDateMode = JsonContractReader.RequireString(
            owner.GetProperty("approvalDateMode"),
            pathPrefix + "approvalDateMode");
        if (!ApprovalDateModeNames.IsCanonical(approvalDateMode))
        {
            throw new MappingMetadataFormatException($"{pathPrefix}approvalDateMode 不是正準值。");
        }

        GlPostingStatusPolicy? postingStatusPolicy = null;
        var postingElement = owner.GetProperty("postingStatusPolicy");
        if (postingElement.ValueKind != JsonValueKind.Null)
        {
            var posting = JsonContractReader.RequireObject(
                postingElement,
                pathPrefix + "postingStatusPolicy",
                ["acceptedValues", "includeBlank"]);
            postingStatusPolicy = new GlPostingStatusPolicy(
                JsonContractReader.ReadStringArray(
                    posting.GetProperty("acceptedValues"),
                    pathPrefix + "postingStatusPolicy.acceptedValues"),
                JsonContractReader.RequireBoolean(
                    posting.GetProperty("includeBlank"),
                    pathPrefix + "postingStatusPolicy.includeBlank"));
        }

        var manual = JsonContractReader.RequireObject(
            owner.GetProperty("manualAutoPolicy"),
            pathPrefix + "manualAutoPolicy",
            ["manualValues", "automaticValues"], ["unlistedValueKind", "blankValueKind"]);
        var manualAutoPolicy = new GlManualAutoPolicy(
            JsonContractReader.ReadStringArray(
                manual.GetProperty("manualValues"),
                pathPrefix + "manualAutoPolicy.manualValues"),
            JsonContractReader.ReadStringArray(
                manual.GetProperty("automaticValues"),
                pathPrefix + "manualAutoPolicy.automaticValues"))
        {
            UnlistedValueKind = manual.TryGetProperty("unlistedValueKind", out var unlisted)
                ? JsonContractReader.RequireString(unlisted, pathPrefix + "manualAutoPolicy.unlistedValueKind") : null,
            BlankValueKind = manual.TryGetProperty("blankValueKind", out var blank)
                ? JsonContractReader.RequireString(blank, pathPrefix + "manualAutoPolicy.blankValueKind") : null
        };

        var rdeFields = ReadRdeFields(owner.GetProperty("rdeFields"), pathPrefix + "rdeFields");
        var options = new GlMappingOptions(
            approvalDateMode,
            postingStatusPolicy,
            manualAutoPolicy,
            rdeFields);
        Validate(options, mapping);
        return options;
    }

    internal static void WriteProperties(Utf8JsonWriter writer, GlMappingOptions options)
    {
        writer.WriteString("approvalDateMode", options.ApprovalDateMode);
        writer.WritePropertyName("postingStatusPolicy");
        if (options.PostingStatusPolicy is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            WriteStringArray(writer, "acceptedValues", options.PostingStatusPolicy.AcceptedValues);
            writer.WriteBoolean("includeBlank", options.PostingStatusPolicy.IncludeBlank);
            writer.WriteEndObject();
        }

        writer.WritePropertyName("manualAutoPolicy");
        writer.WriteStartObject();
        WriteStringArray(writer, "manualValues", options.ManualAutoPolicy.ManualValues);
        WriteStringArray(writer, "automaticValues", options.ManualAutoPolicy.AutomaticValues);
        if (options.ManualAutoPolicy.UnlistedValueKind is { } unlisted) writer.WriteString("unlistedValueKind", unlisted);
        if (options.ManualAutoPolicy.BlankValueKind is { } blank) writer.WriteString("blankValueKind", blank);
        writer.WriteEndObject();

        writer.WritePropertyName("rdeFields");
        writer.WriteStartArray();
        foreach (var field in options.RdeFields)
        {
            writer.WriteStartObject();
            writer.WriteString("fieldId", field.FieldId);
            writer.WriteString("sourceColumn", field.SourceColumn);
            writer.WriteString("label", field.Label);
            writer.WriteString("valueType", field.ValueType);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    internal static void Validate(GlMappingOptions options, IReadOnlyDictionary<string, string> mapping)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(mapping);
        if (!ApprovalDateModeNames.IsCanonical(options.ApprovalDateMode))
        {
            throw new MappingMetadataFormatException("gl.approvalDateMode 不是正準值。");
        }
        var approvalDateMapped = mapping.TryGetValue(GlMappingKeys.DocDate, out var approvalDateColumn)
                                 && !string.IsNullOrWhiteSpace(approvalDateColumn);
        if (options.ApprovalDateMode == ApprovalDateModeNames.Mapped && !approvalDateMapped)
        {
            throw new MappingMetadataFormatException(
                "gl.approvalDateMode 為 mapped 時必須配對 docDate。");
        }
        if (options.ApprovalDateMode != ApprovalDateModeNames.Mapped && approvalDateMapped)
        {
            throw new MappingMetadataFormatException(
                $"gl.approvalDateMode 為 {options.ApprovalDateMode} 時不得配對 docDate。");
        }
        if (options.PostingStatusPolicy is not null)
        {
            ValidateStrings(options.PostingStatusPolicy.AcceptedValues, "gl.postingStatusPolicy.acceptedValues");
            if (options.PostingStatusPolicy.AcceptedValues
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != options.PostingStatusPolicy.AcceptedValues.Count)
            {
                throw new MappingMetadataFormatException(
                    "gl.postingStatusPolicy.acceptedValues 必須以 OrdinalIgnoreCase 唯一。");
            }
        }
        ValidateStrings(options.ManualAutoPolicy.ManualValues, "gl.manualAutoPolicy.manualValues");
        ValidateStrings(options.ManualAutoPolicy.AutomaticValues, "gl.manualAutoPolicy.automaticValues");
        if (!ManualAutoValueKindNames.IsUnlisted(options.ManualAutoPolicy.UnlistedValueKind)
            || !ManualAutoValueKindNames.IsBlank(options.ManualAutoPolicy.BlankValueKind)
            || !ManualAutoValueKindNames.HasRequiredCodes(options.ManualAutoPolicy))
        {
            throw new MappingMetadataFormatException(
                "gl.manualAutoPolicy 的代碼清單、補集或空白處理設定不正確。");
        }
        var manualSet = options.ManualAutoPolicy.ManualValues.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (manualSet.Count != options.ManualAutoPolicy.ManualValues.Count
            || options.ManualAutoPolicy.AutomaticValues.Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != options.ManualAutoPolicy.AutomaticValues.Count
            || options.ManualAutoPolicy.AutomaticValues.Any(manualSet.Contains))
        {
            throw new MappingMetadataFormatException(
                "gl.manualAutoPolicy 的代碼必須各自唯一且 manual/automatic 不得重疊。");
        }
        var fieldIds = new HashSet<string>(StringComparer.Ordinal);
        var rdeSources = new HashSet<string>(StringComparer.Ordinal);
        var mappedSources = mapping
            .Where(static pair => !JetFieldCatalog.IsGlLiteralMappingKey(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .Select(static pair => pair.Value)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var field in options.RdeFields)
        {
            if (!GlMappingOptionsRules.IsCanonicalFieldId(field.FieldId))
            {
                throw new MappingMetadataFormatException("gl.rdeFields.fieldId 不是正準 stable ID。");
            }
            if (!fieldIds.Add(field.FieldId))
            {
                throw new MappingMetadataFormatException("gl.rdeFields.fieldId 不得重複。");
            }
            ValidateString(field.SourceColumn, "gl.rdeFields.sourceColumn");
            ValidateString(field.Label, "gl.rdeFields.label");
            if (field.Label.Length > GlRdeStorageLimits.LabelUtf16CodeUnits)
            {
                throw new MappingMetadataFormatException(
                    $"gl.rdeFields.label 不得超過 {GlRdeStorageLimits.LabelUtf16CodeUnits} 個 UTF-16 code units。");
            }
            if (!rdeSources.Add(field.SourceColumn) || mappedSources.Contains(field.SourceColumn))
            {
                throw new MappingMetadataFormatException(
                    "gl.rdeFields.sourceColumn 不得重複或與核心 mapping 共用。");
            }
            if (!RdeFieldValueTypeNames.IsCanonical(field.ValueType))
            {
                throw new MappingMetadataFormatException("gl.rdeFields.valueType 不是正準值。");
            }
        }

        var postingMapped = mapping.TryGetValue(GlMappingKeys.PostingStatus, out var postingColumn)
                            && !string.IsNullOrWhiteSpace(postingColumn);
        if (!postingMapped && options.PostingStatusPolicy is not null)
        {
            throw new MappingMetadataFormatException(
                "未配對 postingStatus 時不得提供 gl.postingStatusPolicy。");
        }
        if (postingMapped
            && (options.PostingStatusPolicy is null
                || (options.PostingStatusPolicy.AcceptedValues.Count == 0
                    && !options.PostingStatusPolicy.IncludeBlank)))
        {
            throw new MappingMetadataFormatException("已配對 postingStatus 時必須提供有效 postingStatusPolicy。");
        }
    }

    private static IReadOnlyList<GlRdeFieldMetadata> ReadRdeFields(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new MappingMetadataFormatException($"{path} 必須是 array。");
        }

        var result = new List<GlRdeFieldMetadata>();
        foreach (var item in element.EnumerateArray())
        {
            var value = JsonContractReader.RequireObject(
                item,
                path + "[]",
                ["fieldId", "sourceColumn", "label", "valueType"]);
            result.Add(new GlRdeFieldMetadata(
                JsonContractReader.RequireString(value.GetProperty("fieldId"), path + "[].fieldId"),
                JsonContractReader.RequireString(value.GetProperty("sourceColumn"), path + "[].sourceColumn"),
                JsonContractReader.RequireString(value.GetProperty("label"), path + "[].label"),
                JsonContractReader.RequireString(value.GetProperty("valueType"), path + "[].valueType")));
        }

        return result;
    }

    private static void WriteStringArray(Utf8JsonWriter writer, string propertyName, IReadOnlyList<string> values)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }
        writer.WriteEndArray();
    }

    private static void ValidateStrings(IReadOnlyList<string> values, string path)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var value in values)
        {
            ValidateString(value, path + "[]");
        }
    }

    private static void ValidateString(string? value, string path)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new MappingMetadataFormatException($"{path} 必須是無前後空白的非空字串。");
        }
    }

}

internal static class JsonContractReader
{
    internal static IReadOnlyDictionary<string, string> ReadMapping(
        JsonElement element,
        string path,
        IReadOnlyList<string> allowedKeys)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new MappingMetadataFormatException($"{path} 必須是 object。");
        }

        var allowed = new HashSet<string>(allowedKeys, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new MappingMetadataFormatException($"{path} 含重複 logical key。");
            }
            if (!allowed.Contains(property.Name))
            {
                throw new MappingMetadataFormatException($"{path} 含未知 logical key。");
            }

            mapping[property.Name] = RequireString(property.Value, $"{path}.{property.Name}");
        }

        return mapping;
    }

    internal static JsonElement RequireObject(
        JsonElement element,
        string path,
        IReadOnlyCollection<string> requiredProperties,
        IReadOnlyCollection<string>? optionalProperties = null)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new MappingMetadataFormatException($"{path} 必須是 object。");
        }

        var required = new HashSet<string>(requiredProperties, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new MappingMetadataFormatException($"{path} 含重複欄位。");
            }
            if (!required.Contains(property.Name) && !(optionalProperties?.Contains(property.Name) ?? false))
            {
                throw new MappingMetadataFormatException($"{path} 含未知欄位。");
            }
        }

        if (!required.IsSubsetOf(seen))
        {
            throw new MappingMetadataFormatException($"{path} 缺少必要欄位。");
        }

        return element;
    }

    internal static string RequireString(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw new MappingMetadataFormatException($"{path} 必須是非空字串。");
        }

        var value = element.GetString()!;
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new MappingMetadataFormatException($"{path} 不得含前後空白。");
        }

        return value;
    }

    internal static bool RequireBoolean(JsonElement element, string path)
    {
        if (element.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new MappingMetadataFormatException($"{path} 必須是 boolean。");
        }

        return element.GetBoolean();
    }

    internal static IReadOnlyList<string> ReadStringArray(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new MappingMetadataFormatException($"{path} 必須是 array。");
        }

        return element.EnumerateArray()
            .Select((item, index) => RequireString(item, $"{path}[{index}]"))
            .ToArray();
    }
}

internal sealed class MappingMetadataFormatException : Exception
{
    public MappingMetadataFormatException(string message) : base(message) { }
    public MappingMetadataFormatException(string message, Exception innerException) : base(message, innerException) { }
}
