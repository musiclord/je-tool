using System.Text.Json.Serialization;
using JET.Tests.Application;

namespace JET.Tests.Infrastructure;

internal sealed class PrivateCaseJourneyResult
{
    internal PrivateCaseJourneyResult(
        LegacyAuditParityJourneyResult result,
        PrivateCaseScenarioDiagnosticFacts? scenarioDiagnosticFacts)
    {
        CaseAlias = result.CaseAlias;
        Provider = result.Provider;
        CompletedCheckpoint = result.CompletedCheckpoint;
        GlImportedRowCount = result.GlImportedRowCount;
        TbImportedRowCount = result.TbImportedRowCount;
        GlProjectedRowCount = result.GlProjectedRowCount;
        TbProjectedRowCount = result.TbProjectedRowCount;
        ValidationCaptured = result.ValidationCaptured;
        PrescreenCaptured = result.PrescreenCaptured;
        FilterCaptured = result.FilterCaptured;
        TagMatrixCaptured = result.TagMatrixCaptured;
        ArtifactCount = result.Artifacts.Count;
        ActionNames = result.ActionNames;
        Elapsed = result.Elapsed;
        Artifacts = result.Artifacts;
        ProjectId = result.ProjectId;
        InfVerification = result.InfVerificationFacts is null
            ? null
            : PrivateCaseInfVerifier.Verify(
                result.InfVerificationFacts,
                PrivateCaseAcceptancePolicy.Current);
        ScenarioVerificationFacts = result.ScenarioVerificationFacts;
        ScenarioDiagnosticFacts = scenarioDiagnosticFacts;
    }

    public string CaseAlias { get; }

    public LegacyAuditParityProvider Provider { get; }

    public LegacyAuditParityCheckpoint CompletedCheckpoint { get; }

    public long? GlImportedRowCount { get; }

    public long? TbImportedRowCount { get; }

    public long? GlProjectedRowCount { get; }

    public long? TbProjectedRowCount { get; }

    public bool ValidationCaptured { get; }

    public bool PrescreenCaptured { get; }

    public bool FilterCaptured { get; }

    public bool TagMatrixCaptured { get; }

    public int ArtifactCount { get; }

    public IReadOnlyList<string> ActionNames { get; }

    public TimeSpan Elapsed { get; }

    public PrivateCaseInfVerificationResult? InfVerification { get; }

    [JsonIgnore]
    internal PrivateCaseScenarioVerificationFacts? ScenarioVerificationFacts { get; }

    [JsonIgnore]
    internal PrivateCaseScenarioDiagnosticFacts? ScenarioDiagnosticFacts { get; }

    [JsonIgnore]
    internal string? ProjectId { get; }

    [JsonIgnore]
    internal IReadOnlyList<LegacyAuditParityJourneyArtifact> Artifacts { get; }

    public override string ToString() =>
        $"private case journey result ({CaseAlias}, {Provider}, {CompletedCheckpoint})";
}

internal static class PrivateCaseJourney
{
    internal static async Task<PrivateCaseJourneyResult> RunAsync(
        HandlerTestHost host,
        PrivateCasePreparedJourneyInput prepared,
        LegacyAuditParityProvider provider,
        LegacyAuditParityCheckpoint stopAfter,
        string? sqlServerConnectionString = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(prepared);

        var input = prepared.Input;
        var journeyInput = new LegacyAuditParityJourneyInput(
            input.CaseAlias,
            input.ProjectCode,
            input.EntityName,
            input.OperatorId,
            input.PeriodStart,
            input.PeriodEnd,
            input.LastPeriodStart,
            input.SampleSeed,
            input.GlSources.Select(ConvertSource).ToArray(),
            "replace",
            input.GlMapping,
            input.GlAmountMode,
            input.TbSources.Select(ConvertSource).ToArray(),
            "replace",
            input.TbMapping,
            input.TbChangeMode,
            ConvertReference(input.AccountMappingFile),
            input.AuthorizedPreparerFile is null
                ? null
                : ConvertReference(input.AuthorizedPreparerFile),
            ConvertReference(input.HolidayFile),
            ConvertReference(input.MakeupDayFile),
            input.FilterScenarios,
            LegacyFilterScenarioIds: [],
            ObservationCase: null);

        PrivateCaseScenarioDiagnosticFacts? diagnostics = null;
        Func<Task>? captureDiagnostics = stopAfter is
            LegacyAuditParityCheckpoint.Filter or LegacyAuditParityCheckpoint.Export
                ? async () =>
                {
                    diagnostics = await PrivateCaseScenarioDiagnostics.CaptureAsync(
                        input.FilterScenarios,
                        input.MakeupDates,
                        (action, payload) => host.DispatchAsync(action, payload, cancellationToken))
                        .ConfigureAwait(false);
                }
                : null;

        var result = await LegacyAuditParityJourney.RunAsync(
            host,
            journeyInput,
            provider,
            stopAfter,
            sqlServerConnectionString,
            cancellationToken,
            captureDiagnostics);
        if (result.Observation is not null)
        {
            throw new InvalidOperationException(
                "PrivateCase 不得套用 case-A 或 case-B 的舊案件觀察結果。");
        }
        return new PrivateCaseJourneyResult(result, diagnostics);
    }

    private static LegacyAuditParityImportSource ConvertSource(
        PrivateCaseJourneyImportSource source) => new(
            source.FilePath,
            source.FileName,
            source.WorksheetName);

    private static LegacyAuditParityReferenceFile ConvertReference(
        PrivateCaseJourneyReferenceFile reference) => new(
            reference.FilePath,
            reference.FileName);
}
