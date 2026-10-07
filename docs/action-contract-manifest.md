# JET action 介面契約

更新日期：2026-09-23

這份文件是前端、WebView2 Bridge 與 C# 處理器之間的 action 登錄表。它適合用來搜尋 action 名稱，
不需要逐段閱讀。

精確的 `payload`（參數）與回應格式，應由對應處理器、Domain 契約和測試共同確認。修改 action 時，
必須在同一個變更中更新本檔、`wwwroot/js/jet-api.js`、處理器註冊與相應測試，不能只改其中一邊。

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
使用 `caseName`。`error.details` 出現在兩種錯誤。`invalid_scenario` 這類能歸屬到條件列的錯誤，每一項帶第幾組、
第幾條（從 1 起算，無法歸屬時為 null）與那一條的原因；`message` 仍是全部原因合併的整段文字。步驟五用
`details` 把對應的條件列標紅並就地說原因，沒有 `details` 的錯誤維持整段訊息。
`mapping.commit.gl` 與 `mapping.commit.tb` 的 `projection_failed` 也帶 `details`：每一項是同一個來源欄、同一種問題，
`group` 與 `rule` 為 null，`sourceColumn` 是那個來源欄，`message` 寫出有幾列、哪些值在哪幾列，以及一次處理方式。
`message` 是開頭一句（幾列無法轉換、沒有儲存，收集的列數少於總數時另說依前幾列整理）接上各項文字。
第三步逐項列出，並依 `sourceColumn` 提供「前往設定」；其他錯誤的 `sourceColumn` 為 null。
`correlationId` 串起同一次 action 的開始、結束或錯誤事件；picker 可用它輸出該次
去識別支援紀錄。完整錯誤碼登錄位於 `Domain/JetActionException.cs`。

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
- 會變更案件資料的長作業由後端執行閘門協調；背景心跳與取消有明確的例外規則。
- 大量資料只回摘要、中繼資料與有界分頁。`query.*Page` 使用游標與每頁筆數，不回完整母體。
- 進度事件不攜帶資料列，也不是第二個狀態來源。
- 原生選檔、開資料夾、退出與取消控制保留呼叫執行緒；其他 action 及其資料庫保留範圍由 dispatcher 排到背景執行，不改原有互斥分類。
- Debug／AgentGuiTest 專用 action 不得出現在 Release 的使用者流程。

## 目前支援的 action

### 科目清單直接分類

`query.accountMappingPage` 回傳本案科目與已保存分類，每頁預設 100 筆、上限 500 筆。
參數為 `cursor`、`pageSize` 與選用的 `search`、`categoryId`；科目來源與 Excel 配對範本相同，並保留先前匯入的額外科目。
回應 `rows` 含 `accountCode`、`accountName`、`categoryId`，另回 `nextCursor`。尚無配對的 `categoryId` 為 null；
Excel 空白分類則沿用既有的 Others 處理方式，清單顯示其目前分類。
科目代號空白的來源列仍保留在資料驗證中，不列為可指定分類的科目。

`accountMapping.save` 接收 1 至 500 筆 `changes`，每筆含 `accountCode` 與 `categoryId`。
只更新指定科目，其他頁及 Excel 已匯入的分類保留。科目或分類不存在、重複科目及取消均不得留下部分變更。
每次變更保留獨立批次及來源列，原匯入列不改寫。儲存及預篩選、篩選結果失效在同一交易完成，資料驗證結果保留。
回應保留科目配對的既有狀態欄位；直接編輯後的 `rowCount` 為目前已保存的科目數。
配對檔與本案科目的兩項差異筆數只由檔案匯入回應及差異清單查詢提供，這個動作不另外掃描 GL。
此動作為互斥寫入，清單查詢為唯讀。Excel 匯入仍整份取代科目配對。
`query.accountMappingPage.categoryId` 指定分類時，查詢包含該分類及其所有下層，與搜尋文字同時篩選。仍只回有界分頁，不回整份科目表。
第五步科目編號清單沿用既有 100 個值上限；選入整個分類時，若分頁尚有下一頁或合併後超限，保留原清單並提示縮小範圍或改用科目分類條件，不只加入第一頁。

以下名稱逐字對應 `wwwroot/js/jet-api.js` 的 `SUPPORTED_ACTIONS`。

### 系統與取消

- `system.ping`
- `system.databaseInfo`
- `system.whoAmI`
- `operation.cancel`

### 案件

- `project.listLocal`
- `project.list`
- `project.create`
- `project.update`
- `project.load`
- `project.deletePreview`
- `project.delete`
- `project.saveProgress`
- `project.heartbeat`
- `project.releaseLock`
- `project.loadDemo`（開發／GUI 測試專用）

正式前端建立案件時，`caseName`、`periodStart` 與 `periodEnd` 必填，`projectCode`、`entityName`、
`lastPeriodStart` 選填。`databaseProvider` 建立後不能更改。操作人員不由表單提供；
`ProjectCreateHandler` 直接使用目前 `CurrentPrincipal.ShortName`。舊版呼叫者即使仍送 `operatorId`，後端也不採用。
案件編號或客戶名稱留空時，`project.json` 仍保留空字串欄位，讓既有案件、清單與報告使用同一份資料形狀。

`project.listLocal` 與 `project.list` 不再隱藏無法讀取的本機案件。正常列格式不變；損壞列只回
`{ projectId, databaseProvider: null, loadError: { code, message } }`，說明檢查鎖檔、從備份復原或另建案件重新匯入，
不捏造資料庫種類、名稱、期間或時間。其他正常案件仍可開啟，錯誤列也不會被重複列成只存在伺服器的案件。

建案時查核起始日不得晚於截止日，否則回 `invalid_payload`，`field` 為 `periodStart`，不建立案件。
既有案件的期間格式不合法或起日晚於迄日時，載入回 `invalid_project_schema`，提示從備份復原或另建案件重新匯入。
檢查在物化伺服器案件、取得案件鎖及切換作用中案件之前完成，不自動改寫日期；SQL Server 登錄分支只有假資料測試與編譯，未實機執行。
正式畫面送來的空白 `caseName`（包括全形空白）回 `invalid_payload`，不改成隨機案件名稱；程式化呼叫未提供名稱時仍可沿用既有識別值產生方式。
`project.create` 的成功回應另帶 `warnings` 字串陣列：準備日早於查核起始日，或晚於查核截止日超過一年時，提醒確認年份但不阻擋。

