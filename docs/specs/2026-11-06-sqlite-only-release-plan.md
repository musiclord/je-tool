# 短期上線只保留 SQLite 計畫

日期：2026-11-06。

狀態：產品程式、測試、驗證框架與文件都已改完。完整重新編譯沒有警告，`Public`、`FrontendMapping`、`Gui`、`Documentation`
與驗證框架的自我檢查都通過，獨立複審找到的缺口已修正。使用者把裁定總表的上限調到 2 MB，新取的名稱照用。待使用者驗收。

<!-- jet-context:start -->
短期目標這個月上線，只支援 SQLite（裁定 2026-11-06-08）。DuckDB 與 SQL Server 的產品程式、測試、套件與驗證路線已刪除，
最後的完整版本是提交 `7e88b7b`；以前建立的 DuckDB 與 SQL Server 案件不列在案件清單，資料夾也不動（裁定 2026-11-06-09）。
同一份計畫也做了使用者追加的三件事：「核准日不在期間」的明細加「傳票核准日」欄（裁定 2026-11-06-13），
兩份檔案欄位資訊工作表拿掉 V.2019（裁定 2026-11-06-11），以及來源檔正被 Excel 開著時照樣匯入並提醒（裁定 2026-11-06-12）。
程式、測試、驗證框架與文件已改完，完整驗證與獨立複審都做過，複審找到的缺口已修正。
使用者把裁定總表的上限調到 2 MB（裁定 2026-11-06-14），新取的名稱與兩句畫面文字照用（裁定 2026-11-06-15）。待使用者驗收。
<!-- jet-context:end -->

## 使用者原話

2026-11-06：

> 接下來請繼續調整，因為決策關係，目前短期目標中，只支援使用 sqlite 資料庫，因此我需要你 **暫時移除** sql server 和 duckdb 的部分，考慮到先前有指示架構設計需要是模組化，因此，應該能快速的移除 sql server 及 duckdb 的模組，而我說的暫時移除是因為短期目標正式上線之後，就要回去開發 duckdb 和 sql server 的部分了，只不過這個月要先上線了(所以才需要準備SAT文件)，請先把 duckdb 跟 sql server 的代碼隱藏、屏蔽、移除。確保 `je-tool` 對使用者只能使用 sqlite 操作本地案件，且支援案件目錄在不同設備間的轉移，最重要的是確保目前功能都不受影響。

2026-11-06，同一輪追加：

> 我再新增要修改的事項:
>
> * 在 `資料驗證的核准日不在期間` 的預覽資料頁面中，多加一個 `核准日期` 的欄位，這樣檢視資料時就知道具體不去期間的資料日期
> * 需要修改範本文件，將底稿文件中的 `檔案欄位資訊` 工作表的 `v2019` 移除掉，那個不需要

2026-11-06，同一輪再追加：

> 我再新增有問題需要修復的事項:
>
> * 當我在 vscode 中啟用偵錯來執行 `je-tool` 測試時，當我在科目配對的 `用 Excel 配對` 來選擇外部檔案要匯入時，我選擇的檔案就是我正在開啟中的 xlsx 檔案，這時會跳出錯誤於 `OpenXmlSaxTableReader.cs` 那裡，而如果我直接使用 publish 的 .exe 來測試該情境時，則會直接什麼都沒發生就跳回程式視窗，這部分是不是應該要有預防機制? 或是提示使用者相關問題，比如 "請關閉正在使用中的檔案" 等等。

## 使用者的決定

2026-11-06 在對話視窗選答三次，前兩次各三題、第三次兩題。題目照錄，選項只錄使用者選的那一個與它的說明；每題對應的裁定編號寫在最後。

第一次：

- 「DuckDB 與 SQL Server 要怎麼「暫時移除」？你先前裁定 SQLite 為主、DuckDB 為輔，SQL Server 已封存；今天也驗收了用 DuckDB 走完整流程的 Gui 情境。」＝刪除，留還原點 (Recommended)。選項說明：「刪掉兩者的產品程式、測試、套件與驗證路線。執行檔不再帶 DuckDB 原生元件（約 36 MB）和 SQL Server 驅動，SAT 要列的套件與要審的程式都變少。最後完整版本是提交 7e88b7b，日後還原這次的移除即可接回；這段期間共用程式的改動要手動合併。」裁定 2026-11-06-08。
- 「這台或測試環境已建好的 DuckDB 案件，以及 SQL Server 案件留在本機的資料夾，移除後在案件清單上怎麼呈現？你先前說過正式上線前的舊案件可以刪掉重建；資料庫檔遺失的案件目前是列成錯誤列。」＝不列出，資料夾不動。選項說明：「案件清單看不到這些案件，資料夾留在原處，審計員要自己到資料夾刪除。」裁定 2026-11-06-09。
- 「開發現況一次只放一份「目前大型計畫」，現在放的是另一個 session 正在處理的 SAT 文件計畫。這份移除計畫要怎麼放？」＝移除計畫優先 (Recommended)。選項說明：「移除計畫改成目前大型計畫；SAT 文件計畫移到「已結束、待使用者驗收」。移除完成後，SAT 文件的套件清單與架構說明再回頭同步。」裁定 2026-11-06-10。

