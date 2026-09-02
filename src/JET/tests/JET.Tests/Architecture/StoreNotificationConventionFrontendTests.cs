using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 前端 store 通知慣例守衛：凡是會改變衍生畫面輸出的寫入一律 bump；「只 notify」僅限連續文字
/// 輸入且必須在 blur 邊界配一次延遲 bump 收斂。重建後的焦點與捲動由 renderContent 框架層依焦點
/// 識別屬性與 data-preserve-scroll 統一還原，步驟模組不得各自發明保留機制。歷史根因（classic
/// 配對介面右側必填檢查延遲）與收斂裁定見 docs/specs/2026-09-01-frontend-sync-devlog-mutation-plan.md。
/// </summary>
public sealed class StoreNotificationConventionFrontendTests
{
    [Fact]
    public void SetMappingDraft_BumpsSoDerivedViewsRebuild()
    {
        var state = ReadFrontend("js", "state.js");
        var body = ExtractBetween(state, "setMappingDraft: function", "assignColumnToField: function");

        // 草稿餵給必填鐵軌、「確認配對」可用性與 GL 政策區分支，不得退回只 notify 的舊行為。
        Assert.Contains("bump();", body, StringComparison.Ordinal);
        Assert.DoesNotContain("notify();", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MappingKeyHandler_HasNoConditionalRebuildBypass()
    {
        var mapping = ReadFrontend("js", "steps", "mapping-step.js");

        // 舊補丁「INPUT 或已提交才 touch」讓全新草稿的 SELECT 指派不重繪；不得復發。
        Assert.DoesNotContain("control.tagName === 'INPUT'", mapping, StringComparison.Ordinal);
        Assert.Contains(
            "Store.setMappingDraft(kind, control.getAttribute('data-mapping-key'), control.value.trim());",
            mapping,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RenderContent_RestoresMarkedScrollContainersAndInputFocus()
    {
        var app = ReadFrontend("js", "app.js");

        Assert.Contains("function captureContentFocusSelector(", app, StringComparison.Ordinal);
        Assert.Contains("function captureContentScrollPositions(", app, StringComparison.Ordinal);
        Assert.Contains("function restoreContentScrollPositions(", app, StringComparison.Ordinal);
        Assert.Contains("querySelectorAll('[data-preserve-scroll]')", app, StringComparison.Ordinal);
        Assert.Contains("active.getAttribute('data-focus-key')", app, StringComparison.Ordinal);
        Assert.DoesNotContain("FOCUS_IDENTITY_ATTRIBUTES", app, StringComparison.Ordinal);

        // 還原只在同一步驟內的重繪發生；跨步切換由 focusCurrentStepHeading 接手起始焦點。
        var content = ExtractBetween(app, "function renderContent(", "function renderMessages(");
        Assert.Contains("restoreContentScrollPositions(container, scrollPositions);", content, StringComparison.Ordinal);
        Assert.Contains("if (!stepChanged) {", content, StringComparison.Ordinal);
        Assert.Contains("focusElement(container.querySelector(focusSelector));", content, StringComparison.Ordinal);
    }

    [Fact]
    public void MappingStep_UsesFrameworkScrollPreservationInsteadOfLocalState()
    {
        var mapping = ReadFrontend("js", "steps", "mapping-step.js");

        // 步驟私有的捲動暫存已退役；對照表格與簡易清單容器都交由框架層識別。
        Assert.DoesNotContain("pendingGridScroll", mapping, StringComparison.Ordinal);
        Assert.Contains("data-preserve-scroll=\"map-grid-' + kind + '\"", mapping, StringComparison.Ordinal);
        Assert.Contains("data-preserve-scroll=\"mapping-table-' + kind + '\"", mapping, StringComparison.Ordinal);
    }

    [Fact]
    public void RdeLabelContinuousInput_ConvergesWithADeferredBumpAtBlurBoundary()
    {
        var mapping = ReadFrontend("js", "steps", "mapping-step.js");
        var block = ExtractBetween(
            mapping,
            "section.querySelectorAll('[data-rde-label]')",
            "section.querySelectorAll('[data-rde-type]')");

        // input 事件維持只 notify（保住輸入焦點），blur 後延遲一輪再 bump，避免同步重繪搶焦點。
        Assert.Contains("patchGlMappingOptionsQuiet", block, StringComparison.Ordinal);
        Assert.Contains("addEventListener('blur'", block, StringComparison.Ordinal);
        Assert.Contains("global.setTimeout(Store.touch, 0)", block, StringComparison.Ordinal);
        Assert.DoesNotContain("addEventListener('change'", block, StringComparison.Ordinal);
    }

    private static string ExtractBetween(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到起點標記：{startMarker}");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"找不到終點標記：{endMarker}");
        return source[start..end];
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