`project.update` 修改作用中的案件，只讀 `entityName`、`projectCode` 與 `lastPeriodStart`。省略欄位保留原值，null 或空字串清除該選填值；
案件名稱、查核期間、建案者與資料庫種類不變，其他 payload 欄位不採用。回應包含 `project`、`warnings`、`invalidatedResults`、`staleState`、`reportArtifacts` 與 `reportArtifactWarning`；`artifacts` 暫留為舊呼叫者的別名。
`invalidatedResults` 由後端的失效政策提供本次需清除的範圍，前端據此清掉尚未儲存的舊預覽；`staleState` 只描述已持久化結果的過期狀態。
只有準備日實際改變時，才依既有失效政策清除預篩選、篩選命中及全部已存篩選情境，保留驗證結果與配對；同值重送或只改文字不清除審計結果。
任一可改欄位真正改變時，既有報告索引標為過期，但不改寫或刪除已匯出的檔案；再次匯出才使用新資料。這是互斥寫入，SQL Server 登錄同步只經編譯。
`project.load` 與 `project.update` 回應的 `project.rocDateEnabled` 帶回案件既有的民國年解析選項，供日期條件使用；這不是可由 `project.update` 修改的新欄位。
`project.load` 的 payload 必填 `projectId`，另可選填 `databaseProvider`，值是案件清單列上的資料庫種類。
本機沒有這個案件資料夾時，只有 `databaseProvider` 為 `sqlServer` 才會去線上登錄找只存在伺服器上的案件；
沒帶或是其他值時直接回 `project_not_found`，不查線上登錄。本機已有案件資料夾時，資料庫種類一律以 `project.json`
為準，這個欄位不影響載入。建立案件後的載入不必帶。

只有 SQL Server 案件會送 `project.heartbeat`：前端載入資料庫種類為 `sqlServer` 的案件後才啟動心跳計時器，
SQLite 與 DuckDB 案件不送。這個 action 本身與 `project.load` 回應的 `heartbeatSeconds` 都沒有改變。

`project.load` 回應另含 `filterScenarioCheck: { status, recalculatedCount, problems }`，說明開案時用目前篩選規則
檢查已儲存情境的結果。`status` 為 `current`（沒有情境或都已是目前規則）、`recalculated`（整批已改用目前規則，
`recalculatedCount` 是情境數，舊命中已清除，條件篩選報告與底稿依新的 revision 標為過期）、`needsEdit`
（至少一個情境不符合目前規則，整批不動）或 `inconsistent`（同一批的儲存時間、位置或定義格式不一致，整批不動）。
`problems` 每筆為 `{ position, name, messages }`，只在 `needsEdit` 時有內容。改版發生在戳記上次開啟時間之前，
之後載入失敗時改版仍保留。

`project.load` 回應另含 `previousMapping: { gl, tb }`。重新匯入 GL 或 TB（取代或附加）會讓已確認的配對失效；
匯入時把失效前最後一次確認的配對另存一份，那一側還沒重新確認時，這裡回傳它，形狀和 `mapping.gl`、`mapping.tb`
相同，但不帶 `sourceBatchId`。它不是有效配對，驗證、篩選與匯出都不讀它，`mapping.commit.gl` 只從中沿用攸關資料元素
欄位識別碼（見下方欄位配對一節）。前端只拿來預填草稿，只保留新資料仍有的來源欄，
並把新資料沒有的欄位寫出來請審計員重新選。那一側有有效配對、沒有匯入資料，或從來沒有確認過配對時為 null。
SQL Server 的保存與讀取只經過編譯，沒有實機執行。

`project.load` 不會因為報告檔而拒絕載入。報告檔在 JET 之外被改寫、改名或刪除時，回應的 `reportArtifacts`
每筆多一個 `fileState`（`asPublished`、`modifiedOutside`、`missing`）讓前端標示，重新匯出即可。舊版留下的
輸出紀錄檔 `.report-artifacts.mutation-v1.json` 會在載入時直接刪除，並記一筆 `artifact.journal.discarded`
支援日誌事件。`project.delete` 只取得案件外的 artifact lease，刪案不受報告檔狀態影響。
DuckDB 案件的資料庫若還有操作在使用，`project.delete` 回 `operation_in_progress`，不刪資料庫、
`project.json` 或案件資料夾；等那個操作完成後再刪除即可。

`project.deletePreview` 的 payload 只需 `projectId`，回應為 `{ projectId, databaseProvider, reportCount, workpaperCount }`。
這是可併行的唯讀動作，不開啟案件、不切換 session，也不持有目前案件的資料庫。SQL Server 案件先核對與刪除相同的授權。
計數只包含 JET 索引中仍存在的相異檔名，外部修改過的檔案仍計入，已遺失檔案及科目配對工作檔不計入報告數。
前端另明說整個案件資料夾及其他檔案都會刪除；計數失敗顯示未知及重試，不假設零份，也不增加刪除關卡。

GL、TB、科目配對、授權清單、行事曆匯入，GL、TB 配對確認，科目分類或配對儲存、授權清單清除、非工作日設定與案件資料更新，
成功回應共同帶 `invalidatedResults: { validation, prescreen, filter, filterScenarios }`、`staleState`、`reportArtifacts` 與 `reportArtifactWarning`。
前者是本次需要清掉的結果範圍，包含未儲存的預覽；後者只描述已保存結果是否過期。前端不再用 action 名稱或 setter 自行推算失效範圍。
`filterScenarios` 為 true 時，後端已刪除全部已存篩選情境與命中，前端應把第五步回到預設狀態。
它和 `filter` 同值：GL 或 TB 匯入與配對確認、科目配對與分類、授權清單、行事曆、非工作日與財報準備日的實際修改為 true。
GL 與 TB 的匯入或配對確認都回傳四個 true，使用同一條失效規則；非工作日或財報準備日同值重送、案件資料只改文字時為 false。
這是使用者 2026-10-07 裁定的「上游修改時清除下游」。
情境已清除時 `staleState.filter` 回到 false，表示沒有可重跑的篩選，而不是待重跑。已匯出的報告與底稿檔不改寫，索引照舊標成過期。
資料庫內的修改和清除在同一筆交易完成，修改失敗時一起回復，情境與命中都保留。
財報準備日存在 `project.json`，無法和資料庫共用交易。清除結果的交易先不提交，報告索引標過期、新設定存好後才提交；
存檔失敗時交易回復，情境、命中與預篩選結果都保留，舊日期不變。提交失敗時寫回舊設定。存檔失敗前報告索引可能已標過期，寧可多標，不會漏標。
清單刷新包含重新判定報告過期，最多等待 3 秒；失敗時回 null 並提示變更已保存，前端保留舊清單。匯出成功後的清單也先重新判定過期，
失敗只提示檔案已產生，不要求重做匯出。條件篩選報告與底稿一樣記錄 `sourceRef.filterDataRevision`；舊報告缺這個版本時視為過期，不重寫檔案。
`project.load` 會把舊規則版本的驗證或預篩選摘要從 `latestRuns` 移除，並在 `staleState` 明示過期，不冒充從未執行。

### 合成 demo 檔案

- `demo.exportGlFile`
- `demo.exportTbFile`
- `demo.exportAccountMappingFile`
- `demo.exportAuthorizedPreparerFile`

這四個 action 與 `project.loadDemo` 只供 Debug 或不可發布的 AgentGuiTest 組態使用。

### 匯入與行事曆

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

