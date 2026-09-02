# JET 前端說明

更新日期：2026-09-01

JET 的前端是放在 WinForms WebView2 裡的 HTML、CSS 與 JavaScript。它負責讓使用者操作案件、查看摘要
與有界明細；審計規則、資料有效性與報告內容由後端決定。

## 畫面結構

進入案件後，主要畫面有四個區域：

1. 頂部案件摘要：顯示案件、provider、目前步驟與使用者身分。
2. 左側流程目錄：列出六個步驟與已完成狀態。
3. 中央內容：只展開目前步驟，其他步驟顯示一行摘要。
4. 右側狀態與資料檢視：顯示訊息、資料預覽與最近結果。

流程總覽使用 modal 呈現既有 state 的摘要。它不是第二套導航，也不重新計算每個步驟是否完成。

## 案件選擇

啟動後先顯示本機案件。SQLite 與 DuckDB 可以在沒有 SQL Server 的情況下使用。只有使用者要求同步
線上案件時，才查詢 SQL Server registry；線上失聯不能清空已顯示的本機清單。

建立案件時選定 provider。案件名稱錯誤由後端用結構化 `error.field = "caseName"` 指回欄位，前端不
解析錯誤句子猜測焦點。

載入或刪除失敗時，picker 在原操作位置保留錯誤，並提供「複製錯誤」與「輸出支援日誌」。後者使用
同一次錯誤回應的 correlation id，把去識別紀錄直接寫到該案件目錄；不需要先成功載入案件，也不依賴
Release 中不會顯示的 DEV 面板。報告 journal 衝突仍拒絕載入，但使用者確認後可以刪除整個案件。

## 六個步驟

### 1. 建立案件

收集案件名稱、代碼、公司顯示名稱、操作者與期間。建立成功後才進入工作流程。

### 2. 匯入資料

GL 與 TB 可以由一個或多個檔案匯入。前端先讓使用者選檔，再呼叫 inspect／preview／import actions。
授權編製人員、科目配對、假日與補班日是支援資料，不是每個案件都必填。

### 3. 欄位配對

前端顯示來源欄位、JET 目標欄位與值摘要，讓使用者確認 GL／TB mapping。自動建議只是草稿；只有
`mapping.commit.gl` 或 `mapping.commit.tb` 成功後，後端才建立標準資料。

### 4. 資料驗證與測試

畫面先顯示有效母體、來源品質與驗證摘要，再讓使用者以分頁查看差異或問題明細。預篩選可在此執行，
但它只是風險訊號，不是進階篩選的必要前置。

### 5. 進階條件篩選

使用者建立條件情境，先預覽，再保存。條件編輯器只產生後端接受的結構；不在 JavaScript 組 SQL 或
自行計算命中結果。完整命中資料由 `query.filterHitsPage` 分頁取得。

### 6. 匯出底稿

使用者選擇報告並啟動匯出。進度事件只顯示正在準備、寫入或發布哪一部分；只有 action 成功回應才
顯示完成。清理舊 artifact 先 preview，再 confirm。

## State 與資料權威

- `wwwroot/js/state.js` 保存目前畫面、案件 metadata、摘要與 request id，不保存完整 GL／TB。
- 下一步是否可用，以後端結果與 `ui-core.js` 的單一 gate 呈現為準。
- 切換步驟可以保存位置，但不能把「看過某頁」誤寫成「業務條件已完成」。
- 上游資料、mapping 或規則版本改變後，前端要顯示 stale，不沿用舊結果。
- Background response 必須確認仍屬同一案件與同一 request，不能覆寫後來載入的 state。
- 通知慣例（2026-09-01 收斂，整個前端唯一）：凡是會改變任何衍生畫面輸出的 store 寫入一律
  bump（`contentVersion` 進版、面板重建）；「只 notify」僅限「唯一視覺反映就是使用者正在編輯的
  那個控制項本身」的連續文字輸入，且必須在 blur 後延遲一次 bump 收斂。select／radio／checkbox
  是離散提交，永遠直接 bump。重建後的輸入焦點與捲動由 `app.js` 的 `renderContent` 依焦點識別
  屬性與 `data-preserve-scroll` 標記統一還原，步驟模組不得各自發明保留機制。已文件化的唯一
  例外是 filter-step 的規則值編輯（連續輸入且重建成本高，以就地抽換 read-back 與預覽面板收斂）。
  守衛測試：`JET.Tests.Architecture.StoreNotificationConventionFrontendTests`。

欄位配對的必填鐵軌與「確認配對」按鈕都由同一份 draft 推導。下拉選擇會立即 bump；中央內容重建後，
只依每個控制項明示且唯一的 `data-focus-key` 還原焦點，並還原 `data-preserve-scroll` 容器。這避免右側
仍顯示「待指派」、按鈕維持停用，或重繪把焦點送回錯誤控制項。

## 診斷日誌

- 「輸出支援日誌」是 Release 可用的去識別紀錄，案件尚未成功載入時也能從 picker 使用。
- Debug 的「DEV — 診斷日誌匯出」保留較完整的原始紀錄，必須先開啟案件；畫面明示內容可能含案件資料。
- 兩種輸出都由後端直接寫入目前案件目錄。前端不接受任意路徑，也不自行組合或過濾 NDJSON。

## 作業、取消與關閉

長作業一次只允許一項會改動案件資料的操作。前端顯示 busy 狀態與取消入口；`operation.cancel` 只表示
已提出取消，最終結果仍看原 request。

離開案件或關閉視窗時，要先停止新 request、處理在途作業並釋放案件鎖。畫面不能因為 Bridge 尚未
回應，就自行假設鎖已經釋放。

## 實作位置

| 內容 | 位置 |
|:---|:---|
| 應用程式外殼與流程總覽 | `wwwroot/index.html`, `wwwroot/js/app.js` |
| State 與六步名稱 | `wwwroot/js/state.js` |
| 共用 UI 與進入條件 | `wwwroot/js/ui-core.js` |
| 各步驟畫面 | `wwwroot/js/steps/` |
| Action transport | `wwwroot/js/jet-api.js` |
| 樣式 | `wwwroot/css/` |

完整舊版畫面規格保存在
[`history/superseded/jet-frontend-description-2026-08.md`](history/superseded/jet-frontend-description-2026-08.md)。
那份文件適合追查舊決策，不是新增 UI 時要逐段照抄的模板。
