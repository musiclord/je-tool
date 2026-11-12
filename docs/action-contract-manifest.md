# JET action 介面契約

更新日期：2026-11-10

這份文件是前端、WebView2 Bridge 與 C# 處理器之間的 action 登錄表。它適合用來搜尋 action 名稱，
不需要逐段閱讀。

精確的 `payload`（參數）與回應格式，應由對應處理器、Domain 契約和測試共同確認。修改 action 時，
必須在同一個變更中更新本檔、`wwwroot/js/jet-api.js`、處理器註冊與相應測試，不能只改其中一邊。

正式後端實際收到與回應的欄位和型別，由 `ActionShapeContractTests` 走一遍合成案件後記在
`src/JET/tests/JET.Tests/Fixtures/action-shapes.json`，前端測試的假後端都用它核對。改了 `payload` 或回應的欄位時，
照 `tools/README.md` 的說明重新產生這個檔案。

## 請求與回應的外層格式

前端送出：

```json
{
  "requestId": "<unique id>",
  "action": "<namespace.action>",
  "payload": {}
}
```

成功回應：

```json
{
  "requestId": "<same id>",
  "ok": true,
  "data": {},
  "error": null,
  "correlationId": "<server-generated id>"
}
```

失敗回應：

```json
{
  "requestId": "<same id or empty when the request cannot be parsed>",
  "ok": false,
  "data": null,
  "error": {
    "code": "<stable code>",
    "message": "<human-readable message>",
    "field": "<optional payload field>",
    "details": [{ "group": 1, "rule": 2, "message": "<one positioned error>", "sourceColumn": null }]
  },
  "correlationId": "<server-generated id>"
}
```

`error.field` 只能由後端在能明確指出欄位時提供。前端不得從錯誤文字猜欄位；目前建案名稱錯誤會
使用 `projectName`。`error.details` 出現在兩種錯誤。`invalid_scenario` 這類能歸屬到條件列的錯誤，每一項帶第幾組、
第幾條（從 1 起算，無法歸屬時為 null）與那一條的原因；`message` 仍是全部原因合併的整段文字。步驟五用
`details` 把對應的條件列標紅並就地說原因，沒有 `details` 的錯誤維持整段訊息。
篩選情境的 `invalid_scenario` 每一項另帶 `scenarioPosition`（錯誤屬於清單上第幾個情境，從 1 起算）與
`scenarioName`（那個情境的名稱）；預覽與其他錯誤不帶這兩個鍵。步驟五只在錯誤屬於正在儲存的情境時標紅條件列，
屬於別的情境時在儲存按鈕上方寫出是哪一個情境。
`mapping.commit.gl` 與 `mapping.commit.tb` 的 `projection_failed` 也帶 `details`：每一項是同一個來源欄、同一種問題，
`group` 與 `rule` 為 null，`sourceColumn` 是那個來源欄，`message` 寫出有幾列、哪些值在哪幾列，以及一次處理方式。
`message` 是開頭一句「有 N 列的值無法讀取，本次配對未儲存。」，清單只列出一部分時再接「以下僅列出部分的值與列號。」，
然後接上各項文字；每項以「來源欄「X」」開頭。
第三步逐項列出，並依 `sourceColumn` 提供「前往設定」；其他錯誤的 `sourceColumn` 為 null。
`correlationId` 串起同一次 action 的開始、結束或錯誤事件；Debug 組建在 picker 上開案或刪除失敗時，
「輸出診斷日誌（DEV）」用它帶出該次紀錄。picker 的「輸出支援日誌」不帶 `projectId` 與 `correlationId`，
匯出全部去識別紀錄。完整錯誤碼登錄位於 `Domain/JetActionException.cs`。

## 主程式進度事件

主程式可以用 `{ "event": "<name>", "data": {} }` 傳送單向進度。事件沒有 `requestId`，也不代表成功。

| 事件 | 用途 |
|:---|:---|
| `import.progress` | 回報目前來源與已讀列數 |
| `mapping.progress` | 回報投影已處理列數 |
| `export.progress` | 回報產物、階段、工作表與累計列數 |

完成、失敗與取消都以原 action 的回應為準。前端不得從最後一個進度事件推算結果。

## 使用規則

- UI 只透過 `JetApi` 呼叫 action，不直接使用 WebView2 傳輸層。
- Bridge 驗證外層格式、處理取消並轉送給處理器，不做審計判斷。
- 已要求取消後，處理器丟出的例外除了業務錯誤，dispatcher 一律回 `operation_cancelled`（裁定 2026-10-16-23）。
- 會變更案件資料的長作業由後端執行閘門協調；取消有明確的例外規則。
- 大量資料只回摘要、中繼資料與有界分頁。`query.*Page` 使用游標與每頁筆數，不回完整母體。
- 進度事件不攜帶資料列，也不是第二個狀態來源。
- 原生選檔、開資料夾、退出與取消控制保留呼叫執行緒；其他 action 及其資料庫保留範圍由 dispatcher 排到背景執行，不改原有互斥分類。
- Debug／AgentGuiTest 專用 action 不得出現在 Release 的使用者流程。

## 目前支援的 action

### 科目清單直接分類

`query.accountMappingPage` 回傳本案科目與已保存分類，每頁預設 200 筆，上限也是 200 筆。
參數為 `cursor`、`pageSize` 與選用的 `search`、`categoryId`；科目來源與 Excel 配對範本相同，並保留先前匯入的額外科目。
回應 `rows` 含 `accountNumber`、`accountName`、`categoryId`，另回 `nextCursor` 與 `totalCount`。總筆數只在第一頁計算，包含目前搜尋文字和分類下的全部科目；帶游標的續頁 `totalCount` 為 null。尚無配對的 `categoryId` 為 null；
Excel 空白分類則沿用既有的 Others 處理方式，清單顯示其目前分類。
科目代號空白的來源列仍保留在資料驗證中，不列為可指定分類的科目。

`accountMapping.save` 接收 1 至 500 筆 `changes`，每筆含 `accountNumber` 與 `categoryId`。
只更新指定科目，其他頁及 Excel 已匯入的分類保留。科目或分類不存在、重複科目及取消均不得留下部分變更。
每次變更保留獨立批次及來源列，原匯入列不改寫。儲存及預篩選、篩選結果失效在同一交易完成，資料驗證結果保留。
回應保留科目配對的既有狀態欄位；直接編輯後的 `rowCount` 為目前已保存的科目數。
回應另有 `warnings` 字串陣列，平常是空陣列。分類已存好、卻讀不回科目配對狀態時，回應仍成功，但不帶 `batchId`、`rowCount`
等狀態欄位，`warnings` 是「變更已儲存，但畫面上的科目配對摘要沒有更新；重新開啟案件就會更新，不必重做變更。」，
前端保留畫面上原本的摘要；`invalidatedResults` 等變更共同欄位照常回傳。存檔之後的讀取不受取消影響。
支援日誌記一筆 `workflow_state.read_failed`，讀取階段寫 `account_mapping_state`（裁定 2026-10-19-21）。
配對檔與本案科目的兩項差異筆數只由檔案匯入回應及差異清單查詢提供，這個動作不另外掃描 GL。
此動作為互斥寫入，清單查詢為唯讀。Excel 匯入仍整份取代科目配對。
`query.accountMappingPage.categoryId` 指定分類時，查詢包含該分類及其所有下層，與搜尋文字同時篩選。仍只回有界分頁，不回整份科目表。
第一個正式版的畫面建立不了上下層（裁定 2026-11-03-27），分類沒有下層時查的就是該分類本身。
第五步科目編號清單沿用既有 100 個值上限；選入整個分類時，若分頁尚有下一頁或合併後超限，保留原清單並提示縮小範圍或改用科目分類條件，不只加入第一頁。

以下名稱逐字對應 `wwwroot/js/jet-api.js` 的 `SUPPORTED_ACTIONS`。

### 系統與取消

- `system.ping`
- `system.whoAmI`
- `operation.cancel`

`system.whoAmI` 回 `principal` 與 `shortName`。原本另有查 SQL Server 設定的 `system.databaseInfo`；`system.whoAmI` 原本也回
從 SQL Server 取得的使用者編號 `userNumber` 與它的來源 `numberSource`。兩者都隨 SQL Server 暫時移除（裁定 2026-11-06-08）。

### 案件

- `project.listLocal`
- `project.create`
- `project.update`
- `project.load`
- `project.deletePreview`
- `project.delete`
- `project.saveProgress`
- `project.releaseLock`
- `project.loadDemo`（開發／GUI 測試專用）

原本另有同步線上案件的 `project.list` 與線上案件的心跳 `project.heartbeat`，隨 SQL Server 暫時移除（裁定 2026-11-06-08）。

正式前端建立案件時，`projectName`、`periodStart` 與 `periodEnd` 必填，`projectCode`、`clientName`、
`prepStartDate` 選填。建案一律建立 SQLite 案件，畫面不送 `databaseProvider`；送了也只接受 `sqlite`，其他值回 `invalid_payload`
（裁定 2026-11-06-08）。名稱撞到以前建立的 DuckDB 或 SQL Server 案件時回 `invalid_payload`，`error.field` 是 `projectName`，
句子說明這個名稱被不列在清單上的舊案件佔用，請改名或到 JET 的案件位置刪除那個資料夾（裁定 2026-11-06-09）。
資料庫種類建立後不能更改。操作人員不由表單提供；
`ProjectCreateHandler` 直接使用目前 `CurrentPrincipal.ShortName`。舊版呼叫者即使仍送 `operatorId`，後端也不採用。
案件編號或客戶名稱留空時，`project.json` 仍保留空字串欄位，讓既有案件、清單與報告使用同一份資料形狀。

`project.listLocal` 只列 SQLite 案件，回應頂層只有 `projects`。以前建立的 DuckDB 案件，以及 SQL Server 案件留在本機的資料夾不列出，
JET 也不動這些資料夾：`project.json` 記錄 SQLite 以外的種類，或沒有記錄種類、資料夾裡只有 `jet.duckdb` 的案件都算（裁定 2026-11-06-09）。
清單不隱藏無法讀取的 SQLite 案件與判斷不出種類的案件。正常列格式不變；損壞列只回
`{ projectId, databaseProvider: null, loadError: { code, message } }`，說明檢查鎖檔、從備份復原或另建案件重新匯入，
不捏造資料庫種類、名稱、期間或時間。其他正常案件仍可開啟。
本機案件的資料庫檔不見或是 0 位元組時也列成錯誤列，`code` 是 `file_read_error`；建案中的標記還在時照常列出（裁定 2026-11-05-72）。
設定檔的 `projectId` 與實際資料夾名稱不同時，也列成錯誤列，識別碼取資料夾名稱，刪除只作用於該資料夾（裁定 2026-11-10-01）。
`moneyScale` 缺少或不是正數時沿用設定檔無法讀取的錯誤，不補預設值。

建案時查核起始日不得晚於截止日，否則回 `invalid_payload`，`field` 為 `periodStart`，不建立案件。
既有案件的期間格式不合法或起日晚於迄日時，載入回 `invalid_project_schema`，提示從備份復原或另建案件重新匯入。
檢查在取得案件鎖及切換作用中案件之前完成，不自動改寫日期。
正式畫面送來的空白 `projectName`（包括全形空白）回 `invalid_payload`，不改成隨機案件名稱；程式化呼叫未提供名稱時仍可沿用既有識別值產生方式。
`project.create` 的成功回應另帶 `warnings` 字串陣列：準備日早於查核起始日，或晚於查核截止日超過一年時，提醒確認年份但不阻擋。

`project.update` 修改作用中的案件，只讀 `clientName`、`projectCode` 與 `prepStartDate`。省略欄位保留原值，null 或空字串清除該選填值；
案件名稱、查核期間、建案者與資料庫種類不變，其他 payload 欄位不採用。回應包含 `project`、`warnings`、`invalidatedResults`、`staleState`、`reports` 與 `reportWarning`。
`invalidatedResults` 由後端的失效政策提供本次需清除的範圍，前端據此清掉尚未儲存的舊預覽；`staleState` 只描述已持久化結果的過期狀態。
只有準備日實際改變時，才依既有失效政策清除預篩選、篩選命中及全部已存篩選情境，保留驗證結果與配對；同值重送或只改文字不清除審計結果。
任一可改欄位真正改變時，既有報告索引標為過期，但不改寫或刪除已匯出的檔案；再次匯出才使用新資料。這是互斥寫入。
`project.load` 與 `project.update` 回應的 `project.rocDateEnabled` 帶回案件既有的民國年解析選項，供日期條件使用；這不是可由 `project.update` 修改的新欄位。
`project.load` 的 payload 只需 `projectId`。原本另可選填 `databaseProvider`，用來找只存在伺服器上的 SQL Server 案件，
回應也帶心跳間隔 `heartbeatSeconds`，都隨 SQL Server 暫時移除（裁定 2026-11-06-08）。
本機沒有這個案件資料夾時回 `project_not_found`；資料庫種類以 `project.json` 為準，不是 SQLite 時在取得案件鎖之前回 `unsupported_provider`。
本機案件的資料庫檔不見、是 0 位元組或沒有任何資料表時，`project.load` 回 `file_read_error`，不建立新的資料庫（裁定 2026-11-05-72）。

`project.load` 回應的 `mapping.gl` 另含 `availableGlFields`，是已確認的 GL 配對裡有來源的 GL 語意欄位，例如 `voucherNumber`、`createdBy`。
清單由 `JetFieldCatalog.AvailableGlFields` 算出：欄位有來源欄，或欄位目錄標示它有衍生來源，就列入。傳票文件項次 `lineItem`
一律列入，因為沒有來源時由系統自動編號；傳票核准日 `approvalDate` 在 `approvalDateMode` 為 `sameAsGlDate` 時也列入。
`mapping.commit.gl` 的成功回應在最上層帶同一份清單。前端把它存在已確認的 GL 配對上，第五步凡是用到清單外欄位的入口，一律停用；
清單只供畫面呈現，第五步的驗證與預篩選在後端用同一個方法重新算，不讀前端送的資料。

