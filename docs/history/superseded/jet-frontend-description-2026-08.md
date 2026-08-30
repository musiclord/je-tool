# JET 前端介面規格書

本文件是執行期前端（位於 `src/JET/JET/wwwroot/`）的權威描述，說明它的行為與流程，涵蓋畫面結構、使用者流程、互動狀態與資料契約。它不是 HTML 與 CSS 的逐行說明，也不是 action contract 的替代品。換句話說，就算原始程式碼不存在，工程師也應該能照著本文件重建出一個功能等價的前端。

> 從 2026-06-11 起，本文件以六步驟的執行期前端為準。早期的原始模板已於 2026-08-17 移除，視覺參考改為 `docs/design_handoff_jet_frontend/`。規則一律使用 `docs/jet-guide.md` §4 命名登錄表裡的具體名稱；早期的規則代號（V 代表驗證、R 代表預篩選、A 代表進階篩選）已退役。

---

## 1. 文件定位

| 項目 | 定義 |
|:---|:---|
| 主要來源 | `src/JET/JET/wwwroot/`（執行期前端） |
| 契約來源 | `docs/action-contract-manifest.md`（wire shape 唯一事實來源） |
| 領域來源 | `docs/jet-guide.md`（規則語意與命名登錄表） |
| 視覺參考 | `docs/design_handoff_jet_frontend/`（設計交付與參考原型，非行為依據） |
| 文件目標 | 即使沒有原始 JS，也能依本文件重建同等功能的前端流程 |
| 視覺彈性 | 可自由更換視覺風格、排版、色彩與元件樣式（遵循 minimalist-ui skill） |
| 行為要求 | 必須保留流程、畫面目的、狀態閘門、使用者操作與後端 action 權責 |

一句話總結：**視覺可變，行為不可變。**

## 2. 介面原則

JET 前端是一個跑在 WebView2 裡的審計工作流程介面。它的職責有四項：收集使用者輸入、呈現目前的流程狀態、把使用者的各項選擇組合起來，最後透過 action contract 把請求交給後端處理。

前端只是一層薄的呈現與協調層，不得持有任何具有權威性的審計邏輯。具體來說，下列邏輯都不准放在前端：

- 審計規則與 SQL
- 資料匯入時的欄位標準化（也就是投影，projection）
- 資料驗證、預篩選、進階篩選的計算邏輯
- 工作底稿匯出的產製邏輯

上面這些邏輯一律由後端負責。前端只負責三件事：觸發動作、傳遞參數、呈現結果。

正式版的結構表面統一使用方角、1px hairline 與平面層級；圓角只留給狀態點、真正的 pill 與其他封閉語意標籤。專案列 hover 不加陰影，流程總覽 modal 仍是唯一保留外陰影的主要表面。

### 詞彙規範

- 規則名稱一律用命名登錄表裡的中文名，例如完整性測試、期末後核准等等，不得出現 V1、R8 這類已退役的代號。
- 對使用者顯示時用「標準化」這個詞，例如「標準化後分錄」「已標準化 N 列」，不要用實作端的詞「投影」。「測試母體」「匯入原貌」則是正當的審計用語，可以用。

畫面文案採下列固定對照；左欄只供工程文件辨認既有技術詞，執行期 UI 一律使用右欄的審計員語彙。內部 action、wire 欄位與程式識別字不因文案調整而改名。

| 工程或舊畫面用語 | 執行期畫面用語 |
|:---|:---|
| 投影、已投影 | 標準化、標準化後資料 |
| 後端、前端 | 系統、畫面 |
| 權威預覽 | 系統確認的清單 |
| artifact、Artifact ID | 報告、報告識別碼 |
| revision、stale、retention | 資料版本、版本已失效、超出保留範圍 |
| catalog | 報告紀錄或報告清單 |
| wire／前端 state | 畫面不保存完整本機路徑 |
| Provider | 資料儲存方式 |
| WebView2 host／Bridge／Host | 應用程式連線 |
| DBA | 系統管理人員 |
| step4／step4-1／tag | 傳票層／分錄層／標記 |
| `AUTHORIZED_PREPARER`／`DATE_DIMENSION` | 授權編製人員清單／事務所行事曆 |
- 不要對使用者顯示技術內部用詞，例如 AST、SQL、payload、metadata 等。

## 3. 模組結構

JavaScript 採模組化結構，載入順序見 `index.html`：

| 模組 | 職責 |
|:---|:---|
| `js/jet-api.js` | 唯一的 WebView2 傳輸層（`JetApi.*` typed facade；`SUPPORTED_ACTIONS` 清單鏡像 manifest） |
| `js/state.js` | 輕量 UI 狀態（`JetStore`）：步驟、專案、匯入/配對/規則執行/篩選狀態；**不存 GL/TB row 集合** |
| `js/ui-core.js` | 共用核心（`JetUi`）：契約鏡像常數（欄位、模式、條件型別與分組、預篩選鍵、分類選項）、預篩選定位文案 fallback、閘門模型（`stepGate`／`isStepReachable`）、純呈現 helper（`stepPresentation` 推導四態、`doneStepCount` 推導完成步數）、統一頁尾、步驟註冊表、專案載入。runtime 前端沒有 mock registry 或測試案件 handler。**鏡像原則**：條件型別顯示名、預篩選鍵、文字／科目配對模式、可篩選 GL 欄與非營業日原子等中文標籤，以 Domain `FilterConditionLabels` 為單一事實來源；預篩選的定位文案則以 AuditCore `PrescreenPositioningRenderer` 為正本。前端常數只供舊摘要缺少 `positioning` 欄位時 fallback，兩組 mirror 守衛防止漂移。 |
| `js/steps/*.js` | 每步驟一個渲染模組，自行向 `ui-core` 註冊 |
| `js/data-preview.js` | 常駐右欄的資料預覽（正式版功能；頁籤切換資料表、橫向捲動、左邊界拖曳調寬、整欄可收合、半屏自動狀態與可鍵盤操作的「其他資料」選單） |
| `js/overview-bi.js` | 流程總覽的母體事實區塊（`JetOverviewBi`）：依「常用母體彙總 → 分錄金額級距／ECDF → 逐筆輔助訊號分布」呈現三組預設收合分析，將後端 DTO 純鏡射成 vendored ECharts 5.5.0 option，固定使用 SVG renderer、`animation:false`、notMerge 更新與 resize／dispose lifecycle。只有展開時才建立圖表、收合即 dispose。**只鏡射後端算好的彙總並格式化**，不做任何命中數加總或百分比換算 |
| `js/dev-panel.js` | 開發者面板（僅 Debug 組建顯示） |
| `js/dev-log-panel.js` | 開發者診斷日誌匯出面板（僅 Debug 組建顯示，`dev.log.export`） |
| `js/app.js` | 殼層與啟動：三欄工作區排版、專案選擇畫面、文件流四態的步驟導航、左欄訊息面板、流程總覽視窗（皆只呈現、放行一律取自 `ui-core` 閘門） |

## 4. 應用程式外殼

外殼由上而下是三塊：頁首、案件狀態摘要條，以及主要工作區。工作區有兩種視圖——專案選擇畫面（picker）與六步驟工作流程（workflow）——同一時間只顯示一種。workflow 視圖的版面是一條三欄的 `app-body`：左欄是目錄與訊息、中欄是由上而下的步驟文件流、右欄是常駐的資料預覽。以下先列出各區域，再逐一敘述。

| 區域 | 必要元素 | 用途 |
|:---|:---|:---|
| 頁首 | 產品名稱、使用者身分徽章、「流程總覽」、「開啟專案資料夾」、「回專案選擇」、「儲存並結束」 | 顯示操作者身分與編號（應用程式連線故障時顯示失敗）；開啟流程總覽或目前專案資料夾；保存最後進度並釋放案件鎖後切換視圖或結束 |
| 案件狀態摘要條 | 案件編號、客戶名稱、查核期間、目前階段、目前步驟（x / 6） | 常駐顯示目前案件身分與流程位置 |
| 專案選擇畫面 | 本機先顯示的雙分區列表：啟動、返回 picker 與「重新整理本機」只以 `project.listLocal` 列 SQLite／DuckDB；未手動同步時上方「線上資料庫（SQL Server）」顯示「尚未同步線上案件」。使用者按「同步線上案件」後才以 `project.list` 合併線上列、principal 註記、`syncStatus` 徽章（「僅伺服器」／「本機快取・伺服器上找不到」／「無存取權」）與失聯警示。另有「新增專案」列 | 啟動入口；載入續作（resume）、刪除專案（線上案件的確認文案明示「將從伺服器永久刪除（對所有使用者）」）。本機 response 只有 `projects`；手動同步 response 的 `online{reachable,principal,message}` 與條目 `syncStatus` 見 manifest |
| 工作區左欄（目錄，桌面上限 196px） | 六步驟目錄（TOC）＋進度列＋底部可收合訊息面板 | 導航與定位；未解鎖步驟不可點並列出缺漏條件 |
| 工作區中欄（文件流） | workpaper 抬頭＋六步驟區段（四態） | 由上而下呈現整份流程；當前步驟就地展開編輯 |
| 工作區右欄（資料預覽，預設 436px） | GL／TB 的 PBC 與標準化成對頁籤、科目配對直接頁籤、「其他資料」選單、橫向捲動表格、左邊界拖曳調寬、整欄收合 | 檢視已匯入／已標準化的資料集（**正式版功能**，`query.dataPreview`） |
| 流程總覽視窗 | 案件摘要＋案件進度＋六階段狀態登錄＋所選階段執行紀錄＋四個母體事實＋預設收合的三組分析＋技術資訊折疊區＋精簡解讀 | 頁首「流程總覽」鈕開啟的補充視窗（modal），純檢視；不充當第二套流程導航 |
| 統一步驟頁尾 | 上一步/下一步、閘門提示 | 落在當前展開步驟區段底部；閘門未滿足時「下一步」停用並說明缺什麼；第 0 步（建立案件）無上一步按鈕 |
| 開發者面板 | 唯讀資料庫檢視 | **僅 Debug 組建**：依 `system.ping.devToolsEnabled` 顯示；Release 後端不註冊 `dev.db.*` |

**頁首。** 頁首有產品名稱（英文小標「Journal Entry Testing」加中文標題）與右側的狀態列。狀態列上有使用者身分徽章（`data-bind="user-identity"`，2026-07-07 取代原主機連線徽章），以及四顆按鈕。身分徽章的狀態序：應用程式連線未就緒顯示「偵測中」、探測失敗顯示「主機連線失敗」（故障訊號保留）、就緒後先顯示「已連線」，待開機序並行呼叫的 `system.whoAmI` 回應後改為「{帳號短名}・U{使用者編號}」（未取得編號時只顯示帳號短名）；title 提示帶完整 principal 與編號來源（線上確認／本機快取／未取得原因）。身分資料存於純 UI 狀態 `currentUser`（只 notify、不進任何後端 payload）。「流程總覽」與「開啟專案資料夾」只在 workflow 視圖顯示；前者開啟純檢視的總覽視窗，後者送出 `{ target:"projectFolder" }`，由後端依 active session 解析目前專案目錄，畫面不接收或傳送實體路徑。這是全系統唯一的資料夾開啟控制：報告列不另設按鈕，匯出完成也不會自動開啟 Explorer。「回專案選擇」也只在 workflow 視圖顯示：已在處理作業時不重複觸發；其餘情況先設 busy，等 `project.saveProgress` 的 latest-value 合併器保存最後進度，再送出 `project.releaseLock`，只有放鎖成功才解除 busy、切回專案選擇畫面並重載清單。「儲存並結束」則不分視圖都在；位於 workflow 時同樣依序保存最後進度、成功釋放案件鎖，再呼叫 `host.exitApp`。原生標題列 X 在 WebView ready 時也導回這條「儲存並結束」鏈，而不是直接拆 runtime；host fallback 仍先檢查同一 execution gate。離場遇 `operation_in_progress`、底層放鎖失敗、取消或 active project 下 bridge 未就緒時，都保留目前畫面、active session 與心跳；busy overlay 有可取消 requestId 時在遮罩上明示按〔取消作業〕，沒有可取消 request 時改為明示等待完成後重試，不會要求不存在的按鈕。此流程不自動取消、不排隊，也不在放鎖失敗後偷偷離場；若鎖已成功釋放但 `host.exitApp` 失敗，前端清掉 workflow 並回 picker 顯示就地錯誤，不冒充仍在案件中。

