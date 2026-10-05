# 欄位配對同步、支援日誌與損壞案件復原計畫

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

## 目標

本節至「第二階段」之前保留第一階段的歷史目標、邊界與驗證。當時對 journal 衝突拒絕載入的處理，
已由第二階段的直接丟棄舊 journal 取代；目前執行範圍以上方「本次執行範圍與已確認事項」為準。

本計畫接續 Claude 已做的前端同步與 DEV 日誌工作，並納入使用者同日回報的案件 journal 衝突：

- 欄位配對的右側必填檢查要和使用者選擇立即一致，不再偶發停在舊畫面。
- 使用者不必成功載入案件，也能從 picker 複製錯誤並輸出可交給支援人員或 agent 的安全日誌。
- 報告產物 journal 發生無法判定的衝突時，載入維持保守拒絕；使用者確認永久刪除後不能再被同一衝突卡住。
- 重新判斷 Stryker.NET 是否適合 JET 的 harness，不沿用先前以錯誤 runner 得出的結論。

## 已確定的邊界

- Release 日誌只保存 allowlist 事件，進入有界 buffer 前就移除 SQL、參數、檔名、絕對路徑、案件名稱與
  原始 exception message。Debug 另保留既有完整 raw sink。
- 使用者按下匯出後，`.txt` 只寫到選定或目前案件目錄；不接受任意路徑，也不退到其他輸出位置。
- journal 衝突不自動猜測、覆寫、隔離或開啟；`project.load` fail closed。確認刪案則只取得案件外 lease，
  不先復原即將刪除的 journal。
- 本輪不新增案件快照／匯入功能，不讀私人資料根目錄，不執行 Git stage、commit 或 push。
- Stryker 只做暫時的單檔診斷，不改 `tools/verify.ps1`、不設 mutation 門檻、不留下工具 manifest。

## 實作結果

### 欄位配對

- `setMappingDraft` 對會影響必填鐵軌、提交資格與其他衍生區塊的變更一律 bump。
- `renderContent` 只依唯一的 `data-focus-key` 還原焦點，並統一還原 `data-preserve-scroll` 容器；舊的
  `pendingGridScroll` 與條件式 `Store.touch()` 已退役。
- RDE 文字輸入在 `input` 期間維持輕量通知，離開控制項後延遲一輪 bump，避免同步重繪搶回焦點。
- 新 GUI 情境 `mapping-required-sync` 實際按「重新配對」，清空再補回一個必填欄位；缺漏數立即
  `0 → 1 → 0`，提交資格回到原狀，兩次重繪都保留同一下拉焦點。

### 兩層診斷日誌

- Bridge 的成功與錯誤 envelope 都新增 `correlationId`；picker 保留案件、錯誤碼與 correlation，只用於
  複製錯誤及支援日誌匯出。
- `support.log.export` 在 Release／Debug 都註冊。它可按案件或「案件＋correlation」篩選安全 buffer，
  metadata 記錄程式版本、MVID、組態、.NET、Windows 與處理器架構。
- `dev.log.exportFile` 維持 Debug-only；從本次程序的 raw file sink 篩出目前案件，sink 不可讀時退回
  ring buffer。畫面明示 raw 日誌可能含案件資料。
- 兩種 `.txt` 都先在同一案件目錄寫暫存檔，完整 flush 後原子改名；取消、壞 NDJSON 或 I/O 失敗會清除
  半成品。案件目錄不存在或為 reparse point 時直接拒絕。

### journal 衝突與刪案

- 可辨識的內容衝突回穩定錯誤碼 `artifact_recovery_conflict`，並寫入安全的
  `artifact.recovery.conflict` 事件；欄位只描述 operation、manifest／stage／final 狀態、bytes 與 hash
  比對結果，不記錄實體檔名或路徑。
- `AcquireProjectDeletionLeaseAsync` 改成純粹取得案件外鎖，不讀取或更動案件內 journal。
- 新 GUI 情境 `conflicted-journal-recovery` 在 Release 可見介面完成「載入失敗 → 輸出安全支援日誌 →
  確認刪案」，並驗證輸出沒有案件 id、路徑、xlsx 檔名、衝突內容或 SQL 參數。

### Stryker.NET 判定

- Stryker.NET 4.16.0 使用 `--test-runner mtp` 時能分析 `JET.csproj`、辨識 .NET 10，並找到 3,668 項
  MTP 測試；先前「Stryker 不支援 MTP」的結論已取代。
- 它直接啟動整個測試專案，沒有經過 JET harness 的 profile 排除與環境清理；初始 run 實際碰到兩個
  未授權 PrivateCase 測試而停止。上游仍把 MTP runner 標為 preview，issue #3754 說明 mutant test
  selection 會被忽略，issue #3692 記錄固定三分鐘 RPC 上限及 `test-case-filter` 不能作為 MTP workaround。
- 結論：不安裝 Stryker，不建立專用 mutation test project，也不納入日常或正式 gate。JET 已有的
  first-red、獨立 oracle、FsCheck、provider parity、GUI 與 Excel 驗證繼續各自補強斷言；它們不冒充
  mutation score。重啟條件是上述 MTP runner 缺陷修正後，再跑同一個單檔煙霧。

## First-red 與驗證紀錄

- artifact 刪除 first-red：損壞正式檔使刪除 lease 在 recovery 階段拋出同一個 journal 錯誤；改為純鎖後，
  store 與 Application 整合案例都通過，並證明載入仍回明確衝突碼、現場未被修改。
- GUI first-red：最初情境把啟動 seed 與畫面操作競態混在一起，且曾誤把完整 demo 驗證當前置；已改成等待
  封閉 fixture trace，mapping fixture 只保留四筆 GL／TB 與必要提交。正式 `Gui` 最終四情境通過，所有
  JET 程序自行退出、暫存根清除，`privateData.pathInspected=false`。
- Stryker first-red：沙盒內部 restore 因 NuGet 不可達而失敗；本機邊界原樣重跑後越過分析與 build，最終
  由未經 harness 篩選的 PrivateCase 測試攔下。暫存工具、報告與診斷目錄已清除，`.config/` 維持空目錄。

## 完成條件與驗證結果

- 相關 `Focused` 全數通過：欄位配對前端守衛、Release／Debug 日誌匯出、安全 support provider、Bridge
  correlation、journal conflict 與刪案整合、GUI fixture 與小型 demo writer。
- `Contract` 通過；驗證框架接受四個固定 GUI 情境，仍拒絕任意 script、selector、私人路徑與未擁有的清理。
- `Gui` 四情境全數通過；新增 mapping 情境 10 個封閉動作，journal 情境 5 個封閉動作，程序與暫存根清理
  全部完成。
- `Public` 通過：3,409 total、3,402 executed／passed、0 failed、7 個登錄 skip；耗時 633.70 秒，
  `privateData.pathInspected=false`。
- `Documentation` 在最後一輪狀態更新後仍通過；文件改動段落已完整回讀。
- 未執行 live `Provider`、`PrivateCase`、原生 Excel 或公司環境驗收；這些結果不得由本輪推定。
- 2026-09-01 沒有另外授權時停在可提交狀態，不 stage、commit 或 push。2026-09-02 使用者授權：缺陷修正
  且 `Focused`、`Public`、`Documentation` 全部通過後，以明確路徑 stage、單一 commit 提交並推送一次
  `main -> origin/main`。計畫是否標為「已完成」並關閉，仍待使用者確認成果後依
  `development-workflow.md` 處理。

## 提交前複審（2026-09-01）

使用者下達「複審通過即提交」的條件授權後，另一個 session 以全量 diff 加上八個獨立審查角度
（正確性、移除行為重建、重用、簡化、效率、機敏資料與慣例、altitude）複審本輪成果：

- 機敏資料與事實保真：diff、新增檔案與文件中未發現私人路徑、案件名稱或原始例外訊息；支援日誌
  的雙層 allowlist、`InternalProjectId` 的 `JsonIgnore`、`SafeException` 只留型別與 frame 均經逐行
  確認。文件中的數字宣稱（GUI 情境數、位元組數、行數）抽查相符。
- 移除行為逐項核對：刪案 lease 不再 recovery、`pendingGridScroll` 退役、Release logger 由 no-op 改為
  allowlist provider、`dev-log-panel` 舊 UI 移除等，每一項都有對應的重建與測試，無孤兒引用。
- 發現並已修正：`docs/first-root-commit-candidate.txt` 未納入本輪 8 個新檔案（本計畫檔、3 個支援
  日誌來源檔與 4 個新測試檔）；提交後首次 `ReleaseCandidate` 會以 `candidate_manifest_mismatch`
  失敗。已按 ordinal 排序補入，清單現為 1,219 個路徑，與提交後的 `git ls-files --cached` 一致。
