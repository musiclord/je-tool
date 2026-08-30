# JET 的 AI Agent 相容方式

更新日期：2026-08-29

JET 不為每一個 AI 工具維護一套獨立規則。完整共用規則只放在根目錄 `AGENTS.md`；其他檔案只負責讓
各工具找到同一份規則，或保留平台無法可靠追蹤連結時必須先看到的安全摘要。

## 各工具讀取入口

| 工具 | 儲存庫入口 | 本專案做法 |
|:---|:---|:---|
| Codex | `AGENTS.md` | 直接讀取共用權威 |
| Claude Code | `CLAUDE.md` | 用 `@AGENTS.md` 原生匯入共用權威，不複製完整規則 |
| GitHub Copilot | `.github/copilot-instructions.md` | 指向 `AGENTS.md`，並保留 Git、機敏資料與驗證入口的短摘要 |
| VS Code AI Agent | `.github/copilot-instructions.md` 與 `AGENTS.md` | 使用 workspace 指示檔；不需要複製舊專案的 VS Code 實驗性設定 |

GitHub 不同 Copilot 介面支援的指示檔不完全相同，因此 `.github/copilot-instructions.md` 仍有必要；不能只
假設所有 Copilot 介面都會讀 `AGENTS.md`。VS Code 若同時載入多種指示檔，內容不保證固定順序，所以轉接檔
不能建立和 `AGENTS.md` 相反的規則。

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

下列官方資料於 2026-08-29 查核；平台支援若改變，只更新本文件與薄轉接方式，不複製一套新的 JET 規則：

- [GitHub Copilot 指示檔支援矩陣](https://docs.github.com/en/copilot/reference/custom-instructions-support)
- [VS Code custom instructions](https://code.visualstudio.com/docs/agent-customization/custom-instructions)
- [Claude Code 專案記憶與 `AGENTS.md` 匯入](https://code.claude.com/docs/zh-CN/memory)
- [OpenAI 使用 `AGENTS.md` 保存專案脈絡](https://openai.com/business/guides-and-resources/how-openai-uses-codex/)

## 不由儲存庫檔案保證的事情

- 使用者層或組織層指示可能有更高優先順序；遇到衝突要回報，不能假裝 repo 規則已生效。
- repo 文件不能技術上阻止外部 Agent 執行 Git 或上傳資料。實際權限、sandbox 與人工審閱仍然必要。
- Agent 是否使用某個模型、是否能上網或是否有子 Agent，不是產品執行條件，也不寫進 JET 的 runtime 設定。
