# JET 命名規則

更新日期：2026-11-27

這份文件規定 JET 的程式、資料夾、命名空間、資料庫、action、錯誤碼、事件、測試與檔案怎麼取名，以及程式結構怎麼對應審計流程。
每個業務概念在各層實際叫什麼，寫在[名詞表](glossary.md)；這裡只寫取名的方法。新的名稱照這裡的方法取，先寫進名詞表，經使用者確認後才用。

## 適用範圍

使用者 2026-11-27 裁定，命名與結構的整理只針對後端程式（裁定 2026-11-27-03、2026-11-27-05、2026-11-27-09）：

- 這次可以改後端產品程式與 C# 測試的名稱。資料夾與命名空間維持現在依模組的分法，見「程式結構怎麼對應四層」。
- 畫面文字、報告與底稿的工作表名稱和欄名、action 名稱，以及存在案件資料庫與設定檔裡的值，都維持現在的寫法。前端畫面顯示的文字暫時維持定版，前端測試的名稱這次也不改。
- 畫面第四步繼續叫「資料驗證與測試」。工作底稿的 step 編號和舊版 IDEA 的步驟編號已經定版，也不另外更改（裁定 2026-11-27-02）。

下面幾類名稱有各自的規則，這份文件不重寫：

| 對象 | 規則寫在哪裡 |
|:---|:---|
| `tools/` 底下的目錄、腳本與模組 | [`../tools/README.md`](../tools/README.md) 的「名稱與目錄分工」 |
| 送審文件的檔名、工作表名與用語 | [`../.agents/harness/sat-documents.md`](../.agents/harness/sat-documents.md) |
| Excel 工作表名稱的字數與禁用字元 | [`excel-conventions.md`](excel-conventions.md) |
| 五種報告的檔名 | [`jet-guide.md`](jet-guide.md) 第 7 節「檔案、發布與版本」 |
| 畫面用語 | [`jet-frontend-description.md`](jet-frontend-description.md) 與名詞表 |

## 審計邏輯的四層

JET 的審計邏輯分成四層。這是 Agent 2026-11-26 的提議，使用者 2026-11-27 同意照建議處理（裁定 2026-11-27-05）。提議原文如下：

- 步驟：審計員在畫面依序操作的六步。
- 程序：步驟裡的一項審計工作，例如第四步的完整性測試、借貸不平測試、資料可靠性測試、資料缺漏輔助檢視與預篩選，第五步的進階條件篩選。
- 規則與條件：程序逐筆判斷的依據，例如預篩選的各條規則，進階條件篩選的情境、條件組與條件。
- 產出：報告與工作底稿的工作表。

六個步驟各有一個步驟代號，前端在 `wwwroot/js/state.js` 定義，各步驟的主要程式 `js/steps/<步驟代號>-step.js` 也照代號命名。
畫面步驟索引是步驟從 0 起算的位置，案件設定檔的 `currentStep` 存的就是這個值。

| 畫面上的步驟 | 步驟代號 | 畫面步驟索引 |
|:---|:---|:---|
| 建立案件 | `create` | 0 |
| 匯入資料 | `import` | 1 |
| 欄位配對 | `mapping` | 2 |
| 資料驗證與測試 | `validate` | 3 |
| 進階條件篩選 | `filter` | 4 |
| 匯出底稿 | `export` | 5 |

程序在程式裡的英文名稱照名詞表，例如完整性測試是 `Completeness`，資料可靠性測試是 `InfSampling`，預篩選是 `Prescreen`，進階條件篩選是 `Filter`。

### 程式結構怎麼對應四層

後端程式依程式模組組織，分成 Bridge、Application、AuditCore、Domain 與 Infrastructure 五層。各層的責任與可以依賴哪一層，
見 [`jet-guide.md`](jet-guide.md) 第 9 節，由 `LayerDependencyTests` 核對。

每項程序的審計邏輯集中在 AuditCore，這正是使用者當初設計 AuditCore 的用意（裁定 2026-11-27-06）。
審計邏輯只在 AuditCore 與 Domain 定義一次，其他層要用到時呼叫它們的定義（裁定 2026-11-19-04）。
所以要找某項程序怎麼判斷，先看 AuditCore；Application 接收畫面的操作，Infrastructure 讀寫資料庫與檔案。

各層裡面的子資料夾只用來分類，不為了步驟與程序另外分出多層目錄，也不為此搬檔或改命名空間。
程式要讓人看出它屬於哪個步驟、哪個程序，靠的是名稱：型別、方法與檔名寫程序或工作表的內容，例如 `ValidationProcedures`、`WorkingPaperWriter.Completeness.cs`。

