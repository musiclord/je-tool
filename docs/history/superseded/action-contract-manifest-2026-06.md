# JET Frontend Action Contract Manifest

本文件是 JET 前端、WebView2 bridge、C# handler 之間的**唯一 action contract source of truth**。任何 agent 在生成 HTML / UX 或修改 action 之前，應先讀本文件。

## 初步架構階段狀態

專案持久化（project persistence）、檔案匯入（file import）與欄位配對（mapping）子集已進入**正式契約階段**，由下方「Project Persistence / Host / Dev Actions」與既有 Project / Import / Mapping 章節定義。

`__jet.*` 內部 provisional action（`__jet.probe`、`__jet.projectConfig`）與其 %LOCALAPPDATA% prototype blob 資料庫已**退役移除**。它們原是架構驗證期的 scaffolding，會讓組態持久化多出一條與專案儲存無關的旁路。退役後，前端啟動的 round-trip 檢查改走正式的 `system.ping`；專案組態一律持久化於 `projects/{projectId}/`（`project.json` 加上 `jet.db` 的 `config_*` 表），不存在其他組態儲存點。

validation（`validate.run`）、prescreen（`prescreen.run`）與 filter（`filter.preview` / `filter.commit`）已進入**正式契約階段**（見各自章節）。明細分頁 `query.*Page` 與 export 等其餘 action 仍屬規劃中契約，實作前需回到本 manifest 確認。

## 使用原則

1. 前端**優先重用既有 action**，不要自行發明新 action。
2. 如果 UI 真的需要新資料或新行為，先更新本 manifest，再修改 `ActionDispatcher` 與相關 handler。
3. `docs/jet-template.html` 的 UI 生成應服從這裡的 action name、payload shape、response shape 與 fixed binding assumptions。
4. `Bridge` 只負責 transport，不負責業務判斷。
5. `docs/jet-guide.md` 定義業務語意；本文件定義跨 frontend / bridge / handler 的資料契約。兩者若衝突，先回報並修文件，不要在 UI 或 C# 裡發明新 action。

## Bridge Envelope

前端送到 WebView2 bridge 的標準請求：

```json
{
  "requestId": "<uuid>",
  "action": "<namespace.action>",
  "payload": {}
}
```

Bridge 回傳的標準回應：

```json
{
  "requestId": "<uuid>",
  "ok": true,
  "data": {},
  "error": null
}
```

失敗時：

```json
{
  "requestId": "<uuid>",
  "ok": false,
  "data": null,
  "error": {
    "code": "<error_code>",
    "message": "<human_readable_message>"
  }
}
```

## Host→Web 事件（Event Envelope）

除了 request/response，host 可主動推播事件給前端（單向通知，不需要回覆）。事件信封**沒有 `requestId`**，以 `event` 欄位與 response 信封區隔；不認識此形狀的舊前端會安全忽略（`receive()` 只處理帶 `requestId` 的訊息）：

```json
{
  "event": "<namespace.event>",
  "data": {}
}
```

- 事件由 Application handler 經 `IJetEventPublisher` port 發出；WebView2 marshal 與 JSON 序列化由 Bridge 的 `WebViewEventPublisher` 負責（UI 執行緒派送，依發出順序送達）。WebView 尚未就緒時事件靜默丟棄——事件是 UX 提示，**不承載狀態權威**，權威一律以 action response 為準。
- 前端在 `jet-api.js` 以 `JetApi.on(eventName, handler)` / `JetApi.off(eventName, handler)` 訂閱；傳輸細節仍只存在於 `jet-api.js`。
- 事件不得攜帶資料列（與 §1.5.4 bridge 約束相同）。

| Event | Data | 用途 |
|:---|:---|:---|
| `import.progress` | `{ kind: "gl"\|"tb", fileName, sheetName\|null, rowsRead }` | **Implemented**。`import.gl.fromFile` / `import.tb.fromFile` 串流寫入期間，每讀滿 20,000 列發送一次（`rowsRead` = 該來源累計已讀列數）。**沒有完成事件**：匯入完成以該 action 的 response 為準。前端可用 `rowsRead` ÷ `import.inspectFile` 的 `rowCountEstimate` 顯示近似進度；估計值缺席（CSV）時顯示已讀列數即可 |

## Current Action Registry

### Shell / Bootstrap

| Action | Payload | Response | 用途 |
|:---|:---|:---|:---|
| `system.ping` | `{}` | `{ message, utcNow, devToolsEnabled }` | **Implemented**。基本 host 通訊檢查；前端啟動 round-trip 以此為準（取代已退役的 `__jet.probe`）。`devToolsEnabled` 標示本組建是否註冊開發者工具（Debug 組建 true、Release 組建 false）；前端據此決定是否顯示開發面板 |
| `app.bootstrap` | `{}` | `AppBootstrapDto` | Planned。啟動 shell、顯示 DB provider 與 supported actions |
| `project.loadDemo` | `{}` | `DemoProjectDto`（見下方；metadata + mapping，不含 rows） | **Implemented**。載入 deterministic 測試案件 metadata（MockDataLoader 資料來源） |
| `demo.exportGlFile` | `{}` | `{ filePath, fileName }` | **Implemented**。將 deterministic demo GL（2,000 列、2025 年度、金額＋借方旗標模式）寫成 xlsx；前端接 `import.gl.fromFile` |
| `demo.exportTbFile` | `{}` | `{ filePath, fileName }` | **Implemented**。將 deterministic demo TB（100 科目，借貸合計由 demo GL 推導）寫成 xlsx；前端接 `import.tb.fromFile` |
| `demo.exportAccountMappingFile` | `{}` | `{ filePath, fileName }` | **Implemented**。將 deterministic demo 科目配對表（demo GL 科目 → 標準化分類，含 Revenue 與 Receivables/Cash 類）寫成 xlsx；前端接 `import.accountMapping.fromFile` |
| `demo.exportAuthorizedPreparerFile` | `{}` | `{ filePath, fileName }` | **Implemented**。將 demo 授權編製人員清單寫成單欄 .xlsx，供測試案件走 `import.authorizedPreparer.fromFile` 匯入。鏡射 `demo.exportAccountMappingFile` |
| `demo.fetchGlRows` | `{}` | `{ fileName, rows, columns }` | **Legacy compatibility only**；正式 demo/test path 不得使用，改用 `demo.exportGlFile` → `import.gl.fromFile` |
| `demo.fetchTbRows` | `{}` | `{ fileName, rows, columns }` | **Legacy compatibility only**；正式 demo/test path 不得使用，改用 `demo.exportTbFile` → `import.tb.fromFile` |
| `demo.fetchAccountMappingRows` | `{}` | `{ fileName, rows }` | **Legacy compatibility only**；正式 demo/test path 不得使用，改用 `demo.exportAccountMappingFile` → `import.accountMapping.fromFile` |

`DemoProjectDto` 結構（deterministic：同版本程式每次回傳相同內容）：

```json
{
  "project": { "caseName": "範例測試案件", "projectCode": "DEMO-2025-001", "entityName": "...", "operatorId": "...",
               "industry": "...", "periodStart": "2025-01-01", "periodEnd": "2025-12-31",
               "lastPeriodStart": "2025-12-31" },
  "gl": { "fileName": "JE-demo-2025.xlsx", "rowCount": 2000,
          "amountMode": "flag", "mapping": { "docNum": "傳票號碼", "...": "..." } },
  "tb": { "fileName": "TB-demo-2025.xlsx", "rowCount": 100,
          "changeMode": "debitCredit", "mapping": { "accNum": "科目代號", "...": "..." } },
  "holidays": ["2025-01-01", "..."],
  "makeupDays": ["2025-02-08"],
  "demoScenario": { "name": "...", "rationale": "...", "groups": ["（filter 條件 AST，見 Filter / Criteria 章節 schema）"] }
}
```

測試案件為 dev fixture（含 2025 台灣國定假日近似清單），資料埋有未來規則測試所需的特徵（關鍵字摘要、週末/假日分錄、整數金額、多位建立人員、期末後核准日）。不過傳票逐張平衡、TB 借貸合計也由 GL 推導，以確保完整性測試與借貸不平測試都能通過。`demoScenario` 為 MockDataLoader 在進階條件篩選步驟套用的示範條件 AST（deterministic，由後端 `DemoDataFactory` 提供——前端不硬編業務資料），可直接送 `filter.preview` / `filter.commit`。

`AppBootstrapDto` 結構：

```json
{
  "applicationName": "JET",
  "startPage": "https://appassets.example/index.html",
  "supportedActions": ["app.bootstrap", "..."],
  "database": {
    "provider": "Sqlite",
    "isAvailable": true,
    "connectionTarget": "...",
    "mode": "Local"
  },
  "demo": {
    "enabled": true
  }
}
```

### Project Persistence / Host / Dev Actions

本節為已實作的正式契約。專案以**每專案一個資料夾**持久化：`{AppBaseDir}/projects/{projectId}/` 內含 `project.json`（metadata）與 `jet.db`（SQLite）。因為每專案一個 DB 檔，SQLite local provider 的資料表**不帶 `project_id` 欄**（檔案即 scope）；repository 介面仍以 `projectId` 參數解析 DB 路徑。

**資料庫 provider 歸屬**：`project.json` 以 `databaseProvider` 標示該專案的會計資料放在哪個引擎。目前固定 `"sqlite"`（本地）；未來的雲端專案會是 `"sqlServer"`，屆時的連線設定要先以新契約欄位修本 manifest 再實作。舊版 `project.json` 若缺這個欄位，讀取時一律正規化為 `"sqlite"`，不需遷移。所有專案組態（field mapping、filter scenario、calendar、rule run 摘要）都持久化在該專案的資料庫 `config_*` / `result_*` 表與 `project.json`，跨 session 可復用；**不存在任何僅在記憶體或全域旁路的組態儲存**。

**儲存 JSON 可讀性與 SQL Server 可攜性**：`row_json` / `mapping_json` / `columns_json` 與 `project.json` 一律以未跳脫的 UTF-8 JSON 儲存（`JavaScriptEncoder.UnsafeRelaxedJsonEscaping`），中文原文可直接人工檢視。這套形狀對 SQL Server 直接可攜：TEXT 對應 `NVARCHAR(MAX)`（可用 `OPENJSON` / `JSON_VALUE` 查詢），scaled INTEGER 對應 `BIGINT`，日期字串 "yyyy-MM-dd" 也不依賴 provider 的日期函式。投影邏輯（`GlRowProjector` / `TbRowProjector`）是 provider 無關的 C# 純函式，所以 SQL Server provider 只需新增 `SqlServer*` repository 實作（bulk insert 換成 `SqlBulkCopy`），Application 層不變（guide §13）。

