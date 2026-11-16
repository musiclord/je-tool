using System.Globalization;

namespace JET.Domain;

/// <summary>
/// 六份正式報表共用、藏在工作簿裡的 metadata。內容只有少量的案件設定、科目分類的 revision
/// 與目前已確認的欄位配對快照，不帶任何 GL 或 TB 資料列。
/// </summary>
internal sealed record ReportWorkbookMetadata(
    string PeriodStart,
    string PeriodEnd,
    int CategoryRevision,
    CommittedMapping GlMapping,
    CommittedMapping? TbMapping);

internal static class ReportWorkbookMetadataFormat
{
    public const string WorksheetName = "JET_Metadata";
    public const string Marker = "JET_REPORT_METADATA";
    public const int CurrentVersion = 1;
    public const int MaximumChunkLength = 30_000;
}

/// <summary>AuditCore 排計畫與 Infrastructure 編碼、解碼 metadata 時共用的檢查；內容不合規定就丟例外，不繼續。</summary>
internal static class ReportWorkbookMetadataInvariant
{
    public static void Validate(ReportWorkbookMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (!DateOnly.TryParseExact(metadata.PeriodStart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !DateOnly.TryParseExact(metadata.PeriodEnd, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
            || start > end)
        {
            throw new ArgumentException("Report workbook metadata 的期間不是正準 ISO date range。", nameof(metadata));
        }
        if (metadata.CategoryRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(metadata), "Report workbook metadata 缺少科目分類 revision。");
        }

        ValidateMapping(metadata.GlMapping, DatasetKind.Gl);
        if (metadata.TbMapping is not null)
        {
            ValidateMapping(metadata.TbMapping, DatasetKind.Tb);
        }

        var options = metadata.GlMapping.GlOptions
            ?? GlMappingOptions.NormalizeLegacy(metadata.GlMapping.Mapping);
        if (!ApprovalDateModeNames.IsCanonical(options.ApprovalDateMode))
        {
            throw new ArgumentException("Report workbook metadata 的 approval provenance 不是正準值。", nameof(metadata));
        }

        var postingMapped = metadata.GlMapping.Mapping.TryGetValue(
                GlMappingKeys.PostingStatus,
                out var postingColumn)
            && !string.IsNullOrWhiteSpace(postingColumn);
        if (postingMapped != (options.PostingStatusPolicy is not null))
        {
            throw new ArgumentException("Report workbook metadata 的 posting-status policy 與 mapping 不一致。", nameof(metadata));
        }
    }

    public static IReadOnlyList<GlRdeFieldMetadata> ValidateCustomFields(
        ReportWorkbookMetadata metadata,
        IReadOnlyList<GlRdeFieldMetadata>? fields)
    {
        Validate(metadata);
        var selected = fields ?? Array.Empty<GlRdeFieldMetadata>();
        var configured = (metadata.GlMapping.GlOptions
                          ?? GlMappingOptions.NormalizeLegacy(metadata.GlMapping.Mapping))
            .RdeFields;
        var byId = configured.ToDictionary(field => field.FieldId, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var previousOrdinal = -1;
        foreach (var field in selected)
        {
            if (!byId.TryGetValue(field.FieldId, out var current)
                || current != field
                || !seen.Add(field.FieldId))
            {
                throw new ArgumentException(
                    $"Report workbook custom field '{field.FieldId}' 不是目前 committed RDE metadata。",
                    nameof(fields));
            }

            var ordinal = IndexOf(configured, field.FieldId);
            if (ordinal <= previousOrdinal)
            {
                throw new ArgumentException("Report workbook custom fields 必須依 mapping ordinal 唯一排序。", nameof(fields));
            }
            previousOrdinal = ordinal;
        }

        return Array.AsReadOnly(selected.ToArray());
    }

    private static void ValidateMapping(CommittedMapping mapping, DatasetKind expectedKind)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        if (mapping.Kind != expectedKind
            || string.IsNullOrWhiteSpace(mapping.SourceBatchId))
        {
            throw new ArgumentException(
                $"Report workbook metadata 缺少目前 {expectedKind} mapping v2 snapshot。",
                nameof(mapping));
        }
    }

    private static int IndexOf(IReadOnlyList<GlRdeFieldMetadata> fields, string fieldId)
    {
        for (var index = 0; index < fields.Count; index++)
        {
            if (string.Equals(fields[index].FieldId, fieldId, StringComparison.Ordinal))
            {
                return index;
            }
        }
        return -1;
    }
}
