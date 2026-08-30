# JET 的 AI Agent 相容方式

更新日期：2026-08-30

JET 不為每一個 AI 工具維護一套獨立規則。完整共用規則只放在根目錄 `AGENTS.md`；其他檔案只負責讓
各工具找到同一份規則，或保留平台無法可靠追蹤連結時必須先看到的安全摘要。

## 各工具讀取入口

| 工具 | 儲存庫入口 | 本專案做法 |
|:---|:---|:---|
| Codex | `AGENTS.md` | 直接讀取共用權威 |
| Claude Code | `CLAUDE.md` | 用 `@AGENTS.md` 原生匯入共用權威，不複製完整規則 |
| GitHub Copilot | `.github/copilot-instructions.md` | 指向 `AGENTS.md`，並保留 Git、機敏資料與驗證入口的短摘要 |
| VS Code AI Agent | `.github/copilot-instructions.md` 與 `AGENTS.md` | 使用 workspace 指示檔；不需要複製舊專案的 VS Code 實驗性設定 |

GitHub 不同 Copilot 介面支援的指示檔不完全相同，因此 `.github/copilot-instructions.md` 仍有必要。
2026-08-30 依官方支援矩陣重新查證：VS Code、Copilot coding agent 與 Copilot CLI 會原生讀 `AGENTS.md`；
Visual Studio、JetBrains 與 GitHub.com 的 Chat 不會，只讀 `.github/copilot-instructions.md`。所以轉接檔
的安全摘要是那三個介面唯一會看到的規則，不能刪減，也不能建立和 `AGENTS.md` 相反的規則。VS Code 同時
載入多種指示檔時內容不保證固定順序，結論相同。

## 載入預算

兩個主要 host 對指示檔都有量的限制，超出時不會報錯，只會靜默截斷或降低遵循度。2026-08-30 量測：

| Host | 限制 | 目前用量 |
|:---|:---|:---|
| Codex | 各層 `AGENTS.md` 合併總量預設 32 KiB | `AGENTS.md` 5,357 bytes，約 16% |
| Claude Code | 官方建議單檔 200 行以內；`@AGENTS.md` import 不會降低 context 用量 | `CLAUDE.md` 9 行＋`AGENTS.md` 83 行＝92 行 |

2026-08-30 依 harness engineering 的共識把 `AGENTS.md` 從 131 行的規則全文改寫為 83 行的地圖：只保留
不可協商的邊界、常用命令與指向各文件的導航表；被移出的細則都在 `docs/` 有唯一的家（寫作規範在
`docs/README.md`，資料與 legacy 規則在 `docs/data-and-legacy.md`，驗證細節在 `docs/harness.md` 與
`tools/README.md`）。之後在 `AGENTS.md` 新增內容前，先確認它是邊界或導航，不是可以放進 `docs/` 的
細節。

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
  跨工具的共同底線仍然是「`AGENTS.md` 規則＋架構測試」，permissions 是 Claude 側的加層，不是替代。

## 三個 Claude Code hook

`.claude/hooks/` 有三個 PowerShell hook，都只讀 Git 中繼資料與 `artifacts/harness/runs/` 的收據，不讀取
私人案件目錄，也不輸出檔案內容。任何一步失敗都靜默結束，不影響 session。

| 檔案 | 事件 | 作用 |
|:---|:---|:---|
| `session-context.ps1` | `SessionStart` | 把實際的 branch、HEAD、工作樹狀態與最近一次各命令的收據放進開場脈絡 |
| `managed-doc-notice.ps1` | `PostToolUse`（`Edit`／`Write`） | 改到受管文件時提醒執行 `Documentation` 並重讀改動段落 |
| `verification-debt.ps1` | `Stop` | 列出改了但沒有更新通過收據的範圍 |

`session-context.ps1` 的存在理由是 `AGENTS.md` 要求接手時直接查 Git，不能只依長期文件判斷；把這件事
自動化就不會漏。它輸出的收據只描述當次執行，候選內容變動後不能沿用，也不是專案記憶——`artifacts/`
由 Git 忽略，fresh clone 後不存在。

`verification-debt.ps1` 只比較檔案修改時間與收據完成時間，判斷粗但不會漏報改動。它是提醒不是關卡，
不阻止 session 結束，也不替任何命令背書。

這三個 hook 與 `permissions` 都是 Claude Code 專屬。Codex、Copilot 與 VS Code AI Agent 看不到它們，
因此任何規則、驗收條件或架構判斷都不得以它們為前提。

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
不自然用語只產生 warning，最後仍要由人閱讀。這只能降低規則漂移，不能保證每個模型每次都完全遵守。

## 如何確認 VS Code／Copilot 實際載入

在 VS Code 的 Chat 檢視開啟 Diagnostics，確認 `AGENTS.md` 與 `.github/copilot-instructions.md` 已列入目前
workspace。若使用 Claude Code，再用其 memory 檢視確認根目錄 `CLAUDE.md` 已載入 `AGENTS.md`。這是工具端
的實際載入證據；只看到檔案存在還不夠。

## 目前依據

下列官方資料於 2026-08-29 查核，其中 GitHub Copilot 支援矩陣於 2026-08-30 重新確認；平台支援若改變，
只更新本文件與薄轉接方式，不複製一套新的 JET 規則：

- [GitHub Copilot 指示檔支援矩陣](https://docs.github.com/en/copilot/reference/custom-instructions-support)
- [VS Code custom instructions](https://code.visualstudio.com/docs/agent-customization/custom-instructions)
- [Claude Code 專案記憶與 `AGENTS.md` 匯入](https://code.claude.com/docs/zh-CN/memory)
- [OpenAI 使用 `AGENTS.md` 保存專案脈絡](https://openai.com/business/guides-and-resources/how-openai-uses-codex/)

## 不由儲存庫檔案保證的事情

- 使用者層或組織層指示可能有更高優先順序；遇到衝突要回報，不能假裝 repo 規則已生效。
- repo 文件不能技術上阻止外部 Agent 執行 Git 或上傳資料。實際權限、sandbox 與人工審閱仍然必要。
- Agent 是否使用某個模型、是否能上網或是否有子 Agent，不是產品執行條件，也不寫進 JET 的 runtime 設定。
