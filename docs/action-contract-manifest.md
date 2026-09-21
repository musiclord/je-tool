# JET action 介面契約

更新日期：2026-09-18

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

正式前端建立案件時，`caseName`、`periodStart` 與 `periodEnd` 必填，`projectCode`、`entityName`、
`lastPeriodStart` 選填。`databaseProvider` 建立後不能更改。操作人員不由表單提供；
`ProjectCreateHandler` 直接使用目前 `CurrentPrincipal.ShortName`。舊版呼叫者即使仍送 `operatorId`，後端也不採用。
案件編號或客戶名稱留空時，`project.json` 仍保留空字串欄位，讓既有案件、清單與報告使用同一份資料形狀。

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
- `import.authorizedPreparer.clear`
- `import.inspectFile`
- `import.previewFile`
- `import.holiday`
- `import.makeupDay`
- `import.holiday.fromFile`
- `import.makeupDay.fromFile`
- `calendar.setNonWorkingDays`

GL 與 TB 的主資料匯入支援 `.xlsx`、`.xlsm`、`.xls`、`.csv`、`.txt`、`.mdb` 與 `.accdb`。
`.xlsm` 和 `.xlsx` 共用唯讀 Open XML 讀取器，`.xls` 使用逐列 BIFF 讀取器，都不啟動 Excel 或執行巨集。
Access 透過公司既有 Office 資料介面唯讀讀取一般資料表；`sheetName` 指選定資料表。`.xlsb` 不支援。
一個 GL 或 TB 資料集可以追加多個欄位相同的檔案或工作表；這只會合併列，不會把兩份 TB 自動解讀成期初與期末。

授權清單匯入可傳入 `sheetName` 與 `sourceColumn`，後者須精確對應來源標頭；新版畫面要求審計員選取與 GL
編製人員相同的識別欄。省略欄位時仍支援舊呼叫方式。回應另含 `sourceColumn`、`sourceRowCount`、
`blankRowCount`、`duplicateRowCount`，`rowCount` 是去除空白及重複後的有效識別值數量。
`project.load` 的授權清單狀態保留 `rowCount` 與可空的 `sourceColumn`，不保存私人原始路徑。
`import.authorizedPreparer.clear` 無必要 payload 欄位，回應 `{ cleared: true }`；同一交易移除清單及
預篩選與篩選結果，保留 GL、TB、驗證與情境定義。重試及重開後仍可重新匯入。

### 欄位配對與科目分類

- `accountTaxonomy.save`
- `mapping.valueProfile`
- `mapping.restoreDraft`
- `mapping.commit.gl`
- `mapping.commit.tb`

2026-09-17 移除 `mapping.autoSuggest` 的 action、前端呼叫與按鈕。GL 與 TB 欄位由審計員手動選取；
已保存配對及 `mapping.restoreDraft` 的明確還原操作保留。

GL 的 `postDate` 在畫面顯示為「總帳日期」，用來界定案件期間；`voucherDate` 顯示為「傳票日期」，是
回溯過帳判斷使用的選填日期。action 欄位名稱、資料庫欄位與既有報告正準名不變。

`mapping.valueProfile` 只回有界的值分布：預設及畫面請求為 50 個最常見非空白值，action 可指定 1 到 100 個，另回 `blankCount`、`distinctCount` 與
`truncated`。`manualAutoPolicy` 的 `manualValues` 與 `automaticValues` 保留原格式。選填的
`unlistedValueKind` 可為 `reject`、`manual` 或 `automatic`，指定非空白未列值的判定；省略時沿用逐值指定。
單側清單須至少提供一個代碼，另一側明確指定為補集。`blankValueKind` 可為 `reject`、`manual`、`automatic`
或 `unclassified`；最後一項保留空白而不判定人工或自動，仍可參與其他測試。省略時維持舊設定的空白處理。
兩項設定會保存至案件及報告配對資訊，還原草稿時保留。前端在 GL 配對區直接顯示後端原因；取消或更換來源欄時，
清除上一欄的代碼政策。RDE 全選只建立草稿；來源欄、型別、名稱、後端產生並沿用的欄位識別碼，以及核心欄位
不可重用等規則，仍由後端驗證。

過帳狀態的接受值同樣只適用於當時指派的來源欄；取消或更換欄位時清除政策，再由使用者選擇接受值。
值摘要可分別讀取，失敗後可以重試；快取與配對錯誤都隨案件或匯入來源變更失效，不能沿用舊資料的回應。

