using JET.Domain;

namespace JET.Infrastructure;

public sealed partial class WorkpaperWriter
{
    private async Task<IReadOnlyDictionary<long, IReadOnlyDictionary<string, string?>>> ReadWorkpaperRdeValuesAsync(
        string projectId,
        IReadOnlyList<long> entryIds,
        ReportWorkbookMetadata? workbookMetadata,
        IReadOnlyList<GlRdeFieldMetadata> selectedFields,
        int moneyScale,
        CancellationToken cancellationToken)
    {
        ResultPageRdeValueBatch.Validate(entryIds);
        if (selectedFields.Count == 0)
        {
            return entryIds.ToDictionary(
                static entryId => entryId,
                static _ => (IReadOnlyDictionary<string, string?>)
                    new Dictionary<string, string?>(StringComparer.Ordinal));
        }

        if (workbookMetadata is null || resultPageRdeValues is null)
        {
            throw new InvalidOperationException(
                "Formal WorkingPaper RDE projection requires workbook metadata and the result-page RDE values port.");
        }

        var registry = (workbookMetadata.GlMapping.GlOptions
                        ?? GlMappingOptions.NormalizeLegacy(workbookMetadata.GlMapping.Mapping))
            .RdeFields;
        var values = await resultPageRdeValues.ReadAsync(
            projectId,
            entryIds,
            cancellationToken).ConfigureAwait(false);
        return FormalReportRdeValueRenderer.Render(
            entryIds,
            values,
            registry,
            selectedFields,
            moneyScale);
    }
}
