# JET Agent 指南

JET 是 Windows 桌面審計工具，用 C# 與 .NET 10 寫成，WinForms 視窗承載 WebView2 網頁畫面，發布成單一執行檔。
大量資料留在 SQLite 案件資料庫，用集合式 SQL 處理；DuckDB 與 SQL Server 在短期上線期間暫時移除（裁定 2026-11-06-08）。
這份檔案是所有 AI 編碼工具（Claude Code、Codex、GitHub Copilot、VS Code）的共用入口，只寫不可協商的邊界，
以及細節該去哪裡找。給 Agent 讀的流程文件放在 `.agents/`，給人讀的文件放在 `docs/`；兩邊都不複寫這裡的規則。

## 常用命令

```powershell
pwsh -NoProfile -File tools/verify.ps1 -Command Build -Configuration Debug
pwsh -NoProfile -File tools/verify.ps1 -Command Focused -Filter '<測試類別或方法名稱片段>'
pwsh -NoProfile -File tools/verify.ps1 -Command Public          # 完整公開測試
pwsh -NoProfile -File tools/verify.ps1 -Command Documentation   # 改過文件後必跑
dotnet run --project src/JET/JET/JET.csproj -c Debug            # 啟動桌面程式，供使用者自己操作；Agent 不在使用者的桌面執行
```

正式驗證只從 `tools/verify.ps1` 進入，它負責共享鎖、收據與清理。不要直接執行 `dotnet test`，各平台的設定也會擋下它；
需要診斷單一測試時用 `Focused` 篩選，或直接執行建置出來的測試程式。全部命令、結束碼與較重的驗證路線見
`tools/README.md`。

## 不可協商的邊界

**機敏資料**

- 真實案件、客戶資料與由真實案件產生的輸出，不得進入 Git、文件、測試碼、測試輸出、patch、聊天或外部服務。
- 本機私人資料目錄（`data/test-case/`、`data/temporary-test-case/`、`data/legacy-parity-work/` 等）可以在這台電腦讀取，
  用來核對業務邏輯或執行 `PrivateCase` 驗證；不得寫入、不得複製進儲存庫，也不得把內容送進聊天或外部服務。
  一般驗證不順便搜尋這些目錄，真實案件只透過 `PrivateCase` 命令注入。
- 證據與文件不寫私人絕對路徑、公司名稱、工作表、欄位、科目、傳票、人員或金額。測試優先使用程式生成的合成資料；
  發現疑似機敏內容時停止擴大讀取，只回報檔案與風險類型，等使用者裁定。

**Git**

- 沒有使用者明確指令時，不執行也不主動提議 reset、checkout、clean、stash、stage、commit、push 或變更 remote。
  計畫完成、測試通過或工作樹可提交，都不是授權。
- 使用者說要提交時，提交後一併推送，不分成多次提交再一起推送。
- 禁止 `git add -A`、`git add --all`、`git add -u`、`git add .` 與 `git write-tree`；獲准 stage 時也只加入使用者確認的
  明確路徑，避免忽略規則改變時把私人資料寫進 Git。
- Commit 訊息用繁體中文並保持精簡：標題一行說清楚改了什麼，內文只寫主要變更與必要原因，保留足以追溯變更目的的資訊即可。
  避免詳細描述實作過程、驗證細節與逐項修改內容，也不加 `Co-Authored-By`、`Generated with` 等 AI 署名。PR 內文同樣規則。

**事實保真**（適用於所有改寫、轉述與遷移）

- 引用歷史紀錄或他處文件時，先找到原文再改寫；查核對象、檔名、數字、日期與裁定逐字照抄，不改成「等義」的新名稱。
  名稱有新舊對應時另外寫明，不默默替換。
- 每個寫進文件的聲明都要有本次實際讀到的出處；找不到出處就刪掉或標成待確認，不以合理推測填空。「查不到」是合法答案。
- 不自行創造專案名詞，不把暫時性描述升格成正式規範，不刪除條件、例外與不確定性。

**寫給人的文字**（文件、計畫檔、commit 訊息與回覆都算）

- 讀者是第一次接觸專案的維護者。用台灣常見的日常中文寫完整句子；固定命令、程式名稱與必要術語保留英文，
  第一次出現時說用途。
