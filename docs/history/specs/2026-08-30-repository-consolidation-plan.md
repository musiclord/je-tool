# 儲存庫收斂與 Agent 工具層補齊計畫

更新日期：2026-08-30  
狀態：已完成；使用者已於 2026-08-30 驗證並授權提交本輪改動  
目前步驟：提交後執行完整 `ReleaseCandidate`，取得對應新候選內容的收據

## 目標

根提交完成後，把三件事收乾淨：歷史文件不再依來源專案分目錄、`README.md` 成為真正的專案入口、
Claude Code 的執行前攔截與收尾提醒實際存在，而不是只寫在 `AGENTS.md` 裡。

## 使用者已確定的方向

- 歷史文件按時間軸完全重組，取消 `je-testing/` 與 `new-je-tool/` 兩個目錄。來源專案的名稱對後續開發
  沒有意義。
- Agent 工具層只補設定，不建立 skill。這推翻了先前「四個目錄沒有必要內容時維持空目錄」的裁定，
  但只推翻設定的部分。
- 自主測試以 Claude Code hook 承接，不加 GitHub Actions。CI 仍依 `development-guide.md` 的順序，
  等遠端讀回完成後再從適合遠端環境的公開命令設計。
- 變異測試由 agent 研究必要性後決定。研究結論是移除，理由記在 `development-status.md` 的延後事項。

## 本次範圍

- `docs/history/` 依時間軸重組：開發紀錄合併為單一檔案，設計書與證據併入同一個 `specs/`，
  被取代的舊版本收進 `superseded/` 並以世代月份命名。
- `README.md` 改寫為一般專案 README：環境需求、建置、啟動、測試、目錄結構、文件順序與兩條邊界。
- 修正三處已確認過時的事實，並把其中兩類加入 `Documentation` 的 `forbiddenClaims`，避免復發。
- 補上 `.claude/settings.json` 的 `permissions` 攔截與三個唯讀 hook，以及 `.vscode/` 的工作與偵錯設定。
- 移除已失效的變異測試殘留。

## 不在本次範圍

- 不讀取或執行私人案件。
- 不改產品業務行為、畫面、資料庫 schema 或報表內容。
- 不建立 project-owned skill，也不啟用任何 Claude Code plugin。
- 不建立 CI 流程。
- 不改寫歷史條目的文字、數字、條件、例外或使用者裁定；合併只重新排序並補分界說明。

## 已完成的工作

| 工作 | 狀態 |
|:---|:---|
| 歷史文件依時間軸重組 | 已完成 |
| 開發紀錄合併與粒度分界說明 | 已完成 |
| 指向舊歷史路徑的八個連結修正 | 已完成 |
| `README.md` 改寫 | 已完成 |
| 三處過時事實修正並加入 `forbiddenClaims` | 已完成 |
| `.claude/settings.json` 攔截層 | 已完成 |
| 三個 Claude Code hook | 已完成 |
| `.vscode/tasks.json` 與 `launch.json` | 已完成 |
| `agent-compatibility.md` 記載攔截層與 hook | 已完成 |
| 移除失效的變異測試殘留 | 已完成；agent 的刪除嘗試被本機權限層擋下，由使用者親自執行 |
| 重建候選清單 | 已完成；1,199 個路徑，34 增 30 刪，與 Git 未忽略檔案完全一致 |
| 跨 agent 相容性查核 | 已完成，見下節 |

## 歷史文件的合併依據

兩份開發紀錄的日期不是完全不重疊。早期紀錄涵蓋 2026-06-04 到 2026-06-23，是逐日條目；後期紀錄涵蓋
2026-06-20 到 2026-08-26，但 2026-07-08 以前的內容是事後補寫的區間摘要。唯一重疊的是
「2026-06-20 至 2026-06-29」那筆摘要，其中 2026-06-24 到 2026-06-29 只存在於該摘要。

因此合併方式是保留兩份檔案各自的內部順序直接接續，不重新排序個別條目，並在交界處加上分界說明。
這樣沒有任何條目被改寫或遺漏：81 個條目全數保留。

## 變異測試的研究結論

工具本身仍可用：`dotnet-stryker` 目前版本為 4.16.0，需要 .NET 10 runtime，Microsoft Testing Platform
runner 仍是 preview。所以移除的理由不是技術不可行，而是它在 `je-tool` 已經失效且不再回答獨立問題。

三項證據：