科目配對先辨識確切欄名，去頭尾空白且不分大小寫：科目編號為 `GL_Number`（含 `GL_NUMBER`），
科目名稱為 `GL_Name`（含 `GL_NAME`），分類為 `Standardized Account Name*` 或 `STANDARDIZED_ACCOUNT_NAME`。
三欄都先認領，再依分類、科目編號、科目名稱的順序比對尚未認領欄的中英文關鍵字；仍找不到的欄，依剩餘欄位順序補上。
只要有欄位依順序判斷，匯入回應的 `columnMappingWarning` 就會說明來源欄與用途，畫面顯示提醒但不阻擋匯入；否則回 null。
匯入與舊案遷移共用此規則。資料預覽一律讀已儲存的配對，不重新判讀欄名。
Excel 的 autoFilter 與隱藏列不限制匯入範圍，資料列仍全部讀入。
`.xlsx` 科目配對檔的第一個標頭不足三欄時，沿原有做法改讀第三列標頭；略過前兩個實際列號，空白列也算在內，
不會略過第三列標頭或第一筆資料。

GL 與 TB 的主資料匯入支援 `.xlsx`、`.xlsm`、`.xls`、`.csv`、`.txt`、`.mdb` 與 `.accdb`。
`.xlsm` 和 `.xlsx` 共用唯讀 Open XML 讀取器，`.xls` 使用逐列 BIFF 讀取器，都不啟動 Excel 或執行巨集。
Access 透過公司既有 Office 資料介面唯讀讀取一般資料表；`sheetName` 指選定資料表。`.xlsb` 不支援。
一個 GL 或 TB 資料集可以追加多個欄位相同的檔案或工作表；這只會合併列，不會把兩份 TB 自動解讀成期初與期末。
GL 與 TB 匯入成功回應的 `warnings` 為字串陣列。追加來源的實際檔名、工作表與列數都和某份既有來源相同時，
提醒「可能已匯入過」，但本次照常提交；取代模式不提醒，不以預估列數判斷，也不把這個提醒當成去重功能。
文字檔來源有用引號包住、內含換行的欄位時，每個來源再加一則提醒，寫出筆數與前五個列號，匯入照常完成；
支援日誌另記一筆 `import.multiline_fields` 事件，只含來源序號、筆數與列號，不含檔名或內容。

文字檔（`.csv`、`.txt`）照一般 CSV 讀法：只有欄位開頭的引號會被當成引號，欄位中間的引號（例如英吋符號）是一般字元，
不會把後面的列併進來；欄位開頭的引號到下一個引號之間可以換行，讀成同一格。
引號沒有成對時 `import.previewFile` 與匯入以 `file_read_error` 失敗，訊息寫出第幾列。
開頭的空白列會略過，第一個有內容的列是欄名。標頭之外有資料的欄合成 `COL_n` 佔位欄，和 Excel 讀取器相同。
`import.inspectFile` 與 `import.previewFile` 的回應多一個可空的 `notices` 字串陣列，是給審計員看的提醒，不擋匯入；
目前只有文字檔只讀到一欄時回「可能是報表格式」這一則。`.xls` 的錯誤儲存格（`#N/A`、`#REF!` 等）保留 Excel 顯示的原文，
和 `.xlsx` 相同；`.xls` 與 Access 的是或否值寫成 `true`、`false`，只有時間的值用 `hh:mm:ss`，也和 `.xlsx` 相同。
來源檔被其他程式開著、沒有權限、解碼失敗或結構讀不通時，`file_read_error` 的訊息只寫檔名與下一步，不寫完整路徑，
也不含 .NET 的英文例外文字。這兩個 action 失敗時和匯入一樣附上支援日誌的匯入診斷欄位；`import.inspectFile` 成功時
另記一筆 `import.inspect` 事件，只含格式、欄數、工作表數、編碼與分隔符。解碼失敗時，診斷欄位的 `encoding`
寫實際用來解碼的編碼；自動偵測編碼時也一樣，不寫 `unknown`。

授權清單匯入可傳入 `sheetName`，`sourceColumn` 必填並須精確對應來源標頭；畫面要求審計員選取與 GL
傳票建立人員相同的識別欄。省略欄位或所選欄沒有有效識別值時回 `invalid_payload`，`field` 為 `sourceColumn`，
並說明如何改選欄位或換檔重試；失敗不取代原名單與相依結果。
回應另含 `sourceColumn`、`sourceRowCount`、`blankRowCount`、`duplicateRowCount` 與 `matchedPreparerCount`。
`rowCount` 是去除空白及重複後的有效識別值數量；識別值去掉頭尾空白後不分大小寫去重，和 GL 建立人員的比對同一規則。
原始資料列數不含標頭與完全空白列，空白數只計所選識別欄為空、其他欄仍有資料的列。
三項匯入統計與識別欄保存於案件資料庫；`project.load` 帶回相同摘要，舊名單未保存的統計為 null，不捏造為 0。
`matchedPreparerCount` 是有效名單中出現在 GL 傳票建立人員的識別值數量，不是 GL 分錄數；尚未確認 GL 建立人員配對時為 null。
0 人比對成功只提醒可能選錯識別欄，不擋匯入。`mapping.commit.gl` 另回 `authorizedPreparerState` 更新這項比對；GL 重新匯入後清除舊比對。
名單仍不保存私人原始路徑、檔名及匯入時間。
`import.authorizedPreparer.clear` 無必要 payload 欄位，回應 `{ cleared: true }`；同一交易移除清單、
預篩選與篩選結果及全部已存篩選情境，保留 GL、TB 與驗證。重試及重開後仍可重新匯入；重新匯入不會恢復情境，要重新儲存。

### 欄位配對與科目分類

- `accountTaxonomy.save`
- `accountMapping.save`（直接儲存科目清單中改動的分類）
- `mapping.valueProfile`
- `mapping.restoreDraft`
- `mapping.commit.gl`
- `mapping.commit.tb`

2026-09-17 移除 `mapping.autoSuggest` 的 action、前端呼叫與按鈕。GL 與 TB 欄位由審計員手動選取；
已保存配對仍保留。2026-09-22 使用者要求暫時移除從報告載入配對草稿的功能；
前端入口及事件綁定已撤下，`mapping.restoreDraft` 暫留後端相容契約，操作設計待複盤。

GL 的 `postDate` 在畫面顯示為「總帳入帳日」，用來界定案件期間；`voucherDate` 顯示為「傳票日期」，是
回溯過帳判斷及一般日期條件使用的選填日期。`docDate` 對應「傳票核准日」及資料庫 `approval_date`，
不是傳票日期的別名。完整對照見 `jet-guide.md` 第 2 節；action 欄位名稱、資料庫欄位與既有報告正準名不變。