**案件狀態摘要條。** 頁首下方有一條常駐的案件狀態摘要，顯示五項：案件編號、客戶名稱、查核期間、目前階段、目前步驟（x / 6）。案件與客戶未建立時顯示佔位符。它與頁首一樣不隨視圖切換而消失。

這裡的**「目前步驟」是流程位置**（`currentStepIndex + 1` / `STEPS.length`），與「進度」的完成證據是兩個不同語意，數值通常不同：使用者可以站在第 5 步、但只完成 3 步。左欄仍以 `ui-core.doneStepCount` 計前五個推進閘門；流程總覽則顯示六階段狀態，最後階段另以目前版本 Criteria Selection Report 與 Working Paper 的精確來源比對判定，因此可在兩份產物齊備時顯示 6／6。位置、左欄進度與總覽六階段完成度不可互換。查核期間取自 `project.periodStart`／`periodEnd`，缺值時降級為 `—`，不由前端推算。

**統一步驟頁尾。** 頁尾落在文件流中「當前展開步驟」的區段底部（不是整頁固定的一條）。第 0 步沒有「上一步」按鈕；要在第 0 步回專案選擇，改由頁首的按鈕提供。頁尾的「下一步」是否可按，一律以下一步閘門為準（`stepGate`），未備齊時停用並列出缺什麼。

**專案選擇畫面。** 專案列表的每一列會顯示四項資訊：客戶名稱、案件編號、資料庫 provider 標籤（SQLite、DuckDB 或 SQL Server）、上次開啟時間。啟動、返回 picker、刪案成功後的 refresh 與「重新整理本機」一律呼叫 `project.listLocal`，第一份 snapshot 只含 SQLite／DuckDB，不等待 registry 或 lock；SQL Server 案件只有在使用者明示按「同步線上案件」後才由 `project.list` 合併進來。同步取消、失敗或線上逾時不得先清空 Store 的本機 snapshot；本機刷新與同步共用 latest-response generation，離開 picker、建立或開啟案件都會 invalidate，晚到成功與晚到失敗都不得改寫新 view。一次新的本機刷新會把 `online` 重設為 null，畫面顯示「尚未同步線上案件」，不能誤稱線上案件為空。

每列的主要面積是原生開案 button，與尾端的刪除 button 作為同層兄弟控制，不嵌套互動語意。刪除確認框會顯示專案名稱與無法復原的影響；開啟後焦點先到「取消」，Tab 圈限在 dialog 內，Escape、遮罩或「取消」關閉後回原垃圾桶。確認後才送出 `project.delete`。刪除作業結束後，前端先退出 `Ui.run` single-flight，再自動重取本機清單；成功列就地消失，焦點回同位置的下一列（末列則前一列，清單空了則「新增專案」），不要求使用者額外刷新或重啟。picker 另有 inline 錯誤區；本機刷新、線上同步、`project.load` 與 `project.delete` 失敗都在原操作位置顯示可理解、可重試的訊息，不只送往 workflow 的隱藏訊息面板。尚未開案時也沒有可供 `log.append` 持久化的案件。

Picker 不接收或保存舊專案根的搬遷狀態，也沒有舊根提示、搬遷按鈕或相容性操作。`project.listLocal` 與 `project.list` 都只使用目前根；舊根不會出現在前端 state 或 DOM。

三種原生檔案對話框（單選、多選、另存）維持既有 wire shape，前端不傳 initial directory。Application 在每次 action 執行時依 active project 解析目前案件目錄，沒有 active project 就讓 Windows 使用預設位置；因此切案後下一次對話框會跟隨新案件。頁首「開啟專案資料夾」也只傳 `{ target:"projectFolder" }`，Explorer 位置由同一個當下 session 決定。

**工作區三欄版面。** workflow 視圖的 `app-body` 分三欄。左欄在一般桌面上限 196px，高倍率形成的窄 viewport 會依視窗比例收窄；中欄是可捲動的文件流，背景用暖紙色 surface；右欄預設 436px，是常駐的資料預覽，寬度可由使用者拖曳左邊界調整。CSS viewport 在 1360px 以下（含）定義為半屏模式：首次進入或由寬視窗縮入時，系統自動收合右欄；使用者仍可手動展開，這項選擇不會被當下的 media query 覆寫。只有由系統自動收合、期間未被使用者改動者，回到寬螢幕才自動恢復。一般寬度的右欄拖曳沿用 300px 下限，最大寬度則保留 420px 給左欄與中央文件流；等效 viewport 為 720px 以下（含）時，右欄另以 38vw 為上限並隱藏沒有可用拖曳範圍的 resize handle，收合成 34px 直書窄軌仍可用。三欄的 flex item、步驟標題與內容容器都允許收縮／換行，水平寬內容由自己的捲動區承接，不得把 `app-body` 或整份文件推寬。

**目錄與進度（左欄）。** 左欄上半是六步驟目錄（TOC），每一步一列，前面帶狀態記號。步驟能不能點，一律由閘門推導：可達步驟可點，點了走 `gotoStep` 切換並即時保存位置；不可達步驟不可點，並以 tooltip 列出「需先完成」哪些沿路條件。目錄下方有一條進度列，顯示「已完成 x / 6」與一條細進度條，完成步數取自 `ui-core.doneStepCount`，app.js 不自行重定義閘門。

**文件流（中欄）。** 中欄把整份流程當一份工作底稿由上而下攤開：最上面是 workpaper 抬頭（mono 英文小標加 serif 案件標題），其下依序是六個步驟區段，每個區段依 `stepPresentation` 推導的狀態呈現四種樣態之一：

- **完成**：收合成一行摘要，前面打勾。
- **進行中**（＝`currentStepIndex`）：展開，區段內容仍由既有的各步驟渲染模組輸出到該區段的 body。展開的區段底部帶統一步驟頁尾；若有阻塞，頁尾就地列出具體缺漏與恢復方式。條件已備齊時不再重複顯示成功提示。
- **可開始**：收合成一行；可點擊狀態與標題已足以表意，不另加「現在可以開始」提示。
- **鎖定**：淡化的一行，附一句簡短的前置原因（例如「需先完成資料匯入」）。

同一時間只有一個步驟區段是展開的，就是 `currentStepIndex` 指到的那一步。點當前步的標題可以把它收合起來（純 UI 狀態 `stepFlowCollapsed`），再點一次展開。整套四態、完成計數與鎖定原因，全部由 `ui-core` 的 `stepPresentation`／`stepGate`／`isStepReachable`／`doneStepCount` 推導；app.js 只呈現、不放行。

**進入新步驟的捲動與焦點定位。** 中欄 `.content` 是自身捲動容器（`overflow:auto`），切換步驟時 app.js 會整份重建內容。workflow 視圖或當前步驟真的改變時，系統會把中欄捲回頂部、將焦點移到新當前步驟標題，並透過一次性 live region 宣告新位置。回 picker 會清掉前一次定位，回開同一步也會重做這套動作；同一步驟內的資料重繪不動捲軸、不重複宣告，但若焦點已在步驟標題，重繪後會回到新標題節點。完成、可開始與當前收合列都有明確 `:focus-visible`；這些都不改現行閣門或 `project.saveProgress` 語意。

**訊息面板。** 訊息面板住在 workflow 左欄的底部。它預設收合成一道橫向窄軌，只露出「訊息」標籤和一個未讀徽章；收合期間若來了警告（warn）訊息，徽章會轉成訊號紅。點一下窄軌就展開成含標題與訊息清單的面板，面板頂端有「複製紀錄」與「收合」兩顆按鈕。開案後各個 action 的成功摘要與錯誤訊息會進到這裡；GL／TB 匯入與配對、科目配對／授權編製人員／假日／補班匯入、Validation／Pre-screening／篩選與正式報表等指定長作業，會在結算訊息保留 monotonic 總耗時。失敗或取消的同一列另帶最後一筆可見進度，不把每一筆 progress 或 NDJSON 寫入 `app_message_log`。訊息會持久化到專案資料庫裡（`log.append`，每個專案保留最近 500 則），下次 `project.load` 載入專案後再用 `log.recent` 還原回面板；append 使用按序且可重試的 promise queue，只有後端確認寫入後才推進持久化水位，切案時舊 queue 不得污染新案。按「複製紀錄」會先等待目前 queue settle，再以 `log.recent(limit:100)` 取得最近紀錄、轉成時間升冪的 `project_code<TAB>database_provider<TAB>occurred_utc<TAB>level<TAB>text` TSV，並把五欄內的 tab／換行正規化成空白。前兩欄逐列直接鏡射 `project.load` 已放入 `Store.project` 的案件編號與 canonical provider（`sqlite`／`duckdb`／`sqlServer`），不轉顯示標籤、不另算案件身分；formatter 沒有 active project 時兩格留空，且不呼叫 `log.recent`。複製流程在 queue settle 前、讀取最近紀錄前與 response 後都重驗同一個 `Store.project` session；其間若切案就不寫 clipboard，按鈕回饋「案件已切換，未複製」，不得把另一案紀錄套上舊案識別。成功後按鈕暫顯示「已複製」。停在 picker 時沒有開啟中的專案，訊息面板也隨 `app-body` 隱藏；picker 操作錯誤改由獨立 inline 錯誤區呈現，不假稱已寫入某個案件的 log。

**資料預覽面板（右欄）。** 資料預覽服務 GL 與 TB 的來源原貌、標準化後資料、科目配對、授權編製人員清單、日期維度，以及資料庫結構總覽。五個直接頁籤依使用者核定語彙成對顯示為「GL 原始資料」＝`glStaging`、「GL 有效母體」＝`glEntries`、「TB 原始資料」＝`tbStaging`、「TB 標準化資料」＝`tbBalances`，並把「科目配對」＝`accountMappings` 保持直接可見；顯示名逐字鏡射 Domain 權威，wire key 不變。授權編製人員、日期維度與結構總覽收在「其他資料」。按鈕與選單使用持久 DOM，不因 render 被替換；按鈕維持正確 `aria-expanded`／`aria-controls`，選單與三個項目可用滑鼠、上下方向鍵、Home／End 操作，Escape 關閉後焦點回按鈕，Tab 離開時正常關閉。頁籤列可換行，右欄拖窄時仍不裁掉主要資料集；表格的水平與垂直溢出只由 `.data-preview__body` 捲動，不得傳到中央文件流。切換頁籤才抓該資料集，維持 `query.dataPreview` 的有界預覽。科目配對預覽讀最新匯入批次的 staging 原貌，因此全空白分類的合法範本仍列出科目代號與名稱，分類顯示空值；它不會因 target 為空而顯示整張無資料，也不會補成 `Others`。

