# JET action 介面契約

更新日期：2026-08-27

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
  "error": null
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
    "field": "<optional payload field>"
  }
}
```

`error.field` 只能由後端在能明確指出欄位時提供。前端不得從錯誤文字猜欄位；目前建案名稱錯誤會
使用 `caseName`。完整錯誤碼登錄位於 `Domain/JetActionException.cs`。

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
- `query.prescreenPage`
- `query.infSamplePage`
- `query.tagMatrixScenarios`
- `query.tagMatrixVoucherPage`
- `query.tagMatrixRowPage`

### 匯出

- `export.validationArtifacts`
- `export.prescreenReport`
- `export.criteriaSelectionReport`
- `export.workpaperStream`
- `export.accountMappingTemplate`

### 報告產物清理

- `report.cleanupPreview`
- `report.cleanupConfirm`

### 訊息、原生視窗與開發工具

- `log.append`
- `log.recent`
- `host.selectFile`
- `host.selectFiles`
- `host.selectSavePath`
- `host.openFolder`
- `host.exitApp`
- `dev.db.overview`
- `dev.db.tableData`
- `dev.db.reconcile`
- `dev.log.export`

`dev.*` action 只服務開發面板。一般使用者流程不能依賴它們。

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
[`history/new-je-tool/action-contract-manifest-new-je-tool.md`](history/new-je-tool/action-contract-manifest-new-je-tool.md)。
它能協助追查舊設計，但欄位有疑義時仍要回到現行處理器與測試核對。
