# JET Agent 指南

JET 是 Windows 桌面審計工具：C#／.NET 10、WinForms + WebView2、單一執行檔；大量資料留在 SQLite、
DuckDB 或 SQL Server 以集合式 SQL 處理。本檔是所有 AI Agent（Claude Code、Codex、GitHub Copilot、
VS Code）的共用入口，只保存不可協商的邊界與導航地圖；細節都在指向的文件裡，不在此複寫。

## 常用命令

```powershell
pwsh -NoProfile -File tools/verify.ps1 -Command Build -Configuration Debug
pwsh -NoProfile -File tools/verify.ps1 -Command Focused -Filter '<測試類別或方法名稱片段>'
pwsh -NoProfile -File tools/verify.ps1 -Command Public          # 完整公開測試
pwsh -NoProfile -File tools/verify.ps1 -Command Documentation   # 改過文件後必跑
dotnet run --project src/JET/JET/JET.csproj -c Debug            # 啟動桌面程式（診斷用）
```

正式驗證只從 `tools/verify.ps1` 進入；直接 `dotnet test` 只能用於診斷，不能取代正式紀錄。全部命令、
結束碼與較重的驗證路線見 `tools/README.md`。

## 不可協商的邊界

**機敏資料**

- 真實案件、客戶資料與由真實案件產生的輸出，不得進入 Git、文件、測試碼、TRX、patch、聊天或外部服務。
- `data/test-case/`、`data/temporary-test-case/`、`data/legacy-parity-work/` 等私人資料根目錄，未獲當次
  明確授權不得讀取或掃描；真實案件只透過明示授權的 `PrivateCase` 命令注入，一般驗證不得順便搜尋。
- 證據不得寫出私人絕對路徑、公司名稱、工作表、欄位、科目、傳票、人員或金額。測試優先使用程式生成的
  合成資料；發現疑似機敏內容時停止擴大讀取，只回報檔案與風險類型，等待使用者裁定。

**Git**

- 沒有使用者明確指令時，不得執行或主動提議 reset、checkout、clean、stash、stage、commit、push 或變更
  remote。計畫完成、測試通過或工作樹可提交都不是授權。
- 禁止 `git add -A`、`git add --all`、`git add -u`、`git add .` 與 `git write-tree`；獲准 stage 也只加入
  使用者確認的明確路徑，避免忽略規則改變時把私人資料寫入 Git object store。
- Commit 訊息與 PR 內文不得自行加入 `Co-Authored-By`、`Generated with` 等 AI 署名；內容只寫結果、必要
  理由、風險與實際驗證，不逐檔重述 diff。

**事實保真**（適用於所有改寫、轉述與遷移）

- 引用歷史紀錄或他處文件時，先找到原文再改寫；查核對象、檔名、數字、日期與裁定逐字照抄，不得改成
  「等義」的新名稱或正規化寫法。名稱對應要另外寫明，不能默默替換。
- 每個寫進文件的聲明都要有本次實際讀到的出處；找不到出處的聲明刪掉或標成待確認，不以合理推測填空。
  「查不到」是合法答案，比看起來完整的猜測有價值。
- 不自行創造專案名詞，不把暫時性描述升格成正式規範，不刪除條件、例外與不確定性。

**架構**（完整規格見 `docs/jet-guide.md`）

- UI 只負責操作與呈現；審計判斷留在 Domain、AuditCore、Application 與資料庫實作邊界。
- 前端與 C# 之間只走既有 `JetApi` action channel；修改 action 時同步核對 handler、registry、文件與測試。
- 大量資料留在資料庫集合式處理；前端與 session state 只接收摘要、metadata 與有界分頁。
- 金額、日期、欄位配對與 provider 等價規則不得只為單一案件或單一 provider 特化。

## 有歧義時

先查現行 `src/` 與測試，再查 `docs/` 現行文件，最後才是 `docs/history/` 與 `legacy/`；完整追溯順序見
`docs/business-logic-provenance.md`。儲存庫內證據仍支持兩種會改變對外行為的答案時，列出差異請使用者
裁定，不得因實作方便自行選邊，也不能只憑摘要自行補完。