| Action | Payload | Response | 用途 |
|:---|:---|:---|:---|
| `project.create` | `{ caseName?, projectCode, entityName, operatorId, industry?, periodStart, periodEnd, lastPeriodStart?, databaseProvider? }` | `{ projectId, ok }` | **Implemented**。建立查核案件並初始化 `{projects}/{projectId}/`（`project.json` + `jet.db`／SQL Server 庫）。`caseName`（選填，案件名稱）提供時作為 `projectId` 與資料夾名，經 `ProjectNameRules` 驗證（允許 Unicode 文字/數字/空白/`_`/`-`/括號；拒絕路徑分隔與保留字元 `/ \ : * ? < > |`、句點、前後空白、Windows 保留名、長度>100）；同名或（SQL Server）淨化後同庫名已存在 → `invalid_payload`。未提供則回退 32-hex GUID（既有程式化/測試建立）；**UI 表單強制必填**。`databaseProvider` 建立時選定（`sqlite`/`sqlServer`），之後不可改 |
| `project.list` | `{}` | `{ projects: [{ projectId, projectCode, entityName, periodStart, periodEnd, createdUtc, currentStep, databaseProvider, lastOpenedUtc\|null }] }` | 列出所有既有專案（依 `lastOpenedUtc ?? createdUtc` 新→舊，最近開啟者浮上）；損壞的 project.json 會被略過。`databaseProvider` 同 provider 歸屬說明；`lastOpenedUtc` = 最近一次 `project.load` 戳記的 UTC 時間（從未開啟過為 null，前端 fallback 顯示 createdUtc） |
| `project.saveProgress` | `{ currentStep }` | `{ ok, currentStep }` | **Implemented**。保存使用者目前所在的流程步驟（0–5 的 6 步索引，對齊 Step Data Outline）到 project.json 的 `currentStep`，供 `project.load` resume「上次操作的位置」。與匯入/配對的自動推進（只前進不後退）不同，本 action 記錄使用者實際所在位置，**允許倒退**。前端於步驟導航時與結束應用程式前呼叫。無 active project → `no_active_project`；`currentStep` 缺漏或超出 0–5 → `invalid_payload` |
| `log.append` | `{ level?, text }` | `{ ok: true }` | **Implemented**。把一則前端「狀態與訊息」持久化到當前專案資料庫的 `app_message_log` 表，每專案保留最近 500 則、舊的自動修剪。`level` 限 `"info"`／`"warn"`：缺省為 info、不分大小寫、落地為小寫，非白名單值 → `invalid_payload`。`text` 必填，trim 後上限 4000 字元、超長截斷。需要 active project（`no_active_project`）——專案選擇畫面尚無 active project，該畫面的訊息只留在前端記憶體、不持久化。這些訊息是 UX 輔助紀錄，不是審計留痕；審計留痕在 `result_*` 表與工作底稿 |
| `log.recent` | `{ limit? }` | `{ messages: [{ occurredUtc, level, text }] }` | **Implemented**。取當前專案最近持久化訊息（新→舊）。`limit` 預設 30、上限 100（超出夾擠）。供 `project.load` 後還原「狀態與訊息」面板的歷史訊息。需要 active project |
| `host.selectFile` | `{ title?, extensions? }` | `{ filePath, fileName }`（使用者取消時兩者皆 `null`，`ok` 仍為 true） | 開啟原生 OpenFileDialog；`extensions` 為副檔名陣列（如 `[".xlsx"]`），host 端組成 Windows filter。host capability 走同一條 action 通道（guide §12 Host） |
| `host.selectFiles` | `{ title?, extensions? }` | `{ files: [{ filePath, fileName }] }`（使用者取消時 `files` 為空陣列，`ok` 仍為 true） | **Implemented**。多選版本的檔案對話框（`OpenFileDialog.Multiselect`），供匯入精靈一次選取多個來源檔。獨立 action 而非 `host.selectFile` 的旗標——避免同一 action 兩種 response 形狀 |
| `host.exitApp` | `{}` | `{ ok: true }` | **Implemented**。請求 host 關閉應用程式視窗（WinForms `Close`，以 `BeginInvoke` 排入訊息佇列）。前端標準結束流程：先 `project.saveProgress` 保存進度，再呼叫本 action；視窗可能在 response 抵達前關閉，前端不得依賴本 action 的 response。純 host capability，不含業務邏輯（guide §12 Host） |
| `host.selectSavePath` | `{ defaultFileName? }` | `{ path }`（使用者取消時 `path` 為 `null`，`ok` 仍為 true） | 開啟原生 SaveFileDialog（filter 固定 `Excel 活頁簿 (*.xlsx)`）取得匯出底稿的存檔路徑；`defaultFileName` 為客戶／公司名片段（前端傳入，省略時 host 以 `WorkingPaper` 為基底），host 端據此組成預填檔名 `{base}_{yyyymmddHHmmss}_WorkingPaper.xlsx`——**時間戳在 host 端產生**（Form1 `DateTime`，非 Domain）。取消回 `path: null`。純 host I/O，不含業務邏輯（guide §12 Host）。供「匯出到其他位置」備用——前端預設匯出已改為**直接落專案目錄**（`export.workpaperStream` 省略 `outputPath`），故此 action 目前非預設流程必經。**Implemented**（E1 Task 6） |
| `host.openFolder` | `{ path }` | `{ ok }` | **Implemented**。在檔案總管揭示一個路徑（開啟所在目錄並選取該檔，`explorer.exe /select`），供匯出底稿後「打開目錄」按鈕用（`path` 為剛匯出的底稿路徑）。`path` 缺/空 → `invalid_payload`。純 host I/O，不含業務邏輯（guide §12 Host） |
| `query.dataPreview` | `{ dataset, limit? }` | `{ dataset, columns, rows, totalCount, stats }` | **Implemented**。**正式版**的使用者資料預覽（細節見下方）；與 dev.db.* 的差異：只開放業務資料集白名單，不暴露任何實體資料表名 |
| `query.completenessDiffPage` | `{ cursor?, pageSize? }` | `{ rows, nextCursor }` | **Implemented**：完整性全科目差異(diff≠0)keyset 分頁,排序鍵 account_code ASC;cursor opaque、pageSize 預設 200/上限 500;rows 每列 `{accountCode,accountName,tbAmount,glAmount,diff,notInTb}` |
| `query.docBalancePage` | `{ cursor?, pageSize? }` | `{ rows, nextCursor }` | **Implemented**：借貸不平傳票(SUM(amount_scaled)≠0)keyset 分頁,排序鍵 document_number ASC;cursor opaque、pageSize 預設 200/上限 500;rows 每列 `{documentNumber,debit,credit,diff}` |
| `query.nullRecordsPage` | `{ category, cursor?, pageSize? }` | `{ rows, nextCursor }` | **Implemented**：空值/期外日期紀錄 keyset 分頁,排序鍵 entry_id ASC;`category` **必填**,白名單四值 `nullAccount`/`nullDocument`/`nullDescription`/`outOfRangeDate`(非法值 `invalid_payload`);`outOfRangeDate` 以**核准日(approval_date／配對 docDate)** 對專案 PeriodStart/End 判定(2026-06-23 決策,對齊舊工具的「Approval date out of period」;非過帳日);cursor opaque、pageSize 預設 200/上限 500;rows 每列 `{documentNumber,accountCode,postDate,description}` |
| `query.filterHitsPage` | `{ scenarioPosition, cursor?, pageSize? }` | `{ rows, nextCursor }` | **Implemented**：已存篩選情境(result_filter_run)的命中行層明細 keyset 分頁,排序鍵 entry_id ASC;`scenarioPosition` **必填**(整數;缺則 `invalid_payload`);惰性補算——該 position 在 result_filter_run 無列但 config_filter_scenario 有定義時,先重用 filter.commit 同源 materializer 落地命中再查;cursor opaque、pageSize 預設 200/上限 500;rows 每列 `{documentNumber,lineItem,postDate,accountCode,accountName,amount,drCr,description}` |
| `query.infSamplePage` | `{ cursor?, pageSize? }` | `{ rows, nextCursor }` | **Implemented**：INF 抽樣(result_inf_sampling_test_sample,最近一次 validate.run)行層明細 keyset 分頁,排序鍵 entry_id ASC;借/貸由 debit_amount_scaled/credit_amount_scaled 拆欄並換算顯示值;cursor opaque、pageSize 預設 200/上限 500;rows 每列 `{documentNumber,accountCode,accountName,debit,credit,postDate,approvalDate,createdBy,approvedBy,description}` |
| `query.tagMatrixScenarios` | `{}` | `{ scenarios: [{ position, name, voucherHitCount, rowHitCount }] }` | **Implemented (D2)**：多情境 tag 矩陣的情境摘要;由 `result_filter_run` 即時算出每個已存情境(位置 1..N)的傳票層命中數(`COUNT(DISTINCT document_number)`)與行層命中數(`COUNT(*)`)。`name` 取自 config_filter_scenario,依 position 升冪;無命中的情境列出 count=0。惰性補算同 filterHitsPage(全空且有情境 → 落地後重取)。需要 active project |
| `query.tagMatrixVoucherPage` | `{ cursor?, pageSize? }` | `{ rows, nextCursor }` | **Implemented (D2)**：tag 矩陣的傳票層 keyset 分頁,排序鍵 document_number ASC(排除 NULL 傳票號);由 `result_filter_run` 即時 pivot,採每頁兩段查詢(命中傳票 keyset 頁 + 同鍵範圍命中位置)。rows 每列 `{documentNumber,postDate,createdBy,voucherTotal,matchedPositions}`,`voucherTotal` 為該傳票借方總額顯示值(`SUM(debit_amount_scaled)` 換算),`matchedPositions` 為命中的情境位置陣列(1..N,有序去重);cursor opaque、pageSize 預設 200/上限 500;惰性補算同 filterHitsPage |
| `query.tagMatrixRowPage` | `{ cursor?, pageSize? }` | `{ rows, nextCursor }` | **Implemented (D2)**：tag 矩陣的行層 keyset 分頁(命中傳票之所有行,含該傳票內未命中任何情境的行),排序鍵 entry_id ASC(排除 NULL 傳票號);由 `result_filter_run` 即時 pivot,採每頁兩段查詢(命中傳票之所有行 keyset 頁 + 同鍵範圍各行命中位置)。rows 每列 `{documentNumber,lineItem,postDate,approvalDate,createdBy,approvedBy,accountCode,accountName,amount,matchedPositions,description}`,`amount` 為該行 signed 金額顯示值(`amount_scaled` 換算),`matchedPositions` 為該行命中的情境位置陣列(有序去重;**非命中行為空 `[]`**);cursor opaque、pageSize 預設 200/上限 500;惰性補算同 filterHitsPage |

> `query.tagMatrix*` 三 action 為子專案 D2 的條目(形狀已鎖)。`tagMatrixScenarios`/`tagMatrixVoucherPage`/`tagMatrixRowPage` 均已實作。矩陣不落地新表——由 `result_filter_run`(D1 命中落地)即時算出方法學 step4(傳票層 C1..CN 布林)與 step4-1(行層逐行 tag);`matchedPositions` 即命中情境位置集合(對映 C1..CN);cursor opaque、壞 cursor → `invalid_payload`(同下方 cursor 契約);pageSize 預設 200/上限 500。

上列各 `query.*Page` 的 **cursor 契約**:`cursor` 省略／null／空字串 = 首頁(無游標述詞);**有傳 cursor 但無法解碼(非 opaque 格式)→ `invalid_payload`**(handler fail loud,不靜默重置為首頁——對齊「游標格式不符讓 handler 報參數錯,不靜默」)。

