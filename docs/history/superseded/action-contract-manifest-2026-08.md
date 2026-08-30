# JET Frontend Action Contract Manifest

這份文件是 JET 前端、WebView2 bridge 與 C# handler 三者之間 action 契約的唯一事實來源（source of truth）。任何 agent 在生成 HTML 與 UX、或修改任何 action 之前，都要先讀過這份文件。

## 初步架構階段狀態

有三組能力已經從探索階段畢業、進入正式契約階段：專案持久化（project persistence）、檔案匯入（file import）、欄位配對（mapping）。它們的契約由下方的「Project Persistence / Host / Dev Actions」章節，以及既有的 Project、Import、Mapping 章節定義。

早期還有一組以 `__jet.*` 命名的內部臨時 action（`__jet.probe`、`__jet.projectConfig`），連同它們存放在 %LOCALAPPDATA% 的 prototype blob 資料庫，現在都已退役移除。這組 action 原本只是架構驗證期的鷹架（scaffolding）。移除它們的理由是：它們會讓組態持久化多開一條旁路，這條路徑與正式的專案儲存毫無關係，徒增混亂。退役之後有兩項後果。第一，前端啟動時的 round-trip 連線檢查改走正式的 `system.ping`。第二，專案組態一律持久化在 `{root}/{projectId}/` 的 `project.json`，加上專案資料庫（SQLite 的 `jet.db`、DuckDB 的 `jet.duckdb`，或 SQL Server 單庫 `JET` 內的專案 schema）裡的 `config_*` 系列資料表；除此之外不存在任何其他組態儲存點。

驗證（`validate.run`）、預篩選（`prescreen.run`）、進階篩選（`filter.preview` / `filter.commit`）、明細分頁（`query.*Page` 系列與 `query.tagMatrix*`）與六份正式報告匯出也都已進入正式契約階段，各自的細節見對應章節。2026-07-10 的契約清倉已裁決並落地：`app.bootstrap`、`query.validationDetailsPage`、`query.filterPage`、`query.glPage` 除名；`query.prescreenPage` 與 `operation.cancel` 已實作。現行 wire 契約只以本 manifest 對應章節為準，歷史設計由 Git 保存。

## 使用原則

