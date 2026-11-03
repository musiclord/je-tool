# 儲存庫收斂與 Agent 工具層補齊計畫

這份是 2026-11-01 壓縮後的摘要。使用者原話、題目與回答、日期、數字與裁定照原文保留；實作經過、驗證收據與重跑過程壓成結果。
壓縮前的全文在提交 `85af454`，可用 `git show 85af454:docs/history/specs/2026-08-30-repository-consolidation-plan.md` 取回。

更新日期：2026-08-30。狀態：已完成；使用者已於 2026-08-30 驗證並授權提交本輪改動。當時的下一步是提交後執行完整 `ReleaseCandidate`，取得對應新候選內容的收據；
是否已完成要查當時的 Git 與收據，不能從本文件推定。

## 目標

根提交完成後，把三件事收乾淨：歷史文件不再依來源專案分目錄、`README.md` 成為真正的專案入口、
Claude Code 的執行前攔截與收尾提醒實際存在，而不是只寫在 `AGENTS.md` 裡。

## 使用者已確定的方向

- 歷史文件按時間軸完全重組，取消 `je-testing/` 與 `new-je-tool/` 兩個目錄。來源專案的名稱對後續開發沒有意義。
- Agent 工具層只補設定，不建立 skill。這推翻了先前「四個目錄沒有必要內容時維持空目錄」的裁定，但只推翻設定的部分。
- 自主測試以 Claude Code hook 承接，不加 GitHub Actions。CI 仍依 `development-guide.md` 的順序，等遠端讀回完成後再從適合遠端環境的公開命令設計。
- 變異測試由 agent 研究必要性後決定。研究結論是移除，理由記在 `development-status.md` 的延後事項。

## 第一輪：重組與工具層

範圍：`docs/history/` 依時間軸重組，開發紀錄合併為單一檔案，設計書與證據併入同一個 `specs/`，被取代的舊版本收進 `superseded/` 並以世代月份命名；
`README.md` 改寫為一般專案 README；修正三處已確認過時的事實，其中兩類加入 `Documentation` 的 `forbiddenClaims`；補上 `.claude/settings.json` 的 `permissions` 攔截與三個唯讀 hook，
以及 `.vscode/` 的工作與偵錯設定；移除已失效的變異測試殘留。不讀取私人案件、不改產品行為、不建立 project-owned skill 或 plugin、不建立 CI，
也不改寫歷史條目的文字、數字、條件、例外或使用者裁定，合併只重新排序並補分界說明。

結果：上述工作都已完成。指向舊歷史路徑的八個連結已修正。變異測試殘留的刪除被本機權限層擋下，由使用者親自執行。候選清單重建為 1,199 個路徑（34 增 30 刪），與 Git 未忽略的檔案完全一致。

**開發紀錄的合併依據。** 早期紀錄涵蓋 2026-06-04 到 2026-06-23，是逐日條目；後期紀錄涵蓋 2026-06-20 到 2026-08-26，但 2026-07-08 以前是事後補寫的區間摘要。
唯一重疊的是「2026-06-20 至 2026-06-29」那筆摘要，其中 2026-06-24 到 2026-06-29 只存在於該摘要。所以兩份各自保留內部順序直接接續，在交界處加分界說明，81 個條目全數保留。

**變異測試的研究結論。** `dotnet-stryker` 4.16.0 仍可用（需要 .NET 10 runtime，Microsoft Testing Platform runner 仍是 preview），移除的理由不是技術不可行，而是它在 `je-tool` 已失效且不再回答獨立問題：

1. 它從來不是關卡：`stryker-config.json` 的 `thresholds.break` 是 `0`，只是對四個檔案的週期性量測。
2. 引進它要解決的問題已由 harness 機器化：2026-06-18 引進它，是因為前一輪被審查抓到兩次測試通過但斷言是空的；同一輪引進的回應契約鎖與誠實跳過，在 `je-tool` 已變成每次都強制——`Public` 要求至少 3,000 個案例，跳過的測試必須登記在 `public-skip-policy.json` 並比對原因雜湊，`Focused` 選不到測試或遇到跳過就失敗。
3. 殘留本身是壞的：`JET.Mutation.Tests` 不在 `JET.slnx` 內，`AssemblyName` 又設成與正式測試專案同名的 `JET.Tests`，安裝工具用的 `.config/dotnet-tools.json` 從未遷移。