| `dev.db.overview` | `{}` | `{ databasePath, databaseProvider, fileSizeBytes, sqliteVersion, tables: [{ name, rowCount }] }` | **Dev-only 診斷**：當前專案資料庫總覽。**僅 Debug 組建註冊**——Release 組建不註冊此 action（呼叫會得到 `bridge_error` unknown action），前端也依 `system.ping.devToolsEnabled` 隱藏開發面板。走**獨立唯讀路徑**：連線指定 `Mode=ReadOnly`、不共用快取、不開連線池，直接讀磁碟上的 DB 檔。因此檢視**零副作用**——不建 schema、不寫入，看到的必然是已持久化的資料，而非記憶體狀態。DB 檔不存在 → `file_not_found`（不會建立）。這不是審計 workflow 的一部分，UI 置於折疊的開發面板 |
| `dev.db.tableData` | `{ tableName, limit?, offset? }` | `{ tableName, columns, rows, totalCount, limit, offset }` | **Dev-only 診斷**：分頁讀資料表，同上唯讀語意與**僅 Debug 組建註冊**。`limit` 預設 50（上限 200）；`tableName` 以 sqlite_master 白名單精確比對，否則 `table_not_allowed`。`rows` 為字串化 cell 陣列，SQL NULL → JSON null。dev 工具允許 OFFSET 分頁（正式 GL 分頁仍須 keyset） |
| `dev.log.export` | `{}` | `{ ndjson }` | **Dev-only 診斷**：把診斷日誌（第三層、跨專案）的 ring buffer 完整匯出為 NDJSON，每行是一筆完整 JSON 物件:`timestamp`/`level`/`category`/`eventName`/`message`/`correlationId`/`transactionId`/`projectId`/`fields`/`exception`。記錄的內容包含 action 生命週期、SQL（完整命令加上參數 name=value、`rows_affected`、`provider`）、transaction（begin/commit/rollback 共享同一個 `transaction_id`）、exception（含 inner）與大檔 milestone。這層獨立於 result_*（審計）與 `IMessageLogStore`（UX 訊息）兩層。**僅 Debug 組建註冊**——Release 不註冊此 action（`bridge_error` unknown action），也不註冊 `RingBufferLoggerProvider`（log 變 no-op），前端則依 `system.ping.devToolsEnabled` 隱藏「DEV — 診斷日誌匯出」面板。不需 active project（跨專案）。前端以唯讀 textarea 呈現可複製的 NDJSON |

`query.dataPreview` 細節（正式版的使用者資料預覽）：

- **用途**：讓使用者直觀看到「目前操作的資料長什麼樣子」——欄位配對時對照欄名與實際內容、進階篩選前掌握數值／日期／摘要的大概樣貌。**有界預覽，不是分頁瀏覽**：明細分頁屬 `query.*Page` 里程碑（keyset），本 action 絕不回完整母體。
- `dataset` 白名單（業務資料集，不是實體資料表名）：
  - `"glStaging"` / `"tbStaging"`：匯入後的**來源原貌**（columns = 正規化後的來源欄名，與欄位配對下拉選單一字不差；cell 為原始字串）。
  - `"glEntries"`：投影後的 GL 分錄（測試母體）。columns 固定為 `documentNumber, lineItem, postDate, accountCode, accountName, documentDescription, amount, drCr`（與 `filter.preview` 的 previewRows 同欄位集）；`amount` 為帶號顯示值。
  - `"tbBalances"`：投影後的 TB 餘額。columns 固定為 `accountCode, accountName, changeAmount`。
  - `"accountMappings"`：已匯入的科目配對表。columns 固定為 `accountCode, accountName, standardizedCategory`。
  - `"authorizedPreparers"`：已匯入的授權編製人員清單。columns 固定為 `preparerName`。
  - 非白名單值 → `invalid_payload`。
- `limit`：預設 50、上限 100（超出夾擠）；rows 依匯入／投影順序取前 N 列，cell 一律字串、SQL NULL → JSON null。
- `totalCount` = 該資料集目前總列數；資料集尚無資料（未匯入／未投影）→ `{ columns: [], rows: [], totalCount: 0, stats: null }`，**不是錯誤**（前端顯示空狀態）。
- `stats` 只在 `glEntries` 提供（進階篩選的把關資訊）：`{ amountAbsMin, amountAbsMax, postDateMin, postDateMax, voucherCount }`——金額為 `ABS(amount_scaled)` 的顯示值（篩選的數值區間比較的就是絕對值）、日期為 ISO 字串、`voucherCount` = distinct 傳票數。其餘資料集 `stats` 為 null。
- 需要 active project（`no_active_project`）。權威計算仍在 SQL（COUNT/MIN/MAX set-based）；本 action 只是唯讀預覽，與規則執行無關。

### Project / Import

| Action | Payload | Response | 用途 |
|:---|:---|:---|:---|
| `project.create` | `{ projectCode, entityName, operatorId, industry?, periodStart, periodEnd, lastPeriodStart?, databaseProvider? }` | `{ projectId, ok }` | 建立專案：建立 `projects/{projectId}/` 資料夾、寫入 `project.json`、初始化專案資料庫 schema、設定 current session。日期格式 `yyyy-MM-dd`；`lastPeriodStart` 存為 `lastAccountingPeriodDate`；`moneyScale` 預設 10000、`roundingMode` 預設 `AwayFromZero`。`databaseProvider` 選定資料引擎，**只在建立時可選、之後不可改**（guide §13）：省略或 `"sqlite"` → 每專案一個本機 `jet.db`；`"sqlServer"` → 在共用 instance 上每專案一個 `JET_{projectId}` 資料庫（連線取自環境變數 `JET_SQLSERVER_CONNECTION`，相容 Express／Standard／Enterprise）。其他值 → `invalid_payload`。選定結果記錄於 project.json |
| `project.load` | `{ projectId }` | `{ project, mapping: { gl\|null, tb\|null }, importState: { gl\|null, tb\|null, accountMapping\|null, authorizedPreparer\|null, calendar }, latestRuns: { validate\|null, prescreen\|null }, filterScenarios: [] }` | 載入既有專案並設定 session；回傳完整 resume 狀態供重啟後接續。`mapping.gl = { mapping, amountMode, sourceBatchId, committedUtc }`；`importState.gl = { batchId, rowCount, columns, fileName, importedUtc, sources }`（`sources` 形狀同 `import.*.fromFile` response；`fileName` = 第一個來源檔名，向後相容）；`importState.accountMapping = { batchId, rowCount, fileName, importedUtc }`（科目配對未匯入時 null）；`importState.authorizedPreparer = { rowCount }`（授權編製人員清單未匯入時 null；清單是 name 集合、不入 import_batch，故 resume 只回 rowCount，無 fileName/importedUtc）；`importState.calendar = { holidayCount, makeupDayCount }`；mapping 是否已 commit 由 `mapping.gl !== null` 推導。`latestRuns.validate` / `latestRuns.prescreen` = 最近一次 `validate.run` / `prescreen.run` 的完整 response（自 `result_rule_run.summary_json` 原樣回放；schema v3 遷移後為 null，重跑即恢復）。**結果失效不變量**：規則結果是衍生資料。任何改寫其上游的操作（GL/TB re-import 或 re-commit mapping、科目配對匯入、行事曆匯入）都會在同一交易內清除 `result_rule_run` 與抽樣表。因此上游一變動，`latestRuns.*` 即回 null，前端顯示「未執行」並要求重跑——不會回放對應到已不存在資料的舊結果。`filterScenarios = [{ name, rationale, groups, savedUtc }]`（自 `config_filter_scenario`，依 position 排序；v2 時代保存的 `prescreenKey` 已由遷移翻譯為新鍵）；`project.databaseProvider` 標示資料引擎（見上方 provider 歸屬說明）。**載入時戳記 `lastOpenedUtc`**（回寫 project.json，供 `project.list` 依最近開啟排序與顯示） |
| `project.delete` | `{ projectId }` | `{ ok, projectId }` | 永久刪除專案：先刪除該專案資料庫（SQLite 刪 `jet.db`／SQL Server `DROP DATABASE JET_{projectId}`，先 `SET SINGLE_USER WITH ROLLBACK IMMEDIATE` 斷線），再刪除 `projects/{projectId}/` 資料夾。**硬刪、不可復原**（無 soft delete/還原）；**不需 active project**（從專案選擇畫面呼叫）。`projectId` 不存在 → `project_not_found`；選 sqlServer 但連線未設定（`JET_SQLSERVER_CONNECTION` 缺）→ `sql_server_not_configured`（此時資料夾保留、不刪，供修正連線後重試）。若刪除的是當前 session 專案則一併清空 session |
| `import.gl.fromFile` | `{ filePath, fileName?, mode?, sheetName?, encoding?, delimiter? }` | `{ batchId, rowCount, addedRowCount, columns, sources }` | **Scale-aware**：從 `.xlsx` / `.csv` / `.txt` 檔案路徑串流讀 GL 寫入 `staging_gl_raw_row`；payload 不帶 rows。`mode:"append"` 支援多來源合併（見下方細節） |
| `import.gl` | `{ fileName, rows, columns }` | `{ fileName, rows, columns }` | **Legacy compatibility only**；正式 UI/demo/import path 不得使用，新代碼請改用 `import.gl.fromFile` |
| `import.tb.fromFile` | `{ filePath, fileName?, mode?, sheetName?, encoding?, delimiter? }` | `{ batchId, rowCount, addedRowCount, columns, sources }` | **Scale-aware**：從 `.xlsx` / `.csv` / `.txt` 檔案路徑串流讀 TB 寫入 `staging_tb_raw_row`；payload 不帶 rows。語意對齊 `import.gl.fromFile` |
| `import.tb` | `{ fileName, rows, columns }` | `{ fileName, rows, columns }` | **Legacy compatibility only**；正式 UI/demo/import path 不得使用，新代碼請改用 `import.tb.fromFile` |
| `import.accountMapping.fromFile` | `{ filePath, fileName?, mode? }` | `{ batchId, rowCount, columns, fileName, importedUtc }` | **Implemented／Scale-aware**：從 `.xlsx` / `.csv` 檔案路徑串流讀科目配對表寫入 `staging_account_mapping_raw_row` 並投影 `target_account_mapping`；payload 不帶 rows。細節見下方 |
| `import.accountMapping` | `{ fileName, rows }` | `{ fileName, rows }` | **Legacy compatibility only**；正式 UI/demo/import path 不得使用，新代碼請改用 `import.accountMapping.fromFile` |
| `import.authorizedPreparer.fromFile` | `{ filePath, fileName?, mode? }` | `{ batchId, rowCount, fileName, importedUtc }` | **Implemented**：從**單欄** `.xlsx` 讀授權編製人員清單寫入 `staging_authorized_preparer_raw_row` 並投影 `target_authorized_preparer`（name PK）；payload 不帶 rows。細節見下方 |
| `import.inspectFile` | `{ filePath }` | `{ fileType, worksheets, columns, encoding, delimiter }` | **Implemented**。匯入前的唯讀檔案檢視（精靈預覽用），細節見下方 |
| `import.previewFile` | `{ filePath, sheetName?, encoding?, delimiter?, limit? }` | `{ columns, sampleRows }` | **Implemented**。匯入前的逐來源有界預覽（讀標頭 + 前 N 列原貌），細節見下方 |
| `import.holiday` | `{ dates: ["yyyy-MM-dd", …] }` | `{ count }` | **Implemented**。寫入 `staging_calendar_raw_day`（day_type=`holiday`，replace 語意：同 type 先清後寫）。日期格式錯誤 → `invalid_payload`。假日名稱欄位列為未來擴充（假日過帳/核准底稿需要時先修本 manifest） |
| `import.makeupDay` | `{ dates: ["yyyy-MM-dd", …] }` | `{ count }` | **Implemented**。同上，day_type=`makeup` |
| `import.holiday.fromFile` | `{ filePath, fileName?, sheetName? }` | `{ count }` | **Implemented**。從 `.xlsx` 讀事務所假日表（第 1 列樣式標題、第 2 列標頭 `Date_of_Holiday/Holiday_Name/IS_Holiday`，只收 IS_Holiday=Y、缺該欄則全收）寫入 `staging_calendar_raw_day`（`day_type='holiday'`，replace、含 `day_name`）；同交易清結果。僅 `.xlsx`。錯誤碼見下方 |
| `import.makeupDay.fromFile` | `{ filePath, fileName?, sheetName? }` | `{ count }` | **Implemented**。同上，工作表/欄 `Date_of_MakeUpday/MakeUpDay_Desc`，`day_type='makeup'`（無 IS_Holiday 過濾） |

