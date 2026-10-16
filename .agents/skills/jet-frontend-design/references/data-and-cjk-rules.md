# 資料密集畫面與繁體中文排版的規則

每條規則都寫成可以直接檢查的句子，括號內是出處。出處的網址與日期在 `sources.md`。
規則和 `docs/jet-frontend-description.md` 已接受的設計衝突時，以前端說明為準，並回報衝突。

## 色彩

- 只用一個重點色，只標示互動與重要狀態，不放進資料本身。（Windows 色彩指引；impeccable Operate 模式；interface-design）
- 狀態色成套定義並全產品一致：錯誤、注意、通過、資訊、停用、選取、焦點。（impeccable Operate 模式）
- 不只靠顏色傳達意思，至少再加文字、符號或形狀其中一種；狀態指示和背景的對比至少 3:1。（IBM Carbon 狀態指示；Vercel）
- 一般文字對比至少 4.5:1；大字、控制項邊框、圖示與焦點框至少 3:1。（WCAG；impeccable；taste-skill）
- 彩色底上的次要文字從底色推導，不用通用灰色。（impeccable `gray-on-color`）
- 少用半透明疊色，改用明確色值，因為透明度會讓對比隨背景變動。（impeccable）
  JET 依使用者 2026-10-11 與 2026-10-12 的要求帶有 Liquid Glass 的質感，玻璃用在浮在內容上的功能層：標題列、表格固定的表頭、選單、對話框與滑動中的選取。
  玻璃上的字依最壞的背景算對比，例如藍色按鈕剛好捲到標題列的小字底下；內容捲上來時標題列改用霧一點的 `--glass-bar-under`，小字用較深的 `--toolbar-muted`，
  最壞情況仍有 5.2:1。工作面、表格內容、表單與流程目錄的文字底色仍是明確色值。
- 灰階只用同一個色系，不混用冷灰與暖灰。（taste-skill redesign）

## 字級、字重與層級

- 字級只用 `:root` 定義的階層；相鄰兩階相差約 1.15 到 1.25 倍，至少有一階達 1.25 倍。（impeccable Operate 模式與 `flat-type-hierarchy`）
- 層級不只靠字級，同時搭配字重、間距與文字明暗。（impeccable typeset）
- 內文最小 12px，表格與按鈕文字不小於 12px。（Windows 字體排印；impeccable `tiny-text`）
- 說明段落每行不超過約 80 個全形字寬；表格不受此限。（frontend-design 第三版；impeccable）

## 間距與版面

- 間距只取 4 的倍數，例如 4、8、12、16、24、32。控制項之間 8，控制項與標籤之間 12，表面邊緣 16。（Windows 版面指引；impeccable）
- 同一組的元素靠近，不同組拉開；標題上方的間距大於下方。（impeccable `heading-rhythm`）
- 只選一種深度策略：資料密集畫面用邊線與表面明度分層，不加陰影；浮動層例外。（interface-design）
  JET 2026-10-11 依 Apple 的做法，影子只給真的浮起來的東西：分段切換選中的那一格稍微浮起，選單與對話框浮得最高；
  工作面只有貼著的接觸影。流程目錄與資料預覽貼齊視窗邊緣，用細線與明暗一階和中欄分開：流程目錄比畫布暗一階，
  工作面最亮，預覽表格比工作面暗一階。表格、條件組與工作面裡的分區仍只用邊線與表面明度分層。
- 不讓卡片包卡片；先用位置與間距分組，不夠才加框線。（impeccable；frontend-design 第三版）
- flex 與 grid 的子項設 `min-width: 0`，避免長字串撐破版面。（impeccable harden）

## 表格與數字

- 金額、筆數這類數字欄靠右對齊並使用等寬數字；表頭跟著靠右。日期等可以靠左。（Primer；Pencil & Paper；Vercel）
- 同一欄的小數位一致；JET 畫面上的金額一律兩位小數加千分位，由 `ui-core.js` 的 `money` 格式化。（JET 前端說明）
- 列高預設約 32px，緊湊 24px、寬鬆 40px；表頭與資料列同高。（IBM Carbon；Windows 緊湊密度）
- 表頭固定在上方；寬表在自己的框內水平捲動，不撐破外層。（JET 前端說明；Pencil & Paper）
- 只有正在排序的欄顯示排序符號，可排序的表頭使用 `aria-sort`。（IBM Carbon；ui-ux-pro-max）
- 表格工具列最多 5 個動作，其餘放進「操作」選單。（IBM Carbon）
- 長清單分頁或虛擬化，前端只拿有界資料。（Vercel；Primer；JET `AGENTS.md` 架構邊界）

## 表單

- 每個欄位都有看得見的標籤；placeholder 只是範例，不能取代標籤。（impeccable；taste-skill；ui-ux-pro-max）
- 錯誤顯示在欄位旁，用 `aria-describedby` 連結；送出失敗時焦點移到第一個錯誤。（Vercel；ui-ux-pro-max）
- 不在輸入時擋字，事後才顯示驗證結果；不清掉使用者已輸入的內容。（Vercel；impeccable clarify）
- 按鈕寫出具體動作，例如「儲存情境」，不寫「確定」「繼續」。同一個動作在整個流程用同一個名稱。（Vercel；frontend-design 第三版）

## 狀態、空畫面與錯誤