第二次：

- 「「用 Excel 配對」選到 Excel 正開著的檔案時，JET 讀 .xlsx 的方式只允許別人讀，所以開檔失敗；後端其實有回「檔案正被其他程式開啟（例如 Excel），請關閉該檔案後重試」，但這句只出現在收合的訊息面板和步驟標題下方，離托盤很遠，所以看起來像什麼都沒發生（VS Code 停在 OpenXmlSaxTableReader 是偵錯器在例外丟出時暫停，不是程式當掉）。讀 .xls 的程式已經允許在 Excel 開著時讀取。要怎麼處理？」＝照樣讀取並提醒 (Recommended)。選項說明：「Excel 開著也能匯入，讀的是最後一次存檔的內容；偵測到檔案正被開著時，在結果旁加一則「需要注意」：尚未存檔的修改不會匯入。GL、TB、科目配對、授權名單、假日檔等所有選檔匯入一起改。」裁定 2026-11-06-12。
- 「V.2019 寫在「自動化工具-檔案欄位資訊」工作表的 A1，底稿和資料驗證報告各有一份同樣的工作表（2026-10-29-07 的裁定把兩份一起定字級）。你這次點名底稿，資料驗證報告的那份要一起拿掉嗎？」＝兩份都拿掉 (Recommended)。選項說明：「底稿與資料驗證報告的檔案欄位資訊頁都不再寫 V.2019，第一列只留隱藏的配對資料；底稿原本藍底紅字的版本列一併取消。」裁定 2026-11-06-11。
- 「「核准日不在期間」明細新增的那一欄要叫什麼？名詞表把這個欄位定為「傳票核准日」，畫面其他地方（例如「依傳票核准日」）也都用這個名字；你的需求寫的是「核准日期」。」＝傳票核准日 (Recommended)。選項說明：「照名詞表，和畫面其他地方一致，接在「總帳日期」後面。」裁定 2026-11-06-13。

第三次，完整驗證與獨立複審之後：

- 「裁定總表 docs/rulings.md 加了今天六則裁定後是 330,108 位元組，超過你 2026-11-02 定的上限 320 KiB（327,680 位元組，裁定 2026-11-02-04「裁定總表的上限調成 320 KB」）約 2.4 KB。提交前的版本已用到 99%，最近三天每天約多 20 KB，所以只壓縮今天的六則也補不回來。文件檢查因此失敗，驗證框架的自我檢查也停在這一關（前面 243 個斷言都通過）。要怎麼處理？」＝上限調到 2MB。這是使用者自己輸入的答案，不是列出的選項。裁定 2026-11-06-14。
- 「這次新取的兩句畫面文字要照用嗎？一是匯入時來源檔被 Excel 開著的提醒：「檔案「檔名」正被其他程式開啟（例如 Excel）。JET 匯入的是這個檔案最後一次存檔的內容，尚未存檔的修改沒有匯入。」二是建案名稱撞到不列在清單上的舊 DuckDB 或 SQL Server 案件時：「案件名稱「名稱」已被以前建立的 DuckDB 或 SQL Server 案件使用，這類案件不列在案件清單。請改用其他名稱，或到 JET 的案件位置刪除這個案件資料夾後再建立。」程式與測試的新名稱列在計畫檔「待使用者確認」一節。」＝照用 (Recommended)。選項說明：「兩句與計畫檔列的程式、測試名稱都照用；名詞表與計畫的待確認標記改成已確認，記一則裁定。」裁定 2026-11-06-15。

## 目標

- 審計員只能用 SQLite 建立與操作本機案件，畫面與後端都沒有其他資料庫的入口。
- 案件資料夾在 JET 正常離開案件後整份複製到另一台電腦，仍能列出、開啟並接續作業。
- 現有功能的結果不變；短期目標上線後，DuckDB 與 SQL Server 能從記錄好的版本接回來繼續開發。

## 不做的事

- 不改審計規則、報告內容與 SQLite 的資料庫結構，不升資料庫版本。
- 不改 SAT 文件與架構圖；這兩項由 SAT 文件計畫負責，移除完成後再同步套件清單與架構說明。

