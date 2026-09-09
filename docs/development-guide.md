# JET 開發入口

更新日期：2026-09-09

## 前端設計模式

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

頁面上方可切換日期與金額的「有結果」和「無結果」，並查看最近 50 個 action 請求。
有結果時固定命中兩筆分錄、一張傳票，可展開明細。這是經固定答案核對的回放，不是瀏覽器內的審計引擎。
改成未準備的條件、排序、搜尋、保存、匯出或其他 action 時，工具不會假裝成功。
畫面本身的條件編輯、展開與焦點仍由正式 JS 處理；未知結果不能作為業務判斷。

使用 Annotation 選取元件或區域後留言；若目前 Codex 版本提供 Adjust，可先試字體、間距與顏色。
這些操作仍需由 Agent 寫回正式 CSS 或 JS。工具不自行修改原始碼，也不把瀏覽器暫時調整當成已保存。
本次註解協作與前端調整已於 2026-09-09 通過使用者驗收；Adjust 尚未實際試用，不是啟動模式的必要條件。
若目前工具沒有畫面註解，使用者可在聊天指出畫面元件，Agent 自行查看 DOM 和畫面，不要求重做整份截圖說明。

### 新 session 的讀取與啟動

1. 先讀 `AGENTS.md`、開發現況及其唯一現行計畫，核對實際工作目錄、branch、HEAD 與既有未提交變更。
   換用 Agent 不另建預設分支，不把另一個 worktree 當成目前未提交成果；同一批前端同時只由一個 Agent 寫入。
2. 讀本節、`jet-frontend-description.md` 的目前設計、相關 action 與處理器，以及 `jet-guide.md` 的相關業務規則。
   計畫保存需求原話和修改經過；現行前端說明保存已接受的結果。歷史截圖、舊 memory 或新設計包不覆蓋它們。
3. 在現行計畫記錄本批允許改動的路徑與開始時已有的變更。先保存本批相關檔案的差異或雜湊於 ignored
   `artifacts/frontend-preview/`，包含未追蹤的相關檔；結束時據此區分本批修改與上一輪成果，不拿 HEAD 的全部差異冒充本批。
4. 確認現有服務屬於這個工作目錄、`fixtures.json` 可用且來源檢查通過後才沿用；否則依上方正式命令重新產生並啟動。
   不硬用上一輪連接埠。關閉瀏覽器不代表服務已停止；一小時到期後需重啟，刷新會丟棄預覽草稿。
5. Codex 開啟終端顯示的網址。Claude Desktop 在 **Code、本機 Windows session** 選取本儲存庫根目錄，
   準備素材後由 Preview 啟動 `.claude/launch.json` 的 `JET synthetic frontend`，再導向 `/?scene=matches` 或 `/?scene=empty`。
   該設定使用 `127.0.0.1:4243`；若被占用，先辨認服務，不能終止不明程序。需要換埠時同步設定 `port`、`url` 與 `env.PORT`。

Claude 啟動設定只執行 Node.js，不偷偷執行建置、完整驗證或產生合成資料。未準備素材時先完成 Focused；
失敗就查看收據。一般啟動不設定 `PORT` 時仍由系統挑選空閒埠。Node.js 只綁定本機回送位址，不能改成對外服務
來讓雲端設計工具存取。Claude Design 畫布使用下節交接方式，不直接假設能讀取本機網址。

### 設計迭代與正式整合

| 時機 | Agent 要完成的工作 | 完成代表什麼 |
|:---|:---|:---|
| 每輪畫面調整 | 寫回正式 HTML、CSS 或呈現用 JS；走查多組、空組、單一選取、鍵盤焦點、窄版與有無結果，依改動執行相應 Focused。預覽工具變動跑 FrontendPreview 契約檢查，文件變動跑 Documentation。 | 設計可供使用者確認；不代表保存或後端篩選已驗證。 |
| 使用者確認版本並要求整合 | 複審本批差異，核對 action、payload、欄位綁定、預設值與啟用條件，再由正式入口依序跑 Build、Contract、Public、Package、Gui、Excel、Documentation。核對 Debug、Release 前端輸出與來源一致。 | 本次正式整合在實際測試範圍內通過；另列公司環境或其他未驗事項。 |
| 下次 session 或換 Agent | 把本批需求原話、裁定、改動範圍、驗證結果與下一步寫回同一計畫；長期有效的畫面行為更新前端說明，開發現況只連到目前計畫。 | 不依賴某個聊天室仍存在，也不把上一版本通過當成本版通過。 |

