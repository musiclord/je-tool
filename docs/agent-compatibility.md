# JET 的 AI Agent 相容方式

更新日期：2026-09-01

JET 不為每一個 AI 工具維護一套獨立規則。完整共用規則只放在根目錄 `AGENTS.md`；其他檔案只負責讓
各工具找到同一份規則，或保留平台無法可靠追蹤連結時必須先看到的安全摘要。

## 各工具讀取入口

| 工具 | 儲存庫入口 | 本專案做法 |
|:---|:---|:---|
| Codex | `AGENTS.md` | 直接讀取共用權威 |
| Claude Code | `CLAUDE.md` | 用 `@AGENTS.md` 原生匯入共用權威；專案 skill 只保留薄入口 |
| GitHub Copilot | `.github/copilot-instructions.md` | 指向 `AGENTS.md`，並保留 Git、機敏資料與驗證入口的短摘要 |
| VS Code AI Agent | `.github/copilot-instructions.md` 與 `AGENTS.md` | 使用 workspace 指示檔；不需要複製舊專案的 VS Code 實驗性設定 |

GitHub 不同 Copilot 介面支援的指示檔不完全相同，因此 `.github/copilot-instructions.md` 仍有必要。
2026-08-30 依官方支援矩陣重新查證：VS Code、Copilot coding agent 與 Copilot CLI 會原生讀 `AGENTS.md`；
Visual Studio、JetBrains 與 GitHub.com 的 Chat 不會，只讀 `.github/copilot-instructions.md`。所以轉接檔
的安全摘要是那三個介面唯一會看到的規則，不能刪減，也不能建立和 `AGENTS.md` 相反的規則。VS Code 同時
載入多種指示檔時內容不保證固定順序，結論相同。

## Repo-scoped skills

Codex 會從 repository root 的 `.agents/skills/` 發現專案 skill。目前有兩個：

`jet-converge` 的唯一正本是 `.agents/skills/jet-converge/SKILL.md`。`agents/openai.yaml` 設為 explicit-only，
只有使用者輸入 `$jet-converge` 才啟動完整訪談，避免一般小修正被擴張成大型規劃。

`jet-readable-docs` 的唯一正本是 `.agents/skills/jet-readable-docs/SKILL.md`，改寫範例在同目錄的
`references/rewrite-examples.md`。它允許隱含載入（`allow_implicit_invocation: true`），因為它要處理的是
agent 寫文件時的慣性，等使用者明說「太難讀」才載入就太晚了。範圍只限改寫面向人的文字，不改事實、
程式或 JSON；命中的檢查表項目與 `Documentation` 的 warning 對應。

Claude Code 的 `.claude/skills/jet-converge/SKILL.md` 與 `.claude/skills/jet-readable-docs/SKILL.md` 都只轉讀
同一份正本，不保存第二套內容。GitHub Copilot 或不支援 repo skill selector 的介面仍可從 `AGENTS.md` 指向的
`.agents/harness/convergence-and-memory.md` 與 `.agents/skills/jet-readable-docs/SKILL.md` 使用相同規則，
但不宣稱支援 `$jet-converge` 或 `$jet-readable-docs` 命令。

Codex 會自動偵測 skill 檔案變更；若目前 session 的 skill 清單尚未出現，重新啟動 Codex。技能採漸進載入：
平常只暴露名稱與描述，被選取後才讀完整 `SKILL.md`，跨 session 的細節再按需讀 `references/`。

## 載入預算

兩個主要 host 對指示檔都有量的限制，超出時不會報錯，只會靜默截斷或降低遵循度。2026-09-02 量測：

| Host | 限制 | 目前用量 |
|:---|:---|:---|
| Codex | 各層 `AGENTS.md` 合併總量預設 32 KiB | `AGENTS.md` 9,052 bytes，約 28% |
| Claude Code | 官方建議單檔 200 行以內；`@AGENTS.md` import 不會降低 context 用量 | `CLAUDE.md` 10 行加 `AGENTS.md` 126 行，共 136 行 |

