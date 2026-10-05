# JET 的 AI Agent 相容方式

更新日期：2026-09-30

JET 同時支援在 Codex、Claude Code 與 VS Code 裡用 AI agent 開發。三個平台共用同一套規則、現行文件、
驗證入口和驗證紀錄；命令攔截與 hook 則用各平台原生的設定檔註冊，彼此不讀對方的設定。完整共用規則
只放在根目錄 `AGENTS.md`，其他檔案只負責讓各工具找到同一份規則，或把既有規則轉成該平台能執行的形式。

2026-09-30 使用者對這個分工的要求：

> 請你確保相容 codex 及 vscode 的前提，開始進行整理，我需要這個專案可以相容於 codex, claude, vscode 這三個平台上的 agent 來開發，並且不得互相干擾運作機制，但他們應當共用同一套 harness 紀錄，但可以分別設置最適合的 harness 機制，比如 codex 和 claude 對於 harness 的定義和約束會不太一樣，但總體而言整個專案 `je-tool` 的開發方向應該是一致的。

## 共用與各自設定的分界

| 類別 | 三個平台共用 | 各平台自己的設定 |
|:---|:---|:---|
| 規則與開發方向 | `AGENTS.md`、`docs/` 現行文件與唯一現行計畫 | `CLAUDE.md` 和 `.github/copilot-instructions.md` 只做轉接 |
| 正式驗證 | `tools/verify.ps1`，收據寫在 `artifacts/harness/runs/` | 沒有 |
| Skill | `.agents/skills/` 的正本 | Codex 讀 `agents/openai.yaml`；Claude Code 讀 `.claude/skills/` 的薄入口 |
| Hook | `.agents/hooks/` 的腳本，Stop hook 的狀態寫在 `artifacts/harness/hooks/` | Claude Code 在 `.claude/settings.json` 註冊，Codex 在 `.codex/hooks.json` 註冊 |
| 命令攔截 | 攔截的內容都來自 `AGENTS.md` | Claude Code 用 `.claude/settings.json` 的 `permissions`，Codex 用 `.codex/rules/jet.rules` |

平台設定不新增規則。任何規則、驗收條件或架構判斷都不得以某個平台的攔截或 hook 為前提，因為其他平台
看不到它們。要改規則時先改 `AGENTS.md`，再同步各平台的轉換。

## 各平台會讀到哪些專案檔

下表依 2026-09-30 查核的官方文件與本機測試整理。VS Code 1.139 可在同一個視窗選擇不同的 agent harness：
Local、Copilot、Claude 或 Codex。最右欄只描述 Local 與 Copilot。選 Claude 或 Codex 時由該 harness 讀自己的
設定，但 VS Code 官方文件沒有逐項列出這兩個 harness 讀哪些專案檔，本表不推論它們與 Claude Code 或 Codex
CLI 完全相同。

| 專案檔 | Codex | Claude Code | VS Code 的 Local 與 Copilot |
|:---|:---|:---|:---|
| `AGENTS.md` | 讀 | 由 `CLAUDE.md` 的 `@AGENTS.md` 匯入 | 依 `chat.useAgentsMdFile` 設定讀取 |
| `CLAUDE.md` | 不讀 | 讀 | Local 依 `chat.useClaudeMdFile` 設定讀取 |
| `.github/copilot-instructions.md` | 不讀 | 不讀 | 讀 |
| `.agents/skills/` | 讀 | 不讀 | 讀 |
| `.claude/skills/` | 不讀 | 讀 | 讀 |
| `.claude/settings.json` | 不讀 | 讀取 permissions 與 hooks | 只有開啟 `chat.useClaudeHooks` 才讀取 hooks，預設關閉 |
| `.codex/hooks.json` 與 `.codex/rules/` | 只在信任本專案時讀 | 不讀 | 不讀 |

Codex 對未信任的專案會略過整個專案層 `.codex/`。這台開發電腦的 `~/.codex/config.toml` 已把 je-tool 設為
`trusted`；換機後要在 Codex 裡重新信任本專案，專案層 hook 與規則才會生效。