### 資料驗證

- `validate.run`

`completenessTest.eligibility.isEligible` 表示目前驗證結果可供後續操作使用，不代表完整性通過。
`reason` 只用於缺少、損壞或過期結果；新增的可空字串 `warning` 承載審計差異和可繼續操作的提醒。
舊摘要可沒有 `warning`，載入時依原始檢查結果重新產生，不信任舊的衍生阻擋布林值。
預篩選、篩選及匯出共同使用後端判定；不得因差異筆數非零而拒絕。差異、狀態及明細原樣保留。

`completenessTest.partA` 等 action 回應欄位為既有資料形狀，不是使用者可見名稱。畫面和錯誤分別說明「匯入前後的
控制總數核對」及「GL 與 TB 逐科目比對」，不顯示 Part A 或 Part B。`sourceQuality` 目前只有
`nullPostDate`，表示總帳日期空白且不能界定案件期間的來源列；日期在期間外不是這個集合。

### 預篩選

- `prescreen.run`

`backdatedPosting` 與 `lowFrequencyPreparer` 和其他有選用來源的規則一樣，回傳 `status`、`naReason`、`count`。
未配對傳票日期時回溯測試無法執行；未配對傳票建立人員時，人員彙總、低頻及非授權人員測試無法執行。
這些原因只影響相依規則，不阻擋其他程序；補回配對後重新驗證及預篩選即可。
進階篩選選用回溯、低頻人員或非授權人員條件時，也依目前配對指出必要來源缺漏，保留條件供修正，不能把無來源當成零筆。

### 進階篩選

- `filter.preview`
- `filter.commit`

2024 舊表 A–U 與五個填寫範例是前端的條件目錄，仍呼叫上述 action，使用既有 `groups` 和 `rules`。
來源版本 `je-form-2024-1210` 及儲存格位置記於 `filter-legacy.js`；送出並保存的是展開後的條件與使用者參數，
不新增由後端解讀字母的通道，也不以 A–U 代號覆蓋 KCT 來源。篩選計算版本維持 v16，沒有更改既有命中語意。

### 有界查詢

空值明細的 `query.nullRecordsPage` 保留類別、排序與游標契約；搜尋文字現在比對傳票號碼、科目編號或摘要，
因此缺傳票號碼的資料也能搜尋。各類結果分別排序及分頁，同筆缺兩欄時在各類明細中各出現一次。

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
比對科目編號，空值明細另比對科目與摘要），省略或空白表示不過濾。排序後空值一律排在最後。換了排序或搜尋要從第一頁重新載入；
帶著舊游標換排序會回 `invalid_payload`，訊息說「請從第一頁重新載入」；不在白名單的 `key` 也回
`invalid_payload` 並列出允許的鍵。`query.filterHitsPage` 與 `query.infSamplePage` 的 `columns` 另帶
`sortable`，標示固定欄可以排序，額外欄位不能。

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
| `fieldValue` | `field` 或 `fieldId` 擇一、`operator`、`value`、`values`、`from`、`to`、`includeBlank`、`amountBasis`、`drCr` | 文字：`equals`、`notEquals`、`contains`、`notContains`、`startsWith`、`notStartsWith`、`endsWith`、`notEndsWith`、`in`、`notIn`、`isBlank`、`isNotBlank`；日期：`on`、`notEquals`、`before`、`onOrBefore`、`after`、`onOrAfter`、`between`、`notBetween`、`in`、`notIn`、`dayOfMonthIn`、`dayOfMonthNotIn`、`isBlank`、`isNotBlank`；金額：`equals`、`notEquals`、`greaterThan`、`greaterThanOrEqual`、`lessThan`、`lessThanOrEqual`、`between`、`notBetween`、`in`、`notIn`、`isBlank`、`isNotBlank`。`contains` 的 `values` 可放多個關鍵字（任一命中），`notContains` 是它的否定（一個都不含）；`dayOfMonthIn`、`dayOfMonthNotIn` 的 `values` 是 1 到 31 的整數，重複自動合併；`includeBlank` 省略時一律 false（空白不列入）；金額 `amountBasis` 為 `absolute`（預設）或 `signed` |
| `entityFrequency` | `field`、`countUnit`、`countOperator`、`countFrom`、`countTo` | `field` 為 `accNum`、`createBy` 或 `approveBy`；單位為 `entries` 或 `vouchers`；比較為 `equals`、`lessThan`、`greaterThan`、`between`、`lessThanOrEqual` 或 `greaterThanOrEqual`。門檻為非負整數，`between` 含兩端。依所選母體計算，空白識別值不命中；去重傳票不計空白號碼 |
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
比較整數部分的絕對值，不看正負或小數，保留前導零的位數；核心與額外欄位共用相同規則。

