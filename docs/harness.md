# JET 驗證框架

更新日期：2026-09-05

狀態：Phase 1 至 Phase 7 已完成

本文說明 `je-tool` 的驗證框架要負責什麼，以及哪些工作需要分開進行。命令與程式碼中的固定名稱仍保留
英文，其餘內容使用自然的台灣繁體中文。

各階段的工作內容與完成紀錄見
[`specs/2026-08-28-harness-rebuild-plan.md`](specs/2026-08-28-harness-rebuild-plan.md)。

## 要解決的三個循環

1. **開發循環**：快速確認程式可以編譯，並執行這次改動真正影響到的測試。
2. **測試循環**：用合成資料檢查產品規則、不同資料庫是否得到相同答案，以及輸出檔案是否正確。
3. **驗證循環**：在所需軟體、資料和授權都齊備時，再檢查 GUI、原生 Excel、私人 JE／TB 案件及正確底稿。

驗證框架只是用來檢查產品，不是產品本身的一部分。沒有這套框架時，`src/JET/` 仍應能正常建置、發布及執行。

## 基本原則

- 對外只提供 `tools/verify.ps1` 這一個入口。它負責檢查參數、啟動子程序、管理共享資源及寫下執行結果。
- 執行器本身保持精簡。有哪些驗證路線、每條路線需要什麼能力，應放在可直接檢查的設定檔中。
- 每次執行都有獨立編號和專屬資料夾。不同執行不得共用暫存結果，也不得拿舊專案的輸出冒充新證據。
- 第一次失敗必須保留。診斷重跑或修正後重跑可以新增紀錄，但不能覆蓋原始失敗。
- 只有會共用測試、GUI、Excel 或 SQL Server 資源的工作才需要互斥鎖。Restore 和 Build 不應被無故鎖住。
- 缺少必要能力、必要測試被略過、輸出缺件或清理失敗，都不能算通過。
- 只管理本次執行明確啟動的程序與建立的資料夾；不得按程序名稱大量終止，也不得清除來源不明的路徑。
- 子程序輸出寫入紀錄前，先將儲存庫根目錄、使用者目錄及本次明示的敏感值換成固定標記。需要連線資訊的
  測試也要先清理 TRX 等文字證據；遇到無法安全處理的附件就停止。
- 普通 `dotnet build` 或局部測試只能證明它實際涵蓋的範圍，不能代替完整驗收。
- `tools/` 第一層只分成 `harness/` 與 `tests/`；驗證框架子目錄使用小寫與連字號。C# 專案檔、原始碼檔、型別、
  namespace 與組件名稱仍依 .NET 慣例使用 PascalCase。目錄責任和程式語言的識別名稱不能混為一談。

## 文件用語

- 現況、操作方法、技術參考、歷史及執行紀錄要分開保存。每個主題只有一份文件負責說明目前答案。
- 除固定命令與必要術語外，使用台灣常見的短句。不自行創造縮寫、階段名稱或看似精確的新說法。
- `Documentation` 命令會檢查目前仍有效的文件與 Agent 轉接檔。缺檔、必要指向缺漏及已確認過時的事實會
  失敗；不自然用語只是 warning，不使用字數分數或禁詞數量代替人的判斷。固定片語之外，用 `＋`、`→`
  串名詞、兩層括號與 `紅燈`、`綠燈` 這類寫法也只列 warning，每條附一句怎麼改；程式碼區塊與行內程式碼
  不檢查。
- 自動檢查通過後，仍要人工讀完改動的段落，確認主詞、動作與結果都清楚，而且每項現況都有依據。改寫
  不能改變數字、條件、例外、不確定性、schema、UI 行為或使用者裁定。
- GitHub Copilot、Claude Code、Codex 與 VS Code AI Agent 的共用權威及轉接方式見
  [`agent-compatibility.md`](agent-compatibility.md)。驗證框架只檢查檔案與必要指向，不宣稱能保證模型行為。

## 執行狀態

JSON 紀錄使用下列固定值；中文說明才是使用時應理解的意思。