- 回覆使用者一律用繁體中文，長對話或對話壓縮之後也一樣。交給子代理、工作流程與其他 session 的指示也用繁體中文寫，
  並要求對方用繁體中文回報；轉述給使用者時不改成英文（使用者 2026-11-09 裁定）。
- 不用 `＋`、`／`、`→` 把名詞串成句子；不用兩層括號；一句只講一件事。只在讀者需要知道「這是什麼時候定的、
  取代了什麼」時才寫日期與計畫檔名，不每句都掛。
- 驗證框架的工作用語（receipt、gate、lane、`紅燈`、`綠燈`）不進面向人的文字，改說收據、檢查、路線、通過、失敗。
- 收據是 `tools/verify.ps1` 每次執行時留在 `artifacts/` 的紀錄，只給驗證框架與 Agent 查核用，不寫進專案文件與程式碼
  （裁定 2026-11-11-12）。要寫驗證結果或第一次失敗時，用文字寫命令、範圍與結果，不寫收據編號。
- `src/JET` 的原始碼會交給公司內部審查。註解只寫程式現在做什麼、為什麼這樣做；不寫裁定編號、測試收據、開發日期、
  計畫與階段、Agent，也不寫 `docs/`、`tools/` 這些其他目錄的路徑（裁定 2026-11-10-11）。要查某則裁定落實在哪裡，
  看 `docs/rulings.md` 的落實位置。
- 寫完重讀一次，找形近錯字，確認讀者能一句話說出誰做了什麼、結果如何。要整段改寫或收到 style warning 時，
  依 `.agents/skills/jet-readable-docs/SKILL.md` 一段一段處理。

**測試**

- 短期上線版本只支援 SQLite，日常開發與測試只用 SQLite。DuckDB 與 SQL Server 的程式、測試與 `Provider` 路線暫時刪除，
  還原點是提交 `7e88b7b`（裁定 2026-11-06-08）。