2026-08-30 依 harness engineering 的共識把 `AGENTS.md` 從 131 行的規則全文改寫為 83 行的地圖：只保留
不可協商的邊界、常用命令與指向各文件的導航表；被移出的細則都在 `docs/` 有唯一的家（寫作規範在
`docs/README.md`，資料與 legacy 規則在 `docs/data-and-legacy.md`，驗證細節在 `docs/harness.md` 與
`tools/README.md`）。2026-09-01 為 `$jet-converge` 補上 explicit-only 路由與專案記憶導航後為 107 行；
完整流程仍留在 `.agents/`，沒有搬進入口。2026-09-02 依使用者要求補上三條邊界（寫給人的文字、測試
斷言不得放寬、commit 訊息用中文且要短）與 `jet-readable-docs` 的導航後為 123 行；同日再依使用者裁定
加上「預設審計員可信」一條，為 126 行。改寫步驟與範例留在 skill，不進入口。之後在 `AGENTS.md` 新增
內容前，先確認它是邊界或導航，不是可以放進 `docs/` 的細節。

在 `AGENTS.md` 新增規則前先重新量測。中文 UTF-8 每字 3 bytes，行數少不代表 bytes 少。接近上限時優先
刪掉可由 codebase 或架構測試推得的內容，不要複製到轉接檔分攤。

## Claude Code 的強制層

`AGENTS.md` 與 `CLAUDE.md` 是 context，不是強制層。官方文件明講這一點：Claude 會讀，但不保證遵守，
真正在執行前攔截的是 `permissions` 規則。因此 `.claude/settings.json` 把 `AGENTS.md` 已有的禁令升級為
客戶端攔截，內容不超出 `AGENTS.md`，也不建立新規則：

| 規則類型 | 涵蓋範圍 | 對應的 `AGENTS.md` 條文 |
|:---|:---|:---|
| `deny` | 直接 `dotnet test` | 正式測試一律從 `tools/verify.ps1` 進入 |
| `deny` | `git add -A`／`--all`／`-u`／`.`、`git write-tree`、`git stash --all` | 只能加入使用者確認的明確路徑 |
| `deny` | 讀取或寫入六個私人資料根目錄 | 未獲當次授權不得讀取或掃描私人案件目錄 |
| `deny` | 修改兩份已裁定保留的 HTML 範本 | 沒有明確要求時不得修改 |
| `ask` | `git commit`、`push`、`remote`、`reset`、`checkout`、`restore`、`clean`、`stash`、`add` | 沒有明確指令時不得執行這些操作 |
| `ask` | 編輯 `docs/history/**` | 歷史文件是引用的原文來源，不得被日常改寫默默變動；動它要先問過人 |

`deny` 永遠壓過 `allow`；複合命令會逐段比對，所以 `cd x && dotnet test` 一樣被擋；`timeout`、`nice`
這類 wrapper 會先剝除再比對。`ask` 不是永久封鎖，它只是強制在執行前問過人，符合「第一次 commit 或
push 前必須由使用者另行確認」。

這一層有明確界線，不能當成完整防護：

- 它只擋 Claude Code 自己發出的命令與檔案工具。子程序自行開檔不受影響——`PrivateCase` 正是靠子程序
  讀取授權後的私人副本，所以不會被上面的 `Read` 規則擋住。
- `Read` 與 `Edit` 的路徑規則只作用於同名工具。透過 Bash 讀取私人目錄不在攔截範圍內，那一層仍由
  `AGENTS.md` 與架構測試負責。
- Codex 沒有可攜的逐命令 `deny`；`approval_policy` 這類鍵放在專案層 `.codex/config.toml` 會被忽略。
  跨工具的共同底線仍然是 `AGENTS.md` 規則加上架構測試，permissions 是 Claude 側的加層，不是替代。

## 三個 Claude Code hook

`.claude/hooks/` 有三個 PowerShell hook，都只讀 Git 中繼資料、`artifacts/harness/runs/` 的收據與候選清單，
不讀取私人案件目錄，也不輸出檔案內容。任何一步失敗都靜默放行，不影響 session。

| 檔案 | 事件 | 作用 |
|:---|:---|:---|
| `session-context.ps1` | `SessionStart` | 把實際的 branch、HEAD、工作樹狀態與最近一次各命令的收據放進開場脈絡 |
| `managed-doc-notice.ps1` | `PostToolUse`（`Edit`／`Write`） | 改到受管文件時提醒執行 `Documentation` 並重讀改動段落 |
| `verification-debt.ps1` | `Stop` | 列出改了但沒有更新通過收據的範圍，第一次擋住結束；另外點出測試斷言變動與未列入候選清單的新檔 |

