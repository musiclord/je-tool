# 欄位配對同步、支援日誌與損壞案件復原計畫

這份是 2026-11-01 壓縮後的摘要。使用者原話、題目與回答、日期、數字與裁定照原文保留；實作經過、驗證收據與重跑過程壓成結果。
壓縮前的全文在提交 `85af454`，可用 `git show 85af454:docs/history/specs/2026-09-01-frontend-sync-devlog-mutation-plan.md` 取回。

更新日期：2026-09-07

狀態：已完成（使用者 2026-09-05 確認測試環境驗收通過）

目前步驟：第一階段（前端同步、支援日誌、journal 衝突刪案、Stryker 判定）已於 2026-09-02 以
commit `641e3f0` 提交並推送，使用者確認沒問題。同日在公司測試環境撞到 `artifact_recovery_conflict`，
使用者裁定接著在同一份計畫做第二階段：拆解報告產物儲存、科目配對範本改為工作檔、Working Paper 改成
版本檔、移除清理功能。第二階段於 2026-09-02 實作完成，2026-09-04 依使用者要求補齊測試循環，修正鎖檔提示
與 Working Paper 稽核取代數，並獲授權提交及推送，成果為 `9ddd812`，提交後的完整 `ReleaseCandidate` 通過。
2026-09-05 使用者確認公司測試環境驗收通過；審計員回報的兩點（編輯過底稿的專案不能重新載入或刪除、欄位配對
必填檢查偶發失效）分別對應第二階段與第一階段已修正的內容，2026-09-04 使用者裁定視為已修正，在後續計畫用目前
版本重測。

本檔在 2026-09-05 到 06 曾被另一個 agent 改成「篩選完整性與操作體驗改善計畫」並插入篩選工作的段落；2026-09-07
依使用者裁定改回原標題，插入的段落移到文末附錄保留，內容由
[`2026-09-07-filter-convergence-plan.md`](2026-09-07-filter-convergence-plan.md) 取代。

## 第一階段：目標與邊界

本計畫接續 Claude 已做的前端同步與 DEV 日誌工作，並納入使用者同日回報的案件 journal 衝突：

- 欄位配對的右側必填檢查要和使用者選擇立即一致，不再偶發停在舊畫面。
- 使用者不必成功載入案件，也能從 picker 複製錯誤並輸出可交給支援人員或 agent 的安全日誌。
- 報告產物 journal 發生無法判定的衝突時，載入維持保守拒絕；使用者確認永久刪除後不能再被同一衝突卡住。
- 重新判斷 Stryker.NET 是否適合 JET 的 harness，不沿用先前以錯誤 runner 得出的結論。

當時的邊界：Release 日誌只保存 allowlist 事件，進入緩衝前就移除 SQL、參數、檔名、絕對路徑、案件名稱與原始例外訊息，
Debug 另保留完整 raw sink；匯出的 `.txt` 只寫到選定或目前案件目錄；journal 衝突不自動猜測、覆寫、隔離或開啟；
本輪不新增案件快照或匯入功能，不讀私人資料根目錄。當時對 journal 衝突拒絕載入的處理，已由第二階段的直接丟棄舊 journal 取代。

## 第一階段結果

- 欄位配對：會影響必填鐵軌、提交資格與衍生區塊的變更一律推進畫面版本；`renderContent` 只依唯一的 `data-focus-key`
  還原焦點並統一還原捲動容器。新 GUI 情境 `mapping-required-sync` 清空再補回一個必填欄位，缺漏數立即
  `0 → 1 → 0`，兩次重繪都保留同一下拉焦點。
- 兩層診斷日誌：Bridge 回應新增 `correlationId`；`support.log.export` 在 Release 與 Debug 都註冊，可按案件或
  「案件＋correlation」篩選安全緩衝；`dev.log.exportFile` 維持 Debug-only，畫面明示 raw 日誌可能含案件資料。
  兩種 `.txt` 都先寫暫存檔再原子改名，失敗會清除半成品。
