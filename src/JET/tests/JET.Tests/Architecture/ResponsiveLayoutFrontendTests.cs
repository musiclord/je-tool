using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 多倍率縮放的靜態版面守衛。這裡只鎖定 shrink／wrap／own-scroll／containment
/// 原語；實際 75%–200% WebView2 ZoomFactor 與 Windows DPI 仍由人工矩陣驗收。
/// </summary>
public sealed class ResponsiveLayoutFrontendTests
{
    [Fact]
    public void ShellPickerStepFlowAndPreview_UseShrinkableOrSelfScrollingBoundaries()
    {
        var css = ReadFrontend("css", "app.css");

        foreach (var selector in new[]
        {
            ".app-main",
            ".project-picker",
            ".app-body",
            ".content",
            ".stepflow-item__body",
            ".data-preview",
            ".data-preview__body",
            ".data-preview__count",
            ".kv-list__value",
            ".import-task__name"
        })
        {
            AssertDeclaration(css, selector, "min-width", @"0(?:px)?");
        }

        AssertDeclaration(
            css,
            ".data-preview",
            "max-width",
            @"[^;]*(?:%|vw|calc\(|min\(|max\(|clamp\()[^;]*");
        AssertOwnsHorizontalOverflow(css, ".data-preview__body");

        AssertResponsiveExit(css, ".app-header");
        // 案件清單原本的底部操作列在 2026-10-20 拆到標題列、區標題與頁尾，三處都要能換行。
        AssertResponsiveExit(css, ".picker-panel__head");
        AssertResponsiveExit(css, ".picker-section__head");
        AssertResponsiveExit(css, ".picker-section__tools");
        AssertResponsiveExit(css, ".picker-footer");
        AssertResponsiveExit(css, ".stepflow-item__head");
        AssertResponsiveExit(css, ".kv-list__row");
        AssertResponsiveExit(css, ".import-task__row");
        AssertResponsiveExit(css, ".panel__actions");
        AssertResponsiveExit(css, ".data-preview__note-row");
        AssertDeclaration(css, ".data-preview__menu-item", "white-space", @"normal");
        AssertDeclaration(css, ".data-preview__menu-item", "overflow-wrap", @"anywhere");
        AssertDeclaration(css, ".data-preview__tabs", "position", @"relative");
        AssertDeclaration(css, ".data-preview__scroll-hint", "white-space", @"normal");
        AssertDeclaration(css, ".data-preview__scroll-hint", "overflow-wrap", @"anywhere");
        AssertDeclaration(css, ".import-task__action", "white-space", @"normal");
        AssertDeclaration(
            css,
            ".import-task__name",
            "width",
            @"min\([^;]*100%[^;]*\)");
        AssertDeclaration(
            css,
            ".scenario-export-grid",
            "grid-template-columns",
            @"[^;]*minmax\(\s*(?:0|min\()[^;]*");
        foreach (var selector in new[]
        {
            // 2026-10-29 卡片區的格線改名為 --common（原本叫小組名稱），規則本身不變。第一次失敗：Focused 收據 20261029-062123410-6be7ae8a47f140af98c112c94ca35612。
            ".condition-picker__grid--common",
            ".condition-picker__grid--custom",
            ".condition-picker--custom .condition-picker__grid--custom",
            ".stats-bar"
        })
        {
            AssertDeclaration(
                css,
                selector,
                "grid-template-columns",
                @"[^;]*minmax\(\s*min\([^;]*100%[^;]*");
        }
        foreach (var selector in new[]
        {
            ".rule-row__controls > input[type=\"text\"]",
            ".rule-field"
        })
        {
            AssertDeclaration(
                css,
                selector,
                "min-width",
                @"min\([^;]*100%[^;]*\)");
        }
        AssertDeclaration(css, ".rule-field__hint", "max-width", @"100%");
        AssertDeclaration(css, ".rule-field__hint", "white-space", @"normal");
        AssertDeclaration(css, ".rule-field__hint", "overflow-wrap", @"anywhere");

        var narrowViewportCss = BlockBodies(css, "@media (max-width: 720px)");
        AssertDeclaration(
            narrowViewportCss,
            ".data-preview",
            "max-width",
            @"[^;]*(?:%|vw|calc\(|min\(|max\(|clamp\()[^;]*");
        AssertDeclaration(narrowViewportCss, ".data-preview__resize", "display", @"none");
        AssertDeclaration(narrowViewportCss, ".data-preview__more", "position", @"static");
        AssertDeclaration(narrowViewportCss, ".data-preview__menu", "width", @"auto");
        AssertDeclaration(narrowViewportCss, ".data-preview__menu", "min-width", @"0(?:px)?");
        AssertDeclaration(narrowViewportCss, ".data-preview__menu", "max-width", @"none");
    }

