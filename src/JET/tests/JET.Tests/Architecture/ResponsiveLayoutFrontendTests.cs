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
            // 2026-11-03 自訂篩選條件改成欄位條件分頁，欄位改成一欄的清單、不再用格線（裁定 2026-11-03-05、2026-11-03-26），
            // 原本兩個 --custom 格線跟著拿掉；情境範例移到右側托盤，改核對托盤的格線同樣會縮窄。第一次失敗：Focused 收據 20261103-100726697-97e6c3f1045d4beda092d17c7bec9ecb。
            // 2026-11-05 常用篩選條件改成和預篩選分頁同一種清單列（裁定 2026-11-05-08），卡片與 --common 格線一起拿掉，
            // 清單列在下面另外核對名稱那一欄可以縮窄。第一次失敗：Public 收據 20261105-040816961-89151c2148e3414e991951ba43508f1d。
            ".filter-template-tray .filter-example-list__items",
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
        // 條件總覽三個分頁的清單列：名稱那一欄可以縮窄，鍵靠右。
        AssertDeclaration(css, ".filter-check", "grid-template-columns", @"minmax\(\s*0\s*,\s*1fr\)\s+auto");
        // 欄位條件分頁的欄位名稱在窄欄裡換行，不撐寬清單。
        AssertDeclaration(css, ".filter-field__name", "overflow-wrap", @"anywhere");
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
    /// 裁定 2026-10-27-02：視窗放大縮小時對不齊，寬視窗中欄右側空出一塊、單列標題列的案件摘要比標誌與操作組高、
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
        // 2026-11-05 小筆電上標題列也排成一列（裁定 2026-11-05-09），單列的起點從 1536px 改成 900px，原本在 1536px 的區塊裡找。
        // 第一次失敗：Public 收據 20261105-040816961-89151c2148e3414e991951ba43508f1d。
        var wideViewportCss = BlockBodies(css, "@media (min-width: 900px)");
        Assert.Matches(new Regex(@"top:\s*anchor\(--jet-header top", RegexOptions.CultureInvariant), wideViewportCss);
        Assert.Matches(new Regex(@"bottom:\s*anchor\(--jet-header bottom\)", RegexOptions.CultureInvariant), wideViewportCss);

        // 案件摘要換行時，每一行開頭的項目不帶分隔線與縮排。
        AssertDeclaration(css, ".case-summary__details", "clip-path", @"inset\([^;]*\)");

        // 狀態標籤可以縮窄換行，不把「檢視」推出卡片；整張卡片都沒有「檢視」時不保留那一欄的空位。
        // 2026-10-28 複審時範圍由清單改成卡片：以清單為範圍時，預篩選卡片的上下清單只要一份有「檢視」就會錯開。
        // 裁定 2026-10-30-36 拿掉「檢視」，有明細的規則整列就是開關，每一列都只剩標題與狀態，不必再依卡片保留空位。
        // 改核對現在的守衛：標題那一欄可以縮窄、狀態靠右、狀態字可換行，清單很窄時狀態移到標題下方。
        // 第一次失敗：Focused 收據 20261031-101615482-c77433faf82d4c4f86b5fcc87daf2e32。
        AssertDeclaration(css, ".rule-item > .rule-item__head", "grid-template-columns", @"10px\s+minmax\(\s*0\s*,\s*1fr\)\s+auto");
        AssertDeclaration(css, ".rule-item__aside", "justify-content", @"flex-end");
        AssertDeclaration(css, ".rule-item__aside > .rule-status", "white-space", @"normal");
        Assert.Contains("@container rule-list (max-width: 360px)", css, StringComparison.Ordinal);
        Assert.DoesNotContain(".rule-card:not(:has(.rule-item__aside > :not(.rule-status)))", css, StringComparison.Ordinal);
    }

    /// <summary>
    /// 同一份清單裡有的列整列可點、有的列沒有明細時，名稱與數字仍要上下對齊。裁定 2026-10-30-36 改成整列可點後，
    /// 「用 Excel 配對」第一列沒有箭頭，名稱比其他列早開始，科目數也被「資料預覽」往左推；2026-10-31 在預覽主機量到後，
    /// 改成和第四步規則清單一樣的固定箭頭欄。傳票表的分錄和傳票共用「符合情形」欄（裁定 2026-10-30-37），
    /// 原本分錄另開一張表時留下的固定寬度會撐開整張表，金額要橫向捲動才看得到。
    /// 裁定 2026-11-03-03 讓每一列只佔一行：符合第幾組第幾條接在名稱後面，不再是另一個段落。原本斷言那個段落沒有固定寬度、
    /// 跟著欄寬換行；段落拿掉後找不到那條樣式，第一次失敗：Focused 收據 20261103-041447477-5df79ef74b9e422192d0a01554cd1749。
    /// 現在改成斷言符合情形只佔一行，欄寬的下限由表頭決定，見下一個測試。
    /// </summary>
    [Fact]
    public void RowsWithAndWithoutToggle_KeepNamesAndNumbersAligned()
    {
        var css = ReadFrontend("css", "app.css");

        AssertDeclaration(css, ".excel-mapping__result", "grid-template-columns", @"10px\s+auto\s+minmax\(\s*0\s*,\s*1fr\)\s+auto");
        AssertDeclaration(css, ".excel-mapping__label", "grid-column", @"2");
        AssertDeclaration(css, ".excel-mapping__result > .jet-view", "grid-column", @"3");
        AssertDeclaration(css, ".excel-mapping__value", "grid-column", @"4");

        Assert.DoesNotContain(".filter-match-reason > p", css, StringComparison.Ordinal);
        AssertDeclaration(css, ".filter-match-reason", "white-space", @"nowrap");
        AssertDeclaration(css, ".filter-match-reason", "text-overflow", @"ellipsis");
    }

    /// <summary>
    /// 傳票表展開分錄時，分錄列和傳票列一樣高、欄寬不變，分錄再多也不會擠開其他傳票（裁定 2026-11-03-03）。
    /// 2026-11-03 在預覽主機量到：符合條件的分錄寫成兩行，列高 61px，其他列約 32px；分錄的科目與摘要一出現，整張表跟著變寬。
    /// 表格比外框寬時，瀏覽器只把儲存格的 width 當建議值，所以只有分錄才有內容的欄由表頭的支撐留好寬度。
    /// </summary>
    [Fact]
    public void VoucherTable_EntryRowsMatchVoucherRowsAndKeepColumnWidths()
    {
        var css = ReadFrontend("css", "app.css");
        var vouchers = ReadFrontend("js", "filter-vouchers.js");

        foreach (var column in new[] { "line", "date", "account", "description", "match", "amount", "extra" })
        {
            AssertDeclaration(css, $".voucher-table th.voucher-table__{column}", "--voucher-column-width", @"[\d.]+em");
        }

        Assert.Contains("::after { content: ''; display: block; height: 0; width: var(--voucher-column-width); }", css, StringComparison.Ordinal);
        AssertDeclaration(css, ".voucher-table .voucher-table__clip", "white-space", @"nowrap");
        AssertDeclaration(css, ".voucher-table .voucher-table__clip", "text-overflow", @"ellipsis");

        Assert.Contains("clipCellHtml(entry.description, 'voucher-table__description')", vouchers, StringComparison.Ordinal);
        Assert.DoesNotContain("'<p>' + Ui.esc(detail)", vouchers, StringComparison.Ordinal);
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
