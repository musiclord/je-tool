# JET 的 AI Agent 相容方式

更新日期：2026-10-16

Codex、Claude Code 與 VS Code 共用根目錄 `AGENTS.md`、現行規格和 `tools/verify.ps1`。平台設定只負責載入與執行既有規則，不另定產品方向。
本次核對儲存庫設定、共用 hook 原始碼與下列官方文件；沒有重跑三個平台的 session 載入測試。官方支援、設定存在與本機實際生效要分開判斷。

使用者 2026-09-30 的分工要求：

> 請你確保相容 codex 及 vscode 的前提，開始進行整理，我需要這個專案可以相容於 codex, claude, vscode 這三個平台上的 agent 來開發，並且不得互相干擾運作機制，但他們應當共用同一套 harness 紀錄，但可以分別設置最適合的 harness 機制，比如 codex 和 claude 對於 harness 的定義和約束會不太一樣，但總體而言整個專案 `je-tool` 的開發方向應該是一致的。

## 共用與各自設定的分界

| 用途 | 共用來源 | 平台入口 |
|:---|:---|:---|
| 規則與方向 | `AGENTS.md`、`docs/` 現行文件與唯一現行計畫 | `CLAUDE.md` 透過 `@AGENTS.md` 匯入；`.github/copilot-instructions.md` 提供轉接 |
| 正式驗證 | `tools/verify.ps1`；收據在 `artifacts/harness/runs/` | 所有平台用同一入口 |
| Skill | `.agents/skills/` 正本 | Codex 另讀 `agents/openai.yaml`；Claude 用 `.claude/skills/` 的薄入口 |
| Hook | `.agents/hooks/` 共用腳本 | `.claude/settings.json` 與 `.codex/hooks.json` 各自註冊 |
| 命令攔截 | `AGENTS.md` 已有禁令 | Claude 用 permissions；Codex 用 `.codex/rules/jet.rules` |

改規則先改 `AGENTS.md`，再同步對應設定。任何規則、驗收條件或架構判斷都不以某個平台的攔截或 hook 為前提，因為其他平台看不到它們。

## 各平台會讀到哪些專案檔

| 專案檔 | Codex | Claude Code | VS Code 的 Local 與 Copilot |
|:---|:---|:---|:---|
| `AGENTS.md` | 沿目錄載入 | 由 `CLAUDE.md` 的 `@AGENTS.md` 匯入 | Local 依 `chat.useAgentsMdFile` 讀取 |
| `CLAUDE.md` | 不讀 | 讀 | Local 依 `chat.useClaudeMdFile` 讀取 |
| `.github/copilot-instructions.md` | 不讀 | 不讀 | Copilot 讀；Local 依 `github.copilot.chat.codeGeneration.useInstructionFiles` 讀取 |
| `.agents/skills/` | 讀 | 不讀 | 讀 |
| `.claude/skills/` | 不讀 | 讀 | 讀 |
| `.claude/settings.json` | 不讀 | 讀 permissions 與 hooks | 只有開啟 `chat.useClaudeHooks` 才讀 hooks，預設關閉 |
| `.codex/hooks.json` 與 `.codex/rules/` | 只在信任本專案時讀 | 不讀 | 不讀 |

Claude Code v2.1.277 起可以在沒有 `CLAUDE.md` 時直接讀 `AGENTS.md`；有 `CLAUDE.md` 時預設只讀 `CLAUDE.md`。
JET 保留 `CLAUDE.md` 轉接檔，所以仍經由匯入讀到同一份 `AGENTS.md`，行為不因這項新支援而改變。

VS Code 可在同一個視窗選 Local、Copilot、Claude 或 Codex 的 agent harness。上表最右欄只描述 Local 與 Copilot；
選 Claude 或 Codex 時由該 harness 依自己的方式載入，不能只憑在同一個 VS Code 視窗就視為與 CLI 相同。
Copilot 各介面支援的指示檔也不同，不能把 VS Code 的支援推論到 Visual Studio 或網頁 Chat。

VS Code 的技能目錄包含 `.github/skills/`、`.claude/skills/` 與 `.agents/skills/`。JET 的 Claude 薄入口與正本同名，
VS Code 會看到兩個項目，兩者指向同一份內容；不要把重複顯示當成兩套規則。實驗性的 Codex 整合也能發現 `.github/skills/`，JET 不另複製一份。

