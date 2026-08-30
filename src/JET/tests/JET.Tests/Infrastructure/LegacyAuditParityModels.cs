namespace JET.Tests.Infrastructure;

internal enum LegacyAuditParityProvider
{
    Sqlite,
    DuckDb,
    SqlServer,
}

internal enum LegacyReportKind
{
    ValidationReport,
    AccountMapping,
    InfReport,
    PrescreenReport,
    CriteriaSelectionReport,
    WorkingPaper,
}

internal enum LegacyAuditParityCheckpoint
{
    Profile,
    Import,
    Mapping,
    Validate,
    Prescreen,
    Filter,
    Export,
}

internal sealed record LegacyAuditParityRunSelection(
    IReadOnlyList<LegacyAuditParityProvider> Providers,
    LegacyAuditParityCheckpoint StopAfter,
    bool KeepOutputs)
{
    internal const string ProviderEnvironmentVariable = "JET_LEGACY_PARITY_PROVIDER";
    internal const string StopAfterEnvironmentVariable = "JET_LEGACY_PARITY_STOP_AFTER";

    internal static LegacyAuditParityRunSelection FromEnvironment() => Parse(
        Environment.GetEnvironmentVariable(ProviderEnvironmentVariable),
        Environment.GetEnvironmentVariable(StopAfterEnvironmentVariable),
        Environment.GetEnvironmentVariable(LegacyParityWorkspace.KeepOutputsEnvironmentVariableName));

    internal static LegacyAuditParityRunSelection Parse(
        string? provider,
        string? stopAfter,
        string? keepOutputs)
    {
        var providers = ParseProviders(provider);
        var checkpoint = ParseCheckpoint(stopAfter);
        var keep = LegacyParityWorkspace.ParseKeepOutputs(keepOutputs);
        return new LegacyAuditParityRunSelection(providers, checkpoint, keep);
    }

    private static IReadOnlyList<LegacyAuditParityProvider> ParseProviders(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || string.Equals(value.Trim(), "all", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                LegacyAuditParityProvider.Sqlite,
                LegacyAuditParityProvider.DuckDb,
                LegacyAuditParityProvider.SqlServer,
            ];
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "sqlite" => [LegacyAuditParityProvider.Sqlite],
            "duckdb" => [LegacyAuditParityProvider.DuckDb],
            "sqlserver" or "sql-server" => [LegacyAuditParityProvider.SqlServer],
            _ => throw new ArgumentException(
                "Legacy parity provider must be sqlite, duckdb, sqlserver, or all.",
                nameof(value)),
        };
    }

    private static LegacyAuditParityCheckpoint ParseCheckpoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return LegacyAuditParityCheckpoint.Export;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "profile" => LegacyAuditParityCheckpoint.Profile,
            "import" => LegacyAuditParityCheckpoint.Import,
            "mapping" => LegacyAuditParityCheckpoint.Mapping,
            "validate" or "validation" => LegacyAuditParityCheckpoint.Validate,
            "prescreen" => LegacyAuditParityCheckpoint.Prescreen,
            "filter" => LegacyAuditParityCheckpoint.Filter,
            "export" => LegacyAuditParityCheckpoint.Export,
            _ => throw new ArgumentException(
                "Legacy parity stop-after checkpoint must be profile, import, mapping, validate, prescreen, filter, or export.",
                nameof(value)),
        };
    }
}
