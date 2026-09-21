# JET 開發現況

更新日期：2026-09-21

## 目前狀態

`je-tool` 已整理成新儲存庫的根內容。產品程式、參考工作簿、現行文件、legacy 背景與新的驗證框架都在
本目錄內；`je-testing` 與 `new-je-tool` 不再是執行或驗證依賴，來源 Git 歷史也不會匯入。Branch、HEAD 與
遠端同步狀態屬於會變動的執行狀態，接手時應直接查 Git，不能只依這份文件判斷。

驗證框架 Phase 1 至 Phase 7 已完成。公開產品測試、provider、Release 封裝、八個隔離 GUI 情境、六份合成
報表的原生 Excel 檢查，以及先前明示授權的 SQLite／SQL Server 私人案件都曾分別通過。這些結果只能描述
當次執行；候選內容變動後，仍要依變更範圍重跑正式命令。

## 目前大型計畫

目前唯一現行計畫為[使用者回饋與操作流程一致性修正計畫](specs/2026-09-17-user-feedback-and-workflow-review-plan.md)。
2026-09-21 使用者已授權提交並推送累積成果，準備在測試環境驗收。狀態仍為待使用者驗收；
交付範圍、獨立複審、封裝結果及本機來源排除方式見同一計畫的「測試環境交付」。
它追蹤 D01 至 D32、F01 至 F18。主要功能與 A–U 相容入口已有實作，整體仍保留試用及明示延後事項。
本輪交付範圍、重要裁定、產品順序與驗證邊界都在計畫開頭；
本頁只保留入口，避免兩份摘要各自過期。接手可執行 `pwsh -NoProfile -File tools/verify.ps1 -Command Context`。

9/18 已完成指定科目與借貸方向綁定、每月月初及月底條件的本機驗證；任意多欄位綁定同一筆佐證分錄、
本地分類節點、階層及通用量詞已完成實作。相關資料庫、畫面與報表驗證已通過；歷次結果見同一計畫。
公司三項操作依 9/17 回覆驗收通過，不能重新列為欠缺；
新增功能的試用與原始私人 PBC 原因則依計畫分別追蹤。

2026-09-18 使用者確認總帳科目名稱維持必要配對欄位，D12 不再列為待討論或未完成開發。
接續篩選工作合併為三組：複合條件、整張傳票及借貸分側的符合方式、本地分類選取與階層；
共同的流程驗證與文件更新併入同一個開發 session。具體範圍及完成條件見計畫開頭。

同日再盤點時，已修正 50 項摘要中過期的「第三段處理」等待辦。授權清單、單側人工或自動配對、
科目及人員統計、新匯入格式、空值明細和 KCT 缺欄專項流程均已有實作與通過證據。
當時列出的 A–U 操作入口、相容證據及三則可空值建置警告，已於同日依使用者指示完成。
A–U 二十一項、工作簿五個範例及七個補充案例共 33 組，已在 SQLite 和 DuckDB 核對固定答案。
三則警告已修正，Debug、AgentGuiTest 及 Release 建置均為零警告。最終操作與報表驗證結果只在
現行計畫的「A–U 操作相容與警告修正」維護，不在此另建清單。

逐輪數字與已被取代的下一步已移至[歷史執行紀錄](history/2026-09-18-feedback-execution-records.md)。
長期延後事項與重啟條件仍在本頁下方，沒有因整理文件而取消或重啟。

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
- 較早的驗證紀錄為 `Documentation` 檢查 21 份文件與轉接檔，結果為 0 error、0 warning；一般 `Contract`
  通過。當時驗證框架自己的 372 項 assertions、25 個情境全部通過，包含 Provider 缺少連線時維持 `blocked`、
  Documentation 分級、Agent 指向及隔離 Git repo 的 source-index fingerprint 測試。
- 2026-08-30 依目前產品順序修正驗證邊界：`ReleaseCandidate` 不再要求 live SQL Server，固定執行 Contract、
  Documentation、Public、Package、Gui 與 Excel；完整 `Provider` 保留為日後明示執行的 SQL Server 相容性
  驗證。完整候選結果為 Public 3,390 total／3,383 passed／7 個登錄 skip、Package 70／70、兩個 GUI 情境及
  六份 Excel 往返全數通過。第一次執行只因沙盒無法連 NuGet 而 `blocked`，獲准在本機 Windows 邊界原樣
  fresh rerun 後通過；兩次均未連線、查看私人資料、stage、commit 或 push。

