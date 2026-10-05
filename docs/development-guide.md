# JET 開發入口

這份文件給要在這台電腦建置、執行或發佈 JET 的人：需要什麼環境、怎麼用 VS Code 或 Visual Studio 跑起來、
怎麼在瀏覽器裡預覽前端。驗證命令與結束碼在 [`../tools/README.md`](../tools/README.md)；
AI 編碼工具調整前端的工作方式在 [`../.agents/harness/frontend-design-mode.md`](../.agents/harness/frontend-design-mode.md)。

## 環境與入口

- Windows x64
- .NET SDK 10.0.201 以上；`global.json` 允許使用後續的 .NET 10 功能帶
- VS Code 搭配 Microsoft C# 擴充套件，或 Visual Studio 2026 18.0 以上
- WinForms 與 Microsoft Edge WebView2 執行階段
- Microsoft Excel 桌面版；只有執行 `Excel` 驗證路線時需要
- Node.js；只有開發用的前端預覽工具與部分契約檢查需要，公司端執行 JET 不需要
- 方案檔：`src/JET/JET.slnx`
- 應用程式：`src/JET/JET/JET.csproj`

先確認目前這個終端與建置工作實際取得哪一個 `dotnet`，再檢查該位置是否有 SDK：

```powershell
where.exe dotnet
Get-Command dotnet -All
dotnet --list-sdks
dotnet --version
```

電腦可能同時存在系統安裝、Visual Studio 管理或 VS Code C# Dev Kit 取得的 .NET。`global.json` 會在同一個 .NET 10
版本內選擇已安裝的較新功能帶，不要求每台電腦都裝同一個小版本。若某一份 `dotnet --list-sdks` 是空的，先比對 build
task 顯示的實際執行檔，不要直接判定整台電腦缺 SDK；只有實際執行 build 的那一份也完全空白時，才需要補裝 .NET 10 SDK。

## VS Code F5 與 Visual Studio 發佈

VS Code 應開啟儲存庫根目錄，而不是只開啟 `src/JET/`。選擇 `JET (Debug)` 後按 F5，啟動前會直接執行 `dotnet build`，
再以 `JET.dll` 啟動偵錯；這條日常入口不需要 PowerShell 7。`verify: Build (Debug)` 工作保留給需要驗證收據的情況。
命令面板中的 `Tasks: Run Task` 另有 `publish`，會以既有 `FolderProfile` 產生與 Visual Studio 相同的 Release x64
自含式單檔封裝。

Visual Studio 請開啟 `src/JET/JET.slnx`，將 `JET` 設為啟始專案。建置與 F5 通過後，在方案總管對 `JET` 按右鍵選「發佈」，
選擇既有的 `FolderProfile` 再按「發佈」。設定檔位於 `src/JET/JET/Properties/PublishProfiles/FolderProfile.pubxml`，
輸出位於 `src/JET/JET/bin/Release/net10.0-windows/publish/win-x64/`。這是資料夾發佈，不是 ClickOnce；結果是 Windows x64
自含式，主程式為單一 `JET.exe`，同一資料夾保留必要的設定、Excel 範本與前端資產。VS Code 能 F5 不代表 Visual Studio
已安裝自己的 SDK；如果 Visual Studio 顯示 `NETSDK1141`，在 Visual Studio Installer 確認已安裝「.NET 桌面開發」workload
與 .NET 10 個別元件。

直接啟動桌面程式：

```powershell
dotnet run --project src/JET/JET/JET.csproj -c Debug
```

## 驗證

所有正式驗證從 `tools/verify.ps1` 進入，日常改動用 `Focused`，整批改動用 `Public`，改過文件跑 `Documentation`。
命令清單、參數、結束碼與較重的路線（`Package`、`Gui`、`Excel`、`Provider`、`PrivateCase`、`ReleaseCandidate`）
都在 [`../tools/README.md`](../tools/README.md)，這裡不重複。每次回報只能涵蓋實際執行的命令；舊專案的建置、測試或
資料庫結果不能當成 `je-tool` 的驗證結果。

本機設定、秘密、連線字串、案件資料夾、建置輸出與 WebView2 使用者資料都不得加入原始碼樹。

## 開發用的預覽工具

兩個工具都放在 `tools/harness/` 下，是開發時看畫面用的，不屬於正式 JET，也不進封裝。

**合成預覽**（`tools/harness/frontend-preview/`）只回放第五步進階條件篩選的固定答案，適合調整版面與文字。
先用正式測試產生素材，再啟動 Node.js 服務；終端會顯示本次專用的 `127.0.0.1` 網址。按 Ctrl+C 停止，
未手動停止時一小時後結束。

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

**瀏覽器主機**（`tools/harness/browser-host/`）讓正式前端在一般瀏覽器裡執行六個步驟。畫面直接讀 `src/JET/JET/wwwroot/`
原檔，前端送出的 action 交給正式的 `ActionDispatcher`，資料庫、審計運算與報表都是正式程式。服務只綁定 `127.0.0.1`，
不連 SQL Server；案件、診斷日誌與使用者快取放在 Git 忽略的 `artifacts/browser-host/`。案件目錄是空的時，主機會建立
兩個合成案件：「合成示範-篩選就緒」停在第五步，「合成示範-待配對」停在第三步。

```powershell
dotnet run --project tools/harness/browser-host/BrowserHost.csproj -c Debug --no-launch-profile
```

加上 `-- --reset` 會清掉舊的合成案件後重建。改 CSS 或 JS 後重新整理頁面即可；改 C# 後要停止並重新啟動。
Claude Desktop 的 Code 可由 Preview 啟動 `.claude/launch.json` 裡的 `JET browser host`，網址是 `http://127.0.0.1:4244`。
需要中斷點時，用 VS Code 的「JET (attach)」附加到 `Jet.BrowserHost` 程序。

瀏覽器主機不能取代真正的 WebView2 驗證。關窗流程、WebView2 本身的行為與桌面視窗尺寸，仍要用 `verify.ps1 -Command Gui`
確認。
