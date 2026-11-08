using System.Text.Json.Serialization;
using JET.Tests.Application;

namespace JET.Tests.Infrastructure;

internal sealed class PrivateCaseJourneyResult
{
    internal PrivateCaseJourneyResult(
        LegacyAuditParityJourneyResult result,
        PrivateCaseScenarioDiagnosticFacts? scenarioDiagnosticFacts,
        LegacyAuditParityFollowUp? followUp = null)
    {
        FollowUp = followUp;
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
        HighRiskSummaryCaptured = result.HighRiskSummaryCaptured;
        ReportCount = result.Reports.Count;
        ActionNames = result.ActionNames;
        Elapsed = result.Elapsed;
        Reports = result.Reports;
        ProjectId = result.ProjectId;
        InfVerification = result.InfVerificationFacts is null
            ? null
            : PrivateCaseInfVerifier.Verify(
                result.InfVerificationFacts,
                PrivateCaseAcceptancePolicy.Current);
        ScenarioVerificationFacts = result.ScenarioVerificationFacts;
        ScenarioDiagnosticFacts = scenarioDiagnosticFacts;
    }

    /// <summary>第一次流程是 null；後續操作那一段的結果寫明是哪一段。</summary>
    public LegacyAuditParityFollowUp? FollowUp { get; }

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

    public bool HighRiskSummaryCaptured { get; }

    public int ReportCount { get; }

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
    internal IReadOnlyList<LegacyAuditParityJourneyReport> Reports { get; }

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
        CancellationToken cancellationToken = default,
        IReadOnlyList<LegacyAuditParityFollowUp>? followUps = null,
        Func<PrivateCaseJourneyResult, Task>? afterEachExport = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(prepared);
        followUps ??= [];
        // 每一段匯出都會覆蓋前一段的同名報告，所以要由呼叫端在下一段開始前取走並比對；
        // 後續操作只接在完整匯出之後。
        if (followUps.Count > 0
            && (stopAfter != LegacyAuditParityCheckpoint.Export || afterEachExport is null))
        {
            throw new ArgumentException("後續操作只能接在完整匯出之後，並且要提供每段匯出後的比對。", nameof(followUps));
        }

        var input = prepared.Input;
        var journeyInput = new LegacyAuditParityJourneyInput(
            input.CaseAlias,
            input.ProjectCode,
            input.ClientName,
            input.OperatorId,
            input.PeriodStart,
            input.PeriodEnd,
            input.PrepStartDate,
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
        Task<PrivateCaseScenarioDiagnosticFacts> CaptureDiagnosticsAsync() =>
            PrivateCaseScenarioDiagnostics.CaptureAsync(
                input.FilterScenarios,
                input.MakeupDates,
                (action, payload) => host.DispatchAsync(action, payload, cancellationToken));

        Func<LegacyAuditParityJourneyResult, Task>? afterJourney = stopAfter is
            LegacyAuditParityCheckpoint.Filter or LegacyAuditParityCheckpoint.Export
                ? async completed =>
                {
                    RequireNoLegacyObservation(completed);
                    diagnostics = await CaptureDiagnosticsAsync().ConfigureAwait(false);
                    if (afterEachExport is null)
                    {
                        return;
                    }

                    // 第一次與每一段後續操作都在下一段開始前比對，且都在 SQL Server 清理之前完成。
                    await afterEachExport(new PrivateCaseJourneyResult(completed, diagnostics))
                        .ConfigureAwait(false);
                    foreach (var followUp in followUps)
                    {
                        var segment = await LegacyAuditParityJourney.RunFollowUpAsync(
                            host,
                            journeyInput,
                            provider,
                            completed.ProjectId!,
                            followUp,
                            cancellationToken).ConfigureAwait(false);
                        var segmentDiagnostics = await CaptureDiagnosticsAsync().ConfigureAwait(false);
                        await afterEachExport(new PrivateCaseJourneyResult(segment, segmentDiagnostics, followUp))
                            .ConfigureAwait(false);
                    }
                }
                : null;

        var result = await LegacyAuditParityJourney.RunAsync(
            host,
            journeyInput,
            provider,
            stopAfter,
            cancellationToken,
            afterJourney);
        RequireNoLegacyObservation(result);
        return new PrivateCaseJourneyResult(result, diagnostics);
    }

    private static void RequireNoLegacyObservation(LegacyAuditParityJourneyResult result)
    {
        if (result.Observation is not null)
        {
            throw new InvalidOperationException(
                "PrivateCase 不得套用 case-A 或 case-B 的舊案件觀察結果。");
        }
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