## 審計員會看到的改變

- 建立案件時不再選資料儲存方式，一律建立 SQLite 案件。
- 案件清單不再有線上資料庫區段。
- 這台電腦上以前建立的 DuckDB 案件，以及 SQL Server 案件留在本機的資料夾，不出現在案件清單，JET 也不動這些資料夾。
  審計員要刪除時自己到案件根目錄刪除資料夾。
- 第四步「核准日不在期間」的明細多一欄「傳票核准日」，接在「總帳日期」後面，可以依它排序。
- 底稿與資料驗證報告的「自動化工具-檔案欄位資訊」工作表不再寫 V.2019，底稿也不再有藍底紅字的版本列。
- 選檔匯入時，來源檔正被 Excel 開著也能匯入，結果旁提醒「檔案「檔名」正被其他程式開啟（例如 Excel）。JET 匯入的是這個檔案最後一次存檔的內容，尚未存檔的修改沒有匯入。」

## 移除前的盤點

- DuckDB：產品程式有 5 個專屬檔，另有約 10 個共用檔帶少量分支；測試有 469 列 `InlineData("duckdb")`，
  分布在 159 個檔，另有 9 個 DuckDB 專屬測試檔；Gui 有一個用 DuckDB 走完整流程的情境。
- SQL Server：產品程式有 65 個專屬檔，接到案件清單、建案、開案、刪案、帳號辨識與國定假日等處理器；
  測試約 30 個專屬檔，需要連線的部分平常就略過；驗證框架有 `Provider` 路線。
- 套件：DuckDB 原生元件約 36 MB；SQL Server 驅動連帶 13 個套件。
- 案件搬移：現有測試已涵蓋 SQLite 案件資料夾冷複製到新根目錄後續作完整流程，使用者 2026-09-17 已驗收冷複製（裁定 2026-09-17-02）。

## 還原點

DuckDB 與 SQL Server 的最後完整版本是提交 `7e88b7b`。短期目標上線後回來開發時，從這個版本取回兩個模組的程式、測試、
套件與驗證路線；這段期間共用程式的改動要手動合併。

## 做了什麼

### 產品程式

- 刪除 DuckDB 的 5 個檔、SQL Server 的 65 個檔，以及只給 SQL Server 用的 12 個檔，例如線上案件登記、使用者目錄、
  啟動時的 SQL Server 檢查、`system.databaseInfo` 的處理器與 `appsettings.json`。
- 移除套件 `DuckDB.NET.Data.Full`、`Microsoft.Data.SqlClient`、`Microsoft.Extensions.Configuration` 與 `Microsoft.Extensions.Configuration.Json`。
- 資料庫組只剩 SQLite 一組（`ProjectRepositoryCatalog`），建案只建 SQLite 案件。畫面不送 `databaseProvider`，送了也只接受 `sqlite`。
- 拿掉四個 action：同步線上案件的 `project.list`、線上案件的心跳 `project.heartbeat`、查 SQL Server 設定的 `system.databaseInfo`，
  以及只在 Debug 組建出現、核對 SQL Server 案件登記與資料夾的開發工具 `dev.db.reconcile`。
  `project.load` 不再回 `heartbeatSeconds`，`system.whoAmI` 只回 `principal` 與 `shortName`。
- 案件清單只列 SQLite 案件。`project.json` 記錄其他種類的案件，以及沒有記錄種類、資料夾裡只有 `jet.duckdb` 的案件不列出，資料夾也不動；
  讀不出來又判斷不了種類的案件，照舊列成無法開啟。建案的名稱撞到這類資料夾時，訊息說明名稱被以前的案件佔用、
  請改名或自己刪除資料夾，不再只說「已存在」或報那個看不到的案件的設定檔錯誤（獨立複審找到的缺口）。
- 「核准日不在期間」明細加「傳票核准日」欄；`validate.run`、`query.blankRecordsPage` 與 `project.load` 帶回的空值明細，每列多 `approvalDate`。
- 兩份檔案欄位資訊工作表的 A1 不再寫 V.2019，底稿的版本列取消。
- 讀 Excel 與文字檔的來源時允許其他程式同時開著（`SourceFileAccess.OpenRead`）；原本只有讀 .xls 的程式允許。偵測到檔案正被其他程式開著時（`SourceFileAccess.IsOpenElsewhere`），
  GL、TB、科目配對、授權編製人員清單、國定假日與補班日的匯入結果多一則提醒；`import.authorizedPreparer.fromFile`、
  `import.holiday.fromFile` 與 `import.makeupDay.fromFile` 的回應因此新增 `warnings`。讀配對草稿的 `mapping.restoreDraft`
  也能讀開著的檔，但它只讀隱藏的配對資料，不加提醒。