2026-09-18 核對過的[官方支援矩陣](https://docs.github.com/en/copilot/reference/custom-instructions-support)
說明 Copilot 各介面支援的指示檔不同，不能把 VS Code 的支援推論到 Visual Studio 或一般網頁 Chat。
因此保留 `.github/copilot-instructions.md` 的共用入口與安全摘要；實際是否載入仍依工具的診斷畫面確認。

## 前端設計與瀏覽器相容性

各工具共用 [`frontend-design-mode.md`](frontend-design-mode.md)，不建立第二套審計邏輯。
下表於 2026-09-09 查核；官方具備某項能力，不代表本機帳號、版本或組織政策已允許它。

| 工具 | 適合的入口 | 在 JET 的用法與界線 |
|:---|:---|:---|
| Codex 桌面版 | 內建瀏覽器及畫面註解 | 已在本次 JET 調整中使用；開啟本機合成預覽，Agent 寫回正式前端。Adjust 仍以版本實際支援為準，未列為必要能力。 |
| Claude Desktop 的 Code | 本機 session 的 Preview | 官方支援開發伺服器、DOM 與畫面操作；專案提供 `.claude/launch.json` 啟動合成預覽和瀏覽器主機。2026-10-01 已在本機 Preview 啟動 `JET browser host`，開啟合成案件並在第五步預覽篩選結果；合成預覽與畫面註解方式尚未實測，不宣稱和 Codex 完全相同。 |
| Claude Design | Desktop 側欄或網頁設計畫布 | 官方支援設計迭代與交回 coding agent；JET 採用最小合成設計包，不把畫布當成直接操作本機 WebView2 的環境。尚未實際上傳或交接驗收。 |
| Claude Desktop 一般聊天、雲端 coding session | 視目前可用工具而定 | 不假設能讀取這台 Windows 的 repo、localhost 或根目錄指示檔；缺少本機工具時改用 Code 本機 session，或明確提供必要交接內容。 |

Claude Code 的官方預覽設定預設使用 `localhost`，JET 預覽目前只接受 `127.0.0.1` 的指定主機標頭。
因此設定明寫同一個 `port`、`url` 與 `env.PORT`，停用自動換埠，避免啟動的服務與畫面網址不一致。
連接埠衝突由 Agent 辨識後調整這三個值；不放寬服務的對外存取限制。未指定 PORT 的原有啟動方式不變。
`JET browser host` 使用 `127.0.0.1:4244`，接上正式後端並使用合成案件；用途和界線見
[`docs/development-guide.md`](../../docs/development-guide.md#開發用的預覽工具)。

Claude Design 官方提供 `/design-sync`、Design MCP 和交回 Claude Code 的方式，也可交給其他 coding agent。
本輪只研究相容方法，沒有配置帳號、MCP、雲端專案或上傳檔案。下載內容須由本機 Agent 核對原有元件與契約後採用，
不保證輸出的 HTML 可直接覆蓋 JET。原始碼、現行規格與唯一計畫才是跨工具的共同上下文；畫面註解不會自動跨平台同步。

官方來源：

- [OpenAI Browser](https://learn.chatgpt.com/docs/browser)：內建瀏覽器及其操作方式；JET 的註解協作另有本次使用者驗收。
- [Claude Code Desktop](https://code.claude.com/docs/en/desktop#configure-preview-servers)：Code 本機預覽、啟動設定、URL 與連接埠規則。
- [Claude Design 入門](https://support.claude.com/en/articles/14604416-get-started-with-claude-design)：設計畫布、匯入與 Design MCP。
- [Claude Design 原型與 UX](https://academy.claude.com/tutorials/using-claude-design-for-prototypes-and-ux)：設計交接內容與 coding agent 接續。

## Repo-scoped skills

專案 skill 的正本放在 `.agents/skills/`，目前有兩個：

`jet-converge` 的唯一正本是 `.agents/skills/jet-converge/SKILL.md`，只有使用者明示呼叫時才啟動完整訪談，
避免一般小修正被擴張成大型規劃。三個平台各用自己認得的方式表達這個限制：

| 平台 | 讀哪一份 | 限制寫在哪裡 |
|:---|:---|:---|
| Codex | `.agents/skills/jet-converge/` | `agents/openai.yaml` 的 `allow_implicit_invocation: false` |
| Claude Code | `.claude/skills/jet-converge/SKILL.md` | 該檔的 `disable-model-invocation: true` |
| VS Code | 兩份都讀 | 正本 `SKILL.md` 也寫 `disable-model-invocation: true`，因為 VS Code 不讀 `openai.yaml` |

2026-09-30 用 Codex CLI 0.147.0 在暫存 repo 測試：含 `disable-model-invocation` 的 SKILL.md 照常載入，Codex
忽略這個欄位。je-tool 的 `codex debug prompt-input` 結果中，`jet-converge` 仍不在可自動使用的 skill 清單。

`jet-readable-docs` 的唯一正本是 `.agents/skills/jet-readable-docs/SKILL.md`，改寫範例在同目錄的
`references/rewrite-examples.md`。它允許隱含載入（`allow_implicit_invocation: true`），因為它要處理的是
agent 寫文件時的慣性，等使用者明說「太難讀」才載入就太晚了。範圍只限改寫面向人的文字，不改事實、
程式或 JSON；命中的檢查表項目與 `Documentation` 的 warning 對應。

Claude Code 的 `.claude/skills/jet-converge/SKILL.md` 與 `.claude/skills/jet-readable-docs/SKILL.md` 都只轉讀
同一份正本，不保存第二套內容。VS Code 同時掃描 `.agents/skills/` 與 `.claude/skills/`，所以每個 skill 會看到
兩個同名項目；兩者指向同一份內容，官方文件沒有說明 VS Code 如何處理同名 skill。GitHub Copilot 或不支援
repo skill 的介面仍可從 `AGENTS.md` 指向的 `.agents/harness/convergence-and-memory.md` 與
`.agents/skills/jet-readable-docs/SKILL.md` 使用相同規則，但不宣稱支援 `$jet-converge` 或 `$jet-readable-docs` 命令。

Codex 會自動偵測 skill 檔案變更；若目前 session 的 skill 清單尚未出現，重新啟動 Codex。技能採漸進載入：
平常只暴露名稱與描述，被選取後才讀完整 `SKILL.md`，跨 session 的細節再按需讀 `references/`。

## 載入預算

[Codex 官方文件](https://learn.chatgpt.com/docs/agent-configuration/agents-md)說明各層專案指示預設合計
上限為 32 KiB；這不是單看本儲存庫檔案就能判定的剩餘額度。
[Claude Code 官方文件](https://code.claude.com/docs/en/memory)建議精簡指示，將局部程序按需載入；
建議篇幅不等於超過便截斷的硬性限制，`@AGENTS.md` 匯入也不會省去其上下文用量。

`AGENTS.md` 保留不可協商的邊界、命令與導航，不累加每次失敗的故事。增加內容前先核對現有文件能否承接，
需要量測時直接讀目前檔案大小，不用歷史數字推論載入正常。各工具的全域設定也可能影響最後載入結果。

## 各平台的命令攔截

`AGENTS.md` 與 `CLAUDE.md` 是 context，不是強制層：agent 會讀，但不保證遵守。真正在執行前攔截的是各平台
自己的權限機制，所以 JET 把 `AGENTS.md` 已有的禁令分別轉成兩個平台的設定，內容不超出 `AGENTS.md`。

### Claude Code

`.claude/settings.json` 的 `permissions`：

| 規則類型 | 涵蓋範圍 | 對應的 `AGENTS.md` 條文 |
|:---|:---|:---|
| `deny` | 直接 `dotnet test` | 正式測試一律從 `tools/verify.ps1` 進入；診斷用 `Focused` 或直接執行測試程式 |
| `deny` | `git add -A`、`--all`、`-u`、`.`，`git write-tree`，`git stash --all` | 只能加入使用者確認的明確路徑 |
| `deny` | 寫入六個私人資料根目錄 | 私人資料可以在本機讀取核對，但不得寫入、複製進儲存庫或送出。讀取不再攔截（使用者 2026-10-02 裁定改為常設的本機唯讀授權） |
| `deny` | 修改兩份已裁定保留的 HTML 範本 | 沒有明確要求時不得修改 |
| `ask` | `git commit`、`push`、`remote`、`reset`、`checkout`、`restore`、`clean`、`stash`、`add` | 沒有明確指令時不得執行這些操作 |
| `ask` | 編輯 `docs/history/**` | 歷史文件是引用的原文來源，不得被日常改寫默默變動；動它要先問過人 |

`deny` 永遠壓過 `allow`；複合命令會逐段比對，所以 `cd x && dotnet test` 一樣被擋；`timeout`、`nice`
這類 wrapper 會先剝除再比對。`ask` 不是永久封鎖，它只是強制在執行前問過人，符合「第一次 commit 或
push 前必須由使用者另行確認」。

這一層有明確界線，不能當成完整防護：

- 它只擋 Claude Code 自己發出的命令與檔案工具。子程序自行開檔不受影響；`PrivateCase` 就是靠子程序讀取
  授權後的私人副本。
- `Edit` 的路徑規則只作用於同名工具。透過 Bash 寫入私人目錄不在攔截範圍內，那一層仍由 `AGENTS.md` 與架構測試負責。
  私人目錄的 `Read` 攔截已在 2026-10-02 依使用者裁定移除；讀取由 `AGENTS.md` 的「可讀不可寫、不送出」規則約束。

### Codex

`.codex/rules/jet.rules` 用 Codex 的 `prefix_rule` 對應上表的命令部分。`forbidden` 對應 Claude 的 `deny`，
`prompt` 對應 `ask`；同一個命令命中多條規則時，Codex 採用最嚴格的決定。

| 決定 | 涵蓋範圍 |
|:---|:---|
| `forbidden` | `git add -A`、`--all`、`-u`、`--update` 或 `.`，`git write-tree`，`git stash --all` 或 `-a`，直接 `dotnet test` |
| `prompt` | `git commit`、`push`、`remote`、`reset`、`checkout`、`restore`、`clean`、`stash`、`add` |

2026-09-30 用 `codex execpolicy check --rules .codex/rules/jet.rules` 實測：`git add -A`、`git add .`、
`git stash -a`、`git write-tree` 與 `dotnet test` 判定為 `forbidden`；`git add docs/README.md`、`git push` 與
`git stash list` 判定為 `prompt`；`git status` 與 `dotnet build` 沒有命中。規則檔裡的 `match` 與 `not_match`
範例會在 Codex 載入時自動核對。

Codex 規則的界線：

- 規則只比對命令，不涵蓋讀檔、`apply_patch` 修改檔案、私人資料目錄與兩份 HTML 範本。這幾項在 Codex
  仍由 `AGENTS.md` 與架構測試負責。
- 官方文件說明，只由 `&&`、`||`、`;`、`|` 串接的簡單 shell 指令稿會被拆開逐段比對。包在
  `pwsh -Command` 裡的命令，`execpolicy check` 不會拆開；Codex 在 Windows 實際執行時是否拆開，尚未實測。
- 只在 Codex 信任本專案時載入。

2026-08-30 記錄的「Codex 沒有可攜的逐命令 `deny`」已不成立：Codex 目前支援專案層 `.codex/rules/`。

### VS Code 的 Local 與 Copilot

不新增儲存庫層的攔截，安全底線是 `AGENTS.md`、`.github/copilot-instructions.md` 的安全摘要和 VS Code 自己的
工具核准。`chat.useClaudeHooks` 要維持預設的關閉：開啟後 VS Code 會讀 `.claude/settings.json` 的 hooks，
但官方文件說明它會忽略 matcher，每個事件的所有命令都會執行，Claude 專用的 Stop hook 就會擋住 Copilot
結束。這正是平台互相干擾的情況，所以不開。本專案也沒有 VS Code 原生的 `.github/hooks/`。

## 共用 hook 腳本與各平台註冊

`.agents/hooks/` 保存唯一一份 hook 腳本。它們只讀 Git 中繼資料與 `artifacts/harness/runs/` 的收據，
不讀取私人案件目錄，也不輸出檔案內容。任何一步失敗都靜默放行，不影響 session。

| 檔案 | 事件 | Claude Code | Codex | 作用 |
|:---|:---|:---|:---|:---|
| `session-context.ps1` | `SessionStart` | 註冊 | 註冊 | 把實際的 branch、HEAD、工作樹狀態與最近一次各命令的收據放進開場脈絡 |
| `managed-doc-notice.ps1` | `PostToolUse` | 註冊，只比對 `Edit` 和 `Write` | 不註冊 | 改到受管文件時提醒執行 `Documentation` 並重讀改動段落 |
| `verification-debt.ps1` | `Stop` | 註冊 | 註冊 | 以一行提醒列出改了但沒有新通過收據的範圍，以及測試斷言的變動；只提醒，不擋住結束 |
| `hook-common.ps1` | 不直接註冊 | | | 上面三個腳本共用的輸入、根目錄與輸出處理 |

`managed-doc-notice.ps1` 只讀 Claude `Edit` 與 `Write` 工具輸入中的 `file_path`，Codex 改檔的輸入格式不同，
所以 Codex 不註冊它；Codex 改了受管文件而沒有重跑 `Documentation` 時，由 Stop hook 擋住一次。

兩個平台找專案根目錄的方式不同。腳本依序嘗試 `CLAUDE_PROJECT_DIR`、hook 輸入中的 `cwd` 與行程工作目錄，
只接受含 `tools/verify.ps1` 的 Git 根目錄；都不符合時直接結束。Codex 沒有提供專案目錄變數，所以
`.codex/hooks.json` 用 `git rev-parse --show-toplevel` 找腳本，從子目錄啟動 session 也能執行。

2026-09-30 發現原本放在 `.claude/hooks/` 的腳本輸出頂層 `additionalContext`，但 Claude Code 只讀
`hookSpecificOutput.additionalContext`。本機 9 份 je-tool 的 Claude 對話紀錄中，`session-context.ps1` 執行
22 次、`managed-doc-notice.ps1` 出聲 11 次，實際注入脈絡都是 0 次。現在兩個注入脈絡的腳本都改輸出
`hookSpecificOutput`，Claude Code 與 Codex 的官方文件都接受這個格式。

同日測試另外發現，Codex 的註冊方式經由 `pwsh -Command` 呼叫腳本，腳本內的 `exit 2` 傳回時變成 exit 1，
Codex 會把它當成一般錯誤而不擋住結束。因此 `verification-debt.ps1` 改在 stdout 輸出
`{"decision":"block","reason":...}` 並以 exit 0 結束，兩個平台的 Stop 事件都接受這個格式；不擋的情況只輸出
一行 `systemMessage` 給使用者。

`session-context.ps1` 的存在理由是 `AGENTS.md` 要求接手時直接查 Git，不能只依長期文件判斷；把這件事
自動化就不會漏。它輸出的收據只描述當次執行，候選內容變動後不能沿用，也不是專案記憶——`artifacts/`
由 Git 忽略，fresh clone 後不存在。VS Code 的 Local 與 Copilot 沒有這個 hook，接手時改執行
`pwsh -NoProfile -File tools/verify.ps1 -Command Context` 取得同樣的資訊。

`verification-debt.ps1` 比較檔案修改時間與收據完成時間，只能提供粗略提醒，不能證明內容或需求已覆蓋。它只輸出一行
`systemMessage` 給使用者，不擋住結束：使用者 2026-10-02 裁定改成只提醒，因為先前「第一次擋一次」以 session 為單位
記狀態，長時間工作每改一批就被擋，新開的 session 結束時也會再被擋。agent 收尾時仍要照 `AGENTS.md` 逐項寫明
「未執行」與原因，這個提醒是給人核對用的。測試斷言變動是指 `src/JET/tests/` 的改動移除 `Assert.`、移除 `[Fact]`
或 `[Theory]`、新增 `Skip =`，要對照 `AGENTS.md`「測試」說明原因。它不替任何命令背書。

2026-09-30 用模擬輸入在本機測試：Claude Code 與 Codex 兩種呼叫方式的 SessionStart 都輸出
`hookSpecificOutput`，從 `src/JET` 子目錄啟動也找得到根目錄，在儲存庫外則不輸出；PostToolUse 只對受管文件
出聲；Stop 在有驗證欠帳時第一次輸出 `decision: block`，同一 session 第二次只輸出 `systemMessage`。
尚未在真正的 Claude Code 或 Codex session 裡確認這些輸出被採用，第一次使用時請看 session 開場是否出現
「JET 儲存庫現況」。

## 本機 memory 與專案記憶

Codex 與 Claude 的本機 memory 可協助找回舊 session，但背景更新不一定即時，也可能因權限、外部 context
或 host 設定而不產生。它們是回想層，不是 JET 的唯一事實來源；必要的團隊規則、目前計畫、裁定、延後
事項與重啟條件仍寫在 `AGENTS.md`、現行文件和測試。

`jet-converge` 可以利用 host 提供的 task／thread 與 memory 找候選來源，但每項結論要回到原訊息、目前
repository 或使用者本輪裁定。不得手工編輯 Codex 生成的 memory 來代替專案文件，也不得把私人案件內容
放進 memory 或收斂紀錄。

## 共用驗證框架

不論由哪一個 Agent 執行，正式驗證都從下列入口開始：

```powershell
pwsh -NoProfile -File tools/verify.ps1 -Command Help
pwsh -NoProfile -File tools/verify.ps1 -Command Documentation
pwsh -NoProfile -File tools/verify.ps1 -Command Contract
```

產品、provider、GUI、Excel 與私人案件依 `docs/harness.md` 選擇相應命令。Agent 原生的 terminal、測試按鈕、
plan mode 或子 Agent 不會取代 JET 的鎖、收據、清理與私人資料規則。

`Documentation` 會檢查必要轉接檔及其關鍵指向。缺檔、沒有匯入共用規則或出現已確認過時的事實會失敗；
不自然用語與符號代句只產生 warning，每條附一句修正提示，最後仍要由人閱讀。`Contract` 另外檢查兩個平台
都註冊同一份 hook 腳本、Codex 規則涵蓋 `AGENTS.md` 的命令禁令，以及 `jet-converge` 的正本維持只能明示
呼叫。這只能降低規則漂移，不能保證每個模型每次都完全遵守。

## 如何確認各平台實際載入

- VS Code：在 Chat 檢視開啟 Diagnostics，確認 `AGENTS.md` 與 `.github/copilot-instructions.md` 已列入目前
  workspace。
- Claude Code：用 memory 檢視確認根目錄 `CLAUDE.md` 已載入 `AGENTS.md`。
- Codex：`codex debug prompt-input` 不呼叫模型，會列出實際載入的 `AGENTS.md` 與可自動使用的 skill 清單；
  它不顯示明示呼叫才載入的 skill 內容，也不顯示 SessionStart hook 的輸出。命令規則用
  `codex execpolicy check --rules .codex/rules/jet.rules -- <命令>` 檢查。

這些是工具端的實際載入證據；只看到檔案存在還不夠。

## 換模型或上下文壓縮後如何續接

相容性取決於模型、承載工具與執行環境。不同模型共用同一份 JET 規則，沒有依模型名稱另建工作流程。
沒有 skill、自動 memory 或 hook 的工具，仍可讀 `AGENTS.md`，執行 `Context`，再讀目前計畫的
摘要及本次問題相關來源。只有檔案閱讀能力時可做查核；編輯、PowerShell、.NET、Windows 桌面與原生 Excel
是否可用，決定它實際能完成哪些修改與驗證，未執行的部分須明說。

接續摘要保留本輪成果與完成條件、產品順序、已做與未做、重要裁定及否決理由，來源連回原話或驗證。
新的使用者要求優先；摘要過期就更新原位置。按需讀取規格與程式，不把整份歷史、完整工具輸出或所有 skill
塞進開場，也不因上下文壓縮而把未完成任務改成下一個 session 的工作。具體分工見
[`development-workflow.md`](development-workflow.md#工作順序本輪邊界與職責)。

2026-09-18 查核下列公開經驗，並對照 JET 已有問題採用；論壇和 issue 是使用者經驗，不是模型能力評測，
也不代表本機正遇到相同版本缺陷：

| 來源與觀察 | JET 的採用方式 |
|:---|:---|
| [OpenAI 的 GPT-6 Astra 指引](https://developers.openai.com/api/docs/guides/latest-model#instruction-following)提醒，模糊或衝突的 skill 指示可能造成提早停工。 | 清楚區分工作順序與本輪完成條件，移除已授權工作仍要重複確認的表述。 |
| [Anthropic 的長期 harness 經驗](https://www.anthropic.com/engineering/harness-design-long-running-apps)主張逐一檢驗並簡化輔助設計，而不是整套增加或拆除。 | 保留現有正式驗證，只修正可重現的 Context 資訊缺口；不新增排程、狀態資料庫或開工檢查。 |
| [Codex 社群的過度設計討論](https://www.reddit.com/r/codex/comments/1ve9nxe/why_does_codex_constantly_overengineer_code_and/)反映，增加規則與抽象層仍可能反覆失控。 | 以實際缺陷、較小修改及可觀察結果判斷改善，文件按原用途更新，不為每次錯誤另建一層流程。 |
| [Claude Code 社群的壓縮經驗](https://www.reddit.com/r/ClaudeCode/comments/1wgtm88/claude_codes_compaction_keeps_what_you_built_and/)指出否決理由容易遺失，回覆也提醒舊否決可能仍有效。 | 在現行計畫保留仍有效的否決及理由；已結束的執行紀錄移入歷史。不照搬另建 `DECISIONS.md` 的建議。 |
| [Codex issue 31659](https://github.com/openai/codex/issues/31659)回報壓縮後追逐舊提示而偏離目標。 | 壓縮後核對最新任務、摘要與來源，不能只靠聊天摘要的舊編號續做。 |

上述調整改善可讀取的共同上下文，不證明任何型號永不遺忘。尚未逐一實測使用者列出的模型；
框架檢查只驗證資料與命令行為。真正的接手驗證仍需新 session 正確讀出裁定、完成一項修改並留下有效驗證。

## 目前依據

平台支援若改變，只更新本文件與各平台的轉換設定，不複製一套新的 JET 規則。下列官方資料於 2026-09-30 查核：

- [VS Code agent hooks](https://code.visualstudio.com/docs/copilot/customization/hooks)：`.github/hooks/`、`chat.useClaudeHooks`
  預設關閉，以及讀取 Claude 格式時忽略 matcher
- [VS Code agent skills](https://code.visualstudio.com/docs/copilot/customization/agent-skills)：同時掃描
  `.github/skills/`、`.claude/skills/` 與 `.agents/skills/`，並支援 `disable-model-invocation`
- [VS Code agent harness](https://code.visualstudio.com/docs/agents/run/agent-harnesses)：Local、Copilot、Claude 與 Codex 的選擇
- [Codex hooks](https://learn.chatgpt.com/docs/hooks)：`.codex/hooks.json`、信任要求、SessionStart 與 Stop 的輸入輸出
- [Codex rules](https://learn.chatgpt.com/docs/agent-configuration/rules)：`prefix_rule`、`forbidden` 與 `prompt`、`execpolicy check`
- [Codex config basics](https://learn.chatgpt.com/docs/config-file/config-basic)：未信任專案會略過專案層 `.codex/`
- [OpenAI Codex 建立與載入 skills](https://learn.chatgpt.com/docs/build-skills)：只掃描 `.agents/skills/`，不讀 `.claude/skills/`
- [Claude Code hooks 參考](https://code.claude.com/docs/en/hooks)：`hookSpecificOutput.additionalContext`、Stop 的
  `decision: block`、`Stop` 輸入含 `stop_hook_active`
- [Claude Code skills](https://code.claude.com/docs/en/skills)：只掃描 `.claude/skills/`

較早查核、仍作為依據的資料：GitHub Copilot 支援矩陣於 2026-08-30 與 2026-09-18 確認，Claude Code best
practices 於 2026-09-02 查核，其餘於 2026-08-29 查核：

- [GitHub Copilot 指示檔支援矩陣](https://docs.github.com/en/copilot/reference/custom-instructions-support)
- [VS Code custom instructions](https://code.visualstudio.com/docs/agent-customization/custom-instructions)
- [Claude Code 專案記憶與 `AGENTS.md` 匯入](https://code.claude.com/docs/zh-CN/memory)
- [Claude Code best practices](https://code.claude.com/docs/en/best-practices)：CLAUDE.md 要短、只放程式推不出來的事；
  給 Claude 可自己跑的檢查；Stop hook 可以擋住結束直到檢查通過，連續擋 8 次後會被覆寫
- [OpenAI Codex 的 `AGENTS.md` 載入方式](https://learn.chatgpt.com/docs/agent-configuration/agents-md)
- [OpenAI Codex 本機 memories](https://learn.chatgpt.com/docs/customization/memories)

## 不由儲存庫檔案保證的事情

- 使用者層或組織層指示可能有更高優先順序；遇到衝突要回報，不能假裝 repo 規則已生效。
- repo 文件不能技術上阻止外部 Agent 執行 Git 或上傳資料。實際權限、sandbox 與人工審閱仍然必要。
- Agent 是否使用某個模型、是否能上網或是否有子 Agent，不是產品執行條件，也不寫進 JET 的 runtime 設定。
