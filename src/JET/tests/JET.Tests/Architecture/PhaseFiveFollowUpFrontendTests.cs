using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 2026-10-03 使用者回覆第 5 階段待決清單後的補充修正（O5、O7、O8、O9）。畫面行為由 Node 前端測試與桌面情境驗證，
/// 這裡鎖住文字與判斷的來源，避免舊字樣回來。
/// </summary>
public sealed class PhaseFiveFollowUpFrontendTests
{
    [Fact]
    public void ExportStepSummary_FollowsTheCurrentWorkpaper()
    {
        var app = ReadFrontend("js", "app.js");
        var summary = Between(app, "function stepSummary(", "function entryConditionHtml(");

        Assert.DoesNotContain("準備匯出查核底稿", app, StringComparison.Ordinal);
        // 和左側進度、第六步的完成用同一個判斷，不另寫一份。
        Assert.Contains("Ui.currentWorkpaperArtifact(state)", summary, StringComparison.Ordinal);
        Assert.Contains("'匯出工作底稿'", summary, StringComparison.Ordinal);
        Assert.Contains("'已匯出工作底稿，' + exportedAt", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualAutoLegend_UsesTheSameSlashAsTheFieldName()
    {
        var mapping = ReadFrontend("js", "steps", "mapping-step.js");

        Assert.DoesNotContain("人工／自動分錄代碼", mapping, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(mapping, "<legend class=\"map-options__legend\">人工/自動分錄代碼</legend>"));
        Assert.Contains("{ key: 'manual', label: '人工/自動分錄'", ReadFrontend("js", "ui-core.js"), StringComparison.Ordinal);
    }

    [Fact]
    public void OverviewHead_PutsTheNameInTheTitleAndDoesNotRepeatIt()
    {
        var head = Between(ReadFrontend("js", "app.js"), "function overviewHeadHtml(", "分錄測試範圍組成：一欄一個明確事實");

        Assert.DoesNotContain("—", head, StringComparison.Ordinal);
        Assert.DoesNotContain("流程總覽，案件狀態", head, StringComparison.Ordinal);
        Assert.Contains("var eyebrow = '分錄測試流程總覽';", head, StringComparison.Ordinal);
        Assert.Contains("var showCaseName = !!state.project && !!caseName && caseName !== title;", head, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyExampleScenarioName_StartsWithLegacyFormLabel()
    {
        var filter = ReadFrontend("js", "steps", "filter-step.js");

        Assert.Contains("name: '舊表 ' + example.combination + ' ' + example.label", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("name: example.combination + ' ' + example.label", filter, StringComparison.Ordinal);
    }

    private static int CountOf(string source, string value)
    {
        var count = 0;
        for (var index = source.IndexOf(value, StringComparison.Ordinal); index >= 0;
             index = source.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "找不到起點：" + start);
        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, "找不到終點：" + end);
        return source[from..to];
    }

    private static string ReadFrontend(params string[] segments)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot(), "JET", "wwwroot" }.Concat(segments).ToArray()));

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JET.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("向上尋不到 JET.slnx。");
    }
}