| 固定值 | 意義 |
|:---|:---|
| `passed` | 所有必要步驟均實際完成，證據及清理也完整 |
| `failed` | 工作已執行，但產品結果、測試或比對結果不正確 |
| `blocked` | 被鎖、逾時、取消或缺少外部能力，因此尚不能判斷產品正誤 |
| `not_run` | 本次沒有選取，或尚未開始 |
| `skipped` | 只適用於事先標明可略過的檢查；不能用來滿足必要條件 |

Phase 1 已為共用入口固定結束碼；完整表格見 [`../tools/README.md`](../tools/README.md)。

## 驗證路線

下表中的英文是程式內部固定名稱。目前十條路線都已啟用：`Foundation`、`Focused`、`Public`、`Provider`、
`Package`、`Gui`、`Excel`、`PrivateCase`、`ReleaseCandidate` 與 `Mutation`。
列為可用只代表命令已經實作。是否通過，仍要看該次執行是否完成所有必要步驟。

| 固定名稱 | 用途 | 可否讀取私人案件資料夾 |
|:---|:---|:---:|
| `Foundation` | 檢查執行器、文件用語、鎖、子程序、輸出限制、執行紀錄及安全清理 | 否 |
| `Focused` | 執行指定測試，並帶上必要的架構與介面檢查 | 否 |
| `Public` | 執行安全、合成且不需要外部環境的產品測試 | 否 |
| `Provider` | 比較 SQLite、DuckDB 與 SQL Server 的共同業務結果 | 否 |
| `Package` | 檢查產品內建範本、正式報告及封裝內容 | 否 |
| `Gui` | 在隔離環境操作真實 WinForms／WebView2 畫面 | 否 |
| `Excel` | 用原生 Excel 開啟與重算合成產物，並確認程序能完整關閉 | 否 |
| `PrivateCase` | 將明示提供的 JE／TB 走完正式流程，再和獨立正確底稿比較 | 是；每次都要明確授權 |
| `ReleaseCandidate` | 檢查可公開交付的候選快照；live SQL Server 與私人案件另行驗證 | 否；不會暗中執行 `Provider` 或 `PrivateCase` |
| `Mutation` | 在固定小範圍刻意改壞程式，確認公開測試是否抓得到；需要時才執行 | 否；固定排除私人案件、SQL Server 與壓力測試 |

`Focused` 與 `Public` 不得用共同前綴整批隱藏一系列測試。仍會使用受保護工作區的舊測試只能逐一列出；
不接觸私人路徑的解析器、比較器及規則單元測試必須留在公開測試循環。若一個測試混合兩種責任，應先拆開，
不能因名稱相近就整組略過。目前逐類排除清單是空的；保留下來的 Legacy 測試都使用合成資料或各自的系統
暫存目錄。

`Provider` 只從 `JET_SQLSERVER_CONNECTION` 取得 SQL Server 測試連線。缺少連線時直接回報 `blocked`，不會
退回 `appsettings.json`，也不會啟動建置或測試。正式執行先比較 SQLite 與 DuckDB 的共同結果，再執行
SQL Server 測試，最後確認 LocalDB Express 仍被排除。必要的 SQL Server 測試只要略過，就不能算通過；
測試用 SQL 專案若未能清理，也必須讓測試失敗。

`Provider` 保留 SQL Server 相容性的實跑證據，但不屬於一般 `ReleaseCandidate`。這項分離反映目前產品以
SQLite／DuckDB 本機工作為優先、SQL Server 中心化方向暫緩的決定；它不刪除 SQL Server 程式，也不允許
用編譯成功或被略過的測試宣稱 live provider 已通過。

`Package` 固定使用 Release。它會先檢查可追蹤的 `appsettings*.json`，有密碼或其他憑證就立即失敗；通過後
才建置、執行目前的報告與範本測試，並把 publish 輸出放在本次執行專屬的暫存目錄。測試也固定包含
`ReportArtifactTrustJourneyTests` 與 `ProjectReportArtifactStoreTests`，避免封裝只檢查報告內容而漏掉
可編輯範本、Working Paper 版本保留及一般報告覆蓋的操作契約。封裝清單直接由同一次
執行的現行來源與產物產生，不採用舊 P5 基準，暫存封裝在收尾時自動移除。

