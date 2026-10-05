using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 半屏操作、持久選單、單一資料夾入口與摘要優先總覽的呈現層守衛。
/// 審計事實仍只由既有 backend DTO 提供；本組只鎖 DOM、互動與可讀性。
/// </summary>
public sealed class FrontendUsabilityContractTests
{
    [Fact]
    public void Preview_Uses1360CompactBoundaryAndPreservesManualChoiceUntilViewportCrossesAgain()
    {
        var preview = ReadFrontend("js", "data-preview.js");

        Assert.Contains("matchMedia('(max-width: 1360px)')", preview, StringComparison.Ordinal);
        Assert.Contains("autoCollapsed", preview, StringComparison.Ordinal);
        Assert.Contains("addEventListener('change'", preview, StringComparison.Ordinal);
        Assert.Contains("setPreviewCollapsedByUser", preview, StringComparison.Ordinal);
        Assert.Contains("Store.setDataPreviewCollapsed(collapsed)", ExtractFunction(preview, "setPreviewCollapsedByUser"), StringComparison.Ordinal);
    }

    [Fact]
    public void Mapping_UsesContainerWidthKeepsLabelsWholeAndOwnsHorizontalOverflow()
    {
        var css = ReadFrontend("css", "app.css");

        Assert.Matches(new Regex(@"\.map-layout\s*\{[^}]*display:\s*flex;[^}]*flex-wrap:\s*wrap;", RegexOptions.Singleline), css);
        Assert.Matches(new Regex(@"\.mapping-table-wrap\s*\{[^}]*overflow-x:\s*auto;", RegexOptions.Singleline), css);
        Assert.Contains("word-break: keep-all", css, StringComparison.Ordinal);
        Assert.Contains("white-space: nowrap", css, StringComparison.Ordinal);
    }

