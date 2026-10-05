# JET

JET（Journal Entry Testing）是一套 Windows 桌面審計工具，用來執行傳票測試：把總帳與試算表匯入本機
案件、配對欄位、建立有效母體並驗證完整性、依風險條件篩選傳票，最後輸出六份審計底稿。它取代原本在
CaseWare IDEA 中進行、依賴少數熟悉 IDEA 或 SQL 的人才能完成的工作，讓查核人員在一般公司電腦上就能
完成整個流程。專案背景與環境限制見 [`docs/project-context.md`](docs/project-context.md)。

## 主要功能

- 六步驟工作流程，依序是建立案件、匯入 GL／TB 與支援資料、欄位配對、資料驗證、條件篩選、匯出底稿
- 完整性測試、控制總數核對、空值測試，以及期後核准、非營業日、非授權編製者等預篩選風險訊號
- 進階條件篩選：以 AND／OR 層級組合金額、日期、文字與分錄性質條件，可保存為情境重複使用
- 六份正式報表（驗證、科目配對、INF 抽樣、預篩選、條件篩選、工作底稿）直接填入 Excel 範本
- 案件以自含資料夾保存，正常關閉後可整個搬到別的磁碟或電腦繼續作業

## 技術與定位

單一執行檔的 .NET 10 WinForms 應用程式，畫面由 WebView2 承載網頁前端。分層採
Thin-Bridge Action-Dispatcher：前端與 C# 之間只走 action 通道，Application 層以 CQRS 處理流程，
審計規則集中在 Domain 與 AuditCore，資料庫實作隔離在 Infrastructure。

大量資料不進入前端或應用層記憶體：篩選、彙總與連接都以參數化、集合式 SQL 在資料庫內完成，前端只
接收摘要與有界分頁。本機案件使用 SQLite 或 DuckDB（免伺服器、免系統管理權限），SQL Server 保留為
線上實作；三種資料庫在相同輸入下必須得到相同業務結果。

各層的職責與資料流見 [`docs/jet-guide.md`](docs/jet-guide.md) 第 9 節；2026-08-30 畫的架構圖保留在
[`docs/history/architecture-2026-08-30/`](docs/history/architecture-2026-08-30/README.md)，之後程式已大改，只供參考。

## 系統需求

| 項目 | 說明 |
|:---|:---|
| 作業系統 | Windows x64 |
| .NET SDK | 10.0.201 以上的 .NET 10 SDK（[`global.json`](global.json) 允許後續 .NET 10 功能帶） |
| 開發工具 | VS Code 搭配 Microsoft C# 擴充套件，或 Visual Studio 2026 18.0 以上 |
| PowerShell | 7.4 以上；只有正式驗證入口需要，VS Code F5 與發佈不依賴它 |
| WebView2 | Microsoft Edge WebView2 執行階段 |
| Microsoft Excel | 選用，只有 `Excel` 驗證路線需要 |
| SQL Server | 選用，只有 `Provider` 驗證路線需要，且必須是專用測試環境 |

## 快速開始

```powershell
git clone https://github.com/musiclord/je-tool.git
cd je-tool

pwsh -NoProfile -File tools/verify.ps1 -Command Restore
pwsh -NoProfile -File tools/verify.ps1 -Command Build -Configuration Debug

dotnet run --project src/JET/JET/JET.csproj -c Debug
```

方案檔是 [`src/JET/JET.slnx`](src/JET/JET.slnx)，應用程式專案是 `src/JET/JET/JET.csproj`。

在 VS Code 開啟儲存庫根目錄，選擇 `JET (Debug)` 後按 F5；啟動前會直接以 `dotnet build` 建置應用程式。
在 Visual Studio 開啟方案檔後，將 `JET` 設為啟始專案即可建置及偵錯。要產生公司測試用的 x64 單檔版本，
在 `JET` 專案的「發佈」頁選擇既有的 `FolderProfile`；也可在 VS Code 執行 `publish` 工作。輸出位於
`src/JET/JET/bin/Release/net10.0-windows/publish/win-x64/`，主程式為單一 `JET.exe`，同一資料夾另保留
執行時需要的設定、Excel 範本與前端資產。

## 測試

所有正式驗證從 `tools/verify.ps1` 進入，它負責共享鎖、執行收據、輸出遮蔽與清理驗證：

```powershell
# 日常改動：跑指定測試並補跑架構檢查
pwsh -NoProfile -File tools/verify.ps1 -Command Focused -Filter 'JET.Tests.Domain.MappingValidatorTests'

# 完整公開測試（不需外部服務）
pwsh -NoProfile -File tools/verify.ps1 -Command Public

# 列出全部命令
pwsh -NoProfile -File tools/verify.ps1 -Command Help
```

結束碼：`0` 通過、`1` 失敗、`2` 受阻（尚無正誤結論，不能當成通過）、`3` 用法錯誤、`4` 框架本身錯誤。
另有 `Provider`、`Package`、`Gui`、`Excel`、`PrivateCase`、`ReleaseCandidate` 六條較重的路線，見
[`tools/README.md`](tools/README.md)。

## 專案結構

| 目錄 | 內容 |
|:---|:---|
| [`src/`](src/) | 產品程式與測試 |
| [`tools/`](tools/) | 驗證框架 |
| [`docs/`](docs/README.md) | 現行說明、技術參考與歷史文件 |
| [`data/`](data/) | 十份參考工作簿，檔名是儲存庫內工作簿命名的依據 |
| [`legacy/`](legacy/README.md) | IDEA、VBA 與早期 .NET 的對照來源，不是現行程式樹 |
| `artifacts/` | 本機執行證據，由 Git 忽略 |

## 文件

第一次接手依序閱讀：

1. [`docs/project-context.md`](docs/project-context.md) — 為什麼存在、先服務什麼、環境有何限制
2. [`docs/jet-guide.md`](docs/jet-guide.md) — 系統是什麼、資料怎麼流動
3. [`docs/development-status.md`](docs/development-status.md) — 現在做到哪裡、有什麼延後事項
4. [`docs/development-guide.md`](docs/development-guide.md) — 開發環境與可用入口

完整導覽見 [`docs/README.md`](docs/README.md)；設計脈絡與已被取代的舊版本在
[`docs/history/`](docs/history/README.md)。