`import.gl.fromFile` 細節：

- `filePath`：本機絕對路徑；支援 `.xlsx`、`.csv`、`.txt`（`.txt` 內容視為 CSV）；其他副檔名回 `unsupported_file_type`。
- `fileName`：可選，預設取自 `filePath`。
- `mode`：`"replace"`（預設）或 `"append"`；其他值回 `unsupported_mode`。
  - `replace` 在**同一 transaction** 內清除該 dataset 的舊批次、staging rows、**target rows 與 committed mapping**（重匯入使配對失效，前端應重新 commit），再以本次來源開立新批次（該批次的第一個來源）。
  - `append` 把本次來源**加入該 dataset 現有的批次**（多來源合併：一個 GL/TB 資料集 = 一個批次，可由多個檔案或多個工作表組成）。語意：
    - 尚無批次 → `no_import_batch`（第一個來源必須走 replace）。
    - 來源的**有效欄名集合**必須與批次一致（順序無關，欄序以第一個來源為準）；不一致 → `column_mismatch`，訊息列出「來源多出／來源缺少」的雙向差集。驗證分兩階段：串流前先比對**具名標頭**集合以快速失敗，串流完成後再以**收斂後的有效欄位集合**（見下方）做終檢。任一階段不符就 rollback 本次來源，既有批次不受影響。
    - 附加成功與下游失效在**同一 transaction**：寫入來源紀錄與 staging rows、批次 `rowCount` 累加、**清除該 dataset 的 target rows 與 committed mapping**（與 replace 相同，前端應重新 commit 配對）。
    - 來源 0 資料列 → rollback 並回 `empty_workbook`（既有批次不受影響）。
    - 多工作表的 `.xlsx`：逐工作表呼叫本 action（第一個工作表 replace、其後 append、各帶 `sheetName`）。
- Response `rowCount` = 批次**總**列數；`addedRowCount` = 本次寫入列數（replace 時兩者相等）。
- Response `sources` = 批次的來源清單（依匯入順序）：`[{ sourceNo, fileName, sheetName|null, encoding|null, delimiter|null, rowCount, importedUtc }]`。`sheetName`/`encoding`/`delimiter` 記錄**呼叫時指定的值**，null 表示交由偵測鏈自動判定。
- Response `columns` = 批次的**有效欄位集合**（guide §3.1.5 收斂規則）：具名標頭一律保留；空白標頭的 `COL_{n}` 佔位欄**只在該欄實際出現過至少一個非空值時保留**。因此同一工作表 `import.inspectFile` 的 `columns`（標頭列原貌，含佔位欄）可能比匯入後批次的 `columns` 多——這是正確行為，不是資料遺失（被剔除的佔位欄整欄無資料）。
- 匯入串流期間每 20,000 列推播一次 `import.progress` 事件（見「Host→Web 事件」章節）。

`import.inspectFile` 細節：

- **唯讀零副作用**、**不需 active project**（建立案件前也可預覽）、不回任何資料列。用途：匯入精靈在實際匯入前預覽檔案結構，作為編碼/分隔符誤判的人工把關點。檢視只讀到標頭列即停（streaming early-exit），檔案大小不影響回應時間。
- `.xlsx` → `{ fileType: "xlsx", worksheets: [{ name, columns, rowCountEstimate }], columns: null, encoding: null, delimiter: null }`：列出全部工作表與各自的正規化欄名（空工作表 `columns` 為空陣列）。`rowCountEstimate` 是**推估**的資料列數（nullable int），取工作表 `<dimension>` 元素的末列號減去標頭列號；dimension 缺席或不可解析時為 null。這個 dimension 由產生端軟體維護，**可能過時**，所以本欄位只供精靈顯示規模預期與進度估算，**不得**用於任何驗證或匯入判斷——實際列數一律以匯入 response 的 `rowCount`／`addedRowCount` 為準。
- 注意：worksheets 的 `columns` 反映**標頭列原貌**（空白標頭以 `COL_{n}` 佔位呈現）；匯入後批次的 `columns` 是收斂後的有效集合（無資料的佔位欄被剔除，見 `import.gl.fromFile` 細節）。兩者欄數可能不同，屬正確行為。
- `.csv` / `.txt` → `{ fileType: "csv", worksheets: null, columns: [...], encoding, delimiter }`：`encoding`/`delimiter` 為**偵測鏈的判定結果**（如 `"big5"`、`","`；單欄檔 `delimiter` 為 null），可直接作為 `import.*.fromFile` 的覆寫參數。
- 錯誤碼同匯入：`file_not_found`、`unsupported_file_type`、`file_read_error`、`empty_workbook`（CSV 無標頭時）。
- `sheetName`：可選，僅 `.xlsx` 有效；缺省 = 第一個工作表；指定的工作表不存在 → `sheet_not_found`；對 `.csv`/`.txt` 提供 → `invalid_payload`。
- `encoding`：可選，僅 `.csv`/`.txt` 有效；白名單 `"utf-8"`、`"big5"`、`"utf-16"`（不分大小寫）；缺省 = 偵測鏈（BOM → 嚴格 UTF-8 驗證 → Big5，見 guide §3.1.1）；非白名單值或對 `.xlsx` 提供 → `invalid_payload`。
- `delimiter`：可選，僅 `.csv`/`.txt` 有效；白名單 `","`、`"\t"`、`";"`、`"|"`（單字元字串）；缺省 = 引號感知取樣統計偵測（guide §3.1.1）；非白名單值或對 `.xlsx` 提供 → `invalid_payload`。
- Response `columns`：**正規化後**的 header row（trim、空白標頭命名 `COL_{n}`、重複標頭加 `_2`/`_3` 字尾），供 `mapping.autoSuggest` / `mapping.commit.gl` 使用；staging `row_json` 的 key 使用同一套名稱。
- **Scale constraint**：response **絕對不**回 rows；明細只透過後續 paging query 取。
- 正式資料、demo/test pipeline、以及任何可能進入 scale path 的匯入都必須走此 action；`import.gl` 僅保留為 legacy fallback。

`import.previewFile` 細節：

- **用途**：讓匯入精靈在按下〔開始匯入〕之前，逐來源預覽「正規化標頭 + 前 N 列原貌」。這是判讀「這份檔案到底有沒有標頭列」的人工把關點——PBC 原始檔常常整份都是資料、根本沒有標頭列。
- **唯讀、零副作用、不需 active project**。預覽有界：只讀標頭加上最多 `limit` 列就 early-exit，**絕不回完整母體**。讀檔、編碼偵測與標頭正規化都沿用 `import.inspectFile` 與正式匯入的同一條鏈。
- `columns`：正規化後的標頭列（trim、空白標頭命名 `COL_{n}`、重複標頭加 `_2`／`_3` 字尾），與 `import.inspectFile` 及欄位配對下拉選單一字不差。
- `sampleRows`：資料列陣列（≤ `limit` 列），每列是對齊 `columns` 的字串 cell 陣列，空 cell → JSON null。讀取順序為來源檔內順序，全空列略過（與匯入一致）。
- `sampleRows` 的 cell 一律對齊 `columns`（標頭欄）。資料列若有超出標頭範圍的儲存格（ragged 列），預覽不呈現——預覽是用來判讀標頭，不是還原完整原貌。
- `limit`：預設 10、上限 10（超出夾擠）。
- `sheetName`／`encoding`／`delimiter` 的適用性與白名單同 `import.gl.fromFile`：僅 `.xlsx` 可用 `sheetName`，僅 `.csv`／`.txt` 可用 `encoding`／`delimiter`，違反 → `invalid_payload`。之所以開放這些覆寫，是因為使用者可能在精靈裡改了 CSV 的編碼或分隔符再重新展開預覽，內容會隨之改變。
- 錯誤碼比照 inspect／匯入：`file_not_found`、`unsupported_file_type`、`sheet_not_found`、`invalid_payload`、`file_read_error`、`empty_workbook`（CSV 無標頭時）。

`import.accountMapping.fromFile` 細節：

- 格式固定三欄（guide §2.3）：**科目代號、科目名稱、標準化分類**。欄位辨識：正規化標頭以關鍵字命中（「科目代號/account code」「科目名稱/account name」「分類/category」）優先；無法命中時依位次 1/2/3。英文標頭 `GL_NUMBER` / `GL_NAME` / `STANDARDIZED_ACCOUNT_NAME`（事務所底稿格式）亦由關鍵字涵蓋；仍保留位次 1/2/3 fallback。不經欄位配對步驟——格式固定，**匯入即投影**（staging 寫入與 `target_account_mapping` 投影同一 transaction）。
- 標準化分類白名單：`Revenue` / `Receivables` / `Cash` / `Receipt in advance` / `Others`（trim 後不分大小寫比對，落地為正準大小寫）。分類不在白名單 → `projection_failed`（訊息含列號與原值，前 10 筆；整批 rollback）。
- 支援 `.xlsx` / `.csv`（不含 `.txt`）；其他副檔名 → `unsupported_file_type`。
- `mode`：僅 `"replace"`（預設）；`"append"` 或其他值 → `unsupported_mode`（科目配對表是整份替換的設定檔，不做多來源合併）。replace 在同一 transaction 清舊批次與 staging/target rows 後重建。
- 同一科目代號重複出現時，後列覆蓋前列（投影層 last-wins 去重，避免同一科目同時落兩種分類造成借貸組合判定歧義）；0 資料列 → `empty_workbook`。
- 匯入成功會使**未預期借貸組合**預篩選與 `accountPair` 篩選條件解鎖；重新匯入不影響既有 GL/TB 批次與配對。

`import.authorizedPreparer.fromFile` 細節：

- 單欄姓名清單：欄位辨識以正規化標頭關鍵字命中（`AUTHORIZED_PREPARER` / `preparer` / `編製人員` / `姓名` / `name`）優先；無法命中時退回位次 1。至少一欄，否則 `projection_failed`。
- 僅支援 `.xlsx`（事務所授權清單範本）；其他副檔名（含 `.csv`）→ `unsupported_file_type`。
- 姓名一律 TRIM 正規化；空白列略過；去重（name 為 PK，集合語意）。`rowCount` = 去重後落地筆數。
- `mode`：僅 `"replace"`（預設）；`"append"` 或其他值 → `unsupported_mode`（授權清單是整份替換的設定檔，不做多來源合併）。replace 在同一 transaction 清舊 staging/target rows 後重建。
- 不寫 `import_batch`／`import_batch_source`（授權清單不入 dataset_kind 體系）；`batchId` 為本次 response 用識別碼，不持久化。
- 匯入成功會使**非授權編製人員**預篩選解鎖（C5）；重新匯入時在同一 transaction 內呼叫 `RuleRunResultReset` 使依賴它的規則結果失效。

