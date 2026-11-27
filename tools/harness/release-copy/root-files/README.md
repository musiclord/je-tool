# JET

JET 是 Windows 桌面審計工具，使用 C#、.NET 10、WinForms 與 WebView2，案件資料保存在 SQLite。前端畫面、前端元件的授權檔與 Excel 範本隨程式發布。

| 路徑 | 內容 |
|---|---|
| `JET/JET.slnx` | Visual Studio 方案。 |
| `JET/JET/` | 主程式、前端資產、範本與發布設定。 |
| `JET/tests/JET.Tests/` | C# 測試、合成資料與固定答案。 |
| `JET/tests/frontend/` | Node.js 前端測試。 |
| `docs/SAT/` | 送審文件。 |
| `docs/architecture/` | SAT 對應的系統架構圖、技術設計圖表索引與資料流程。 |
| `docs/security/` | 元件清冊、安全檢查摘要與佐證索引。 |
| `data/` | 參考工作簿。 |
| `artifacts/` | 測試結果與 ZIP 封裝的輸出位置。 |

開發電腦需要 Windows x64、.NET 10 SDK 與 Microsoft Edge WebView2 執行階段。SDK 版本由 `global.json` 選擇，最低為 10.0.201，允許後續 .NET 10 功能帶。前端測試使用 Node.js，ZIP 封裝使用 PowerShell 7.4 以上版本。

以下命令都在儲存庫根目錄執行。

## 建置與執行

```powershell
dotnet build JET/JET.slnx --configuration Debug
dotnet run --project JET/JET/JET.csproj --configuration Debug
```

一般執行時，案件放在 `%USERPROFILE%\JET`。需要指定其他本機位置時，可在啟動前設定環境變數 `JET_PROJECTS_ROOT`。

## 偵錯

Visual Studio 開啟 `JET/JET.slnx`，將 `JET` 設為啟始專案，選擇 Debug 後按 F5。

VS Code 開啟儲存庫根目錄，安裝工作區建議的 C# 與 C# Dev Kit 擴充套件，選擇 `JET (Debug)` 後按 F5。偵錯設定會先建置主程式，再載入 Debug 組件與符號。`JET (attach)` 可附加到已執行的程序。

## 測試

先建置方案，再執行 C# 測試與前端測試。

```powershell
dotnet build JET/JET.slnx --configuration Debug
.\JET\tests\JET.Tests\bin\Debug\net10.0-windows\JET.Tests.exe --filter-not-trait "TestProfile=Scale" --results-directory "$PWD/artifacts/tests"
node --test --test-concurrency=4 JET/tests/frontend/
```

一般測試排除 `Scale` 標記。該類測試用大量合成資料量測完整流程，需要另行啟用。VS Code 的 `test backend` 與 `test frontend` 工作可執行上述兩組測試。

## 發布與封裝

```powershell
dotnet publish JET/JET/JET.csproj --configuration Release -p:PublishProfile=FolderProfile
```

發布輸出位於 `JET/JET/bin/Release/net10.0-windows/publish/win-x64/`，包含自含 .NET 執行階段的 `JET.exe`、`Templates/` 與 `wwwroot/`。將整個發布目錄交付，執行電腦需具備 Microsoft Edge WebView2 執行階段。

Visual Studio 可在 JET 專案的發布頁選取 `FolderProfile`，VS Code 可執行 `publish` 工作。兩者使用同一份發布設定。

產生 ZIP 封裝時執行下列命令。

```powershell
pwsh -NoProfile -File JET/Package.ps1
```

腳本會發布到新建的暫存目錄，檢查主程式、首頁與範本後壓縮，輸出 `artifacts/packages/JET-win-x64.zip`。再次執行會更新同名 ZIP，可用 `-OutputDirectory` 指定其他輸出資料夾。VS Code 的 `package` 工作會執行同一個腳本。

## 範本與送審文件

`JET/JET/Templates/` 的八份 Excel 範本隨程式發布。`data/` 的十份工作簿是業務範例，供對照參考；其中 `INFReport.xlsx` 與 `WorkingPaper.xlsx` 的程式隨附版本另做過安全清理，內容和 `data/` 的版本不同。

送審文件與佐證的對應關係見 [SAT 文件入口](docs/README.md)，技術文件中的方案內命令以 `JET/` 為工作目錄。