`project.load` 回應另含 `filterScenarioCheck: { status, recalculatedCount, problems }`，說明開案時用目前篩選規則
檢查已儲存情境的結果。已是目前規則版本的情境也逐一檢查（裁定 2026-10-13-25）。`status` 為 `current`（沒有情境，或都已是目前規則而且檢查合格）、`recalculated`（整批已改用目前規則，
`recalculatedCount` 是情境數，舊命中已清除，條件篩選報告與底稿依新的 revision 標為過期）、`needsEdit`
（至少一個情境不符合目前規則，或條件內容讀不出來，整批不動，`filterResultRef` 為 null）或 `inconsistent`（位置重複或超出範圍，
整批不動）。每個情境各自儲存，各情境的保存時間可以不同，不算成不一致（裁定 2026-10-30-33）。條件內容讀不出來的情境也逐筆列在 `problems`，審計員可以移除它；
這個情境在 `filterScenarios` 裡的 `groups` 是空陣列。
`problems` 每筆為 `{ position, name, messages }`，只在 `needsEdit` 時有內容。`messages` 只寫原因，不寫下一步；
損毀的條件不論幾條，同一個情境只有一句「條件內容無法讀取」，不含英文代碼（見下方「進階篩選」的驗證失敗說明）。改版發生在戳記上次開啟時間之前，
之後載入失敗時改版仍保留。`filter.scenario.save`、`filter.scenario.remove` 與 `filter.scenario.recheck`
的回應也帶同樣形狀的 `filterScenarioCheck`，`status` 只有 `current` 或 `needsEdit`，見下方「進階篩選」。

`project.load` 回應另含 `previousMapping: { gl, tb }`。重新匯入 GL 或 TB（取代或附加）會讓已確認的配對失效；
匯入時把失效前最後一次確認的配對另存一份，那一側還沒重新確認時，這裡回傳它，形狀和 `mapping.gl`、`mapping.tb`
相同，但不帶 `sourceBatchId`。它不是有效配對，驗證、篩選與匯出都不讀它，`mapping.commit.gl` 只從中沿用攸關資料元素
欄位識別碼（見下方欄位配對一節）。前端只拿來預填草稿，只保留新資料仍有的來源欄，
並把新資料沒有的欄位寫出來請審計員重新選。那一側有有效配對、沒有匯入資料，或從來沒有確認過配對時為 null；
存下的那一份讀不出來時（例如配對格式改版或內容被改壞）也是 null，開案與確認配對照常。

`project.load` 不會因為報告檔而拒絕載入。報告檔在 JET 之外被改寫、改名或刪除時，回應的 `reports`
每筆多一個 `fileState`（`asPublished`、`modifiedOutside`、`missing`）讓前端標示，重新匯出即可。
`project.delete` 只取得案件外的報告租約，刪案不受報告檔狀態影響。
設定檔損壞、舊版或查核期間無效而無法開啟的案件，`project.delete` 仍可刪除：確定是 SQLite 案件時，也就是設定檔記錄 SQLite，
或沒有記錄種類、資料夾裡有 `jet.db` 而沒有 `jet.duckdb` 時，照一般流程刪除資料庫與資料夾。記錄的是其他種類或判斷不出來時，
回應原本讀取設定檔的錯誤，不刪任何資料。設定檔讀得出來、記錄 SQLite 以外的種類時，在動任何檔案之前回 `unsupported_provider`，
資料夾保持原狀（裁定 2026-11-06-09）。

`project.deletePreview` 的 payload 只需 `projectId`，回應為 `{ projectId, databaseProvider, reportCount, workingPaperCount }`。
這是可併行的唯讀動作，不開啟案件、不切換 session，也不持有目前案件的資料庫。
計數只包含 JET 索引中仍存在的相異檔名，外部修改過的檔案仍計入，已遺失檔案及科目配對工作檔不計入報告數。
前端另明說整個案件資料夾及其他檔案都會刪除；計數失敗顯示未知及重試，不假設零份，也不增加刪除關卡。

GL、TB、科目配對、授權編製人員清單、國定假日與補班日匯入，GL、TB 配對確認，科目分類或配對儲存、授權編製人員清單清除、每週休息日設定與案件資料更新，
成功回應共同帶 `invalidatedResults: { validation, prescreen, filter, filterScenarios, accountMapping }`、`staleState`、`reports` 與 `reportWarning`。
前者是本次需要清掉的結果範圍，包含未儲存的預覽；後者只描述已保存結果是否過期。前端不再用 action 名稱或 setter 自行推算失效範圍。
變更已提交、卻讀不回目前結果狀態時，回應仍成功：`staleState` 與 `reports` 回 null，前端保留畫面上的值，
`reportWarning` 說明變更已儲存、重新開啟案件就會更新。支援日誌記一筆 `workflow_state.read_failed`，只寫讀取階段與錯誤類型。
`filterScenarios` 為 true 時，後端已刪除全部已存篩選情境與命中，前端應把第五步回到預設狀態。
`accountMapping` 為 true 時，後端已在來源修改的同一交易清除科目配對、原始配對列及配對匯入狀態，分類定義保留。
GL 與 TB 的取代、附加及重新確認欄位配對會回傳 true，其他事件不清除科目配對。前端同步清除配對狀態、勾選與未儲存分類，重新讀取現行科目。
失敗或取消時，來源與科目配對一併回復，不回傳成功的清除結果。
`filterScenarios` 和 `filter` 同值。GL、TB、科目配對範本、授權編製人員清單與國定假日與補班日的匯入一律為 true，不比對檔案內容是否和上次相同；
GL 或 TB 配對確認、儲存科目分類或科目配對、清除授權編製人員清單也一律為 true。只有每週休息日與財報準備日會先比對，同值重送時為 false；
案件資料只改文字時也是 false。同一步驟的同類事件使用同一條規則，匯入不比對內容（裁定 2026-10-07-22）。
GL 與 TB 的匯入或配對確認都回傳五個 true，使用同一條失效規則。
這是上游修改時清除下游的規則（裁定 2026-10-07-03、2026-10-07-04）。
情境已清除時 `staleState.filter` 回到 false，表示沒有可重跑的篩選，而不是待重跑。已匯出的報告與底稿檔不改寫，索引照舊標成過期。
資料庫內的修改和清除在同一筆交易完成，修改失敗時一起回復，情境與命中都保留。
財報準備日存在 `project.json`，無法和資料庫共用交易。清除結果的交易先不提交，報告索引標過期、新設定存好後才提交；
存檔失敗時交易回復，情境、命中與預篩選結果都保留，舊日期不變。提交失敗時寫回舊設定。存檔失敗前報告索引可能已標過期，寧可多標，不會漏標。
清單刷新包含重新判定報告過期，最多等待 3 秒；失敗時回 null 並提示變更已保存，前端保留舊清單。匯出成功後的清單也先重新判定過期，
失敗只提示檔案已產生，不要求重做匯出。條件篩選報告與底稿一樣記錄 `sourceRef.filterDataRevision`；舊報告缺這個版本時視為過期，不重寫檔案。
`project.load` 回應的 `validationResult` 與 `prescreenResult` 是最近一次 `validate.run` 與 `prescreen.run` 的回應，沒有結果時是 null。
驗證結果與預篩選結果的編號都寫在回應的 `resultRef.resultId`，
`export.validationReports` 與 `export.prescreenReport` 的 payload 以必填的 `resultId` 指定依據哪一次結果，
不是目前結果時回 `stale_result`。
`project.load` 會把舊規則版本的驗證或預篩選摘要改回 null，並在 `staleState` 明示過期，不冒充從未執行。
摘要不是合法 JSON，或裡面有讀不出來的文字時（例如內容在 JET 以外被改壞），也一樣改回 null 並標成過期，重新執行就恢復。
驗證或預篩選摘要缺欄、欄名重複或型別不對時，對應結果同樣回 null 並標成過期，相關報告索引也過期；已匯出的檔案保持原狀（裁定 2026-11-10-02）。
預篩選仍保留既有舊摘要的回放支援：當時沒有提供的區塊不補造，已有區塊的必要欄位與型別會檢查；顯示用的 `positioning` 由目前程式刷新。
舊摘要缺少匯出所需內容時，匯出回 `stale_result` 並請重新執行預篩選，不補成零命中。
已存 JSON 超過 60 層時也當成無法讀取，避免到回應寫出時才失敗。結果列的時間無法解讀時也回 null 並標過期；重新執行成功時，在保存新結果的同一交易清除日期無效的舊結果列，有效舊結果保留。情境列的時間或整數無法解讀時回既有 `database_corrupt`，不捏造日期與編號。

### 合成 demo 檔案

- `demo.exportGlFile`
- `demo.exportTbFile`
- `demo.exportAccountMappingFile`
- `demo.exportAuthorizedPreparerFile`

這四個 action 與 `project.loadDemo` 只供 Debug 或不可發布的 AgentGuiTest 組態使用。

### 匯入、國定假日與補班日

- `import.gl.fromFile`
- `import.tb.fromFile`
- `import.accountMapping.fromFile`
- `import.authorizedPreparer.fromFile`
- `import.authorizedPreparer.clear`
- `import.inspectFile`
- `import.previewFile`
- `import.holiday`
- `import.makeupDay`
- `import.holiday.fromFile`
- `import.makeupDay.fromFile`
- `calendar.setNonWorkingDays`

科目配對可沿用前一案件的檔案。`import.accountMapping.fromFile` 保留原有回應欄位，另回
`mappingOnlyCount`（配對檔有、本案沒有的科目數）及 `unmappedCount`（本案有、配對檔未列的科目數）。
本案科目是有效 GL 與 TB 的聯集，排除空白科目編號，不包含配對檔帶來的額外科目。
檔案有列但分類留白的科目仍視為 Others，另由 `blankCategoryCount` 計數；額外科目照常保存，不阻擋匯入。
兩項新筆數不存進開案摘要；清單查詢見下方 `query.accountMappingDifferencePage`。匯入完成後才讀取筆數，
讀取失敗不撤銷匯入，兩個值回 null，支援日誌記下摘要失敗。畫面顯示「尚未核對」，展開清單時重新查詢。

`import.accountMapping.fromFile` 另可帶 `sheetName`，指定 .xlsx 裡要讀的工作表，例如工作底稿的「自動化工具-科目配對資訊」
（裁定 2026-11-05-07）。沒有帶時照舊讀第一個工作表；文字檔帶 `sheetName` 以 `sheet_name_not_applicable` 拒絕。
畫面選了 .xlsx 會先送 `import.inspectFile` 讀工作表清單，有欄名的工作表超過一個時請審計員選，選定後才送匯入。
工作底稿在 GL 有、TB 沒有的科目，科目名稱一格寫「Not in TB」；匯入時這一格當成沒有科目名稱。
JET 產生的科目配對範本也照舊版在這些科目的名稱欄寫「Not in TB」，和底稿一致；試算表也有的科目照舊寫試算表名稱。
範本匯回時同樣把「Not in TB」當成沒有科目名稱（裁定 2026-11-05-40、2026-11-05-10）。

科目配對先辨識確切欄名，去頭尾空白且不分大小寫：科目編號為 `GL_Number`（含 `GL_NUMBER`），
科目名稱為 `GL_Name`（含 `GL_NAME`），分類為 `Standardized Account Name*` 或 `STANDARDIZED_ACCOUNT_NAME`。
三欄都先認領，再依分類、科目編號、科目名稱的順序比對尚未認領欄的中英文關鍵字；仍找不到的欄，依剩餘欄位順序補上。
只要有欄位依順序判斷，匯入回應的 `columnMappingWarning` 就會說明來源欄與用途，畫面顯示提醒但不阻擋匯入；否則回 null。
回應另有 `warnings` 字串陣列：科目配對範本是文字檔且有用引號包住、內含換行的欄位時，加一則和 GL、TB 匯入同一句的提醒，
支援日誌也記同一種 `import.multiline_fields` 事件；沒有時是空陣列。
匯入與舊案遷移共用此規則。資料預覽一律讀已儲存的科目配對，不重新判讀欄名。
Excel 的 autoFilter 與隱藏列不限制匯入範圍，資料列仍全部讀入。
`.xlsx` 科目配對範本的第一個標頭不足三欄時，沿原有做法改讀第三列標頭；略過前兩個實際列號，空白列也算在內，
不會略過第三列標頭或第一筆資料。

GL 與 TB 的主資料匯入支援 `.xlsx`、`.xlsm`、`.xls`、`.csv`、`.txt`、`.mdb` 與 `.accdb`。
`.xlsm` 和 `.xlsx` 共用唯讀 Open XML 讀取器，`.xls` 使用逐列 BIFF 讀取器，都不啟動 Excel 或執行巨集。
Access 透過公司既有 Office 資料介面唯讀讀取一般資料表；`sheetName` 指選定資料表。`.xlsb` 不支援。
一個 GL 或 TB 資料集可以追加多個欄位相同的檔案或工作表；這只會合併列，不會把兩份 TB 自動解讀成期初與期末。
畫面一次選多個檔案或工作表時，把勾選的來源依序放進 payload 的 `sources` 陣列，以一個 action 送出，GL 與 TB 相同（裁定 2026-11-09-19）。
`mode` 是 `replace` 或 `append`，省略時是 `replace`。同一批每個來源的欄名集合要相同，不同時以 `column_mismatch` 拒絕；任一來源讀不出來時，整批都不寫入。
追加時每個來源也要和已匯入的資料欄名相同。兩種情況共用同一句訊息，寫出是哪個檔案、多出與缺少哪些欄，並說明本次未匯入、先前匯入的資料不受影響（裁定 2026-11-09-22）。
GL 與 TB 匯入成功回應的 `warnings` 為字串陣列。追加來源的實際檔名、工作表與列數都和某份既有來源相同時，
提醒來源檔名、工作表及列數皆相同、已照常加入，請使用者確認是否重複匯入；取代模式不提醒，不以預估列數判斷，也不把這個提醒當成去重功能。
文字檔來源有用引號包住、內含換行的欄位時，每個來源再加一則提醒，寫出列數與前五個列號，匯入照常完成；
支援日誌另記一筆 `import.multiline_fields` 事件，只含來源序號、列數與列號，不含檔名或內容。
以取代模式重新匯入既有資料時，JET 寫一筆操作紀錄。寫不進去時資料仍已匯入，回應照常成功，
`warnings` 另加「變更已儲存，但這次變更沒有寫入操作紀錄。」（裁定 2026-10-19-19）。