    [Fact]
    public void OverviewModal_ClosesTheContainmentChainAroundWideBiContent()
    {
        var css = ReadFrontend("css", "app.css");

        foreach (var selector in new[]
        {
            ".overview-modal__card",
            ".overview",
            ".overview__bi",
            ".overview-bi__body"
        })
        {
            AssertDeclaration(css, selector, "min-width", @"0(?:px)?");
            AssertDeclaration(css, selector, "max-width", @"100%");
        }

        AssertDeclaration(
            css,
            ".overview-modal__card",
            "overflow",
            @"(?:hidden|clip|auto|scroll)");
        AssertOwnsHorizontalOverflow(css, ".overview-bi__body");
        AssertDeclaration(
            css,
            ".overview__pipeline",
            "grid-template-columns",
            @"[^;]*minmax\(\s*0[^;]*");
        AssertDeclaration(css, ".overview-stage", "min-width", @"0(?:px)?");
        AssertDeclaration(css, ".overview-step-detail", "min-width", @"0(?:px)?");
        AssertDeclaration(
            css,
            ".overview-step-detail__facts",
            "grid-template-columns",
            @"[^;]*minmax\(\s*0[^;]*");

        AssertResponsiveExit(css, ".overview__progress");
        var narrowViewportCss = BlockBodies(css, "@media (max-width: 720px)");
        AssertDeclaration(
            narrowViewportCss,
            ".overview__pipeline",
            "grid-template-columns",
            @"repeat\(\s*2\s*,\s*minmax\(\s*0[^;]*");
        Assert.True(
            HasResponsiveExit(css, ".overview-bi__head") ||
            OwnsHorizontalOverflow(css, ".overview-bi__tabs"),
            "overview BI 標題列必須可換行／堆疊，或讓 tabs 在自身範圍內水平捲動。");
    }

