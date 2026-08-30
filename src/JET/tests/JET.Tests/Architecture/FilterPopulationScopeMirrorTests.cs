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
        Assert.Contains("以查核期間重新保存", filter, StringComparison.Ordinal);
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
