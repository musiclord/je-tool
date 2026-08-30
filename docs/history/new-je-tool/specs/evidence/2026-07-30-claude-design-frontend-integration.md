# Receipt — Claude Design 前端與現有後端整合

執行日期：2026-07-30，Windows 11 + PowerShell 7
輸入：目前 repository working tree ＋ `docs/design_handoff_jet_frontend/`（01–05 規格與 `design/*.dc.html` 參考稿）
基準 commit：`a3393b5`（working tree 仍為 dirty，本輪未 commit／push）

本文件自含：可獨立閱讀，不需回頭查已移除的 `claude-code-handoff.md`。

---

## 1. 範圍與邊界

只修改 runtime 前端 `src/JET/JET/wwwroot/`，外加一支新的守衛測試。**未變更**任何後端程式、action 名稱、payload、response、固定 `data-bind`／`data-action`、可存取名稱或 `JetApi` 呼叫；`docs/action-contract-manifest.md` 因此未動。

### 刻意未採用設計原型的結構（使用者明示邊界）

| 原型結構 | 處置 | 理由 |
|:---|:---|:---|
| `STEPS = ['建立案件','匯入資料','資料驗證與測試','進階條件篩選','匯出底稿','完成']` | **不採用**，`Store.STEPS` 原封不動 | 原型把欄位配對併入匯入並多出「完成」；runtime 的欄位配對是獨立步驟且有自己的閘門與 action |
| 欄位配對併入匯入 | **不採用** | 同上；`mapping` 步驟有 `mapping.commit.gl/tb` 與獨立閘門 |
| 第六個「完成」步驟 | **轉譯**為匯出成功後的步驟內完成摘要 | 不改 STEPS 長度、閘門、`doneStepCount` 與 `project.saveProgress` 進度持久化 |
| 區塊①逐項母體完整性核對搬進總覽 | **不採用**，改為連結導向步驟 3 | 逐項判定只能有一個權威渲染面；複製會形成第二套審計判定 |

這四條邊界由 `DesignIntegrationFrontendTests` 機器把關，不只寫在文件裡。

### 設計稿的「關鍵缺陷」不存在於 runtime

`04-state-machine-and-gating.md` 標記的阻斷級 bug（`gate(i)` 位置短路導致「下一步」永遠不可按）只存在於原型。runtime 的 `ui-core.stepGate` 本來就是位置無關的狀態閘門，`stepPresentation`／`doneStepCount` 也已存在。已核對，未做任何修補。

---

## 2. 實際變更

| 檔案 | 變更 |
|:---|:---|
| `wwwroot/css/app.css` | 補齊設計 tokens 為 CSS 變數（需檢視底色、三種邊框變體、鎖定／骨架灰階、核對三態色）；把散落的字面 hex 收斂到 token；新增總覽基準行／母體概況／空狀態／唯讀聲明與完成摘要的樣式 |
| `wwwroot/index.html` | 案件資訊列新增「查核期間」欄（`data-bind="case-period"`）；原「進度」標籤正名為「目前步驟」。既有四個 `data-bind` 全數保留 |
| `wwwroot/js/app.js` | `renderSummary` 填入查核期間；總覽新增執行識別基準行、母體概況四欄 KPI、未執行驗證時的空狀態、底部唯讀聲明 |
| `wwwroot/js/steps/export-step.js` | 新增 `completionSummaryHtml`，在目前版本的 CriteriaSelectionReport 與 WorkingPaper 都存在時顯示完成摘要 |
| `tests/JET.Tests/Architecture/DesignIntegrationFrontendTests.cs` | 新增 6 個守衛（詳見 §4） |

### 語意修正：「目前步驟」≠「進度」

整合前，頁首標籤寫「進度」但值是 `currentStepIndex + 1`（位置），與左欄目錄同名的「進度 x/6」（`doneStepCount`，已完成步數）語意衝突且數值不同。依設計 `02 §B` 把頁首正名為「目前步驟」，兩者不再共用標籤。這是本輪唯一改動使用者可見語意的地方。

### 資料來源全為既有 state

