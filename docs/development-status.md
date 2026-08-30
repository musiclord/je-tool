# JET 開發現況

更新日期：2026-08-30

## 目前狀態

`je-tool` 已整理成新儲存庫的根內容。產品程式、參考工作簿、現行文件、legacy 背景與新的驗證框架都在
本目錄內；`je-testing` 與 `new-je-tool` 不再是執行或驗證依賴，來源 Git 歷史也不會匯入。Branch、HEAD 與
遠端同步狀態屬於會變動的執行狀態，接手時應直接查 Git，不能只依這份文件判斷。

驗證框架 Phase 1 至 Phase 7 已完成。公開產品測試、provider、Release 封裝、兩個隔離 GUI 情境、六份合成
報表的原生 Excel 檢查，以及先前明示授權的 SQLite／SQL Server 私人案件都曾分別通過。這些結果只能描述
當次執行；候選內容變動後，仍要依變更範圍重跑正式命令。

## 目前大型計畫

[`specs/2026-08-30-repository-consolidation-plan.md`](specs/2026-08-30-repository-consolidation-plan.md)：
歷史文件依時間軸重組、過時事實修正、Claude Code 攔截層與三個 hook、跨 agent 相容性查核；第二輪再依
harness engineering 共識把 `AGENTS.md` 改寫為 83 行的地圖、`README.md` 回歸標準 GitHub 佈局、新增
`CONTRIBUTING.md`，被移出的細則逐條併入 `docs/` 對應文件。實作與驗證都已完成：`Documentation`（24 份
受管文件 0 error 0 warning）、`Contract` 與驗證框架契約測試通過，候選清單重建為 1,200 個路徑並與 Git
未忽略檔案一致。剩餘動作是使用者驗證後另行授權提交，提交後執行完整 `ReleaseCandidate`。這項工作沒有
改產品業務行為。