## 已知但延後的事項

2026-09-17 合併新計畫後，下列事項仍保留。PBC 診斷及與本輪相關的流程查核已由新計畫接續，
合成診斷與部分流程已完成產品修正，最終驗證見現行計畫；其他延後事項不因本次合併而自動重啟。

2026-09-09 使用者補充目前安排：SQL Server 實機驗證與企業多人開發近期擱置，audit log 查詢、匯出與保留政策
等 SQL Server 開始開發時再處理。預篩選報告與條件維持現況，目前不啟動縮編或退役討論。
KCT 保存情境已依目前配對重新檢查，兩個本機資料庫的缺欄及補回流程與 43 次專項 GUI 均已通過；
空值明細的 35 次專項 GUI 也已通過。兩項不再放在延後表；KCT 新條件仍延後，不推測缺少的正式來源。
Adjust、Claude Desktop UI 與 Claude Design 交接屬 Agent 開發框架，不列為 JET 產品待辦或人工驗收。
空值表原於 2026-09-09 延後，2026-09-17 已授權改善。KCT 保存情境重驗也已納入現行計畫。

這些事項不阻擋目前遷移，也不能因當下沒有處理就從後續進度整理中消失。每次大型工作結束，或使用者詢問
尚未完成的事情時，應逐項回報目前狀態、延後原因與重啟條件；已解決的項目才從表中移除。