**資料落地後的自動刷新原則。** 面板訂閱 `state.dataGeneration`；只有後端寫入成功且改變可預覽資料後才遞增。訊號一變就作廢快取：面板可見時自動重抓作用中的資料集，收合時延至下次展開。每張 request 一發出，右欄立即以具資料集名稱的「正在載入」取代舊表格並設 `aria-busy`；當前 request 失敗時改為就地「載入失敗」與原生「重試」鈕。成功與失敗都只能由同一 latest-response ticket、案件、資料世代與資料集全數符合的回應收口；過期成功與過期失敗不得污染新畫面。同一同步輪的多次世代變動以 microtask 合併成一次，避免載入專案時連發查詢。右欄仍可收合、拖曳調寬，寬度只以 CSS 變數就地生效，不進 state 或後端 payload。步驟內的預覽按鈕會展開右欄並切到對應的直接頁籤；匯入前的單一來源預覽仍留在匯入精靈。

**流程總覽視窗（4b）。** 頁首的「流程總覽」鈕會開啟補充 modal，讓使用者不用離開目前步驟就掌握案件。它是案件狀態登錄／監看面，不是第二套流程頁。資訊順序固定為摘要優先：案件名稱、案件編號與查核期間；完成／可處理／等待前置的案件進度；六階段狀態登錄；所選階段的單一執行紀錄；四個目前有效的母體事實。其後才是預設全部收合的三組「母體分析」：常用母體彙總在前、分錄金額分布居中、逐筆輔助訊號分布在後；最後是「技術資訊」折疊區與一句精簡解讀。`runId`、`logicVersion` 與資料庫 provider 只放在技術資訊，不和案件摘要爭奪第一視線；最近訊息不在總覽重複顯示。

六個階段控制是 modal 內的狀態檢閱頁籤，不是流程捷徑。每張卡只放步驟識別、文字狀態，以及必要時獨立的「目前主畫面」位置標記；匯入列數、時間、模式、報告與產物等證據只屬於下方單一「執行紀錄」，不在卡片再抄一次。點選或用左右方向鍵、Home／End 切換，只更新該面板，不關閉總覽、不改 `currentStepIndex`，也不呼叫 `project.saveProgress`。即使階段尚未解鎖，仍可選取以閱讀前置原因。主畫面的左側目錄與文件流繼續承擔流程切換；總覽不再複製這項職責。

總覽只讀既有 state，不持有業務流程狀態、不建立 action，也不複製完整性權威判定。階段生命週期固定為「完成／可處理／等待前置步驟」，並以文字與符號雙重表達；`currentStepIndex` 只是主畫面位置，不能冒充「進行中」。前五階段完成度沿用既有下一步 `ui-core.stepGate`，最後階段則沿用匯出頁對目前版本 `CriteriaSelectionReport` 與 `WorkingPaper` 的精確來源比對；不得由主畫面位置、任意舊報表或單一情境數量推算完成。結果失效或版本不相容時寫「目前版本需重新執行」，缺值略去或寫尚無紀錄，不以 0 冒充執行結果；紅色只留給真正的失敗／警示。

畫面採「一項可見事實、一個區塊負責」：案件編號與期間只在抬頭；案件進度只呈現三類狀態的總數；階段卡只呈現個別生命週期；執行紀錄只呈現所選階段的既有 evidence／blocker；母體概況只呈現 validation stats 的查核期間分錄、傳票數、借方總額與貸方總額（借貸淨額只作貸方卡註記）；三組分析共用一次「全查核期間」範圍；provider／run ID／logicVersion 只在技術資訊。這些不是有 target／threshold 的績效 KPI，而是可追溯的母體事實。尚未執行驗證時顯示缺少母體資料的空狀態，不以 0 冒充已核對。完整性適格與恢復原因仍以後端 `eligibility` 為唯一權威；逐項處理仍留在「資料驗證與測試」。

三個分析區都用原生 closed `details`。圖表容器在使用者展開後才插入並建立 ECharts；收合、重繪或 modal 關閉時即 dispose，避免背景圖表占用版面與資源。字型與 ECharts 5.5.0 都從 `wwwroot` 本地載入，沒有 CDN 或執行時下載。每個分析 body 是自己的水平捲動邊界，窄 viewport 不得把 modal 或底層 workflow 推寬。

逐筆輔助訊號圖只納入後端裁定適用的規則；`naReason` 非空者移到圖表外的「不適用」清單並保留原因。適用但零命中仍保留在圖表中，以 `0／0／0.0%` 表示；母體為零時比率維持 `—`，不得和不適用混為一談。金額分布仍純鏡射後端 15 列 count／ECDF；常用母體彙總仍純鏡射後端編製人員與低頻科目資料。兩個彙總檢視是完整 ARIA tabs：左右鍵、Home／End 切換後維持 roving tabindex、tabpanel 關聯與 modal 內焦點。前端不從 counts 重算百分比、累積值、風險分數或審計結論。

總覽的固定解讀取自 `prescreen.run.positioning.overviewGuidance`，明示彙總只描述母體分布、逐筆命中不等於錯誤且不是高風險裁定，再補「不適用不等於零」；不重複長篇免責文字。背景遮罩、右上角關閉鈕與 Escape 都能關閉；開啟後焦點進入 modal，Tab／Shift+Tab 被圈限在 modal 內，關閉後回到原觸發按鈕。這些 `overview-open`／`overview-close` 等識別字都是純 UI，不進 wire payload。

**作業進行中的整介面阻斷（作業序列化防呆，2026-07-08；取消與進度 2026-07-10／2026-07-22）。** 所有後端呼叫都經 `Ui.run(label, factory)` 包裝，它是**權威 single-flight**：若已有作業在跑（`state.busy` 為真），第二次 `run` 不重複送出，並留下一則「目前作業仍在處理中」的 info 訊息，讓再入不再是無聲 no-op。作業一開始，app.js 立刻對 `<main data-jet-root>` 整棵子樹設 HTML 標準屬性 `inert`——一次讓頁首、目錄、文件流、右欄預覽、流程總覽視窗、開發面板全部無法以滑鼠、鍵盤或焦點互動（取代先前只擋 `.content` 滑鼠點擊、鍵盤與其他區域全繞得過的 `pointer-events:none`）。可見遮罩刻意延遲約 200ms 才顯示；它位於 inert 主樹之外，會顯示 `import.progress`／`mapping.progress`／`export.progress` 的輕量進度與〔取消作業〕。正式匯出的 formatter 嚴格只接受 manifest 六欄與四個 phase，顯示 artifact、phase、實際工作表、已關閉張數、累計列數與耗時；它不使用總量、不顯示百分比、不從 phase 宣稱完成，未知或不合法 payload 直接忽略。`JetApi.invoke` 會通知真正送出的長作業 `requestId`（即使呼叫端已接過 `.then()`），app 只追蹤 GL／TB 匯入、配對、驗證、預篩選、篩選預覽／提交，以及 Validation 批次、AccountMapping、Pre-screening、CriteriaSelection、WorkingPaper 五個正式匯出 action，避免同時送出的 heartbeat／log／一般 query 覆寫取消目標；該 ID 只供 `operation.cancel` 指定目標。取消請求本身直接走 concurrent action，不經 `Ui.run` 的 single-flight；按下時先用當下 `busyDetail` 記一列「已要求取消」，等目標 action 最終回應後，再以同一 monotonic clock 寫入總耗時與最後可見進度。`requested:true` 只表示已送出合作取消，完成仍以目標 response 為準；`operation_cancelled` 顯示 info 而非失敗，其他失敗也以同一結算格式保留耗時與最後進度。作業結束即解除 inert、隱藏遮罩。這層是**縱深防禦與體驗**，權威仍在後端序列化閘（見 manifest 的 `operation_in_progress`／`operation_cancelled`）：後端對變更型動作同一時間至多放行一項，第二項回 `operation_in_progress`；唯讀與取消 action 可併行。`state.busy`／`busyLabel`／`busyDetail`／`activeRequestId` 為純 UI（不 bump、不持久化、不進業務 payload）。**背景唯讀刷新的例外**：常駐資料預覽的世代自動刷新（匯入／配對提交／載入專案後）會在外層作業的 `run` 仍持有 busy 期間觸發，若也走 `Ui.run` 會被 single-flight 吞掉而顯示陳舊預覽；因此資料預覽的刷新改走 `Ui.runBackground`——不佔 single-flight、不設 busy、不觸發遮罩。各讀取點先用共用 latest-response guard 丟棄已切案、已換資料世代或已被新版請求取代的成功與失敗回應；只有目前請求的失敗才交由 `runBackground` 顯示。使用者手動切頁籤發生在非 busy 時（作業進行中右欄已被 inert 擋住），走同一路徑。

**長作業的本機時間感（2026-08-20）。** busy overlay 對所有長作業以本機 monotonic clock 顯示「已經過 mm:ss」。`validate.run` 與 `prescreen.run`（含匯出面先補跑的預篩選）另以精確 action identity 顯示「大型案件可能需要較長時間，可取消」；不以 busy label 或錯誤文字猜測，不推算百分比、剩餘時間或後端 phase。

## 5. 流程總覽（六步）

```mermaid
flowchart LR
  picker["專案選擇"]
  s0["0. 建立案件"]
  s1["1. 匯入資料"]
  s2["2. 欄位配對"]
  s3["3. 資料驗證與測試"]
  s4["4. 進階條件篩選"]
  s5["5. 匯出底稿"]

  picker --> s0 --> s1 --> s2 --> s3 --> s4 --> s5
```

閘門模型（`ui-core.stepGate`；線性流程，進入第 n 步須滿足沿路所有閘門）：

| 步驟 | 進入條件 | 完成（推進下一步閘門）條件 |
|:---|:---|:---|
| 0 建立案件 | 預設入口 | 已建立或載入案件 |
| 1 匯入資料 | 有案件 | GL 與 TB 皆已匯入 |
| 2 欄位配對 | GL/TB 已匯入 | GL 與 TB 配對皆已提交 |
| 3 資料驗證與測試 | 配對已提交 | 後端完整性裁定適格；風險預篩選為本步驟內可自由選用的輔助程序 |
| 4 進階條件篩選 | `lastRuns.validate.completenessTest.eligibility.isEligible = true` | 已保存至少一個篩選情境，並有目前版本的條件篩選結果與 `CriteriaSelectionReport` |
| 5 匯出底稿 | 已保存至少一個篩選情境，並有目前版本的條件篩選結果與 `CriteriaSelectionReport` | （最後一步，無下一步閘門；匯出功能已實作，見 §11） |

使用者目前停在哪一步，會用 `project.saveProgress` 即時保存，而且允許往回退到前面的步驟。下次用 `project.load` 載入時，會 resume 回到上次的位置。但如果此時資料已經不再滿足某個閘門，就退回到最近一個還能進入的步驟。

## 6. 步驟 0：建立案件

