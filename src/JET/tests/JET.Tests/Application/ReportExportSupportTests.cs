using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 報告的 stale 是來源結果失效旗標，不是「已被更新匯出取代」旗標。
/// </summary>
public sealed class ReportExportSupportTests
{
    private static readonly ResultRecord CurrentValidation = new(
        "11111111111111111111111111111111",
        ResultKind.Validation,
        new DateTimeOffset(2026, 7, 10, 1, 0, 0, TimeSpan.Zero),
        "{}");
    private static readonly ResultRecord CurrentPrescreen = new(
        "33333333333333333333333333333333",
        ResultKind.Prescreen,
        new DateTimeOffset(2026, 7, 10, 1, 1, 0, TimeSpan.Zero),
        "{}");

    [Fact]
    public void RequireRequestedValidationResult_MatchingResultId_ReturnsNormally()
    {
        ReportExportSupport.RequireRequestedValidationResult(
            CurrentValidation,
            CurrentValidation.ResultId);
    }

    [Fact]
    public void RequireRequestedValidationResult_DifferentResultId_PreservesStaleContract()
    {
        var exception = Assert.Throws<JetActionException>(() =>
            ReportExportSupport.RequireRequestedValidationResult(
                CurrentValidation,
                "22222222222222222222222222222222"));

        Assert.Equal(JetErrorCodes.StaleResult, exception.Code);
        // 這句的用字已定案。
        Assert.Equal(
            "這份報告依據的資料驗證結果已不是目前的結果，請重新執行資料驗證後再產生。",
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
            ReportExportSupport.RequireRevisionState(scenarios, savedUtc.ToString("O")));

        Assert.Equal(JetErrorCodes.StaleResult, exception.Code);
    }

    [Fact]
    public void IsSourceStale_LegacyCriteriaHasRevisionOnly_ReturnsTrueWithoutRejectingReport()
    {
        var report = Report(
            ReportKind.CriteriaSelectionReport,
            new ReportSourceRefs(ScenarioRevision: "revision-1"));

        var stale = ReportExportSupport.IsSourceStale(
            report,
            CurrentValidation,
            CurrentPrescreen,
            "revision-1",
            [1, 2]);

        Assert.True(stale);
    }

    [Theory]
    [InlineData(ReportKind.CriteriaSelectionReport)]
    [InlineData(ReportKind.WorkingPaper)]
    public void IsSourceStale_ScenarioReportIgnoresLegacyPrescreenReference(
        ReportKind kind)
    {
        // Criteria須有同一資料版本；明示來源版本，讓這則只測舊prescreen參照不影響判定。
        var report = Report(
            kind,
            new ReportSourceRefs(
                CurrentValidation.ResultId,
                "legacy-prescreen-run",
                "revision-1",
                kind == ReportKind.CriteriaSelectionReport ? [1, 2] : [2], FilterDataRevision: "9"));

        var stale = ReportExportSupport.IsSourceStale(
            report,
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
    // 缺來源版本的底稿一律過期，和 CriteriaSelection 報告的判斷相同。
    [InlineData(null, "8", false, true)]
    public void IsSourceStale_SelectedWorkingPaperUsesItsDataRevision_AndIsStaleWithoutOne(
        string? reportRevision, string? currentRevision, bool allScenarioResultsStale, bool expected)
    {
        var report = Report(ReportKind.WorkingPaper, new ReportSourceRefs(
            ValidationResultId: CurrentValidation.ResultId, ScenarioRevision: "revision-1",
            ScenarioPositions: [1], FilterDataRevision: reportRevision));
        Assert.Equal(expected, ReportExportSupport.IsSourceStale(report, CurrentValidation, null,
            allScenarioResultsStale, "revision-1", [1, 2], currentRevision));
    }

    [Fact]
    public void IsSourceStale_SelectedWorkingPaperDataRevisionDoesNotOverrideValidationOrDefinitionChanges()
    {
        var source = new ReportSourceRefs(ValidationResultId: CurrentValidation.ResultId,
            ScenarioRevision: "revision-1", ScenarioPositions: [2], FilterDataRevision: "8");
        var report = Report(ReportKind.WorkingPaper, source);
        Assert.True(ReportExportSupport.IsSourceStale(report, null, null, false, "revision-1", [1, 2], "8"));
        Assert.True(ReportExportSupport.IsSourceStale(report, CurrentValidation, null, false, "revision-2", [1, 2], "8"));
        Assert.True(ReportExportSupport.IsSourceStale(report, CurrentValidation, null, false, "revision-1", [1], "8"));
    }

    private static Report Report(
        ReportKind kind,
        ReportSourceRefs source)
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
