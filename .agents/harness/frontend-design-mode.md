# 前端設計模式

更新日期：2026-10-16

這份文件說明如何在 JET 調整前端、取得畫面證據並選擇驗證。設計方向依
[jet-frontend-design](../skills/jet-frontend-design/SKILL.md)，已接受的操作與用語依
[前端說明](../../docs/jet-frontend-description.md)，業務意義依 [JET 規格](../../docs/jet-guide.md)。

使用者明確要求修正或實作時，在授權範圍內直接完成。只提出畫面註解、驗收發現或請 Agent 釐清來由時，
依 [development-workflow.md](development-workflow.md) 的提案階段處理。一般排版、樣式與可回復的實作選擇由 Agent 判斷，不反覆要求確認。

## 選擇預覽工具

| 需要確認的事 | 工具 | 能證明的範圍 |
|:---|:---|:---|
| 樣式、空結果、第五步固定情境 | `tools/harness/frontend-preview/` | 正式前端配合固定回應的畫面，不會重算業務結果 |
| 六步驟互動、實際 action、分頁與資料庫結果 | `tools/harness/browser-host/` | 正式 `ActionDispatcher` 與合成案件，不包含 WebView2 和原生對話框 |
| 真正 WinForms、WebView2 與關窗流程 | `verify.ps1 -Command Gui -Configuration AgentGuiTest` | 所選 GUI 情境；在另一個 Windows 桌面執行 |

兩個預覽工具都只供開發，不進正式封裝。前端直接讀 `src/JET/JET/wwwroot/`，不維護第二份手工同步的前端。

### 合成預覽

首次使用或 C#、action 契約變更後，重新產生固定素材；只改 CSS 或呈現用 JS 時不必重建同一份素材。

```powershell
try {
    $env:JET_FRONTEND_PREVIEW_EXPORT = '1'
    pwsh -NoProfile -File tools/verify.ps1 -Command Focused -Configuration Release -Filter 'FrontendPreviewFixtureTests'
    if ($LASTEXITCODE -ne 0) { throw '合成素材驗證未通過，先查看該次收據。' }
} finally {
    Remove-Item Env:JET_FRONTEND_PREVIEW_EXPORT -ErrorAction SilentlyContinue
}
node tools/harness/frontend-preview/server.cjs
```

服務顯示本次 `127.0.0.1` 網址，預設自選空閒埠；Claude Preview 的 `JET synthetic frontend` 固定使用 4243。
確認服務屬於本工作目錄後開啟。刷新會重設預覽草稿；Ctrl+C 可停止，未手動停止時一小時後結束。
工具只有 `matches`、`empty` 與 `stale` 等固定回應，未知請求不假裝成功。需要排序、保存或其他實際結果時改用瀏覽器主機。
截圖做法見 [screenshot-review.md](../skills/jet-frontend-design/references/screenshot-review.md)。

### 接上正式後端的瀏覽器主機

主機只綁定 `127.0.0.1`，action 交給正式 C# 程式，事件用 Server-Sent Events 傳送；不讀 SQL Server 連線設定。
資料、日誌與快取放在 `artifacts`；終端列出每個 action 的結果與 correlation id，可對照該主機資料夾下 `logs/` 的診斷日誌。
`JET_BROWSER_HOST_SEED` 可選 `demo` 或 `taiwan-sme`；後者會建立 SQLite、DuckDB 與待配對案件，並把結果和產生器的獨立答案比對。合成資料來源在 `tools/harness/browser-host/TaiwanSme/`。

#### 台灣中小企業合成案件與交錯操作檢查

固定分工依使用者原話：「4245 給我操作，4246 做無頭檢查，不要碰 4244；同時執行的代理最多三個。」
來源是 [KCT 需求計畫](../../docs/history/specs/2026-10-06-kct-gl-requirements-plan.md) 2026-10-14 的交接提示；
同一計畫第九節另記明 4244 是使用者在 Codex 檢查畫面的主機，清除或重建它的案件前先問使用者。

| 連接埠 | 用途 | `.claude/launch.json` 名稱 | 資料與建置目錄 |
|:---|:---|:---|:---|
| 4243 | 固定素材預覽 | `JET synthetic frontend` | `artifacts/frontend-preview/` |
| 4244 | 使用者在 Codex 檢查畫面；Claude 不連線、不重啟也不清除 | `JET browser host` | 不由這份流程操作 |
| 4245 | 使用者操作 | `JET browser host (Claude)` | `artifacts/browser-host-claude/` |
| 4246 | Agent 無頭檢查 | `JET browser host (Claude QA)` | `artifacts/browser-host-claude-qa/` |

