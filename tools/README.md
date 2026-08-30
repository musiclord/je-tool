# JET 驗證框架使用說明

`tools/verify.ps1` 是儲存庫唯一的公開驗證入口。Phase 1 至 Phase 7 已完成；`Provider`、`Package`、兩個 GUI
情境、六份合成報表的原生 Excel 檢查、SQLite／SQL Server 的同一私人案件，以及完整 `ReleaseCandidate`
都有通過紀錄。

## 名稱與目錄分工

- `tools/` 第一層只分成 `harness/` 與 `tests/`。新增的驗證框架子目錄一律使用小寫英文，單字之間以連字號分隔。
- JSON 設定檔、公開入口、測試與一次性 probe 使用小寫與連字號。可重用的 PowerShell 模組與內部 verifier
  使用 `Jet` 開頭的 PascalCase；C# 專案、原始碼檔、型別、namespace 和組件名稱也依 .NET 慣例使用
  PascalCase。這些都是檔案或程式識別名稱，不會成為 `tools/` 的新分類。
- C# 驅動程式仍屬於驗證框架，所以專案目錄要放在 `tools/harness/` 下，不能在 `tools/` 增加第三個分類。
- `bin/`、`obj/`、`artifacts/` 和 `TestResults/` 都是本機產物，不屬於第一次提交內容。

`new-je-tool/tools/` 曾使用 `AgentGuiDriver/`、`ExcelAcceptanceDriver/` 這類 PascalCase 專案目錄。新框架沒有複製
那些目錄；Phase 4 初版卻沿用了它們的目錄慣例，才一度出現 `tools/GuiSmokeDriver/`。目前已改成
`tools/harness/gui-driver/`。這個名稱反映共用 GUI 驅動程式的責任，不會再把單一情境名稱當成元件名稱；
契約測試也會拒絕舊目錄回來。

## 目前可用的命令

```powershell
pwsh -NoProfile -File tools/verify.ps1 -Command Help
pwsh -NoProfile -File tools/verify.ps1 -Command Contract
pwsh -NoProfile -File tools/verify.ps1 -Command Documentation
pwsh -NoProfile -File tools/verify.ps1 -Command Restore
pwsh -NoProfile -File tools/verify.ps1 -Command Build -Configuration Debug -NoRestore
pwsh -NoProfile -File tools/verify.ps1 -Command Focused -Filter 'JET.Tests.Domain.MappingValidatorTests'
pwsh -NoProfile -File tools/verify.ps1 -Command Public
pwsh -NoProfile -File tools/verify.ps1 -Command Provider -Configuration Release
pwsh -NoProfile -File tools/verify.ps1 -Command Package -Configuration Release
pwsh -NoProfile -File tools/verify.ps1 -Command Gui -Configuration AgentGuiTest
pwsh -NoProfile -File tools/verify.ps1 -Command Excel -Configuration Release
$env:JET_PRIVATE_CASE_ROOT = '<完整授權根目錄>'
$env:JET_PRIVATE_CASE_MANIFEST = '.harness/private-case.json'
$env:JET_PRIVATE_CASE_PROVIDER = 'sqlite'
pwsh -NoProfile -File tools/verify.ps1 -Command PrivateCase -Configuration Release
pwsh -NoProfile -File tools/verify.ps1 -Command ReleaseCandidate -Configuration Release
```

`Build` 預設先執行 Restore，再以 `--no-restore` 進行建置。只有確定 NuGet 還原資產已存在時，才使用
`-NoRestore`。

`Focused` 的 `-Filter` 接受測試方法或類別的完整名稱片段，不接受萬用字元。它會先建置，再執行選取的測試，
並固定補跑架構檢查；選不到測試或遇到跳過都不會算通過。`Public` 不接受 `-Filter`，會依固定種子、單一執行緒
跑完目前安全且不需外部服務的公開測試。這組測試至少要找到 3,000 個案例。只有明列在
`public-skip-policy.json`、且需要真實檔案系統連結的那組測試，可以因本機不支援而全組略過；成員、原因或
數量有差異時仍會失敗。