- journal 衝突與刪案：可辨識的內容衝突回穩定錯誤碼 `artifact_recovery_conflict` 並寫安全事件；刪案只取得案件外鎖，
  不讀取或更動案件內 journal。新 GUI 情境 `conflicted-journal-recovery` 完成「載入失敗 → 輸出安全支援日誌 → 確認刪案」。
- Stryker.NET 判定：4.16.0 使用 `--test-runner mtp` 能分析 `JET.csproj` 並找到 3,668 項 MTP 測試，先前「Stryker 不支援 MTP」
  的結論已取代。但它不經過 JET harness 的 profile 排除，初始 run 碰到兩個未授權 PrivateCase 測試而停止；上游 issue #3754、
  #3692 也記錄 MTP runner 的限制。結論：不安裝 Stryker，不建立專用 mutation test project，也不納入日常或正式檢查；
  重啟條件是 MTP runner 缺陷修正後，再跑同一個單檔煙霧。這個結論後來由文末「本機功能與品質補齊」的變異測試範圍取代。
- 驗證：相關 `Focused`、`Contract`、四個 `Gui` 情境與 `Documentation` 通過；`Public` 為 3,409 total、3,402 executed／passed、
  0 failed、7 個登錄 skip。未執行 live `Provider`、`PrivateCase`、原生 Excel 或公司環境驗收。

### 提交前複審與缺陷修正（2026-09-01 至 2026-09-02）

使用者下達「複審通過即提交」的條件授權後，另一個 session 以全量 diff 與八個審查角度複審。機敏資料與移除行為都已逐項核對；
補上候選清單漏列的 8 個新檔案（清單成為 1,219 個路徑），並找到一個待修缺陷：`dev.log.exportFile` 在 sink 檔存在但無法開啟時，
因例外被包裝成 `support_log_export_failed`，不會退回 ring buffer。其餘品質項目記入 `development-status.md` 的「已知技術債」。

使用者裁定選項一「先修缺陷再提交」。修正後 `ProjectLogFileWriter.WriteAsync` 只包裝自身寫入的例外，來源讀取失敗原樣拋回，
handler 因此能退回 ring buffer。新測試 `DevLogExportFile_LockedSinkFallsBackToRingBufferAndRemovesPartialTemporaryFile`
先失敗再通過；`Public` 為 3,410 total、3,403 executed／passed、0 failed、7 個登錄 skip，多出來的一個就是這次新增的測試。

## 第二階段：報告產物儲存拆解（2026-09-02 立案）

### 起因

公司測試環境在 picker 載入案件時撞到 `artifact_recovery_conflict`。追到程式路徑後確認這不是競態，而是
照設計流程操作就會發生：科目配對範本（`IAccountMappingTemplateWriter`）設計上要審計員用 Excel 填 C 欄後
存回原檔再匯回；同一份檔卻由 `ProjectReportArtifactStore`（2,612 行）當成不可變產物，用 manifest 的
SHA-256 在重新匯出前核對舊檔。檔被 Excel 存過就核對失敗，journal 留下，之後每次載入都拒絕，唯一出路是
刪案重建。原則已寫進 `AGENTS.md`「預設審計員可信」與 `docs/project-context.md`；本階段是第一次落實。

### 使用者裁定（2026-09-02 兩輪問答）

| 問題 | 裁定 |
|:---|:---|
| 報告產物儲存怎麼處理 | 只留「寫暫存檔、完整後改名」。拿掉 journal、隔離區、雜湊核對、載入時拒絕 |
| 「正式輸出不得擅自更動」範圍 | 只有 Working Paper。每次匯出寫新版本檔、不覆蓋、不刪舊版；其餘報告覆蓋同名檔 |
| 科目配對範本定位 | 移出報告清單，改為工作檔：固定檔名、不進 manifest、不核對、可反覆覆寫；匯回時直接讀原檔 |
| 單人工具上的多人機制 | 這次不動（科目分類 revision、案件鎖、一次一作業），寫進已知技術債附重啟條件 |
| 立案方式 | 不關第一階段，本工作接在同一份計畫檔後面 |
| 第六步「清理舊報告版本」 | 整個拿掉（handler、retention policy、前端面板、audit receipt） |
| 殘留的舊 journal 檔 | 載入時直接刪除，以目前 manifest 與檔案為準，寫一筆支援日誌事件 |