- 開著檔案的提醒放在匯入結果旁。第一版只記進收合的訊息面板，和使用者選的「在結果旁加一則需要注意」不符，文件重讀時發現後補上：
  六個匯入的回應另列 `openElsewhereWarnings`，畫面依它在 GL、TB、授權編製人員清單、日曆卡片與「用 Excel 配對」托盤的結果旁畫琥珀色提示；
  `warnings` 照舊全部記進訊息面板。同一項再匯入成功時換成這次的提醒，移除清單或換案件時拿掉。
- 說明為什麼 SQL 要寫得能搬到 DuckDB 或 SQL Server 的註解保留，留給日後恢復時用；`ISqlDialect`、`ILocalProjectDatabase`
  與 `IBulkRowWriter` 的說明註明目前只有 SQLite。描述已不存在的實作、路徑或呼叫點的註解已改掉。

### 測試

- 刪除 44 個 C# 測試檔與 4 個前端測試，測的都是 DuckDB、SQL Server 或跟著它們移除的功能，其中 `DuckDbTargetWriteGuardTests`
  改名留下；參數化測試只留 `sqlite`。清單見下一節。
- 新增 `SourceFileOpenElsewhereTests`：開著的檔照樣讀得到最後存檔的內容；只有別的程式能寫入時才算開著；GL 兩個來源只有開著的那個提醒；
  科目配對、授權編製人員清單、國定假日與補班日照樣匯入並提醒；GL 追加一個又開著又重複的來源時，兩則提醒都在 `warnings`，
  `openElsewhereWarnings` 只有開著檔案的那一則。
- 新增三個前端測試，確認開著檔案的提醒畫在結果旁、訊息面板照舊另記，下一次乾淨的匯入後拿掉：GL 卡片、日曆卡片與「用 Excel 配對」托盤各一個。
- `MappingRestoreDraftHandlerTests` 加一項，確認拿掉 V.2019 後欄位資訊頁仍帶著配對資料；`ProjectListLocalFirstTests` 加一項，
  確認以前的 DuckDB 與 SQL Server 案件不列出、資料夾不動，讀不出來的其他案件照舊列出；另加一項，確認建案撞到這類名稱時的訊息，
  資料夾照樣不動；`ApprovalDateOutOfPeriodBlankTests`
  加一項，確認每列帶出傳票核准日並能依它排序。
- `DuckDbTargetWriteGuardTests` 改名 `LocalTargetWriteGuardTests`，改檢查 `Sqlite` 與 `Local` 兩個資料夾，偵測規則照舊。
- action 形狀契約檔重新產生，只走 SQLite 一次。

### 驗證框架與工具

- 拿掉 `Provider` 命令與路線、`provider-skip-policy.json`、SQL Server 連線的執行前檢查，以及 `JET_SQLSERVER_CONNECTION` 的清除設定；
  框架的契約版本從 8.2 改成 9.0。
- `PrivateCase` 的資料庫實作只接受 `sqlite`。
- Gui 拿掉 `case-to-working-paper-duckdb`，剩 19 個情境；建案的情境不再等待與核對資料庫選項。
- Excel 路線的原生來源匯入只核對 SQLite 的三個案例。
- 瀏覽器主機的台灣中小企業自訂情境案件改建成 SQLite，名稱改為「台灣中小企業-自訂情境（SQLite）」；既有的主機資料夾要用 `--reset` 重建，
  舊的 DuckDB 案件才會換掉。
- 開工導覽只列現有命令的收據，舊的 `Provider` 收據不再列出。
- 變異測試工具在有固定雜湊的修補裡排除 `TestProfile=Provider`；已經沒有測試帶這個標記，結果不受影響，這次不動。

### 文件

名詞表、裁定總表、action 契約、系統規格、前端說明、專案方向、`AGENTS.md`、`tools/README.md` 與開發現況照新的範圍更新。

## 改動既有測試的紀錄

每項寫原因與第一次失敗的收據，程式註解也寫了同一份收據。

只保留 SQLite（裁定 2026-11-06-08）：

- `LegacyAuditParityControlTests.Parse_ProvidesBoundedProviderCheckpointAndRetentionControls`：不指定資料庫時的種類數 3 改 1。
  Focused 收據 20261106-084027187-45312a25ca8043b8a0f4152099dca2a0。