| 事項 | 背景與目前邊界 | 何時重啟 |
|:---|:---|:---|
| 原始 PBC 匯入失敗原因 | 2026-09-14 的 GL 匯入在本機批次取代路徑失敗，舊日誌無法確認原檔觸發原因。2026-09-17 已改共用例外保留及安全診斷；合成檔案占用、後段解析、解碼、儲存格轉換、進度通知、資料庫失敗與重試均有測試，交易回復失敗另以替身驗證。 | 原始私人檔的原因仍未知。新發生錯誤先依去識別支援日誌判讀；只有當次獲准才執行真實案件驗收，不將合成測試當成原檔已修好。 |
| 期初與期末 TB 分檔匯入 | D06 保留延後。兩份檔案如何依科目連接、缺邊及重複科目怎麼處理尚未啟動，不能當作普通追加。 | 使用者明示重啟後，先確認兩期餘額的資料規則，再建立合成答案及完整匯入驗證。 |
| ValidationReport 的「完整性測試出現差異時之指引」工作表改寫 | 2026-09-12 記錄使用者第四步原話：「ValidationReport.xlsx的"完整性測試出現差異時之指引"工作表，內容整份要大改 (這個要跟DPP討論，以後再說)」。該工作表目前由 `LegacyReportWriter.Validation.cs` 依 legacy 範本輸出，外觀比對夾具鎖住多個儲存格位置。內容屬審計指引，不由 Agent 自行改寫。 | 使用者與 DPP 討論出新內容後另立短期計畫；改寫時同步更新外觀比對夾具與 `ReportArtifactExportTests`。 |
| SQL Server live `Provider` 驗證 | 2026-09-02 使用者確認 SQL Server 開發暫緩，日常開發與測試集中在 SQLite 和 DuckDB。既有 SQL Server 相容性保留；一般 `ReleaseCandidate` 不要求 `JET_SQLSERVER_CONNECTION`，也不能代表 live SQL Server 已通過。本機服務平時關閉，使用後依 [`tools/README.md`](../tools/README.md#日常資料庫測試與服務收尾) 完成收尾。公司端權限、網路、身分及維運方式仍未定。 | 使用者當次明示要驗證本機 SQL Server，且已準備專用 `JET_Test`、最低必要權限及只存在於當次程序的連線資訊時，完整執行 `Provider`；若沙盒阻擋，保留 first-red 後在獲准的本機 Windows 邊界原樣 fresh rerun。成功、失敗或中止後都要關閉本機服務，不把 live 驗證列入日常例行工作。 |
| SQL Server 企業多人環境 | 四個已確認的多人安全缺口（真實身分驗證、serverOnly 刪除、noAccess metadata 隱藏、SQL Server 2022 版本硬閘）與多人共用案件、使用鎖、容量資訊的驗收範圍，完整記錄在 [`sqlserver-enterprise-deferred.md`](sqlserver-enterprise-deferred.md)。只記錄、不推測實作。 | 公司能提供 SQL Server 2022、至少兩個真實帳號、DBA 支援與已核定的授權政策時，依該文件另立短期驗收計畫。 |
| 預篩選的後續收斂 | 預篩選規則目錄已於 2026-08-20 凍結，逐筆命中改為輔助訊號，Pre-screening Report 改為純可選輸出（匯出面默認勾選）。該報告的長期去留（縮編或退役）與預篩選條件的最終棄用清單，當時裁定留待另場收斂。 | 使用者要求收斂 Pre-screening Report 去留或條件棄用清單時另立計畫；在此之前不新增逐筆預篩選規則。 |
| audit log 查詢介面、匯出與保留政策 | 本機案件的最小 audit log 已落地（schema v9 資料庫層 append-only；DuckDB 因引擎沒有 trigger 維持程式紀律）。查詢介面、匯出、保留政策與企業部署稽核當時裁定另案，屬企業線範圍。SQLite／DuckDB 的刪案留痕已於 2026-08-20 裁決不做。 | 隨 SQL Server 企業線一併重啟，或使用者明示要先做本機查詢介面時另立計畫。 |
| KCT 後續條件 | 目前先讓 GA 完成原 IDEA JET 的本機替代；既有 KCT A–J 條件行為保留（前端 `FILTER_KCT_CHECKLIST`、後端 `FilterCompilation` 與 `GlRulePredicates`），A–J 與現行執行的對應已於 2026-08-18 回流當時的指南（現存於 [`history/superseded/jet-guide-2026-08.md`](history/superseded/jet-guide-2026-08.md) §3–§4）。前代已查過當時指定的 `ideascript.bas`、`JE_Tool.ism` 與 draw.io 流程圖，查無 KCT 正式名稱、A–J 原始清單、KCT 全稱或條件 B 的 BS／IS／PPE 分類表；這些來源的正規化文字版本現存為 `legacy/idea-script.bas` 與 `legacy/idea-tool.bas`，不必再回頭搜尋。 | 使用者或 KCT 小組提供正式來源與條件清單後，再建立新的功能計畫；不先推測實作。 |

變異測試已於 2026-09-05 經使用者確認重啟，並在前輪完成，不列為本次篩選修正工作。
固定版本、測試篩選、程序收尾與兩個範圍的量測已完成。2026-09-01 的 Stryker.NET 4.16.0 試跑與
無效量測結論仍保留在
[`前輪計畫`](specs/2026-09-01-frontend-sync-devlog-mutation-plan.md) 歷史中，不能和有明確來源及範圍的結果混用。

這些分別延後或尚待驗證的事項不會阻止已確認的技術修正，不能據此宣稱 SQL Server live 相容性已通過。公司三項操作依 2026-09-17 使用者回報為通過。

## 已知技術債

從前代專案帶入、仍適用於現行程式的已知限制。修到相關區域時要知道它們存在；不阻擋日常開發：

2026-09-17 盤點計畫時另核對下列維護事項。它們不列為使用者功能驗收，後續與現行計畫的合併修正一起核對：

| 事項 | 本次查核與處理條件 |
|:---|:---|
| 文件相對連結自動檢查 | 8/30 計畫記錄尚未納入；本次讀取 `JetDocumentationCheck.ps1`，仍只檢查列管文件、文字及必要標記。是否新增連結解析尚未裁定，維護框架時再評估；本次新增連結另外逐一核對。 |
| 候選清單舊檔名 | `first-root-commit-candidate.txt` 仍是現行候選清單，導覽已說明用途。若日後改名，須同步更新框架及所有引用；目前沒有因此失效。 |
| 架構圖更新 | `architecture/` 仍標示 2026-08-30 的程式修訂。本次未重產圖；需要呈現最新模組時，先核對來源，再同步更新圖及說明。 |
| 歷史文件的疑似案件識別內容 | 盤點 `history/specs/2026-06-21-low-frequency-account-escalation-design.md` 開頭時發現疑似真實案件識別內容。依 AGENTS.md 停止擴大讀取，未複製內容或清理原文；是否清理及處理範圍待使用者裁定。 |

前次盤點的三則可空值警告已於 2026-09-18 修正。底稿使用必要的非空依賴，GL 配對檢查明示失敗時不返回；
缺欄的原有錯誤行為保留，Release 建置零警告。具體驗證見現行計畫，不再列為技術債。

8/30 計畫曾記錄 `documentation-report.json` 的 `manualReview` 亂碼；本次讀取最新收尾報告時中文正常，
程式也已明確使用 UTF-8，因此不沿用為目前尚未修正的缺陷。該計畫當時的 `.agents/` 空目錄敘述同樣已過時。

- Pre-screening Report 的欄位來源忠實性裁決（2026-08-14）只涵蓋 R2 明細家族與 R6；R5 的
  `CREATED_BY` 等彙總頁標題不在該裁決範圍，維持既有輸出契約名稱。要延伸同樣的 lineage 規則需另行
  裁決。
- CSV 欄名與資料讀取會重做一次格式偵測；只有實測成為瓶頸時才優化。
- 大型本機案件使用較多磁碟空間與 WAL；不能以把母體載入記憶體換速度。
- 變異工具目前的批次編譯回復，會讓 MoneyScaling 的五個有效且有行為差異的變異未被完整測試執行。
  本輪以獨立編譯與固定案例補證，原 Stryker 分類仍保留 CompileError，不重算分數。上游改善這個行為時，
  先重跑來源與測試選取資格檢查，再重跑同一範圍；細節與 ID 對應見上述前輪計畫。
- 前代的 `JET_PBC_DIR` 大檔煙霧測試家族（114 MB PBC fixture）沒有遷入 `je-tool`，相關的
  skip 登錄機制也隨舊驗證框架退役。若日後需要大檔煙霧驗證，依現行框架另立路線，不復刻舊機制。

2026-09-01 提交前複審（全量 diff、八個獨立審查角度）另記錄下列品質項目，供後續 session 收斂；收斂時
逐項驗證後才從表中移除。複審同時記錄的 `dev.log.exportFile` fallback 待修缺陷已於 2026-09-02 依使用者
裁定修正並以 first-red 測試驗證，見
[`specs/2026-09-01-frontend-sync-devlog-mutation-plan.md`](specs/2026-09-01-frontend-sync-devlog-mutation-plan.md)
的「缺陷修正（2026-09-02）」：

- `SupportRingBufferLogger` 以寫死的 `"support.log.export"` 字串排除自身事件，action 政策滲入
  logging 層；日後若有其他要排除的 action，應集中到 action 分類表管理。
- 本機單人案件上的多人機制（2026-09-02 使用者裁定先不動）：科目分類儲存的 revision 衝突
  `taxonomy_revision_conflict`、案件鎖 `project_locked`、一次只允許一項變更作業的
  `operation_in_progress`。它們是為兩個人或兩個程序同時操作同一案件設計的；本機 SQLite／DuckDB 案件
  屬個人工作，單人幾乎不會觸發，但每一個都是使用者可能撞到卻沒有出路的死巷。目前邊界：SQL Server 線
  仍需要這些機制，所以不能整個拿掉。重啟條件：出現兩個 JET 同時開同一本機案件的實際需求，或使用者在
  單人操作時實際撞到其中一個錯誤；重啟後第一個可驗證動作是把本機 provider 的這三種錯誤改成提醒並以
  旅程測試證明流程不中斷。
- 效能觀察（未量測，僅在實測成為瓶頸時處理）：Release 每次 action dispatch 為 support provider 配置
  一次 Dictionary 與 scope 走訪；`dev.log.exportFile` 對 sink 全檔逐行 `JsonDocument.Parse` 只為讀
  `projectId`；`setMappingDraft` 每次選擇即整面板重建（正確性優先的既定裁定，見 `state.js` 通知慣例）。

2026-09-05 已共用日誌環形緩衝、逐行輸出及 NDJSON 選項來源，移除相同的例外處理分支，保留原本的
文字轉義與資料篩選差異。診斷器已依每個情境必有計數的實作改用明確索引，以合成的空結果和零命中
確認行為；這些公開測試通過，沒有執行私人案件。GUI 的可見性與輪詢也已共用，六個正式情境通過。

2026-09-04 第二階段（報告產物儲存拆解，實作與驗證都在 2026-09-02）收尾時另記下列項目。它們是這次裁定的代價或還沒回頭清的殘留，
不阻擋驗收；依當次任務範圍逐項處理，不因此要求另開 session：

- Working Paper 版本檔會一直累積。清理功能已依裁定拿掉，使用者自行刪檔，第六步清單會把刪掉的標成
  「檔案不存在，重新匯出即可」。`report-artifacts.json` 有 4,096 筆上限，2026-09-05 已修正為寫檔前檢查，
  避免失敗時留下未登錄的新底稿。同日使用者裁定清單滿額時只移除已確認刪檔的底稿紀錄，已實作並以
  滿額、檔案被放回、取消與失敗保留案例驗證；未滿額時仍保留刪檔歷史。更大的長期容量需求另行量測。
- 只為讀舊資料保留的名稱：`ReportArtifactKind.AccountMapping` 讓舊 manifest 的範本條目能被略過而不是
  讓整份清單失效；`ProjectAuditOperations.ReportCleanup` 讓舊的 audit 列仍能被辨認；測試裡的
  `LegacyReportKind.AccountMapping` 現在代表範本工作簿而不是報告。收斂方向：確認沒有舊版建立的案件
  還在使用後，一起移除並更新 `ReportArtifactKindValuesTests`。
- `ProjectWorkFileWriter` 與 `ProjectReportArtifactStore` 都會先寫暫存檔，再完整改名並處理 Excel 佔用。
  兩者現在還分別負責「範本存在就保留」與「報告清單隨發布保存」，不能當成完全相同的流程合併。
  共用底層檔案操作留待維護這兩處時再評估，先以各自的取消與發布測試保護行為。
- 報告檔的 reparse point 鏈檢查已隨 store 重寫移除，這是「相信使用者」的已接受風險（見計畫檔第二階段
  「範圍外」）；GUI 驅動程式的 `OwnedGuiRun.RejectReparsePoint` 是測試治具自己的檢查，仍保留。

## Git 交付與來源儲存庫退役

- 最新變更仍依現行計畫驗證；是否暫存、提交或推送需使用者另外明示，人工驗收通過不是 Git 授權。
- 2026-09-21 使用者已明示授權本次 commit 和 push；這是本次累積成果的交付授權，不延伸至未來變更。
- 2026-09-07 使用者確認 `.vscode/settings.json` 不同步至 repo，保留本機，不列入候選清單。
- 同日使用者表示來源儲存庫的封存與舊本機目錄處理由自己負責；不再列為 Agent 待辦，不自行盤點或刪除。
## 不屬於目前阻擋事項

- `JET_Test` 舊資料清理、變異測試、壓力測試與涵蓋率是獨立工作，不阻擋本次治理補強。
- `PrivateCase` 永遠需要當次明示授權，不會自動加入公開 CI 或 `ReleaseCandidate`。
- Agent 的原生 Excel 自動與目視檢查只證明這台開發電腦。公司 Office 365 的既有操作已由使用者於
  2026-09-17 回覆通過；其他版本及後續新功能的相容性按各自影響範圍另驗，不把已通過項目重列待驗收。
- `.claude/` 與 `.vscode/` 已依 2026-08-30 的裁定補上必要內容：Claude Code 的 permission 攔截、三個 hook，
  以及對應 `tools/verify.ps1` 的 VS Code 工作。2026-09-01 再依使用者明示需求，新增一個按 `je-tool`
  現況重建、只能明示呼叫的 `jet-converge`；它不是把舊專案的 skills、output style、plugin 設定或逐次驗證
  紀錄整批搬回來。正式版本位於 `.agents/skills/jet-converge/`，Claude 只保留薄包裝，責任與界線見
  [`agent-compatibility.md`](agent-compatibility.md)。
- `.agents/` 現在也保存跨工具共用的收斂與專案記憶 harness；`.config/` 仍維持空目錄。舊變異工具的安裝
  已移除，本輪固定來源工具改放在被忽略的 `artifacts/harness/mutation/`，不恢復全域安裝。
