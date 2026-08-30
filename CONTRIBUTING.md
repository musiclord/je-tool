# 參與 JET 開發

這份文件是人類開發者的入口，只做導引，不另立規則；每個主題的權威都在指向的文件裡。

## 開始之前

1. 依 [`README.md`](README.md) 的快速開始把專案建置起來。
2. 讀 [`docs/project-context.md`](docs/project-context.md) 了解專案目的與正式環境限制。
3. 讀 [`docs/development-status.md`](docs/development-status.md) 確認目前的大型計畫與延後事項，
   避免與進行中的工作衝突。

## 開發流程

- 開發環境、可用命令與測試邊界：[`docs/development-guide.md`](docs/development-guide.md)
- 跨多個工作階段的大型開發如何立案、續接與關閉：[`docs/development-workflow.md`](docs/development-workflow.md)
- 架構規則與資料流：[`docs/jet-guide.md`](docs/jet-guide.md)。UI 只負責操作與呈現，審計判斷留在
  Domain、AuditCore、Application 與資料庫實作邊界；前端與 C# 之間只走 `JetApi` action channel。

## 驗證

正式驗證一律從 `tools/verify.ps1` 進入，命令與結束碼見 [`tools/README.md`](tools/README.md)。
直接 `dotnet test` 只能用於診斷。提交前至少要有涵蓋本次改動範圍的通過收據；改過文件要跑
`Documentation` 命令並把改動段落重新讀一次。

## 文件

文件的分層、寫作規範與完成前檢查見 [`docs/README.md`](docs/README.md)。歷史文件（`docs/history/`）
描述過去，不描述現況；不要把兩者混寫。

## 機敏資料

本專案處理審計案件。真實案件、客戶資料與由真實案件產生的輸出不得進入 Git、文件、測試碼或對外服務；
私人資料根目錄（如 `data/test-case/`）整個由 Git 排除。完整邊界見 [`AGENTS.md`](AGENTS.md) 的
「不可協商的邊界」——那份文件寫給 AI 編碼工具，但機敏資料與 Git 的邊界對人同樣適用。

## 使用 AI 編碼工具

Claude Code、Codex、GitHub Copilot 與 VS Code AI Agent 的共用規則在 [`AGENTS.md`](AGENTS.md)；
各工具的載入方式與 Claude Code 的執行前攔截見
[`docs/agent-compatibility.md`](docs/agent-compatibility.md)。