### 使用者流程（審計員視角）

1. 第四步驗證完成後按「產生科目配對範本」，JET 把 `<案件前綴>_AccountMapping.xlsx` 寫進案件資料夾並顯示路徑。
   審計員用 Excel 開它、填 C 欄、存檔，再按「上傳」匯回。格式不對時訊息指出哪一欄；重新產生範本永遠可行。
2. 第六步匯出報告。Validation Report、INF Report、Pre-screening Report、Criteria Selection Report 每次覆蓋同名檔；
   Working Paper 每次產生 `<案件前綴>_WorkingPaper_<yyyyMMdd-HHmmss>.xlsx`，舊版留著。匯出中斷只會留下以 `.` 開頭的暫存檔，
   下次寫入前自動清掉。
3. 審計員在 JET 之外改了任何報告檔，JET 都不擋：載入照常，第六步清單在該筆旁標示「已在 JET 之外修改」或「檔案不存在」，
   重新匯出即可。
4. 用舊版 JET 鎖住的案件，裝新版後直接能開；支援日誌多一筆「已清除殘留的輸出紀錄」。

範圍外：科目分類 revision 衝突、案件鎖、一次一作業閘、SQL Server 線；報告內容與規則；資安強化（reparse point
鏈檢查一併移除，只保留「檔案必須在案件資料夾內」的路徑包含檢查）。

### 第二階段結果（2026-09-02）

- 旅程測試 `ReportArtifactTrustJourneyTests` 六個測試在舊程式上 5 個失敗、1 個通過；
  `AccountMappingTemplate_EditedInPlaceAndReexported_ProjectStillLoads` 的失敗訊息就是 `artifact_recovery_conflict`，
  證明「填範本存回原檔再匯出」這條路徑必定鎖案。
- `ProjectReportArtifactStore` 由 2,612 行改為 765 行，只留暫存改名、manifest、`fileState`、殘留 journal 清除與刪案 lease；
  `ReportArtifact` 以 `LastWriteUtc` 與 `FileState` 取代 `Sha256`。範本改走 `ProjectWorkFileWriter`。清理功能與六個舊測試檔移除，
  GUI 情境 `conflicted-journal-recovery` 換成 `edited-report-still-loads`。
- 修正過程找到暫存檔在寫入成功後才登記清理、唯讀 `project.json` 會拋出未包裝的例外等問題，都已修正；未參與實作的複審確認
  各關卡都是「防 JET 自己」，沒有「防審計員操作」型。
- `Public` 為 3,328 total、全部通過、0 skip（登錄 skip 從 7 變 0，總數比第一階段少 82，少的是退役的 store、清理與 journal 測試）。
  `Contract`、框架自身測試、`Package`、四個 `Gui` 情境與 `Excel`（五份報告加科目配對範本共六份工作簿）都通過。
  `Provider`、`PrivateCase` 與公司環境實機驗收不在本階段。

### 使用者驗收清單（2026-09-04 整理，2026-09-05 使用者確認既有版本通過）

自動驗證只證明合成資料與測試主機上的行為。下面每一步都要由使用者在自己的電腦或公司測試環境操作一次。

1. 建置並啟動桌面程式：`dotnet run --project src/JET/JET/JET.csproj -c Debug`。
2. 舊案件開得起來：用撞到 `artifact_recovery_conflict` 的案件，或資料夾裡還留著 `.report-artifacts.mutation-v1.json` 的舊案件開啟。
   預期載入成功、那個檔案消失，輸出的 `JET-support-*.txt` 含 `artifact.journal.discarded`，沒有檔名、路徑或案件名稱。
3. 範本填回原檔再匯出：產生範本、用 Excel 在 C 欄填分類存回原檔、選同一個檔重新匯入，再按「重新產生兩份報告」。
   預期三步都成功，重新開啟案件也成功。這條路徑在舊版會讓整個案件無法載入。
