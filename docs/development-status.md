# JET 開發現況

更新日期：2026-09-04

## 目前狀態

`je-tool` 已整理成新儲存庫的根內容。產品程式、參考工作簿、現行文件、legacy 背景與新的驗證框架都在
本目錄內；`je-testing` 與 `new-je-tool` 不再是執行或驗證依賴，來源 Git 歷史也不會匯入。Branch、HEAD 與
遠端同步狀態屬於會變動的執行狀態，接手時應直接查 Git，不能只依這份文件判斷。

驗證框架 Phase 1 至 Phase 7 已完成。公開產品測試、provider、Release 封裝、四個隔離 GUI 情境、六份合成
報表的原生 Excel 檢查，以及先前明示授權的 SQLite／SQL Server 私人案件都曾分別通過。這些結果只能描述
當次執行；候選內容變動後，仍要依變更範圍重跑正式命令。

## 目前大型計畫

[`specs/2026-09-01-frontend-sync-devlog-mutation-plan.md`](specs/2026-09-01-frontend-sync-devlog-mutation-plan.md)：
本輪把 Claude 已做的前端與 DEV 日誌修改，和使用者回報的報告 journal 衝突一起收斂。欄位配對改成
衍生畫面寫入必定 bump，並由框架層還原焦點與捲動；Release 新增可從 picker 使用的去識別支援日誌，
Debug 原始日誌則只匯出目前案件；損壞 journal 仍拒絕載入，但確認刪案不再先做 recovery。四個 GUI
情境已實跑通過，包含必填欄位往返，以及損壞案件從顯示錯誤、輸出支援日誌到確認刪除的整段流程。
Stryker.NET 4.16.0 能以 MTP 分析 .NET 10，但會繞過 JET harness 的 PrivateCase 篩選，且上游 MTP preview
有測試選取失效與固定逾時缺陷，因此不安裝、不列入正式 gate。本輪 `Contract`、`Documentation`、`Public`
（3,402／3,402 executed passed）與四個 `Gui` 情境均通過。2026-09-02 依使用者裁定先修正提交前複審記錄
的 `dev.log.exportFile` fallback 缺陷（first-red 測試加上 `ProjectLogFileWriter` 例外分層），相關 `Focused`
重跑通過後，依同一授權以單一 commit 提交並推送整輪成果；使用者確認第一階段沒問題。同日公司測試環境
撞到 `artifact_recovery_conflict`，追出科目配對範本與報告產物儲存的假設互相矛盾，使用者裁定在同一份
計畫進行第二階段：儲存只留暫存改名、範本改為工作檔、Working Paper 改成版本檔、移除清理功能。
第二階段同日實作完成：`Focused`、`Public`（3,328／3,328 executed passed、0 skip）、`Contract`、框架自身
測試、`Package`、四個 `Gui` 情境（含新的 `edited-report-still-loads`）、`Excel` 與 `Documentation` 全部
通過；`Provider`、`PrivateCase` 與公司環境實機驗收未執行。狀態：第二階段待使用者驗收，成果未提交。

2026-09-04 接手後依使用者要求更新測試循環：封裝納入報告儲存回歸測試，Excel 輸入改由範本原檔往返
並重新匯出驗證報告的旅程產生，GUI 明確檢查外部修改提醒、匯出按鈕與清理面板移除。新鎖檔測試發現
報告與範本改名發布時會漏出原始存取例外，已補上關閉檔案及檢查權限後重試的提示。Release 公開測試
3,335 項、封裝測試 91 項、四個 GUI 情境與六份工作簿的原生 Excel 檢查全部通過，框架自身測試為
399 個斷言、28 個情境。第一次 NuGet 連線受阻與修正過程記在同一份計畫。完整 `ReleaseCandidate` 因
工作樹未提交而停在 `candidate_source_dirty`。使用者隨後已授權將本輪成果提交並推送，供測試環境人工
驗收；本次交付須在提交後重跑完整候選驗證。計畫保持待使用者驗收，Working Paper 歷史版本顯示方式仍
待確認。交付後等使用者回報驗收結果並指示後續項目，不自行展開其他開發。

同日提交前複審另發現 Working Paper 的稽核紀錄仍把舊版本算成已取代。新斷言先證明第二次匯出的
`replaced_count` 錯為 1，修正為排除 Working Paper 後，對應 `Focused` 與 298 項架構檢查通過；
一般報告覆蓋的計數保留，完整公開測試會在提交後的候選驗證重跑。