`session-context.ps1` 的存在理由是 `AGENTS.md` 要求接手時直接查 Git，不能只依長期文件判斷；把這件事
自動化就不會漏。它輸出的收據只描述當次執行，候選內容變動後不能沿用，也不是專案記憶——`artifacts/`
由 Git 忽略，fresh clone 後不存在。

`verification-debt.ps1` 比較檔案修改時間與收據完成時間，判斷粗但不會漏報改動。2026-09-02 起它有欠帳時
會用 exit code 2 擋住第一次結束，把清單交回給 Claude；同一個 session 裡，同一個命令在同一個檔案時間戳
下只擋一次，狀態記在 `artifacts/harness/hooks/`，之後只提醒。這是 Claude Code 官方建議的用法：Stop hook
可以當確定性的關卡，輸入的 `stop_hook_active` 用來避免循環，Claude Code 自己也會在連續擋 8 次後放行。
被擋住時正確的回應不是硬跑命令，而是執行對應驗證，或在回覆裡逐項寫明「未執行」與原因再結束。它另外
會提醒兩件事，但不擋：`src/JET/tests/` 的改動若移除 `Assert.`、移除 `[Fact]` 或 `[Theory]`、新增 `Skip =`，
要對照 `AGENTS.md`「測試」說明原因；未追蹤的新檔若不在候選清單，提交後 `ReleaseCandidate` 會失敗。
它不替任何命令背書。

這三個 hook 與 `permissions` 都是 Claude Code 專屬。Codex、Copilot 與 VS Code AI Agent 看不到它們，
因此任何規則、驗收條件或架構判斷都不得以它們為前提。

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
不自然用語與符號代句只產生 warning，每條附一句修正提示，最後仍要由人閱讀。這只能降低規則漂移，不能
保證每個模型每次都完全遵守。

## 如何確認 VS Code／Copilot 實際載入

在 VS Code 的 Chat 檢視開啟 Diagnostics，確認 `AGENTS.md` 與 `.github/copilot-instructions.md` 已列入目前
workspace。若使用 Claude Code，再用其 memory 檢視確認根目錄 `CLAUDE.md` 已載入 `AGENTS.md`。這是工具端
的實際載入證據；只看到檔案存在還不夠。

## 目前依據

下列官方資料於 2026-08-29 查核，其中 GitHub Copilot 支援矩陣於 2026-08-30 重新確認，Claude Code 的
best practices 與 hooks 參考於 2026-09-02 查核；平台支援若改變，只更新本文件與薄轉接方式，不複製一套
新的 JET 規則：

- [GitHub Copilot 指示檔支援矩陣](https://docs.github.com/en/copilot/reference/custom-instructions-support)
- [VS Code custom instructions](https://code.visualstudio.com/docs/agent-customization/custom-instructions)
- [Claude Code 專案記憶與 `AGENTS.md` 匯入](https://code.claude.com/docs/zh-CN/memory)
- [Claude Code best practices](https://code.claude.com/docs/en/best-practices)：CLAUDE.md 要短、只放程式推不出來的事；
  給 Claude 可自己跑的檢查；Stop hook 可以擋住結束直到檢查通過，連續擋 8 次後會被覆寫
- [Claude Code hooks 參考](https://code.claude.com/docs/en/hooks)：exit code 2 才會攔截，`Stop` 事件輸入含
  `stop_hook_active`，用它避免無限循環
- [OpenAI Codex 建立與載入 skills](https://learn.chatgpt.com/docs/build-skills)
- [OpenAI Codex 的 `AGENTS.md` 載入方式](https://learn.chatgpt.com/docs/agent-configuration/agents-md)
- [OpenAI Codex 本機 memories](https://learn.chatgpt.com/docs/customization/memories)

## 不由儲存庫檔案保證的事情

- 使用者層或組織層指示可能有更高優先順序；遇到衝突要回報，不能假裝 repo 規則已生效。
- repo 文件不能技術上阻止外部 Agent 執行 Git 或上傳資料。實際權限、sandbox 與人工審閱仍然必要。
- Agent 是否使用某個模型、是否能上網或是否有子 Agent，不是產品執行條件，也不寫進 JET 的 runtime 設定。
