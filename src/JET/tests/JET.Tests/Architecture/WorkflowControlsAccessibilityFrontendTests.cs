using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 前端工作流整合新增控制項的可用性守衛：每個輸入都有可讀名稱、狀態不只靠顏色、
/// 新區塊在窄視窗可收縮或自行捲動。實際縮放與 Windows DPI 仍由人工驗收。
/// </summary>
public sealed class WorkflowControlsAccessibilityFrontendTests
{
    [Fact]
    public void EveryNewInputHasAProgrammaticName()
    {
        var mapping = ReadFrontend("js", "steps", "mapping-step.js");
        var validate = ReadFrontend("js", "steps", "validate-step.js");
        var css = ReadFrontend("css", "app.css");

        // 只以視覺鄰近表達的欄位一律補 visually-hidden label（下拉、單行輸入、清單項目）。
        Assert.Contains("class=\"visually-hidden\" for=\"rde-label-", mapping, StringComparison.Ordinal);
        Assert.Contains("class=\"visually-hidden\" for=\"rde-type-", mapping, StringComparison.Ordinal);
        Assert.Contains("class=\"visually-hidden\" for=\"manual-code-add\"", mapping, StringComparison.Ordinal);
        Assert.Contains("class=\"visually-hidden\" for=\"automatic-code-add\"", mapping, StringComparison.Ordinal);
        // 9/21 分類標籤改為直接可見，for 仍對應原本控制項的 id。
        Assert.Contains("<label for=\"taxonomy-label-", validate, StringComparison.Ordinal);
        Assert.Contains("<label for=\"taxonomy-role-", validate, StringComparison.Ordinal);
        Assert.Contains("id=\"taxonomy-label-", validate, StringComparison.Ordinal);
        Assert.Contains("id=\"taxonomy-role-", validate, StringComparison.Ordinal);

        // 移除鈕是純符號按鈕，必須帶可讀名稱。
        Assert.Contains("aria-label=\"移除 ", mapping, StringComparison.Ordinal);

        Assert.Contains(".visually-hidden {", css, StringComparison.Ordinal);
        Assert.Contains("clip: rect(0 0 0 0)", css, StringComparison.Ordinal);
    }

    [Fact]
    public void VisuallyHiddenLabels_StayOutOfLayoutAndHitTesting()
    {
        var css = ReadFrontend("css", "app.css");
        var index = css.IndexOf(".visually-hidden {", StringComparison.Ordinal);
        Assert.True(index >= 0, "app.css 找不到 .visually-hidden。");
        var block = css[index..(css.IndexOf('}', index) + 1)];

        // 兩個座標都必須明示：省略時元素落在靜態位置，而它的 containing block 是
        // .app-shell（中間的 overflow:hidden 不會裁切 absolutely positioned 後代），
        // 因此可能落到視窗外並撐大文件捲動範圍，把標題列捲出視窗。
        Assert.Contains("position: absolute", block, StringComparison.Ordinal);
        Assert.Contains("left: 0", block, StringComparison.Ordinal);
        Assert.Contains("top: 0", block, StringComparison.Ordinal);

        // clip 只影響繪製；clip-path 與 pointer-events 才真正讓它不參與點擊命中。
        Assert.Contains("clip-path: inset(50%)", block, StringComparison.Ordinal);
        Assert.Contains("pointer-events: none", block, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupedChoicesUseFieldsetAndLegendInsteadOfBareDivs()
    {
        var mapping = ReadFrontend("js", "steps", "mapping-step.js");
        var filter = ReadFrontend("js", "steps", "filter-step.js");

        Assert.Contains("<fieldset class=\"map-options__group\">", mapping, StringComparison.Ordinal);
        Assert.Contains("<legend class=\"map-options__legend\">", mapping, StringComparison.Ordinal);
        Assert.Contains("aria-describedby=\"gl-approval-help\"", mapping, StringComparison.Ordinal);

        Assert.Contains("<fieldset class=\"category-select\">", filter, StringComparison.Ordinal);
        Assert.Contains("<legend class=\"category-select__legend\">", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledControlsExplainWhyInsteadOfSilentlyLocking()
    {
        var validate = ReadFrontend("js", "steps", "validate-step.js");
        var mapping = ReadFrontend("js", "steps", "mapping-step.js");

        // 停用的刪除鈕帶 title 說明原因；被擋下的提交在按鈕上方列出缺什麼。
        Assert.Contains("' disabled title=\"' + Ui.esc(lockedReason) + '\"'", validate, StringComparison.Ordinal);
        Assert.Contains("data-bind=\"taxonomy-problems\"", validate, StringComparison.Ordinal);
        Assert.Contains("data-bind=\"mapping-option-problems\"", mapping, StringComparison.Ordinal);
        Assert.Contains("尚需補齊：", mapping, StringComparison.Ordinal);
    }

    [Fact]
    public void NewBlocksShrinkOrScrollInsteadOfWideningTheDocumentFlow()
    {
        var css = ReadFrontend("css", "app.css");

        foreach (var selector in new[]
        {
            ".map-options {",
            ".map-options__group {",
            ".value-option {",
            ".rde-field {",
            ".taxonomy-row {",
            ".population-summary {",
            ".category-select {"
        })
        {
            var index = css.IndexOf(selector, StringComparison.Ordinal);
            Assert.True(index >= 0, $"app.css 找不到 {selector}");
            var block = css[index..(css.IndexOf('}', index) + 1)];
            Assert.Contains("min-width: 0", block, StringComparison.Ordinal);
        }

        // 長清單自行捲動，不把整頁撐長；母體分區在窄視窗自動換行。
        var lists = css[css.IndexOf(".value-option-list,", StringComparison.Ordinal)..];
        Assert.Contains("overflow-y: auto", lists, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: repeat(auto-fit, minmax(148px, 1fr))", css, StringComparison.Ordinal);

        // 來源欄名與分類名稱可能很長：允許在任意位置換行，不撐破容器。
        Assert.Contains("overflow-wrap: anywhere", css, StringComparison.Ordinal);
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

        return directory?.FullName ?? throw new InvalidOperationException("向上尋不到 JET.slnx。");
    }
}