    [Fact]
    public void OverviewEChartsContainers_AreSizedInsideABoundedScrollport()
    {
        var source = ReadFrontend("js", "overview-bi.js");
        var css = ReadFrontend("css", "app.css");

        Assert.Contains("data-overview-chart=", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<svg", source, StringComparison.OrdinalIgnoreCase);
        AssertDeclaration(css, ".overview-bi__chart", "width", @"100%");
        AssertDeclaration(css, ".overview-bi__chart", "min-width", @"720px");
        AssertDeclaration(css, ".overview-bi__chart", "max-width", @"none");
        AssertDeclaration(css, ".overview-bi__chart--rule-period", "height", @"360px");
        AssertDeclaration(css, ".overview-bi__chart--rule-period", "min-height", @"360px");
        AssertDeclaration(css, ".overview-bi__chart--amount", "min-height", @"360px");
        AssertDeclaration(css, ".overview-bi__chart--concentration", "min-height", @"320px");
        AssertOwnsHorizontalOverflow(css, ".overview-bi__body");
        AssertDeclaration(
            css,
            ".overview-modal__card",
            "overflow",
            @"(?:hidden|clip|auto|scroll)");
    }

    /// <summary>
    /// 使用者 2026-10-27 指出視窗放大縮小時對不齊：寬視窗中欄右側空出一塊、單列標題列的案件摘要比標誌與操作組高、
    /// 窄視窗時步驟列的箭頭掉到下一行、案件摘要換行後下一行縮排、驗證清單沒有「檢視」時狀態標籤沒有貼齊右緣、
    /// 長狀態文字把「檢視」推出卡片。這裡鎖住讓這些對齊線在任何寬度下都不移動的版面原語。
    /// </summary>
    [Fact]
    public void AlignmentLines_StayPutAcrossWindowWidths()
    {
        var css = ReadFrontend("css", "app.css");

        // 流程填滿中欄，左右間距相同，不在寬視窗時只在右側空出一塊。
        Assert.False(
            HasProperty(RuleBodies(css, ".stepflow"), "max-width", @"[^;]+"),
            ".stepflow 不設最大寬度，多出的寬度全部給內容。");

        // 步驟列的箭頭固定在右緣第一行，不參與換行；列內各項目對齊第一行。
        Assert.Matches(
            new Regex(
                @"(?ms)^\.stepflow-item__caret,\s*\.stepflow-item__spacer-caret\s*\{[^}]*position:\s*absolute;[^}]*right:\s*0;",
                RegexOptions.CultureInvariant),
            css);
        AssertDeclaration(css, ".stepflow-item", "align-items", @"flex-start");

        // 單列標題列時，案件摘要的上下緣跟著標題列本身，和標誌、操作組置中在同一條水平線上。
        var wideViewportCss = BlockBodies(css, "@media (min-width: 1536px)");
        Assert.Matches(new Regex(@"top:\s*anchor\(--jet-header top", RegexOptions.CultureInvariant), wideViewportCss);
        Assert.Matches(new Regex(@"bottom:\s*anchor\(--jet-header bottom\)", RegexOptions.CultureInvariant), wideViewportCss);

        // 案件摘要換行時，每一行開頭的項目不帶分隔線與縮排。
        AssertDeclaration(css, ".case-summary__details", "clip-path", @"inset\([^;]*\)");

        // 狀態標籤可以縮窄換行，不把「檢視」推出卡片；整張卡片都沒有「檢視」時不保留那一欄的空位。
        // 2026-10-28 複審時範圍由清單改成卡片：以清單為範圍時，預篩選卡片的上下清單只要一份有「檢視」就會錯開。
        AssertDeclaration(css, ".rule-item__aside", "grid-template-columns", @"minmax\(\s*0[^;]*");
        AssertDeclaration(css, ".rule-item__aside > .rule-status", "white-space", @"normal");
        Assert.Contains(
            ".rule-card:not(:has(.rule-item__aside > :not(.rule-status))) .rule-item__aside",
            css,
            StringComparison.Ordinal);
    }

    private static string ReadFrontend(params string[] relativeParts) =>
        File.ReadAllText(Path.Combine(
            new[] { AppContext.BaseDirectory, "wwwroot" }.Concat(relativeParts).ToArray()));

    private static void AssertResponsiveExit(string css, string selector)
    {
        Assert.True(
            HasResponsiveExit(css, selector),
            $"{selector} 必須可換行、窄版堆疊，或在自身範圍內水平捲動。");
    }

    private static bool HasResponsiveExit(string css, string selector)
    {
        var declarations = RuleBodies(css, selector);
        return HasProperty(declarations, "flex-wrap", @"wrap") ||
            HasProperty(declarations, "flex-direction", @"column") ||
            HasProperty(declarations, "overflow-x", @"(?:auto|scroll)");
    }

    private static void AssertOwnsHorizontalOverflow(string css, string selector)
    {
        Assert.True(
            OwnsHorizontalOverflow(css, selector),
            $"{selector} 必須自行承接水平溢出，不能把寬內容推到外層。");
    }

    private static bool OwnsHorizontalOverflow(string css, string selector)
    {
        var declarations = RuleBodies(css, selector);
        return HasProperty(declarations, "overflow-x", @"(?:auto|scroll)") ||
            HasProperty(declarations, "overflow", @"auto");
    }

    private static void AssertDeclaration(
        string css,
        string selector,
        string property,
        string valuePattern)
    {
        var declarations = RuleBodies(css, selector);
        Assert.True(
            HasProperty(declarations, property, valuePattern),
            $"{selector} 缺少 {property}: {valuePattern} 的縮放版面守衛。");
    }

    private static string RuleBodies(string css, string selector)
    {
        var pattern =
            @"(?ms)^[ \t]*" + Regex.Escape(selector) +
            @"[ \t]*\{(?<body>[^{}]*)\}";
        var bodies = Regex.Matches(css, pattern, RegexOptions.CultureInvariant)
            .Select(match => match.Groups["body"].Value)
            .ToArray();

        Assert.NotEmpty(bodies);
        return string.Join(Environment.NewLine, bodies);
    }

    private static string BlockBodies(string source, string marker)
    {
        var bodies = new List<string>();
        var searchIndex = 0;

        while (searchIndex < source.Length)
        {
            var markerIndex = source.IndexOf(marker, searchIndex, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                break;
            }

            var openBrace = source.IndexOf('{', markerIndex);
            Assert.True(openBrace >= 0, $"CSS block 缺少左大括號：{marker}");

            var depth = 0;
            var closeBrace = -1;
            for (var index = openBrace; index < source.Length; index++)
            {
                if (source[index] == '{')
                {
                    depth++;
                }
                else if (source[index] == '}' && --depth == 0)
                {
                    closeBrace = index;
                    break;
                }
            }

            Assert.True(closeBrace >= 0, $"CSS block 缺少右大括號：{marker}");
            bodies.Add(source[(openBrace + 1)..closeBrace]);
            searchIndex = closeBrace + 1;
        }

        Assert.NotEmpty(bodies);
        return string.Join(Environment.NewLine, bodies);
    }

    private static bool HasProperty(
        string declarations,
        string property,
        string valuePattern) =>
        Regex.IsMatch(
            declarations,
            @"(?m)(?:^|;)\s*" + Regex.Escape(property) +
            @"\s*:\s*(?:" + valuePattern + @")\s*(?:!important\s*)?;",
            RegexOptions.CultureInvariant);
}