- 每個互動元件都有一般、滑鼠移上去、焦點、按下、停用、處理中與錯誤狀態。（impeccable Operate 模式）
- 空狀態分「還沒開始」「條件下沒有結果」「出錯」三種，各自說明原因並給一個主要的下一步。（IBM Carbon 空狀態）
- 錯誤訊息回答三件事：什麼失敗、知道時說為什麼、怎麼補救。不拿內部代碼當主要訊息，不道歉，不開玩笑。（impeccable clarify；IBM Carbon 通知）
- 要新增會擋住使用者的錯誤時，先寫出使用者接下來能做什麼。（JET `AGENTS.md`）
- 載入指示延遲約 200ms 才出現，出現後至少停留約 300ms；骨架畫面和最終內容同形；按鈕處理中時保留原文字。（Vercel；IBM Carbon）
- 不只設計正常流程，也要看多組、空組、單一選取、錯誤與窄版。（gstack；JET 前端設計模式）

## 焦點與鍵盤

- 所有可操作的元素都有 `:focus-visible` 的 2px 焦點框，對比至少 3:1，全產品一致。（impeccable；Vercel）
- 不寫 `outline: none` 而沒有替代。固定的表頭或工具列不能遮住目前焦點。（Vercel；ui-ux-pro-max）
- 頁籤與分段切換這類群組用方向鍵移動；對話框困住焦點，關閉後焦點回到原處。（impeccable；Vercel）
- 用原生 button，切換類控制項用 `aria-pressed`，不用可點擊的 div。（ui-ux-pro-max；Vercel）

## 動態

- 動態只用來說明狀態改變或回應使用者的操作。一般轉場 150 到 250ms，即時回饋 100 到 150ms。（impeccable Operate 模式）
- 只動 `transform` 與 `opacity`，不用 `transition: all`，不動寬高與位置。（Vercel；impeccable；taste-skill）
  JET 的例外是選取底：它是組裡一個絕對定位的圖層，滑到新選項時寬高跟著變，不會推動其他內容；
  2026-10-12 起分段切換滑動中的選取是組的 `::after` 玻璃，朝目的地拉長再收回，也只動這個圖層；流程欄的選取同樣大小平移，只動位置。
  2026-10-11 第三次調整又加兩個例外，2026-10-12 再擴大：加入條件時帶過去的標籤固定在畫面上、不佔版面，只動位置；
  展開的內容從 0 長到原本的高度、收起時收回 0，加入的東西長出來、拿掉的收起來（app.js 的 JetMotion），
  下面的內容跟著移動，讓人看到東西屬於哪裡、去了哪裡，不會一次跳動。都只在使用者操作造成的改變上做一次。
- 緩動用減速曲線，不用彈跳或彈性曲線。（impeccable animate）
  JET 的內容層用不回彈的彈簧曲線 `--ease-spring`（臨界阻尼），收尾減速、不會衝過頭；這也是 Apple 建議的預設。
  2026-10-15 起，玻璃層被操作的東西用 bounce 0.15 的 `--ease-glass`，接近 Apple 的 snappy：最多只超過目標 0.6%，看不出在彈，
  只是比不回彈的早一點到、收尾俐落，不是這一條要避免的彈跳曲線。Apple 的玻璃在互動時帶一點彈性，「減少動態效果」才把彈性關掉。
  2026-10-12 起全部的動態照 Apple 的彈簧參數（WWDC23「Animate with springs」）；2026-10-15 縮短後看起來完成約 0.22 到 0.3 秒，
  仍比上面一般轉場的建議長一點。使用者 2026-10-12 認為原本的動態突兀，研究後改照 Apple 的做法，Apple 的預設是 0.5 秒。
  會被連續操作打斷的動態由 `app.js` 帶著當下的速度接著走。
- 每天會按上百次的操作不加動畫；不做頁面載入的編排動畫。（interface-design；impeccable Operate 模式）
  JET 按下時立刻變深；按鈕鼓起只用 90ms，放開後彈回原本大小，不延後任何動作；表格捲動與儲存格點選不加動畫。
- 尊重 `prefers-reduced-motion`，並保留狀態回饋。（impeccable audit）
  JET 在減少動態時用 `transition: none` 關掉轉場，不用常見的「每個元素都設極短的 `transition-duration`」寫法；
  2026-10-12 在 4246 實測後者會讓減少動態下的重繪慢兩到四倍，經過記在 KCT 需求計畫（`docs/history/specs/2026-10-06-kct-gl-requirements-plan.md`）的「減少動態時重繪變慢」。

## 繁體中文排版

- 中文正文與表格不加 `letter-spacing`；加字距只用在極短的標題，而且 JET 目前不需要。（W3C clreq 6.3.1；W3C i18n）
- 不對中文用 `text-transform: uppercase`，也不用英文 eyebrow 的寬字距寫法。（frontend-design 第三版）
- 強調用字重 600，不用斜體；Windows 也把斜體排除在字級表外。（W3C clreq；Windows 字體排印）
- 行高：內文約 1.6，表格約 1.45，標題約 1.3，多行文字不低於 1.3。（impeccable `tight-leading`；JET 現行樣式）
- 中英文之間的間距：W3C 建議用 `text-autospace` 取代手打空格，Edge 140 起支援 `normal`，但預設不開；
  台灣常見的文案習慣是手打半形空格。JET 目前沿用既有文案，不全面開啟。要改先在 WebView2 實測，再請使用者決定。（W3C i18n；MDN 相容性資料）
- 全形標點寬度：台灣出版品多半不壓縮；Chromium 的 `text-spacing-trim` 預設會壓縮相鄰標點。要改設定先請使用者決定。（W3C clreq 6.3.2）
- 2026-10-11 起數字由 Instrument Sans 顯示，它預設是比例寬度，所以 `body` 預設 `font-variant-numeric: tabular-nums`，金額、筆數與日期上下對齊。
  打包的 Noto Sans TC 數字本來就等寬，但字型沒有 `tnum` 功能。（2026-10-08 本機字型檢查；2026-10-10 字體研究）
- 英文字母與數字用 Instrument Sans，以 `unicode-range` 排除中文常用的破折號、刪節號、引號與間隔號，這些標點仍由 Noto Sans TC 顯示。