現行 harness 能證明測試有跑、沒被偷偷跳過，證明不了斷言夠嚴；這個缺口目前靠 first-red 紀律以流程補，重啟條件記在 `development-status.md` 的延後事項表。

驗證：`Documentation` 通過，22 份受管文件 0 error、0 warning；新增的 `stale-unborn-main` 規則第一次執行就抓到 `docs/harness.md` 第 188 行的第三處過時事實，修正後才通過。
`Contract` 通過；框架契約測試 372 項斷言、25 個情境全數通過；全部受管文件與 `legacy/` 筆記的相對連結都解析得到；三個 hook 以實際輸入執行過，輸出為合法 JSON。
本輪沒有執行 `Build`、`Focused`、`Public`、`Provider`、`Package`、`Gui`、`Excel`、`PrivateCase` 或 `ReleaseCandidate`；產品程式沒有改，但候選內容有變，這些命令的舊結果不能沿用。

## 第二輪：重排入口文件

使用者指出 `README.md` 不該有「給 AI Agent 的規則」段落，並要求參考 2026 年的 harness engineering 做法重排專案入口。
本輪查閱了 OpenAI、Martin Fowler、Anthropic 的相關文章與兩個範本儲存庫。硬約束不變：技術棧與 Thin-Bridge Action-Dispatcher、Application CQRS、Clean Core、集合式規則。

採納：

- `AGENTS.md` 當地圖不當百科，從 131 行改寫為 83 行，含專案一句話、常用命令、不可協商的邊界、歧義處理、跨 session 續接、導航表與收尾；移出的細則逐條確認在 `docs/` 有唯一的家後才移除，缺的四條寫作規範補進 `docs/README.md`。
- `docs/` 當唯一的紀錄來源，規則正文只在一處。
- README 回歸標準 GitHub 佈局，AI Agent 規則段落整段移除；新增 `CONTRIBUTING.md` 作為人類開發者入口，只導引不立規則。
- 建置與測試命令放進 `AGENTS.md` 的常用命令區塊。
- 盤點後確認回饋層本來就強（verify.ps1 收據、`forbiddenClaims`、Claude hooks），本輪主要是把入口文件修薄。

否決：feature list JSON（那是從零長功能的機制，JET 剩餘範圍已由 `idea-replacement-scope.md` 與延後事項表追蹤）；skills 化（Claude 與 Codex 各有自己的 skill 目錄，做了就要同步兩份，等某個程序高頻到值得再說）；
「描述現況的文件移出 repo、只留測試當規格」（JET 的業務規則來自已退役的 IDEA 流程，`jet-guide.md` 這類文件是唯一的來歷解釋）；
候選清單改名（`docs/first-root-commit-candidate.txt` 被 `lanes.json`、`JetHarness.psm1` 與 372 項契約測試釘住，提交前不動，列為日後小案）；
PostToolUse 自動格式化（`dotnet format` 每次要數秒，格式由 `.editorconfig` 與建置警告把關）。

## 第三輪：背景脈絡遷移補洞

使用者指出擱置事項與開發背景仍留在 `new-je-tool`，接手時不該需要重新追溯。逐項比對後補上：

- `README.md` 移除「參與開發」與「沿革與授權」，新增「技術與定位」一節。
- 新增 `sqlserver-enterprise-deferred.md`：四個已確認的多人安全缺口與企業驗收範圍原本只在 `history/superseded/`，但它們是現行待辦，回收為現行文件。
- `development-status.md` 延後表補四列（SQL Server 企業多人環境、預篩選後續收斂、audit log 查詢與保留政策、KCT 保存情境重驗契約），KCT 列補回「legacy 來源已查無 KCT 名稱與 A–J 原始清單」的追查結論；新增「已知技術債」一節（R5 lineage 裁決範圍、CSV 重偵測、磁碟與 WAL、PBC 家族未遷入）。
- `jet-guide.md` 補三段：§4 母體判定唯一權威面（2026-08-20 裁定）、§6 KCT A–J 主線與對應位置、§8 SQLite 與 DuckDB 選型理由、可攜性前提與本地刪案留痕不做的裁定。
- `AGENTS.md` 新增「動工前先讀」任務路由表。