`Gui` 固定使用 `AgentGuiTest`，透過 WebView2 的本機偵錯連線依序執行八個隔離情境。
篩選步驟有兩個情境：`filter-auditor-journey` 從範本「借現金、貸非現金」開始，另開一組加一條
「每月幾日不屬於 28、31」，切換作用中組，預覽後對命中傳票清單排序，保存後核對讀回文字與情境數；
同一情境另操作多選日曆、期末七天與未填分類的錯誤列。`filter-kct-editing` 檢查 KCT 新舊來源的重開編輯。
審計員情境另驗證保存區關閉、工作區切換、矩陣按需載入與報告版本，也檢查加入後左側重設、右側欄位固定、
單條件不顯示關係設定、直接移除及排除指定日期。期末天數直接帶入日期，且每月幾日不出現無關設定；
分類區不溢出、保存按鈕保留間距、傳票查看按鈕完整可見。另檢查日期區間的響應式排列、借貸別標題間距，
以及情境名稱和動機草稿隨條件更新並保留手動修改，共 96 個操作，上限 96 個。
篩選視窗的尺寸以 96 DPI 邏輯像素定義，依測試視窗所在螢幕換算；不修改桌面縮放。
修改草稿前後另比對已保存的條件說明，確認它不被草稿更新改寫。
每次新增自訂條件後，另核對只有最新列顯示「選取中」，且焦點位於該列可見控制項。
審計員情境另等待七秒確認標示保留，並點選不同條件確認選取移轉。
借貸範本另核對滑鼠提示與套用後的動機均為業務說明，並保留原有借貸規則檢查。
桌面版另開啟資料預覽與其他資料選單，核對標題、GL 名稱及實際字體與字級一致。
KCT 情境另驗證移除和取消後焦點回到可見按鈕、選單不推高情境列，並核對金額門檻邊界、正負號、空白與文字排除方式。
同時確認摘要輸入框的起始高度與垂直對齊，共 70 個操作，上限 70 個。
KCT 情境另開啟流程總覽，核對母體數字字體及等寬數字設定，保留該區截圖後關閉總覽。
兩者都先使用最小視窗及 125% 放大。審計員情境再把測試視窗調大，
保存第二張桌面版面截圖；KCT 情境保留一張截圖。只調整測試擁有的視窗，不影響其他案件。
`-GuiScenario <名稱>` 只供診斷單跑一個情境，收據標記 `partial`，不算 `Gui` 通過。
`startup-smoke` 確認
頁面、bridge、`systemPing`、專案選擇畫面和離開按鈕。`synthetic-sqlite-create` 從可見表單輸入固定合成資料，
案件編號與客戶名稱保持空白。建立 SQLite 專案、進入匯入步驟後，再核對畫面上的案件名稱、本次暫存目錄中的
`project.json` 與 `jet.db`、兩個選填欄位的保存結果，以及操作人員是否為隔離測試帳號，並保存匯入步驟畫面。
`mapping-required-sync` 從已提交的合成 mapping 按「重新配對」，實際清空並補回同一個必填欄位十次，每次確認右側
缺漏狀態、提交資格與焦點同步。`edited-report-still-loads` 先在 JET 之外改寫一份已發布的 Working Paper
並放回舊版輸出紀錄檔，再從 Release 可見介面開啟案件，確認能載入、第六步清單標示「已在 JET 之外修改」、
匯出按鈕仍可用、清理面板已移除、去識別支援日誌含 `artifact.journal.discarded`，且舊紀錄檔已被清掉。
這個情境另準備 52 筆過期底稿紀錄，實際操作歷史分頁，確認紀錄仍可見但不構成目前流程完成的證據。
從畫面匯出新版後，53 筆紀錄都必須保留、新版排首位、完成摘要才出現；仍存在的舊版有定位入口，缺檔
入口停用。此情境不實際開啟檔案總管，路徑解析與缺檔錯誤另由 handler 測試核對。

`approval-mapping-modes` 檢查兩種配對畫面的核准日同步、未提交提示、人工或自動來源欄取消後的政策重設、
過帳狀態換欄後清除舊政策、RDE 全選與全部取消、完整還原與缺漏欄位定位，共 47 次操作。
還原結果以第一次編輯前的獨立快照核對。`validation-auto-outputs` 確認驗證後自動產生報告與範本，再以
合成分類填寫範本並重新驗證，核對檔案內容保留。這兩個情境各保存一張有大小上限的合成畫面截圖。