`mapping.commit.gl` 和 `mapping.commit.tb` 的 commit 指確認配對設定並建立標準資料，成功後畫面顯示
「已確認配對」及時間，不稱為提交審計結論。成功回應才更新完成狀態；案件或來源已更換時，舊回應不得
寫回目前畫面。操作名稱一致不代表改動既有 action 識別字。
GL 與 TB 都在投影資料的同一交易內保存配對與確認時間；任一步失敗或取消，保留原投影與配對。
匯入、配對、驗證、預篩選及篩選已完成資料保存後，案件步驟更新失敗只記錄 `workflow.milestone` 支援日誌，
不把已完成的操作誤報失敗。這只適用附帶的步驟紀錄，不放寬審計結果或使用者修改案件資料的保存錯誤。

GL 的 `side` 與 `flag` 金額模式都必填 `mapping.dcDebitCode` 和 `mapping.dcCreditCode`；兩者是字面代碼，不是來源欄名。
前後空白修整後以 `OrdinalIgnoreCase` 比對，兩碼不得相同。來源借貸別空白或不符合任一代碼時，即使金額為零也以 `projection_failed` 列出來源列號，
不把未知值當成貸方；金額或日期解析失敗時，GL、TB 投影都保留原有資料。TB 不新增貸方代碼欄。
舊案已投影資料不自動重寫，重新確認 GL 配對才要求填齊兩碼。舊報告還原配對草稿若缺碼，明說回第三步填齊，不猜代碼或只還原一部分。
千分位逗號只接受每組三位；括號可表示負數，不可再疊加正負號。GL、TB、RDE 金額與篩選運算值共用相同解析。
Excel 序列值只接受 1900 到 2100 年，原生日期儲存格及 1904 日期制同樣檢查；明確西元或民國文字日期的原範圍不變，Access 原生日期不套 Excel 限制。
年份在後的數字三段式日期不猜月日順序，帶時間也一樣拒絕。

`mapping.valueProfile` 只回有界的值分布：預設及畫面請求為 50 個最常見非空白值，action 可指定 1 到 100 個，另回 `blankCount`、`distinctCount` 與
`truncated`。可選的 `comparisonValues` 每次最多 100 個原始字串；有提供時另外回傳 `comparisonGroups`，
將顯示值和所附字串依後端既有的 `Trim` 和 `OrdinalIgnoreCase` 比對分組，組內仍保留原字串。
同時回傳 `comparisonKeys`，每項為 `{ value, key }`，保留每個原始字串及後端產生的比對鍵。前端用這個鍵連接不同請求的等價關係，
不能自行把代碼轉大寫；空白的鍵為 null，表示已核對為空白，不是缺少資料，也不等於字串 `"null"`。
每份回應最多包含當次有界顯示值與最多 100 個請求字串，不把完整來源清單傳給前端。
SQLite 和 DuckDB 在資料庫內先用相同的 .NET 比對鍵彙總全部來源，再取常見值與計算不同值數量，大小寫等價的值不分成兩組。
`checkSourceValues: true` 必須搭配 `comparisonValues`，不能與 `comparisonOnly: true` 同時使用；它另外核對完整來源，
回傳 `missingComparisonValues` 及 `sourceValueCheckStatus: "checked"`，不能拿前 50 個顯示值推定某個代碼不存在。
SQL Server 的值概況目前仍區分大小寫，也尚未實作完整來源的存在性核對；明確要求核對時，原摘要仍可讀取，
但 `missingComparisonValues` 回 null，狀態為 `unsupportedProvider`。畫面須寫尚未核對，不得當成缺值或空清單。
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

過帳狀態的接受值同樣只適用於當時指派的來源欄；取消或更換欄位時清除政策，再由使用者選擇接受值。
值摘要可分別讀取，失敗後可以重試；快取與配對錯誤都隨案件或匯入來源變更失效，不能沿用舊資料的回應。

確認配對的失敗明細保留 `sourceColumn`，人工判定另提供 `reasonCode`：`manual_blank` 指來源空白，
`manual_unlisted` 指非空白值尚未歸類，`manual_overlap` 指同一個值同時出現在兩側。前端依代碼定位設定，不分析中文錯誤句子。
GL 和 TB 都掃描完整來源來計算錯誤總數；每組只保留最多 10 個值、每值最多 10 個列號，原 `Errors` 樣本仍最多 50 列。
TB 成功回應的 `warnings` 和 GL 一樣，必填文字欄整欄空白時提醒，但不阻擋確認。
GL 的人工/自動分錄選「只列人工」或「只列自動」，而清單代碼在來源裡一筆都沒有時，`warnings` 另加一則提醒，
寫出代碼、來源欄、可能的後果與下一步；比對方式和投影相同，去掉頭尾空白、不分大小寫。逐值指定時不做這項檢查。

### 資料驗證

- `validate.run`

驗證摘要另含 `documentDateReuse: { documentNumberCount, entryCount }`，計算納入測試的分錄中，同一非空白傳票號碼出現在兩個以上總帳入帳日的號碼數及分錄筆數。
同月不同日也計入，空白號碼與未納入測試的分錄不計入；不改傳票比對鍵或既有驗證判定。兩個數字隨驗證摘要保存，重開案件不另掃母體。
第一步依有效摘要提醒，尚未驗證、缺欄或結果過期時顯示尚未確認，不把未知當零，也不阻擋後續操作。

`completenessTest.eligibility.isEligible` 表示目前驗證結果可供後續操作使用，不代表完整性通過。
`reason` 只用於缺少、損壞或過期結果；新增的可空字串 `warning` 承載審計差異和可繼續操作的提醒。
驗證摘要一定帶 `warning`，沒有提醒時是 null；缺這一欄的摘要當成無效結果，需要重新執行資料驗證。
回應時依原始檢查結果重新產生整個判定，不信任存檔中的衍生阻擋布林值。
預篩選、篩選及匯出共同使用後端判定；不得因差異筆數非零而拒絕。差異、狀態及明細原樣保留。

`completenessTest.partA` 等 action 回應欄位為既有資料形狀，不是使用者可見名稱。`partA.eligibleSource` 是確認 GL 欄位配對時
逐列算出的數字，`partA.effectiveTarget` 是驗證時從存下的分錄重新計算的數字，兩者都不是來源檔本身的合計。
畫面和錯誤分別說明「存下的分錄和確認配對時算出的是否一致」及「GL 與 TB 逐科目比對」，不顯示 Part A 或 Part B。`sourceQuality` 目前只有
`nullPostDate`，表示總帳入帳日空白且不能界定案件期間的來源列；日期在期間外不是這個集合。
驗證的 INF 樣本、來源品質有界樣本與摘要由同一交易取得並保存，摘要寫入失敗會回滾本次樣本與失效狀態變更。
後端不再把「存在 INF 樣本」視為已完成驗證的證據。

### 預篩選

- `prescreen.run`