分在很多檔的部分類別只改檔名，不拆成多個類別，避免整理名稱變成重構（裁定 2026-11-27-07）。
AuditCore 的總入口 `JetAuditProgram` 分在 11 個檔。`JetAuditProgram.cs` 放驗證與預篩選的程序總表；
7 個檔名是 `JetAuditProgram.<步驟或程序>.cs`，例如 `JetAuditProgram.Prescreen.cs`、`JetAuditProgram.WorkingPaper.cs`，
放總入口裡那項程序的方法，以及那項程序的請求、計畫、結果與讀寫資料的介面；
另外 3 個檔以檔裡主要的類別命名，`ValidationProcedures.cs`、`CompletenessEligibility.cs`、`FieldInfoProjection.cs`，總入口在那裡只放一兩個入口方法。
四種報告的寫出程式 `LegacyReportWriter` 也是這個寫法，檔名是 `LegacyReportWriter.<主題>.cs`。
2026-11-27 改名的檔案與新舊對照見[後端改名紀錄](history/2026-11-27-backend-rename-records.md)。

## 步驟與編號

JET 有好幾套從 1 或 0 開始的編號，同一個數字在不同地方指不同的東西，所以程式與文件照下面的規則寫。
這些規則是 Agent 2026-11-26 的提議，使用者 2026-11-27 同意照建議處理（裁定 2026-11-27-05）：

- 程式與檔名不用畫面步驟的序號，改用步驟代號 create、import、mapping、validate、filter、export。
  後端用列舉 `WorkflowStep` 表示畫面步驟，六個值的順序只定義在這裡（裁定 2026-11-27-11）。
- 底稿的工作表名稱沿用範本的寫法。程式裡指稱某張工作表的常數、類別、方法、檔名、暫存表與暫存檔，依工作表的內容取名，
  常數的值仍是範本上的工作表名稱（裁定 2026-11-27-08）。九張表的程式名稱如下，名詞表的「底稿工作表的程式名稱」也有同一份對照。
- 文件提到畫面步驟時寫步驟名稱，提到底稿時寫工作表的完整名稱，例如「step4-1 符合高風險條件傳票明細」，不寫「底稿第四步」。
- 規則舊代號 V1 到 V4 與 R1 到 R7 只留在回查用的欄位與報告範本，不用在程式名稱。
- 舊版 IDEA、VBA 與原型的編號只出現在 `legacy/` 與比對舊版的文件。

| 範本上的工作表 | 程式名稱 | 名稱的來源 |
|:---|:---|:---|
| step1 完整性測試 | `Completeness` | 名詞表「完整性測試」 |
| step1-1 借貸不平測試 | `VoucherBalance` | 名詞表「借貸不平測試」 |
| step1-2 分錄編製人員說明 | `PreparerSummary` | 名詞表「依分錄編製者彙總」 |
| step1-3 完整性測試之差異說明 | `CompletenessDifference` | 名詞表「完整性測試」 |
| step2 可靠性測試 | `InfSampling` | 名詞表「資料可靠性測試」 |
| step3 高風險條件彙總 | `HighRiskSummary` | 名詞表「高風險條件彙總」 |
| step4 符合高風險條件傳票 | `HighRiskVoucher` | 使用者 2026-11-27 選定；IDEA 版只有暫存檔名 `#To_WP_Sum` |
| step4-1 符合高風險條件傳票明細 | `HighRiskVoucherDetail` | 使用者 2026-11-27 選定；IDEA 版只有暫存檔名 `#To_WP` |
| step5 財務報表關帳後調整之分錄 | `PostClosingEntries` | 使用者 2026-11-27 選定；VBA 版的 `ShowPostClosingEntries`，範本說明也寫 Post-closing entries |

資料驗證報告的 V_Report 1 到 6、預篩選報告的 R1 到 R7 是報告的工作表名稱，沿用範本的寫法。

### 同一個數字在各處的意思

| 數字 | 畫面第 N 步 | 畫面步驟索引 N | 底稿的 stepN 工作表 | 舊版 IDEA 的步驟 N | 舊版 VBA 的 `DoStepN` |
|:---|:---|:---|:---|:---|:---|
| 1 | 建立案件 | 匯入資料 | step1 完整性測試，另有 step1-1、step1-2 與 step1-3 | 欄位配對與驗證 | 匯入 |
| 2 | 匯入資料 | 欄位配對 | step2 可靠性測試 | 上傳科目配對與假日 | 驗證 |
| 3 | 欄位配對 | 資料驗證與測試 | step3 高風險條件彙總 | 預先篩選 | 篩選 |
| 4 | 資料驗證與測試 | 進階條件篩選 | step4 符合高風險條件傳票，另有 step4-1 | 進階篩選 | 匯出 |
| 5 | 進階條件篩選 | 匯出底稿 | step5 財務報表關帳後調整之分錄 | 產生工作底稿 | 沒有這一步 |
| 6 | 匯出底稿 | 沒有這個值 | 沒有這張工作表 | 重新執行的旗標 | 沒有這一步 |