文字檔（`.csv`、`.txt`）照一般 CSV 讀法：只有欄位開頭的引號會被當成引號，欄位中間的引號（例如英吋符號）是一般字元，
不會把後面的列併進來；欄位開頭的引號到下一個引號之間可以換行，讀成同一格。
引號沒有成對時 `import.previewFile` 與匯入以 `file_read_error` 失敗，訊息寫出第幾列。
開頭的空白列會略過，第一個有內容的列是欄名。標頭之外有資料的欄合成 `COL_n` 佔位欄，和 Excel 讀取器相同。
`import.inspectFile` 與 `import.previewFile` 的回應多一個可空的 `notices` 字串陣列，是給審計員看的提醒，不擋匯入；
目前只有文字檔只讀到一欄時回「可能是報表格式」這一則。`.xls` 的錯誤儲存格（`#N/A`、`#REF!` 等）保留 Excel 顯示的原文，
和 `.xlsx` 相同；`.xls` 與 Access 的是或否值寫成 `true`、`false`，只有時間的值用 `hh:mm:ss`，也和 `.xlsx` 相同。
來源檔沒有權限、解碼失敗或結構讀不通時，`file_read_error` 的訊息只寫檔名與下一步，不寫完整路徑，
也不含 .NET 的英文例外文字。這兩個 action 失敗時和匯入一樣附上支援日誌的匯入診斷欄位；`import.inspectFile` 成功時
另記一筆 `import.inspect` 事件，只含格式、欄數、工作表數、編碼與分隔符。解碼失敗時，診斷欄位的 `encoding`
寫實際用來解碼的編碼；自動偵測編碼時也一樣，不寫 `unknown`。

Excel 與文字檔的來源檔正被其他程式開著時，JET 照樣讀這個檔案最後一次存檔的內容（裁定 2026-11-06-12）。GL、TB、科目配對、
授權編製人員清單、國定假日與補班日的匯入，在讀檔前先判斷每個來源檔是否被別的程式以可寫入的方式開著；是的話，成功回應的
`warnings` 加一則「檔案「檔名」正被其他程式開啟（例如 Excel）。JET 匯入的是這個檔案最後一次存檔的內容，尚未存檔的修改沒有匯入。」，
同一個檔案只提醒一次。`import.authorizedPreparer.fromFile`、`import.holiday.fromFile` 與 `import.makeupDay.fromFile` 的回應因此新增
`warnings` 字串陣列，沒有提醒時是空陣列。這六個匯入的回應另有 `openElsewhereWarnings` 字串陣列，只列開著檔案的那幾則，
句子和 `warnings` 裡的相同；畫面把它放在那一項的匯入結果旁，`warnings` 照舊全部記進訊息面板。
`import.inspectFile`、`import.previewFile` 與 `mapping.restoreDraft` 也讀得到開著的檔，但不提醒。

`import.previewFile` 的 payload 與 `import.gl.fromFile`、`import.tb.fromFile` 的每個來源都可以帶 `headerRow`，指欄名在第幾列，
從 1 起算，以檔案的實際列號計算，空白列也算；省略時是 1，和以前一樣取第一個有內容的列當欄名。Excel 與文字檔都接受；
Access 資料表的欄名來自資料表本身，帶了以 `invalid_payload` 拒絕，不是正整數也拒絕。欄名列起沒有任何內容時以 `empty_workbook` 拒絕，
訊息寫從第幾列起沒有內容。它只解決表頭：欄名列之前的列全部略過，
表尾的合計列仍會被當成資料。匯入成功回應與 `project.load` 的 `importState` 裡，每個來源的 `sources[].headerRow` 寫回審計員指定的列號，
沒指定時是 null。欄名列開放給 GL 與 TB 的一般匯入（裁定 2026-10-22-15）。

授權編製人員清單匯入可傳入 `sheetName`，`sourceColumn` 必填並須精確對應來源標頭；畫面要求審計員選取與 GL
傳票建立人員相同的識別欄。省略欄位或所選欄沒有有效識別值時回 `invalid_payload`，`field` 為 `sourceColumn`，
並說明如何改選欄位或換檔重試；失敗不取代原名單與相依結果。
回應另含 `sourceColumn`、`sourceRowCount`、`blankRowCount`、`duplicateRowCount` 與 `matchedPreparerCount`。
`rowCount` 是去除空白及重複後的有效識別值數量；識別值去掉頭尾空白後不分大小寫去重，和 GL 建立人員的比對同一規則。
原始資料列數不含標頭與完全空白列，空白數只計所選識別欄為空、其他欄仍有資料的列。
三項匯入統計與識別欄保存於案件資料庫；`project.load` 帶回相同摘要，舊名單未保存的統計為 null，不捏造為 0。
`matchedPreparerCount` 是授權編製人員清單中出現在 GL 傳票建立人員的識別值數量，不是 GL 分錄數；尚未確認 GL 建立人員配對時為 null。
0 人比對成功只提醒可能選錯識別欄，不擋匯入。`mapping.commit.gl` 另回 `authorizedPreparerState` 更新這項比對；GL 重新匯入後清除舊比對。
配對已提交、卻讀不回這項比對時，回應省略 `authorizedPreparerState`，前端保留畫面上的比對；`staleState`、`reports`
與 `reportWarning` 照前述「變更已提交、卻讀不回目前結果狀態」處理（裁定 2026-10-19-19）。
名單仍不保存私人原始路徑、檔名及匯入時間。
`import.authorizedPreparer.clear` 無必要 payload 欄位，回應 `cleared: true`，另帶上方變更共同的四個欄位；同一交易移除清單、
預篩選與篩選結果及全部已存篩選情境，保留 GL、TB 與驗證。重試及重開後仍可重新匯入；重新匯入不會恢復情境，要重新儲存。

### 欄位配對與科目分類

- `accountCategory.save`
- `accountMapping.save`（直接儲存科目清單中改動的分類）
- `mapping.valueProfile`
- `mapping.restoreDraft`
- `mapping.commit.gl`
- `mapping.commit.tb`

沒有 `mapping.autoSuggest` 這類自動建議，GL 與 TB 欄位由審計員手動選取（裁定 2026-09-17-04）；已保存配對仍保留。
第三步的「由底稿匯入欄位」選了 .xlsx 後送 `mapping.restoreDraft`，payload 只有 `filePath`（裁定 2026-11-05-03、2026-11-09-27）。
後端先讀六份正式報表共用的隱藏工作表 `JET_Metadata`，沒有這張表時讀「自動化工具-檔案欄位資訊」的 F1:H1。
F1:H1 三格都空白才算沒有這份紀錄；有內容而 F1 不是標記，算紀錄存在但格式不對。
兩種隱藏紀錄都沒有時，讀「自動化工具-檔案欄位資訊」看得到的 A 到 E 欄，從「配對前欄位名稱」「欄位型態」「配對後欄位名稱」推得草稿，
舊版 IDEA JET 與新版 JET 的寫法都認得（裁定 2026-11-09-28）。人工傳票否照舊版，`manualAutoPolicy` 是 `manualValues` `["1"]`、`automaticValues` `["0"]`；
攸關資料元素的 `fieldId` 由後端新產生。推不出來的項目列在 `unmatchedFields`，不整份拒絕：TB 只有兩欄相減時看不出是借減貸還是期末減期初，
列出 `changeAmount`；舊版沒記借方代碼時列出 `drCrDebitCode`，`sourceColumn` 是空字串。
「配對前欄位名稱」和「配對後欄位名稱」相同時，新版要加了【】才算配對，舊版都算來源欄同名的配對。
舊版金額說明句裡的「-」一律當成借方欄與貸方欄的分隔；新版說明句對不上時不套用這條，列出金額。
新版說明句完整寫出已填代碼。兩側都填時寫「借方為「代碼」，貸方為「代碼」」，推回 `drCrDebitCode` 與 `drCrCreditCode`；
只有一側時只寫該側並推回該側代碼（裁定 2026-11-10-03）。以前只寫借方代碼的檔案仍只帶回借方代碼，不猜貸方代碼。
看得見的表沒有補值欄位，推得的 `fillDownFields` 是空的；`rdeFields` 依表上的列序排列。
攸關資料元素名稱後面的「（來源欄名）」，去掉後的名稱和其他列相同或加 `_JE` 是標準欄名時，當成新版補上的後綴，推回的 `label` 不含這一段。
來源欄名先找完全相同的，找不到時比對只留文字與數字、全形半形視為相同、不分大小寫的寫法，唯一一個對得上才算。
這個 action 只讀不寫，不保存配對，也不建立標準資料。回應的 `gl` 是 GL 草稿。`tb` 是 TB 草稿；
沒有匯入 TB、檔案匯出時 TB 還沒確認配對，或 TB 段沒有任何配對也沒有要重選的項目時，`tb` 是 null，不換掉 TB 草稿。
檔案裡選的來源欄在目前資料找不到時，對得上的照樣帶入，找不到的連同綁在它上面的設定一起拿掉，列在 `unmatchedFields`。
每一項有 `dataset`、`key`、`label`、`sourceColumn`；攸關資料元素的 `key` 是它的 `fieldId`。
隱藏紀錄存在但格式不對（裁定 2026-11-05-14）、隱藏紀錄缺必要欄位或用了不支援的金額計算方式，或檔案裡沒有「自動化工具-檔案欄位資訊」與隱藏紀錄時，
整份拒絕並寫出原因。沒有隱藏紀錄，而工作表裡找不到 GL 的欄位對照表，或 GL 的對照表推不出任何欄位時，也整份拒絕；
這時不看 TB 段，因為 GL 草稿會整份換掉。還沒匯入 GL、檔案不是 .xlsx、找不到檔案、檔案讀不出來，
以及進階設定和目前匯入的資料不一致時，也整份拒絕。每種拒絕的訊息都寫出下一步。

GL 的 `glDate` 在畫面顯示為「總帳日期」，用來界定案件期間。`approvalDate` 對應「傳票核准日」及資料庫 `approval_date`。
JET 沒有傳票日期欄位與回溯過帳規則，`mapping.commit.gl` 不接受 `voucherDate`（裁定 2026-10-08-03）。
完整對照見 `jet-guide.md` 第 2 節；其他 action 欄位名稱與既有報告正準名不變。

`mapping.commit.gl` 和 `mapping.commit.tb` 的 commit 指確認配對設定並建立標準資料，成功後畫面顯示
「已確認配對」及時間，不稱為提交審計結論。成功回應才更新完成狀態；案件或來源已更換時，舊回應不得
寫回目前畫面。操作名稱一致不代表改動既有 action 識別字。
GL 與 TB 都在投影資料的同一交易內保存配對與確認時間；任一步失敗或取消，保留原投影與配對。
匯入、配對、驗證、預篩選及篩選已完成資料保存後，案件步驟更新失敗只在支援日誌記一筆 `action.step_failed`，
`step` 為 `workflow.milestone`，不把已完成的操作誤報失敗。這只適用附帶的步驟紀錄，不放寬審計結果或使用者修改案件資料的保存錯誤。

GL 的 `side` 與 `flag` 金額模式共用判定，畫面合併為「金額與借貸別」；兩種模式都可以送出與讀取，`flag` 在畫面上顯示成同一個選項。
`mapping.drCrDebitCode` 與 `mapping.drCrCreditCode` 至少填一個；兩者是字面代碼，不是來源欄名。前後空白修整後以 `OrdinalIgnoreCase` 比對。
金額照舊版保留來源金額的正負號：借方代碼的列照原金額，其他列乘以 -1，不先取絕對值；借貸衍生欄再依結果的正負號決定。
所以紅字沖銷，例如借方代碼的列寫 -100，算成貸方 100。資料本來就用正負號表示借貸時，改選「單一帶號金額欄」（裁定 2026-11-05-37）。
只填借方碼時，其餘全部值包含空白都算貸方；只填貸方碼時方向相反。兩側都有填時，兩碼不得相同，來源空白或不符合任一碼仍以 `projection_failed` 列出來源列號，零金額也相同。
單邊代碼的做法依裁定 2026-10-08-11。金額或日期解析失敗時，GL、TB 投影都保留原有資料；TB 不新增貸方代碼欄。
已投影的資料不會自動重寫。重新確認 GL 配對才按目前設定投影；還原草稿接受有效的單邊代碼，但不猜另一側代碼，也不改寫已確認配對。
千分位逗號只接受每組三位；括號可表示負數（裁定 2026-10-04-06），不可再疊加正負號。正負號放在尾端時，前面可以有空白，有沒有千分位都讀成同一個值。
GL、TB、RDE 金額與篩選運算值共用相同解析。
純數字不當成日期（裁定 2026-10-22-11）：沒套日期格式的數字儲存格與文字檔裡的 `2025`、`2025.12`、`45292` 這類值，
都視為無法辨識的日期，確認配對時寫出列號。日期型儲存格由讀取器依儲存格格式換成 ISO，
只接受 1900 到 2100 年，1904 日期制同樣檢查，超出範圍的儲存格也以無法辨識列出列號；明確西元或民國文字日期的原範圍不變，
Access 原生日期不套 Excel 限制。帶逗號的數字不當成日期，也不交給寬鬆解析；不論逗號是否三位一組（例如 `1,234`、`12,31`），
都視為無法辨識的日期，確認配對時寫出列號。第五步貼上的日期清單同樣保留原文，由後端寫出格式錯誤（裁定 2026-10-19-09）。
前後端的日期規則共用 `src/JET/tests/JET.Tests/Fixtures/date-normalization.json` 的固定答案，一邊改了規則，另一邊的測試就會失敗。
年份在後的數字三段式日期不猜月日順序，帶時間也一樣拒絕。
交給寬鬆解析的值，原文要寫出四位數年份與日：`12/31`、`2025-12`、`Dec 2025`、`31 Dec`、`Dec 31, 25` 這類缺年份、缺日
或只寫兩位數年份的值，不補成今年、補成 1 日或猜年份，同樣視為無法辨識的日期，確認配對時寫出列號，日期清單也由後端寫出格式錯誤。
`June 11, 2025` 與帶時間的 `2025/06/11 00:00:00` 照常接受（裁定 2026-10-19-11）。

