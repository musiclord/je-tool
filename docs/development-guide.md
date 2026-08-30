# JET 開發入口

更新日期：2026-08-30

`je-tool` 已完成驗證框架的 Phase 1 至 Phase 7，所有正式檢查都從 `tools/verify.ps1` 進入。`Provider`、
Release `Package`、兩個 GUI 情境與六份合成報表的原生 Excel 開啟與儲存檢查都有完整通過紀錄。完整分工見
[`harness.md`](harness.md)，建置順序見
[`specs/2026-08-28-harness-rebuild-plan.md`](specs/2026-08-28-harness-rebuild-plan.md)。

## 環境與入口

- Windows x64
- .NET SDK 10.0.400；`global.json` 只允許同一功能帶內較新的修補版本
- WinForms 與 Microsoft Edge WebView2 執行階段
- Microsoft Excel 桌面版；只有執行 `Excel` 路線時需要
- 方案檔：`src/JET/JET.slnx`
- 應用程式：`src/JET/JET/JET.csproj`

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

`Gui` 會分別執行啟動檢查與合成 SQLite 建案。第二個情境會從真實 JET 畫面輸入固定的合成資料，確認建案後
進入匯入步驟，而且本次暫存目錄中確實產生 `project.json` 與 `jet.db`。這兩個情境不使用私人案件，也不代表
JE／TB 匯入、篩選或底稿匯出已完成驗收。

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