- 架構檢查 13 項拿掉已刪除的 SQL Server 檔案與型別：`CompatibilityPathOwnershipTests`、`GlMappedColumnAuditOwnershipTests`
  （方法改名 `GlBlankColumnProbe_IsBuiltFromTheFieldCatalog_AndUsedByTheGlRepository`）、`PrescreenRuleRegistryTests`
  （方法改名 `Compilation_AndThePrescreenRepository_TakePredicatesFromTheRegisteredMap`）、`IntakeMappingOwnershipTests` 兩項、
  `GlQueryOwnershipTests` 兩項、`DataPreviewColumnOwnershipTests`、`HandlerRepositoryScopeTests` 的心跳列、
  `GlExcludedPopulationReadGuardTests` 兩項、`TestEvidencePolicyTests` 與 `LayerDependencyTests`。
  Focused 收據 20261106-084107421-33f747f01c134f3ab499d047fcc1c3ed；`IntakeMappingOwnershipTests` 的第三個型別 `AccountMappingFailureStyle`
  是 Focused 收據 20261106-085055547-a2c9a7aec0f442e09a6e43dd7e32b968。
- `ActionContractTests`：`project.load` 拿掉 `heartbeatSeconds`；原本鎖 `project.list` 的測試改鎖 `project.listLocal`，
  方法改名 `ProjectListLocal_ResponseShape_IsLocked`；`system.whoAmI` 四個鍵改兩個。Public 收據 20261106-085246006-0b8a050479f1422b8d8ffc9fcd54c83b。
- `ProjectStorageDirectCutoverTests`：清單頂層只剩 `projects`。同上收據。
- `ProviderParityJourneyTests`：SQLite 對 DuckDB 改成兩個各自建立的 SQLite 案件，方法改名 `InfSampleMemberSequence_FixedSeed_IsTheSameInTwoSeparateCases`。同上收據。
- `SupportCaseHistoryTests.SwitchingCases_KeepsEachCaseHistorySeparate`：第二個案件改成 SQLite。同上收據。
- `ActionShapeContractTests` 的契約檔重新產生：拿掉 `project.list`、`project.heartbeat`、`system.databaseInfo`、建案與開案的 `databaseProvider`、
  `heartbeatSeconds` 與 `system.whoAmI` 的兩個欄位；三個匯入多 `warnings`；空值明細多 `approvalDate`。
  第一次失敗是 Focused 收據 20261106-084150657-c05d09a2b1bb4d639f2e79d1a56c2648。之後六個匯入多 `openElsewhereWarnings`，
  再重新產生一次，第一次失敗是 Focused 收據 20261106-102248064-b2aa94d9387d405d9f63f7f1c4a00db6。
- 錄形狀的 `ActionShapeJourney`：匯出後回頭修改上游的那一段，國定假日、補班日、授權編製人員清單、加入的總帳、試算表與科目配對的再匯入，
  改成在 Excel 的開檔方式佔住來源檔時送出，匯入結果不變。原本這幾次的提醒都是空陣列，前端測試給有內容的提醒時對不上契約，
  第一次失敗是 FrontendMapping 收據 20261106-102438642-40eab56cacd64595943f7463210c1bf1 的三個新測試。
- 驗證框架自我檢查 `verify-contract.tests.ps1`：路線清單、Help 列出的路線、ReleaseCandidate 不再有 `liveProviderIncluded`、契約版本改 9.0；
  原本核對 Provider 缺少連線時停在執行前檢查的情境，改成確認 `-Command Provider` 被當成未知命令擋下。
  第一次失敗是自我檢查 20261106-092713325-5c033e8458a846358846bbe6d1804784。
- Gui 夾具行為測試 `AgentGuiTestFixtureBehaviorTests`：原本的 `ProjectListFixtures_ConcurrentCallsKeepDelayAndFailureOnFirstInvocation`
  驗線上案件清單的延遲與失敗夾具，這兩個夾具隨 `project.list` 刪掉，改成驗還在的本機清單失敗夾具，方法改名
  `ProjectListLocalFixture_ConcurrentCallsKeepFailureOnFirstInvocation`；驗假線上登記的 `FakeOnlineRegistry_ExistsIsClosedAndNeverCallsRealRegistry`
  跟著它的夾具刪除。這三個夾具 Gui 情境都沒有用到。Gui 前置測試的下限 `guiFixtureMinimumExpectedTests` 因此從 32 改成 31，
  第一次失敗是 Gui 收據 20261106-103607193-bad85928c5c34b34945ba7ef5df73d0d（執行 31 個、少於下限）。改測試時舊寫法已經編譯不過，沒有那次的收據。
