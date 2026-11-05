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
        // 刪除案件對話框不再自己接 Escape 與 Tab，改登記到 app.js 的對話框堆疊
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
        // 2026-11-05 小筆電上已完成的步驟收成一列（裁定 2026-11-05-09）：stepSectionHtml 先決定要不要收合，
        // 每一步的那一列改由 stepRowHtml 畫出，目前步驟的標題跟著搬過去。原本在 stepSectionHtml 裡找標題，
        // 改成在 stepRowHtml 裡找，並確認 stepSectionHtml 仍交給它畫。第一次失敗：Public 收據 20261105-040816961-89151c2148e3414e991951ba43508f1d。
        var section = ExtractFunction(app, "stepRowHtml");
        Assert.Contains("stepRowHtml(state, step, index)", ExtractFunction(app, "stepSectionHtml"), StringComparison.Ordinal);
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

    // Gui 的假選檔器靠選檔視窗的標題認出是哪一張卡片在選檔，標題對不上時改開真的 Windows 選檔視窗，Gui 要等到逾時才失敗。
    // 2026-11-05「用 Excel 配對」的選檔標題改了（裁定 2026-11-05-07），假選檔器沒有跟著改，提交前複審才發現。
    // 這是新加的測試：假選檔器寫的每個標題，都要在前端程式裡逐字出現。
    [Fact]
    public void AgentGuiFileChooserFixtures_UseTheTitlesTheScreensPass()
    {
        var fixtures = File.ReadAllText(Path.Combine(RepoRoot(), "JET", "AgentGuiTestFixtures.cs"));
        var scripts = string.Join(
            "\n",
            Directory.GetFiles(Path.Combine(RepoRoot(), "JET", "wwwroot", "js"), "*.js", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        var titles = Regex.Matches(fixtures, @"private const string \w+Title = ""([^""]+)"";")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.Equal(3, titles.Length);
        foreach (var title in titles)
        {
            Assert.True(
                scripts.Contains("'" + title + "'", StringComparison.Ordinal),
                $"假選檔器的標題「{title}」在前端程式裡找不到。");
        }
    }

    [Fact]
    public void ReleaseSurfaces_UseThemeRadiiAndShadowRoles()
    {
        var css = ReadFrontend("css", "app.css");

        // 使用者選定「深藍框架」改版（裁定 2026-10-11-06），取代裁定 2026-10-08-14 的「混合二」版面：圓角依角色分開，
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