- 發現並記錄待修（未在複審中修改程式；已於 2026-09-02 修正，見下節）：`dev.log.exportFile` 的 ring
  buffer fallback 在「sink 檔存在但開啟或讀取失敗」時不會觸發。`ProjectLogFileWriter.WriteAsync` 把
  來源枚舉丟出的 `IOException`／`UnauthorizedAccessException` 一併包成 `JetActionException(support_log_export_failed)`
  （來源 `FileStream` 在 `await foreach` 內才惰性開啟），而 `DevLogExportFileHandler.HandleAsync`
  的 catch 濾器只認原始 `IOException or UnauthorizedAccessException or JsonException`，看不到包裝後
  型別，於是直接對使用者報錯，不退回 ring buffer；現有測試只涵蓋 `File.Exists` 為假與壞 JSON 兩條
  fallback。修正方向：把來源枚舉與目的地寫入的例外分層（例如在 handler 端先開啟 sink 串流，或讓
  `WriteAsync` 只包裝自身寫入例外），並先寫 first-red 測試（測試中以 `FileShare.None` 佔住 sink 檔，
  預期回應 `source == "ringBuffer"`）。Debug-only 診斷路徑、觸發罕見、失敗可見且不毀損資料。
- 其餘為不影響行為的品質項目（支援日誌鏈路的重複實作、GUI 情境以訊息文字斷言、logger 內寫死
  action 名稱、冗餘 catch、三項未量測的效能觀察），記錄於 `development-status.md`「已知技術債」，
  由後續 session 收斂。
- 複審驗證：`Public` 於複審 session 重跑通過（3,409 total、3,402 executed/passed、0 failed、7 個
  登錄 skip，`privateData.pathInspected=false`）；候選清單與文件修正後，`Documentation` 與
  `Contract` 重跑均通過。GUI 與 Excel 情境未於複審 session 重跑，沿用本輪稍早的正式紀錄。

## 缺陷修正（2026-09-02）

使用者裁定選項一「先修缺陷再提交」。本次只修上節記錄的 `dev.log.exportFile` fallback；複審記錄的其他
品質項目維持在 `development-status.md`「已知技術債」，不在本次範圍。

- 修正：`ProjectLogFileWriter.WriteAsync` 改以 `GetAsyncEnumerator` 手動枚舉來源，`MoveNextAsync` 拋出的
  例外標記為來源失敗後原樣拋回（仍先移除同目錄暫存），只有 writer 自身的 `IOException`／
  `UnauthorizedAccessException`／`InvalidDataException` 才包成 `JetActionException(support_log_export_failed)`。
  `DevLogExportFileHandler.HandleAsync` 的 catch 濾器不變，因此能看到原始 `IOException` 並退回 ring buffer。
  `support.log.export` 共用同一 writer，但其來源是記憶體陣列，行為不受影響。
- First-red：新測試 `DevLogExportFile_LockedSinkFallsBackToRingBufferAndRemovesPartialTemporaryFile` 先以
  `FileShare.None` 佔住 sink 檔再呼叫 action。修正前 `Focused -Filter 'JET.Tests.Application.DevLogHandlersTests'`
  失敗，唯一失敗訊息為 `JetActionException : 日誌無法寫入專案資料夾（IOException）`，堆疊指向 `WriteAsync`
  的包裝 catch；sink 的 `NdjsonFileLoggerProvider` 每筆以 `File.AppendAllText` 開閉、無常開 handle，
  所以測試能獨占該檔。
- 驗證：修正後同一 `Focused` 通過（6 executed／6 passed，含類別內 5 個既有測試）。`Public` 重跑通過：
  3,410 total、3,403 executed／passed、0 failed、7 個登錄 skip，`privateData.pathInspected=false`。總數與
  通過數都比 2026-09-01 多一個，多出來的就是這次新增的測試。`Documentation` 於本節與
  `development-status.md` 更新後重跑。
- 候選清單：本次沒有新增檔案；`docs/first-root-commit-candidate.txt` 與「已追蹤＋未追蹤」共 1,219 個路徑
  逐行一致、ordinal 排序正確，未修改。
- 未重跑 `Gui`、`Contract`、`Provider`、`PrivateCase` 或原生 Excel；本次改動只在 `DevLogHandlers.cs` 與
  `DevLogHandlersTests.cs`，不涉及前端、action 契約或驗證框架。

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

1. 第四步驗證完成後按「產生科目配對範本」，JET 把 `<案件前綴>_AccountMapping.xlsx` 寫進案件資料夾並
   顯示路徑。審計員用 Excel 開它、填 C 欄、存檔（原檔或另存都可以），再按「上傳」匯回。格式不對時
   訊息指出哪一欄；重新產生範本永遠可行。
2. 第六步匯出報告。Validation Report、INF Report、Pre-screening Report、Criteria Selection Report 每次
   覆蓋同名檔；Working Paper 每次產生 `<案件前綴>_WorkingPaper_<yyyyMMdd-HHmmss>.xlsx`，舊版留著。匯出
   中斷只會留下以 `.` 開頭的暫存檔，下次寫入前自動清掉。
3. 審計員在 JET 之外改了任何報告檔，JET 都不擋：載入照常，第六步清單在該筆旁標示「已在 JET 之外修改」
   或「檔案不存在」，重新匯出即可。
4. 用舊版 JET 鎖住的案件，裝新版後直接能開；支援日誌多一筆「已清除殘留的輸出紀錄」。

### 範圍外

科目分類 revision 衝突、案件鎖、一次一作業閘、SQL Server 線；報告內容與規則；資安強化（reparse point
鏈檢查一併移除，只保留「檔案必須在案件資料夾內」的路徑包含檢查）。

### 工作包

1. 立案文件（本節與 `development-status.md`）。
2. First-red 旅程測試 `ReportArtifactTrustJourneyTests`：範本原檔改寫後再匯出仍能載入；殘留 journal 載入
   時清除並記事件；Working Paper 兩次匯出留兩檔；其他報告覆蓋同名檔；檔案在外部被改只標示不擋；範本
   不列入報告清單。
3. 儲存層重寫：`ProjectReportArtifactStore` 只留暫存改名、manifest（artifactId、kind、fileName、
   generatedUtc、bytes、lastWriteUtc、sourceRef、stale）、`ListAsync` 附 `fileState`、`MarkStaleAsync`、
   `ResolvePathAsync`、刪案 lease；載入或寫入前清掉殘留 journal 並記 `artifact.journal.discarded`。
   契約拿掉 `Sha256`、catalog 與清理型別。
4. 科目配對範本改為工作檔：`ExportAccountMappingTemplateHandler` 直接寫案件資料夾，不進 manifest；
   `export.validationArtifacts` 只發布 Validation Report 與 INF Report。
5. 移除清理功能：handler、`ReportArtifactRetentionPolicy`、前端面板、action、樣式、對應測試。
6. 退役舊 store 測試（含 `public-skip-policy.json` 的 `FileSystemLinks` 群組），新寫最小 store 測試。
7. GUI 情境 `conflicted-journal-recovery` 改為 `edited-report-still-loads`；Excel 路線的報告種類改為五份
   報告加範本。
8. 文件：`jet-guide.md` §3／§7、`action-contract-manifest.md`、`jet-frontend-description.md`、
   `harness.md`、`tools/README.md`、候選清單。

### 驗收條件

依序 `Focused`（旅程測試先紅後綠）、`Public`（登錄 skip 變 0，總數仍高於 3,000）、`Package`、`Gui`、
`Excel`、`Contract` 與框架自身測試、`Documentation`；任一紅燈停下。提交前做 fresh context 複審，固定問
「這個關卡防的是誰」。`Provider` 與 `PrivateCase` 不在本階段。

### 進度紀錄

- 2026-09-02：立案。
- 2026-09-02 first-red：`ReportArtifactTrustJourneyTests` 六個測試在舊程式上 5 紅 1 綠。
  `AccountMappingTemplate_EditedInPlaceAndReexported_ProjectStillLoads` 的失敗訊息就是
  `artifact_recovery_conflict`，證明「填範本存回原檔再匯出」這條路徑必定鎖案，不需要外部程式介入。
  綠的那個是 `ValidationExport_Twice_OverwritesSameFileName`，它是既有行為的守衛，不是修正目標。
- 2026-09-02 實作：工作包 3 到 8 的程式與文件改完。`ProjectReportArtifactStore` 由 2,612 行改為 765 行，
  只留暫存改名、manifest、`fileState`、殘留 journal 清除與刪案 lease；`ReportArtifact` 以 `LastWriteUtc`
  與 `FileState` 取代 `Sha256`。範本改走 `ProjectWorkFileWriter`，檔名 `ProjectFileNames.AccountMappingTemplate`。
  刪掉清理 handler、`ReportArtifactRetentionPolicy`、前端清理面板與六個舊 store／清理測試檔；
  `public-skip-policy.json` 的 `FileSystemLinks` 群組整個移除（成員全在退役測試裡）。GUI 情境
  `conflicted-journal-recovery` 換成 `edited-report-still-loads`（4 個動作：開案、展開訊息面板、輸出支援
  日誌、離開）。Excel 路線的 fixture 仍產六份工作簿：五份報告加範本工作檔。
  下一動作：依驗收條件跑 `Focused`、`Public`、`Package`、`Gui`、`Excel`、`Contract`、`Documentation`。