情境根層不再接受 `exclusions`；舊情境帶這個欄位時回 `invalid_scenario`，訊息說改用條件的否定模式重新
保存。`filter.preview` 回 `count`、`voucherCount` 與 `previewRows`，沒有待判定計數。

`fieldValue` 的 `isBlank`、`isNotBlank` 若選到沒有來源配對的核心欄位，回 `invalid_scenario` 與條件列
`details`，引導回「欄位配對」指派或改選欄位；已配對欄位的空白資料仍可正常篩選。
`docDate` 採 `sameAsPostDate` 時是有來源的衍生欄位，不因未直接配對 `docDate` 而拒絕。
這項修正當時的規則版本為 `filter-2026-09-07-v12`；文字讀回仍用「且」「或」，不改變條件的左至右合併順序。
2026-09-18 新增欄位內的借貸方向限制及每月月初、月底天數後，篩選版本為 `filter-2026-09-18-v15`。
加入條件括號、傳票量詞與分類階層後，現行篩選版本為 `filter-2026-09-18-v16`。

規則新增 `type: "group"` 和 `type: "voucher"`，兩者以 `rules` 保存子規則。`group` 的欄位條件綁定同一筆分錄；
各層的 `join` 仍依原有順序左折疊，最多巢狀八層。`voucher` 必填 `side: "all" | "debit" | "credit"`
及 `quantifier: "any" | "all" | "none"`。子條件先在每筆分錄上計算，`all` 額外要求範圍內至少有一筆。
`voucher` 內可有 `group`，不可再嵌另一個 `voucher`。沒有該側分錄時只有 `none` 成立。
舊 `row`、`sameVoucher` 和省略新欄位的情境維持原語意。

`accountSide`、`accountPair` 和 `specialAccountCategoryPair` 的 `categorySelection` 可選 `role`、`node`、`subtree`，
依序表示相同審計角色、僅分類本身、分類及所有下層。借貸組合的兩側使用同一選取方式；兩側要各自選取時可組合單側條件。
省略時與舊版一樣使用 `role`；`categoryIds` 仍保存穩定分類 ID，名稱只用於呈現。
`accountTaxonomy.save.categories[].parentCategoryId` 可為既有分類 ID 或 null；舊呼叫端省略此欄時保留原上層。
同一回應及 `project.load.taxonomy` 都帶回上層 ID。新增分類仍由後端產生 ID；先保存上層後即可選為其他分類的上層。
分類角色不因移動階層而改變，階層變更沿用既有分類修訂與結果失效契約。
預篩選版本維持 `prescreen-2026-09-17-v8`。舊命中結果需重新產生，情境定義保留供重新保存；
未指定借貸方向的舊條件維持原結果。

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
`export.workpaperStream` 每次寫新的版本檔 `<案件前綴>_<yyyyMMdd-yyyyMMdd>_WorkingPaper_<yyyyMMdd-HHmmss>.xlsx`，不覆蓋、不刪
舊版。`export.accountMappingTemplate` 產生的是工作檔，不是報告：固定檔名 `<案件前綴>_AccountMapping.xlsx`
寫進案件資料夾、不進 `reportArtifacts`。Payload 的 `onlyIfMissing` 預設為 false，手動產生會覆寫；
true 表示只在檔案不存在時建立，最後發布時也不覆蓋稍後出現的檔案。非布林值會回 `invalid_payload`。
回應為 `{ ok, filePath, fileName, rowCount, validationRunId, disposition }`；`disposition` 為 `created`
或 `kept`。保留原檔時不讀取其中內容，`rowCount` 為 null。審計員用 Excel 填完 C 欄存回原檔，再由
`import.accountMapping.fromFile` 讀同一路徑。
五種報告的檔名均加入案件查核起訖日 `yyyyMMdd-yyyyMMdd`；同期間重匯維持原覆寫或版本規則。
報告項目新增 `fullPath`，供本機畫面顯示實際儲存位置；此欄位不寫入索引，重開或搬移案件後重新解析。
`fileName` 仍只有相對檔名。
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
