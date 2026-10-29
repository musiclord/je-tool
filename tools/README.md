# JET 驗證框架使用說明

`tools/verify.ps1` 是儲存庫唯一的公開驗證入口，本文件列出它的完整命令、參數與結束碼。驗證框架的原則
與判定依據見 [`.agents/harness/harness.md`](../.agents/harness/harness.md)。

## 名稱與目錄分工

前端設計預覽放在 `tools/harness/frontend-preview/`，操作方式見
[`docs/development-guide.md` 的「開發用的預覽工具」](../docs/development-guide.md#開發用的預覽工具)。
這是開發用的固定合成回放，不是新增業務後端或正式 GUI 驗證。
工具測試由 `tools/verify.ps1 -Command Contract -ContractScenario FrontendPreview` 啟動，
沿用既有子程序、逾時與收據管理；這項檢查也納入 Public，因此 Public 及包含它的 ReleaseCandidate 需要 Node.js。
合成素材由 `Focused -Configuration Release -Filter 'FrontendPreviewFixtureTests'` 在明示環境變數下產生。
Package 拒絕發布預覽目錄與 fixtures.json，正式 JET 不依賴預覽工具。

接上正式後端的瀏覽器主機放在 `tools/harness/browser-host/`，操作方式見
[`docs/development-guide.md`](../docs/development-guide.md#開發用的預覽工具)。
它讓正式前端在一般瀏覽器執行，action 交給正式 dispatcher，案件只使用合成資料。它是開發時操作畫面的工具，
不是驗證命令，也不能取代 Gui 檢查。

配對前端的行為檢查使用 `tools/verify.ps1 -Command Contract -ContractScenario FrontendMapping`。
它用 Node.js 直接執行正式前端腳本，控制合成回應的先後順序，核對重試、來源變更、政策還原、就地錯誤及
RDE 全選，也涵蓋分類草稿的輸入、取消與保存重試，以及雙側預覽、快取與晚到回應。
完整性有差異仍可繼續、畫面保留差異，以及兩類空值明細各自呈現也有行為檢查。DOM 接線使用測試替身，
不代表真實版面已驗收；實際滑鼠與鍵盤操作另由 Gui 檢查。
Public 在 .NET 測試通過後，依序執行這項檢查與 FrontendPreview。任何一項失敗都會讓 Public 失敗；
兩項各自保存輸出、管理子程序及清除私人案件環境變數。可以單獨選用 Contract 快速檢查，不必每次重跑全部 .NET 測試。

這項檢查會自動收集 `tools/tests/` 第一層所有 `frontend-*.test.cjs`，只有 `frontend-preview.test.cjs` 留給 FrontendPreview。
新增前端行為測試時照這個名稱命名，不必修改執行腳本。`tools/tests/` 底下如果有其他 `*.test.cjs`、`*.test.js`
或 `*.test.mjs` 沒有被任何路線執行，檢查會直接失敗。

依審計員使用情境寫的測試放在 `frontend-journey-<情境>.test.cjs`，例如 `frontend-journey-picker.test.cjs` 是案件清單，
`frontend-journey-export.test.cjs` 是第六步匯出。每個檔開頭的註解寫出審計員的操作步驟、使用的夾具，以及它取代的舊字串測試。
新的情境檔只要放在 `tools/tests/` 第一層並照這個名稱命名，下一次執行就會被自動收集。
兩個以上測試檔共用的夾具放在支援模組 `tools/tests/support/frontend-fixtures.cjs`，舊的 `frontend-workflow.test.cjs`
與 `frontend-mapping.test.cjs` 也使用同一份。只服務一個測試檔的夾具留在那個檔裡。夾具本身的測試是 `frontend-support.test.cjs`，
改了支援模組要在這裡補對應的測試。支援模組的檔名不含 `.test.`，Node 不會把它當成測試執行。

每個前端測試程序都要載入 `tools/tests/support/resource-guard.cjs`，直接載入或經由共用夾具載入都可以，
FrontendMapping 會檢查每個前端測試檔都有載入。它在背景每 100 毫秒檢查一次整個程序的記憶體與執行時間，
預設超過 2048 MB 或 900 秒就結束程序，並在錯誤輸出寫一行以 `JET test resource guard:` 開頭的說明。
上限可以用環境變數 `JET_TEST_MAX_RSS_MB` 與 `JET_TEST_MAX_SECONDS` 調整。2026-10-13 曾有測試失敗時，
報表沿著假 DOM 元件的父子連結不停展開，單一測試程序占用數十 GB 記憶體，整台電腦因此停擺。
共用夾具已把這些連結改成不可列舉，這個上限則讓同類問題不再拖垮整台電腦。

這個上限只管單一程序。`node --test` 預設依 CPU 核心數同時執行很多個測試檔，合計記憶體會跟著核心數放大，
所以 FrontendMapping 與改壞檢查工具一次最多執行 4 個測試檔，數值寫在 `tools/tests/support/resource-guard-marker.cjs`。
這樣合計最多 4 × 2048 MB，不超過驗證框架給整個步驟的 8192 MB。自己用 `node --test` 一次跑多個測試檔診斷時，
也加上 `--test-concurrency=4`；只跑一個測試檔時不必加。

前端測試的假後端要和正式後端一致。`src/JET/tests/JET.Tests/Application/ActionShapeJourney.cs` 照審計員的順序，
用正式的 dispatcher 走一遍合成案件，SQLite 與 DuckDB 各一次，記下每個 action 收到的資料與回應的欄位和型別，
寫在 `tools/tests/fixtures/action-shapes.json`。`ActionShapeContractTests` 每次重走一遍；兩種資料庫的形狀不一致，
或和檔案內容不同，都會失敗。前端測試的假後端都包在 `tools/tests/support/action-contract.cjs` 的 `contractJetApi` 裡：
假回應的欄位與型別要在正式回應出現過，前端送出的資料要是正式後端收過的，後端每次都收到的欄位不能少。
對不上時，那個測試會失敗，錯誤輸出寫出測試名稱與對不上的欄位。這裡只核對形狀，審計結果仍由各自算獨立答案的測試判定。

對不上時，先判斷是哪一種情況：

- 審計員真的會遇到、流程還沒走到的情況：在 `ActionShapeJourney` 補上那一步，再重新產生契約檔。
- 假資料用了正式後端不會回的欄位：照正式的形狀改假資料，並在旁邊註明原因，斷言不動。
- 正式後端會回、但流程穩定走不到的狀態：列進 `ActionShapeJourney.UnreachedKinds`，寫明產生它的程式與走不到的原因。
- 測試刻意假造正式後端不會回的資料，用來確認畫面的防呆：用 `unchecked('原因', 資料)` 包起來。

改了 action 的資料形狀後，用下面的命令重新產生契約檔，看過檔案差異符合預期，再跑一次 FrontendMapping：

```powershell
try {
    $env:JET_ACTION_SHAPES_WRITE = '1'
    pwsh -NoProfile -File tools/verify.ps1 -Command Focused -Filter 'ActionShapeContractTests'
} finally {
    Remove-Item Env:JET_ACTION_SHAPES_WRITE -ErrorAction SilentlyContinue
}
```

要確認新寫的前端測試真的抓得到錯，可以執行 `node tools/tests/support/mutate-frontend.cjs --spec <改壞清單.json>`。
它把正式前端複製到 `artifacts/frontend-mutation/<批次>/<編號>/`，在副本上做一處文字替換，再對副本執行指定的測試。
同一個測試在工作樹上要通過，改壞後要有原本通過的測試失敗，才算抓到。還在撰寫、尚未收進正式路線的 `*.wip.cjs`
也可以用這支工具檢查。同一時間只會跑一個改壞檢查，後來的會排隊等候。每次執行測試預設最多 300 秒，
逾時就連同它開出的子程序一起結束。工具本身不載入上面的資源上限，整份清單跑得再久也不會在中途被停掉；
它開出的每個測試程序仍各自受上限保護。任何一次執行碰到時間、輸出量或記憶體上限，結果都是 `inconclusive-resource-limit`；
這時要查出是產品還是測試停不下來，不能重跑了事。改壞檢查一律使用這支工具，不另寫腳本。
這支工具不改工作樹裡的產品檔，不是正式驗證命令，輸出只是診斷紀錄。改壞清單的格式寫在工具檔開頭。
它和下面用 Stryker 改壞 C# 規則的 `Mutation` 是兩回事。

- `tools/` 第一層只分成 `harness/` 與 `tests/`。新增的驗證框架子目錄一律使用小寫英文，單字之間以連字號分隔。
- JSON 設定檔、公開入口、測試與一次性 probe 使用小寫與連字號。可重用的 PowerShell 模組與內部 verifier
  使用 `Jet` 開頭的 PascalCase；C# 專案、原始碼檔、型別、namespace 和組件名稱也依 .NET 慣例使用
  PascalCase。這些都是檔案或程式識別名稱，不會成為 `tools/` 的新分類。
- C# 驅動程式仍屬於驗證框架，所以專案目錄要放在 `tools/harness/` 下，不能在 `tools/` 增加第三個分類。
- 驗證框架的主模組 `tools/harness/JetHarness.psm1` 只留主函式 `Invoke-JetHarness`。其餘函式依職責分在同目錄的
  `JetHarness.Foundation.ps1`、`JetHarness.Artifacts.ps1`、`JetHarness.Results.ps1`、`JetHarness.Steps.ps1`、
  `JetHarness.Preflight.ps1`、`JetHarness.Lanes.ps1` 與 `JetHarness.ReleaseCandidate.ps1`，每個檔開頭寫明內容。
  主模組用點號載入（dot-source）這些檔，函式仍在同一個模組裡執行。新增或改名這類檔案時，同步改主模組的載入清單與
  `Get-JetRunnerIdentity` 的檔案清單，收據才會記到它的雜湊；`tools/tests/verify-contract.tests.ps1` 會檢查兩份清單都涵蓋每個檔。
- `bin/`、`obj/`、`artifacts/` 和 `TestResults/` 都是本機產物，不屬於第一次提交內容。

## 日常資料庫測試與服務收尾

2026-09-02 使用者確認，SQL Server 開發目前暫緩，日常工作集中在 SQLite 和 DuckDB。依修改範圍選擇
`Focused` 或 `Public`；需要比較兩個本機資料庫時，可用 `Focused -Filter 'ProviderParity'`。
`Provider` 包含 live SQL Server 測試，只有使用者當次明示需要時才執行，也不為了補齊一般開發驗證而啟動。
`PrivateCase` 讀取本機私人案件的授權是常設的，不必每次再問；但選擇 SQL Server 不能從既有連線設定推定。

`Focused` 和 `Public` 已排除 `Profile=Provider`，一般測試子程序也會移除 `JET_SQLSERVER_CONNECTION`；
`ReleaseCandidate` 不包含 `Provider`。不需連線的 SQL 產生與契約測試仍保留，不按檔名整批移除 SQL Server
測試，也不把未執行的 live 驗證記成通過。

本機服務的啟停由執行開發工作的 Agent 或開發者負責；`tools/verify.ps1` 目前不會自動管理 Windows 服務。
每次開發測試結束時確認本機 SQL Server 沒有留在背景。明示授權使用本機 SQL Server 時，依下列方式收尾：

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

`Context` 是唯讀導覽，回傳 `observed`，不是測試通過。它從 `development-status.md` 的目前大型計畫連結
讀取現行摘要，列出程式、文件、工具及 Agent 轉接檔的工作樹變更，以及近期最多八組驗證的最新可讀收據。資料目錄不在
Git 路徑查詢範圍，亦不讀取案件內容。它不建立收據、不拿共享鎖、不改工作樹，也不自動啟動其他驗證。
缺少來源只列提示，其他資訊照常輸出。近期收據僅查看最新二十個執行目錄，不代表完整驗證歷史。
相同命令、組態、篩選及情境只列最近一次，避免重複文件檢查擠掉其他結果；最新失敗也保留，不退回較舊的通過。
輸出會說明截斷及未確認的部分；相同 HEAD 的既有收據仍不保證目前未提交內容已驗證。

計畫可用 `<!-- jet-context:start -->` 和 `<!-- jet-context:end -->` 標出簡短接續摘要；沒有標記則使用第一節，
最多回傳 7,000 字元並標明是否截斷。摘要分清本輪成果與產品工作順序，仍保存於原計畫，不另建狀態檔。
Context 不判定摘要是否完整或需求是否完成；範圍與來源仍由 Agent 核對。Context 的行為驗證使用
`pwsh -NoProfile -File tools/verify.ps1 -Command Contract -ContractScenario Context`。

`Build` 預設先執行 Restore，再以 `--no-restore` 進行建置。只有確定 NuGet 還原資產已存在時，才使用
`-NoRestore`。

`Focused` 的 `-Filter` 接受測試方法或類別的完整名稱片段，不接受萬用字元。它會先建置，再執行選取的測試，
並固定補跑架構檢查；選不到測試或遇到跳過都不會算通過。`Public` 不接受 `-Filter`，會依固定種子
跑完目前安全且不需外部服務的公開測試。這組測試至少要找到 3,000 個案例。
`Focused`、`Public` 與 `Package` 讓不同測試類別平行執行，執行緒數等於 CPU 執行緒數；檢查實際經過時間上限的
測試放在 `TimingSensitive` 集合，等其他測試結束後單獨執行。`Provider` 會共用同一個 SQL Server 測試資料庫，
`PrivateCase` 會處理私人案件副本，`Excel` 只有一個案例，這三條路線仍逐一執行。改為平行執行後，
`Public` 從約 1,000 秒縮短為約 180 秒。

同一個類別裡的測試會依序執行，耗時最長的類別常常是 `Public` 最後跑完的部分。內容相同的合成案件在一個測試程序裡
只用正式 action 建一次，之後每個測試把案件資料夾複製到自己的暫存目錄再開啟，做法在
`src/JET/tests/JET.Tests/Application/CaseTemplates.cs`。SQL Server 的資料在伺服器上，照舊從頭建案。
每個測試仍有自己的案件，可以任意寫入。要檢查建案者身分或建案過程本身的測試，例如進度事件、稽核紀錄與日誌，
呼叫 `DemoProjectPipeline.SetupAsync` 或 `Batch9ArtifactStateTests.PrepareAsync` 時加 `fromScratch: true`。
2026-10-15 依使用者的選擇改成這樣後，`Public` 的 .NET 測試從 260 到 310 秒縮短為 170 到 195 秒。

平行執行時，同時執行的類別數等於 CPU 執行緒數；一個類別跑完，才照順序放行下一個。xUnit 4.0 的預設順序依各類別的
UniqueID 排序。UniqueID 是 xUnit 由測試程式的絕對路徑與類別名稱算出的雜湊值，所以同一個 clone 路徑每次順序都相同；
固定種子不影響類別的先後。排在後面的長測試會在其他類別都跑完後獨自拖長整段測試，所以 `src/JET/tests/JET.Tests/LongRunningFirstCollectionOrderer.cs`
把 `ActionShapeContractTests` 排第一，其餘類別維持 xUnit 的預設順序。加上這個排序後，兩次 `Public` 的 .NET 測試是 156 秒與 168 秒。

`public-skip-policy.json` 目前沒有允許略過的群組；舊儲存層的檔案系統連結測試已退役，因此任何跳過都會
讓公開測試失敗。

### 需要時執行變異測試

`Mutation` 刻意改壞指定規則，檢查公開測試能否發現行為變化。它不會隨 `Public` 或 `ReleaseCandidate`
自動執行，結果只適用於所選範圍。固定使用 Release，預設範圍為 GlProjectionGuard：

```powershell
pwsh -NoProfile -File tools/verify.ps1 -Command Mutation -Configuration Release
pwsh -NoProfile -File tools/verify.ps1 -Command Mutation -Configuration Release -MutationScope MoneyScaling
```

每次上限 1,800 秒、同時一個工作程序，使用較低優先權。工具會先完成獨立的篩選與取消檢查，再建立產品檔案的工作樹副本。
檔案清單取自 Git：`global.json`、`src/JET` 底下已追蹤與未追蹤但未被忽略的檔案，以及 `src/JET` 的專案從 `tools/` 連結進來的原始碼；
已追蹤但在工作樹刪掉的檔案不放進來。所以包含尚未提交的修改，不掃描被忽略的本機設定或私人資料。
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

`artifacts/harness/mutation/` 的資料有保留期限，做法比照收據，是使用者 2026-10-15 的裁定；當時這個資料夾約 7 GB，沒有上限。
每個使用共享鎖的命令結束前整理一次。`Mutation` 也要拿這把鎖，所以整理時不會有進行中的改壞檢查。

- `r/` 每次執行一個資料夾，完整的約 1 GB，大部分是工作樹副本。同一個範圍只完整保留最新 2 次；更舊的只留最上層的摘要檔，
  工作樹副本、暫存與報告資料夾都刪掉。摘要裡的 `result.json` 已逐項記下每個變異的結果。中斷而沒有寫 `pipeline.json` 的執行不整理。
- `t/` 每個 Stryker 修補版本各有一份工具來源，只留最新一次執行用的那份。
- `c/http` 是 NuGet 的下載快取，整個刪掉。`c/packages` 保留，還原時不必連網；它只在套件版本改變時增加。

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
這兩組沒有從封裝選取條件中消失。清單以本次現行來源產生，不沿用舊專案留下的封裝基準。暫存封裝完成後會自動移除，只保留清單
與有限大小的紀錄。實際 publish 會占用較多 CPU 與磁碟，執行前應先確認本機負載。

`Gui` 固定使用 `AgentGuiTest` 組態。它會先建置一次，再分別用全新的暫存目錄執行下列情境。每個情境的
操作上限、預期次數、逾時與截圖數只寫在 `tools/harness/lanes.json`；驗證框架把預算交給驅動程式，再用同一份
數值核對收據。驅動程式與 `AgentGuiTest` 組態另有固定的最大值（操作 120 次、截圖 2 張），調高情境預算超過
這個值時要一起改。截圖都是合成畫面，每張不超過 2 MB。

從 2026-10-15 起，驅動程式為每個情境另建一個 Windows 桌面，用 `CreateProcess` 把 JET 開在那裡，情境結束時關閉
這個桌面（`OwnedGuiRun.cs`）。驅動程式不切換桌面，所以使用者的桌面上看不到 JET 視窗，點擊與打字也不會落到 JET 上。
要調整視窗大小時，驅動程式在掛到那個桌面的執行緒上，找 JET 唯一看得見、沒有擁有者的 WinForms 主視窗。
不用 `Process.MainWindowHandle`，因為那個桌面上 Windows 的輸入指示器小視窗也屬於 JET 程序，而且排在主視窗前面。
2026-10-15 依使用者裁定縮減後，完整執行 18 個情境約 4 分 54 秒。日常開發不跑這條路線，改用 `Public` 與
`Contract -ContractScenario FrontendMapping`；只在提交、交付或確認整合時執行。規則見 `AGENTS.md`「測試」。

- `startup-smoke`：檢查頁面、`JetApi`、`systemPing`、專案選擇畫面及離開按鈕，最後按下「結束 JET」。
- `synthetic-sqlite-create`：從可見畫面只以鍵盤輸入建立 SQLite 案件，案件編號與客戶名稱留白，再核對畫面
  上的案件名稱、本次暫存目錄中的 `project.json` 與 `jet.db`、兩個選填欄位保存為空白，以及操作人員是否為
  隔離測試帳號。
- `case-to-workpaper`：從空白 SQLite 案件開始，在畫面匯入兩張工作表的合成 GL 與一份 TB，首次確認兩種配對、
  執行驗證、儲存 G 情境並匯出，重開確認完成狀態。接著回頭修改上游資料，核對每一步清除與保留的狀態。
  用 Excel 匯入科目配對後，資料驗證保留，預篩選與已存情境清除，底稿標成過期。匯入假日後，資料驗證與科目配對都保留；
  再匯入補班日，資料驗證仍保留。附加一次 GL 後，GL 配對、資料驗證與科目配對都清除，畫面提醒確認是否重複匯入。
  最後取代 GL 與 TB，重新確認配對、驗證、儲存 G 情境並再匯出，兩份底稿紀錄中只有新的一份是現行版本。
  選檔回應由僅存在於 AgentGuiTest 的固定合成夾具提供，不開原生選檔視窗；匯入、配對、驗證與匯出都走正式 action。
- `mapping-required-sync`：從已提交的合成 mapping 進入「重新配對」，先把借方代碼改成另一個值，不離開
  輸入框就用滑鼠點一次「重新確認配對」，確認第一次點擊就完成配對；再清空並補回同一個必填欄位兩次，
  每次確認右側缺漏狀態、提交資格與焦點同步。原本十次，2026-10-15 依使用者選擇減為兩次。
- `edited-report-still-loads`：先在 JET 之外改寫已發布的 Working Paper 並放回舊版輸出紀錄檔，再從 Release
  可見介面開啟案件，確認能載入、第六步標示「已在 JET 之外修改」、匯出仍可用、支援日誌含
  `artifact.journal.discarded`，且過期與缺檔的歷史紀錄不被當成目前流程已完成。
- `approval-mapping-modes`：操作兩種配對畫面的核准日方式與來源欄，確認同步、未提交提示、取消後的政策
  重設、完整還原及缺漏欄位定位；還原結果以第一次編輯前保存的獨立快照核對。
- `validation-auto-outputs`：執行驗證，確認兩份報告與科目配對範本自動產生，再填入合成分類並重新驗證，
  核對已填範本逐位元組保留。
- `filter-auditor-journey`：走一遍審計員在第五步的路，從範本「借現金、貸非現金」開始，加條件、切換作用中
  的組、預覽、排序命中傳票、保存並核對讀回文字，也檢查多選日期、期末天數、錯誤標示、版面與草稿名稱。
- `filter-common-editing`：檢查用了常用篩選條件卡片的情境保存後重開案件能還原卡片，「非營業日之分錄」留在目前組的條件括號，
  2026-10-29 以前儲存的舊版情境重開時不還原卡片、保留原條件，重新儲存後改存第 2 版編輯來源，
  以及移除、取消、金額門檻、文字排除與自動命名的行為。逐張加入前五張卡核對預設動機、固定資產與費用那張卡加入的條件，
  2026-10-15 依使用者選擇改由前端點擊測試核對，這個情境不再重複。
- `feedback-workflow`：在合成案件驗證配對清單捲動、分類輸入、中文組字、取消、保存、返回與重開，再改配
  TB 金額欄建立完整性差異，確認仍可預篩選、匯出底稿、產生條件報告並在重開後保留差異。
- `null-details-recovery`：兩類空值明細各自跨頁、排序與搜尋，並檢查搜尋失敗保留、重試、零筆、返回及重開。
- `common-remap-recovery`：保存用了常用篩選條件卡片的情境後取消配對修改，情境仍在；移除必要欄位並確認配對後，已存情境清空，缺欄時情境存不進去；
  補回欄位、重新儲存、重新驗證、匯出及重開。
- `authorized-list-recovery`：手動選欄、取消、匯入失敗重試、預覽有效清單、移除、重開、再匯入及下游失效。
  檔案選擇 action 由封閉的合成來源替代，不代表操作了原生檔案選擇視窗。
- `extended-conditions`：人工與自動補集的取消及保存、統計張數、日曆、金額尾數、人員清單與文字否定，再
  保存五個情境、匯出及重開；精確條件和保存值都要相符。
- `side-month-workflow`：在窄視窗用子條件組與整張傳票條件組合借貸別和科目編號，並操作每月月初、月底天數，核對錯誤值修正、保存、取消
  編輯、返回、重開、條件報告及底稿匯出，並讀回 Step 4-1 的人工旗標，確認「人工」「自動」都以文字輸出。
- `nested-voucher-workflow`：操作分類上層設定、巢狀條件、整張傳票條件及分類選取方式，核對空條件失敗後
  重試、保存、取消編輯、返回、案件重開及兩種篩選報表匯出。
- `legacy-form-workflow`：從「常用篩選條件」分頁的情境範例套用由原工作簿轉入的四個範例，再到自訂分頁加入「依審計目的」的「金額尾數連續多個 0」，
  核對固定筆數、預覽、保存、取消修改、返回、兩種報表及重開。夾具只有一個文字型攸關資料元素欄位，
  所以部門範例核對自動帶入該欄位；切換「依審計目的」的分類不應清除已有預覽。
- 其他情境加入自訂條件時，改點欄位按鈕（`data-add-field`）、「依審計目的」的「加入」（`data-add-check`）或
  每組標頭的「加入子條件組」與「加入整張傳票條件」（`data-add-compound`），共用 `GuiDataRecoveryScenarios.cs` 的 `AddCustomConditionAsync`。

`null-details-recovery`、`common-remap-recovery`、`authorized-list-recovery` 與 `extended-conditions` 全部沿
正式前端及 action 操作，CDP 評估只讀取畫面與狀態。中文組字透過 WebView2 的
CDP 組字事件驗證，沒有變更 Windows 輸入法，也不代表已實測各家輸入法。`edited-report-still-loads` 的
定位入口實際路徑解析與缺檔錯誤由 handler 測試驗證，GUI 情境不開啟檔案總管。

兩個篩選情境都先使用 1024 × 640 邏輯像素視窗及 125% WebView 放大，依實際 DPI 換算，並核對網頁寬度為
760 至 840px。審計員情境完成窄版檢查後，只將該次測試擁有的視窗調整為 1560 × 1100 補拍雙欄版面，
網頁寬度另核對為 1200 至 1260px；不變更桌面設定或其他案件視窗。

只想診斷其中一個情境時，可加 `-GuiScenario <名稱>` 單跑；這種收據會標記 `partial`，不算 `Gui` 通過，
收尾前仍要完整跑一次 `Gui`。

驅動程式位於 `tools/harness/gui-driver/`。外部只能選擇上述固定情境，不能傳入 JavaScript、selector 或任意
動作；程式也不使用 Selenium、EdgeDriver 或網路下載。每個情境都必須由 JET 自行結束，並完成程序與暫存
目錄清理。這些結果不代表原生檔案對話框或私人案件已完成驗收。

`Excel` 固定使用 Release。它先執行產生五份合成報告與一份科目配對範本的現行測試，把同一輪產生的六份工作簿放進本次專屬暫存目錄，
再由 `tools/harness/excel-driver/` 逐份使用原生 Excel 開啟。每份工作簿都要以唯讀方式開啟、停用巨集與外部連結
更新、完整重算、另存副本、重新開啟副本，並匯出第一頁 PDF。框架另核對 V_Report 5 在原生 Excel 的表頭、中段與最後資料列均為 15 點。
框架會確認來源檔沒有改變、重算後公式錯誤沒有
增加、副本沒有外部連結，而且 PDF 結構可讀。

Excel 輸入由 `SixReportWorkflowJourneyTests.AccountMappingHandoff_PreservesValidationRun_AndPublishesFiveReportsAndTemplate`
產生。這個旅程先填寫範本原檔、匯回、重新開啟案件，再匯出兩份驗證報告與後續報告；範本保留填好的內容。
合成案件包含兩張傳票，以借貸分類、多日期、金額區間及排除條件核對預覽、報告文字及底稿數字。
「分類與指定日期」情境將分類留白的貸方科目視為 Others，兩張都命中；篩選相關報告不得出現「待判定」。
類別名稱與 `JET_SIX_REPORT_EVIDENCE_DIR` 為既有介面保留，六份工作簿只有五份進入報告清單。
原生 Excel 部分仍只驗證開啟、重算和另存副本；它沒有操作範本原檔填寫，也沒有測試 Excel 佔用中的再次匯出。
這兩項在公司環境的實際操作仍依現行計畫的人工驗收清單確認。

Excel 的合成來源檢查另由既有 Office 建立 BIFF8 .xls、.mdb 與 .accdb，各在 SQLite 與 DuckDB 完成預覽、
錯表重試、匯入、配對、驗證、篩選、重開及原檔不變檢查，共六例；結果保存在收據的
`excelFixture.nativeSourceImports`。此檢查只使用自行產生的資料，結果限於實際執行的 Office 環境；六份
報表的原生 Excel 往返檢查仍獨立保留。

Excel 驅動程式不接受任意工作簿路徑，只能選擇設定檔中的六種合成工作簿（五份報告加科目配對範本）。它可在使用者已開啟 Excel 時執行，
但只會透過 Excel 視窗代碼、PID 與啟動時間管理本次建立的 Excel 行程。正常情況先要求 Excel 結束；若行程沒有
及時退出，後備清理也只能終止已確認屬於本次的 PID。Excel 不可用、工作簿無法開啟或無法證明行程歸屬時，
結果為 `blocked`。這條路線不讀取 `data/test-case/`，也不能代替私人 JE 與 TB 案件驗收。

`PrivateCase` 是唯一可讀取私人案件的命令，而且每次都要明示提供完整根目錄、根目錄內的案件清單相對路徑
及資料庫實作。三項設定缺一時，命令會在建置和資料存取前回報 `blocked`。正式流程會建立安全副本，再用
JET 走完匯入、驗證、篩選、五份報告與科目配對範本輸出。六份工作簿同時比較內容與版面；INF 的 59 筆樣本則檢查抽樣規則、
實際母體與樣本有效性，不要求重現舊 IDEA 的隨機列。每次執行建立的私人副本與輸出一律在收尾時清除，
成功與失敗都不保留。收據只保存去識別化的狀態、數量與差異類型。

`ReleaseCandidate` 固定使用 Release，不要求 live SQL Server。候選內容直接取自 Git：已有提交時是全部已追蹤的檔案，
而且工作樹必須乾淨；第一次根提交前則是尚未被忽略的檔案。候選裡若出現私人資料目錄的路徑會直接失敗。
框架把這些檔案複製到本次專屬的一次性快照；建立前後會比較來源儲存庫的 Git index entries，不能只由執行器宣告未修改。快照內依序執行
Contract、Documentation、Public、Package、Gui 與 Excel，任何一步不通過就停止。`Provider` 與
`PrivateCase` 都不在這條路線內，也不會因本機存在連線或
`data/test-case/` 就自動讀取；需要真實案件時仍要另行明示執行。為避免原生 Excel 無法開啟過深路徑，快照
使用 `artifacts/harness/rc/<短代碼>/s`；這個目錄仍有本次執行的所有權標記，且和子執行資料一樣在收尾時
一律清除。

Restore、Build 和 Documentation 不使用共享鎖；其餘正式命令都會使用共享鎖。
`Contract` 只檢查驗證框架本身的鎖、子程序、逾時、輸出大小、JSON 紀錄及安全清理；它通過不代表產品測試
已通過。

2026-10-13 起，建置、發布、.NET 測試與前端測試都在本次專屬的 Windows Job 裡以較低優先權執行。整個程序樹合計的
已認可記憶體超過 `lanes.json` 的 `limits.maximumProcessTreeMemoryMegabytes`（目前 8192 MB）時，框架立刻結束整個程序樹，
該步驟判定為 `failed`，原因碼是 `memory_limit_reached`。收據的 `process` 另外記下上限、尖峰記憶體與是否碰到上限。
建置與發布另加 `--disable-build-servers`，編譯伺服器與重複使用的 MSBuild 節點不會留在背景。
Restore、Gui 與 Excel 驅動程式自己管理的程序，以及 `Mutation` 的程序樹，不在這個上限內。

`Documentation` 會檢查目前仍有效的文件與 AI Agent 轉接檔。缺檔、必要指向缺漏及已確認過時的事實會
失敗；不自然用語只列為 warning。`documentation-check.json` 的 `styleWarnings` 比對固定片語，
`stylePatterns` 用正規表示式抓符號代句與工作用語，命中時附一句修正提示，程式碼區塊與行內程式碼不檢查。
這個命令不會替人判斷文章品質；執行通過後，仍要把本次改動的段落完整讀一次。

架構文件另由 [architecture/](harness/architecture/README.md)檢查固定圖表包的雜湊、來源版本與 17 組類別關係，
並執行八個小型合成案例。結果寫在文件報告的 `architecture` 欄位；不重新繪圖、索引或執行產品。
這項檢查不代表十八張圖的全部連線及業務說明都已受自動檢查保護。

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

每次命令寫好收據後，框架會整理 `artifacts/harness/runs/` 的舊執行。同一種檢查只完整保留最新 10 次執行；
命令相同時，只跑單一情境或篩選的執行另外計算。更舊的執行只留下 `receipt.json`、各步驟的 `*.receipt.json` 與擁有者標記，
TRX、記錄檔、截圖與暫存都會刪掉，所以文件或測試註解引用的舊收據仍讀得到結論與第一個失敗。
還沒寫好收據的執行不會被碰到，整理失敗也不影響這次的結果。這是使用者 2026-10-14 裁定的保留期限，
當時本機累積 3,226 次執行、2.9 GB，整理後約 225 MB。改壞檢查放在 `artifacts/harness/mutation/` 的資料另有保留期限，
寫在「需要時執行變異測試」一節。

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