- 使用者先輸入案件的基本資料，包括案件名稱、案件編號、客戶名稱、操作人員、查核期間、期末財報準備日，然後送出 `project.create`。其中案件名稱不只是顯示用：它同時被當成專案資料夾的名稱，也被當成內部的 projectId，取代了過去用雜湊值的做法。案件名稱會經過字元白名單驗證，同名的案件會被擋下來。前端要求這個欄位必填；只有在「以程式化方式建立、且沒填名稱」這種情況下，才會回退去產生一個 GUID。
- 使用者要從下拉選單選擇資料庫 provider。選項有三個：SQLite（資料存在本機檔案，是預設值）、DuckDB（本地・分析型，同屬一案一資料夾的本機檔案，2026-07-07 納入）與 SQL Server（連到共用實例）。所選的 provider 會放進 payload 的 `databaseProvider` 一起送出。這個選擇一旦建立就不能再改。要注意：選 SQL Server 的前提是後端的連線設定已就緒（單一 `appsettings.json` 的 `Sql:*`，或環境變數 `JET_SQLSERVER_CONNECTION` 覆寫——見 guide §13）；如果沒設好，建立時會得到 `sql_server_not_configured` 錯誤；連到的引擎若是 Express／LocalDB 則得到 `sql_server_express_unsupported`。
- 建案失敗時，表單動作區就地顯示 `role="alert"` summary，直接鏡射後端安全訊息並保留操作紀錄。錯誤 wire 的選填 `field` 只有後端能明確歸屬 payload 欄位時才提供；現行 `project.create` 只對名稱格式、同名資料夾／並行發布與 SQL Server 同名後端殘留回 `field:"caseName"`，前端此時才標示並回焦案件名稱。其他錯誤聚焦 summary；前端不得由 `invalid_payload`、其他欄位是否非空或自由錯誤文字自行推測欄位。空欄仍由原生 `required` 處理。
- 如果案件已經存在，畫面改為顯示一份唯讀摘要，裡面包含當初選的 provider。案件建立成功後，系統會自動 `project.load` 載入它，進入工作流程。

## 7. 步驟 1：匯入資料

這一步的入口收斂成一份四列的任務清單，一列一件事：GL（總帳明細）、TB（試算表）、授權編製人員清單、日期維度（假日/補班日）。每一列的組成固定是：狀態符號＋名稱（GL/TB 標「必要」，授權清單與日期維度標「選用」）＋一個 mono 的關鍵數字（例如已匯入列數、已匯入人數）＋單一動作鈕。動作鈕的字樣依項目而定：GL 與 TB 是「管理來源」，授權清單在尚未匯入時是紅字「匯入」（提示這是主要的下一步動作）、匯入後是「調整」，日期維度是「調整」。按下動作鈕會就地展開該列對應的既有卡片完整 UI——底下描述的多來源精靈、上傳、預覽與成功態橫幅全部原樣保留，只是入口從四張並排的卡片改成四列可展開的清單。所有組態都不顯示「套用測試案件」或等價 mock 入口。這是呈現重組，不是 wire 或匯入行為變更。（科目配對的匯入卡自 2026-06-23 起移到步驟 3「資料驗證與測試」的後段，見 §9——它的序位跟著「驗證跑過才解鎖科目配對相關功能」的流程走。）

### GL / TB：多來源匯入精靈

一個資料集就是一個匯入批次。一個批次可以由多個檔案、或多張工作表合併組成（見 guide §3.1.4）。匯入卡片是一台狀態機，有三個互斥的狀態。設計上有一條鐵律：入口鈕和〔開始匯入〕鈕永遠不會同時出現在同一個畫面裡。三個狀態如下：

| 卡片狀態 | 顯示內容 | 可用動作 |
|:---|:---|:---|
| 空狀態（尚未匯入） | 「尚未匯入。可由多個檔案或多個工作表合併成一個資料集。」 | 僅〔選擇來源檔〕 |
| 已匯入摘要 | 已匯入列數/欄數/來源數、來源清單（檔名、工作表膠囊、編碼/分隔符、列數、時間） | 〔加入來源〕（`append`）、〔重新匯入〕（`replace`）；**不顯示**〔開始匯入〕 |
| 匯入工作區 | 模式橫幅＋待匯入清單＋進度條 | 〔選擇來源檔／再加入檔案〕、〔開始匯入（n）〕、〔取消〕 |

在空狀態下，按〔選擇來源檔〕會直接以 `replace` 模式打開工作區，而且立刻彈出檔案對話框，不需要再點第二次。一旦進入匯入工作區，原本的摘要面和入口鈕就都不再渲染。

工作區頂端有一條模式橫幅，用來標示本次匯入是哪一種模式。`replace`（重新匯入）是破壞性操作，所以用警示色，並說明三件後果：現有的 N 列會被新來源整批取代、欄位配對必須重做、先前的測試與篩選結果也會被清除。`append`（加入來源）用藍字，說明的後果是：新資料會附加到現有的 N 列後面、欄位配對同樣必須重做、先前的測試與篩選結果也會被清除。如果是首次建立這個資料集，橫幅也用藍字。

工作區流程：

| 觸發 | UI 行為 | 後端 action |
|:---|:---|:---|
| 選擇來源檔（可多選，可累加） | 原生多選對話框，逐檔附加到待匯入清單 | `host.selectFiles` |
| 逐檔預覽（待匯入清單） | xlsx 逐工作表列出（可勾選）、CSV 顯示偵測到的編碼/分隔符（可下拉覆寫） | `import.inspectFile` |
| 逐來源〔預覽 ▸〕 | 每列展開「表頭＋前 10 列原貌」的有界小表（≤10 列） | `import.previewFile` |
| 開始匯入（n） | 把全部勾選來源一次送出；任一來源失敗時整批不落地、待匯入清單完整保留；勾選歸零時停用 | 單次 `import.gl.fromFile` / `import.tb.fromFile` |
| 取消 | 關閉工作區回到原狀態（空／摘要），無副作用 | — |

逐檔預覽時，xlsx 的每一張工作表會標出「N 欄・約 X 列」。這裡的列數是 `rowCountEstimate` 推估出來的，只供顯示參考，並非精確值。

逐來源預覽展開的那個小表，絕不會載入完整母體，最多只顯示 10 列。表上附了一句提示：「最上方一列是被當成欄名的標頭；若它看起來是資料而非欄名，代表這份檔案可能沒有標頭列」。這句話的用意，是幫使用者用人工判斷這是不是一份無標頭的檔案。預覽結果會依來源各自快取。如果使用者改了 CSV 的編碼或分隔符，對應的快取就失效，系統會重新抓一次。如果展開失敗，這個小表就收合起來。

按下開始匯入後，前端會以一個 action 傳送工作區當下的模式（`replace` 或 `append`）與全部勾選來源；不逐檔呼叫 action，也不在前端合成部分成功。後端把整批來源放在同一 provider transaction：任一檔案或工作表讀取、欄位終檢或資料庫寫入失敗時，本次較早來源不會留下，呼叫前的摘要與資料也保持不變。錯誤訊息指名失敗的檔案／工作表與原因；使用者修正後可用同一批來源直接重試。

匯入進行期間，前端會訂閱 `import.progress` 事件來顯示一條細進度條。訂閱用的是 `JetApi.on`，也就是 manifest 裡所稱的「Host→Web 事件」；事件的 `sourceNo`／`sourceCount` 把進度對回本 action 的待匯入列。xlsx 的進度顯示成「已寫入 N 列（約 P%）」，這個百分比是「該來源已讀列數 ÷ inspect 階段的估計值」算出來的，上限封在 99%；整批到底完成了沒，最終只以 action response 為準，不以百分比或較早來源的事件為準。CSV 沒有估計值，所以只顯示已寫入的列數。精靈一旦結束（不論成功或失敗），就解除這個訂閱。

匯入成功後，畫面顯示來源清單，每個來源列出檔名、工作表膠囊、編碼或分隔符、列數、時間。當「重新匯入」或「加入來源」完成、卡片收回到摘要面時，卡片上會短暫出現一個成功態：重新匯入顯示「剛剛重新匯入」，加入來源顯示「已加入來源」，後面接上完成時間；過幾秒就淡回平常的已匯入摘要。這個確認訊號就直接顯示在卡片上，不會再往訊息面板裡多塞文字。最後要注意一點：匯入動作（不論是 replace 還是 append）都會讓已提交的欄位配對失效。後端會把配對清除，前端同步標記成失效，詳見步驟 2 的狀態模型。

### 授權編製人員清單

查核團隊維護的單欄姓名 `.xlsx`（英文標頭，如 `AUTHORIZED_PREPARER`）。流程是 `host.selectFile` 選檔後送 `import.authorizedPreparer.fromFile`；replace-only、匯入即投影（TRIM、去重）。卡片顯示已匯入的姓名筆數並提供「預覽授權清單」鈕（換位到資料預覽面板）。匯入後解鎖「非授權編製人員」預篩選與對應的篩選條件。

### 日期維度

上傳事務所行事曆 `.xlsx`（假日表與補班表各一顆按鈕，走 `host.selectFile` → `import.holiday.fromFile` / `import.makeupDay.fromFile`；只支援 `.xlsx`，標頭固定在第 2 列）。假日與補班互不歸零；上傳後依賴行事曆的規則結果會失效、需重跑。卡上另有「每週非工作日」的週幾選擇器（不選＝預設週六、日），影響週末過帳／核准規則的判定。Debug／AgentGuiTest 的 demo 管線另保留以 `dates` 陣列餵入的 `import.holiday` / `import.makeupDay` 相容路徑。

日期維度任務列的完成狀態只鏡射 `project.load.importState.calendar` 的兩個後端持久 marker：`calendarImported` 表示至少一次假日／補班 replace 成功（合法零筆仍為 true），`nonWorkingDaysConfigured` 表示每週非工作日曾明示保存；任一為 true 即完成。`holidayCount`／`makeupDayCount` 只顯示摘要，不再推導狀態。匯入被拒絕時 marker 不變；只調整每週非工作日也會完成此選用任務。

## 8. 步驟 2：欄位配對

這一步要做的是把「來源欄位」對應到「JET 邏輯欄位」。三件主要工作都在後端執行：自動建議走 `mapping.autoSuggest`，從 JET 報告還原草稿走 `mapping.restoreDraft`，提交並標準化走 `mapping.commit.gl` 或 `mapping.commit.tb`。GL 有四種金額模式，TB 有四種變動模式，各模式定義見 manifest。切換到 `openClose`／`openCloseBySide` 時，欄位槽會換成該模式所需的期初／期末（借貸）欄；所有欄位組合的驗證仍在後端。

GL 的邏輯欄位清單包含選填的「過帳狀態」。GL 編輯區下方另有一段「標準化政策」，它決定哪些分錄進入測試母體、以及要保留哪些額外欄位；四個區塊都只收集設定，判定與正規化全在後端：