一般字體、間距和排版調整不反覆要求批准，也不為每次刷新跑完整桌面測試。互動改動依影響提早做原生 GUI；
使用者確認一版後的整合仍完整執行上列命令。第一次失敗的證據保留，修正後重跑正式命令，不能放寬斷言。
`ReleaseCandidate` 要求乾淨的已提交來源，等 Git 交付另獲授權才執行；逐項正式檢查不等同候選快照通過。

設計模式預設只改呈現與操作；即使只改 JS，也要核對序列化和送出的資料，不能只用「沒有改 C#」證明安全。
`jet-api.js`、C# handler、Domain、資料庫、正式報表範本與審計運算不因設計模式而取得修改授權。
若必須改預設條件、母體、日期或金額意義、分類、同分錄或同傳票、AND 或 OR、版本失效或報告結果，
先說明使用者操作前後的差異，再另行裁定。不能靠隱藏提示、放寬下一步或偽造成功掩蓋契約不符。

目前第五步的已接受設計以 `jet-frontend-description.md` 為準：只有一個加入目標和一個條件高亮，
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
請在目前 je-tool 工作目錄開啟「前端設計模式」，依 docs/development-guide.md 執行。
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
- 每批修改依上方兩個時機執行相應 Focused 與真正 WebView2 GUI；文件修改執行 Documentation。
  完整 GUI、封裝與必要回歸結果分別記錄，不能用預覽回放代替。SQL Server、私人資料與 Git 交付仍須另行授權。

工具檢查也由正式入口執行：

```powershell
pwsh -NoProfile -File tools/verify.ps1 -Command Contract -ContractScenario FrontendPreview
```

這項檢查確認固定請求回放、未知請求拒絕、正式 JetApi 串接和本機資源邊界。
Package 另拒絕把預覽目錄或 fixtures.json 帶入正式發布。

`je-tool` 已完成驗證框架的 Phase 1 至 Phase 7，所有正式檢查都從 `tools/verify.ps1` 進入。`Provider`、
Release `Package`、四個 GUI 情境與六份合成報表的原生 Excel 開啟與儲存檢查都有完整通過紀錄。完整分工見
[`harness.md`](harness.md)，建置順序見
[`specs/2026-08-28-harness-rebuild-plan.md`](specs/2026-08-28-harness-rebuild-plan.md)。

## 環境與入口

- Windows x64
- .NET SDK 10.0.201 以上；`global.json` 允許使用後續的 .NET 10 功能帶
- VS Code 搭配 Microsoft C# 擴充套件，或 Visual Studio 2026 18.0 以上
- WinForms 與 Microsoft Edge WebView2 執行階段
- Microsoft Excel 桌面版；只有執行 `Excel` 路線時需要
- 方案檔：`src/JET/JET.slnx`
- 應用程式：`src/JET/JET/JET.csproj`

先確認目前這個終端與建置工作實際取得哪一個 `dotnet`，再檢查該位置是否有 SDK：

```powershell
where.exe dotnet
Get-Command dotnet -All
dotnet --list-sdks
dotnet --version
```

`new-je-tool` 與目前 `je-tool` 都要求 `10.0.201` 並使用 `latestFeature`；在同一個 VS Code 終端內，兩個
儲存庫的 `dotnet --version` 應得到相同結果。`global.json` 會在同一個 .NET 10 版本內選擇已安裝的較新
功能帶，因此不再要求每台電腦都安裝 `10.0.400`。

