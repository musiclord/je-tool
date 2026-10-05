using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 前端沒有 WebView2 自動化測試；這組 source mirror 只鎖 auditPeriod-only 的 wire 與狀態邊界，
/// 不在 JavaScript 重測任何篩選業務規則。
/// </summary>
public sealed class FilterPopulationScopeMirrorTests
{
    [Fact]
    public void FilterState_UsesAuditPeriodAsTheOnlyRuntimeScope()
    {
        var state = ReadFrontend("js", "state.js");
        var core = ReadFrontend("js", "ui-core.js");

        Assert.Contains("populationScope: 'auditPeriod'", state, StringComparison.Ordinal);
        Assert.DoesNotContain("allProjected", state, StringComparison.Ordinal);
        Assert.Contains("setFilterPopulationScope: function", state, StringComparison.Ordinal);
        Assert.Contains("restoreFilterPopulationScope: function", state, StringComparison.Ordinal);
        Assert.Contains("state.filter.preview = null", state, StringComparison.Ordinal);
        Assert.Contains(
            "Store.restoreFilterPopulationScope(data.filterResultRef || null, data.filterScenarios || [])",
            core,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FilterActions_AlwaysSendAuditScopeAndRejectLatePreviewAcrossDraftChanges()
    {
        var source = ReadFrontend("js", "steps", "filter-step.js");

        Assert.Contains("var POPULATION_SCOPE_AUDIT = 'auditPeriod'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("allProjected", source, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(
            source,
            @"JetApi\.filterPreview\(\{\s*populationScope:\s*populationScope",
            RegexOptions.Multiline).Count);
        Assert.Equal(3, Regex.Matches(
            source,
            @"JetApi\.filterCommit\(\{\s*populationScope:\s*populationScope",
            RegexOptions.Multiline).Count);
        Assert.Contains(
            "draftResponseGuard.issue",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "selectedPopulationScope(latest) === populationScope",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "data.scenario.populationScope !== populationScope",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MissingCommittedRevision_DisablesDetailMatrixAndCriteriaReportUntilResave()
    {
        var source = ReadFrontend("js", "steps", "filter-step.js");

        Assert.Contains("data-action=\"resave-scenarios\"", source, StringComparison.Ordinal);
        Assert.Contains(
            "var populationScope = committedScope || selectedPopulationScope(currentState)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("var allowLoadMore = !!committedScope", source, StringComparison.Ordinal);
        Assert.Contains("if (!allowLoadMore) { return; }", source, StringComparison.Ordinal);
        Assert.Contains("if (!committedPopulationScope(state))", source, StringComparison.Ordinal);
        Assert.Contains(
            "var canExport = !!committedScope",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RetiredScopeSelectorAndPeriodCondition_AreAbsentFromRuntimeFrontend()
    {
        var filter = ReadFrontend("js", "steps", "filter-step.js");
        var core = ReadFrontend("js", "ui-core.js");
        var export = ReadFrontend("js", "steps", "export-step.js");

        Assert.DoesNotContain("periodInOut", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("periodInOut", core, StringComparison.Ordinal);
        Assert.DoesNotContain("allProjected", export, StringComparison.Ordinal);
        Assert.DoesNotContain("data-population-scope", filter, StringComparison.Ordinal);
        Assert.Contains("data-action=\"resave-scenarios\"", filter, StringComparison.Ordinal);
        // 2026-10-02 起開案時自動改用目前規則，按鈕不再處理版本問題，只用在補回欄位配對後重新檢查；
        // 原本鎖住的「以查核期間重新保存」字樣改為新按鈕名稱。
        Assert.Contains("重新檢查並儲存情境", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("以查核期間重新保存", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void ScopeAndFilterDataInvalidation_PreserveExpandedSavedViews()
    {
        var filter = ReadFrontend("js", "steps", "filter-step.js");
        var state = ReadFrontend("js", "state.js");
        var generation = Regex.Match(
            filter,
            @"function dataGeneration\(state\)\s*\{(?<body>.*?)\n\s*\}",
            RegexOptions.Singleline);

        Assert.True(generation.Success, "filter-step.js 找不到 dataGeneration helper");
        Assert.Contains("imp.gl", generation.Groups["body"].Value, StringComparison.Ordinal);
        Assert.DoesNotContain("imp.tb", generation.Groups["body"].Value, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "state.filter.savedScenarios = state.filter.savedScenarios.slice()",
            state,
            StringComparison.Ordinal);
        Assert.Contains("viewState.scenarioPreviews = {}", filter, StringComparison.Ordinal);
        Assert.Contains("viewState.matrixData = null", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadTimeRuleUpgrade_ShowsRecalculationNoticeProblemListAndRecheckButton()
    {
        // 2026-10-02：開案時自動改用目前規則；無法套用時第五步逐一列出情境與下一步。
        var core = ReadFrontend("js", "ui-core.js");
        var state = ReadFrontend("js", "state.js");
        var filter = ReadFrontend("js", "steps", "filter-step.js");
        var export = ReadFrontend("js", "steps", "export-step.js");

        Assert.Contains("Store.setFilterScenarioCheck(data.filterScenarioCheck || null)", core, StringComparison.Ordinal);
        Assert.Contains("filterScenarioCheck: null", state, StringComparison.Ordinal);
        Assert.Contains("setFilterScenarioCheck: function (check)", state, StringComparison.Ordinal);
        Assert.Contains("'篩選規則已更新，JET 已用新規則重新計算 ' + check.recalculatedCount +", core, StringComparison.Ordinal);
        Assert.Contains("' 個已儲存情境；先前的條件篩選報告與底稿已標為過期，需要時請重新產生。'", core, StringComparison.Ordinal);
        Assert.Contains("'」無法套用目前的篩選規則：' +", core, StringComparison.Ordinal);
        Assert.Contains("'請到「進階條件篩選」開啟這個情境修改後儲存；' +", core, StringComparison.Ordinal);
        Assert.Contains("'如果原因是欄位配對少了欄位，請先到「欄位配對」補回，再按「重新檢查並儲存情境」。'", core, StringComparison.Ordinal);
        // 2026-10-02 修改原因（W17）：第六步改成「有可用情境或已有工作底稿版本紀錄」就能進入，
        // 情境缺少的原因集中到 filterScenarioMissing 回傳，stepGate 只在兩者都沒有時加入。
        // 原斷言的 missing.push 字樣因此不存在，第一次失敗收據 20261002-143316067-2c640e57d4b8434e8e42466b6208b7ed。
        // 原因字樣不變，並要求 stepGate 仍使用它。
        Assert.Contains("return '修改無法套用目前規則的篩選情境';", core, StringComparison.Ordinal);
        Assert.Contains("var scenarioMissing = filterScenarioMissing(state);", core, StringComparison.Ordinal);
        Assert.Contains("missing.push(scenarioMissing)", core, StringComparison.Ordinal);
        Assert.DoesNotContain("保存目前版本的篩選情境", core, StringComparison.Ordinal);

        Assert.Contains("'重新檢查並儲存情境</button>'", filter, StringComparison.Ordinal);
        Assert.Contains("Ui.run('重新檢查並儲存情境'", filter, StringComparison.Ordinal);
        Assert.Contains("Ui.filterScenarioProblemsHtml(state)", export, StringComparison.Ordinal);
        foreach (var retired in new[] { "較早的版本", "以查核期間重新保存" })
        {
            Assert.DoesNotContain(retired, filter, StringComparison.Ordinal);
            Assert.DoesNotContain(retired, export, StringComparison.Ordinal);
        }
    }

    private static string ReadFrontend(params string[] relativeSegments)
    {
        var path = Path.Combine(new[] { RepoRoot(), "JET", "wwwroot" }
            .Concat(relativeSegments)
            .ToArray());
        return File.ReadAllText(path);
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