- **核准日**：三選一的封閉選項（沒有核准日／由來源欄提供／與總帳日期相同）。選「由來源欄提供」時必須指派「傳票核准日」；選「與總帳日期相同」時該欄位從可指派清單移除，既有指派同步清空——兩者互斥，畫面不允許同時成立。
- **過帳狀態**：只有指派了「過帳狀態」來源欄才出現。按〔讀取來源值〕以 `mapping.valueProfile` 取回該欄的值、各值筆數、空白筆數與是否只列出前幾種；審計員勾選代表「已過帳」的值，另可勾選「空白也視為已過帳」。值太多而被截斷時，可另以輸入框補上未列出的值。至少要有一個接受值或接受空白，否則不能提交。
- **人工／自動分錄代碼**：只有指派了「人工/自動分錄」來源欄才出現。同樣以來源值分布逐值指定「人工／自動／不歸類」，並以可移除的標籤顯示目前兩組代碼、可手動補值。預設是人工 `1`、自動 `0`。兩組各需至少一個值且不得重複（比較時先去除前後空白、不分大小寫）。
- **攸關資料元素欄位**：列出所有「沒有被核心欄位配對佔用」的來源欄，勾選即一併保留，並可填顯示名稱、選型別（文字／日期／金額）。沒有勾選的來源欄不會被保留。新勾選的欄位不帶識別字，由後端產生；提交成功後前端以 response 的正準設定取代草稿，下一次重新提交才會沿用同一組欄位身分。

這些設定未補齊時，〔確認配對〕停用並在按鈕上方列出還缺什麼。這只是就近引導，後端 `mapping.commit.gl` 仍是權威驗證。已提交狀態的摘要卡另以一行複述目前政策（核准日模式、過帳狀態接受值、人工／自動代碼、保留了哪些額外欄位），內容直接取自後端回傳的正準設定。

案件的欄位配對如果還是舊版本，步驟頂端會出現說明橫幅，並鎖住「資料驗證與測試」以後的步驟——後端在驗證、預篩選、篩選與匯出前一律以同一條件擋下，畫面只鏡射它並指路回這一步重新確認。GL 與 TB 各自成功重新確認後即解除，不必重開案件。

步驟頂部的〔從既有報告載入配對草稿〕只在目前案件已有 GL 與 TB 匯入批次時可用。它先用 `host.selectFile` 選擇 `.xlsx`，再把路徑交給 `mapping.restoreDraft`；只接受 JET P2 起產生的 ValidationReport 或 WorkingPaper。後端會讀固定工作表的隱藏、版本化 metadata，整批驗證 GL／TB logical key、兩個 mode 與目前來源欄；metadata v2 另帶 approval-date mode、nullable posting policy、manual／automatic policy 與 RDE field definitions。Reader 同時接受 strict v1 與 v2，writer 只寫 v2；v1 讀取後以明示的相容預設正規化為 format v2 draft，但仍是待確認資料，不會自動 commit。舊檔缺 marker、任一側格式錯誤或欄名不相容時整份拒絕，前端不從可見的「配對前／後」欄猜測。成功時前端以一次 state transition 同時替換 GL／TB draft 與 mode，保留既有 committed snapshot，所以已提交案件會進入「草稿偏離」狀態；畫面明示仍須分別確認配對。這條路徑不呼叫 `mapping.commit.*`、不投影資料，也不推進步驟；backend 只暫時授權同 project／current GL batch 的完整 canonical RDE definitions 在下一次 commit 沿用報告內 stable IDs，batch 改變或 definition 被前端改寫都須重新產生／驗證。

`project.load` 回傳 mapping v2、project taxonomy、`mappingReviewRequired` 與 `{ validation, prescreen, filter }` stale state，前端全部原樣鏡射。taxonomy snapshot 精確為 `{ revision, categories:[{ categoryId, label, ordinal, semanticRole, isBuiltIn }] }`。`mappingReviewRequired` 只由已存在且仍是 v1 的 GL／TB committed mappings 推導；缺少 mapping 不算舊版，restore draft 也不會清除，舊的兩側 mapping 必須各自重新提交。前端保存每一側的來源版本，成功重新提交後依同一條規則重新推導這個旗標，讓修復路徑不必重開案件；但案件載入當下一律以後端回傳值為準。三個 stale 布林只能鏡射，不可從 `latestRuns == null` 或報告的 stale 狀態自行推算。

資料預覽多一個資料集「未進入測試母體的分錄」（`glExcludedEntries`）：固定十欄、只讀被排除的列，
並在底部註記兩類排除筆數。排除原因是後端的封閉值，畫面只做顯示層對照（不在查核期間／過帳狀態不符）。
`glEntries` 仍只顯示有效母體。`query.sourceQualityPage` 目前只回成功產生世代的空白過帳日 findings，
硬性的資料品質錯誤仍由提交失敗回傳。

在草稿狀態下，前端提供兩種可以互相切換的配對介面：簡易清單與對照表格。切換鈕不放在步驟頂部，而是在 GL 與 TB「各自的」編輯區頂部各擺一顆——因為 GL 與 TB 上下排列，把切換鈕就近放在每個資料集旁，使用者不必為了切介面而捲回頁面最上方、失去檢閱焦點。兩顆鈕共用同一份偏好，切任一顆兩邊一起換。預設是簡易清單。使用者選了哪一種，會在這次 app session 裡記住，連換專案都保留；但重開 app 後會回到預設的簡易清單。這兩種介面只是同一份配對草稿的不同視圖，底層共用同一份 `draft`，所以切換不會掉資料：在一種介面改到一半切去另一種，已選好的對應都還在。（已提交的摘要卡是唯讀、沒有可切換的編輯介面，因此該狀態不顯示此切換鈕。）

**對照表格的橫向捲動守恆。** 對照表格採加寬版面、欄多時需橫向捲動。每指派一次欄位會觸發整步重繪，預設會把橫向捲動歸零；前端會在指派前記下當時的捲動位置、重繪後還原，讓使用者停在原本檢視的欄位，不必反覆左右捲動。此守恆只在「由指派欄位引發的重繪」生效；切換金額／變動模式會實際改變欄位組成，捲動仍自然回到起點。

**簡易清單。** 每個 JET 邏輯欄位佔一列，右側有一個下拉，用來挑它對應的來源欄。清單下方與對照表格共用同一份目前模式資格判斷，顯示同一條必填欄位鐵軌與缺欄提示；必填未齊時〔確認配對〕停用，不能靠切換介面繞過。

**對照表格。** 這種介面把配對動作直接做在資料本身上。畫面長得像一張試算表：每個來源欄是一直行，欄的標頭正上方放一個下拉，由它選定這一欄要對應到哪個 JET 邏輯欄位。標頭下方直接鋪上該批次的前 10 列原貌，重用的是 `query.dataPreview`（GL 取 `glStaging`，TB 取 `tbStaging`）。這樣使用者掃過幾筆實際值，當場就能判斷這一欄到底是什麼。佈局以中央容器實際寬度判定，不以整個 viewport 猜測：空間足夠時二維表與「必填欄位鐵軌」並排，不足時鐵軌移到表格下方。JET 欄位名稱使用 `nowrap`／`keep-all`，來源欄維持合理最小寬度；必要的水平溢出只由表格自己的 wrapper 捲動，不得逐字斷行、壓住狀態卡或把中央文件流推寬。鐵軌只列出目前模式下必填的 JET 欄位。每個欄位還沒指派時，顯示中性灰的「待指派」；一旦指派好，就翻成淡綠的「✓」，並標出它對到的來源欄名。在 side/flag 模式下，借方代碼這個字面值只要填了也算綠。要等到所有必填都變綠，〔確認配對〕才會啟用。

兩種介面有幾條共通規則。第一，下拉裡可選的欄位會隨著模式即時重算；而模式本身由表格上方一個獨立的選擇器來切換。第二，借方代碼是一個字面值、不是某個來源欄，所以它只在 side/flag 模式下才以一個小輸入框出現。第三，指派維持一對一：一個 JET 欄位至多對應一個來源欄，一個來源欄也至多對應一個 JET 欄位。第四，必填還沒補齊時就停用〔確認配對〕；但這只是前端的引導，最終權威仍在後端。最後，不論使用者用哪一種介面，前端的正規狀態都是同一份「JET 欄位 → 來源欄」的 `draft` map，提交出去的 payload 與行為一字未變。

### 每個資料集的狀態模型

| 狀態 | 判定 | 呈現 |
|:---|:---|:---|
| 未匯入 | 無匯入批次 | 警示：請先完成「匯入資料」 |
| 草稿 | 尚未提交 | 頂部介面切換（簡易清單／對照表格）＋上方模式選擇器＋所選介面的編輯區（簡易清單＝每個 JET 欄位一列右側挑來源欄；對照表格＝來源欄為直行、每欄標頭一個下拉選定對應的 JET 欄位、標頭下方鋪該批次前 10 列原貌）＋兩介面共用的必填鐵軌與缺欄提示＋（side/flag 模式才出現）借方代碼字面值輸入＋必填覆蓋檢核＋〔自動建議〕〔確認配對〕 |
| **已提交** | 已提交且草稿與快照一致 | **收合摘要卡**（淡綠）：「已提交，依此配對執行後續測試」＋模式、已標準化列數、提交時間＋**唯讀對照表**（來源欄為表頭、其下標對應的 JET 欄位、再附該批次前 10 列樣本；side/flag 模式另以小字補述借方代碼字面值）；動作只有〔重新配對〕〔預覽標準化資料〕——編輯表格與確認鈕**不渲染** |
| 草稿偏離 | 已提交但草稿/模式被改動（或按過〔重新配對〕） | 編輯表格＋黃色橫幅「下方修改尚未生效，目前仍以已提交版本執行」＋〔重新確認配對〕〔還原為已提交版本〕 |
| 來源變更失效 | 匯入動作清除了已提交配對 | 草稿畫面＋黃色橫幅「來源資料已變更，原配對已失效」 |

在「已提交」狀態下，前端會保存一份完整快照 `{ projectedRowCount, committedUtc, mapping, mode }`。這份快照在 resume 時來自 `project.load.mapping`；如果 resume 當下還不知道標準化後的列數，就把該欄省略。草稿狀態下不再有獨立的「預覽來源資料」按鈕，因為來源原貌已經內嵌在二維表的標頭下方了。摘要卡上的「預覽標準化資料」則保留，按下後換位到資料預覽面板的標準化後資料集。

## 9. 步驟 3：資料驗證與測試

這一步有兩張主要卡：資料驗證，以及「母體概況與輔助訊號」。資料驗證先證明母體是否可用；後者由 GA 自由選用，執行後把「依分錄編製者彙總」與「較少使用之科目」直接放在主要雙卡區，逐筆 row-tag 命中與既有明細分頁則收進次要的「逐筆輔助訊號」 disclosure。它不自行決定高風險範圍，KCT A–J 才是下一步的實務主線。後端完整性裁定適格後，本步驟即完成且下一步可進入；是否執行預篩選不影響 step gate。

每張卡各有自己的執行按鈕，並顯示上次執行的時間。卡裡的每條規則用「中文名＋一行說明＋狀態徽章」三件呈現，不顯示代號。狀態徽章共六種：未執行是灰色、通過或未發現是綠色、命中（值得留意）是黃色、需要處理是紅色、彙總參考是藍色、無法執行則用虛線（無法執行的原因 `naReason` 放在 tooltip 裡）。