`mapping.valueProfile` 只回有界的值分布：預設及畫面請求為 50 個最常見非空白值，action 可指定 1 到 100 個，另回 `blankCount`、`distinctCount` 與
`truncated`。可選的 `comparisonValues` 每次最多 100 個原始字串；有提供時另外回傳 `comparisonGroups`，
將顯示值和所附字串依後端既有的 `Trim` 和 `OrdinalIgnoreCase` 比對分組，組內仍保留原字串。
同時回傳 `comparisonKeys`，每項為 `{ value, key }`，保留每個原始字串及後端產生的比對鍵。前端用這個鍵連接不同請求的等價關係，
不能自行把代碼轉大寫；空白的鍵為 null，表示已核對為空白，不是缺少資料，也不等於字串 `"null"`。
每份回應最多包含當次有界顯示值與最多 100 個請求字串，不把完整來源清單傳給前端。
可選的 `fillDown` 是布林值，前端在這個來源欄確認配對時會被往下填補時帶 `true`：資料庫在每份來源內依列序把空白補成上一列的值，
再計算常見值、`blankCount` 與 `distinctCount`，每份來源開頭的空白仍算空白，和確認配對時的填補同一規則（裁定 2026-10-28-03）。
來源存在性的核對照原樣做，填補只複製已有的值；`fillDown` 不是布林值，或和 `comparisonOnly: true` 一起送，都以 `invalid_payload` 拒絕。
SQLite 在資料庫內先用 .NET 的比對鍵彙總全部來源，再取常見值與計算不同值數量，大小寫等價的值不分成兩組。
`checkSourceValues: true` 必須搭配 `comparisonValues`，不能與 `comparisonOnly: true` 同時使用；它另外核對完整來源，
回傳 `missingComparisonValues` 及 `sourceValueCheckStatus: "checked"`，不能拿前 50 個顯示值推定某個代碼不存在。
資料庫實作沒有這項核對時，原摘要仍可讀取，但 `missingComparisonValues` 回 null，狀態為 `unsupportedProvider`；
畫面須寫尚未核對，不得當成缺值或空清單。目前只有 SQLite，它會核對，所以不會出現這個狀態。
`comparisonOnly: true` 時必須提供 `comparisonValues`，只回 `sourceColumn`、`comparisonGroups` 和 `comparisonKeys`；
此模式仍要求已開啟案件，但不查匯入批次、不讀母體、不保存或驗證政策。100 是這項呈現資料的單次傳輸上限，
不是人工、自動或過帳政策可保存的代碼上限。畫面將較長清單分批送出；只取得文字等價對應時不重查母體，核對來源是否存在時才另行查詢。
前端保留手輸原字串，不自行作 Unicode 大小寫展開；對應未確認時不顯示為已判定人工或自動，讀取失敗可重試，
也不阻擋完成配對。等待對應時只暫停來源值清單的指定或取消，以免只改掉一側而留下同義代碼；
手輸代碼、取消變更與完成配對仍可操作。取消變更、換欄位、重新匯入或換案件後，舊比較回應不得套回目前畫面。

`manualAutoPolicy` 的 `manualValues` 與 `automaticValues` 保留原格式。選填的
`unlistedValueKind` 可為 `reject`、`manual` 或 `automatic`，指定非空白未列值的判定；省略時沿用逐值指定。
單側清單須至少提供一個代碼，另一側明確指定為補集。`blankValueKind` 可為 `reject`、`manual`、`automatic`
或 `unclassified`；最後一項保留空白而不判定人工或自動，仍可參與其他測試。省略時維持舊設定的空白處理。
兩項設定會保存至案件及報告配對資訊，還原草稿時保留。前端在 GL 配對區直接顯示後端原因；取消或更換來源欄時，
清除上一欄的代碼政策。RDE 全選只建立草稿；來源欄、型別、名稱、後端產生並沿用的欄位識別碼，以及核心欄位
不可重用等規則，仍由後端驗證。`rdeFields[].fieldId` 只能沿用目前已確認配對發出的識別碼；重新匯入後還沒重新確認時，
改為沿用 `previousMapping` 那一份發出的識別碼，已儲存情境裡指到這些欄位的條件才對得上。其他識別碼照舊拒絕。

`mapping.commit.gl` 另可帶 `fillDownFields`，是要「往下填補空白」的 GL 欄位鍵清單，例如 `voucherNumber`、`glDate`；省略或空陣列表示不填。
只能是這次配對有來源欄的欄位，借方與貸方代碼是字面值不能填；沒有來源欄、重複或不是 GL 欄位的鍵以 `invalid_payload` 拒絕。
填補是對來源欄做的，後端把配到同一個來源欄的其他可填欄位一起列入（裁定 2026-10-28-04）。
確認配對時，JET 在投影每一列之前把這些欄位來源欄的空白補成同一來源內上一列的值，PBC-GL 原樣不動；多個來源追加時不跨來源延續，
每個來源的第一列空白就是空白；以列序為準，只填勾選欄位的來源欄。成功回應帶 `fillDownFields`（照欄位目錄順序，含一起列入的欄位）
與 `fillDownCount`（這次補了幾格，沒勾選是 0），以及由這次投影的控制總數算出的 `zerosThreshold`（見預篩選一節）。清單與格數跟著配對保存：`project.load` 回應的 `mapping.gl` 帶 `fillDownFields`
與 `fillDownCount`（裁定 2026-10-28-05）；`previousMapping.gl` 與 `mapping.restoreDraft` 回應的 `gl`
是草稿，只帶 `fillDownFields`。報告的配對資訊兩項都寫入；案件與報告沒有這兩項時，清單視為空、格數視為 0。
改選擇只要重新確認配對，走既有的失效規則。往下填補放在第三步確認配對時（裁定 2026-10-22-16）。

過帳狀態的接受值同樣只適用於當時指派的來源欄；取消或更換欄位時清除政策，再由使用者選擇接受值。
值摘要可分別讀取，失敗後可以重試；快取與配對錯誤都隨案件或匯入來源變更失效，不能沿用舊資料的回應。

確認配對的失敗明細保留 `sourceColumn`，人工判定另提供 `reasonCode`：`manual_blank` 指來源空白，
`manual_unlisted` 指非空白值尚未歸類，`manual_overlap` 指同一個值同時出現在兩側。前端依代碼定位設定，不分析中文錯誤句子。
GL 和 TB 都掃描完整來源來計算錯誤總數；每組只保留最多 10 個值、每值最多 10 個列號，原 `Errors` 樣本仍最多 50 列。
TB 成功回應的 `warnings` 和 GL 一樣，必填文字欄整欄空白時提醒，但不阻擋確認。
GL 的人工/自動分錄選「只列人工」或「只列自動」，而清單代碼在來源裡一筆都沒有時，`warnings` 另加一則提醒，
寫出代碼、來源欄、有值的列會怎麼判定與下一步；比對方式和投影相同，去掉頭尾空白、不分大小寫。逐值指定時不做這項檢查。
GL 或 TB 已有確認過的配對，或重新匯入後第一次確認時，JET 寫一筆重新配對的操作紀錄。寫不進去時配對仍已生效，
`warnings` 另加「變更已儲存，但這次變更沒有寫入操作紀錄。」，和重新匯入相同。本機案件確認前讀不到操作紀錄、
無法判斷這次要不要記時，不寫紀錄，`warnings` 同樣加這一句。

### 資料驗證

- `validate.run`

可傳入 `sampleSize` 整數，省略時為 59，允許 1 到 1048524；超出範圍或型別錯誤回傳 `invalid_payload`，欄位為 `sampleSize`。
`infSamplingTest.requestedSampleSize` 保存指定筆數，`sampleSize` 是實際抽出筆數。可用分錄不足時全部抽出。
舊摘要缺少 `requestedSampleSize` 時仍可讀取，畫面以 59 作為下一次執行的預設值。

驗證摘要另含 `voucherNumberAcrossGlDates: { voucherNumberCount, entryCount }`，計算 GL 中，同一非空白傳票號碼出現在兩個以上總帳日期的號碼數及分錄筆數。
同月不同日也計入，空白號碼與 GL(未納入測試) 不計入；不改傳票比對鍵或既有驗證判定。兩個數字隨驗證摘要保存，重開案件不另掃母體。
第一步依有效摘要提醒，尚未驗證、缺欄或結果過期時顯示尚未確認，不把未知當零，也不阻擋後續操作。

`completenessTest.eligibility.isEligible` 表示目前驗證結果可供後續操作使用，不代表完整性通過。
`reason` 只用於缺少、損壞或過期結果；新增的可空字串 `warning` 承載審計差異和可繼續操作的提醒。
驗證摘要一定帶 `warning`，沒有提醒時是 null；缺這一欄的摘要當成無效結果，需要重新執行資料驗證。
回應時依原始檢查結果重新產生整個判定，不信任存檔中的衍生阻擋布林值。
預篩選、篩選及匯出共同使用後端判定；不得因差異筆數非零而拒絕。差異、狀態及明細原樣保留。
`validate.run` 只要求已確認 GL 欄位配對。沒有 TB 時 `completenessTest.status` 是 `na`、`diffAccountCount` 是 0，
`naReason` 依是否匯入 TB 寫「沒有匯入 TB，無法執行 GL 與 TB 逐科目比對。」或「TB 欄位配對尚未確認，無法執行 GL 與 TB 逐科目比對。」，
`eligibility.warning` 以同一句開頭（裁定 2026-10-29-16、2026-10-30-16）。

`completenessTest.storedTotals` 等 action 回應欄位為既有資料形狀，不是使用者可見名稱。`storedTotals.eligibleSource` 是確認 GL 欄位配對時
逐列算出的數字，`storedTotals.gl` 是驗證時從存下的分錄重新計算的數字，兩者都不是來源檔本身的合計。
畫面和錯誤用兩項檢查的名稱「存入筆數與合計核對」及「GL 與 TB 逐科目比對」，不顯示 Part A 或 Part B。`sourceQuality` 目前只有
`blankGlDate`，表示總帳日期空白且不能界定案件期間的來源列；日期在期間外不是這個集合。
驗證的 INF 樣本、總帳日期空白有界樣本與摘要由同一交易取得並保存，摘要寫入失敗會回滾本次樣本與失效狀態變更。
後端不再把「存在 INF 樣本」視為已完成驗證的證據。

`blankRecordsTest` 另帶可為 null 的字串 `approvalDateNotInPeriodNaReason`，對應畫面的「核准日不在期間」。能檢查時，
`approvalDateNotInPeriodCount` 是筆數，原因是 null；照舊版，核准日落在查核期間之外或空白的分錄都算進來（裁定 2026-11-05-38）。
傳票核准日不可用時這一項無法執行，`approvalDateNotInPeriodCount` 是 null，明細樣本與明細頁不列出任何一筆，
原因由 `JetRuleRequirements` 產生，寫出缺什麼、到哪裡補，前端照原樣顯示。可不可用照 `JetFieldCatalog.AvailableGlFields` 判斷，
和預篩選、第五步讀同一份答案。存下的摘要裡，筆數與原因必須恰好一個有值。存下的摘要沒有原因欄時，
當成無效結果，需要重新執行資料驗證。
驗證報告讀同一份摘要，筆數是 null 時主表寫 N/A，也不建立核准日不在期間的明細表。
`validate.run` 的空值明細樣本、`project.load` 帶回的同一份樣本與 `query.blankRecordsPage` 的每列另帶傳票核准日 `approvalDate`，
可為 null，`query.blankRecordsPage` 也能依它排序；畫面只在「核准日不在期間」的明細顯示「傳票核准日」欄（裁定 2026-11-06-13）。

### 預篩選

- `prescreen.run`

有選用來源的規則回傳 `status`、`naReason`、`count`。
未配對傳票建立人員時，人員彙總及非授權人員測試無法執行。
這些原因只影響相依規則，不阻擋其他程序；補回配對後重新驗證及預篩選即可。
進階篩選選用編製分錄較少的人員或非授權人員條件時，也依目前配對指出必要來源缺漏，保留條件供修正，不能把無來源當成零筆。
第四步沒有編製分錄較少的人員與使用較少的科目兩條規則（裁定 2026-10-29-27）：`prescreen.run` 不回傳 `rarePreparer`、`rareAccount`
與 `rareAccounts.rareAccountCount`，`query.prescreenPage` 也不接受這兩個規則鍵；第五步欄位條件裡的「編製分錄較少的人員」與「較少使用的科目」仍可用。
`unexpectedAccountPair.count` 與 `rulePeriod` 的命中列數是命中傳票的全部分錄數，`query.prescreenPage` 也列出這些分錄，
和預篩選報告的 R3 相同；傳票張數不變。

`naReason` 由 `JetRuleRequirements` 依缺少的前置條件產生，缺幾項接幾句，每句以句號結尾，寫出缺什麼、到哪裡補；
前端照原樣顯示，不另接補救文字。未預期借貸組合的句子寫科目分類設定裡內建分類目前的名稱，所以 `prescreen.run` 也讀分類設定。
`weekendActivity` 與 `holidayActivity` 各合併過帳與核准兩側，另帶可為 null 的字串 `approvalNaReason`。
總帳日期那一側不能執行時，`naReason` 列出兩側的原因，同一句只寫一次，`approvalNaReason` 為 null。
只有傳票核准日那一側不能執行時，`naReason` 為 null，`approvalNaReason` 是「只檢查了總帳日期。」再接那一側的原因，
`postingCount` 照常計算，`approvalCount` 為 null。兩側都能執行時，兩個原因都是 null。
預篩選沒有回溯過帳規則（裁定 2026-10-08-03）：`prescreen.run` 不回傳 `backdatedPosting`，`query.prescreenPage` 也不接受這個規則鍵。
舊情境含回溯過帳條件時，開案的規則版本檢查會列出這條條件已停用，審計員刪除這條條件後再儲存即可。
`rareAccounts.distinctAccountCount` 是 GL 裡的相異科目數，不是前 50 個科目摘要的列數，第四步「較少使用之科目」卡片的狀態顯示這個數字。
編製者彙總仍使用既有的去空白、不分大小寫規則分組；空白組的資料值不改，只有畫面與報告將它顯示為「（空白）」。實際人員代碼恰為「（空白）」時，仍是另一組。
`preparerSummary.preparers` 最多 50 列；`preparerSummary.totalPreparerCount` 是查核期間的完整人數，空白人員組算一位，
和 `concentration.preparers.totalPreparerCount` 相同。這項不適用時為 null。畫面的「N 位人員」與預篩選報告的摘要列都用這個數字。