舊版的編號出自 `legacy/idea-tool.bas` 與 `legacy/VBA-legacy/`；JET 和舊版的差異怎麼分類見 [`jet-guide.md`](jet-guide.md) 第 17 節。

## 使用者確認的命名方式

下面九條原本寫在名詞表的「命名方式」，2026-11-27 搬到這裡，文字照原樣。條文裡的「這份名詞表」與「不再使用的名稱」都指[名詞表](glossary.md)。

下面是使用者確認的名稱共同遵守的取法，由 Agent 依使用者 2026-10-29 在確認頁 4-3、4-4 的備註整理。
之後要提新名稱時照同樣的方式提，仍要先寫進這份名詞表，經使用者確認後才用。

1. 同一個概念在每一層用同一個英文詞，只依各層慣例換大小寫：資料庫用小寫加底線，C# 型別與屬性用大寫開頭，
   JavaScript 與 action 欄位用小寫開頭。例如總帳日期是 `gl_date`、`GlDate`、`glDate`。
2. 表名用單數名詞，直接取畫面或舊版對這份資料的稱呼，不加 `staging_`、`target_`、`result_`、`config_` 這類分層字首，
   也就不必再經過 `JetSchemaCatalog` 對照成畫面名稱。
3. 審計員在資料預覽看得到的資料集，表名就是資料預覽名稱的轉寫，例如 PBC-GL 是 `pbc_gl`，GL 是 `gl`，科目配對是 `account_mapping`。
   GL(未納入測試) 和 GL 在同一張表，用欄位區分。
4. 匯入時原樣暫存的資料一律用 `pbc_` 開頭，例如 `pbc_account_mapping`。
5. 程式識別字先找舊版 VBA 或 IDEA 的用語，其次是使用者原話與已確認的畫面用語；兩者都沒有時才由 Agent 取英文，並在「依據」寫明。
6. 技術慣例保留：金額欄的 `_scaled` 後綴表示放大成整數存放，布林欄用 `is_` 開頭，時間欄用 `_utc` 結尾，識別碼用 `_id` 結尾。
7. 報告與底稿的欄名、工作表名與範本檔名照舊版範本，例如「總帳日期_JE」與 `WorkingPaper.xlsx`。
   底稿的英文名稱依審計部門統一的命名規範，一律寫 WorkingPaper，這是使用者 2026-10-30 的裁定。
8. 條件型別值存在已儲存的情境裡，`group`、`voucher` 都不改。`typed` 2026-11-26 停用（裁定 2026-11-26-10），已存的原文照存。
9. 處理 action 的類別，名稱是 action 以句點分開的每一段首字改大寫後相連，再接 `Handler`，例如 `import.authorizedPreparer.clear` 是
   `ImportAuthorizedPreparerClearHandler`。一個檔案只放一個處理器時檔名就是類別名稱；放多個時寫共同的主題再接 `Handlers`。
   幾個處理器共用的抽象父類別不對應 action，不受這一條限制。這一條由 Agent 依既有的寫法整理（裁定 2026-11-16-02），
   `HandlerNamingTests` 逐一核對。照這一條改名的兩個類別列在「不再使用的名稱」最後兩列。檔名改了三個：
   `ImportAuthorizedPreparerFromFileHandler.cs` 改成 `ImportAuthorizedPreparerHandlers.cs`，`ImportFromFileHandler.cs` 改成 `ImportFromFileHandlers.cs`，
   `ProjectListHandler.cs` 改成 `ProjectListLocalHandler.cs`；測試類別 `ImportAccountMappingHandlerTests` 改成 `ImportAccountMappingFromFileHandlerTests`。
   這些新名稱與 `HandlerNamingTests`，使用者 2026-11-16 確認照用（裁定 2026-11-16-10）。

名詞表確認過的 `result_stale_state` 保留 `result_` 字首，是第 2 條現有的例外。

## C# 程式