`Provider` 不讀 `appsettings.json` 的 SQL 密碼，只接受程序環境中的 `JET_SQLSERVER_CONNECTION`。缺少時回報
`blocked`，連建置都不會開始。正式執行會使用 `JET_Test`，建立及清除測試資料結構與資料，因此只能連到
明確準備的測試環境。測試用專案若清理失敗，該次測試也會失敗。連線值不會寫進命令或收據，stdout、
stderr 與 TRX 也會在保存前遮蔽。

目前 SQL Server 中心化方向暫緩，因此 `Provider` 是明示執行的 live 相容性驗證，不是一般開發或
`ReleaseCandidate` 的先決條件。連線只在確定要驗證的本機程序中暫時提供；不得寫入儲存庫、設定範例、
命令列、聊天或長期保存的收據。

`Package` 固定使用 Release。它先檢查可追蹤設定沒有憑證，再執行建置、70 項以上的範本與報告測試，最後在
本次專屬暫存目錄使用 FolderProfile 發布。驗證會核對 x64 單檔執行檔、八份範本、`wwwroot`、安全設定及
禁止出現的執行期資料；清單以本次現行來源產生，不沿用舊 P5 基準。暫存封裝完成後會自動移除，只保留清單
與有限大小的紀錄。實際 publish 會占用較多 CPU 與磁碟，執行前應先確認本機負載。

`Gui` 固定使用 `AgentGuiTest` 組態。它會先建置一次，再分別用全新的暫存目錄執行兩個情境：

- `startup-smoke` 檢查頁面、`JetApi`、`systemPing`、專案選擇畫面及離開按鈕，最後按下「儲存並結束」。
- `synthetic-sqlite-create` 從可見畫面點選新增專案，以鍵盤事件輸入六個固定的合成欄位，沿用畫面預設的
  SQLite，建立專案並進入匯入步驟。通過前還會核對本次暫存根目錄中的 `project.json` 與 `jet.db`。

驅動程式位於 `tools/harness/gui-driver/`。外部只能選擇上述固定情境，不能傳入 JavaScript、selector 或任意
動作；程式也不使用 Selenium、EdgeDriver 或網路下載。每個情境都必須由 JET 自行結束，並完成程序與暫存
目錄清理。這兩項結果不代表 JE／TB 匯入、原生對話框或私人案件已完成驗收。

`Excel` 固定使用 Release。它先執行產生六份合成報表的現行測試，把同一輪產生的工作簿放進本次專屬暫存目錄，
再由 `tools/harness/excel-driver/` 逐份使用原生 Excel 開啟。每份工作簿都要以唯讀方式開啟、停用巨集與外部連結
更新、完整重算、另存副本、重新開啟副本，並匯出第一頁 PDF。框架會確認來源檔沒有改變、重算後公式錯誤沒有
增加、副本沒有外部連結，而且 PDF 結構可讀。

Excel 驅動程式不接受任意工作簿路徑，只能選擇設定檔中的六種合成報表。它可在使用者已開啟 Excel 時執行，
但只會透過 Excel 視窗代碼、PID 與啟動時間管理本次建立的 Excel 行程。正常情況先要求 Excel 結束；若行程沒有
及時退出，後備清理也只能終止已確認屬於本次的 PID。Excel 不可用、工作簿無法開啟或無法證明行程歸屬時，
結果為 `blocked`。這條路線不讀取 `data/test-case/`，也不能代替私人 JE／TB 案件驗收。

`PrivateCase` 是唯一可讀取私人案件的命令，而且每次都要明示提供完整根目錄、根目錄內的案件清單相對路徑
及資料庫實作。三項設定缺一時，命令會在建置和資料存取前回報 `blocked`。正式流程會建立安全副本，再用
JET 走完匯入、驗證、篩選及六份報表輸出。六份報表同時比較內容與版面；INF 的 59 筆樣本則檢查抽樣規則、
實際母體與樣本有效性，不要求重現舊 IDEA 的隨機列。每次執行建立的私人副本與輸出一律在收尾時清除，
成功與失敗都不保留。收據只保存去識別化的狀態、數量與差異類型。

