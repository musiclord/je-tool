# 來源、授權與查核日期

只有追查設計來由或維護本 skill 時才需要讀這份。各節保留原研究的查核日期，不代表 2026-10-16 全部重新研究過。
本 skill 只吸收下列來源的原則，用自己的話針對 JET 改寫，沒有複製原文；引用的樣式名稱與偵測規則名稱只用來對照。
研究過程的完整筆記在本機 `artifacts/research/2026-10-08-frontend-design-research.md`，不進 Git。

## 使用者指定的方向與原話

- MindBridge 的 [KPMG 合作頁](https://www.mindbridge.ai/partner-program/kpmg-mindbridge/) 是使用者 2026-10-10 指定的參考。
  原話與樣張在 KCT 需求計畫（歷史文件）的
  「整體介面大改版：研究與方向（2026-10-10）」一節，總表條目是 2026-10-10-03。
  後續保留的是商業工具的結構與部分元件分工，暖紙、石板藍和淺色標題列依使用者後續裁定；不能直接恢復參考網站的深藍漸層。
- KCT 需求計畫的「介面色系、材質與標誌調整（2026-10-11）」「依 Apple 設計理念重新調整外觀（2026-10-11）」
  「整體的 Liquid Glass 質感（2026-10-12）」「動態的完整度、流程欄與訊息區（2026-10-12）」
  「第三步切換閃動與整體動態重新設計（2026-10-12）」與「加快淡入淡出、補齊展開收合與移除前的晃動（2026-10-13）」保留原話，
  總表條目是 2026-10-11-07、2026-10-11-14、2026-10-12-01、2026-10-12-02、2026-10-12-03、2026-10-13-10、2026-10-13-11 與 2026-10-13-13。
  現行結果見 [前端說明](../../../../docs/jet-frontend-description.md)。
- 介面動態與驗證循環整理計畫（歷史文件）保存動態速度、玻璃彈性與檢查時機的後續裁定，總表條目是 2026-10-15-05 與 2026-10-15-08。
  使用者否定的是標籤的拋物線，不能轉述為禁止所有回彈。
- 2026-10-16 技能整理改為「正文放有效指示、參考檔保存來源」，沒有重新採用已被後續裁定取代的樣張。
  表格表頭、「傳票符合的條件」的代號表頭與 200 筆分頁依裁定 2026-10-16-01、2026-10-16-02 與 2026-10-16-03，原話在
  資料表分頁計畫（歷史文件）；程式數值與完成狀態分別查現行來源和計畫。

下列版本、實測數字與日期都是當時研究紀錄。它們說明設計由來，不用來宣稱目前外部套件版本、帳號能力或畫面測量結果。

## 本 skill 的底稿

- Anthropic `frontend-design` skill 第三版：anthropics/skills 的提交 `41bbe19`，2026-09-03。
  https://github.com/anthropics/skills/tree/main/skills/frontend-design 。授權是 Apache License 2.0。
  採用的原則：先界定主題與受眾、先寫設計計畫再對照預設樣式自審、把大膽只用在一個地方、邊做邊截圖檢查、
  五種 AI 常見風格的分類，以及介面文案寫法。拿掉的部分：網頁首屏、行銷頁的字體冒險與裝飾動畫。
- 這台電腦的 Claude 環境另外載入同名 skill 的 2025 年第一版，要求選擇大膽極端的美學；
  它不適合 JET，本 skill 取代它在 JET 前端的角色。

## 官方提示文件

- Anthropic〈Prompting Claude Opus 5.5〉的 Frontend design defaults 一節：
  https://platform.claude.com/docs/en/build-with-claude/prompt-engineering/prompting-claude-opus-5-5 。
  點名奶油或米白底、標題斜體強調字、01／02／03 編號、等寬小標、膠囊按鈕；建議逐項點名並逐輪擴充清單。
- Anthropic〈Prompting Claude Opus 4.8〉的 Design and frontend defaults 一節：
  https://platform.claude.com/docs/en/build-with-claude/prompt-engineering/prompting-claude-opus-4-8 。
  說明暖奶油色預設不適合儀表板、金融與企業應用；建議指定具體替代方案，或先提出四個方向讓使用者選。

## 社群 skill 與工具

| 來源 | 版本與日期 | 授權 | 採用了什麼 |
|:---|:---|:---|:---|
| pbakaus/impeccable，https://github.com/pbakaus/impeccable | skill v4.5.0，2026-10-02；偵測引擎 engine-v0.1.12，2026-10-07 | Apache-2.0 | Operate 模式的原則、59 條偵測規則中的 AI 樣式名稱、狀態與錯誤訊息寫法。社群評價是適合收尾、不擅長從零設計 |
| Leonxlnx/taste-skill，https://github.com/Leonxlnx/taste-skill | 無版本號，最後提交 2026-10-07 | MIT | 禁用清單中的暖色底、陶土色與行銷文案；高密度時用細線分組。它自己說明不處理儀表板與後台 |
| nextlevelbuilder/ui-ux-pro-max-skill，https://github.com/nextlevelbuilder/ui-ux-pro-max-skill | skill.json 標示 2.13.0，最後提交 2026-10-08 | MIT | 無障礙、表單、圖表與數字格式的規則。它對金融與後台推薦的配色與字體本身就是常見預設，不採用 |
| vercel-labs/web-interface-guidelines，https://github.com/vercel-labs/web-interface-guidelines | 最後推送 2026-10-05 | MIT | 鍵盤、焦點、表單、數字與載入狀態的條目 |
| Dammyjay93/interface-design，https://github.com/Dammyjay93/interface-design | 最後推送 2026-06-20 | MIT | 只選一種深度策略、密度一致、每天上百次的操作不加動畫、瞇眼檢查 |
| garrytan/gstack 的 design-consultation、design-shotgun、design-review，https://github.com/garrytan/gstack | 最後推送 2026-10-08 | MIT | 讓非設計背景的使用者用選擇題決定方向、後台專屬的毛病清單 |
| anthropics/skills PR #1806，https://github.com/anthropics/skills/pull/1806 | 2026-09-23，尚未合併 | 不適用 | 點名禁止的實測數字；第二層預設是從它的樣本頁觀察，未經統計 |
| educlopez/ui-craft，https://github.com/educlopez/ui-craft | 無版本號；第三方介紹頁寫最後推送 2026-09-03 | MIT | 篩選控制不搶主要動作、相關的篩選分在一起。它把篩選鍵做成沒有框的按鈕，不採用 |
| abdiraimov-otabek/dashboard-design-skill，https://github.com/abdiraimov-otabek/dashboard-design-skill | README 標示 2.0，頁面沒有日期 | MIT | 一張卡用細線分組，勝過好幾張浮動卡。它把建議的篩選做成近似膠囊的晶片，不採用 |
| s0xDk/refactoring-ui-skill，https://github.com/s0xDk/refactoring-ui-skill | 頁面沒有日期 | MIT，不含原書 | 動作分主要、次要與第三級；組外的間距大於組內；降低其他元素來突顯重點。它把第三級動作做成連結，使用者要看得出能按，不採用 |

## 欄位條件分頁的重新設計

查核日期是 2026-11-03，用於使用者同日要求深入研究欄位條件分頁，缺少參考時上網找強化美學的 skills（裁定 2026-11-03-28）。
研究報告在本機 `artifacts/research/2026-11-03-field-picker-design-research.md`，不進 Git。

- Excel 的自動篩選、Linear、Airtable 與 Metabase，都是先選欄位，再依欄位型別出現該欄位的查法；Power BI 的篩選窗格是一個欄位一張卡。
  https://support.microsoft.com/en-us/office/filter-data-in-a-range-or-table-01832226-31b5-4568-8806-38c37dcc180e 、
  https://linear.app/docs/filters 、https://support.airtable.com/docs/filtering-records-using-conditions 、
  https://www.metabase.com/docs/latest/questions/query-builder/filters 、https://learn.microsoft.com/en-us/power-bi/create-reports/power-bi-report-filter 。
- GitHub Primer ActionList：分隔線只在幫助閱讀時才加，列尾的動作屬於次要。https://primer.style/product/components/action-list/guidelines/ 。
- 三個社群 skill 見上面「社群 skill 與工具」最後三列，用網頁讀過內容，沒有下載安裝。

採用的原則：一列只放一把按鍵，其他新增動作收進那把按鍵打開的選單；同一張紙用小標與細線分組；組外的間距大於組內。
沒有採用的部分與原因：把次要動作做成沒有框的文字、連結或晶片，和 JET 白底細框按鍵的裁定衝突；使用者裁定合併時以 JET 的規則為準
（裁定 2026-11-03-34）。收起時與打開後的樣子，是使用者從三份樣張中選的（裁定 2026-11-03-32）；選單改從按鍵打開，是使用者在內建瀏覽器試用兩種開法後選的
（裁定 2026-11-03-37）。兩者都不是從來源推得。

## 設計系統與排版規範

- IBM Carbon：data table 的樣式與使用、status indicator、empty states、notification。
  https://carbondesignsystem.com/components/data-table/usage/ 等頁。列高分級、狀態要用兩種以上線索、空狀態三分類。
- Microsoft Windows 應用程式設計：字體排印、版面間距、色彩。
  https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography 等頁。字級階層、只用 Regular 與 Semibold、間距數值。
- GitHub Primer data table：https://primer.style/product/components/data-table/ 。數字欄靠右與等寬數字。
- Atlassian dynamic table：https://atlassian.design/components/dynamic-table/usage 。選取不只靠反白、欄標題不放按鈕。
- W3C〈Requirements for Chinese Text Layout〉clreq：https://www.w3.org/TR/clreq/ 。正文不加字距、台灣標點多半不壓縮、強調不用斜體。
- W3C i18n〈Managing inline spaces in Chinese & Japanese〉：https://www.w3.org/International/articles/styling/inline-space 。
- MDN 相容性資料：`text-autospace` 的 `normal` 自 Chromium 140 起支援，預設不開；`text-spacing-trim` 自 Chromium 123 起支援。

## Apple 的設計理念與 Liquid Glass

查核日期是 2026-10-11，用於使用者同日要求「借用蘋果在設計的思路，長年下來到現在最新的改動之後的理念和表達」的調整。
兩份研究筆記在本機 `artifacts/research/2026-10-11-apple-design-research.md`，不進 Git。HIG 頁面是 JavaScript 產生的，
直接抓網頁只拿到標題，要改抓 `https://developer.apple.com/tutorials/data/design/human-interface-guidelines/<頁名>.json`。

- Human Interface Guidelines：Design principles（2026-06-08 新增）、Materials、Toolbars、Sidebars、Scroll views、Layout、Color、
  Branding、Motion、Buttons、Pointing devices、Focus and selection、Designing for macOS。
  https://developer.apple.com/design/human-interface-guidelines/ 。
- WWDC 逐字稿，網址是 `https://developer.apple.com/videos/play/<年份>/<編號>/`：
  2018 年 803「Designing Fluid Interfaces」；2020 年 10640「Design for the iPadOS pointer」；
  2023 年 10072「Principles of spatial design」與 10076「Design for spatial user interfaces」；
  2025 年 219「Meet Liquid Glass」、356「Get to know the new design system」、310「Build an AppKit app with the new design」、
  323「Build a SwiftUI app with the new design」與 102 Platforms State of the Union；
  2026 年 101 主題演講、102 Platforms State of the Union、289「Modernize your AppKit app」、269 與 250「Principles of great design」。
- Apple Newsroom 2025-06-09 的新設計發表稿；Apple 支援文章 123075 與 122868，記載 26.1 起的 Clear 與 Tinted 外觀選項。
- 第三方：Nielsen Norman Group〈Liquid Glass Is Cracked, and Usability Suffers in iOS 26〉，2025-10-10，https://www.nngroup.com/articles/liquid-glass/ 。

採用的原則：內容層與操作層分開，玻璃只用在操作層、不疊玻璃；層級靠版面與分組，不靠裝飾；內容捲到浮動元素下面時才出現分界，
Mac 與表頭用清楚的分界；巢狀形狀同心；顏色只給主要動作與狀態；按下時立刻回饋、材質從裡面亮起；換狀態時元素從原處移動過去，
預設不回彈；降級設定要讓玻璃變成不透明。也採用 2026 年 Mac 介面的修正方向：頂端一條一致的工具列、側欄貼齊視窗邊緣、
側欄圖示帶顏色、選取的導覽項目用半粗體、玻璃更霧更好讀。

2026-10-11 第三次調整再從同一批來源採用：元素在兩個狀態之間變形而不是消失再出現（WWDC25 219 的 morphing 與 materialization，
用在條件飛進條件組與新的一列浮現）；可以拿起來的東西在指標移上去時浮起一點（WWDC20 10640 的 lift，JET 只加長影子、不放大）；
無框按鈕用 tint 色表示能按、一般文字不上色（人機介面指引 Buttons）；展開的內容從標題長出來，維持空間一致性（WWDC18 803）。
頂部改回橫跨整個視窗的一條標題列，是使用者從兩種樣張中選定的（裁定 2026-10-11-10），不是從 Apple 來源推得。

2026-10-12 使用者認為條件飛進條件組的做法像廉價的套版，所以變形只用在起點與落點：標籤大小不變地帶過去，
只在落點讓字疊在同樣的標題上再交接，途中不變形；移動原本用 Core Animation 的 ease-in-ease-out，同一天稍後改用下一節的彈簧。
加入、移除、清除與換掉的東西一律從原處長出來或收回去，這是同一個空間一致性的原則套到整個 JE Tool，
不是新的 Apple 來源。流程欄的選取同樣大小平移，是使用者的要求（裁定 2026-10-12-02），不是從來源推得。

沒有採用的部分與原因：玻璃的折射，因為 CSS 只能模糊、做不出折射，而且 JET 的控制項底下多半是平的紙面，折射也看不出來；
固定在畫面底部、內容從下面經過的玻璃操作列，因為它會蓋住條件列，Gui 測試用真實滑鼠點擊時也會被擋住；
跟著指標的高光，因為它在 iPadOS 是用來顯示被吸附的指標真正的位置，Windows 的指標不會被吸附；膠囊形按鈕，
因為 Apple 在密集的 Mac 版面也只給大尺寸控制項用膠囊，JET 的按鈕形狀又是使用者核准過的 6px。

## Apple 的動態與跨平台一致性

查核日期是 2026-10-12，用於使用者同日要求「先完整的研究近年來蘋果的設計哲學」再重做動態。三份研究筆記在本機
`artifacts/research/2026-10-12-apple-motion.md`、`2026-10-12-apple-consistency.md` 與 `2026-10-12-web-spring-motion.md`，不進 Git。

- Apple 的彈簧：WWDC23 10158「Animate with springs」與 10156「Explore SwiftUI animation」，WWDC24 10145「Enhance your UI animations and transitions」，
  SwiftUI `Spring` 文件 https://developer.apple.com/documentation/swiftui/spring 。彈簧只用 duration 與 bounce 描述，duration 是看起來完成的時間；
  stiffness 是 (2π ÷ duration)²，阻尼比是 1 − bounce；彈簧被打斷時保留速度。公式用文件的兩個範例數字驗算過。
  WWDC23 投影片上的阻尼寫法照字面算單位不對，Apple 開發者論壇 thread 739811 的修正版和文件範例一致。
- 人機介面指引 Motion（2025-09-09 更新）與 Accessibility：回饋要簡短精準，一般 App 不替頻繁的互動加動態，動畫要能取消；
  Liquid Glass 對觸控的反應比較明顯，對觸控板比較收斂；降低動態時把移動改成淡入淡出。整頁沒有給任何毫秒數。
- 跨平台的一致性：上一節列的 WWDC25 219、356、310 與 WWDC26 250、289，另讀人機介面指引 Layout 2026-09-09 版。
  同一套元件構造在各平台只調整密度、尺寸與擺放；空間大小只決定露出多少功能；看起來一樣的東西，行為也要一樣。
- 網頁實作：Chrome for Developers 的〈CSS linear() easing function〉、〈Animate to height: auto〉與〈Hardware-accelerated animations〉，
  MDN 的 `linear()` 與 `Animation.commitStyles()`，以及 Lighthouse 12.6.1 的 non-composited-animations 原始碼。
  第三方工具 https://www.kvin.me/css-springs 取樣出來的曲線，和用上面公式算的一致。

採用的原則：全部的動態用同一組 Apple 的彈簧參數，被打斷時帶著速度接著走；CSS 的 `linear()` 只能取樣固定的曲線，轉場被打斷時從零速重來，
所以會被連續操作打斷的動態改由 `app.js` 用公式解播放。時間比 Apple 預設的 0.5 秒短一點，依據是人機介面指引說 Liquid Glass 用觸控板操作時的反應比直接觸控收斂，用滑鼠操作的 JET 跟著收斂；
0.34、0.4 與 0.32 秒這些數字是 Claude 依此判斷，不是 Apple 給的；2026-10-15 同比例縮短成 0.28、0.22 與 0.3 秒，
也是 Claude 依「不回彈的彈簧形狀相同、只差時間長短」所做的判斷，沒有另查新的來源。同一份筆記整理的建議是選單 0.25 到 0.3 秒、
選取與清單插入 0.3 秒、面板收合 0.3 秒；收起的 0.22 秒比收合建議短，理由是收起時字先淡出，要拿掉的東西不需要看清去向。原地改變的東西在兩個狀態之間變形，不消失再出現（WWDC25 219）；
內容差很多的整塊先淡掉舊的、再淡入新的，是 Claude 看過逐格截圖後的判斷，Apple 文件沒有規定先後順序。降低動態時直接換。

沒有採用的部分與原因：View Transitions，因為新的轉場會讓前一個直接跳到終點、不保留速度，連續操作時會跳；
`composite: 'add'` 這類疊加的動畫，因為 Chromium 不能在合成層播放它。研究也沒有找到要推翻已裁定外觀的理由，所以這次不重做外觀。

## Liquid Glass 與動態性格的重新查核

查核日期是 2026-10-15。使用者同日指出 2026-10-13 的「goofy」是指拋物線軌跡、不是回彈（裁定 2026-10-13-12、2026-10-15-05），要求完整確認 Apple 現行的
Liquid Glass 設計元素與理念，避免拆東牆補西牆。這次由 Claude 直接讀原文，不經研究助手轉述；筆記在本機
`artifacts/research/2026-10-15-apple-liquid-glass-recheck.md`，不進 Git。

- 人機介面指引共 44 頁，重點是 Design principles（2026-06-08 加入）、Materials、Motion、Color、Buttons、Toolbars、
  Segmented controls、Scroll views、Accessibility、Windows 與 Designing for macOS。
  網址是 https://developer.apple.com/design/human-interface-guidelines/ 加上頁名，例如 `motion`、`materials`、`windows`。
- 技術文件：Liquid Glass 總覽與 Adopting Liquid Glass（https://developer.apple.com/documentation/technologyoverviews/liquid-glass ），
  SwiftUI 的 Applying Liquid Glass to custom views、`Spring` 與 `Animation.snappy`、`smooth`、`bouncy`。
- WWDC：2025 年 219、356、310、323、284 與 102；2026 年 250、289、278、269 與 102；2023 年 10158；2024 年 10145；2018 年 803。

查到的事實：

- WWDC25 219：玻璃在互動時立刻彎曲、帶著像凝膠的彈性；元件互動時可以暫時浮起成玻璃；出現與消失不是淡入淡出，
  而是逐步調整折射與模糊。開啟「減少動態效果」會關掉玻璃的彈性；人機介面指引 Accessibility 對這個設定的做法第一條是收緊彈簧、減少回彈。
- 人機介面指引 Motion：Liquid Glass 對觸控板的反應比直接觸控收斂；回饋要簡短精準；頻繁的互動不另加動態；不要讓人等動畫跑完。
- WWDC26 289：macOS 27 給控制項、按鈕與互動控制項的容器加上被點擊時的輕微回彈，原文提醒「A little goes a long way」。
- WWDC23 10158：先用 duration 定節奏，再用 bounce 定性格。bounce 約 0.15 時「doesn't feel very bouncy yet」，但收尾比較俐落；
  約 0.3 開始明顯；0.4 以上對介面太誇張；拿不定時用 0。
- 人機介面指引 Windows：不在作用中的視窗不使用 vibrancy，看起來比較收斂、離得比較遠。
- WWDC26 250：Craft 包含「responsive animations」；按了按鈕要等才有反應，會讓軟體顯得脆弱。

採用的原則：分層、材質與色系維持 2026-10-11、12 的裁定，和上面的事實一致。動態分成兩種性格：玻璃層被操作的東西用 bounce 0.15，
內容層的版面改變維持 bounce 0；Claude 計算同樣的 duration 下，bounce 0.15 走完 95% 的時間從 0.755 倍提早到 0.59 倍，
最多只超過目標 0.6%。選單和對話框用同一種浮現，從稍小、稍霧變清楚；視窗不在前景時玻璃改成不透明。使用者同日選「直接照建議改」（裁定 2026-10-15-05）。
帶過去的標籤維持直線，因為使用者不喜歡的是拋物線軌跡。2026-10-15 第五步實測，從按下到第一個畫格都在 56 到 64 毫秒，
慢的主因是動態的長度與不回彈的長尾巴，不是畫面重繪。

沒有採用的部分與原因：流程欄與底線頁籤的選取不回彈，因為它們是導覽與內容層的標記，不是浮起的玻璃；
帶過去的標籤與中欄的捲動不跟著彈簧超出目標，因為字要剛好疊在標題上，中欄也不能捲過頭。

## Skill 格式

2026-10-16 重新查核格式入口，載入行為以 [agent-compatibility.md](../../../harness/agent-compatibility.md) 為準。

- [Claude Code skills](https://code.claude.com/docs/en/skills)：專案技能放在 `.claude/skills/`；正文之外的參考檔按需讀取。
- [Codex skills](https://learn.chatgpt.com/docs/build-skills)：正本使用 `.agents/skills/`，`agents/openai.yaml` 可設定 `allow_implicit_invocation`。
- [VS Code skills](https://code.visualstudio.com/docs/agent-customization/agent-skills)：技能位置與可選欄位依所選 harness 判斷。