- 大小寫照微軟的 C# 識別字慣例：型別、方法、屬性、事件與常數用 PascalCase，介面以 `I` 開頭，參數與區域變數用 camelCase。
- 縮寫 `Gl`、`Tb`、`Rde`、`Inf` 只大寫第一個字母，例如 `GlDate`、`InfSampling`。微軟的準則要兩個字母的縮寫全部大寫，
  但 JET 的程式一直這樣寫，沒有混用，全面改要動上千個名稱，所以維持現況，這是 JET 自己的約定（裁定 2026-11-27-05）。
- 名稱寫事項本身是什麼。不用開發過程的代號，例如 `Phase2`、`Batch3`；不用數字字尾分版本；不用畫面步驟的序號或規則舊代號。
  微軟的準則也建議改版時優先取有意義的新名稱，數字字尾是不得已才用。
- 產品程式裡宣告成 `async` 的方法都以 `Async` 結尾，新寫的方法照同樣寫法。
- 部分類別分成幾個檔時，檔名寫型別名稱、一個點與主題，例如 `GlRulePredicates.Frequency.cs`、`LegacyReportWriter.Validation.cs`。
- 私有的執行個體欄位用底線開頭的 camelCase，例如 `_workerQueue`，這是微軟的慣例。static 欄位用 PascalCase，不加 `s_`：
  微軟同一份文件寫明 `s_` 不是 Visual Studio 的預設，也不屬於 Framework 設計準則，而 JET 的 static 欄位本來就幾乎都用 PascalCase（裁定 2026-11-27-11）。

## 命名空間與資料夾

- 產品程式只有六個命名空間：`JET`、`JET.Bridge`、`JET.Application`、`JET.AuditCore`、`JET.Domain` 與 `JET.Infrastructure`，對應五層與根目錄的主程式。
  子資料夾只用來分類，所以 `src/JET/JET/.editorconfig` 關掉了要求命名空間跟著資料夾的 IDE0130（裁定 2026-11-27-06）。
- 測試專案 `src/JET/tests/JET.Tests` 的資料夾對應產品程式的同名資料夾，例如 `Application`、`AuditCore`、`Domain`、`Infrastructure`；
  `Architecture`、`Fixtures` 與 `TestInfrastructure` 是測試專用的資料夾。
- C# 程式的資料夾與檔名用 PascalCase。

## 資料庫

- 表與欄的取法見上面第 1 到 4 條與第 6 條。每張表都列在名詞表的「資料庫表名」，文件檢查會核對建表程式和這份清單一致。
- 索引用 `ix_` 開頭，接表名與欄名，例如 `ix_gl_account`；觸發條件用 `trg_` 開頭，接表名與用途，例如 `trg_operation_log_no_update`。
  名詞表確認過的 `idx_filter_hit_entry` 是現有的例外。
- 定版之後新增表或欄位時，登記方式見 [`jet-guide.md`](jet-guide.md) 第 8 節。
- 存在案件資料庫與設定檔裡的值不改，例如 `dataset_kind` 的 `gl` 與 `tb`，以及上面第 8 條的條件型別值。

## action、錯誤碼與事件

- action 名稱用 camelCase，以句點分段，第一段是功能領域，例如 `validate.run`、`mapping.commit.gl`。
  現有的 action 名稱不改（裁定 2026-11-27-05）。全部 action 列在 [`action-contract-manifest.md`](action-contract-manifest.md)，
  和前端 `wwwroot/js/jet-api.js` 的 `SUPPORTED_ACTIONS` 逐字對應。處理器的類別與檔名照上面第 9 條。
- 錯誤碼用小寫英文與底線，集中定義在 `JetErrorCodes`，例如 `invalid_payload`。可以辨識的資料庫錯誤用 `database_` 開頭。
- 支援日誌的事件名稱用句點分成範圍與事件兩段，兩段都用小寫英文，段內用底線連接，例如 `action.payload_rejected`、`sql.slow`。
  動作分派、案件紀錄與案件開啟共用的事件名稱集中定義在 `SupportEventNames`，寫入端與讀取端都用這些常數。
- 主程式送給前端的進度事件用 `<功能領域>.progress`，例如 `import.progress`、`export.progress`，格式見 action 契約的「主程式進度事件」。

## 前端

前端的畫面文字暫時維持定版，下面只記現在的寫法，這次不改前端的名稱。

- 檔名用小寫與連字號。各步驟的主要程式放在 `js/steps`，檔名是 `<步驟代號>-step.js`；同一步驟拆成幾個檔時寫 `<步驟代號>-step-<主題>.js`，
  例如 `filter-step-rules.js`。
