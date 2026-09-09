using Xunit;

namespace JET.Tests.Architecture;

/// <summary>第五步的工作責任守衛；實際操作另由 filter-auditor-journey 驗證。</summary>
public sealed class FilterWorkspaceFrontendTests
{
    private static string Source()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JET.slnx"))) directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory!.FullName, "JET", "wwwroot", "js", "steps", "filter-step.js"));
    }

    [Fact]
    public void ConditionEntry_DoesNotPretendThatScenarioTemplatesAreCustomConditions()
    {
        var source = Source();
        var start = source.IndexOf("  function customPickerHtml(", StringComparison.Ordinal);
        var end = source.IndexOf("\n  }", start, StringComparison.Ordinal);
        var picker = source[start..end];
        Assert.DoesNotContain("templatePickerHtml", picker, StringComparison.Ordinal);
        Assert.Contains("addRuleBarHtml", picker, StringComparison.Ordinal);
        Assert.DoesNotContain("✦", source, StringComparison.Ordinal);
        Assert.DoesNotContain("★", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SavingAndSavedResults_HaveSeparateProgressivelyDisclosedPlaces()
    {
        var source = Source();
        Assert.Contains("data-filter-pane=\"filter\"", source, StringComparison.Ordinal);
        Assert.Contains("data-filter-pane=\"saved\"", source, StringComparison.Ordinal);
        Assert.Contains("data-save-panel", source, StringComparison.Ordinal);
        Assert.Contains("viewState.saveOpen", source, StringComparison.Ordinal);
        Assert.Contains("data-action=\"open-save\"", source, StringComparison.Ordinal);
        Assert.Contains("data-matrix-view", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SimpleConditions_HaveVisibleRemovalAndDiscoverableGroups()
    {
        var source = Source();
        Assert.Contains("title=\"移除這條條件\">移除</button>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<p class=\"filter-draft-note\">", source, StringComparison.Ordinal);
        Assert.Contains("data-action=\"add-set\">新增條件組</button>", source, StringComparison.Ordinal);
        Assert.Contains("group.rules.length > 1", source, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupEditor_DoesNotRelocateTheConditionCatalog()
    {
        var source = Source();
        var start = source.IndexOf("  function setWellHtml(", StringComparison.Ordinal);
        var end = source.IndexOf("\n  }", start, StringComparison.Ordinal);
        var groupEditor = source[start..end];
        Assert.DoesNotContain("kctPickerHtml", groupEditor, StringComparison.Ordinal);
        Assert.DoesNotContain("customPickerHtml", groupEditor, StringComparison.Ordinal);
        Assert.DoesNotContain("conditionChoicesHtml", groupEditor, StringComparison.Ordinal);
        Assert.DoesNotContain("data-inline-picker", source, StringComparison.Ordinal);
        Assert.DoesNotContain("data-pick-for-group", source, StringComparison.Ordinal);
        Assert.DoesNotContain("data-select-group", groupEditor, StringComparison.Ordinal);
        Assert.Contains("data-condition-target>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("目前加入目標", source, StringComparison.Ordinal);
        Assert.Contains("rule.type === 'prescreen' ? '預篩選訊號'", source, StringComparison.Ordinal);
    }
}