- 2026-09-02 驗證與複審：`Focused` 第一次紅在四個架構守衛（`report.cleanupConfirm` 仍列在前端可取消
  清單、範本 handler 仍被要求走報告 plan、按鈕舊名「產生科目配對報告」、清理面板專用詞「報告識別碼」），
  都是殘留期待，修後綠。`Public` 第一次跑 3,320 執行、20 個失敗：九個是取消或失敗後留下 `.tmp`，根因
  是暫存檔在寫入成功後才登記到清理清單，寫到一半失敗就沒人刪；四個 `LocalProjectLockHandlerTests` 原本
  靠壞掉的報告清單讓載入失敗，新設計不再擋，改用唯讀 `project.json` 讓載入在取鎖後、寫回 LastOpened 時
  失敗；三個旅程測試的動作序列多了 `export.accountMappingTemplate`；兩個六份工作簿的測試改從範本路徑取
  第六份；`ReportArtifactOpenXmlParityTests` 同前；Excel 鎖住舊版 Working Paper 的測試改為「寫出新版本檔、
  舊檔不動」。沒有參與實作的 sonnet 子代理以 diff 加計畫複審：工作包全數有實作與測試，範圍外未動，關卡
  逐一標為「防 JET 自己」，沒有「防審計員操作」型；補了它指出的四處：`IsSourceStale` 的 accountMapping
  死分支、`ProjectWorkFileWriter` 的路徑包含檢查、`project-context.md` 仍寫「正式輸出範圍待確認」、
  `tools/README.md` 仍稱六份報表，並新寫 `Infrastructure/ProjectReportArtifactStoreTests`（八案）補上計畫
  承諾的最小 store 測試。第二次 `Public` 只剩四個 `LocalProjectLockHandlerTests`：唯讀 `project.json`
  讓 `File.Replace` 拋出裸 `UnauthorizedAccessException`，而不是 `JetActionException`。`JsonFileProjectStore`
  寫入失敗現在包成 `file_read_error` 並附「確認沒有唯讀或被其他程式開著後再試」；測試以 try／finally
  還原檔案屬性，避免清暫存資料夾的例外蓋掉真正的失敗原因。對應 `Focused` 通過後重跑 `Public`。
- 2026-09-02 `Public` 第三次通過：3,328 total、3,328 executed／passed、0 failed、0 skip（登錄 skip 從 7 變 0，
  總數比第一階段的 3,410 少 82，少的是退役的 store、清理與 journal 測試，多的是新寫的 16 案）。
  接著依序跑 `Contract`、框架自身測試、`Package`、`Gui`、`Excel`。
- 2026-09-02 後續路線：`Contract` 通過；`tools/tests/verify-contract.tests.ps1` 通過（391 個斷言、28 個情境，
  含 `GuiBoundary` 對新情境名稱的檢查）；`Package -Configuration Release` 通過（70／70；第一次忘了帶
  Release 組態，是用法錯誤不是失敗）；`Gui -Configuration AgentGuiTest` 四情境通過，`edited-report-still-loads`
  用掉 4 個動作，五個斷言（載入成功、支援日誌按鈕可用、日誌已寫入、日誌內容安全、舊紀錄檔已清）全為
  true，程序自行結束、暫存根已清除。`Excel -Configuration Release` 通過：五份報告加科目配對範本共六份工作簿
  都由原生 Excel 開啟、重算、另存並匯出 PDF。第二階段改為「待使用者驗收」；`Provider`、`PrivateCase`
  與公司環境實機驗收不在本階段，未執行。全部成果留在工作樹，未 stage、未提交。

### 使用者驗收清單（2026-09-04 整理，2026-09-05 使用者確認既有版本通過）

自動驗證只證明合成資料與測試主機上的行為。下面每一步都要由使用者在自己的電腦或公司測試環境操作一次，
每步寫「預期」，不符就記下實際看到的訊息。

1. 建置並啟動桌面程式：`dotnet run --project src/JET/JET/JET.csproj -c Debug`。
2. 舊案件開得起來。用公司測試環境當初撞到 `artifact_recovery_conflict` 的案件，或任一資料夾裡還留著
   `.report-artifacts.mutation-v1.json` 的舊案件，從案件清單開啟。預期：載入成功，那個檔案消失；展開右側
   訊息面板按「輸出支援日誌」，案件資料夾多一個 `JET-support-*.txt`，內容含 `artifact.journal.discarded`，
   沒有檔名、路徑或案件名稱。
3. 範本填回原檔再匯出。第四步驗證完成後按「產生科目配對範本」，畫面顯示 `<案件前綴>_AccountMapping.xlsx`
   的完整路徑；用 Excel 開、在 C 欄填分類、直接存回原檔；按「選擇科目配對檔」選同一個檔，已匯入過時
   按鈕會顯示「重新匯入科目配對檔」；再按「重新產生兩份報告」。預期：三步都成功，重新開啟案件也成功。
   這條路徑在舊版會讓整個案件無法載入。
4. Working Paper 留版本，其餘覆蓋。第六步匯出 Working Paper 兩次，資料夾出現兩個
   `<案件前綴>_WorkingPaper_<日期-時間>.xlsx`；9 月 4 日版本的畫面只顯示最新一份，9 月 5 日使用者另行
   裁定改為顯示全部版本，新增內容見本計畫最後一節。Validation Report、INF Report、
   Pre-screening Report、Criteria Selection Report 各匯出兩次，資料夾裡每種仍只有一個檔。
5. 在 JET 之外改檔只提醒不擋。用 Excel 開任一報告並存檔，回 JET 重新開啟案件，在對應步驟的報告清單中
   該筆旁出現「已在 JET 之外修改」；用檔案總管刪掉一個報告檔，重新開啟後出現「檔案不存在，重新匯出即可」；重新匯出後標示消失。
   全程不應出現紅色錯誤。
6. 舊功能已拿掉。第六步不再有「清理舊報告版本」面板；第四步報告卡片寫「兩份驗證報告」，範本另有自己的
   按鈕與說明。
7. 錯誤訊息有出路。用 Excel 開著一份 Validation Report 不關，再匯出一次驗證報告。預期：錯誤訊息說檔案被
   其他程式開著、關閉後再試，案件不受影響。Working Paper 不在此限，開著舊版也能匯出新版本。

沒有機器能代替的部分：公司正式環境的 Windows 與 Office 版本、`Provider`（SQL Server）、`PrivateCase`
（真實案件）都未執行，以上七步通過也不代表這些已驗收。

### 2026-09-04 交付時的接手紀錄

以下是當時的交付安排，已由最後的「驗收結果與後續補強（2026-09-05）」取代，不作為本次 Git 授權。

1. 先讀 `docs/development-status.md`「目前大型計畫」與本節，再用 Git 核對 branch、HEAD、遠端及工作樹。
   提交與推送的實際結果以 Git 和當次交付回報為準，不沿用先前未提交時的檔案數量。
2. 把使用者驗收清單的結果逐條記回本節；有不符的先開 first-red 測試再修。
3. 使用者已於 2026-09-04 明示授權本輪 commit 與 push，供測試環境人工驗收。交付包含 agent 指引與可讀
   文件 skill、驗證框架的失敗證據與 Stop hook、`dev.log.exportFile` 的 correlationId、第二階段報告儲存
   調整，以及本輪測試循環與鎖檔提示修正。只暫存已核對的明確路徑，以一個繁體中文 commit 提交，推送至
   `origin/main`；不使用 `git add -A` 或 `git add .`。
4. 推送後維持「待使用者驗收」，目前計畫繼續保留。只有使用者確認成果後才能依工作流程關閉計畫；
   提交或推送成功不代表人工驗收完成。
5. 使用者會在驗收結束後另行指示後續項目。交付後不自行展開已知技術債、歷史版本顯示或其他開發；
   `development-status.md` 的延後事項繼續保留。

### 測試循環更新（2026-09-04）

使用者要求依修正後的內容完善測試，避免後續驗證沿用舊邏輯。本輪更新第二階段相關測試、合成 GUI
檢查、驗證設定與文件，並修正新測試找出的鎖檔提示缺陷。不更改審計規則或 Working Paper 歷史版本的
畫面選擇。

- 完整工作簿旅程改走「填範本原檔、匯回、重開案件、重新匯出兩份驗證報告」。Excel 使用這次流程產生的
  五份報告與一份工作檔；現有類別名稱及證據環境變數為相容用途保留，方法名稱要反映五份報告。
- 補強四種一般報告的同名覆蓋、Working Paper 舊檔內容保留、外部修改與刪檔後重新匯出，以及檔案被佔用
  時的失敗與重試。既有斷言保留，原本只測 Validation Report 的案例擴成四種報告。
- `Package` 固定納入報告信任旅程和儲存層回歸測試；框架自身測試核對這些選取條件與 Excel 治具方法，
  防止後續改名或移除選取條件時，封裝仍以舊測試組合通過。
- GUI 保留四個隔離情境，補上外部修改提醒、可繼續匯出與清理面板已移除的明確斷言。檔案遺失後的重建
  由 Application 與儲存層測試覆蓋；GUI 不宣稱已驗證未操作的流程。
