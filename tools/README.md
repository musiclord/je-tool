# JET 驗證框架使用說明

`tools/verify.ps1` 是儲存庫唯一的公開驗證入口，本文件列出它的完整命令、參數與結束碼。驗證框架的原則
與判定依據見 [`.agents/harness/harness.md`](../.agents/harness/harness.md)。

## 目前可用的命令

```powershell
pwsh -NoProfile -File tools/verify.ps1 -Command Context
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

`Context` 是唯讀導覽，回傳 `observed`，不是測試通過。它從 `development-status.md` 的目前大型計畫連結讀取現行摘要，
列出程式、文件、工具及 Agent 轉接檔的工作樹變更，以及近期最多八組驗證的最新可讀收據；不讀資料目錄與案件內容，
不建立收據、不拿共享鎖、不改工作樹，也不自動啟動其他驗證。近期收據只查最新二十個執行目錄，相同命令、組態、篩選
及情境只列最近一次，最新失敗不會被較舊的通過蓋掉；相同 HEAD 的既有收據也不保證目前未提交的內容已驗證。
它也列出裁定總表 `docs/rulings.md` 與名詞表 `docs/glossary.md` 的則數與待使用者確認的則數，不印內容。
計畫可用 `<!-- jet-context:start -->` 和 `<!-- jet-context:end -->` 標出接續摘要；沒有標記就取第一節，最多 7,000 字元。
Context 自己的行為由 `Contract -ContractScenario Context` 驗證。

`Build` 預設先執行 Restore，再以 `--no-restore` 建置；確定 NuGet 還原資產已存在時才用 `-NoRestore`。

`Focused` 的 `-Filter` 接受測試方法或類別的完整名稱片段，不接受萬用字元，也不能用 `|` 一次選多個。它先建置再執行選取的測試，
並固定補跑架構檢查；選不到測試或遇到跳過都不算通過。`Public` 不接受 `-Filter`，依固定種子跑完目前安全且不需外部服務的
公開測試，至少要找到 3,000 個案例。`public-skip-policy.json` 目前沒有允許略過的群組，任何跳過都會讓公開測試失敗。

`Focused`、`Public` 與 `Package` 讓不同測試類別平行執行，同時執行的類別數等於 CPU 執行緒數；同一類別裡的測試依序執行。
檢查實際經過時間上限的測試放在 `TimingSensitive` 集合，等其他測試結束後單獨執行。`Provider`、`PrivateCase` 與 `Excel`
仍逐一執行。xUnit 依 UniqueID 排定類別先後，同一個 clone 路徑每次順序相同；`src/JET/tests/JET.Tests/LongRunningFirstCollectionOrderer.cs`
把耗時最長的 `ActionShapeContractTests` 排第一，避免它最後獨自拖長整段測試。

內容相同的合成案件在一個測試程序裡只用正式 action 建一次，之後每個測試把案件資料夾複製到自己的暫存目錄再開啟，
做法在 `src/JET/tests/JET.Tests/Application/CaseTemplates.cs`；SQL Server 照舊從頭建案。要檢查建案者身分或建案過程本身的測試，
例如進度事件、稽核紀錄與日誌，呼叫 `DemoProjectPipeline.SetupAsync` 或 `ReportRevisionStateTests.PrepareAsync` 時加 `fromScratch: true`。

## 各路線的範圍

### 需要時執行的變異測試

`Mutation` 刻意改壞指定規則，檢查公開測試能否發現行為變化。它不會隨 `Public` 或 `ReleaseCandidate` 自動執行，
結果只適用於所選範圍。固定使用 Release，預設範圍是 GlProjectionGuard：

```powershell
pwsh -NoProfile -File tools/verify.ps1 -Command Mutation -Configuration Release
pwsh -NoProfile -File tools/verify.ps1 -Command Mutation -Configuration Release -MutationScope MoneyScaling
```

每次上限 1,800 秒、同時一個工作程序、較低優先權。工具先完成篩選與取消檢查，再建立產品檔案的工作樹副本：
檔案清單取自 Git，包含 `global.json`、`src/JET` 底下已追蹤與未追蹤但未被忽略的檔案，以及 `src/JET` 專案從 `tools/` 連結進來的原始碼，
所以包含尚未提交的修改，不掃描被忽略的本機設定或私人資料。只執行指定的公開測試類別，固定排除 PrivateCase、Provider 和 Scale，
並核對每次實際送出的測試身分。

工具來源固定為 Stryker `30005a2f52c53ac2b0de49a90145f326ea43d675`，相容修補與雜湊見 `harness/mutation/`。它尚未正式發行，
畫面仍顯示上游版本字樣 `4.16.0`，實際來源以固定 revision、修補雜湊與收據中的工具雜湊為準；官方日後提供等價行為時，
先重跑相同資格檢查再換版。程序在開始前加入本次專屬的 Windows Job，結束、逾時或取消後都確認子程序已結束。

`Survived` 與 `NoCoverage` 都要逐項判讀，補強測試後重跑，不能只看分數宣稱整個 JET 沒有漏洞；`Ignored` 與 `CompileError`
保留原始分類，不算測試抓到。CompileError 可能包含上游批次編譯連帶回復的變異，不能推論每一項替換都無法獨立編譯。

快取、副本與報告放在 `artifacts/harness/mutation/`，不全域安裝、不上傳。依裁定 2026-10-15-01，這個資料夾比照收據有保留期限，
每個使用共享鎖的命令結束前整理一次；`Mutation` 也拿這把鎖，所以整理時不會有進行中的變異測試：

- `r/` 每次執行一個資料夾。同一個範圍只完整保留最新 2 次；更舊的只留最上層的摘要檔，`result.json` 已逐項記下每個變異的結果。
  中斷而沒有寫 `pipeline.json` 的執行不整理。
- `t/` 每個 Stryker 修補版本各有一份工具來源，只留最新一次執行用的那份。
- `c/http` 是 NuGet 下載快取，整個刪掉；`c/packages` 保留，還原時不必連網。

工具本身可用 `Contract -ContractScenario MutationBoundary` 與 `Contract -ContractScenario OwnedProcessTree` 檢查。

### Provider

`Provider` 不讀 `appsettings.json` 的 SQL 密碼，只接受程序環境中的 `JET_SQLSERVER_CONNECTION`；缺少時回報 `blocked`，
連建置都不開始。正式執行使用 `JET_Test`，會建立及清除測試資料結構與資料，所以只能連到明確準備的測試環境；
測試用專案清理失敗時，該次測試也失敗。連線只在確定要驗證的本機程序中暫時提供，不寫入儲存庫、設定範例、命令列、
聊天或收據，stdout、stderr 與 TRX 在保存前都會遮蔽。何時執行見下方「日常資料庫測試與服務收尾」。

### Package

`Package` 固定使用 Release。它先檢查可追蹤設定沒有憑證，再執行建置、70 項以上的範本與報告測試，最後在本次專屬暫存目錄
用 FolderProfile 發布，核對 x64 單檔執行檔、八份範本、`wwwroot`、安全設定及禁止出現的執行期資料。測試範圍固定包含
`ReportTrustJourneyTests` 與 `ProjectReportStoreTests`，框架自身測試會確認兩組沒有從選取條件中消失。暫存封裝完成後自動移除，
只保留清單與有限大小的紀錄。實際 publish 占用較多 CPU 與磁碟，執行前先確認本機負載。

### Gui

`Gui` 固定使用 `AgentGuiTest` 組態，先建置一次，再分別用全新的暫存目錄執行下列情境。每個情境的操作上限、預期次數、
逾時與截圖數只寫在 `tools/harness/lanes.json`；驅動程式與組態另有固定最大值（操作 120 次、截圖 2 張），調高情境預算超過時要一起改。
截圖都是合成畫面，每張不超過 2 MB。

驅動程式為每個情境另建一個 Windows 桌面，用 `CreateProcess` 把 JET 開在那裡，情境結束時關閉（`OwnedGuiRun.cs`），
不切換桌面，所以使用者看不到 JET 視窗，點擊與打字也不會落到 JET 上。調整視窗大小時找 JET 唯一看得見、
沒有擁有者的主視窗，不用 `Process.MainWindowHandle`，因為輸入指示器的小視窗也屬於 JET 程序。
完整執行要幾分鐘並占用 CPU，何時執行見 `AGENTS.md`「測試」。

- `startup-smoke`：頁面、`JetApi`、`systemPing` 與專案選擇畫面，最後按「結束 JET」。
- `synthetic-sqlite-create`：只用鍵盤建立 SQLite 案件，核對畫面、`project.json`、`jet.db` 與操作人員。
- `case-to-working-paper`：從空白案件走到匯出底稿，再依序匯入科目配對、假日、補班日，附加與取代 GL、取代 TB，
  核對每一步清除與保留的狀態，以及只有新的底稿是現行版本。
- `mapping-required-sync`：改借方代碼後直接「重新確認配對」一次完成；清空並補回必填欄位，核對缺漏狀態與焦點。
- `edited-report-still-loads`：底稿在 JET 之外被改寫後，案件仍能載入、第六步有標示、匯出可用，支援日誌不洩漏檔名與路徑，過期與缺檔不算完成。
- `approval-mapping-modes`：兩種配對畫面的核准日方式與來源欄，含未提交提示、取消、還原與缺漏定位。
- `validation-auto-outputs`：驗證後自動產生兩份報告與科目配對範本，填入分類重新驗證，已填範本逐位元組保留。
- `filter-auditor-journey`：第五步從範本「借現金、貸非現金」開始，加條件、切換組、預覽、排序、保存並核對讀回文字。
- `filter-common-editing`：常用篩選條件卡片的保存與重開還原、舊版情境的處理，以及移除、取消、金額門檻、文字排除與自動命名。
- `feedback-workflow`：配對清單捲動、分類輸入、中文組字、取消、保存與重開，改配 TB 金額欄後仍可預篩選與匯出。
- `blank-details-recovery`：資料缺漏輔助檢視兩類明細的跨頁、排序與搜尋，以及搜尋失敗後保留、重試與零筆。
- `common-remap-recovery`：取消配對修改時情境仍在；移除必要欄位會清空已存情境，補回後重新儲存、驗證、匯出與重開。
- `upstream-change-after-export`：已匯出底稿後回頭修改前面步驟。取消與只改文字保留情境；重新確認 TB 配對或改每週休息日會清除情境，
  底稿仍能匯出；重新儲存後只勾一個情境匯出，重開仍是同一份結果。
- `authorized-list-recovery`：授權編製人員清單的手動選欄、取消、匯入失敗重試、預覽、移除、重開、再匯入與下游失效。
- `extended-conditions`：人工與自動補集、統計張數、日曆、金額尾數、人員清單與文字否定，保存五個情境、匯出與重開。
- `side-month-workflow`：窄視窗用子條件組與整張傳票條件組合借貸別與科目編號，設定月初月底天數，核對報告、底稿與人工旗標。
- `nested-voucher-workflow`：分類上層設定、巢狀條件、整張傳票條件與分類選取，空條件失敗後重試、保存、取消、重開與兩種報表。
- `legacy-form-workflow`：套用原工作簿轉入的四個情境範例，再加「金額尾數連續多個 0」，核對預覽、保存、兩種報表與重開。

所有情境都走正式前端與 action，CDP 只讀取畫面與狀態；選檔回應由固定合成夾具提供，中文組字用 WebView2 的 CDP 組字事件驗證，
不代表已實測各家輸入法。兩個篩選情境先用 1024 × 640 邏輯像素與 125% 放大核對網頁寬度 760 至 840px，再把本次視窗調成
1560 × 1100，核對 1200 至 1260px。診斷時可加 `-GuiScenario <名稱>` 單跑，收據標記 `partial`，不算 `Gui` 通過。
驅動程式在 `tools/harness/gui-driver/`，外部只能選固定情境，不能傳入 JavaScript、selector 或任意動作，也不用 Selenium、
EdgeDriver 或網路下載。這些結果不代表原生檔案對話框或私人案件已驗收。

### Excel

`Excel` 固定使用 Release。它先執行產生五份合成報告與一份科目配對範本的測試
`SixReportWorkflowJourneyTests.AccountMappingHandoff_PreservesValidationResult_AndPublishesFiveReportsAndTemplate`
（類別名稱與 `JET_SIX_REPORT_EVIDENCE_DIR` 沿用舊名，六份工作簿只有五份進入報告清單），再由 `tools/harness/excel-driver/`
逐份用原生 Excel 以唯讀開啟、停用巨集與外部連結更新、完整重算、另存副本、重新開啟副本，並匯出第一頁 PDF。框架核對來源檔沒有改變、公式錯誤沒有增加、副本沒有外部連結、PDF 可讀，以及 V_Report 5 的表頭、
中段與最後資料列都是 15 點。另由既有 Office 建立 .xls、.mdb 與 .accdb，在 SQLite 與 DuckDB 各走完預覽、錯表重試、匯入、配對、
驗證、篩選、重開與原檔不變檢查，共六例，結果在收據的 `excelFixture.nativeSourceImports`。

原生 Excel 部分只驗證開啟、重算和另存副本，沒有操作範本原檔填寫，也沒有測試 Excel 佔用中的再次匯出。
驅動程式只能選設定檔中的六種合成工作簿，可在使用者已開啟 Excel 時執行，但只管理本次建立、已確認 PID 的 Excel 行程；
Excel 不可用、工作簿無法開啟或無法證明行程歸屬時，結果是 `blocked`。這條路線不讀 `data/test-case/`，不能代替私人案件驗收。

### PrivateCase

`PrivateCase` 是唯一可讀取私人案件的命令，每次都要提供完整根目錄、根目錄內的案件清單相對路徑與資料庫實作，
缺一就在建置和資料存取前回報 `blocked`。它建立安全副本，用 JET 走完匯入、驗證、篩選、五份報告與科目配對範本輸出，
六份工作簿同時比較內容與版面；INF 樣本檢查抽樣規則、實際母體與樣本有效性，不要求重現舊 IDEA 的隨機列。
私人副本與輸出在收尾時一律清除，收據只保存去識別化的狀態、數量與差異類型。案件清單照舊寫法也能讀，讀取時換成現行名稱。

### ReleaseCandidate

`ReleaseCandidate` 固定使用 Release，不要求 live SQL Server。候選內容直接取自 Git：已有提交時是全部已追蹤的檔案，
而且工作樹必須乾淨；候選裡出現私人資料目錄的路徑就直接失敗。框架把檔案複製到本次專屬的快照
`artifacts/harness/rc/<短代碼>/s`，建立前後比較來源儲存庫的 Git index entries，再在快照內依序執行 Contract、Documentation、
Public、Package、Gui 與 Excel，任何一步不通過就停止；收尾時快照一律清除。`Provider` 與 `PrivateCase` 不在這條路線內。

### 共享鎖、記憶體上限與 Contract

Restore、Build 和 Documentation 不使用共享鎖，其餘正式命令都使用。`Contract` 只檢查驗證框架本身的鎖、子程序、逾時、
輸出大小、JSON 紀錄及安全清理，通過不代表產品測試已通過。

建置、發布、.NET 測試與前端測試都在本次專屬的 Windows Job 裡以較低優先權執行。整個程序樹合計的已認可記憶體超過
`lanes.json` 的 `limits.maximumProcessTreeMemoryMegabytes`（目前 8192 MB）時，框架立刻結束整個程序樹，該步驟判定為 `failed`，
原因碼 `memory_limit_reached`；收據的 `process` 記下上限、尖峰記憶體與是否碰到上限。建置與發布另加 `--disable-build-servers`。
Restore、Gui 與 Excel 驅動程式自己管理的程序，以及 `Mutation` 的程序樹，不在這個上限內。

### Documentation

`Documentation` 檢查目前仍有效的文件與 AI Agent 轉接檔。缺檔、必要指向缺漏及已確認過時的事實會失敗；不自然用語只列為提醒。
`documentation-check.json` 的 `styleWarnings` 比對固定片語，`stylePatterns` 用正規表示式抓符號代句與工作用語，程式碼不檢查。
`sizeLimits` 依文件類型設長度上限，超過就失敗：現行計畫 120 KiB；裁定總表 320 KiB；系統規格、action 契約、前端說明與名詞表 160 KiB；
其他現行文件與 Agent 文件 30 KiB；歷史文件不設上限（裁定 2026-11-02-04）。
`rulingLocations` 核對裁定總表「落實位置」寫的檔案都存在；`rulingReferences` 核對 `src/JET` 與 `tools` 裡寫的「裁定 編號」都是現行裁定；
`retiredNames` 在 `src/JET/JET` 找名詞表「不再使用的名稱」列的舊名稱，註解也算；`tableNames` 核對三種資料庫建立的表都列在名詞表，
名詞表列的表也都還存在。架構圖表另由 [`harness/architecture/`](harness/architecture/README.md) 檢查，結果寫在文件報告的
`architecture` 欄位，它不重新繪圖，也不證明業務說明正確。這個命令不判斷文章品質，通過後仍要把改動段落完整讀一次。

## 日常資料庫測試與服務收尾

SQL Server 開發目前暫緩（裁定 2026-09-02-04），日常工作集中在 SQLite 和 DuckDB。依修改範圍選擇
`Focused` 或 `Public`；需要比較兩個本機資料庫時，可用 `Focused -Filter 'ProviderParity'`。
`Provider` 包含 live SQL Server 測試，只有使用者當次明示需要時才執行，也不為了補齊一般開發驗證而啟動。
`PrivateCase` 讀取本機私人案件的授權是常設的，不必每次再問；但選擇 SQL Server 不能從既有連線設定推定。

`Focused` 和 `Public` 已排除 `Profile=Provider`，一般測試子程序也會移除 `JET_SQLSERVER_CONNECTION`。
不需連線的 SQL 產生與契約測試仍保留，不按檔名整批移除 SQL Server 測試，也不把未執行的 live 驗證記成通過。

`tools/verify.ps1` 不會管理 Windows 服務，啟停由執行開發工作的 Agent 或開發者負責，每次測試結束都確認本機
SQL Server 沒有留在背景。明示授權使用本機 SQL Server 時，依下列方式收尾：

1. 啟動前核對本次使用的本機執行個體、服務與相依服務，只開啟測試需要的服務；維持手動啟動。
2. 把測試及收尾放在同一段 `try` 和 `finally` 流程。成功、失敗或可處理的中止都先結束測試連線、完成
   原有測試資料清理，再正常停止服務。若程序遭強制中斷，接手時先檢查並完成服務收尾。
3. 這台開發電腦使用 `MSSQLSERVER`，平時與 `MSSQLLaunchpad`、`SQLSERVERAGENT`、`MSSQLFDLauncher` 一起
   保持停止且手動啟動。停止引擎前先停止仍在執行的相依服務。`MSSQLFDLauncher` 是全文檢索的啟動服務，
   Windows 沒有把它登記成引擎的相依服務，引擎停止後它仍會執行，要另外停止。最後重新讀取狀態，確認這些
   服務都已停止且沒有 `sqlservr.exe`。
4. 停止服務不刪資料庫檔案，也不用終止全部同名程序代替正常關閉。只處理本次已確認的本機 JET 服務；
   遠端或共用執行個體另依其管理責任處理。若權限不足而無法停止，明說尚未關閉及需要的 Windows 管理員
   授權，不得回報已完成收尾。

## 前端測試與預覽工具

前端設計預覽放在 `tools/harness/frontend-preview/`，接上正式後端的瀏覽器主機放在 `tools/harness/browser-host/`，操作方式都見
[`docs/development-guide.md` 的「開發用的預覽工具」](../docs/development-guide.md#開發用的預覽工具)。兩者都是只用合成資料的
開發工具，不能取代 Gui 檢查，正式 JET 也不依賴它們。
預覽工具的測試由 `Contract -ContractScenario FrontendPreview` 啟動，合成素材由 `Focused -Configuration Release -Filter 'FrontendPreviewFixtureTests'`
在明示環境變數下產生；Package 拒絕發布預覽目錄與 fixtures.json。

前端行為檢查是 `Contract -ContractScenario FrontendMapping`：用 Node.js 直接執行正式前端腳本，控制合成回應的先後順序，
核對重試、來源變更、還原、就地錯誤、草稿與晚到回應等行為。DOM 接線使用測試替身，不代表真實版面已驗收。
Public 在 .NET 測試通過後依序執行 FrontendMapping 與 FrontendPreview，任何一項失敗都讓 Public 失敗，所以 Public 與
ReleaseCandidate 需要 Node.js。

- FrontendMapping 自動收集 `tools/tests/` 第一層所有 `frontend-*.test.cjs`，只有 `frontend-preview.test.cjs` 留給 FrontendPreview；
  其他沒有被任何路線執行的 `*.test.cjs`、`*.test.js` 或 `*.test.mjs` 會讓檢查失敗。
- 依審計員使用情境寫的測試命名為 `frontend-journey-<情境>.test.cjs`，檔案開頭的註解寫審計員的操作步驟與使用的夾具。
  兩個以上測試檔共用的夾具放在 `tools/tests/support/frontend-fixtures.cjs`，只服務一個測試檔的夾具留在那個檔裡；
  改了共用夾具要在 `frontend-support.test.cjs` 補測試。支援模組的檔名不含 `.test.`，Node 不會把它當成測試執行。
- 每個前端測試程序都要直接或經由共用夾具載入 `tools/tests/support/resource-guard.cjs`，FrontendMapping 會檢查。它每 100 毫秒
  檢查一次記憶體與執行時間，預設超過 2048 MB 或 900 秒就結束程序，並在錯誤輸出寫一行以 `JET test resource guard:` 開頭的說明；
  上限可用 `JET_TEST_MAX_RSS_MB` 與 `JET_TEST_MAX_SECONDS` 調整。2026-10-13 曾有測試失敗時沿著假 DOM 的父子連結不停展開，
  占用數十 GB 記憶體讓整台電腦停擺；共用夾具已把這些連結改成不可列舉。
- FrontendMapping 與前端改壞工具一次最多執行 4 個測試檔，數值在 `tools/tests/support/resource-guard-marker.cjs`，
  合計最多 4 × 2048 MB，不超過框架給整個步驟的 8192 MB。自己用 `node --test` 一次跑多個測試檔時，也加 `--test-concurrency=4`。

前端測試的假後端要和正式後端一致。`src/JET/tests/JET.Tests/Application/ActionShapeJourney.cs` 照審計員的順序用正式 dispatcher
走一遍合成案件，SQLite 與 DuckDB 各一次，把每個 action 收到的資料與回應的欄位和型別寫在 `tools/tests/fixtures/action-shapes.json`；
`ActionShapeContractTests` 每次重走，兩種資料庫不一致或和檔案不同就失敗。前端測試的假後端都包在
`tools/tests/support/action-contract.cjs` 的 `contractJetApi` 裡，假回應與送出的資料都要在正式契約出現過。對不上時，先判斷是哪一種：

- 審計員真的會遇到、流程還沒走到：在 `ActionShapeJourney` 補上那一步，再重新產生契約檔。
- 假資料用了正式後端不會回的欄位：照正式的形狀改假資料並註明原因，斷言不動。
- 正式後端會回、但流程穩定走不到：列進 `ActionShapeJourney.UnreachedKinds`，寫明產生它的程式與走不到的原因。
- 測試刻意假造正式後端不會回的資料來確認畫面防呆：用 `unchecked('原因', 資料)` 包起來。

改了 action 的資料形狀後，用下面的命令重新產生契約檔，看過差異符合預期，再跑一次 FrontendMapping：

```powershell
try {
    $env:JET_ACTION_SHAPES_WRITE = '1'
    pwsh -NoProfile -File tools/verify.ps1 -Command Focused -Filter 'ActionShapeContractTests'
} finally {
    Remove-Item Env:JET_ACTION_SHAPES_WRITE -ErrorAction SilentlyContinue
}
```

要確認新寫的前端測試真的抓得到錯，執行 `node tools/tests/support/mutate-frontend.cjs --spec <改壞清單.json>`。它把正式前端複製到
`artifacts/frontend-mutation/<批次>/<編號>/`，在副本做一處文字替換再跑指定的測試；同一個測試在工作樹上要通過、改壞後要失敗，才算抓到。
同一時間只跑一個，每次測試預設最多 300 秒，逾時連同子程序一起結束；碰到時間、輸出量或記憶體上限時結果是
`inconclusive-resource-limit`，要查出是產品還是測試停不下來，不能重跑了事。改壞清單的格式寫在工具檔開頭，
還在撰寫的 `*.wip.cjs` 也能檢查。前端改壞一律用這支工具，它不改工作樹裡的產品檔，輸出只是診斷紀錄；
它和用 Stryker 改壞 C# 規則的 `Mutation` 是兩回事。

## 名稱與目錄分工

- `tools/` 第一層只分成 `harness/` 與 `tests/`。新增的驗證框架子目錄一律使用小寫英文，單字之間以連字號分隔；
  C# 驅動程式也放在 `tools/harness/` 下。
- JSON 設定檔、公開入口、測試與一次性 probe 使用小寫與連字號。可重用的 PowerShell 模組與內部 verifier
  使用 `Jet` 開頭的 PascalCase；C# 專案、原始碼檔、型別、namespace 和組件名稱依 .NET 慣例使用 PascalCase。
- 驗證框架的主模組 `tools/harness/JetHarness.psm1` 只留主函式 `Invoke-JetHarness`，其餘函式依職責分在同目錄的
  `JetHarness.Foundation.ps1`、`JetHarness.Artifacts.ps1`、`JetHarness.Results.ps1`、`JetHarness.Steps.ps1`、
  `JetHarness.Preflight.ps1`、`JetHarness.Lanes.ps1` 與 `JetHarness.ReleaseCandidate.ps1`，由主模組點號載入。新增或改名這類檔案時，
  同步改主模組的載入清單與 `Get-JetRunnerIdentity` 的檔案清單，收據才會記到它的雜湊；`tools/tests/verify-contract.tests.ps1` 會檢查。
- `bin/`、`obj/`、`artifacts/` 和 `TestResults/` 都是本機產物，不進 Git。

## 結束碼

| 結束碼 | JSON 狀態 | 意義 |
|:---:|:---|:---|
| 0 | `passed` | 本次命令的必要步驟與清理都已完成 |
| 1 | `failed` | 子程序已執行，但回傳失敗 |
| 2 | `blocked` | 被共享鎖、逾時或取消擋住，尚未得到產品正誤結論 |
| 3 | `usage_error` | 命令、參數或證據路徑不合法 |
| 4 | `infrastructure_error` | 驗證框架本身、JSON 紀錄或安全清理失敗 |

Restore 因 `NU1301` 無法讀取 NuGet 來源時回報 `blocked`，因為還沒進入產品正誤判定；套件不存在或其他真正的還原錯誤仍回報 `failed`。

標準輸出只有一行 JSON 摘要，失敗時其中的 `firstRed` 帶出第一個失敗步驟的名稱、原因碼與 `detail`；測試步驟的 `detail`
是失敗數加上第一個失敗測試的名稱與一行訊息，不必再開 TRX。完整執行紀錄與限制大小的子程序輸出放在 `artifacts/harness/` 下的
獨立資料夾，受 `.gitignore` 排除。保存前，框架把儲存庫根目錄與使用者目錄換成 `[repository]` 和 `[user-profile]`；
Provider 另把連線字串及可辨識的連線欄位換成 `[sensitive]`，讀取 TRX 前也做相同處理。

依裁定 2026-10-14-14，每次命令寫好收據後，框架整理 `artifacts/harness/runs/` 的舊執行：同一種檢查只完整保留最新 10 次，
只跑單一情境或篩選的執行另外計算；更舊的只留 `receipt.json`、各步驟的 `*.receipt.json` 與擁有者標記，所以文件或測試註解
引用的舊收據仍讀得到結論與第一個失敗。還沒寫好收據的執行不會被碰到，整理失敗也不影響這次的結果。
變異測試的保留期限寫在「需要時執行的變異測試」一節。

除了明示執行的 `PrivateCase`，其他命令都不得讀取舊專案、舊執行輸出或私人案件資料夾，執行紀錄必須顯示
`privateData.pathInspected=false`。`PrivateCase` 只記錄是否確實讀取，不保存私人路徑，並固定記錄 `outputsRetained=false`。
測試程序在這台 Windows 主機用本機主控台碼頁輸出，框架先正確解碼再以 UTF-8 保存，避免中文失敗原因變成亂碼。

## 驗證框架自身測試

下列腳本用來測試驗證框架本身，不是第二個產品驗證入口：

```powershell
pwsh -NoProfile -File tools/tests/verify-contract.tests.ps1
```

它在 `artifacts/harness/contract-tests/` 產生隔離的測試證據，模擬子程序失敗、逾時、輸出截斷、遮蔽、憑證攔截、
JSON 寫入失敗、清理拒絕及共享鎖衝突，也檢查 `tools/` 的目錄規則，以及 GUI 與 Excel 驅動程式的輸入和行程清理限制。
它不啟動 Excel、不修改產品檔案、不讀私人案件資料；另執行 `FrontendMapping` 的實際派送與程序清理檢查，所以需要 Node.js。

## 技術依據

現行測試專案使用 .NET 10、xUnit v3 與 Microsoft Testing Platform。`Focused` 與 `Public` 直接執行 MTP
測試程式，並使用 MTP 的方法、命名空間與 trait 標記篩選；不能套用舊 VSTest 的 `--filter` 習慣。參考資料：

- [Testing with `dotnet test`](https://learn.microsoft.com/dotnet/core/testing/unit-testing-with-dotnet-test)
- [Migrate from VSTest to Microsoft Testing Platform](https://learn.microsoft.com/dotnet/core/testing/migrating-vstest-microsoft-testing-platform)
- [Microsoft Testing Platform test reports](https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-test-reports)
- [.NET `Path.GetFullPath`](https://learn.microsoft.com/dotnet/api/system.io.path.getfullpath)
- [.NET `FileAttributes.ReparsePoint`](https://learn.microsoft.com/dotnet/api/system.io.fileattributes)
- [.NET `File.GetAttributes`](https://learn.microsoft.com/dotnet/api/system.io.file.getattributes)
- [Use the Chrome DevTools Protocol in WebView2](https://learn.microsoft.com/microsoft-edge/webview2/how-to/chromium-devtools-protocol)
- [Excel `Application.hWnd`](https://learn.microsoft.com/office/vba/api/excel.application.hwnd)
- [Excel `Workbooks.Open`](https://learn.microsoft.com/office/vba/api/excel.workbooks.open)
- [Excel `Application.CalculateFullRebuild`](https://learn.microsoft.com/office/vba/api/excel.application.calculatefullrebuild)
- [Excel `Workbook.SaveCopyAs`](https://learn.microsoft.com/office/vba/api/excel.workbook.savecopyas)
- [Excel `Workbook.LinkSources`](https://learn.microsoft.com/office/vba/api/excel.workbook.linksources)