    [Fact]
    public void OtherDataMenu_UsesPersistentNativeControlsWithKeyboardAndAria()
    {
        var preview = ReadFrontend("js", "data-preview.js");

        Assert.Contains("其他資料", preview, StringComparison.Ordinal);
        Assert.Contains("aria-haspopup=\"menu\"", preview, StringComparison.Ordinal);
        Assert.Contains("aria-controls=\"data-preview-other-menu\"", preview, StringComparison.Ordinal);
        Assert.Contains("role=\"menu\"", preview, StringComparison.Ordinal);
        Assert.Contains("role=\"menuitem\"", preview, StringComparison.Ordinal);
        Assert.Contains("function syncTabs(", preview, StringComparison.Ordinal);
        Assert.Contains("Escape", preview, StringComparison.Ordinal);
        Assert.Contains("ArrowDown", preview, StringComparison.Ordinal);
        Assert.Contains("ArrowUp", preview, StringComparison.Ordinal);
        Assert.Contains("focus", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("openMenu() { menuOpen = true; renderTabs();", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_HasHeaderAndMappingFolderEntriesAndSharedReportRevealControls()
    {
        var root = FrontendRoot();
        var index = File.ReadAllText(Path.Combine(root, "index.html"));
        var javascript = string.Join('\n', Directory.EnumerateFiles(Path.Combine(root, "js"), "*.js", SearchOption.AllDirectories).Select(File.ReadAllText));

        Assert.Single(Regex.Matches(index, "data-action=\"open-project-folder\"", RegexOptions.CultureInvariant).Cast<Match>());
        // 9/22 科目配對區新增就近開啟資料夾，報告定位由各步共用一個事件綁定。
        Assert.Equal(3, Regex.Matches(javascript, @"JetApi\.hostOpenFolder\(", RegexOptions.CultureInvariant).Count);
        Assert.Contains("hostOpenFolder({ target: 'projectFolder' })", javascript, StringComparison.Ordinal);
        Assert.Contains("data-open-artifact", javascript, StringComparison.Ordinal);
        Assert.Contains("hostOpenFolder({ artifactId: artifactId })", javascript, StringComparison.Ordinal);
        Assert.DoesNotContain("hostOpenFolder({ path:", javascript, StringComparison.Ordinal);
        Assert.DoesNotContain("artifactId: artifact.artifactId", javascript, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_RemovesRedundantSuccessPromptsButRetainsBlockersAndAuditInterpretation()
    {
        var javascript = string.Join('\n', Directory.EnumerateFiles(Path.Combine(FrontendRoot(), "js"), "*.js", SearchOption.AllDirectories).Select(File.ReadAllText));

        Assert.DoesNotContain("現在可以開始", javascript, StringComparison.Ordinal);
        Assert.DoesNotContain("進入條件已備齊", javascript, StringComparison.Ordinal);
        Assert.DoesNotContain("本步驟條件已備齊", javascript, StringComparison.Ordinal);
        Assert.Contains("已阻擋：", javascript, StringComparison.Ordinal);
        // 「符合條件不等於錯誤」與「判斷由審計員做」在 2026-09-22 改成白話後合併為同一句，整句檢查。
        // 2026-10-03 用語統一 T1：畫面不再使用「母體」（第一次失敗：收據 20261003-023349721-0ccefea0a80c412aa8460624eaae563a）。
        Assert.Contains("預篩選呈現查核期間分錄的分布與符合條件的分錄，是否需進一步查核由審計員判斷。", javascript, StringComparison.Ordinal);
        Assert.DoesNotContain("母體分布", javascript, StringComparison.Ordinal);
        // 2026-10-02 整體複審 W16：「不適用不等於零。」改成完整句，語意仍是「不適用不代表沒有符合的分錄」；舊短句不得再出現。
        Assert.Contains("列在「不適用規則」的條件因缺少所需資料或設定而沒有執行，不代表沒有符合的分錄。", javascript, StringComparison.Ordinal);
        Assert.DoesNotContain("不適用不等於零", javascript, StringComparison.Ordinal);
    }

    [Fact]
    public void Overview_IsSummaryFirstWithCollapsedAnalysesTechnicalDetailsAndFocusContainment()
    {
        var app = ReadFrontend("js", "app.js");
        var overviewRenderer = ReadFrontend("js", "overview-bi.js");
        var overview = ExtractFunction(app, "overviewHtml");

        AssertOrder(overview, "overviewHeadHtml", "overviewPipelineHtml", "overviewPopulationHtml", "overviewAnalysisHtml");
        Assert.Contains("overviewTechnicalHtml", overview, StringComparison.Ordinal);
        Assert.Equal(3, Regex.Matches(overviewRenderer, "data-overview-analysis", RegexOptions.CultureInvariant).Count);
        Assert.DoesNotContain("<details open", overviewRenderer, StringComparison.Ordinal);
        Assert.DoesNotContain("overviewMessagesHtml", overview, StringComparison.Ordinal);
        Assert.Contains("overviewExpandedSections", app, StringComparison.Ordinal);
        Assert.Contains("Escape", ExtractFunction(app, "bindOverviewDialog"), StringComparison.Ordinal);
        Assert.Contains("focus", ExtractFunction(app, "bindOverviewDialog"), StringComparison.Ordinal);
    }

    [Fact]
    public void Overview_StageRegisterReviewsStatusWithoutDuplicatingDetailEvidenceOrRoutingTheWorkflow()
    {
        var app = ReadFrontend("js", "app.js");
        var pipeline = ExtractFunction(app, "overviewPipelineHtml");
        var detail = ExtractFunction(app, "overviewStepDetailHtml");
        var detailBody = ExtractFunction(app, "overviewStepDetailBodyHtml");
        var facts = ExtractFunction(app, "overviewStepFactsHtml");
        var attention = ExtractFunction(app, "overviewStepAttentionHtml");
        var binder = ExtractFunction(app, "bindOverviewStepReview");

        Assert.Contains("Store.STEPS.map", pipeline, StringComparison.Ordinal);
        Assert.Contains("role=\"tablist\"", pipeline, StringComparison.Ordinal);
        Assert.Contains("role=\"tab\"", pipeline, StringComparison.Ordinal);
        Assert.Contains("aria-selected=", pipeline, StringComparison.Ordinal);
        Assert.Contains("aria-controls=\"overview-step-detail\"", pipeline, StringComparison.Ordinal);
        Assert.DoesNotContain("overviewStepEvidence", pipeline, StringComparison.Ordinal);
        Assert.DoesNotContain("overview-stage__evidence", pipeline, StringComparison.Ordinal);
        Assert.Contains("role=\"tabpanel\"", detail, StringComparison.Ordinal);
        Assert.Contains("aria-live=\"polite\"", detail, StringComparison.Ordinal);
        Assert.Contains("overviewStepFactsHtml", detailBody, StringComparison.Ordinal);
        Assert.DoesNotContain("OVERVIEW_STAGE_STATUS", detailBody, StringComparison.Ordinal);
        Assert.DoesNotContain("overviewStepEvidence", detailBody, StringComparison.Ordinal);
        Assert.DoesNotContain("overview-step-detail__status", detailBody, StringComparison.Ordinal);
        Assert.DoesNotContain("overview-step-detail__summary", detailBody, StringComparison.Ordinal);
        Assert.Contains("ArrowLeft", binder, StringComparison.Ordinal);
        Assert.Contains("ArrowRight", binder, StringComparison.Ordinal);
        Assert.Contains("Home", binder, StringComparison.Ordinal);
        Assert.Contains("End", binder, StringComparison.Ordinal);
        Assert.DoesNotContain("navigateToStep", binder, StringComparison.Ordinal);
        Assert.DoesNotContain("Store.setOverviewOpen(false)", binder, StringComparison.Ordinal);
        Assert.DoesNotContain("JetApi", string.Concat(pipeline, detail, detailBody, facts, binder), StringComparison.Ordinal);
        Assert.DoesNotContain("Store.set", binder, StringComparison.Ordinal);
        Assert.DoesNotContain("data-overview-step", app, StringComparison.Ordinal);
        // 2026-10-02 整體複審 T4：欄位配對狀態不再說「尚未提交」，改用「尚未完成」；舊字樣不得再出現。
        Assert.Contains("glCommitted ? overviewModeLabel(Ui.GL_MODES, glCommitted.mode) : '尚未完成'", facts, StringComparison.Ordinal);
        Assert.Contains("tbCommitted ? overviewModeLabel(Ui.TB_MODES, tbCommitted.mode) : '尚未完成'", facts, StringComparison.Ordinal);
        Assert.DoesNotContain("尚未提交", facts, StringComparison.Ordinal);
        Assert.Contains("Ui.stepPresentation(state, index).lockedReason", attention, StringComparison.Ordinal);
        Assert.Contains("等待前置步驟", attention, StringComparison.Ordinal);
    }

    [Fact]
    public void Overview_SeparatesWorkflowLocationFromLifecycleStatusAndNeverReliesOnColorAlone()
    {
        var app = ReadFrontend("js", "app.js");
        var css = ReadFrontend("css", "app.css");
        var progress = ExtractFunction(app, "overviewProgressHtml");

        Assert.DoesNotContain("current: '● 進行中'", app, StringComparison.Ordinal);
        Assert.Contains("available: '可處理'", app, StringComparison.Ordinal);
        Assert.Contains("locked: '等待前置步驟'", app, StringComparison.Ordinal);
        Assert.DoesNotContain("if (index === state.currentStepIndex) { return 'current'; }", app, StringComparison.Ordinal);
        Assert.Contains("status === 'available'", progress, StringComparison.Ordinal);
        Assert.Contains("status === 'locked'", progress, StringComparison.Ordinal);
        Assert.Contains("個可處理", progress, StringComparison.Ordinal);
        Assert.Contains("個等待前置", progress, StringComparison.Ordinal);
        Assert.DoesNotContain("個進行中", progress, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(@"\.overview-stage__location\s*\{[^}]*color:\s*var\(--pale-blue-ink\);", RegexOptions.Singleline),
            css);
        Assert.Matches(
            new Regex(@"\.overview__progress-fill\s*\{[^}]*background:\s*var\(--pale-blue-ink\);", RegexOptions.Singleline),
            css);
        Assert.DoesNotMatch(
            new Regex(@"\.overview__progress-fill\s*\{[^}]*background:\s*#d64422;", RegexOptions.Singleline),
            css);
    }

    [Fact]
    public void Overview_AssignsEachVisibleFactToOneSurface()
    {
        var app = ReadFrontend("js", "app.js");
        var overviewBi = ReadFrontend("js", "overview-bi.js");
        var head = ExtractFunction(app, "overviewHeadHtml");
        var progress = ExtractFunction(app, "overviewProgressHtml");
        var pipeline = ExtractFunction(app, "overviewPipelineHtml");
        var details = ExtractFunction(app, "overviewStepFactsHtml");
        var factRenderer = ExtractFunction(app, "overviewFactHtml");
        var population = ExtractFunction(app, "overviewPopulationHtml");
        var populationRenderer = ExtractFunction(app, "overviewPopKpiHtml");
        var technical = ExtractFunction(app, "overviewTechnicalHtml");
        var technicalRenderer = ExtractFunction(app, "overviewKvRow");

        Assert.Contains("data-overview-fact-key", head, StringComparison.Ordinal);
        Assert.Contains("data-overview-fact-key=\"workflow.aggregate\"", progress, StringComparison.Ordinal);
        Assert.Contains("data-overview-fact-key=\"workflow.stage.", pipeline, StringComparison.Ordinal);
        Assert.Contains("data-overview-fact-key", factRenderer, StringComparison.Ordinal);
        Assert.Contains("data-overview-fact-key", populationRenderer, StringComparison.Ordinal);
        Assert.Contains("data-overview-fact-key", technicalRenderer, StringComparison.Ordinal);
        Assert.Contains("project.operator", details, StringComparison.Ordinal);
        Assert.Contains("validation.stats.total-credit", population, StringComparison.Ordinal);
        Assert.Contains("execution.logic-version", technical, StringComparison.Ordinal);

        Assert.DoesNotContain("overviewStepEvidence", pipeline, StringComparison.Ordinal);
        Assert.DoesNotContain("auditPeriodText(state)", details, StringComparison.Ordinal);
        Assert.DoesNotContain("stats.periodStart", population, StringComparison.Ordinal);
        Assert.DoesNotContain("stats.periodEnd", population, StringComparison.Ordinal);
        Assert.DoesNotContain("auditPeriodText(state)", population, StringComparison.Ordinal);
        Assert.Contains("stats.totalCredit", population, StringComparison.Ordinal);
        Assert.DoesNotContain("目前有效的資料驗證結果", population, StringComparison.Ordinal);
        Assert.DoesNotContain("案件編號", technical, StringComparison.Ordinal);
        Assert.DoesNotContain("查核期間", technical, StringComparison.Ordinal);
        Assert.DoesNotContain("var currentCriteria", details, StringComparison.Ordinal);

        Assert.Single(Regex.Matches(app + overviewBi, ">全查核期間<", RegexOptions.CultureInvariant).Cast<Match>());

        var semanticKeys = new[]
        {
            "project.case-id", "project.audit-period", "workflow.aggregate", "workflow.stage.",
            "workflow.location", "project.database-provider", "execution.run-id", "execution.logic-version",
            "project.operator", "project.created-at", "import.gl.source", "import.tb.source",
            "import.supporting", "mapping.gl.commit", "mapping.tb.commit", "validation.current-result",
            "validation.completeness", "prescreen.current-result", "filter.saved-scenarios",
            "filter.population-scope", "filter.current-result", "filter.criteria-report",
            "export.working-paper", "export.working-paper-bytes", "export.scenario-count",
            "validation.stats.gl-row-count", "validation.stats.voucher-count",
            "validation.stats.total-debit", "validation.stats.total-credit", "analysis.scope"
        };
        foreach (var key in semanticKeys)
        {
            Assert.Single(Regex.Matches(
                app,
                $"[\"']{Regex.Escape(key)}[\"']",
                RegexOptions.CultureInvariant).Cast<Match>());
        }
    }

    [Fact]
    public void Overview_CompletionEvidenceReusesExistingGatesAndCurrentArtifactPredicates()
    {
        var app = ReadFrontend("js", "app.js");
        var core = ReadFrontend("js", "ui-core.js");
        var completion = ExtractFunction(app, "overviewStageComplete");
        var criteria = ExtractFunction(app, "overviewCurrentCriteriaArtifact");
        var workpaper = ExtractFunction(app, "overviewCurrentWorkpaperArtifact");
        var sharedCompletion = ExtractFunction(core, "stepComplete");
        var sharedWorkpaper = ExtractFunction(core, "currentWorkpaperArtifact");

        // 2026-10-02 修改原因（W15）：總覽、左側進度與第六步原本各算一份完成判斷，左側寫 5/6 而總覽寫 6 個完成。
        // 判斷改集中在 ui-core 的 stepComplete 與 currentWorkpaperArtifact，總覽只呼叫它們，
        // 原本在 overviewStageComplete 裡找 stepGate 與 overviewCurrentWorkpaperArtifact 的斷言改到共用函式上，
        // 第一次失敗收據 20261002-143207412-706428487ae84c538ac9734c4afe9f33。前五步沿用下一步條件、
        // 最後一步只認目前版本工作底稿的要求不變。
        Assert.Contains("Ui.stepComplete(state, index)", completion, StringComparison.Ordinal);
        Assert.Contains("stepGate(state, index + 1).ok", sharedCompletion, StringComparison.Ordinal);
        Assert.Contains("currentWorkpaperArtifact(state)", sharedCompletion, StringComparison.Ordinal);
        Assert.DoesNotContain("CriteriaArtifact", sharedCompletion, StringComparison.Ordinal);
        Assert.DoesNotContain("overviewCurrentCriteriaArtifact(state)", completion, StringComparison.Ordinal);
        Assert.Contains("Ui.findCurrentReportArtifact", criteria, StringComparison.Ordinal);
        Assert.Contains("scenarioPositions: overviewScenarioPositions(state)", criteria, StringComparison.Ordinal);
        Assert.Contains("Ui.currentWorkpaperArtifact(state)", workpaper, StringComparison.Ordinal);
        Assert.Contains("findCurrentReportArtifact(state, 'workingPaper'", sharedWorkpaper, StringComparison.Ordinal);
        Assert.DoesNotContain("state.currentStepIndex", completion, StringComparison.Ordinal);
        Assert.DoesNotContain("state.currentStepIndex", sharedCompletion, StringComparison.Ordinal);
    }

    private static string ExtractFunction(string source, string name)
    {
        var start = source.IndexOf($"function {name}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到 {name} 函式。");
        var depth = 0;
        var opened = false;
        for (var index = start; index < source.Length; index++)
        {
            if (source[index] == '{') { depth++; opened = true; }
            else if (source[index] == '}' && opened && --depth == 0) { return source[start..(index + 1)]; }
        }
        throw new InvalidOperationException($"找不到 {name} 函式結尾。");
    }

    private static void AssertOrder(string source, params string[] fragments)
    {
        var cursor = -1;
        foreach (var fragment in fragments)
        {
            var next = source.IndexOf(fragment, cursor + 1, StringComparison.Ordinal);
            Assert.True(next > cursor, $"找不到依序片段：{fragment}");
            cursor = next;
        }
    }

    private static string ReadFrontend(params string[] segments)
        => File.ReadAllText(Path.Combine(new[] { FrontendRoot() }.Concat(segments).ToArray()));

    private static string FrontendRoot() => Path.Combine(RepoRoot(), "JET", "wwwroot");

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JET.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("向上尋不到 JET.slnx，無法定位 repo 根。");
    }
}