- 依序執行框架自身測試與相關 `Focused`，再執行正式候選需要的檢查，保留第一次失敗的證據。各命令串行
  執行，只使用合成資料；不執行 live `Provider` 或 `PrivateCase`。`ReleaseCandidate` 要求來源已提交且
  工作樹乾淨；未獲 Git 授權時先在工作樹分別執行各項檢查，不把分項通過當成完整候選通過。

第一次失敗的證據與修正：

- 框架自身測試先失敗，訊息為 `Package must include the report storage regression family *ReportArtifactTrustJourneyTests*.`。
  原因是封裝未選取這組回歸測試；現已補入兩組測試。Excel 方法改名後，第一次 `Focused` 在測試開始前
  回報 `registry_invalid`，原因是執行器的固定允許值仍用舊名；設定、允許值與框架測試已一併更新。
- `Focused -Filter 'ReportArtifact' -NoRestore` 執行 68 項，67 項通過；新加入的報告佔用重試測試失敗。
  `Focused -Filter 'ReportArtifactTrustJourneyTests.AccountMappingTemplate_LockedFile' -NoRestore` 隨後確認
  範本也有相同缺陷：`File.Move` 無法取得目的檔的獨占存取時拋出 `UnauthorizedAccessException`，原處理
  沒有把它轉成使用者可操作的提示。兩處現在只在改名發布時轉成 `file_read_error`，提示關閉開檔程式、
  確認唯讀屬性與寫入權限後重試；既有暫存清理保留，不捕捉或改寫報告內容產生器的例外。

修正後 `Focused -Filter 'ReportArtifact' -NoRestore` 通過，69 項選取測試與 298 項架構檢查全部成功。
`Focused -Filter 'SixReportWorkflowJourneyTests' -NoRestore` 通過，1 項完整旅程與 298 項架構檢查全部成功。
框架自身測試通過，399 個斷言、28 個情境。只讀複審未發現阻擋問題，另補上報告鎖檔測試的錯誤碼斷言，
和範本一樣固定要求 `file_read_error`；這個補強已在後續的 Release 公開測試與封裝驗證重新執行並通過。

`ReleaseCandidate -Configuration Release` 在建立候選前回報 `candidate_source_dirty`，未開始產品測試。
本輪延續既有未提交工作樹，沒有 Git 提交授權，因此不更改這項前置條件。接著在同一工作樹分別執行
`Contract`、`Documentation`、`Public -Configuration Release`、`Package -Configuration Release`、
`Gui -Configuration AgentGuiTest` 與 `Excel -Configuration Release`。日後取得提交授權且工作樹乾淨後，
仍須重新執行完整 `ReleaseCandidate`。

`Public -Configuration Release` 第一次在套件還原時回報 `nuget_source_unavailable`，NuGet 連線因沙盒的
通訊端權限被拒，尚未進入產品測試。保留這次紀錄後，已透過執行權限審核，在本機環境原樣重跑同一命令。

正式分項驗證結果：

- `Public -Configuration Release` 在本機環境原樣重跑通過：3,335 項全部執行並通過，0 失敗、0 跳過。
  相較 2026-09-02 增加 7 項，包括四種覆蓋行為的展開案例、修改時間判定、刪檔重建及兩種鎖檔重試。
- `Package -Configuration Release -NoRestore` 通過：91 項測試全部成功，發布內容檢查與暫存清理完成。
  封裝選取範圍已包含本輪兩組回歸測試。
- `Gui -Configuration AgentGuiTest` 四個情境通過。`edited-report-still-loads` 仍使用 4 個動作，新增的
  `modifiedOutsideVisible`、`workpaperExportEnabled`、`cleanupPanelAbsent` 均為 true，且由框架核對；
  本次 JET 程序正常離開，暫存資料夾已清除。
- `Excel -Configuration Release` 通過：使用新版旅程產生的五份報告與一份科目配對工作檔，六份全部通過
  原生 Excel 往返及 PDF 檢查，程序與暫存資料清理完成。
- `Contract`、框架自身測試與 `Documentation` 通過。文件修改段落已回讀，第一次失敗紀錄保留。
- 本輪未執行 live `Provider`、`PrivateCase` 或公司環境人工驗收，所有正式分項收據都記錄
  `privateData.pathInspected=false`。未暫存、提交或推送。

目前狀態：本輪測試循環更新完成，第二階段仍待人工驗收。Working Paper 歷史版本顯示方式仍未裁定，
其他已知延後事項維持 `development-status.md` 記錄的邊界。使用者隨後已授權提交與推送；本次交付須在
提交後重跑完整 `ReleaseCandidate`，交付完成後等待人工驗收結果，不展開後續開發。

### 提交前複審與交付（2026-09-04）

未參與實作的只讀複審核對整份工作樹、七個新增檔案、測試移除原因、前端、DEV correlation、驗證框架
與 Stop hook。只發現一項需要修正：`AuditedReportArtifactStore` 仍把 Working Paper 的舊版本計入
`replaced_count`，第二次匯出會記為取代 1 份，與舊檔保留的行為不符。

既有 `WorkpaperExport_Twice_KeepsBothVersionFiles` 加上獨立資料庫查詢，要求兩次發布紀錄的取代數都是
0。第一次 `Focused -Filter 'ReportArtifactTrustJourneyTests.WorkpaperExport_Twice' -NoRestore` 確實失敗，
第二筆預期 0、實際 1；第一次失敗的證據已保留。修正只從取代數計算排除 Working Paper，一般報告覆蓋的
計數與既有 audit 斷言保留。修正後同一個 `Focused` 通過，1 項選取測試與 298 項架構檢查全部成功；
只讀複審已確認唯一交付缺口關閉。接著提交並執行完整 `ReleaseCandidate`，全部通過後推送。

這次交付不代表人工驗收完成。推送後停止開發，等待使用者在測試環境驗收並指示下一項工作。

### 驗收結果與後續補強（2026-09-05）

- 使用者確認第二階段公司測試環境的人工驗收已通過，並要求繼續完成計畫、修正開發中發現的問題。
- 本次核對 `main` 的 HEAD 為 `9ddd8128130a526be51d7f3e423c451b98f3f748`，工作樹起始乾淨，與本機
  `origin/main` 一致。9 月 4 日提交後的 `ReleaseCandidate -Configuration Release` 收據對應同一個提交，
  Contract、Documentation、Public、Package、Gui、Excel 及清理均通過，結束碼 0。
- 使用者裁定第六步列出全部 Working Paper 版本，按產生時間由新到舊排列，保留舊版入口。歷史清單包含
  已過期版本；目前流程是否完成仍須根據現行驗證與篩選結果判斷，不能由舊版底稿冒充。
- 開發與測試維持 SQLite 和 DuckDB 範圍；不啟動 live `Provider`，不讀取 `PrivateCase` 資料。只讀審查
  可以分工，所有檔案修改與正式驗證由同一個執行者依序完成。
- 儲存審查發現 4,096 筆上限在正式檔發布後才檢查，且刪檔不會減少清單筆數。合成回歸測試與寫檔前
  檢查已完成；清單已滿時如何處理已刪檔紀錄，仍待使用者裁定。既有 4 MiB 讀取上限沒有變更。
- 下一步：由使用者確認新增版本清單的操作結果，並裁定清單已滿時是否移除已刪檔紀錄。尚未裁定前保留
  所有歷史紀錄。若有不符，先用合成資料重現再修正；新的 Git 交付仍需要另外明示授權。

本次修正與第一次失敗的證據：

- 新增範本發布前取消測試，先在原範本填分類後取消重新產生。第一次正式 Focused 證明取消後原檔位元組
  已被改寫；發布通知與最後取消檢查現已移到改名前，原檔與暫存清理斷言均保留。
- 新增 Working Paper 同名檔測試，在發布通知時模擬外部程式放入同名檔。第一次正式 Focused 證明原檔
  會被覆蓋；發布改為禁止覆蓋，錯誤提示重新匯出，重試另取版本名。另驗證鎖住舊版仍能產生新版。
- 新增 4,096 筆合成清單測試，第一次正式 Focused 證明內容 writer 已執行後才拒絕。檢查現已移到 writer
  前，失敗不產生新檔、不改清單；刪檔紀錄仍保留，已移除「刪檔就能解除上限」的不實提示。
