# 欄位配對同步、支援日誌與損壞案件復原計畫

更新日期：2026-09-02  
狀態：待使用者驗收  
目前步驟：使用者 2026-09-02 裁定選項一「先修缺陷再提交」。提交前複審記錄的 `dev.log.exportFile`
fallback 缺陷已以 first-red 測試修正並重新驗證（見「缺陷修正（2026-09-02）」）；依同一授權，整輪成果
以明確路徑 stage、單一 commit 提交並推送一次 `main -> origin/main`。Commit 與 push 是否實際完成，以
當時的 Git 與遠端為準

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
  3,410 total、3,403 executed／passed、0 failed、7 個登錄 skip（較 2026-09-01 各多 1，即新測試），
  `privateData.pathInspected=false`。`Documentation` 於本節與 `development-status.md` 更新後重跑。
- 候選清單：本次沒有新增檔案；`docs/first-root-commit-candidate.txt` 與「已追蹤＋未追蹤」共 1,219 個路徑
  逐行一致、ordinal 排序正確，未修改。
- 未重跑 `Gui`、`Contract`、`Provider`、`PrivateCase` 或原生 Excel；本次改動只在 `DevLogHandlers.cs` 與
  `DevLogHandlersTests.cs`，不涉及前端、action 契約或驗證框架。