- Gui 的 `synthetic-sqlite-create` 不再核對資料儲存方式預設選 SQLite（收據欄位 `sqliteSelected`），`case-to-working-paper`
  也不再先核對這個預設值；案件是 SQLite 仍由 `jet.db` 與 `project.json` 核對。Excel 路線原生匯入的案例數 6 改 3。
  這兩條路線改完後還沒執行，沒有第一次失敗的收據；建案表單已經沒有這一欄，舊的核對不可能通過。

舊資料庫的案件不列出（裁定 2026-11-06-09）：

- `ProjectListLocalFirstTests.ProjectListLocal_ReturnsOnlyLocalProvidersWithBoundedWireShape`：DuckDB 案件不再列出，兩筆改一筆。
  Focused 收據 20261106-084017183-285ee971d2ad44c3b0f22bb9c5f69f21。

拿掉 V.2019 與版本列（裁定 2026-11-06-11）：

- `ValidationDirectTemplatePackageTests` 與 `WorkingPaperWriterCoverAndReferenceSheetTests`：A1 的 V.2019 改成空白。
  Focused 收據 20261106-084035819-12cef79878a44a3198bc08e4445b76cb 與 20261106-084046692-14d369b348a74677801bd994c8d906b6。
- `ReportWorkbookPresentationTests.ValidationFieldInfo_UsesTheSameBorderlessVisibleGridAsWorkingPaper`：兩份都沒有 A1 的樣式，拿掉藍底的比對。
  Focused 收據 20261106-084908758-24fb2c6e3edb4280be91c6f9b2febd97。
- 外觀登記 `legacy-appearance-registry.json`：底稿版本列的五項改記成沒有對應，寫明原因。`WorkingPaperWriterAppearanceTests` 的方法改名
  `RegisteredAppearances_HaveNoCellOrColumnMismatch`，只比對其餘 38 項，並確認沒有對應的恰好是這五項。Public 收據 20261106-085246006-0b8a050479f1422b8d8ffc9fcd54c83b。
- `LegacyAppearanceRegistryTests`：拿掉 A1 的對照，另確認這五項沒有對應。Focused 收據 20261106-090058591-fdb42007ccca4a8e9903e179017b508d。

空值明細多傳票核准日（裁定 2026-11-06-13）：

- `ActionContractTests` 的 `validate.run` 明細與 `query.blankRecordsPage` 多 `approvalDate`。Public 收據 20261106-085246006-0b8a050479f1422b8d8ffc9fcd54c83b。
- `ValidationRunLoggingTests`：SQLite 的 SQL 指紋更新。把第 10 條命令換回舊字後，指紋精確回到舊值 `F5A37597…`。同上收據。
- `GlExcludedPopulationReadGuardTests`：本機驗證倉儲用來認查詢的片段改成 `approval_date FROM gl WHERE ({anyMatch})`。
  Focused 收據 20261106-084107421-33f747f01c134f3ab499d047fcc1c3ed。

前端測試，第一次失敗是 Public 收據 20261106-090143893-4f1fd1506e9b48cdbb57e5b6bab20337 的前端步驟：

- `support/frontend-fixtures.cjs` 的 `bootFullApp`：啟動不再回 `system.databaseInfo`，`system.whoAmI` 只回兩個欄位；`frontend-support.test.cjs` 的啟動順序改成三個請求。
- `frontend-journey-picker`：第一個測試拿掉線上同步，改名「JET starts from the local list only, and refreshing asks only for the local list again」；
  第二個改成只剩本機一區，改名「the case list has only the local section and the title has no sentence restating it」；第三個的同步失敗改成重新整理失敗，
  改名「a failed refresh keeps the local rows, puts the reason above the list as an alert, and the next refresh clears it at once」，失敗句逐字比對
  「讀取案件清單失敗：合成：清單讀不出來（耗時 2.5 秒）。」；刪除後不再檢查 `project.list`；開案請求只帶 `projectId`；乙案的假資料改成 SQLite，線上丙案拿掉。
- `frontend-journey-case-setup`：建案資料不帶 `databaseProvider`；表單標籤不再有「資料儲存方式」，改成確認沒有這一欄；假資料的 DuckDB 改成 SQLite。
- `frontend-journey-session`：SQL Server 心跳的測試改成「opening a case starts no background timer, and the case list sends only the case name」。
- `frontend-journey-transport`：第二個請求 `project.list` 改成 `project.listLocal`。
- `frontend-journey-operations`：整理用的請求 `system.databaseInfo` 改成 `system.whoAmI`；取消的測試拿掉線上同步，改名
  「opening a case can be cancelled with the request that is actually running」，背景請求改讀訊息紀錄；複製訊息的案件從 DuckDB 改成 SQLite。
