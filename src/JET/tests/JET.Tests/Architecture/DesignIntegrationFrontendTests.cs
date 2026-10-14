using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// Claude Design 前端整合的邊界守衛：案件摘要不重複目前步驟、總覽維持唯讀，以及已裁定的外觀
/// （設計資源全在本機、品牌字樣、頂部玻璃帶、可按的操作與一致的動畫）。
/// 設計稿把流程畫成「六步含『完成』、且欄位配對併入匯入」，runtime 沒有採用；六步模型、第六步完成摘要與總覽的分錄統計
/// 2026-10-14 起改由 <c>tools/tests/frontend-journey-completion.test.cjs</c> 與 <c>frontend-journey-overview.test.cjs</c> 的行為測試核對。
/// </summary>
public sealed class DesignIntegrationFrontendTests
{
    private static string FrontendPath(params string[] relativeParts) =>
        Path.Combine(new[] { AppContext.BaseDirectory, "wwwroot" }.Concat(relativeParts).ToArray());

    private static string ReadFrontend(params string[] relativeParts) =>
        File.ReadAllText(FrontendPath(relativeParts));

    [Fact]
    public void CaseSummary_LeavesCurrentStepInNavigation_AndRetainsCaseMetadata()
    {
        var markup = ReadFrontend("index.html");

        // 2026-10-09 使用者要求縮小摘要，步驟位置只留在左側，不改成完成進度。
        // 舊摘要斷言第一次失敗：20261009-085703982-31645074621a4596bfb1f4ecb83e31d7。
        Assert.DoesNotContain("<span class=\"case-summary__label\">目前步驟</span>", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("data-bind=\"case-progress\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"case-summary__label\">進度</span>", markup, StringComparison.Ordinal);

        // 查核期間欄位與既有固定 data-bind 一併保留。
        Assert.Contains("data-bind=\"case-period\"", markup, StringComparison.Ordinal);
        Assert.Contains("data-bind=\"case-id\"", markup, StringComparison.Ordinal);
        Assert.Contains("data-bind=\"case-client\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("data-bind=\"case-step\"", markup, StringComparison.Ordinal);
        Assert.Contains("data-bind=\"step-nav\"", markup, StringComparison.Ordinal);

        var source = ReadFrontend("js", "app.js");
        Assert.Contains("status === 'current' ? ' data-bind=\"case-step\"'", source, StringComparison.Ordinal);
        Assert.Contains("var done = Ui.doneStepCount(state);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Overview_StaysReadOnly_AndDeclaresNotApplicableSemantics()
    {
        var source = ReadFrontend("js", "app.js") + ReadFrontend("js", "ui-core.js");

        // 總覽的解讀文字要保留三個不可混淆的語意：符合條件不等於錯誤、不適用不等於零、判斷由審計員做。
        // 2026-09-22 使用者要求改成白話後，前兩個語意合併在同一句，所以這裡檢查整句，不拆開檢查片段。
        // 2026-10-03 用語統一 T1：畫面不再使用「母體」，與 AuditCore 正本一起改（第一次失敗：收據 20261003-023349721-0ccefea0a80c412aa8460624eaae563a）。
        Assert.Contains("預篩選呈現查核期間分錄的分布與符合條件的分錄，是否需進一步查核由審計員判斷。", source, StringComparison.Ordinal);
        Assert.DoesNotContain("母體分布", source, StringComparison.Ordinal);
        // 2026-10-02 整體複審 W16：「不適用不等於零」改成完整句，仍鎖住「不適用不代表沒有符合的分錄」這個語意；舊短句不得再出現。
        Assert.Contains("列在「不適用規則」的條件因缺少所需資料或設定而沒有執行，不代表沒有符合的分錄。", source, StringComparison.Ordinal);
        Assert.DoesNotContain("不適用不等於零", source, StringComparison.Ordinal);
        Assert.DoesNotContain("不含綜合風險分數或審計判斷", source, StringComparison.Ordinal);

        // 總覽不得出現風險分級／評分或引導判斷的措辭。
        Assert.DoesNotContain("風險評分", source, StringComparison.Ordinal);
        Assert.DoesNotContain("高風險專案", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Frontend_LoadsOnlyVendoredDesignAssets_WithTheirLicenses()
    {
        var markup = ReadFrontend("index.html");
        var css = ReadFrontend("css", "app.css");
        var loadableMarkup = markup + Environment.NewLine + css;

        // WebView2 離線執行：任何 runtime 載入面都不得出現外部 URL。
        Assert.DoesNotContain("http://", loadableMarkup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", loadableMarkup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=\"//", loadableMarkup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"//", loadableMarkup, StringComparison.OrdinalIgnoreCase);

        const string echartsPath = "./vendor/echarts/5.5.0/echarts.min.js";
        const string overviewPath = "./js/overview-bi.js";
        const string appPath = "./js/app.js";
        var echartsIndex = markup.IndexOf(echartsPath, StringComparison.Ordinal);
        var overviewIndex = markup.IndexOf(overviewPath, StringComparison.Ordinal);
        var appIndex = markup.IndexOf(appPath, StringComparison.Ordinal);
        Assert.True(echartsIndex >= 0, "index.html 未載入 vendored ECharts 5.5.0。");
        Assert.True(echartsIndex < overviewIndex, "ECharts 必須先於 overview-bi.js 載入。");
        Assert.True(overviewIndex < appIndex, "overview-bi.js 必須先於 app.js 載入。");

        Assert.Contains("@font-face", css, StringComparison.Ordinal);
        Assert.Contains(
            "../assets/fonts/noto-sans-tc/NotoSansTC-VariableFont_wght.ttf",
            css,
            StringComparison.Ordinal);
        // 使用者 2026-10-08 裁定移除沒有畫面使用的 Noto Serif TC；全介面只載入 Noto Sans TC。
        Assert.DoesNotContain("Noto Serif TC", css, StringComparison.Ordinal);
        Assert.DoesNotContain("noto-serif-tc", css, StringComparison.Ordinal);

        var assets = new[]
        {
            ("vendor/echarts/5.5.0/echarts.min.js", 500_000L),
            ("vendor/echarts/5.5.0/LICENSE.txt", 500L),
            ("vendor/echarts/5.5.0/NOTICE.txt", 100L),
            ("vendor/echarts/5.5.0/LICENSE-d3.txt", 500L),
            ("assets/fonts/noto-sans-tc/NotoSansTC-VariableFont_wght.ttf", 1_000_000L),
            ("assets/fonts/noto-sans-tc/OFL.txt", 1_000L)
        };
        foreach (var (relativePath, minimumBytes) in assets)
        {
            var path = FrontendPath(relativePath.Split('/'));
            Assert.True(File.Exists(path), $"缺少 vendored asset：{relativePath}");
            Assert.True(
                new FileInfo(path).Length >= minimumBytes,
                $"Vendored asset 過小或不完整：{relativePath}");
        }

        Assert.Contains(
            "5.5.0",
            ReadFrontend("vendor", "echarts", "5.5.0", "echarts.min.js"),
            StringComparison.Ordinal);
        Assert.Contains(
            "Apache License",
            ReadFrontend("vendor", "echarts", "5.5.0", "LICENSE.txt"),
            StringComparison.Ordinal);
        Assert.Contains(
            "SIL OPEN FONT LICENSE",
            ReadFrontend("assets", "fonts", "noto-sans-tc", "OFL.txt"),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Frontend_BundlesLatinFontAndIconsOffline_WithTheirLicenses()
    {
        // 使用者 2026-10-11 選定「深藍框架」改版，並同意下載 Instrument Sans 與 Fluent UI System Icons 打包離線使用。
        var css = ReadFrontend("css", "app.css");

        Assert.Contains("../assets/fonts/instrument-sans/InstrumentSans-Latin.woff2", css, StringComparison.Ordinal);
        var font = FrontendPath("assets", "fonts", "instrument-sans", "InstrumentSans-Latin.woff2");
        Assert.True(File.Exists(font), "缺少 Instrument Sans 字型檔。");
        Assert.True(new FileInfo(font).Length >= 20_000, "Instrument Sans 字型檔過小或不完整。");
        Assert.Equal("wOF2", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(font), 0, 4));
        Assert.Contains(
            "SIL Open Font License",
            ReadFrontend("assets", "fonts", "instrument-sans", "OFL.txt"),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MIT License", ReadFrontend("assets", "icons", "fluent", "LICENSE"), StringComparison.Ordinal);

        // 樣式表引用的每個圖示都要真的打包在程式裡，而且只是單純的向量圖形，不含程式碼或外部連結。
        var references = System.Text.RegularExpressions.Regex.Matches(css, @"\.\./assets/icons/fluent/([a-z0-9_]+\.svg)")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(references);
        foreach (var name in references)
        {
            var path = FrontendPath("assets", "icons", "fluent", name);
            Assert.True(File.Exists(path), $"樣式表引用的圖示沒有打包：{name}");
            var svg = File.ReadAllText(path);
            Assert.StartsWith("<svg", svg, StringComparison.Ordinal);
            Assert.DoesNotContain("<script", svg, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("href", svg, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("foreignObject", svg, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Frontend_BrandIsAWordmark_WithJeInAnAccentTile()
    {
        // 使用者 2026-10-11 兩次認為圖形標誌看不出意思：勾記號像勾選框，一頁分錄加光帶又像一般的清單圖示。
        // 同日第三次調整改成字標：名稱裡的 JE 放進石板藍的圓角方形，Tool 接在後面，名稱仍是「JE Tool」。
        // 原本斷言標誌圖檔的測試第一次失敗：20261011-141648053-e438cea272124ecca040d8d15e5cb570。
        var markup = ReadFrontend("index.html");
        Assert.Contains("<h1 class=\"app-header__title\"><span class=\"app-header__mono\">JE</span> Tool</h1>", markup, StringComparison.Ordinal);

        var css = ReadFrontend("css", "app.css");
        Assert.DoesNotContain("assets/brand/", css, StringComparison.Ordinal);
        var tile = css[css.IndexOf(".app-header__mono {", StringComparison.Ordinal)..];
        tile = tile[..(tile.IndexOf('}') + 1)];
        Assert.Contains("background: var(--brand-tile);", tile, StringComparison.Ordinal);
        Assert.Contains("border-radius: var(--radius-control);", tile, StringComparison.Ordinal);

        // 目前帳號和名稱排成一組、對齊同一條左緣，不再用色點，也不再疊進流程欄的頂端。
        var brand = markup[markup.IndexOf("<div class=\"app-header__brand\">", StringComparison.Ordinal)..];
        brand = brand[..brand.IndexOf("</div>", StringComparison.Ordinal)];
        Assert.Contains("data-bind=\"user-identity\"", brand, StringComparison.Ordinal);
        Assert.DoesNotContain(".badge::before", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Frontend_TopIsOneBandAcrossTheWindow_AboveEveryColumn()
    {
        // 使用者 2026-10-11 第三次調整認為標誌併進流程欄頂端、案件摘要浮在畫布上、操作組獨自在右上角，彼此割裂；
        // 選了「淺色標題列」：頂部是一條橫跨整個視窗的標題列，操作組收在這一條裡。
        // 使用者 2026-10-12 要求整個 JE Tool 帶有 Liquid Glass 的質感，看過樣張後選了「照樣張套用」：
        // 這一條改成一整片淺色的玻璃，流程欄、中欄與資料預覽的上緣藏在它下面，捲動時內容從它下面經過。
        // 原本鎖住白紙底色 --color-surface 與下緣 inset 細線的斷言第一次失敗：20261012-031048053-9eca8de789444ca1b28d6f3fc2d8f298。
        var css = ReadFrontend("css", "app.css");
        var app = ReadFrontend("js", "app.js");

        // 仍是一整條：同一片玻璃從視窗左緣到右緣，兩列時也不在案件摘要那裡斷開；顏色是淺色的玻璃，不是深色框帶。
        var band = css[css.IndexOf(".app-shell::before {", StringComparison.Ordinal)..];
        band = band[..(band.IndexOf('}') + 1)];
        Assert.Contains("left: 0;", band, StringComparison.Ordinal);
        Assert.Contains("right: 0;", band, StringComparison.Ordinal);
        Assert.Contains("height: var(--chrome-h, 64px);", band, StringComparison.Ordinal);
        Assert.Contains("background: var(--toolbar-bg);", band, StringComparison.Ordinal);
        Assert.Contains("backdrop-filter: var(--glass-bar-blur);", band, StringComparison.Ordinal);
        Assert.Contains("box-shadow: var(--glass-edge);", band, StringComparison.Ordinal);
        Assert.Contains("--toolbar-bg: var(--glass-bar);", css, StringComparison.Ordinal);
        Assert.Contains("--glass-bar: rgba(var(--glass-tint), 0.5);", css, StringComparison.Ordinal);
        Assert.Contains("--glass-tint: 255, 254, 251;", css, StringComparison.Ordinal);
        Assert.DoesNotMatch(
            new System.Text.RegularExpressions.Regex(@"\.app-header::before\s*\{[^}]*--toc-width", System.Text.RegularExpressions.RegexOptions.Singleline),
            css);
        Assert.DoesNotContain("anchor(--jet-brand", css, StringComparison.Ordinal);

        // 各欄從視窗頂端開始，讓出標題列的高度；捲動與焦點移動不會把東西停在玻璃後面。
        Assert.Contains("function bindChromeHeight()", app, StringComparison.Ordinal);
        Assert.Contains("margin-top: calc(-1 * var(--chrome-h, 64px));", css, StringComparison.Ordinal);
        Assert.Contains("scroll-padding-top: calc(var(--chrome-h, 64px) + var(--space-3));", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Frontend_GlassIsTheFunctionalLayer_AndControlsAreTactile()
    {
        // 使用者 2026-10-12 指出沒有看到玻璃效果，要求整個 JE Tool 帶有 Liquid Glass 的質感：通透、自然的光澤、擬物化復興，
        // 不用無意義的漸層填色；看過樣張後選了「照樣張套用」。
        // 功能層是玻璃（標題列、表格固定的表頭、選單、對話框、圖表提示框與滑動中的選取），內容層是實物（按鍵、凹槽、紙）。
        var css = ReadFrontend("css", "app.css");
        var app = ReadFrontend("js", "app.js");
        var overview = ReadFrontend("js", "overview-bi.js");

        // 玻璃只用模糊、透明度與 1px 的邊光，不用漸層填色。
        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"--glass-[a-z-]+:[^;]*gradient"), css);
        Assert.Contains("--glass-rim: inset 1px 1px 0 rgba(255, 255, 255, 0.95), inset -1px -1px 0 rgba(255, 255, 255, 0.45),", css, StringComparison.Ordinal);

        // 表格固定的表頭整列一起是一片玻璃，每個表格只模糊一次；第三步對應表的表頭不會固定，所以不是玻璃。
        var heads = css[css.IndexOf(".preview-table thead,\n.data-preview__body .data-preview__table thead {", StringComparison.Ordinal)..];
        heads = heads[..(heads.IndexOf('}') + 1)];
        Assert.Contains("position: sticky;", heads, StringComparison.Ordinal);
        Assert.Contains("backdrop-filter: var(--glass-head-blur);", heads, StringComparison.Ordinal);
        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"\.map-grid__(?:assign|head) th[^{]*\{[^}]*--glass-head"), css);

        // 標題列的玻璃在中欄的選單之上、對話框之下，捲上來的選單不會畫到標題列上面。
        var band = css[css.IndexOf(".app-shell::before {", StringComparison.Ordinal)..];
        band = band[..(band.IndexOf('}') + 1)];
        Assert.Contains("z-index: 30;", band, StringComparison.Ordinal);

        Assert.Contains("box-shadow: var(--glass-rim), 0 0 0 0.5px var(--glass-line), var(--shadow-overlay);", css, StringComparison.Ordinal);
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(overview, @"extraCssText: TOOLTIP_GLASS").Count);

        // 選取在滑動時浮起來變成玻璃，到位後落回原本的一片；底線頁籤只有底線滑過去。
        Assert.Contains("backdrop-filter: var(--glass-lens-blur);", css, StringComparison.Ordinal);
        Assert.Contains("pseudoElement: '::after'", app, StringComparison.Ordinal);
        // 滑到一半遇到畫面更新時接著原本的進度滑，不重新起跑；滑到一半改去別的地方時從當下的形狀轉向。
        Assert.Contains("animation.currentTime = elapsed;", app, StringComparison.Ordinal);
        Assert.Contains("lifted: moving", app, StringComparison.Ordinal);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(app, @"active: '[^']+', line: true \}").Count);

        // 按鈕是實體按鍵，按下時稍微鼓起；輸入框與底槽往下凹。
        Assert.Contains("box-shadow: var(--key-accent);", css, StringComparison.Ordinal);
        Assert.Contains("transform: scale(1.025);", css, StringComparison.Ordinal);
        Assert.Contains("box-shadow: var(--recess);   /* 往下凹的欄位，和凸起的按鍵分得出來 */", css, StringComparison.Ordinal);

        // 退路：關閉透明效果與要求高對比時玻璃改成不透明；高對比佈景用系統色的線取代玻璃的邊；減少動態時按下不鼓起。
        var reduced = css[css.IndexOf("@media (prefers-reduced-transparency: reduce) {\n  :root {\n    --glass-bar:", StringComparison.Ordinal)..];
        reduced = reduced[..reduced.IndexOf("\n}\n", StringComparison.Ordinal)];
        Assert.Contains("--glass-bar: var(--color-surface);", reduced, StringComparison.Ordinal);
        Assert.Contains("--glass-head-blur: none;", reduced, StringComparison.Ordinal);
        Assert.Contains("--glass-lens: var(--color-surface-raised);", reduced, StringComparison.Ordinal);
        Assert.Contains(".app-shell::before {\n    border-bottom: 1px solid CanvasText;", css, StringComparison.Ordinal);
        var forced = css[css.LastIndexOf("@media (forced-colors: active) {", StringComparison.Ordinal)..];
        Assert.Contains("--glass-bar: Canvas;", forced, StringComparison.Ordinal);
        Assert.Contains("--glass-blur: none;", forced, StringComparison.Ordinal);
        Assert.Contains("  .btn:not(.btn--tiny):not(:disabled):active {\n    transform: none;\n  }", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Frontend_OutputsSitBelowTheirProcedures_AsADocumentTray()
    {
        // 使用者 2026-10-11 指出「驗證報告與抽樣清單」不是測試程序，卻和測試項目看起來同一層，並要求其他流程也避免這種層級混淆。
        // 產出放在工作面底部一塊低一階的托盤，每份檔案是一張文件卡；預篩選報告也有自己的標題。
        var css = ReadFrontend("css", "app.css");
        var validate = ReadFrontend("js", "steps", "validate-step.js");

        Assert.Contains(".rule-card > .report-output:last-child {", css, StringComparison.Ordinal);
        Assert.Contains(".report-output .report-artifact::before {", css, StringComparison.Ordinal);
        // 使用者 2026-10-13 選擇加深直接放在畫布上的托盤：獨立的報告區改用托盤色 --color-tray，
        // 工作面裡的報告區仍用紙面的內凹色。原本只鎖獨立報告區的內凹色，改色前的失敗收據：
        // 20261013-032940090-2c7428e6106a440e88245978a2af09af。意圖不變：產出是比周圍低一階的托盤。
        var tray = css[css.IndexOf("\n.report-output {", StringComparison.Ordinal)..];
        tray = tray[..(tray.IndexOf('}') + 1)];
        Assert.Contains("background: var(--color-tray);", tray, StringComparison.Ordinal);
        var inCard = css[css.IndexOf("\n.rule-card > .report-output {", StringComparison.Ordinal)..];
        inCard = inCard[..(inCard.IndexOf('}') + 1)];
        Assert.Contains("background: var(--color-surface-sunken);", inCard, StringComparison.Ordinal);

        var prescreenOutput = validate[validate.IndexOf("function prescreenArtifactHtml(", StringComparison.Ordinal)..];
        prescreenOutput = prescreenOutput[..prescreenOutput.IndexOf("function prescreenCardHtml(", StringComparison.Ordinal)];
        Assert.Contains("<h4 class=\"report-output__title\">預篩選報告</h4>", prescreenOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Frontend_CategoryHierarchyIsDrawnAsATree_AndBuiltInsAreLabelled()
    {
        // 使用者 2026-10-11 認為分類設定看不出分類之間的上下層，像單純的排版問題。
        // 下層分類往內縮並用連接線接到上層；內建分類標出「內建」，名稱輸入框的右緣一律對齊。
        var validate = ReadFrontend("js", "steps", "validate-step.js");
        var css = ReadFrontend("css", "app.css");

        Assert.Contains("function treeGuidesHtml(index)", validate, StringComparison.Ordinal);
        Assert.Contains("'\" style=\"--taxonomy-depth:' + row.depth + '\">' + treeGuidesHtml(index) +", validate, StringComparison.Ordinal);
        Assert.DoesNotContain("margin-inline-start:' + row.depth + 'em", validate, StringComparison.Ordinal);
        Assert.Contains("<span class=\"taxonomy-row__badge\" title=\"JET 內建的分類，可以改名，不能移除\">內建</span>", validate, StringComparison.Ordinal);
        Assert.Contains(".taxonomy-row__tree > .is-last::before {", css, StringComparison.Ordinal);
        Assert.Contains("--taxonomy-line: var(--color-border-control-strong);", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Frontend_AddActionsLookPressable_AndArrivalsShowWhereTheyLanded()
    {
        // 使用者 2026-10-11 指出條件總覽的「加入」像一般文字；同日要求多參考 Apple 的設計思路。
        // 會新增東西的按鈕是淡藍底藍字加號，低調按鈕用藍字；條件從總覽飛進條件組，展開的內容從標題長出來。
        // 這些動態只畫呈現，減少動態與高對比佈景時不做。
        var css = ReadFrontend("css", "app.css");
        var app = ReadFrontend("js", "app.js");

        Assert.Contains(".btn:is([data-add-check], [data-action=\"add-set\"], [data-action=\"add-taxonomy-category\"]) {", css, StringComparison.Ordinal);
        Assert.Contains("../assets/icons/fluent/add_20_regular.svg", css, StringComparison.Ordinal);
        var lowKey = css[css.IndexOf(".btn--tiny,\n.btn--ghost.btn--tiny,\n.filter-workspace .rule-row__remove,", StringComparison.Ordinal)..];
        lowKey = lowKey[..(lowKey.IndexOf('}') + 1)];
        Assert.Contains("color: var(--color-accent);", lowKey, StringComparison.Ordinal);

        // 使用者 2026-10-12 認為加入條件的飛行像廉價的套版，切換常用查核條件時畫面會閃，所以加入改成帶過去的玻璃標籤
        // .arrival-token，原本只處理打開的 bindDisclosureMotion 改成 app.css 的 [data-collapsible]，打開與收起都轉場。
        // 這裡原本鎖住 bindDisclosureMotion 與 .arrival-ghost，改版後依預期失敗，首敗收據 20261012-054042188-3710b446aff74f86bc2e963be4f7cca0。
        // 改成鎖住新的做法，意圖不變：加入看得到落在哪裡、展開的內容從標題長出來、只畫呈現、減少動態時不做。
        Assert.Contains("function bindArrival()", app, StringComparison.Ordinal);
        Assert.Contains("'(prefers-reduced-motion: reduce)'", app, StringComparison.Ordinal);
        Assert.Contains("ghost.setAttribute('aria-hidden', 'true');", app, StringComparison.Ordinal);
        Assert.Contains("token.setAttribute('aria-hidden', 'true');", app, StringComparison.Ordinal);
        Assert.Contains("pointer-events: none;", css[css.IndexOf(".arrival-token {", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Contains("@starting-style {\n  [data-collapsible]:not([hidden]) {", css, StringComparison.Ordinal);
        Assert.Contains("[data-collapsible][hidden] {", css, StringComparison.Ordinal);
        // 使用者 2026-10-12 要求動態改成 Apple 的彈簧後，展開用 --motion-open、收起用 --motion-close；這條原本鎖 --motion-glide，
        // 改版後依預期失敗，首敗收據 20261012-120406121-bdf6303c521e434c91f652b5bbd1ab4e。意圖不變：收起時 display 也跟著轉場。
        Assert.Contains("display var(--motion-open) allow-discrete", css, StringComparison.Ordinal);
        Assert.Contains("display var(--motion-close) allow-discrete", css, StringComparison.Ordinal);
        Assert.Contains("::details-content", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Frontend_TopAreaHasNoColorBand_AndLightOnlyAnswersAPress()
    {
        // 使用者 2026-10-11 否定漸層框帶與立體高光，要求依 Apple 的設計理念重做：頂部和畫布同一張紙，
        // 層級靠版面與分組；光只在按下時出現，不跟著指標移動；選取底滑動，高對比佈景與降低透明度都有退路。
        var css = ReadFrontend("css", "app.css");
        var app = ReadFrontend("js", "app.js");

        Assert.DoesNotContain("--color-frame-", css, StringComparison.Ordinal);
        Assert.DoesNotMatch(
            new System.Text.RegularExpressions.Regex(
                @"(?:^|[,\s}])\.(?:app-header|case-summary)(?![\w-])[^{}]*\{[^}]*gradient",
                System.Text.RegularExpressions.RegexOptions.Multiline),
            css);
        Assert.DoesNotContain("--pointer-x", css, StringComparison.Ordinal);
        Assert.DoesNotContain("bindPointerLight", app, StringComparison.Ordinal);

        Assert.Contains("function bindPressLight()", app, StringComparison.Ordinal);
        Assert.Contains("--press-x", css, StringComparison.Ordinal);
        Assert.Contains("function bindGlide()", app, StringComparison.Ordinal);
        Assert.Contains("(forced-colors: active)", app, StringComparison.Ordinal);
        Assert.Contains(".has-glide::before", css, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-reduced-transparency: reduce)", css, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-contrast: more)", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Frontend_AddRemoveClearAndSwitchMoveTheSameWay_AcrossTheWholeTool()
    {
        // 使用者 2026-10-12 指出：流程欄換步驟時選取放大縮小過於醒目；加入條件的飛行突兀；移除與清除沒有動態，
        // 其他步驟也一樣；切換常用查核條件會閃；左下角的訊息區展開後再收合整塊錯位。
        // 這裡鎖住共用的做法：重繪前後由 JetMotion 比對 data-motion-key，加入的長出來、消失的收起來；
        // 原地切換的展開區由 [data-collapsible] 兩個方向都轉場；流程欄的那一片同樣大小平移；訊息區的開關固定在欄底。
        var app = ReadFrontend("js", "app.js");
        var css = ReadFrontend("css", "app.css");
        var index = ReadFrontend("index.html");
        var filter = ReadFrontend("js", "steps", "filter-step.js");
        var core = ReadFrontend("js", "ui-core.js");

        Assert.Contains("var Motion = (function () {", app, StringComparison.Ordinal);
        Assert.Contains("global.JetMotion = Motion;", app, StringComparison.Ordinal);
        Assert.Contains("function begin(root, allowed) {", app, StringComparison.Ordinal);
        Assert.Contains("function retire(ghost) {", app, StringComparison.Ordinal);
        Assert.Contains("ghost.inert = true;", app, StringComparison.Ordinal);
        var renderContent = app[app.IndexOf("function renderContent(", StringComparison.Ordinal)..app.IndexOf("function contentMotion(", StringComparison.Ordinal)];
        // 獨立檢查指出收合與展開目前的步驟時只有一部分內容長出來，所以只切換收合也整塊換掉、不逐項比對；
        // 這條原本鎖 !stepChanged，改版後依預期失敗，首敗收據 20261012-064432129-2189dec86f5a416585cdd535cccdbb81。
        Assert.Contains("var motion = contentMotion(container, !stepChanged && !stepFolded);", renderContent, StringComparison.Ordinal);
        Assert.True(
            renderContent.IndexOf("motion.play();", StringComparison.Ordinal) <
                renderContent.IndexOf("restoreContentScrollPositions(container, scrollPositions);", StringComparison.Ordinal),
            "收起來的舊東西要在還原捲軸之前放回去，還原時的版面才和重繪前一樣。");

        Assert.Contains("{ group: '.step-nav', item: '.toc-item', text: '.toc-item__label', active: '.toc-item--current', rigid: true }", app, StringComparison.Ordinal);
        Assert.Contains("if (motion.rigid) {", app, StringComparison.Ordinal);
        Assert.DoesNotContain(".step-nav.has-glide::after", css, StringComparison.Ordinal);

        // 帶過去的標籤改用 SPRINGS.carry 的彈簧；這條原本鎖 320ms，改版後依預期失敗，首敗收據 20261012-120406121-bdf6303c521e434c91f652b5bbd1ab4e。
        // 意圖不變：標籤移動有固定的時間與曲線。
        Assert.Contains("var ARRIVAL_TRAVEL = SPRINGS.carry.duration * 1000;", app, StringComparison.Ordinal);
        Assert.Contains("function travelProgress(elapsed) {", app, StringComparison.Ordinal);
        Assert.Contains("return Math.min(springProgress(SPRINGS.carry, elapsed), 1);", app, StringComparison.Ordinal);
        Assert.Contains("Motion.expect({", app, StringComparison.Ordinal);

        Assert.Contains("data-motion-key=\"' + motionKey(rule, 'rule') + '\"", filter, StringComparison.Ordinal);
        Assert.Contains("keepMotionKey(group, fresh);", filter, StringComparison.Ordinal);
        Assert.Contains("'<ul class=\"filter-check-group__items\" id=\"' + listId + '\" data-collapsible'", filter, StringComparison.Ordinal);
        Assert.Contains("global.JetMotion.morph(container, function () { render(container, Store.getState()); });", filter, StringComparison.Ordinal);
        Assert.Contains("' data-motion-key=\"' + esc('artifact-' + motion + '-' + artifact.artifactId) + '\"'", core, StringComparison.Ordinal);
        Assert.Contains(".filter-workspace .scenario-flat__list > .rule-row:not([data-motion-leaving]) ~ .rule-row::before {", css, StringComparison.Ordinal);
        Assert.Contains("[data-motion-rendering] :is([data-collapsible], [data-motion-pane]) {", css, StringComparison.Ordinal);
        Assert.Contains(".modal[hidden],\n.overview-modal[hidden] {", css, StringComparison.Ordinal);

        Assert.True(
            index.IndexOf("class=\"messages__panel\" id=\"messages-panel\"", StringComparison.Ordinal) <
                index.IndexOf("class=\"messages__rail\" data-action=\"messages-toggle\"", StringComparison.Ordinal),
            "訊息區的開關固定在欄底，展開的清單從它上方長出來。");
        Assert.Contains("aria-controls=\"messages-panel\"", index, StringComparison.Ordinal);
        Assert.DoesNotContain("messages__collapse", index, StringComparison.Ordinal);
        var collapsed = css[css.IndexOf(".messages--collapsed .messages__panel {", StringComparison.Ordinal)..];
        collapsed = collapsed[..(collapsed.IndexOf('}') + 1)];
        Assert.Contains("height: 0;", collapsed, StringComparison.Ordinal);
        Assert.Contains("visibility: hidden;", collapsed, StringComparison.Ordinal);

        Assert.Contains("  .arrival-token {\n    display: none;\n  }", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Frontend_AddedConditionsCarryTheScroll_AndInPlaceChangesStayContinuous()
    {
        // 2026-10-12 獨立檢查在瀏覽器主機逐格量到：第二條以後的條件不飛、中欄一下跳約 900px；訊息區高度沒有在動，
        // 視窗矮時開關被推出畫面；切換常用查核條件的第一格仍會閃；切換頁籤時已打開的區塊會再長一次；原地換掉的區塊新的在上面；
        // 收合目前的步驟一瞬間消失；設為主要條件沒有動態。這裡鎖住修正後的做法。
        var app = ReadFrontend("js", "app.js");
        var css = ReadFrontend("css", "app.css");
        var filter = ReadFrontend("js", "steps", "filter-step.js");
        var mapping = ReadFrontend("js", "steps", "mapping-step.js");

        // 加入的條件由標籤帶過去時，中欄跟著標籤用同一條曲線捲到新的一列，移焦點不再瞬間捲動。
        Assert.Contains("function scrollPlan(area, row, limit) {", app, StringComparison.Ordinal);
        Assert.Contains("function drive(eased) {", app, StringComparison.Ordinal);
        Assert.Contains("var to = { x: point.x - offset.x, y: point.y - offset.y - remaining };", app, StringComparison.Ordinal);
        Assert.Contains("if (lastFrame && now - lastFrame > 50) { started += now - lastFrame - 17; }", app, StringComparison.Ordinal);
        Assert.Contains("carries: function (element) { return !!carried && !!element && carried.has(element); }", app, StringComparison.Ordinal);
        Assert.Contains("if (options && options.preventScroll) { element.focus({ preventScroll: true }); } else { element.focus(); }", app, StringComparison.Ordinal);
        Assert.Contains("var carried = !!(global.JetMotion && global.JetMotion.carries && global.JetMotion.carries(row));", filter, StringComparison.Ordinal);
        Assert.Contains("if (!carried) { row.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: 'instant' }); }", filter, StringComparison.Ordinal);
        Assert.Contains("global.JetFocus.move(control, { preventScroll: true });", filter, StringComparison.Ordinal);
        // 新的一列照常長出來，只有標題等標籤放下。
        Assert.Contains("var holds = hide(title);", app, StringComparison.Ordinal);
        // 長出來改用彈簧並帶著此刻的速度；這條原本鎖不帶速度的呼叫，改版後依預期失敗，首敗收據 20261012-120406121-bdf6303c521e434c91f652b5bbd1ab4e。
        // 使用者 2026-10-13 要求移除前先輕微快速地晃一下，同一次重繪的其他動態因此晚一點開始，呼叫多帶 delay；
        // 這條原本鎖只帶 velocity 的呼叫，改版後依預期失敗，首敗收據 20261013-054600810-effc73931390437c8015d25ac3a6ebf1。
        // 意圖不變：新的一列照常長出來，只有標題等標籤放下。
        Assert.Contains("run(root, item.key, item.element, item.size, item.presence, 1, { velocity: item.velocity, delay: lead });", app, StringComparison.Ordinal);

        // 收放時 flex 項目放開最小尺寸、gap 跟著收放；舊的放在同一個空隙裡新出現的東西前面；換順序的在原位收起、新位置長出。
        Assert.Contains("frame[size.inline ? 'minWidth' : 'minHeight'] = '0px';", app, StringComparison.Ordinal);
        Assert.Contains("function gapOf(element, layout, flex, inline) {", app, StringComparison.Ordinal);
        Assert.Contains("while (neighbor.previousElementSibling && entering(neighbor.previousElementSibling)) { neighbor = neighbor.previousElementSibling; }", app, StringComparison.Ordinal);
        Assert.Contains("function reordered(snapshot, current) {", app, StringComparison.Ordinal);
        // 看不到畫面時不播；記下的畫面最多沿用一秒；still 之後的重繪拿 still 的結果來比；中途轉向時字仍和選取一起到。
        Assert.Contains("function pageHidden() { return document.visibilityState === 'hidden'; }", app, StringComparison.Ordinal);
        Assert.Contains("if (root.__jetMotionPending && at - root.__jetMotionAt < 1000) {", app, StringComparison.Ordinal);
        Assert.Contains("if (quiet > 0) {\n          root.__jetMotionPending = false;", app, StringComparison.Ordinal);
        Assert.Contains("animations = animations.concat(animateText(group, spec, motion, options, prior));", app, StringComparison.Ordinal);

        // 頁籤底下的窗格重新出現時，裡面已打開的展開區不重播。
        Assert.Contains("function setShown(element, visible) {", app, StringComparison.Ordinal);
        Assert.Contains("[data-motion-revealing] :is([data-collapsible], [data-motion-pane]) {", css, StringComparison.Ordinal);
        // 使用者 2026-10-12 要求切換流暢後，頁籤底下換窗格改由 JetMotion.swap 原地淡換、高度平順改變，新的窗格仍用 setShown 顯示。
        // 這三條原本鎖 showPane，改版後一併改寫（同一個測試的首敗收據 20261012-120406121-bdf6303c521e434c91f652b5bbd1ab4e，當時先停在前面的斷言）。
        // 意圖不變：三組頁籤都經過同一個原地切換，已打開的展開區不重播。
        Assert.Contains("showOnly(container.querySelectorAll('[data-filter-pane]'), function (pane) { return pane.dataset.filterPane === viewState.workspacePane; },", filter, StringComparison.Ordinal);
        Assert.Contains("showOnly(container.querySelectorAll('[data-condition-pane]'), function (pane) { return pane.dataset.conditionPane === viewState.conditionSource; });", filter, StringComparison.Ordinal);
        Assert.Contains("showOnly(boxes, function (box) { return box.getAttribute('data-bind') === 'matrix-' + viewState.matrixView; }, function () {", filter, StringComparison.Ordinal);
        Assert.Contains("if (to && global.JetMotion && global.JetMotion.swap) { global.JetMotion.swap(from, to, fill); return; }", filter, StringComparison.Ordinal);
        var swap = app[app.IndexOf("function swap(from, to, fill) {", StringComparison.Ordinal)..];
        swap = swap[..swap.IndexOf("swap.count = 0;", StringComparison.Ordinal)];
        Assert.Contains("setShown(to, true);", swap, StringComparison.Ordinal);

        // 收合目前的步驟：整段內容先收回標題再換成一行；展開時整段從標題長出來。
        // 使用者 2026-10-13 要求展開與收合一致套用動態，換步驟時舊的步驟內容也收回它那一列、新的從標題長出來，
        // 所以先找出新的內容再判斷要不要長出來；這條原本鎖直接查詢後長出來的呼叫，改版後依預期失敗，
        // 首敗收據 20261013-060027504-4116bbcc5f8540f09d1adc5f31c94e27。意圖不變：展開與換步驟時整段從標題長出來。
        Assert.Contains("global.JetMotion.fold(body, function () { Store.setStepFlowCollapsed(true); });", app, StringComparison.Ordinal);
        Assert.Contains("var newBody = container.querySelector('.stepflow-item--current > .stepflow-item__body');", app, StringComparison.Ordinal);
        Assert.Contains("} else if ((stepFolded || switching) && !bodyGrowth) {\n          global.JetMotion.enter(newBody);", app, StringComparison.Ordinal);
        Assert.Contains("global.JetMotion.leave(foldingBody.element, stepRow(foldingBody.index), foldingBody.from)", app, StringComparison.Ordinal);

        // 第三步的人工與自動代碼用值配對識別、左右收放。
        Assert.Contains("var keys = codeMotionKeys(values, group);", mapping, StringComparison.Ordinal);
        Assert.Contains("'<label class=\"map-options__code-value\" data-motion=\"inline\" data-motion-key=\"' + Ui.esc(keys[index]) + '\">", mapping, StringComparison.Ordinal);

        // 原地展開區：收起與剛出現的大小一定贏過元件自己的內外距；在 flex 裡也真的改變高度。常用查核條件的白紙收起時一起淡掉。
        Assert.Contains("[data-collapsible] {\n  min-height: 0;", css, StringComparison.Ordinal);
        Assert.Contains("[data-collapsible][hidden] {\n  height: 0 !important;", css, StringComparison.Ordinal);
        Assert.Contains("@starting-style {\n  [data-collapsible]:not([hidden]) {\n    height: 0 !important;", css, StringComparison.Ordinal);
        Assert.Contains(".filter-workspace .filter-check-group__items {\n  margin-bottom: var(--space-2);", css, StringComparison.Ordinal);
        Assert.DoesNotContain(".filter-check-group__items:not([hidden])", css, StringComparison.Ordinal);

        // 訊息區：高度真的在動；視窗矮時黏在欄底，步驟從後面捲過去時才變成玻璃。
        var panel = css[css.IndexOf(".messages__panel {", StringComparison.Ordinal)..];
        panel = panel[..(panel.IndexOf('}') + 1)];
        Assert.Contains("min-height: 0;", panel, StringComparison.Ordinal);
        var drawer = css[css.IndexOf(".messages {", StringComparison.Ordinal)..];
        drawer = drawer[..(drawer.IndexOf('}') + 1)];
        Assert.Contains("position: sticky;", drawer, StringComparison.Ordinal);
        Assert.Contains("bottom: 0;", drawer, StringComparison.Ordinal);
        Assert.Contains(".toc-col[data-under-messages] .messages {", css, StringComparison.Ordinal);
        Assert.Contains("column.toggleAttribute('data-under-messages', column.scrollHeight - column.scrollTop - column.clientHeight > 1);", app, StringComparison.Ordinal);

        // 樣式不掛在收起中的舊元素會被拿掉的 data-bind 上；高對比佈景時原地展開區與訊息區直接換。
        Assert.Contains(".import-card--calendar > .calendar-nonworking__hint", css, StringComparison.Ordinal);
        Assert.DoesNotContain("[data-bind=\"import-card-calendar\"] >", css, StringComparison.Ordinal);
        Assert.Contains(".account-mapping-checks > .account-mapping-checks__list", css, StringComparison.Ordinal);
        Assert.Contains("  [data-collapsible],\n  [data-collapsible][hidden],\n  [data-motion-pane],\n  .messages__panel,", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Frontend_MotionFollowsAppleSprings_AndInPlaceChangesStayContinuous()
    {
        // 使用者 2026-10-12 指出第三步重新配對時換金額欄位格式，選取一閃而過、提示直接跳出來，並要求動態更有質感、更自然，
        // 先研究 Apple 的設計理念與指引，再套用到全部流程畫面。這裡鎖住修正後的共用做法。
        var app = ReadFrontend("js", "app.js");
        var css = ReadFrontend("css", "app.css");
        var mapping = ReadFrontend("js", "steps", "mapping-step.js");
        var validate = ReadFrontend("js", "steps", "validate-step.js");
        var filter = ReadFrontend("js", "steps", "filter-step.js");

        // 全部的動態是同一組 Apple 的彈簧（WWDC23「Animate with springs」），CSS 的曲線與時間由同樣的參數算出來。
        // 使用者 2026-10-13 指出淡入淡出太緩慢、要求加快，長出來與收起改成 0.34 秒與 0.27 秒；
        // 這四條原本鎖 0.4 秒、0.32 秒、588ms 與 471ms，改版後依預期失敗，首敗收據 20261013-054600810-effc73931390437c8015d25ac3a6ebf1。
        // 意圖不變：JS 的彈簧與 CSS 的時間由同樣的參數算出來。
        Assert.Contains("glide: { duration: 0.34, bounce: 0 },", app, StringComparison.Ordinal);
        Assert.Contains("open: { duration: 0.34, bounce: 0 },", app, StringComparison.Ordinal);
        Assert.Contains("close: { duration: 0.27, bounce: 0 },", app, StringComparison.Ordinal);
        Assert.Contains("function springSegment(preset, from, to, velocity, start) {", app, StringComparison.Ordinal);
        Assert.Contains("--ease-spring: linear(", css, StringComparison.Ordinal);
        Assert.Contains("--motion-glide: 500ms;", css, StringComparison.Ordinal);
        Assert.Contains("--motion-open: 500ms;", css, StringComparison.Ordinal);
        Assert.Contains("--motion-close: 400ms;", css, StringComparison.Ordinal);

        // 選取依區塊識別認得重繪後的同一組；滑到一半改去別的地方時帶著此刻的速度接著走。
        Assert.Contains("var holder = group.closest('[data-motion-key]');", app, StringComparison.Ordinal);
        Assert.Contains("var velocity = moving ? velocityAt(last.motion, now) : null;", app, StringComparison.Ordinal);

        // 標了識別的東西底下直接一層的區塊也比對；同一個東西重繪後高度變了或字變了，原地改變高度或交叉淡化。
        Assert.Contains("var implicitKey = key + '>' + name + '#' + counts[name];", app, StringComparison.Ordinal);
        Assert.Contains("function change(root, key, element, options) {", app, StringComparison.Ordinal);
        Assert.Contains("if (item.fade) { item.copy = old.cloneNode(true); }", app, StringComparison.Ordinal);
        // 放回去的是舊元素的複本：同一個畫面週期裡再重繪一次時，拿來比對的仍是完整的舊畫面。
        Assert.Contains("return Object.assign({}, item, { element: item.element.cloneNode(true) });", app, StringComparison.Ordinal);
        // 舊的那一份放在不會讓新的畫面變形的位置；內容不同的整塊先後淡換。
        Assert.Contains("function seat(ghost, parent, spots, around) {", app, StringComparison.Ordinal);
        Assert.Contains("through: Math.max(pair.from, pair.item.size.natural.height) > THROUGH_HEIGHT", app, StringComparison.Ordinal);
        // 裡面沒有接住的高度改變，由內往外補上外層的高度；收放期間不讓 flex 清單把它壓扁。
        Assert.Contains(".sort(function (a, b) { return depthOf(b.element) - depthOf(a.element); })", app, StringComparison.Ordinal);
        Assert.Contains("frame.flexShrink = '0';", app, StringComparison.Ordinal);
        // 打字時不交叉淡化，捲動容器本身不改高度。
        Assert.Contains("function typing(root) {", app, StringComparison.Ordinal);
        Assert.Contains("item.grow = !item.scrolls && item.before.height > 0 && Math.abs(item.to - item.before.height) > 2;", app, StringComparison.Ordinal);

        // 第三步的提示、對照表、必填清單與資料處理都有識別；各步驟的主畫面也有，底下直接一層的區塊因此都會比對。
        Assert.Contains("data-motion-key=\"mapping-banner-' + kind + '\">", mapping, StringComparison.Ordinal);
        Assert.Contains("'<div class=\"map-layout\" data-motion-key=\"map-layout-' + kind + '\">' +", mapping, StringComparison.Ordinal);
        Assert.Contains("'\" data-motion-key=\"map-rail-' + kind + '\">' +", mapping, StringComparison.Ordinal);
        Assert.Contains("data-motion-key=\"map-options-gl\"", mapping, StringComparison.Ordinal);
        Assert.Contains("data-motion-key=\"mapping-panel\"", mapping, StringComparison.Ordinal);
        Assert.Contains("data-motion-key=\"validate-panel\"", validate, StringComparison.Ordinal);
        Assert.Contains("'<div class=\"account-methods-block\" data-motion-key=\"account-methods\">' +", validate, StringComparison.Ordinal);
        Assert.Contains("data-motion-key=\"filter-panel\"", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void Frontend_RemovalsShakeFirst_AndFadesStayQuick_AcrossTheWholeTool()
    {
        // 使用者 2026-10-13 指出淡入淡出太緩慢；要求展開與收合的元素一致套用動態；進階條件篩選加入條件時的標籤不要跳躍；
        // 清除條件時那一下輕微快速的晃動不錯，要讓右側條件組合、移除案件，以及其他流程中要移除或清除的東西都這樣晃一下。
        // 這裡鎖住共用的做法：晃動由 JetMotion 統一處理，按下移除的地方只說明要拿掉的是什麼。
        var app = ReadFrontend("js", "app.js");
        var css = ReadFrontend("css", "app.css");
        var filter = ReadFrontend("js", "steps", "filter-step.js");

        // 淡入淡出比長出來與收起短，淡出又比淡入短一點。
        Assert.Contains("--motion-fade: 150ms;", css, StringComparison.Ordinal);
        Assert.Contains("--motion-fade-out: 120ms;", css, StringComparison.Ordinal);
        Assert.Contains("var FADE_IN = 100;", app, StringComparison.Ordinal);
        Assert.Contains("var FADE_OUT = 130;", app, StringComparison.Ordinal);

        // 晃動是同一組 Apple 的彈簧，最遠偏離 4px；晃到第二下時這一次重繪的其他動態才開始。
        Assert.Contains("shake: { duration: 0.11, bounce: 0.75 }", app, StringComparison.Ordinal);
        Assert.Contains("var SHAKE_REACH = 4;", app, StringComparison.Ordinal);
        Assert.Contains("function removing(intent) {", app, StringComparison.Ordinal);
        Assert.Contains("var lead = doomed.size ? SHAKE_LEAD : 0;", app, StringComparison.Ordinal);
        Assert.Contains("removing: removing, shake: shakeNow", app, StringComparison.Ordinal);

        // 移除鈕放在要拿掉的東西裡面時，由同一張表記下要晃的是什麼；確認後才重繪的地方由畫面程式說明。
        Assert.Contains("var REMOVAL_SOURCES = [", app, StringComparison.Ordinal);
        Assert.Contains("{ trigger: '[data-action=\"clear-set\"]', scope: '.scenario-set', inside: true, only: '.rule-row' },", app, StringComparison.Ordinal);
        Assert.Contains("{ trigger: '[data-action=\"remove-set\"]', scope: '.scenario-set' },", app, StringComparison.Ordinal);
        Assert.Contains("global.JetMotion.removing({ keys: 'project-' + target.id, wait: 20000 });", app, StringComparison.Ordinal);
        Assert.Contains("global.JetMotion.removing({ keys: rules.map(function (rule) { return motionKey(rule, 'rule'); }) });", filter, StringComparison.Ordinal);
        Assert.Contains("global.JetMotion.removing({ within: ['filter-panel'], only: '.scenario-set, .rule-row' });", filter, StringComparison.Ordinal);

        // 重繪時打開著的選單不會一下不見，在原處淡掉。
        Assert.Contains("function fadeMenus(root, snapshot) {", app, StringComparison.Ordinal);
    }

    [Fact]
    public void Frontend_ReducedMotionTurnsTransitionsOff_InsteadOfShorteningThem()
    {
        // 使用者 2026-10-12 要求查明減少動態時重繪變慢。原本這裡替每個元素設 0.001ms 的 transition-duration，
        // 頁面一開始就套用時，減少動態下的重繪慢兩到四倍；改成 transition: none 後回到原本的速度，畫面一樣瞬間完成，
        // 也不留轉場的延遲。
        var css = ReadFrontend("css", "app.css");
        var block = css[css.IndexOf("@media (prefers-reduced-motion: reduce) {\n  *,\n  *::before,\n  *::after {", StringComparison.Ordinal)..];
        block = block[..(block.IndexOf("}\n}", StringComparison.Ordinal) + 3)];

        Assert.Contains("transition: none !important;", block, StringComparison.Ordinal);
        Assert.DoesNotContain("transition-duration", block, StringComparison.Ordinal);
        // CSS 動畫仍直接停在最後的樣子，表格表頭捲動時加深的影子照常。
        Assert.Contains("animation-duration: 0.001ms !important;", block, StringComparison.Ordinal);
    }
}
