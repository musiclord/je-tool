using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 2026-08-20 已核准 UX 項目的前端來源守衛。這些斷言只鎖交互與呈現，
/// 不新增 wire、schema 或審計語意。
/// </summary>
public sealed class ApprovedUxFrontendTests
{
    [Fact]
    public void ProjectDeleteDialog_UsesSiblingNativeControlsAndCompleteKeyboardFocusLifecycle()
    {
        var app = ReadFrontend("js", "app.js");
        var row = ExtractFunction(app, "projectRowHtml");
        var binder = ExtractFunction(app, "bindPicker");

        Assert.Contains("<button type=\"button\" class=\"project-row__open\"", row, StringComparison.Ordinal);
        Assert.DoesNotContain("<div class=\"project-row\" role=\"button\"", row, StringComparison.Ordinal);
        Assert.Contains("Focus.move", binder, StringComparison.Ordinal);
        // 預篩選收斂計畫階段八（2026-10-24）：刪除案件對話框不再自己接 Escape 與 Tab，改登記到 app.js 的對話框堆疊
        //（JetFocus.openDialog 與 closeDialog），鍵盤由堆疊只交給最上層。原本斷言 bindPicker 內含 Focus.trapDialogTab 與
        // event.key === 'Escape'，第一次失敗收據 20261024-150719374-622a89799ae54dbc9470318f14899ebe。
        Assert.Contains("Focus.openDialog(", binder, StringComparison.Ordinal);
        Assert.Contains("Focus.closeDialog(", binder, StringComparison.Ordinal);
        Assert.Contains("restoreProjectDeleteFocus", binder, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkflowNavigation_FocusesTheNewHeadingAndAnnouncesItsLocation()
    {
        var app = ReadFrontend("js", "app.js");
        var markup = ReadFrontend("index.html");
        var css = ReadFrontend("css", "app.css");
        var section = ExtractFunction(app, "stepSectionHtml");
        var content = ExtractFunction(app, "renderContent");
        var focus = ExtractFunction(app, "focusCurrentStepHeading");

        Assert.Contains("data-bind=\"current-step-title\"", section, StringComparison.Ordinal);
        Assert.Contains("tabindex=\"-1\"", section, StringComparison.Ordinal);
        Assert.Contains("focusCurrentStepHeading", content, StringComparison.Ordinal);
        Assert.Contains("latest.view !== 'workflow'", focus, StringComparison.Ordinal);
        Assert.Contains("latest.currentStepIndex !== stepIndex", focus, StringComparison.Ordinal);
        Assert.Contains("data-bind=\"step-announcer\"", markup, StringComparison.Ordinal);
        Assert.True(
            markup.IndexOf("data-bind=\"step-announcer\"", StringComparison.Ordinal) >
            markup.IndexOf("</main>", StringComparison.Ordinal),
            "切步宣告必須位於 inert 主樹之外。");
        Assert.Matches(
            new Regex(@"\.stepflow-item\[data-step-nav\]:focus-visible[\s\S]*?outline:\s*2px", RegexOptions.CultureInvariant),
            css);
    }

    [Fact]
    public void ReleaseSurfaces_UseThemeRadiiAndShadowRoles()
    {
        var css = ReadFrontend("css", "app.css");

        // 使用者 2026-10-11 選定「深藍框架」改版，取代 2026-10-08 的「混合二」：圓角依角色分開，
        // 控制項 6px、工作面與對話框 12px，狀態標籤是膠囊、按鈕不是；工作面只有極淡的落影，浮動層才有明顯陰影。
        // 舊斷言（控制項 4px、工作面 8px、只有浮動層有陰影）第一次失敗：20261010-162448026-856212538f8d4f0ba6043a613466e359。
        AssertCssDeclaration(css, ":root", "--radius-control", "6px");
        AssertCssDeclaration(css, ":root", "--radius-surface", "12px");
        AssertCssDeclaration(css, ":root", "--radius-pill", "999px");
        AssertCssDeclaration(css, ".picker-panel", "border-radius", "var(--radius-surface)");
        AssertCssDeclaration(css, ".modal__card", "border-radius", "var(--radius-surface)");
        AssertCssDeclaration(css, ".btn", "border-radius", "var(--radius-control)");
        AssertCssDeclaration(css, ".rule-status", "border-radius", "var(--radius-pill)");
        AssertCssDeclaration(css, ".rule-card", "box-shadow", "var(--shadow-surface)");
        AssertCssDeclaration(css, ".project-row:hover", "box-shadow", "none");
        AssertCssDeclaration(css, ".project-provider", "border-radius", "var(--radius-control)");
        Assert.DoesNotMatch(
            new Regex(@"\.project-provider\s*\{[^}]*border-radius:\s*9999px;", RegexOptions.Singleline),
            css);
        Assert.DoesNotMatch(
            new Regex(@"\.btn\s*\{[^}]*border-radius:\s*var\(--radius-pill\)", RegexOptions.Singleline),
            css);
        Assert.Matches(
            new Regex(@"\.overview-modal__card\s*\{[^}]*box-shadow:\s*(?!none)[^;]+;", RegexOptions.Singleline),
            css);
    }

    private static void AssertCssDeclaration(string css, string selector, string property, string value)
    {
        Assert.Matches(
            new Regex(
                $@"{Regex.Escape(selector)}\s*\{{[^}}]*{Regex.Escape(property)}:\s*{Regex.Escape(value)}\s*;",
                RegexOptions.Singleline),
            css);
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