`prescreen.run` 的 12 條規則與第五步 `prescreen` 條件的 10 個 `prescreenKey`，都登錄在 Domain 的 `PrescreenRuleDefinitions`；
`RuleCatalog`、`FilterConditionLabels` 與前端的 `PRESCREEN_RULES` 由它衍生或鏡像，架構測試逐項比對。
回應裡的 `zerosThreshold` 是這次「分錄金額中有連續 0 的尾數」用的 0 的個數，照 IDEA 版由 GL 決定：借方金額合計除以分錄筆數的平均，
整數部分的位數減一，至少 1、最多 12（裁定 2026-11-05-25、2026-11-05-27）。借方合計與筆數取確認 GL 配對時寫下的控制總數；
`project.load` 的 `mapping.gl` 與 `mapping.commit.gl` 的成功回應也帶同名的 `zerosThreshold`，第四步卡片與第五步「尾數連續多個 0」的預設讀這裡。
沒有已確認的 GL 配對時 `project.load` 不帶 `mapping.gl`。預篩選報告讀保存的那次結果裡的 `zerosThreshold`。

### 進階篩選

- `filter.preview`
- `filter.scenario.save`
- `filter.scenario.remove`
- `filter.scenario.recheck`
- `filter.commit`：整批取代全部已存情境，每條條件同樣只留用得到的欄位。正式畫面不呼叫，留給程式化佈置與既有測試使用。

第五步欄位條件的「加入」與常用查法、常用篩選條件的卡片與情境範例，都只在前端組出既有 `groups` 和 `conditions`，再呼叫上述 action；
送出並儲存的是展開後的條件與使用者參數。2026-11-03 為了「尾數連續多個 0」新增 `endsWithZeros`、`notEndsWithZeros` 兩種比較方式，
見下方 `fieldValue` 一段；其餘入口沒有新增規則型別或比較方式。2024 篩選表的 A 到 U 與五個填寫範例
（來源版本 `je-form-2024-1210`）已從正式前端移除，目錄原文只留在測試夾具
`src/JET/tests/JET.Tests/Fixtures/legacy-form-2024-1210.json`。後端不解讀這些代號，也沒有接收代號的通道，
常用篩選條件的來源標記不受影響。目前篩選計算版本為 `filter-2026-10-14-v20`；
空白傳票號碼與非營業日的裁定見裁定總表 2026-10-04-02。

### 有界查詢

資料缺漏輔助檢視明細的 `query.blankRecordsPage` 保留類別、排序與游標契約；搜尋文字現在比對傳票號碼、科目編號或摘要，
因此缺傳票號碼的資料也能搜尋。各類結果分別排序及分頁，同筆缺兩欄時在各類明細中各出現一次。

- `query.dataPreview`（`gl` 與 `glExcluded` 的欄位依已確認的 GL 配對決定，見本節後面的說明。人工或自動分錄有配對時，
  兩頁都列 `manualAuto`，值是 `manual`、`automatic` 或 null。`filter.preview` 不加此欄。）
  `dataset` 的值和資料名稱一致（裁定 2026-10-08-04、2026-10-30-06）：`pbcGl`（PBC-GL）、`gl`（GL）、`pbcTb`（PBC-TB）、`tb`（TB）、
  `glExcluded`（GL(未納入測試)），以及 `accountMapping`、`authorizedPreparer`、`calendar` 與 `schemaOverview`。改名前的舊值不接受，會回 `InvalidPayload`。
  `dataset: "gl"` 的 `gl` 和匯入批次 `dataset_kind` 的 `gl` 同字不同義，後者仍指 GL 或 TB 這一類匯入資料。
- `query.completenessDiffPage`
- `query.voucherBalancePage`
- `query.blankRecordsPage`
- `query.sourceQualityPage`
- `query.filterHitsPage`
- `query.filterVoucherPage`
- `query.filterVoucherRowsPage`
- `query.prescreenPage`
- `query.infSamplePage`
- `query.highRiskSummaryScenarios`
- `query.highRiskSummaryVoucherPage`
- `query.highRiskSummaryRowPage`
- `query.accountMappingPage`（第四步科目分類清單，可依科目編號或名稱搜尋；有界分頁）
- `query.accountMappingDifferencePage`（`kind` 為 `mappingOnly` 或 `unmapped`，另收 `cursor`、`pageSize`；
  每頁預設 200 筆，上限也是 200 筆。回 `{ kind, rows: [{ accountNumber, accountName }], nextCursor, totalCount }`。
  依科目編號升冪，`accountName` 可為 null。第一頁回總筆數，空清單為 0；續頁的 `totalCount` 為 null。
  游標不可跨清單種類使用。只在使用者列出清單時查詢，不在 `project.load` 時計算。）
- `query.accountMappingBlankPage`（第四步「分類留白 N 筆，視為 Others」的清單；`cursor`、`pageSize`，
  回 `rows[{ accountNumber, accountName }]`、`nextCursor` 與 `totalCount`，依科目編號升冪）

`query.filterHitsPage`、`query.highRiskSummaryVoucherPage` 與 `query.highRiskSummaryRowPage` 的游標是不可自行拆解的 token，
綁定案件、來源資料版本、情境版本、查詢種類及排序搜尋。來源或情境變更後，即使已重新計算完成，舊游標
也必須從第一頁重新載入，不能把兩代結果接起來。最後一個情境被刪除時，首次查詢可以回空集合，舊續頁
則回 `stale_result`。無法辨識的游標也要求回第一頁；資料庫 keyset 排序與 action 欄位形狀不變。
上游修改清除全部情境後，`query.filterHitsPage`、指定已存情境的傳票查詢、條件篩選報告與底稿匯出回 `no_saved_scenario`，
訊息是「尚未儲存任何篩選情境，請先在「進階條件篩選」儲存情境」；高風險條件彙總三個查詢的首頁照舊回空集合。
前端把這個錯誤碼當成前置條件不足，寫成「〈作業名稱〉尚無法執行：〈原因〉。」，不附耗時（裁定 2026-10-14-01）。

四個既有篩選結果查詢以持久化失效旗標判斷是否需要補算，零筆與搜尋無結果不再觸發寫入。只有實際
失效的首頁需要既有寫入鎖；一般讀取前後核對資料、情境及案件，避免切換期間回傳混合結果。

所有供畫面使用的 `query.*Page` 每頁預設 200 筆，上限也是 200 筆。不帶游標的第一頁回應 `totalCount`，
它是目前查詢範圍的總列數，包含搜尋、分類或情境條件。帶游標的續頁不再計數，`totalCount` 為 null，
前端沿用第一頁的總筆數計算頁數。資料庫用集合查詢計數，不為了計頁而載入完整資料。
內部報表的批次大小維持原本設定，不額外要求這項計數。

`query.dataPreview` 接受 `dataset`、`limit` 與 `offset`。`limit` 預設 200，上限也是 200；`offset` 預設 0，
負值視為 0。資料庫依固定順序只讀當頁資料。`offset` 為 0 時，`totalCount` 和 `stats` 描述完整資料集；
`offset` 大於 0 時兩者為 null，前端沿用第一頁的值。
回應有 `dataset`、`columns`、`columnDetails`、`rows`、`totalCount` 與 `stats`，欄位配對及右側預覽共用這個查詢。
`columnDetails` 在 `gl`、`glExcluded`、`tb`、`accountMapping`、`pbcGl` 與 `pbcTb` 都會給，其他資料集是 null（裁定 2026-11-05-04）。
`tb` 與 `accountMapping` 的 `label` 和欄位配對畫面相同，例如「會計科目編號」「年度變動金額」，只有 `changeAmount` 是 `amount`。
`pbcGl` 與 `pbcTb` 的 `key` 與 `label` 都是來源欄名；只有已確認配對到金額位置的來源欄是 `amount`，還沒確認配對時全部是 `text`。
金額位置以 `JetFieldCatalog.AmountMappingKeys` 為準：GL 是 `amount`、`debitAmount` 與 `creditAmount`，TB 是各種變動金額的來源位置。

`dataset` 為 `gl` 或 `glExcluded` 時，兩頁依已確認的 GL 配對列同一組欄位（裁定 2026-10-22-09）。
最前面是固定的八欄：`voucherNumber`、`lineItem`、`glDate`、`accountNumber`、`accountName`、`description`、
`amount` 與 `drCr`。接著依欄位目錄的順序列出已配對的選填欄位，可能有 `approvalDate`、`sourceModule`、`createdBy`、
`approvedBy`、`manualAuto` 與 `postingStatus`。再來是已配對的攸關資料元素欄位，鍵是欄位識別
`rde.<32 個小寫十六進位字元>`。`glExcluded` 最後另加 `exclusionReason`。
選填欄位算不算已配對，和第四步、第五步讀同一份可用欄位，所以傳票核准日選「與總帳日期相同」時也列出 `approvalDate`。
`lineItem` 是固定欄位，傳票文件項次自動編號時照樣列出。
未配對的欄位不列，借方與貸方金額也不分欄。`gl` 沒有分錄時，`columns`、`columnDetails` 與 `rows` 都是空陣列；
`glExcluded` 沒有分錄時照樣列出欄位，只有 `rows` 是空陣列。

`columnDetails` 和 `columns` 順序相同，每一欄是 `{ key, label, valueType }`。`label` 和欄位配對畫面上的名稱相同，
攸關資料元素用審計員取的名稱；`valueType` 是 `text`、`date` 或 `amount`。前端用 `label` 當欄名，只把 `amount` 欄靠右對齊，
不逐格判斷哪一欄是數字；`pbcGl` 與 `pbcTb` 的金額欄只靠右，數值照來源文字顯示。
`rows` 裡的金額是照資料庫存值換算的十進位字串，例如 `1234.5678` 或 `-1000`，前端補成四位小數加千分位顯示。
`gl` 的 `stats` 是 `{ amountAbsMin, amountAbsMax, glDateMin, glDateMax, voucherCount }`。兩個金額也是
同樣的十進位字串，因為金額大到約 5,497 億以上時，經過浮點數會讓第四位小數失真。

`dataset` 為 `glExcluded` 時，`rows` 的 `exclusionReason` 欄是 `blankGlDate`（總帳日期空白）、`beforePeriod`
（早於查核期間）、`afterPeriod`（晚於查核期間）或 `postingStatus`（過帳狀態不符）。日期原因分成這三種（裁定 2026-10-21-04）。
`stats` 固定為 `{ blankGlDateCount, beforePeriodCount, afterPeriodCount, postingStatusCount }`。
請求可另帶選填的 `exclusionReason`，值是同一組四個字，只回這種原因的分錄；這時 `totalCount` 是這種原因的筆數，
`stats` 仍是全部未納入測試分錄的四個筆數。畫面不會送出其他值。其他值，或在別的資料集帶這個欄位，回 `invalid_payload`，
支援日誌的 `payload_check` 記 `preview_exclusion_reason_invalid`。

**排序與搜尋。** 明細表的分頁查詢都接受選填 `sort` 與 `search`：`sort` 是
`{ key, direction }`，`key` 是該查詢回應列的欄位名（白名單見下表），`direction` 是 `asc` 或 `desc`，
省略時依該查詢的穩定鍵升冪；`search` 是最多 200 個字的文字，不分大小寫比對傳票號碼（科目層的表
比對科目編號，空值明細另比對科目與摘要），省略或空白表示不過濾。排序在資料庫的完整查詢範圍內完成後才取當頁，不是只排序前端已載入的資料。排序後空值一律排在最後。換了排序或搜尋要從第一頁重新載入；
帶著舊游標換排序會回 `invalid_payload`，訊息說「請從第一頁重新載入」；不在白名單的 `key` 也回
`invalid_payload` 並列出允許的鍵。`query.filterHitsPage` 與 `query.infSamplePage` 的 `columns` 另帶
`sortable`，標示固定欄可以排序，攸關資料元素欄位不能。
`query.completenessDiffPage` 省略排序時，科目編號空白的列排在最前，試算表那一列在總帳那一列之前，其餘依科目編號；
驗證報告與底稿逐頁讀全科目表時也是這個順序。

| 查詢 | 搜尋比對 | 可排序的 `key` |
|:---|:---|:---|
| `query.completenessDiffPage` | 科目編號 | `accountNumber`、`accountName`、`tbAmount`、`glAmount`、`diff`、`notInTb` |
| `query.voucherBalancePage` | 傳票號碼 | `voucherNumber`、`debit`、`credit`、`diff` |
| `query.blankRecordsPage` | 傳票號碼、科目編號或摘要 | `voucherNumber`、`accountNumber`、`glDate`、`approvalDate`、`description` |
| `query.sourceQualityPage` | 傳票號碼 | `sourceRowNumber`、`voucherNumber`、`accountNumber`、`glDate`、`description` |
| `query.infSamplePage` | 傳票號碼 | `voucherNumber`、`accountNumber`、`accountName`、`debit`、`credit`、`glDate`、`approvalDate`、`createdBy`、`approvedBy`、`description` |
| `query.prescreenPage` | 傳票號碼 | `voucherNumber`、`lineItem`、`glDate`、`accountNumber`、`accountName`、`amount`、`drCr`、`description` |
| `query.filterHitsPage` | 傳票號碼 | `voucherNumber`、`lineItem`、`glDate`、`accountNumber`、`accountName`、`amount`、`drCr`、`description` |
| `query.highRiskSummaryVoucherPage` | 傳票號碼 | `voucherNumber`、`glDate`、`createdBy`、`voucherTotal` |
| `query.highRiskSummaryRowPage` | 傳票號碼 | `voucherNumber`、`lineItem`、`glDate`、`approvalDate`、`createdBy`、`approvedBy`、`accountNumber`、`accountName`、`amount`、`description` |
| `query.filterVoucherPage` | 傳票號碼 | `voucherNumber`、`glDate`、`hitRowCount`、`totalRowCount`、`voucherTotal` |