`import.holiday.fromFile` / `import.makeupDay.fromFile` 細節：

- 僅支援 `.xlsx`（事務所範本帶樣式標題列；非 `.xlsx` → `unsupported_file_type`）。標頭固定在**第 2 列**（第 1 列為樣式標題，後端以 reader 的 `LeadingRowsToSkip=1` 略過）。
- 欄位辨識：日期欄以標準名（`Date_of_Holiday` / `Date_of_MakeUpday`）關鍵字命中，**必有**（缺 → `projection_failed`，訊息點名缺欄）；名稱欄（`Holiday_Name` / `MakeUpDay_Desc`）與 `IS_Holiday` 選用。
- 假日：`IS_Holiday` 欄存在時只收 `Y`（trim 後不分大小寫），`N`/空白略過；欄缺席則全收。補班無此過濾。
- 多年度照單全收（不依檔名年度過濾）；同日去重（先到者勝）。
- 任一資料列日期非 `yyyy-MM-dd` → `projection_failed`（訊息含列號與原值，前 10 筆；不寫入）。標頭存在但 0 資料列 → `count=0`（以空清單 replace = 清空該 type）。
- replace 語意，同交易清規則結果（`RuleRunResultReset`）。`dates` 陣列舊 action 仍為相容/demo 路徑（名稱 null）。

### Mapping

| Action | Payload | Response | 用途 |
|:---|:---|:---|:---|
| `mapping.autoSuggest` | `{ fields, columns }` | `{ suggested }` | 依欄位標籤與關鍵字自動配對（後端執行；只回命中的 key） |
| `mapping.commit.gl` | `{ mapping, amountMode }` | `{ ok, mapping, amountMode, batchId, projectedRowCount, warnings }` | 提交 GL logical mapping 並將最新 GL staging 批次投影到 `target_gl_entry`。`amountMode` 必填：`signed`（單一帶號金額欄）\| `side`（金額+借貸別文字欄）\| `flag`（金額+借方旗標欄）\| `dual`（借方欄−貸方欄），對應 guide §2.1 四模式。`warnings` 為非阻斷提醒字串陣列（可空）：投影成功但必填文字欄（傳票號碼／會計科目編號／會計科目名稱／傳票摘要）整欄空白時，逐欄列出疑似配錯的來源欄（如來源重複標頭中的空白欄），供前端提交成功後就近提示；不影響投影結果 |
| `mapping.commit.tb` | `{ mapping, changeMode }` | `{ ok, mapping, changeMode, batchId, projectedRowCount }` | 提交 TB logical mapping 並投影到 `target_tb_balance`。`changeMode` 必填：`direct`（直接變動金額）\| `debitCredit`（借方−貸方） |

投影語意為 **import-stage normalization**：backend 以 streaming 方式逐列讀 staging、以 `decimal` 解析金額並乘以 project `MoneyScale` 轉 scaled integer（guide §1.5.3），逐列回報轉換錯誤（Excel 列號 + 欄名 + 原值）；任一列失敗則整批 rollback 並回 `projection_failed`。**另有退化母體守門：投影無列級錯誤、母體也非空，但整個 GL 母體借貸總額皆為 0（金額欄誤配到傳票總額或空欄，無法用於完整性與後續規則）時，整批 rollback 並回 `gl_amounts_all_zero`。** set-based pushdown（guide §1.5.2）約束的是 V/R/Filter 規則計算，不受此影響。

**傳票文件項次（`line_item`）自動編號**：`mapping.commit.gl` 提交時，如果 `lineID` **未對應**到來源欄，投影落地後會在**同一交易**內以 `ROW_NUMBER() OVER (PARTITION BY document_number ORDER BY source_row_number)` 替每張傳票自動補上 `line_item`；`lineID` 有對應就照來源**逐字寫入、不自動編號**。這個 `line_item` 只是文字形式的衍生顯示值，**不參與**任何驗證／預篩選／篩選計算，也**不作**任何規則或抽樣的鍵（抽樣依 `source_row_number`），因此不改變任何測試結果。wire shape 不變、兩個 provider（SQLite / SQL Server）等價。

`dcDebitCode` 的 mapping 值是**借方代碼字面值**（例如 `"D"`、`"1"`），不是來源欄位名稱；比對方式為 trim 後不分大小寫文字相等。其餘 mapping 值必須是匯入批次 `columns` 中存在的欄位名稱，否則回 `mapping_column_not_found`。

`fields` 來源通常為 UI 的欄位定義陣列。每個元素：

```json
{
  "key": "docNum",
  "label": "傳票號碼",
  "req": true,
  "type": "mix"
}
```

### Validation

規則命名一律使用**具體名稱**（wire key = lowerCamelCase、資料表 slug = snake_case、UI 顯示中文名），不再使用 V1–V4 代號——正準對照見 guide §4 的命名登錄表。

| Action | Payload | Response | 用途 |
|:---|:---|:---|:---|
| `validate.run` | `{}` | 見下方 response 形狀 | **Implemented**。以 SQL set-based 對 `target_gl_entry` / `target_tb_balance` 執行四項資料驗證 summary；完整 response 以 `JetJsonStorage` 存入 `result_rule_run`（resume 用），INF 抽樣樣本落地 `result_inf_sampling_test_sample`（guide §4：抽樣必須可重現） |
| `query.validationDetailsPage` | `{ projectId, kind, cursor?, pageSize? }` | `{ rows, nextCursor }` | Planned（明細分頁屬匯出/明細里程碑；本版 UI 消費 summary counts 與 `completenessTest.diffAccounts`／`docBalanceTest.unbalancedDocuments`／`nullRecordsTest.nullRows` 有界清單） |

`validate.run` response（金額欄位為 scaled ÷ MoneyScale 的顯示值；權威計算只在 SQL 的 scaled BIGINT）：

```json
{
  "stats": { "glRowCount": 2000, "voucherCount": 491, "totalDebit": 0.0, "totalCredit": 0.0,
             "net": 0.0, "periodStart": "2025-01-01", "periodEnd": "2025-12-31" },
  "completenessTest": { "status": "V", "naReason": null, "diffAccountCount": 0,
          "diffAccounts": [ { "accountCode": "", "accountName": "", "tbAmount": 0.0, "glAmount": 0.0, "diff": 0.0, "notInTb": false } ],
          "partA": { "sourceRowCount": 0, "targetRowCount": 0, "totalDebit": 0.0, "totalCredit": 0.0,
                     "rowCountMatch": true, "amountMatch": true } },
  "docBalanceTest": { "status": "V", "unbalancedDocumentCount": 0,
          "unbalancedDocuments": [ { "documentNumber": "", "debit": 0.0, "credit": 0.0, "diff": 0.0 } ] },
  "infSamplingTest": { "status": "V", "sampleSize": 60, "seed": 48271 },
  "nullRecordsTest": { "status": "V", "nullAccountCount": 0, "nullDocumentCount": 0,
          "nullDescriptionCount": 0, "outOfRangeDateCount": 0,
          "nullRows": [ { "documentNumber": "", "accountCode": "", "postDate": "", "description": "", "issues": ["account"] } ] },
  "resultRef": { "runId": "<32hex>", "generatedUtc": "<ISO-8601>" }
}
```

- **有界內嵌明細（衍生、僅供顯示）**：`completenessTest.diffAccounts`、`docBalanceTest.unbalancedDocuments`、`nullRecordsTest.nullRows` 各為後端算出的有上限樣本（≤50 筆），供 UI 展開檢視。借貸不平明細依差額絕對值排序；空值明細的 `issues` 標明該列命中的檢查（`account`／`document`／`description`／`date`）。完整性差異明細的 `notInTb` 標記該科目「GL 有、TB 無」（具名化的 Not-in-TB；`false` 表示 TB 與 GL 皆有但金額不符）。這些明細**不參與**任何規則或抽樣計算，也不影響任何 count。完整 Not-in-TB 列舉仍依規劃中的 `query.validationDetailsPage`（≤50 筆為有界樣本）。
- **完整性 part(a)（控制總數核對）**：`completenessTest.partA` 為總額層級的端到端核對——投影 staging→target 時落地的控制總數（來源列數、母體列數、母體借/貸總額）對上 `target_gl_entry` 現值。`rowCountMatch`／`amountMatch` 為 scaled 整數比較結果；尚未投影（無控制總數）時各鍵齊備、值為 null/false（`status:"na"` 形狀）。
- 規則狀態語意依 guide §5 狀態表：`"V"` = 已執行且有結果；`"na"` = 前置條件不足（缺欄位/設定）**或已執行但 0 筆命中**（count 仍回 0 數值）。`naReason` 只在前置條件不足時提供文字。
- **前置條件**：GL mapping 尚未 commit（無 target 投影資料）→ `no_target_data`。TB mapping 未 commit 不是錯誤：完整性測試回 `status:"na"`（naReason 說明需 TB）。
- **INF 抽樣公式（可攜、可重現）**：`ORDER BY (source_row_number * @seed) % 2147483647, entry_id LIMIT @n`，seed 固定 `48271`、n 預設 60。排序鍵用 `source_row_number`（批次內穩定），不用 AUTOINCREMENT 的 `entry_id`（重投影後不穩定）。同一批次重跑必得相同樣本；seed、樣本 keys、runId 落地 `result_inf_sampling_test_sample`。guide §4 的「hash」措辭以此可攜整數算術實作（SQLite 與 SQL Server 同義）；若需變更 RuleSpec 語意，先修 guide。
- **歷史鍵相容**：schema v3 遷移會清除 v2 時代以舊鍵（`v1`–`v4`）儲存的 `result_rule_run` 摘要（衍生資料，重跑即恢復且結果相同——抽樣 seed 固定）；遷移後 `project.load.latestRuns` 為 null，前端顯示「未執行」。
- **結果失效不變量**：`result_rule_run` 與 `result_inf_sampling_test_sample` 是衍生資料。任何改寫其上游的操作都必須在「同一交易」內清除這兩張表，才不會出現「資料已換、舊結果還在」的中間態；上游若 rollback，清除也一併回退。上游包含：GL/TB `import.*.fromFile`（replace 與 append）、`mapping.commit.{gl,tb}` 重投影、`import.accountMapping.fromFile`、`import.authorizedPreparer.fromFile`、`import.{holiday,makeupDay}`。失效後 `validate.run` / `prescreen.run` 必須重跑才有結果。`result_filter_run`（匯出里程碑、目前未持久化）落地時也須沿用本不變量。

### Prescreen

規則命名一律使用**具體名稱**，不再使用 R1–R8 代號——正準對照見 guide §5 的命名登錄表。

| Action | Payload | Response | 用途 |
|:---|:---|:---|:---|
| `prescreen.run` | `{}` | 見下方 response 形狀 | **Implemented**。以 SQL set-based 對 `target_gl_entry`（join `staging_calendar_raw_day` / `target_account_mapping`）執行預篩選 summary；完整 response 存入 `result_rule_run`（resume 用），不回任何完整 row list |
| `query.prescreenPage` | `{ projectId, kind, cursor?, pageSize? }` | `{ rows, nextCursor }` | Planned（明細分頁屬匯出/明細里程碑） |

