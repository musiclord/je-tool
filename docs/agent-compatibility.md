# JET 的 AI Agent 相容方式

更新日期：2026-09-18

JET 不為每一個 AI 工具維護一套獨立規則。完整共用規則只放在根目錄 `AGENTS.md`；其他檔案只負責讓
各工具找到同一份規則，或保留平台無法可靠追蹤連結時必須先看到的安全摘要。

## 各工具讀取入口

| 工具 | 儲存庫入口 | 本專案做法 |
|:---|:---|:---|
| Codex | `AGENTS.md` | 直接讀取共用權威 |
| Claude Code | `CLAUDE.md` | 用 `@AGENTS.md` 原生匯入共用權威；專案 skill 只保留薄入口 |
| GitHub Copilot | `.github/copilot-instructions.md` | 指向 `AGENTS.md`，並保留 Git、機敏資料與驗證入口的短摘要 |
| VS Code AI Agent | `.github/copilot-instructions.md` 與 `AGENTS.md` | 使用 workspace 指示檔；不需要複製舊專案的 VS Code 實驗性設定 |

2026-09-18 重新核對[官方支援矩陣](https://docs.github.com/en/copilot/reference/custom-instructions-support)：
Copilot 各介面支援的指示檔不同，不能把 VS Code 的支援推論到 Visual Studio 或一般網頁 Chat。
因此保留 `.github/copilot-instructions.md` 的共用入口與安全摘要；實際是否載入仍依工具的診斷畫面確認。

## 前端設計與瀏覽器相容性

各工具共用 [`development-guide.md` 的前端設計模式](development-guide.md#前端設計模式)，不建立第二套審計邏輯。
下表於 2026-09-09 查核；官方具備某項能力，不代表本機帳號、版本或組織政策已允許它。

| 工具 | 適合的入口 | 在 JET 的用法與界線 |
|:---|:---|:---|
| Codex 桌面版 | 內建瀏覽器及畫面註解 | 已在本次 JET 調整中使用；開啟本機合成預覽，Agent 寫回正式前端。Adjust 仍以版本實際支援為準，未列為必要能力。 |
| Claude Desktop 的 Code | 本機 session 的 Preview | 官方支援開發伺服器、DOM 與畫面操作；專案提供 `.claude/launch.json` 啟動同一預覽。本機 Claude UI 尚未實測，不宣稱註解方式和 Codex 完全相同。 |
| Claude Design | Desktop 側欄或網頁設計畫布 | 官方支援設計迭代與交回 coding agent；JET 採用最小合成設計包，不把畫布當成直接操作本機 WebView2 的環境。尚未實際上傳或交接驗收。 |
| Claude Desktop 一般聊天、雲端 coding session | 視目前可用工具而定 | 不假設能讀取這台 Windows 的 repo、localhost 或根目錄指示檔；缺少本機工具時改用 Code 本機 session，或明確提供必要交接內容。 |

Claude Code 的官方預覽設定預設使用 `localhost`，JET 預覽目前只接受 `127.0.0.1` 的指定主機標頭。
因此設定明寫同一個 `port`、`url` 與 `env.PORT`，停用自動換埠，避免啟動的服務與畫面網址不一致。
連接埠衝突由 Agent 辨識後調整這三個值；不放寬服務的對外存取限制。未指定 PORT 的原有啟動方式不變。

Claude Design 官方提供 `/design-sync`、Design MCP 和交回 Claude Code 的方式，也可交給其他 coding agent。
本輪只研究相容方法，沒有配置帳號、MCP、雲端專案或上傳檔案。下載內容須由本機 Agent 核對原有元件與契約後採用，
不保證輸出的 HTML 可直接覆蓋 JET。原始碼、現行規格與唯一計畫才是跨工具的共同上下文；畫面註解不會自動跨平台同步。

官方來源：

- [OpenAI Browser](https://learn.chatgpt.com/docs/browser)：內建瀏覽器及其操作方式；JET 的註解協作另有本次使用者驗收。
- [Claude Code Desktop](https://code.claude.com/docs/en/desktop#configure-preview-servers)：Code 本機預覽、啟動設定、URL 與連接埠規則。
- [Claude Design 入門](https://support.claude.com/en/articles/14604416-get-started-with-claude-design)：設計畫布、匯入與 Design MCP。
- [Claude Design 原型與 UX](https://academy.claude.com/tutorials/using-claude-design-for-prototypes-and-ux)：設計交接內容與 coding agent 接續。

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

[Codex 官方文件](https://learn.chatgpt.com/docs/agent-configuration/agents-md)說明各層專案指示預設合計
上限為 32 KiB；這不是單看本儲存庫檔案就能判定的剩餘額度。
[Claude Code 官方文件](https://code.claude.com/docs/en/memory)建議精簡指示，將局部程序按需載入；
建議篇幅不等於超過便截斷的硬性限制，`@AGENTS.md` 匯入也不會省去其上下文用量。

`AGENTS.md` 保留不可協商的邊界、命令與導航，不累加每次失敗的故事。增加內容前先核對現有文件能否承接，
需要量測時直接讀目前檔案大小，不用歷史數字推論載入正常。各工具的全域設定也可能影響最後載入結果。

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

`verification-debt.ps1` 比較檔案修改時間與收據完成時間，只能提供粗略提醒，不能證明內容或需求已覆蓋。2026-09-02 起它有欠帳時
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

## 換模型或上下文壓縮後如何續接

相容性取決於模型、承載工具與執行環境。不同模型共用同一份 JET 規則，沒有依模型名稱另建工作流程。
沒有 skill、自動 memory 或 Claude hooks 的工具，仍可讀 `AGENTS.md`，執行 `Context`，再讀目前計畫的
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
