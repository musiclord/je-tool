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
        // 9/22 各步報告都可定位檔案，共用綁定仍只傳伺服器解析的產物識別碼。
        Assert.Contains("hostOpenFolder({ artifactId: artifactId })", core, StringComparison.Ordinal);
        Assert.Contains("Ui.bindReportArtifacts(container)", export, StringComparison.Ordinal);
        Assert.Contains("Ui.bindReportArtifacts(container)", validate, StringComparison.Ordinal);
        Assert.Contains("Ui.bindReportArtifacts(container)", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("{ path:", core, StringComparison.Ordinal);
        Assert.DoesNotContain("target: 'projectFolder'", export, StringComparison.Ordinal);
        Assert.DoesNotContain("{ path:", export, StringComparison.Ordinal);
        Assert.Contains("hostOpenFolder({ target: 'projectFolder' })", validate, StringComparison.Ordinal);
        Assert.DoesNotContain("{ path:", validate, StringComparison.Ordinal);
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

        // 第二遍第9批把失效範圍與報告目錄都交回後端；原criteria payload約束維持不變。
        // 首次失敗：20261004-100752118-a195c091201e444d858ec7d7bee3d291。
        // 清除未儲存預覽與持久化stale旗標是兩件事，不得由前端用artifact種類或有無舊結果推測。
        var invalidation = ExtractBetween(state, "function invalidateDerivedResults(options)", "var Store = {");
        Assert.Contains("var clearValidation = !!(options && options.validation)", invalidation, StringComparison.Ordinal);
        Assert.Contains("var clearPrescreen = !!(options && options.prescreen)", invalidation, StringComparison.Ordinal);
        Assert.Contains("var clearFilter = !!(options && options.filter)", invalidation, StringComparison.Ordinal);
        Assert.Contains("if (clearValidation) { state.lastRuns.validate = null; }", invalidation, StringComparison.Ordinal);
        Assert.Contains("if (clearPrescreen) { state.lastRuns.prescreen = null; }", invalidation, StringComparison.Ordinal);
        Assert.Contains("if (clearFilter)", invalidation, StringComparison.Ordinal);
        Assert.Contains("state.filter.preview = null", invalidation, StringComparison.Ordinal);
        Assert.Contains("filterDraftRev++", invalidation, StringComparison.Ordinal);
        Assert.DoesNotContain("staleState", invalidation, StringComparison.Ordinal);
        Assert.DoesNotContain("reportArtifacts", invalidation, StringComparison.Ordinal);
        Assert.DoesNotContain("invalidateDerivedResults({", state, StringComparison.Ordinal);

        var effects = ExtractBetween(state, "applyMutationEffects: function (result)", "resetWorkflow: function");
        Assert.Contains("var invalidated = result.invalidatedResults || {}", effects, StringComparison.Ordinal);
        Assert.Contains("invalidateDerivedResults(invalidated)", effects, StringComparison.Ordinal);
        Assert.Contains("validation: !!result.staleState.validation", effects, StringComparison.Ordinal);
        Assert.Contains("prescreen: !!result.staleState.prescreen", effects, StringComparison.Ordinal);
        Assert.Contains("filter: !!result.staleState.filter", effects, StringComparison.Ordinal);
        Assert.Contains("if (Array.isArray(result.reportArtifacts)) { state.reportArtifacts = result.reportArtifacts.slice(); }", effects, StringComparison.Ordinal);
        Assert.Contains("Store.addMessage(result.reportArtifactWarning, 'warn')", effects, StringComparison.Ordinal);
        Assert.DoesNotContain("result.reportArtifacts || []", effects, StringComparison.Ordinal);
        Assert.DoesNotContain("artifact.kind", effects, StringComparison.Ordinal);
        Assert.DoesNotContain("stale: true", effects, StringComparison.Ordinal);
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
        // 2026-10-02 修改原因（W15）：完成摘要改用和左側進度、流程總覽共用的 Ui.currentWorkpaperArtifact，
        // 參數由陣列 currentWorkpapers 改成單一的 currentWorkpaper。原斷言的字樣因此不存在，
        // 第一次失敗收據 20261002-143202027-0932a0e4100f4e419c7609149b4cb025。仍然只認目前版本，不拿歷史紀錄冒充。
        Assert.Contains("var currentWorkpaper = Ui.currentWorkpaperArtifact(state);", export, StringComparison.Ordinal);
        Assert.Contains("completionSummaryHtml(criteriaArtifact, currentWorkpaper)", export, StringComparison.Ordinal);
        Assert.DoesNotContain("completionSummaryHtml(criteriaArtifact, workpapers) +", export, StringComparison.Ordinal);
        // 第9批改用具名共用展示常數，仍固定50份；不是改用query分頁或審計門檻。
        // 首次失敗：20261004-105344715-0a3f9684995a4a20bcdcb2fb954649f6；Node另鎖51份實際分成50與1。
        Assert.Contains("HISTORY_PAGE_SIZE = Ui.REPORT_HISTORY_PAGE_SIZE", export, StringComparison.Ordinal);
        Assert.Matches(@"\bvar\s+REPORT_HISTORY_PAGE_SIZE\s*=\s*50\s*;", ReadFrontend("ui-core.js"));
    }

    [Fact]
    public void WorkpaperEntry_RequiresSavedDefinitionsNotAnIntermediateFile()
    {
        var core = ReadFrontend("ui-core.js");
        var export = ReadFrontend("steps", "export-step.js");

        Assert.DoesNotContain("prescreenRunId", core, StringComparison.Ordinal);
        Assert.Contains("state.filter.savedScenarios.length === 0", core, StringComparison.Ordinal);
        Assert.Contains("state.filterResultRef.populationScope !== 'auditPeriod'", core, StringComparison.Ordinal);
        Assert.DoesNotContain("產生目前版本的條件篩選報告", core, StringComparison.Ordinal);

        Assert.Contains("validationRunId: validationRunId", export, StringComparison.Ordinal);
        Assert.DoesNotContain("prescreenRunId", export, StringComparison.Ordinal);
        Assert.Contains("scenarioRevision: scenarioRevision", export, StringComparison.Ordinal);
        Assert.Contains("scenarioPositions: allScenarioPositions(state)", export, StringComparison.Ordinal);
    }

    private static string ExtractBetween(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到起點標記：{startMarker}");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"找不到終點標記：{endMarker}");
        return source[start..end];
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
