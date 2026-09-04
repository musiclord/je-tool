namespace JET.Tests.Infrastructure;

internal static class LegacyAuditParityJourneyContract
{
    private static readonly HashSet<string> PaginatedActions =
    [
        "query.infSamplePage",
        "query.prescreenPage",
    ];

    internal static IReadOnlyList<string> FullActionOrder { get; } =
    [
        "project.create",
        "import.gl.fromFile",
        "import.tb.fromFile",
        "import.accountMapping.fromFile",
        "import.authorizedPreparer.fromFile",
        "import.holiday.fromFile",
        "import.makeupDay.fromFile",
        "mapping.commit.gl",
        "mapping.commit.tb",
        "validate.run",
        "query.infSamplePage",
        "export.validationArtifacts",
        "export.accountMappingTemplate",
        "prescreen.run",
        "query.prescreenPage",
        "query.prescreenPage",
        "export.prescreenReport",
        "filter.commit",
        "query.tagMatrixScenarios",
        "export.criteriaSelectionReport",
        "export.workpaperStream",
    ];

    internal static IReadOnlyList<string> DecidedNotProvidedFullActionOrder { get; } =
        FullActionOrder
            .Where(static action => action != "import.authorizedPreparer.fromFile")
            .ToArray();

    internal static IReadOnlyList<string> NormalizePaginatedActionOrder(
        IReadOnlyList<string> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var normalized = new List<string>(actions.Count);
        foreach (var action in actions)
        {
            if (normalized.Count > 0
                && PaginatedActions.Contains(action)
                && string.Equals(normalized[^1], action, StringComparison.Ordinal))
            {
                continue;
            }
            normalized.Add(action);
        }
        return normalized;
    }
}