- 報告改名後的 manifest 寫入改用 `CancellationToken.None`；發布前仍接受取消。這項由程式時序與
  [Microsoft 取消模式建議](https://devblogs.microsoft.com/premier-developer/recommended-patterns-for-cancellationtoken/)
  核對，沒有宣稱以排程競態測試實測改名後的取消時間窗。Working Paper 禁止覆蓋的 API 行為也核對了
  [File.Move 官方文件](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.move?view=net-10.0)。
- 歷史清單包含過期與缺檔版本，完成摘要仍只取現行來源。前端 upsert 改為按 artifactId 更新 Working Paper，
  一般報告仍取代同種類舊項。依新裁定更新原先禁止逐檔入口的守衛，保留禁止任意路徑、唯一案件資料夾
  入口與一般報告覆蓋的檢查。Node 診斷檢查了重複 id、同時間排序、完成隔離與 51 份紀錄的分頁；
  這是補充診斷，不取代正式 GUI。
- 新測試第一次建置少了 `JET.Application` using，補上後進入上述產品測試失敗。GUI 增加一次實際匯出後，
  第一次命令因固定允許的動作數尚未同步而回報 `registry_invalid`；設定、允許值與框架自身測試已同步。
- 兩個只讀複審分別核對前端與後端改動，未發現新的阻擋問題，也未發現不當放寬既有測試。複審未執行
  測試；正式結果以下述命令為準。

本輪正式驗證結果：

| 命令 | 結果與範圍 |
|:---|:---|
| `Focused -Filter 'ReportArtifact' -NoRestore` | 通過；73 項選取測試與 299 項架構檢查全部成功。 |
| `Gui -Configuration AgentGuiTest` | 通過；四個情境全部成功。歷史版本情境實際執行 5 個動作，新舊版本、完成判定、修改與缺檔提醒都符合預期，程序正常離開並清除暫存資料。 |
| `Public -Configuration Release -NoRestore` | 通過；3,341 項全部執行並成功，0 失敗、0 跳過。 |
| `Package -Configuration Release -NoRestore` | 通過；95 項測試全部成功，封裝內容檢查與暫存清理完成。 |
| `Excel -Configuration Release` | 通過；同輪五份合成報告與一份科目配對工作檔全部完成原生 Excel 往返及 PDF 檢查，程序與暫存清理完成。 |
| `Contract` 與 `tools/tests/verify-contract.tests.ps1` | 通過；框架自身測試最終為 418 個斷言、29 個情境，含修正後的鎖競爭與生命週期檢查。 |
| `Documentation` | 通過；34 份文件，0 error、0 warning。修改段落已回讀。 |

GUI 第一次套件還原在沙盒回報 `nuget_source_unavailable`，尚未開始畫面測試；保留收據後，經執行權限
審核在本機環境原樣重跑並通過。另一次帶 `-NoRestore` 的 GUI 呼叫被參數檢查拒絕，移除不支援的參數
後才進入正式流程。這些紀錄都不算產品測試通過。

框架自身測試的第一次失敗停在 `LockBusy`，訊息為 `Lock contender native exit code must be 2.`。
收據顯示持鎖程序於 01:42:55 UTC 結束，競爭程序於 01:42:56 UTC 才開始，兩者未重疊，因此回傳成功
不是鎖失效。測試現改由主程序呼叫正式 `Enter-JetExclusiveLock`，在競爭程序結束前持續持鎖，並在
`finally` 呼叫 `Exit-JetExclusiveLock`。原本的結束碼 2、blocked、錯誤碼、持鎖 PID 與重新取得鎖檢查
全部保留，另核對 runId 和釋放結果；原 HoldLock 子程序的生命週期檢查也獨立保留。沒有延長競爭視窗
或修改正式鎖實作，第一次失敗收據保留。

獨立 HoldLock 檢查也改讀結束後的正式收據，核對取得鎖、釋放與移除標記，避免輪詢錯過短暫標記。
即時持鎖者 PID 與 runId 由前段主程序持鎖時核對，原結束碼、空 stderr、正常完成及重新取得鎖的斷言保留。

建置成功，但仍有一筆來自未修改檔案 `PrivateCaseScenarioDiagnostics.cs:155` 的 CS8602 警告：
`GetValueOrDefault` 可能回傳 null，後續卻讀取計數欄位。本輪沒有執行私人案件診斷；這項限制記入
`development-status.md` 的技術債，不宣稱已修正。最後 `git diff --check` 通過；沒有新增受追蹤檔案，
候選清單不需增加路徑。本機 JET、Excel、sqlservr 程序均未殘留，四項本機 SQL Server 服務為停止且手動啟動。

本輪未執行 live `Provider`、`PrivateCase` 或公司環境實機驗收。新的完整 `ReleaseCandidate` 要求來源
已提交且工作樹乾淨；本輪沒有新的 Git 授權，因此以分項命令驗證，不把它們記成完整候選通過。

## 本機功能與品質補齊（2026-09-05）

使用者已確認本節方案並要求開始開發。本節取代前文的等待容量裁定、暫緩變異測試及本輪只允許單一
修改者的安排；既有歷史結果與 Git 授權不延伸到本輪。開工時 HEAD 仍為 `9ddd8128130a526be51d7f3e423c451b98f3f748`，
位於 `main`，有前輪留下的 26 份已修改檔案，沒有暫存內容。SQL Server 四項服務均停止且為手動啟動。

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

### 變異測試的範圍與完成條件

變異測試用刻意改壞的程式檢查測試是否抓得到錯誤。本輪新增需要時才執行的 `Mutation` 命令，沿用
`tools/verify.ps1`。它不成為日常開發或發行的必跑項，也不以小範圍結果代表整個 JET。

- 使用者允許採用尚未正式發行、已查核的 Stryker 原始碼
  `30005a2f52c53ac2b0de49a90145f326ea43d675`，並維護測試選取所需的小範圍修補。版本、修補理由和雜湊
  要可追溯，日後官方具備等價行為時再移除修補。上游來源只從官方儲存庫取得。
- 先驗證 `GlProjectionGuard`，再處理 `MoneyScaling`，各自固定對應公開測試類別。使用現行產品測試
  專案，不另造複製專案或降低 MTP 版本。初始測試、涵蓋範圍蒐集和每次變異都套用相同範圍；排除
  `TestProfile=PrivateCase`、`TestProfile=Provider` 與 `TestProfile=Scale`。測試身分不明、未選到預期
  測試或出現範圍外請求時停止，不退回執行全套測試。
- 工具、快取、工作樹副本與 HTML 和 JSON 報告都位於被忽略的 `artifacts/harness/mutation/`。
  不全域安裝，不上傳報告。副本包含已確認的未提交改動，不掃描私人資料根或以舊執行產物作為輸入。
- 同時一個 worker，採較低程序優先權，每次上限 30 分鐘。先以合成測試檢查篩選、取消、逾時、程序
  重啟和清理，再實跑 JET。未抓到的變異逐項確認原因；確實改變行為的就補測試，等價或範圍外的要說明。
  不為分數修改既有斷言或預期值。

### 日誌整理與執行分工

共用兩種日誌的環形緩衝邏輯、逐行輸出方法與相同的例外處理；保留各自的文字轉義、資料篩選及
Release 和 Debug 邊界。GUI 測試的可見性判斷與輪詢骨架可以共用。診斷計數的空值警告依現有每個情境
均建立計數的實作修正，以合成空結果確認零命中仍正常回報，不執行私人案件。

主代理負責前端、範本與報告保存、共用 action 回應、正式驗證及文件。子代理 A 只修改獨立的變異工具
與工具測試；子代理 B 只修改日誌與上述診斷器。共用驗證入口由主代理整合。正式驗證前完成整合，受測
檔案在驗證期間停止修改；所有建置、測試、變異、GUI 和 Excel 依序執行，最後安排未參與實作的複審。

### 驗收與目前進度

- 已完成回歸案例與產品修正、日誌整理、GUI 共用工具、新增操作案例及變異量測。公開、封裝、GUI、
  Excel、框架自身與文件檢查通過；新增畫面仍待使用者操作確認。
- 正式驗證依序執行相關 `Focused`、`Public`、`Package`、`Gui`、`Excel`、`Contract`、框架自身測試
  和 `Documentation`。第一次失敗的證據保留，修正後重跑同一項。公開結果不能冒充公司實機驗收。
- 必測：兩種配對畫面的模式同步、草稿和完整還原、缺漏定位與焦點；範本新建、保留、覆寫、佔用、
  取消與重試；清單滿額、部分刪檔、全數存在、不明檔案狀態及失敗保留；歷史分頁與舊版完成隔離。
- 使用者最後確認新增第三、四步及底稿操作。此時才依工作流程完成本輪人工驗收狀態。
- SQL Server、公司正式部署、完整 IDEA 驗收、新 KCT 來源、保存情境的重驗契約、預篩選去留及 audit log
  介面維持 `development-status.md` 中的背景與重啟條件。未量測效能、舊案相容名稱與案件鎖也不趁機改動。

### 實作與檢查進度

- 範本與底稿回歸案例首次 `Focused -Filter ReportArtifact -NoRestore` 有 5 項失敗，分別涵蓋自動保留
  範本的回應、發布時才出現的檔案，以及滿額清單的整理與失敗保留。修正後選取測試通過；畫面守衛另有
  2 項仍要求舊的模式限制及按鈕名稱，已依使用者的新裁定改成同步設定與明示覆寫，原互斥及完成訊息
  要求保留。完整 `ReportArtifact` 選取與架構檢查最後通過。
- 獨立只讀複審發現，底稿在內容產生期間被放回時會遺失清單紀錄。新增兩個合成案例，第一次正式測試
  均失敗；發布前重新核對後，還原的檔案保留，若再次滿額則在改名前拒絕。相同測試與既有保存案例通過。
- 補充 Node 診斷先重現核准日指派後模式仍是 unmapped；修正後通過來源指派、清除、模式切換、自動
  建議和完整還原的檢查，也確認還原草稿不共用已提交設定的物件。它不取代待執行的正式 GUI 操作。
- 日誌整理前的 `Diagnostic` 與 `ProjectLogFileWriterTests` 正式基準通過。日誌重複程式與診斷計數的
  空值警告已修正，新的完整公開測試仍待執行。
- 變異工具的獨立傳輸替身資格檢查最後通過 17 個斷言、21 個情境，涵蓋錯誤選取、取消與重啟。真實 OS
  的 `OwnedProcessTree` 檢查另通過 158 個斷言、6 個情境，確認本次子程序、較低優先權、正常退出、
  逾時及取消後清理。首次失敗是合成子程序未繼承輸出管線；修正治具後，原歸屬與清理斷言全部保留。
- GUI 曾重現必填欄位定位失敗，以及草稿和已提交配對共用物件造成還原依據被改寫。定位改查當前
  `data-bind="content"` 節點；草稿複製欄位對應，已提交結果複製完整設定。還原測試改用第一次編輯前
  保存的獨立快照。舊情境點擊也改為核對可見位置後再送滑鼠事件，沒有減少斷言、增加動作或延長時間。
  六個情境最後通過，52 筆歷史分頁及新增後 53 筆保留均由 GUI 實際驗證；檔案總管本身未開啟。
- 第六步匯出補上取消與原案件、驗證結果、篩選版本檢查。補充 Node 診斷先證明取消後仍會啟動底稿，
  修正後保留已完成報告並停止後續動作。正式 `PrescreenDefaultExportFrontendTests` 首次有 2 項失敗，
  原因是新增案件參數後舊字串斷言不再相符；更新參數並保留先後順序，新增逐步取消檢查後，4 項測試
  與完整架構檢查通過。
- 首次正式變異已確認 MTP 只選到 7 個公開測試，之後在未選取的 record 建構式遇到上游空值錯誤。
  原因是 Stryker 先改寫所有檔案，之後才套用檔案篩選。修補改為只將指定檔案交給原有變異器，其餘
  原始語法樹照常參與編譯。合成案例含建構式初始化中的 `out var` 才能重現該錯誤；第一次缺少它的
  資格檢查失敗已保留。修正案例後的獨立診斷通過 20 個斷言、12 個情境，6 個變異前後一致，固定
  預期值抓到兩個 AND 改 OR 的錯誤。後續完整正式 `Mutation` 也重新通過相同資格檢查。
- Stryker 工具套件出現 VisualBasic 版本不一致及 Xml 安全警告。工具專用相依項目固定為 VisualBasic
  `5.9.0`、Xml 與 Pkcs `9.0.18`；一次還原核對沒有 NuGet 警告或錯誤，其他 448 個相依項目不變。
  修補與九份鎖定檔差異留在工具目錄，正式建置仍核對鎖定檔與安全公告；JET 產品套件沒有改動。
- 工具複審依 [Microsoft 的下載逾時說明](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-10.0)
  補上涵蓋標頭與內容的共同期限，避免伺服器中途停傳後一直持鎖。依
  [Stryker 的結果定義](https://stryker-mutator.io/docs/mutation-testing-elements/mutant-states-and-metrics/)，
  未被任何測試涵蓋的 `NoCoverage` 也納入待判讀。新增案例另抓到 PowerShell 分組未正確取得字典欄位，
  分類數量被放進空白名稱的缺陷；改成明確依狀態累計。正式 `MutationBoundary` 最後通過 84 個斷言，
  包含下載停傳、慢速內容、大小限制、雜湊錯誤、清理、整體期限與混合結果計數。
- 完整 `Public -Configuration Release -NoRestore` 首次執行 3,357 項，3,349 項通過、8 項失敗、沒有跳過。
  兩項舊回應形狀尚未登錄 `disposition` 或 `reportArtifacts`；其餘六項的測試替身尚未支援發布後讀清單，
  或仍把清單讀取次數固定為命名前的一次。已保留全部舊欄位與原有呼叫順序，補上新欄位、完整清單，
  以及發布後取消仍須讀回清單的檢查。新增金額邊界測試後，完整公開命令重新執行，3,359 項全部通過，
  沒有失敗或跳過；這次產品建置為 0 warning、0 error。
- 完整框架自身測試通過 439 個斷言、32 個情境，涵蓋新命令參數限制、變異工具、程序歸屬與既有鎖、
  第一次失敗、清理、私人資料及候選內容限制。最後的六個 GUI 情境也再次通過；截圖複核後，正常的
  範本保留訊息已改成資訊樣式，只有實際失敗才用紅色提示。

### 本輪正式驗證結果

| 命令 | 結果與範圍 |
|:---|:---|
| `Mutation -Configuration Release` | GlProjectionGuard 通過；選取 7 個公開測試，6 個變異全部被測試抓到。初始、涵蓋範圍及變異執行共核對 14 次請求，3 次 server 啟動都維持同一範圍，所有本次子程序清理完成。 |
| `Mutation -Configuration Release -MutationScope MoneyScaling` | 重跑通過；選取 15 個公開測試，25 個變異中有 14 個被測試抓到、3 個由上游略過、8 個由上游判為編譯失敗。沒有 Survived 或 NoCoverage；未執行的 11 個不算測試抓到。30 次請求與 3 次 server 啟動的範圍及子程序清理均通過。 |
| `Gui -Configuration AgentGuiTest` | 六個情境通過，兩張合成截圖已人工複核。未開啟檔案總管或原生檔案對話框。 |
| `tools/tests/verify-contract.tests.ps1` | 通過，439 個斷言、32 個情境。變異邊界另為 84 個斷言，真實程序歸屬另為 158 個斷言。 |
| `Public -Configuration Release -NoRestore` | 通過，3,359 項全部成功，沒有失敗或跳過；產品建置 0 warning、0 error。 |
| `Package -Configuration Release -NoRestore` | 通過，103 項範本與報告測試全部成功；Release 單檔發布、內容核對與暫存封裝清理通過。 |
| `Excel -Configuration Release` | 通過；本輪新產生的六份合成工作簿均完成原生 Excel 開啟、重算、另存、重開及 PDF 檢查，來源未變，公式錯誤未增加，本次程序與暫存目錄已清理。 |
| `Documentation` | 通過，34 份文件，0 error、0 warning；修改段落已回讀並核對來源、數字及保留條件。 |

MoneyScaling 首次量測有一個未被抓到的錯誤：把超過 `long.MaxValue` 才拒絕改成等於上限也拒絕，
原來的測試仍通過。本輪新增兩項測試，以 12 個固定案例涵蓋正負上下界、界外值及四捨五入邊界，
預期值不由被測方法反算。`Focused -Filter MoneyScalingTests -Configuration Release -NoRestore`
的 15 項測試與 300 項架構檢查通過，再重跑同一變異命令，原本存活的 ID 21 已被抓到。

略過的 ID 0、5、9 都來自上游「區塊內已有其他變異」的過濾規則；它們不能解讀成這三個區塊刪除
版本已被測試抓到。ID 11、12、14、16、17、18、19、22 的原始狀態為 CompileError，意思是批次編譯
回復程序移除了它們，並非逐個獨立編譯所得的判斷。補充診斷已逐一獨立編譯這八個替換，先確認原始
函式通過全部 18 個固定案例。結果如下；原始 Stryker 報告與分類不改寫，也不重新計算分數。

| ID | 獨立編譯與固定案例的結果 |
|:---|:---|
| 11 | 方法缺少回傳及 out 參數賦值，回報 CS0161、CS0177，無法執行。 |
| 12、14 | `rounded` 有未賦值路徑，回報 CS0165，無法執行。 |
| 16、17、18、19、22 | 都可獨立編譯；18 個案例分別觀察到 6、17、11、3、6 個行為差異，並非等價替換。 |

最後五項屬於本輪上游批次編譯的量測限制。補充診斷證明固定案例可以區分錯誤，但沒有替代完整 MTP
測試執行，因此不標為 Stryker Killed。正式工具沒有修改一般變異器或其分類；未來上游改善編譯回復時，
再依相同版本與資格檢查更換工具，重跑這兩個固定範圍。這份診斷只讀公開來源與原始報告，程序收尾通過。

### 收尾與下一步

候選來源清單已核對 1,235 個檔案，和既有追蹤檔案及本輪已確認的新檔完全一致，沒有重複或漏列。
工作樹保留所有修改；branch 仍為 `main`，HEAD 仍為 `9ddd8128130a526be51d7f3e423c451b98f3f748`，
沒有暫存、提交或推送。`git diff --check` 通過。JET、測試與 Excel 程序沒有殘留；四項本機 SQL Server
服務均為停止且手動啟動，沒有 `sqlservr.exe`。變異工具的快取、來源副本與有限的報告保留在被忽略的
本機目錄供追查，不能把「程序已清理」誤寫成這些證據檔都已刪除。

本輪未執行 live `Provider`、`PrivateCase`、公司環境的新增功能驗收或新的完整 `ReleaseCandidate`。
後者需要已提交且乾淨的來源；本輪沒有新的 Git 授權，因此分項通過不記成候選通過。

下一步請使用者確認三件操作：第三步切換核准日來源及還原配對是否自然；第四步重新驗證能否保留已填
範本，手動重新產生的覆寫提示是否清楚；第六步能否找到並開啟需要的舊版底稿。這些新增操作確認後，
才把本計畫標為已完成；若有問題，沿用本計畫修正，不另建一份同範圍待辦。

既有八項延後工作已逐項回讀，背景、目前邊界與重啟條件仍保存在 `development-status.md`：SQL Server
live 驗證、企業多人環境、預篩選去留、audit log 查詢與保留政策、KCT 保存情境重驗、公司正式部署、
KCT 新條件來源及 IDEA 完整人工驗收。它們沒有因這次本機檢查通過而自動完成或擴大授權。

## 附錄：2026-09-05 另一輪篩選工作的紀錄（已由 2026-09-07 計畫取代）

以下段落是另一個 agent 在 2026-09-05 到 06 插入本檔的內容，逐字保留作為歷史與第一次失敗的證據；其中的需求敘述、
「待判定」與「排除區域」設計已由 [`2026-09-07-filter-convergence-plan.md`](2026-09-07-filter-convergence-plan.md)
的裁定取代，不再是現行規格。

### 本次執行範圍與已確認事項

使用者於 2026-09-05 回報，審計員已確認「編輯底稿後，專案可以正常重新載入與刪除」及「欄位配對的
必填欄位檢查，實際操作結果可接受」兩項修正驗收通過。這是操作驗收，不擴大為全部 IDEA 功能已完成。
前輪其他新增操作仍依各自紀錄等待驗收，變異工具的五項量測限制維持原有邊界。

#### 尚待完成的部分

- 待判定的交叉案例仍需使用者裁定：貸方只有未分類科目，同時要求「存在貸方非 Cash」及「貸方完全
  沒有 Cash」時，補分類可能改變兩個條件。目前存在條件要求已分類科目，因此不命中，也不列待判定。
  已向使用者提出是否應改列待判定；尚未收到回覆，未擅自更改這個交叉行為。
- 目前程式的公開測試、封裝、GUI、Excel、Contract 和文件檢查已通過。2026-09-06 已核對
  分支及 HEAD 未變，也沒有暫存內容。待判定案例若需改動，再針對改動重新驗證。
- 新增功能仍待審計員操作驗收。下方案例和本機合成檔可供核對，前輪兩項已通過的驗收保持原紀錄。

#### 審計員會怎麼操作

第五步可自由組合借方或貸方的指定分類、其他已分類科目，以及整側沒有指定分類。日期可用多選日曆、
逐行輸入或貼上選定，包含跨月日期，並可指定包含或排除。文字增加清單排除、開頭與結尾符合、空白
判斷；金額增加清單與區間外比較，明示是否比較絕對值。設定後按預覽才查詢，畫面以傳票清單呈現，
可分頁展開完整分錄並區分命中、參考及排除原因。已保存情境可編輯、更新、另存副本或取消編輯。

主要條件決定命中列，其他同傳票條件可由不同分錄佐證；畫面明示並允許更換主要條件，儲存時仍放在
組內第一順位。只有傳票層條件時呈現該傳票查核期間內分錄，註明「傳票條件成立」。同傳票仍只用全部
符合的組合，列層與組間沿用既有結合方式。

非 Cash 有兩種不同需求：「有其他已分類科目」允許同側仍有 Cash；「整側沒有 Cash」不允許。
未分類不算非 Cash。新的整側沒有條件，若不見指定分類但仍有未分類科目，先列待判定，提供補分類
入口；已見指定分類則不符合。待判定只計入仍可能因補分類而改變整個情境結果的傳票，不重複計數。
實作中查明舊匯入會將分類留白轉成 Others，使用者再次裁定：新條件把這種留白視為未分類，舊情境
仍維持原結果。因此 schema v10 增加分類是否實際填寫的紀錄，從原始分類列分批回填，原分類值不改。
缺少原始紀錄時不猜測，審計員可重新匯入已填好分類的範本；一般文字或日期條件仍可使用。

獨立排除區域預設只移除命中列，也可移除整張傳票；多項排除採符合任一項即排除。排除不改寫同傳票
判斷所依據的查核期間資料，避免排除某天後把有 Cash 的傳票誤判成沒有 Cash。完整傳票可顯示被排除
的參考列，但不計入命中數。只有排除條件時明示「查核期間全部資料，扣除以下項目」，空白情境不能保存。

新排除條件預設保留空白並提供選項；舊情境缺少新設定時維持原有規則，包括負面、空白與未分類行為。
舊情境的開啟、編輯與另存不得自動改變結果。日期及金額區間包含兩端，起訖倒置提示修正；清單最多
100 個不同值，每行一值，保留文字前置零，不丟棄無效輸入。日期不經時區轉換。

#### 其他步驟及實作界線

- 建立案件統一期末財報準備日期名稱及用途說明；匯入清楚區分加入來源與取代資料，說明影響的配對及
  結果，也明示已保存情境和既有底稿保留。
- 欄位配對區分草稿與確認狀態；驗證區分資料檢查、報表與科目分類，各缺項提供可操作的下一步。
- 第六步明示所選情境與結果版本，保留全部底稿歷史；條件說明、命中數與輸出共用後端判斷。
- 將「儲存並結束」改為「結束 JET」，提示未保存草稿不會留到下次開啟。不加自動保存或確認關卡。
- 沿用參數化集合式 SQL、金額精度、零元借貸歸屬與查核期間。前端只接摘要及分頁。
- 新增 `query.filterVoucherPage` 與 `query.filterVoucherRowsPage`，支援草稿或指定版本情境；游標綁定
  專案、資料版本與條件。原 `filter.preview`、`filter.commit` 欄位意義維持，補待判定資訊。
- SQLite 與 DuckDB 以合成資料循序驗證；不讀私人案件，不跑 live SQL Server，不新增 KCT 或 AI 規則。

參考資料：[Power Query 條件類型](https://learn.microsoft.com/en-us/power-query/filter-values)、
[Carbon 批次篩選](https://carbondesignsystem.com/patterns/filtering/)、
[W3C 日期選擇器鍵盤操作](https://www.w3.org/WAI/ARIA/apg/patterns/dialog-modal/examples/datepicker-dialog/)。
W3C 範例為單選，JET 須另完成多選、焦點與 WebView2 操作驗證。

#### 執行順序與驗收

1. 保存裁定及 legacy 對照；寫出固定合成案例的預期命中與待判定傳票。
2. 完成條件驗證、編譯、預覽與保存的一致性，再加入傳票及明細分頁。
3. 完成多選日曆、排除、主要條件、情境編輯及完整結果呈現，再修正其他步驟。
4. 指定測試確認兩個本機資料庫結果一致，再跑 Public、Contract、Package、Gui、Excel 和 Documentation。
5. 複審需求與差異，保存首次失敗及最後有效驗證。新增操作標為待審計員驗收，提供可直接照做的案例。

案例至少涵蓋混合 Cash、未分類、自訂分類的相同業務意義、四側自由組合、主要條件切換、待判定的
組合、日期跨月跨年與閏日、空白、逐列及整票排除、文字特殊字元及前置零、金額正負與邊界，並確認
舊情境不變、情境更新失敗保留原版、重新匯入後結果失效、命中列和參考列分開，以及前輪兩項修正沒有退步。

實作與修正經過：新條件、傳票分頁、情境編輯及多選日曆完成後，整合驗證曾發現以下問題。
複查發現保存摘要漏回排除設定，已先留下兩個失敗測試再修正；原始定義本身仍有保留。
`20260905-125639668-af3fb874969a42ac8b8aa4269b6d1e16` 保留這次失敗。
後續 `20260905-130058698-5c5034f69b8c43b4bb760f0e7c04c92f` 的架構檢查要求舊版兩個分類陣列名稱，
新借貸條件增加第三個 `categoryIds`。因此更新精確預期字串，繼續要求三種已存分類引用都受到保護。
第一次正式 `Focused -Configuration Release -Filter FilterCompletenessTests -NoRestore` 的 build 完成，
但前置架構檢查發現新增兩種條件尚未同步中文標籤，共 1 項失敗，指定案例未執行。已補上 Domain 與
前端標籤，保留收據 `20260905-111316603-888b83d7bc3e4df7b06aea31c064a295`；原命令後續通過。
加入傳票分頁與 schema v10 回填案例後，`20260905-115657085-c320f93b6ea94ac69cd492c0f8ed7786`
發現 DuckDB 的加總回傳 BigInteger，分頁讀取不能直接轉成 long。已將列數加總明確轉為 BIGINT，
維持兩個本機資料庫的同一份 SQL；原測試的固定預期結果未改，同一命令重跑後通過。
完整公開測試 `20260905-120304305-8c984447ea0640e1a41c30b2962d4bff` 有 26 項失敗：21 項仍預期
schema v9，4 項固定舊介面文字或只新增情境的程式寫法，1 項回應欄位清單尚未包含 `pendingVoucherCount`。
本輪新增的條件、分頁及分類留白升級案例通過。以下既有測試需要同步：升級後版本改為固定的 10，保留
原始舊版輸入及所有資料保留斷言；回應清單增加待判定欄位並驗證零值；保存路徑同時核對新增與更新都
使用共用投影；金額空白條件仍帶後端要求的 amountBasis；匯出文字改成使用者能理解的同傳票參考說明。
這些調整反映本次已核准行為，不刪除或放寬既有業務斷言。
公開測試第二次執行 3,384 項，3,382 項通過，兩項仍斷言底稿計畫不得有 `ConditionLogic` 欄位。
收據 `20260905-135626492-d3a5c00f10ad4ddca503b17f967a399c` 已保留。本次要求底稿完整說明新條件，
因此改為檢查舊情境的該欄值仍為 null，新增情境另以實際工作簿驗證文字，不取消原有計數與來源檢查。
複查也確認 Excel 單格最多 32,767 字元，長條件須接續寫入後面的列，不截斷內容；上限來源為
[Microsoft Excel 規格](https://support.microsoft.com/en-US/Excel/excel-specifications-and-limits)。
長條件測試先在 `20260905-141308819-6d3a0c1bf39b474ab76340a103fa686a` 重現單格超長而不能開啟。
分段後的 `20260905-141608778-e796e09bc88e4665ae760864c4184fc0` 又找到 Step 3 範本覆寫只接受
第 19 至 28 列的舊限制，續列因此遺失；已讓同一張工作表接續列出完整條件。原範本第 29 列沒有內容，
後面也沒有資料，兩份範本均無固定列印範圍。隨附範本檔不修改，舊情境仍維持原樣；長文字另測中文、
表情符號及換行分段後可完整接回。
相容性複查發現舊摘要會把小寫 or 誤寫成 AND，也會把第一條本來不參與組合的 join 算入說明。
新增三個固定預期案例後，`20260905-143618507-68905a4b364f4d9fbdbe5e6898087229` 全部重現失敗。
後端既有判斷不變，畫面及報告改依同一個實際順序說明。原先固定錯誤說明的兩個測試一併改成正確
布林式，保留原輸入；SQLite 和 DuckDB 另核對相同舊條件仍分別命中零列及全部十二列。
同一輪的 `20260905-144624562-1c53254e2e5a4f6da56416e2e8226a6b` 也重現非營業日摘要移過 OR
造成說明變意的情況。摘要現在只在不改變 AND 組合的前提下移到句尾，其他情況保持原位置與括號。
完整公開測試 `20260905-145001000-712223adc2654616a4775c4745018f0a` 的 3,390 項全部通過。
之後另新增排除空白預設的案例，`20260905-150726143-7a5d8d2f536948e4881b8193204eb2c7` 在兩個
本機資料庫都重現預期 11 列卻只有 10 列：未帶空白選項時，負面排除多移除了一列空白。
編譯、畫面與報告現已統一為排除區域省略選項時保留空白；主條件的負面比較仍保留原本行為。
`20260905-165231816-3333c4ea51a64ef5841783e0152ef7da` 的 `Focused -Filter FilterCompleteness`
已重新通過。其後依目前修正重新執行各項檢查，結果列在下方驗證表。
複查發現保存摘要尚未帶回新增的排除條件，會在重新載入或編輯時遺失。先新增原樣往返測試，
`20260905-125639668-af3fb874969a42ac8b8aa4269b6d1e16` 在兩個本機資料庫都因缺少 exclusions 失敗，
再修正共用摘要；沒有 groups 的純排除情境也回傳空陣列。底稿同步加入新條件完整說明；舊的整張傳票
標記例外遇到排除條件時改採實際命中列，避免被排除的列在底稿重新被標記。
GUI 原沙盒還原因 NuGet 連線受阻；相同命令獲准用本機網路權限重跑後，前六個情境通過。
新增情境因設定 360 秒超過驅動程式原有 240 秒上限而未啟動，收據為
`20260905-124851331-b76d1b73e3d84d53a0c752ef1b76de13`；已改回 240 秒，不擴張驅動程式上限。
其後確認新增情境需要兩張合成截圖，已在固定情境範圍內同步上限。示範資料原本只有貸方 Cash，
GUI 因而改成實際切換借貸選項，仍要求有命中及參考分錄，不把零命中當通過。
`20260905-134406737-bfe9b701d984425abea31c4c56e0c557` 的七個 GUI 情境通過，篩選情境共 32 個
操作，並保存傳票與跨月日曆兩張截圖；之後的介面修正也已重跑，結果列在下方驗證表。

#### 審計員可操作的合成案例

本機 `artifacts/acceptance/filter-completeness/` 已備有 GL.csv、TB.csv、AccountMapping.csv 和操作說明。
共十二筆分錄，查核期間為 2025-01-01 至 2025-12-31；全部是合成資料。GL 使用含正負號金額，TB
使用期初及期末餘額，科目配對表的 999 分類刻意留白。程式測試的固定資料另保存在
`FilterCompletenessTests.cs`，不依賴這份本機產物存續。

| 傳票 | 刻意安排的情況 | 主要預期 |
|:---|:---|:---|
| A | 第 1 列借 Cash，第 2 列貸 Others | 有貸方非 Cash，也完全沒有貸方 Cash |
| B | 第 1 列借 Cash、第 2 列貸 Others、第 3 列借 Others、第 4 列貸 Cash | 四種條件同時成立，不能說貸方沒有 Cash |
| C | 借 Cash，貸方分類留白 | 不算有已分類的貸方非 Cash；貸方沒有 Cash 先待判定 |
| D | 借 Cash，貸方混有 Cash 及未分類 | 已有貸方 Cash，直接不符合完全沒有 Cash |
| E | 只有零元的借方 Cash | 沿用零元歸借方；沒有貸方也符合沒有貸方 Cash |

1. 第五步按「借 Cash，且同傳票有貸非 Cash」，應看到 A、B，各命中第 1 列。
2. 改成貸方「整個借貸側完全沒有指定分類」，應看到 A、E，各 1 列，另有 C 一張待判定。
3. 用「同傳票同時有四種借貸條件」只會看到 B。把貸方非 Cash 設成主要條件，命中改為第 2 列。
4. 選總帳日期 2025-02-01 和 2025-03-01，應有七筆分錄、四張傳票。核准日期 2024-02-29 只有 A
   第 1 列；2025-02-29 則應要求修正。
5. 排除 2025-03-01 不會讓 B、D 變成沒有貸方 Cash。只選借方 Cash 再排除 2025-02-01，逐列排除
   仍有五列；整票排除只剩 E。
6. 金額含正負號的清單 -50 和 0 應有四列；絕對值在 1 至 100 之外只剩 E。科目代號 001 有六列，
   輸入 1 沒有命中。文字特殊符號按字面比對，前置零保留。
7. 編輯含排除日期的情境，更新不增加名額，另存副本才增加；移除日期後取消，再開啟應仍有原設定。
   重開專案後設定仍在。匯出 Step 3 的說明及 Step 4 的命中標記應與預覽一致，舊底稿版本仍可開啟。
8. 在第四步新增具有現金意義的「零用金」分類，再把科目 003 配成這個分類，重新預覽時 E 仍應算 Cash。

#### 2026-09-06 目前版本的驗證結果

| 檢查 | 實際結果 | 收據或證據識別 |
|:---|:---|:---|
| `Public -Configuration Release -NoRestore` | 3,392 項執行及通過，沒有失敗或略過 | `20260905-170117778-2749e98d4f88473bbdcab55224896994` |
| `Package -Configuration Release -NoRestore` | 103 項測試通過，封裝檢查通過 | `20260905-171255481-14318cec06c847fe9b678020e1769b47` |
| `Gui -Configuration AgentGuiTest` | 七個情境通過，包含跨月日曆、預覽過期、更新、副本及取消 | `20260905-171609948-9b443d397ce34543a7cee26dc875a3ec` |
| `Excel -Configuration Release` | 五份合成報告及一份科目配對工作檔通過原生 Excel 檢查 | `20260905-172120886-b30ab2f10783412ab57179e975cf2b0e` |
| `Contract` | 通過 | `20260905-172559397-b00b03e28e9c48c0b6017d90740d8b7c` |
| 驗證框架自身測試 | 439 個斷言、32 個情境通過 | `20260905-172602176-55ce601684d54de1bedd85ec1943e472` |
| `Documentation` | 34 份文件通過，0 error、0 warning；修改段落已回讀 | `20260905-174016487-757a91f1cfda44c68d2a7376af2f0817` |

三份新增或重點修改的篩選 JavaScript 已做語法檢查。GUI 的傳票及多選日曆截圖已檢視。候選清單
補入本次新檔，目前共 1,249 個路徑。以上使用合成資料，未執行 live SQL Server、私人案件或本輪
審計員操作驗收；沒有暫存、提交或推送，也沒有把分項檢查稱為新的完整 ReleaseCandidate。
收尾時確認沒有 JET、JET.Tests、Excel 或 sqlservr 程序；SQL Server 相關服務均已停止。
