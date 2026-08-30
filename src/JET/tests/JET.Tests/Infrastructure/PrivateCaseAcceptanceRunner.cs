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
        int artifactCount,
        PrivateCaseAcceptancePolicy policy,
        PrivateCaseLegacyScenarioEvidenceResult legacyScenarioEvidence,
        PrivateCaseInfVerificationResult infVerification,
        PrivateCaseScenarioVerificationResult scenarioVerification,
        PrivateCaseScenarioPopulationComparisonResult scenarioPopulationComparison,
        PrivateCaseReportComparisonResult reportComparison)
    {
        CaseAlias = caseAlias;
        Provider = provider;
        ArtifactCount = artifactCount;
        Policy = policy;
        LegacyScenarioEvidence = legacyScenarioEvidence;
        InfVerification = infVerification;
        ScenarioVerification = scenarioVerification;
        ScenarioPopulationComparison = scenarioPopulationComparison;
        ReportComparison = reportComparison;
    }

    public string CaseAlias { get; }

    public LegacyAuditParityProvider Provider { get; }

    public int ArtifactCount { get; }

    public PrivateCaseAcceptancePolicy Policy { get; }

    public PrivateCaseLegacyScenarioEvidenceResult LegacyScenarioEvidence { get; }

    public PrivateCaseInfVerificationResult InfVerification { get; }

    public PrivateCaseScenarioVerificationResult ScenarioVerification { get; }

    public PrivateCaseScenarioPopulationComparisonResult ScenarioPopulationComparison { get; }

    public PrivateCaseReportComparisonResult ReportComparison { get; }

    public bool Passed => ArtifactCount == Enum.GetValues<LegacyReportKind>().Length
        && LegacyScenarioEvidence.Passed
        && InfVerification.Passed
        && ScenarioVerification.Passed
        && ReportComparison.Passed;

    public override string ToString() =>
        $"private case acceptance ({CaseAlias}, {Provider})";
}

internal static class PrivateCaseAcceptanceRunner
{
    internal static Task<PrivateCaseAcceptanceResult> RunAsync(
        string authorizedRootPath,
        string manifestRelativePath,
        LegacyAuditParityProvider provider,
        string? sqlServerConnectionString = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(authorizedRootPath)
            || !System.IO.Path.IsPathFullyQualified(authorizedRootPath)
            || string.IsNullOrWhiteSpace(manifestRelativePath)
            || !Enum.IsDefined(provider)
            || (provider == LegacyAuditParityProvider.SqlServer
                && string.IsNullOrWhiteSpace(sqlServerConnectionString)))
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

                var journey = await PrivateCaseJourney.RunAsync(
                    scope.Host,
                    prepared,
                    provider,
                    LegacyAuditParityCheckpoint.Export,
                    sqlServerConnectionString,
                    token).ConfigureAwait(false);
                if (journey.CompletedCheckpoint != LegacyAuditParityCheckpoint.Export
                    || journey.ProjectId is null
                    || journey.InfVerification is null
                    || journey.ScenarioVerificationFacts is null
                    || journey.ScenarioDiagnosticFacts is null
                    || journey.ArtifactCount != Enum.GetValues<LegacyReportKind>().Length)
                {
                    throw Error(PrivateCaseAcceptanceFailure.IncompleteJourney);
                }

                var scenarioVerification = PrivateCaseScenarioVerifier.Verify(
                    manifest.Legacy.ScenarioCounts,
                    journey.ScenarioVerificationFacts,
                    journey.ScenarioDiagnosticFacts);
                var scenarioPopulationComparison = PrivateCaseScenarioPopulationComparator.Compare(
                    legacyScenarioEvidence,
                    journey.ScenarioDiagnosticFacts);
                var generatedRoot = System.IO.Path.GetFullPath(
                    journey.ProjectId,
                    scope.Host.ProjectsRoot);
                var actualReports = PrivateCaseActualReportSet.Create(
                    journey.Artifacts,
                    generatedRoot,
                    scope.InputWorkspace);
                var pairs = PrivateCaseReportPairSet.Create(expectedReports, actualReports);
                var reportComparison = PrivateCaseReportComparator.Compare(
                    pairs,
                    policy,
                    manifest.Legacy.ContentDecisions);

                return new PrivateCaseAcceptanceResult(
                    manifest.CaseAlias,
                    provider,
                    journey.ArtifactCount,
                    policy,
                    legacyScenarioEvidence,
                    journey.InfVerification,
                    scenarioVerification,
                    scenarioPopulationComparison,
                    reportComparison);
            },
            sqlServerConnectionString,
            cancellationToken);
    }

    private static PrivateCaseAcceptanceException Error(
        PrivateCaseAcceptanceFailure failure) => new(failure);
}
