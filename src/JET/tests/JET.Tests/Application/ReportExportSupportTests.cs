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
        // 第9批高3；Public首敗100911120：Criteria現在須有同一資料版本，補明示來源版本以繼續只測舊prescreen參照不影響判定。
        var artifact = Artifact(
            kind,
            new ReportArtifactSourceRefs(
                CurrentValidation.RunId,
                "legacy-prescreen-run",
                "revision-1",
                kind == ReportArtifactKind.CriteriaSelectionReport ? [1, 2] : [2], FilterDataRevision: "9"));

        var stale = ReportExportSupport.IsSourceStale(
            artifact,
            CurrentValidation,
            latestPrescreen: null,
            filterRevision: "revision-1",
            currentScenarioPositions: [1, 2], currentFilterDataRevision: "9");

        Assert.False(stale);
    }

    [Theory]
    [InlineData("8", "8", true, false)]
    [InlineData("8", "9", false, true)]
    [InlineData("8", null, false, true)]
    [InlineData(null, "8", true, true)]
    [InlineData(null, "8", false, false)]
    public void IsSourceStale_SelectedWorkpaperUsesItsDataRevision_AndLegacyKeepsPriorFlagRule(
        string? artifactRevision, string? currentRevision, bool allScenarioResultsStale, bool expected)
    {
        var artifact = Artifact(ReportArtifactKind.WorkingPaper, new ReportArtifactSourceRefs(
            ValidationRunId: CurrentValidation.RunId, ScenarioRevision: "revision-1",
            ScenarioPositions: [1], FilterDataRevision: artifactRevision));
        Assert.Equal(expected, ReportExportSupport.IsSourceStale(artifact, CurrentValidation, null,
            allScenarioResultsStale, "revision-1", [1, 2], currentRevision));
    }

    [Fact]
    public void IsSourceStale_SelectedWorkpaperDataRevisionDoesNotOverrideValidationOrDefinitionChanges()
    {
        var source = new ReportArtifactSourceRefs(ValidationRunId: CurrentValidation.RunId,
            ScenarioRevision: "revision-1", ScenarioPositions: [2], FilterDataRevision: "8");
        var artifact = Artifact(ReportArtifactKind.WorkingPaper, source);
        Assert.True(ReportExportSupport.IsSourceStale(artifact, null, null, false, "revision-1", [1, 2], "8"));
        Assert.True(ReportExportSupport.IsSourceStale(artifact, CurrentValidation, null, false, "revision-2", [1, 2], "8"));
        Assert.True(ReportExportSupport.IsSourceStale(artifact, CurrentValidation, null, false, "revision-1", [1], "8"));
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