名稱中的 Claude 不限制工具種類。自動檢查不操作 4245，也不為了測試重建使用者正在操作的案件。
下面命令只啟動 4246，並以獨立建置輸出避免和其他主機搶檔案：

```powershell
try {
    $env:PORT = '4246'
    $env:JET_BROWSER_HOST_DATA = 'browser-host-claude-qa'
    $env:JET_BROWSER_HOST_SEED = 'taiwan-sme'
    dotnet run --project tools/harness/browser-host/BrowserHost.csproj -c Debug --no-launch-profile --artifacts-path artifacts/browser-host-claude-qa/build
} finally {
    Remove-Item Env:PORT, Env:JET_BROWSER_HOST_DATA, Env:JET_BROWSER_HOST_SEED -ErrorAction SilentlyContinue
}
```

只改 CSS 或 JS 後刷新即可；改 C# 後重啟本次擁有的 QA 主機。需要重建 QA 案件時，先確認目標確實是 QA，
在同一命令後加 `-- --reset --seed-only`，完成後再啟動主機。這會取代 QA 合成案件，不能套在使用者主機。
啟動前先辨識既有服務；不要只靠連接埠或程序名稱終止服務。

需要跨步驟交錯操作時，執行 `node tools/harness/browser-host/interleaving-check.cjs`。它使用無頭 Edge，拒絕連到 4244 與 4245，
結果放在 QA 目錄的 `interleaving/`。它會改動 QA 案件，執行前先重建 QA 案件；從 Preview 啟動 QA 主機時，
關掉 Preview 開的分頁，避免兩個前端同時操作同一台主機。
腳本是開發診斷，不能宣稱為正式 Gui 通過；單純樣式修改也不需要每輪執行整份交錯檢查。

瀏覽器主機仍會呼叫原生選檔和檔案總管；無頭檢查不要按會開這些視窗的操作。不要以桌面 computer use 代替無頭檢查。

## 設計迭代與正式整合

1. 讀本次相關的現行規格、元件與契約，確認既有變更和本輪範圍。小改不另立計畫；跨 session 工作沿用唯一現行計畫。
2. 完成使用者要求，優先沿用共用樣式與元件。需要調整 class、資料屬性、JS 或後端契約時，一起核對呼叫端、測試與文件；
   不用「設計模式只能改 CSS」限制必要修正。日期邊界、金額基準、空白、借貸、同分錄或同傳票、AND 或 OR、
   版本失效及報告意義不因版面簡化而改變；要改這些語意，先列出操作前後的差異請使用者裁定。
3. 用受影響的合成情境看實際畫面。依問題選長標頭、大筆數、空結果、錯誤、窄版、鍵盤焦點與連續操作，不要求每個小改都跑完所有情境。
4. 先執行受影響的 Focused；前端互動用 `Contract -ContractScenario FrontendMapping`，預覽工具用 `FrontendPreview`，文件用 `Documentation`。
   修正失敗後重跑受影響的檢查；已通過且未再受影響的部分不重複執行。完整 `Public` 留給跨層回歸或整合需要。
5. 使用者確認版本並要求正式整合時，依驗收範圍執行 Build、Contract、Public、Package、Gui、Excel 與 Documentation。
   視覺獨立檢查只在整合時交給一位未參與實作的助手做一次；一般調整和每次修正不反覆複審。
6. 回報實際修改、已驗範圍與未執行項目。長期行為寫回前端說明，需求原話與下一動作依大型開發流程保存。

`Gui` 已在另一個 Windows 桌面執行，不搶使用者的滑鼠或前景；仍會占用 CPU，執行時機依 `AGENTS.md`。
`ReleaseCandidate` 需要乾淨的已提交來源，不能為了執行它自行做 Git 交付。命令細節以 [tools/README.md](../../tools/README.md) 為準。

## 設計工具的交接

使用者要求另試方向時，可以用同一個合成畫面比較少量明確不同的樣張。現行方向已明確時直接延續，不為日常修正重新選風格。
註解或瀏覽器暫時調整要寫回正式原始碼才算完成；註解能力不足時，可直接依使用者指出的位置檢查 DOM 和畫面。

Claude Design 或其他外部設計工具只接收當次授權、與畫面有關的前端素材及合成畫面。真實案件、公司檔案與整份儲存庫不隨設計包上傳。
包與下載結果放在 ignored `artifacts/frontend-preview/`，採用前核對現行元件、契約與使用者裁定，不直接覆蓋產品。
本機網址不可用時以合成畫面交接，不為設計工具架公開通道。

換 session 時的短指示可寫成：

```text
請依 .agents/harness/frontend-design-mode.md 延續 JET 已接受的設計，修正這次指出的畫面與操作問題，完成受影響的驗證並更新相關文件。
```