電腦可能同時存在系統安裝、Visual Studio 管理或 VS Code C# Dev Kit 取得的 .NET。若
`C:\Program Files\dotnet\dotnet.exe --list-sdks` 是空的，但 `new-je-tool` 在同一個 VS Code 視窗仍可 F5，
先比對兩邊 build task 顯示的實際 executable，不要直接判定整台電腦缺 SDK。只有實際執行 build 的那一份
`dotnet --list-sdks` 也完全空白時，才需要為 VS Code 補裝 .NET 10 SDK。

## VS Code F5 與 Visual Studio 發佈

VS Code 應開啟儲存庫根目錄，而不是只開啟 `src/JET/`。選擇 `JET (Debug)` 後按 F5，`preLaunchTask`
會直接執行 `dotnet build`，再以 `JET.dll` 啟動偵錯；這條日常入口不需要 PowerShell 7。
`verify: Build (Debug)` 仍保留給需要驗證收據的情況，不再阻擋 F5。命令面板中的 `Tasks: Run Task` 另有
`publish`，會以既有 `FolderProfile` 產生與 Visual Studio 相同的 Release x64 自含式單檔封裝。

Visual Studio 請開啟 `src/JET/JET.slnx`，將 `JET` 設為啟始專案。建置與 F5 通過後，在方案總管對 `JET`
按右鍵選「發佈」，選擇既有的 `FolderProfile` 再按「發佈」。設定檔位於
`src/JET/JET/Properties/PublishProfiles/FolderProfile.pubxml`，輸出位於
`src/JET/JET/bin/Release/net10.0-windows/publish/win-x64/`。這是資料夾發佈，不是 ClickOnce；結果是
Windows x64、自含式，主程式為單一 `JET.exe`，並在同一資料夾保留必要的設定、Excel 範本與前端資產。
VS Code 能 F5 不代表 Visual Studio 已安裝自己的 SDK；如果 Visual Studio 仍顯示 `NETSDK1141`，應在
Visual Studio Installer 確認已安裝「.NET 桌面開發」workload 與 .NET 10 個別元件。

## 目前可用的共用命令

請在儲存庫根目錄執行：

```powershell
pwsh -NoProfile -File tools/verify.ps1 -Command Help
pwsh -NoProfile -File tools/verify.ps1 -Command Contract
pwsh -NoProfile -File tools/verify.ps1 -Command Documentation
pwsh -NoProfile -File tools/verify.ps1 -Command Restore
pwsh -NoProfile -File tools/verify.ps1 -Command Build -Configuration Debug
pwsh -NoProfile -File tools/verify.ps1 -Command Focused -Filter 'JET.Tests.Domain.MappingValidatorTests'
pwsh -NoProfile -File tools/verify.ps1 -Command Public
pwsh -NoProfile -File tools/verify.ps1 -Command Provider -Configuration Release
pwsh -NoProfile -File tools/verify.ps1 -Command Package -Configuration Release
pwsh -NoProfile -File tools/verify.ps1 -Command Gui -Configuration AgentGuiTest
pwsh -NoProfile -File tools/verify.ps1 -Command Excel -Configuration Release
pwsh -NoProfile -File tools/verify.ps1 -Command ReleaseCandidate -Configuration Release
```

`Contract` 只檢查驗證框架本身，不會執行產品測試。`Build` 預設先還原套件，再進行建置；只有在確定套件資產
已存在時才加上 `-NoRestore`。`Focused` 適合日常改動：它會執行名稱符合 `-Filter` 的測試，再補跑架構檢查。
`Public` 會跑完所有安全且不需外部服務的公開測試，不接受篩選條件。詳細參數與結束碼見
[`../tools/README.md`](../tools/README.md)。

修改現行文件後，執行 `Documentation`，再把改動的段落讀一次。這個命令只抓已知的壞用語，不能取代人工
確認句子是否自然、資訊是否有依據。

