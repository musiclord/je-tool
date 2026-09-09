using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 工作底稿的前端邊界只選情境；工作表與檔案落點由後端契約固定。
/// 這組 source mirror 阻擋 UI 再把 sheets／outputPath 帶回 wire。
/// </summary>
public sealed class WorkpaperSheetMirrorTests
{
    [Fact]
    public void ExportStep_SelectsScenariosWithoutSheetsOrOutputPath()
    {
        var source = ReadFrontend("steps", "export-step.js");

        Assert.Contains("validationRunId:", source, StringComparison.Ordinal);
        Assert.DoesNotContain("prescreenRunId", source, StringComparison.Ordinal);
        Assert.Contains("scenarioRevision:", source, StringComparison.Ordinal);
        Assert.Contains("scenarioPositions:", source, StringComparison.Ordinal);
        Assert.Contains("data-scenario-position", source, StringComparison.Ordinal);
        Assert.Contains("sheet.sheetName", source, StringComparison.Ordinal);
        Assert.Contains("底稿會標示符合所選情境的分錄，並保留同傳票參考分錄", source, StringComparison.Ordinal);

        Assert.DoesNotContain("WORKPAPER_SHEETS", source, StringComparison.Ordinal);
        Assert.DoesNotContain("step1-3-1", source, StringComparison.Ordinal);
        Assert.DoesNotContain("data-sheet-index", source, StringComparison.Ordinal);
        Assert.DoesNotContain("hostSelectSavePath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("outputPath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("{ path:", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportWorkflow_ResumesArtifactStateAndRevealsHistoryByServerResolvedId()
    {
        var core = ReadFrontend("ui-core.js");
        var state = ReadFrontend("state.js");
        var export = ReadFrontend("steps", "export-step.js");
        var validate = ReadFrontend("steps", "validate-step.js");
        var filter = ReadFrontend("steps", "filter-step.js");
        var app = ReadFrontend("app.js");

        Assert.Contains("Store.setFilterResultRef(data.filterResultRef || null)", core, StringComparison.Ordinal);
        Assert.Contains("Store.setReportArtifacts(data.reportArtifacts || [])", core, StringComparison.Ordinal);
        Assert.Contains("filterResultRef: null", state, StringComparison.Ordinal);
        Assert.Contains("reportArtifacts: []", state, StringComparison.Ordinal);
        Assert.Contains("hostOpenFolder({ target: 'projectFolder' })", app, StringComparison.Ordinal);
        Assert.Contains("data-open-artifact", core, StringComparison.Ordinal);
        Assert.DoesNotContain("hostOpenFolder", core, StringComparison.Ordinal);
        Assert.Contains("hostOpenFolder({ artifactId: artifactId })", export, StringComparison.Ordinal);
        Assert.DoesNotContain("target: 'projectFolder'", export, StringComparison.Ordinal);
        Assert.DoesNotContain("{ path:", export, StringComparison.Ordinal);
        Assert.DoesNotContain("hostOpenFolder", validate, StringComparison.Ordinal);
        Assert.DoesNotContain("hostOpenFolder", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void CriteriaAndFrontendInvalidation_BindTheCorrectDataGeneration()
    {
        var filter = ReadFrontend("steps", "filter-step.js");
        var state = ReadFrontend("state.js");

        Assert.Contains("exportCriteriaSelectionReport({", filter, StringComparison.Ordinal);
        Assert.Contains("validationRunId: validationRunId", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("prescreenRunId", filter, StringComparison.Ordinal);
        Assert.Contains("revision: resultRef.revision", filter, StringComparison.Ordinal);

        Assert.Contains("kind === 'gl'", state, StringComparison.Ordinal);
        Assert.Contains("{ validation: true, prescreen: true, filter: true }", state, StringComparison.Ordinal);
        Assert.Contains("{ validation: true }", state, StringComparison.Ordinal);
        Assert.Contains("{ prescreen: true, filter: true }", state, StringComparison.Ordinal);
        Assert.Contains("artifact.kind === 'prescreenReport'", state, StringComparison.Ordinal);
        Assert.Contains("artifact.kind === 'criteriaSelectionReport' || artifact.kind === 'workingPaper'", state, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportArtifactUpsert_PreservesWorkpaperHistoryAndReplacesOrdinaryReports()
    {
        var state = ReadFrontend("state.js");

        Assert.Contains("if (artifact.kind !== 'workingPaper') { incomingKinds[artifact.kind] = true; }", state, StringComparison.Ordinal);
        Assert.Contains("return !incomingKinds[artifact.kind] && !incomingIds[artifact.artifactId];", state, StringComparison.Ordinal);
        Assert.DoesNotContain("hasSameReportValiditySource", state, StringComparison.Ordinal);
        Assert.DoesNotContain("if (sameValiditySource) { return artifact; }", state, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkpaperHistory_DoesNotReplaceCurrentCompletionEvidence()
    {
        var export = ReadFrontend("steps", "export-step.js");
        Assert.Contains("Ui.reportArtifactHistory(state, 'workingPaper')", export, StringComparison.Ordinal);
        Assert.Contains("completionSummaryHtml(criteriaArtifact, currentWorkpapers)", export, StringComparison.Ordinal);
        Assert.DoesNotContain("completionSummaryHtml(criteriaArtifact, workpapers) +", export, StringComparison.Ordinal);
        Assert.Contains("HISTORY_PAGE_SIZE = 50", export, StringComparison.Ordinal);
    }

    [Fact]
    public void StepFiveGates_CriteriaOnValidationRevisionAndAllPositions()
    {
        var core = ReadFrontend("ui-core.js");
        var export = ReadFrontend("steps", "export-step.js");

        Assert.Contains("validationRunId: ruleRunId(state.lastRuns.validate)", core, StringComparison.Ordinal);
        Assert.DoesNotContain("prescreenRunId", core, StringComparison.Ordinal);
        Assert.Contains("scenarioRevision: state.filterResultRef.revision", core, StringComparison.Ordinal);
        Assert.Contains("scenarioPositions: allScenarioPositions(state)", core, StringComparison.Ordinal);

        Assert.Contains("validationRunId: validationRunId", export, StringComparison.Ordinal);
        Assert.DoesNotContain("prescreenRunId", export, StringComparison.Ordinal);
        Assert.Contains("scenarioRevision: scenarioRevision", export, StringComparison.Ordinal);
        Assert.Contains("scenarioPositions: allScenarioPositions(state)", export, StringComparison.Ordinal);
    }

    private static string ReadFrontend(params string[] relativeSegments)
    {
        var segments = new[] { RepoRoot(), "JET", "wwwroot", "js" }
            .Concat(relativeSegments)
            .ToArray();
        return File.ReadAllText(Path.Combine(segments));
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JET.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("向上尋不到 JET.slnx，無法定位 repo 根。");
    }
}