前一項計畫
[`specs/2026-08-30-repository-consolidation-plan.md`](specs/2026-08-30-repository-consolidation-plan.md)
已完成：歷史文件依時間軸重組、過時事實修正、Claude Code 攔截層與 hook、`AGENTS.md`／`README.md`
改寫都已實作並驗證，使用者已於 2026-08-30 驗證並授權提交，改動已進 Git（2026-08-31 session 開始時
工作樹乾淨）。該計畫檔記載的收尾動作「提交後執行完整 `ReleaseCandidate`」在本文件沒有執行紀錄，
重啟大型驗證前先確認或補跑。

## 目前產品與方向

- 產品位於 `src/JET/`，以 C#、WinForms、WebView2 及 SQLite／DuckDB／SQL Server 實作。
- `data/` 的十份工作簿是使用者裁定保留的業務範例；遷移期間沒有重新儲存或改寫內容。
- 目前先完成 GA 對 CaseWare IDEA JET 的本機替代範圍；既有 KCT 行為保留，新的 KCT 條件稍後處理。
- 正式環境不能假設有系統管理員權限或 AI 網路服務。SQLite／DuckDB 本機作業優先，SQL Server 中心化
  方向等公司權限、網路、身分與維運責任確認後再啟動。
- 完整背景見 [`project-context.md`](project-context.md)，IDEA 完成條件見
  [`idea-replacement-scope.md`](idea-replacement-scope.md)。

## 驗證基準

- 完整驗證框架與命令責任見 [`harness.md`](harness.md)。正式測試只從 `tools/verify.ps1` 進入。
- 調整 SQL Server 邊界後的 `ReleaseCandidate` 已在沒有 `JET_SQLSERVER_CONNECTION` 的 fresh rerun 通過。
  第一次根提交候選為 1,195 個路徑，和 Git 尚未忽略的檔案完全一致；來源 Git index 前後都是 0 個 entries，
  快照與各子命令清理均完成。
- `artifacts/` 是 ignored 的本機執行證據，不是換機或 fresh clone 後的專案記憶。現行計畫只保存命令、
  結果、適用範圍與第一次失敗摘要，不累積每一次本機路徑。
- 本輪 `Documentation` 已檢查 21 份文件與轉接檔，結果為 0 error、0 warning；一般 `Contract` 已通過。
  驗證框架自己的 372 項 assertions、25 個情境全部通過，包含 Provider 缺少連線時維持 `blocked`、
  Documentation 分級、Agent 指向及隔離 Git repo 的 source-index fingerprint 測試。
- 2026-08-30 依目前產品順序修正驗證邊界：`ReleaseCandidate` 不再要求 live SQL Server，固定執行 Contract、
  Documentation、Public、Package、Gui 與 Excel；完整 `Provider` 保留為日後明示執行的 SQL Server 相容性
  驗證。完整候選結果為 Public 3,390 total／3,383 passed／7 個登錄 skip、Package 70／70、兩個 GUI 情境及
  六份 Excel 往返全數通過。第一次執行只因沙盒無法連 NuGet 而 `blocked`，獲准在本機 Windows 邊界原樣
  fresh rerun 後通過；兩次均未連線、查看私人資料、stage、commit 或 push。

## 已知但延後的事項

這些事項不阻擋目前遷移，也不能因當下沒有處理就從後續進度整理中消失。每次大型工作結束，或使用者詢問
尚未完成的事情時，應逐項回報目前狀態、延後原因與重啟條件；已解決的項目才從表中移除。