- 本機 SQL Server 平時保持停止，只有使用者當次明示需要時才啟動，不因環境有連線設定就自行啟動。當次用到時，
  不論成功、失敗或中止，結束前都要停止服務並確認沒有留下 `sqlservr.exe`；步驟見 [`tools/README.md`](tools/README.md#日常資料庫測試與服務收尾)。
- 不為了讓測試通過而放寬、刪除或跳過既有斷言，也不把預期值改成由被測程式算出來；新增測試不受此限。
  要改既有測試時先寫明原因，並保留第一次失敗的證據；原因與第一次失敗的情形用文字記在 `docs/history/test-change-records.md`，
  不寫進測試的註解。
- 改產品行為時先寫測試，確認它因為斷言不成立而失敗，再做讓它通過的最小修改，通過後才整理程式；
  順序與例外見 `.agents/harness/development-workflow.md` 的「測試先行的開發循環」（裁定 2026-11-10-18）。
- 不要每次修改都跑完整 `Public`。先依改動範圍跑相關測試，失敗修正後只重跑受影響的部分；相關修改整合完成、確實需要完整驗證時再跑。
- 測試不能干擾使用者操作電腦（使用者 2026-10-05 裁定）。`Gui` 把 JET 開在另一個 Windows 桌面，使用者看不到視窗，
  點擊與打字也不會落到 JET 上；完整執行需要幾分鐘並占用 CPU。`ReleaseCandidate` 也會執行它。日常開發用 `Public`（含前端測試）、
  `Contract -ContractScenario FrontendMapping` 與瀏覽器主機確認畫面。`Gui` 只在提交、交付或使用者確認版本要整合時執行。
  除非使用者當次要求，不用 computer use 這類會操作實體滑鼠與鍵盤的工具。
- 量測效能、截圖或診斷需要開 JET 時也一樣：不在使用者的桌面開 JET 視窗，沿用 `Gui` 另開 Windows 桌面的做法，
  見 `tools/harness/gui-driver/OwnedGuiRun.cs`；做不到時先問使用者（使用者 2026-11-09 裁定）。

**架構**（完整規格見 `docs/jet-guide.md`）

- UI 只負責操作與呈現；審計判斷留在 Domain、AuditCore、Application 與資料庫實作邊界。
- 前端與 C# 之間只走既有的 `JetApi` action 通道；修改 action 時同步核對 handler、註冊、文件與測試。
- 大量資料留在資料庫集合式處理；前端與 session 狀態只接收摘要、中繼資料與有界分頁。
- 金額、日期、欄位配對與資料庫等價規則不為單一案件或單一資料庫特化。
- 預設審計員可信：不為防範審計員自己的操作加關卡；只有第六步匯出的正式底稿不得被 JET 默默改寫或覆蓋，
  其他檔案與中間結果能重算就不擋人。要新增會擋住使用者的錯誤時，先寫出使用者接下來能做什麼；
  原則與起因見 `docs/project-context.md`。
- JET 讓審計員不必操作資料庫，就能直觀地篩選 GL 與 TB。程式只為了確保資料完整性設防呆，不因審計業務擋下；
  案件資料的缺失或風險，由審計員在底稿備註，不由系統決定。
- 修正要考量全局架構：在定義它的地方修根本原因，並找出同類問題一起處理，不只修發現問題的那一處。

**名稱**

- 畫面、報告、底稿與檔名，以及程式、action 欄位、資料庫與測試的名稱，只用 `docs/glossary.md` 寫的名稱。
  新的名稱先進名詞表，經使用者確認後才用。報告與底稿上照舊版範本的欄名不改。
- 改一個概念時，畫面、輸出、程式、文件與測試一起查，不只改被點名的地方。

## 有歧義時

審計業務沒有明確指定時，依序看三個來源：使用者的裁定最優先，查 `docs/rulings.md`；沒有裁定時照 IDEA 版 JET 的作法；
舊版沒有涵蓋的，參考 KCT 小組提出的作業方式與期望。國際準則只用來理解名詞，或找出要問使用者的問題，不能單獨決定規則。
三者都沒有答案時，先問使用者。

Agent 不自行更動審計邏輯，基本的審計邏輯要和舊版 JET 吻合。舊版的 GL 母體前處理有不一致的地方，照舊版核對時，
要分辨是審計規則，還是舊版程式的限制。新版 JET 也有部分邏輯刻意和 IDEA 版不同；比出差異時先照 `docs/jet-guide.md`
第 17 節分類，不把刻意的更新當成 JET 缺陷修掉，分不出來時先問使用者（裁定 2026-11-11-13）。

要找現行規則寫在哪裡時，先查現行 `src/` 與測試，再查 `docs/` 現行文件，最後才是 `docs/history/` 與 `legacy/`；完整順序見
`docs/business-logic-provenance.md`。證據仍支持兩種會改變對外行為的答案時，列出差異請使用者裁定，
不因實作方便自行選邊。

## 跨 session 工作

- 開工可先執行 `pwsh -NoProfile -File tools/verify.ps1 -Command Context`，取得現行計畫、裁定總表與名詞表的則數、工作樹與近期驗證。
  它是唯讀導覽，不是門檻。
- 需要跨多個步驟或 session 的工作，一次只保留一份現行大型計畫（放在 `docs/specs/`）。進度、裁定與下一動作寫進計畫檔，
  不只存在聊天或 Agent 記憶裡。計畫裡的需求與裁定逐字保留使用者原話並附日期；改寫要先獲授權。
  分工、停止與續接方式見 `.agents/harness/development-workflow.md`。
- 計畫的「下一步」是工作順序；本輪交付範圍依使用者任務說清楚，不把做完一個步驟當成整輪完成。
- 大型工作結束或使用者詢問未完成事項時，逐項回報 `docs/development-status.md` 的「已知但延後的事項」。
- 本專案的 `security-audit` 執行紀錄放在 `docs/security/run-<N>/`，`<N>` 從 1 起取下一個未使用的整數。
  執行完整稽核前，先讀 [`docs/security/README.md`](docs/security/README.md)。
- 使用者明確呼叫 `$jet-converge` 時，完整讀 `.agents/skills/jet-converge/SKILL.md`；一般查詢或小修正不展開收斂訪談。
  多個 session 的觀點與待辦如何收斂，見 `.agents/harness/convergence-and-memory.md`。

## 地圖

| 主題 | 文件 |
|:---|:---|
| 專案目的、服務順序與正式環境限制 | `docs/project-context.md` |
| 系統規格與資料流（單一事實來源） | `docs/jet-guide.md` |
| 使用者的裁定、原話與出處 | `docs/rulings.md` |
| 業務概念在畫面、程式與資料庫各用的名稱 | `docs/glossary.md` |
| 架構圖表與 agent 分析流程 | [圖表入口](docs/architecture/README.md)、[繪圖流程](.agents/harness/architecture-documentation.md) |
| 前端與 C# 的 action 契約 | `docs/action-contract-manifest.md` |
| 畫面結構、操作步驟與畫面用語 | `docs/jet-frontend-description.md` |
| 驗證命令、結束碼與各路線 | `tools/README.md` |
| 驗證框架的責任與判定原則 | `.agents/harness/harness.md` |
| 大型開發如何立案、續接與關閉 | `.agents/harness/development-workflow.md` |
| 各 AI 工具如何載入規則、平台設定與 hook | `.agents/harness/agent-compatibility.md` |
| 前端設計模式與瀏覽器預覽 | `.agents/harness/frontend-design-mode.md` |
| SAT 送審文件的標準、寫作界線與交接 | `.agents/harness/sat-documents.md` |
| `data/` 工作簿、`legacy/` 與來源專案 | `docs/data-and-legacy.md` |
| 文件寫作規範與收尾檢查 | `docs/README.md` |
| 面向人的文字怎麼改寫 | `.agents/skills/jet-readable-docs/SKILL.md` |
| 前端外觀的設計方向與 AI 預設樣式禁止清單 | `.agents/skills/jet-frontend-design/SKILL.md` |

**動工前先讀**（依這次要碰的東西）：

| 要做的事 | 先讀 |
|:---|:---|
| 改驗證、預篩選、篩選或抽樣規則 | `docs/jet-guide.md` 第 4 到第 6 節；常用篩選條件十張卡和歷史紀錄裡 KCT A 到 J 的對應在第 6 節，更早的精確舊規則在 `docs/history/superseded/jet-guide-2026-08.md`；衝突裁決走 `docs/business-logic-provenance.md` |
| 改 action、payload 或前端 | `docs/action-contract-manifest.md` 與 `docs/jet-frontend-description.md` |
| 改報表輸出或範本 | `docs/jet-guide.md` 第 7 節與 `docs/data-and-legacy.md` |
| 碰資料庫實作，或接回 DuckDB 與 SQL Server | `docs/jet-guide.md` 第 8 節；兩者的程式留在提交 `7e88b7b`（裁定 2026-11-06-08）；SQL Server 的延後事項、已知安全缺口與暫緩期間的修改，封存在 `docs/history/2026-10-20-sqlserver-archive.md` |
| 與 IDEA 或 VBA 舊行為比對 | `docs/jet-guide.md` 第 17 節與 `legacy/jet-legacy-notes.md`；差異只分五類，不自創例外 |
| 立案或接手大型開發 | `docs/development-status.md` 與 `.agents/harness/development-workflow.md` |
| 改 `docs/SAT/` 的文件、來源或產生腳本 | `.agents/harness/sat-documents.md` 與 `tools/harness/sat-documents/README.md`；只寫公司範本要求的項目，從開發者的角度寫，審查門檻不高於公司的審查規則 |

`CLAUDE.md` 與 `.github/copilot-instructions.md` 只是轉接檔。`.claude/settings.json` 與 `.codex/rules/jet.rules` 把上面的
禁令轉成各平台的執行前攔截，兩者註冊同一份 `.agents/hooks/` 腳本；這些設定只在各自平台生效，不是規則本身。
要改規則先改本檔，再同步各平台設定，見 `.agents/harness/agent-compatibility.md`。

## 收尾

- 回報實際讀取、修改、驗證、跳過與未能確認的項目；沒有執行 build、測試、GUI、Excel 或真實資料驗收時必須明說。
- 修改受管文件後執行 `tools/verify.ps1 -Command Documentation`，再把改動段落完整讀一次。
- 沒有明確授權時，最多整理到可提交狀態，不暫存、提交或推送。