Codex 對未信任的專案會略過整個專案層 `.codex/`。非受管理的 hook 還要逐一信任，信任依該 hook 目前內容的雜湊記錄；
`.codex/hooks.json` 改動後，改過的 hook 會被略過，直到重新信任。CLI 可用 `/hooks` 查看。2026-09-30 這台電腦的 `~/.codex/config.toml` 把 je-tool 設為 trusted；
2026-10-16 在該檔已查不到這筆設定，信任狀態以 Codex 實際顯示為準。換機後要重新信任本專案，專案層 hook 與規則才會生效。
本次沒有修改使用者信任狀態或全域設定。

## Repo-scoped skills

| Skill | 用途 | 啟動方式 |
|:---|:---|:---|
| `jet-converge` | 收斂跨 session 的需求與裁定 | 只有使用者明示呼叫；Codex 的 `allow_implicit_invocation: false`，Claude 與 VS Code 的 `disable-model-invocation: true` |
| `jet-readable-docs` | 寫清楚面向人的繁體中文，保留原有事實 | 可依任務自動使用 |
| `jet-frontend-design` | 延續已接受的視覺方向，設計與檢查前端；JET 前端用它，不用通用的 frontend-design | 可依任務自動使用；來源與詳細檢查按需讀 references |

正本都在 `.agents/skills/`；`.claude/skills/` 只轉讀正本，不放第二份內容。沒有自動技能能力的工具仍能從 `AGENTS.md` 讀同一文件。
是否提供 `$skill` 或 `/skill` 命令依工具判斷，不把某一平台的命令名稱寫成共同前提。

技能通常先載入名稱與描述，用到時再讀正文和參考檔。描述只寫何時使用與用途，不塞進完整規則；正文只保留執行需要的方向。
Codex 的專案指示預設合計上限為 32 KiB，包含其他目錄層，不能只量本儲存庫就宣稱未超限。