| 事項 | 背景與目前邊界 | 何時重啟 |
|:---|:---|:---|
| SQL Server live `Provider` 驗證 | 2026-09-02 使用者確認 SQL Server 開發暫緩，日常開發與測試集中在 SQLite 和 DuckDB。既有 SQL Server 相容性保留；一般 `ReleaseCandidate` 不要求 `JET_SQLSERVER_CONNECTION`，也不能代表 live SQL Server 已通過。本機服務平時關閉，使用後依 [`tools/README.md`](../tools/README.md#日常資料庫測試與服務收尾) 完成收尾。公司端權限、網路、身分及維運方式仍未定。 | 使用者當次明示要驗證本機 SQL Server，且已準備專用 `JET_Test`、最低必要權限及只存在於當次程序的連線資訊時，完整執行 `Provider`；若沙盒阻擋，保留 first-red 後在獲准的本機 Windows 邊界原樣 fresh rerun。成功、失敗或中止後都要關閉本機服務，不把 live 驗證列入日常例行工作。 |
| SQL Server 企業多人環境 | 四個已確認的多人安全缺口（真實身分驗證、serverOnly 刪除、noAccess metadata 隱藏、SQL Server 2022 版本硬閘）與多人共用案件、使用鎖、容量資訊的驗收範圍，完整記錄在 [`sqlserver-enterprise-deferred.md`](sqlserver-enterprise-deferred.md)。只記錄、不推測實作。 | 公司能提供 SQL Server 2022、至少兩個真實帳號、DBA 支援與已核定的授權政策時，依該文件另立短期驗收計畫。 |
| 預篩選的後續收斂 | 預篩選規則目錄已於 2026-08-20 凍結，逐筆命中改為輔助訊號，Pre-screening Report 改為純可選輸出（匯出面默認勾選）。該報告的長期去留（縮編或退役）與預篩選條件的最終棄用清單，當時裁定留待另場收斂。 | 使用者要求收斂 Pre-screening Report 去留或條件棄用清單時另立計畫；在此之前不新增逐筆預篩選規則。 |
| audit log 查詢介面、匯出與保留政策 | 本機案件的最小 audit log 已落地（schema v9 資料庫層 append-only；DuckDB 因引擎沒有 trigger 維持程式紀律）。查詢介面、匯出、保留政策與企業部署稽核當時裁定另案，屬企業線範圍。SQLite／DuckDB 的刪案留痕已於 2026-08-20 裁決不做。 | 隨 SQL Server 企業線一併重啟，或使用者明示要先做本機查詢介面時另立計畫。 |
| KCT 保存情境的重驗契約 | KCT 已保存的條件情境，在之後的 GL 重投影失去必要欄位時，惰性重建（materialize）的 mapping-aware 重驗仍待另立契約與生命週期測試。 | 使用者回報保存情境在重新配對後行為不明，或 KCT 新條件開發啟動時一併處理。 |
| 公司正式部署環境 | 正式環境不能假設有系統管理員權限或 AI 網路服務；最低 Windows／Office 版本、安裝方式、允許的本機資料庫與檔案傳遞仍未知。個人電腦的通過結果不能代替公司驗收。 | 公司能提供實際政策、帳號、設備或代表性測試環境時，另立部署與操作驗收計畫。 |
| KCT 後續條件 | 目前先讓 GA 完成原 IDEA JET 的本機替代；既有 KCT A–J 條件行為保留（前端 `FILTER_KCT_CHECKLIST`、後端 `FilterCompilation` 與 `GlRulePredicates`），A–J 與現行執行的對應已於 2026-08-18 回流當時的指南（現存於 [`history/superseded/jet-guide-2026-08.md`](history/superseded/jet-guide-2026-08.md) §3–§4）。前代已查過當時指定的 `ideascript.bas`、`JE_Tool.ism` 與 draw.io 流程圖，查無 KCT 正式名稱、A–J 原始清單、KCT 全稱或條件 B 的 BS／IS／PPE 分類表；這些來源的正規化文字版本現存為 `legacy/idea-script.bas` 與 `legacy/idea-tool.bas`，不必再回頭搜尋。 | 使用者或 KCT 小組提供正式來源與條件清單後，再建立新的功能計畫；不先推測實作。 |
| IDEA 替代的完整人工驗收 | 已知主線包含匯入、欄位配對、完整性、Account Mapping、條件篩選與底稿，但原 IDEA 的完整功能、條件、底稿及人工判讀清單尚未逐項確認。 | 使用者、GA 或保存資料能提供完整清單時，逐項補入 `idea-replacement-scope.md` 並安排公司條件下的操作驗收。 |
| 斷言強度量測（變異測試） | 現行 harness 能證明測試有執行且未被偷偷略過，但 mutation testing 回答的是另一個問題：斷言能否抓到刻意注入的行為變化。2026-09-01 以 Stryker.NET 4.16.0、`--test-runner mtp`、單一 Domain 檔實跑；它能分析 .NET 10、找到 3,668 項測試並啟動 MTP，先前「完全不支援 MTP」的結論不成立。不過它繞過 `tools/verify.ps1` 的安全環境與 profile 排除，初始 run 實際碰到兩個未授權 PrivateCase 測試；MTP runner 仍標為 preview，上游 issue #3754 也確認每個 mutant 的測試選取會被忽略，issue #3692 則記錄固定三分鐘 RPC 上限。工具、報告與 `.config` 設定已全數清除。 | 不把 Stryker 或其他 mutation 套件放進日常／正式 gate，也不另造只為工具服務的測試專案。日常以 first-red、獨立 oracle、FsCheck、provider parity、GUI 與 Excel 邊界測試補強；這些方法不冒充 mutation score。只有上游 MTP runner 能遵守測試篩選與 mutant test selection，並解除固定逾時後，才以同一個單檔煙霧重新評估。 |

這些資料不足不會阻止已確認的技術修正，但在補齊前不能宣稱 IDEA 替代範圍、SQL Server live 相容性或
公司部署驗收已全部完成。

## 已知技術債

從前代專案帶入、仍適用於現行程式的已知限制。修到相關區域時要知道它們存在；不阻擋日常開發：

- Pre-screening Report 的欄位來源忠實性裁決（2026-08-14）只涵蓋 R2 明細家族與 R6；R5 的
  `CREATED_BY` 等彙總頁標題不在該裁決範圍，維持既有輸出契約名稱。要延伸同樣的 lineage 規則需另行
  裁決。
- CSV 欄名與資料讀取會重做一次格式偵測；只有實測成為瓶頸時才優化。
- 大型本機案件使用較多磁碟空間與 WAL；不能以把母體載入記憶體換速度。
- 前代的 `JET_PBC_DIR` 大檔煙霧測試家族（114 MB PBC fixture）沒有遷入 `je-tool`，相關的
  skip 登錄機制也隨舊驗證框架退役。若日後需要大檔煙霧驗證，依現行框架另立路線，不復刻舊機制。

2026-09-01 提交前複審（全量 diff、八個獨立審查角度）另記錄下列品質項目，供後續 session 收斂；收斂時
逐項驗證後才從表中移除。複審同時記錄的 `dev.log.exportFile` fallback 待修缺陷已於 2026-09-02 依使用者
裁定修正並以 first-red 測試驗證，見
[`specs/2026-09-01-frontend-sync-devlog-mutation-plan.md`](specs/2026-09-01-frontend-sync-devlog-mutation-plan.md)
的「缺陷修正（2026-09-02）」：

- 支援日誌鏈路的重複實作：`SupportDiagnosticRingBuffer` 與 `RingBufferLoggerProvider.cs` 的
  `DiagnosticRingBuffer` 環形緩衝邏輯逐欄相同（僅 entry 型別不同）；`DevLogHandlers.cs` 內兩個
  handler 各持一份相同的 `ToAsyncLines`；`SupportDiagnosticNdjson` 與 `DiagnosticNdjson` 各自維護
  幾乎相同的 serializer 選項。收斂方向：泛型 ring buffer、`ToAsyncLines` 併入 `ProjectLogFileWriter`、
  共用 NDJSON 選項來源。
- `tools/harness/gui-driver/GuiScenarios.cs`：`visible()` JS helper 已有 8 份逐字複本、probe 輪詢骨架
  5 份。2026-09-02 `conflicted-journal-recovery` 已整個換成 `edited-report-still-loads`，不再有錯誤碼斷言。
  收斂方向：抽共用 probe helper 常數。
- `SupportRingBufferLogger` 以寫死的 `"support.log.export"` 字串排除自身事件，action 政策滲入
  logging 層；日後若有其他要排除的 action，應集中到 action 分類表管理。
- 本機單人案件上的多人機制（2026-09-02 使用者裁定先不動）：科目分類儲存的 revision 衝突
  `taxonomy_revision_conflict`、案件鎖 `project_locked`、一次只允許一項變更作業的
  `operation_in_progress`。它們是為兩個人或兩個程序同時操作同一案件設計的；本機 SQLite／DuckDB 案件
  屬個人工作，單人幾乎不會觸發，但每一個都是使用者可能撞到卻沒有出路的死巷。目前邊界：SQL Server 線
  仍需要這些機制，所以不能整個拿掉。重啟條件：出現兩個 JET 同時開同一本機案件的實際需求，或使用者在
  單人操作時實際撞到其中一個錯誤；重啟後第一個可驗證動作是把本機 provider 的這三種錯誤改成提醒並以
  旅程測試證明流程不中斷。
- `ProjectLogFileWriter.WriteAsync` 的 `OperationCanceledException` 與 `JetActionException` 兩個 catch
  與末端 catch-all 行為相同，屬冗餘分支。
- 效能觀察（未量測，僅在實測成為瓶頸時處理）：Release 每次 action dispatch 為 support provider 配置
  一次 Dictionary 與 scope 走訪；`dev.log.exportFile` 對 sink 全檔逐行 `JsonDocument.Parse` 只為讀
  `projectId`；`setMappingDraft` 每次選擇即整面板重建（正確性優先的既定裁定，見 `state.js` 通知慣例）。

2026-09-04 第二階段（報告產物儲存拆解，實作與驗證都在 2026-09-02）收尾時另記下列項目。它們是這次裁定的代價或還沒回頭清的殘留，
不阻擋驗收；下一個 session 依使用者指示逐項處理：

- Working Paper 版本檔會一直累積。清理功能已依裁定拿掉，使用者自行刪檔，第六步清單會把刪掉的標成
  「檔案不存在，重新匯出即可」。`report-artifacts.json` 有 4,096 筆上限，超過時匯出會失敗並提示刪掉不再
  需要的舊版；長年反覆匯出的案件才可能碰到。重啟條件：使用者回報資料夾雜亂或實際撞到上限。
- 只為讀舊資料保留的名稱：`ReportArtifactKind.AccountMapping` 讓舊 manifest 的範本條目能被略過而不是
  讓整份清單失效；`ProjectAuditOperations.ReportCleanup` 讓舊的 audit 列仍能被辨認；測試裡的
  `LegacyReportKind.AccountMapping` 現在代表範本工作簿而不是報告。收斂方向：確認沒有舊版建立的案件
  還在使用後，一起移除並更新 `ReportArtifactKindValuesTests`。
- `ProjectWorkFileWriter` 與 `ProjectReportArtifactStore` 各有一套「寫暫存檔、完整後改名、Excel 佔用時
  的提示、檔名必須在案件資料夾內」。兩套目前行為一致，日後修其中一邊要記得另一邊；可抽成共用 helper。
- 報告檔的 reparse point 鏈檢查已隨 store 重寫移除，這是「相信使用者」的已接受風險（見計畫檔第二階段
  「範圍外」）；GUI 驅動程式的 `OwnedGuiRun.RejectReparsePoint` 是測試治具自己的檢查，仍保留。
- 第六步畫面每種報告只顯示最新一份：`ui-core.js` 的 `findCurrentReportArtifact` 依 `generatedUtc` 取符合
  目前驗證與情境版本的最新產物，所以 Working Paper 的舊版本只存在於案件資料夾與 `project.load` 的
  `reportArtifacts`，畫面上看不到。計畫第二階段的使用者流程寫的是「列出所有版本檔、最新在上」，這一點
  尚未實作，2026-09-04 收尾時才發現。收斂方向：由使用者裁定要顯示全部版本還是維持只顯示最新；若要
  全部顯示，改 `export-step.js` 對 `workingPaper` 的取法並補前端守衛測試。

## Git 交付與來源儲存庫退役

- 第一次根提交候選已完成正式驗證。使用者已確認完整內容、`main` 與 `origin`，並明示授權建立唯一的根提交
  及一次 `main -> origin/main` 推送。Commit、push 與遠端讀回是否實際完成，必須以當時的 Git、遠端及正式
  驗證結果為準，不能從這份長期文件推定。
- 推送後應從遠端建立全新工作目錄，核對內容並重新執行正式驗證，證明新儲存庫不依賴來源工作目錄。
- 遠端讀回與驗證通過後，再由使用者決定是否封存 `je-testing` 與 `new-je-tool` 的遠端儲存庫。舊本機目錄
  若要刪除，必須另外盤點 ignored／untracked 內容並取得授權。

## 不屬於目前阻擋事項

- `JET_Test` 舊資料清理、變異測試、壓力測試與涵蓋率是獨立工作，不阻擋本次治理補強。
- `PrivateCase` 永遠需要當次明示授權，不會自動加入公開 CI 或 `ReleaseCandidate`。
- 原生 Excel 目前只證明這台開發電腦的 Excel 16.0；其他版本與公司正式環境仍需另外驗收。
- `.claude/` 與 `.vscode/` 已依 2026-08-30 的裁定補上必要內容：Claude Code 的 permission 攔截、三個 hook，
  以及對應 `tools/verify.ps1` 的 VS Code 工作。2026-09-01 再依使用者明示需求，新增一個按 `je-tool`
  現況重建、只能明示呼叫的 `jet-converge`；它不是把舊專案的 skills、output style、plugin 設定或逐次驗證
  紀錄整批搬回來。正式版本位於 `.agents/skills/jet-converge/`，Claude 只保留薄包裝，責任與界線見
  [`agent-compatibility.md`](agent-compatibility.md)。
- `.agents/` 現在也保存跨工具共用的收斂與專案記憶 harness；`.config/` 仍維持空目錄，原本用來安裝的
  變異測試工具已退出（見下表）。