`ReleaseCandidate` 固定使用 Release，不要求 live SQL Server。它先核對
`docs/first-root-commit-candidate.txt` 與 Git 尚未忽略的候選檔案完全一致，再把這些檔案複製到本次專屬的
一次性快照；建立前後會比較來源儲存庫的 Git index entries，不能只由執行器宣告未修改。快照內依序執行
Contract、Documentation、Public、Package、Gui 與 Excel，任何一步不通過就停止。`Provider` 與
`PrivateCase` 都不在這條路線內，也不會因本機存在連線或
`data/test-case/` 就自動讀取；需要真實案件時仍要另行明示執行。為避免原生 Excel 無法開啟過深路徑，快照
使用 `artifacts/harness/rc/<短代碼>/s`；這個目錄仍有本次執行的所有權標記，且和子執行資料一樣在收尾時
一律清除。

Restore、Build 和 Documentation 不使用共享鎖；其餘正式命令都會使用共享鎖。
`Contract` 只檢查驗證框架本身的鎖、子程序、逾時、輸出大小、JSON 紀錄及安全清理；它通過不代表產品測試
已通過。

`Documentation` 會檢查目前仍有效的文件與 AI Agent 轉接檔。缺檔、必要指向缺漏及已確認過時的事實會
失敗；不自然用語只列為 warning。這個命令不會替人判斷文章品質；執行通過後，仍要把本次改動的段落完整
讀一次。

## 結束碼

| 結束碼 | JSON 狀態 | 意義 |
|:---:|:---|:---|
| 0 | `passed` | 本次命令的必要步驟與清理都已完成 |
| 1 | `failed` | 子程序已執行，但回傳失敗 |
| 2 | `blocked` | 被共享鎖、逾時或取消擋住，尚未得到產品正誤結論 |
| 3 | `usage_error` | 命令、參數或證據路徑不合法 |
| 4 | `infrastructure_error` | 驗證框架本身、JSON 紀錄或安全清理失敗 |

Restore 因 `NU1301` 無法讀取 NuGet 來源時，會回報 `blocked`，因為此時尚未進入產品正誤判定；
套件不存在或其他真正的還原錯誤仍會回報 `failed`。

標準輸出只會有一行 JSON 摘要。完整執行紀錄與已限制大小的子程序輸出，會放在
`artifacts/harness/` 下的獨立資料夾；這些資料受 `.gitignore` 排除。
保存子程序輸出前，框架會將儲存庫根目錄與使用者目錄換成 `[repository]` 和 `[user-profile]`。Provider
另會把連線字串及可辨識的連線欄位換成 `[sensitive]`，並在讀取 TRX 前做相同處理。

除了明示執行的 `PrivateCase`，其他命令都不得讀取舊專案、舊執行輸出或私人案件資料夾，其執行紀錄必須
顯示 `privateData.pathInspected=false`。`PrivateCase` 只記錄是否確實讀取，不保存私人路徑，並固定記錄
`outputsRetained=false`。測試程序在這台 Windows 主機使用本機主控台碼頁輸出；驗證框架會先正確解碼，
再以 UTF-8 保存，避免中文失敗原因變成亂碼。

## 驗證框架自身測試

下列腳本用來測試驗證框架本身，不是第二個產品驗證入口：

```powershell
pwsh -NoProfile -File tools/tests/verify-contract.tests.ps1
```

它會在 `artifacts/harness/contract-tests/` 產生隔離的測試證據，安全地模擬子程序失敗、逾時、輸出截斷、
本機路徑與敏感值遮蔽、封裝設定憑證攔截、JSON 寫入失敗、清理拒絕及共享鎖衝突。它也會檢查 `tools/`
第一層只有 `harness` 與 `tests`，並檢查驗證框架子目錄的命名。它也會確認 GUI 驅動程式沒有瀏覽器驅動
套件或私人資料路徑，以及 Excel 驅動程式只能使用固定合成輸入、原生 Excel API 和精確的行程清理規則。
這個腳本不啟動 Excel、不修改產品檔案，也不讀取私人案件資料。