`Provider` 只接受程序環境中的 `JET_SQLSERVER_CONNECTION`，並會在專用的 `JET_Test` 資料庫建立及清除
測試資料；沒有明確準備好這個測試環境時不要執行。`ReleaseCandidate` 不要求 live SQL Server，也不能用來
宣稱 SQL Server 實跑已通過。`Package` 會先拒絕含憑證的
可追蹤設定，通過後才會建置、執行輸出檔案測試，並在本次專屬暫存目錄產生 Release 封裝。`Provider`、
`Package` 與完整 `ReleaseCandidate` 都已有通過紀錄。

`Gui` 會執行啟動、合成 SQLite 建案、欄位配對必填同步及損壞 journal 復原四個情境。它會從真實 JET
畫面輸入建案資料、操作 mapping 下拉、從 picker 匯出去識別支援日誌並確認刪案；每個情境都使用自己的
合成暫存根。這些情境不使用私人案件，也不代表篩選、底稿匯出或原生檔案對話框已完成驗收。

`Excel` 會先由現行產品測試產生六份合成報表，再逐份交給原生 Excel 開啟、完整重算、另存、重新開啟並
輸出第一頁 PDF。來源檔不能被改動，副本不能帶有外部連結，公式錯誤也不能增加。這條路線只接受本次暫存
目錄中的固定報表，不會讀取 `data/test-case/`；它證明的是合成報表能由原生 Excel 開啟、儲存並重新開啟，
不是私人案件的 JE／TB 全流程或正確底稿一致性。

如果要直接啟動桌面程式，可另外使用：

```powershell
dotnet run --project src/JET/JET/JET.csproj -c Debug
```

每次回報只能涵蓋實際執行的命令。舊專案的建置、測試、GUI、Excel 或資料庫結果，不能當成 `je-tool` 的
驗證結果。

## 測試邊界

- 儲存庫內已移除會要求特定真實案件、舊環境變數、舊工具或舊執行產物存在的測試入口。
- GL、TB、PBC、不同資料庫實作及結果比較等產品能力沒有刪除。保留下來的測試使用合成或去識別化資料，
  並繼續檢查路徑安全與敏感資料外洩風險。
- `Focused` 與 `Public` 已接入現行 xUnit v3／Microsoft Testing Platform 測試專案。兩者會排除 Provider
  與 Scale；Provider 只能由自己的完整路線執行，不能用公開測試的結果代替。先前會寫入舊工作目錄的
  Legacy 測試已改用各自的系統暫存目錄，目前沒有再用類別名稱把它們排除在公開測試之外。
- 正式結果一律以 `tools/verify.ps1` 的收據為準。直接執行 `dotnet test` 或測試程式只能用於診斷，不能取代
  `Focused` 或 `Public`。
- 舊專案的測試結果、建置輸出、執行產物和私人案件資料夾，不得成為自動搜尋範圍、判定基準或預設輸入。
  新驗證框架只接受明確指定且已獲授權的私人資料；來源專案或舊輸出不存在時，也不應影響一般測試。

## 目前收尾

1. Phase 6 已完成。情境 1 只有在單獨使用「未預期借貸組合」時，Criteria 摘要與 Working Paper 標記沿用
   舊 IDEA 的整張傳票口徑；情境 5 要求三個條件在同一列成立，情境 6 的週末規則納入補班日。同一私人
   案件已用 SQLite 與 SQL Server 通過。
2. Phase 7 已完成。`ReleaseCandidate` 已在一次性候選快照中依序通過全部公開必要檢查；提交前仍要核對
   候選清單與實際未忽略檔案完全一致，而且候選內容每次變動後都要取得新的完整收據。
3. `PrivateCase` 是明示授權的本機命令，不屬於 `ReleaseCandidate` 或公開 CI；需要新的私人案件結論時才
   重新授權執行。
4. 第一次根提交不複製舊專案 CI。新的 CI 等根提交與遠端讀回完成後，再從適合遠端環境的公開命令設計。
5. 變異測試、壓力測試與涵蓋率只有在能回答獨立問題時才加入，不因舊設定曾經存在就恢復。

本機設定、秘密、連線字串、案件資料夾、建置輸出與 WebView2 使用者資料都不得加入原始碼樹。
