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
        if (gl is null || gl.FormatVersion != MappingMetadataFormat.CurrentVersion)
        {
            throw new JetActionException(
                JetErrorCodes.StaleResult,
                "正式報表需要目前已確認的 GL mapping v2，請回到欄位配對後重新產出。");
        }

        var tb = await mappingStore.FindAsync(
            projectId,
            DatasetKind.Tb,
            cancellationToken).ConfigureAwait(false);
        if (tb is { FormatVersion: not MappingMetadataFormat.CurrentVersion })
        {
            throw new JetActionException(
                JetErrorCodes.StaleResult,
                "TB 欄位配對需要更新，請回第三步重新確認欄位配對後再匯出報表。");
        }

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
            throw new JetActionException(JetErrorCodes.StaleResult, exception.Message);
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
