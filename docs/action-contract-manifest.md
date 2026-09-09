# JET action 介面契約

更新日期：2026-09-05

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
    "details": [{ "group": 1, "rule": 2, "message": "<one positioned error>" }]
  },
  "correlationId": "<server-generated id>"
}
```

`error.field` 只能由後端在能明確指出欄位時提供。前端不得從錯誤文字猜欄位；目前建案名稱錯誤會
使用 `caseName`。`error.details` 只在 `invalid_scenario` 這類能歸屬到條件列的錯誤出現：每一項帶第幾組、
第幾條（從 1 起算，無法歸屬時為 null）與那一條的原因；`message` 仍是全部原因合併的整段文字。步驟五用
`details` 把對應的條件列標紅並就地說原因，沒有 `details` 的錯誤維持整段訊息。`correlationId` 串起同一次 action 的開始、結束或錯誤事件；picker 可用它輸出該次
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
- Debug／AgentGuiTest 專用 action 不得出現在 Release 的使用者流程。

## 目前支援的 action

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
- `project.load`
- `project.delete`
- `project.saveProgress`
- `project.heartbeat`
- `project.releaseLock`
- `project.loadDemo`（開發／GUI 測試專用）

`project.load` 不會因為報告檔而拒絕載入。報告檔在 JET 之外被改寫、改名或刪除時，回應的 `reportArtifacts`
每筆多一個 `fileState`（`asPublished`、`modifiedOutside`、`missing`）讓前端標示，重新匯出即可。舊版留下的
輸出紀錄檔 `.report-artifacts.mutation-v1.json` 會在載入時直接刪除，並記一筆 `artifact.journal.discarded`
支援日誌事件。`project.delete` 只取得案件外的 artifact lease，刪案不受報告檔狀態影響。

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
- `import.inspectFile`
- `import.previewFile`
- `import.holiday`
- `import.makeupDay`
- `import.holiday.fromFile`
- `import.makeupDay.fromFile`
- `calendar.setNonWorkingDays`

### 欄位配對與科目分類

- `accountTaxonomy.save`
- `mapping.autoSuggest`
- `mapping.valueProfile`
- `mapping.restoreDraft`
- `mapping.commit.gl`
- `mapping.commit.tb`

### 資料驗證

- `validate.run`

### 預篩選

- `prescreen.run`

### 進階篩選

- `filter.preview`
- `filter.commit`

### 有界查詢

- `query.dataPreview`
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
- `query.accountMappingBlankPage`（第四步「分類留白 N 筆，視為 Others」的清單；`cursor`、`pageSize`，
  回 `rows[{ accountCode, accountName }]` 與 `nextCursor`，依科目編號升冪）

**排序與搜尋（2026-09-07 起）。** 明細表的分頁查詢都接受選填 `sort` 與 `search`：`sort` 是
`{ key, direction }`，`key` 是該查詢回應列的欄位名（白名單見下表），`direction` 是 `asc` 或 `desc`，
省略時依該查詢的穩定鍵升冪；`search` 是最多 200 個字的文字，不分大小寫比對傳票號碼（科目層的表
比對科目編號），省略或空白表示不過濾。排序後空值一律排在最後。換了排序或搜尋要從第一頁重新載入；
帶著舊游標換排序會回 `invalid_payload`，訊息說「請從第一頁重新載入」；不在白名單的 `key` 也回
`invalid_payload` 並列出允許的鍵。`query.filterHitsPage` 與 `query.infSamplePage` 的 `columns` 另帶
`sortable`，標示固定欄可以排序，額外欄位不能。

| 查詢 | 搜尋比對 | 可排序的 `key` |
|:---|:---|:---|
| `query.completenessDiffPage` | 科目編號 | `accountCode`、`accountName`、`tbAmount`、`glAmount`、`diff`、`notInTb` |
| `query.docBalancePage` | 傳票號碼 | `documentNumber`、`debit`、`credit`、`diff` |
| `query.nullRecordsPage` | 傳票號碼 | `documentNumber`、`accountCode`、`postDate`、`description` |
| `query.sourceQualityPage` | 傳票號碼 | `sourceRowNumber`、`documentNumber`、`accountCode`、`postDate`、`description` |
| `query.infSamplePage` | 傳票號碼 | `documentNumber`、`accountCode`、`accountName`、`debit`、`credit`、`postDate`、`approvalDate`、`createdBy`、`approvedBy`、`description` |
| `query.prescreenPage` | 傳票號碼 | `documentNumber`、`lineItem`、`postDate`、`accountCode`、`accountName`、`amount`、`drCr`、`documentDescription` |
| `query.filterHitsPage` | 傳票號碼 | `documentNumber`、`lineItem`、`postDate`、`accountCode`、`accountName`、`amount`、`drCr`、`description` |
| `query.tagMatrixVoucherPage` | 傳票號碼 | `documentNumber`、`postDate`、`createdBy`、`voucherTotal` |
| `query.tagMatrixRowPage` | 傳票號碼 | `documentNumber`、`lineItem`、`postDate`、`approvalDate`、`createdBy`、`approvedBy`、`accountCode`、`accountName`、`amount`、`description` |
| `query.filterVoucherPage` | 傳票號碼 | `documentNumber`、`postDate`、`hitRowCount`、`totalRowCount`、`voucherTotal` |

兩個 `query.filterVoucher*` 查詢以 `scenario` 草稿或 `scenarioPosition` 和 `scenarioRevision` 指定
已保存情境，兩種方式擇一。只列出有命中分錄的傳票，沒有待判定。
`pageSize` 沿用預設 200、最多 500，回應包含 `rows`、`nextCursor`、
`queryRevision`、`scenarioRevision` 及 `conditionText`。傳票列提供 `documentNumber`、`postDate`、
`hitRowCount`、`totalRowCount` 與 `voucherTotal`（該傳票期間內全部分錄的借方總額顯示值），可用上表的
`sort` 與 `search`；展開分錄時另帶 `documentNumber` 及上次
回應的 `queryRevision`，分錄頁固定依分錄順序，不接受排序與搜尋。
明細提供日期、科目、金額、摘要、`isHit` 與 `matchDescription`。另有 `primaryConditions`、
`evidenceConditions` 和 `voucherConditions`，分別對應主要、佐證與傳票層條件的位置；位置由 `group` 和
`rule` 組成，皆從 1 起算。
回應另有欄位 metadata `columns`，明細的額外欄位值在 `customValues`。展開必須帶 `queryRevision`。
游標綁定條件與資料版本，過期回 `stale_result`，審計員重新預覽即可。查詢不保存草稿，不改寫已存命中。

**條件目錄（2026-09-07 收斂後）。** 情境是 `{ name, rationale, groups[], editorOrigins? }`，每組
`{ join, matchScope, rules[] }`：`join` 為 `AND` 或 `OR`；`matchScope` 為 `row`（同一分錄）或
`sameVoucher`（同一傳票，只允許 AND，至少兩條，第一條是輸出錨點）。每條規則帶 `type` 與 `join`，
其餘欄位依型別：

| `type` | wire 欄位 | 允許值與說明 |
|:---|:---|:---|
| `fieldValue` | `field` 或 `fieldId` 擇一、`operator`、`value`、`values`、`from`、`to`、`includeBlank`、`amountBasis` | 文字：`equals`、`notEquals`、`contains`、`notContains`、`startsWith`、`endsWith`、`in`、`notIn`、`isBlank`、`isNotBlank`；日期：`on`、`notEquals`、`before`、`onOrBefore`、`after`、`onOrAfter`、`between`、`notBetween`、`in`、`notIn`、`dayOfMonthIn`、`dayOfMonthNotIn`、`isBlank`、`isNotBlank`；金額：`equals`、`notEquals`、`greaterThan`、`greaterThanOrEqual`、`lessThan`、`lessThanOrEqual`、`between`、`notBetween`、`in`、`notIn`、`isBlank`、`isNotBlank`。`contains` 的 `values` 可放多個關鍵字（任一命中），`notContains` 是它的否定（一個都不含）；`dayOfMonthIn`、`dayOfMonthNotIn` 的 `values` 是 1 到 31 的整數，重複自動合併；`includeBlank` 省略時一律 false（空白不列入）；金額 `amountBasis` 為 `absolute`（預設）或 `signed` |
| `accountSide` | `drCr`、`categoryMode`、`categoryIds[]` | `drCr` 為 `debit` 或 `credit`；`categoryMode` 為 `is`（科目屬於）、`isNot`（科目不屬於）、`absent`（整張傳票的這一側都不屬於）；需要案件已匯入科目配對。分類留白的科目依 legacy 視為 `builtin.others`，不在配對檔的科目不屬於任何分類 |
| `specialAccountCategoryPair` | `pairMode`、`debitCategoryIds[]`、`creditCategoryIds[]` | `drAndCr`（借方是 A 且貸方是 B）、`drNotCr`（借方是 A 且整張傳票沒有 B 貸方）、`notDrCr`（貸方是 B 且整張傳票沒有 A 借方） |
| `accountPair` | `pairMode`、`debitCategoryIds[]` 或 `debitCategory`、`creditCategoryIds[]` 或 `creditCategory` | `exact`（舊情境讀回用，不再新建）、`debitAnchor`（借方是 A，看它的對方科目）、`creditAnchor`（貸方是 B，看它的對方科目） |
| `prescreen` | `prescreenKey` | 預篩選規則鍵 |
| `text`、`textSet`、`customKeywords` | `keywords`（逗號分隔）、`mode`、`values[]`、`normalization` | `mode` 為 `contains`、`exact`、`notContains`、`notExact`；`textSet` 舊情境讀回用 |
| `numRange`、`dateRange` | `field`、`from`、`to` | 金額以顯示值字串，日期為 ISO 字串；區間含兩端 |
| `drCrOnly`、`manualAuto` | `drCr`、`isManual` | `debit` 或 `credit`；布林 |
| `customTrailingZeros`、`customPreparerEntryCount`、`customAccountEntryCount`、`revenueDebitNearQuarterEnd` | `digits`、`maxEntries`、`windowDays` | 整數門檻，範圍由 Domain 驗證 |
| `revenueWithoutNormalCounterpart`、`manualRevenueEntry`、`trailingDigits`、`preparerEqualsApprover` | 無 | 固定規則 |
| `typed` | `fieldId`、`operator`、`value`、`values`、`from`、`to`、`amountBasis` | 2026-08-14 凍結的額外欄位條件，契約不變 |

情境根層不再接受 `exclusions`；舊情境帶這個欄位時回 `invalid_scenario`，訊息說改用條件的否定模式重新
保存。`filter.preview` 回 `count`、`voucherCount` 與 `previewRows`，沒有待判定計數。

`fieldValue` 的 `isBlank`、`isNotBlank` 若選到沒有來源配對的核心欄位，回 `invalid_scenario` 與條件列
`details`，引導回「欄位配對」指派或改選欄位；已配對欄位的空白資料仍可正常篩選。
`docDate` 採 `sameAsPostDate` 時是有來源的衍生欄位，不因未直接配對 `docDate` 而拒絕。
這項修正當時的規則版本為 `filter-2026-09-07-v12`；文字讀回仍用「且」「或」，不改變條件的左至右合併順序。
2026-09-08 修正 SQLite 的 Unicode 大小寫比對後，目前版本為 `filter-2026-09-08-v13`，
共用文字述詞的預篩選為 `prescreen-2026-09-08-v7`；舊結果需重新產生，action 與 payload 形狀不變。

`filter.preview` 和草稿傳票查詢只檢查條件有效性，不要求名稱或動機；`filter.commit` 才要求自訂情境的
名稱與動機，KCT 沿用原有例外。驗證失敗回 `invalid_scenario`，`error.details` 帶每一條的位置與原因。

情境可帶 `editorOrigins: { version: 1, legacyKctSource?: boolean, groups: [{ presetGroup?: boolean, letters: [...] }] }`。
每個 group 和 letters 的長度及位置對應原條件；letters 為 A–J 字母或 null。它只還原 KCT 卡片與預設群組，
不參與審計判斷。`legacyKctSource` 記錄舊 KCT 情境中仍無法還原來源的部分；省略表示 false。
保存回應與重新載入保留這份資訊。舊情境沒有資訊時不猜卡片，也不改寫原條件、來源或結果。

### 匯出

- `export.validationArtifacts`
- `export.prescreenReport`
- `export.criteriaSelectionReport`
- `export.workpaperStream`
- `export.accountMappingTemplate`

`export.validationArtifacts` 只發布 Validation Report 與 INF Report 兩份，同名檔覆蓋。
`export.workpaperStream` 每次寫新的版本檔 `<案件前綴>_WorkingPaper_<yyyyMMdd-HHmmss>.xlsx`，不覆蓋、不刪
舊版。`export.accountMappingTemplate` 產生的是工作檔，不是報告：固定檔名 `<案件前綴>_AccountMapping.xlsx`
寫進案件資料夾、不進 `reportArtifacts`。Payload 的 `onlyIfMissing` 預設為 false，手動產生會覆寫；
true 表示只在檔案不存在時建立，最後發布時也不覆蓋稍後出現的檔案。非布林值會回 `invalid_payload`。
回應為 `{ ok, filePath, fileName, rowCount, validationRunId, disposition }`；`disposition` 為 `created`
或 `kept`。保留原檔時不讀取其中內容，`rowCount` 為 null。審計員用 Excel 填完 C 欄存回原檔，再由
`import.accountMapping.fromFile` 讀同一路徑。
四個報告匯出 action 的回應另提供完整 `reportArtifacts`，供前端同步容量整理後的清單；原有 `artifact`
或 `artifacts` 仍只表示這次發布的報告。清單即將超過 4,096 筆時，只移除已確認檔案不存在的底稿紀錄。
產生內容後、正式發布前會再次確認，途中被放回或無法確認的檔案仍保留。若仍超限，本次不發布新檔，
使用者可自行整理不需要的舊底稿後重試。
2026-09-02 以前的 `report.cleanupPreview`／`report.cleanupConfirm` 已移除。

### 訊息、原生視窗與開發工具

- `log.append`
- `log.recent`
- `support.log.export`
- `host.selectFile`
- `host.selectFiles`
- `host.selectSavePath`
- `host.openFolder`
- `host.exitApp`
- `dev.db.overview`
- `dev.db.tableData`
- `dev.db.reconcile`
- `dev.log.export`
- `dev.log.exportFile`

`host.openFolder` 的既有參數為 `{ target: "projectFolder" }` 或 `{ artifactId }`，兩者只能擇一，不接受
任意路徑。第六步的版本入口使用 `artifactId`，由後端在目前案件解析檔案並交給檔案總管選取。過期的
Working Paper 仍可定位；檔案已移動或刪除時回傳 `artifact_not_found`，不開啟其他位置。

`support.log.export` 在 Release 與 Debug 都可用。輸入 `{ projectId, correlationId? }`；只從有界記憶體
取出該案件的 allowlist 事件，若有 correlation 則再縮成該次操作。內容在進入 buffer 前就已去識別：
不保存 SQL、參數值、檔名、絕對路徑、案件名稱或原始 exception message。輸出固定為案件目錄內的
`JET-support-*.txt`，每行一個 JSON；案件目錄不存在或是 reparse point 時直接失敗，不改寫到其他位置。

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
