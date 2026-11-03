# Claude Design 前端與現有後端整合紀錄

這份是 2026-11-01 壓縮後的摘要。使用者原話、題目與回答、日期、數字與裁定照原文保留；實作經過、驗證收據與重跑過程壓成結果。
壓縮前的全文在提交 `85af454`，可用 `git show 85af454:docs/history/specs/2026-07-30-claude-design-frontend-integration.md` 取回。

執行日期 2026-07-30，Windows 11 與 PowerShell 7。輸入是當時的工作樹與 `docs/design_handoff_jet_frontend/`（01–05 規格與 `design/*.dc.html` 參考稿），
基準 commit `a3393b5`，工作樹未提交，本輪沒有 commit 或 push。這份可以獨立閱讀，不需回頭查已移除的 `claude-code-handoff.md`。

## 範圍與邊界

只修改 `src/JET/JET/wwwroot/` 的前端，外加一支新的守衛測試。沒有變更後端程式、action 名稱、payload、response、固定的 `data-bind` 與 `data-action`、
可存取名稱或 `JetApi` 呼叫，所以 `docs/action-contract-manifest.md` 沒有改。

### 刻意未採用設計原型的結構（使用者明示邊界）

| 原型結構 | 處置 | 理由 |
|:---|:---|:---|
| `STEPS = ['建立案件','匯入資料','資料驗證與測試','進階條件篩選','匯出底稿','完成']` | **不採用**，`Store.STEPS` 原封不動 | 原型把欄位配對併入匯入並多出「完成」；runtime 的欄位配對是獨立步驟且有自己的閘門與 action |
| 欄位配對併入匯入 | **不採用** | 同上；`mapping` 步驟有 `mapping.commit.gl/tb` 與獨立閘門 |
| 第六個「完成」步驟 | **轉譯**為匯出成功後的步驟內完成摘要 | 不改 STEPS 長度、閘門、`doneStepCount` 與 `project.saveProgress` 進度持久化 |
| 區塊①逐項母體完整性核對搬進總覽 | **不採用**，改為連結導向步驟 3 | 逐項判定只能有一個權威渲染面；複製會形成第二套審計判定 |

這四條邊界由 `DesignIntegrationFrontendTests` 自動檢查。

設計稿 `04-state-machine-and-gating.md` 標記的阻斷級錯誤（`gate(i)` 依位置短路，導致「下一步」永遠不可按）只存在於原型；
現行的 `ui-core.stepGate` 本來就是不依位置的狀態閘門，所以沒有修補。

## 實際變更

- `app.css` 把設計 tokens 補成 CSS 變數，收斂散落的字面色碼，並加上總覽與完成摘要的樣式。
- `index.html` 的案件資訊列新增「查核期間」欄；原本的「進度」標籤正名為「目前步驟」，既有四個 `data-bind` 都保留。
- `app.js` 的總覽新增執行識別基準行、母體概況四欄、未執行驗證時的空狀態與底部唯讀聲明。
- `export-step.js` 在目前版本的 CriteriaSelectionReport 與 WorkingPaper 都存在時顯示完成摘要。
- 新增 `DesignIntegrationFrontendTests.cs` 的 6 個守衛。

整合前，頁首標籤寫「進度」但值是目前步驟的位置，和左欄目錄同名的「進度 x/6」（已完成步數）意思不同、數值也不同。依設計 `02 §B` 把頁首改名為「目前步驟」。
這是本輪唯一改變使用者可見語意的地方。

資料都取自既有狀態：母體概況四欄直接取 `state.lastRuns.validate.stats` 的 `glRowCount`、`voucherCount`、`totalDebit`、`net` 與期間，前端不聚合；
基準行取 `resultRef.runId` 與 `logicVersion`。設計要求的 `dataRev` 當時沒有，整段略去，不用佔位符假裝可追溯。未執行驗證時顯示空狀態，不用 0 冒充「已核對為零」。

## 未實作項目

