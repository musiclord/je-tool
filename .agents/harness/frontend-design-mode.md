# 前端設計模式

這份文件給在 JET 調整前端的 AI 編碼工具讀：怎麼開合成預覽與瀏覽器主機、每輪調整要做什麼檢查、使用者確認版本後怎麼做正式整合、
怎麼把設計工具的結果交回正式前端。人類開發者只需要知道兩個預覽工具怎麼啟動，寫在 `docs/development-guide.md` 的「開發用的預覽工具」。
畫面的已接受設計以 `docs/jet-frontend-description.md` 為準，審計規則以 `docs/jet-guide.md` 為準；本文件不另立第二套。

## 合成預覽

需要微調第五步時，可在 Codex 內建瀏覽器或 Claude Desktop 的 Code 預覽查看正式前端的合成情境。
本節是各 Agent 共用的操作方法；平台支援與 Claude Design 交接界線見
[`agent-compatibility.md`](agent-compatibility.md#前端設計與瀏覽器相容性)。
工具在 `tools/harness/frontend-preview/`，不屬於正式 JET，也不需要安裝新的套件。
開發電腦需要已有的 Node.js；公司端執行 JET 不需要 Node.js 或預覽服務。

先在 PowerShell 執行以下命令。合成測試會經正式 C# action 匯入四列分錄、配對與驗證，
確認日期和金額條件的固定答案，再輸出預覽素材。環境變數只用於此次明示產生，不設定成全域設定。

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

確認 Focused 通過後才啟動 Node.js。終端會顯示本次專用的 `127.0.0.1` 網址；在目前 Agent 的內建瀏覽器開啟。
服務只提供正式前端資源與合成素材，不提供案件、任意檔案或寫入 API。按 Ctrl+C 停止；未手動停止時一小時後結束。
前端檔案修改後刷新頁面即可；刷新會重設尚未保存的預覽草稿。正式 C# 或 JetApi 契約變更後，必須重新產生素材。

頁面上方可切換日期與金額的「有結果」、「無結果」和「資料已變更（結果待更新）」，並查看最近 50 個 action 請求。
有結果時固定命中兩筆分錄、一張傳票，可展開明細。這是經固定答案核對的回放，不是瀏覽器內的審計引擎。
結果待更新情境使用同一份合成案件，將預篩選及條件篩選標成過期，移除舊預篩選摘要並標示相依報告。
第四、五步不再出現全頁黃色提示；此情境仍可用來確認未沿用舊結果。它不重算篩選，也不代表真正匯入了新資料。
改成未準備的條件、排序、搜尋、保存、匯出或其他 action 時，工具不會假裝成功。
畫面本身的條件編輯、展開與焦點仍由正式 JS 處理；未知結果不能作為業務判斷。

使用 Annotation 選取元件或區域後留言；若目前 Codex 版本提供 Adjust，可先試字體、間距與顏色。
這些操作仍需由 Agent 寫回正式 CSS 或 JS。工具不自行修改原始碼，也不把瀏覽器暫時調整當成已保存。
本次註解協作與前端調整已於 2026-09-09 通過使用者驗收；Adjust 尚未實際試用，不是啟動模式的必要條件。
若目前工具沒有畫面註解，使用者可在聊天指出畫面元件，Agent 自行查看 DOM 和畫面，不要求重做整份截圖說明。

### 接上正式後端的瀏覽器主機

上面的合成預覽只回放第五步的固定答案。要在瀏覽器裡操作六個步驟，或確認前端和後端的實際互動時，
改用 `tools/harness/browser-host/`。它是開發用的本機服務，不屬於正式 JET，也不進封裝。

- 畫面直接讀取 `src/JET/JET/wwwroot/` 原檔。前端送出的 action 經同源 HTTP 交給正式 `ActionDispatcher`，
  資料庫、審計運算和報表都是正式程式碼；後端推給前端的事件改用 Server-Sent Events 傳送。
- 服務只綁定 `127.0.0.1`，只接受同源的 JSON 請求。它不讀 `JET_SQLSERVER_CONNECTION`，也不連 SQL Server。
- 案件、診斷日誌和使用者快取都放在 Git 忽略的 `artifacts/browser-host/`。案件目錄是空的時，主機會經正式
  action 建立兩個合成案件。「合成示範-篩選就緒」完成匯入、配對、驗證和預篩選，保存在第五步；
  「合成示範-待配對」只匯入總帳和試算表，保存在第三步。資料來自 `DemoDataFactory`，不使用私人案件。
- 主機以 Debug 組建執行，所以開發面板和 `demo.*` action 都能用。選檔和存檔對話框會在這台電腦的桌面彈出。

Claude Desktop 在 Code 的本機 session，由 Preview 啟動 `.claude/launch.json` 的 `JET browser host`，
網址是 `http://127.0.0.1:4244`。其他工具在終端機執行下列命令；加上 `-- --reset` 會清掉舊的合成案件後重建。

```powershell
dotnet run --project tools/harness/browser-host/BrowserHost.csproj -c Debug --no-launch-profile
```

改 CSS 或 JS 後重新整理頁面即可。改 C# 後要停止並重新啟動服務，`dotnet run` 會先重新建置。
Preview 停止服務時只會結束外層的 `dotnet run`，所以主機會監看啟動它的程序，該程序結束時主機也跟著結束，釋放連接埠。
終端機會列出每個 action 的名稱、結果和耗時，失敗時附上錯誤碼和 correlation id，方便對照
`artifacts/browser-host/logs/` 的 Debug 診斷日誌。需要中斷點時，可用 VS Code 的「JET (attach)」附加到
`Jet.BrowserHost` 程序。

瀏覽器主機不能取代真正的 WebView2 驗證。關窗流程、WebView2 本身的行為和桌面視窗尺寸，
仍要用 `verify.ps1 -Command Gui` 或 AgentGuiTest 確認；正式整合的命令順序見下方「設計迭代與正式整合」。
`Gui` 會在使用者桌面開啟並搶走前景，所以日常改動先用瀏覽器主機與 `FrontendMapping` 檢查，`Gui` 依 `AGENTS.md`「測試」的時機執行。

### 新 session 的讀取與啟動

1. 先讀 `AGENTS.md`、開發現況及其唯一現行計畫，核對實際工作目錄、branch、HEAD 與既有未提交變更。
   換用 Agent 不另建預設分支，不把另一個 worktree 當成目前未提交成果；同一批前端同時只由一個 Agent 寫入。
2. 讀本節、`docs/jet-frontend-description.md` 的目前設計、相關 action 與處理器，以及 `docs/jet-guide.md` 的相關業務規則。
   計畫保存需求原話和修改經過；現行前端說明保存已接受的結果。歷史截圖、舊 memory 或新設計包不覆蓋它們。
3. 在現行計畫記錄本批允許改動的路徑與開始時已有的變更。先保存本批相關檔案的差異或雜湊於 ignored
   `artifacts/frontend-preview/`，包含未追蹤的相關檔；結束時據此區分本批修改與上一輪成果，不拿 HEAD 的全部差異冒充本批。
4. 確認現有服務屬於這個工作目錄、`fixtures.json` 可用且來源檢查通過後才沿用；否則依上方正式命令重新產生並啟動。
   不硬用上一輪連接埠。關閉瀏覽器不代表服務已停止；一小時到期後需重啟，刷新會丟棄預覽草稿。
5. Codex 開啟終端顯示的網址。Claude Desktop 在 **Code、本機 Windows session** 選取本儲存庫根目錄，
   準備素材後由 Preview 啟動 `.claude/launch.json` 的 `JET synthetic frontend`，再導向 `/?scene=matches` 或 `/?scene=empty`。
   該設定使用 `127.0.0.1:4243`；若被占用，先辨認服務，不能終止不明程序。需要換埠時同步設定 `port`、`url` 與 `env.PORT`。

`JET synthetic frontend` 設定只執行 Node.js，不偷偷執行建置、完整驗證或產生合成資料。`JET browser host`
設定會由 `dotnet run` 建置主機和 JET 的 Debug 組建，但不執行驗證命令，也不產生這裡的預覽素材。未準備素材時先完成 Focused；
失敗就查看收據。一般啟動不設定 `PORT` 時仍由系統挑選空閒埠。Node.js 只綁定本機回送位址，不能改成對外服務
來讓雲端設計工具存取。Claude Design 畫布使用下節交接方式，不直接假設能讀取本機網址。

### 設計迭代與正式整合

| 時機 | Agent 要完成的工作 | 完成代表什麼 |
|:---|:---|:---|
| 每輪畫面調整 | 寫回正式 HTML、CSS 或呈現用 JS；走查多組、空組、單一選取、鍵盤焦點、窄版與有無結果，依改動執行相應 Focused。預覽工具變動跑 FrontendPreview 契約檢查，文件變動跑 Documentation。 | 設計可供使用者確認；不代表保存或後端篩選已驗證。 |
| 使用者確認版本並要求整合 | 複審本批差異，核對 action、payload、欄位綁定、預設值與啟用條件，再由正式入口依序跑 Build、Contract、Public、Package、Gui、Excel、Documentation。核對 Debug、Release 前端輸出與來源一致。 | 本次正式整合在實際測試範圍內通過；另列公司環境或其他未驗事項。 |
| 下次 session 或換 Agent | 把本批需求原話、裁定、改動範圍、驗證結果與下一步寫回同一計畫；長期有效的畫面行為更新前端說明，開發現況只連到目前計畫。 | 不依賴某個聊天室仍存在，也不把上一版本通過當成本版通過。 |

一般字體、間距和排版調整不反覆要求批准，也不為每次刷新跑完整桌面測試。互動改動先用瀏覽器主機與
`FrontendMapping` 確認，原生 GUI 留到使用者確認版本要整合時，執行前先告訴使用者會跳出視窗；
使用者確認一版後的整合仍完整執行上列命令。第一次失敗的證據保留，修正後重跑正式命令，不能放寬斷言。
`ReleaseCandidate` 要求乾淨的已提交來源，等 Git 交付另獲授權才執行；逐項正式檢查不等同候選快照通過。

設計模式預設只改呈現與操作；即使只改 JS，也要核對序列化和送出的資料，不能只用「沒有改 C#」證明安全。
`jet-api.js`、C# handler、Domain、資料庫、正式報表範本與審計運算不因設計模式而取得修改授權。
若必須改預設條件、母體、日期或金額意義、分類、同分錄或同傳票、AND 或 OR、版本失效或報告結果，
先說明使用者操作前後的差異，再另行裁定。不能靠隱藏提示、放寬下一步或偽造成功掩蓋契約不符。

目前第五步的已接受設計以 `docs/jet-frontend-description.md` 為準：只有一個加入目標和一個條件高亮，
左側總覽不嵌入組內，所有條件有名稱，保存情境是主要動作；分組依靠位置與邊界，不能再加多個競爭焦點。

### Claude Design 的交接

Claude Design 適合另試視覺方案。採用前先確認本機帳號可用；本專案沒有自動上傳、安裝 MCP 或雲端同步。
需要匯入時，只準備此次畫面相關的前端檔、現行設計要求、合成畫面與必要的操作語意摘錄，逐項列明內容後再交付。
不要上傳整個儲存庫、Git 歷史、真實案件、執行記錄或公司檔案；不能因功能有 `/design-sync` 就同步全部目錄。

設計包及下載結果先放 ignored `artifacts/frontend-preview/`。在目前計畫記下來源版本或雜湊、採用畫面與使用者裁定，
不要把下載檔當成第二份正式前端。交回 Codex 或 Claude Code 時，依原有 `wwwroot` 做最小修改；包內的 README
和聊天只作參考，不能授權新增框架、修改 action 或取代商業規則。交接後仍走上列設計確認與正式整合。
雲端畫布不能存取本機網址時，採用合成畫面與必要檔案交接；不架公開通道或把資料庫搬到設計工具。

### 可直接貼到新 session 的提示

```text
請在目前 je-tool 工作目錄開啟「前端設計模式」，依 .agents/harness/frontend-design-mode.md 執行。
先讀 AGENTS.md、開發現況、唯一現行計畫與前端說明，延續已接受的設計及過去修正，保留未提交變更。
確認合成素材有效，再開啟你目前支援的內建瀏覽器；Claude Desktop 請用 Code 的本機 Preview。
本輪先依我的畫面註解調整正式前端，維持 action、payload、審計規則與後端不變。
完成相應檢查並把裁定和下一步寫回現行計畫；等我確認版本並要求整合，再執行完整正式驗證。
不要暫存、提交、推送、上傳整個儲存庫或使用私人案件。
這次要調整的是：〔填入畫面或操作問題〕。
```

版本確認後可說：「這版前端驗收通過，請依前端設計模式做正式前後端整合與完整驗證，更新同一份驗收計畫；不做 Git 交付。」

檔案、需求和驗證沿用現有分工：

- 正式畫面只維護 `src/JET/JET/wwwroot/` 原檔，不建立手工同步的第二份前端。
- 預覽工具、合成測試與固定答案納入版本管理；生成的 `artifacts/frontend-preview/` 由忽略規則排除。
- 重要需求逐字附日期寫入目前大型計畫，另外寫實作解讀、結果和下一步；原始截圖不搬進現行文件。
- 視覺與操作調整核對文字、action、payload、後端契約與固定答案。日期邊界、金額基準、空白、借貸、
  同分錄或同傳票、AND 或 OR、版本失效及報告意義不得因版面簡化而改變。
- 真正的語意變更先列出差異請使用者裁定。一般視覺調整不反覆要求確認。
- 每批修改執行相應 Focused；真正 WebView2 GUI 依上方「使用者確認版本並要求整合」的時機執行，並先告知使用者。
  文件修改執行 Documentation。完整 GUI、封裝與必要回歸結果分別記錄，不能用預覽回放代替。SQL Server、私人資料與 Git 交付仍須另行授權。

工具檢查也由正式入口執行：

```powershell
pwsh -NoProfile -File tools/verify.ps1 -Command Contract -ContractScenario FrontendPreview
```

這項檢查確認固定請求回放、未知請求拒絕、正式 JetApi 串接和本機資源邊界。
Package 另拒絕把預覽目錄或 fixtures.json 帶入正式發布。