`prescreen.run` response：

```json
{
  "postPeriodApproval": { "status": "V", "naReason": null, "count": 0 },
  "suspiciousKeywords": { "status": "V", "count": 0 },
  "unexpectedAccountPair": { "status": "na", "naReason": "需先匯入科目配對", "count": 0 },
  "trailingZeros": { "status": "V", "count": 0, "zerosThreshold": 3 },
  "creatorSummary": { "status": "V", "naReason": null,
          "creators": [ { "createdBy": "", "entryCount": 0, "debitTotal": 0.0, "creditTotal": 0.0, "manualCount": 0 } ] },
  "rareAccounts": { "status": "V", "distinctAccountCount": 0,
          "accounts": [ { "accountCode": "", "accountName": "", "entryCount": 0, "debitTotal": 0.0, "creditTotal": 0.0 } ] },
  "weekendActivity": { "status": "V", "naReason": null, "postingCount": 0, "approvalCount": 0 },
  "holidayActivity": { "status": "V", "naReason": null, "postingCount": 0, "approvalCount": 0 },
  "blankDescription": { "status": "V", "count": 0 },
  "backdatedPosting": { "status": "V", "count": 0 },
  "nonAuthorizedPreparer": { "status": "na", "naReason": "需先匯入授權編製人員清單", "count": 0 },
  "lowFrequencyPreparer": { "status": "V", "count": 0 },
  "lowFrequencyAccount": { "status": "V", "count": 0 },
  "resultRef": { "runId": "<32hex>", "generatedUtc": "<ISO-8601>" }
}
```

備註：

- 規則語意見 guide §5：期末財報準備日後核准、分錄摘要特定描述（預設關鍵字）、未預期借貸組合、連續零尾數、編製者彙總、較少使用科目、週末過帳/核准（**排除補班日**）、假日過帳/核准、摘要空白、回溯過帳（過帳日早於傳票日；`voucher_date` 為 NULL 不命中）、非授權編製人員（`created_by` 不在授權清單；C5）、低頻編製者（`created_by` 全期分錄筆數 ≤ 11；C6）。counts 一律為 summary，不是 row list。
- 前置條件→`na` 映射：`postPeriodApproval` = `docDate` 未映射或專案無 `lastPeriodStart`；`unexpectedAccountPair` = 科目配對未匯入，或配對表缺 `Revenue` 或對方分類（`Receivables`/`Cash`/`Receipt in advance` 全缺）；`creatorSummary` = `createBy` 未映射；`weekendActivity.approvalCount` / `holidayActivity.approvalCount` = `docDate` 未映射時為 `null`（`postingCount` 恆可算——`postDate` 為必填 mapping）；`holidayActivity` = 尚無假日資料；`nonAuthorizedPreparer` = 授權編製人員清單未匯入（`target_authorized_preparer` 為空）。0 命中也標 `na`（guide §5 狀態表），count 仍回 0。`lowFrequencyPreparer` 無前置條件、永遠跑（`{ status, count }`，無 `naReason`）。
- `creatorSummary` / `rareAccounts` 為彙總規則（非 row tag），各限 50 列；`rareAccounts.accounts` 依使用次數升冪。`lowFrequencyAccount`（R12）為 `rareAccounts`（R6 彙總）的列述詞版，可作進階篩選列述詞，述詞 `account_code IN (SELECT account_code FROM target_gl_entry GROUP BY account_code HAVING COUNT(*) <= @n)`，固定預設門檻 11。
- 連續零尾數門檻為動態推估：以借方金額平均（C# decimal 自 scaled 換算，不以 SQLite REAL `AVG` 作權威）取 `max(3, floor(log10(avg)) − 1)`；`zerosThreshold` 回報當次採用值。進階篩選的 `customTrailingZeros` 條件可指定固定位數取代動態值（原 A4 語意）。
- 自訂關鍵字（原 A2 語意）由 filter `customKeywords` 條件涵蓋；自訂科目配對（原 A3 語意）由 filter `accountPair` 條件涵蓋（見 Filter / Criteria 章節）。
- **歷史鍵相容**：schema v3 遷移會清除 v2 時代以舊鍵（`r1`–`r8`、`descNullCount`）儲存的摘要，重跑即恢復；`config_filter_scenario` 內的舊 `prescreenKey` 由遷移逐鍵翻譯保留（`r1→postPeriodApproval`、`r2→suspiciousKeywords`、`r4→trailingZeros`、`r7post→weekendPosting`、`r7doc→weekendApproval`、`r8post→holidayPosting`、`r8doc→holidayApproval`、`descNull→blankDescription`）。

### Filter / Criteria

| Action | Payload | Response | 用途 |
|:---|:---|:---|:---|
| `filter.preview` | `{ scenario }` | `{ scenario: { name, count, voucherCount, previewRows } }` | **Implemented**。後端將條件 AST 轉成**參數化 SQL**（識別字只來自欄位白名單）交由 DB set-based 評估；`previewRows` ≤ 50（`{ documentNumber, lineItem, postDate, accountCode, accountName, documentDescription, amount, drCr }`）。**本版為無狀態查詢**：不落地結果——與原規劃差異：`result_filter_run` / `resultRef` 延後到匯出里程碑，屆時 export 以同一 AST set-based 重跑 |
| `query.filterPage` | `{ projectId, runId?, cursor?, pageSize? }` | `{ rows, nextCursor }` | Planned（隨 `result_filter_run` 一起實作） |
| `filter.commit` | `{ scenarios }` | `{ ok, savedCount }` | **Implemented**。保存情境**定義**（非結果）到 `config_filter_scenario`：replace-all 語意、上限 **10** 個、名稱不可重複、每個情境重新驗證；`project.load` 以 `filterScenarios` 回傳 resume。原規劃的 `savedRef` 延後到匯出里程碑 |

`scenario` 條件 AST schema（前端 Query Builder **只組裝此 JSON**，不評估規則）：

```json
{
  "name": "情境名稱（必填）",
  "rationale": "篩選動機說明（必填；保留到工作底稿）",
  "groups": [
    {
      "join": "AND",
      "rules": [
        {
          "join": "AND",
          "type": "prescreen | text | dateRange | numRange | drCrOnly | manualAuto | accountPair | periodInOut | customKeywords | customTrailingZeros | customPreparerEntryCount | customAccountEntryCount",
          "prescreenKey": "postPeriodApproval | suspiciousKeywords | unexpectedAccountPair | trailingZeros | weekendPosting | weekendApproval | holidayPosting | holidayApproval | blankDescription | backdatedPosting | nonAuthorizedPreparer | lowFrequencyPreparer | lowFrequencyAccount",
          "field": "docNum | lineID | accNum | accName | description | jeSource | createBy | approveBy | postDate | docDate | voucherDate | amount",
          "keywords": "逗號分隔的關鍵字",
          "mode": "contains | exact | notContains | notExact",
          "from": "",
          "to": "",
          "drCr": "debit | credit",
          "isManual": "true | false",
          "pairMode": "exact | debitAnchor | creditAnchor",
          "debitCategory": "Receivables | Cash | Receipt in advance | Revenue | Others",
          "creditCategory": "Receivables | Cash | Receipt in advance | Revenue | Others",
          "inPeriod": "true | false",
          "digits": 3,
          "maxEntries": 11
        }
      ]
    }
  ]
}
```

AST 語意與安全約束：

- **結合律**：群組內規則之間、群組與群組之間皆為**左折疊**累積括號 `((c1 OP c2) OP c3)`；第一條規則／第一個群組的 `join` 忽略。
- **prescreenKey** 只接受 row-tag 規則（上列十二鍵）。`creatorSummary`/`rareAccounts` 是彙總規則，不可作列述詞；`unexpectedAccountPair` 需科目配對已匯入（未匯入 → `invalid_scenario`）；`nonAuthorizedPreparer` 需授權編製人員清單已匯入（filter 端**空名單 → `invalid_scenario`**，鏡像 `unexpectedAccountPair` 的 validator 閘控；述詞層另以 `EXISTS (SELECT 1 FROM target_authorized_preparer) AND …` 自保——即便繞過 validator，空名單時整體述詞為 FALSE、零命中，與 `prescreen.run` 的 `na` 語意對齊，不會因 `NOT IN (空集合)` 反轉成全命中）；`lowFrequencyPreparer` 無前置條件。prescreen 條件與 `prescreen.run` **共用同一份 SQL 述詞**（單一事實來源，Infrastructure `GlRulePredicates`），即時計算、不依賴先前 run 結果。
- **field 白名單**：`field` 為邏輯 id，經 Domain 白名單映射實體欄位（`docNum→document_number`、`docDate→approval_date`、`voucherDate→voucher_date`、`amount→ABS(amount_scaled)`…）；未知 id → `invalid_scenario`。**SQL 識別字永遠不來自使用者輸入**；所有值（關鍵字、日期、金額、分類、位數）一律參數綁定。
- **legacy 預篩選遷移對照**（vba-1120 ServiceFilter 12 條件 → 本 AST）：週末/假日過帳或核准 → `prescreen` 的 `weekendPosting/weekendApproval/holidayPosting/holidayApproval`（補班日排除內建於週末規則）；僅借方/僅貸方 → `drCrOnly`；人工分錄 → `manualAuto`；關鍵字 → `text`；日期/數值區間 → `dateRange`/`numRange`。
- `accountPair`（科目配對分析，guide §6.1 三模式；原 A3 語意）：需科目配對已匯入（未匯入 → `invalid_scenario`；UI 鏡像隱藏按鈕，權威在後端）。`pairMode:"exact"` 需 `debitCategory` 與 `creditCategory` 皆填；`"debitAnchor"` 只需 `debitCategory`；`"creditAnchor"` 只需 `creditCategory`；分類值必須在 guide §2.3 白名單內。借方側 = 指定借方分類且 `amount_scaled >= 0`、貸方側 = 指定貸方分類且 `amount_scaled < 0`（0 元歸借方側，與 `drCr` 推導一致）；錨定模式輸出錨定分錄與同傳票的對方側分錄。
- `periodInOut`（期內/期外，guide §6.2）：`inPeriod:"true"` → `post_date` 落在專案會計期間（含邊界）；`"false"` → 期間外。`post_date` 為 NULL 的列兩側皆不命中。無前置條件（會計期間為專案必填欄位）。
- `customKeywords`（自訂關鍵字；原 A2 語意）：述詞同 `suspiciousKeywords`（contains-any、不分大小寫、NULL 以空字串參與），關鍵字改為使用者輸入（逗號分隔，至少一個非空白）。
- `customTrailingZeros`（自訂尾數位數；原 A4 語意）：`digits` 為整數 1–12（主單位的連續零位數），取代動態門檻；述詞同 `trailingZeros`（整數取模，不用字串函式）。
- `customPreparerEntryCount`（自訂低頻編製者門檻；C6 自訂軌）：`maxEntries` 為整數 ≥ 1，取代固定預設 11；述詞同 `lowFrequencyPreparer`（`created_by IN (SELECT created_by FROM target_gl_entry GROUP BY created_by HAVING COUNT(*) <= @n)`，門檻參數綁定）。
- customAccountEntryCount(自訂低頻科目門檻;C9 自訂軌):maxEntries 為整數 ≥ 1,取代固定預設 11;述詞同 lowFrequencyAccount(account_code IN (SELECT account_code FROM target_gl_entry GROUP BY account_code HAVING COUNT(*) <= @n),門檻參數綁定)。
- `numRange`：`from`/`to` 為顯示值 decimal，後端以 MoneyScale 轉 scaled 後比較 `ABS(amount_scaled)`；至少填一個邊界。`dateRange`：`yyyy-MM-dd`，比較 `field` 指定的日期欄；至少填一個邊界。
- `text`：NULL 欄位以空字串參與比對（`COALESCE`，`notContains` 對 NULL 列成立）；比對不分大小寫。
- `manualAuto`：比對 `is_manual = 1|0`；來源未提供人工旗標（NULL）的列永不匹配。
- 驗證錯誤（缺名稱/動機、空群組、未知 field/prescreenKey/type/mode、缺邊界、`prescreen postPeriodApproval` 但專案無 `lastPeriodStart`、`accountPair`/`unexpectedAccountPair` 但科目配對未匯入、`digits` 超出 1–12、`customPreparerEntryCount/customAccountEntryCount maxEntries` 小於 1、分類不在白名單…）→ `invalid_scenario`（訊息合併全部錯誤）；commit 超過 10 個 → `scenario_limit_reached`；commit 重名 → `invalid_scenario`。