依據：[Codex skills](https://learn.chatgpt.com/docs/build-skills)、[Claude skills](https://code.claude.com/docs/en/skills)、
[VS Code skills](https://code.visualstudio.com/docs/agent-customization/agent-skills)。

## 命令攔截

`AGENTS.md` 與 `CLAUDE.md` 是 context，不是強制層。真正在執行前攔截的是各平台自己的權限機制，所以 JET 把 `AGENTS.md`
已有的禁令分別轉成兩個平台的設定，內容不超出 `AGENTS.md`。

### Claude Code

`.claude/settings.json` 的 `permissions` 對每條命令規則都同時寫 `Bash(...)` 與 `PowerShell(...)` 兩種：

| 規則類型 | 涵蓋範圍 | 對應的 `AGENTS.md` 條文 |
|:---|:---|:---|
| `deny` | 直接 `dotnet test` | 正式測試一律從 `tools/verify.ps1` 進入 |
| `deny` | `git add -A`、`--all`、`-u`、`--update`、`.`，`git write-tree`，`git stash --all` 與 `-a` | 只能加入使用者確認的明確路徑 |
| `deny` | 用 Edit 寫入六個私人資料根目錄：`data/test-case`、`data/temporary-test-case`、`data/legacy-parity-work`、`data/private`、`data/real-case`、`data/customer-data` | 私人資料可在本機讀取核對，不得寫入、複製進儲存庫或送出 |
| `deny` | 修改 `docs/jet-template-v1.html` 與 `docs/jet-template-v2.html` | 已裁定保留的範本，沒有明確要求不改 |
| `ask` | 編輯 `docs/history/**` | 歷史文件是引用的原文來源，動它要先問過人 |
| `ask` | `git commit`、`push`、`remote`、`reset`、`checkout`、`restore`、`clean`、`stash`、`add` | 沒有明確指令時不執行 |

`deny` 壓過 `allow`；`ask` 不是永久封鎖，只是強制在執行前問過人。`git add --update` 是 `-u` 的完整寫法，
2026-10-16 起兩個平台都直接封鎖。

這一層只擋 Claude Code 自己發出的命令與同名工具：Edit 的路徑規則不擋經由 shell 寫檔，子程序自行開檔也不受影響，
`PrivateCase` 就是靠子程序讀取授權後的私人副本。私人目錄的讀取不攔截，由 `AGENTS.md` 的「可讀不可寫、不送出」約束
（使用者 2026-10-02 裁定改為常設的本機唯讀授權）。被 Git 忽略的 `.claude/settings.local.json` 可另加 allow，屬於個人設定。

### Codex

`.codex/rules/jet.rules` 用 `prefix_rule` 對應上表的命令部分。`forbidden` 對應 Claude 的 `deny`，`prompt` 對應 `ask`；
同一命令命中多條規則時，Codex 採用最嚴格的決定。

| 決定 | 涵蓋範圍 |
|:---|:---|
| `forbidden` | `git add -A`、`--all`、`-u`、`--update` 或 `.`，`git write-tree`，`git stash --all` 或 `-a`，直接 `dotnet test` |
| `prompt` | `git commit`、`push`、`remote`、`reset`、`checkout`、`restore`、`clean`、`stash`、`add` |

規則只比對命令，不涵蓋讀檔、`apply_patch` 修改檔案、私人資料目錄與兩份 HTML 範本，這幾項在 Codex 只靠 `AGENTS.md`
與架構測試。官方說明只拆解部分 shell 包裝；不推論任意 `pwsh -Command` 都會拆解。
檢查命令可用 `codex execpolicy check --rules .codex/rules/jet.rules -- <命令>`，這只檢查規則判斷，不執行該命令。
依據：[Codex rules](https://learn.chatgpt.com/docs/agent-configuration/rules)。

### VS Code 的 Local 與 Copilot

不新增儲存庫層的攔截，安全底線是 `AGENTS.md`、`.github/copilot-instructions.md` 的安全摘要和 VS Code 自己的工具核准。
`chat.useClaudeHooks` 維持預設的關閉：開啟後 VS Code 會讀 `.claude/settings.json` 的 hooks，但忽略 matcher，
每個事件的全部命令都會執行，例如只該在編輯後出聲的 `managed-doc-notice.ps1` 會在每次使用工具後都執行。
JET 也沒有 VS Code 原生的 `.github/hooks/`。

## 共用 hook

| 腳本 | Claude | Codex | 現行作用 |
|:---|:---|:---|:---|
| `session-context.ps1` | SessionStart | SessionStart，matcher 只含 `startup` 與 `resume` | 讀 Git 狀態及近期收據，注入開場脈絡 |
| `managed-doc-notice.ps1` | PostToolUse，僅 Edit 與 Write | 未註冊 | 改受管文件後提醒 Documentation 與重讀 |
| `verification-debt.ps1` | Stop | Stop | 比較修改時間與通過收據，提醒尚未重跑的範圍與測試斷言變動 |
| `hook-common.ps1` | 由上述腳本載入 | 由上述腳本載入 | 解析輸入、尋找根目錄與輸出脈絡 |

Codex 的 SessionStart 只在新開與恢復 session 時執行，壓縮上下文之後不會重新注入；需要時改執行
`pwsh -NoProfile -File tools/verify.ps1 -Command Context`。VS Code 的 Local 與 Copilot 沒有這個 hook，接手時同樣用 `Context`。
`managed-doc-notice.ps1` 只讀 Claude Edit 與 Write 的 `file_path`，Codex 改檔的輸入格式不同，所以不註冊。

共用腳本不讀私人案件。失敗時靜默結束；Stop 只輸出 `systemMessage`，不擋住 session（使用者 2026-10-02 裁定改成只提醒）。
時間比較只是粗略提醒，不證明測試涵蓋範圍。Agent 仍須依本次改動選擇驗證，並說明未執行項目。
`Contract` 會檢查兩個平台註冊同一份 hook 腳本、Codex 規則涵蓋 `AGENTS.md` 的命令禁令，以及 `jet-converge` 維持只能明示呼叫；
只改其中一個平台的註冊會被擋下。

`hook-common.ps1` 依序從 `CLAUDE_PROJECT_DIR`、輸入的 `cwd` 與程序工作目錄找 Git 根目錄，確認有 `tools/verify.ps1`。
注入脈絡使用 `hookSpecificOutput.additionalContext`。Codex 註冊命令另用 `git rev-parse --show-toplevel` 找腳本。
來源是上述四份腳本、兩個平台的設定，以及 [Claude hooks](https://code.claude.com/docs/en/hooks) 與 [Codex hooks](https://learn.chatgpt.com/docs/hooks)。

## 前端設計與瀏覽器相容性

各工具依 [`frontend-design-mode.md`](frontend-design-mode.md) 選合成預覽或正式後端的瀏覽器主機。
Codex 桌面版有內建瀏覽器，Codex CLI 與 IDE 擴充套件沒有；Claude Desktop 的 Code 本機 session 使用 `.claude/launch.json` 的 Preview。
雲端 session 不因此取得這台 Windows 的 repo、localhost 或桌面能力。

| 連接埠 | 用途 |
|:---|:---|
| 4243 | 固定素材的合成預覽 |
| 4244 | 使用者在 Codex 檢查畫面的主機；Claude 不連線、不重啟也不清除，任何 Agent 清除或重建它的案件前先問使用者 |
| 4245 | 使用者操作的瀏覽器主機 |
| 4246 | Agent 無頭檢查用的瀏覽器主機 |

啟動設定名稱保留 Claude，只是既有名稱，不是操作權限。Preview 預設開 `localhost`，JET 預覽只接受 `127.0.0.1` 的主機標頭，
所以每個設定的 `port`、`url` 與 `env.PORT` 一致，並以 `autoPort: false` 避免暗中換埠。服務占用先確認來源，不關閉不明程序。
依據：[OpenAI Browser](https://learn.chatgpt.com/docs/browser)、[Claude Desktop Preview](https://code.claude.com/docs/en/desktop#configure-preview-servers)。

Claude Design 可另作視覺方案；既有研究入口保留為 [Design 入門](https://support.claude.com/en/articles/14604416-get-started-with-claude-design)
與 [原型及 UX 交接](https://academy.claude.com/tutorials/using-claude-design-for-prototypes-and-ux)。本次沒有重新查核帳號權限、上傳或測試交接。
採用下載內容前仍要對照正式前端、契約與使用者裁定；不把設計包變成第二份產品。

## 本機 memory

Codex 與 Claude 的本機 memory 可協助找回舊 session，但不是 JET 的事實來源；規則、目前計畫、裁定與延後事項仍寫在
`AGENTS.md`、現行文件和測試。不手工編輯 Codex 生成的 memory 來代替專案文件，也不把私人案件內容放進 memory。
收斂方式見 [`convergence-and-memory.md`](convergence-and-memory.md)。

## 如何確認實際生效

- VS Code：先確認所選 harness，再用 Chat: Open Customizations、References 與 Developer: Open Agent Debug Logs 核對實際載入。
- Claude Code：檢查記憶與技能清單是否包含專案入口，並看 session 開場是否出現 SessionStart hook 注入的「JET 儲存庫現況」。
- Codex：直接詢問 Codex 目前載入哪些指示，或依官方說明開啟記錄後查看 session 紀錄；hook 狀態用 `/hooks` 查看。
  `codex debug prompt-input` 曾於 2026-09-30 用於本機診斷，官方頁面沒有列出，不保證所有版本都有。

只看到檔案存在不足以證明載入。沒有 hook 或缺少診斷工具時，可執行唯讀的 `tools/verify.ps1 -Command Context`，
再讀本次任務相關的現行來源。它提供導覽，不是開工門檻，也不取代必要的實際 Git 查詢。

## 換模型或上下文壓縮後如何續接

正式驗證、判定與清理見 [`harness.md`](harness.md) 和 [`tools/README.md`](../../tools/README.md)。
跨 session 的範圍、裁定與下一動作依 [`development-workflow.md`](development-workflow.md) 和 [`convergence-and-memory.md`](convergence-and-memory.md)。
只讀與任務有關的資料，不因壓縮上下文就重跑已通過且未受影響的測試或結束尚未完成的工作。

## 不由儲存庫檔案保證的事情

- 使用者層或組織層指示可能有更高優先順序；遇到衝突要回報，不能假裝 repo 規則已生效。
- repo 文件不能技術上阻止外部 Agent 執行 Git 或上傳資料。實際權限、sandbox 與人工審閱仍然必要。
- Agent 是否使用某個模型、能否上網或有沒有子 Agent，不是產品執行條件，也不寫進 JET 的 runtime 設定。

## 依據

下列官方資料於 2026-10-16 查核：[Codex 指示載入](https://learn.chatgpt.com/docs/agent-configuration/agents-md)、
[Codex 設定層](https://learn.chatgpt.com/docs/config-file/config-basic)、[Codex hooks](https://learn.chatgpt.com/docs/hooks)、
[Claude 記憶與 AGENTS.md](https://code.claude.com/docs/en/memory)、[VS Code 指示](https://code.visualstudio.com/docs/agent-customization/custom-instructions)、
[VS Code harness](https://code.visualstudio.com/docs/agents/run/agent-harnesses)、[VS Code hooks](https://code.visualstudio.com/docs/agent-customization/hooks)。
Copilot 各介面的[指示檔支援矩陣](https://docs.github.com/en/copilot/reference/custom-instructions-support)於 2026-09-18 查核。
平台支援改變時，只更新本文件與各平台的轉換設定，不複製一套新的 JET 規則。
