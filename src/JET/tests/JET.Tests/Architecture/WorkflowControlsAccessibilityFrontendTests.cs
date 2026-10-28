using Xunit;

namespace JET.Tests.Architecture;

// 預篩選收斂計畫階段十（2026-10-25）：filter-step.js 依職責拆成五個檔案。讀第五步前端的斷言改用 ProductSources.FilterStepFrontend()，
// 把五個檔案合起來讀，範圍和拆檔前讀單一檔案相同；斷言本身不變。第一次失敗：Focused 收據 20261025-053438454-333c59b548b548008f977941c5502503。

/// <summary>
/// 前端工作流整合新增控制項的可用性守衛：每個輸入都有可讀名稱、群組選項用 fieldset 與 legend、
/// 新區塊在窄視窗可收縮或自行捲動。實際縮放與 Windows DPI 仍由人工驗收。
/// 停用的控制項要說明原因，2026-10-14 起改由 <c>tools/tests/frontend-journey-mapping.test.cjs</c>
/// 與 <c>frontend-journey-step4.test.cjs</c> 在畫面上核對。
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
        var filter = ProductSources.FilterStepFrontend();
        var core = ReadFrontend("js", "ui-core.js");

        Assert.Contains("<fieldset class=\"map-options__group\">", mapping, StringComparison.Ordinal);
        Assert.Contains("<legend class=\"map-options__legend\">", mapping, StringComparison.Ordinal);
        Assert.Contains("aria-describedby=\"gl-approval-help\"", mapping, StringComparison.Ordinal);

        // 第二遍第 5 批改由共同分類樹輸出群組。首次失敗收據：
        // 20261004-075340533-87179f5019724ec89d7c19b4243fdb09；仍要求實際輸出 fieldset、legend 及有名稱的控制項。
        Assert.Contains("Ui.taxonomyPickerHtml(state, selected, idsKey, legend, view)", filter, StringComparison.Ordinal);
        var pickerStart = core.IndexOf("function taxonomyPickerHtml(", StringComparison.Ordinal);
        var pickerEnd = core.IndexOf("function bindTaxonomyPicker(", StringComparison.Ordinal);
        Assert.True(pickerStart >= 0 && pickerEnd > pickerStart, "找不到共同分類樹的呈現函式。");
        var picker = core[pickerStart..pickerEnd];
        Assert.Contains("<fieldset class=\"category-select\" data-category-picker=", picker, StringComparison.Ordinal);
        Assert.Contains("<legend class=\"category-select__legend\">' + esc(legend) + '</legend>", picker, StringComparison.Ordinal);
        // 2026-10-08 第 13 則移除搜尋，不移除有名稱的分類勾選與展開控制。
        Assert.DoesNotContain("data-category-search", picker, StringComparison.Ordinal);
        Assert.Contains("<label class=\"category-option\"><input type=\"checkbox\"", picker, StringComparison.Ordinal);
        Assert.Contains("<span>' + esc(category.label) + '</span></label>", picker, StringComparison.Ordinal);
        // 第九節 F 把展開按鈕移到三處共用的樹函式，fieldset 與勾選語意仍留在選取器。
        // 首次失敗：20261014-130014323-112f3ca2d352414ba9de606d2b6e0e09；仍檢查名稱及展開狀態。
        Assert.Contains("taxonomyTreeHtml(state, view, function (category)", picker, StringComparison.Ordinal);
        var treeStart = core.IndexOf("function taxonomyTreeHtml(", StringComparison.Ordinal);
        Assert.True(treeStart >= 0 && treeStart < pickerStart);
        var tree = core[treeStart..pickerStart];
        // 使用者 2026-10-19 要求同功能元件只留一套：展開按鈕改用共用的 disclosureAttributes，名稱改說是哪一類的下層分類。
        // 首次失敗：20261019-102446198-5198acd5b7e747feae8ed16dcfac32a3；仍檢查名稱及展開狀態。
        Assert.Contains("aria-label=\"' + esc(category.label) + '的下層分類\"' +", tree, StringComparison.Ordinal);
        Assert.Contains("disclosureAttributes(open) + '></button>'", tree, StringComparison.Ordinal);
        Assert.Contains("return ' aria-expanded=\"' + (open ? 'true' : 'false') + '\"'", core, StringComparison.Ordinal);
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
            ".category-select {"
        })
        {
            var index = css.IndexOf(selector, StringComparison.Ordinal);
            Assert.True(index >= 0, $"app.css 找不到 {selector}");
            var block = css[index..(css.IndexOf('}', index) + 1)];
            Assert.Contains("min-width: 0", block, StringComparison.Ordinal);
        }

        // 第 11 則移除分錄摘要；剩下的長清單仍自行捲動，不把整頁撐長。
        var lists = css[css.IndexOf(".value-option-list,", StringComparison.Ordinal)..];
        Assert.Contains("overflow-y: auto", lists, StringComparison.Ordinal);
        Assert.DoesNotContain(".population-summary {", css, StringComparison.Ordinal);
        Assert.Contains(".stats-bar--validation { grid-template-columns: repeat(3, minmax(0, 1fr)); }", css, StringComparison.Ordinal);

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