4. Working Paper 留版本，其餘覆蓋：匯出 Working Paper 兩次會出現兩個 `<案件前綴>_WorkingPaper_<日期-時間>.xlsx`；
   9 月 4 日版本的畫面只顯示最新一份，9 月 5 日使用者另行裁定改為顯示全部版本。其他四種報告各匯出兩次，每種仍只有一個檔。
5. 在 JET 之外改檔只提醒不擋：改過的報告出現「已在 JET 之外修改」，刪掉的報告出現「檔案不存在，重新匯出即可」，全程不應出現紅色錯誤。
6. 舊功能已拿掉：第六步不再有「清理舊報告版本」面板；第四步報告卡片寫「兩份驗證報告」，範本另有自己的按鈕與說明。
7. 錯誤訊息有出路：用 Excel 開著 Validation Report 再匯出一次，錯誤訊息說檔案被其他程式開著、關閉後再試。
   Working Paper 不在此限，開著舊版也能匯出新版本。

沒有機器能代替的部分：公司正式環境的 Windows 與 Office 版本、`Provider`（SQL Server）、`PrivateCase`（真實案件）都未執行。

### 測試循環更新與交付（2026-09-04）

使用者要求依修正後的內容完善測試，避免後續驗證沿用舊邏輯。完整工作簿旅程改走「填範本原檔、匯回、重開案件、重新匯出」；
補強四種一般報告的同名覆蓋、Working Paper 舊檔保留、外部修改與刪檔後重新匯出，以及檔案被佔用時的失敗與重試；
`Package` 固定納入報告信任旅程和儲存層回歸測試。新測試找出範本與報告在檔案被佔用時拋出未轉換的
`UnauthorizedAccessException`，已改成 `file_read_error` 並提示關閉開檔程式後重試。
提交前複審另找到 Working Paper 舊版本被計入 `replaced_count`，修正後兩次發布的取代數都是 0。

結果：`Public` 3,335 項全部通過，`Package` 91 項、四個 `Gui` 情境、`Excel` 六份工作簿、`Contract`、框架自身測試與
`Documentation` 都通過。使用者 2026-09-04 明示授權本輪 commit 與 push 供測試環境人工驗收；推送後停止開發，等待驗收。

### 驗收結果與後續補強（2026-09-05）

- 使用者確認第二階段公司測試環境的人工驗收已通過，並要求繼續完成計畫、修正開發中發現的問題。
- 9 月 4 日提交 `9ddd8128130a526be51d7f3e423c451b98f3f748` 後的 `ReleaseCandidate -Configuration Release` 全部通過，結束碼 0。
- 使用者裁定第六步列出全部 Working Paper 版本，按產生時間由新到舊排列，保留舊版入口。歷史清單包含
  已過期版本；目前流程是否完成仍須根據現行驗證與篩選結果判斷，不能由舊版底稿冒充。
- 開發與測試維持 SQLite 和 DuckDB 範圍；不啟動 live `Provider`，不讀取 `PrivateCase` 資料。
- 儲存審查發現 4,096 筆上限在正式檔發布後才檢查，且刪檔不會減少清單筆數；檢查已移到寫檔前。清單已滿時如何處理已刪檔紀錄，
  當時仍待使用者裁定，裁定見下一節。既有 4 MiB 讀取上限沒有變更。