驅動程式位於 `tools/harness/gui-driver/`；資料夾名稱使用小寫並以連字號分隔，C# 型別、namespace 和組件則
維持 .NET 的 PascalCase。外部只能選擇已列出的情境，不能提供腳本、元素選取器或其他動作。驅動程式不下載
瀏覽器驅動，也不讀取私人資料。每個情境都要由 JET 自行結束，並確認 WebView2 與驗證框架暫存目錄已移除。
舊驗證框架的其他情境不會整組搬回；原生檔案對話框留到匯入流程需要時再建立專用驗證。

`Excel` 固定使用 Release，先以現行產品測試建立五份合成報告與一份科目配對工作檔，再由 `tools/harness/excel-driver/` 逐份交給
原生 Excel。來源工作簿以唯讀方式開啟，停用巨集與連結更新後完整重算；接著另存副本、重新開啟副本，並
輸出第一頁 PDF。通過前要確認來源檔未改變、公式錯誤沒有增加、副本沒有外部連結，而且 PDF 結構可讀。

產生輸入的產品旅程會填寫範本原檔、匯回、重開案件，再重新產生兩份驗證報告與後續報告。原生 Excel
只做開啟、重算和另存副本，不代表已驗證 Excel 原檔填寫或佔用中的再次匯出；公司操作的驗收結果依
現行計畫記錄，不能由這條自動驗證路線推定。

Excel 驅動程式不接受外部指定的工作簿路徑，只能使用本次驗證框架暫存目錄中的六種固定工作簿。它先以 Excel
的視窗代碼找到 PID，再連同啟動時間確認行程確實屬於本次。既有 Excel 不必先關閉，驗證框架也不得管理它們。
本次行程若在要求結束後仍未退出，只能對已確認的同一個 PID 使用後備清理；無法證明歸屬就回報 `blocked`。
Phase 5 不讀取私人案件，也不代表 JE／TB 正式匯入與底稿比對已通過。