### Export

| Action | Payload | Response | 用途 |
|:---|:---|:---|:---|
| `export.validation` | `{}` | `{ ok, message }` | Current stub：validation 匯出仍委派前端；scale-complete export 需走 backend-controlled path |
| `export.prescreen` | `{}` | `{ ok, message }` | Current stub：prescreen 匯出仍委派前端；scale-complete export 需走 backend-controlled path |
| `export.criteria` | `{}` | `{ ok, message }` | Current stub：criteria 匯出仍委派前端；scale-complete export 需走 backend-controlled path |
| `export.workpaper` | `{ selected }` | `{ ok, message }` | Current stub：workpaper 匯出仍委派前端；正式 large-data path 必須改為 backend streaming writer |

## JetApi Typed Facade

前端**唯一**呼叫 bridge 的管道是 `window.JetApi.*`。此 facade 目前**手動維護**於 `wwwroot/js/jet-api.js`，以檔內 `SUPPORTED_ACTIONS` 清單為單一事實來源自動生成 typed methods（尚無 C# `JetBridgeScriptFactory`；未來若建立 script factory，生成規則不變）。action name 與 facade method 對照規則為：

1. 以 `.` 切段。
2. 第一段小寫；後續每段首字母大寫（lowerCamelCase 串接）。
3. 例：`validate.run` → `JetApi.validateRun`；`mapping.commit.gl` → `JetApi.mappingCommitGl`。

目前已註冊的 method（以 `wwwroot/js/jet-api.js` 的 `SUPPORTED_ACTIONS` 為單一事實來源;dev.db.* / dev.log.* 僅 Debug 組建）：

| Action | JetApi method |
|:---|:---|
| `system.ping` | `JetApi.systemPing` |
| `project.list` | `JetApi.projectList` |
| `project.saveProgress` | `JetApi.projectSaveProgress` |
| `project.create` | `JetApi.projectCreate` |
| `project.load` | `JetApi.projectLoad` |
| `project.delete` | `JetApi.projectDelete` |
| `project.loadDemo` | `JetApi.projectLoadDemo` |
| `demo.exportGlFile` | `JetApi.demoExportGlFile` |
| `demo.exportTbFile` | `JetApi.demoExportTbFile` |
| `demo.exportAccountMappingFile` | `JetApi.demoExportAccountMappingFile` |
| `demo.exportAuthorizedPreparerFile` | `JetApi.demoExportAuthorizedPreparerFile` |
| `import.gl.fromFile` | `JetApi.importGlFromFile` |
| `import.tb.fromFile` | `JetApi.importTbFromFile` |
| `import.accountMapping.fromFile` | `JetApi.importAccountMappingFromFile` |
| `import.authorizedPreparer.fromFile` | `JetApi.importAuthorizedPreparerFromFile` |
| `import.inspectFile` | `JetApi.importInspectFile` |
| `import.previewFile` | `JetApi.importPreviewFile` |
| `import.holiday` | `JetApi.importHoliday` |
| `import.makeupDay` | `JetApi.importMakeupDay` |
| `import.holiday.fromFile` | `JetApi.importHolidayFromFile` |
| `import.makeupDay.fromFile` | `JetApi.importMakeupDayFromFile` |
| `mapping.autoSuggest` | `JetApi.mappingAutoSuggest` |
| `mapping.commit.gl` | `JetApi.mappingCommitGl` |
| `mapping.commit.tb` | `JetApi.mappingCommitTb` |
| `validate.run` | `JetApi.validateRun` |
| `prescreen.run` | `JetApi.prescreenRun` |
| `filter.preview` | `JetApi.filterPreview` |
| `filter.commit` | `JetApi.filterCommit` |
| `query.dataPreview` | `JetApi.queryDataPreview` |
| `query.completenessDiffPage` | `JetApi.queryCompletenessDiffPage` |
| `query.docBalancePage` | `JetApi.queryDocBalancePage` |
| `query.nullRecordsPage` | `JetApi.queryNullRecordsPage` |
| `query.filterHitsPage` | `JetApi.queryFilterHitsPage` |
| `query.infSamplePage` | `JetApi.queryInfSamplePage` |
| `query.tagMatrixScenarios` | `JetApi.queryTagMatrixScenarios` |
| `query.tagMatrixVoucherPage` | `JetApi.queryTagMatrixVoucherPage` |
| `query.tagMatrixRowPage` | `JetApi.queryTagMatrixRowPage` |
| `log.append` | `JetApi.logAppend` |
| `log.recent` | `JetApi.logRecent` |
| `host.selectFile` | `JetApi.hostSelectFile` |
| `host.selectFiles` | `JetApi.hostSelectFiles` |
| `host.selectSavePath` | `JetApi.hostSelectSavePath` |
| `host.openFolder` | `JetApi.hostOpenFolder` |
| `host.exitApp` | `JetApi.hostExitApp` |
| `export.workpaperStream` | `JetApi.exportWorkpaperStream` |
| `dev.db.overview` | `JetApi.devDbOverview` |
| `dev.db.tableData` | `JetApi.devDbTableData` |
| `dev.log.export` | `JetApi.devLogExport` |

規則：

- UI/demo/workflow code 一律呼叫 `await JetApi.xxx(payload)`，不得直接呼叫 `window.jet.invoke(...)` 或 `window.chrome.webview.postMessage(...)`（除 bootstrap script 本身）。
- 呼叫未註冊的 method 即為 undefined function error，提示先在本 manifest 新增對應 action。
- 新增 action 時必定先改本 manifest、`SUPPORTED_ACTIONS`、handler，最後才在 UI 使用 `JetApi.<newMethod>`。

## Error Codes

Handler 以 `JetActionException(code, message)` 回報業務錯誤；bridge 將其 `code` 直接放入 response `error.code`。所有其他未預期例外一律以 `bridge_error` 呈現。已註冊錯誤碼：

| Code | 意義 |
|:---|:---|
| `invalid_payload` | payload 缺必填欄位或格式錯誤（訊息列出欄名） |
| `no_active_project` | 尚未建立或載入任何專案 |
| `project_not_found` | projectId 不存在或格式無效 |
| `file_not_found` | filePath 指向的檔案不存在；或 dev.db.* 檢視時專案 DB 檔不存在（唯讀檢視不會建檔） |
| `unsupported_file_type` | 副檔名不在支援清單（`.xlsx`、`.csv`、`.txt`） |
| `file_read_error` | 檔案無法開啟或解析（如被 Excel 鎖定、損壞、文字檔編碼不可解碼） |
| `sheet_not_found` | `sheetName` 指定的工作表不存在於 `.xlsx` 檔案中 |
| `empty_workbook` | 無工作表、無標頭列（含 CSV 空檔／僅標頭），或匯入後資料列數為 0 |
| `no_import_batch` | 尚未匯入該 dataset 就執行 mapping commit，或尚無批次就以 `mode:"append"` 附加來源 |
| `column_mismatch` | `mode:"append"` 的來源欄名集合與既有批次不一致（訊息列出雙向差集） |
| `missing_required_mapping` | mapping 缺必填 key（訊息列出缺漏 keys） |
| `mapping_column_not_found` | mapping 指到的欄位不在匯入批次 columns 中 |
| `projection_failed` | staging→target 轉換有列級錯誤（訊息含錯誤數與前 10 筆明細；整批已 rollback） |
| `unsupported_mode` | import mode 或 amountMode/changeMode 不被支援 |
| `table_not_allowed` | dev.db.tableData 的 tableName 不在白名單 |
| `no_target_data` | 尚未 commit GL mapping（無 target 投影資料）就執行 `validate.run` / `prescreen.run` / `filter.preview`。與 `no_import_batch`（mapping commit 階段缺匯入批次）語意區隔 |
| `invalid_scenario` | filter 條件 AST 驗證失敗（缺名稱/動機、空群組、未知 field/prescreenKey/type/mode、缺邊界、科目配對未匯入即用 accountPair/unexpectedAccountPair、digits 超界、分類不在白名單、commit 重名…；訊息合併列出全部錯誤） |
| `scenario_limit_reached` | `filter.commit` 超過 10 個情境上限 |
| `gl_amounts_all_zero` | `mapping.commit.gl` 投影出非空 GL 母體，但整個母體借貸總額皆為 0（常見於借/貸金額欄誤配到「傳票總額」欄——同一張傳票每列借=貸、逐列淨額恆為 0——或配到空欄）；整批已 rollback，請改配對列層借/貸金額欄 |
| `bridge_error` | 其他未分類錯誤（fallback，含未知 action） |

## Demo Pipeline 對齊原則

Demo 載入流程**必須**走與使用者上傳相同的 file-based import pipeline，不得再用 row-based demo fallback。目前 MockDataLoader 已實作步驟 1-6（含 accountMapping 匯入；該格式固定、無獨立 commit 步驟）以及 7 的 `validate.run` / `prescreen.run` / `filter.preview` / `filter.commit`（明細分頁 `query.*Page` 為 Planned）：

1. `JetApi.projectLoadDemo()` → 取 metadata（專案欄位、file names、holidays、makeup、建議 mapping）。
2. `JetApi.demoExportGlFile()` / `JetApi.demoExportTbFile()` / `JetApi.demoExportAccountMappingFile()` → 取得 host 端 `.xlsx` 檔案路徑。
3. `JetApi.projectCreate(metadata)`。
4. `JetApi.importGlFromFile({ filePath, fileName })` / `JetApi.importTbFromFile(...)` / `JetApi.importAccountMappingFromFile(...)`。
5. `JetApi.importHoliday({ dates })` / `JetApi.importMakeupDay({ dates })`。
6. `JetApi.mappingCommitGl({ mapping })` / `JetApi.mappingCommitTb({ mapping })`，以及任何未來已在本 manifest 登記的 account/classification commit action。
7. `JetApi.validateRun()` / `JetApi.prescreenRun()`；filter 步驟以 `DemoProjectDto.demoScenario` 走 `JetApi.filterPreview({ scenario })` → `JetApi.filterCommit({ scenarios })`。明細分頁（`query.validationDetailsPage` / `query.prescreenPage` / `query.filterPage`）屬未來里程碑。