- 母體概況四欄直接鏡射 `state.lastRuns.validate.stats` 的 `glRowCount`／`voucherCount`／`totalDebit`／`net` 與期間欄位，前端零聚合。
- 基準行取 `resultRef.runId` 與 `logicVersion`（優先 prescreen、其次 validate）。設計要求的 `dataRev` 目前 state 沒有，**整段略去而非以佔位符冒充可追溯性**。
- 未執行驗證時走空狀態，不以 0 冒充「已核對為零」（設計的「不適用 ≠ 0」硬性規範）。

---

## 3. 未實作項目與理由

| 設計交付項目 | 狀態 | 理由 |
|:---|:---|:---|
| `03` 區塊③ 預篩選命中率熱圖 | 未實作 | 需要 `query.populationInsights.ruleMonthly`（13 規則 × 月份 × 命中／去重傳票／當月母體分母），後端目前沒有這個聚合 |
| `03` 區塊④ 金額級距與 ECDF | 未實作 | 需要 `query.populationInsights.amountDistribution`（15 對數級距 + ECDF） |
| `03` 區塊⑤ 編製人員 Pareto／低頻科目 | 未實作 | 需要 `query.populationInsights.concentration` |
| `03` 區塊⑥ 多重命中 UpSet | 未實作 | 需要 `query.ruleIntersections` 與 `query.ruleIntersectionPage`；**設計交付本身要求在 2,000 萬列 DuckDB 實測通過前延後** |
| ECharts 5.5.0 圖表庫 | 未引入 | WebView2 離線執行，需先內嵌 vendored 版本；無圖表區塊即無需求 |
| Google Fonts 網路載入 | 未引入（刻意） | runtime 既有規則是不載入網路字體，靠系統安裝字體與 fallback 鏈；已由守衛測試固定 |

這四個區塊屬**後端功能階段**，不在「前端與現有後端整合」範圍內。依 manifest-first 規則，相關契約**尚未**寫入 `action-contract-manifest.md`——manifest 只描述已實作契約，先寫入會讓文件描述不存在的欄位。

**後續**：本 receipt 完成後，使用者要求把這部分併入開發規劃並列為優先處理事項。重新盤點後端的結果推翻了上表「需要三個新 action」的前提（區塊⑤資料已存在於 `prescreen.run`，其餘可在既有 run 的同一遍計算窗口落地）。後續計畫已於 2026-07-31 完成並通過人工驗收；master spec 依結案規則移除，重要決策與結果見 `development-log.md`，逐階段 receipts 由 Git 歷史保存。上表仍是本輪整合當下的狀態紀錄，不追溯改寫。

---

## 4. 新增守衛測試

`src/JET/tests/JET.Tests/Architecture/DesignIntegrationFrontendTests.cs`，6 個全綠：

1. `Store_KeepsSixStepModel_WithMappingStepAndExportLast` — 欄位配對仍是獨立步驟；最後一步是匯出底稿；`STEPS` 不得出現「完成」成員。
2. `CaseSummary_SeparatesCurrentStepPositionFromCompletedProgress` — 頁首標籤是「目前步驟」不是「進度」；五個 `data-bind` 都在。
3. `Overview_StaysReadOnly_AndDeclaresNotApplicableSemantics` — 唯讀聲明存在；不得出現風險評分／分級措辭。
4. `Overview_PopulationBlock_MirrorsValidateStatsWithoutFrontendAggregation` — KPI 綁 `lastRuns.validate.stats` 欄位；有空狀態。
5. `ExportStep_RendersCompletionSummary_WithoutAddingAStep` — 完成摘要存在且綁既有 artifact；不得出現 `setStepIndex` 或 `projectSaveProgress`。
6. `Frontend_DoesNotLoadExternalDesignAssets` — 不得引入 Google Fonts／CDN／ECharts。

---

## 5. 驗證結果

實際執行命令與輸出：