`backdatedPosting` 與 `lowFrequencyPreparer` 和其他有選用來源的規則一樣，回傳 `status`、`naReason`、`count`。
未配對傳票日期時回溯測試無法執行；未配對傳票建立人員時，人員彙總、編製分錄較少的人員及非授權人員測試無法執行。
這些原因只影響相依規則，不阻擋其他程序；補回配對後重新驗證及預篩選即可。
進階篩選選用回溯、編製分錄較少的人員或非授權人員條件時，也依目前配對指出必要來源缺漏，保留條件供修正，不能把無來源當成零筆。
`rareAccounts.lowFrequencyAccountCount` 是完整分錄測試範圍內，出現 11 筆以下分錄的相異科目數，不是前 50 個科目摘要的列數。
原有 `rareAccounts.distinctAccountCount` 仍是全部相異科目數，`lowFrequencyAccount.count` 仍是命中的分錄筆數。舊結果缺少新欄位時，畫面說明尚未取得，不當成 0。
編製者彙總仍使用既有的去空白、不分大小寫規則分組；空白組的資料值不改，只有畫面與報告將它顯示為「（空白）」。實際人員代碼恰為「（空白）」時，仍是另一組。
`creatorSummary.creators` 最多 50 列；`creatorSummary.totalPreparerCount` 是查核期間的完整人數，空白人員組算一位，
和 `concentration.preparers.totalPreparerCount` 相同。這項不適用時為 null。畫面的「N 位人員」與預篩選報告的摘要列都用這個數字；
舊結果沒有這個欄位時，畫面請使用者重新執行預篩選，報告沿用清單列數。

### 進階篩選

- `filter.preview`
- `filter.commit`

2024 舊表 A–U 與五個填寫範例是前端的條件目錄，仍呼叫上述 action，使用既有 `groups` 和 `rules`。
來源版本 `je-form-2024-1210` 及儲存格位置記於 `filter-legacy.js`；送出並儲存的是展開後的條件與使用者參數，
不新增由後端解讀字母的通道，也不以 A–U 代號覆蓋 KCT 來源。目前篩選計算版本為 `filter-2026-10-06-v18`；
空白傳票號碼與非營業日的裁定見[已歸檔的回饋計畫](history/specs/2026-09-17-user-feedback-and-workflow-review-plan.md)第 7 批。

### 有界查詢

空值明細的 `query.nullRecordsPage` 保留類別、排序與游標契約；搜尋文字現在比對傳票號碼、科目編號或摘要，
因此缺傳票號碼的資料也能搜尋。各類結果分別排序及分頁，同筆缺兩欄時在各類明細中各出現一次。

- `query.dataPreview`（`dataset: "glEntries"` 的 `columns` 最後新增 `manualAuto`，對應值是 `manual`、`automatic` 或 null；
  既有八欄及順序不變。`glExcludedEntries` 與 `filter.preview` 不加此欄。）
- `query.completenessDiffPage`
- `query.docBalancePage`
- `query.nullRecordsPage`
- `query.sourceQualityPage`
- `query.filterHitsPage`
- `query.filterVoucherPage`
- `query.filterVoucherRowsPage`
- `query.prescreenPage`
- `query.infSamplePage`
- `query.tagMatrixScenarios`
- `query.tagMatrixVoucherPage`
- `query.tagMatrixRowPage`
- `query.accountMappingPage`（第四步科目分類清單，可依科目編號或名稱搜尋；有界分頁）
- `query.accountMappingDifferencePage`（`kind` 為 `mappingOnly` 或 `unmapped`，另收 `cursor`、`pageSize`；
  每頁預設 200 筆、上限 500 筆。回 `{ kind, rows: [{ accountCode, accountName }], nextCursor, totalCount }`。
  依科目編號升冪，`accountName` 可為 null。只有首頁回總筆數，續頁的 `totalCount` 為 null；空清單首頁為 0。
  游標不可跨清單種類使用。只在使用者列出清單時查詢，不在 `project.load` 時計算。）
- `query.accountMappingBlankPage`（第四步「分類留白 N 筆，視為 Others」的清單；`cursor`、`pageSize`，
  回 `rows[{ accountCode, accountName }]` 與 `nextCursor`，依科目編號升冪）

`query.filterHitsPage`、`query.tagMatrixVoucherPage` 與 `query.tagMatrixRowPage` 的游標是不可自行拆解的 token，
綁定案件、來源資料版本、情境版本、查詢種類及排序搜尋。來源或情境變更後，即使已重新計算完成，舊游標
也必須從第一頁重新載入，不能把兩代結果接起來。最後一個情境被刪除時，首次查詢可以回空集合，舊續頁
則回 `stale_result`。無法辨識的游標也要求回第一頁；資料庫 keyset 排序與 action 欄位形狀不變。
上游修改清除全部情境後，`query.filterHitsPage`、指定已存情境的傳票查詢、條件篩選報告與底稿匯出回 `stale_result`，
訊息說明目前沒有已儲存的篩選情境，請到「進階條件篩選」設定並儲存後再執行；標籤矩陣三個查詢的首頁照舊回空集合。

四個既有篩選結果查詢以持久化失效旗標判斷是否需要補算，零筆與搜尋無結果不再觸發寫入。只有實際
失效的首頁需要既有寫入鎖；一般讀取前後核對資料、情境及案件，避免切換期間回傳混合結果。

**排序與搜尋（2026-09-07 起）。** 明細表的分頁查詢都接受選填 `sort` 與 `search`：`sort` 是
`{ key, direction }`，`key` 是該查詢回應列的欄位名（白名單見下表），`direction` 是 `asc` 或 `desc`，
省略時依該查詢的穩定鍵升冪；`search` 是最多 200 個字的文字，不分大小寫比對傳票號碼（科目層的表
比對科目編號，空值明細另比對科目與摘要），省略或空白表示不過濾。排序後空值一律排在最後。換了排序或搜尋要從第一頁重新載入；
帶著舊游標換排序會回 `invalid_payload`，訊息說「請從第一頁重新載入」；不在白名單的 `key` 也回
`invalid_payload` 並列出允許的鍵。`query.filterHitsPage` 與 `query.infSamplePage` 的 `columns` 另帶
`sortable`，標示固定欄可以排序，攸關資料元素欄位不能。
`query.completenessDiffPage` 省略排序時，科目編號空白的列排在最前，試算表那一列在總帳那一列之前，其餘依科目編號；
驗證報告與底稿逐頁讀全科目表時也是這個順序，SQLite 與 DuckDB 相同。