兩個 `query.filterVoucher*` 查詢以 `scenario` 草稿或 `scenarioPosition` 和 `scenarioRevision` 指定
已儲存情境，兩種方式擇一。只列出有命中分錄的傳票，沒有待判定。草稿每次都即時判斷條件。
已儲存情境讀儲存時存下的命中，以及每筆分錄符合第幾組第幾條，不再判斷條件；傳票的分錄數、總額與日期仍讀 GL，
排序、搜尋、分頁與每列內容都和草稿相同。`scenarioRevision` 可以送 `resultRef.revision`，也可以送前一次回應的
`scenarioRevision`。後端只核對這個位置上的情境自己的版本：存或移除別的情境，正在看的頁面不會過期；這個情境改了、
因為移除前面的情境而換了位置，或上游修改清掉了情境，就回 `stale_result`。已儲存情境回應的 `scenarioRevision` 是這個情境自己的版本。
已儲存情境傳票清單的第一頁另帶 `voucherCount`、`summaryRowCount` 與 `countsWholeVoucherRows`，數字讀存好的結果，
和 `query.highRiskSummaryScenarios` 同一個口徑；草稿、續頁與分錄頁沒有這三個欄位。
`pageSize` 預設 200，最多 200，回應包含 `rows`、`nextCursor`、`totalCount`（只在第一頁計算，續頁為 null）、
`queryRevision`、`scenarioRevision` 及 `conditionText`。傳票列提供 `voucherNumber`、`glDate`、
`hitRowCount`、`totalRowCount` 與 `voucherTotal`（該傳票期間內全部分錄的借方總額顯示值），可用上表的
`sort` 與 `search`；展開分錄時另帶 `voucherNumber` 及上次
回應的 `queryRevision`，分錄頁固定依分錄順序，不接受排序與搜尋。
明細提供日期、科目、金額、摘要、`isHit` 與 `matchDescription`。
情境只有一組，而且這組只有一條預篩選條件「未預期借貸組合」時，判斷與條件篩選報告摘要相同：
`hitRowCount` 是整張傳票的分錄數，分錄頁每列的 `isHit` 都是 true，不是直接命中的列，`matchDescription` 為
「同一張傳票的其他分錄，整張傳票列入結果」（裁定 2026-08-28-01、2026-10-16-08）。另有 `primaryConditions`
和 `voucherConditions`，分別對應主要條件與傳票層條件的位置；位置由 `group` 和
`rule` 組成，皆從 1 起算。
回應另有欄位 metadata `columns`，明細的攸關資料元素欄位值在 `customValues`。展開必須帶 `queryRevision`。
游標綁定條件與資料版本，過期回 `stale_result`，審計員重新預覽即可。查詢不保存草稿，不改寫已存命中。

**條件目錄。** 情境是 `{ name, rationale, groups[], editorOrigins? }`，每組
`{ join, matchScope?, conditions[] }`：`join` 為 `AND` 或 `OR`；`matchScope` 只能省略或為 `row`。
傳票層判斷用 `voucher` 子條件；`matchScope` 寫了其他值時，包括已拿掉的 `sameVoucher`，條件內容當成無法讀取。每條規則帶 `type` 與 `join`，
其餘欄位依型別：

| `type` | wire 欄位 | 允許值與說明 |
|:---|:---|:---|
| `fieldValue` | `field` 或 `fieldId` 擇一、`operator`、`value`、`values`、`from`、`to`、`includeBlank`、`amountBasis` | 文字：`equals`、`notEquals`、`contains`、`notContains`、`startsWith`、`notStartsWith`、`endsWith`、`notEndsWith`、`in`、`notIn`、`isBlank`、`isNotBlank`；日期：`on`、`notEquals`、`before`、`onOrBefore`、`after`、`onOrAfter`、`between`、`notBetween`、`in`、`notIn`、`dayOfMonthIn`、`dayOfMonthNotIn`、`isBlank`、`isNotBlank`；金額：`equals`、`notEquals`、`greaterThan`、`greaterThanOrEqual`、`lessThan`、`lessThanOrEqual`、`between`、`notBetween`、`in`、`notIn`、`containsDigits`、`notContainsDigits`、`isBlank`、`isNotBlank`，尾數與連續 0 見表下說明。`contains` 的 `values` 可放多個關鍵字（任一命中），`notContains` 是它的否定（一個都不含）；`dayOfMonthIn`、`dayOfMonthNotIn` 的 `values` 是 1 到 31 的整數，重複自動合併；`includeBlank` 省略時一律 false（空白不列入）；文字欄位的否定比較在畫面上預設勾選空白也符合，畫面一律明確送出 `includeBlank`（裁定 2026-11-05-56）。核心金額的 `amountBasis` 只接受 `absolute`，沒有帶或帶其他值都當成內容損毀，和舊版 IDEA JET 的數字條件一律比較絕對值一致（裁定 2026-11-05-70）；額外數值欄位仍可用 `signed`，畫面新增時預設 `absolute`（裁定 2026-11-05-47） |
| `entityFrequency` | `field`、`countUnit`、`countOperator`、`countFrom`、`countTo` | `field` 為 `accountNumber`、`createdBy` 或 `approvedBy`；單位為 `entries` 或 `vouchers`；比較為 `equals`、`lessThan`、`greaterThan`、`between`、`lessThanOrEqual` 或 `greaterThanOrEqual`。門檻為非負整數，`between` 含兩端。依 GL 計算，空白識別值不命中；傳票張數同號只算一張，不計空白號碼 |
| `accountSide` | `categoryMode`、`categoryIds[]` | `categoryMode` 為 `is`（科目屬於）或 `isNot`（科目不屬於），不帶 `drCr`；需要案件已匯入科目配對。分類留白的科目依 legacy 視為 `builtin.others`，不在配對檔的科目不屬於任何分類 |
| `specialAccountCategoryPair` | `pairMode`、`debitCategoryIds[]`、`creditCategoryIds[]` | `drAndCr`（借方是 A 且貸方是 B）、`drNotCr`（借方是 A 且整張傳票沒有 B 貸方）、`notDrCr`（貸方是 B 且整張傳票沒有 A 借方）。`drNotCr` 標記 A 類借方與同一張傳票貸方那一側的全部分錄，`notDrCr` 標記 B 類貸方與同一張傳票借方那一側的全部分錄，和 `accountPair` 的 `debitAnchor`、`creditAnchor` 相同（裁定 2026-11-05-45） |
| `accountPair` | `pairMode`、`debitCategoryIds[]`、`creditCategoryIds[]` | `exact`（舊情境讀回用，不再新建）、`debitAnchor`（借方是 A，看它的對方科目）、`creditAnchor`（貸方是 B，看它的對方科目） |
| `prescreen` | `prescreenKey` | 預篩選規則鍵 |
| `text`、`textSet` | `keywords`（逗號分隔）、`mode`、`values[]`、`normalization` | `mode` 為 `contains`、`exact`、`notContains`、`notExact`；舊情境讀回與編輯用，不提供新建 |
| `numRange`、`dateRange` | `field`、`from`、`to` | 金額以顯示值字串，日期為 ISO 字串；區間含兩端 |
| `drCrOnly`、`manualAuto` | `drCr`、`isManual` | `debit` 或 `credit`；布林 |
| `revenueDebitNearQuarterEnd` | `windowDays` | 整數門檻，範圍由 Domain 驗證 |
| `revenueWithoutNormalCounterpart`、`manualRevenueEntry`、`preparerEqualsApprover` | 無 | 固定規則 |
| `typed` | `fieldId`、`operator`、`value`、`values`、`from`、`to`、`amountBasis` | 2026-08-14 凍結的攸關資料元素欄位條件，契約不變 |

`fieldValue` 日期另支援 `isWeekend`、`isHoliday`、`isMakeupDay`、`isNonBusinessDay` 及各自的 `isNot…` 否定形式，
不帶值參數。週末依第二步設定的每週休息日；國定假日、補班日依第二步上傳的清單；非營業日為週末或國定假日再排除補班日。
日期另支援 `monthStartDays`、`notMonthStartDays`、`monthEndDays`、`notMonthEndDays`，`value` 是
1 到 31 的整數字串，不同時帶清單或區間。範圍包含月初或月底當天，超過該月天數時涵蓋全月，
核心與額外日期欄位都可使用。否定不納入空白，除非 `includeBlank` 明示為 true。
借貸方向只由 `drCrOnly` 表達，使用既有標準化 `dr_cr`。需要和欄位值由同一筆分錄符合時，
以 `group` 組合方向與欄位條件。要讓其他條件由同張傳票的其他分錄符合，第一條留在一般組，
其餘各條分別放進 `quantifier: "any"` 的 `voucher`。
金額另支援 `endsWithDigits`、`notEndsWithDigits`，`value` 為逗號分隔尾數，最多 100 組、每組最多 12 位。
比較整數部分的絕對值，不看正負或小數，保留前導零的位數；核心欄位與攸關資料元素欄位共用相同規則。
金額另支援 `containsDigits`、`notContainsDigits`（裁定 2026-11-05-46），畫面名稱是「整數部分包含」與「整數部分不包含」。
`value` 和尾數一樣是逗號分隔的數字，每串 1 到 12 位，最多 100 串；只看絕對值的整數部分，不看正負、小數與 `amountBasis`。
多串時任一串包含就符合，`notContainsDigits` 要每一串都不包含。否定形式不納入空白，除非 `includeBlank` 明示為 true。
金額也支援 `endsWithZeros`、`notEndsWithZeros`（裁定 2026-11-03-07）。`value` 是整數部分結尾連續 0 的個數，1 到 12 的整數字串，
不同時帶清單或區間；帶了 `values`、`from` 或 `to` 時當成條件內容損毀。比較整數部分的絕對值，不看正負、小數與 `amountBasis`，
整數部分為 0 的金額不符合；和第四步「分錄金額中有連續 0 的尾數」用同一條述詞，前端加入時預設 `mapping.gl.zerosThreshold`。個數不合時回「金額：連續 0 的個數須為 1 到 12 的整數」。
否定形式不納入空白，除非 `includeBlank` 明示為 true。

情境沒有排除區域；排除寫成條件本身的否定，例如「不屬於」或「不在區間」。`filter.preview` 回 `count`、`voucherCount` 與 `previewRows`，沒有待判定計數。
`count` 是直接符合條件的分錄筆數。另回 `summaryRowCount` 與 `countsWholeVoucherRows`，
畫面的「符合條件的分錄筆數」用 `summaryRowCount`。情境只有一組，而且這組只有一條預篩選條件「未預期借貸組合」時，
`countsWholeVoucherRows` 為 true，`summaryRowCount` 是命中傳票在查核期間內的全部分錄數，和條件篩選報告摘要的列數及
工作底稿 Step 4-1 的整張傳票標記相同；其他情境為 false，`summaryRowCount` 等於 `count`。
`query.highRiskSummaryScenarios` 的每個情境也回這兩個欄位，判斷相同；`voucherHitCount` 與 `rowHitCount` 的意思不變，`rowHitCount` 仍是直接命中列。

條件用到沒有來源的核心 GL 欄位時，不論哪一種比較方式，預覽或儲存都回 `invalid_scenario` 與條件列 `details`，
原因是「第 N 組第 M 條：「欄位名稱」尚未選擇來源欄」；有來源的欄位，空白資料仍可正常篩選。
哪些欄位有來源，和 `availableGlFields` 用同一個方法算，`approvalDate` 採 `sameAsGlDate` 與 `lineItem` 自動編號都算有來源。
期末財報準備日、授權編製人員清單與科目分類這些前置條件也由同一張 `JetRuleRequirements` 表決定，原因句和第四步讀同一張表。
沒有上傳國定假日清單時，`isHoliday`、`isNotHoliday` 與預篩選的假日過帳、假日核准同樣回 `invalid_scenario`，原因是
「尚未上傳國定假日清單，請先在第二步「國定假日與補班日」上傳」；`isNonBusinessDay` 也看週末，沒有清單時照常可用（裁定 2026-11-05-55）。
沒有上傳補班日清單時，`isMakeupDay` 與 `isNotMakeupDay` 同樣回 `invalid_scenario`，原因是「尚未上傳補班日清單，請先在第二步「國定假日與補班日」上傳」（裁定 2026-11-05-66）。
這項檢查用在所有比較方式（裁定 2026-10-22-05）；擴大範圍時篩選版本沒有推進，因為能存下的情境命中不變，舊情境由開案的逐筆檢查列為待修正。
文字讀回仍用「且」「或」，不改變條件的左至右合併順序。
現行篩選版本為 `filter-2026-10-14-v20`，篩選條件每條只表達一件事（裁定 2026-10-14-08）；預篩選版本為 `prescreen-2026-10-08-v10`。
用較早版本算出的命中需重新計算，舊預篩選結果視為過期、需重新執行預篩選。已停用的寫法列為待修正，定義不自動轉換。
日期運算值沿用 GL 匯入的 `DateNormalizer` 與案件 `DateParseOptions`，從預覽、儲存到重開及匯出都使用同一選項。
摘要關鍵字、開頭與結尾的值、尾數、人員代號與科目編號的文字輸入接受換行、半形與全形逗號、頓號及 Tab；已送成 `values` 陣列的文字值不拆逗號。
「包含任一文字」依實際送出的清單計算 100 個上限，修剪前後空白後仍區分大小寫，不拿不分大小寫的摘要筆數代替。
文字的 `startsWith`、`notStartsWith`、`endsWith`、`notEndsWith` 和 `contains` 一樣以 `values` 送出多個值，上限與計算方式相同；
以前存下只帶 `value` 的條件，後端照舊讀成一個值（裁定 2026-11-09-09）。
特定人員、特定摘要、空白摘要與特定尾數四張卡，分別使用人員、摘要與金額的 `fieldValue`，卡片來源仍保存於 `editorOrigins`。
超過 100 個值時回報拆成多條並放進子條件組，用 OR 連接；否定清單則用 AND 連接（裁定 2026-10-30-32）。
條件驗證訊息以「第 N 組第 M 條」指出位置；`FilterScenarioErrorDetails` 同時接受這個寫法與舊訊息，保留逐列錯誤定位。