```powershell
dotnet build src/JET/JET.slnx --nologo -c Release
# 建置成功。0 個警告，0 個錯誤。經過時間 00:00:06.91

dotnet test ... -c Release --filter "FullyQualifiedName~DesignIntegrationFrontendTests"
# 已通過! 失敗 0，通過 6，略過 0，總計 6，持續時間 186 ms

dotnet test ... -c Release --filter "FullyQualifiedName~JET.Tests.Architecture"
# 已通過! 失敗 0，通過 178，略過 0，總計 178，持續時間 21 s

dotnet test src/JET/tests/JET.Tests/JET.Tests.csproj --no-build --nologo -c Release --logger trx
# 已通過! 失敗 0，通過 2465，略過 13，總計 2478，持續時間 2 m 21 s

pwsh -NoProfile -File tools/agent-gui-harness.ps1 -Command Run -Scenario All
# ok:true，15/15 場全數通過，289 個 GUI 操作，495 個斷言、0 失敗，每場 cleanup=stopped/complete

pwsh -NoProfile -File tools/agent-gui-harness.ps1 -Command Status
# {"ok":true,"status":"inactive","active":false}
```

| 檢查 | 結果 |
|:---|:---|
| Release build | 0 警告、0 錯誤 |
| 新增定向測試 | 6 通過、0 失敗 |
| Architecture／契約守衛套件 | 178 通過、0 失敗 |
| 完整測試 | 2,465 通過、0 失敗、13 具名略過（共 2,478） |
| 受控 Agent GUI `All` | 15/15 場、289 操作、495 斷言、0 失敗；離場 `Status=inactive` |

完整測試較 2026-07-28 基準的 2,459 增加 6 項，恰為本輪新增守衛；13 項略過與基準相同，全部需要 repository 外條件（真實檔案連結、私有 PBC、大型基準資料根、凍結歷史基準），無未知原因略過或失敗。

### 本輪未執行的檢查（2026-07-30 時點）

- **本輪未產生新的 FolderProfile 候選程式**。既有候選 `artifacts/convergence/FolderProfile-20260728-145315/JET.exe`（SHA-256 `F30C9A2D…F588B`）**只代表大案修補，不含本輪前端變更**，未被覆寫也不得冒充新 UI 候選。當時若要驗前端，須先建立新資料夾並完成 verifier。
- **未執行 Microsoft Excel 與 2,026 萬列人工驗收**——這是使用者關卡，不是自動檢查。
- **未做視覺截圖比對**。GUI harness 本輪非視覺 smoke、截圖預算 0；版面重疊、捲動與不同視窗寬度的判斷留給人工目視。

上述缺口已由後續 `FolderProfile-20260731-131350` 與 2026-07-31 一場制人工驗收銷帳；本節只保存 2026-07-30 這一輪的停止點。

---

## 6. 本輪結束時仍需人工驗收的項目（其後已銷帳）

自動測試涵蓋的是結構與契約邊界，**不涵蓋外觀正確性**。下列項目在本輪結束時曾移交使用者目視，並已在 2026-07-31 驗收通過：

1. 案件資訊列五欄與「目前步驟」標籤正確。
2. 「目前步驟」與左欄「進度」數值不同（永遠相同即為缺陷）。
3. 流程總覽基準行顯示案號、期間、run、logic。
4. 母體概況四欄與步驟 3 統計一致。
5. 未跑驗證時總覽顯示空狀態與骨架，**不顯示 0**。
6. 匯出成功後出現完成摘要，且流程仍是六步。
7. 拉窄／拉寬視窗時無重疊、裁切或版面撐破。

長中文、空值、loading、error、locked 與鍵盤焦點在實作時已就地處理（KPI 值與副標設 `overflow-wrap:anywhere`；空值一律降級為 `—`；總覽連結有 `:focus-visible` 輪廓；`prefers-reduced-motion` 由既有全域規則涵蓋）；這些目視項也已由後續人工場銷帳。

---

## 7. 風險

- 母體概況與步驟 3 統計列讀同一份 `stats`，但呈現位置不同；若日後有人改動其中一處的格式化方式，兩處會產生外觀落差。守衛測試只固定資料來源，不固定格式。
- 完成摘要的出現條件與匯出按鈕的啟用條件共用同一組 artifact 判斷，但各自寫在 `render` 內；若未來調整 artifact 比對規則，兩處要一起改。
- 移除 `docs/claude-code-handoff.md` 後，本 receipt 是該輪交接結論的唯一自含出處；`development-log.md` 2026-07-30 條目保留摘要。