| 查詢 | 搜尋比對 | 可排序的 `key` |
|:---|:---|:---|
| `query.completenessDiffPage` | 科目編號 | `accountCode`、`accountName`、`tbAmount`、`glAmount`、`diff`、`notInTb` |
| `query.docBalancePage` | 傳票號碼 | `documentNumber`、`debit`、`credit`、`diff` |
| `query.nullRecordsPage` | 傳票號碼、科目編號或摘要 | `documentNumber`、`accountCode`、`postDate`、`description` |
| `query.sourceQualityPage` | 傳票號碼 | `sourceRowNumber`、`documentNumber`、`accountCode`、`postDate`、`description` |
| `query.infSamplePage` | 傳票號碼 | `documentNumber`、`accountCode`、`accountName`、`debit`、`credit`、`postDate`、`approvalDate`、`createdBy`、`approvedBy`、`description` |
| `query.prescreenPage` | 傳票號碼 | `documentNumber`、`lineItem`、`postDate`、`accountCode`、`accountName`、`amount`、`drCr`、`documentDescription` |
| `query.filterHitsPage` | 傳票號碼 | `documentNumber`、`lineItem`、`postDate`、`accountCode`、`accountName`、`amount`、`drCr`、`description` |
| `query.tagMatrixVoucherPage` | 傳票號碼 | `documentNumber`、`postDate`、`createdBy`、`voucherTotal` |
| `query.tagMatrixRowPage` | 傳票號碼 | `documentNumber`、`lineItem`、`postDate`、`approvalDate`、`createdBy`、`approvedBy`、`accountCode`、`accountName`、`amount`、`description` |
| `query.filterVoucherPage` | 傳票號碼 | `documentNumber`、`postDate`、`hitRowCount`、`totalRowCount`、`voucherTotal` |

兩個 `query.filterVoucher*` 查詢以 `scenario` 草稿或 `scenarioPosition` 和 `scenarioRevision` 指定
已儲存情境，兩種方式擇一。只列出有命中分錄的傳票，沒有待判定。
`pageSize` 沿用預設 200、最多 500，回應包含 `rows`、`nextCursor`、
`queryRevision`、`scenarioRevision` 及 `conditionText`。傳票列提供 `documentNumber`、`postDate`、
`hitRowCount`、`totalRowCount` 與 `voucherTotal`（該傳票期間內全部分錄的借方總額顯示值），可用上表的
`sort` 與 `search`；展開分錄時另帶 `documentNumber` 及上次
回應的 `queryRevision`，分錄頁固定依分錄順序，不接受排序與搜尋。
明細提供日期、科目、金額、摘要、`isHit` 與 `matchDescription`。另有 `primaryConditions`、
`evidenceConditions` 和 `voucherConditions`，分別對應主要、佐證與傳票層條件的位置；位置由 `group` 和
`rule` 組成，皆從 1 起算。
回應另有欄位 metadata `columns`，明細的攸關資料元素欄位值在 `customValues`。展開必須帶 `queryRevision`。
游標綁定條件與資料版本，過期回 `stale_result`，審計員重新預覽即可。查詢不保存草稿，不改寫已存命中。

**條件目錄（2026-09-07 收斂後）。** 情境是 `{ name, rationale, groups[], editorOrigins? }`，每組
`{ join, matchScope, rules[] }`：`join` 為 `AND` 或 `OR`；`matchScope` 為 `row`（同一分錄）或
`sameVoucher`（同一傳票，只允許 AND，至少兩條，第一條是輸出錨點）。每條規則帶 `type` 與 `join`，
其餘欄位依型別：

| `type` | wire 欄位 | 允許值與說明 |
|:---|:---|:---|
| `fieldValue` | `field` 或 `fieldId` 擇一、`operator`、`value`、`values`、`from`、`to`、`includeBlank`、`amountBasis`、`drCr` | 文字：`equals`、`notEquals`、`contains`、`notContains`、`startsWith`、`notStartsWith`、`endsWith`、`notEndsWith`、`in`、`notIn`、`isBlank`、`isNotBlank`；日期：`on`、`notEquals`、`before`、`onOrBefore`、`after`、`onOrAfter`、`between`、`notBetween`、`in`、`notIn`、`dayOfMonthIn`、`dayOfMonthNotIn`、`isBlank`、`isNotBlank`；金額：`equals`、`notEquals`、`greaterThan`、`greaterThanOrEqual`、`lessThan`、`lessThanOrEqual`、`between`、`notBetween`、`in`、`notIn`、`isBlank`、`isNotBlank`。`contains` 的 `values` 可放多個關鍵字（任一命中），`notContains` 是它的否定（一個都不含）；`dayOfMonthIn`、`dayOfMonthNotIn` 的 `values` 是 1 到 31 的整數，重複自動合併；`includeBlank` 省略時一律 false（空白不列入）；金額 `amountBasis` 為 `absolute`（預設）或 `signed` |
| `entityFrequency` | `field`、`countUnit`、`countOperator`、`countFrom`、`countTo` | `field` 為 `accNum`、`createBy` 或 `approveBy`；單位為 `entries` 或 `vouchers`；比較為 `equals`、`lessThan`、`greaterThan`、`between`、`lessThanOrEqual` 或 `greaterThanOrEqual`。門檻為非負整數，`between` 含兩端。依分錄測試範圍計算，空白識別值不命中；傳票張數同號只算一張，不計空白號碼 |
| `accountSide` | `drCr`、`categoryMode`、`categoryIds[]` | `drCr` 為 `debit`、`credit` 或 `any`（不限借貸）；`categoryMode` 為 `is`（科目屬於）、`isNot`（科目不屬於）、`absent`（整張傳票的這一側都不屬於）；需要案件已匯入科目配對。分類留白的科目依 legacy 視為 `builtin.others`，不在配對檔的科目不屬於任何分類 |
| `specialAccountCategoryPair` | `pairMode`、`debitCategoryIds[]`、`creditCategoryIds[]` | `drAndCr`（借方是 A 且貸方是 B）、`drNotCr`（借方是 A 且整張傳票沒有 B 貸方）、`notDrCr`（貸方是 B 且整張傳票沒有 A 借方） |
| `accountPair` | `pairMode`、`debitCategoryIds[]`、`creditCategoryIds[]` | `exact`（舊情境讀回用，不再新建）、`debitAnchor`（借方是 A，看它的對方科目）、`creditAnchor`（貸方是 B，看它的對方科目） |
| `prescreen` | `prescreenKey` | 預篩選規則鍵 |
| `text`、`textSet`、`customKeywords` | `keywords`（逗號分隔）、`mode`、`values[]`、`normalization` | `mode` 為 `contains`、`exact`、`notContains`、`notExact`；`textSet` 舊情境讀回用 |
| `numRange`、`dateRange` | `field`、`from`、`to` | 金額以顯示值字串，日期為 ISO 字串；區間含兩端 |
| `drCrOnly`、`manualAuto` | `drCr`、`isManual` | `debit` 或 `credit`；布林 |
| `customTrailingZeros`、`customPreparerEntryCount`、`customAccountEntryCount`、`revenueDebitNearQuarterEnd` | `digits`、`maxEntries`、`windowDays` | 整數門檻，範圍由 Domain 驗證 |
| `revenueWithoutNormalCounterpart`、`manualRevenueEntry`、`trailingDigits`、`preparerEqualsApprover` | 無 | 固定規則 |
| `typed` | `fieldId`、`operator`、`value`、`values`、`from`、`to`、`amountBasis` | 2026-08-14 凍結的攸關資料元素欄位條件，契約不變 |