- `frontend-workflow`：刪除預覽拿掉 SQL Server 的說明，改名「cancelled or replaced delete preview responses cannot overwrite the new target」；
  建案啟動的夾具不再切出 `databaseInfo`；線上狀態的測試改成只確認清單重繪，改名「project picker still refreshes its rows after startup-only notifications」。
- `frontend-preview.test.cjs` 與 `frontend-preview/replay.js`：預覽的固定回應拿掉 `databaseInfo` 與心跳，`system.whoAmI` 只回兩個欄位。

刪除的測試：

- DuckDB：`DuckDbOperationRetentionTests`、`DuckDbProviderTests`、`ResultInvalidationMatrixDuckDbTests`、`DuckDbDatabaseRetentionTests`、
  `DuckDbParameterAdapterTests`；`DuckDbTargetWriteGuardTests` 改名，見上一節。
- SQL Server：`ResultInvalidationSqlServerTests`、`SqlServerProjectPeriodValidationTests`、`SqlServerStartupOrchestrationTests`、`SqlServerAppConfigStoreTests`、
  `SqlServerAssumeDatabaseExistsTests`、`SqlServerCollationProbeTests`、`SqlServerCommandFactoryTests`、`SqlServerConnectionProbeTests`、
  `SqlServerControlPlaneMigrationTests`、`SqlServerDataPreviewRepositoryTests`、`SqlServerDatabaseInfoCapacityTests`、`SqlServerDevDatabaseInspectorTests`、
  `SqlServerEngineErrorsTests`、`SqlServerFact`、`SqlServerFieldInfoTests`、`SqlServerGlRepositoryTests`、`SqlServerHealthCheckTests`、
  `SqlServerImportLoggingTests`、`SqlServerImportRepositoryTests`、`SqlServerLockServiceTests`、`SqlServerMessageLogStoreTests`、`SqlServerOperationLogTests`、
  `SqlServerProjectCreateRollbackTests`、`SqlServerProjectDatabaseDeleteTests`、`SqlServerProjectRegistryTests`、`SqlServerProjectSchemaTests`、
  `SqlServerSchemaCatalogDriftTests`、`SqlServerSchemaMigrationTests`、`SqlServerTextStorageLimitTests`、`SqlServerUserDirectoryTests`、
  `DevDbReconcileHandlerTests`、`ProjectListRemoteDeadlineTests`、`ProjectLoadServerLookupTests`、`SystemDatabaseInfoHandlerTests`、
  `SqlConnectionStringFactoryTests`、`SqlServerExpressPhaseOutTests`。
- `UserProfileCacheTests`：測的使用者編號離線快取，隨 `system.whoAmI` 的線上取號一起移除。
- `AgentGuiTestFixtureBehaviorTests.FakeOnlineRegistry_ExistsIsClosedAndNeverCallsRealRegistry`：測的假線上登記夾具隨線上案件清單刪除，見上一節。
- `WorkingPaperStep41LegacyBaselineBenchmarkTests`：三個測試都以 DuckDB 為對象，剩下的只有輔助程式。
- 前端 `frontend-workflow` 的四個測試：「late unconfigured SQL disables its option and explains it without resetting a selected provider or the create draft」
  「a later configured SQL response refreshes only option and hint while preserving the create form」「an initially unknown then configured SQL response
  does not rebuild the create draft」「a failed SQL configuration query leaves unknown availability and the create draft unchanged」。
  建案表單沒有資料庫選項，啟動也不再查 SQL Server 設定。

## 使用者確認的名稱

這次新取或改名的名稱，照名詞表的規定要經使用者確認才算定案。使用者 2026-11-06 選「照用」，下面的名稱與句子都照用
（裁定 2026-11-06-15）；這一節原本叫「待使用者確認」：

- 名詞表「正被其他程式開啟的來源檔」一列：概念名稱、提醒句，以及 `SourceFileAccess` 和它的 `ReadShare`、`OpenRead`、`IsOpenElsewhere`，
  讀檔介面同名的 `ITabularFileReader.IsOpenElsewhere` 與 `CompositeTabularFileReader.IsOpenElsewhere`，
  `ImportMessages.SourceFileOpenElsewhere` 與 `SourceFilesOpenElsewhere`，以及 `SourceFileOpenElsewhereTests`。
- 放在結果旁的提醒：回應欄位 `openElsewhereWarnings`，前端 `import-step.js` 的 `openElsewhere`、`rememberOpenElsewhere`、`openElsewhereHtml`，
  `validate-step.js` 的 `accountMappingOpenElsewhere`，畫面屬性 `data-open-elsewhere`，以及測試輔助 `ActionShapeJourney.HoldLikeExcel`。