1. 它從來不是關卡。`stryker-config.json` 的 `thresholds.break` 是 `0`，不會讓任何步驟失敗；它是對四個
   檔案的週期性量測。
2. 引進它時要解決的問題已由 harness 機器化。2026-06-18 的紀錄寫明它是為了回答「測試到底有沒有效」
   而引進，因為前一輪被審查抓到兩次測試通過、但斷言是空的。同一輪還引進回應契約鎖與誠實跳過，這兩項在
   `je-tool` 已升級為每次執行都強制：`Public` 要求至少找到 3,000 個案例，跳過的測試必須逐一登記在
   `public-skip-policy.json` 並比對跳過原因的雜湊；`Focused` 選不到測試或遇到跳過就失敗。
3. 殘留本身是壞的。`JET.Mutation.Tests` 不在 `JET.slnx` 內，沒有任何建置路徑會碰到它；它的
   `AssemblyName` 又設成 `JET.Tests`，與正式測試專案同名；而安裝工具用的 `.config/dotnet-tools.json`
   從未遷移，所以指令根本無法還原。

誠實記下現行 harness 答不了的部分：上述守衛能證明測試有跑、沒有被偷偷跳過，證明不了斷言夠嚴。
那個缺口目前靠 first-red 紀律以流程補。重啟條件記在 `development-status.md` 的延後事項表。

## 本輪驗證

- `Documentation`：`passed`。22 份受管文件全數檢查，0 error、0 warning，未讀取私人路徑。
  新增的 `stale-unborn-main` 規則在第一次執行就抓到 `docs/harness.md` 第 188 行第三處過時事實，
  修正後才通過；這是規則實際生效的證據，不是事後補寫。
- `Contract`：`passed`。基本鎖、收據與清理正常，未讀取私人路徑。
- 驗證框架契約測試：372 assertions、25 個情境全數通過，與本次改動前相同。
- 全部受管文件與 `legacy/` 筆記的相對連結逐一解析，沒有指向不存在的路徑。移動造成的八個失效連結
  與一個深度錯誤的相對路徑都已修正。
- 三個 hook 都以實際輸入執行過，輸出為合法 JSON；受管文件與非受管文件的分支行為都確認過。
- 本輪沒有執行 `Build`、`Focused`、`Public`、`Provider`、`Package`、`Gui`、`Excel`、`PrivateCase` 或
  `ReleaseCandidate`。產品程式沒有改動，但候選內容有變，這些命令的舊結果不能沿用。

## 第二輪：依 harness engineering 共識重排入口文件（2026-08-30）

使用者指出 `README.md` 不該有「給 AI Agent 的規則」段落，並要求參考 2026 年的 harness engineering
做法重排專案入口。本輪查閱了 OpenAI、Martin Fowler、Anthropic 的 harness 文章、CLAUDE.md 三層分工
整理、兩個 harness 範本儲存庫，並補搜 2026 年 6 月後的新做法。硬約束不變：技術棧與
Thin-Bridge Action-Dispatcher、Application CQRS、Clean Core、集合式規則。

採納：

- **`AGENTS.md` 當地圖不當百科**（OpenAI 共識，約 100 行內）。從 131 行規則全文改寫為 83 行：專案
  一句話、常用命令、不可協商的邊界（機敏資料、Git、架構）、歧義處理、跨 session 續接、導航表、收尾。
  被移出的細則逐條確認在 `docs/` 有唯一的家後才移除；缺的四條寫作規範補進 `docs/README.md`。
- **`docs/` 當 system of record**。原本就是；本輪把「規則正文只在一處」貫徹到入口檔。
- **README 回歸標準 GitHub 佈局**（簡介、功能、需求、快速開始、測試、結構、文件、參與開發、授權）。
  AI Agent 規則段落整段移除，改為「參與開發」一節的一句話指向 `AGENTS.md`。
- **新增 `CONTRIBUTING.md`**（GitHub 慣例的人類開發者入口）。純導引不立規則，指向既有文件。
- **命令進入口檔**（AGENTS.md 規範共識：build/test 命令是入口檔的必要內容）。`AGENTS.md` 新增常用
  命令區塊；先前的版本連 `tools/verify.ps1` 的存在都要讀到測試段落才知道。
- **guides 與 sensors 的分工**（Fowler）。盤點結論是本儲存庫的 sensors 本來就強：verify.ps1 收據、
  Documentation 的 forbiddenClaims、Claude hooks 都是回饋層；本輪主要是把 guides（入口文件）修薄。

