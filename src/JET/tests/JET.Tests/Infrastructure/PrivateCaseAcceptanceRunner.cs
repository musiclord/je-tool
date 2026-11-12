namespace JET.Tests.Infrastructure;

internal enum PrivateCaseAcceptanceFailure
{
    InvalidInput,
    IncompleteJourney,
}

internal sealed class PrivateCaseAcceptanceException : InvalidOperationException
{
    internal PrivateCaseAcceptanceException(PrivateCaseAcceptanceFailure failure)
        : base($"私人案件驗收無法完成（{failure}）。")
    {
        Failure = failure;
    }

    internal PrivateCaseAcceptanceFailure Failure { get; }
}

internal sealed class PrivateCaseAcceptanceResult
{
    internal PrivateCaseAcceptanceResult(
        string caseAlias,
        LegacyAuditParityProvider provider,
        int reportCount,
        PrivateCaseAcceptancePolicy policy,
        PrivateCaseLegacyScenarioEvidenceResult legacyScenarioEvidence,
        PrivateCaseInfVerificationResult infVerification,
        PrivateCaseScenarioVerificationResult scenarioVerification,
        PrivateCaseScenarioPopulationComparisonResult scenarioPopulationComparison,
        PrivateCaseReportComparisonResult reportComparison,
        IReadOnlyList<PrivateCaseAcceptanceFollowUpResult> followUps)
    {
        FollowUps = followUps;
        CaseAlias = caseAlias;
        Provider = provider;
        ReportCount = reportCount;
        Policy = policy;
        LegacyScenarioEvidence = legacyScenarioEvidence;
        InfVerification = infVerification;
        ScenarioVerification = scenarioVerification;
        ScenarioPopulationComparison = scenarioPopulationComparison;
        ReportComparison = reportComparison;
    }

    public string CaseAlias { get; }

    public LegacyAuditParityProvider Provider { get; }

    public int ReportCount { get; }

    public PrivateCaseAcceptancePolicy Policy { get; }

    public PrivateCaseLegacyScenarioEvidenceResult LegacyScenarioEvidence { get; }

    public PrivateCaseInfVerificationResult InfVerification { get; }

    public PrivateCaseScenarioVerificationResult ScenarioVerification { get; }

    public PrivateCaseScenarioPopulationComparisonResult ScenarioPopulationComparison { get; }

    public PrivateCaseReportComparisonResult ReportComparison { get; }

    /// <summary>第一次比對之後，同一個案件依序做的三段後續操作各自的比對結果。</summary>
    public IReadOnlyList<PrivateCaseAcceptanceFollowUpResult> FollowUps { get; }

    public bool Passed => ReportCount == Enum.GetValues<LegacyReportKind>().Length
        && LegacyScenarioEvidence.Passed
        && InfVerification.Passed
        && ScenarioVerification.Passed
        && ReportComparison.Passed
        && FollowUps.Select(static item => item.FollowUp)
            .SequenceEqual(PrivateCaseAcceptanceRunner.FollowUps)
        && FollowUps.All(static item => item.Passed);

    public override string ToString() =>
        $"private case acceptance ({CaseAlias}, {Provider})";
}

/// <summary>
/// 一段後續操作重新匯出後的比對。規則、政策與內容裁決和第一次完全相同，通過條件也相同。
/// </summary>
internal sealed class PrivateCaseAcceptanceFollowUpResult
{
    internal PrivateCaseAcceptanceFollowUpResult(
        LegacyAuditParityFollowUp followUp,
        int reportCount,
        PrivateCaseInfVerificationResult infVerification,
        PrivateCaseScenarioVerificationResult scenarioVerification,
        PrivateCaseScenarioPopulationComparisonResult scenarioPopulationComparison,
        PrivateCaseReportComparisonResult reportComparison)
    {
        FollowUp = followUp;
        ReportCount = reportCount;
        InfVerification = infVerification;
        ScenarioVerification = scenarioVerification;
        ScenarioPopulationComparison = scenarioPopulationComparison;
        ReportComparison = reportComparison;
    }

    public LegacyAuditParityFollowUp FollowUp { get; }

    public int ReportCount { get; }

    public PrivateCaseInfVerificationResult InfVerification { get; }

    public PrivateCaseScenarioVerificationResult ScenarioVerification { get; }

    public PrivateCaseScenarioPopulationComparisonResult ScenarioPopulationComparison { get; }

    public PrivateCaseReportComparisonResult ReportComparison { get; }

    public bool Passed => ReportCount == Enum.GetValues<LegacyReportKind>().Length
        && InfVerification.Passed
        && ScenarioVerification.Passed
        && ReportComparison.Passed;

    public override string ToString() => $"private case follow-up acceptance ({FollowUp})";
}

internal static class PrivateCaseAcceptanceRunner
{
    /// <summary>
    /// 第一次流程通過比對後，在同一個案件依序做的審計員操作。
    /// </summary>
    internal static IReadOnlyList<LegacyAuditParityFollowUp> FollowUps { get; } =
    [
        LegacyAuditParityFollowUp.Reopen,
        LegacyAuditParityFollowUp.MappingRecommit,
        LegacyAuditParityFollowUp.Reimport,
    ];