效能壓力測試、變異測試或測試涵蓋率，只有在確實能回答獨立問題時才加入，不因舊框架曾包含就恢復。
2026-09-01 的 Stryker.NET 4.16.0 試跑曾繞過 JET 的測試篩選，未得到有效量測。2026-09-05 使用者確認
重啟變異測試，採用固定上游原始碼與小範圍 MTP 修補，從正式入口執行獨立的 `Mutation` 命令。
工具先驗證測試身分、取消、逾時與重啟，再對固定公開測試與規則執行。未知身分、錯誤範圍或程序收尾
未完成時都不算通過。命令與來源版本見 [`../tools/README.md`](../tools/README.md#需要時執行變異測試)。
它不加入日常或發行必跑項。第一次失敗的證據、獨立預期值、FsCheck、資料庫等價及 GUI 和 Excel 邊界
測試仍各自保留，不用其中一項的結果代替其他證據。

## 資料與判定依據

### 合成資料測試

- 每項規則應放在足以證明行為的最低層級測試；跨層測試只確認各層連接正確，不重複所有低層案例。
- 預期答案要來自現行規格、手算小案例、獨立算法或已核准的標準檔案，不能由被測程式重新計算。
- 若同一規則支援多種資料庫，應比較相同輸入得到的資料集合、排序、金額及失效狀態。

### `data/` 與產品內建範本

- `data/` 的十份工作簿是使用者裁定保留的 JET 業務範例。不得為了測試方便而重存或去識別化。
- `src/JET/JET/Templates/` 放的是產品實際隨程式發布的範本。
- `data/WorkingPaper.xlsx` 和產品內建的 `WorkingPaper.xlsx` 都不是某個私人案件的正確答案。它們可以用來檢查
  範本與封裝，但不能直接當作 JE／TB 案件的正確底稿。

### Legacy 業務來源

- [`../legacy/idea-tool.bas`](../legacy/idea-tool.bas) 用來查 IDEA 正式工具的特定規則；
  [`../legacy/idea-script.bas`](../legacy/idea-script.bas) 用來追查較完整的舊流程與邊界。
- 兩份 Legacy 程式不直接執行，也不整段翻譯成 .NET。每項要驗證的舊規則，都要先指出來源區段、現行程式位置，
  以及能獨立算出預期答案的測試案例。
- 如果 Legacy 與現行規格或使用者的新裁定衝突，依
  [`business-logic-provenance.md`](business-logic-provenance.md) 的權威順序處理。驗證框架不替產品做決策。

### 私人 JE／TB 與正確底稿

- 這台電腦的私人驗收案件放在 `data/test-case/`，整個目錄受 `.gitignore` 排除，不會進入第一次提交或
  後續 Git 歷史。已追蹤的程式只定義案件清單格式、路徑安全與比較規則，不保存客戶檔名、欄位配對或內容。
- 除了明示執行 `PrivateCase`，其他驗證不得列舉、猜測或解析私人案件資料夾。
- `PrivateCase` 必須由使用者在當次工作授權，並由 Git 排除的本機案件清單明確指定 JE、TB、舊工具紀錄、
  驗證報告與標準底稿。即使本機目錄已存在，也不得依固定檔名或舊環境變數自行搜尋。
- JE、TB、選配支援檔、操作選項和正確底稿是不同角色。案件清單格式與路徑安全先由合成測試驗證，通過後
  才能接入真實案件。
- 這台電腦已在 Git 排除的 `data/test-case/.harness/` 建立版本 1 的本機清單。它明列已確認的檔案角色、
  專案設定、匯入模式、欄位配對、日期及情境；一般驗證路線會排除 `PrivateCase` 測試並移除相關環境變數，
  不會讀取這份清單或同目錄的其他檔案。
- 正式流程只能使用本次建立的受控副本。GL、TB、Account Mapping 與選配的授權編製人員檔案會以安全名稱
  複製到本次工作目錄；假日與補班日則依本機清單建立新的輸入工作簿。產品程式不會取得原始私人路徑。
- 相同的產品動作順序已先用合成資料驗證，再由正式 `PrivateCase` 以 SQLite 執行本機案件。流程可從建案、
  匯入及篩選走到五份報告與一份科目配對工作檔輸出；它不帶入舊 `case-A`／`case-B` 的案件判定或既有差異清單。
- 六份標準底稿會先用安全開檔規則複製到本次工作目錄，固定命名為 `expected-report-001.xlsx` 至
  `expected-report-006.xlsx`。後續比較器只會取得這些受控副本，不會取得原始私人路徑；原檔保持不變，
  副本由本次執行負責清理。
- 六份標準底稿副本與六份新輸出會再按報表種類做一對一配對。這一層只檢查是否缺少或重複，不開啟
  工作簿，也不決定比較內容；兩側路徑都不會寫進 JSON。
- 目前這份本機案件以舊 CaseWare IDEA JE Tool 產出的報告與底稿作為獨立標準。JET 只能拿本次輸出和它比較，
  不能覆寫、重存或用自己的輸出更新標準檔。
- 使用者已決定六份底稿同時比較內容與版面。比較器會檢查工作表結構、儲存格內容、公式、樣式、尺寸、
  合併範圍、頁面及列印設定；封裝時間等不影響工作簿行為的資料不作為差異。INF 依抽樣規則與實際母體
  檢查 59 筆樣本，不要求重現舊 IDEA 當時隨機選到的相同資料列。
- `PrivateCase` 的成功與失敗都採相同清理政策。本次建立的來源副本、標準底稿副本、JET 專案及輸出一律
  清除；收據固定記錄沒有保留輸出。清理不完整時，不能回報產品通過。
- 第一次 SQLite 真實案件確實攔下三個篩選差異及六份底稿差異，沒有放寬比較規則。使用者後續決定：
  情境 1 採舊 IDEA、情境 5 的三個條件須在同一列成立、情境 6 的週末規則納入補班日。情境 1 的整張
  傳票口徑只用於單獨的「未預期借貸組合」，包含 Criteria 摘要與 Working Paper 標記；其他情境仍使用
  直接命中列。依此修正後，SQLite 與 SQL Server 的同一案件都已通過 INF、業務結果及六份底稿內容與
  版面比較。SQL Server 首次診斷曾因執行流程在額外情境檢查前先清除資料庫 schema 而回報
  `no_target_data`；目前已改成先完成診斷，再由原本的 `finally` 清理。正式收據為
  `artifacts/harness/runs/20260829-033750694-59af56de52f84b48af83b2c75f716c87/receipt.json`，清理狀態為
  `passed`，且沒有保存私人路徑或輸出。
- 長期保存的紀錄只放去識別化的數量、狀態、版本和差異類型。不得寫下私人路徑、公司、工作表、欄位、
  儲存格、科目、傳票、人員或金額。

## 每次執行要留下什麼

每次正式執行至少要記錄：格式版本、執行編號、入口版本、目前的 Git 分支與提交、選取的驗證路線、組態、
開始與結束時間、各步驟結果、必要能力、測試與輸出數量、首次失敗、產物相對位置，以及清理結果。

首次失敗要讓讀的人不必再翻 TRX。測試步驟失敗時，收據的 `firstRed.detail` 與步驟的 `test.firstFailure`
會寫出失敗數、依名稱排序的第一個失敗測試，以及它的訊息；訊息壓成一行、最多 240 字，儲存庫根目錄與
使用者目錄換成固定標記，`PrivateCase` 只留測試名稱、不留訊息。標準輸出那一行 JSON 也帶同一份
`firstRed`。`Focused` 與 `Public` 另會在 `testBoundary.assertionDrift` 記錄工作樹相對 HEAD 在
`src/JET/tests/` 移除了幾個 `Assert.`、幾個 `[Fact]` 或 `[Theory]`、新增了幾個 `Skip =`。這只是警示，
數字大於 0 表示要說明原因，不會讓命令失敗；沒有 HEAD 或沒有 git 時記為未量測。

`privateData.pathInspected` 會明確記錄這次是否真的查看過私人案件資料夾。一般驗證必須是 `false`。
`PrivateCase` 還要記錄沒有私人路徑進入收據，而且 `outputsRetained` 必須是 `false`。

`ReleaseCandidate` 會先核對候選清單與 Git 尚未忽略的檔案完全一致。它不用 `checkout` 建立乾淨副本，
因為候選範圍是以「實際未被忽略的檔案」定義的，包含尚未追蹤的新檔；`checkout` 只會取出 HEAD 已記錄的
內容，漏掉的部分不會被發現。因此框架會逐一複製清單中的未忽略檔案、核對雜湊，並在快照內
建立一次性的 Git 索引，讓隱私與 Git 規則測試能檢查和第一次提交相同的範圍。來源儲存庫不會用來建立
快照索引；框架會在建立快照前後比較來源的 cached stage entries，兩者不同就失敗。快照中依序執行
Contract、Documentation、Public（Release）、Package、Gui 與 Excel；
每個子命令都有自己的收據與清理結果，第一個失敗會停止後續步驟。私人案件不會被複製或執行，整份快照
最後由外層驗證框架清除。原生 Excel 無法開啟過深的巢狀路徑，因此快照放在
`artifacts/harness/rc/<短代碼>/s`，而不是外層執行目錄的 `scratch` 深處。短代碼只用於目錄名稱；完整執行
編號仍保留在收據與所有權標記中。清理時會同時核對目錄範圍、所有權標記與重新解析點。

前一版候選曾完成包含 `Provider` 的七個子命令及所有清理，但當時的 `sourceIndexMutated=false` 是執行器依流程
寫入，沒有獨立比較前後 index entries。2026-08-30 起，live `Provider` 改為獨立的 SQL Server 相容性驗證，
不再是每次公開候選的先決條件；需要 SQL Server 結論時必須另附當次 `Provider` 收據。

調整後的第一場完整候選只因沙盒無法存取 NuGet 而 `blocked`；該 first-red 保留。獲准在本機 Windows 邊界
原樣 fresh rerun 後，1,195 個候選路徑、Contract、Documentation、Public、Package、兩個 GUI 情境與六份
Excel 往返全部通過。Public 為 3,390 total／3,383 passed／7 個登錄 skip，Package 為 70／70；來源 Git index
前後都是 0 個 entries，快照與子命令清理完成，`privateData.pathInspected=false`。這份結果不包含 live
`Provider` 或 `PrivateCase`。

執行紀錄只描述這一次真正做過的事。舊專案的結果、別次執行的產物或人工推測，都不能補成通過。

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