`demo.fetch*Rows` 與 row-based `import.*` 只保留為 legacy fallback。正式 UI、測試按鈕、文件範例與新程式碼不得使用它們作為 demo pipeline。

## Current Logical Mapping Keys

### GL Mapping Keys

這些 key 由 `docs/jet-template.html` 與 C# handlers 共同使用：

| Key | Label | Required | Notes |
|:---|:---|:---|:---|
| `docNum` | 傳票號碼 | Yes | 憑證聚合主鍵 |
| `lineID` | 傳票文件項次 | Conditional | 實際 ERP 匯出（如目前 fixture）常無項次欄；缺漏時 `target_gl_entry.line_item` 為 NULL，`docNum` 仍是憑證聚合鍵 |
| `postDate` | 總帳日期 | Yes | validation / filter |
| `docDate` | 傳票核准日 | No | 期末後核准、週末/假日核准等日期類規則 |
| `voucherDate` | 傳票日期 | No | 回溯過帳偵測(過帳日 < 傳票日)、日期區間篩選;選填 |
| `accNum` | 會計科目編號 | Yes | validation / prescreen / filters |
| `accName` | 會計科目名稱 | Yes | UI / reporting |
| `description` | 傳票摘要 | Yes | 摘要關鍵字規則 / 文字篩選 |
| `jeSource` | 分錄來源模組 | No | UI only today |
| `createBy` | 傳票建立人員 | No | 編製者彙總 |
| `approveBy` | 傳票核准人員 | No | UI only today |
| `manual` | 人工/自動分錄 | No | manualAuto filter |
| `amount` | 傳票金額（單欄） | Conditional | 與 debit/credit 雙欄位互斥 |
| `debitAmount` | 借方金額 | Conditional | 雙欄位模式 |
| `creditAmount` | 貸方金額 | Conditional | 雙欄位模式 |
| `dcField` | 借貸別欄位 | No | optional UI mapping key |
| `dcDebitCode` | 借方標識代碼 | No | optional UI mapping key |

### TB Mapping Keys

| Key | Label | Required | Notes |
|:---|:---|:---|:---|
| `accNum` | 會計科目編號 | Yes | completeness diff |
| `accName` | 會計科目名稱 | Yes | UI only today |
| `amount` | 年度變動金額 | Conditional | DirectChange mode（`changeMode: "direct"`） |
| `debitAmt` | 借方金額 | Conditional | DebitCredit change mode（`changeMode: "debitCredit"`，變動 = 借方 − 貸方）— supported |
| `creditAmt` | 貸方金額 | Conditional | DebitCredit change mode — supported |

> TB 金額表示法為條件式：`amount` 單欄（direct）或 `debitAmt`+`creditAmt` 雙欄（debitCredit）擇一。OpenClose / OpenCloseBySide 模式（guide §2.2）尚無對應 mapping keys，列為未來契約擴充。

## Step Data Outline

這份綱要是前端生成 UI 前應先對齊的資料模型。

前端步驟模型為 **6 步**：建立案件 → 匯入資料 → 欄位配對 → **資料驗證與測試** → **進階條件篩選** → 匯出底稿。其中「資料驗證與測試」把 validation 與 prescreen 合併在同一步驟，對齊 `docs/jet-template.html` 的 step3 設計；此安排於 2026-06-10 收斂「七步骨架」與「五步規格」不一致時定案。

| Step | 前端需要的資料 | 建議 action |
|:---|:---|:---|
| Step 0 Shell | app name, DB provider, supported actions, demo enabled | `app.bootstrap`, `system.ping` |
| Step 1 Project / Import | project metadata, import file names, streaming import columns, holidays, makeup days | `project.create`, `import.*.fromFile`, `project.loadDemo` |
| Step 2 Mapping | GL/TB field definitions, uploaded columns, suggested mappings, committed mappings | `mapping.autoSuggest`, `mapping.commit.gl`, `mapping.commit.tb` |
| Step 3 資料驗證與測試 | stats、四項資料驗證狀態物件、九項預篩選狀態物件、resultRef（counts 渲染為徽章；na 顯示 `—`；規則以中文名呈現，不用代號） | `validate.run`, `prescreen.run`（明細分頁 Planned：`query.validationDetailsPage` / `query.prescreenPage`） |
| Step 4 進階條件篩選 | 條件 AST 草稿（Query Builder 本地組裝）、預覽 `{ count, voucherCount, previewRows ≤50 }`、已儲存情境清單 ≤10、科目配對 presence（決定 accountPair 條件是否顯示） | `filter.preview`, `filter.commit`（`query.filterPage` Planned） |
| Step 5 Export | selected outputs, export feedback | `export.validation`, `export.prescreen`, `export.criteria`, `export.workpaper` |

## Change Process For New UI Or New Actions

當 agent 被要求新增畫面、重做 UX、或擴充 bridge 時，依序做：

1. 明確指出影響哪一個 workflow step。
2. 先檢查現有 action 是否已足夠。
3. 若不足，先在本 manifest 補齊：
   - action name
   - payload shape
   - response shape
   - owner layer
   - UI caller / fixed bindings
4. 再修改 `ActionDispatcher`、DTO、handler、HTML。
5. 若契約變動會影響 `docs/jet-guide.md`，同步更新。

## Anti-Patterns

- 先生成很完整的 UI，事後才補 action 契約
- 在 HTML 裡拼 SQL 或內嵌業務規則
- 把 bridge 當 application service 寫
- 改了 action payload，卻不更新 manifest
- 對同一需求同時發明 `query.*`、`load.*`、`fetch.*` 三種名稱空間
- 在前端實作 authoritative 的 validation / prescreen / filter 規則（必須走 handler）
- UI code 直接呼叫 `window.jet.invoke('xxx', payload)` 或 `window.chrome.webview.postMessage(...)`；一律改走 `JetApi.*`
- 同一條業務規則在 HTML/JS 與 C# handler 各寫一份（必然發散）
- Demo/test path 使用 `demo.fetch*Rows` 或 row-based `import.*` 模擬正式 pipeline
- **Bridge payload / response 攜帶超過 1000 筆明細 row**（大型 GL 母體會炸 JS 端與 postMessage；違反 `docs/jet-guide.md` §1.5）
- **在 Application/Bridge 層對 GL/TB row 集合做 LINQ 計算 V/R/Filter 規則**（必須由 DB 引擎 set-based 處理；違反 §1.5.2）

## Scale-First Contract Baseline

本章定義正式契約基準。原則是：匯入傳檔案路徑、規則執行回 summary + `resultRef`、明細一律走 keyset paging；Bridge payload / response 不搬運完整 GL/TB row set。完整背景見 `docs/jet-guide.md` §1.5。

### Ingest 契約基準

| 動作 | Payload | Response | 執行要求 |
|:---|:---|:---|:---|
| `import.gl.fromFile` | `{ filePath, fileName?, mode?, sheetName?, encoding?, delimiter? }` | `{ batchId, rowCount, addedRowCount, columns, sources }` | 後端透過 `ITabularFileReader` streaming 讀檔，直接 bulk insert 進 staging；payload 不帶 rows |
| `import.tb.fromFile` | `{ filePath, fileName?, mode?, sheetName?, encoding?, delimiter? }` | `{ batchId, rowCount, addedRowCount, columns, sources }` | 同 GL 匯入語意；payload 不帶 rows |
| `import.accountMapping.fromFile` | `{ filePath, fileName?, mode? }` | `{ batchId, rowCount, columns, fileName, importedUtc }` | 讀取科目配對來源檔並寫入 staging＋投影 target；payload 不帶 rows |
| `import.authorizedPreparer.fromFile` | `{ filePath, fileName?, mode? }` | `{ batchId, rowCount, fileName, importedUtc }` | 讀取單欄授權編製人員清單（`.xlsx`）寫入 staging＋投影 `target_authorized_preparer`；payload 不帶 rows |

Row-based `import.gl`、`import.tb`、`import.accountMapping` 只作 legacy compatibility，不屬於正式 UI、demo/test pipeline 或新程式碼的實作路徑。

### Query 契約基準（Result Reference + Paging）

| 動作 | Response 基準 | 明細讀取 |
|:---|:---|:---|
| `validate.run` | `{ stats, 四項資料驗證狀態物件, resultRef }`（Implemented，見 Validation 章節） | `query.validationDetailsPage`（Planned） |
| `prescreen.run` | `{ 九項預篩選狀態物件, resultRef }`（Implemented，見 Prescreen 章節） | `query.prescreenPage`（Planned） |
| `filter.preview` | `{ scenario: { name, count, voucherCount, previewRows } }`，`previewRows` ≤ 50；本版無狀態、無 `resultRef`（`result_filter_run` 隨匯出里程碑落地後恢復） | `query.filterPage`（Planned） |
| `filter.commit` | `{ ok, savedCount }`（Implemented；`savedRef` 隨匯出里程碑） | — |

### 分頁與匯出動作

| 動作 | Payload | Response | 用途 |
|:---|:---|:---|:---|
| `query.glPage` | `{ projectId, cursor, pageSize, sort? }` | `{ rows[], nextCursor }` | GL keyset paging（給 step 1 預覽 / step 5 工作底稿明細） |
| `query.validationDetailsPage` | `{ projectId, kind, cursor, pageSize }` | `{ rows[], nextCursor }` | 資料驗證明細分頁（`kind` 用命名登錄表的 wire key） |
| `query.prescreenPage` | `{ projectId, ruleKey, cursor, pageSize }` | `{ rows[], nextCursor }` | 預篩選規則明細分頁（`ruleKey` 用命名登錄表的 wire key） |
| `query.filterPage` | `{ projectId, scenarioId, cursor, pageSize }` | `{ rows[], nextCursor }` | 自訂篩選明細分頁 |
| `export.workpaperStream` | `{ sheets?, outputPath? }` | `{ ok, outputPath, bytesWritten, sheetStats: [{ sheetName, rowsWritten }] }` | 走 OpenXML SAX writer 直接串流寫出底稿 `.xlsx`。**`outputPath` 選填**：省略時**直接落專案目錄**,檔名 `{公司名}_{yyyyMMddHHmmss}_WorkingPaper.xlsx`(前端預設走此,匯出後以 `host.openFolder` 揭示);給定時寫到該路徑(匯出到其他位置 / 測試確定性落點)。實際落點一律回在 `response.outputPath`。`sheets` 省略=匯出全部工作表,否則只匯出指定的工作表名子集。`bytesWritten` 為寫出位元組數、`sheetStats` 為每張工作表的 `{ sheetName, rowsWritten }`(rowsWritten 為資料列數)。資料表 keyset 逐頁串流、不全載入(guide §1.5)。匯出前 handler 先以同源 materializer 把全部已存篩選情境的命中落地 `result_filter_run`(step3/4/4-1 矩陣由它即時 pivot,故惰性補算屬 handler 編排、writer 不反向依賴 Application)。**Implemented**(E1 Task 6;落專案目錄與 host.openFolder 為 GUI 驗收後修正) |

### Result Reference 概念

`resultRef = { projectId, runId, generatedUtc }`。用途：

1. 後續分頁 query 以 `runId` 鎖定同一次執行的結果（避免重跑時資料不一致）。
2. Workpaper export 以 `runId` 確認匯出的是哪一次規則執行。
3. 結果落地 `result_*` 表時 `runId` 是 partition key。
