using JET.Domain;

namespace JET.Application;

internal static class FormalReportRdeFieldSelection
{
    public static IReadOnlyList<GlRdeFieldMetadata> ForScenarios(
        IReadOnlyList<SavedFilterScenario> scenarios,
        ReportWorkbookMetadata workbookMetadata,
        int moneyScale)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        ReportWorkbookMetadataInvariant.Validate(workbookMetadata);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scenario in scenarios)
        {
            foreach (var field in ResultPageColumnRegistry.ForFilter(
                         scenario,
                         workbookMetadata.GlMapping,
                         moneyScale).CustomFields)
            {
                referenced.Add(field.FieldId);
            }
        }

        var registry = (workbookMetadata.GlMapping.GlOptions
                        ?? GlMappingOptions.NormalizeLegacy(workbookMetadata.GlMapping.Mapping))
            .RdeFields;
        return ReportWorkbookMetadataInvariant.ValidateCustomFields(
            workbookMetadata,
            registry.Where(field => referenced.Contains(field.FieldId)).ToArray());
    }
}