規則新增 `type: "group"` 和 `type: "voucher"`，兩者以 `conditions` 保存子規則。`group` 的欄位條件綁定同一筆分錄；
各層的 `join` 仍依原有順序左折疊，最多巢狀八層。`voucher` 必填 `quantifier: "any" | "all" | "none"`，不帶 `side`。
子條件先在每筆分錄上計算，`all` 額外要求整張傳票至少有一筆。`voucher` 內可有 `group`，不可再嵌另一個 `voucher`。
空白傳票號碼對三種量詞一律不成立；revenueWithoutNormalCounterpart 和 unexpectedAccountPair 也不命中空白號碼的收入貸方。
要表達「這一側都不屬於某分類」，用 `voucher` 的 `none` 組合 `drCrOnly` 與分類 `is`。要表達某一側每筆都符合時，
`none` 加上該側與反向條件在沒有該側分錄時也會成立；要排除這種傳票，須再加一個 `any` 確認該側至少有一筆，並留意空白值是否列入反向條件。

已停用的寫法包括 `sameVoucher`、欄位附帶借貸方向、分類附帶方向或 `absent`、傳票附帶 `side`、核心金額 `signed`，
以及 `customKeywords`、`customTrailingZeros`、`trailingDigits`、`customPreparerEntryCount`、`customAccountEntryCount`。
這些寫法不讀回，也不寫出替代操作（裁定 2026-10-30-25）。五種停用的型別在解析時就回 `invalid_scenario`，
原因是「此條件的類型已停用，請刪除此條件」；其餘寫法當成無法讀取的條件，那一條寫「此條件內容有誤，無法儲存。請刪除此條件後重新設定。」
開案時經既有待修正檢查列出問題，原因寫「條件內容無法讀取」或上面那句，保留原定義，不自動轉換。

`accountSide`、`accountPair` 和 `specialAccountCategoryPair` 的 `categorySelection` 可選 `role`、`node`、`subtree`，
依序表示相同分類用途、僅分類本身、包含下層分類。借貸組合的兩側使用同一選取方式；兩側要各自選取時可組合單側條件。
省略時與舊版一樣使用 `role`；`categoryIds` 仍保存穩定分類 ID，名稱只用於呈現。
第一個正式版的畫面隱藏「選取依據」（裁定 2026-11-03-27），新建的條件一律送 `subtree`；契約與後端計算不變。
`subtree` 或 `node` 而且選的分類都沒有下層時，兩者選到的分類相同，條件說明不寫選取方式與實際納入分類；
畫面讀回與後端寫進報告的條件說明用同一個規則，`role` 照舊寫出。
`accountCategory.save.categories[].parentCategoryId` 可為既有分類 ID、同一次請求的暫存 ID 或 null；舊呼叫端省略此欄時保留原上層。
第一個正式版的分類設定不顯示上層分類與分類用途（裁定 2026-11-03-27），畫面新增的分類送 `parentCategoryId: null` 與 `semanticRole: "others"`，
已有分類照原值送回；契約照舊接受上層與用途。
新分類可在 `categoryId` 傳入唯一的 `draft-N`，N 是以 1 到 9 開頭的正整數，暫存 ID 最長 64 字元；子列可引用同一次新建的父列，不限列出順序。
後端先配置全部正式 ID，再解析上層關係，回應只包含正式 ID。省略 `categoryId` 的舊呼叫仍可新增，但無法供同次其他列引用。
分類 ID 重複、未知上層與循環仍拒絕，失敗不留下部分分類；新增分類的用途依請求保存，不由後端從上層推導。
同一回應及 `project.load.accountCategory` 都帶回上層 ID。新增分類仍由後端產生正式 ID；既有上層與同次新增的上層都可以選取。
分類角色不因移動階層而改變，階層變更沿用既有分類修訂與結果失效契約。
儲存分類設定會清除全部已存篩選情境與命中，審計員要重新設定情境；未指定借貸方向的舊條件維持原結果。
`accountCategory.save` 要刪除的自訂分類若仍有科目配對使用，回 `category_in_use`，整次儲存不生效，
訊息請審計員先把這些科目改到其他分類。分類只被已存篩選情境使用時不再擋下刪除，因為這次儲存會一併清掉情境。

`filter.preview`、草稿傳票查詢、`filter.scenario.save` 與 `filter.commit` 都只檢查條件有效性，不要求名稱或動機。
名稱與動機可以留白（裁定 2026-10-29-16、2026-10-30-14）：儲存時 `name` 或 `rationale` 是空白，後端依條件與 `editorOrigins` 記的卡片補上，
文字和第五步畫面自動帶入的相同；自動名稱和其他已存情境同名時加上數字。用了常用篩選條件卡片的情境仍帶 `source: "common"`；
其他來源代碼或沒有來源，一律當成審計員自己寫的情境。
驗證失敗回 `invalid_scenario`，`error.details` 帶每一條的位置與原因。
情境名稱最多 400 字（裁定 2026-10-19-26）；超過時回 `invalid_scenario`，訊息寫出上限，不截斷後儲存。
只有存檔內容損毀或程式送錯資料才會出現的錯誤，`message` 與 `details[].message` 一律是「此條件內容有誤，無法儲存。請刪除此條件後重新設定。」，
能歸屬到條件時帶組號與條號，不能歸屬時兩者為 null；原本的檢查只以程式寫死的英文代號記進支援日誌的
`filter.scenario_unreadable` 事件（`action`、`scenario_position`、`group_no`、`condition_no`、`reason_code` 欄），不含情境名稱或條件內容。
開案與一次處理一個情境時列出的待修正原因若有這類損毀，也記同一個事件。
通過驗證後，篩選條件編譯時的檢查仍沒過，只會是程式錯誤。這時回 `invalid_scenario`，`message` 是
「發生非預期的錯誤，此操作未完成。請再試一次。」；檢查代號與位置記進支援日誌的 `filter.compile_check_failed` 事件
（`action`、`group_no`、`condition_no`、`compile_check` 欄），不含欄位名稱或輸入值（裁定 2026-10-14-01）。

第五步一次只存或刪一個情境（裁定 2026-10-13-24）。三個 action 的 payload：

```text
filter.scenario.save    { position: int | null, expectedName?: string, scenario: {…}, populationScope?: "auditPeriod" }
filter.scenario.remove  { position: int, expectedName: string, populationScope?: "auditPeriod" }
filter.scenario.recheck { populationScope?: "auditPeriod" }
```

`position` 是畫面清單上的第幾個（從 1 起算）；`save` 傳 null 表示新增或另存副本，加在清單最後。給了 `position`
就必須帶 `expectedName`，也就是畫面上那個情境目前的名稱，後端用它確認畫面清單和資料庫是同一個情境。`scenario` 的形狀與
`filter.preview` 相同。其他已存情境由後端從資料庫讀出、照原樣寫回，不經畫面重組，也不用儲存規則重新檢查；
只有這次儲存的那一個用儲存規則檢查。這個情境通過檢查後，後端只保留每條條件那種類型用得到的欄位，
情境層與條件組層的內容照送入的保留；各類型保留哪些欄位寫在 `FilterConditionWireFields.cs`。前端送出的格式不變，
回應的 `scenarios` 裡，這個情境就是整理後的內容。其他已存情境要等下次被儲存時才整理。
`recheck` 不帶情境：能套用目前規則的情境改用目前規則，不能套用的照原樣保留並列在回應裡，不會整批失敗。

三個 action 的回應相同：

```text
{ ok, position, savedCount, scenarios, resultRef, filterScenarioCheck, staleState, reportWarning }
```

`position` 是這次存下的那一個的位置，移除與重新檢查為 null。`scenarios` 與 `project.load.filterScenarios` 同形狀。
儲存只計算並存下這一個情境的命中，以及每筆分錄符合第幾組第幾條；其他情境的定義、保存時間與存好的結果照原樣，
不重算（裁定 2026-10-30-33）。修改只換掉這個情境自己的結果。移除在同一個交易只刪掉這個情境的定義與結果，後面的情境在清單上的位置往前補，
它們的結果不動，也不重算。這兩種寫入後 `filterScenarioCheck.status` 為 `current`，`resultRef` 帶新的 revision；清單空了時 `resultRef` 為 null。
其他情境還不是目前規則版本，或存好的結果已標成過期（例如開案時整批改用新規則之後），寫入照整批處理：全部情境換成同一個保存時間、
改用目前規則並重算全部命中，篩選結果不再過期；`recalculatedCount` 是這次順帶改用目前規則的其他情境數。
`recheck` 一律照整批處理。還有情境不符合時只寫定義、清掉全部情境的命中，`status` 為 `needsEdit`，
`resultRef` 為 null，高風險條件彙總、命中查詢、條件篩選報告與第六步照樣回 `stale_result`。
`resultRef.revision` 是整批情境的版本，存或移除任何一個情境都會改變，前端只當成不能拆解的字串原樣送回；
`resultRef.generatedUtc` 是最近一次儲存情境的時間。`staleState` 是寫入後讀出的結果狀態。
寫入成功後讀不回結果狀態時，仍回成功並帶回已保存的情境清單，`resultRef` 與 `staleState` 回 null，`reportWarning` 沿用變更已儲存的提醒；前端保留原旗標，請審計員重新開啟案件（裁定 2026-11-10-02）。

錯誤：缺 `scenario`、`position` 不是正整數或 null、給了 `position` 卻缺 `expectedName` 時回 `invalid_payload`；
`position` 超出清單或 `expectedName` 和資料庫那一筆不同時回 `stale_result`，不寫入；新增時已有 10 個情境回
`scenario_limit_reached`；條件不合格或名稱和其他情境重複時回 `invalid_scenario`。驗證結果不是目前的、GL 欄位配對未確認時，
回應與 `filter.commit` 相同。失敗時資料庫不變。三個 action 都是變更型，同一時間只有一項變更作業。

情境可帶 `editorOrigins`，現行是第 2 版：`{ version: 2, nameIsAutomatic?: boolean, rationaleIsAutomatic?: boolean, groups: [{ cards: [...] }] }`。
每個 group 和 cards 的長度及位置對應原條件；cards 記帶入那一條的常用篩選條件卡片代號，例如 `blankDescription`，沒有卡片時記 null。
卡片代號是 1 到 64 個英文字母，不符時回 `editor_origins_card_invalid`。它只還原卡片，不參與審計判斷。
「非營業日之分錄」這張卡是組內的一條 `fieldValue`，欄位是 `glDate`、比較方式是 `isNonBusinessDay`，卡片代號記在這一條上（裁定 2026-11-03-09）。
2026-11-03 以前存下的是只有這一條的 `type: "group"` 子條件組，卡片代號記在括號那一條上；照舊能讀，重開後也照舊還原成這張卡。
後端只接受第 2 版；其他版本回 `editor_origins_incomplete`，當成條件內容損毀（裁定 2026-10-30-25）。
儲存回應與重新載入保留這份資訊。舊情境沒有資訊時不猜卡片，也不改寫原條件、來源或結果。
兩個 `IsAutomatic` 欄位分別記錄名稱及動機是否自動產生，只供編輯器恢復命名方式，不參與篩選判定。
省略時保留原文字，不從卡片或名稱相似度猜測；提供時必須是布林值。失敗的儲存不替換既有定義。

### 匯出

- `export.validationReports`
- `export.prescreenReport`
- `export.criteriaSelectionReport`
- `export.workingPaperStream`
- `export.accountMappingTemplate`