- 資料驗證走 `validate.run`，包含四項測試：完整性測試、借貸不平測試、INF 抽樣測試、空值紀錄測試；來源品質則是同一 response 的獨立 `sourceQuality` 區塊，不冒充第五條 validation rule。卡頂統計列讀有效母體 `stats`。統計列下方另有一段「測試母體怎麼來的」：四格複述 `populationSummary` 的標準化後全部分錄、進入測試母體、期間排除與過帳狀態排除，並提供〔檢視被排除的分錄〕直接換位到資料預覽的排除分錄資料集。四個數字一律直接取自後端，畫面不相減、不加總。
- `nullRecordsTest` 的 closed 類別只有空白科目、空白傳票、空白摘要與核准日不在期間。空白過帳日屬「來源品質」項目：徽章讀 `sourceQuality.findingCount`，展開顯示後端的有界樣本（類別、來源檔、來源列號、傳票號、科目、摘要），全量明細走 `query.sourceQualityPage`，不套有效母體的期間述詞。它是獨立計數，不與空值紀錄的四類相加。
- 完整性是否可推進只讀後端 `completenessTest.eligibility`：前端不從 `status`、part(a) 布林、`naReason` 或差異筆數自行組合另一套判定。`isEligible:false` 時，完整性卡與預篩選卡顯示後端 `reason`，停用預篩選按鈕，Step 4 導覽、頁尾與流程總覽沿 `ui-core.stepGate` 同步鎖定。欄位缺漏或舊摘要無法由後端安全 renderer 時同樣 fail-closed，提示重新執行資料驗證；沒有前端或後端人工 override。
- `nullRecordsTest` 的四類異常可能由同一有效列重複命中；若前端顯示其總和，固定標成「異常項次合計」，不得暗示為 distinct row count。`sourceQuality.findingCount` 是獨立的完整 finding 數，不與這四類相加。
- 驗證成功後，前端以 `validate.run.resultRef.runId` 呼叫 `export.validationArtifacts`。`resultRef` 另帶 `logicVersion`，但前端只保存並回放，不自行判斷版本；後端會拒絕缺漏或不相容的舊版本。後端把 `ValidationReport`、`AccountMapping` 與 `INF_Report` 當成同一個原子批次發布；三個檔案未全部成功時，不會留下半批正式產物。前端只顯示三份 artifact 的檔名、時間與大小，不為每列提供資料夾按鈕，也不在成功後自動開啟 Explorer。需要目錄時由使用者明示按頁首唯一的「開啟專案資料夾」。正式報告流程不開啟另存對話框，也不傳遞任何檔案路徑。
- 預篩選走 `prescreen.run`，只在目前 validation 的後端完整性裁定適格時可執行；直接繞過 UI 呼叫 action 仍會得到 `completeness_prerequisite_failed`。兩條 Aggregate 規則固定進主要區；RowTag 項目包括期末財報準備日後核准、摘要特定描述、未預期借貸組合、連續零尾數、週末／假日過帳或核准、回溯過帳、摘要空白、非授權編製人員、低頻編製者與低頻科目，全部留在可展開的輔助區，既有 `query.prescreenPage` 能力不變。
- `prescreen.run.positioning` 的四句文案由 AuditCore renderer 形成，Application 只做 camelCase wire；前端以 `ui-core.prescreenPositioningCopy` 鏡射。舊摘要缺少這個加法欄位時才使用逐字相同的 fallback，不要求為了文案重跑，也不推進 logicVersion。
- 執行預篩選只更新摘要、彙總與逐筆輔助訊號，不自動呼叫報告匯出。完成後另顯示「Pre-screening Report」入口；按下才以目前 `prescreen.run.resultRef.runId` 呼叫 `export.prescreenReport`，只更新報告 metadata，不呼叫 `host.openFolder`。這是「先在這裡產生」的捷徑；不按也可以，步驟五匯出面預設會一併產出同一份報告。這份 run reference 只屬 Pre-screening Report 與預篩選明細，不再參與 Criteria Selection Report 或 Working Paper 的來源判定。
- 這裡的 counts 只是摘要徽章上的數字，不是逐列的明細清單。全量明細走 keyset 分頁：完整性差異、借貸不平、空值四子項、Source Quality、INF 抽樣的詳情分別接 `query.completenessDiffPage`／`query.docBalancePage`／`query.nullRecordsPage`／`query.sourceQualityPage`／`query.infSamplePage`（游標增量、到底隱藏、零商業邏輯）。ValidationReport 因 10,000 筆 Excel 門檻採 summary-only 時，也不會截斷這些 UI 分頁或改寫摘要 count。
- **科目分類卡（本步驟後段、科目配對卡之前）**。分類決定科目配對表的可選項目，也決定哪些條件可以使用。每一列可以改顯示名稱；自訂分類可以選「類型」與移除，內建五類的類型固定、不可移除。已被已保存篩選情境引用的自訂分類，畫面先擋下刪除並說明原因；科目配對本身的引用只有系統知道，因此後端仍以「分類使用中」作最終權威。顯示名稱必填、不可重複（不分大小寫），否則〔保存科目分類〕停用。保存時送出目前的版本號與整份清單（順序即排序），新分類不帶識別字、由後端產生。保存成功會使既有的風險預篩選與篩選命中失效，畫面在保存前就明說這件事；資料驗證不受影響。判定只看分類類型不看顯示名稱：改名不改變任何測試結果，而自訂分類只要類型與內建相同，就會一起參與同一個結果。
- **科目配對卡（本步驟後段）**。科目配對的格式固定為三欄：科目代號、科目名稱、標準化分類。驗證三檔批次已自動產生本次 validation run 對應的 `AccountMapping`；若只需重試這一檔，前端可用同一個 runId 呼叫 `export.accountMappingTemplate`，但不得指定落點。範本的 `List`、C 欄下拉與 A2 說明都由 backend 依目前 project taxonomy 的 ordinal／label 動態產生。審計員填完 C 欄後，先用 `host.selectFile` 選檔（接受 .xlsx 或 .csv），再送 `import.accountMapping.fromFile`。匯入後立即生效，而且是整份替換，不支援 `mode:"append"`；C 欄空白投影為 built-in Others，未知非空白 label／ID 回 `projection_failed` 並整批 rollback。這次匯入不會讓剛完成的 validation run 失效，因為 validation 不依賴科目分類；它只會使既有 prescreen 與 filter 命中失效。匯入完成後解鎖兩項功能：「未預期出現之特定借貸組合」預篩選，以及「科目配對分析」等需要分類的篩選條件。商業規則 join taxonomy 並只比較 semantic role；顯示 label 改名不改變命中。卡上的「預覽科目配對」會直接切到右欄「科目配對」頁籤；即使來源分類全空，staging 預覽仍顯示原始空白列。
- 步驟頂端另鏡射後端的資料版本狀態：`staleState.validation`／`staleState.prescreen` 為真時顯示「先前結果已失效，請重新執行」的說明。這兩個布林只能來自後端，不可由 `latestRuns == null` 猜測。
- 執行結果會從 `project.load.latestRuns` 回放出來；validation v4 summary 原樣帶 `populationSummary`、`sourceQuality` 與 `completenessTest.eligibility`。宣告目前 logicVersion 卻缺任一必要區塊、欄位或型別時，後端把該 run 視為不可回放並回 `latestRuns.validate:null`，不得補造 0、false 或空陣列；前端只提示重新執行資料驗證，不由 JS 補算。
- **「檢視」詳情展開。** 只有狀態是示警、命中或無法執行的項目，徽章旁邊才會出現「檢視」切換；狀態是通過或未發現的項目不顯示這個切換。展開後會在該列下方顯示詳情，詳情內容依項目而異：
  - 無法執行的項目：顯示 `naReason` 說明它為什麼跑不了。
  - 完整性、借貸不平、空值紀錄：顯示後端回傳的有界明細列，每項至多 50 筆。
  - INF 抽樣：展開時才連同欄位定義一起取回。表頭與資料列都由 `query.infSamplePage` 回傳的欄位定義決定——固定欄之後附全部已提交的攸關資料元素欄位——畫面不預設欄序或欄名。
  - 預篩選命中：惰性呼叫 `query.prescreenPage` 取前 50 列；同 prescreen 述詞與本期母體，後續以〔載入更多〕沿 `entry_id` keyset 逐頁接續。首頁會以 runId＋ruleKey 快取，不重複請求。
  - 依編製者彙總、較少使用科目：直接顯示它們各自的彙總列表。

## 10. 步驟 4：進階條件篩選

Step 4 的正式文件 provenance 與 `Pre-screeningReport` 分開：Pre-screening Report 只鎖定自身 prescreen run；`CriteriaSelectionReport` 鎖定 validation、filter revision 與全部情境 positions，不承接 prescreen reference。這不改 filter 計算語意：進階篩選若使用 prescreen 類條件，後端仍在共同有效母體上即時計算同一述詞，不讀先前 prescreen run 的命中結果。GA 沒有執行 prescreen 時也可建立、預覽、保存情境並產生 CriteriaSelectionReport 與後續 Working Paper。

這一步以 KCT A–J 為實務主線，另容許 GA 疊加自訂條件或預篩選述詞。畫面分成「挑選條件」與「彙整調整」兩段，由上而下是三個區塊：① **KCT條件** 選取區——KCT 小組方法學檢核清單 A–J 以卡片呈現、可**複選 toggle**；單規則字母卡（A、C–H、J）的高亮**跟隨作用中組**（該組含此字母才亮），字母被用在其他非作用中組時卡上顯示「也在其他組」的淡標記；「非營業日(I)」卡用黃色「情境層級」狀態，與組層藍色高亮明確區隔；停用卡（B 屬 Phase 2，或 A/C/D 尚未符合各自的科目配對 target 內容資格）標註具體原因而不可點。② **自訂篩選條件** 選取區——查核員自訂的條件型別依「審計意圖」分組以精簡卡片呈現，點一張即新增一條可重複的條件；`accountPair` 與 `specialAccountCategoryPair` 未達 target 至少一筆非空白分類時仍顯示卡片，但停用並註明缺件。③ **建立篩選情境** 彙整調整區——把已選條件組成統一的條件建構器供設定數值與布林邏輯。前端只負責把這些條件組裝成 JSON，本身不評估任何規則。三個區塊上方另有一列統計卡，其中「草稿條件數」依畫面上可見的條件塊計數：非營業日(I) 的情境層級區塊計為一條，不按它底層的兩條規則計。KCT 選取區的計數徽章寫「作用中組已選 N 項」，只計作用中組的組層字母卡，情境層級的 I 與「也在其他組」不計入。

條件建構器採「條件組（set）＋作用中組」的統一模型（2026-06-24 r7–r12 定案）：

