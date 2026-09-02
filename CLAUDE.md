# Claude Code repository adapter

@AGENTS.md

`AGENTS.md` 是 Claude Code、GitHub Copilot、Codex 與 VS Code AI Agent 的共用權威。本檔只使用 Claude Code
原生的 `@AGENTS.md` 語法匯入它，不複製 harness、profile、Git、隱私或命令規則。使用者明確呼叫
`$jet-converge` 時，`.claude/skills/jet-converge/SKILL.md` 只負責轉讀 `.agents/` 下的共用正本。

開始大型開發前，另讀 `docs/development-status.md` 指向的現行計畫；專案方向與正式環境限制見
`docs/project-context.md`。Claude 的 auto memory 不是儲存庫證據，不能取代現行文件、測試或本次 receipt。