否決：

- **feature list JSON**（Anthropic 長時程模式）：那是綠地專案從零長功能的機制。JET 是已驗收的成熟
  產品，剩餘範圍由 `idea-replacement-scope.md` 與延後事項表追蹤，再造一份 pass/fail 清單是重複。
- **skills 化**（三層分工的中層）：本儲存庫的程序類知識已在 `docs/` 且跨工具可讀；skill 只有
  Claude（`.claude/skills/`）與 Codex（`.agents/skills/`）各自的目錄，做了就是兩份入口要同步，前代
  專案為此養了一套對稱守衛。等某個程序高頻到 context 成本可觀再說。
- **「描述現況的文件移出 repo、只留測試當規格」**（部分 2026 文章主張）：JET 的業務規則來自已退役
  的 IDEA 流程，測試只鎖行為不解釋審計理由；`jet-guide.md` 這類文件正是不可再生的 system of
  record，移出等於丟掉唯一的來歷解釋。
- **候選清單改名**：`docs/first-root-commit-candidate.txt` 名稱已過時，但它被 `lanes.json`、
  `JetHarness.psm1` 的字面驗證與 372 項契約測試釘住。提交前動 sensor 層的釘子不值得，先在
  `docs/README.md` 導航表標注實際角色，改名列為日後獨立小案。
- **PostToolUse 自動格式化**（TypeScript 生態的常見做法）：`dotnet format` 每次編輯跑一次要數秒，
  回饋速度不成立；格式由 `.editorconfig` 與建置警告把關。

## 第三輪：背景脈絡遷移補洞（2026-08-30）

使用者指出擱置事項與開發背景仍留在 `new-je-tool`，接手時不該需要重新追溯。逐項比對
`new-je-tool` 現行文件與 `je-tool` 後補上：

- `README.md` 移除「參與開發」與「沿革與授權」，回歸純人類讀者的專案介紹；新增「技術與定位」一節
  說明架構原則與資料庫分工。
- 新增 [`../sqlserver-enterprise-deferred.md`](../../sqlserver-enterprise-deferred.md)：四個已確認的多人
  安全缺口與企業驗收範圍原本只存在於 `history/superseded/`（定位是描述過去），但它們是現行待辦，
  回收為現行文件。歷史對照表同步修正。
- `development-status.md` 延後表補四列（SQL Server 企業多人環境、預篩選後續收斂、audit log 查詢與
  保留政策、KCT 保存情境重驗契約），KCT 列補回「legacy 來源已查無 KCT 名稱與 A–J 原始清單」的
  追查結論；新增「已知技術債」節（R5 lineage 裁決範圍、CSV 重偵測、磁碟與 WAL、PBC 家族未遷入）。
- `jet-guide.md` 補三段：§4 母體判定唯一權威面（2026-08-20 裁定）、§6 KCT A–J 主線與對應位置、
  §8 SQLite／DuckDB 選型理由、可攜性前提與本地刪案留痕不做的裁定。
- `AGENTS.md` 新增「動工前先讀」任務路由表，讓 agent 依要碰的區域找到對的背景文件。

## 第四輪：事實保真防線（2026-08-30）

使用者發現 agent 在改寫文件時捏造事實（延後表 KCT 列把原紀錄的 `ideascript.bas`、`JE_Tool.ism` 默默
替換成現行檔名，等於宣稱一筆不存在的查核），並提供 Codex 側「如何避免 agent talk shit」的研究對話
要求接續處理。該對話的結論：問題核心是改寫時的事實保真，不是風格；風格層（污染詞 warning、寫作
規範）Codex 側已落地，缺的是事實層防線。本輪補上：

- 修正被捏造的 KCT 列：原始查核對象逐字還原，新舊檔名對應依 `legacy/README.md` 另行寫明。
- `AGENTS.md` 的不可協商邊界新增「事實保真」段：先找原文再改寫、名稱逐字照抄不得等義替換、
  找不到出處就刪掉或標待確認、「查不到」是合法答案。這是 Anthropic 官方減幻覺指引（直接引文
  接地、引用後驗證、允許不確定）與該對話「事實保真」規則的合併。
- `docs/README.md` 寫作規範新增引用保真與新名詞准入規則（只有程式、UI、資料結構、外部標準既有的
  名稱，或反覆使用且能一句白話定義的概念才可命名）。