- **兩層結構**：一個情境＝一或多個**條件組**，每組是一塊淡底 well。組內條件之間用一個灰階小型的 AND/OR 段控（前綴「條件之間」，該組 ≥2 條件才顯示）；組與組之間用一個藍色大型的 AND/OR 段控，置中跨在水平軌道上（前綴「組間」）。藍色＝情境層、灰色＝組內層，父子層級以色彩與尺寸區隔。這對應後端兩層 AST：組內各規則 `join` 一致、組間用 `group.join`，結合一律左折疊。組間的藍色段控是**單一的情境層運算子**：「＋另一組條件」新增的組直接繼承目前的組間運算子（首次加到第二組時預設 OR），送出 `filter.preview` 或 `filter.commit` 前，前端還會把所有可編輯組的 `join` 收斂成這個運算子（非營業日(I) 的預設組不受影響、維持固定 AND），確保畫面顯示的布林邏輯與後端收到的完全一致。
- **每組的比對範圍**：fieldset 段控提供「同一分錄列」（`matchScope:"row"`，預設）與「同一傳票」（`sameVoucher`）兩個封閉選項。切到同一傳票時，第 1 條明示為「輸出錨點（第 1 條）」；說明文字固定告知「後續條件可由同一傳票的其他分錄列符合」，組內 OR 同時停用且既有 rule join 收斂為 AND。前端只編輯這個 AST 狀態，不找傳票、不展開佐證列；至少兩條規則、同期間 EXISTS 與錨點命中集合仍由 backend validator／compiler 權威決定。舊情境省略 `matchScope` 時顯示並送出既有 row 語意。
- **作用中（active）組**：上方兩個選取區是唯一的新增入口，新條件（KCT 或自訂）一律落入「作用中組」。單組時不顯示作用中樣式；「＋另一組條件」新增後新組自動成為作用中；點某組的中性區域把它設為作用中（左側藍軸＋淡藍底＋「作用中」徽章）。**非作用中組以 CSS pointer-events 鎖定**（滑鼠不可誤觸、點任一處即切換作用中；鍵盤 Tab 仍可進入屬已知次要限制）。非作用中只代表「不是新增條件的落點」，該組條件仍然參與篩選；畫面以較輕的淡化（透明度 0.75）搭配「仍參與篩選・點此設為作用中」提示語與 well 的 title 說明，避免被誤讀成停用。
- **非營業日(I) 是情境層級的獨立黃色區塊**：它本質是「週末 OR 假日」的巢狀 OR，而後端組內左折疊、無子括號，因此 I 不能塞進可編輯組——它自成一組、固定以 AND 接到整個情境，`toWireScenario` 送出時一律把它排到所有可編輯組之後（左折疊下語意才正確）。不要把 I 併入任何可編輯組。
- **藍色 read-back（即時布林式）**：條件清單下方常駐一段藍色回顯，把整個情境寫成精確布林式，例如「（A AND B） OR （C） AND 非營業日（週末或假日）」；OR（情境層）藍粗、AND（組內）灰 mono，與段控同一套色彩語意。值編輯即時刷新（`input` 事件），空組不進布林式。它是純顯示，不評估任何條件。主前端編輯器仍把組內規則收斂為同一運算子；若載入既存或外部 raw AST，而忽略第一條規則後的有效邊同時含 AND 與 OR，read-back 才依每條邊精確左折疊並加全形括號，且判定忽略 join 的前後空白與大小寫。Uniform group 的既有輸出不變。WorkingPaper step3 只列情境編號、名稱、動機與命中筆數（B:E），不再鏡射這段布林式或新增 F 欄；前端條件 read-back 的顯示行為與 filter wire 本身未改。
- **組感知命名**：KCT 情境自動命名依組分段——單組為 `G+H`，多組以 `｜` 分隔（如 `G+H｜J`），I 自成 token 排最後（如 `G｜H｜I`）；動機自動帶入各條 KCT 條件的說明。名稱與動機皆可手改，KCT 情境兩欄選填，一般自訂情境仍必填。手改受保護：一旦把名稱或動機改成非空值，後續勾選或取消 KCT 卡都不再覆寫該欄位；把欄位清空，自動命名就恢復接手。草稿只要仍含 KCT marker 就走選填；最後一個 KCT 被取消或改成一般條件後，兩欄立即恢復必填，不信任可能殘留的 root source。

各種條件型別依「審計意圖」分四組呈現（依 NN/g 的 chunking 分塊原則）：每組一個小標題與該組的條件卡片；型別下拉也用同一套 optgroup 分塊。這套分組純粹是前端呈現方式，型別實際送出的 wire 值（也就是 AST type）並沒有變。19 個條件型別顯示名以 Domain `FilterConditionLabels.RuleTypes` 為正本，`ui-core.js` 的 `FILTER_RULE_TYPES[].label` 只作鏡像並由 `FilterConditionLabelMirrorTests` 雙向把關；quickLabel、分組與 availability metadata 仍由前端持有。§4 命名登錄表只登錄具名審計規則與有對應 slug 的條件，不是所有 AST type 的字典。

| 分組 | 條件型別 | 控制項 |
|:---|:---|:---|
| 風險預篩選訊號 | 預篩選 | row-tag 規則下拉（中文名，含「回溯過帳」；「未預期借貸組合」需科目配對、「非授權編製人員」需授權清單已匯入才出現） |
| 依欄位內容 | 文字條件 | 欄位＋關鍵字（逗號分隔）＋比對模式（包含/完全符合/排除） |
| 依欄位內容 | 文字值清單 | 文字欄位＋每行一值的 textarea＋包含任一值／完全符合任一值＋保留／移除 ASCII 空白；wire 送 `values[]`，不以逗號拆值；每組最多 100 個值，前端 gate 只做就近提示，後端 validator 與每情境 2,000 SQL plan 參數總預算仍是權威 |
| 依欄位內容 | 金額區間 | 金額（絕對值）下限～上限（欄位標籤明示「不分借貸、以金額大小比較」） |
| 依欄位內容 | 日期區間 | 欄位（總帳日期／核准日／傳票日期）＋起迄 |
| 依欄位內容 | 自訂關鍵字 | 摘要含使用者關鍵字（逗號分隔） |
| 依分錄性質 | 借貸限定 | 僅借方/僅貸方 |
| 依分錄性質 | 人工/自動 | 人工分錄/自動分錄 |
| 進階樣態分析 | 自訂尾數位數 | 尾數連續 0 位數（1–12） |
| 進階樣態分析 | 科目配對分析 | 三模式（精確配對/借方錨定/貸方錨定）＋借方/貸方分類多選；target 至少有一筆非空白分類才提供。每一側是一組 checkbox，送出目前專案分類的身分陣列，重複勾選不影響結果（判定只看分類類型） |
| 依欄位內容 | 攸關資料元素條件 | 欄位（欄位配對勾選並提交的額外欄位）＋比較方式＋輸入值；沒有任何已提交的額外欄位時卡片停用並註明「需先在欄位配對勾選額外欄位」 |
| 進階樣態分析 | 考量特殊科目類別配對 | 借方類別 A＋貸方類別 B（皆為多選）＋三模式（借A且貸B／借A且貸非B／借非A且貸B）；target 至少有一筆非空白分類才提供 |
| 進階樣態分析 | 自訂編製人員張數 | 所選母體內編製人員張數 ≤ N |
| 進階樣態分析 | 自訂科目張數 | 所選母體內科目張數 ≤ N |
| KCT 小組條件（獨立分組） | 季末前借記收入（A）／收入無一般對方科目（C）／收入之人工分錄（D）／特定金額尾數（H）／編製與核准同一人（J） | 各為專屬 wire 型別（見 manifest Filter 章節）；A/C/D 需 target 含 Revenue，C 另需至少一個一般對方分類；D 另需 GL `manual`，J 需 `createBy`＋`approveBy` mapping |
| KCT 小組條件（預設按鈕） | 特定人員（E）／特定摘要（F）／空白摘要（G）／非營業日（I） | 重用既有型別的預填規則（text／customKeywords／prescreen blankDescription／prescreen weekendPosting OR holidayPosting），不另立 wire 型別；E 需 GL `createBy`，G 需 `description` mapping |

**攸關資料元素條件的編輯形狀跟著欄位型別走。** 選定欄位後，比較方式只列出該型別可用的封閉選項（文字、日期、金額三套各自不同），輸入框也隨之改變：一般比較一個輸入框、「介於」兩個、「屬於任一值／不屬於任何值」是每行一個值的多行輸入（最多 100 個），「為空白／非空白」不需要輸入值。金額型欄位另需選比較基準（帶正負號或絕對值）。送出時只帶該比較方式真正使用的那一個輸入，其餘一律不送——帶了不適用的輸入會被系統擋下。欄位或比較方式沒選齊時，畫面就近擋下並提示。讀回顯示目前的欄位名稱：欄位只是改名時自動跟進，欄位被移除時退回原本的識別字，型別改變時必須由使用者修正後才能重新保存。

**已保存情境的完整命中明細採用系統端的欄位定義。** 詳情裡的前 10 筆預覽沿用固定欄位；按〔載入更多〕改接完整命中時，表頭與資料列會一起換成系統端回傳的欄位——固定欄之後只附這個情境實際引用的額外欄位。畫面不重建這份欄位定義，也不自行決定欄序。

操作流程：進階篩選的查核母體固定為案件的〔查核期間〕（wire 為唯一正準值 `auditPeriod`），畫面不提供母體切換，也不提供期內／期外條件。建構器仍顯示固定母體說明；若載入舊版情境而沒有可用的 `filterResultRef`，同一位置顯示 stale 提示與〔以查核期間重新保存〕按鈕。接著從兩個條件區挑條件、選每組的比對範圍、設定布林組合並填名稱與動機；KCT 情境仍可自動帶入且可留白。按〔預覽這個情境〕送出 `{ populationScope:"auditPeriod", scenario }`，只要草稿仍含任何 KCT marker，scenario 根層就帶 `source:"kct"`；取回正準 scope、命中筆數、傳票數與前 50 列。按〔保存為篩選情境〕送出 `{ populationScope:"auditPeriod", scenarios }`，最多 10 個。已存／resume 的 KCT summary 沒有 UI marker，重預覽、重存或移除其他情境時由 canonical `source` 保留 KCT 身分；一般情境不送 source。預覽後修改草稿，舊結果立即失效；遲到回應若草稿已變則丟棄。保存成功後清草稿。已存情境詳情、命中快取與矩陣在 commit／移除／載入時重置；舊 definition 可開案回放，但 `filterResultRef` 為 null，必須以現行 logicVersion 重新保存後才可讀命中或產報告。

步驟頂端另鏡射後端的 `staleState.filter`：為真時說明命中已失效、情境定義仍保留，重新保存後即可再取得命中結果。

資料失效訊號依真正依賴切分。GL 重新匯入或 GL 重投影會使 validation、prescreen 與 filter 命中失效。TB 重新匯入或 TB 重投影只使 validation 失效，不動 prescreen 與 filter 命中；但 Step 4 與下游正式產物會因缺目前 validation eligibility 而暫停，直到重跑驗證通過。科目配對、授權清單或行事曆匯入只使 prescreen 與 filter 命中失效，不動 validation。prescreen 失效只使 Pre-screening Report stale；filter 命中失效仍使 CriteriaSelectionReport 與 WorkingPaper stale，即使情境定義與 revision 保留。命中預覽快取與矩陣摘要會作廢並就地重抓，已展開的詳情與矩陣維持展開，不需要使用者重新點開。

`filter.commit` 成功時，response 帶正準 `scenarios` 與 `{ revision, generatedUtc, logicVersion, populationScope }`。revision 是整批已存情境的版本；definitions 與 `result_filter_run` 命中會在同一 provider transaction 發布，任一 materialize 失敗或取消都保留呼叫前的完整 revision 與 hits，前端不採用本次草稿為已保存狀態，修正後可直接重試。`logicVersion` 與唯一正準 scope `auditPeriod` 都由後端戳記；現行 filter 版本為 `filter-2026-08-14-v10`。`scenarios` 與 `project.load.filterScenarios` 共用同一後端 renderer，包含 canonical `source` 與 KCT 留白後的非空留痕替補，前端保存成功後直接採用，不在本 session 顯示空白、reload 後才變名。CriteriaSelectionReport 與 WorkingPaper 以同一 filter revision 為來源，報告內容固定明示「查核期間」。Scope 已封裝於 revision definitions／`filterResultRef`，artifact `sourceRef` 不重複存第二份；任一 run、revision 或情境集合換版時，受影響 artifact 標 stale。這些英文名只存在契約與工程文件，畫面依上方對照表顯示審計員語彙。

