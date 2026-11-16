---
title: 設計原則
english: Design Principle
---
## 1. UX

- 開啟案件、匯入、欄位配對、資料驗證、預篩選、篩選與匯出執行時，顯示處理中的提示與「取消作業」按鈕。總帳（GL）與試算表（TB）的匯入、欄位配對與匯出另回報進度。
- 除選取檔案、開啟資料夾、結束程式與取消外，前端送出的請求（action）均於背景執行緒執行，不阻塞 UI。
- 變更案件資料、開啟案件與匯出的作業同一時間僅執行一項，「訊息」區紀錄的寫入（log.append）除外；期間收到的同類請求立即以錯誤碼 operation_in_progress 回覆。
- 錯誤訊息使用繁體中文並說明原因；使用者可自行排除時另寫處理方式。

## 2. UI

顏色與字級定義於 JET/wwwroot/css/app.css 的 :root 變數；其他樣式規則不另寫十六進位色碼或 px 字級。圖表的顏色與字級另寫於 JET/wwwroot/js/overview-bi.js，值須與變數相同。

| 項目 | 規則 |
|---|---|
| 字型 | 英文與數字用 Instrument Sans，中文用 Noto Sans TC。 |
| 字級 | 12、13、14、18、22、28 px 六階。內文、輸入框與下拉選單 14 px，按鈕 13 px，緊湊控制項 12 或 13 px。 |
| 顏色 | 頁面底色 #f5f3ed，重點色 #1f6c9f，錯誤色 #a3302b，通過色 #3b7a47，警告色 #855700。 |
| 對比 | 一般文字至少 4.5:1，停用文字至少 3:1。 |
| 控制項 | 按鈕、輸入框與下拉選單高度 32 px，表格、工具列與條件列內 28 px。 |
| 金額 | 顯示至小數第 2 位，並加千分位符號；資料預覽顯示至小數第 4 位。 |
| 必填欄位 | 建立案件的表單以錯誤色的星號標示；欄位配對以標籤標示，未選取時為警告色。 |

index.html 與 app.css 不得引用外部網址的資源。字型、圖示與 ECharts 連同授權檔置於 JET/wwwroot 的 assets 與 vendor。

## 3. 專案與模組

應用程式專案編譯為單一 assembly。JET 資料夾下的 Domain、AuditCore、Application、Bridge 與 Infrastructure 各為一個模組，命名空間為 JET 加上資料夾名稱；子資料夾不另增命名空間。各模組的職責見 Technical Document.docx 第 6 節。

| 模組 | 不得相依的對象 |
|---|---|
| Domain | Application、AuditCore、Infrastructure、Bridge |
| AuditCore | Application、Infrastructure、Bridge、Microsoft.Web.WebView2 |
| Application | Infrastructure |
| Bridge | Infrastructure |
| Infrastructure | Application（DemoWorkbookWriter 除外） |

- 上表由 tests/JET.Tests/Architecture 的 LayerDependencyTests 檢查。
- AuditCore 僅公開 10 個型別，對外入口為 JetAuditProgram。
- dev.* 與 demo.* 的請求、project.loadDemo，以及 WebView2 的開發人員工具，僅於 Debug 組態啟用。

## 4. 函式與命名

| 對象 | 規則 | 範例 |
|---|---|---|
| 後端型別與方法 | PascalCase；介面以 I 開頭 | IApplicationActionHandler、HandleAsync |
| action 名稱 | camelCase，以句點分段，第一段為功能領域 | validate.run、mapping.commit.gl |
| Handler | 每種請求一個類別；類別名稱為請求的各段首字改為大寫後相連，再加上 Handler | ValidateRunHandler |
| 錯誤碼 | 小寫英文與底線，集中定義於 JetErrorCodes | invalid_payload |
| 前端檔案 | 小寫與連字號；各步驟的主要程式置於 js/steps | validate-step.js |
| CSS 類別 | 區塊__元素；變體與狀態以 -- 修飾，另有 is- 與 has- 開頭的狀態類別 | toc-item--current、is-missing、busy-overlay__label |

- 新增請求時，須實作 IApplicationActionHandler、於 AppCompositionRoot 註冊、於 ActionExecutionPolicy 歸類（是否須單獨執行），並列入 jet-api.js 的 SUPPORTED_ACTIONS。前後端清單不一致或未歸類時，測試失敗。
- 前端不計算金額，也不執行查核邏輯。
- 註解說明程式的行為與原因。XML 文件註解的格式錯誤與無效參照視為建置錯誤。

## 5. 資料與效能

- 金額以 decimal 解析，乘以 10,000 後四捨五入為 64 位元整數；篩選與彙總以該整數計算。
- 篩選、彙總與比對於 SQLite 以 SQL 執行。前端狀態不得保存 GL 或 TB 的完整資料列。
- 明細查詢採游標分頁，資料預覽以 limit 與 offset 分頁；總筆數僅於第一頁計算。
- GL 與 TB 的匯入以串流逐列讀取來源檔，經 IBulkRowWriter 寫入；匯出的資料列以串流寫入工作表。
- 匯入、欄位配對、資料驗證與篩選的寫入於交易內執行。取消時中斷執行中的 SQL statement 並 rollback 交易。
- 匯入資料、欄位配對，或行事曆、科目配對等設定變更後，資料驗證、預篩選與篩選結果何者過期，僅由 AuditDependencyPolicy 決定。

## 6. 錯誤與安全

- 可預期的錯誤以 JetActionException 回報，可附欄位名稱與逐條明細。
- 作業取消時回覆錯誤碼 operation_cancelled；可辨識的資料庫錯誤轉為 database_ 開頭的錯誤碼。其餘例外回覆錯誤碼 bridge_error 與固定的錯誤訊息，例外內容不傳至前端。
- 支援日誌僅記錄 allowlist 內的事件，不含 SQL、參數值、檔名、路徑、例外訊息與案件名稱。
- 輸入的字串值以參數傳入 SQL，不得組入 SQL 文字；欄位名稱與排序鍵僅能取自程式內的 allowlist。
- 報告與工作底稿輸出至案件資料夾，檔名不得含路徑；先寫入同資料夾的暫存檔，完成後更名。
- 工作底稿的檔名附匯出時間（yyyyMMdd-HHmmss），同名時加流水號；其餘報告覆寫同名檔。

## 7. 測試與複核

- 驗收用的自動化測試使用合成測試資料，執行範圍見 Technical Document.docx 第 14 節。預期結果依需求與查核邏輯訂定。
- 驗收依據、人工複核的範圍與複核流程見 Technical Document.docx 第 13 節。

## 8. 版本與修訂紀錄

- 初版的版本號為 1.0.0.0。修正錯誤時增加第三碼，例如 1.0.1.0；新增相容的功能時增加第二碼，例如 1.1.0.0；不相容的大改版增加第一碼，例如 2.0.0.0。最後一碼固定為 0。
- 支援日誌記錄 assembly 版本、建置組態與編譯時寫入 assembly 的識別碼（module_version_id）。
- commit message 的規則、程式修訂紀錄與版本更新紀錄見 Technical Document.docx 第 8、17、18 節。
- 案件資料庫與案件設定檔的 schema 變更時遞增 schema 版本；版本不符時的處理見 Technical Document.docx 第 10 節。

## 9. 部署與維護

- 發布設定以 JET/Properties/PublishProfiles/FolderProfile.pubxml 為準，發布內容見 Technical Document.docx 第 15 節。
- NuGet 套件於 JET/JET.csproj 指定固定版本。
- 新增 Excel 範本時，須列入 JET/JET.csproj 的 Content 項目。