1. 前端要優先重用既有的 action，不要自行發明新的 action。
2. 如果 UI 確實需要新的資料或新的行為，順序是先更新這份 manifest，再去修改 `ActionDispatcher` 與相關的 handler。
3. 前端 UI（`src/JET/JET/wwwroot/`）必須服從這份文件定義的 action 名稱、payload 形狀、response 形狀，以及固定的 binding 假設。
4. `Bridge` 只負責傳輸（transport），不做任何業務判斷。
5. 業務語意由 `docs/jet-guide.md` 定義，而跨前端、bridge、handler 的資料契約由本文件定義。兩份文件如果牴觸，先回報並修正文件，不要在 UI 或 C# 裡擅自發明新的 action。

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
    "message": "<human_readable_message>",
    "field": "<optional_payload_field>"
  }
}
```

`error.field` 只有後端能把失敗明確歸屬到某個 payload 欄位時才出現；沒有欄位歸屬時省略。前端不得由 `error.code`、自由文字或其他欄位是否非空自行推測。現行唯一登錄值是 `project.create` 的 `caseName`，涵蓋名稱格式、同名資料夾、並行同名發布與 SQL Server 同名後端殘留；其他建案錯誤不帶 `field`。

## Host→Web 事件（Event Envelope）

除了一來一回的 request/response，host 也能主動推播事件給前端。這類事件是單向通知，前端收到後不需要回覆。事件信封與 response 信封的差別在於：事件信封沒有 `requestId`，改以 `event` 欄位辨識。這個設計讓不認識事件形狀的舊前端能安全忽略它，因為前端的 `receive()` 只處理帶 `requestId` 的訊息。事件信封形狀如下：

```json
{
  "event": "<namespace.event>",
  "data": {}
}
```

- 事件的發出與送達分屬兩層。Application handler 透過 `IJetEventPublisher` 這個 port 發出事件；Bridge 的 `WebViewEventPublisher` 接手負責 WebView2 的執行緒 marshal 與 JSON 序列化，它在 UI 執行緒派送，並保證依發出順序送達。如果此時 WebView 還沒就緒，事件會被靜默丟棄。這樣處理是因為事件只是 UX 提示，它不承載狀態權威；真正的狀態權威一律以 action 的 response 為準。
- 前端在 `jet-api.js` 裡用 `JetApi.on(eventName, handler)` 訂閱、用 `JetApi.off(eventName, handler)` 取消訂閱。傳輸細節同樣只存在於 `jet-api.js` 之內。
- 事件不得夾帶資料列，這條約束與 §1.5.4 對 bridge 的約束相同。

| Event | Data | 用途 |
|:---|:---|:---|
| `import.progress` | `{ kind: "gl"\|"tb", sourceNo, sourceCount, fileName, sheetName\|null, rowsRead }` | **Implemented**。`import.gl.fromFile` / `import.tb.fromFile` 串流寫入期間，每個來源每讀滿 20,000 列發送一次（`rowsRead` = 該來源累計已讀列數）；`sourceNo` 是本次 action 內一基序號，`sourceCount` 是本次整批來源數。**沒有完成事件**：整批匯入完成只以該 action 的 response 為準。前端可用 `rowsRead` ÷ `import.inspectFile` 的 `rowCountEstimate` 顯示近似進度；估計值缺席（CSV）時顯示已讀列數即可 |
| `mapping.progress` | `{ kind: "gl"\|"tb", rowsProcessed, totalRows }` | **Implemented**。`mapping.commit.gl`／`mapping.commit.tb` 的 staging→target 投影每 20,000 列與尾段回報一次。資料列不進事件；完成與錯誤仍以 action response 為準 |
| `export.progress` | `{ artifactKind, phase, sheetName|null, sheetsCompleted, rowsWritten, elapsedMilliseconds }` | **Implemented**。五種正式 `export.*` action 的非權威進度快照；Validation 三檔批次以 `artifactKind` 區分。`rowsWritten` 是同一 artifact 已寫入的累計資料列數，不因換表歸零；大型資料仍由 writer 串流，不因事件累積列集合。writer 可在昂貴查詢／spool 前先送同 shape、`rowsWritten=0` 的工作表起始 checkpoint；此時 `sheetsCompleted` 不增加。沒有百分比或完成事件；成功只以 action response 為準 |

`export.progress.artifactKind` 只允許 `validationReport`、`accountMapping`、`infReport`、`prescreenReport`、`criteriaSelectionReport`、`workingPaper`。`phase` 只允許 `preparingData`、`writingSheet`、`finalizingWorkbook`、`publishingArtifact`，並對每個 `artifactKind` 依下表單向前進。同一 action（包含 Validation 三檔批次）共用一個 monotonic stopwatch，故 `elapsedMilliseconds` 在整個 action 事件流只能不減；`sheetsCompleted` 與 `rowsWritten` 則各自在相同 `artifactKind` 內只能不減，Validation 三檔換 artifact 時各自從零開始。事件沒有 request correlation，也不承載成功狀態。

| Phase | `sheetName` | 計數語意與發布邊界 |
|:---|:---|:---|
| `preparingData` | `null` | 該 artifact 開始取得有界 metadata／facts 時發布；初始 `sheetsCompleted=0`、`rowsWritten=0` |
| `writingSheet` | 實際工作表名稱 | writer 的工作表起始、行進中列數或關表 callback。起始 checkpoint 在首筆資料查詢前送出，該表列數為 0、`sheetsCompleted` 沿用前值；工作表實際關閉後才增加完成張數。`rowsWritten` 是該 artifact 所有已觸及工作表的累計資料列數 |
| `finalizingWorkbook` | `null` | production writer 已完成全部工作表並關閉 workbook/package、artifact store 尚在完成暫存檔 flush／驗證時發布；沿用最後累計計數 |
| `publishingArtifact` | `null` | 全部相關暫存檔已關閉、flush、hash 完成，artifact store 即將進入 journal／原子發布邊界時發布；Validation 三檔在共同批次邊界各發布一次，沿用各自最後累計計數 |

取消或失敗後不得再發布後續 `export.progress`。`publishingArtifact` 只表示開始發布，不表示成功；若在 commit 前取消或發布失敗，action 仍回錯誤且既有 artifact 不變。只有成功 response 才表示匯出完成，前端不得從 phase、工作表數或列數推算完成或顯示百分比。

## Current Action Registry

### Shell / Bootstrap

| Action | Payload | Response | 用途 |
|:---|:---|:---|:---|
| `system.ping` | `{}` | `{ message, utcNow, devToolsEnabled }` | **Implemented**。基本 host 通訊檢查；前端啟動 round-trip 以此為準（取代已退役的 `__jet.probe`）。`devToolsEnabled` 標示本組建是否啟用開發輔助（Debug 組建 true、Release 組建 false）；前端只據此決定是否顯示唯讀開發面板，不提供任何使用者可見的測試案件入口 |
| `system.databaseInfo` | `{}` | `{ sqlServer: { configured, reachable, server, database, edition, productName, productVersion, engineEdition, isExpress, detail, databaseSizeMb, schemaCount, summary } }` | **Implemented**。回報本組建設定的 **SQL Server 後端身分**（去敏，**永不含密碼或整段連線字串**），供前端在「狀態與訊息」面板顯示「目前連到哪一台／哪個版本／是否 Express」。`configured`＝是否設定了 SQL Server 連線（環境變數 `JET_SQLSERVER_CONNECTION` 或 `Sql:*` 且伺服器非空；只使用本機 provider 時為 false）。`configured` 為 true 時連 **master** 探測（不連單庫，避免單庫尚未建立而誤判）並回 `reachable`＋`edition`（`SERVERPROPERTY('Edition')`，如 `Developer Edition (64-bit)`／`Express Edition (64-bit)`）、`productName`（自 `@@VERSION` 解析，如 `Microsoft SQL Server 2022`）、`productVersion`（如 `16.0.1180.1`）、`engineEdition`（int，`4`＝Express）、`isExpress`（`engineEdition==4` 或 edition 含 "Express"）。`server`＝連線目標伺服器；`database`＝單庫名（`Sql:Database` 或預設 `JET`）。`reachable` 為 false 時 `detail` 為去敏失敗原因（例 `SqlException Number=...`，不含密碼）。`databaseSizeMb`（int，控制面第七輪）＝單庫整庫大小（MB，`sys.master_files` 頁數換算，含所有專案 schema 與交易記錄檔，**非**單專案）；`schemaCount`（int）＝單庫內 `prj_%` schema 數（＝線上專案數；dbo 控制面表不計）。兩欄在未設定／不可達／庫尚未建立時一律為 `0`（探測失敗不翻動 `reachable`）。`summary` 為後端組好可直接顯示的中文摘要句（前端不另組業務文字）。此 action **無副作用**（只讀 server 屬性與 catalog，不建庫不寫入）；探測逾時設短（5 秒），失敗一律收斂、不丟例外、不阻斷啟動。未設定 SQL Server 時得到 `configured:false` 與「本機案件仍可使用 SQLite 或 DuckDB」摘要。容量預警＝**純前端呈現**：`databaseSizeMb` 超過前端可調門檻（預設 8192 MB）時於訊息面板補一則 warn，零商業邏輯、不改 wire |
| `system.whoAmI` | `{}` | `{ principal, shortName, userNumber\|null, numberSource }` | **Implemented**。回報當前使用者身分與編號，供右上角身分徽章與專案選擇畫面的身分註記。`principal`＝合格化 Windows 帳號（`網域\帳號`；未加網域的機器為 `機器名\帳號`；host 端以 `WindowsIdentity` 取得，屬 client 自報的軟性身分，資料庫端不驗證）。`shortName`＝principal 最後一個 `\` 之後的帳號短名（顯示用）。`userNumber`＝線上單庫使用者目錄 `dbo.app_user` 的編號（int，部門內唯一、永不改變）；線上可達時本 action 會自動註冊當前身分（冪等）並把編號寫入本機快取（`%LOCALAPPDATA%\JET\user-profile.json`）。`numberSource` 三值：`"online"`＝本次即時取得或註冊、`"cached"`＝線上不可達而取自本機快取、`"unavailable"`＝未設定 SQL Server 或不可達且無快取（此時 `userNumber` 為 null）。本 action **永不**因線上不可達而失敗——身分是本機事實，編號取不到就退階回報；只使用 SQLite／DuckDB 的使用者恆得 `unavailable` 或 `cached` |
| `operation.cancel` | `{ requestId }` | `{ requestId, requested }` | **Implemented**。要求取消仍在執行的 request。`requested:true` 只表示已對 active request 發出 cooperative cancellation；最終狀態以目標 request 的 response 為準。目標已完成或不存在時回 `requested:false`，本 action 自身仍 `ok:true`。屬 concurrent action，不受 exclusive gate 阻擋 |
| `app.bootstrap` | — | — | **2026-07-10 除名**：啟動握手由 `system.ping`、`system.databaseInfo`、`system.whoAmI` 與專案 actions 組成；supported actions 由 `jet-api.js` 靜態清單與 parity test 守衛，不建立重複 DTO |
| `project.loadDemo` | `{}` | `DemoProjectDto`（見下方；metadata + mapping，不含 rows） | **Implemented，Debug／不可發布 AgentGuiTest-only 的內部測試契約**。載入 deterministic fixture metadata。後端 handler 只以條件編譯納入這兩種開發組建；Release 不註冊，呼叫會得到 `bridge_error` unknown action；所有組態的 runtime 前端都沒有可見入口或 handler |
| `demo.exportGlFile` | `{}` | `{ filePath, fileName }` | **Implemented，Debug／不可發布 AgentGuiTest-only 的內部測試契約**。將 deterministic demo GL（2,000 列、2025 年度、金額＋借方旗標模式）寫成 xlsx，供測試透過既有 `import.gl.fromFile` handler pipeline 驗證。Release 不註冊此 action，runtime 前端沒有 caller |
| `demo.exportTbFile` | `{}` | `{ filePath, fileName }` | **Implemented，Debug／不可發布 AgentGuiTest-only 的內部測試契約**。將 deterministic demo TB（100 科目，借貸合計由 demo GL 推導）寫成 xlsx，供測試透過既有 `import.tb.fromFile` handler pipeline 驗證。Release 不註冊此 action，runtime 前端沒有 caller |
| `demo.exportAccountMappingFile` | `{}` | `{ filePath, fileName }` | **Implemented，Debug／不可發布 AgentGuiTest-only 的內部測試契約**。將 deterministic demo 科目配對表（demo GL 科目 → 標準化分類，含 Revenue 與 Receivables/Cash 類）寫成 xlsx。Release 不註冊此 action，runtime 前端沒有 caller |
| `demo.exportAuthorizedPreparerFile` | `{}` | `{ filePath, fileName }` | **Implemented，Debug／不可發布 AgentGuiTest-only 的內部測試契約**。將 demo 授權編製人員清單寫成單欄 xlsx。Release 不註冊此 action，runtime 前端沒有 caller |
| `demo.fetchGlRows` / `demo.fetchTbRows` / `demo.fetchAccountMappingRows` | — | — | **已退役（handler 已移除）**：呼叫會得到 `bridge_error` unknown action。demo 一律走 `demo.export*File` → `import.*.fromFile` 的 file-based 正式管線 |

`DemoProjectDto` 結構（deterministic：同版本程式每次回傳相同內容）：

```json
{
  "project": { "caseName": "範例測試案件", "projectCode": "DEMO-2025-001", "entityName": "...", "operatorId": "...",
               "periodStart": "2025-01-01", "periodEnd": "2025-12-31",
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

這個 deterministic fixture 只供程式測試，內含一份近似的 2025 年台灣國定假日清單。它的資料刻意埋進規則測試會用到的特徵：帶關鍵字的摘要、落在週末或假日的分錄、整數金額、多位不同的建立人員、以及期末之後才核准的日期。即便如此，每一張傳票本身都是借貸平衡的，TB 的借貸合計也是從 GL 推導出來的；這樣安排是為了讓完整性測試與借貸不平測試兩者都有辦法通過。`demoScenario` 是 deterministic 的測試情境 AST，由後端 `DemoDataFactory` 提供，可直接送進 `filter.preview` 或 `filter.commit`；runtime 前端不讀取、不呈現，也不提供套用入口。

### Project Persistence / Host / Dev Actions

本節描述的都是正式契約。專案採取「每個專案一個資料夾」的方式持久化：正式根目錄是 `%USERPROFILE%\JET`，可以用 `JET_PROJECTS_ROOT` 明示覆寫供開發、測試或 portable 環境使用。每個專案在 `{root}/{projectId}/` 之下有自己的資料夾，裡面放著 `project.json`（存 metadata）；SQLite 專案另有 `jet.db`，DuckDB 專案另有 `jet.duckdb`，sqlServer 專案的會計資料則落在單庫 `JET` 的專案 schema 內、資料夾只留本機 cache 與 artifacts。本機 provider 每個專案都是獨立 DB 檔，因此資料表不需要 `project_id` 欄位；SQL Server 端同理由 schema 界定（見 guide §13）。即便如此，repository 介面對外仍以 `projectId` 為參數，再由它解析對應的 DB 路徑或 schema。產品只使用目前解析出的根，不偵測、列出、讀取、搬移或刪除任何舊專案根；診斷 logs 與使用者設定仍留在 `%LOCALAPPDATA%\JET`。

**資料庫 provider 歸屬**：`project.json` 用 `databaseProvider` 欄位記錄這個專案的會計資料實際存放在哪個引擎。值為 `"sqlite"`（本地，每專案一個 `jet.db`）、`"duckdb"`（本地第二引擎，每專案一個 `jet.duckdb`；與 sqlite 同屬「一資料夾一專案」檔案式模型、共用同一套本地 repository 家族）或 `"sqlServer"`（單一資料庫 `JET`、每專案一個 `prj_xxx` schema；目標引擎 SQL Server 2022，Express／LocalDB 已淘汰硬擋；連線設定收斂於單一 `appsettings.json` 的 `Sql:*`，環境變數 `JET_SQLSERVER_CONNECTION` 為選用覆寫——見 guide §13）。舊版的 `project.json` 如果缺這個欄位，讀取時一律正規化成 `"sqlite"`，不需要做任何遷移。**雙來源管理**：sqlServer 專案的存在性與 metadata 權威移至單庫的 `dbo.project_registry`（`dbo.project_access` 以 principal＝Windows 帳號名做可見性 ACL 雛形；建立者自動獲授權），本機 `project.json` 對 sqlServer 專案**降格為快取**（開啟僅存在於伺服器的案件時自動物化）。本地（sqlite／duckdb）專案模型不變，並有明文**可攜性不變式**：`{root}/{projectId}/` 資料夾自含（`project.json`＋`jet.db` 或 `jet.duckdb`、資料夾名==projectId），不需依賴專案外的絕對路徑即可載入與續作，JET 關閉後，使用者可把整個案件資料夾冷複製到另一個符合路徑限制的 root，再照常列出。本地資料庫的 `source_file_name` 與 legacy 實體欄 `source_file_path` 都只保存來源 leaf filename；呼叫端即使把 `fileName` 傳成路徑也會先正規化，既有本地案件的 rooted 歷史值則在開啟時冪等清理。SQL Server 不屬於整夾可攜模型，仍依其 provider repository 保存既有 provenance 語意。人工冷複製只做 byte-copy，產品不提供舊根相容搬遷；本地專案無授權概念，操作者身分只用於顯示與識別（`system.whoAmI`）。所有專案組態，包括 field mapping、filter scenario、calendar、rule run 摘要，都持久化在該專案資料庫的 `config_*` 與 `result_*` 資料表以及 `project.json` 裡，因此跨 session 可以復用。除此之外，不存在任何只活在記憶體、或藏在全域旁路的組態儲存。

**儲存 JSON 的可讀性與 SQL Server 可攜性**：`row_json`、`mapping_json`、`columns_json` 以及 `project.json`，一律以未跳脫的 UTF-8 JSON 儲存，做法是用 `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`。這麼做是為了讓中文原文能直接人工檢視，不會被跳脫成一堆轉義碼。這套儲存形狀也刻意做到對 SQL Server 直接可攜。對應關係有三組：TEXT 對應 `NVARCHAR(MAX)`，可以用 `OPENJSON` 或 `JSON_VALUE` 查詢；scaled INTEGER 對應 `BIGINT`；日期統一存成 "yyyy-MM-dd" 字串，因此不依賴任何 provider 自己的日期函式。投影邏輯落在 `GlRowProjector` 與 `TbRowProjector`，兩者都是與 provider 無關的 C# 純函式。正因如此，SQL Server provider（2026-06-14 全面落地）只需要 `SqlServer*` 系列的 repository 實作、把 bulk insert 換成 `SqlBulkCopy`，Application 層完全不用動（見 guide §13）。

**`project.create` 失敗原子性**：新案資料夾先在同一 projects root 的唯一 staging directory 完整寫入 `project.json`，再以 directory rename 原子發布；寫入取消／I/O failure 不會留下半成品正式資料夾，並行同名發布也只有一方能成功。發布本機文件後，provider lifecycle 先取得同名 create ownership，再要求一把本次新取得的工作鎖，最後才 materialize provider database；`Acquired(NewlyAcquired:false)` 一律 fail closed，不讓等待中的同 principal attempt 繼承可能即將被前一失敗流程釋放的鎖。SQL Server 的 ownership 是 `dbo.project_registry` 上的 Serializable key-range transaction；schema／tables 在同一個尚未提交的 transaction 內建立，Application 完成 finalize、response 物化與 session staging 後，才以不可取消 final commit 一次發布 registry、建立者 access 與唯一一筆 `project.create` audit。final commit 前任何一步失敗，都會無條件 compare-and-clear 本次 session，依「釋放本次工作鎖 → rollback backend ownership／未提交資源 → 刪本機案件資料夾」順序補償；任一關鍵補償失敗則保留資料夾與 `project.json` 作 reconcile 錨點，且補償例外不得遮蔽原始 create failure。這條路徑不寫 `project.delete` audit，也不以刪除已提交資源模擬原子性；另一個已提交 owner 的 schema／registry 不在本次 attempt 的可刪範圍。這只收斂失敗時的資源原子性，沒有新增 action、payload、response 或 error code。

| Action | Payload | Response | 用途 |
|:---|:---|:---|:---|
| `project.create` | `{ caseName?, projectCode, entityName, operatorId, periodStart, periodEnd, lastPeriodStart?, databaseProvider? }` | `{ projectId, ok }` | **Implemented**。建立查核案件並初始化 `{root}/{projectId}/`（`project.json`，本機 provider 另有 `jet.db` 或 `jet.duckdb`；SQL Server 使用專案 schema）。`caseName`（選填，案件名稱）提供時作為 `projectId` 與資料夾名，經 `ProjectNameRules` 驗證（允許 Unicode 文字/數字/空白/`_`/`-`/括號；拒絕路徑分隔與保留字元 `/ \ : * ? < > \|`、句點、前後空白、Windows 保留名、長度>100）；同名、或（sqlServer）後端單庫內已有此案名的既有資料（**專案 schema 存在、或 `dbo.project_registry` 已有登記**）——含本機 projects 登記遺失後的孤兒殘留（資料夾被手動刪除、或案件由其他電腦建立）——皆 → `invalid_payload`，並由後端明示 `error.field:"caseName"`；後端殘留檢查在寫入任何本機檔案之前執行（provider 顯式傳入、不經路由解析快取）。sqlServer 建案成功時**於同一交易**寫入 `dbo.project_registry`（project_json 原樣入庫）、自動授權建立者 principal（`dbo.project_access`）並寫一列 `dbo.audit_log` 治理留痕（`action='project.create'`、`login_name=SUSER_SNAME()`、`host_name=HOST_NAME()`；控制面第四輪 §4）——建案失敗整筆回滾、留痕一併不落（fail loud）。未提供則回退 32-hex GUID（既有程式化/測試建立）；**UI 表單強制必填**。`databaseProvider` 建立時選定（`sqlite`/`sqlServer`/`duckdb`），之後不可改。建立順序固定為：原子發布本機 `project.json` → 取得 provider create ownership → 取得本次新工作鎖 → materialize provider 儲存體 → finalize／response／session staging → final commit；由其他持有人占用、或只取得同 principal reentrant lock 時回 `project_locked`、不得進入成功 session。失敗補償只釋放本次新鎖，並在 ownership 尚未放行等待者前先完成放鎖；response 形狀不變 |
| `project.listLocal` | `{}` | `{ projects: [{ projectId, projectCode, entityName, periodStart, periodEnd, createdUtc, currentStep, databaseProvider, lastOpenedUtc\|null }] }` | **Implemented。** 只掃描目前專案根並只回 SQLite／DuckDB 案件；損壞的 `project.json` 仍由 store 略過。此 action 不注入也不呼叫 registry、SQL Server lock 或其他線上服務，不回 `online`、`syncStatus` 或 `lock`。啟動、返回 picker 與「重新整理本機」只呼叫本 action，因此線上失聯或延遲不會阻塞第一份本機 snapshot |
| `project.list` | `{}` | `{ projects: [{ projectId, projectCode, entityName, periodStart, periodEnd, createdUtc, currentStep, databaseProvider, lastOpenedUtc\|null, syncStatus?, lock? }], online: { reachable, principal, message\|null } }` | **Implemented；只供使用者明示的「同步線上案件」操作。** 本機目前根掃描照舊（損壞的 project.json 略過）；sqlServer 部分另查 `dbo.project_registry` 中當前 principal 可見的案件並依 projectId 合併。Registry 與 SQL Server lock snapshot 在同一個 30 秒 deadline 內並行；caller 取消仍回 `operation_cancelled`，deadline／失聯則降級回應，不清空已由 `project.listLocal` 顯示的本機 snapshot。兩邊都有且可見＝`syncStatus:"synced"`、僅伺服器＝`"serverOnly"`（條目自 registry 的 project_json 產生）、僅本機快取＝`"localOnly"`（線上查無此登記、或伺服器不可達）、本機快取存在**且登記存在但當前 principal 未被授權**＝`"noAccess"`（他人案件的本機快取；`project.load`／`project.delete` 會回 `not_authorized`）；`syncStatus` 僅 sqlServer 條目有。`noAccess` 判定只對「本機有快取但不在可見清單」的條目逐筆補問一次登記存在性。伺服器不可達或 registry 超時時不失敗：`online.reachable=false`＋`message`，本機 sqlServer 快取全標 `localOnly`。`online.principal` = 當前身分（合格化 Windows 帳號 `網域\帳號`，與 `system.whoAmI` 的 `principal` 同源；資料庫端不驗證的軟性身分）。排序不變（`lastOpenedUtc ?? createdUtc` 新→舊；serverOnly 用 registry 對應欄）。同名跨來源條目允許並存（衝突於 `project.load` 物化時擋）。每個 sqlServer 條目附選填 `lock`＝當前未過期租約 `{ lockedBy, machineName, lockedUtc }`；lock 查詢失敗或超時只降級為無鎖，不改寫 registry 的 reachable 結論，也不使清單失敗 |
| `project.saveProgress` | `{ currentStep }` | `{ ok, currentStep }` | **Implemented**。保存使用者目前所在的流程步驟（0–5 的 6 步索引，對齊 Step Data Outline）到 project.json 的 `currentStep`，供 `project.load` resume「上次操作的位置」。與匯入/配對的自動推進（只前進不後退）不同，本 action 記錄使用者實際所在位置，**允許倒退**。前端於主畫面的流程導航時與結束應用程式前呼叫；流程總覽的六階段狀態選取只切換 modal 內的執行紀錄，不屬流程導航、不得呼叫本 action。無 active project → `no_active_project`；`currentStep` 缺漏或超出 0–5 → `invalid_payload` |
| `project.heartbeat` | `{}` | `{ ok }` | **Implemented（控制面第六輪＋本地檔案鎖）**。續租當前 session 專案的鎖：sqlServer 更新持鎖列的 `heartbeat_utc`（只更新自己持有的列，被他人接管則 no-op）；本地（sqlite/duckdb）由程序持有 OS 檔案 handle，不需續租，因此 heartbeat 本身為 no-op，**但不釋放 handle**。**背景保活 action、分類 concurrent**（絕不被作業 busy 閘擋住）；前端於 `project.load` 成功後以 `heartbeatSeconds` 間隔透過 `Ui.runBackground` 週期呼叫。無 active session 專案時 no-op 回 `{ ok:true }`（背景保活不報錯） |
| `project.releaseLock` | `{}` | `{ ok }` | **Implemented（控制面第六輪＋本地檔案鎖；2026-08-01 execution gate 原子協調）**。釋放當前 session 專案的鎖：sqlServer 刪除自己持有的租約列（釋放他人的為 no-op）；本地（sqlite/duckdb）關閉自己持有的排他檔案 handle。放鎖與 session 離場是同一狀態轉移：只有底層放鎖成功才清除取閘後仍為 current 的同一 projectId，不得由遲到回應清掉其後載入的新案；放鎖失敗或取消則保留 session 與心跳供安全重試。此 action 不排入全域 FIFO，也不由 dispatcher 當成一般 exclusive；handler 會**非阻塞試取與變更型作業共用的 execution gate**，把「取得閘 → 釋放鎖 → 離開 session → 還閘」包成單一原子區段。取不到即回 `operation_in_progress`，且不得呼叫 lock service 或清 session；因此匯入、驗證或其他 exclusive 作業未結束時，另一程序仍不能接手同案。前端只在實際離開專案時（回專案選擇／儲存並結束）呼叫，步驟導航**不**呼叫；原生標題列 X 也會導回同一條前端離場鏈。被擋時保留目前畫面與心跳：有可取消 requestId 才提示 `operation.cancel`，否則提示等待完成後重試；active project 下 bridge 未 ready 亦 fail closed，不得把未呼叫後端誤當成功。WebView 尚未 ready 的 host fallback 仍須先非阻塞取同一共用閘，runtime shutdown deadline 後若 request 尚未 drain 也不得先 dispose 本地鎖 handle。無 active session 時仍須先取得閘，再安全 no-op 回 `{ ok:true }`。sqlServer 崩潰未釋放者由心跳過期（`lock.timeoutSeconds`，預設 120 秒）後由他人接管；本地程序死亡時由 OS 自動關閉 handle |
| `log.append` | `{ level?, text }` | `{ ok: true }` | **Implemented**。把一則前端「狀態與訊息」持久化到當前專案資料庫的 `app_message_log` 表，每專案保留最近 500 則、舊的自動修剪。`level` 限 `"info"`／`"warn"`：缺省為 info、不分大小寫、落地為小寫，非白名單值 → `invalid_payload`。`text` 必填，trim 後上限 4000 字元、超長截斷。需要 active project（`no_active_project`）——專案選擇畫面尚無 active project，該畫面的訊息只留在前端記憶體、不持久化。這些訊息是 UX 輔助紀錄，不是審計留痕；審計留痕在 `result_*` 表與工作底稿。它是文件化的 concurrent current-project database 寫入例外，讓長作業期間的進度／取消訊息不被自身 execution gate 阻塞；唯一可寫目標是 `app_message_log`，不得觸及任何 `staging_*`、`target_*`、`config_*`、`result_*` 或其他案件資料表，並由 leaf-store DML 守衛鎖定 |
| `log.recent` | `{ limit? }` | `{ messages: [{ occurredUtc, level, text }] }` | **Implemented**。取當前專案最近持久化訊息（新→舊）。`limit` 預設 30、上限 100（超出夾擠）。供 `project.load` 後還原「狀態與訊息」面板的歷史訊息。需要 active project |
| `host.selectFile` | `{ title?, extensions? }` | `{ filePath, fileName }`（使用者取消時兩者皆 `null`，`ok` 仍為 true） | **Implemented。** 開啟原生 OpenFileDialog；`extensions` 為副檔名陣列（如 `[".xlsx"]`），host 端組成 Windows filter。有 active project 時，Application 只以 session 的 `projectId` 經 internal locator 解析 initial directory；無 active project 時不設定，沿用 Windows 預設。payload 不接受或回傳 initial directory／任意路徑欄位。host capability 走同一條 action 通道（guide §12 Host） |
| `host.selectFiles` | `{ title?, extensions? }` | `{ files: [{ filePath, fileName }] }`（使用者取消時 `files` 為空陣列，`ok` 仍為 true） | **Implemented**。多選版本的檔案對話框（`OpenFileDialog.Multiselect`），供匯入精靈一次選取多個來源檔。Initial directory 與 `host.selectFile` 相同，只能由 active session 的案件目錄 internal 解析；無 active project 不設定。獨立 action 而非 `host.selectFile` 的旗標——避免同一 action 兩種 response 形狀 |
| `host.exitApp` | `{}` | `{ ok: true }` | **Implemented**。請求 host 關閉應用程式視窗（WinForms `Close`，以 `BeginInvoke` 排入訊息佇列）。前端標準結束流程：先 `project.saveProgress` 保存進度，再成功完成 `project.releaseLock`，最後才呼叫本 action；若放鎖回 `operation_in_progress`，不得呼叫本 action。有可取消 requestId 時，畫面提示先取消進行中作業並在取消完成後重試；沒有 requestId 時，提示等待作業完成後重試。原生標題列 X 在 WebView ready 時亦先呼叫這條前端離場流程；host 真正關窗前另非阻塞試取共用 execution gate，取不到就取消本次關窗並留在畫面顯示同一組條件式取消／等待指引。視窗可能在 response 抵達前關閉，前端不得依賴本 action 的 response。純 host capability，不含業務邏輯（guide §12 Host） |
| `host.selectSavePath` | `{ defaultFileName? }` | `{ path }`（使用者取消時 `path` 為 `null`，`ok` 仍為 true） | **Implemented，但不屬正式報告流程**。有 active project 時以同一個 internal session／locator seam 把 SaveFileDialog initial directory 設為案件目錄；無 active project 不設定。Wire shape 不新增路徑欄位。保留既有通用 host capability；六份 JE Testing 報告禁止使用它，所有正式產物只能由後端落在目前專案目錄 |
| `host.openFolder` | `{ artifactId }` **或** `{ target: "projectFolder" }`（二擇一） | `{ ok }` | **Implemented。** Wire 仍可在檔案總管揭示目前專案的一個正式報告，或直接開啟目前專案資料夾；runtime 前端只保留頁首唯一的「開啟專案資料夾」，固定送 `{ target:"projectFolder" }`。報告列不提供按鈕，任何匯出成功也不得自動呼叫本 action。`artifactId` 只保留既有相容契約，只能由 project-local artifact manifest 解析相對檔名；`target:"projectFolder"` 只能由 active session 的 `projectId` 解析。caller 不得傳 `path` 或其他檔案系統路徑；兩種欄位同時出現、兩者皆缺、未知 `target` 或額外欄位均為 `invalid_payload`。沒有 active project → `no_active_project`；未知、stale 不影響揭示既有報告，但報告不存在或不屬目前專案 → `artifact_not_found` |
| `query.dataPreview` | `{ dataset, limit? }` | `{ dataset, columns, rows, totalCount, stats }` | **Implemented**。**正式版**的使用者資料預覽（細節見下方）；與 dev.db.* 的差異：只開放業務資料集白名單，不暴露任何實體資料表名 |
| `query.completenessDiffPage` | `{ cursor?, pageSize? }` | `{ rows, nextCursor }` | **Implemented**：完整性全科目差異(diff≠0)keyset 分頁,排序鍵 account_code ASC;cursor opaque、pageSize 預設 200/上限 500;rows 每列 `{accountCode,accountName,tbAmount,glAmount,diff,notInTb}` |
| `query.docBalancePage` | `{ cursor?, pageSize? }` | `{ rows, nextCursor }` | **Implemented**：借貸不平傳票(SUM(amount_scaled)≠0)keyset 分頁,排序鍵 document_number ASC;cursor opaque、pageSize 預設 200/上限 500;rows 每列 `{documentNumber,debit,credit,diff}` |
| `query.nullRecordsPage` | `{ category, cursor?, pageSize? }` | `{ rows, nextCursor }` | **Implemented**：空值／期外日期紀錄 keyset 分頁，排序鍵 entry_id ASC；`category` 必填，白名單四值 `nullAccount`／`nullDocument`／`nullDescription`／`outOfRangeDate`。前三類限 `is_effective=1`；`outOfRangeDate` 同樣限有效母體，再以核准日對專案 PeriodStart/End 判定。空白過帳日只走 `query.sourceQualityPage`，不再屬於 nullRecords。cursor opaque、pageSize 預設 200／上限 500；rows 每列 `{documentNumber,accountCode,postDate,description}` |
| `query.sourceQualityPage` | `{ cursor?, pageSize? }` | `{ rows, nextCursor }` | **Implemented**：只讀目前成功提交的 GL generation，現階段 closed finding category 只有 `nullPostDate`；不加 `is_effective=1`，因此仍揭示被 period-first projection 排除的空白過帳日。rows 每列 `{category:"nullPostDate",sourceRowNumber,sourceLabel,documentNumber,accountCode,postDate:null,description}`；`sourceRowNumber` 是來源檔內實際列號，`sourceLabel` 是檔名加選填 `[工作表]`，其餘 context 欄可為 null。以 `entry_id` ASC 作唯一 keyset，cursor 是 Base64 包裝的十進位 entry ID，pageSize 預設 200／上限 500。manual／RDE hard quality errors 仍由 `projection_failed` 的總數與有界樣本回傳並整批 rollback，不會寫成成功 generation finding；空白 RDE 合法且不是 finding |
| `query.filterHitsPage` | `{ scenarioPosition, cursor?, pageSize? }` | `{ columns, rows, nextCursor }` | **Implemented**：已存篩選情境(result_filter_run)的命中行層明細 keyset 分頁,排序鍵 entry_id ASC;`scenarioPosition` **必填**(整數;缺則 `invalid_payload`);惰性補算與 shared action gate 邊界維持不變。固定 columns 依序為 `documentNumber,lineItem,postDate,accountCode,accountName,amount,drCr,description`，其後只附該情境 typed rules 引用的 RDE union；row 固定欄後附 exact-key `customValues`。cursor opaque、pageSize 預設 200/上限 500 |
| `query.infSamplePage` | `{ cursor?, pageSize? }` | `{ columns, rows, nextCursor }` | **Implemented**：INF 抽樣(result_inf_sampling_test_sample,目前有效 validate.run)行層明細 keyset 分頁,排序鍵 entry_id ASC；固定 columns 依序為 `documentNumber,accountCode,accountName,debit,credit,postDate,approvalDate,createdBy,approvedBy,description`，其後附全部 committed GL RDE；row 固定欄後附 exact-key `customValues`。借/貸與 money RDE 均由 scaled integer 換算顯示值；cursor opaque、pageSize 預設 200/上限 500 |
| `query.tagMatrixScenarios` | `{}` | `{ scenarios: [{ position, name, voucherHitCount, rowHitCount }] }` | **Implemented (D2)**：多情境 tag 矩陣的情境摘要;由 `result_filter_run` 即時算出每個已存情境(位置 1..N)的傳票層命中數(`COUNT(DISTINCT document_number)`)與行層命中數(`COUNT(*)`)。`name` 取自 config_filter_scenario,依 position 升冪;無命中的情境列出 count=0。惰性補算同 filterHitsPage：全空且有情境時才試取共用 exclusive 閘、取閘後重讀目前 revision、落地後重取；取不到回 `operation_in_progress`。已有結果的純讀路徑仍 concurrent。需要 active project |
| `query.tagMatrixVoucherPage` | `{ cursor?, pageSize? }` | `{ rows, nextCursor }` | **Implemented (D2)**：tag 矩陣的傳票層 keyset 分頁,排序鍵 document_number ASC(排除 NULL 傳票號);由 `result_filter_run` 即時 pivot,採每頁兩段查詢(命中傳票 keyset 頁 + 同鍵範圍命中位置)。rows 每列 `{documentNumber,postDate,createdBy,voucherTotal,matchedPositions}`,`voucherTotal` 為**同一 filter revision `populationScope` 內**該傳票借方總額顯示值，不能把期外／NULL 日期列重新混回 auditPeriod revision；`matchedPositions` 為命中的情境位置陣列(1..N,有序去重);cursor opaque、pageSize 預設 200/上限 500;惰性補算同 filterHitsPage（只有空結果分支試取共用 exclusive 閘；取不到回 `operation_in_progress`，純讀仍 concurrent） |
| `query.tagMatrixRowPage` | `{ cursor?, pageSize? }` | `{ rows, nextCursor }` | **Implemented (D2)**：tag 矩陣的行層 keyset 分頁（命中傳票在**同一 filter revision `populationScope` 內**的所有行，含母體內未命中任何情境的行；auditPeriod 不得重新帶回期外／NULL 日期列）,排序鍵 entry_id ASC(排除 NULL 傳票號);由 `result_filter_run` 即時 pivot,採每頁兩段查詢(命中傳票之所有行 keyset 頁 + 同鍵範圍各行命中位置)。rows 每列 `{documentNumber,lineItem,postDate,approvalDate,createdBy,approvedBy,accountCode,accountName,amount,matchedPositions,description}`,`amount` 為該行 signed 金額顯示值(`amount_scaled` 換算),`matchedPositions` 為該行命中的情境位置陣列(有序去重;**非命中行為空 `[]`**);cursor opaque、pageSize 預設 200/上限 500;惰性補算同 filterHitsPage（只有空結果分支試取共用 exclusive 閘；取不到回 `operation_in_progress`，純讀仍 concurrent） |

> `query.tagMatrix*` 這三個 action 屬於子專案 D2（多情境 tag 矩陣），形狀已經鎖定。`tagMatrixScenarios`、`tagMatrixVoucherPage`、`tagMatrixRowPage` 都已實作。這個矩陣不會落地成新的資料表，而是由 `result_filter_run`（也就是 D1 階段落地的命中結果）即時算出來，算的是方法學的 step4（傳票層的 C1..CN 布林）與 step4-1（行層逐行的 tag）。其中 `matchedPositions` 就是命中的情境位置集合，對映到 C1..CN。cursor 是 opaque 的，壞掉的 cursor 回 `invalid_payload`（與下方的 cursor 契約相同）；pageSize 預設 200、上限 500。

上面這些 `query.*Page` 共用一套 **cursor 契約**：`cursor` 省略、null 或空字串都代表取首頁（不帶游標述詞）。但如果有傳 cursor、卻無法解碼（不是 opaque 格式），就回 `invalid_payload`。這裡 handler 刻意 fail loud，不會默默把它重置為首頁；這是為了貫徹「游標格式不符就讓 handler 報參數錯、不靜默吞掉」的原則。

| `dev.db.overview` | `{}` | `{ databasePath, databaseProvider, fileSizeBytes, engineVersion, tables: [{ name, rowCount }] }` | **Dev-only 診斷**：當前專案資料庫總覽。**僅 Debug 組建註冊**——Release 組建不註冊此 action（呼叫會得到 `bridge_error` unknown action），前端也依 `system.ping.devToolsEnabled` 隱藏開發面板。走**獨立唯讀路徑**：連線指定唯讀模式、不共用快取、不開連線池，直接讀磁碟上的 DB 檔。因此檢視**零副作用**——不建 schema、不寫入，看到的必然是已持久化的資料，而非記憶體狀態。DB 檔不存在 → `file_not_found`（不會建立）。`engineVersion` 適用 SQLite、DuckDB 與 SQL Server，不再以欄位名綁死 provider。這不是審計 workflow 的一部分，UI 置於折疊的開發面板 |
| `dev.db.tableData` | `{ tableName, limit?, offset? }` | `{ tableName, columns, rows, totalCount, limit, offset }` | **Dev-only 診斷**：分頁讀資料表，同上唯讀語意與**僅 Debug 組建註冊**。`limit` 預設 50（上限 200）；`tableName` 必須精確存在於目前 provider 的唯讀資料表 catalog（SQLite `sqlite_master`、DuckDB `information_schema.tables`、SQL Server 目前專案 schema），否則 `table_not_allowed`。`rows` 為字串化 cell 陣列，SQL NULL → JSON null。dev 工具允許 OFFSET 分頁（正式 GL 分頁仍須 keyset） |
| `dev.db.reconcile` | `{}` | `{ orphanSchemas: [schemaName], ghostRegistrations: [{ projectId, schemaName }], zombieFolders: [projectId] }` | **Dev-only 診斷（控制面第四輪 §5）**：單庫控制面三方對帳，`sys.schemas`（`prj_%`）↔ `dbo.project_registry` ↔ 本機 `projects/` 資料夾，回報三種漂移（**只列建議、不自動清理**）：`orphanSchemas`＝有 `prj_%` schema 但 registry 無對應列（舊資料殘留,建議 DROP）；`ghostRegistrations`＝registry 有列但無對應 schema（建議清 registry 列）；`zombieFolders`＝本機有 sqlServer `project.json` 但對應 schema 不存在（建議清資料夾）。schema 反查以純函式 `SqlServerProjectSchema.For` + registry.schema_name 對照（不依賴已移除的 `project_schema_map`）。對帳時**一併全清 provider 解析快取**（解「app 執行中外部刪除資料夾後快取殘留」技術債）。不需 active project（跨專案）。單庫不存在時 schema/registry 兩集合視為空（本機所有 sqlServer 資料夾即殭屍）。**僅 Debug 組建註冊**——Release 呼叫得 unknown action。唯讀、零清理副作用 |
| `dev.log.export` | `{}` | `{ ndjson }` | **Dev-only 診斷**：把診斷日誌（第三層、跨專案）的 ring buffer 完整匯出為 NDJSON，每行是一筆完整 JSON 物件:`timestamp`/`level`/`category`/`eventName`/`message`/`correlationId`/`transactionId`/`projectId`/`fields`/`exception`。記錄的內容包含 action 生命週期、SQL（完整命令加上參數 name=value、`rows_affected`、`provider`）、transaction（begin/commit/rollback 共享同一個 `transaction_id`）、exception（含 inner）與大檔 milestone。這層獨立於 result_*（審計）與 `IMessageLogStore`（UX 訊息）兩層。**僅 Debug 組建註冊**——Release 不註冊此 action（`bridge_error` unknown action），也不註冊 `RingBufferLoggerProvider`（log 變 no-op），前端則依 `system.ping.devToolsEnabled` 隱藏「DEV — 診斷日誌匯出」面板。不需 active project（跨專案）。前端以唯讀 textarea 呈現可複製的 NDJSON |

訊息面板的「複製紀錄」不擴充 `log.recent` wire response，而是先等待前端的 `log.append`
queue settle，再以 `log.recent({ limit: 100 })` 取得資料並輸出時間升冪的 TSV。首列與欄位順序固定為
`project_code<TAB>database_provider<TAB>occurred_utc<TAB>level<TAB>text`；每筆資料列的
`project_code` 與 `database_provider` 分別直接鏡射 backend-returned
`Store.project.projectCode` 與 `Store.project.databaseProvider`，其中 provider 保留 canonical
`sqlite`／`duckdb`／`sqlServer`，不轉成顯示標籤，也不由前端推算案件身分。若 formatter
沒有 active project，這兩格輸出空字串，不以 picker 清單、舊 session 或 placeholder 補值；
picker 本身仍隱藏訊息面板，且沒有 active project 時不得呼叫 `log.recent` 冒充案件紀錄。
複製流程在 queue settle 前、`log.recent` 前與 response 後都必須仍是同一個
`Store.project` session（同一 object identity 且 `projectId` 相同）；其間若切案就中止、
不寫 clipboard，並回饋「案件已切換，未複製」，不得把另一案紀錄套上舊案識別。
五欄皆把 tab／CR／LF 正規化為單一空白；`log.append`／`log.recent` payload、response、
`app_message_log` schema 與每專案 500 則修剪規則均不變。

`query.dataPreview` 細節（正式版的使用者資料預覽）：

- **用途**：讓使用者直觀看到自己目前操作的資料長什麼樣子。具體有兩個場景：欄位配對時對照欄名與實際內容是否吻合，以及進階篩選之前先掌握數值、日期、摘要的大概樣貌。要注意這是「有界預覽」，不是分頁瀏覽。完整的明細分頁屬於 `query.*Page` 那個里程碑（採 keyset 分頁），這個 action 絕對不會回傳完整母體。
- `dataset` 白名單列出可預覽的業務資料集。每個資料集的 wire key 是小駝峰命名；`JetSchemaCatalog` 仍保存正準審計名，右側常駐預覽的五個直接頁籤則逐字鏡射 Domain 顯示名權威：`GL 原始資料`、`GL 有效母體`、`TB 原始資料`、`TB 標準化資料`、`科目配對`。顯示名不改 wire key、白名單或預覽語意；其餘資料集收在「其他資料」選單。各資料集如下：
  - `"glStaging"`（**JE_PBC**）與 `"tbStaging"`（**TB_PBC**）：匯入後的來源原貌。它的 columns 是正規化後的來源欄名，與欄位配對下拉選單裡看到的一字不差；每個 cell 都是未經處理的原始字串。
  - `"glEntries"`（**JE**）：投影後的**有效** GL 分錄，也就是測試母體；只讀 `is_effective = 1`，不重新解讀期間或過帳狀態。columns 固定為 `documentNumber, lineItem, postDate, accountCode, accountName, documentDescription, amount, drCr`，這組欄位與 `filter.preview` 回傳的 previewRows 相同。其中 `amount` 是帶正負號的顯示值。
  - `"glExcludedEntries"`（**排除分錄**）：投影後未進有效母體的 GL 分錄；只讀 `is_effective = 0`。ordered columns 精確固定為 `documentNumber, lineItem, postDate, postingStatus, accountCode, accountName, documentDescription, amount, drCr, exclusionReason`；rows 逐欄對應同一順序，cell 一律為字串或 null。`exclusionReason` 是 closed wire value `period | postingStatus`；repository 必須把資料庫 storage token `period | posting_status` 轉成這兩個 wire value，不得直接外洩 `posting_status`。
  - `"tbBalances"`（**TB**）：投影後的 TB 餘額。columns 固定為 `accountCode, accountName, changeAmount`。
  - `"accountMappings"`（**ACCOUNT_MAPPING**）：最新一次匯入的科目配對檔原貌，以該批 `columns_json` 經既有 `AccountMappingColumnResolver` 辨識三欄，再從 `staging_account_mapping_raw_row` 有界讀取；因此分類仍空白的合法來源列也必須出現在預覽，不能因未投影至 `target_account_mapping` 而消失。columns 固定投影為 `accountCode, accountName, standardizedCategory`，`totalCount` 是最新匯入批次的來源列數，列序依 `row_number`。分類空白維持空白；預覽不替使用者補成 `Others`，也不改變規則只讀 target 的權威邊界。
  - `"authorizedPreparers"`（**AUTHORIZED_PREPARER**）：已匯入的授權編製人員清單。columns 固定為 `preparerName`。
  - `"dateDimension"`（**DATE_DIMENSION**）：已匯入的事務所假日與補班日，資料來源是 `staging_calendar_raw_day`。columns 固定為 `date, dayType, dayName`。其中 `dayType` 是 `holiday` 或 `makeup` 的原值；`dayName` 是假日名稱或補班說明，缺漏時為 null。各列依 `date` 升冪排序。這個資料集沒有 `stats`。
  - `"schemaOverview"`（**資料庫結構總覽**）：列出這個專案的資料表結構。它和其他資料集最大的不同在於：它的 rows 來自 `JetSchemaCatalog` 的 metadata，而不是任何一張資料表的實際列資料。它會列出 audience 標為 `DataView` 或 `StructureOnly` 的條目，標為 `Hidden` 的不列；每一列對應 catalog 的一筆登錄。columns 固定為 `canonicalName, physicalName, layer, audience, browsable`。其中 `layer` 是審計層，取值為 `Source`、`Staging`、`Target` 或 `System`；`audience` 是曝光程度，取值為 `DataView` 或 `StructureOnly`；`browsable` 在 audience 等於 `DataView` 時為 `"是"`，否則為 `"—"`。各列順序依 catalog 的宣告順序。`totalCount` 等於列出的條目數。這個資料集不依賴任何實際資料，所以永遠有列可顯示，這點與專案是否有資料無關；不過呼叫它仍然需要有 active project。它沒有 `stats`。
  - 傳入不在白名單內的值，回 `invalid_payload`。
- `limit`：預設 50、上限 100，超出上限會夾擠回 100。rows 依匯入或投影的順序取前 N 列，cell 一律是字串，SQL 的 NULL 對應成 JSON 的 null；`glEntries` 與 `glExcludedEntries` 都固定依 `entry_id ASC` 取前 N 列，`totalCount` 不受 limit 影響，絕不得回完整母體。`schemaOverview` 例外：它的列數恆等於 catalog 曝光條目數（遠少於 50），所以 limit 對它沒有作用。
- `totalCount` 是該資料集目前的總列數；`glEntries` 計有效分錄，`glExcludedEntries` 計全部排除分錄。如果一般資料集還沒有任何資料（尚未匯入或尚未投影），回傳 `{ columns: [], rows: [], totalCount: 0, stats: null }`。`glExcludedEntries` 的空狀態是明示例外：仍回上述固定 columns、`rows: []`、`totalCount: 0`，以及非 null 的零值 stats。這不是錯誤，而是讓前端顯示空狀態。`schemaOverview` 由 catalog 驅動，因此永遠有列、不會走到空狀態。
- `glEntries.stats` 是進階篩選用來把關的資訊：`{ amountAbsMin, amountAbsMax, postDateMin, postDateMax, voucherCount }`。其中金額是 `ABS(amount_scaled)` 換算後的顯示值，之所以取絕對值，是因為篩選的數值區間比較的就是絕對值；日期是 ISO 字串；`voucherCount` 是不重複的傳票數。`glExcludedEntries.stats` 精確固定為 `{ excludedByPeriodCount, excludedByPostingStatusCount }`；非空與空 dataset 都必須回非 null 物件，空 dataset 的兩值皆為 0。其餘資料集的 `stats` 一律為 null。
- 這個 action 需要 active project，否則回 `no_active_project`。權威的計算仍然落在 SQL，用的是 set-based 的 COUNT、MIN、MAX；這個 action 本身只是唯讀預覽，與規則執行無關。`dateDimension` 與 `schemaOverview` 屬於正準目錄檢視，但讀取來源不同：`dateDimension` 讀 `staging_calendar_raw_day` 這張資料表，`schemaOverview` 讀 AuditCore 的 internal `JetSchemaCatalog`，完全不查資料庫。

### Project / Import

| Action | Payload | Response | 用途 |
|:---|:---|:---|:---|
| `project.create` | `{ projectCode, entityName, operatorId, periodStart, periodEnd, lastPeriodStart?, databaseProvider? }` | `{ projectId, ok }` | 建立專案：建立 `{root}/{projectId}/` 資料夾、寫入 `project.json`、初始化專案資料庫 schema、取得專案工作鎖後設定 current session；取不到鎖回 `project_locked`，不進 session。日期格式 `yyyy-MM-dd`；`lastPeriodStart` 存為 `lastAccountingPeriodDate`；`moneyScale` 預設 10000、`roundingMode` 預設 `AwayFromZero`。`databaseProvider` 選定資料引擎，**只在建立時可選、之後不可改**（guide §13）：省略或 `"sqlite"` → 每專案一個本機 `jet.db`；`"duckdb"` → 每專案一個本機 `jet.duckdb`（本地第二引擎，與 sqlite 共用本地 repository 家族與可攜性語意）；`"sqlServer"` → 單一資料庫 `JET` 內每專案一個 `prj_xxx` schema（目標引擎 SQL Server 2022；連線見 guide §13——單一 `appsettings.json` 的 `Sql:*`、`JET_SQLSERVER_CONNECTION` 為選用覆寫；連到 Express／LocalDB 即 `sql_server_express_unsupported`）。其他值 → `invalid_payload`。選定結果記錄於 project.json。（`caseName` 等完整驗證規則見上方「Project Persistence」章節的 `project.create` 條目；本列為匯入章節的簡述，兩列描述同一個 action） |
| `project.load` | `{ projectId }` | `{ project, mapping: { gl\|null, tb\|null }, taxonomy: { revision, categories }, staleState, importState, latestRuns, filterScenarios, filterResultRef, reportArtifacts, heartbeatSeconds }` | 載入既有專案並設定 session；回傳完整 resume 狀態供重啟後接續。本機 `project.json` 存在但被截斷、無法讀取或無法解析時回 `file_read_error`，不得當成 `project_not_found`。`project.list` 仍會略過這類受損項目，避免一案使整份清單失敗。`mapping.gl = { mapping, amountMode, approvalDateMode, postingStatusPolicy, manualAutoPolicy, rdeFields, formatVersion, sourceBatchId, committedUtc }`；`mapping.tb = { mapping, changeMode, formatVersion, sourceBatchId, committedUtc }`；`importState.gl = { batchId, rowCount, columns, fileName, importedUtc, sources }`（`sources` 形狀同 `import.*.fromFile` response；`fileName` = 第一個來源檔名，向後相容）；`importState.accountMapping = { batchId, rowCount, fileName, importedUtc }`（科目配對未匯入時 null）；`importState.authorizedPreparer = { rowCount }`（授權編製人員清單未匯入時 null；清單是 name 集合、不入 import_batch，故 resume 只回 rowCount，無 fileName/importedUtc）；`importState.calendar = { holidayCount, makeupDayCount, calendarImported, nonWorkingDays, nonWorkingDaysConfigured }`（`calendarImported` 取自 project 文件內的成功匯入 marker；新案即使合法檔案落地為 0 筆也回 true，從未成功匯入則為 false；缺 marker 的舊案只以既有非零 count 向後相容推知 true。`nonWorkingDays` = 非工作日週幾集合，.NET DayOfWeek 編碼週日=0…週六=6，未設定回預設 `[0,6]`；`nonWorkingDaysConfigured` 只在 project 文件已明示保存該設定時為 true。日期維度任務的完成態鏡射這兩個 marker，不再由 count 是否大於零推測）；mapping 是否已 commit 由 `mapping.gl !== null` 推導。`latestRuns.validate` / `latestRuns.prescreen` = 最近一次且 `logicVersion` 仍相容的完整 response；摘要缺少或不符合目前版本時回 null，要求重跑。**結果失效依賴**：GL 匯入／重投影使 validation、prescreen 與 filter 命中失效；TB 匯入／重投影只使 validation 失效；taxonomy、科目配對、授權編製人員清單與行事曆 mutation 只使 prescreen 與 filter 命中失效。清除與上游改寫同交易，filter 情境定義與 revision 保留。`filterScenarios = [{ source, name, rationale, groups, populationScope, savedUtc }]`（自 `config_filter_scenario`，依 position 排序；`source` 只會是 `"kct"` 或 null，KCT resume／重存不得遺失來源；同一 revision 的 scope 必須一致）；`filterResultRef` 是全部情境共同 `savedUtc` 的 opaque revision，並帶後端權威 `logicVersion` 與正準 `populationScope`。舊定義缺新版 logicVersion 時不回放 resultRef、必須重新保存，不能用新 SQL 自動重算。`reportArtifacts = [{ artifactId, kind, fileName, generatedUtc, bytes, sha256, sourceRef, stale }]`，由專案內 `report-artifacts.json` 讀取並依目前 run／revision 計算 stale，不含絕對路徑；scope 已封裝在 revision definitions 與 `filterResultRef`，不在 artifact `sourceRef` 重複存第二份。`project.databaseProvider` 標示資料引擎（見上方 provider 歸屬說明）。**完整 response 組裝成功後才發布載入副作用**：先以不可再失敗的 response 快照完成所有 resume I/O，再戳記 `lastOpenedUtc`、設定 session；sqlServer 專案另 best-effort 回寫 registry 的 `last_opened_utc`。取鎖後至發布前任一步驟失敗或取消，都以不可取消補償釋放本次新取得的鎖，且不改 session／`lastOpenedUtc`；同案 reload 原先已持有的鎖不得被補償誤放。**sqlServer 專案的載入前置**：本機無資料夾但 registry 對當前 principal 可見時，自動把 registry 的 `project_json` 物化為目前專案根下的 `{projectId}/project.json` 再載入；目標資料夾已被其他 provider 案件占用則回 `invalid_payload`。本機文件存在時，以當前 principal 查 registry：可見即放行；登記存在但不可見回 `not_authorized`；登記不存在而 schema 存在則以本機文件補登記並授權目前開啟者，schema 也不存在則回 `project_not_found`，不得把幽靈快取復活成空 schema。**專案鎖**：授權通過後、設定 session 前以 `ILockService.AcquireAsync` 取鎖；sqlServer 走未過期租約，本地（sqlite/duckdb）走 projects 根下、由 canonical 專案路徑雜湊的排他鎖檔。由另一人／另一 JET 程序持有時回 `project_locked`，不設 session、不進 workflow、不戳 `lastOpenedUtc`；本地程序死亡時 OS 自動釋放 handle。`heartbeatSeconds` 對 sqlServer 讀自 `dbo.app_config` 的 `lock.heartbeatSeconds`，缺鍵回預設 30；本地回預設值但不需心跳續租。 |
| `accountTaxonomy.save` | `{ revision, categories:[{ categoryId?, label, ordinal, semanticRole }] }` | `{ revision, categories:[{ categoryId, label, ordinal, semanticRole, isBuiltIn }] }` | **Implemented／exclusive replace-all**。新 custom 項目省略 `categoryId`，後端產生 `custom.<32 lowercase hex>`；既有 custom 與 built-in 必須回傳原 ID。內建五項不可刪、semantic role 不可改；custom 可指定 role。revision 必須等於目前 snapshot，成功加一。使用中的 custom 不可刪；詳細 atomicity 與 validation 見下段 |
| `project.delete` | `{ projectId }` | `{ ok, projectId, message? }` | 永久刪除專案。**sqlServer 案件先做授權前置**（2026-07-07，同 `project.load` 的三路判定）：registry 有登記但當前 principal 不可見 → `not_authorized`（本機快取資料夾也保留不刪，避免「刪了快取卻誤以為刪了案件」）；登記不存在的孤兒沿現行放行（屬清理路徑）。通過後 sqlServer 案件的刪除為**單一連線、單一顯式交易**（原子,控制面第四輪 §2；單庫是 sqlServer 專案的唯一管家）：同交易內先 drop 該專案 schema 內所有表、再 `DROP SCHEMA`，寫一列 `dbo.audit_log`（`action='project.delete'`、`login_name=SUSER_SNAME()`、`host_name=HOST_NAME()`），再刪 `dbo.project_access`＋`dbo.project_registry`＋`dbo.project_lock`（控制面第六輪：刪案即清鎖，避免幽靈鎖）對應列——全成或全回滾（無「schema 已刪但登記還在」的半刪窗口；中間態失敗整筆 rollback）。單庫 `JET` 本身保留；單庫在目前伺服器不存在時優雅略過；以死鎖有限次自動重試包裹。本地案件（SQLite/DuckDB）在刪資料庫前非阻塞試取同一個本地排他檔案鎖；另一 JET 程序仍開啟時回 `project_locked`，不得刪 DB、artifact 或資料夾。sqlServer 刪案維持既有交易內清租約，不納入本地刪案檔案鎖的路線。清單即時消失、所有使用者可見性同步移除。最後刪除 `{root}/{projectId}/` 資料夾（交易外 best-effort 本機快取清理）——**此步失敗不使已成功的刪除回報失敗**（控制面第六輪折疊修正）：DB 刪除交易 commit 後才做資料夾清理，清理拋錯只在 response 附 `message`（「案件已刪除；本機快取資料夾清理失敗，可稍後手動移除。」）並照常回 `{ ok:true, projectId, message? }`，不讓已 commit 的刪除誤報失敗。schema drop 失敗殘留的孤兒由建案預檢擋同名、待 Debug-only `dev.db.reconcile` 清理（`dbo.project_schema_map` 反查表已於本輪移除,schema 反查改讀 registry.schema_name）；這個開發 action 不作為 Release 使用者的清理指引。**硬刪、不可復原**（無 soft delete/還原）；**不需 active project**（從專案選擇畫面呼叫）。`projectId` 不存在 → `project_not_found`；讀到文件後一律以文件內的 canonical `projectId` 完成授權、取鎖、刪除、session 離場與 response，Windows 路徑大小寫變體不得分裂 provider cache 或留下幽靈 session；選 sqlServer 但連線未設定（環境變數與 `Sql:*` 皆缺）→ `sql_server_not_configured`（此時資料夾保留、不刪，供修正連線後重試）。資料庫已完成邏輯刪除時，若它仍是當前 session 專案則以條件式離場清空 session；本地刪案鎖持有至資料夾清理結束後才釋放 |
| `import.gl.fromFile` | 單來源 `{ filePath, fileName?, mode?, sheetName?, encoding?, delimiter? }`；或批次 `{ mode?, sources: [{ filePath, fileName?, sheetName?, encoding?, delimiter? }] }` | `{ batchId, rowCount, addedRowCount, columns, sources }` | **Scale-aware**：從 `.xlsx` / `.csv` / `.txt` 檔案路徑串流讀 GL 寫入 `staging_gl_raw_row`；payload 不帶 rows。一次選取多個來源必須使用 `sources` 由單一 action 送出，後端以整批原子交易完成（見下方細節） |
| `import.tb.fromFile` | 單來源 `{ filePath, fileName?, mode?, sheetName?, encoding?, delimiter? }`；或批次 `{ mode?, sources: [{ filePath, fileName?, sheetName?, encoding?, delimiter? }] }` | `{ batchId, rowCount, addedRowCount, columns, sources }` | 同 GL 整批原子匯入契約，寫入 `staging_tb_raw_row` |
| `import.accountMapping.fromFile` | `{ filePath, fileName?, mode? }` | `{ batchId, rowCount, columns, fileName, importedUtc, hasAnyCategory, hasRevenue, hasCounterpart }` | **Implemented／Scale-aware**：從 `.xlsx` / `.csv` 檔案路徑串流讀科目配對表寫入 `staging_account_mapping_raw_row` 並投影 `target_account_mapping`；三個內容 bool 由匯入後 target 計算，`hasCounterpart` 代表 Receivables／Cash／Receipt in advance 至少一類；payload 不帶 rows。細節見下方 |

科目配對的 resume 形狀同步擴充：上表 `project.load.importState.accountMapping` 的完整形狀是 `{ batchId, rowCount, fileName, importedUtc, hasAnyCategory, hasRevenue, hasCounterpart }`；未匯入時仍為 null。三個 bool 一律由 `target_account_mapping` join project taxonomy 後的內容計算，不能以來源檔存在、source rowCount 或顯示 label 代替；`hasAnyCategory` 代表 target 至少一筆已解析 identity，`hasRevenue` 代表至少一筆 semantic role 為 `revenue`，`hasCounterpart` 代表至少一筆 role 為 `receivables`／`cash`／`receipt_in_advance`。custom category 只要 role 相同就參與同一判定。

Schema v7 的 additive resume 契約（本段覆蓋上表尚未展開的省略欄位）固定增加
`taxonomy: { revision, categories: [{ categoryId, label, ordinal, semanticRole, isBuiltIn }] }`、
`mappingReviewRequired` 與
`staleState: { validation, prescreen, filter }`。三個 stale 值皆為 bool，表示該類曾存在的結果
已被上游 mutation 或 v6→v7 migration 失效；「從未執行」維持 false，因此不能再由
`latestRuns.* === null` 猜 stale。報告檔仍沿用各筆 `reportArtifacts[*].stale`，不在 root 另造第二份
artifact stale。`mappingReviewRequired` 的精確規則是：任何已提交的 GL 或 TB mapping row 仍為
format v1 即為 true；不存在的 dataset 不算舊 mapping。各 dataset 成功 recommit 後只更新自己的
format，故已有 GL＋TB 的舊案必須兩者都成功 recommit 才會變 false。這個旗標不代表 schema migration
失敗，也不因 `mapping.restoreDraft` 而清除。

Filter 的「結果曾存在」以已發布的情境 revision 為準，不以 `result_filter_run` 是否非空判斷；
合法執行可以是 0 命中。因此上游 mutation 或 migration 會讓已保存的零命中 revision 同樣得到
`staleState.filter=true`，只有完全沒有情境／revision 的 never-run 案維持 false。

Schema v7 的 mapping resume shape 也固定為上表的完整欄位，不得只回 v1 的舊投影。
`mapping.gl.postingStatusPolicy` 是 `null | { acceptedValues: [string, ...], includeBlank: bool }`；
`mapping.gl.manualAutoPolicy = { manualValues: [string, ...], automaticValues: [string, ...] }`；
`mapping.gl.rdeFields = [{ fieldId, sourceColumn, label, valueType }]`，其中 `valueType` 只允許
`"text" | "date" | "money"`。`mapping.gl.approvalDateMode` 只允許
`"unmapped" | "mapped" | "sameAsPostDate"`。GL／TB 各自的 `formatVersion` 保存該筆 committed
mapping 的來源版本（`1 | 2`）；v1 row 的缺欄先依 Mapping metadata v2 的 legacy normalization
補成可顯示、待 recommit 的 options，但不得把 `formatVersion` 偽升為 2。

`accountTaxonomy.save` 的 `label`／`semanticRole` 先 trim，分別限制 1–400／1–64 字元；label
OrdinalIgnoreCase 唯一，category ID 與非負 ordinal 各自唯一。caller 不得 mint custom ID；新增必須
省略 `categoryId`。刪除 custom 前，provider 會在同一交易檢查 `target_account_mapping.category_id`
與已保存 scenario JSON 的 exact string reference；任一仍使用即回 `taxonomy_category_in_use`，整次
replace 不生效。revision 不符回 `taxonomy_revision_conflict`。成功 replace 與 taxonomy revision 推進、
prescreen／filter result reset 及 stale-state 更新同交易 commit；validation 不因 taxonomy 變更而失效。

`project.load.latestRuns.prescreen` 保留已存的 status、count、明細與 resultRef，不重新執行規則；但同一現行 logicVersion 的舊摘要若仍含已退役的工程式 `naReason`（如 `docDate`、`createBy`、`lastPeriodStart`、`import.holiday` 或英文科目分類），會先由 AuditCore resume renderer 轉成目前權威的審計員文案再回傳。只改 `naReason` 顯示字串，不補統計、不改命中集合。
| `import.authorizedPreparer.fromFile` | `{ filePath, fileName?, mode? }` | `{ batchId, rowCount, fileName, importedUtc }` | **Implemented**：從**單欄** `.xlsx` 讀授權編製人員清單寫入 `staging_authorized_preparer_raw_row` 並投影 `target_authorized_preparer`（name PK）；payload 不帶 rows。細節見下方 |
| `import.inspectFile` | `{ filePath }` | `{ fileType, worksheets, columns, encoding, delimiter }` | **Implemented**。匯入前的唯讀檔案檢視（精靈預覽用），細節見下方 |
| `import.previewFile` | `{ filePath, sheetName?, encoding?, delimiter?, limit? }` | `{ columns, sampleRows }` | **Implemented**。匯入前的逐來源有界預覽（讀標頭 + 前 N 列原貌），細節見下方 |
| `import.holiday` | `{ dates: ["yyyy-MM-dd", …] }` | `{ count }` | **Implemented；runtime 前端沒有 caller，現行只有 `AgentGuiTestFixtures` 直接 dispatch 覆蓋。** 保留的日期陣列相容路徑，寫入 `staging_calendar_raw_day`（day_type=`holiday`，replace 語意：同 type 先清後寫）；此 payload 本身不帶名稱。正式 UI 只使用 `import.holiday.fromFile`，會保存 `Holiday_Name` 至 `day_name`，並由日期維度預覽與底稿「假期假日資訊」消費。成功 replace 後保存 `calendarImported:true`（0 筆也算成功）；日期格式錯誤 → `invalid_payload`，不留 marker |
| `import.makeupDay` | `{ dates: ["yyyy-MM-dd", …] }` | `{ count }` | **Implemented；runtime 前端沒有 caller，現行只有 `AgentGuiTestFixtures` 直接 dispatch 覆蓋。** 同上，day_type=`makeup`；正式 UI 只使用 `import.makeupDay.fromFile`，成功 replace 同樣保存 `calendarImported:true` |
| `import.holiday.fromFile` | `{ filePath, fileName?, sheetName? }` | `{ count }` | **Implemented**。從 `.xlsx` 讀事務所假日表（第 1 列樣式標題、第 2 列標頭 `Date_of_Holiday/Holiday_Name/IS_Holiday`，只收 IS_Holiday=Y、缺該欄則全收）寫入 `staging_calendar_raw_day`（`day_type='holiday'`，replace、含 `day_name`）；同交易清結果。完整成功後保存 `calendarImported:true`，合法 0 筆與從未成功匯入可區分。僅 `.xlsx`。錯誤碼見下方 |
| `import.makeupDay.fromFile` | `{ filePath, fileName?, sheetName? }` | `{ count }` | **Implemented**。同上，工作表/欄 `Date_of_MakeUpday/MakeUpDay_Desc`，`day_type='makeup'`（無 IS_Holiday 過濾）；完整成功後保存同一 marker |
| `calendar.setNonWorkingDays` | `{ days: [int, …] }` | `{ ok, nonWorkingDays }` | **Implemented**。設定每案「非工作日是週幾」（.NET DayOfWeek 編碼，週日=0…週六=6），寫入 `project.json` 的 `nonWorkingDays`。未設定時預設週六、週日（canonical `[0,6]`），完全重現舊有週末判定；影響週末過帳／核准預篩選規則與週末篩選條件。只要正規化後的設定值實際改變，就清除已存的 prescreen run 與 filter 命中；重開案件後 prescreen 要求重跑，既有篩選情境則在命中查詢時依新設定惰性重算。validate run、篩選情境定義與 revision 保留。重存同一組正規化值不清除結果。空集合 = 整週皆工作日，且 UI 明示「非工作日 無」；值不在 0–6 或非整數陣列 → `invalid_payload`；需要 active project（`no_active_project`）。sqlServer 案件另同步 registry 的 project_json，供跨機 serverOnly 物化保留此設定 |

`import.gl.fromFile` 細節（`import.tb.fromFile` 同語意）：

- payload 必須使用兩種形狀之一：既有單來源形狀（根層 `filePath` 與來源選項），或批次形狀（非空 `sources` 陣列）。兩者同時出現、`sources` 不是陣列／為空，或來源元素不是 object，皆回 `invalid_payload`。單來源形狀維持相容；主前端一次選取多個檔案／工作表時只能送一個批次 action，不得逐 action 補償。
- `filePath`：每個來源的本機絕對路徑。支援 `.xlsx`、`.csv`、`.txt` 三種，其中 `.txt` 的內容當作 CSV 處理。其他副檔名會回 `unsupported_file_type`。
- `fileName`：每個來源選填。省略時預設從該來源的 `filePath` 取檔名。
- `sheetName`／`encoding`／`delimiter`：都是每個來源自己的選項；`sheetName` 只適用 `.xlsx`，`encoding`／`delimiter` 只適用 `.csv`／`.txt`，適用性與值域沿用既有單來源驗證。批次形狀不得把這些欄位放在根層，避免把某一來源的讀取設定誤套整批。
- `mode`：取 `"replace"`（預設）或 `"append"`，其他值回 `unsupported_mode`。兩種模式的行為如下：
  - `replace` 會在同一個 transaction 內，先清掉該 dataset 的舊批次、staging rows，以及 target rows 與已 commit 的 mapping；之所以連 mapping 一起清，是因為重新匯入會讓原本的配對失效，前端必須重新 commit。清完之後，再用批次中的第一個來源開立新批次，其餘來源依陣列順序 append 到同一批次。
  - `append` 則把批次中的全部來源依陣列順序加入該 dataset 現有批次。這是多來源合併的機制：一個 GL 或 TB 資料集對應一個批次，而這個批次可以由多個檔案或多個工作表組成。它的語意有幾條：
    - 如果該 dataset 還沒有任何批次，回 `no_import_batch`，因為第一個來源必須走 replace 才能開批次。
    - 這次來源的有效欄名集合必須與既有批次一致。比對與順序無關，欄序以批次的第一個來源為準。若不一致，回 `column_mismatch`，訊息會列出雙向差集，也就是「來源多出的欄」與「來源缺少的欄」。驗證分兩個階段：串流之前先比對具名標頭的集合，好讓不符的情況快速失敗；串流完成之後，再用收斂後的有效欄位集合（定義見下方）做最終檢查。任一來源任一階段不符，就 rollback 本 action 的全部來源，既有批次不受影響。
    - 附加成功與下游失效落在同一個 transaction 裡，一起發生：寫入來源紀錄與 staging rows、把批次的 `rowCount` 累加上去，同時清掉該 dataset 的 target rows 與已 commit 的 mapping。這點與 replace 相同，因此前端事後同樣要重新 commit 配對。
    - 如果任一來源有 0 筆資料列，rollback 本 action 的全部來源並回 `empty_workbook`，既有批次不受影響。
    - 對於多工作表的 `.xlsx`，每個工作表是 `sources` 中一個帶對應 `sheetName` 的來源；整個陣列只呼叫一次 action。
- **整批原子性**：同一 action 的所有來源共用一個 provider transaction。任一來源的讀取、欄位兩階段驗證、串流寫入或終檢失敗時，全部來源、來源紀錄、欄位定義、row count、target／mapping 失效與規則結果失效一併 rollback；replace 失敗保留整份舊批次，append 失敗保留 action 開始前的既有批次。錯誤訊息必須指名失敗來源（檔名及適用時的工作表）與原原因，不回部分成功 response；修正後可直接重試整批。
- Response 的 `rowCount` 是批次的總列數；`addedRowCount` 是這一次 action 全部來源合計寫入的列數。單來源 replace 時兩者相等；批次 append 時 `rowCount` 另包含 action 開始前的既有列。
- Response 的 `sources` 是批次的來源清單，依匯入順序排列：`[{ sourceNo, fileName, sheetName|null, encoding|null, delimiter|null, rowCount, importedUtc }]`。其中 `sheetName`、`encoding`、`delimiter` 記錄的是呼叫當下指定的值；若為 null，表示這個值是交由偵測鏈自動判定的。
- Response 的 `columns` 是批次的有效欄位集合，收斂規則見 guide §3.1.5。規則是：具名標頭一律保留；至於空白標頭那種 `COL_{n}` 佔位欄，只有在該欄實際出現過至少一個非空值時才保留。因此同一個工作表，`import.inspectFile` 回的 `columns`（標頭列原貌，含佔位欄）可能比匯入後批次的 `columns` 多。這是正確行為，不是資料遺失，因為被剔除的佔位欄整欄根本沒有資料。
- 匯入串流的過程中，每個來源每讀滿 20,000 列就推播一次 `import.progress` 事件；來源完成與整批成功仍只以 action response 為準，細節見「Host→Web 事件」章節。

`import.inspectFile` 細節：

- 這個 action 是唯讀的、零副作用，而且不需要 active project，所以在建立案件之前就能預覽；它不回傳任何資料列。它的用途是讓匯入精靈在真正匯入之前先看看檔案結構，作為人工把關點，及早攔下編碼或分隔符判錯的情況。檢視只讀到標頭列就停下（streaming early-exit），因此檔案多大都不影響回應時間。
- 檔案是 `.xlsx` 時，回 `{ fileType: "xlsx", worksheets: [{ name, columns, rowCountEstimate }], columns: null, encoding: null, delimiter: null }`，列出全部工作表與各自正規化後的欄名（空工作表的 `columns` 是空陣列）。`rowCountEstimate` 是推估的資料列數（nullable int），算法是取工作表 `<dimension>` 元素的末列號減去標頭列號；當 dimension 缺席或無法解析時為 null。這個 dimension 是由產生檔案的軟體維護的，可能已經過時，所以這個欄位只能用來在精靈裡顯示規模預期與進度估算，不得拿去做任何驗證或匯入判斷。實際列數一律以匯入 response 的 `rowCount` 與 `addedRowCount` 為準。
- 要注意 worksheets 的 `columns` 反映的是標頭列原貌，空白標頭會以 `COL_{n}` 佔位呈現。匯入之後批次的 `columns` 則是收斂後的有效集合，沒有資料的佔位欄已被剔除（理由見 `import.gl.fromFile` 細節）。兩者欄數可能不同，這是正確行為。
- 檔案是 `.csv` 或 `.txt` 時，回 `{ fileType: "csv", worksheets: null, columns: [...], encoding, delimiter }`。其中 `encoding` 與 `delimiter` 是偵測鏈判定出來的結果（例如 `"big5"`、`","`；單欄檔的 `delimiter` 為 null），可以直接拿去當作 `import.*.fromFile` 的覆寫參數。
- 錯誤碼與匯入相同：`file_not_found`、`unsupported_file_type`、`file_read_error`、以及 CSV 無標頭時的 `empty_workbook`。
- `sheetName`：選填，只對 `.xlsx` 有效。缺省時指第一個工作表。指定的工作表不存在時回 `sheet_not_found`；對 `.csv` 或 `.txt` 提供這個參數則回 `invalid_payload`。
- `encoding`：選填，只對 `.csv` 與 `.txt` 有效。白名單是 `"utf-8"`、`"big5"`、`"utf-16"`（不分大小寫）。缺省時走偵測鏈，順序是先看 BOM、再做嚴格的 UTF-8 驗證、最後落到 Big5（見 guide §3.1.1）。傳入非白名單值、或對 `.xlsx` 提供這個參數，回 `invalid_payload`。
- `delimiter`：選填，只對 `.csv` 與 `.txt` 有效。白名單是 `","`、`"\t"`、`";"`、`"|"`（皆為單字元字串）。缺省時走引號感知的取樣統計偵測（見 guide §3.1.1）。傳入非白名單值、或對 `.xlsx` 提供這個參數，回 `invalid_payload`。
- Response 的 `columns` 是正規化之後的標頭列：經過 trim、空白標頭命名為 `COL_{n}`、重複的標頭加上 `_2` 或 `_3` 字尾。這組欄名供 `mapping.autoSuggest` 與 `mapping.commit.gl` 使用；staging `row_json` 的 key 也用同一套名稱。
- 規模約束（scale constraint）：response 絕對不回 rows，明細只能透過後續的 paging query 取得。
- 正式資料、demo 與測試 pipeline，以及任何可能進入 scale path 的匯入，都必須走 file-based action；registry 不提供 row-based GL 匯入 fallback。

`import.previewFile` 細節：

- **用途**：讓匯入精靈在使用者按下〔開始匯入〕之前，逐個來源預覽「正規化後的標頭，加上前 N 列的原貌」。這是用來判讀「這份檔案到底有沒有標頭列」的人工把關點，因為 PBC 的原始檔常常整份都是資料、根本沒有標頭列。
- 這個 action 是唯讀的、零副作用、不需要 active project。預覽是有界的：只讀標頭再加上最多 `limit` 列就 early-exit，絕對不會回傳完整母體。讀檔、編碼偵測與標頭正規化，都沿用 `import.inspectFile` 與正式匯入的同一條處理鏈。
- `columns`：正規化後的標頭列，經過 trim、空白標頭命名為 `COL_{n}`、重複標頭加 `_2` 或 `_3` 字尾。這組欄名與 `import.inspectFile` 以及欄位配對下拉選單裡看到的一字不差。
- `sampleRows`：資料列的陣列，至多 `limit` 列。每一列是對齊 `columns` 的字串 cell 陣列，空 cell 對應成 JSON 的 null。讀取順序就是來源檔內的順序，全空的列會略過，這點與匯入一致。
- `sampleRows` 的 cell 一律對齊 `columns`（也就是標頭欄）。如果某個資料列有超出標頭範圍的儲存格（也就是 ragged 列），預覽不會把這些多出來的儲存格呈現出來。這是刻意的，因為預覽的目的是判讀標頭，不是還原檔案的完整原貌。
- `limit`：預設 10、上限也是 10，超出會夾擠回 10。
- `sheetName`、`encoding`、`delimiter` 的適用條件與白名單，都和 `import.gl.fromFile` 相同：`sheetName` 只能用於 `.xlsx`，`encoding` 與 `delimiter` 只能用於 `.csv` 與 `.txt`，違反就回 `invalid_payload`。這裡之所以開放這些覆寫參數，是因為使用者可能在精靈裡改了 CSV 的編碼或分隔符，然後重新展開預覽，這時預覽內容就應該隨之改變。
- 錯誤碼比照 inspect 與匯入：`file_not_found`、`unsupported_file_type`、`sheet_not_found`、`invalid_payload`、`file_read_error`、以及 CSV 無標頭時的 `empty_workbook`。

`import.accountMapping.fromFile` 細節：

- 科目配對表的格式固定為三欄（見 guide §2.3）：科目代號、科目名稱、標準化分類。欄位的辨識方式是：先用關鍵字去命中正規化後的標頭（「科目代號／account code」對第一欄、「科目名稱／account name」對第二欄、「分類／category」對第三欄），命不中時才退回依位次 1、2、3 對應。事務所底稿格式常用的英文標頭 `GL_NUMBER`、`GL_NAME`、`STANDARDIZED_ACCOUNT_NAME` 也已被關鍵字涵蓋；位次 1、2、3 的 fallback 仍然保留。科目配對表不經過欄位配對那一步，因為它的格式是固定的，所以匯入時直接投影：staging 寫入與 `target_account_mapping` 的投影落在同一個 transaction。
- 標準化分類依目前 project taxonomy 解析：接受 label（先 trim、再不分大小寫）或 exact category ID。**分類空白依 legacy 行為投影為 `builtin.others`**，仍保留 staging 並計入 response `rowCount`；未知非空白值回 `projection_failed`，訊息含列號、原值與目前 labels（最多前 10 筆），整批 rollback。target 的權威身分是 `category_id`；`standardized_category` 只保存五類相容投影，報表顯示 label 時 join taxonomy。
- 支援 `.xlsx` 與 `.csv`，不支援 `.txt`；其他副檔名回 `unsupported_file_type`。
- `mode`：只接受 `"replace"`（預設）。傳 `"append"` 或其他值回 `unsupported_mode`，因為科目配對表是一份整份替換的設定檔，不做多來源合併。replace 會在同一個 transaction 內，先清掉舊批次與 staging、target rows，再重建。
- 同一個科目代號如果重複出現，後出現的列覆蓋先出現的列。這是投影層的 last-wins 去重，目的是避免同一科目同時落入兩種分類，否則借貸組合的判定會出現歧義。來源若有 0 筆資料列，回 `empty_workbook`。
- 匯入成功會記錄這份設定檔，讓流程可以繼續；空白列以 `others` role 落地。「未預期借貸組合」要求實際分類中同時有 `revenue` role 與至少一個 `receivables`／`cash`／`receipt_in_advance` role，built-in 或 custom 都可滿足。重新匯入不影響既有的 GL、TB 批次與配對。

`import.authorizedPreparer.fromFile` 細節：

- 這是一份單欄的姓名清單。欄位辨識先用關鍵字去命中正規化後的標頭（`AUTHORIZED_PREPARER`、`preparer`、`編製人員`、`姓名`、`name`），命不中時退回位次 1。至少要有一欄，否則回 `projection_failed`。
- 只支援 `.xlsx`，也就是事務所的授權清單範本；其他副檔名（包含 `.csv`）回 `unsupported_file_type`。
- 姓名一律先做 TRIM 正規化，空白列略過，並做去重，因為 name 是主鍵（PK），語意上是個集合。`rowCount` 是去重之後實際落地的筆數。
- `mode`：只接受 `"replace"`（預設）。傳 `"append"` 或其他值回 `unsupported_mode`，因為授權清單是一份整份替換的設定檔，不做多來源合併。replace 會在同一個 transaction 內，先清掉舊的 staging、target rows，再重建。
- 這個 action 不寫 `import_batch` 與 `import_batch_source`，因為授權清單不納入 dataset_kind 那一套體系。response 裡的 `batchId` 只是這一次回應用的識別碼，並不持久化。
- 匯入成功之後會解鎖「非授權編製人員」這項預篩選。重新匯入時，會在同一個 transaction 內呼叫 `RuleRunResultReset`，讓依賴這份清單的規則結果失效。

`import.holiday.fromFile` / `import.makeupDay.fromFile` 細節：

- 只支援 `.xlsx`，因為事務所範本帶有樣式化的標題列；非 `.xlsx` 回 `unsupported_file_type`。標頭固定在第 2 列，第 1 列是樣式標題，後端用 reader 的 `LeadingRowsToSkip=1` 把它略過。
- 欄位辨識：日期欄以標準名的關鍵字命中（假日是 `Date_of_Holiday`、補班是 `Date_of_MakeUpday`），這一欄是必有的，缺了就回 `projection_failed`，訊息會點名缺哪一欄。名稱欄（`Holiday_Name` 或 `MakeUpDay_Desc`）與 `IS_Holiday` 欄則是選用的。
- 假日有一道過濾：當 `IS_Holiday` 欄存在時，只收值為 `Y` 的列（trim 後不分大小寫），值為 `N` 或空白的略過；若這一欄缺席，則全部收下。補班沒有這道過濾。
- 多年度的資料照單全收，不依檔名上的年度做過濾；同一天會去重，先讀到的那筆勝出。
- 只要有任何一列的日期不是 `yyyy-MM-dd` 格式，就回 `projection_failed`，訊息含列號與原值（最多前 10 筆），且整批不寫入。如果標頭存在但底下 0 筆資料列，回 `count=0`，這等於用一份空清單做 replace，也就是把該 type 清空。
- 語意是 replace，並在同一個 transaction 內清掉規則結果（`RuleRunResultReset`）。至於舊的、payload 帶 `dates` 陣列的 action，仍保留作為相容與 demo 路徑（這條路徑沒有名稱，名稱為 null）。
- 完整投影與 replace commit 後才在 project 文件保存 `calendarImported:true`；合法 0 筆也保存，任何解析／投影／replace 失敗則不保存。sqlServer 案件同時更新 registry 的 project_json 快照，讓另一台機器從 serverOnly 條目物化時不會倒退成「從未匯入」；後置快照失敗會讓 action fail-loud，重試仍是冪等 replace。

### Mapping

| Action | Payload | Response | 用途 |
|:---|:---|:---|:---|
| `mapping.autoSuggest` | `{ fields, columns }` | `{ suggested }` | 依欄位標籤與關鍵字自動配對（後端執行；只回命中的 key） |
| `mapping.restoreDraft` | `{ filePath }` | `{ formatVersion: 2, gl: { mapping, amountMode, approvalDateMode, postingStatusPolicy, manualAutoPolicy, rdeFields }, tb: { mapping, changeMode } }` | **Implemented**。唯讀解析 JET 產生的 ValidationReport 或 WorkingPaper 內版本化欄位配對 metadata，並以目前 GL／TB 匯入批次驗證相容性後回傳兩份草稿；不保存 mapping、不投影 target、不推進步驟。reader 接受 v1／v2，response 一律正規化為 v2；整份 metadata 與兩個 dataset 必須全數有效才回 response，不提供部分成功。成功的 v2 restore 另在 Application 記憶體授權「目前 project＋GL batch＋完整 canonical RDE definition」於下一次 commit 沿用該 backend stable ID；它不是持久 draft，batch 改變即失效，任意 caller-minted ID 或改寫已授權 definition 仍 fail loud |
| `mapping.commit.gl` | `{ mapping, amountMode, approvalDateMode?, postingStatusPolicy?, manualAutoPolicy?, rdeFields? }` | `{ ok, mapping, amountMode, approvalDateMode, postingStatusPolicy, manualAutoPolicy, rdeFields, batchId, projectedRowCount, warnings }` | **Implemented**。`amountMode` 必填：`signed`（單一帶號金額欄）\| `side`（金額+借貸別文字欄）\| `flag`（金額+借方旗標欄）\| `dual`（借方欄−貸方欄），對應 guide §2.1 四模式。配對 `mapping.postingStatus` 時 `postingStatusPolicy = { acceptedValues:[string,...], includeBlank:bool }` 必填；未配對時不得提供 policy。核准日 mode 固定 `unmapped \| mapped \| sameAsPostDate`：只有 `mapped` 必須且只可配 `docDate`，`sameAsPostDate` 直接沿用正規化後的過帳日。`manualAutoPolicy` 缺省為人工 `1`／自動 `0`；兩組都 trim、OrdinalIgnoreCase 去重、至少各一值且不得交集。RDE 形狀為 `{fieldId?,sourceColumn,label,valueType}`，type 限 `text \| date \| money`；新欄省略或傳 null `fieldId`，後端產生 `rde.<32 lowercase hex>`，只有目前案件已提交的 ID，或同 project／batch 經 `mapping.restoreDraft` 驗證且 definition 完全相同的既有 ID，才可沿用。RDE source 必須存在、彼此唯一且不得重用核心 mapping source；label trim 後限 1–400 UTF-16 code units。成功 response 回全部 canonical options 與 stable IDs；mapping、target、control totals、RDE definitions／values、metadata v2 與結果失效在同一 provider transaction 提交。`warnings` 為非阻斷提醒字串陣列（可空） |
| `mapping.commit.tb` | `{ mapping, changeMode }` | `{ ok, mapping, changeMode, batchId, projectedRowCount }` | 提交 TB logical mapping 並投影到 `target_tb_balance`。`changeMode` 必填：`direct`（直接變動金額）\| `debitCredit`（借方−貸方）\| `openClose`（期末−期初）\| `openCloseBySide`（期末借貸淨額−期初借貸淨額） |

投影的語意是「全部來源先驗證、保留 raw、一次裁定 effective」。後端以 streaming 方式逐列讀 staging，把金額用 `decimal` 解析後乘上專案的 `MoneyScale`，轉成 scaled integer（見 guide §1.5.3）。核准日、manual／automatic 與所有已選 RDE 都在 period／posting 分類前驗證；因此即使某列之後會被排除，mapped manual 的空白／未知代碼、非空但 malformed 的 RDE date／money，或超過 450 UTF-16 code units 的 RDE text 仍使整批失敗。空白 RDE 不寫 value row；非空 text 原樣保存，date 沿共同日期正規化，money 沿專案 MoneyScale。每列再依閉區間查核期間與 posting policy 分類，所有成功正規化的列都保留在 `target_gl_entry`。raw 恆精確分割為 effective＋兩類排除；同一交易另保存 raw／effective／excluded counts 與 effective 借貸總額。列級錯誤會掃完整批以取得精確總數，但只保留有界樣本，回 `projection_failed` 並 rollback；有效母體為零回 `empty_effective_population`，有效母體非空但借貸總額皆為 0 時回 `gl_amounts_all_zero`。成功後所有 validation／prescreen／filter／INF／tag matrix／正式報告只消費 `is_effective=1`，不重新解讀期間或 policy；`nullPostDate` 是明示的 raw source-quality 例外。

**傳票文件項次（`line_item`）的自動編號**：`mapping.commit.gl` 提交時，分兩種情況。如果 `lineID` 沒有對應到任何來源欄，投影落地之後會在同一個 transaction 內，用 `ROW_NUMBER() OVER (PARTITION BY document_number ORDER BY source_row_number)` 替每張傳票自動補上 `line_item`。如果 `lineID` 有對應，則照來源逐字寫入、不自動編號。這個 `line_item` 不供內建 validation／prescreen 規則或 INF 抽樣使用，也不是這些程序的排序鍵；INF 抽樣依 `source_row_number`。使用者仍可在進階條件篩選明確選取 `lineID` 作文字比對，因此自動補編值會影響這種明確建立的篩選條件。wire shape 維持不變，SQLite、DuckDB 與 SQL Server 三個 provider 行為等價。

`dcDebitCode` 這個 mapping 值比較特別：它是借方代碼的字面值（例如 `"D"`、`"1"`），而不是來源欄位的名稱；比對方式是先 trim 再做不分大小寫的文字相等。除了它以外，其餘的 mapping 值都必須是匯入批次 `columns` 裡確實存在的欄位名稱，否則回 `mapping_column_not_found`。

**Mapping metadata v2 格式**：ValidationReport 與 WorkingPaper 都在既有工作表 `自動化工具-檔案欄位資訊` 的隱藏 F:H 欄寫同一份 machine-readable metadata；不新增可見工作表，也不從 `GlCanonicalNames` 的部分顯示名反推。`F1` 固定為 `JET_MAPPING_METADATA`，`G1` 的新 writer 只寫十進位版本 `2`；reader 仍接受版本 `1` 並轉成待確認的 v2 draft。`H1` 是 UTF-8 語意的 JSON 字串：

```json
{
  "gl": {
    "mapping": { "docNum": "Document No", "dcDebitCode": "D" },
    "amountMode": "flag",
    "approvalDateMode": "unmapped",
    "postingStatusPolicy": null,
    "manualAutoPolicy": { "manualValues": ["1"], "automaticValues": ["0"] },
    "rdeFields": []
  },
  "tb": { "mapping": { "accNum": "Account", "amount": "Movement" }, "changeMode": "direct" }
}
```

兩個 writer 都由實際 committed mapping 產生 JSON；mapping key 只允許 `GlMappingKeys.All`／`TbMappingKeys.All`，模式只允許正準 wire 值，`dcDebitCode` 原樣保存為字面值。v2 另以與 commit 共用的 rules 鎖定 approval 三態、manual sets 與 RDE stable ID／source／label／type，encode→decode→encode 必須得到相同 canonical bytes／SHA-256。v1 缺少的新欄採唯一可由舊契約推出的 draft：已配 `docDate` 為 `approvalDateMode:"mapped"`，否則為 `"unmapped"`；未配過帳狀態故 policy 為 null；人工／自動採既定預設 `1`／`0`；RDE 為空。這只是待確認 draft，不寫回、不 commit。`mapping.restoreDraft` 只接受本機絕對 `.xlsx` 路徑，要求固定工作表、marker、支援版本、兩個 dataset、全部必填欄位與合法 JSON 都存在；再以目前最新 GL／TB import batch 驗證 core mapping 與完整 GL options，包括 RDE source 是否仍存在。舊檔缺 marker回 `mapping_metadata_missing`，marker 存在但版本／JSON／key／mode／options 不相容回 `mapping_metadata_invalid`；目前批次缺失或核心來源欄已不存在則沿用 `no_import_batch`／`mapping_column_not_found`。任一步失敗都不回部分草稿。前端只在成功 response 後一次替換 GL／TB draft，保留既有 committed snapshot，並要求使用者分別確認提交；不得連帶呼叫 `mapping.commit.*`。

`mapping.valueProfile` 現已加入 Current Action Registry、dispatcher、concurrent-read execution policy、
`JetApi` 與 runtime 配對畫面的過帳狀態／人工自動代碼設定（見 `docs/jet-frontend-description.md` §8）。
payload 精確為 `{ dataset:"gl", sourceColumn, limit? }`；response 為
`{ sourceColumn, blankCount, distinctCount, values:[{ value, count }], truncated }`。它只讀目前最新 GL
import batch；`sourceColumn` 必須與 batch column 作 Ordinal exact match，`limit` 預設 50、只允許
1–100。缺 key、null 或以 .NET `string.Trim()` 規則正規化後為空的值都計入 `blankCount`；非空值以
case-sensitive identity 分組，`distinctCount` 是全部非空 distinct 數，`values` 依 count DESC、binary
value ASC 取有界前 N，未全列出時 `truncated=true`。SQLite／DuckDB／SQL Server 都以參數化 set-based
JSON extraction／aggregation 執行，不把完整來源列載入 Application。

本計畫 additive wire 皆採**先凍結、再實作、fail-loud**。Typed dynamic rule 與本節
Validation／dynamic result column 契約均已於 2026-08-14 凍結並啟用；以下文為唯一正準：

- `validate.run` 已加入 additive root keys `populationSummary` 與 `sourceQuality`；完整性
  `partA` 已改為 eligible source 對 effective target，raw／excluded 另列。nested keys、
  nullability 與 sample shape 見 Validation 章節；validation logicVersion 已同步推進，舊摘要
  不得配目前 SQL 或 renderer。
- Typed dynamic rule（**2026-08-14 契約凍結，同日於「Typed RDE／進階條件」階段啟用**；
  本條目仍是完整驗證規則的唯一權威，`filter.preview`／`filter.commit` 已依此執行）。
  規則物件為 `{ type:"typed", fieldId, operator, ... }`，各要素如下：
  - **fieldId**：只接受目前案件 committed、`valueType` 為 `text | date | money` 的 RDE 欄位
    （`rde.<32 lowercase hex>`）。fieldId 只作 registry lookup 與 parameter binding，永不成為
    SQL identifier；核心 GL 欄位仍走既有 filter types，不建立第二套重疊語意。
  - **Operators**：text 固定為 `equals`、`notEquals`、`contains`、`notContains`、`in`、`notIn`、
    `isBlank`、`isNotBlank`；date 固定為 `on`、`before`、`onOrBefore`、`after`、`onOrAfter`、
    `between`、`isBlank`、`isNotBlank`；money 固定為 `equals`、`notEquals`、`greaterThan`、
    `greaterThanOrEqual`、`lessThan`、`lessThanOrEqual`、`between`、`isBlank`、`isNotBlank`。
  - **Operand carrier 固定三種**：single-value operators 用 `value`；`between` 用 `from`＋`to`
    （兩者必填，解析後必須 `from <= to`）；`in`／`notIn` 用 `values`（1–100 個字串的陣列，
    依該型別的正規化語意去重）。`isBlank`／`isNotBlank` 不得帶任何 operand carrier。
    `value`／`from`／`to` 必須是非 null 字串。
  - **值格式**：date 用 `yyyy-MM-dd`；money 用 invariant decimal string 並依專案 MoneyScale
    轉 scaled integer；每條 money 規則必須明示 `amountBasis:"signed"|"absolute"`（僅 money
    允許此欄）。`in`／`notIn` 最多 100 值，整情境仍受 2,000 SQL-parameter 上限。
  - **驗證**：operand 缺漏、null、型別錯誤、同時帶不適用 carrier、blank operator 帶 operand，
    或 closed token 非正準拼法，一律 `invalid_scenario`。
  - **Text 比較語意**：trim＋不分大小寫（operand 與 database value 都以 trim 後判定，同既有
    `TextMatch` 的 `UPPER(TRIM(...))` 家族）；第一版不提供 regex、free formula 或額外
    normalization 選項；儲存的原始 RDE text 不改寫，以上只屬 filter comparison semantics。
  - **Blank 語意**：blank＝沒有 `target_gl_rde_value` row。blank 不命中任何比較 operator
    （含 `notEquals`／`notContains`／`notIn`、日期與金額比較）；`isBlank` 專門命中 missing
    row、`isNotBlank` 專門命中存在合法 typed value row；負向 operator 不得把 blank 靜默納入。
  - **sameVoucher**：沿用現行同構語意——第一條仍是輸出列錨點；每條佐證規則＝同傳票有效母體
    內至少存在一列符合該規則；typed 負向 operator 只是 evidence row 自身的述詞，不重新解讀
    成 voucher-level `NOT EXISTS`；不展開 evidence rows、不複製 anchor。
  - **Read-back**：accepted raw scenario JSON 仍是 persistence authority；`project.load` 原樣
    回放（含未知屬性與原始數字 lexeme），不得從 typed projection 重建。
  - **RDE lifecycle**：GL 重投影或 RDE definition 變更時，相關 filter results 與 artifacts 依
    既有失效政策 stale；已存 definition 保留且可回放供修正。field 被移除、unknown 或 operator
    與新 type 不相容時，preview／commit 回 `invalid_scenario` 並指名 field、不發布 resultRef、
    不刪 definition。僅 label 改名（type／identity 不變）時情境仍合法，renderer 用目前
    metadata；source 或值內容改變但 type 相容時可重新執行，舊結果必須 stale；type 改變時必須
    由使用者修正 operator／operand 後才能重新保存。
  - **版本**：啟用 typed compiler 時推進 filter logicVersion；舊 definitions 可回放供修正，
    但不得沿用舊 resultRef 或直接惰性補算。
- `query.filterHitsPage`／`query.infSamplePage` response 精確為
  `{ columns, rows, nextCursor }`。`columns` 每列精確為
  `{ key, label, valueType:"text"|"date"|"money", isCustom }`；固定欄在前，custom 欄按
  committed GL RDE ordinal 升冪附加。每個 row 最後帶 `customValues` object，其 key set 必須
  精確等於 custom columns，即使值不存在也明示 JSON null；text／date 是 string|null，money
  是 number|null，不得把 sourceColumn 帶到 wire。Filter 只展開該情境 typed rules 引用欄位的
  去重 union；INF 展開全部 mapped RDE。removed／unknown／type-incompatible field fail closed，
  不得靜默省略。

`fields` 通常來自 UI 的欄位定義陣列。每個元素長這樣：

```json
{
  "key": "docNum",
  "label": "傳票號碼",
  "req": true,
  "type": "mix"
}
```

### Validation

規則命名一律使用具體名稱，不再用 V1–V4 這類代號。具體名稱在不同層有不同寫法：wire key 用 lowerCamelCase、資料表 slug 用 snake_case、UI 顯示用中文名。三者的正準對照見 guide §4 的命名登錄表。

| Action | Payload | Response | 用途 |
|:---|:---|:---|:---|
| `validate.run` | `{}` | 見下方 response 形狀 | **Implemented**。以 SQL set-based 對 `target_gl_entry`／`target_tb_balance` 執行四項資料驗證 summary，並以同一交易內的一條參數化 `GROUP BY CASE` 取得有效母體金額級距。`stats`、金額級距、完整性 GL 彙總、借貸不平與 INF 一律只讀 `is_effective=1`；raw／excluded controls 另由 `populationSummary` 明示。空白過帳日只進 `sourceQuality`，一般 nullRecords 與 `outOfRangeDate` 維持有效母體口徑。完整 response 以同一 backend renderer 存入 `result_rule_run`，INF 樣本落地 `result_inf_sampling_test_sample`；本階段已推進 validation logicVersion |
| `query.validationDetailsPage` | — | — | **2026-07-10 除名**：不同驗證結果的 row shape 不同，改由 `query.completenessDiffPage`、`query.docBalancePage`、`query.nullRecordsPage`、`query.sourceQualityPage`、`query.infSamplePage` 五支具體 action 提供全量 keyset 分頁 |

`validate.run` response（金額欄位為 scaled ÷ MoneyScale 的顯示值；權威計算只在 SQL 的 scaled BIGINT）：

```json
{
  "stats": { "glRowCount": 2000, "voucherCount": 491, "totalDebit": 0.0, "totalCredit": 0.0,
             "net": 0.0, "periodStart": "2025-01-01", "periodEnd": "2025-12-31" },
  "populationSummary": {
    "raw": { "rowCount": 2100, "totalDebit": 0.0, "totalCredit": 0.0 },
    "effective": { "rowCount": 2000, "voucherCount": 491, "totalDebit": 0.0,
                   "totalCredit": 0.0, "net": 0.0 },
    "excluded": { "rowCount": 100, "byPeriodCount": 70, "byPostingStatusCount": 30 }
  },
  "amountDistribution": {
    "bins": [
      { "key": "zero",       "count": 2000, "ecdfPct": null },
      { "key": "lt1k",       "count": 0, "ecdfPct": null },
      { "key": "1k-2k",      "count": 0, "ecdfPct": null },
      { "key": "2k-5k",      "count": 0, "ecdfPct": null },
      { "key": "5k-10k",     "count": 0, "ecdfPct": null },
      { "key": "10k-20k",    "count": 0, "ecdfPct": null },
      { "key": "20k-50k",    "count": 0, "ecdfPct": null },
      { "key": "50k-100k",   "count": 0, "ecdfPct": null },
      { "key": "100k-200k",  "count": 0, "ecdfPct": null },
      { "key": "200k-500k",  "count": 0, "ecdfPct": null },
      { "key": "500k-1M",    "count": 0, "ecdfPct": null },
      { "key": "1M-2M",      "count": 0, "ecdfPct": null },
      { "key": "2M-5M",      "count": 0, "ecdfPct": null },
      { "key": "5M-10M",     "count": 0, "ecdfPct": null },
      { "key": "gt10M",      "count": 0, "ecdfPct": null }
    ]
  },
  "completenessTest": { "status": "na", "naReason": null, "diffAccountCount": 0,
          "diffAccounts": [ { "accountCode": "", "accountName": "", "tbAmount": 0.0, "glAmount": 0.0, "diff": 0.0, "notInTb": false } ],
          "partA": {
            "eligibleSource": { "rowCount": 2000, "totalDebit": 0.0, "totalCredit": 0.0 },
            "effectiveTarget": { "rowCount": 2000, "totalDebit": 0.0, "totalCredit": 0.0 },
            "rowCountMatch": true, "amountMatch": true
          },
          "eligibility": { "isEligible": true, "reason": null } },
  "docBalanceTest": { "status": "na", "unbalancedDocumentCount": 0,
          "unbalancedDocuments": [ { "documentNumber": "", "debit": 0.0, "credit": 0.0, "diff": 0.0 } ] },
  "infSamplingTest": { "status": "V", "sampleSize": 59, "seed": 1287349021 },
  "nullRecordsTest": { "status": "na", "nullAccountCount": 0, "nullDocumentCount": 0,
          "nullDescriptionCount": 0, "outOfRangeDateCount": 0,
          "nullRows": [ { "documentNumber": "", "accountCode": "", "postDate": "", "description": "", "issues": ["account"] } ] },
  "sourceQuality": { "findingCount": 1,
          "sampleRows": [ { "category": "nullPostDate", "sourceRowNumber": 8,
            "sourceLabel": "source.xlsx [GL]", "documentNumber": null, "accountCode": null,
            "postDate": null, "description": null } ] },
  "resultRef": { "runId": "<32hex>", "generatedUtc": "<ISO-8601>", "logicVersion": "<opaque-version>" }
}
```

- **金額級距與累積分布**：`amountDistribution.bins` 是固定 15 列、固定順序的 additive 區塊：`zero, lt1k, 1k-2k, 2k-5k, 5k-10k, 10k-20k, 20k-50k, 50k-100k, 100k-200k, 200k-500k, 500k-1M, 1M-2M, 2M-5M, 5M-10M, gt10M`。每列 exact keys 為 `{ key, count, ecdfPct }`；`count` 是有效母體（`is_effective=1`）GL 分錄行數，不是傳票數。令 `x = |amount_scaled| ÷ MoneyScale`：`zero` 為 `x = 0`、`lt1k` 為 `0 < x < 1k`、`1k-2k` 為 `1k ≤ x ≤ 2k`，其後有限級距皆為「前一上界 < x ≤ 本級上界」，`gt10M` 為 `x > 10M`。Infrastructure 實際在 scaled BIGINT 域使用正負對稱界線，避免 `ABS(BIGINT min)` 溢位；門檻全數參數化，SQLite／DuckDB／SQL Server 只執行同一個 set-based `GROUP BY CASE`。
- **ECDF 語意**：分母是有效母體全部非零元分錄，累積值包含目前級距，由 AuditCore 以 decimal 計算並採 `MidpointRounding.AwayFromZero` 四捨五入到一位小數。`zero.ecdfPct` 固定為 null；非零分母為 0 時其餘 14 列也全為 null。成功的目前版本 projection 不允許有效母體為空；若讀到不完整 legacy 狀態，mapping review guard 會先 fail closed。全零有效母體則在 mapping commit 以 `gl_amounts_all_zero` rollback，不會保存成可執行 validation 的資料世代。
- **保存與版本相容**：新執行結果連同 `populationSummary`、`sourceQuality` 與 `amountDistribution` 一起保存並由 `project.load.latestRuns.validate` 原樣回放。Part A／明細集合已改變，因此 `RuleLogicVersions.Validation` 已推進至 `validation-2026-08-14-v4`；舊 summary 不得視為 current 或拿來配目前明細 SQL。對 current-version summary，後端不得為缺漏區塊捏造 0 或空陣列。
- **有界的內嵌明細（衍生資料，僅供顯示）**：`completenessTest.diffAccounts`、`docBalanceTest.unbalancedDocuments`、`nullRecordsTest.nullRows` 與 `sourceQuality.sampleRows` 各自是後端算出的有上限樣本（最多 50 筆）。空值明細的 `issues` closed values 只有 `account`、`document`、`description`、`date`；空白過帳日不在此集合。`sourceQuality.sampleRows` 的 category 固定 `nullPostDate`，`sourceRowNumber`／`sourceLabel` 非 null，context 欄可 null，`postDate` 固定 null；`findingCount` 是完整筆數，不是 sample 長度。這些明細不參與規則或抽樣計算；全量列舉走具體 page actions。
- **母體與完整性 part(a)**：`stats` 與 `populationSummary.effective` 皆代表有效母體。`populationSummary.raw` 是成功投影但尚未套期間／posting policy 的母體，`excluded` 分拆互斥的 period 與 posting-status 排除；必須滿足 `raw.rowCount = effective.rowCount + excluded.rowCount` 及 `excluded.rowCount = byPeriodCount + byPostingStatusCount`。`completenessTest.partA` 精確比較 `eligibleSource` 與 `effectiveTarget` 的 rowCount／debit／credit，所有比較都在 scaled integer 域；controls 不可得時兩個 object 與兩個 match 一起為 null，不得以 false 冒充 N/A。`completenessTest.status` 仍只由 part(b) 的適用性與差異科目數決定。
- **完整性適格裁定與後端硬閘**：`completenessTest.eligibility` 是 AuditCore 依同一 validation run 的 part(a) 控制總數核對與 part(b) 科目差異所形成的唯一權威裁定；`isEligible:true` 只在 part(a) 的列數與金額均一致、part(b) 適用且 `diffAccountCount = 0` 時成立，`reason` 在不適格時提供審計員可採取行動的說明。前端只鏡射這個 renderer 輸出，不自行重算。不存在目前有效 validation run（包含 GL／TB 重匯入或重投影已依失效矩陣清除）、摘要無法完整解析或裁定不適格時，`prescreen.run`、`filter.commit`、`export.prescreenReport`、`export.criteriaSelectionReport` 與 `export.workpaperStream` 一律 fail-closed 回 `completeness_prerequisite_failed`。本段硬閘形狀沒有第二份結果表；本階段的 logicVersion 推進來自有效母體命中集合改變。`export.validationArtifacts`、`export.accountMappingTemplate` 保留為驗證診斷／修復鏈，`filter.preview` 與唯讀查詢也不納入完整性硬閘（但舊 mapping review guard 仍適用）。
- 規則狀態的語意依 guide §5 的狀態表。`"V"` 表示已執行且有結果。`"na"` 有兩種來源：一是前置條件不足（缺欄位或缺設定），二是已經執行但 0 筆命中（這種情況 count 仍會回 0 這個數值）。`naReason` 只有在前置條件不足時才會提供文字說明。
- **前置條件**：如果 GL mapping 還沒 commit（沒有 target 投影資料），回 `no_target_data`。但 TB mapping 沒 commit 不算錯誤：這時完整性測試回 `status:"na"`，並在 naReason 裡說明它需要 TB。
- **INF 抽樣公式（可攜、可重現）**：先限有效母體（`WHERE is_effective = 1`），再依案件 seed 版本算出非負 BIGINT 排序鍵，以 `entry_id` 作唯一 tiebreak，取前 n 列；n 固定 59（母體不足時取全部；取列子句由方言提供）。新建案件寫入 `sampleSeedVersion:2`，v2 對 `source_row_number` 使用 AuditCore 正準的三輪 keyed Feistel PRF（模數 `2147483647`，輸出域為模數平方），各 provider 由 `ISqlDialect` 渲染完全相同的精確整數語意。既有案件沒有版本欄時一律視為 v1，逐字保留 `ORDER BY (source_row_number * @seed) % 2147483647, entry_id`，不重算已落地樣本；連 `sampleSeed` 都缺少的更舊案件仍回退固定值 `48271`。**seed 於建案（`project.create`）時隨機生成一次**（範圍 `[1, 2147483646]`）、與版本一起寫進 `project.json` 並終身固定；回應的 seed、action、payload 與 response shape 均不變。排序識別仍用 target 的 `source_row_number`，seed、樣本 keys 與 runId 仍落地 `result_inf_sampling_test_sample`。
- **歷史鍵相容**：schema v3 的遷移會清除 v2 時代用舊鍵（`v1`–`v4`）儲存的 `result_rule_run` 摘要。這些摘要是衍生資料，重跑就會恢復，而且結果相同，因為抽樣 seed 是固定的。遷移之後 `project.load.latestRuns` 為 null，前端顯示「未執行」。
- **結果失效按真正依賴精確切分**：GL 匯入或 GL 重投影會同時清除 validation、prescreen 與 `result_filter_run`，因為三者都讀 GL 母體。TB 匯入或 TB 重投影只清 validation；它不會清除只依賴 GL 的 prescreen 與 filter 命中。科目配對、授權編製人員清單或行事曆匯入只清 prescreen 與 filter 命中，不會清除 validation。所有清除都與上游改寫放在同一個 transaction；上游 rollback 時，清除也一併回退。情境定義與 `filterResultRef.revision` 不因資料失效而刪除，之後由 `filter.commit` 或分頁查詢的惰性補算重建命中。
- `resultRef.logicVersion` 是持久化摘要與目前規則／明細 SQL 的相容版本。只要 validation 或 prescreen 的規則集合、判定方式或明細集合語意改變，就必須推進對應版本。`project.load` 會把缺少或不符合目前版本的舊摘要視為 null，正式報告 action 也會以 `stale_result` 拒絕，避免用舊摘要搭配新 SQL 產檔。

**空白過帳日分類（2026-08-14）**：`post_date IS NULL` 刻意不套查核期間述詞，唯一 outward summary 是 `sourceQuality`、全量入口是 `query.sourceQualityPage`；它已從 `nullRecordsTest`、`query.nullRecordsPage` 與 Validation 的 V_Report 家族移出。正式 ValidationReport 保留既有 V_Report 1–6 身分與順序，另以固定 `Source_Quality` 工作表承載來源忠實性 findings，不建立 V_Report 7。

### Prescreen

Prescreen 是一般輔助訊號層：`prescreen.run` 產生可判讀的母體彙總、逐筆訊號摘要與明細入口，GA 需要留存時再由獨立入口選擇產生 Pre-screening Report；row-tag 述詞也可被 Filter / Criteria 重用。KCT A–J 才是目前實務主線。`prescreen.run` 本身仍須有適格 validation，但產品 step gate 只要求 validation 適格，GA 不先執行 prescreen 也可進入進階條件篩選。Pre-screening Report 維持要求目前有效的 prescreen run；Criteria Selection Report 與 Working Paper 只綁目前 validation 與 filter revision，不要求 prescreen run。Filter 中的 `prescreen` 類條件直接即時計算同一份 AuditCore 述詞，不讀取先前 run 的命中結果。

規則命名一律使用具體名稱，不再用 R1–R8 這類代號。正準對照見 guide §4 的命名登錄表。

| Action | Payload | Response | 用途 |
|:---|:---|:---|:---|
| `prescreen.run` | `{}` | 見下方 response 形狀 | **Implemented**。以 SQL set-based 對 `target_gl_entry`（join `staging_calendar_raw_day`／`target_account_mapping`）執行預篩選 summary；全部 row-tag 規則、編製者／科目彙總及 counterpart／frequency 子查詢都限 `is_effective=1`。KCT／自訂 filter 依 revision `populationScope` 使用同一母體；完整 response 存入 `result_rule_run`，不回完整 row list。六報表 provenance／metadata 契約啟用後，現行 logicVersion 為 `prescreen-2026-08-14-v6`；所有非 v6（含 v5）run 與 Pre-screening artifact 均 stale |
| `query.prescreenPage` | `{ ruleKey, cursor?, pageSize? }` | `{ rows: [{ entryId, documentNumber, lineItem, postDate, accountCode, accountName, documentDescription, amount, drCr }], nextCursor }` | **Implemented**。逐一檢視預篩選規則的完整命中明細；`ruleKey` 只能取可作為 filter 條件的預篩選 wire key。沿用現行參數化 SQL 述詞與本期母體，依 `entry_id` keyset 升冪分頁，不回完整母體 |

`prescreen.run` 執行前必須有目前有效且 `completenessTest.eligibility.isEligible = true` 的 validation run；否則回 `completeness_prerequisite_failed`，不執行任何預篩選 SQL 或保存結果。

`prescreen.run` response：

```json
{
  "postPeriodApproval": { "status": "V", "naReason": null, "count": 0 },
  "suspiciousKeywords": { "status": "V", "count": 0 },
  "unexpectedAccountPair": { "status": "na", "naReason": "需先匯入科目配對", "count": 0 },
  "trailingZeros": { "status": "V", "count": 0, "zerosThreshold": 6 },
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
  "rulePeriod": {
    "population": 100,
    "rules": [
      { "key": "postPeriodApproval", "naReason": null, "hitLines": 0, "hitVouchers": 0, "ratePct": 0.0 },
      { "key": "suspiciousKeywords", "naReason": null, "hitLines": 0, "hitVouchers": 0, "ratePct": 0.0 },
      { "key": "unexpectedAccountPair", "naReason": "需先匯入科目配對", "hitLines": null, "hitVouchers": null, "ratePct": null },
      { "key": "trailingZeros", "naReason": null, "hitLines": 0, "hitVouchers": 0, "ratePct": 0.0 },
      { "key": "weekendPosting", "naReason": null, "hitLines": 0, "hitVouchers": 0, "ratePct": 0.0 },
      { "key": "weekendApproval", "naReason": null, "hitLines": 0, "hitVouchers": 0, "ratePct": 0.0 },
      { "key": "holidayPosting", "naReason": null, "hitLines": 0, "hitVouchers": 0, "ratePct": 0.0 },
      { "key": "holidayApproval", "naReason": null, "hitLines": 0, "hitVouchers": 0, "ratePct": 0.0 },
      { "key": "blankDescription", "naReason": null, "hitLines": 0, "hitVouchers": 0, "ratePct": 0.0 },
      { "key": "backdatedPosting", "naReason": null, "hitLines": 0, "hitVouchers": 0, "ratePct": 0.0 },
      { "key": "nonAuthorizedPreparer", "naReason": "需先匯入授權編製人員清單", "hitLines": null, "hitVouchers": null, "ratePct": null },
      { "key": "lowFrequencyPreparer", "naReason": null, "hitLines": 0, "hitVouchers": 0, "ratePct": 0.0 },
      { "key": "lowFrequencyAccount", "naReason": null, "hitLines": 0, "hitVouchers": 0, "ratePct": 0.0 }
    ]
  },
  "concentration": {
    "status": "V", "naReason": null,
    "preparers": {
      "top": [ { "createdBy": "", "entryCount": 0, "manualCount": 0, "cumulativePct": 0.0 } ],
      "othersEntryCount": 0, "totalPreparerCount": 0, "totalEntryCount": 0, "top5SharePct": 0.0
    },
    "rareAccounts": [ { "accountCode": "", "accountName": "", "entryCount": 0 } ],
    "distinctAccountCount": 0
  },
  "positioning": {
    "aggregateGuidance": "先看依分錄編製者與較少使用科目的全期彙總；這兩項是常用的母體判讀面。",
    "signalGuidance": "逐筆命中只供初步判讀，不是高風險裁定；要形成測試範圍，請到「進階條件篩選」組合 KCT 與其他條件。",
    "reportGuidance": "Pre-screening Report 預設隨匯出底稿一併產出，這裡可以先單獨產生；不產生也不影響進階條件篩選、Criteria Selection Report 或 Working Paper。",
    "overviewGuidance": "彙總只描述母體分布；逐筆命中不等於錯誤，也不是高風險裁定；兩者都不代替審計判斷。",
    "exportDefaultGuidance": "匯出底稿時預設一併產出 Pre-screening Report；取消勾選只會少這一份，其餘報告與底稿內容都不受影響。",
    "exportPendingRunGuidance": "目前沒有可用的預篩選結果。維持勾選並按下產生，系統會先執行一次預篩選再產出這份報告；大型案件的預篩選可能需要數分鐘到十餘分鐘。"
  },
  "resultRef": { "runId": "<32hex>", "generatedUtc": "<ISO-8601>", "logicVersion": "<opaque-version>" }
}
```

備註：

- 各規則的語意見 guide §5。涵蓋的規則有：期末財報準備日之後才核准；分錄摘要含特定描述（以預設關鍵字比對）；未預期的借貸組合；金額尾端連續為零；編製者彙總；較少使用的科目；週末過帳或核准（會排除補班日）；假日過帳或核准；摘要空白；回溯過帳（過帳日早於傳票日，`voucher_date` 為 NULL 的列不命中）；非授權編製人員（`created_by` 不在授權清單裡）；低頻編製者（`created_by` 在查核期間的分錄筆數 ≤ 11）；低頻科目（`account_code` 在查核期間的分錄筆數 ≤ 11，對應已登錄的 C9）。這些規則回的 counts 一律是 summary，不是 row list。
- 哪些前置條件不足會讓規則落到 `na`，整理如下。`postPeriodApproval`：`docDate` 沒映射，或專案沒有 `lastPeriodStart`。`unexpectedAccountPair`：科目配對沒匯入，或配對表裡缺 `Revenue`，或對方分類（`Receivables`、`Cash`、`Receipt in advance`）全部缺。`creatorSummary`：`createBy` 沒映射。`weekendActivity.approvalCount` 與 `holidayActivity.approvalCount`：當 `docDate` 沒映射時為 `null`（但這兩項的 `postingCount` 永遠算得出來，因為 `postDate` 是必填的 mapping）。`holidayActivity`：還沒有假日資料。`nonAuthorizedPreparer`：授權編製人員清單沒匯入，也就是 `target_authorized_preparer` 是空的。要注意 0 筆命中同樣會標成 `na`（依 guide §5 狀態表），但 count 仍回 0。`lowFrequencyPreparer` 則沒有前置條件，永遠會跑，回的是 `{ status, count }`、沒有 `naReason`。
- `rulePeriod` 是流程總覽「預篩選規則命中分布」的全查核期間自含統計，不新增 action。`population` 是查核期間 GL 分錄總筆數；`rules` 固定依上例 13 個 canonical wire key 排序。每個適用規則的 `hitLines` 是命中分錄數，`hitVouchers` 是同一命中集合中非 null `document_number` 的去重數；兩者由 provider 在同一條參數化、set-based 規則查詢取得。`ratePct` 由後端以 `hitLines × 100 ÷ population` 計算，不由前端反推；適用且 0 命中時三者為 `0／0／0.0`，母體為 0 時 counts 仍為 0 而 `ratePct` 為 `null`。真正前置不足的規則以非 null `naReason` 表達，三個數值一律為 `null`；本列刻意不新增 `status`，因既有程序的 `status="na"` 同時涵蓋「適用但 0 命中」，不能拿來裁定 N/A。
- `concentration` 是流程總覽「集中度分析」區塊（唯讀呈現）的自含資料，不新增 action、不改任何既有欄位。整塊的適用性直接沿用 `creator_summary` 的程序裁定：該程序為 `na` 時 `concentration.status` 為 `na`，且 `preparers`、`rareAccounts`、`distinctAccountCount` 一律為 `null`（不以 0 冒充統計）；`naReason` 只在前置不足時有值。`preparers.top` 是依分錄筆數遞減的前 10 位（取自同一份 50 列上限彙總），`rareAccounts` 是依使用次數升冪的前 20 個科目。所有顯示用彙總都由後端算好：`cumulativePct` 與 `top5SharePct` 取一位小數、中點遠離零，分母固定為 `totalEntryCount`（查核期間全母體筆數，不是前 N 名合計），分母為 0 時回 `null`；`othersEntryCount` ＝ `totalEntryCount` − 前 10 位合計。`totalPreparerCount`／`totalEntryCount` 是查核期間的全母體純量（與編製者彙總同口徑、同 `COALESCE(created_by, '')` 分組鍵），一律由獨立聚合取得，不得由 50 列上限內的列相加得出。前端只鏡射與格式化，不得自行加總或換算百分比。
- `rulePeriod` 與 `concentration` 都只擴充回應，`prescreen.run` 的規則判定與命中集合未變，因此 `resultRef.logicVersion` 不推進；升版前存下的舊摘要仍會被 `project.load` 原樣回放。缺少任一新區塊時，前端各自降級為「需重新執行風險預篩選以產生此統計」空狀態，不臆造統計。
- `positioning` 是 AuditCore renderer 形成的使用者可見定位文案：彙總分析是步驟四的主要母體判讀面，逐筆命中是輔助訊號而非高風險裁定，Pre-screening Report 預設隨匯出底稿一併產出、使用者仍可在匯出面取消。`exportDefaultGuidance` 與 `exportPendingRunGuidance` 供步驟五匯出面使用，後者是「目前沒有現行 prescreen run，勾選狀態下會先補跑」的明示文案。Application 只做 camelCase wire 命名，前端只鏡射；舊摘要缺少這些加法欄位時，使用受 mirror 守衛逐字鎖定的 fallback（匯出面在完全沒有 prescreen run 時也走同一 fallback）。這不改規則判定、命中集合或 step gate，因此 `resultRef.logicVersion` 不推進。
- `creatorSummary` 與 `rareAccounts` 是彙總規則，不是逐列打標（row tag），各自最多回 50 列；`rareAccounts.accounts` 依使用次數升冪排序。`lowFrequencyAccount` 是 `rareAccounts` 的列述詞版本，可以拿來當進階篩選條件；它與低頻編製者的計數母體都固定為查核期間，子查詢不得偷偷掃全投影。
- 連續零尾數的門檻是**固定預設 6 位**（`TrailingZeroThreshold.DefaultZerosThreshold`，2026-06-20 子專案 A 取代早期的動態推估）。先把 `ABS(amount_scaled)` 以 provider 明確的向零整數商換成主單位整數，再對 `10^digits` 取模；小數位不參與、主單位整數 0 不命中。這與 legacy `@int` 對齊。`zerosThreshold` 回報本次門檻；filter 的 `customTrailingZeros` 可指定 1–12 位。
- 自訂關鍵字（legacy A2 語意）由 filter 的 `customKeywords` 條件涵蓋。Legacy A3 的「借 A 貸 B／借 A 且無貸 B／無借 A 且貸 B」對應 `specialAccountCategoryPair`；`accountPair` 是另一組精確／借方錨定／貸方錨定分析。詳見 Filter / Criteria 章節。
- **歷史鍵相容**：schema v3 的遷移會清除 v2 時代用舊鍵（`r1`–`r8`、`descNullCount`）儲存的摘要，重跑即可恢復。至於 `config_filter_scenario` 裡的舊 `prescreenKey`，遷移會逐一翻譯成新鍵並保留下來，對照是：`r1→postPeriodApproval`、`r2→suspiciousKeywords`、`r4→trailingZeros`、`r7post→weekendPosting`、`r7doc→weekendApproval`、`r8post→holidayPosting`、`r8doc→holidayApproval`、`descNull→blankDescription`。

### Filter / Criteria

KCT A–J 是進階篩選的實務主線；GA 也可組合自訂條件或重用 prescreen row-tag 述詞。三者都收斂到下列同一份 AST、同一個 `auditPeriod` 有效母體與同一條後端編譯／執行路徑；「重用 prescreen」指重用述詞，不是把 `result_rule_run` 或某次 `prescreen.run` 的結果當成輸入。

| Action | Payload | Response | 用途 |
|:---|:---|:---|:---|
| `filter.preview` | `{ populationScope?, scenario }` | `{ scenario: { name, populationScope, count, voucherCount, previewRows } }` | 後端將條件 AST 轉成**參數化 SQL**（識別字只來自欄位白名單）交由 DB set-based 評估；`previewRows` ≤ 50（`{ documentNumber, lineItem, postDate, accountCode, accountName, documentDescription, amount, drCr }`）。`populationScope` 省略／null／空白時正規化為唯一正準值 `auditPeriod`；明示 `auditPeriod` 亦合法，其他值或錯誤 JSON 型別回 `invalid_payload`。**preview 為無狀態查詢**：本 action 不落地結果；命中落地屬 `filter.commit` 與分頁查詢的惰性補算 |
| `query.filterPage` | — | — | **2026-07-10 除名**：由已實作的 `query.filterHitsPage` 取代；後者以已存情境 position 與 opaque cursor 提供同一份完整命中明細 |
| `filter.commit` | `{ populationScope?, scenarios }` | `{ ok, savedCount, scenarios: [{ source, name, rationale, groups, populationScope, savedUtc }], resultRef: { revision, generatedUtc, logicVersion, populationScope } }` | 保存情境定義到 `config_filter_scenario`：replace-all 語意、上限 **10** 個、名稱不可重複、每個情境重新驗證；scope 固定為 `auditPeriod`，後端把正準 `populationScope` 與 `logicVersion` 寫入每份 definition JSON，不相信 scenario 內同名欄位。除這兩個後端權威欄位外，持久化以 caller 的 raw scenario JSON 為準，未知 properties 與原始數字 lexeme 不從 typed projection 重建。definition replace-all、清除舊 hits 與依同一 AST set-based materialize 新 `result_filter_run` 必須共用同一 provider transaction；該 transaction 內任一步失敗或取消即全部 rollback，DB 保留 action 開始前的 revision 與 hits，且不推進步驟。transaction 成功後才把專案步驟推進到至少 Step 5；同批情境共用一個 `savedUtc`，其 UTC round-trip 字串即 opaque `revision`。`project.load` 只在全部情境版本相容、revision 與 scope 一致時回傳 `filterResultRef`。response 的 `scenarios` 與 `project.load.filterScenarios` 共用同一後端 renderer，會回傳 KCT 留白後的正準替補名稱／動機與 canonical source，前端保存成功後直接採用，避免同一 session 先顯示空白、reload 後才改名。既有 `allProjected` definition 可回放供使用者重存，但一律視為 stale、不發布 resultRef，也不得被 direct query／報告重新執行 |

`filter.commit` 在解析或保存任何 scenario 前先套用完整性硬閘；不適格時回 `completeness_prerequisite_failed`，既有情境與命中結果均不得被改寫。`filter.preview` 是不落地的診斷查詢，刻意不納入此閘。

兩個 action 在執行 KCT D／E／G／J 之前都以目前已提交的 GL mapping 組裝同一份 validation context；缺少必要欄位時以 `invalid_scenario` 在 SQL preview、情境 replace-all 或 hits materialize 之前拒絕，訊息以「缺少前置資料」指名未配對的審計欄位。D 需要「人工/自動分錄」，E 需要「傳票建立人員」，G 需要「傳票摘要」，J 同時需要「傳票建立人員」與「傳票核准人員」。欄位可用性由 Domain `JetFieldCatalog` 的 semantic field／mapping slot 關係判定，不以 target 欄位存在或 NULL 值猜測。

下面是 `scenario` 條件 AST 的 schema。前端的 Query Builder 只負責組裝這份 JSON，它本身不評估任何規則：

```json
{
  "source": "kct（KCT 卡建立的情境；一般自訂情境省略）",
  "name": "情境名稱（一般情境必填；source:kct 時選填）",
  "rationale": "篩選動機說明（一般情境必填；source:kct 時選填；保留到工作底稿）",
  "groups": [
    {
      "join": "AND",
      "matchScope": "row | sameVoucher",
      "rules": [
        {
          "join": "AND",
          "type": "prescreen | text | textSet | dateRange | numRange | drCrOnly | manualAuto | accountPair | specialAccountCategoryPair | customKeywords | customTrailingZeros | customPreparerEntryCount | customAccountEntryCount | revenueDebitNearQuarterEnd | revenueWithoutNormalCounterpart | manualRevenueEntry | trailingDigits | preparerEqualsApprover | typed",
          "prescreenKey": "postPeriodApproval | suspiciousKeywords | unexpectedAccountPair | trailingZeros | weekendPosting | weekendApproval | holidayPosting | holidayApproval | blankDescription | backdatedPosting | nonAuthorizedPreparer | lowFrequencyPreparer | lowFrequencyAccount",
          "field": "docNum | lineID | accNum | accName | description | jeSource | createBy | approveBy | postDate | docDate | voucherDate | amount",
          "keywords": "逗號分隔的關鍵字",
          "values": ["textSet 的第一個文字值", "textSet 的第二個文字值"],
          "mode": "contains | exact | notContains | notExact",
          "normalization": "preserve | removeAsciiSpaces",
          "from": "",
          "to": "",
          "drCr": "debit | credit",
          "isManual": "true | false",
          "pairMode": "exact | debitAnchor | creditAnchor (accountPair) ｜ drAndCr | drNotCr | notDrCr (specialAccountCategoryPair)",
          "debitCategoryIds": ["builtin.receivables", "custom.<32 lowercase hex>"],
          "creditCategoryIds": ["builtin.revenue"],
          "debitCategory": "legacy 單選相容輸入：Receivables | Cash | Receipt in advance | Revenue | Others",
          "creditCategory": "legacy 單選相容輸入：Receivables | Cash | Receipt in advance | Revenue | Others",
          "digits": 3,
          "maxEntries": 11,
          "windowDays": 5,
          "fieldId": "typed 條件的 committed RDE 欄位身分：rde.<32 lowercase hex>",
          "operator": "typed 條件的 closed operator token（per-type 集合見 Mapping 段凍結條目）",
          "value": "typed single-value operand（原始 wire 字串）",
          "amountBasis": "signed | absolute（typed money 條件必填，僅 money 允許）"
        }
      ]
    }
  ]
}
```

`typed` 條件的 `from`／`to`（between）與 `values`（in／notIn，1–100 個字串）重用上列同名欄位，
但 operand 一律是非 null 字串（金額不經前端換算、日期用 `yyyy-MM-dd`）；完整 operator matrix、
operand carrier、blank 語意、text 正規化與 RDE lifecycle 的唯一權威是 Mapping 段的
「Typed dynamic rule（2026-08-14 契約凍結）」條目。

AST 語意與安全約束：

- **populationScope（revision-level）**：唯一正準 token 仍是 `auditPeriod`，但目前版本的權威語意是 projection 已裁定的有效母體 `is_effective=1`；它包含閉區間期間與選用 posting policy，不允許 query 再以日期重算一份近似集合。外層命中列、同傳票 counterpart、編製者／科目頻率子查詢、KCT revenue counterpart、tag matrix 的完整傳票列與 voucherTotal 全部使用同一述詞；validation、Prescreen、INF 與正式報表亦同。payload 省略／null／空白或明示 `auditPeriod` 都正規化為此值；`allProjected` 與其他值對新 preview／commit 一律回 `invalid_payload`。本階段已推進 filter logicVersion；舊 definition 只可回放供重存，不得以新 SQL 惰性補算。
- **結合律**：不論是同一群組內的規則之間，還是群組與群組之間，結合都採左折疊、累積加括號，也就是 `((c1 OP c2) OP c3)` 這種形式。第一條規則、以及第一個群組的 `join` 會被忽略。`join` 的值不分大小寫，後端會先正規化成大寫再參與左折疊；缺欄、null 或空白字串一律當成 `AND`，這是為了向後相容既有落地資料與省略欄位的編碼；非字串的 JSON 值（例如數字或布林）也視同缺欄，這沿用解析層對所有字串欄位的既有慣例。但只要 `join` 填了非空白的值，就必須是 `AND` 或 `OR` 之一——任何其他值都會讓驗證失敗、回 `invalid_scenario`，而不是靜默當成 `AND`（2026-07-06 收緊；未知值過去會被靜默容忍，曾掩護一次前端「畫面顯示與實際送出不一致」的回歸）。這條檢查涵蓋每一個 `join` 欄位，包括左折疊時會被忽略的第一條規則與第一個群組，因為被忽略的位置若出現未知值，同樣代表前端組裝出了問題。
- **群組比對範圍**：`matchScope` 省略、null、非字串、空白或明示 `row` 時，維持既有「同一 GL 列逐邊左折疊」語意。明示 `sameVoucher` 時，第一條規則是輸出列錨點；後續每條規則各自要求同一 `document_number`、同一 `auditPeriod` 母體內至少存在一列符合條件，允許由不同列滿足，但不得把那些佐證列擴張為命中列，也不得因佐證列多筆而複製錨點。`sameVoucher` 至少要有兩條規則，所有規則的 `join` 都必須是 `AND`（第一條仍須合法但不參與結合），不接受群組內 OR；群組本身仍依自己的 `join` 與其他群組逐邊左折疊。未知的非空白 `matchScope` 會回 `invalid_scenario`。
- **closed token 正準拼法**：`type`、`mode`、`matchScope`、`normalization` 的非空白字串必須逐字等於上列 closed value；前後加空白不是正準拼法，會在保存前回 `invalid_scenario`。理由是 definition 會保留原始 wire 供正式 read-back／replay，若執行端先 trim、保存端卻保留 padded spelling，會造成畫面與實際 SQL 語意分裂。省略、null、非字串或全空白仍依各欄既有預設處理；`join` 另依上一條既有的不分大小寫／trim 規則，不套用此限制。
- **混合 join 與呈現**：合法的 AND／OR 可以在同一群組或不同群組的邊上混用，後端仍依上一條逐邊左折疊；validator 不要求齊一，也不會改寫既存 JSON。現行主前端在編輯時會把組內規則收斂為一個群組運算子，並把可編輯群組收斂為一個情境運算子。只有殘餘 raw AST 在忽略第一條規則後的有效邊同時含 AND 與 OR 時，前端 read-back 與 Application `FilterConditionRenderer` 才依每條有效邊精確左折疊並加全形括號；判定會忽略 join 的前後空白與大小寫。Uniform group 仍沿用既有單一群組運算子輸出，不藉此正規化歷史 lowercase join。這是呈現算法，不是 SQL 執行規則；命中權威仍是 AuditCore 編譯出的逐邊左折疊 SQL。
- **source（KCT 方法學來源）**：使用者選取任何 KCT 卡時，主前端送 `source:"kct"`；純自訂情境省略。後端會先 trim 字串，只有正規化後精確等於小寫 `kct` 才辨識為 KCT 並在 summary 回 canonical `"kct"`。前端仍依組感知規則自動填入名稱與動機，兩者可手改；KCT 情境可留白，一般情境仍須通過原必填 gate。`filter.commit` 落地時，空白 KCT 名稱依同批一基 position 補成 `KCT 小組方法論檢核條件（第 1 項）`、`KCT 小組方法論檢核條件（第 2 項）`……，空白動機固定補成「KCT 小組方法論檢核條件」；因此同批多個空名不會撞名，`config_filter_scenario.name`／`.rationale` 與 `project.load.filterScenarios` 留痕也永遠非空。`project.load` 回傳 source，前端惰性預覽、移除與 replace-all 重存都必須保留它。省略、null 或其他 `source` 值仍視為一般情境，不享有後端空白豁免。`sameVoucher` 與 `textSet` 會改變可重放的命中集合語意，因此 filter `logicVersion` 當時推進為 `filter-2026-08-04-v6`；其後有效母體（v7）、借貸組合多選（v8）、typed 動態 RDE 條件（v9）與 dynamic result／report columns（v10）再各推進一次，現行版本為 `filter-2026-08-14-v10`（版本常數的唯一權威是 `RuleLogicVersions`）。舊 definition 仍可由 `project.load` 回放供編輯，但不發布 resultRef、不得直接補算，必須經目前 preview／commit 重新驗證並依現行版本重新保存。
- **prescreenKey** 只接受 row-tag 類型的規則，也就是上面列的那些鍵（單一事實來源是 `JET.AuditCore.RuleCatalog` 的 RowTag 集合，文件不另抄數字）。`creatorSummary` 與 `rareAccounts` 是彙總規則，不能拿來當列述詞。`unexpectedAccountPair` 需要科目配對已匯入，否則回 `invalid_scenario`；其語意為**否定面**（2026-07-08 落地）：挑「貸 Revenue 且同傳票無任何 Receivables/Cash/Receipt in advance 借方」的分錄，**只標 Revenue 貸方列**（wire 形狀不變，變的是命中集與計數口徑）。2026-08-14 起，這裡的正常對方借方採 `amount_scaled > 0`：0 元分錄不代表已收到對價，因此不足以消解疑慮。這是唯一採 `> 0` 的借貸組合規則，`accountPair`／`specialAccountCategoryPair` 與 KCT 條件 C 都維持 `>= 0`。它與 KCT 條件 C `revenueWithoutNormalCounterpart` 同否定面，差別在對方集合含不含 Cash（本規則含、C 不含）與零元邊界；沒有零元正常對方借方時本規則命中集 ⊆ C，唯一分歧是「對方借方剛好 0 元」的傳票（見 guide §5 規則卡重疊表）。`nonAuthorizedPreparer` 需要授權編製人員清單已匯入；在 filter 這一端，空名單會回 `invalid_scenario`，這道閘控刻意鏡像 `unexpectedAccountPair` 的 validator。除了 validator，述詞層自己還有一道自保：它用 `EXISTS (SELECT 1 FROM target_authorized_preparer) AND …` 包住，所以即使有人繞過了 validator，只要名單是空的，整個述詞就是 FALSE、零命中，這與 `prescreen.run` 的 `na` 語意一致；它不會因為寫成 `NOT IN (空集合)` 而反轉成全部命中。`lowFrequencyPreparer` 則沒有前置條件。prescreen 類的條件與 `prescreen.run` 共用 AuditCore 的同一份 `GlRulePredicates`，都是即時計算，不依賴先前任何一次 run 的結果。
- **field 白名單**：`field` 是邏輯 id，經由 Domain 的白名單映射到實體欄位，例如 `docNum→document_number`、`docDate→approval_date`、`voucherDate→voucher_date`、`amount→ABS(amount_scaled)` 等。未知的 id 回 `invalid_scenario`。這裡有一條安全鐵律：SQL 的識別字永遠不來自使用者輸入；所有的值（關鍵字、日期、金額、分類、位數）一律走參數綁定。
- **`textSet` 結構化文字集合**：只接受文字型 `field`、`mode:"contains"` 或 `mode:"exact"`，以及 1–100 個字串元素的 `values` JSON array；不解析逗號，也不接受 regex，所有符號都按字面值比對，NULL 仍以空字串參與、不分大小寫。`normalization` 省略、null、非字串、空白或 `preserve` 時只 trim 各值；`removeAsciiSpaces` 另從各輸入值移除 U+0020 ASCII space，以封閉方式重現本階段需要的 legacy 輸入正規化，但不改寫資料欄本身。任何值在正規化後為空、超過 100 個值、`values` 不是字串陣列、mode 或 normalization 未知，都回 `invalid_scenario`。前端逐行 textarea 鏡射 100 個上限，但後端仍是權威。
- **`typed` 動態 RDE 條件（2026-08-14 凍結並啟用）**：只接受目前案件 committed、`valueType` 為
  `text | date | money` 的 RDE 欄位。fieldId 只作 registry lookup 與參數綁定，永不成為 SQL
  identifier；operand 一律參數化。text 比較 trim＋不分大小寫（同 `TextMatch` 的
  `UPPER(TRIM(...))` 家族）；date 以 `yyyy-MM-dd` 正規化字串精確比較；money 依專案 MoneyScale
  轉 scaled integer，並依必填 `amountBasis` 決定帶號或 `ABS` 比較。blank＝沒有
  `target_gl_rde_value` row：blank 不命中任何比較 operator（含 `notEquals`／`notContains`／
  `notIn`），`isBlank`／`isNotBlank` 專門判 missing／存在 value row——負向 operator 是 value
  row 自身的述詞（EXISTS 內取負），不重新解讀成 voucher-level `NOT EXISTS`。`in`／`notIn`
  依該型別正規化語意去重（1–100 值），整情境仍受 2,000 參數總預算；`sameVoucher` 群組內的
  typed 規則沿用同構語意。operator matrix、operand carrier 與 RDE lifecycle 的完整驗證規則
  以 Mapping 段「Typed dynamic rule（2026-08-14 契約凍結）」條目為唯一權威；Criteria 與底稿
  讀回以目前 RDE metadata 的顯示 label 呈現（僅 label 改名時自動跟進），欄位已移除時退回
  fieldId 原字串。
- **編譯參數總預算**：每個情境的 AuditCore SQL plan（含查核期間參數與所有群組／規則）最多 2,000 個參數；超出時會在該情境的 SQL command 綁定與執行前統一回 `invalid_scenario`。這是 provider-neutral 的最後防線，避免同一個 wire AST 因 provider 的 command 容量不同而只在部分資料庫可執行；Infrastructure 自有的 command metadata 不計入 plan，預算已為它保留餘裕。Replace-all commit／惰性補算仍依既有交易順序處理整批情境：較後項才發現超限時，前面已執行的清除或寫入會由同一交易完整 rollback；本條不宣稱整批在第一個 DB command 前預編譯。
- **階段 5 legacy 能力登錄**：case-A#2 使用 `textSet` 與 `removeAsciiSpaces`；case-A#3 使用 `sameVoucher` 錨定群組及文字集合／樣態；case-A#4 使用 `sameVoucher` 錨定群組及文字集合；case-A#5 使用 `sameVoucher` 錨定群組及文字集合；case-B#2 使用 `sameVoucher` 錨定群組、既有單側數值區間及文字樣態。此登錄只描述能力，不保存案件條件或衍生值；驗收一律使用合成 fixture。
- **legacy 預篩選的遷移對照**（從 vba-1120 ServiceFilter 的 12 條件對應到本 AST）：週末或假日的過帳/核准，對應到 `prescreen` 的 `weekendPosting`、`weekendApproval`、`holidayPosting`、`holidayApproval`（補班日的排除已內建在週末規則裡）；僅借方或僅貸方對應 `drCrOnly`；人工分錄對應 `manualAuto`；關鍵字對應 `text`；日期或數值區間對應 `dateRange` 與 `numRange`。
- `accountPair`（科目配對分析，三模式見 guide §6.1；不是 legacy A3）：需要 `target_account_mapping` 至少一筆非空白分類，只有來源檔但 target 為空時仍回 `invalid_scenario`；UI 會鏡像這個內容條件去隱藏按鈕，但權威判斷在後端。三個模式所需的欄位不同：`pairMode:"exact"` 需要借方與貸方兩側都選；`"debitAnchor"` 只需要借方側；`"creditAnchor"` 只需要貸方側。UI 與底稿讀回也只顯示實際參與判定的分類，不宣稱未使用的另一側。借貸側的判定是：借方側等於「屬於指定借方分類，且 `amount_scaled >= 0`」，貸方側等於「屬於指定貸方分類，且 `amount_scaled < 0`」；金額為 0 元的歸到借方側，這與 `drCr` 的推導一致。錨定模式（anchor）的輸出，是錨定的那筆分錄，加上同一張傳票裡對方側的分錄。
- **雙側多選（2026-08-14 啟用）**：兩個 pair 條件的每一側都以 `debitCategoryIds`／`creditCategoryIds` 承載 taxonomy 的分類身分陣列（`builtin.*` 或 `custom.<32 lowercase hex>`），值一律參數化。後端把選到的身分解析成它們的 semantic role，再以 `semantic_role IN (…)` 編譯——與所有內建規則一致，商業判定只看 role、不看顯示名稱，因此改名不改命中，而與內建分類同 role 的自訂分類會一併參與同一個商業結果。細節與邊界：
  - 陣列存在時即為權威，同一條規則上的 legacy `debitCategory`／`creditCategory` 完全不參與判定（migration 產生的舊定義同時帶兩者，屬正常形狀）。沒有帶陣列時才把 scalar 讀成「該內建分類的單元素集合」。
  - 明示送出空陣列代表「沒有選任何分類」，一律回 `invalid_scenario`，不會回退到 scalar；這是刻意的 fail closed——否定模式的 `NOT EXISTS` 遇到空集合會反轉成全命中。
  - 重複身分會去重，選取順序不影響 SQL、參數或命中；選兩個同 role 的分類與只選其中一個等價。多選不產生分類的笛卡兒積，同一筆 GL 列最多輸出一次。
  - 每一側最多 100 個分類，整個情境仍受 2,000 個 SQL plan 參數的總預算約束；非陣列、非字串元素或超過上限都在解析階段回 `invalid_scenario`。
  - 身分必須存在於目前專案的 taxonomy，否則回 `invalid_scenario`。
  - commit 時後端不改寫規則物件：使用者送什麼就原樣保存（只覆寫 `populationScope` 與 `logicVersion`），因此 resume 讀回與底稿呈現都以保存當下的陣列為準；讀回顯示目前 taxonomy 的顯示名稱，多選以「、」串接。
- `specialAccountCategoryPair`（考量特殊科目類別配對，採顯式雙類別加上否定語意，2026-06-23 加入；對應 legacy A3）：這是 `accountPair` 的姊妹條件，內容前置同樣要求 `target_account_mapping` 至少一筆非空白分類。查核員選一組借方類別 A（`debitCategoryIds`）、一組貸方類別 B（`creditCategoryIds`），再選三個模式之一，用來標記出帶有該借貸類別配對（含「不存在這種配對」）的傳票或分錄。借貸側的判定與 `accountPair` 相同：A 借等於「`amount_scaled >= 0` 且分類屬於 A」，B 貸等於「`amount_scaled < 0` 且分類屬於 B」。三個模式都要求兩側各至少選一項（連否定模式也需要 B 與 A 才能判定「不存在」），多選規則同上一項。以下 `pairMode` 的取值都以一張傳票（`document_number`）為判定單位：
  - `drAndCr`（借 A 且貸 B）：這張傳票同時有 A 借列與 B 貸列。命中時標記「A 借列或 B 貸列」。
  - `drNotCr`（借 A 且貸非 B）：這張傳票有 A 借列，但**整張傳票完全沒有任何 B 貸列**（以 voucher-level `NOT EXISTS` 判定）。命中時標記 A 借列；若同張同時有 B 貸與非 B 貸，仍須排除，不能把「存在任一非 B 貸」誤當本模式。
  - `notDrCr`（借非 A 且貸 B）：這張傳票有 B 貸列，但沒有任何 A 借列（以 `NOT EXISTS` 判定）。命中時標記 B 貸列。
  以上都是純 ANSI 的寫法（`EXISTS` 與 `NOT EXISTS`，分類值參數綁定），並由 SQLite、DuckDB 與 SQL Server 三 provider × 三模式真執行矩陣鎖定；`drNotCr` 另有同傳票混合 B／非 B 貸方的反例。這裡有一個刻意的取捨：`drAndCr` 的 SQL 邏輯其實與 `accountPair` 的 `exact` 模式重疊，但這兩者是面向使用者的不同條件（模式標籤不同、否定語意也不同），所以這份重複是刻意保留的。
- `customKeywords`（自訂關鍵字，即原本的 A2 語意）：述詞與 `suspiciousKeywords` 相同（contains-any、不分大小寫、NULL 以空字串參與比對），差別只在關鍵字改由使用者輸入（逗號分隔，至少要有一個非空白）。
- `customTrailingZeros`（自訂尾數位數，即原本的 A4 語意）：`digits` 是 1–12 的整數；述詞與 `trailingZeros` 相同，先取主單位整數再判斷尾端連續零，小數位不參與。
- `customPreparerEntryCount`（自訂低頻編製者門檻）：`maxEntries` 是 ≥ 1 的整數，用它取代固定預設 11；GROUP BY 子查詢跟隨 revision `populationScope`。
- `customAccountEntryCount`（自訂低頻科目門檻，C9 的自訂軌）：`maxEntries` 是 ≥ 1 的整數，用它取代固定預設 11；GROUP BY 子查詢跟隨 revision `populationScope`。
- **KCT 小組條件（2026-06-23，Phase 1）**：下列五個型別是 KCT 小組方法學清單專屬的條件，前端把它們歸在獨立的「KCT 小組條件」分組底下。它們各有獨立的 wire 型別，述詞主要只讀 `target_gl_entry`（其中部分還會讀 `target_account_mapping`）。識別字一樣只出自欄位白名單或常數，使用者輸入的值（天數、尾數）一律參數綁定。
  - `revenueDebitNearQuarterEnd`（季末前借記收入，清單 A）：科目分類為 Revenue、且在借方側（`amount_scaled >= 0`），同時 `post_date` 要落在任一曆年季底（3/31、6/30、9/30、12/31）往前推 `windowDays` 天（含季底當天）所構成的視窗內。這些視窗是後端依專案查核期間加上 `windowDays` 枚舉出來的，視窗邊界參數綁定。`windowDays` 是 1–92 的整數。target 必須含 Revenue 分類，否則回 `invalid_scenario`。
  - `revenueWithoutNormalCounterpart`（收入無一般對方科目，清單 C）：本列是 Revenue 貸方（`amount_scaled < 0`），但它所在的那張傳票裡，沒有任何一筆「在借方側、且分類屬於 {Receivables, Receipt in advance}」的分錄。注意現金（Cash）不算本條命中述詞的一般對方科目。資格閘要求 target 同時含 Revenue，且 Receivables／Cash／Receipt in advance 至少存在一類；Cash 只參與資格閘，不改變命中述詞。
  - `manualRevenueEntry`（收入之人工分錄，清單 D）：科目分類為 Revenue、且 `is_manual = 1`。除 target 必須含 Revenue 分類外，GL「人工/自動分錄」來源欄也必須已配對；缺配對時在 preview／commit 直接拒絕，不以全 NULL 的零命中冒充有效結果。
  - `trailingDigits`（特定金額尾數，清單 H）：純機械式尾數比對——把金額主單位整數（`ABS(amount_scaled) / MoneyScale`，向零捨去小數，等同 legacy `@int(amount)`）的末 k 位，與審計員指定的 k 位樣態逐字比對，相等即命中；多個樣態任一相等即命中。**不把小數位、顯示補零、scale 或格式化結果納入比對**；某個尾數是否值得篩、在特定案件代表什麼（整數化金額、一致尾數、接近門檻或其他 red flag），一律由審計員自行判斷，工具只回答「尾數是否相符」，不輸出風險/門檻/舞弊結論。等價於 legacy IDEA `@Right(@Str(@int(amount),1,0), k) = pattern`：k≥2 時整數需至少 k 位（`≥ 10^(k-1)`，否則字串短於樣態、長度不符即不相等）；k=1 時不設下界（每個整數含 0 都 ≥1 位）。樣態清單重用 `keywords` 欄位傳遞（逗號分隔，每組 1–12 位純數字，例如 `999999`、`000000`）。三 provider 必須經 `ISqlDialect` 的整數商片段產生等價結果。
  - `preparerEqualsApprover`（編製與核准為同一人，清單 J）：要求 `created_by` 與 `approved_by` 都非空白，且兩者相等（比較時忽略大小寫與前後空白）。GL「傳票建立人員」與「傳票核准人員」來源欄都必須已配對；缺任一欄時在 preview／commit 直接拒絕並指名缺口。
- **KCT 重用既有型別（清單 E/F/G/I）**：這幾項不新增 wire 型別。前端的「KCT 小組條件」分組改用預設按鈕，帶入既有型別的預填規則：特定人員用 `text`（`createBy`、`exact`），且需要 GL「傳票建立人員」已配對；特定摘要用 `customKeywords`；空白摘要用 `prescreen`（`blankDescription`），且需要 GL「傳票摘要」已配對；非營業日則組成一個群組 `prescreen weekendPosting OR prescreen holidayPosting`（兩者都比對 `post_date`）。G 的述詞只命中本列 `document_description IS NULL OR TRIM(...) = ''`；其他非空白列不命中，不因同傳票另有空白列而擴張，也不把「欄位未配對」解讀成全母體空白。
- `numRange`：`from` 與 `to` 是顯示值的 decimal，後端會先用 MoneyScale 轉成 scaled，再去比較 `ABS(amount_scaled)`；兩個邊界至少要填一個。`dateRange`：日期是 `yyyy-MM-dd`，比較的是 `field` 指定的那個日期欄；兩個邊界至少要填一個。
- `text`：NULL 欄位以空字串參與比對（用 `COALESCE`，所以 `notContains` 對 NULL 列會成立）；比對不分大小寫。既有逗號分隔 `keywords` wire 與語意不變；需要無歧義的多值或 legacy 空白正規化時使用 `textSet`。
- `manualAuto`：比對 `is_manual = 1|0`；來源沒提供人工旗標（也就是 NULL）的列永遠不匹配。
- 以下任何一種情況都會讓 AST 驗證失敗、回 `invalid_scenario`（訊息會把所有錯誤合併列出）：缺名稱或動機（非 KCT 來源時這兩項必填；`source:"kct"` 豁免這兩項，其餘檢查照舊）；空群組；未知的 field／prescreenKey，或未知／非正準拼法的 type、mode、`matchScope`、`normalization`；group 層或 rule 層的 `join` 填了非空白、卻不是 `AND`／`OR`（不分大小寫）的值（缺欄、null、空白字串維持預設 `AND`，不算錯誤）；`sameVoucher` 少於兩條規則或群組內含 OR；`textSet.values` 不是 1–100 個字串的陣列、任一值正規化後為空、或編譯後超過 2,000 個 SQL plan 參數；缺邊界；用了 `prescreen postPeriodApproval` 但專案沒有 `lastPeriodStart`；用了 `unexpectedAccountPair` 但科目配對未匯入；用了 `accountPair` 或 `specialAccountCategoryPair` 但 target 沒有任何非空白分類；用了 KCT 的 `revenueDebitNearQuarterEnd` 或 `manualRevenueEntry` 但 target 沒有 Revenue；用了 `revenueWithoutNormalCounterpart` 但 target 沒有 Revenue 或沒有 Receivables／Cash／Receipt in advance 任一一般對方分類；KCT D／E／G／J 缺少上列必要 GL mapping 欄位；`digits` 超出 1–12；`customPreparerEntryCount` 或 `customAccountEntryCount` 的 `maxEntries` 小於 1；`windowDays` 超出 1–92；`trailingDigits` 的樣態不是 1–12 位的純數字；`specialAccountCategoryPair` 的 `pairMode` 不是 `drAndCr`、`drNotCr`、`notDrCr` 三者之一；pair 條件實際使用的那一側沒有選任何分類（含明示空陣列）、單側超過 100 個分類，或分類身分不存在於目前專案的 taxonomy；`debitCategoryIds`／`creditCategoryIds` 不是字串陣列；`typed` 條件的 fieldId 缺漏或不存在於目前案件 committed 的 RDE 欄位、operator 缺漏或與欄位目前型別不相容（一律指名 field；RDE lifecycle 的 unknown／removed／type-changed 都落在這裡，definition 保留供修正、不發布 resultRef）、operand carrier 與 operator 種類不符（single-value 用 `value`；`between` 用 `from`＋`to` 且解析後 `from <= to`；`in`／`notIn` 用 `values` 1–100 個字串；`isBlank`／`isNotBlank` 不得帶任何 operand）、operand 為 null／非字串／型別錯誤或格式無效（date 非 `yyyy-MM-dd`、money 非 invariant decimal、text trim 後為空）、money 條件缺正準 `amountBasis` 或非 money 條件帶 `amountBasis`。另外兩種 commit 階段的錯誤：commit 超過 10 個情境回 `scenario_limit_reached`；commit 出現重名回 `invalid_scenario`。

### Export

| Action | Payload | Response | 用途 |
|:---|:---|:---|:---|
| `export.validationArtifacts` | `{ runId }` | `{ ok, artifacts: [ValidationReport, AccountMapping, INF_Report] }` | 資料驗證完成後的原子三檔批次。`runId` 必須等於目前有效的 validate run；ValidationReport、AccountMapping 與 INF_Report 都直接填固定範本副本，三個 OpenXML temp 全部成功才發布並更新 manifest，任一失敗／取消不留下半批。INF 明細顯式綁 runId、固定可重現 59 筆，並依 committed GL RDE ordinal 追加全部 mapped RDE，值保留 text／date／money native type 與 null。ValidationReport 的摘要 count 永遠保留；V_Report 1–4 只有單類 `0 < count <= 10000` 時讀取並輸出明細，超量時為正常的 summary-only；blank post date 只進固定 `Source_Quality`，不建立 V_Report 7；V_Report 6 以借貸不平傳票回接後的 raw GL 明細列數判斷，只有 `0 < detailRowCount < 10000` 時讀取並輸出。省略明細不改 response 或三檔原子成功語意 |
| `export.accountMappingTemplate` | `{ runId }` | `{ ok, artifact, rowCount }` | Validation batch 中 AccountMapping 的單檔重試入口。母體＝GL∪TB；`AccountMapping` 自第 4 列寫 A/B，C4:C(last) 留空、解鎖並套目前 project taxonomy label 下拉；`List`、dimension、validation formula 與 A2 說明都依 ordinal 動態重寫。只落專案目錄，不接受 `outputPath` |
| `export.prescreenReport` | `{ runId }` | `{ ok, artifact }` | `runId` 必須等於目前有效的 prescreen run；直接填 `Pre-screening_Report` 固定範本副本。R1–R4／R7 的 distinct voucher count 與 GL row count 在 writer 執行前由 AuditCore finalized `PrescreenReportPlan` 裁定：`0 < rowCount < 10000` 才以 keyset paging 讀取並輸出明細；`rowCount >= 10000` 只保留摘要，D 欄固定為 `明細筆數超過10,000筆，明細資料不匯出`，而且不得讀取明細 repository；零筆也不建立明細頁。Legacy R5／R6 的 E、F 欄，以及已由 Step 4 自訂條件取代的 A2–A4，固定顯示 N/A。欄位來源忠實性（guide §7.1，2026-08-14）：明細頁輸出報表實際讀取的 GL 匯入批次完整欄位目錄（含未被配對選中的欄位，維持實際來源欄名與 ordinal）；R6 前兩欄標題沿用目前 committed GL mapping 的 `accNum`／`accName` 來源欄名（只有來源資料表確實存在同名欄位時才會出現 `ACCOUNT_CODE`／`ACCOUNT_NAME`），彙總衍生欄維持 `ENTRY_COUNT`／`DEBIT_TOTAL`／`CREDIT_TOTAL`；缺 committed GL mapping 時 fail closed，不退回固定 alias |
| `export.criteriaSelectionReport` | `{ validationRunId, revision }` | `{ ok, artifact }` | validation 與 filter revision 都必須仍是目前有效版本；不要求或接受 prescreen run 作為來源前置。直接填固定範本副本，輸出 `Summary Inforamtion` 與每個 `#Criteria Select N`。摘要第 3 列固定留白；第 4 列只保留 B4 `條件的內容`，A4／C4／D4 不加標題。每個情境摘要的 C 欄為命中傳票數、D 欄為命中列數；明細以該情境命中的傳票集合為母體，依 `entry_id` keyset paging 輸出每張命中傳票的全部原始 GL 分錄，而非只輸出命中列。各明細頁依 committed ordinal 追加目前匯出 revision 全部情境所引用 RDE 的 union，不依 AST encounter order。Criteria 不套用 Pre-screening 的 10,000 門檻或 legacy 1,000,000 截斷，超過單張工作表容量時建立續頁並重複標頭。artifact 的來源 tuple 保存目前 validation、revision 與全部 scenario positions，不能由 caller 裁切；prescreenRunId 固定為 null。validation／revision／position 集合換版後舊 artifact 標 stale，舊 reference 不得以目前資料重製 |
| `export.workpaperStream` | `{ validationRunId, scenarioRevision, scenarioPositions }` | `{ ok, artifact, sheetStats }` | 正式底稿固定輸出封面／說明、step1、step1-1、step1-2、條件式 step1-3、step2、step3、step4、step4-1、step5 與三張參考頁；不產生 `step1-3-1`。payload 只選擇要納入的已存情境（1–10），不選工作表或落點。step3 只輸出 B:E，不另加條件邏輯 F 欄；step4 每個命中傳票只輸出一列，固定 C1–C10 槽位只對本次所選情境標記。step2 與 step4-1 依 committed ordinal 追加 payload `scenarioPositions` 所選情境引用 RDE 的 union，保留 native type 與 null。Field Info 使用與 ValidationReport 相同的 target TableDef canonical projection；WorkingPaper 不讀 Validation V_Report 1–4、`Source_Quality` 異常明細或 prescreen 結果。validation 與 filter revision 均須為目前有效版本；不要求或接受 prescreen run 作為來源前置，並要求同一來源集合的目前 CriteriaSelectionReport 已存在。step4-1 由 internal export seam 輸出 12 個 Legacy 優先欄、其餘實際 `_JE`／`_JE_S` ordinal 欄與有 row hit 的 compact tag 欄；保留 native cell type、signed amount，排除 voucherDate，並依傳票號碼、native line item、`entry_id` 排序，欄寬取完整資料。Number line item 的 internal cursor 使用 projection 時持久化的 provider-neutral ordinal key；v5 舊列缺 key 時以 `stale_result` fail closed，不從顯示值猜測。public `query.tagMatrixRowPage` 的 payload、response、paging 與 cursor 不變；production 直接 SAX 重寫固定範本的授權 worksheet parts，不建立第二 workbook。step4-1 內部改由 provider-neutral prepared session 在單一 dedicated connection／transaction 內一次 materialize 命中傳票、一次 materialize row tags，再以一個 ordered reader 逐列投影到 exact typed disk spool；無所選 RDE 時維持同一 reader 直接投影的六個 SQL command fast path。有所選 RDE 時先把 source rows 寫入 DeleteOnClose spool，完整釋放 prepared session 後才以最多 500 筆的 set-based RDE batch 完成 projected spool，再 SAX replay；不在 active provider reader 期間開第二連線。取消／失敗先 rollback 再 bounded cleanup，維持既有 artifact 原子性 |
| `report.cleanupPreview` | `{}` | `{ catalogRevision, candidateCount, candidateBytes, candidates: [{ artifactId, kind, fileName, generatedUtc, bytes, reason }] }` | **P4／使用者核定 1A**。需要 active project；先依目前 validation／prescreen／filter 版本把 artifact stale 狀態寫回，再由後端計算完整但有界的清理候選。`reason` 只會是 `stale` 或 `retention`。新產檔契約讓每一 `kind` 只保留一份，不會再新增 retention 候選；既有專案尚未被同種類重跑收斂前，舊版多版本 catalog 仍沿既有規則列出全部 stale，並讓其餘有效版本依 validity family 保留最近三版，最新有效版另有硬守衛。Criteria／WorkingPaper 的 positions 是同一有效資料世代內的輸出選擇，不切開 family。response 不含絕對路徑、sha256、sourceRef、family key 或可調門檻。因會刷新 stale catalog，分類 exclusive；不列入取消追蹤。只預覽，不刪檔、不 archive |
| `report.cleanupConfirm` | `{ catalogRevision }` | `{ ok, deletedCount, deletedBytes, auditId, catalogRevision, reportArtifacts }` | **P4／第二次明示確認**。需要 active project、exclusive、可取消。payload 只能回傳 preview 的 opaque revision；不接受 projectId、artifact IDs、路徑、source refs 或保留版數。後端再次刷新 stale、重新計畫，store 在 projects-root-local cross-process lock 內比對 semantic catalog revision；不同即 `artifact_catalog_changed`，零刪除。刪除前同時驗證全部候選為 project direct-child regular file，且 bytes／SHA-256 相符、沒有阻擋 write／rename 的 Excel 或其他 writer 占用；單純 read handle 若明示允許 delete，不視為 writer lock。任一失敗整批回滾。manifest atomic replace 是 commit point，之前取消會完整復原，之後忽略取消並完成 immutable project-local audit receipt 與 quarantine 清理。`reportArtifacts` 是清理後的權威完整 artifact wire 清單，形狀同本節下方定義 |

`export.prescreenReport`、`export.criteriaSelectionReport` 與 `export.workpaperStream` 在讀取 action-specific source reference 或建立暫存檔前，先要求目前 validation run 的完整性裁定適格；不適格一律回 `completeness_prerequisite_failed`。驗證三檔與 AccountMapping 單檔重試是處理完整性差異所需的診斷／修復輸出，故刻意不納入。

步驟五匯出面預設把 Pre-screening Report 納入輸出家族：勾選框預設為勾選、使用者可取消。有目前有效的 prescreen run 時，按下產生會依序執行 `export.prescreenReport` 與 `export.workpaperStream`；沒有時畫面先明示「會先執行一次預篩選」（文案取自 `prescreen.run` 的 `positioning.exportPendingRunGuidance`），使用者按同一顆按鈕即依序執行 `prescreen.run` → `export.prescreenReport` → `export.workpaperStream`。這是前端以既有 action 編排的序列，三個 action 的 payload、response 與判定語意都不變；`prescreen.run` 仍要求完整性適格，不適格時匯出面不提供補跑、只保留 Working Paper。取消或任一步失敗都停止後續 action；已完成 action 的產物會保留，每個 action 各自沿用既有原子性與取消語意，因此這個前端序列不是跨產物交易。步驟四的手動 `export.prescreenReport` 入口維持不變。

所有 `artifact` 形狀為 `{ artifactId, kind, fileName, generatedUtc, bytes, sha256, sourceRef, stale }`；`sourceRef = { validationRunId?, prescreenRunId?, scenarioRevision?, scenarioPositions? }`。`fileName` 只有檔名、不含目錄，wire 不得出現絕對路徑；所有新產生的 Excel `fileName` 副檔名固定為精確小寫 `.xlsx`，不得產生 `.XLSX`。每一專案、每一 `kind` 的正式報告固定只有一個檔名 `{projectId}_{report-kind}.xlsx`（例如 `{projectId}_WorkingPaper.xlsx`）：時間戳只留在 `generatedUtc`，`artifactId` 只是 wire／manifest 的 opaque identifier，兩者都不得再拼進檔名。相同種類重新執行時，後端必須先完整產生並驗證暫存檔，再以同一交易式檔案操作原子覆寫固定路徑，並以新 artifact 取代 catalog 中該種類的所有舊項目；成功後專案目錄與前端 catalog 都只能看到該種類一份，失敗或取消則保留原檔與原 catalog。既有帶時間戳／隨機碼的 legacy 檔名仍可載入；該種類下一次成功匯出時，所有 legacy 同種類項目與檔案一併收斂為固定檔名。所有產檔 action 都是 exclusive，且只寫入目前專案資料夾。manifest 的正準欄位名是 `generatedUtc` 與 `sourceRef`；讀取器仍接受舊版 `createdUtc` 與 `sourceRefs`，但新寫入一律使用正準名稱。`stale` 只表示目前 catalog 內的產物來源已失效；同種類重跑是取代，不以 stale 保留舊版。

六份正式 `.xlsx` 共用固定 `JET_Metadata` worksheet，狀態必須為 `VeryHidden`。A1／B1／C1 依序保存 marker、format version 與 chunk count；canonical payload 保存 `approvalDateMode` provenance、effective population policy、taxonomy revision 與 mapping metadata v2（GL／TB 缺側以 null 表示）。上游 run／revision／logicVersion reference 位於 project-local artifact manifest 的 `sourceRef`，不重複寫進 workbook metadata；因此 CriteriaSelectionReport 與 WorkingPaper 的 `JET_Metadata` 沒有 prescreen reference 可沿用或補寫。載荷超過單儲存格時依 ordinal 切成有界 chunks，chunk 列與載荷欄維持 hidden；任一 metadata／workbook 寫入失敗或取消都不得發布半套 artifact。這是 workbook provenance 契約，不改上述 wire `artifact` shape。

六報告的 legacy 資料語意由 writer 固定執行。ValidationReport 的 legacy V5 是每次都存在的完整 GL／TB 科目調節表，不是只列差異；legacy V6 則把借貸不平傳票回接到總帳，輸出那些傳票的完整原始 GL 明細。ValidationReport 的 V_Report 1–4 與 V_Report 6 依上列各自邊界省略超量 Excel 明細，但審計 count、UI 有界樣本及使用者明示分頁不截斷；blank post date 只由獨立 `Source_Quality` 揭示，不建立 V_Report 7；summary-only 不等於 N/A。Pre-screeningReport 的 E 欄是 distinct voucher count，F 欄是 GL row count；R1–R4／R7 以 finalized plan 的 `rowCount >= 10000` 作 summary-only 門檻，legacy R5／R6 的 E、F 欄，以及已移到 Step 4 的 legacy A2–A4，固定顯示 N/A。CriteriaSelectionReport 不套用這個門檻，且每個情境輸出命中傳票的全部原始 GL 分錄。未受具名 summary-only 政策約束且可能超過單張 Excel 容量的大型表，仍建立續頁並重複標頭。

## JetApi Typed Facade

前端呼叫 bridge 的唯一管道是 `window.JetApi.*`。這個 facade 目前是手動維護的，放在 `wwwroot/js/jet-api.js`；它以檔內的 `SUPPORTED_ACTIONS` 清單為單一事實來源，據此自動生成各個 typed method。目前還沒有 C# 版的 `JetBridgeScriptFactory`；將來就算建了 script factory，生成規則也不會變。action 名稱對應到 facade method 的規則如下：

1. 以 `.` 把 action 名切成數段。
2. 第一段全部小寫，後續每段的首字母大寫，再串接起來（也就是 lowerCamelCase）。
3. 例如：`validate.run` 變成 `JetApi.validateRun`；`mapping.commit.gl` 變成 `JetApi.mappingCommitGl`。

下表是目前 facade 提供的 method，同樣以 `wwwroot/js/jet-api.js` 的 `SUPPORTED_ACTIONS` 為單一事實來源；其中 `project.loadDemo` 與 `demo.*` 的後端 handler 只在 Debug／不可發布 AgentGuiTest 組建註冊，dev.db.* 與 dev.log.* 則只在 Debug 組建註冊（facade method 本身靜態存在，Release 下呼叫會得到 unknown action）。runtime 前端不渲染或註冊任何 demo 入口；`system.ping.devToolsEnabled` 只控制唯讀開發面板：

| Action | JetApi method |
|:---|:---|
| `system.ping` | `JetApi.systemPing` |
| `system.databaseInfo` | `JetApi.systemDatabaseInfo` |
| `system.whoAmI` | `JetApi.systemWhoAmI` |
| `operation.cancel` | `JetApi.operationCancel` |
| `project.list` | `JetApi.projectList` |
| `project.listLocal` | `JetApi.projectListLocal` |
| `report.cleanupPreview` | `JetApi.reportCleanupPreview` |
| `report.cleanupConfirm` | `JetApi.reportCleanupConfirm` |
| `project.saveProgress` | `JetApi.projectSaveProgress` |
| `project.heartbeat` | `JetApi.projectHeartbeat` |
| `project.releaseLock` | `JetApi.projectReleaseLock` |
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
| `accountTaxonomy.save` | `JetApi.accountTaxonomySave` |
| `import.authorizedPreparer.fromFile` | `JetApi.importAuthorizedPreparerFromFile` |
| `import.inspectFile` | `JetApi.importInspectFile` |
| `import.previewFile` | `JetApi.importPreviewFile` |
| `import.holiday` | `JetApi.importHoliday` |
| `import.makeupDay` | `JetApi.importMakeupDay` |
| `import.holiday.fromFile` | `JetApi.importHolidayFromFile` |
| `import.makeupDay.fromFile` | `JetApi.importMakeupDayFromFile` |
| `calendar.setNonWorkingDays` | `JetApi.calendarSetNonWorkingDays` |
| `mapping.autoSuggest` | `JetApi.mappingAutoSuggest` |
| `mapping.restoreDraft` | `JetApi.mappingRestoreDraft` |
| `mapping.valueProfile` | `JetApi.mappingValueProfile` |
| `mapping.commit.gl` | `JetApi.mappingCommitGl` |
| `mapping.commit.tb` | `JetApi.mappingCommitTb` |
| `validate.run` | `JetApi.validateRun` |
| `prescreen.run` | `JetApi.prescreenRun` |
| `filter.preview` | `JetApi.filterPreview` |
| `filter.commit` | `JetApi.filterCommit` |
| `query.dataPreview` | `JetApi.queryDataPreview` |
| `query.prescreenPage` | `JetApi.queryPrescreenPage` |
| `query.completenessDiffPage` | `JetApi.queryCompletenessDiffPage` |
| `query.docBalancePage` | `JetApi.queryDocBalancePage` |
| `query.nullRecordsPage` | `JetApi.queryNullRecordsPage` |
| `query.sourceQualityPage` | `JetApi.querySourceQualityPage` |
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
| `export.validationArtifacts` | `JetApi.exportValidationArtifacts` |
| `export.prescreenReport` | `JetApi.exportPrescreenReport` |
| `export.criteriaSelectionReport` | `JetApi.exportCriteriaSelectionReport` |
| `export.workpaperStream` | `JetApi.exportWorkpaperStream` |
| `export.accountMappingTemplate` | `JetApi.exportAccountMappingTemplate` |
| `dev.db.overview` | `JetApi.devDbOverview` |
| `dev.db.tableData` | `JetApi.devDbTableData` |
| `dev.db.reconcile` | `JetApi.devDbReconcile` |
| `dev.log.export` | `JetApi.devLogExport` |

規則：

- UI、demo、workflow 的程式碼一律用 `await JetApi.xxx(payload)` 呼叫，不得直接呼叫 `window.jet.invoke(...)` 或 `window.chrome.webview.postMessage(...)`；唯一的例外是 bootstrap script 本身。
- 呼叫一個沒註冊的 method 會得到 undefined function error，這正好提示你要先在這份 manifest 新增對應的 action。
- 新增 action 的順序固定是：先改這份 manifest，再改 `SUPPORTED_ACTIONS`，再改 handler，最後才在 UI 裡使用 `JetApi.<newMethod>`。

## Error Codes

Handler 用 `JetActionException(code, message, field?)` 回報業務錯誤；bridge 會把其中的 `code` 直接放進 response 的 `error.code`，只有具明確欄位歸屬的錯誤才另帶 `error.field`。引擎例外在 dispatcher 的單一映射點依序轉譯：SQLite／DuckDB 的明確忙碌、外部鎖定、儲存空間不足與損壞型樣映射為 `database_*`，SQL Server 的 `SqlException` 映射為 `sql_server_*`／`duplicate_key`（不分 action，涵蓋對應 provider 的所有操作）。未知引擎型樣與其他未預期例外仍以 `bridge_error` 呈現，不把一般程式錯誤誤包裝成可重試錯誤。已註冊的錯誤碼如下：

| Code | 意義 |
|:---|:---|
| `invalid_payload` | payload 缺必填欄位或格式錯誤（訊息列出欄名） |
| `no_active_project` | 尚未建立或載入任何專案 |
| `project_not_found` | projectId 不存在或格式無效 |
| `artifact_catalog_changed` | 報告清理預覽後，artifact catalog 已因新匯出、stale 刷新或其他清理而改變。confirm 零刪除；前端清除舊 token 並在 busy 解除後重新 preview |
| `artifact_cleanup_failed` | 報告清理無法安全完成，例如候選被 Excel／其他程序占用、內容的 bytes／SHA-256 不符索引、reparse／containment 驗證失敗，或 journal／audit 無法安全發布。manifest commit 前整批回滾；若已 commit 則由同一呼叫或下一個持鎖者完成 durable recovery，不猜測刪除未索引檔案 |
| `file_not_found` | filePath 指向的檔案不存在；或 dev.db.* 檢視時專案 DB 檔不存在（唯讀檢視不會建檔） |
| `unsupported_file_type` | 副檔名不在支援清單（`.xlsx`、`.csv`、`.txt`） |
| `file_read_error` | 檔案無法開啟或解析（如被 Excel 鎖定、損壞、文字檔編碼不可解碼），或案件 `project.json` 的 INF 抽樣 seed／版本格式、範圍或配對關係損壞；後者不得靜默重生 seed 或改抽 |
| `sheet_not_found` | `sheetName` 指定的工作表不存在於 `.xlsx` 檔案中 |
| `empty_workbook` | 無工作表、無標頭列（含 CSV 空檔／僅標頭），或匯入後資料列數為 0 |
| `no_import_batch` | 尚未匯入該 dataset 就執行 mapping commit，或尚無批次就以 `mode:"append"` 附加來源 |
| `column_mismatch` | `mode:"append"` 的來源欄名集合與既有批次不一致（訊息列出雙向差集） |
| `missing_required_mapping` | mapping 缺必填 key（訊息列出缺漏 keys） |
| `mapping_column_not_found` | mapping 指到的欄位不在匯入批次 columns 中 |
| `mapping_metadata_missing` | 指定 `.xlsx` 沒有 JET 版本化 mapping marker（包含 P2 前舊報告）；不得從可見的部分欄位名稱猜測草稿 |
| `mapping_metadata_invalid` | JET mapping marker 存在，但格式版本不支援，或 JSON、dataset、logical key、mode／必要欄位不符合該版本契約；整份拒絕、不回部分草稿 |
| `mapping_review_required` | 舊案仍有已提交的 GL 或 TB mapping 保持 format v1。Production composition 在 inner handler 前統一攔截 validation、prescreen、filter、effective／excluded preview、結果 pages 與正式 exports，因此不得讀 facts、惰性補算 result 或建立 artifact temp。`project.load`、匯入、`mapping.restoreDraft`、`mapping.valueProfile`、staging preview 與 `mapping.commit.*` 不受阻擋，讓使用者完成修復 |
| `projection_failed` | staging→target 轉換有列級錯誤；projection 會繼續掃完整來源以計算精確總數，訊息帶總數與有界樣本（目前前 10 筆），整批已 rollback，前一個成功 generation 不變 |
| `taxonomy_revision_conflict` | `accountTaxonomy.save.revision` 不是目前 project snapshot；不得覆寫較新的分類，重新 `project.load` 後再提交 |
| `taxonomy_category_in_use` | replace 想刪除仍被 AccountMapping 或 saved filter scenario 精確引用的 custom category；整次 taxonomy mutation rollback |
| `unsupported_mode` | import mode 或 amountMode/changeMode 不被支援 |
| `table_not_allowed` | dev.db.tableData 的 tableName 不在白名單 |
| `no_target_data` | 尚未 commit GL mapping（無 target 投影資料）就執行 `validate.run` / `filter.preview`。`prescreen.run` 會更早套用完整性硬閘，因此缺少有效 validation 時回 `completeness_prerequisite_failed`。與 `no_import_batch`（mapping commit 階段缺匯入批次）語意區隔 |
| `completeness_prerequisite_failed` | 目前資料世代沒有有效 validation run，或其完整性 part(a)／part(b) 不適格、摘要不完整。`prescreen.run`、`filter.commit` 與完整性下游正式報表 fail-closed；訊息會指明需先執行驗證、補齊 TB、處理控制總數／科目差異或重新驗證 |
| `invalid_scenario` | filter 條件 AST 驗證失敗（缺名稱/動機（非 KCT 來源時必填；`source:"kct"` 豁免）、空群組、未知或 padded closed token、`sameVoucher` 少於兩條或含 OR、`textSet.values` 不是 1–100 個字串／正規化後含空值、SQL plan 超過 2,000 參數、缺邊界、科目配對未匯入即用 accountPair/unexpectedAccountPair、digits 超界、KCT 條件前置/參數不符、pair 條件單側未選分類或分類身分不存在於專案 taxonomy、commit 重名…；訊息合併列出全部錯誤）。KCT D／E／G／J 缺必要 GL mapping 時沿用本碼，訊息固定含「缺少前置資料」並指名「人工/自動分錄」「傳票建立人員」「傳票摘要」或「傳票核准人員」 |
| `scenario_limit_reached` | `filter.commit` 超過 10 個情境上限 |
| `empty_effective_population` | `mapping.commit.gl` 成功正規化 raw 列後，依查核期間與選用 posting policy 分割出的有效母體為 0；target、control totals、mapping metadata 與結果失效均完整 rollback，保留前一個已提交資料世代 |
| `gl_amounts_all_zero` | `mapping.commit.gl` 的有效母體非空，但有效借貸總額皆為 0（常見於借/貸金額欄誤配到傳票總額或空欄）；整批已 rollback，請改配對列層借/貸金額欄 |
| `database_busy` | SQLite 回 `SQLITE_BUSY`，或 DuckDB 回明確的 transaction／write-write conflict。另一項本地資料庫交易暫時占用資源；目前操作已失敗或 rollback，請稍後重試 |
| `database_locked` | SQLite 回 `SQLITE_LOCKED`，或 DuckDB 明確指出資料庫檔已由另一程序開啟、無法取得檔案鎖。請關閉占用該案件資料庫的其他程式或 JET 實例後重試 |
| `database_storage_full` | SQLite 回 `SQLITE_FULL`，或 DuckDB 明確回報磁碟／儲存空間不足。請先釋放專案所在磁碟空間，再重試；未完成的交易已 rollback |
| `database_corrupt` | SQLite 回 `SQLITE_CORRUPT`／`SQLITE_NOTADB`，或 DuckDB 明確回報 corruption／checksum mismatch。不得以重試掩蓋；停止寫入並從已知良好備份復原或交由維護人員處理 |
| `sql_server_not_configured` | 選用 sqlServer provider，但 SQL Server 連線或單一資料庫名未設定（`JET_SQLSERVER_CONNECTION` 缺、或 `Sql:Database` 缺）；SQLite／DuckDB 本機專案不受影響 |
| `sql_server_express_unsupported` | 選用 sqlServer provider 但連到的引擎是 SQL Server Express（含 LocalDB，`EngineEdition=4`）；單庫模型下所有專案共用一個資料庫會撞 Express 的 10 GB 上限。Express 已淘汰，請改用 SQL Server 2022（Developer／Standard）。偵測到即擋下、不建庫 |
| `invalid_project_schema` | sqlServer 專案的 schema 名稱不符合 JET 的安全命名與 registry 契約；拒絕組 SQL，不以未驗證識別字查詢或刪除 |
| `unsupported_provider` | 已保存的 `databaseProvider` 不在 `sqlite`／`duckdb`／`sqlServer` 白名單；provider 路由 fail loud，不猜測替代引擎 |
| `sql_server_login_failed` | SQL Server 登入失敗（引擎錯誤 18456：帳號／密碼錯、或登入尚未生效——如剛切混合模式未重啟）。訊息引導檢查 `appsettings.json` 的 `Sql:*` 或 `JET_SQLSERVER_CONNECTION` |
| `duplicate_key` | SQL Server 唯一鍵／主鍵衝突（引擎錯誤 2601/2627）。訊息保留引擎原文（含索引／資料表與重複鍵值方向），供辨識衝突來源 |
| `sql_server_deadlock` | SQL Server 死鎖犧牲者（引擎錯誤 1205）。控制面共用表的寫入已內建有限次自動重試（指數退避）；重試耗盡仍死鎖、或未包重試的操作首次即中選，才回此碼——語意是「已重試仍失敗，請稍後再試該操作」 |
| `sql_server_timeout` | SQL Server 執行逾時（`SqlException` 逾時類別，含連線逾時與指令逾時）。伺服器暫時過載或不可達；確認伺服器狀態後重試 |
| `project_locked` | 案件已被另一持有人開啟：sqlServer 專案表示未過期租約由他人持有（訊息含 `lockedBy`／`machineName`／`lockedUtc`；崩潰後依 `lock.timeoutSeconds` 過期接管）；本地 sqlite／duckdb 專案表示另一 JET 程序仍持有 projects 根下的排他鎖檔 handle（程序死亡由 OS 自動釋放）。本地外部程序的鎖檔無法可靠讀出持有人機器與起始時間，訊息只說明另一個 JET 執行個體正開啟案件並引導先在該視窗離開，不顯示臆造時間戳。`project.create`、`project.load` 與本地 `project.delete` 取不到鎖時都回此碼；不得進 session、戳 `lastOpenedUtc` 或刪資料 |
| `not_authorized` | 當前使用者對此線上（sqlServer）案件沒有存取權（`dbo.project_access` 無此 principal 的授權列）：`project.load`／`project.delete` 對他人建立的案件回此碼。現階段授權模型＝建立者於建案時自動獲授權、使用者之間互不可見不可操作；對他人授權的名單機制待公司環境測試後另輪。身分為 client 自報的軟性身分（見 `system.whoAmI`） |
| `operation_in_progress` | 已有一項「變更型」作業（建立／載入／刪除／儲存進度、各種 `import.*`、`calendar.setNonWorkingDays`、`mapping.commit.*`、`validate.run`、`prescreen.run`、`filter.commit`，以及所有正式 `export.*`）進行中，又收到第二項變更型作業時回此碼。系統維持「變更型單工 fail-fast＋唯讀併行」：同一時間至多一項、非阻塞試取、**不排隊**（回絕優於堆積誤點）。`project.releaseLock` 是 handler-owned conditional-gate action：不在 dispatcher 重複取閘，但整個放鎖／session 離場區段會試取同一閘，取不到也回此碼，原鎖與 session 保持不變；底層放鎖失敗或取消也保留 session 與心跳供重試。前端保留目前畫面與心跳；有可取消 requestId 時提示先以 `operation.cancel` 取消並等待結束，否則提示等待目前作業完成，再重試離場；原生標題列 X 亦走同一流程。一般唯讀動作不佔閘、可與作業併行；`project.heartbeat` 與 `operation.cancel` 必須穿閘。`log.append` 是有界的 current-project database 寫入例外，只能寫 `app_message_log`。另四支篩選命中 query 在空結果需要惰性重建 `result_filter_run` 時，其補算分支會試取同一閘；已有結果的純讀分支仍 concurrent。分類單一事實來源＝Domain `ActionExecutionPolicy`，handler-owned conditional-gate action 由第三類明確登錄 |
| `operation_cancelled` | 目標 request 在完成前收到 `operation.cancel` 並於 cooperative cancellation point 結束。資料庫 transaction 已 rollback，匯出暫存檔已清除；若主要 transaction 已跨過 commit point，系統會完成必要後置狀態並回成功，不以此碼製造半完成中間態 |
| `stale_result` | 匯出 payload 指定的 validate／prescreen runId 或 filter revision 已不是目前有效版本；系統拒絕用新資料冒充舊來源重新產檔，請回到對應步驟重跑／重新完成 |
| `artifact_not_found` | `host.openFolder` 的 artifactId 不存在、不屬目前專案、manifest 相對路徑不安全，或正式檔已不在專案資料夾 |
| `bridge_error` | 其他未分類錯誤（fallback，含未知 action） |

## Demo Pipeline 對齊原則

Demo 契約只供內部測試，runtime 前端在 Debug、Release 與 AgentGuiTest 都不渲染入口、不註冊 handler，也不把它放進任何步驟的使用者流程。測試若使用這份 deterministic fixture，仍必須走與使用者實際上傳相同的 file-based handler pipeline，不得退回 row-based fallback：先取得 metadata 與固定 `.xlsx`，再依序經 `project.create`、`import.*.fromFile`、行事曆、`mapping.commit.*`、`validate.run`、`prescreen.run`、`filter.preview`／`filter.commit`。舊的 `demo.fetch*Rows` 與 row-based import actions 不提供 legacy compatibility。

不可發布的 AgentGuiTest 另有一個封閉啟動 fixture：沒有任意 payload、action、SQL 或 path 參數，只能在該場 harness 配發的隔離 run root 內，以既有 dispatcher／handler pipeline 建立 deterministic export-ready 專案。它不是公開 action、不改任何 wire shape，Debug／Release 不編入；正常 cleanup 後 run root 必須不存在。

## Current Logical Mapping Keys

### GL Mapping Keys

下表的這些 key 由 runtime 前端（`wwwroot`）與 C# handler 共同使用：

| Key | Label | Required | Notes |
|:---|:---|:---|:---|
| `docNum` | 傳票號碼 | Yes | 憑證聚合主鍵 |
| `lineID` | 傳票文件項次 | No | 配對本身非必填；未配對時於投影同一交易內依 `document_number` 分組、`source_row_number` 排序，自動補 1 起的文字項次。已配對時逐字保留來源值；來源值空白仍可落為 NULL。此欄不供內建 validation／prescreen 規則或 INF 抽樣使用，但可由使用者明確選作進階文字篩選欄位 |
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
| `postingStatus` | 過帳狀態 | No | 後端 projection／metadata 已啟用；配對時必須同時提交 `postingStatusPolicy`。Runtime 前端仍未產生控制項，現階段由 contract caller／測試路徑使用 |
| `amount` | 傳票金額（單欄） | Conditional | 與 debit/credit 雙欄位互斥 |
| `debitAmount` | 借方金額 | Conditional | 雙欄位模式 |
| `creditAmount` | 貸方金額 | Conditional | 雙欄位模式 |
| `dcField` | 借貸別欄位 | Conditional | `amountMode` 為 `side` 或 `flag` 時必填；值必須是目前 GL 匯入批次存在的來源欄名 |
| `dcDebitCode` | 借方標識代碼 | Conditional | `amountMode` 為 `side` 或 `flag` 時必填；值是借方代碼字面值，不是來源欄名。比較前先 trim，再以不分大小寫的文字相等判定 |

### TB Mapping Keys

| Key | Label | Required | Notes |
|:---|:---|:---|:---|
| `accNum` | 會計科目編號 | Yes | completeness diff |
| `accName` | 會計科目名稱 | Yes | UI only today |
| `amount` | 年度變動金額 | Conditional | DirectChange mode（`changeMode: "direct"`）— supported |
| `debitAmt` | 借方金額 | Conditional | DebitCredit change mode（`changeMode: "debitCredit"`，變動 = 借方 − 貸方）— supported |
| `creditAmt` | 貸方金額 | Conditional | DebitCredit change mode — supported |
| `openingBalance` | 期初餘額 | Conditional | OpenClose change mode（`changeMode: "openClose"`，變動 = 期末 − 期初，legacy SA=2）— supported |
| `closingBalance` | 期末餘額 | Conditional | OpenClose change mode — supported |
| `openingDebit` | 期初借方 | Conditional | OpenCloseBySide change mode（`changeMode: "openCloseBySide"`，legacy SA=4）— supported |
| `openingCredit` | 期初貸方 | Conditional | OpenCloseBySide change mode — supported |
| `closingDebit` | 期末借方 | Conditional | OpenCloseBySide change mode — supported |
| `closingCredit` | 期末貸方 | Conditional | OpenCloseBySide change mode — supported |

> TB 的金額表示法是條件式的，`changeMode` 四選一，每種模式要求一組必填欄位（缺漏會被 `mapping.commit.tb` 以 `missing_required_mapping` 擋下；跨模式殘留指派由前端切模式時清除、後端投影僅讀當前模式欄位，多餘指派被忽略）：
> - `direct`：`amount` 單欄，直接採用。
> - `debitCredit`：`debitAmt` + `creditAmt`，變動 = 借方 − 貸方。
> - `openClose`：`openingBalance` + `closingBalance`，變動 = 期末 − 期初（legacy SA=2）。
> - `openCloseBySide`：`openingDebit` + `openingCredit` + `closingDebit` + `closingCredit`，變動 = (期末借 − 期末貸) − (期初借 − 期初貸)（legacy SA=4）。
>
> 四種模式的換算結果都進同一個統一比較基準（借正貸負的本期變動 `change_amount_scaled`，與 DirectChange 同語意）。後端先以 `decimal` 完成模式所需的加減，再對最終結果執行一次 MoneyScaling 定標（scaled BIGINT、away-from-zero），避免每欄先捨入造成差異；下游完整性測試（§4）零改動。期初/期末分屬兩檔的案件，須先於 Excel 以科目編號 join 成單一寬表再匯入（JET 的「加入來源」是垂直堆疊、不做 key join）；詳見 guide §2.2。

## Step Data Outline

這份綱要是前端在生成 UI 之前，應該先對齊的資料模型。

前端的步驟模型是 6 步，依序為：建立案件 → 匯入資料 → 欄位配對 → 資料驗證與測試 → 進階條件篩選 → 匯出底稿。「資料驗證與測試」先以 validation 證明母體可用，再在同一步提供 GA 可自由選用的 prescreen 一般風險訊號、摘要、明細與報告入口，也讓 row-tag 述詞可供進階篩選重用；這不表示 filter 計算依賴先前 prescreen run。業務定位見 guide §3。

| Step | 前端需要的資料 | 建議 action |
|:---|:---|:---|
| Step 0 Shell | app name, DB provider, supported actions | `system.ping`, `system.databaseInfo`, `system.whoAmI` |
| Step 1 Project / Import | project metadata, import file names, streaming import columns, holidays, makeup days | `project.create`, `import.*.fromFile` |
| Step 2 Mapping | GL/TB field definitions, uploaded columns, suggested/restored drafts, committed mappings | `mapping.autoSuggest`, `mapping.restoreDraft`, `mapping.commit.gl`, `mapping.commit.tb` |
| Step 3 資料驗證與測試 | `stats`、`populationSummary`、`amountDistribution`、四項資料驗證狀態物件、獨立 `sourceQuality`、十三項預篩選狀態物件、validate／prescreen resultRef、三檔 validation batch 與 Pre-screeningReport artifact（counts 渲染為徽章；na 顯示 `—`；規則以中文名呈現，不用代號；`nullRecordsTest` 四類異常可重複計入且只能標為「異常項次合計」；`sourceQuality.findingCount` 獨立，不與四類相加） | `validate.run` → `export.validationArtifacts`；`prescreen.run` → `export.prescreenReport`；全量明細走 `query.completenessDiffPage` / `query.docBalancePage` / `query.nullRecordsPage` / `query.sourceQualityPage` / `query.infSamplePage` / `query.prescreenPage` |
| Step 4 進階條件篩選 | 條件 AST 草稿（Query Builder 本地組裝）、預覽 `{ count, voucherCount, previewRows ≤50 }`、已儲存情境清單 ≤10、共同 filter revision、科目配對 presence、高風險條件矩陣與 CriteriaSelectionReport artifact | `filter.preview`, `filter.commit`, `export.criteriaSelectionReport`, `query.filterHitsPage`, `query.tagMatrix{Scenarios,VoucherPage,RowPage}` |
| Step 5 Export | 目前有效的 validation runId、scenario revision、所選 scenario positions、目前 prescreen run（有無決定匯出面顯示哪一段定位文案）、project-local artifact feedback、後端權威清理預覽 | `prescreen.run`, `export.prescreenReport`, `export.workpaperStream`, `report.cleanupPreview`, `report.cleanupConfirm`；正式報告流程不使用 `host.selectSavePath`，匯出後不呼叫 `host.openFolder`；全系統僅頁首以 `{ target:"projectFolder" }` 提供唯一手動資料夾入口；清理為行內兩次明示點擊，前端不計算 retention |

## Change Process For New UI Or New Actions

當 agent 被要求新增畫面、重做 UX、或擴充 bridge 時，要依下列順序進行：

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
- Demo／測試繞過 file-based pipeline，或重新引入 `demo.fetch*Rows`／row-based import action
- **Bridge payload / response 攜帶超過 1000 筆明細 row**（大型 GL 母體會炸 JS 端與 postMessage；違反 `docs/jet-guide.md` §1.5）
- **在 Application/Bridge 層對 GL/TB row 集合做 LINQ 計算 V/R/Filter 規則**（必須由 DB 引擎 set-based 處理；違反 §1.5.2）

## Scale-First Contract Baseline

本章定義正式契約的基準。原則有四條：匯入時傳的是檔案路徑、規則執行回的是 summary 加上 `resultRef`、明細一律走 keyset paging、而且 Bridge 的 payload 與 response 都不搬運完整的 GL 或 TB row set。完整背景見 `docs/jet-guide.md` §1.5。

### Ingest 契約基準

| 動作 | Payload | Response | 執行要求 |
|:---|:---|:---|:---|
| `import.gl.fromFile` | 單來源 `{ filePath, fileName?, mode?, sheetName?, encoding?, delimiter? }`；批次 `{ mode?, sources: [{ filePath, fileName?, sheetName?, encoding?, delimiter? }] }` | `{ batchId, rowCount, addedRowCount, columns, sources }` | 後端透過 `ITabularFileReader` streaming 讀檔，整個 `sources` 批次共用 provider transaction；payload 不帶 rows |
| `import.tb.fromFile` | 單來源 `{ filePath, fileName?, mode?, sheetName?, encoding?, delimiter? }`；批次 `{ mode?, sources: [{ filePath, fileName?, sheetName?, encoding?, delimiter? }] }` | `{ batchId, rowCount, addedRowCount, columns, sources }` | 同 GL 整批原子匯入語意；payload 不帶 rows |
| `import.accountMapping.fromFile` | `{ filePath, fileName?, mode? }` | `{ batchId, rowCount, columns, fileName, importedUtc, hasAnyCategory, hasRevenue, hasCounterpart }` | 讀取科目配對來源檔並寫入 staging＋投影 target；內容 bool 由 target 計算；payload 不帶 rows |
| `import.authorizedPreparer.fromFile` | `{ filePath, fileName?, mode? }` | `{ batchId, rowCount, fileName, importedUtc }` | 讀取單欄授權編製人員清單（`.xlsx`）寫入 staging＋投影 `target_authorized_preparer`；payload 不帶 rows |

Registry 不存在 `import.gl`、`import.tb`、`import.accountMapping` 這三個 row-based action，也不承諾相容它們；正式入口只有上表的 file-based actions。

### Query 契約基準（Result Reference + Paging）

所有回傳 `nextCursor` 的 keyset 分頁動作共用同一個到底語意。每頁 `rows` 最多為後端夾擠後的 `pageSize`；只有實際存在下一筆時才回非 null `nextCursor`。因此，當總數剛好是頁大小的整數倍時，最後一個非空頁就必須回 `nextCursor:null`，前端不需要再送一次只得到零列的尾端請求。非 null cursor 只能由本頁實際回傳的最後一列產生，不能使用偵測下一頁的額外列。完整走訪必須不漏列、不重複；零列首頁一律回 `nextCursor:null`。

| 動作 | Response 基準 | 明細讀取 |
|:---|:---|:---|
| `validate.run` | `{ stats, populationSummary, amountDistribution, 四項資料驗證狀態物件, sourceQuality, resultRef }`（Implemented，見 Validation 章節） | `query.completenessDiffPage`／`query.docBalancePage`／`query.nullRecordsPage`／`query.sourceQualityPage`／`query.infSamplePage` |
| `prescreen.run` | `{ 十三項預篩選狀態物件, resultRef }`（Implemented，見 Prescreen 章節） | `query.prescreenPage` |
| `filter.preview` | `{ scenario: { name, populationScope, count, voucherCount, previewRows } }`，`previewRows` ≤ 50；preview 本身無狀態、無 `resultRef`（命中落地屬 `filter.commit` 的 materialize 與分頁查詢的惰性補算） | `query.filterHitsPage` |
| `filter.commit` | `{ ok, savedCount, scenarios: [{ source, name, rationale, groups, populationScope, savedUtc }], resultRef: { revision, generatedUtc, logicVersion, populationScope } }`（definition replace-all 與 `result_filter_run` materialize 在同一 provider transaction；`scenarios` 與 `project.load.filterScenarios` 共用正準 renderer） | — |

### 分頁與匯出動作

| 動作 | Payload | Response | 用途 |
|:---|:---|:---|:---|
| `query.glPage` | — | — | **2026-07-10 除名**：標準化 GL 的有界觀察走 `query.dataPreview`；底稿明細由 writer 直接使用 repository keyset 串流。Legacy 沒有獨立的全 GL 瀏覽流程，不建立無 UI 消費端的重複 action |
| `query.validationDetailsPage` | — | — | **2026-07-10 除名**：由五支具體 validation page actions 取代 |
| `query.prescreenPage` | `{ ruleKey, cursor?, pageSize? }` | `{ rows[], nextCursor }` | 預篩選規則完整明細分頁；`ruleKey` 用命名登錄表的 wire key，row shape 見 Prescreen 章節 |
| `query.filterPage` | — | — | **2026-07-10 除名**：由 `query.filterHitsPage` 取代 |
| `export.validationArtifacts` | `{ runId }` | `{ ok, artifacts }` | 以目前有效 validation run 原子發布 ValidationReport、AccountMapping、INF_Report；三檔全成才更新 manifest |
| `export.accountMappingTemplate` | `{ runId }` | `{ ok, artifact, rowCount }` | AccountMapping 單檔重試；仍只寫專案目錄 |
| `export.prescreenReport` | `{ runId }` | `{ ok, artifact }` | 以目前有效 prescreen run 直接填固定範本；R1–R4／R7 僅在 `0 < rowCount < 10000` 讀取並輸出明細，超量時 summary-only |
| `export.criteriaSelectionReport` | `{ validationRunId, revision }` | `{ ok, artifact }` | 以目前有效 validation run 與 filter revision 直接填固定範本；逐情境輸出命中傳票的全部原始 GL 分錄且不套 10,000 門檻；artifact 另綁目前全部 scenario positions，不綁 prescreen run |
| `export.workpaperStream` | `{ validationRunId, scenarioRevision, scenarioPositions }` | `{ ok, artifact, sheetStats: [{ sheetName, rowsWritten }] }` | 先複製固定方法學範本，再以 OpenXML forward-only SAX 直接重寫 12 張授權動態 worksheet parts；Intro／Step5 與未授權 package parts 不變，不建立 current-result workbook 或第二 workbook merge。`scenarioPositions` 只選納入的情境，不選工作表。validation、filter revision 與 positions 必須屬目前有效版本；不要求 prescreen run，也不接受 `sheets` 或 `outputPath`。一般大型表仍以 keyset paging 串流；finalized production step4-1 則以單一 dedicated connection／transaction 一次 materialize 命中傳票與 row tags，再用一個 ordered reader 寫 exact typed disk spool，資料庫 session 關閉後才 SAX replay；固定六個 SQL command attempts、零 typed page calls，不隨 continuation 數增加。同目錄 temp 完整關閉後才原子發布並更新 artifact manifest |

WorkingPaper 的方法學工作表家族由 writer 固定控制；只有 step1-3 依完整性差異決定是否產生，已移除的 `step1-3-1` 不在 catalog、plan、writer 或輸出。前端沒有工作表 catalog，也不能裁切固定版面。step3 固定為 B:E；step4 固定保留 C1–C10 槽位，每張命中傳票只出現一列，且只對本次 `scenarioPositions` 選入的情境標記。step4-1 的可見欄位與 native value/type 由 finalized backend plan 形成，前端與 public tag-matrix wire 不承載 schema。三張參考頁中，Field Info 與 ValidationReport 共用 canonical target TableDef projection；科目配對顯示 project taxonomy label，規則判定只讀 semantic role。六份報告一律由 project-local artifact store 產生固定檔名，wire 只回傳 artifact metadata 與相對 `fileName`，不回傳絕對路徑。

### Result Reference 概念

Validation／prescreen 的 `resultRef` 形狀是 `{ runId, generatedUtc, logicVersion }`；filter 的 `resultRef` 形狀是 `{ revision, generatedUtc, logicVersion, populationScope }`。`logicVersion` 讓持久化摘要或情境只能搭配相容的規則與明細 SQL；`populationScope` 讓命中、矩陣與報告可追溯到同一母體。缺漏或版本不符時，`project.load` 不回放該 run／filter reference，匯出也會拒絕。這些 reference 有三個用途：

1. 後續的分頁 query 用 `runId` 鎖定同一次執行的結果，避免重跑時讀到前後不一致的資料。
2. CriteriaSelectionReport 同時鎖定 validation runId、revision 與目前全部 scenario positions。WorkingPaper 使用相同兩種版本，再加上這次選入的 scenario positions 子集；Pre-screening Report 才鎖定 prescreen runId。
3. artifact 的 `sourceRef` 保存各報告真正使用的 opaque reference。validation 或 filter 失效會使 CriteriaSelectionReport／WorkingPaper stale；prescreen 失效只影響 Pre-screening Report。舊檔與舊版 sourceRef 原樣可載入，不因新契約重寫；同一來源重匯則新舊檔都維持有效。