    internal static Task<PrivateCaseAcceptanceResult> RunAsync(
        string authorizedRootPath,
        string manifestRelativePath,
        LegacyAuditParityProvider provider,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(authorizedRootPath)
            || !System.IO.Path.IsPathFullyQualified(authorizedRootPath)
            || string.IsNullOrWhiteSpace(manifestRelativePath)
            || !Enum.IsDefined(provider))
        {
            throw Error(PrivateCaseAcceptanceFailure.InvalidInput);
        }

        return PrivateCaseRunScope.RunAsync(
            async (scope, token) =>
            {
                var policy = PrivateCaseAcceptancePolicy.Current;
                var manifest = PrivateCaseManifestLoader.Load(
                    authorizedRootPath,
                    manifestRelativePath);
                var prepared = PrivateCaseJourneyInputFactory.Create(
                    manifest,
                    authorizedRootPath,
                    scope.InputWorkspace);
                var expectedReports = PrivateCaseExpectedReportSet.Create(
                    manifest,
                    authorizedRootPath,
                    scope.InputWorkspace);
                var legacyScenarioEvidence = PrivateCaseLegacyScenarioEvidenceVerifier.Verify(
                    manifest,
                    expectedReports);

                // 每一段匯出都在下一段開始前比對：後一段會覆蓋同名的報告。
                // 第一次與三段後續操作用同一組預期底稿、同一份政策與 contentDecisions。
                var segments = new List<SegmentVerification>(FollowUps.Count + 1);
                Task VerifySegmentAsync(PrivateCaseJourneyResult segment)
                {
                    if (segment.CompletedCheckpoint != LegacyAuditParityCheckpoint.Export
                        || segment.ProjectId is null
                        || segment.InfVerification is null
                        || segment.ScenarioVerificationFacts is null
                        || segment.ScenarioDiagnosticFacts is null
                        || segment.ReportCount != Enum.GetValues<LegacyReportKind>().Length
                        || segments.Count > FollowUps.Count
                        || segment.FollowUp != (segments.Count == 0
                            ? (LegacyAuditParityFollowUp?)null
                            : FollowUps[segments.Count - 1]))
                    {
                        throw Error(PrivateCaseAcceptanceFailure.IncompleteJourney);
                    }

                    var scenarioVerification = PrivateCaseScenarioVerifier.Verify(
                        manifest.Legacy.ScenarioCounts,
                        segment.ScenarioVerificationFacts,
                        segment.ScenarioDiagnosticFacts);
                    var scenarioPopulationComparison = PrivateCaseScenarioPopulationComparator.Compare(
                        legacyScenarioEvidence,
                        segment.ScenarioDiagnosticFacts);
                    var generatedRoot = System.IO.Path.GetFullPath(
                        segment.ProjectId,
                        scope.Host.ProjectsRoot);
                    var actualReports = PrivateCaseActualReportSet.Create(
                        segment.Reports,
                        generatedRoot,
                        scope.InputWorkspace,
                        segmentIndex: segments.Count);
                    var pairs = PrivateCaseReportPairSet.Create(expectedReports, actualReports);
                    var reportComparison = PrivateCaseReportComparator.Compare(
                        pairs,
                        policy,
                        manifest.Legacy.ContentDecisions);
                    segments.Add(new SegmentVerification(
                        segment.ReportCount,
                        segment.InfVerification,
                        scenarioVerification,
                        scenarioPopulationComparison,
                        reportComparison));
                    return Task.CompletedTask;
                }

                var journey = await PrivateCaseJourney.RunAsync(
                    scope.Host,
                    prepared,
                    provider,
                    LegacyAuditParityCheckpoint.Export,
                    token,
                    FollowUps,
                    VerifySegmentAsync).ConfigureAwait(false);
                if (segments.Count != FollowUps.Count + 1
                    || journey.ReportCount != segments[0].ReportCount)
                {
                    throw Error(PrivateCaseAcceptanceFailure.IncompleteJourney);
                }

                var first = segments[0];
                return new PrivateCaseAcceptanceResult(
                    manifest.CaseAlias,
                    provider,
                    first.ReportCount,
                    policy,
                    legacyScenarioEvidence,
                    first.InfVerification,
                    first.ScenarioVerification,
                    first.ScenarioPopulationComparison,
                    first.ReportComparison,
                    segments
                        .Skip(1)
                        .Select((item, index) => new PrivateCaseAcceptanceFollowUpResult(
                            FollowUps[index],
                            item.ReportCount,
                            item.InfVerification,
                            item.ScenarioVerification,
                            item.ScenarioPopulationComparison,
                            item.ReportComparison))
                        .ToArray());
            },
            cancellationToken);
    }

    /// <summary>一段匯出的比對結果；第一次與後續操作共用，回傳前再分開。</summary>
    private sealed record SegmentVerification(
        int ReportCount,
        PrivateCaseInfVerificationResult InfVerification,
        PrivateCaseScenarioVerificationResult ScenarioVerification,
        PrivateCaseScenarioPopulationComparisonResult ScenarioPopulationComparison,
        PrivateCaseReportComparisonResult ReportComparison);

    private static PrivateCaseAcceptanceException Error(
        PrivateCaseAcceptanceFailure failure) => new(failure);
}
