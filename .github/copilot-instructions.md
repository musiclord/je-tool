# JET Copilot repository adapter

開始修改前，先讀取並遵守 [`AGENTS.md`](../AGENTS.md)。它是 GitHub Copilot、Claude Code、Codex 與
VS Code AI Agent 的共用規則權威；本檔只是 Copilot 與 VS Code 的入口，不建立第二套架構或工作流程。

若目前 Copilot 介面沒有自動載入連結內容，至少先遵守下列安全摘要，再開啟 `AGENTS.md` 核對完整規則：

- 未經使用者明確授權，不 stage、commit、push、變更 remote，也不主動提議這些操作。
- 禁止 `git add -A`、`git add --all`、`git add .` 與 `git write-tree`；獲准後只加入明確路徑。
- Commit、PR 與同步說明不得自行加入 `Co-Authored-By`、`Generated with` 或其他 AI 署名。
- 不讀取、上傳或記錄 `AGENTS.md` 所列私人案件路徑；一般驗證只使用合成或已核准資料。
- 正式驗證從 `pwsh -NoProfile -File tools/verify.ps1` 進入，不用直接 `dotnet test` 取代收據、鎖與清理。
- 交付說明只寫結果、必要理由、風險與實際驗證，不逐檔重述 diff。

專案目的與公司環境限制見 [`docs/project-context.md`](../docs/project-context.md)；跨 session 開發先讀
[`docs/development-status.md`](../docs/development-status.md) 的現行計畫，再依
[`docs/development-workflow.md`](../docs/development-workflow.md) 核對實際工作樹。
