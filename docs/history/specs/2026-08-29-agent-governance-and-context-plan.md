# JET Agent 治理與專案脈絡補強計畫

更新日期：2026-08-30  
狀態：已完成  
目前步驟：無；後續工作另立計畫，並先核對實際 Git 狀態與延後事項

## 目標

讓 GitHub Copilot、Claude Code、Codex 與 VS Code 的 AI Agent 都從同一份儲存庫規則開始工作，並補齊
大型開發續接、JET 背景、公司環境限制及 IDEA 替代範圍。這些內容要能在新的 session 或換用其他 Agent
後繼續使用，不能依賴單次對話或某一個工具的本機記憶。

## 本次範圍

- `AGENTS.md` 保持為跨工具共用權威；Claude Code 與 GitHub Copilot 只增加薄轉接檔。
- 補回未授權不得進行 Git 寫入、禁止 AI 署名、只 stage 明確路徑及簡短交付說明等規則。
- 建立大型開發的現行計畫與跨 session 續接方式。
- 建立現行專案背景、部署限制、GA／KCT 順序及 IDEA 替代完成條件。
- 收斂 `development-status.md`，不再讓現況入口累積逐次執行日誌。
- 將已確認錯誤的事實與文字風格疑點分開；只有前者能由自動檢查直接判定失敗。
- 讓驗證框架檢查必要的 Agent 轉接檔，並實際比較來源 Git index 前後狀態。

## 不在本次範圍

- 不讀取或執行私人案件。
- 不改產品業務行為、畫面、資料庫 schema 或報表內容。
- 不建立模型專屬角色、外部 MCP、雲端 Agent 或第三方 plugin 依賴。
- 不設定公司正式環境尚未確認的 Windows、Office、安裝權限或資料庫版本。
- 不 stage、commit 或 push。

## 使用者已確定的方向

- 正式環境不是個人電腦，不能假設有高權限或可存取 AI 網路服務。
- 目前先讓 GA 在本機完成 CaseWare IDEA 的 JET 工作；既有 KCT 功能保留，新的 KCT 條件稍後再處理。
- SQL Server 企業中心化方向暫緩；目前以 SQLite／DuckDB 的本機工作為優先。
- 專案文件要讓人類看得懂背景與理由，不用冗長的 Agent 術語取代實際內容。
- Agent 沒有明確授權時不得 commit 或 push，提交與同步內容也不得自行加入 AI 署名。

## 進度

| 工作 | 狀態 |
|:---|:---|
| 查核四種 Agent 的官方指示檔支援 | 已完成 |
| 共用規則與工具轉接 | 已完成 |
| 專案背景、IDEA 範圍與開發續接文件 | 已完成 |
| 現況文件收斂 | 已完成 |
| Documentation 檢查分級 | 已完成 |
| Git index 前後量測 | 已完成 |
| Contract 與 Documentation 驗證 | 已完成 |

## 本輪驗證

- `Documentation`：`passed`；21 份文件與轉接檔全數檢查，0 error、0 warning，未讀取私人路徑。
- 一般 `Contract`：`passed`；基本鎖、收據與清理正常，未讀取私人路徑。
- 驗證框架契約測試：372 assertions、25 個情境全數通過。合成 Git repo 已證明候選快照不改來源 index
  entries，也證明精確 stage 後 fingerprint 會改變。
- 21 份受管文件的相對 Markdown 連結全部存在；三份 PowerShell 檔案與 Documentation JSON 均可解析。
- 第一次根提交候選清單已更新為 1,195 個路徑，和 Git 尚未忽略的檔案完全一致；正式候選驗證開始前的
  來源 index 為空。
- 調整後的完整 `ReleaseCandidate` 已在未設定 `JET_SQLSERVER_CONNECTION` 的狀態執行。第一場只因沙盒無法
  存取 NuGet 而 `blocked`，保留 first-red；獲准在本機 Windows 邊界以完全相同命令 fresh rerun 後通過。
- fresh rerun 逐一通過 Contract、Documentation、Public、Package、兩個 GUI 情境與六份 Excel 往返。
  Public 為 3,390 total／3,383 passed／7 個登錄 skip，Package 為 70／70；1,195 個候選路徑與來源 index
  前後 0 個 entries 完全核對，所有快照與子命令完成清理。
- 本計畫的正式驗證沒有執行 `Provider` 或 `PrivateCase`，也沒有查看私人路徑；stage、commit 與 push 則在
  計畫完成並取得使用者另行授權後才可進行。一般候選通過不能改寫成 SQL Server live provider 或私人案件
  已通過。

## 驗收條件

1. `AGENTS.md` 是唯一完整規則來源；`CLAUDE.md` 與 `.github/copilot-instructions.md` 不另立衝突規則。
2. 驗證框架能檢查必要轉接檔、關鍵指向及文件缺漏。
3. 文字風格疑點只產生 warning；缺檔、必要指向缺漏與已確認過時的事實仍會失敗。
4. `development-status.md` 能在一次閱讀內看出目前狀態、現行計畫、未決事項與 Git 交付邊界，不依賴 ignored
   receipt。
5. 新 session 能只靠儲存庫文件找到背景、目前計畫、最後有效驗證與下一個動作。
6. Contract 與 Documentation 正式命令通過；若未執行較重的產品、GUI、Excel 或私人案件驗證，結案時明說。

目前六項條件均已達到實作要求。使用者於 2026-08-30 提供的早期筆記補足了 2025 年春季課程階段的目標與
IDEA／VBA／Access 取捨，但原件混有敏感資料，只採去識別化摘要，不搬入儲存庫。這份背景已足夠，不再把
正式課程名稱或更細交付紀錄列為待辦。完整 IDEA 責任、KCT 來源及公司正式環境條件仍待確認；Agent 不會
自行填入看似完整但沒有依據的內容。

同日依使用者裁定修正 SQL Server 驗證邊界：產品與獨立 `Provider` 路線繼續保留 SQL Server 相容性；一般
`ReleaseCandidate` 不再以 live SQL Server 為必要條件。修改後的 harness 自測與完整公開候選均已通過，
計畫已由使用者確認完成；第一次根提交與推送另依 2026-08-30 的明示授權執行。

## 中斷時從這裡繼續

本計畫已完成，沒有待續步驟。後續工作先讀取 `AGENTS.md` 與 `docs/development-status.md`，再核對目前 branch、
HEAD、工作樹、遠端及延後事項；需要多步驟執行時另立一份現行計畫。沒有當次明確指令時仍不得 stage、
commit、push 或執行私人案件，也不要把本計畫中的摘要當成實際工作樹證明。
