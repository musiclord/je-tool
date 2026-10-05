using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 前端 store 通知慣例守衛：凡是會改變衍生畫面輸出的寫入一律 bump；「只 notify」僅限連續文字
/// 輸入且必須在 blur 邊界配一次延遲 bump 收斂。欄位配對的借方代碼與攸關資料元素顯示名稱是文件化例外：
/// 輸入時就地更新衍生畫面，失焦不重建，避免確認鈕在滑鼠按下與放開之間被換掉（2026-10-02 W3）。重建後的焦點與捲動由 renderContent 框架層依焦點
/// 識別屬性與 data-preserve-scroll 統一還原，步驟模組不得各自發明保留機制。歷史根因（classic
/// 配對介面右側必填檢查延遲）與收斂裁定見 docs/history/specs/2026-09-01-frontend-sync-devlog-mutation-plan.md。
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
        // 2026-09-23：同一節點需同時還原焦點與文字選取，不能只鎖查找與 focus 寫在同一行。
        Assert.Contains("var focused = container.querySelector(focusSelector);", content, StringComparison.Ordinal);
        Assert.Contains("focusElement(focused);", content, StringComparison.Ordinal);
        Assert.Contains("restoreContentTextSelection(focused, textSelection);", content, StringComparison.Ordinal);
        Assert.Contains("captureContentTextSelection(container)", content, StringComparison.Ordinal);
        var selection = ExtractBetween(app, "function restoreContentTextSelection(", "function captureContentScrollPositions(");
        Assert.Contains("typeof element.selectionStart === 'number'", selection, StringComparison.Ordinal);
        Assert.Contains("element.value === selection.value", selection, StringComparison.Ordinal);
        Assert.Contains("element.setSelectionRange(selection.start, selection.end, selection.direction", selection, StringComparison.Ordinal);
        Assert.Contains("composition.project === state.project", content, StringComparison.Ordinal);
        Assert.Contains("composition.step === state.currentStepIndex", content, StringComparison.Ordinal);
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
    public void RdeLabelContinuousInput_RefreshesDerivedViewsInPlaceWithoutBlurRebuild()
    {
        var mapping = ReadFrontend("js", "steps", "mapping-step.js");
        var block = ExtractBetween(
            mapping,
            "section.querySelectorAll('[data-rde-label]')",
            "section.querySelectorAll('[data-rde-type]')");

        // 2026-10-03 修改既有測試的原因：原測試鎖住「blur 後 setTimeout(Store.touch, 0) 重建整步」。
        // 2026-10-02 主線實測 W3 證明這個延遲重建會在滑鼠按下與放開之間換掉「確認配對」按鈕，
        // 第一次點擊沒有送出。主線裁定改成輸入時就地更新衍生畫面、失焦不重建，因此改鎖新做法；
        // 第一次失敗收據 20261003-020747009-ee4dd9fbeb694d50bc7947d8930b79df。
        Assert.Contains("patchGlMappingOptionsQuiet", block, StringComparison.Ordinal);
        Assert.Contains("refreshMappingDerived(section, 'gl', Ui.GL_FIELDS);", block, StringComparison.Ordinal);
        Assert.DoesNotContain("addEventListener('blur'", block, StringComparison.Ordinal);
        Assert.DoesNotContain("setTimeout", block, StringComparison.Ordinal);
        Assert.DoesNotContain("addEventListener('change'", block, StringComparison.Ordinal);
    }

    [Fact]
    public void MappingLiteralInput_UpdatesDraftQuietlyAndKeepsTheCommitButtonNode()
    {
        var mapping = ReadFrontend("js", "steps", "mapping-step.js");
        var state = ReadFrontend("js", "state.js");

        // 2026-10-02 W3：借方代碼輸入框改值後直接用滑鼠點確認鈕，第一次就要送出。
        // 輸入時寫草稿並就地更新，失焦時不呼叫會重建整步的 setMappingDraft。
        var literal = ExtractBetween(mapping, "section.querySelectorAll('input[data-mapping-key]')", "var restoreBtn");
        Assert.Contains("addEventListener('input'", literal, StringComparison.Ordinal);
        Assert.Contains("Store.setMappingLiteralQuiet(kind, control.getAttribute('data-mapping-key'), control.value.trim());", literal, StringComparison.Ordinal);
        Assert.Contains("refreshMappingDerived(section, kind, fields);", literal, StringComparison.Ordinal);
        Assert.DoesNotContain("setMappingDraft", literal, StringComparison.Ordinal);
        Assert.DoesNotContain("Store.touch", literal, StringComparison.Ordinal);
        Assert.DoesNotContain("setTimeout", literal, StringComparison.Ordinal);

        // 來源欄下拉仍是離散選擇，照通知慣例直接重建。
        var select = ExtractBetween(mapping, "section.querySelectorAll('select[data-mapping-key]')", "section.querySelectorAll('input[data-mapping-key]')");
        Assert.Contains("Store.setMappingDraft(kind, control.getAttribute('data-mapping-key'), control.value.trim());", select, StringComparison.Ordinal);

        // 就地更新只換提示與必填清單，確認鈕只改 disabled，不換節點。
        var refresh = ExtractBetween(mapping, "function refreshMappingDerived(", "function replaceDerivedNotice(");
        Assert.Contains("commit.disabled = !eligibility.canCommit;", refresh, StringComparison.Ordinal);
        Assert.Contains("rail.outerHTML = requiredRailHtml(eligibility, mappingState);", refresh, StringComparison.Ordinal);
        Assert.Contains("mappingBannerHtml(kind, mappingState, committed, matches)", refresh, StringComparison.Ordinal);
        Assert.Contains("optionProblemsHtml(eligibility)", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain("Store.touch", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", refresh, StringComparison.Ordinal);

        // 必填清單會被換新，跳到欄位的按鈕改用委派，換新後仍可點。
        Assert.DoesNotContain("section.querySelectorAll('[data-focus-mapping-field]')", mapping, StringComparison.Ordinal);
        Assert.Contains("event.target.closest('[data-focus-mapping-field]')", mapping, StringComparison.Ordinal);

        // 安靜寫入只 notify，不 bump。
        var quiet = ExtractBetween(state, "setMappingLiteralQuiet: function", "setMappingDraft: function");
        Assert.Contains("notify();", quiet, StringComparison.Ordinal);
        Assert.DoesNotContain("bump();", quiet, StringComparison.Ordinal);
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