## 跨 session 續接

- 接手先讀 `docs/development-status.md` 的「目前大型計畫」與「已知但延後的事項」，再依
  `docs/development-workflow.md` 核對實際 branch、HEAD 與工作樹；不能只依長期文件判斷 Git 狀態。
- 需要跨多個步驟或 session 的工作，一次只保留一份現行大型計畫（`docs/specs/`）。進度、裁定與下一動作
  要寫進計畫檔，不能只存在聊天、Agent memory 或被忽略的執行紀錄裡。
- 大型工作結束或使用者詢問未完成事項時，逐項回報「已知但延後的事項」的背景、邊界與重啟條件。
- 使用者明確呼叫 `$jet-converge` 時，完整讀取 `.agents/skills/jet-converge/SKILL.md`；一般狀態查詢或範圍
  已明確的小修正，不自動展開完整收斂訪談。
- 需要整理多個 session 的觀點、需求、功能或待完成事項時，依
  `.agents/harness/convergence-and-memory.md` 把 repository 當專案記憶，task／memory 只作查找線索。

## 地圖

| 主題 | 文件 |
|:---|:---|
| 專案目的、服務順序與正式環境限制 | `docs/project-context.md` |
| 系統規格與資料流（單一事實來源） | `docs/jet-guide.md` |
| 前端與 C# 的 action 契約 | `docs/action-contract-manifest.md` |
| 畫面結構與操作步驟 | `docs/jet-frontend-description.md` |
| 驗證框架的責任、命令與判定界線 | `docs/harness.md`、`tools/README.md` |
| `data/` 工作簿與 `legacy/` 的角色和限制 | `docs/data-and-legacy.md` |
| 文件寫作規範與收尾檢查 | `docs/README.md` |
| 各工具的載入方式、預算與 Claude Code 強制層 | `docs/agent-compatibility.md` |
| 跨 session 觀點、待完成事項與專案記憶如何收斂 | `.agents/harness/convergence-and-memory.md` |
| 來源專案關係與已裁定的遷移邊界 | `docs/repository-lineage.md` |

**動工前先讀**（依這次要碰的東西）：

| 要做的事 | 先讀 |
|:---|:---|
| 改驗證、預篩選、篩選或抽樣規則 | `docs/jet-guide.md` §4–§6；精確規則與 KCT A–J 對應在 `docs/history/superseded/jet-guide-2026-08.md`；衝突裁決走 `docs/business-logic-provenance.md` |
| 改 action、payload 或前端 | `docs/action-contract-manifest.md`＋`docs/jet-frontend-description.md` |
| 改報表輸出或範本 | `docs/jet-guide.md` §7＋`docs/data-and-legacy.md` |
| 碰資料庫實作或 SQL Server | `docs/jet-guide.md` §8；企業多人範圍與已知安全缺口在 `docs/sqlserver-enterprise-deferred.md` |
| 與 IDEA／VBA 舊行為比對 | `docs/jet-guide.md` §17＋`legacy/jet-legacy-notes.md`；差異只分五類，不自創例外 |
| 立案或接手大型開發 | `docs/development-status.md`＋`docs/development-workflow.md` |

`CLAUDE.md` 與 `.github/copilot-instructions.md` 是薄轉接檔，不建立第二套規則。本檔是 context 不是
強制層；`.claude/` 的 permissions 與 hooks 把上述禁令升級為 Claude Code 的執行前攔截，但只在
Claude Code 生效，不得成為規則、驗收條件或架構判斷的前提。

## 收尾

- 回報實際讀取、修改、驗證、跳過與未能確認的項目；未執行 build、測試、GUI、Excel 或真實資料驗收時
  必須明說。
- 修改受管文件後執行 `tools/verify.ps1 -Command Documentation`，再把改動段落完整讀一次。
- 沒有明確授權時，最多整理到可提交狀態，不進行暫存、提交或推送。
