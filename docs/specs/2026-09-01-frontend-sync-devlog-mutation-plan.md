# 欄位配對同步、支援日誌與損壞案件復原計畫

更新日期：2026-09-04

狀態：待使用者驗收（第二階段）

目前步驟：第一階段（前端同步、支援日誌、journal 衝突刪案、Stryker 判定）已於 2026-09-02 以
commit `641e3f0` 提交並推送，使用者確認沒問題。同日在公司測試環境撞到 `artifact_recovery_conflict`，
使用者裁定接著在同一份計畫做第二階段：拆解報告產物儲存、科目配對範本改為工作檔、Working Paper 改成
版本檔、移除清理功能。第二階段已於 2026-09-02 實作完成，`Focused`、`Public`、`Contract`、框架自身測試、
`Package`、`Gui`、`Excel`、`Documentation` 全部通過，成果留在工作樹未提交。2026-09-04 使用者要求依第二階段
的新行為補齊測試循環，回歸測試與正式命令的選取範圍已更新，正式分項驗證已通過；下一步仍為使用者
人工驗收。2026-09-04 使用者已授權將本輪成果提交並推送，供測試環境驗收；提交後須在乾淨工作樹執行
完整 `ReleaseCandidate`。交付後維持「待使用者驗收」，等使用者回報結果並指示後續項目。

## 目標

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

### 使用者驗收清單（2026-09-04 整理，待做）

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
   `<案件前綴>_WorkingPaper_<日期-時間>.xlsx`；畫面目前只會顯示最新那一份（已知落差，見
   `development-status.md`「已知技術債」，要不要顯示全部版本請一併裁定）；Validation Report、INF Report、
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

### 下一個 session 的接手順序

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
