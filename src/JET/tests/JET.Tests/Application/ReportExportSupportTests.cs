using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 報告 artifact 的 stale 是來源結果失效旗標，不是「已被更新匯出取代」旗標。
/// </summary>
public sealed class ReportExportSupportTests
{
    private static readonly RuleRunRecord CurrentValidation = new(
        "11111111111111111111111111111111",
        RuleRunKinds.Validate,
        new DateTimeOffset(2026, 7, 10, 1, 0, 0, TimeSpan.Zero),
        "{}");
    private static readonly RuleRunRecord CurrentPrescreen = new(
        "33333333333333333333333333333333",
        RuleRunKinds.Prescreen,
        new DateTimeOffset(2026, 7, 10, 1, 1, 0, TimeSpan.Zero),
        "{}");

    [Fact]
    public void RequireRequestedValidationRun_MatchingRunId_ReturnsNormally()
    {
        ReportExportSupport.RequireRequestedValidationRun(
            CurrentValidation,
            CurrentValidation.RunId);
    }

    [Fact]
    public void RequireRequestedValidationRun_DifferentRunId_PreservesStaleContract()
    {
        var exception = Assert.Throws<JetActionException>(() =>
            ReportExportSupport.RequireRequestedValidationRun(
                CurrentValidation,
                "22222222222222222222222222222222"));

        Assert.Equal(JetErrorCodes.StaleResult, exception.Code);
        Assert.Equal(
            "指定的 validate 執行結果已不是目前版本，請重新執行並使用最新結果。",
            exception.Message);
    }

    [Fact]
    public void RequireRevision_OldFilterLogicVersion_ThrowsStaleResult()
    {
        var savedUtc = new DateTimeOffset(2026, 7, 10, 1, 2, 3, TimeSpan.Zero);
        var scenarios = new[]
        {
            new SavedFilterScenario(
                1,
                "old",
                "old",
                "{\"name\":\"old\",\"groups\":[]}",
                savedUtc)
        };

        var exception = Assert.Throws<JetActionException>(() =>
            ReportExportSupport.RequireRevision(scenarios, savedUtc.ToString("O")));

        Assert.Equal(JetErrorCodes.StaleResult, exception.Code);
    }

    [Fact]
    public void IsSourceStale_LegacyCriteriaHasRevisionOnly_ReturnsTrueWithoutRejectingArtifact()
    {
        var artifact = Artifact(
            ReportArtifactKind.CriteriaSelectionReport,
            new ReportArtifactSourceRefs(ScenarioRevision: "revision-1"));

        var stale = ReportExportSupport.IsSourceStale(
            artifact,
            CurrentValidation,
            CurrentPrescreen,
            "revision-1",
            [1, 2]);

        Assert.True(stale);
    }

    [Theory]
    [InlineData(ReportArtifactKind.CriteriaSelectionReport)]
    [InlineData(ReportArtifactKind.WorkingPaper)]
    public void IsSourceStale_ScenarioReportIgnoresLegacyPrescreenReference(
        ReportArtifactKind kind)
    {
        var artifact = Artifact(
            kind,
            new ReportArtifactSourceRefs(
                CurrentValidation.RunId,
                "legacy-prescreen-run",
                "revision-1",
                kind == ReportArtifactKind.CriteriaSelectionReport ? [1, 2] : [2]));

        var stale = ReportExportSupport.IsSourceStale(
            artifact,
            CurrentValidation,
            latestPrescreen: null,
            filterRevision: "revision-1",
            currentScenarioPositions: [1, 2]);

        Assert.False(stale);
    }

    private static ReportArtifact Artifact(
        ReportArtifactKind kind,
        ReportArtifactSourceRefs source)
        => new(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            kind,
            "placeholder.xlsx",
            source,
            new DateTimeOffset(2026, 7, 10, 1, 2, 0, TimeSpan.Zero),
            0,
            LastWriteUtc: null,
            Stale: false);
}
