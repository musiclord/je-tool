# 給 AI 編碼工具的檔案

這個目錄放的是只有 AI 編碼工具（Claude Code、Codex、GitHub Copilot、VS Code 的 agent）才需要讀的流程文件、
技能與 hook 腳本。人類維護者看 `README.md`、`CONTRIBUTING.md` 與 `docs/` 就夠了；這裡的內容不另立規則，
規則的唯一正本是根目錄的 `AGENTS.md`。

| 位置 | 內容 |
|:---|:---|
| `harness/harness.md` | 驗證框架負責什麼、怎麼判定通過、私人案件怎麼處理。命令與參數看 `tools/README.md` |
| `harness/development-workflow.md` | 需要跨多個步驟或 session 的開發怎麼立案、續接與關閉 |
| `harness/agent-compatibility.md` | 各平台讀哪些檔、權限攔截與 hook 怎麼註冊、怎麼確認已載入 |
| `harness/frontend-design-mode.md` | 用內建瀏覽器或 Preview 調整前端時的工作方式與正式整合順序 |
| `harness/convergence-and-memory.md` | 多個 session 的觀點與待辦如何收斂，什麼算專案記憶 |
| `skills/jet-converge/` | 使用者明確呼叫 `$jet-converge` 時的收斂訪談 |
| `skills/jet-readable-docs/` | 把面向人的文字改成台灣日常中文的檢查表與範例 |
| `hooks/` | Claude Code 與 Codex 共用的 SessionStart、PostToolUse 與 Stop hook 腳本 |

Claude Code 另有 `.claude/`，Codex 另有 `.codex/`，裡面只放各平台的註冊與薄入口，內容都指回這裡。