`fieldValue` 日期另支援 `isWeekend`、`isHoliday`、`isMakeupDay`、`isNonBusinessDay` 及各自的 `isNot…` 否定形式，
不帶值參數。週末依案件設定；假日、補班日依匯入清單；非營業日為週末或假日再排除補班日。
日期另支援 `monthStartDays`、`notMonthStartDays`、`monthEndDays`、`notMonthEndDays`，`value` 是
1 到 31 的整數字串，不同時帶清單或區間。範圍包含月初或月底當天，超過該月天數時涵蓋全月，
核心與額外日期欄位都可使用。否定不納入空白，除非 `includeBlank` 明示為 true。
所有 `fieldValue` 可另帶 `drCr: "debit" | "credit"`；省略時不限方向，其他拼法、空字串或非字串會回報無效條件。
借貸別使用既有標準化 `dr_cr`，
與欄位條件在同一筆分錄上判斷，包含空白條件和否定條件。在 `sameVoucher` 組內仍由第一條決定命中列，
後續每條各自在同一傳票尋找符合列；不把多條欄位條件自動綁成同一筆佐證分錄。
金額另支援 `endsWithDigits`、`notEndsWithDigits`，`value` 為逗號分隔尾數，最多 100 組、每組最多 12 位。
比較整數部分的絕對值，不看正負或小數，保留前導零的位數；核心欄位與攸關資料元素欄位共用相同規則。

情境根層不再接受 `exclusions`；舊情境帶這個欄位時回 `invalid_scenario`，訊息說改用條件的否定模式重新
儲存。`filter.preview` 回 `count`、`voucherCount` 與 `previewRows`，沒有待判定計數。

`fieldValue` 的 `isBlank`、`isNotBlank` 若選到沒有來源配對的核心欄位，回 `invalid_scenario` 與條件列
`details`，引導回「欄位配對」指派或改選欄位；已配對欄位的空白資料仍可正常篩選。
`docDate` 採 `sameAsPostDate` 時是有來源的衍生欄位，不因未直接配對 `docDate` 而拒絕。
這項修正當時的規則版本為 `filter-2026-09-07-v12`；文字讀回仍用「且」「或」，不改變條件的左至右合併順序。
2026-09-18 新增欄位內的借貸方向限制及每月月初、月底天數後，篩選版本為 `filter-2026-09-18-v15`。
加入條件括號、傳票量詞與分類階層時的篩選版本為 `filter-2026-09-18-v16`。
2026-10-04 落實空白號碼不屬於任何傳票的裁定，篩選版本推進為 `filter-2026-10-04-v17`，預篩選版本為 `prescreen-2026-10-04-v9`。
2026-10-06 KCT 條件 A 加入查核期末視窗，現行篩選版本為 `filter-2026-10-06-v18`。舊命中需重新計算，預篩選版本維持不變。
日期運算值沿用 GL 匯入的 `DateNormalizer` 與案件 `DateParseOptions`，從預覽、儲存到重開及匯出都使用同一選項。
摘要關鍵字、尾數、人員代號與科目編號的文字輸入接受換行、半形與全形逗號、頓號及 Tab；已送成 `values` 陣列的文字值不再拆逗號。
「包含任一文字」依實際送出的清單計算 100 個上限，修剪前後空白後仍區分大小寫，不拿不分大小寫的摘要筆數代替。
條件驗證訊息以「第 N 組第 M 條」指出位置；`FilterScenarioErrorDetails` 同時接受這個寫法與舊訊息，保留逐列錯誤定位。

規則新增 `type: "group"` 和 `type: "voucher"`，兩者以 `rules` 保存子規則。`group` 的欄位條件綁定同一筆分錄；
各層的 `join` 仍依原有順序左折疊，最多巢狀八層。`voucher` 必填 `side: "all" | "debit" | "credit"`
及 `quantifier: "any" | "all" | "none"`。子條件先在每筆分錄上計算，`all` 額外要求範圍內至少有一筆。
`voucher` 內可有 `group`，不可再嵌另一個 `voucher`。沒有該側分錄時只有 `none` 成立。
空白傳票號碼對三種量詞與 `accountSide` 的 absent 模式一律不成立；KCT C 和 unexpectedAccountPair 也不命中空白號碼的收入貸方。
舊 `row`、`sameVoucher` 和省略新欄位的情境維持原語意。

`accountSide`、`accountPair` 和 `specialAccountCategoryPair` 的 `categorySelection` 可選 `role`、`node`、`subtree`，
依序表示相同分類用途、僅分類本身、分類及所有下層。借貸組合的兩側使用同一選取方式；兩側要各自選取時可組合單側條件。
省略時與舊版一樣使用 `role`；`categoryIds` 仍保存穩定分類 ID，名稱只用於呈現。
`accountTaxonomy.save.categories[].parentCategoryId` 可為既有分類 ID、同一次請求的暫存 ID 或 null；舊呼叫端省略此欄時保留原上層。
新分類可在 `categoryId` 傳入唯一的 `draft-N`，N 是以 1 到 9 開頭的正整數，暫存 ID 最長 64 字元；子列可引用同一次新建的父列，不限列出順序。
後端先配置全部正式 ID，再解析上層關係，回應只包含正式 ID。省略 `categoryId` 的舊呼叫仍可新增，但無法供同次其他列引用。
分類 ID 重複、未知上層與循環仍拒絕，失敗不留下部分分類；新增分類的用途依請求保存，不由後端從上層推導。
同一回應及 `project.load.taxonomy` 都帶回上層 ID。新增分類仍由後端產生正式 ID；既有上層與同次新增的上層都可以選取。
分類角色不因移動階層而改變，階層變更沿用既有分類修訂與結果失效契約。
儲存分類設定會清除全部已存篩選情境與命中，審計員要重新設定情境；未指定借貸方向的舊條件維持原結果。
`accountTaxonomy.save` 要刪除的自訂分類若仍有科目配對使用，回 `taxonomy_category_in_use`，整次儲存不生效，
訊息請審計員先把這些科目改到其他分類。分類只被已存篩選情境使用時不再擋下刪除，因為這次儲存會一併清掉情境。

`filter.preview` 和草稿傳票查詢只檢查條件有效性，不要求名稱或動機；`filter.commit` 才要求自訂情境的
名稱與動機，KCT 沿用原有例外。驗證失敗回 `invalid_scenario`，`error.details` 帶每一條的位置與原因。

情境可帶 `editorOrigins: { version: 1, legacyKctSource?: boolean, nameIsAutomatic?: boolean, rationaleIsAutomatic?: boolean, groups: [{ letters: [...] }] }`。
每個 group 和 letters 的長度及位置對應原條件；letters 為 A–J 字母或 null。它只還原 KCT 卡片，
不參與審計判斷。KCT I 是組內的一條 `type: "group"` 條件括號，字母記在括號這一條上。
2026-10-02 以前儲存的情境，group 可能另帶 `presetGroup` 布林值，記錄當時獨立的非營業日組；
後端仍接受並檢查它是布林值，前端不再產生也不讀取，舊情境不轉換。`legacyKctSource` 記錄舊 KCT 情境中仍無法還原來源的部分；省略表示 false。
儲存回應與重新載入保留這份資訊。舊情境沒有資訊時不猜卡片，也不改寫原條件、來源或結果。
兩個 `IsAutomatic` 欄位分別記錄名稱及動機是否自動產生，只供編輯器恢復命名方式，不參與篩選判定。
省略時保留原文字，不從字母或名稱相似度猜測；提供時必須是布林值。失敗的儲存不替換既有定義。