## 第四輪：事實保真防線

使用者發現 agent 在改寫文件時捏造事實（延後表 KCT 列把原紀錄的 `ideascript.bas`、`JE_Tool.ism` 默默替換成現行檔名，等於宣稱一筆不存在的查核），
並提供 Codex 側「如何避免 agent talk shit」的研究對話要求接續處理。該對話的結論：問題核心是改寫時的事實保真，不是風格；風格層 Codex 側已落地，缺的是事實層防線。本輪補上：

- 修正被捏造的 KCT 列：原始查核對象逐字還原，新舊檔名對應依 `legacy/README.md` 另行寫明。
- `AGENTS.md` 的不可協商邊界新增「事實保真」段：先找原文再改寫、名稱逐字照抄不得等義替換、找不到出處就刪掉或標待確認、「查不到」是合法答案。
- `docs/README.md` 寫作規範新增引用保真與新名詞准入規則。
- `managed-doc-notice` hook 的收尾清單把事實保真升為第一項。
- `.claude/settings.json` 新增 `ask`：編輯 `docs/history/**` 要先問過人，因為歷史文件是引用的原文來源。

刻意不做：每次編輯都攔截的 LLM 事實查核 hook（成本高，且把判斷外包給另一個會幻覺的模型）、大型禁用詞表與句長硬性失敗（容易誤傷且會被換詞繞過）、
修改 harness 程式加自動事實比對（提交前不動回饋層，且語意層面的捏造無法機械攔截）。最後一道防線仍是人工驗收與轉述測試。

## 跨 agent 相容性查核

- GitHub Copilot 官方支援矩陣重新查證：VS Code、Copilot coding agent 與 Copilot CLI 原生讀 `AGENTS.md`；Visual Studio、JetBrains 與 GitHub.com 的 Chat 只讀 `.github/copilot-instructions.md`，所以薄轉接檔必須保留安全摘要。
- 載入預算：`AGENTS.md` 11,647 bytes，佔 Codex 32 KiB 合併預算約 36%；Claude Code 實際載入 `CLAUDE.md` 9 行加 `AGENTS.md` 131 行共 140 行，在官方建議的 200 行內。
- `.claude/settings.json` 的 deny 擋了 `git add -u`，但 `AGENTS.md` 原本沒列；攔截層不得超出共用權威，所以把 `-u` 補進 `AGENTS.md` 與 Copilot 轉接檔。
- `.claude/` 與 `.vscode/` 只影響 Claude Code 與 VS Code，不得成為規則或驗收前提。

## 驗收條件

1. `docs/history/` 不再出現來源專案名稱作為目錄或檔名，且開發紀錄是單一時序檔案。已達成。
2. `README.md` 能讓沒看過這個專案的人裝好環境、建置、啟動並跑測試。已達成。
3. `AGENTS.md` 的禁令有對應的 Claude Code 執行前攔截，且攔截範圍不超出 `AGENTS.md`。已達成。
4. 新 session 不必問人就能知道實際的 branch、HEAD、工作樹與最近一次各命令的驗證結果。已達成。
5. 已確認過時的事實不能只靠人記得，要由 `Documentation` 擋住。已達成，並已實際攔截一次。
6. 候選清單與實際未忽略檔案一致。已達成：使用者移除變異測試殘留後重建為 1,199 個路徑。

`ReleaseCandidate` 在 HEAD 已存在時要求工作樹完全乾淨，所以只能在本輪改動提交之後執行。

## 已知但本次未處理

- `Documentation` 不檢查 Markdown 相對連結是否存在；本輪的八個失效連結是用一次性腳本找出來的。要不要加進 `JetDocumentationCheck.ps1` 尚未裁定。
- `docs/first-root-commit-candidate.txt` 的名稱在根提交後已不精確，實際角色是 `ReleaseCandidate` 的檔案清單；改名會動到 `JetHarness.psm1` 的字面比對，本次沒有改。
- `documentation-report.json` 的 `manualReview` 項目在報告檔中是亂碼，命令的 stdout 摘要則正常，是寫檔時的編碼問題，本次沒有處理。
- `.agents/` 與 `.config/` 仍是空目錄。前者要等決定是否為 Codex 另建 skill 入口；後者在變異測試退出後沒有用途。