| 設計交付項目 | 狀態 | 理由 |
|:---|:---|:---|
| `03` 區塊③ 預篩選命中率熱圖 | 未實作 | 需要 `query.populationInsights.ruleMonthly`（13 規則 × 月份 × 命中、去重傳票與當月母體分母），後端沒有這個聚合 |
| `03` 區塊④ 金額級距與 ECDF | 未實作 | 需要 `query.populationInsights.amountDistribution`（15 對數級距與 ECDF） |
| `03` 區塊⑤ 編製人員 Pareto 與低頻科目 | 未實作 | 需要 `query.populationInsights.concentration` |
| `03` 區塊⑥ 多重命中 UpSet | 未實作 | 需要 `query.ruleIntersections` 與 `query.ruleIntersectionPage`；**設計交付本身要求在 2,000 萬列 DuckDB 實測通過前延後** |
| ECharts 5.5.0 圖表庫 | 未引入 | WebView2 離線執行，需先內嵌版本；沒有圖表區塊就不需要 |
| Google Fonts 網路載入 | 未引入（刻意） | 現行規則不載入網路字體，靠系統字體與 fallback；已由守衛測試固定 |

這四個區塊屬後端功能階段，不在本輪範圍；相關契約也沒有先寫進 manifest，因為 manifest 只描述已實作的契約。

後續：本紀錄完成後，使用者要求把這部分併入開發規劃並列為優先處理事項。重新盤點後端後，推翻了上表「需要三個新 action」的前提：
區塊⑤的資料已存在於 `prescreen.run`，其餘可在既有執行的同一遍計算中落地。後續計畫已於 2026-07-31 完成並通過人工驗收，
重要決策與結果見 `development-log.md`。上表保留本輪當下的狀態，不追溯改寫。

## 守衛測試與驗證結果

`DesignIntegrationFrontendTests` 的 6 個守衛：步驟仍是六步且欄位配對獨立、匯出底稿在最後、沒有「完成」步驟；頁首標籤是「目前步驟」且五個 `data-bind` 都在；
總覽有唯讀聲明且不出現風險評分或分級措辭；母體概況綁 `lastRuns.validate.stats` 且有空狀態；完成摘要存在且不呼叫 `setStepIndex` 或 `projectSaveProgress`；
不載入 Google Fonts、CDN 或 ECharts。

| 檢查 | 結果 |
|:---|:---|
| Release build | 0 警告、0 錯誤 |
| 新增定向測試 | 6 通過、0 失敗 |
| Architecture 與契約守衛套件 | 178 通過、0 失敗 |
| 完整測試 | 2,465 通過、0 失敗、13 具名略過（共 2,478） |
| 受控 Agent GUI `All` | 15/15 場、289 操作、495 斷言、0 失敗；離場 `Status=inactive` |

完整測試比 2026-07-28 的 2,459 多 6 項，正好是本輪新增的守衛；13 項略過與先前相同，都需要儲存庫以外的條件。

本輪沒有產生新的 FolderProfile 候選；既有候選 `FolderProfile-20260728-145315` 只含大案修補，不含本輪前端變更。
也沒有做 Microsoft Excel 與千萬列等級案件的人工驗收、沒有做視覺截圖比對（GUI 檢查截圖數為 0）。這些缺口已由後續 `FolderProfile-20260731-131350` 與 2026-07-31 的人工驗收補上。

本輪結束時移交使用者目視的七項，已於 2026-07-31 驗收通過：案件資訊列五欄與「目前步驟」標籤；「目前步驟」與左欄「進度」數值不同；
總覽基準行顯示案號、期間、run 與 logic；母體概況與步驟 3 統計一致；未跑驗證時顯示空狀態而不顯示 0；匯出成功後出現完成摘要且仍是六步；
拉窄拉寬視窗時沒有重疊或裁切。

## 風險

- 母體概況與步驟 3 統計讀同一份 `stats`，但格式化寫在兩處；守衛測試只固定資料來源，不固定格式。
- 完成摘要的出現條件與匯出按鈕的啟用條件共用同一組判斷，但各自寫在 `render` 內，日後要一起改。
- 移除 `docs/claude-code-handoff.md` 後，這份是該輪交接結論唯一的完整出處；`development-log.md` 2026-07-30 條目保留摘要。
