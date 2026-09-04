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
    public void SavedScenarioRemoval_UsesInlineImpactConfirmationAndRestoresFocus()
    {
        var filter = ReadFrontend("js", "steps", "filter-step.js");
        var saved = ExtractFunction(filter, "savedScenariosHtml");
        var binder = ExtractFunction(filter, "bind");
        var viewSync = ExtractFunction(filter, "syncViewState");

        Assert.Contains("將移除此情境，並需重新產生條件篩選報告與底稿", saved, StringComparison.Ordinal);
        Assert.Contains("data-action=\"cancel-remove-scenario\"", saved, StringComparison.Ordinal);
        Assert.Contains("data-action=\"confirm-remove-scenario\"", saved, StringComparison.Ordinal);
        Assert.Contains("viewState.pendingRemovalIndex", binder, StringComparison.Ordinal);
        Assert.Contains("restoreScenarioRemovalFocus", binder, StringComparison.Ordinal);
        Assert.Contains("viewState.pendingRemovalIndex = null", viewSync, StringComparison.Ordinal);
        Assert.Contains("global.JetFocus", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectDeleteDialog_UsesSiblingNativeControlsAndCompleteKeyboardFocusLifecycle()
    {
        var app = ReadFrontend("js", "app.js");
        var row = ExtractFunction(app, "projectRowHtml");
        var binder = ExtractFunction(app, "bindPicker");

        Assert.Contains("<button type=\"button\" class=\"project-row__open\"", row, StringComparison.Ordinal);
        Assert.DoesNotContain("<div class=\"project-row\" role=\"button\"", row, StringComparison.Ordinal);
        Assert.Contains("Focus.move", binder, StringComparison.Ordinal);
        Assert.Contains("Focus.trapDialogTab", binder, StringComparison.Ordinal);
        Assert.Contains("event.key === 'Escape'", binder, StringComparison.Ordinal);
        Assert.Contains("restoreProjectDeleteFocus", binder, StringComparison.Ordinal);
    }

    [Fact]
    public void DataPreview_ReplacesStaleContentWithNamedLoadingAndInlineRetryableFailure()
    {
        var preview = ReadFrontend("js", "data-preview.js");
        var refresh = ExtractFunction(preview, "refresh");

        Assert.Contains("function renderPreviewLoading(", preview, StringComparison.Ordinal);
        Assert.Contains("function renderPreviewError(", preview, StringComparison.Ordinal);
        Assert.Contains("setAttribute('aria-busy', 'true')", preview, StringComparison.Ordinal);
        Assert.Contains("removeAttribute('aria-busy')", preview, StringComparison.Ordinal);
        Assert.Contains("正在載入", preview, StringComparison.Ordinal);
        Assert.Contains("載入失敗", preview, StringComparison.Ordinal);
        Assert.Contains("data-action=\"retry-data-preview\"", preview, StringComparison.Ordinal);
        Assert.Contains("previewResponseGuard.issue", refresh, StringComparison.Ordinal);
        AssertOrder(refresh, "previewResponseGuard.issue", "renderPreviewLoading(dataset)", "Ui.runBackground");
    }

    [Fact]
    public void BusyChrome_UsesMonotonicElapsedClockAndExactLongOperationGuidance()
    {
        var app = ReadFrontend("js", "app.js");
        var markup = ReadFrontend("index.html");

        Assert.Contains("busyElapsedTimer", app, StringComparison.Ordinal);
        Assert.Contains("global.performance.now()", app, StringComparison.Ordinal);
        Assert.Contains("'validate.run'", app, StringComparison.Ordinal);
        Assert.Contains("'prescreen.run'", app, StringComparison.Ordinal);
        Assert.Contains("大型案件可能需要較長時間，可取消", app, StringComparison.Ordinal);
        Assert.Contains("data-bind=\"busy-elapsed\"", markup, StringComparison.Ordinal);
        Assert.Contains("data-bind=\"busy-guidance\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("剩餘時間", app, StringComparison.Ordinal);
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
    public void CreateProjectFailure_UsesStructuredBackendFieldWithoutGuessingFromCodeOrMessage()
    {
        var create = ReadFrontend("js", "steps", "create-step.js");
        var showError = ExtractFunction(create, "showCreateError");

        Assert.Contains("data-bind=\"create-error\"", create, StringComparison.Ordinal);
        Assert.Contains("role=\"alert\"", create, StringComparison.Ordinal);
        Assert.Contains("onError: function", create, StringComparison.Ordinal);
        Assert.Contains("error.message", showError, StringComparison.Ordinal);
        Assert.Contains("error.field === 'caseName'", showError, StringComparison.Ordinal);
        Assert.Contains("form.elements.caseName", showError, StringComparison.Ordinal);
        Assert.Contains("aria-invalid", showError, StringComparison.Ordinal);
        Assert.Contains("global.JetFocus.defer(target)", showError, StringComparison.Ordinal);
        Assert.Contains("err.field =", ReadFrontend("js", "jet-api.js"), StringComparison.Ordinal);
        Assert.DoesNotContain("canAttributeInvalidPayloadToCaseName", create, StringComparison.Ordinal);
        Assert.DoesNotContain("error.code === 'invalid_payload'", showError, StringComparison.Ordinal);
        Assert.DoesNotContain(".includes(", showError, StringComparison.Ordinal);
        Assert.DoesNotContain("indexOf(", showError, StringComparison.Ordinal);
        Assert.DoesNotContain("match(", showError, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseStructuralSurfaces_AreSquareAndFlatWhileSemanticPillsAndOverviewShadowRemain()
    {
        var css = ReadFrontend("css", "app.css");

        AssertCssDeclaration(css, ".picker-panel", "border-radius", "0");
        AssertCssDeclaration(css, ".modal__card", "border-radius", "0");
        AssertCssDeclaration(css, ".project-row:hover", "box-shadow", "none");
        Assert.Matches(
            new Regex(@"\.project-provider\s*\{[^}]*border-radius:\s*9999px;", RegexOptions.Singleline),
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