**必填驗證的呈現（採全域樣式）。** 一般自訂情境的名稱、動機，與所有情境的「至少一個條件」若沒補齊，前端會在使用者按下〔預覽〕或〔保存〕的當下就近提示，並擋下不送出；KCT 情境的名稱與動機標示為選填，只保留「至少一個條件」的 gate。提示有四個部分：把未填的必填欄位標上紅框、在該欄位正下方加一行紅字、在按鈕上方放一條彙總提示（`form-notice`）列出還缺哪些、把游標聚焦到第一個有問題的欄位。這些提示一律就落在主畫面上，不依賴那道可收合的「狀態與訊息」欄。使用者一旦補齊，提示就即時放寬，不會在使用者還在打字的途中就責備他。這整套都只是前端的引導，最終權威仍在後端，由 `invalid_scenario` 把關。顯示提示的 `setFieldError`／`clearFieldError` 是 filter-step 內部的 helper（目前只有這一步需要，樣式 `.form__input--error`／`.field-error`／`.form-notice` 仍是全域 CSS，其他步驟若日後需要可沿用樣式）。已儲存的情境清單可以逐個展開看詳情：詳情包含一份條件 pill 摘要，以及惰性載入的命中預覽。命中預覽走 `filter.preview`，內容是命中筆數加上前 10 筆的 grid，結果會快取、不重抓。清單裡的情境也可以移除。〔預覽標準化 GL〕按下後換位到資料預覽面板的 `GL` 分錄；這是有界資料觀察，不代表 selector 的篩選母體。〔保存為篩選情境〕是主要按鈕，〔預覽這個情境〕採 ghost 次要樣式，讓兩個動作的視覺層級清楚。最後，科目配對相關選項依 `importState.accountMapping` 的內容事實逐條鏡像：`accountPair`／`specialAccountCategoryPair` 要 `hasAnyCategory`；季末前借記收入與收入之人工分錄要 `hasRevenue`；收入無一般對方科目要 `hasRevenue && hasCounterpart`。只有匯入來源檔、但分類全空白時不會解鎖任何一項。GL mapping 的 D/E/G/J 欄位 availability 不由前端猜測；使用者預覽或保存時，後端以 `JetFieldCatalog` 權威檢查並用「缺少前置資料」指名缺欄。這些只負責前端引導；權威驗證仍在後端，一樣由 `invalid_scenario` 把關。

2026-08-20 起，建立案件的 inline error summary 也重用 `.form__input--error`／`.field-error`／`.form-notice` 全域樣式；審計規則的權威驗證邊界不變。

**移除已儲存情境。** 首次按「移除」只在原列展開第二段確認，逐字說明「將移除此情境，並需重新產生條件篩選報告與底稿」，並保留「取消」。只有「確認移除」才沿用既有 `filter.commit` replace-all 鏈；不新增 undo 或 action。取消後焦點回原移除鈕；成功後回同位置的相鄰列，清單歸零時回清單標題。

## 11. 步驟 5：匯出底稿

Step 5 決定 WorkingPaper 要納入哪些已存情境、以及要不要一併產出 Pre-screening Report，不讓使用者裁切方法學工作表，也不讓使用者指定任意落點。前端在這一步零商業邏輯：

- **選情境與觸發**：依 position 升冪列出目前 revision 內的已存情境，預設全選；送出的是 `scenarioPositions`，不是工作表名稱。前端同時帶入目前有效的 `validationRunId` 與 `scenarioRevision`，而且只在相同來源與全部 positions 的 CriteriaSelectionReport 已存在時開放 WorkingPaper；`export.workpaperStream` 的 payload 不含 prescreen run。任一必要 reference 缺漏、validation stale 或 filter stale 時停用匯出並引導使用者回到對應步驟重跑；後端除 `stale_result` 外，仍在任何 materialize／暫存檔建立前以目前 validation 的 AuditCore eligibility 作 `completeness_prerequisite_failed` 權威守衛。
- **一併產出 Pre-screening Report（預設）**：情境清單下方有一個預設勾選的「Pre-screening Report」選項，說明文字取自 `prescreen.run.positioning.exportDefaultGuidance`（沒有 run 時用同一份逐字 fallback）。有目前有效的 prescreen run 時，按下產生就依序呼叫 `export.prescreenReport` 與 `export.workpaperStream`。沒有 run 時，畫面在按下之前就先以 `exportPendingRunGuidance` 明說「會先執行一次預篩選、大型案件可能要十餘分鐘」，按鈕字樣同時換成「先執行預篩選並產生底稿」；按下即依序執行 `prescreen.run` → `export.prescreenReport` → `export.workpaperStream`。取消勾選只少這一份，走原本單一 `export.workpaperStream` 的路徑。完整性不適格時無法補跑，畫面說明原因並只產出 WorkingPaper。整段是既有 action 的序列編排，三個 action 的 payload、response、provenance 與取消／原子性語意都沒有改變；步驟四的手動 Pre-screening Report 入口也維持不變。
- **完成回饋**：`export.workpaperStream` 回傳 WorkingPaper 的 artifact metadata 與 `sheetStats`。畫面只顯示檔名、產生時間、檔案大小、來源版本與 stale 狀態，不顯示絕對路徑，也不提供逐列資料夾按鈕。六份報告都固定留在目前專案資料夾，每一 kind 只有一個 `{案件前綴}_{報告種類}.xlsx`；同種類重匯時 Store 以新 artifact 整類取代，不保留舊畫面列，也不依賴 WebView/F5 重整。所有匯出流程完成後都留在原畫面；只有使用者明示按頁首「開啟專案資料夾」才呼叫 `host.openFolder({ target:"projectFolder" })`。正式流程不呼叫 `host.selectSavePath`。
- **固定版面與大資料邊界**：後端 `WorkpaperWriter` 固定控制 14 張正準工作表：封面與說明、step1／step1-1／step1-2、條件式 step1-3、step2、step3、step4、step4-1、step5，以及三張自動化工具參考頁；不再產生 `step1-3-1`。step3 只用 B:E，step4 保留 C1–C10 固定槽位並只標記本次所選情境。所有動態資料欄的可見文字、數字與日期都由 backend 依完整格式化資料形成安全顯示寬度，資料 cell 不換行、不縮排、不縮小字型；Legacy 說明與方法學表頭的刻意換行保留。Finalized step4 使用一次 hit-first、ordered forward-only voucher stream，step4-1 使用 prepared materialization 後的一個 ordered row reader；兩者都以 DeleteOnClose 投影 spool 同一遍量寬後再重播到 SAX，不為量寬重新查詢母體。step4-1 的 12 個優先欄、其餘 `_JE`／`_JE_S` ordinal 欄、row-hit tag、native cell type、signed amount、排序與完整資料欄寬都由 finalized backend plan／renderer 唯一形成；frontend 不接收或重建這份 schema。前端沒有工作表 catalog 或 selector，`sheetStats` 只鏡射後端實際輸出的名稱。Public tag-matrix paging actions 仍保留相容，前端不重算規則、不搬運明細列。
- **完成摘要**：目前版本的 `CriteriaSelectionReport` 與 `WorkingPaper` 都存在時，步驟頂端出現一段「案件流程已完成」的狀態區塊，列出兩份產物名稱並提示可按頁首「儲存並結束」保留進度、或就地重新產生。它**只是本步驟內的完成狀態，不是第六個步驟**：`Store.STEPS` 仍是六步（最後一步＝匯出底稿），閘門、`doneStepCount`、`project.saveProgress` 的進度持久化與所有 action 語意都不因它改變。出現條件直接綁既有 artifact 是否存在，區塊本身不做任何審計判定。
- **舊版清理**：最近底稿下方的兩階段區塊仍處理尚未被同種類重匯收斂的 legacy 多版本與 stale 報告。正常的新產檔每 kind 只有一份，因此不會因重跑產生 retention 候選。〔檢查可清理版本〕只顯示後端回傳的候選摘要與逐筆 `reason`；有候選才出現〔刪除 N 份舊報告〕。前端不計算 stale、validity family、保留門檻或 bytes 總和，也不用 browser `confirm()`／`alert()`。Confirm 成功先以 response `reportArtifacts` 替換 Store；成功、取消、revision 衝突或失敗都在 busy 解除後重新 preview。

`project.load` 會回傳帶 `populationScope` 的 `filterScenarios`／`filterResultRef`、`reportArtifacts`，以及 additive 的 mapping v2／taxonomy／mapping review／result stale state，所以重開案件後仍能恢復 revision、母體、六份產物與後端判定的狀態。Artifact 仍沿用每筆 `reportArtifacts[*].stale`；result stale 則是獨立三布林，不用 artifact state 或 null resultRef 代算。artifact manifest 只保存相對檔名與輕量 source reference；前端不保存本機絕對路徑，也不自行推導 scope。

## 12. 資料契約備忘

- 所有對後端的呼叫一律經過 `JetApi.*`。新增一個 action 時，動工順序固定是：先改 manifest，再加進 `SUPPORTED_ACTIONS`，接著寫 handler，最後才動 UI。
- Bridge 的 payload 與 response 都不搬運完整的 GL 或 TB 整批列。內嵌摘要與資料預覽一律有界，通常至多 50 列；需要走訪完整明細時改用 keyset 分頁 action（預設每頁 200 列、上限 500 列）逐頁載入，仍不回傳整個母體。
- 對映到後端呼叫的 `data-bind` 與 `data-action` 識別字屬於契約面的一部分。要重新命名它們之前，先確認 manifest 與測試。純前端的 UI 識別字則不在此列——例如流程總覽的 `overview-open`／`overview-close`、以及訊息面板與資料預覽的收合切換——它們不進 manifest、不進任何 payload，只驅動純 UI 狀態。
- 自動化控制項 identity 固定使用既有語意 hook：`data-action`、`data-bind`、`data-task-toggle`、`data-step-index`、`data-weekday`、表單 `name` 與 ARIA role／name；presentation class、DOM 順序、Tab 次序與畫面座標都不是穩定契約。`data-action` 是 UI intent，不保證與單一 backend action 同名；例如建案送出會依序走 `project.create`／`project.load`，返回 picker 會走 save／release／`project.listLocal`。Harness 由前端 handler 行為鏈映射到 manifest action，深層測試再直接以 action 名稱與 payload 驗證業務邏輯；版面重排只要不改上述語意，就不應要求重寫測試。
- 規則的 wire key（例如 `completenessTest`、`postPeriodApproval` 等）在前端有兩處鏡像：`ui-core.js` 裡的常數，以及 `validate-step.js` 裡的規則卡定義。要改這些名稱，一律 manifest 先行。
- 正式報告一律由後端以 project-local artifact 管理。前端不得接收或傳送 `outputPath`，不得把檔案另存到專案外，也不得以樣本活頁簿作 runtime template。外部非空白參考檔只供本機結構比對；其中的儲存格值、外部連結、圖片與文件 metadata 都不會進入前端 state 或正式產物。
