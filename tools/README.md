# JET 驗證框架使用說明

`tools/verify.ps1` 是儲存庫唯一的公開驗證入口。Phase 1 至 Phase 7 已完成；`Provider`、`Package`、七個 GUI
情境、五份合成報告與科目配對範本的原生 Excel 檢查、SQLite／SQL Server 的同一私人案件，以及完整 `ReleaseCandidate`
都有歷史通過紀錄；本輪新增篩選情境的最新結果以現行計畫為準。

## 名稱與目錄分工

前端設計預覽放在 `tools/harness/frontend-preview/`，操作方式見
[`docs/development-guide.md`](../docs/development-guide.md#前端設計預覽試用)。
這是開發用的固定合成回放，不是新增業務後端或正式 GUI 驗證。
工具測試由 `tools/verify.ps1 -Command Contract -ContractScenario FrontendPreview` 啟動，
沿用既有子程序、逾時與收據管理；只有這項明示檢查需要 Node.js，不加入 Public 或 ReleaseCandidate 的必要依賴。
合成素材由 `Focused -Configuration Release -Filter 'FrontendPreviewFixtureTests'` 在明示環境變數下產生。
Package 拒絕發布預覽目錄與 fixtures.json，正式 JET 不依賴預覽工具。

配對前端的行為檢查使用 `tools/verify.ps1 -Command Contract -ContractScenario FrontendMapping`。
它用 Node.js 直接執行正式前端腳本，控制合成回應的先後順序，核對重試、來源變更、政策還原、就地錯誤及
RDE 全選。DOM 接線使用測試替身，不代表真實版面已驗收；實際滑鼠與鍵盤操作另由 Gui 檢查。
這項檢查只在明示選用時需要 Node.js，不增加 Public 或 ReleaseCandidate 的執行依賴。

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

## 日常資料庫測試與服務收尾

2026-09-02 使用者確認，SQL Server 開發目前暫緩，日常工作集中在 SQLite 和 DuckDB。依修改範圍選擇
`Focused` 或 `Public`；需要比較兩個本機資料庫時，可用 `Focused -Filter 'ProviderParity'`。
`Provider` 包含 live SQL Server 測試，只有使用者當次明示需要時才執行，也不為了補齊一般開發驗證而啟動。
`PrivateCase` 同樣要取得當次授權，選擇 SQL Server 更不能從既有連線設定推定。

`Focused` 和 `Public` 已排除 `Profile=Provider`，一般測試子程序也會移除 `JET_SQLSERVER_CONNECTION`；
`ReleaseCandidate` 不包含 `Provider`。不需連線的 SQL 產生與契約測試仍保留，不按檔名整批移除 SQL Server
測試，也不把未執行的 live 驗證記成通過。

本機服務的啟停由執行開發工作的 Agent 或開發者負責；`tools/verify.ps1` 目前不會自動管理 Windows 服務。
每次開發測試結束時確認本機 SQL Server 沒有留在背景。明示授權使用本機 SQL Server 時，依下列方式收尾：

1. 啟動前核對本次使用的本機執行個體、服務與相依服務，只開啟測試需要的服務；維持手動啟動。
2. 把測試及收尾放在同一段 `try` 和 `finally` 流程。成功、失敗或可處理的中止都先結束測試連線、完成
   原有測試資料清理，再正常停止服務。若程序遭強制中斷，接手時先檢查並完成服務收尾。
3. 這台開發電腦使用 `MSSQLSERVER`，平時與 `MSSQLLaunchpad`、`SQLSERVERAGENT` 一起保持停止且手動
   啟動。停止引擎前先停止仍在執行的相依服務，然後重新讀取狀態，確認服務已停止且沒有 `sqlservr.exe`。
4. 停止服務不刪資料庫檔案，也不用終止全部同名程序代替正常關閉。只處理本次已確認的本機 JET 服務；
   遠端或共用執行個體另依其管理責任處理。若權限不足而無法停止，明說尚未關閉及需要的 Windows 管理員
   授權，不得回報已完成收尾。

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
跑完目前安全且不需外部服務的公開測試。這組測試至少要找到 3,000 個案例。
`public-skip-policy.json` 目前沒有允許略過的群組；舊儲存層的檔案系統連結測試已退役，因此任何跳過都會
讓公開測試失敗。

### 需要時執行變異測試

`Mutation` 刻意改壞指定規則，檢查公開測試能否發現行為變化。它不會隨 `Public` 或 `ReleaseCandidate`
自動執行，結果只適用於所選範圍。固定使用 Release，預設範圍為 GlProjectionGuard：

```powershell
pwsh -NoProfile -File tools/verify.ps1 -Command Mutation -Configuration Release
pwsh -NoProfile -File tools/verify.ps1 -Command Mutation -Configuration Release -MutationScope MoneyScaling
```

每次上限 1,800 秒、同時一個工作程序，使用較低優先權。工具會先完成獨立的篩選與取消檢查，再建立來源
清單中產品檔案的工作樹副本；包含已確認但未提交的檔案，不掃描未列入清單的本機設定或私人資料。
只執行指定公開測試類別，固定排除 PrivateCase、Provider 和 Scale，並核對每次實際送出的測試身分。
來源固定為 Stryker `30005a2f52c53ac2b0de49a90145f326ea43d675`，相容修補與雜湊見 `harness/mutation/`。
它尚未正式發行；未來官方提供等價行為時，先重跑相同資格檢查再更換版本。
工具畫面仍顯示上游版本字樣 `4.16.0`，實際來源以固定 revision、修補雜湊與收據中的工具雜湊為準，
不能把它當成先前未修補的正式發行版。

快取、副本及 HTML 和 JSON 報告保存在 `artifacts/harness/mutation/`，不全域安裝或上傳報告。程序在開始
執行前先加入本次專屬 Windows Job，正常結束、逾時或取消後皆確認所屬子程序已結束。未抓到的變異必須
逐項判讀；補強測試後重跑，不能只看分數宣稱整個 JET 已無漏洞。
`Survived` 和 `NoCoverage` 都會要求判讀；`Ignored` 與 `CompileError` 保留原始分類，不計為測試抓到。
其中 CompileError 可能包含上游批次編譯連帶回復的變異，不能直接推論每一項替換都無法獨立編譯。
本輪的實際量測與工具限制記在現行計畫。

驗證工具本身可分別執行 `Contract -ContractScenario MutationBoundary` 與
`Contract -ContractScenario OwnedProcessTree`；完整框架自身測試也會執行這兩項。

`Provider` 不讀 `appsettings.json` 的 SQL 密碼，只接受程序環境中的 `JET_SQLSERVER_CONNECTION`。缺少時回報
`blocked`，連建置都不會開始。正式執行會使用 `JET_Test`，建立及清除測試資料結構與資料，因此只能連到
明確準備的測試環境。測試用專案若清理失敗，該次測試也會失敗。連線值不會寫進命令或收據，stdout、
stderr 與 TRX 也會在保存前遮蔽。

目前 SQL Server 中心化方向暫緩，因此 `Provider` 是明示執行的 live 相容性驗證，不是一般開發或
`ReleaseCandidate` 的先決條件。連線只在確定要驗證的本機程序中暫時提供；不得寫入儲存庫、設定範例、
命令列、聊天或長期保存的收據。

`Package` 固定使用 Release。它先檢查可追蹤設定沒有憑證，再執行建置、70 項以上的範本與報告測試，最後在
本次專屬暫存目錄使用 FolderProfile 發布。驗證會核對 x64 單檔執行檔、八份範本、`wwwroot`、安全設定及
禁止出現的執行期資料。測試範圍固定包含 `ReportArtifactTrustJourneyTests` 與
`ProjectReportArtifactStoreTests`，檢查範本原檔往返、版本保留、同名覆蓋及失敗後重試；框架自身測試會確認
這兩組沒有從封裝選取條件中消失。清單以本次現行來源產生，不沿用舊 P5 基準。暫存封裝完成後會自動移除，只保留清單
與有限大小的紀錄。實際 publish 會占用較多 CPU 與磁碟，執行前應先確認本機負載。

`Gui` 固定使用 `AgentGuiTest` 組態。它會先建置一次，再分別用全新的暫存目錄執行八個情境：

- `filter-auditor-journey` 走一遍審計員在第五步的路：點範本「借現金、貸非現金」，另開一組加一條
  「每月幾日不屬於 28、31」，切換作用中組，預覽後對命中傳票清單的表頭排序，保存後核對讀回文字與情境數。
  另以真實點擊驗證多選三個日期、期末最後七天，以及缺分類錯誤標示到條件列。
  另檢查切換工作區和關閉保存區仍保留條件、矩陣三種檢視、隱藏矩陣不重載，以及報告對應目前版本。
  也檢查加入後左側重設、右側欄位固定、單條件不顯示關係設定、直接移除，以及排除指定日期。
  另核對期末天數直接帶入日期、每月幾日沒有無關設定、借貸分類不溢出、保存按鈕間距與傳票查看按鈕完整可見。
  另檢查日期區間在窄版與桌面版的排列、借貸別標題間距，以及情境名稱和動機草稿隨條件更新並保留手動修改。
  修改草稿前後另核對已保存的條件說明沒有被改寫。
  固定 96 個操作，上限 96 個，保存兩張截圖。多組加入目標以原生下拉選擇，保存表單開啟時不重複顯示入口。
- `filter-kct-editing` 檢查新 KCT 情境保存後重新開案，卡片與非營業日群組能還原；取消 KCT 不刪同型自訂
  條件。舊情境缺少卡片來源時，保留原來源及條件，直接編輯後仍不猜字母；也驗證取消移除、確認移除後的
  清單與鍵盤焦點。另檢查選單不推高情境列、金額門檻是否包含邊界、正負號與空白設定，以及文字排除方式切換。
  同時確認摘要輸入框的起始高度與垂直對齊。固定 70 個操作，上限 70 個，保存一張截圖。

兩個篩選情境都先使用 1024 × 640 邏輯像素視窗及 125% WebView 放大，依實際 DPI 換算，並核對網頁寬度為 760 至 840px。
審計員情境完成此窄版檢查後，
只將該次測試擁有的視窗調整為 1560 × 1100，補拍雙欄版面；不變更桌面設定或其他案件視窗。
桌面尺寸也以邏輯像素換算，網頁寬度另核對為 1200 至 1260px。
只想診斷其中一個情境時，可加 `-GuiScenario <名稱>` 單跑；這種收據會標記 `partial`，不算 `Gui` 通過，
收尾前仍要完整跑一次 `Gui`。

- `startup-smoke` 檢查頁面、`JetApi`、`systemPing`、專案選擇畫面及離開按鈕，最後按下「結束 JET」。
- `synthetic-sqlite-create` 從可見畫面點選新增專案，只以鍵盤事件輸入案件名稱及兩個查核日期，案件編號與
  客戶名稱保持空白，資料儲存方式沿用 SQLite。建立案件並進入匯入步驟後，檢查會核對畫面顯示的案件名稱、
  本次暫存根目錄中的 `project.json` 與 `jet.db`、兩個選填欄位確實保存為空白，以及操作人員是否為隔離測試帳號，
  並保存一張匯入步驟的畫面供版面複核。
- `mapping-required-sync` 從已提交的合成 mapping 進入「重新配對」，清空並補回同一個必填欄位十次，每次確認右側
  缺漏狀態即時往返、提交資格回復，而且重建後仍聚焦同一個下拉。
- `edited-report-still-loads` 先在 JET 之外改寫已發布的 Working Paper 並放回舊版輸出紀錄檔，再從 Release
  可見介面開啟案件：載入成功、第六步清單標示「已在 JET 之外修改」、匯出按鈕仍可用、清理面板已移除，
  支援日誌安全寫入案件目錄且含
  `artifact.journal.discarded`、舊紀錄檔已清掉。
- `approval-mapping-modes` 操作兩種配對畫面的核准日方式與來源欄，確認同步、未提交提示、人工或自動來源欄
  取消後的政策重設、完整還原及缺漏欄位定位。還原結果以第一次編輯前保存的獨立快照核對。
- `validation-auto-outputs` 執行驗證，確認兩份報告與科目配對範本自動產生，再填入合成分類並重新驗證，
  核對已填範本逐位元組保留。

`edited-report-still-loads` 包含 52 筆過期 Working Paper 紀錄，其中有外部修改與已刪除的檔案。第六步
必須列出歷史紀錄並實際切換分頁，提供仍存在檔案的定位入口，且不能把舊底稿算成目前流程已完成。
再從畫面匯出新版，確認 53 筆紀錄都保留、新版排首位，完成摘要此時才出現。定位入口的實際路徑解析
與缺檔錯誤由 handler 測試驗證；GUI 情境不開啟檔案總管。

配對與驗證輸出情境各保存一張不超過 2 MB 的合成畫面截圖，篩選情境保存傳票與日曆兩張，其餘不截圖。
動作數量與時間上限固定在
`harness/lanes.json`，驅動程式與收據都會核對，不接受外部擴大操作範圍。

驅動程式位於 `tools/harness/gui-driver/`。外部只能選擇上述固定情境，不能傳入 JavaScript、selector 或任意
動作；程式也不使用 Selenium、EdgeDriver 或網路下載。每個情境都必須由 JET 自行結束，並完成程序與暫存
目錄清理。這些結果不代表原生檔案對話框或私人案件已完成驗收。

`Excel` 固定使用 Release。它先執行產生五份合成報告與一份科目配對範本的現行測試，把同一輪產生的六份工作簿放進本次專屬暫存目錄，
再由 `tools/harness/excel-driver/` 逐份使用原生 Excel 開啟。每份工作簿都要以唯讀方式開啟、停用巨集與外部連結
更新、完整重算、另存副本、重新開啟副本，並匯出第一頁 PDF。框架會確認來源檔沒有改變、重算後公式錯誤沒有
增加、副本沒有外部連結，而且 PDF 結構可讀。

Excel 輸入由 `SixReportWorkflowJourneyTests.AccountMappingHandoff_PreservesValidationRun_AndPublishesFiveReportsAndTemplate`
產生。這個旅程先填寫範本原檔、匯回、重新開啟案件，再匯出兩份驗證報告與後續報告；範本保留填好的內容。
合成案件包含兩張傳票，以借貸分類、多日期、金額區間及排除條件核對預覽、報告文字及底稿數字。
「分類與指定日期」情境將分類留白的貸方科目視為 Others，兩張都命中；篩選相關報告不得出現「待判定」。
類別名稱與 `JET_SIX_REPORT_EVIDENCE_DIR` 為既有介面保留，六份工作簿只有五份進入報告清單。
原生 Excel 部分仍只驗證開啟、重算和另存副本；它沒有操作範本原檔填寫，也沒有測試 Excel 佔用中的再次匯出。
這兩項在公司環境的實際操作仍依現行計畫的人工驗收清單確認。

Excel 驅動程式不接受任意工作簿路徑，只能選擇設定檔中的六種合成工作簿（五份報告加科目配對範本）。它可在使用者已開啟 Excel 時執行，
但只會透過 Excel 視窗代碼、PID 與啟動時間管理本次建立的 Excel 行程。正常情況先要求 Excel 結束；若行程沒有
及時退出，後備清理也只能終止已確認屬於本次的 PID。Excel 不可用、工作簿無法開啟或無法證明行程歸屬時，
結果為 `blocked`。這條路線不讀取 `data/test-case/`，也不能代替私人 JE／TB 案件驗收。

`PrivateCase` 是唯一可讀取私人案件的命令，而且每次都要明示提供完整根目錄、根目錄內的案件清單相對路徑
及資料庫實作。三項設定缺一時，命令會在建置和資料存取前回報 `blocked`。正式流程會建立安全副本，再用
JET 走完匯入、驗證、篩選、五份報告與科目配對範本輸出。六份工作簿同時比較內容與版面；INF 的 59 筆樣本則檢查抽樣規則、
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
失敗；不自然用語只列為 warning。`documentation-check.json` 的 `styleWarnings` 比對固定片語，
`stylePatterns` 用正規表示式抓符號代句與工作用語，命中時附一句修正提示，程式碼區塊與行內程式碼不檢查。
這個命令不會替人判斷文章品質；執行通過後，仍要把本次改動的段落完整讀一次。

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

標準輸出只會有一行 JSON 摘要，失敗時其中的 `firstRed` 帶出第一個失敗步驟的名稱、原因碼與 `detail`；
測試步驟的 `detail` 是失敗數加上第一個失敗測試的名稱與一行訊息，不必再開 TRX。完整執行紀錄與已限制
大小的子程序輸出，會放在 `artifacts/harness/` 下的獨立資料夾；這些資料受 `.gitignore` 排除。
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
它另執行 `FrontendMapping` 的實際派送與程序清理檢查，因此執行完整框架自身測試時需要 Node.js。