- 其他修正：範本在發布前取消時不再改寫原檔；Working Paper 發布改為禁止覆蓋外部放入的同名檔，重試另取版本名；
  報告改名後的 manifest 寫入不再接受取消。依據是程式時序、
  [Microsoft 取消模式建議](https://devblogs.microsoft.com/premier-developer/recommended-patterns-for-cancellationtoken/)
  與 [File.Move 官方文件](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.move?view=net-10.0)，沒有以排程競態實測改名後的取消時間窗。
- 結果：`Focused` 73 項與 299 項架構檢查、四個 `Gui` 情境、`Public` 3,341 項、`Package` 95 項、`Excel`、`Contract`
  與框架自身測試（418 個斷言、29 個情境）、`Documentation`（34 份文件）都通過。框架自身的鎖競爭測試曾因持鎖與競爭程序沒有重疊而失敗，
  已改成由主程序持鎖，原斷言全部保留。
- 未修改檔案 `PrivateCaseScenarioDiagnostics.cs:155` 有一筆 CS8602 警告，記入 `development-status.md` 的技術債。

## 本機功能與品質補齊（2026-09-05）

使用者已確認本節方案並要求開始開發。本節取代前文的等待容量裁定、暫緩變異測試及本輪只允許單一
修改者的安排；既有歷史結果與 Git 授權不延伸到本輪。

### 使用者的操作與已確認裁定

1. 第三步保留對照表格和簡易清單，核准日方式與來源欄放在一起。指定核准日來源欄時改成由來源欄提供；
   選不提供或與總帳日期相同時清除該欄配對。清除原本使用的來源欄時改成不提供。兩種畫面、自動建議與
   草稿載入都保持一致，後端規則不變。「還原為已提交版本」一次還原欄位、金額模式與所有 GL 設定。
2. 第三、四步集中改善操作體驗，不重做六步外觀。配對缺漏提示可帶使用者找到欄位，選完立即更新可否
   確認，保留焦點與捲動位置。第四步分別顯示驗證結果、報告產生結果與範本狀態。
3. 驗證完成後，範本檔不存在才自動建立，已有檔案就保留。手動重新產生仍可覆寫，按鈕明說結果。
   範本與兩份驗證報告各自記錄成功或失敗，單項失敗保留已完成成果並提供重試，取消後停止後續輸出。
   驗證發現資料差異時仍能取得範本與報告。自動建立在最後改名時也不得覆蓋稍後才出現的檔案。
4. 第六步維持全部 Working Paper 版本、最新在上、每頁 50 筆與舊版入口。只有即將超過 4,096 筆時才移除
   已確認檔案不存在的底稿紀錄；仍存在或無法確認的檔案都保留，不刪實體檔。仍超限時在寫檔前說明可
   自行整理舊檔後重試。移除紀錄與新底稿一起保存；取消或內容寫入失敗不提前改清單。匯出回應補上目前
   完整清單，前端立即同步，不改原有 artifact 或 artifacts 欄位的意義。

### 變異測試的範圍

本輪新增需要時才執行的 `Mutation` 命令，沿用 `tools/verify.ps1`，不成為日常開發或發行的必跑項。

- 使用者允許採用尚未正式發行、已查核的 Stryker 原始碼
  `30005a2f52c53ac2b0de49a90145f326ea43d675`，並維護測試選取所需的小範圍修補。版本、修補理由和雜湊
  要可追溯，日後官方具備等價行為時再移除修補。上游來源只從官方儲存庫取得。
- 先驗證 `GlProjectionGuard`，再處理 `MoneyScaling`，各自固定對應公開測試類別；排除 `TestProfile=PrivateCase`、
  `TestProfile=Provider` 與 `TestProfile=Scale`。測試身分不明或出現範圍外請求時停止，不退回執行全套測試。
- 工具、快取、副本與報告都放在被忽略的 `artifacts/harness/mutation/`，不全域安裝、不上傳報告。
  同時一個 worker、較低程序優先權、每次上限 30 分鐘；不為分數修改既有斷言或預期值。

### 結果

- 第三、四步與底稿的回歸案例、日誌重複程式整理與診斷計數的空值警告修正都已完成。修正過程中找到並修好：
  核准日指派後模式仍是 unmapped、草稿與已提交配對共用物件造成還原依據被改寫、底稿在內容產生期間被放回時遺失清單紀錄、
  第六步取消後仍會啟動底稿。正常的範本保留訊息改成資訊樣式，只有實際失敗才用紅色提示。
- 變異測試：上游 Stryker 先改寫所有檔案才套用檔案篩選，在未選取的 record 建構式遇到空值錯誤；修補改為只把指定檔案交給變異器。
  工具專用相依項目固定為 VisualBasic `5.9.0`、Xml 與 Pkcs `9.0.18`，JET 產品套件沒有改動。工具另依
  [Microsoft 的下載逾時說明](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-10.0)
  補上共同期限，並依 [Stryker 的結果定義](https://stryker-mutator.io/docs/mutation-testing-elements/mutant-states-and-metrics/)
  把 `NoCoverage` 納入待判讀。

| 命令 | 結果與範圍 |
|:---|:---|
| `Mutation -Configuration Release` | GlProjectionGuard 通過；選取 7 個公開測試，6 個變異全部被測試抓到。 |
| `Mutation -Configuration Release -MutationScope MoneyScaling` | 重跑通過；選取 15 個公開測試，25 個變異中有 14 個被測試抓到、3 個由上游略過、8 個由上游判為編譯失敗。沒有 Survived 或 NoCoverage；未執行的 11 個不算測試抓到。 |
| `Gui -Configuration AgentGuiTest` | 六個情境通過，兩張合成截圖已人工複核。 |
| `tools/tests/verify-contract.tests.ps1` | 通過，439 個斷言、32 個情境。變異邊界另為 84 個斷言，真實程序歸屬另為 158 個斷言。 |
| `Public -Configuration Release -NoRestore` | 通過，3,359 項全部成功，沒有失敗或跳過；產品建置 0 warning、0 error。 |
| `Package -Configuration Release -NoRestore` | 通過，103 項範本與報告測試全部成功。 |
| `Excel -Configuration Release` | 通過；六份合成工作簿完成原生 Excel 開啟、重算、另存、重開及 PDF 檢查。 |
| `Documentation` | 通過，34 份文件，0 error、0 warning。 |

MoneyScaling 首次量測有一個未被抓到的錯誤：把超過 `long.MaxValue` 才拒絕改成等於上限也拒絕，原來的測試仍通過。
新增兩項測試、以 12 個固定案例涵蓋正負上下界、界外值及四捨五入邊界後，原本存活的 ID 21 已被抓到。
略過的 ID 0、5、9 來自上游「區塊內已有其他變異」的過濾規則，不能解讀成已被測試抓到。ID 11、12、14、16、17、18、19、22
的原始狀態為 CompileError；逐一獨立編譯後，11、12、14 確實無法編譯，16、17、18、19、22 可編譯且 18 個固定案例分別觀察到
6、17、11、3、6 個行為差異，並非等價替換。這五項屬上游批次編譯的量測限制，不標為 Stryker Killed，也不重新計算分數。

### 收尾

候選來源清單核對 1,235 個檔案。工作樹保留所有修改，HEAD 仍為 `9ddd8128130a526be51d7f3e423c451b98f3f748`，沒有暫存、提交或推送。
未執行 live `Provider`、`PrivateCase`、公司環境的新增功能驗收或新的完整 `ReleaseCandidate`。

下一步請使用者確認三件操作：第三步切換核准日來源及還原配對是否自然；第四步重新驗證能否保留已填
範本，手動重新產生的覆寫提示是否清楚；第六步能否找到並開啟需要的舊版底稿。這些新增操作確認後，
才把本計畫標為已完成；若有問題，沿用本計畫修正，不另建一份同範圍待辦。

既有八項延後工作仍保存在 `development-status.md`：SQL Server live 驗證、企業多人環境、預篩選去留、audit log 查詢與保留政策、
KCT 保存情境重驗、公司正式部署、KCT 新條件來源及 IDEA 完整人工驗收。

## 附錄：2026-09-05 另一輪篩選工作的紀錄（已由 2026-09-07 計畫取代）

以下段落是另一個 agent 在 2026-09-05 到 06 插入本檔的內容，保留作為歷史；其中的需求敘述、
「待判定」與「排除區域」設計已由 [`2026-09-07-filter-convergence-plan.md`](2026-09-07-filter-convergence-plan.md)
的裁定取代，不再是現行規格。

### 當時的範圍與已確認事項

使用者於 2026-09-05 回報，審計員已確認「編輯底稿後，專案可以正常重新載入與刪除」及「欄位配對的
必填欄位檢查，實際操作結果可接受」兩項修正驗收通過。這是操作驗收，不擴大為全部 IDEA 功能已完成。

尚待完成：貸方只有未分類科目，同時要求「存在貸方非 Cash」及「貸方完全沒有 Cash」時，補分類可能改變兩個條件。
目前存在條件要求已分類科目，因此不命中，也不列待判定。已向使用者提出是否應改列待判定；尚未收到回覆，未擅自更改這個交叉行為。

### 當時的設計重點

- 第五步可自由組合借方或貸方的指定分類、其他已分類科目，以及整側沒有指定分類；日期可多選、逐行輸入或貼上，並可包含或排除；
  文字增加清單排除、開頭與結尾符合、空白判斷；金額增加清單與區間外比較。設定後按預覽才查詢，以傳票清單呈現。
- 主要條件決定命中列，其他同傳票條件可由不同分錄佐證；只有傳票層條件時呈現該傳票查核期間內分錄，註明「傳票條件成立」。
- 非 Cash 有兩種需求：「有其他已分類科目」允許同側仍有 Cash；「整側沒有 Cash」不允許。未分類不算非 Cash。
  新的整側沒有條件，若不見指定分類但仍有未分類科目，先列待判定，提供補分類入口。
- 實作中查明舊匯入會將分類留白轉成 Others，使用者再次裁定：新條件把這種留白視為未分類，舊情境
  仍維持原結果。因此 schema v10 增加分類是否實際填寫的紀錄，從原始分類列分批回填，原分類值不改。
- 獨立排除區域預設只移除命中列，也可移除整張傳票；多項排除採符合任一項即排除，排除不改寫同傳票判斷所依據的資料。
- 日期及金額區間包含兩端，起訖倒置提示修正；清單最多 100 個不同值，每行一值，保留文字前置零。日期不經時區轉換。
- 其他步驟：建立案件統一期末財報準備日期名稱；將「儲存並結束」改為「結束 JET」，提示未保存草稿不會留到下次開啟，
  不加自動保存或確認關卡；新增 `query.filterVoucherPage` 與 `query.filterVoucherRowsPage`。
- 參考資料：[Power Query 條件類型](https://learn.microsoft.com/en-us/power-query/filter-values)、
  [Carbon 批次篩選](https://carbondesignsystem.com/patterns/filtering/)、
  [W3C 日期選擇器鍵盤操作](https://www.w3.org/WAI/ARIA/apg/patterns/dialog-modal/examples/datepicker-dialog/)。

### 當時的實作經過與結果

整合驗證中依序找到並修正：保存摘要漏回排除設定、新條件尚未同步中文標籤、DuckDB 加總回傳 BigInteger、
舊摘要把小寫 or 誤寫成 AND、非營業日摘要移過 OR 造成說明變意、排除區域省略選項時多移除一列空白，
以及 Excel 單格超過 32,767 字元的長條件須接續寫入後面的列。上限來源為
[Microsoft Excel 規格](https://support.microsoft.com/en-US/Excel/excel-specifications-and-limits)。
既有測試的調整只反映已核准行為，不刪除或放寬既有業務斷言。

合成驗收案例放在本機 `artifacts/acceptance/filter-completeness/`，共十二筆分錄、查核期間 2025-01-01 至 2025-12-31，
全部是合成資料；程式測試的固定資料另保存在 `FilterCompletenessTests.cs`。

| 檢查 | 2026-09-06 目前版本的結果 |
|:---|:---|
| `Public -Configuration Release -NoRestore` | 3,392 項執行及通過，沒有失敗或略過 |
| `Package -Configuration Release -NoRestore` | 103 項測試通過，封裝檢查通過 |
| `Gui -Configuration AgentGuiTest` | 七個情境通過，包含跨月日曆、預覽過期、更新、副本及取消 |
| `Excel -Configuration Release` | 五份合成報告及一份科目配對工作檔通過原生 Excel 檢查 |
| `Contract` 與驗證框架自身測試 | 通過；框架自身 439 個斷言、32 個情境 |
| `Documentation` | 34 份文件通過，0 error、0 warning |

以上使用合成資料，未執行 live SQL Server、私人案件或審計員操作驗收；沒有暫存、提交或推送。