### 匯出

- `export.validationArtifacts`
- `export.prescreenReport`
- `export.criteriaSelectionReport`
- `export.workpaperStream`
- `export.calendarTemplates`
- `export.accountMappingTemplate`

`export.validationArtifacts` 只發布 Validation Report 與 INF Report 兩份，同名檔覆蓋。
`export.workpaperStream` 仍檢查 `validationRunId`、`scenarioRevision` 及選取的情境位置，並在寫檔前重新計算。
只重算 `scenarioPositions` 所選情境，未選情境不參與本次來源欄位驗證，也不刪除其命中。
回應 `filterResultsCurrent` 表示全案結果是否目前有效；false 不代表本次底稿失敗，前端不得因此清除全案失效旗標。
新底稿的 `sourceRef.filterDataRevision` 保存既有篩選資料版本，搭配驗證 run、情境版本及位置判斷來源有效性。
舊產物沒有此欄位時沿用原本全案失效判定，不推測舊底稿來源。
匯出不要求已有條件篩選報告，也不因那份檔案過期、被刪除或被修改而拒絕；其他來源版本及正式底稿不覆寫的規則不變。
`export.workpaperStream` 每次寫新的版本檔 `<案件前綴>_<yyyyMMdd-yyyyMMdd>_WorkingPaper_<yyyyMMdd-HHmmss>.xlsx`，不覆蓋、不刪
舊版。`export.accountMappingTemplate` 產生的是工作檔，不是報告：固定檔名 `<案件前綴>_AccountMapping.xlsx`
寫進案件資料夾、不進 `reportArtifacts`。Payload 的 `onlyIfMissing` 預設為 false，手動產生會覆寫；
true 表示只在檔案不存在時建立，最後發布時也不覆蓋稍後出現的檔案。非布林值會回 `invalid_payload`。
`runId` 改為選填，接受舊呼叫，但不再當成產生工作檔的前置。回應中的 `validationRunId` 只記錄當下已有的可用驗證結果，沒有則為 null。
回應為 `{ ok, filePath, fileName, rowCount, validationRunId, disposition }`；`disposition` 為 `created`
或 `kept`。保留原檔時不讀取其中內容，`rowCount` 為 null。審計員用 Excel 填好 `Standardized Account Name*` 欄存回原檔，再由
`import.accountMapping.fromFile` 讀同一路徑。
五種報告的檔名均加入案件查核起訖日 `yyyyMMdd-yyyyMMdd`；同期間重匯維持原覆寫或版本規則。
報告項目保留 `fullPath` 供本機定位用途，畫面只顯示檔名、建立時間與大小；此欄位不寫入索引，重開或搬移案件後重新解析。
`fileName` 仍只有相對檔名。
四個報告匯出 action 的回應另提供完整 `reportArtifacts`，供前端同步容量整理後的清單；原有 `artifact`
或 `artifacts` 仍只表示這次發布的報告。發布成功後，清單刷新是可省略的呈現工作，不是另一個發布條件。
可恢復的清單讀取失敗或 3 秒內未完成時，仍回 `ok: true` 及本次產物，`reportArtifacts: null`，另回
`reportArtifactWarning`。前端保留原清單並加入本次產物，在旁邊提示可開啟案件資料夾，無須重新產生。
清單之後讀取成功就清除該暫時提示；不得將 null 當成空清單，也不把程式錯誤偽裝成成功。
清單即將超過 4,096 筆時，只移除已確認檔案不存在的底稿紀錄。
產生內容後、正式發布前會再次確認，途中被放回或無法確認的檔案仍保留。若仍超限，本次不發布新檔，
使用者可自行整理不需要的舊底稿後重試。
2026-09-02 以前的 `report.cleanupPreview`／`report.cleanupConfirm` 已移除。

`export.calendarTemplates` 接受空物件，須有作用中案件，回傳 `{ files: [{ fileName, disposition }] }`。
兩個固定檔名是 `Holiday2025TW.xlsx` 與 `MakeUpDay2025TW.xlsx`；`disposition` 為 `created` 或 `kept`。
只在案件資料夾缺檔時複製，已有檔案保留，不匯入、不建立報告紀錄，也不自動開啟資料夾。
中途失敗或取消時，已完成的檔案保留，重試會略過它，不留下未完成的暫存檔。

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
- `dev.db.reconcile`
- `dev.log.exportFile`

`host.openFolder` 的既有參數為 `{ target: "projectFolder" }` 或 `{ artifactId }`，兩者只能擇一，不接受
任意路徑。第四、第五與第六步的報告入口共用 `artifactId`，由後端在目前案件解析檔案並交給檔案總管選取。過期的
Working Paper 仍可定位；檔案已移動或刪除時回傳 `artifact_not_found`，不開啟其他位置。

`support.log.export` 在 Release 與 Debug 都可用。輸入 `{ projectId, correlationId? }`；只從有界記憶體
取出該案件的 allowlist 事件，若有 correlation 則再縮成該次操作。內容在進入 buffer 前就已去識別：
不保存 SQL、參數值、檔名、絕對路徑、案件名稱或原始 exception message。輸出固定為案件目錄內的
`JET-support-*.txt`，每行一個 JSON；案件目錄不存在或是 reparse point 時直接失敗，不改寫到其他位置。
診斷匯出不要求案件已載入，也不解析 `project.json`；設定損壞或遺失時仍可將該次載入錯誤匯出到既有案件目錄。
兩種日誌匯出都使用請求的案件 ID 定位，不使用設定檔內的 ID 改變目的地；非法 ID 或不存在的目錄回 `project_not_found`。

匯入失敗的紀錄包含來源序號、處理階段、最後成功列、可取得的失敗列欄、解析設定、元件版本與交易回復結果。
`import_progress_failed` 表示進度通知失敗，不再歸為讀檔錯誤。取消保留原取消語意。
原始例外鏈僅輸出型別、系統錯誤碼與方法名稱，不輸出訊息或檔案位置；未知列欄明示為未知。
記憶體保留最近最多 32 次失敗摘要，總輸出仍受容量限制。`support.snapshot` 的 `schemaVersion` 為 2，
`processEventsOmitted` 表示本程序已因容量限制省略的事件數，不能視為完整歷史。

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
| 報告產物種類 | `Domain/Contracts/ReportArtifactContracts.cs` |

過往 1,000 行的逐 action 說明保存在
[`history/superseded/action-contract-manifest-2026-08.md`](history/superseded/action-contract-manifest-2026-08.md)。
它能協助追查舊設計，但欄位有疑義時仍要回到現行處理器與測試核對。