`export.validationReports` 只發布資料驗證報告與資料可靠性抽樣清單兩份，同名檔覆蓋。
`export.workingPaperStream` 仍檢查 `validationResultId`、`scenarioRevision` 及選取的情境位置，寫檔直接用儲存情境時存好的結果，
不重新計算；只照重算前的同一套檢查確認所選情境仍能套用目前規則。`export.criteriaSelectionReport` 同樣直接用存好的結果，檢查全部情境。
存好的結果標成過期時（例如開案時整批改用新規則之後），兩者才先用目前規則重算。
沒有已儲存情境也能匯出底稿（裁定 2026-10-29-16）：這時 `scenarioRevision` 送 null 或不送，`scenarioPositions` 送空陣列，
回應的 `publishedReport.sourceRef.scenarioRevision` 是 null；之後一儲存情境，這份底稿就標成過期。
有情境時 `scenarioRevision` 仍必須是目前版本，送 null 回 `stale_result`。有情境但一個都沒勾選時也照樣匯出（裁定 2026-10-30-15），
`scenarioPositions` 可以是空陣列。兩種情況的底稿第三步與第四步只留表頭。
底稿需要重算時只重算 `scenarioPositions` 所選情境，未選情境不參與本次來源欄位驗證，也不刪除其命中。
回應 `filterResultsCurrent` 表示全案結果是否目前有效；false 不代表本次底稿失敗，前端不得因此清除全案失效旗標。
底稿的 `sourceRef.filterDataRevision` 保存既有篩選資料版本，搭配驗證 run、情境版本及位置判斷來源有效性。
缺這個版本的底稿一律視為過期，和條件篩選報告的判斷相同。
匯出不要求已有條件篩選報告，也不因那份檔案過期、被刪除或被修改而拒絕；其他來源版本及正式底稿不覆寫的規則不變。
`export.workingPaperStream` 每次寫新的版本檔 `<案件前綴>_<yyyyMMdd-yyyyMMdd>_WorkingPaper_<yyyyMMdd-HHmmss>.xlsx`，不覆蓋、不刪
舊版。`export.accountMappingTemplate` 產生的是工作檔，不是報告：固定檔名 `<案件前綴>_AccountMapping.xlsx`
寫進案件資料夾、不進 `reports`。Payload 的 `onlyIfMissing` 預設為 false，手動產生會覆寫；
true 表示只在檔案不存在時建立，最後發布時也不覆蓋稍後出現的檔案。非布林值會回 `invalid_payload`。
Payload 的 `resultId` 是選填，畫面會附上目前驗證結果的編號，但不再當成產生工作檔的前置。回應中的 `validationResultId` 只記錄當下已有的可用驗證結果，沒有則為 null。
回應為 `{ ok, filePath, fileName, rowCount, validationResultId, disposition }`；`disposition` 為 `created`
或 `kept`。保留原檔時不讀取其中內容，`rowCount` 為 null。審計員用 Excel 填好 `Standardized Account Name*` 欄存回原檔，再由
`import.accountMapping.fromFile` 讀同一路徑。
五種報告的檔名均加入案件查核起訖日 `yyyyMMdd-yyyyMMdd`；同期間重匯維持原覆寫或版本規則。
報告項目保留 `fullPath` 供本機定位用途，畫面只顯示檔名、建立時間與大小；此欄位不寫入索引，重開或搬移案件後重新解析。
`fileName` 仍只有相對檔名。
四個報告匯出 action 的回應另提供完整 `reports`，供前端同步容量整理後的清單；`publishedReport`
或 `publishedReports` 只表示這次發布的報告。發布成功後，清單刷新是可省略的呈現工作，不是另一個發布條件。
可恢復的清單讀取失敗或 3 秒內未完成時，仍回 `ok: true` 及本次產物，`reports: null`，另回
`reportWarning`。前端保留原清單並加入本次產物，在旁邊提示「檔案已產生，但報告清單沒有更新；重新開啟案件就會更新，不必重新產生。」。
清單之後讀取成功就清除該暫時提示；不得將 null 當成空清單，也不把程式錯誤偽裝成成功。
四個報告匯出 action 的回應另有 `warnings` 字串陣列，平常是空陣列。報告已發布、卻寫不進操作紀錄時，
回 `ok: true` 及本次產物，`warnings` 是「檔案已產生，但這次產生的檔案沒有寫入操作紀錄。」，前端把這句當成提醒放進訊息區。
失敗記在支援日誌的 `project_audit.write_failed`（裁定 2026-10-19-20）。
清單即將超過 4,096 筆時，只移除已確認檔案不存在的底稿紀錄。
產生內容後、正式發布前會再次確認，途中被放回或無法確認的檔案仍保留。若仍超限，本次不發布新檔，
使用者可自行整理不需要的舊底稿後重試。

### 訊息、原生視窗與開發工具

- `log.append`
- `log.recent`
- `support.log.export`
- `host.selectFile`
- `host.selectFiles`
- `host.openFolder`
- `host.exitApp`
- `dev.db.overview`
- `dev.db.tableData`
- `dev.log.exportFile`

原本另有核對 SQL Server 案件登記與資料夾的 `dev.db.reconcile`，隨 SQL Server 暫時移除（裁定 2026-11-06-08）。

`host.openFolder` 的既有參數為 `{ target: "projectFolder" }` 或 `{ reportId }`，兩者只能擇一，不接受
任意路徑。第四、第五與第六步的報告入口共用 `reportId`，由後端在目前案件解析檔案並交給檔案總管選取。過期的
工作底稿仍可定位；檔案已移動或刪除時回傳 `report_not_found`，不開啟其他位置。

`support.log.export` 在 Release 與 Debug 都可用。輸入 `{ projectId?, correlationId? }`。有 `projectId` 時匯出該案件的整份支援紀錄，
包括之前每次開啟留下的事件，再補上這個程式還沒寫進紀錄的事件；有 correlation 時只留該次操作的行。內容在記進記憶體或案件紀錄之前
就已去識別：不保存 SQL、參數值、檔名、絕對路徑、案件名稱或原始 exception message。輸出固定為案件目錄內的
`JET-support-*.txt`，每行一個 JSON；案件目錄不存在或是 reparse point 時直接失敗，不改寫到其他位置。
省略 `projectId` 時是一般工具，不需要先開案件：匯出這個程式記憶體裡目前保留的全部去識別紀錄，忽略 `correlationId`，
寫到 `%LOCALAPPDATA%\JET\logs\`，檔名同樣是 `JET-support-*.txt`，回應的 `scope` 為 `all`，仍回傳 `filePath` 與 `lineCount`。
有 `projectId` 時 `scope` 為 `project` 或 `correlation`。不指定案件的一般工具依裁定 2026-10-13-26。
畫面上，訊息面板的「輸出支援日誌」帶目前案件的 `projectId`；案件清單頁尾的「輸出支援日誌」送 `{}`，
這顆按鈕只在 Debug 組建出現。action 本身在 Release 仍註冊，供訊息面板使用。

畫面照正常操作送不出、只有前端送錯資料時才會被後端拒絕的格式檢查，例如 `mapping.commit.gl` 的 `amountMode`、
`rdeFields` 或 `postingStatusPolicy` 形狀不對，錯誤碼不變（`invalid_payload` 或 `unsupported_mode`），
訊息改為非預期錯誤的同一句「發生非預期的錯誤，此操作未完成。請再試一次。」。沒過的是哪一項檢查，
只以程式寫死的英文代號記進支援日誌的 `action.payload_rejected` 事件（`payload_check` 欄），不含資料內容、
本機路徑或來源欄名。
診斷匯出不要求案件已載入，也不解析 `project.json`；設定損壞或遺失時仍可將該次載入錯誤匯出到既有案件目錄。
兩種日誌匯出都使用請求的案件 ID 定位，不使用設定檔內的 ID 改變目的地；非法 ID 或不存在的目錄回 `project_not_found`。

匯入失敗的紀錄包含來源序號、處理階段、最後成功列、可取得的失敗列欄、解析設定、元件版本與交易回復結果。
`import_progress_failed` 表示進度通知失敗，不再歸為讀檔錯誤。取消保留原取消語意。未知列欄明示為未知。

每次操作的 `action.start` 以 `payload_shape` 記下輸入結構：只有欄位名稱、值的型別與陣列筆數。模式、借貸別這類欄位的值
是 JET 認得的代碼才照記，前端送來的未知值只記型別。欄位配對底下的值、檔名、路徑、名稱與條件值也只記型別，
案件代號連欄位名稱都不記。同一案件同一動作上一次失敗或取消時，下一次 `action.start` 的
`retry_of` 會記下那次操作的 correlation。成功的 `action.end` 以 `result_refs` 記下批次、執行、報告與條件的版本代號、筆數和狀態，
不含檔名、路徑或金額。取消改記 `action.cancelled`，不算失敗；`cancellation_requested` 為 true 表示使用者或關窗要求取消，
false 表示程式內部丟出取消例外。

分步驟的操作失敗或取消時帶 `failure_stage`，寫出停在哪個處理步驟，例如案件載入的 `metadata_read`、報告的 `report_publish`；
還沒進入任何步驟就失敗時不帶。匯入沿用前述匯入失敗紀錄的處理階段。
報告寫入或發布失敗另帶 `report_kind`；發布失敗時，`final_in_use` 與 `final_read_only` 記下同名正式檔是否被其他程式開著、
是否唯讀，因為兩種情況都回報拒絕存取。
報告、範本或底稿的工作表寫不出去時，`failure_stage` 在原本的步驟後面接 `worksheet_write`，例如 `report_write > worksheet_write`。
這時另帶 `sheet_name`，寫出工作表名稱，例如 `V_Report 6`；名稱都是程式寫死的範本名稱，不是案件資料，查不到時不帶。
有儲存格含 XML 不允許的字元時，`cell_reference` 寫出第一個這樣的儲存格，例如 `E2`；`invalid_codepoint` 寫出那個字元的
十進位碼位，例如 31 表示 U+001F。這些欄位不記儲存格文字。取消不算寫入失敗，不帶這些欄位。
檔案錯誤帶 `io_category` 與 `win32_error`。主要工作完成或失敗後，
更新案件步驟、刪除案件資料夾或放開工作鎖失敗時記 `action.step_failed`，`step` 寫出是哪一步。

例外列出整條例外鏈。每層寫型別、系統錯誤碼與可取得的位置代碼，例如 JSON 的行與位元組位置、資料庫錯誤碼；
再列丟出處的前三層方法與所有 JET 方法的名稱和 IL 位移，其餘框架方法只寫省略幾層。不輸出例外訊息、檔案路徑或原始碼行號。
維護者依匯出檔開頭的版本與模組代號取得相應版本的程式，就能找到呼叫位置。

記憶體保留最近最多 32 次失敗摘要，總輸出仍受容量限制。`support.snapshot` 的 `schemaVersion` 為 3，
`processInstance` 是這次啟動的隨機代號。每筆事件帶記下它的程式的 `processInstance`，以及在該程式內遞增的 `sequence`。
不指定案件的匯出裡序號跳號，表示中間的事件已因記憶體容量沒有保留；`processEventsOmitted` 是這個程式因此省略的事件數。
案件紀錄裡同一個程式的序號跳號，通常是中間的事件屬於其他案件或不屬於任何案件；放棄的事件另有 `support.history_gap` 寫出筆數。

案件的支援紀錄存在案件資料夾的 `support-log/events.ndjson`，整個案件一個檔，保留到刪除案件；位置、分段、續接方式與保留期限
依裁定 2026-10-21-08。建立或載入案件並取得工作鎖之後，JET 建立 `support-log` 資料夾並記一筆 `support.case_opened`，
帶 `opened_by`（`create` 或 `load`）、`database_provider`、`app_version`、`informational_version`、`module_version_id` 與
`build_configuration`，不記電腦、帳號或路徑。之後屬於這個案件的事件由背景依序附加，審計操作不等寫檔。
紀錄檔自己也寫幾種行，`sequence` 都是 0：第一行是 `support.history_started`，`history_start` 為 `case_created` 表示案件在這次建立，
`no_earlier_record` 表示之前沒有保存的紀錄，例如紀錄檔被刪除的案件；每個程式第一次寫入這個案件時先寫一行 `support.history_process`，
帶同樣的版本與模組欄位。

案件還沒在這個程式開啟時，例如載入失敗，事件只附加到已經存在的紀錄檔，不建立資料夾或檔案；沒有紀錄檔時先留在記憶體，
開啟案件後依原順序補寫。紀錄寫不進去時不影響審計操作：事件先保留，下次寫入、匯出前或程式正常結束時依原順序補寫。
每個案件最多保留 2,000 筆，超過時放棄最舊的；補寫時先寫一行 `support.history_gap`，`missing_count` 是放棄的筆數，
並帶最近一次失敗的 `io_category`、`win32_error` 與 `error_type`。兩個 JET 程式碰到同一個案件時輪流附加，行不會交錯。
刪除案件時，這個程式先寫完手上的紀錄再刪除資料夾，之後不再寫入該案件；重新建立同名案件時從新的起點開始。
案件資料夾或支援紀錄資料夾是 reparse point 時不寫入。

指定案件的匯出，第一行 `support.snapshot` 的 `fields.history` 摘要整份紀錄：
`status` 為 `saved` 表示讀到紀錄，`not_started` 表示還沒有紀錄檔，`read_failed` 表示讀不到紀錄，這時 `readFailure` 寫出錯誤類型與系統錯誤碼，
匯出只含這個程式還沒寫進紀錄的事件。`start` 與 `startUtc` 是紀錄的起點。`unreadableLines` 與 `unreadableLineNumbers`
是無法解讀的行，例如程式寫到一半就結束；`incompleteTailBytes` 是匯出當下最後一行還沒寫完的位元組數，這部分不讀。
`gapMarkers` 與 `markedMissingEvents` 是缺口的行數與合計筆數，`openings` 與 `processes` 是開啟次數與寫過紀錄的程式數。
`processesNotClosed` 列出開啟案件後沒有記到 `project.releaseLock` 成功的程式代號，通常是程式沒有正常結束，不含匯出的程式本身。
`unsavedEvents`、`droppedEvents` 與 `lastWriteFailure` 是這個程式還沒寫進紀錄的筆數、已經放棄的筆數，以及最近一次寫不進去的原因。
程式被強制結束時，還沒寫進紀錄的事件會遺失，紀錄裡也不會有缺口行；`processesNotClosed` 仍會列出這個程式。

`dev.*` action 只服務開發面板，一般使用者流程不能依賴。`dev.log.exportFile` 輸入
`{ projectId, correlationId? }`，從本次 Debug 程序的完整檔案 sink 篩出該案件的紀錄；有 correlationId 時
也一併帶出同一次操作的紀錄，因為 picker 上載入或刪除失敗時案件尚未開啟，那次 action 的紀錄只有
correlation、沒有案件 id。sink 不可讀時退回 ring buffer，並於回應標記 `source`。
這份原始紀錄可能含 SQL、參數及案件資料，只能留在本機診斷；輸出固定為該案件目錄內的
`JET-dev-log-*.txt`。兩個檔案匯出都回傳 `filePath` 與 `lineCount`，並以同目錄暫存檔完成後原子改名。

## 找精確欄位的位置

| 想確認的內容 | 位置 |
|:---|:---|
| 請求與回應的外層格式 | `Bridge/JetWebMessageBridge.cs` |
| 前端 action 名稱與 method 轉換 | `wwwroot/js/jet-api.js` |
| 實際處理器與 `payload` 解析 | `Application/Handlers/` |
| 穩定錯誤碼 | `Domain/JetActionException.cs` |
| GL／TB mapping keys | `Domain/Rules/MappingSpecs.cs` 與 `JetFieldCatalog.cs` |
| 進階篩選契約 | `Domain/Contracts/FilterContracts.cs` 與 `Domain/Rules/` |
| 報告產物種類 | `Domain/Contracts/ReportContracts.cs` |

過往 1,000 行的逐 action 說明保存在
[`history/superseded/action-contract-manifest-2026-08.md`](history/superseded/action-contract-manifest-2026-08.md)。
它能協助追查舊設計，但欄位有疑義時仍要回到現行處理器與測試核對。
