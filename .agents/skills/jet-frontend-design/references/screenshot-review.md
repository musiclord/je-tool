# 截圖檢查

這份說明讓 Agent 自己看得到畫面，又不打擾使用者：截圖在背景執行的 Edge 裡完成，不在桌面開視窗，
也不連使用者在 Codex 開的瀏覽器主機（`127.0.0.1:4244`）。

## 準備合成預覽

依 `.agents/harness/frontend-design-mode.md` 的「合成預覽」先產生素材，再啟動預覽服務：

1. 用 Focused 命令與 `JET_FRONTEND_PREVIEW_EXPORT=1` 產生 `artifacts/frontend-preview/fixtures.json`。
   C# 或 action 契約改過後要重新產生；只改 CSS 不必。
2. Claude Code 用 Preview 啟動 `.claude/launch.json` 的 `JET synthetic frontend`，其他工具執行
   `node tools/harness/frontend-preview/server.cjs`。服務在 `127.0.0.1:4243`，一小時後自動結束，結束後重新啟動即可。

合成預覽備有第五步的固定答案，也能從左欄切到第一到第五步，看到這些步驟用既有狀態畫出的樣子。
需要後端回應的子區塊會顯示「此操作需在 JET 桌面程式執行」或「預覽載入失敗」，這是預覽的限制，不是產品錯誤。

## 截圖

腳本在本 skill 目錄的 `scripts/preview-shot.cjs`。它需要 `playwright-core`，本專案沒有安裝；
可以設定 `JET_PLAYWRIGHT_CORE` 指向已有的資料夾，或沿用 Playwright MCP 外掛留在 npm 快取裡的那一份。
找不到時腳本會停下來說明，不會自行下載；要不要安裝由使用者決定。

```powershell
node .agents/skills/jet-frontend-design/scripts/preview-shot.cjs --step=5 --out=artifacts/frontend-preview/shots/step5.png
```

常用參數：

- `--step=1` 到 `--step=6`：點左欄的第幾個步驟。
- `--scene=matches`、`empty` 或 `stale`：第五步的固定情境。
- `--overview`：開啟流程總覽。
- `--width=1536 --height=864`：預設是 1920×1080、縮放 125% 的畫面大小；檢查窄版時改成 `--width=1024`。
- `--css=檔案`：在正式樣式之後另外載入一份樣式表，只用在方向樣張，不改正式前端。

截圖存在 Git 忽略的 `artifacts/` 底下。

## 看截圖的順序

1. 先瞇眼看：主要區塊、次要區塊與目前步驟是否一眼分得出來。
2. 對照 `SKILL.md` 的禁止清單逐項看，命中的地方記下選擇器。
3. 對照 `data-and-cjk-rules.md`：數字對齊、最小字級、對比、焦點、狀態是否有文字或符號。
4. 換一個情境再看：多組條件、空結果、錯誤、窄版與右側資料預覽展開時。
5. 改完重截同一張，前後並排比較，確認沒有改壞其他步驟。

## 交付前的一次獨立檢查

交給使用者確認版本前，請一位沒參與開發的助手做一次檢查，不必每輪做。只提供下列內容，不提供開發過程：

```text
你是第一次看到這個介面的資深介面設計審查者。以下是 JET 的設計方向、禁止清單與截圖。
請只找兩類問題：一、命中禁止清單或看起來像 AI 預設樣式的地方；二、違反資料密集畫面與繁體中文排版規則的地方。
每個問題寫出在哪張截圖、哪個區塊、為什麼，以及對審計員的影響。沒有把握的標成待確認。
不要提出和方向無關的個人偏好，也不要重新設計整個畫面。
```

被要求挑錯的審查者幾乎一定會回報一些問題，所以只處理和需求、禁止清單或規則有關的發現；其餘記下即可。