- `managed-doc-notice` hook 的收尾清單把事實保真升為第一項，在每次受管文件編輯後立即出現。
- `.claude/settings.json` 新增 `ask`：編輯 `docs/history/**` 要先問過人——歷史文件是引用的原文來源，
  被默默改寫會讓事實保真失去對照基準。

刻意不做：每次編輯攔截的 LLM 事實查核 hook（成本高、會把判斷外包給另一個會幻覺的模型）、大型禁用
詞表與句長硬性失敗（該對話已論證容易誤傷且會被換詞繞過）、修改 harness 程式加自動事實比對（提交前
不動 sensor 層；且語意層面的捏造無法用機械比對可靠攔截）。機制降低錯誤率，最後一道防線仍是人工
驗收與轉述測試。

## 跨 agent 相容性查核（2026-08-30）

- GitHub Copilot 官方支援矩陣重新查證：VS Code、Copilot coding agent 與 Copilot CLI 原生讀
  `AGENTS.md`；Visual Studio、JetBrains 與 GitHub.com 的 Chat 只讀 `.github/copilot-instructions.md`。
  薄轉接檔因此必須保留安全摘要，不能縮成純指標。
- 載入預算量測：`AGENTS.md` 11,647 bytes，佔 Codex 32 KiB 合併預算約 36%；Claude Code 實際載入
  `CLAUDE.md` 9 行加 `AGENTS.md` 131 行共 140 行，在官方建議的 200 行內。
- 規則一致性修正：`.claude/settings.json` 的 deny 擋了 `git add -u`，但 `AGENTS.md` 的禁列清單原本
  沒有這一項。攔截層不得超出共用權威，因此把 `-u` 補進 `AGENTS.md` 與 Copilot 轉接檔的安全摘要。
- `.claude/`、`.vscode/` 的內容經確認只影響 Claude Code 與 VS Code 本身，對 Codex、Copilot 是不可見
  的加層；`AGENTS.md` 已載明它們不得成為規則或驗收前提。

## 提交前的最後一步

`ReleaseCandidate` 在 HEAD 已存在時要求工作樹完全乾淨，所以它只能在本輪改動提交之後執行。提交需要
使用者驗證內容後另行授權；提交完成後執行完整 `ReleaseCandidate`，取得涵蓋本輪候選內容的新收據。

## 驗收條件

1. `docs/history/` 不再出現來源專案名稱作為目錄或檔名，且開發紀錄是單一時序檔案。已達成。
2. `README.md` 能讓沒看過這個專案的人裝好環境、建置、啟動並跑測試。已達成。
3. `AGENTS.md` 的禁令有對應的 Claude Code 執行前攔截，且攔截範圍不超出 `AGENTS.md`。已達成。
4. 新 session 不必問人就能知道實際的 branch、HEAD、工作樹與最近一次各命令的驗證結果。已達成。
5. 已確認過時的事實不能只靠人記得，要由 `Documentation` 擋住。已達成，並已實際攔截一次。
6. 候選清單與實際未忽略檔案一致。已達成：使用者移除變異測試殘留後重建為 1,199 個路徑。

## 中斷時從這裡繼續

本計畫的實作與驗證都已完成，使用者已授權提交。提交後的下一步是執行完整 `ReleaseCandidate` 取得
對應新候選內容的收據；是否已完成要查當時的 Git 與收據，不能從本文件推定。接手的新 session 先讀
`AGENTS.md` 與 `docs/development-status.md`，再核對實際 branch、HEAD 與工作樹。沒有當次明確指令時
仍不得 stage、commit、push 或執行私人案件。

## 已知但本次未處理

- `Documentation` 不檢查 Markdown 相對連結是否存在。本輪的八個失效連結是用一次性腳本找出來的，
  不是 harness 抓到的。要不要把連結解析加進 `JetDocumentationCheck.ps1` 尚未裁定。
- `docs/first-root-commit-candidate.txt` 的名稱在根提交完成後已不精確，它現在的實際角色是
  `ReleaseCandidate` 的檔案清單。改名會動到 `JetHarness.psm1` 的字面比對，本次沒有改。
- `documentation-report.json` 的 `manualReview` 項目在報告檔中是亂碼，命令的 stdout 摘要則正常。
  這是報告寫檔時的編碼問題，本次沒有處理。
- `.agents/` 與 `.config/` 仍是空目錄。前者要等決定是否為 Codex 另建 skill 入口；後者在變異測試退出後
  沒有用途。
