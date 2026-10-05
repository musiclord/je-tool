using JET.Domain;

namespace JET.Application;

internal static class ReportWorkbookMetadataFactory
{
    public static async Task<ReportWorkbookMetadata> LoadAsync(
        string projectId,
        ProjectDocument document,
        IMappingStateStore mappingStore,
        IAccountTaxonomyStore taxonomyStore,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(mappingStore);
        ArgumentNullException.ThrowIfNull(taxonomyStore);

        var gl = await mappingStore.FindAsync(
            projectId,
            DatasetKind.Gl,
            cancellationToken).ConfigureAwait(false);
        if (gl is null)
        {
            throw new JetActionException(
                JetErrorCodes.StaleResult,
                "正式報表需要目前已確認配對的 GL 欄位，請回第三步按「確認配對」後再匯出報表。");
        }

        var tb = await mappingStore.FindAsync(
            projectId,
            DatasetKind.Tb,
            cancellationToken).ConfigureAwait(false);

        var taxonomy = await taxonomyStore.ReadAsync(projectId, cancellationToken).ConfigureAwait(false);
        var metadata = new ReportWorkbookMetadata(
            document.PeriodStart,
            document.PeriodEnd,
            taxonomy.Revision,
            NormalizeGl(gl),
            tb);
        try
        {
            ReportWorkbookMetadataInvariant.Validate(metadata);
        }
        catch (ArgumentException exception)
        {
            throw new JetActionException(
                JetErrorCodes.StaleResult,
                "案件的查核期間、科目分類或欄位配對已和報表需要的資料不一致，無法匯出。請回第三步按「確認配對」，再到第六步匯出底稿。",
                innerException: exception);
        }

        return metadata;
    }

    public static IReadOnlyList<GlRdeFieldMetadata> AllRdeFields(ReportWorkbookMetadata metadata)
    {
        ReportWorkbookMetadataInvariant.Validate(metadata);
        var fields = (metadata.GlMapping.GlOptions
                      ?? GlMappingOptions.NormalizeLegacy(metadata.GlMapping.Mapping))
            .RdeFields;
        return ReportWorkbookMetadataInvariant.ValidateCustomFields(metadata, fields);
    }

    private static CommittedMapping NormalizeGl(CommittedMapping mapping) =>
        mapping.GlOptions is not null
            ? mapping
            : mapping with { GlOptions = GlMappingOptions.NormalizeLegacy(mapping.Mapping) };
}