- CSS 類別用 BEM：區塊與元素寫成 `block__element`，變體寫成 `block--modifier`；狀態另用 `is-` 與 `has-` 開頭的類別，例如 `is-missing`。
- 前端用到的名稱以後端的定義為準，例如資料預覽的資料集名稱由 `DataPreviewDatasetLabels` 定義，前端逐字照用；
  後端的定義又照名詞表。前端測試檔的命名見 [`../tools/README.md`](../tools/README.md)。

## 報告、底稿與 Excel

- 報告與底稿的欄名、工作表名與範本檔名沿用舊版範本，見上面第 7 條。
- 後端程式裡指稱底稿工作表的常數、類別、檔名與暫存表，照工作表的內容取名，見「步驟與編號」。
- 範本與參考工作簿的檔名以 `data/` 和程式隨附的範本為準，見 [`data-and-legacy.md`](data-and-legacy.md)；
  JET 匯出的報告檔名另外規定，和範本檔名刻意不同，見 `jet-guide.md` 第 7 節。

## 測試

- 測試類別的名稱寫受測對象，以 `Tests` 結尾；夾具與輔助類別不加 `Tests`。
- 測試方法依序寫受測對象、情境與預期結果，用底線分段，讓人不讀程式碼也知道哪個行為壞了。這是送審的測試規則
  `Unit test rule` 的要求，微軟的單元測試建議也是這個順序。
- 名稱寫事項本身是什麼，不寫開發經過、版本號、畫面步驟的序號或規則舊代號。版本號這類會變的值放在測試資料裡。
- 測試的資料夾跟著產品程式，見「命名空間與資料夾」。

## 自動檢查

現有的檢查：

| 檢查 | 核對什麼 |
|:---|:---|
| `HandlerNamingTests` | 每個註冊的處理器照第 9 條命名。放多個處理器的檔案，它只檢查檔名以 `Handlers.cs` 結尾，主題取得好不好由人複核。 |
| `SourceCommentPolicyTests`，在 `tools/harness/development-repository-tests` | 原始碼的識別字、字串與註解不含 `Phase2`、`Batch3` 這類代號、裁定編號與 AI 工具名稱；註解不引用文件的章節。 |
| `TerminologyUnificationFrontendTests` | 畫面文字不出現已停用的字樣。它不檢查識別字。 |
| `SupportEventNameOwnershipTests` | 支援日誌的事件名稱由 `SupportEventNames` 定義，寫入端與讀取端都用常數。 |
| `LayerDependencyTests` | 各層只依賴可以依賴的層。 |
| 文件檢查 `tools/verify.ps1 -Command Documentation` | 名詞表的「資料庫表名」和建表程式一致；「不再使用的名稱」沒有出現在產品程式。 |
| 建置時的命名檢查 | 私有的執行個體欄位用底線開頭的 camelCase，私有的 static 欄位與常數用 PascalCase，違反就建置失敗。規則寫在 `src/JET/.editorconfig`，違規代號 IDE1006 設成 error；`src/JET/Directory.Build.props` 打開 `EnforceCodeStyleInBuild`，因為命令列建置預設不檢查程式風格。 |
| `IdentifierNamingTests` | 產品程式與 C# 測試宣告的名稱，不用畫面步驟與底稿工作表的序號、規則舊代號、版本號與開發經過的字，例如 `Step41`、`R3`、`V20`、`SinglePurpose`；宣告的名稱從編譯好的組件讀出。原始檔名不用序號、規則舊代號與版本號。原始碼裡的暫存表、測試註冊的 SQLite 函式與暫存檔名不用工作表序號，例如 `jet_step41_hit_voucher`。其他字串與註解不在檢查範圍。開發儲存庫多編進 `tools/harness/development-repository-tests` 的檔案，也照同一套規則檢查。 |

上面最後兩項是照使用者的決定新加的（裁定 2026-11-27-05、2026-11-27-11）。

## 參考的標準

- 微軟的 C# 識別字慣例：[Identifier names](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/identifier-names)。
- 微軟的 .NET 命名準則：[Capitalization Conventions](https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/capitalization-conventions)、
  [General Naming Conventions](https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/general-naming-conventions)、
  [Names of Namespaces](https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/names-of-namespaces)。
- 建置時的命名檢查：[Code-style naming rules](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/style-rules/naming-rules)、
  [Code analysis overview](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/overview)。
- 測試命名：[Best practices for writing unit tests](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-best-practices)。
- 讓名稱呈現業務：[UbiquitousLanguage](https://martinfowler.com/bliki/UbiquitousLanguage.html)。
- 文件和程式一起管理：[Docs as Code](https://www.writethedocs.org/guide/docs-as-code/)。