- 2026-11-06 清 VS Code 可空性警告時新加的 `OpenXmlPartRoots`、`RequiredWorkbook` 與 `RequiredWorksheet`。
- 判斷以前的資料庫案件的 `IsRetiredProviderCaseAsync`、`HasRetiredDuckDbDatabase` 與 `RetiredDuckDbDatabaseFileName`；
  `IsRetiredProviderCaseAsync` 現在也列在 `IProjectStore`，建案時用它判斷名稱是否被舊案件佔用。
- 建案名稱撞到以前的 DuckDB 或 SQL Server 案件時的句子 `ProjectFolderMessages.RetiredProviderNameTaken`：「案件名稱「名稱」已被以前建立的
  DuckDB 或 SQL Server 案件使用，這類案件不列在案件清單。請改用其他名稱，或到 JET 的案件位置刪除這個案件資料夾後再建立。」
  以及測試 `ProjectCreate_WithTheNameOfAHiddenRetiredCase_ExplainsTheOldFolderAndLeavesItUntouched`。
- 測試類別 `LocalTargetWriteGuardTests` 與它的方法 `SqliteAndLocalPersistence_HaveNoConcreteDirectTargetInsert`。
- 新增的測試方法：`SourceFileOpenElsewhereTests` 的 `OpenRead_WhileHeldLikeExcel_ReadsTheSavedContent`、`IsOpenElsewhere_OnlyWhileAnotherProgramCanWrite`、
  `GlImport_OneOfTwoSourcesHeldLikeExcel_ImportsBothAndWarnsOnlyForThatFile`、`GlAppend_HeldDuplicateSource_ListsOnlyTheOpenFileReminderSeparately`、
  `AccountMappingImport_WorkbookHeldLikeExcel_ListsSheetsImportsAndWarns`、
  `AuthorizedPreparerImport_WorkbookHeldLikeExcel_ImportsAndWarns` 與 `CalendarImport_TemplateHeldLikeExcel_ImportsTheSameDaysAndWarns`；
  `FieldInfoSheetWithoutVersionText_StillCarriesTheCommittedMapping`；`ProjectListLocal_HidesRetiredProviderCases_AndStillListsUnreadableOtherCases`；
  `ApprovalDateNotInPeriod_ShowsEachRowsApprovalDate_AndSortsByIt`。
- 上一節改名的測試方法與前端測試標題，以及三個新前端測試的標題：「a GL source held open by Excel shows its reminder beside the import result
  while other reminders stay in the message panel」「a holiday or make-up day list held open by Excel shows its reminder in the calendar card until
  that list is imported again cleanly」「Excel mapping of a workbook held open by Excel puts the reminder above the results in the tray and drops
  it after a clean import」。
- 驗證框架自我檢查的情境 `RemovedProviderCommand`，以及瀏覽器主機的案件名稱「台灣中小企業-自訂情境（SQLite）」。

## 延後事項

- 約五十個測試類別的說明仍寫「SQLite 與 DuckDB 各跑一次」這類句子，類別名稱也還帶 `ProviderParity`。目前只跑 SQLite；
  恢復 DuckDB 時這些說明會重新成立，這次不改。
- `mapping-step.js` 的 `unsupportedProvider` 句子照裁定 2026-10-14-01 保留；只有 SQLite 時不會出現。
- SAT 文件的套件清單與架構圖要回頭同步（裁定 2026-11-06-10），由 SAT 文件計畫處理。
- 開著檔案的提醒還沒用真的 Excel 與 OneDrive 實測；測試是用和 Excel 相同的開檔方式模擬。獨立複審另外指出幾個沒處理的邊界，
  都只看過程式、沒有實測：任何持有寫入權限的程式都算開著，提醒句仍寫「例如 Excel」；不一直開著檔案的程式不會提醒；
  CSV 匯入會開檔好幾次，匯入途中在 Excel 存檔時，欄名與資料列可能來自不同版本；兩個資料夾的同名檔案都開著時只提醒一則。

## 下一步

1. 交使用者驗收。計畫結案時移到歷史文件，SAT 文件的套件清單與架構說明再回頭同步（裁定 2026-11-06-10）。

已做的驗證：Debug 方案與 Release 發布設定完整重新編譯都是 0 個警告；最後一次 `Public` 執行 4,770 個測試全部通過；
`FrontendMapping` 997 個前端測試通過；`Gui` 的前置測試 31 個與 19 個情境通過，修正建案撞名後單跑的建案情境也通過；
裁定總表的上限調整後，`Documentation` 與驗證框架的自我檢查 `tools/tests/verify-contract.tests.ps1`（36 個情境、533 個斷言）通過。
沒有執行 `Excel`、`Package`、`PrivateCase` 與 `ReleaseCandidate`。