前一項計畫
[`specs/2026-08-29-agent-governance-and-context-plan.md`](specs/2026-08-29-agent-governance-and-context-plan.md)
已完成：跨 Agent 規則、專案背景、IDEA 替代範圍、跨 session 續接與文件檢查分級都已實作、驗證及確認。
使用者另於 2026-08-30 明示授權第一次根提交及一次 `main -> origin/main` 推送。

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
| SQL Server live `Provider` 驗證 | 產品與測試仍保留 SQL Server 相容性，但目前只有個人電腦可能具備環境，公司端權限、網路、身分及維運方式未定。一般 `ReleaseCandidate` 不要求 `JET_SQLSERVER_CONNECTION`，也不能代表 live SQL Server 已通過。 | 使用者明示要驗證本機 SQL Server，且已準備專用 `JET_Test`、最低必要權限及只存在於當次程序的連線資訊時，完整執行 `Provider`；若沙盒阻擋，保留 first-red 後在獲准的本機 Windows 邊界原樣 fresh rerun。 |
| SQL Server 企業多人環境 | 四個已確認的多人安全缺口（真實身分驗證、serverOnly 刪除、noAccess metadata 隱藏、SQL Server 2022 版本硬閘）與多人共用案件、使用鎖、容量資訊的驗收範圍，完整記錄在 [`sqlserver-enterprise-deferred.md`](sqlserver-enterprise-deferred.md)。只記錄、不推測實作。 | 公司能提供 SQL Server 2022、至少兩個真實帳號、DBA 支援與已核定的授權政策時，依該文件另立短期驗收計畫。 |
| 預篩選的後續收斂 | 預篩選規則目錄已於 2026-08-20 凍結，逐筆命中改為輔助訊號，Pre-screening Report 改為純可選輸出（匯出面默認勾選）。該報告的長期去留（縮編或退役）與預篩選條件的最終棄用清單，當時裁定留待另場收斂。 | 使用者要求收斂 Pre-screening Report 去留或條件棄用清單時另立計畫；在此之前不新增逐筆預篩選規則。 |
| audit log 查詢介面、匯出與保留政策 | 本機案件的最小 audit log 已落地（schema v9 資料庫層 append-only；DuckDB 因引擎沒有 trigger 維持程式紀律）。查詢介面、匯出、保留政策與企業部署稽核當時裁定另案，屬企業線範圍。SQLite／DuckDB 的刪案留痕已於 2026-08-20 裁決不做。 | 隨 SQL Server 企業線一併重啟，或使用者明示要先做本機查詢介面時另立計畫。 |
| KCT 保存情境的重驗契約 | KCT 已保存的條件情境，在之後的 GL 重投影失去必要欄位時，惰性重建（materialize）的 mapping-aware 重驗仍待另立契約與生命週期測試。 | 使用者回報保存情境在重新配對後行為不明，或 KCT 新條件開發啟動時一併處理。 |
| 公司正式部署環境 | 正式環境不能假設有系統管理員權限或 AI 網路服務；最低 Windows／Office 版本、安裝方式、允許的本機資料庫與檔案傳遞仍未知。個人電腦的通過結果不能代替公司驗收。 | 公司能提供實際政策、帳號、設備或代表性測試環境時，另立部署與操作驗收計畫。 |
| KCT 後續條件 | 目前先讓 GA 完成原 IDEA JET 的本機替代；既有 KCT A–J 條件行為保留（前端 `FILTER_KCT_CHECKLIST`、後端 `FilterCompilation` 與 `GlRulePredicates`），A–J 與現行執行的對應已於 2026-08-18 回流當時的指南（現存於 [`history/superseded/jet-guide-2026-08.md`](history/superseded/jet-guide-2026-08.md) §3–§4）。前代已查過當時指定的 `ideascript.bas`、`JE_Tool.ism` 與 draw.io 流程圖，查無 KCT 正式名稱、A–J 原始清單、KCT 全稱或條件 B 的 BS／IS／PPE 分類表；這些來源的正規化文字版本現存為 `legacy/idea-script.bas` 與 `legacy/idea-tool.bas`，不必再回頭搜尋。 | 使用者或 KCT 小組提供正式來源與條件清單後，再建立新的功能計畫；不先推測實作。 |
| IDEA 替代的完整人工驗收 | 已知主線包含匯入、欄位配對、完整性、Account Mapping、條件篩選與底稿，但原 IDEA 的完整功能、條件、底稿及人工判讀清單尚未逐項確認。 | 使用者、GA 或保存資料能提供完整清單時，逐項補入 `idea-replacement-scope.md` 並安排公司條件下的操作驗收。 |
| 斷言強度量測（變異測試） | 現行 harness 能證明測試有跑、沒有被偷偷跳過：`Public` 要求至少找到 3,000 個案例，跳過的測試必須逐一登記在 `public-skip-policy.json` 並比對跳過原因的雜湊，`Focused` 選不到測試或遇到跳過就失敗。這些都證明不了斷言夠嚴，目前靠 first-red 紀律以流程補。2026-08-30 移除了前代留下但已失效的變異測試設定：`JET.Mutation.Tests` 不在 `JET.slnx` 內、沒有任何建置路徑會碰到它，組件名稱又與正式測試專案相同，而安裝工具用的 `.config/dotnet-tools.json` 並未遷移。 | 出現具體的斷言強度疑慮，例如審查再度發現空斷言或測試對行為改變不敏感時，才重新評估。屆時要先確認它回答的是現行 harness 答不了的問題，並解決組件同名與方案未納入的問題，不因舊設定曾經存在就恢復。 |

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
  以及對應 `tools/verify.ps1` 的 VS Code 工作。內容全部依 `je-tool` 現況新寫，沒有從舊專案複製 skill、
  output style、plugin 設定或逐次驗證紀錄。責任與界線見 [`agent-compatibility.md`](agent-compatibility.md)。
- `.agents/` 與 `.config/` 維持空目錄。前者要等決定是否為 Codex 另建 skill 入口才需要；後者原本只用來
  安裝變異測試工具，該工具已退出（見下表）。
