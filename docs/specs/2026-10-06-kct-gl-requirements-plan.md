# KCT 總帳資料整理需求與新版 JET 對照

更新日期：2026-10-07

狀態：待使用者人工核對與驗收。TB 匯入與配對確認已比照 GL，使用同一失效政策清除下游結果與全部情境。
原 26 個使用情境缺口與 3 個漏列情境已補測，TB 相關舊預期已同步；補測找到的損壞案件無法輸出支援日誌也已修正。
Build、三種 Contract、Public、Documentation、完整 Gui、Package 與 Excel 均通過，首次失敗與適用範圍記在第八節「TB 規則與使用情境補測收尾」。
提交前的唯讀核對後，依使用者裁定修正文件裡的舊 TB 說法與漏記事項，並把兩條工作原則寫進共用規則；
Git 清單因此增為 189 個路徑，使用者已確認清單與改寫後的提交訊息內容。
剩下使用者以兩組合成資料人工核對並回覆驗收結果；內容確認不代表授權暫存、提交或推送。
私人案件人工旗標仍只有合成測試補證。PrivateCase、Provider 與 SQL Server 本輪未執行，下一階段事項依裁定不處理。

## 完成摘要與下一步

<!-- jet-context:start -->
本計畫處理 KCT 簡報的流程、條件與 15 項優化，來源與使用者原話保存在第一到第八節。
兩批開發、各輪修正及七步接續均已完成；2026-10-07 接續收尾將 TB 匯入與配對確認併入 GL 同一失效政策，
補齊重新對照的 26 個使用情境及 3 個漏列情境，另核對 TB 報告過期與失敗回復。
補測也修正兩種診斷日誌匯出不應以案件設定可解析為前提的問題。Working Paper 版面依使用者裁定維持，不新增 Prepared by。

本輪最終 Build、三種 Contract、Public、Documentation、完整 Gui 19 個情境、Package 與 Excel 通過。
Public 共 4,837 個 .NET 測試通過，前端 411 個測試通過；未以補測第一次通過宣稱產品修正。
詳細首次失敗、交叉複核與收據見第八節「TB 規則與使用情境補測收尾」。逐項對照在本機
`artifacts/review/kct-filter-ops/usage-scenario-remap-20261007.md`，不進 Git；120 個原情境已有行為測試，漏列 33 項中 24 項已涵蓋、9 項依裁定排除。

提交前的唯讀核對找到幾處舊 TB 說法與漏記事項，已依使用者 2026-10-07 裁定只修正文件；
「問題用選項問」與「測試依完整使用情境設計」兩條原則寫進 `.agents/harness/development-workflow.md`，
經過見第八節「提交前的唯讀核對與文件修正」。

下一步只等使用者：依 `test-environment-notes.md` 與本機合成資料 README 人工核對人工旗標、非季底期末及 TB 修改接續，
再回覆驗收結果。使用者 2026-10-07 已確認第八節的 189 個 Git 路徑與改寫後的提交訊息內容，並明示不授權任何 Git 動作。
尚未取得人工驗收，不標成整份計畫已完成。
PrivateCase、Provider、SQL Server 與下一階段事項均未執行；不暫存、不提交、不推送。
<!-- jet-context:end -->

## 使用者需求原話（2026-10-06）

使用者訊息開頭附上 PDF 的本機路徑，依規定不寫進文件，其餘逐字如下：

```text
請詳細閱讀我提供的這份PDF，這是KCT小組提供的需求內容，我需要你把這整份內容轉換為下一個工作階段的spec前置，但是嚴格禁止提取任何機敏資料，包括所屬公司名稱、客戶資料等，你應該只針對KCT小組對於JE資料處理(目前更名為GL)的流程有明確理解，並且記錄於專案文件，然後分析目前新版 je tool 是否可以滿足KCT小組期望優化之功能，因為該文件是以舊版 je tool 來解釋KCT的運作流程，但目前新版 je tool 已經有相當大程度的改變，所以你同時也要研究目前新版 je tool 是否可以相容舊版 je tool 的執行步驟和邏輯，尤其有相當多的流程內化至 je tool 而不用來回經手審計員與KCT小組也是優化的方向之一。

對此，請研究:

* 目前新版 je tool 是否可以相容於舊版 je tool 對 KCT 小組所需的執行步驟與邏輯
* 目前新版 je tool 是否可以滿足 KCT 小組期望優化之功能


我與你討論的內容以及你的研究結果將視為下一個工作階段的 spec 文件，因此請確保你的研究完整性，這是目前專案的短期目標，預期可以滿足 KCT 小組的業務需求。
```

## 範圍與來源

規格整理階段交付三件事：KCT 流程文件、本計畫，以及待決事項的本機勾選頁；當時不改產品程式、不新增測試、不動 Git。
使用者後續授權第一批實作的原話、結果與未執行範圍記在第八節。

不做的事：

- 不把簡報原檔、截圖或其中的公司、客戶、人員、單號、路徑、科目代碼與金額放進儲存庫或文件。
- 不在裁定前改寫已裁定的事項，包括試算表分兩個檔案匯入、匯入前的資料前置處理與總帳科目名稱必要配對；
  本計畫只列出 KCT 簡報提供的新證據，請使用者決定要不要重新評估。
- 不處理 SQL Server；本機開發仍以 SQLite 與 DuckDB 為主。

來源與查核方式：

- KCT 簡報，50 頁，原檔只在使用者電腦，不記檔名與所屬機構。文字以本機 `pdftotext` 抽出，
  50 頁截圖逐頁轉成圖片閱讀，條件表、驗證報告、科目配對檔說明與舊工具第三、四步畫面另外放大確認。轉出的文字與圖片
  只放在本 session 的暫存資料夾，不在儲存庫。
- 舊版 JE tool 的原始碼保存在 `legacy/idea-tool.bas`，版本比簡報畫面新，見第三節。本輪只讀與 KCT 條件直接相關的段落，
  例如預篩選 #3、文字篩選與借貸組合。
- 新版 JET 的行為以現行程式為準，並對照 `docs/jet-guide.md`、`docs/jet-frontend-description.md` 與上一份計畫的裁定。
  三個唯讀子代理分別查了 KCT 條件語意、匯入與科目配對、前端操作；影響相容判斷的結論，主線另外讀原始碼確認過。
- 下表的「依據」欄列出程式位置，路徑省略 `src/JET/JET/`。標「推論」的是讀碼得到、沒有實測的結論。

判斷用語：「已處理」表示新版 JET 直接做這件事；「做法不同」表示結果相容但操作或位置不同；「部分」表示只做到一部分；
「JET 之外」表示仍要在 JET 以外完成；「未滿足」表示 KCT 要的功能還沒有。

## 一、KCT 流程逐階段對照

### 1. 確認申請人檢附的檔案

| KCT 的工作 | 新版 JET | 判斷 | 依據 |
|:---|:---|:---|:---|
| 核對公司名稱與申請單相同 | JET 不讀申請單，也不判斷檔案屬於哪家公司 | JET 之外 | 這是派件作業的一部分 |
| 核對 TB 與 GL 的期間 | 第四步「分錄測試範圍」分開列出日期未符合與過帳狀態未符合的筆數；同一傳票號碼出現在多個總帳入帳日時，第一步提醒 | 部分 | `jet-guide.md` 第 4 節；`jet-frontend-description.md` 第 1、4 步 |
| 核對必要欄位都有 | 第三步的必要欄位沒有配對就不能確認。選填欄位沒配對時，KCT 條件會寫出缺哪一欄；自訂條件用到沒配對的欄位，結果是 0 筆而且不提醒 | 部分 | `JetFieldCatalog`；`test-environment-notes.md`；2026-10-04 裁定 C1 |

### 2. 整理檔案

| KCT 的工作 | 新版 JET | 判斷 | 依據 |
|:---|:---|:---|:---|
| 移除 TB、GL 的表頭與小計等不必要資訊 | 欄名固定取第一個有內容的列；只讀到一欄時提醒可能是報表格式。讀檔程式已有略過前幾列的參數，但只有科目配對檔與假日檔使用，總帳與試算表匯入沒有開放 | JET 之外 | `Domain/Contracts/ImportContracts.cs:153`；2026-10-02 裁定 P3-1 |
| TB 期初、期末分成借方與貸方欄，算出餘額與本期變動 | TB 有四種金額模式，可以直接用本期變動，或由本期借方與貸方、期初與期末餘額、期初與期末各自的借貸四欄算出本期變動。KCT 簡報那種有期初借貸、本期借貸、期末借貸六欄的 TB，可選期初與期末的借貸四欄，或只用本期借方與貸方 | 已處理 | `wwwroot/js/ui-core.js:89-94`；`Domain/Rules/TbRowProjector.cs:62-74` |
| 期初、期末分在兩份 TB，用 VLOOKUP 合併並比對兩期科目 | 多檔只把欄名相同的列往下合併，不會依科目編號左右合併 | JET 之外 | 2026-10-01 裁定 D06 不做；2026-07-31 需求銷帳 |
| 兩份 TB 科目代碼格式不同，先整理成一致 | 欄位配對一個 JET 欄位只對一個來源欄，沒有公式 | JET 之外 | 2026-10-02 裁定 P3-1 |
| GL 建立會計科目名稱 | GL 科目名稱是必要配對，沒有從 TB 依科目編號帶入名稱的功能 | JET 之外 | `Domain/Rules/JetFieldCatalog.cs:122`；2026-09-18 裁定 D12 |
| 讓 GL 每一列都有入帳日期與傳票號碼，也就是把表頭資訊往下補 | 空白照原樣匯入，驗證會列成空白傳票號碼或總帳入帳日空白 | JET 之外 | 2026-10-02 裁定 P3-1 |
| 從借方、貸方欄整理出單一傳票金額 | GL 有四種金額模式：單欄正負號、金額加借貸別、金額加借方標誌、借貸兩欄 | 已處理 | `jet-guide.md` 第 2 節；遷移基準指南第 2.1 節 |
| ERP 原始資料表的系統代碼欄名 | 欄位配對可直接選任何來源欄名 | 已處理 | 第三步欄位配對 |
| ERP 原始資料表的數字日期 | 支援 ISO、西元斜線或點號、八位數年月日、民國年與 Excel 序列值。簡報範例那種六位數日期，由年份與當年第幾天組成，JET 不支援：它會被當成 Excel 序列值，超出 2100 年而判為無效，確認配對失敗並列出錯誤列，不會默默讀錯 | 部分 | `Domain/Primitives/DateNormalizer.cs:43-131`；六位數的情形是讀碼推論，沒有測試 |
| 另建「人工/自動」欄，人工 1、自動 0 | 第三步可把來源欄的任何代碼對應人工或自動，也可只列一側；可直接選分錄來源模組等現有欄位 | 已處理 | `Domain/Rules/GlRowProjector.cs:325-374`；2026-10-03 裁定 S2-2「維持現況」 |
| 另建「高階主管 Y/N」欄 | KCT E 直接輸入人員清單，完全相等比對 | 已處理 | 見第三節 E |

### 3. 匯入 IDEA

| KCT 的工作 | 新版 JET | 判斷 | 依據 |
|:---|:---|:---|:---|
| 建立 IDEA 專案，依單號、公司全名與期間命名 | 第一步建立案件：案件名稱與查核期間必填，案件編號、客戶名稱、期末財報準備日選填。報告檔名前綴取案件名稱，再加查核起訖日 | 已處理 | `Domain/ProjectFileNames.cs:11-25`；`jet-guide.md` 第 7 節 |
| 用匯入助手匯入 Excel，第一列為欄名，核對列數 | 直接匯入 xlsx、xlsm、xls、csv、txt、mdb 與 accdb；每個來源先預覽前 10 列，匯入後顯示筆數 | 已處理 | `jet-guide.md` 第 3 節 |
| 逐欄定義資料型態 | 欄位配對與金額模式決定型態；其他欄可勾成攸關資料元素欄位並選型別 | 已處理 | 第三步 |
| 合併多個 Excel 檔，例如分季總帳 | 同一資料集可選多個檔案或工作表，欄名相同就合併成一批；欄名不同仍要先對齊 | 已處理 | `jet-frontend-description.md` 第 2 步 |
| 合併前核對筆數與金額加總 | 匯入顯示筆數，驗證顯示借貸合計；完整性第一項只比 JET 自己存下的數字，不和來源檔的合計比對 | 部分 | `test-environment-notes.md`「目前已知的限制」 |
| 沒有項次時用「新增項次」程式補上 | 沒有配對項次時，JET 依傳票號碼與來源列序自動編 1、2、3；多個來源接續編號 | 已處理 | `Infrastructure/Persistence/Local/LocalGlRepository.cs:308-330`；2026-10-03 裁定 S2-4「維持現況」 |

### 4.1 用 JE tool 確認完整性

| KCT 的工作 | 新版 JET | 判斷 | 依據 |
|:---|:---|:---|:---|
| 輸入客戶名稱、財務報導期間與期末財務報表準備期間開始日 | 第一步填客戶名稱、查核期間與期末財報準備日，建案後可修改客戶名稱、案件編號與準備日 | 已處理 | `jet-frontend-description.md` 第 1 步 |
| 產業別選 General Manufacturing | 新版沒有產業別 | 已處理 | `src` 內沒有產業別欄位 |
| 配對 TB 的科目編號、名稱與金額 | 第三步 TB 配對 | 已處理 | |
| 配對 GL 必要與非必要欄位，含四個自行定義欄位 | 第三步 GL 配對。核准日有三種方式：沒有、由來源欄提供、與總帳入帳日相同；另有人工/自動、過帳狀態、分錄來源模組、建立與核准人員；其他欄可勾成攸關資料元素欄位，數量不限 | 已處理 | `jet-guide.md` 第 2 節 |
| 開 Validation Report，確認第五點為 0 | 第四步執行驗證，畫面直接顯示完整性結果與差異科目；驗證報告沿用舊範本，V_Report 1 到 6 的編號與內容對應。明細上限：第 1 到 4 類 10,000 列、第 6 類 9,999 列，舊工具寫超過 1 萬筆不產生 | 已處理 | `jet-guide.md` 第 4、7 節 |
| 第五點不為 0 時，篩出差異科目回頭查原因 | 差異科目在畫面分頁列出，可排序與搜尋；底稿 Step 1-3 填原因與調節；有差異仍可繼續後續步驟 | 做法不同 | 2026-09-17 使用者裁定完整性有差異仍可繼續 |

### 4.2 設置高風險條件

| KCT 的工作 | 新版 JET | 判斷 | 依據 |
|:---|:---|:---|:---|
| 把完整性結果寄給申請人 | 驗證報告可直接附上 | JET 之外 | |
| 在 Excel 的 List 新增分類，在 AccountMapping 替 TB 科目選分類 | 第四步可在 JET 內搜尋科目、整批勾選或拖曳連選，再套用分類；也可用 Excel 範本整批匯入。分類設定可新增自訂分類與上下層 | 做法不同 | `jet-frontend-description.md` 開頭與第 4 步 |
| 依財務報表在 TB 找出明細科目來配對 | 沒有自動分類 | 未滿足 | 見第四節第 9 項 |
| 上傳科目配對檔 | Excel 範本匯回即可；分類空白視為 Others | 已處理 | 2026-09-07 裁定 |
| 國定假日、週末與補班日 | 第二步匯入假日與補班日清單，並可設定每週非工作日。JET 不內建假日資料；2025 年台灣假日與補班日範本隨程式放在 `Templates` 資料夾，程式沒有用到它，畫面也沒有取得範本的入口。舊工具也要上傳檔案 | 做法不同 | `Infrastructure/Export/ReportTemplatePackage.cs:22-23` 只宣告檔名；`legacy/idea-tool.bas:458` |
| 步驟三預先篩選 | 第四步的預篩選，13 項，選用，不是第五步的前置 | 做法不同 | 見第二節 |
| 步驟四依條件 A 到 E 設定進階篩選 | 第五步的 KCT A 到 J 卡片與自訂條件 | 見第三節 | |
| 查看篩選結果、匯出彙總報告 | 預覽符合條件的傳票，展開整張傳票；已儲存情境與矩陣；條件篩選報告可獨立產生 | 已處理 | `jet-frontend-description.md` 第 5 步 |
| 步驟五勾選條件、逐一輸入評估說明、匯出工作底稿 | 第六步匯出工作底稿；底稿 Step 3 的「選擇此篩選條件的原因」就是情境儲存時的動機 | 做法不同 | `Infrastructure/Export/WorkpaperWriter.Step2To41.cs:293-306`；見第四節第 15 項 |

### 5. 交還申請人

| KCT 的工作 | 新版 JET | 判斷 | 依據 |
|:---|:---|:---|:---|
| 專案檔只留 GL 與 TB，把 JE 回歸成配對前，交還申請人 | 新版的原始資料與配對結果分開保存，不需要回復配對前。交出的是整個案件資料夾：要正常離開 JET 後冷複製；接手的人要把資料夾放進自己的 JET 案件根目錄，預設在使用者資料夾下的 `JET`，不接受網路路徑 | 待確認 | `ProjectStoragePathResolver.cs:42-76`；`jet-guide.md` 第 8 節 |
| 交還專案檔 | 同上。報告的 Prepared by 是建立案件的人，不是匯出報告的人；KCT 建案後交給查核團隊時，報告會寫 KCT 人員 | 待確認 | `Application/Handlers/Project/ProjectCreateHandler.cs:99`；`jet-guide.md` 第 7 節 |
| 在派工系統送請驗收 | 不屬於 JET | JET 之外 | |

## 二、舊版 JE tool 的執行邏輯與新版相容性

| 舊工具功能 | 新版對應 | 相容判斷 | 差異與裁定 |
|:---|:---|:---|:---|
| 驗證六項：無科目編號、無傳票號碼、無摘要、核准日不在期間、完整性、借貸不平 | 第四步的空白紀錄、核准日、完整性與借貸不平；驗證報告 V_Report 1 到 6 | 相容 | 新版另有完整性第一項，核對 JET 存下的分錄筆數與合計；INF 抽樣固定 59 筆，2026-08-28 使用者確認 |
| 預篩選 #1 於期末財務報表準備期間核准之分錄 | 預篩選「財報準備日起核准」，核准日大於或等於準備日 | 相容 | 舊工具同樣比較核准日不早於準備開始日，`legacy/idea-tool.bas:8099`；沒有核准日配對時顯示無法執行 |
| 預篩選 #2 分錄摘要出現特定描述 | 預篩選「摘要特定描述」，預設 25 個詞，含補回的 9 個簡體詞 | 相容 | 自訂關鍵字移到第五步的「摘要關鍵字」條件 |
| 預篩選 #3 未預期出現之特定借貸組合 | 預篩選「未預期借貸組合」 | 相容 | 對方科目同樣含 Receivables、Cash、Receipt in advance，借方門檻同為大於 0。新版只標收入貸方列，舊工具標整張傳票，2026-07-03 裁決；舊工具另要求同一張傳票至少有一筆借方，`legacy/idea-tool.bas:8366-8442` |
| 預篩選 #4 分錄金額中有連續 0 的尾數 | 預篩選固定 6 位連續零；第五步可指定 1 到 12 位 | 做法不同 | 2026-09-18 使用者裁定不恢復依借方平均金額決定位數的舊算法 |
| 預篩選 #5 依分錄編製者彙總分錄 | 預篩選「依分錄編製者彙總」 | 相容 | 人員去空白、不分大小寫，2026-10-04 裁定 C3 |
| 預篩選 #6 較少使用之科目 | 預篩選「較少使用之科目」與「使用較少的科目（11 筆以下）」 | 相容 | 舊工具與新版報告的 R6 都列出全部科目，按分錄筆數再按科目編號升冪，`legacy/idea-tool.bas:8772-8806` |
| 自訂摘要描述、借貸組合、特定尾數 | 第五步的摘要關鍵字、借貸科目組合與尾數條件 | 做法不同 | 位置從預篩選移到條件篩選；預篩選規則目錄已凍結 |
| 步驟四的預先篩選結果 | 第五步的「預篩選條件」，用同一套規則即時計算 | 相容 | 不讀先前執行的命中檔 |
| 預設參數：總帳或核准日在週末、國定假日、排除補班日 | 日期條件：週末、假日、補班日、非營業日與各自的否定，總帳入帳日與核准日都能用 | 相容 | 非營業日是週末或假日再排除補班日 |
| 僅考量借方傳票、僅考量貸方傳票 | 借貸別條件；科目條件也可直接選分錄方向 | 相容 | 兩邊都是逐筆判斷，金額 0 算借方 |
| 篩選人工編制傳票 | 人工/自動條件 | 相容 | |
| 兩組文字欄位篩選 | 文字條件不限數量，可選關鍵字、完整內容相同、開頭、結尾、空白 | 相容 | 新版人員與科目編號清單預設完全相等，見第四節第 6 項 |
| 兩組日期區間 | 日期條件不限數量，另有指定日期、每月幾日、月初月底與查核期末最後幾天 | 相容 | |
| 一組數字區間，可同時限定科目類別 | 金額條件與科目分類條件放在同一組 | 相容 | 核心金額預設比較絕對值 |
| 科目類別配對 Dr A, Cr B、Dr A, Cr not B、Dr not A, Cr B | 借貸科目組合的五種模式，含「借方是 A 且貸方是 B」「借方是 A 且整張傳票沒有 B 貸方」「貸方是 B 且整張傳票沒有 A 借方」 | 相容 | 逐列命中範圍沒有逐一和舊工具比對 |
| 最多十組篩選條件 | 最多十個已儲存情境，每個情境可有多組條件 | 相容 | |
| 步驟五要求每個條件都填理由，否則不產生底稿 | 自訂情境儲存時要填動機；KCT 情境可留白，存成「KCT 小組方法論檢核條件」 | 做法不同 | 舊工具在 `legacy/idea-tool.bas:11548-11563` 擋下；新版見 `Domain/Rules/FilterScenario.cs:281-316` |
| 輸出：Validation Report、AccountMapping、INF、Pre-screening Report、彙總報告、工作底稿 | 五種報告與一份科目配對工作檔；工作底稿每次匯出新增版本 | 相容 | `jet-guide.md` 第 7 節 |

結論：舊工具五個步驟的審計邏輯，新版都有對應，而且條件可以自由組合。不同的地方集中在四類：已裁定的新制差異，例如連續零
尾數；功能換了位置，例如自訂預篩選移到第五步；KCT 條件的定義細節，見第三節；以及 KCT 在 JET 之外的資料整理，見第一節。

## 三、KCT 條件 A 到 E 的對照

KCT 條件的原文與舊工具的設定方式見 [`kct-gl-workflow.md`](../kct-gl-workflow.md#kct-的高風險條件-a-到-e)。新版的 KCT 卡片名稱
與簡報一致，例如 A 的卡名就是「在該季度前 X 天借記收入的會計分錄」。由此推論，現有 A 到 J 清單的 A 到 E 和這份簡報出自同一個來源。

| 條件 | 新版 JET 的做法 | 與簡報、舊工具的差異 | 需要決定的事 |
|:---|:---|:---|:---|
| A 季末前借記收入 | 收入用科目分類的 Revenue 用途判斷，不必輸入科目代碼；逐筆看借方，金額 0 也算；總帳入帳日落在任一季底前 X 天，含季底當天。季底固定是曆年的 3/31、6/30、9/30、12/31，只取和查核期間有交集的視窗；X 可填 1 到 92 | 簡報名稱寫「該季度前」，評估說明與操作範例只看期末一段。新版會多出其他季底。查核期末不是季底時，例如期末是 11 月 30 日，期末前 X 天不在任何視窗內 | K1 |
| B 借記固定資產且貸記費用 | 卡片停用，畫面寫「尚未支援此條件」，原因是等 KCT 提供分類表。現在可以手動做：分類設定新增「固定資產」「營業費用」，配對科目後用借貸科目組合「借方是 A 且貸方是 B」 | 簡報已提供分類：固定資產指不動產、廠房和設備，不含在建資產；營業費用含維修費用等費用科目。分類用途只有五種內建值，沒有固定資產或費用 | K2 |
| C 貸方收入但借方非一般對方科目 | 命中收入貸方列：同一張傳票沒有任何屬於 Receivables 或 Receipt in advance 的借方分錄。現金不算一般對方科目；空白傳票號碼不命中 | 舊工具做 C 是勾預篩選 #3，#3 把 Cash 也算一般對方科目，所以現金銷貨不命中；新版 KCT C 依簡報文字不含現金，現金銷貨會命中。新版的預篩選「未預期借貸組合」仍與舊 #3 相同。審計員若沒把現金科目配到 Cash，兩者結果一樣 | K3 |
| D 收入的人工分錄 | Revenue 用途且人工旗標為人工，不限借貸方；人工旗標由第三步的代碼對應產生 | 判斷方式相同；收入改用分類，不必輸入科目代碼 | 無 |
| E 特定人員建立的分錄 | 傳票建立人員的完整值清單，去頭尾空白、不分大小寫，完全相等；清單可用換行、逗號、頓號或 Tab 分隔 | 簡報說舊工具是包含比對，A00 會篩到 A001。同一人有兩個編號時，新版仍要把兩個編號都列入 | 無 |

其他觀察：

- 簡報的 E 條件名稱寫「傳票編製人員為會計經理」，評估說明與操作寫的是執行長、財務長及高階主管；新版卡名是
  「特定人員(財務長/執行長/高階主管等)建立之分錄」，和評估說明一致，不需要改。
- 簡報第 30 頁的條件讀回寫「值為」，KCT 卻說文字篩選是包含比對。`legacy/idea-tool.bas` 第 3785 到 3788 行註解掉的舊程式，
  正是用包含比對、讀回卻寫「值為」；現存的舊原始碼另有「值包含」「值為」「值不包含」「值不為」四種方式。
  儲存庫的舊原始碼開頭標示 Ver.202607，簡報畫面是 Ver.2018 與 Ver.2022，所以 KCT 用的是較舊的版本；
  四種比對方式可能是之後才加的，這點是推論。
- 簡報只有 A 到 E，並說明是範例。新版的 F 到 J，也就是特定摘要、空白摘要、特定尾數、非營業日、編製與核准同一人，
  仍查不到 KCT 的原始清單，見第六節 K16。

## 四、KCT 期望的 15 項優化

「KCT 原文」照簡報第 34 到 48 頁的標題逐字抄錄。本節是 2026-10-06 的對照；目前的落實狀況以第八節
「KCT 簡報議題的落實核對（2026-10-07）」為準。第 3 項寫的 TB 規則，已由 2026-10-07「TB 修改改成比照 GL」取代。

| 項 | KCT 原文 | 新版現況 | 判斷 | 依據 |
|:---|:---|:---|:---|:---|
| 1 | 日期設定能否自己直接key in或用下拉式選單 選擇年/月/日 | 建立案件的三個日期是原生日期輸入框，可以直接鍵入，也有日曆。第五步的日期區間是文字框，可鍵入 2025/8/1、20250801 等寫法。只有第五步「指定日期」的日曆沒有年、月選單，要逐月切換或用 Shift 加 PageUp、PageDown 跳一年 | 大致滿足 | `wwwroot/js/steps/create-step.js:18`；`wwwroot/js/filter-values.js:367-381,535-566`；原生日期框的鍵入方式是推論，沒有實測；K11 |
| 2 | 產業別功能是否可以不用 | 已沒有產業別 | 滿足 | |
| 3 | 資料欄位設定，TB設定錯誤, 需要先設完GL&跑完完整性才能回頭，可否增加一個【回上一步】的按鈕 | 左側流程可回到任何已開放的步驟；GL 與 TB 各自確認、各自重新配對；重配 TB 只讓驗證結果失效，預篩選、篩選命中與情境保留 | 滿足 | `wwwroot/js/steps/mapping-step.js:1806-1811`；`Domain/Rules/AuditDependencyPolicy.cs:42-43` |
| 4 | 能否直接新增【傳票建立日期】的欄位，而不要另外於分錄來源模組中手動點選來新增 | 沒有固定的「傳票建立日」欄位。新版的三種日期是總帳入帳日、傳票日期與傳票核准日；2026-09-23 使用者採用這些名稱，傳票日期不自行改稱建立日。建立日要勾成攸關資料元素欄位並選日期型別，之後可用在日期條件、INF 與底稿，做法和舊工具的自行定義欄位相同 | 未滿足 | `jet-guide.md` 第 2、6 節；K4 |
| 5 | 人工及自動分錄之欄位，tool目前設定是僅能匯入1(人工)/0(自動)，檢視時較不直觀，是否可以單純寫上【人工/自動】即可? | 匯入端已滿足：任何代碼都能對應人工或自動。檢視端未滿足：存下後是 1、0 或空白，底稿 Step 4-1 寫 1 或 0；Step 1-2 的「自動或人工」欄留給審計員填；資料預覽的「納入測試的分錄」沒有這一欄 | 部分 | `Infrastructure/Export/WorkpaperWriter.Step2To41.cs:896-897`；`WorkpaperWriter.Step1.cs:481`；K5 |
| 6 | 文字篩選時，是以【包含】的概念進行，容易不小心篩選到錯誤資訊 | 文字條件可選關鍵字、完整內容相同、開頭、結尾與空白；人員與科目編號清單預設完全相等；KCT E 用完全相等 | 滿足 | `wwwroot/js/ui-core.js:141-142`；`AuditCore/GlRulePredicates.cs:752` |
| 7 | 【新增項次】能否直接內建，而不要另外手動點選應用程式來新增 | 沒有配對項次時自動編號 | 滿足 | 2026-10-03 裁定 S2-4 |
| 8 | 執行完步驟二~五後不要跳資料夾出來 | 匯出後不會自動開啟檔案總管或 Excel；只有按「開啟資料夾」或「開啟案件資料夾」才開 | 滿足 | `Form1.cs:205-213`，只由 `host.openFolder` 觸發 |
| 9 | 科目配對的時候，目前是人工判斷，並將判斷結果填入acc mapping檔案中，是否有機會改成自動? | 沒有自動分類。JET 內可搜尋、整批勾選、拖曳連選、一次套用分類，也可用 Excel 整批匯入。科目配對檔匯入不檢查科目是否屬於本案，所以前一期的配對檔可以直接匯入，但本案沒有的科目也會寫進去，沒有提醒 | 未滿足 | `Infrastructure/Persistence/Local/LocalAccountMappingRepository.cs:119-190`，匯入行為是讀碼結果；K6 |
| 10 | 科目配對的時候，若檔案有使用【篩選】功能，就會匯不進去 | JET 讀 Excel 時不看自動篩選與隱藏列，推論不會失敗，被篩選隱藏的列也會讀入；沒有測試涵蓋 | 推論滿足 | `Infrastructure/FileIO/OpenXmlSaxTableReader.cs`；K7 |
| 11 | 下圖項目是否有保留之必要(須視其餘案件需求) | 指預篩選 #1 執行太久。新版預篩選是選用，不擋第五步；13 項一次執行。2026-07 的月份彙總實驗用 500 萬列 DuckDB 量測完整預篩選，中位數從約 1.3 秒增加到約 2.9 秒，那不是現行版本的量測。#1 在新版仍保留 | 待決定 | `Application/Handlers/PrescreenRunHandler.cs:31-169`；`history/development-log.md:195`；K8 |
| 12 | 進階篩選條件設定是否可以每個條件各自獨立? | 每個已儲存情境可個別編輯、更新、另存副本與移除；情境內單一條件可個別移除或修改；「新增情境」會清空草稿，不殘留上一個情境的設定 | 滿足 | `wwwroot/js/steps/filter-step.js:1061-1063,1245,1859-1871,2604-2615` |
| 13 | 特定文字篩選，如果是想要放入較多的收入科目, 目前要把所有收入的科目編號都輸入，是否有其他作法? | KCT A、C、D 直接用 Revenue 分類；自訂條件可用科目分類條件，預設包含下層，或從分類樹勾選科目編號；清單上限 100 個值 | 滿足 | `AuditCore/GlRulePredicates.cs:582-653`；`wwwroot/js/filter-values.js:162-199` |
| 14 | 特定文字篩選，方向能否不要限定【橫向】或者是有卷軸or統計已key了幾個項目? | 文字清單可換行，隨內容增高到 160 像素後在框內捲動，可用換行分隔；沒有「已輸入幾個值」的計數，月曆與科目樹才有 | 大致滿足 | `wwwroot/js/filter-values.js:434-440`；K9 |
| 15 | 步驟五，匯出底稿需要填寫條件設定的文字說明，能否直接預設文字? | 第五步儲存時自動帶入動機草稿；KCT 卡片的自動動機是「字母：卡名」，留白時存成「KCT 小組方法論檢核條件」。不是簡報的評估說明 | 部分 | `wwwroot/js/steps/filter-step.js:569-595`；`Domain/Rules/FilterScenario.cs:281-316`；K10 |

統計：滿足 7 項，大致滿足 2 項，部分 2 項，推論滿足 1 項，未滿足 2 項，待決定 1 項。

## 五、哪些來回經手可以內化

已經內化、KCT 不必再做的事：匯入 IDEA 與定義欄位型態、合併多個同欄位的總帳檔、補項次、把借貸兩欄整理成單一金額、
算 TB 本期變動、另建人工/自動欄、另建高階主管欄、在 Excel 填科目配對、每步驟跳出的檔案總管、重新設定整組條件、
逐一輸入收入科目代碼。

仍在 JET 之外的手工整理，集中在 KCT 的第二階段：移除表頭與小計列、把表頭資訊往下填補、補 GL 科目名稱、合併兩份 TB、
轉換特殊日期格式與整理科目代碼。其中指定欄名所在列、往下填補空白與依科目編號從 TB 帶入名稱是機械性的整理；刪除小計列
或統制科目、整理科目代碼，要先判斷哪些列或哪一段代碼要處理；合併兩份 TB 要先定好兩期科目對不上時怎麼辦。
這些在 2026-10-02 的 P3-1、2026-10-01 的 D06 與 2026-09-18 的 D12 都已裁定先不做或維持現況；KCT 簡報是新的證據，
要不要重新評估由使用者決定，見 K12、K13。

查核團隊與 KCT 之間的交接有三個點：

1. 申請人送件，KCT 確認檔案。JET 看不到申請單，這一步仍在 JET 之外；JET 能做的是匯入後讓期間與欄位問題一眼可見。
2. KCT 寄完整性結果給申請人，申請人確認條件。驗證報告可以直接附上；條件若由申請人自己在 JET 設定，可以少一次往返。
3. KCT 交還成果。新版交的是案件資料夾，接手的人可以直接從第四、五步繼續，不必「回歸配對前」。但報告的 Prepared by
   會是建案的 KCT 人員，而且接手的人要把資料夾放進自己的案件根目錄，見 K14。

由誰建案、誰設條件、誰匯出底稿，是 KCT 與查核團隊的分工問題，JET 不替他們決定；K14 請使用者先確認分工，再決定報告怎麼寫。

## 六、待使用者裁定

本機勾選頁：`artifacts/review/kct-gl-decision-checklist-2026-10-06.html`。每項都有現況、建議與選項；回覆貼回對話後逐字寫進
本節下方，再依裁定更新第七節。

| 編號 | 事項 | 建議 |
|:---|:---|:---|
| K1 | KCT A 的季度範圍 | 季底之外也納入查核期末，讓期末不是季底的案件仍看得到期末 |
| K2 | KCT B 怎麼啟用 | 啟用卡片，借方與貸方分類由審計員在卡片上選，不新增分類用途 |
| K3 | KCT C 是否把現金算一般對方科目 | 維持新版依簡報文字不含現金，卡片說明補一句；要排除現金銷貨改用預篩選條件 |
| K4 | 傳票建立日 | 在攸關資料元素欄位加一個可一鍵加入的「傳票建立日」日期欄 |
| K5 | 人工/自動的呈現 | 底稿 Step 4-1 與資料預覽改寫「人工」「自動」 |
| K6 | 科目配對自動化 | 不自動判斷分類；做「沿用前一案件的科目配對」，匯入時列出本案沒有的科目與還沒分類的科目 |
| K7 | 科目配對檔有 Excel 篩選 | 補含自動篩選與隱藏列的測試，確認全部列都讀入，行為不變 |
| K8 | 預篩選 #1 的去留 | 保留，記入「預篩選的後續收斂」 |
| K9 | 文字清單的計數 | 清單下方顯示已輸入幾個值與上限 |
| K10 | KCT 條件的預設理由 | A 到 E 的預設動機改用簡報的評估說明，F 到 J 沿用卡名 |
| K11 | 指定日期日曆 | 日曆加年與月的選單 |
| K12 | 匯入前的資料整理 | 重啟 P3-1 的兩個小功能：指定欄名所在列、往下填補空白 |
| K13 | 期初、期末兩份 TB | 維持 2026-10-01 的裁定，不做 |
| K14 | 案件交接與 Prepared by | 先確認 KCT 與查核團隊的分工，再決定 Prepared by 寫建案者還是匯出者 |
| K15 | 假日與補班日範本 | 第二步提供取得範本的入口，並說明年份要自行更新 |
| K16 | KCT F 到 J 的來源 | 請使用者提供 KCT 的完整條件清單；沒有時維持現有 F 到 J |
| K17 | 下一階段的範圍與順序 | 先做小項與條件，再依 K12、K14 的結果處理匯入與交接 |

### 使用者回覆（2026-10-06）

使用者在勾選頁按「產生回覆」後貼回對話，全文逐字如下：

```text
KCT 總帳資料整理需求裁定回覆（2026-10-06 清單）

## KCT 條件
- K1 KCT A 的季度範圍：季底加上查核期末（建議）
- K2 KCT B 怎麼啟用：啟用，分類由審計員選（建議）
- K3 KCT C 是否把現金算一般對方科目：維持新版並在卡片說明（建議）
- K10 KCT 條件的預設理由（優化第 15 項）：A 到 E 用簡報評估說明（建議）
- K16 KCT F 到 J 的來源：不確定，先維持現況

## 優化需求
- K4 傳票建立日（優化第 4 項）：加攸關資料元素的傳票建立日快捷（建議）
- K5 人工/自動的呈現（優化第 5 項）：Step 4-1 與資料預覽都改（建議）
- K6 科目配對自動化（優化第 9 項）：做沿用前案配對與差異提醒（建議）
- K7 科目配對檔有 Excel 篩選（優化第 10 項）：補測試並改欄名判斷（建議）
- K8 預篩選 #1 的去留（優化第 11 項）：保留並記入預篩選收斂（建議）
- K9 文字清單的計數（優化第 14 項）：加計數（建議）
- K11 指定日期日曆（優化第 1 項）：加年月選單並實測鍵入（建議）
- K15 假日與補班日範本：加取得範本的入口（建議）

## 匯入與交接
- K12 匯入前的資料整理：維持 2026-10-02 裁定，仍在 JET 之外
- K13 期初、期末兩份 TB：維持不做（建議）
- K14 案件交接與 Prepared by：維持建案者

## 下一階段
- K17 下一階段的範圍與順序：照建議分三批（建議）
```

每一項選擇的完整文字是勾選頁上的「建議」欄，原文保存在本機 `artifacts/review/kct-gl-decision-checklist-2026-10-06.html`。
處理方式：

| 編號 | 裁定 | 處理 |
|:---|:---|:---|
| K1、K2、K3、K10 | 照建議 | 第一批實作 |
| K4、K5、K9、K11、K15 | 照建議 | 第一批實作。K17 建議的批次清單漏列 K4，依性質放進第一批 |
| K6、K7 | 照建議 | 第二批實作 |
| K8 | 保留預篩選 #1 | 不改程式；寫進開發現況「預篩選的後續收斂」 |
| K12 | 維持 2026-10-02 的裁定 | 不改程式；寫進開發現況「匯入前的資料前置處理」 |
| K13 | 維持不做 | 不改程式；寫進開發現況「已裁定不做的事」 |
| K14 | 維持 Prepared by 是建案者 | 不改程式；寫進 `jet-guide.md` 第 7 節 |
| K16 | 不確定，先維持現況 | 不改程式；現有 F 到 J 保留，來源仍待確認 |
| K17 | 照建議分三批 | 第三批的三項都選維持，所以第三批只記錄裁定，不改程式 |

## 七、下一階段的工作批次

依 K17 分三批。第一批與第二批要改程式；第三批的 K12、K13、K14 都裁定維持，只記錄裁定，已在 2026-10-06 寫回
開發現況與 `jet-guide.md` 第 7 節。下面每一項的程式位置都是 2026-10-06 讀碼所得，路徑省略 `src/JET/JET/`；
實作時先重新確認行號。

### 共通做法

- 每一項先寫會失敗的測試，保存第一次失敗的證據，再實作。要改既有測試的預期時，先在本計畫寫明原因，不放寬其他斷言。
- 只用程式產生的合成資料，SQLite 與 DuckDB 都要測。SQL Server 只比照修改、無法實跑時，記到開發現況的
  「SQL Server 只經編譯的修改」。
- 新增或修改 action 時，同步 handler、註冊、`ActionExecutionPolicy`、`wwwroot/js/jet-api.js`、
  `docs/action-contract-manifest.md` 與架構測試。
- 每批結束執行 `Build`、`Public`、`Contract -ContractScenario FrontendMapping` 與 `Documentation`。`Gui` 會搶走滑鼠與前景，
  只在交付前或使用者確認要整合時執行，執行前先告知使用者；`PrivateCase` 要使用者當次授權。
- 每完成一項就更新本計畫的進度、驗證結果與下一個動作。Git 的暫存、提交與推送由使用者另外決定。

### 第一批：KCT 條件與小項

建議順序是 K1、K2、K3、K10、K5、K4、K9、K11、K15。K1 會推進篩選規則版本，先做可以讓後面的測試建立在新版本上；
K15 要新增 action，放最後。

#### K1：KCT A 加入查核期末

- 做法：`Domain/Rules/QuarterEndWindows.cs` 在曆年季底的視窗之外，再加入查核截止日往前 X−1 天到截止日這一段；
  截止日本身是季底時不重複。重疊或相連的視窗合併成一段，依起日排序。SQL 述詞仍是各段以 OR 連接，三種資料庫共用。
- 讀回：後端 `Application/Support/FilterConditionRenderer.cs` 第 270 行目前只寫「季末前 N 天借記收入」，`Render`
  沒有查核期間參數。比照財報準備日的做法傳入查核起訖日，讀回寫出實際採用的日期區間。呼叫端有四處：
  `ExportWorkpaperStreamHandler`、`ExportReportHandlers` 與 `QueryFilterVoucherHandlers` 的兩處。前端讀回
  `wwwroot/js/steps/filter-step.js` 第 1788 行，以及第 1630 行寫「曆年季末前」的提示，一起改寫。
- 版本：推進 `Application/Support/RuleLogicVersions.cs` 的篩選版本，實作前為 `filter-2026-10-04-v17`。開案時
  `FilterScenarioRuleUpgrade` 會整批改用新版本並清掉舊命中，條件篩選報告與底稿標成過期。寫死版本字串的 9 個測試檔
  一起更新。
- 既有測試：`QuarterEndWindowsTests` 的 `Compute_PeriodBetweenQuarterEnds_ReturnsEmpty` 期望查核期間 2025-04-01 到
  2025-05-31 沒有任何視窗，和 K1 衝突。先跑出第一次失敗並保存，再改成期望只有期末這一段，理由引用 K1。
- 第一個失敗測試：`QuarterEndWindowsTests` 新增三個案例：期末 2025-11-30、X 為 5，期望 2025-11-26 到 2025-11-30；
  期末 2025-12-31 時不重複；期末在季底後幾天時兩段合併。`KctFilterPredicateTests` 在 SQLite 與 DuckDB 加期末不是季底的
  命中案例；`FilterConditionRendererTests` 斷言讀回的日期區間。
- 文件：`jet-guide.md` 第 3 節「KCT A 與舊表 A 不是同一項」與第 5 節 KCT A 的季底說明；前端說明的 KCT 卡片段落。

#### K2：啟用 KCT B

- 做法：B 卡改用帶覆寫值的規格：條件型別 `specialAccountCategoryPair`，`pairMode` 為 `drAndCr`，也就是「借方是 A 且
  貸方是 B」；`categorySelection` 為 `subtree`，也就是包含下層；借方與貸方分類都留空，由審計員加入後選擇。
  不能用 `kind:'type'`，因為 `newFilterRule` 會預設帶入應收與收入兩個分類。
- 前置條件：比照 `'any'`，科目配對至少要有一筆非空白分類，否則停用並顯示既有的原因文字。
- 卡片說明：借方選固定資產、貸方選營業費用，要先在第四步「分類設定」新增這兩類並配對科目。在建工程不要配到固定資產，
  也不要放在固定資產底下，因為預設會包含下層分類。
- 補檢查：前端 `ruleProblem` 目前不檢查分類是否留空，只有後端會回「借方分類至少需選擇一項」。改成在前端「尚需補齊」
  寫出第幾組第幾條的分類還沒選。
- 不做：不新增分類用途、不自動建立分類、不新增 wire 型別，卡名維持現有文字。
- 第一個失敗測試：前端測試斷言 B 卡可用、加入後兩側分類為空、沒有任何非空白分類時停用並寫出原因、分類未選時預覽被擋下。
  後端用合成資料建立自訂分類「固定資產」與它的下層、「營業費用」，以及獨立的「在建工程」；KCT 來源的情境在 SQLite 與
  DuckDB 命中「借固定資產、貸營業費用」的傳票，不命中「借在建工程、貸營業費用」的傳票。
- 文件：`jet-guide.md` 第 6 節 KCT 段落；前端說明的 KCT 卡片與「尚不可用」段落。

#### K3：KCT C 的卡片說明

- 做法：KCT 清單加一個說明欄位。C 的說明逐字是「現金不算一般對方科目；要排除現金銷貨，改用預篩選條件『未預期借貸組合』。」
  在卡片與規則編輯區的判定說明顯示；報告的條件讀回不改。
- 注意：C 的前置條件仍把 Cash 算在一般對方分類內，見 `Domain/Rules/FilterScenario.cs` 第 594 到 595 行。只配了 Revenue
  與 Cash 時 C 可以執行，會列出所有收入貸方分錄。裁定沒有要求改，維持現況。
- 第一個失敗測試：`ScreenWordingReviewFrontendTests` 斷言這句說明逐字出現。

#### K10：A 到 E 的預設理由

- 做法：KCT 清單每張卡加一個預設理由欄位。A 到 E 填 [`kct-gl-workflow.md`](../kct-gl-workflow.md#kct-的高風險條件-a-到-e)
  條件表的評估說明，逐字照抄；F 到 J 不填，沿用「字母：卡名」。自動動機每張卡一行，A 到 E 寫成「字母：評估說明」。
  文字只放在 KCT 清單這一處，前端自動帶入與測試都讀它。
- 待確認：D 的原文有重字「根據查核核團隊的瞭解」。照裁定逐字照抄；使用者若要改成「查核團隊」，只改這一處。
- 不改：後端留白時的替補文字「KCT 小組方法論檢核條件」。已儲存的情境不改寫；理由仍是自動產生的情境，使用者再切換卡片時，
  才依現有行為重算。
- 長度：前後端都沒有理由的長度上限，五段合計約 450 字，底稿 Step 3 不會截斷。
- 既有測試：`ScreenWordingReviewFrontendTests.cs` 第 61 行寫死「字母：卡名」，依裁定改寫預期並保存第一次失敗。

#### K5：人工/自動的呈現

- 底稿 Step 4-1：`人工傳票否_JE_S` 欄改寫「人工」「自動」，空白維持空白，表頭不改。欄位定義在
  `AuditCore/WorkpaperProgram.cs` 第 585 到 599 行，目前是 0 位小數的數字型，註解寫明不得輸出成文字；依 K5 改成文字型，
  並改寫註解、引用裁定。寫值在 `Infrastructure/Export/WorkpaperWriter.Step2To41.cs` 第 896 到 897 行。
- 既有測試：`WorkpaperStep41ProviderParityTests` 斷言的值 "1" 與 "0"、型別 `N|0` 與舊版指紋都會失敗，依裁定更新預期並保存
  第一次失敗。`WorkpaperProgramTests` 第 248 到 249 行檢查表頭與來源，預期不變。
- 私人案件比對：`PrivateCase` 比對正確底稿時這一欄會出現差異，依 `jet-guide.md` 第 17 節歸為已裁定的刻意差異，
  更新 `PrivateCaseReportDifferencePolicy`；實跑要等使用者授權。
- 資料預覽：「納入測試的分錄」加一欄人工/自動，放在最後一欄，避免改動既有欄位的位置。後端回傳 manual、automatic 或空值，
  前端 `wwwroot/js/data-preview.js` 轉成「人工」「自動」。SQLite 與 DuckDB 共用 `LocalDataPreviewRepository`，
  `SqlServerDataPreviewRepository` 比照修改。「未納入測試的分錄」不在裁定內，不改；`filter.preview` 的預覽列也不加這一欄，
  `LocalDataPreviewRepository.cs` 第 17 行說兩者欄位相同的註解要改。
- 第一個失敗測試：`QueryDataPreviewHandlerTests` 斷言新欄存在且值正確；底稿測試斷言 Step 4-1 寫出「人工」「自動」與空白。
- 文件：`jet-guide.md` 第 7 節 Step 4-1 列；manifest 的 `query.dataPreview`；前端說明的資料預覽段落。

#### K4：傳票建立日快捷

- 做法：第三步攸關資料元素欄位區加「傳票建立日」快捷：選一個來源欄後，自動勾選並設定顯示名稱「傳票建立日」、型別日期。
  該來源欄已勾選時，就地改名改型並保留欄位識別碼；已有名為「傳票建立日」的欄位時，快捷改成更換那一欄的來源欄，不新增第二欄。
- 位置：`wwwroot/js/steps/mapping-step.js` 的 `rdeFieldsHtml`、全選與勾選事件；送出形狀是來源欄、顯示名稱、型別與既有的
  欄位識別碼。後端 `GlMappingOptionsRules.NormalizeAndValidate` 不改。
- 第一個失敗測試：`tools/tests/frontend-mapping.test.cjs` 驗證選欄後送出的內容：顯示名稱「傳票建立日」、型別 date、新欄不帶
  欄位識別碼；已存在時沿用原識別碼。
- 文件：`jet-guide.md` 第 6 節「建立日或原幣金額需先明確配成攸關資料元素欄位」那句；前端說明第三步。

#### K9：文字清單的計數

- 做法：清單下方顯示「已輸入 N 個值，上限 100」。N 一律取送出時的 `values(rule, state).length`，和後端拿到的清單相同，
  已包含去空白與去除完全相同的重複；尾數條件用同一套分隔規則。輸入時就地更新計數，不重繪整個條件。
- 第一個失敗測試：`tools/tests/frontend-workflow.test.cjs` 輸入含重複值與多種分隔符號的清單，斷言計數等於送出的筆數，
  超過 100 時仍顯示既有提示。`FilterBackendRuleMirrorFrontendTests.cs` 第 56 行逐字鎖住提示文字，提示改字時一起更新。
- 文件：前端說明第五步的多值輸入段落。

#### K11：指定日期日曆與日期鍵入實測

- 做法：第五步「指定日期」的日曆標題加年與月的下拉選單。年份範圍取查核起訖年各前後 5 年，並包含已選日期的年份；方向鍵、
  Home、End、PageUp、PageDown 與 Shift 跳一年照舊。日曆在 `wwwroot/js/filter-values.js` 第 523 到 581 行。
- 實測：在 `.claude/launch.json` 的「JET browser host」實際操作第一步建立案件的三個日期欄，用鍵盤逐段輸入年月日，
  把結果寫進本計畫。
- 第一個失敗測試：`frontend-workflow.test.cjs` 選年份與月份後格子換到該月，Shift 加 PageUp 仍跳一年。目前沒有日曆測試，
  這組是新增。
- 文件：前端說明第五步「指定日期以日曆多選」段落。

#### K15：假日與補班日範本入口

- 做法：新增一個 action，把隨附的 `Holiday2025TW.xlsx` 與 `MakeUpDay2025TW.xlsx` 複製到案件資料夾。已有同名檔就保留，
  回應寫出哪些是新複製、哪些已存在。不自動匯入、不自動開資料夾；成功後畫面說明欄位格式與年份要自行更新，並提供既有的
  「開啟案件資料夾」按鈕。
- 位置：比照 `ExportAccountMappingTemplateHandler`。寫檔用 `Application/Support/ProjectWorkFileWriter.cs` 的「只在不存在時
  寫入」；範本路徑由 `Infrastructure/Export/ReportTemplatePackage.cs` 的 `ReportTemplateCatalog` 提供，Application 要透過
  新介面取得，先核對 `LayerDependencyTests`。前端在 `wwwroot/js/steps/import-step.js` 的假日卡片。
- 第一個失敗測試：新 handler 的測試斷言兩個檔案複製到案件資料夾、內容與隨附範本相同、已存在時不覆寫、沒有觸發匯入。
  架構測試 `HandlerRepositoryScopeTests`、`ReportExportOwnershipTests` 與 `SupportedActionsParityTests` 跟著更新。
- 文件：manifest 新增這個 action；前端說明第二步「假日與補班日」；`data-and-legacy.md` 這兩份範本的用途。

第一批的完成條件：九項都有通過的測試；`Build`、`Public`、`Contract -ContractScenario FrontendMapping` 與 `Documentation`
通過；K11 的實測結果寫進本計畫；K5 的 SQL Server 修改記到開發現況。

### 第二批：科目配對

#### K6：沿用前案配對與差異提醒

- 比較基準：科目配對範本用的科目集合，也就是完整性比對的有效 GL 科目與 TB 科目的聯集，定義在
  `AuditCore/ValidationProcedures.cs` 的 `diff`。第四步的科目清單已經併入配對檔帶來的多餘科目，不能當基準。
- 兩份清單：第一份是檔案裡有、本案沒有的科目；第二份是本案有、檔案沒列的科目，它們不屬於任何分類。本案有、檔案有列
  但分類留白的科目視為 Others，已由既有的「分類留白 N 筆，視為 Others」提醒列出，不另做清單。
- 傳遞：`import.accountMapping.fromFile` 的回應加兩個筆數；清單用新的有界分頁查詢，參數是清單種類、游標與每頁筆數，
  沿用分類留白清單 `LocalAccountMappingBlankPageRepository` 的分頁寫法。筆數只在匯入時與使用者打開清單時計算，
  不放進開案載入，避免每次開案都掃一次 GL。
- 畫面：第四步「用 Excel 配對」在「分類留白 N 筆，視為 Others」提醒旁，加兩行同樣格式的提醒，各附「列出科目」；
  匯入成功訊息補一句可以沿用前一個案件的配對檔。只提醒，不擋匯入，也不刪除多餘科目。
- 已知影響：多餘科目照樣寫入，不會造成命中，因為條件都從 GL 分錄出發。但多餘科目若分類成 Revenue 或 Cash，前置條件會判定
  已配對；本案的收入科目如果都沒配到 Revenue，KCT A、C、D 仍可以執行，結果是 0 筆。分類留白筆數與底稿 sheet 15 也會
  包含多餘科目。這些不改，「本案沒有的科目」提醒寫明這些科目不會出現在篩選結果。
- 空白：依 2026-10-04 的 C3，匯入的文字都去頭尾空白；實作時確認科目配對檔與 GL 的科目編號用同一規則比對。
- 第一個失敗測試：`ImportAccountMappingHandlerTests` 匯入一個含本案沒有的科目、又缺本案科目的合成檔，斷言兩個筆數；
  新的分頁查詢在 SQLite 與 DuckDB 回傳相同清單。
- 文件：manifest 的 `import.accountMapping.fromFile` 回應形狀與新查詢；`jet-guide.md` 第 6 節科目配對段落；前端說明第四步。

#### K7：Excel 篩選與欄名判斷

- 篩選測試：用測試工具 `RawXlsxBuilder` 寫出含 `autoFilter` 與隱藏列的合成檔，確認全部列都讀入；再用 ClosedXML 做標頭在
  第 3 列的完整匯入。推論這兩個測試第一次就會通過，屬於鎖定現有行為，計畫照實記錄「第一次就通過」，不算先失敗。
- 欄名判斷：抽出一個共用判斷器，`AuditCore/IntakeMappingProgram.cs` 的 `AccountMappingProjection` 與
  `Domain/Contracts/AccountMappingContracts.cs` 的 `AccountMappingColumnResolver` 都改呼叫它；後者也供資料預覽與舊案遷移使用，
  只改一份會讓預覽和匯入讀到不同的欄。判斷順序是：先比對確切欄名，去頭尾空白、不分大小寫，科目編號是 `GL_Number` 或
  `GL_NUMBER`，科目名稱是 `GL_Name` 或 `GL_NAME`，分類是 `Standardized Account Name*` 或 `STANDARDIZED_ACCOUNT_NAME`；
  再用關鍵字，已被認領的欄不再比對；最後才退回欄位順序。
- 第一個失敗測試：`AccountMappingColumnResolverTests` 與 `IntakeMappingProgramTests` 對新範本與舊契約兩套欄名各跑 6 種欄序，
  另加中文欄名、未知欄名退回欄位順序、多一個干擾欄。目前欄序一換就讀錯欄，所以排列案例會先失敗。
- 文件：manifest 的科目配對檔欄名說明；`jet-guide.md` 第 6 節。

第二批的完成條件：兩項都有通過的測試；`Build`、`Public`、`Contract -ContractScenario FrontendMapping` 與 `Documentation`
通過。

### 第三批：只記錄裁定

K12、K13 與 K14 都裁定維持，第三批不改程式。裁定已寫進開發現況的「匯入前的資料前置處理」「已裁定不做的事」，以及
`jet-guide.md` 第 7 節的 Prepared by 說明。

## 八、本輪驗證

### 交付測試環境前收尾（2026-10-06）

使用者本次原話：

```text
讀 docs/development-status.md 指向的現行計畫 docs/specs/2026-10-06-kct-gl-requirements-plan.md 第七節與第八節。兩批已完成，現在做交付測試環境前的收尾，依下列順序做，結果寫回計畫第八節。

1. 改寫 docs/test-environment-notes.md，改成描述這一版。上一版的內容已在測試環境，只保留還沒解決的已知限制。要寫的事先核對 jet-guide.md 第 6、7 節與計畫第八節再寫，不照這段提示直接抄：舊案件開啟時，已儲存的情境會改用新版篩選規則，條件篩選報告與底稿標為過期，第一次查看或匯出時重算；底稿 Step 4-1 的人工旗標從 1、0 改成「人工」「自動」，資料預覽多一欄；KCT A 的視窗加上查核期末、B 卡可用、C 卡的現金說明、A 到 E 的預設動機；傳票建立日快捷、值清單計數、日曆年月選單、取得假日範本的入口；第四步科目配對檔的兩種差異提醒；科目配對檔的欄序、第三列標頭、篩選與隱藏列都能匯入。讀者是非專業人員，寫完依 jet-readable-docs 重讀。

2. 用瀏覽器主機與合成案件實際走一次：第四步匯入一份含多餘科目又缺本案科目的配對檔，看兩種差異提醒、清單與分頁；第五步加入 B 卡並看 C 卡說明；第一步取得假日範本。不要開桌面 JET。

3. 完整驗證依序執行：Build、Contract（不帶情境、FrontendMapping、FrontendPreview）、Public、Package -Configuration Release、Excel -Configuration Release、Gui -Configuration AgentGuiTest。Gui 會在我的桌面開 JET 視窗並搶前景，開始前先回報我，等我說可以再跑。Gui 的 KCT 情境若因卡片文字、B 卡可用或預設動機改變而失敗，保留第一次失敗，依計畫第六節的裁定更新預期並寫明原因，不放寬斷言。

4. PrivateCase 本次授權執行 SQLite 與 DuckDB 各一次，Release 組態。確認底稿差異只在 Step 4-1 的人工旗標欄；私人路徑與輸出照規則不記錄、不保留。

5. 全部通過後，照 .agents/harness/development-workflow.md「驗收與關閉」收尾：計畫標為待使用者驗收並移到 docs/history/specs/，開發現況的目前計畫改回「無」並留一段完成摘要，同步 docs/README.md 與 docs/history/README.md，跑 Documentation 並重讀改動段落。列出要加入 Git 的明確路徑，逐一列檔名，不用目錄或萬用字元，確認沒有 data/ 與 artifacts/ 底下的檔案。草擬繁體中文提交訊息，不加 AI 署名。不要暫存、提交或推送，回報我後停止。
```

開始時保留兩批及規格整理留下的全部未提交變更，HEAD 為 `a7f5982a7772c1ff6012946bec9faf5148e11264`。
本輪預定修改交付說明、計畫與現況導覽；只有驗證實際失敗且符合第六節裁定時，才改相應的 GUI 預期。
前端及產品程式不預先改動。瀏覽器使用合成資料，不讀私人案件；私人資料只交由明示的 PrivateCase 執行。

已核對 `jet-guide.md` 第 6、7 節與兩批紀錄，改寫交付說明。舊案的自動改版有「全部情境有效」的條件，
不是一律改版；已輸出的正式底稿不會被默默覆寫。上一版已交付的功能不再重述，只保留未解決限制。
交付說明依寫作規範重讀，Documentation 通過，收據 `20261006-052353655-65073a5580a24f198a077256c2035865`。

瀏覽器操作已完成。正式 browser host 配合新建的合成案件，GL 兩列、TB 207 個科目；配對檔保留其中一個科目，
另有 205 個本案沒有的科目。從第四步按匯入後，兩種提醒分別為 205 與 206 個。
第一份清單先載入 200 個，再載入 5 個；第二份先載入 200 個，再載入 6 個，末頁都不再顯示載入更多。
第五步實際加入 B 卡，借貸分類留空、預設包含下層；C 卡顯示現金不算一般對方科目的說明。
假日範本入口實際在第二步「匯入資料」，不是本次提示的第一步；首次按下後兩檔都顯示已複製，再按則顯示已存在、保留原檔，
沒有匯入假日。沒有開啟桌面 JET、Excel 或檔案總管。

瀏覽器只替換原生選檔回應，指向固定合成檔，避免跳出桌面對話框；匯入、摘要及清單全部由正式 action 計算，沒有模擬業務結果。
合成請求紀錄與截圖保留在 `artifacts/review/kct-delivery/`。主機、暫時轉送服務與背景分頁已關閉。
啟動主機首次因 NuGet 沙箱網路限制失敗，允許連線還原後正常啟動。合成資料準備時曾漏配摘要、重複配對日期欄，
已修正合成檔與配對後重建有效資料；未修改產品或測試斷言。
完整驗證依指定順序執行至 Excel，結果如下：

| 檢查 | 結果 | 收據 |
|:---|:---|:---|
| Build（Debug） | 通過；首次因 NuGet 沙箱網路限制中止，保留收據 `20261006-052844477-381389d14dbf42efb2a1d3fb345765ce`，允許連線後重跑同一命令 | `20261006-052900390-6206239a27d74344bc4e52a016ee8a3d` |
| Contract（不帶情境） | 通過 | `20261006-052928445-f002f90c9d834605a952350f3183cd83` |
| Contract FrontendMapping | 通過 | `20261006-052931251-98e67e2834aa48a0bdcb854c083b74a5` |
| Contract FrontendPreview | 通過 | `20261006-052937176-4ceaec456ccd4b309615c2a06c941403` |
| Public | 4,591 個 .NET、299 個前端、8 個預覽案例通過，無失敗或略過 | `20261006-052949506-8887056996db48f4987df5f3f61b5629` |
| Package（Release） | 104 個案例與封裝檢查通過，暫存封裝已清除 | `20261006-053318575-b4bf6f52af4840b19bc38180c578ee32` |
| Excel（Release） | 六份工作簿的原生往返、六個合成來源匯入案例通過，清理完成 | `20261006-053415500-bfa8197a894448989aea78221a3452c7` |

目前停在 Gui 前，已告知使用者會開啟桌面 JET 並搶前景，等待明確同意。Gui、SQLite 與 DuckDB 的 PrivateCase、
獨立複審及歸檔尚未執行。PrivateCase 執行前仍須在本次程序提供根目錄與案件清單設定；目前環境未設定，不能直接空參數執行。
沒有讀取私人案件，沒有啟動 SQL Server。沒有暫存、提交或推送。

使用者 2026-10-06 回覆 Gui 執行確認，原話：「可以」。本輪依此開始 Gui，再接續已授權的兩個本機資料庫 PrivateCase。

| 檢查 | 結果 | 收據 |
|:---|:---|:---|
| Gui（AgentGuiTest） | 一次通過，沒有修改 GUI 預期；程序與暫存資料清理完成 | `20261006-054057744-4093e8be437c4c13be6fd537e97341cd` |
| PrivateCase（Release、SQLite） | 執行一次，兩個案例通過，沒有略過；私人輸出已清除 | `20261006-054557718-dc8d279624cc44c89a6bd4ab65e1b86a` |
| PrivateCase（Release、DuckDB） | 執行一次，兩個案例通過，沒有略過；私人輸出已清除 | `20261006-054714582-9f44b5a23f134c5d987a7cf3322331e9` |

PrivateCase 執行前在本機找到唯一的既有案件清單，核對來源檔存在及檔案大小後，僅在當次程序設定參數。
原始資料與清單未修改；兩份收據均記錄私人路徑未保存、輸出未保留，工作鎖與清理通過。沒有啟動 SQL Server。

K5 驗證限制：既有私人清單沒有配對人工旗標，所以這兩次驗收未涵蓋該欄，不能說已確認私人底稿只差人工旗標。
K5 的欄值、型別、其他欄位及差異限制已有合成測試，紀錄見第一批；但它們不能冒充私人案件的欄值驗收。
已詢問使用者：提供含該配對的清單後再驗，或以合成測試補證、註明此限制後歸檔待驗收。目前不更改私人清單、不自行猜來源欄。
依「驗收與關閉」已開始獨立唯讀複審，只交付工作樹差異與本計畫，不讀私人資料。

獨立複審發現 K9 的正式入口漏項：KCT E、F、H 沿原有 `text`、`customKeywords`、`trailingDigits` 編輯器，
沒有新增值數計數；原測試只涵蓋 `fieldValue`。依第七節的清單及尾數需求補這三個入口，先新增失敗測試。
不更改既有測試預期。首次三個入口都因沒有計數而失敗，收據 `20261006-055233870-b0bda3278e974a638fb529ebb87e9d38`。
接著補齊合成 DOM 的 `hasAttribute` 支援後，測試又指出原有入口仍送出完全相同的重複值，收據
`20261006-055329407-b195ed61930c488595342db6be7c57ea`。依 K9 的「去除完全相同的重複」要求，三入口的計數與送出共用去重後清單；
保留原有分隔符號、前導零、大小寫不同的值及輸入框原文，不新增阻擋。修正後須重跑受影響的驗證；再次執行 Gui 前另行告知並等同意。

第二次唯讀複審確認三入口已補齊，但指出 E、F、H 原有型別沒有 100 個值的上限，不能照 `fieldValue` 顯示上限。
因此這三入口只顯示「已輸入 N 個值」，不新增限制；`fieldValue` 的計數與既有上限不變。
這是第七節 K9 實作範圍的補充，使用者裁定原話不改。剛新增的三個測試依真實契約調整提示，保留 101 個值、空清單、精確去重與焦點檢查。

修正後 FrontendMapping 的 302 個案例通過，收據 `20261006-055614800-0d49463da65244fcb4f066fbd2a8258b`。
獨立複審再次核對，確認兩項問題都已關閉，未發現其他影響正確性或需求的缺口；複審只讀程式與計畫，沒有代跑驗證。
瀏覽器主機也從 E、F、H 卡實際加入條件，輸入含重複及不同分隔符號的合成清單，三者都就地顯示 3 個值。
截圖保存為 `artifacts/review/kct-delivery/k9-efh-counts.png`，主機與背景分頁已關閉。已按原順序重跑至 Excel；Gui 尚未再跑。
PrivateCase 不重跑：本次補正只改前端與其合成測試，私人流程使用的 C#、資料庫及底稿輸出沒有再改，先前各一次的結果仍保留。

K9 補正後的驗證：

| 檢查 | 結果 | 收據 |
|:---|:---|:---|
| Build（Debug） | 通過 | `20261006-055725235-87a8975a586e43b8b17e5a2465cbdaa4` |
| Contract（不帶情境） | 通過 | `20261006-055728805-4bec7d83ff2541baa69a91a0764c2eb2` |
| Contract FrontendMapping | 302 個案例通過 | `20261006-055731185-f576b5cc56384b6b975b1dfe231f60b5` |
| Contract FrontendPreview | 通過 | `20261006-055737069-51550576a9f240a6bcef604fc962953d` |
| Public | 4,591 個 .NET、302 個前端、8 個預覽案例通過，沒有失敗或略過 | `20261006-055741292-b9008fa41d474286b993031db474d313` |
| Package（Release） | 通過 | `20261006-060110390-388a4ecd362d450aa4f3ab9c1ac406b2` |
| Excel（Release） | 通過 | `20261006-060138058-ac1e2b2fe22240368c7f715b82432fa0` |
| Documentation | 通過，沒有警告 | `20261006-060258220-a83ff6f00ff543b5896bd06d904539b1` |

已再次告知使用者，Gui 會開啟桌面 JET 並搶前景，等同意才重跑。先前 Gui 證明的是 K9 補正前的版本，不能冒充本次修正後的結果。
仍待使用者決定 K5 私人案件未涵蓋該欄的補證方式，計畫暫不歸檔；Git 路徑清單與提交訊息在收尾條件完成後定稿。
目前沒有暫存、提交或推送，SQL Server 服務保持停止。

使用者 2026-10-06 以兩則註解確認：

- 選取「可以重跑 Gui 嗎？ 會再次搶前景。」，回覆原話：「可以」。
- 選取「要以合成測試補證並註明限制」，回覆原話：「可以」。

依此重跑修正後版本的完整 Gui。K5 以既有合成固定答案補證，私人案件未配對人工旗標的限制保留在本計畫與交付說明，
不修改私人清單、不重跑 PrivateCase，也不把補證方式寫成私人欄值已驗收。Gui 通過後依原授權歸檔為待使用者驗收。

#### 交付前收尾結果

修正後的完整 Gui 通過，收據 `20261006-060452074-6c4a184609d9475daf16df7cabd33807`。17 個情境完成，沒有修改 GUI 預期，
程序、工作鎖與暫存資料清理均通過。至此，Build、三種 Contract、Public、Release Package、Release Excel、AgentGuiTest Gui，
以及已授權的兩個本機資料庫 PrivateCase 都已實際通過。K9 最後只改前端，沒有更動 PrivateCase 使用的 C# 與底稿輸出。

獨立複審找到的 K9 入口漏項及錯誤上限提示已修正，再次複核未發現剩餘的正確性或需求缺口。
K5 的私人案件驗證限制按使用者同意保留，交付說明已要求在有人工旗標配對的案件核對預覽與底稿。
SQL Server 與 ReleaseCandidate 未執行；後者需要乾淨且已提交的來源，本輪沒有 Git 授權。

本計畫依「驗收與關閉」標為待使用者驗收並移到歷史文件；開發現況的目前大型計畫改回「無」，
文件導覽、歷史索引、KCT 流程、交付說明及文件檢查清單同步更新。另核對 `RuleLogicVersions.Filter`，
把 action 契約仍寫 v17 的舊文字改為實際的 `filter-2026-10-06-v18`，舊表 A 到 U 的來源改連已歸檔的回饋計畫。
需求原話、裁定與首次失敗不改寫。原始私人資料及清單未修改，私人路徑未保存，私人輸出未保留。

下方 11 項延後事項已在交付收尾再次逐項核對；除了本計畫的 KCT 調整與 SQL Server 只經編譯的紀錄外，維持原狀與重啟條件。
下一步是使用者在測試環境驗收；本輪不暫存、提交或推送。

歸檔後 Documentation 通過，收據 `20261006-061150851-fd762f7ad4174ed99eaf452e639168e0`：
46 個檔案、240 個連結及 20 個錨點檢查完成，沒有錯誤或警告。改動段落已完整重讀；補記本結果後再檢查一次。

#### Git 檔案清單（第二輪複審前，已作廢）

這份清單已作廢，只保留紀錄；現行清單見第八節「第四輪 Git 逐檔清單與提交訊息草稿」。
以下 78 個檔案是本次兩批實作、規格與交付收尾的待加入範圍，以儲存庫根目錄為起點，逐一列出完整檔名。
已核對每項都是現存檔案，沒有目錄、萬用字元、`data/` 或 `artifacts/` 下的檔案；沒有其他範圍外的未提交變更。
這只是清單，尚未暫存；提交與推送也未執行。

```text
docs/action-contract-manifest.md
docs/data-and-legacy.md
docs/development-status.md
docs/history/README.md
docs/history/specs/2026-10-06-kct-gl-requirements-plan.md
docs/idea-replacement-scope.md
docs/jet-frontend-description.md
docs/jet-guide.md
docs/kct-gl-workflow.md
docs/project-context.md
docs/README.md
docs/test-environment-notes.md
src/JET/JET/AppCompositionRoot.cs
src/JET/JET/AppCompositionRoot.Repositories.cs
src/JET/JET/Application/Handlers/ExportCalendarTemplatesHandler.cs
src/JET/JET/Application/Handlers/ExportReportHandlers.cs
src/JET/JET/Application/Handlers/ExportWorkpaperStreamHandler.cs
src/JET/JET/Application/Handlers/Import/ImportAccountMappingHandler.cs
src/JET/JET/Application/Handlers/Query/QueryAccountMappingDifferencePageHandler.cs
src/JET/JET/Application/Handlers/Query/QueryFilterVoucherHandlers.cs
src/JET/JET/Application/ProjectRepositories.cs
src/JET/JET/Application/Support/FilterConditionRenderer.cs
src/JET/JET/Application/Support/ProjectWorkFileWriter.cs
src/JET/JET/Application/Support/RuleLogicVersions.cs
src/JET/JET/AuditCore/IntakeMappingProgram.cs
src/JET/JET/AuditCore/WorkpaperProgram.cs
src/JET/JET/Domain/Abstractions/AccountMappingDifferenceRepository.cs
src/JET/JET/Domain/ActionExecutionPolicy.cs
src/JET/JET/Domain/Contracts/AccountMappingContracts.cs
src/JET/JET/Domain/Contracts/ICalendarTemplateSource.cs
src/JET/JET/Domain/Contracts/ImportContracts.cs
src/JET/JET/Domain/Rules/AccountMappingColumnMatcher.cs
src/JET/JET/Domain/Rules/QuarterEndWindows.cs
src/JET/JET/Infrastructure/Export/CalendarTemplateSource.cs
src/JET/JET/Infrastructure/Export/WorkpaperWriter.Step2To41.cs
src/JET/JET/Infrastructure/FileIO/OpenXmlSaxTableReader.cs
src/JET/JET/Infrastructure/Persistence/AccountMappingDifferenceQuery.cs
src/JET/JET/Infrastructure/Persistence/Local/LocalAccountMappingDifferenceRepository.cs
src/JET/JET/Infrastructure/Persistence/Local/LocalDataPreviewRepository.cs
src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerAccountMappingDifferenceRepository.cs
src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerDataPreviewRepository.cs
src/JET/JET/wwwroot/js/app.js
src/JET/JET/wwwroot/js/data-preview.js
src/JET/JET/wwwroot/js/filter-values.js
src/JET/JET/wwwroot/js/jet-api.js
src/JET/JET/wwwroot/js/steps/filter-step.js
src/JET/JET/wwwroot/js/steps/import-step.js
src/JET/JET/wwwroot/js/steps/mapping-step.js
src/JET/JET/wwwroot/js/steps/validate-step.js
src/JET/JET/wwwroot/js/ui-core.js
src/JET/tests/JET.Tests/Application/AdvancedFilterAstContractTests.cs
src/JET/tests/JET.Tests/Application/ExportCalendarTemplatesHandlerTests.cs
src/JET/tests/JET.Tests/Application/FilterConditionRendererTests.cs
src/JET/tests/JET.Tests/Application/ImportAccountMappingHandlerTests.cs
src/JET/tests/JET.Tests/Application/InlineWorkbookProject.cs
src/JET/tests/JET.Tests/Application/QueryDataPreviewHandlerTests.cs
src/JET/tests/JET.Tests/Application/RuleLogicVersionsTests.cs
src/JET/tests/JET.Tests/Application/TypedFieldFilterContractTests.cs
src/JET/tests/JET.Tests/Application/WorkpaperStep41ProviderParityTests.cs
src/JET/tests/JET.Tests/Architecture/FrontendUsabilityContractTests.cs
src/JET/tests/JET.Tests/Architecture/HandlerRepositoryScopeTests.cs
src/JET/tests/JET.Tests/Architecture/MappingProjectionPolicyFrontendTests.cs
src/JET/tests/JET.Tests/Architecture/ReportExportOwnershipTests.cs
src/JET/tests/JET.Tests/Architecture/ScreenWordingReviewFrontendTests.cs
src/JET/tests/JET.Tests/Architecture/SupportedActionsParityTests.cs
src/JET/tests/JET.Tests/AuditCore/IntakeMappingProgramTests.cs
src/JET/tests/JET.Tests/AuditCore/WorkpaperProgramTests.cs
src/JET/tests/JET.Tests/Domain/AccountMappingColumnResolverTests.cs
src/JET/tests/JET.Tests/Domain/QuarterEndWindowsTests.cs
src/JET/tests/JET.Tests/Infrastructure/KctFilterPredicateTests.cs
src/JET/tests/JET.Tests/Infrastructure/OpenXmlSaxTableReaderTests.cs
src/JET/tests/JET.Tests/Infrastructure/PrivateCaseReportComparator.cs
src/JET/tests/JET.Tests/Infrastructure/PrivateCaseReportComparatorTests.cs
src/JET/tests/JET.Tests/Infrastructure/PrivateCaseReportDifferencePolicy.cs
src/JET/tests/JET.Tests/Infrastructure/WorkpaperStep41LegacyBaselineBenchmarkTests.cs
tools/harness/documentation-check.json
tools/tests/frontend-mapping.test.cjs
tools/tests/frontend-workflow.test.cjs
```

#### 提交訊息草稿（第二輪複審前，已作廢）

這份草稿已作廢，只保留紀錄；現行草稿見第八節「第四輪 Git 逐檔清單與提交訊息草稿」。

```text
補齊 KCT 條件與科目配對操作

- 調整 KCT 條件、人工旗標呈現、日期與值清單操作，新增假日範本入口。
- 加入科目配對差異清單，統一欄名判斷並修正第三列標頭漏匯。
- Build、Contract、Public、Package、Excel、Gui、兩個本機資料庫的 PrivateCase 與 Documentation 通過，獨立複審問題已修正。
- 私人案件未涵蓋人工旗標，依使用者同意以合成測試補證；計畫歸檔為待使用者驗收。
```

上面的 Git 清單與提交訊息草稿是第二輪複審前的版本，修正完成後要重新定稿。

### 提交前獨立複審（第二輪，2026-10-06）

使用者原話：「我目前還沒有要提交或推送，我想要你複審 codex 的工作內容，查驗是否符合先前提出的工作計畫? 以及代碼品質、文件紀錄等。」

複審由 Claude 這邊進行，沒有參與兩批實作。四個唯讀子代理分別看第一批 KCT 條件、第一批小項、第二批科目配對，以及文件、
紀錄、收據與 Git 清單；只讀工作樹差異、本計畫與 `artifacts/harness/runs/` 的收據，沒有執行驗證命令，沒有讀私人資料目錄。
主線另外親自核對了入帳日的儲存格式、K4 刪欄、K6 計數位置與空白科目編號四處。

#### 符合計畫的部分

- 11 項都照第七節的做法實作，也都有對應測試。K2、K3、K9、K10、K11、K15、K6、K7 判定符合計畫；K1、K4、K5 部分符合，差在下面列的問題。
- 第八節列出的 25 份收據都存在，命令、狀態、組態與情境和文中一致。78 個 Git 路徑與工作樹的 66 個修改加 12 個未追蹤完全相同，
  沒有 `data/`、`artifacts/` 或私人路徑。
- 既有測試的修改沒有放寬斷言；QuarterEndWindowsTests 反而更嚴格。
- 沒有新增擋住審計員的檢查，新增的前置檢查都是防 JET 自己的失敗。
- K3 的現金說明與 K10 的 A 到 E 動機和來源逐字相同，D 的「查核核團隊」重字保留。
- 「修正後 Gui 17 個情境通過」指的是 Codex 自己那輪唯讀複審抓到 K9 漏了 E、F、H 三個入口，補正後重跑；兩次 Gui 都一次通過，
  中間沒有 Gui 失敗。使用者的兩則「可以」有記在第 592 到 595 行。

#### 找到的問題與修正方向

使用者對每一項的備註都要求：「對於發現的問題，你提出的修正方式應該考量到全局架構，而不只針對發現問題的局部做修復，
避免整體開發方向偏離軌道，尤其是在遵循 JET 工具的審計業務以及使用者體驗優化這兩大目標上。」下表的修正方向依此寫，
不是最小補丁。

| 編號 | 問題 | 位置 | 修正方向 |
|:---|:---|:---|:---|
| R1 | action 契約第 487 行仍寫「現行篩選版本為 `filter-2026-10-04-v17`」；程式與同檔第 372 行已是 v18。第 612 行宣稱已改，只改了一半。 | `docs/action-contract-manifest.md:487` | 把版本沿革寫完整：v17 之後，2026-10-06 加入查核期末視窗為 v18。和 D2 一起處理：推進版本時，所有寫死現行版本的地方（程式常數、契約、測試名稱與註解、舊版本清單）一次核對。 |
| R2 | 兩處寫出簡報原始檔名，檔名含一所大學名稱與專題字樣。這是 Claude 整理規格時寫進去的。 | `docs/kct-gl-workflow.md:12`；本計畫第 60 行 | 已由 Claude 刪掉檔名，改寫保密範圍：只記錄 KCT 小組的執行步驟與期望優化的項目，當成需求看待，不記檔案名稱與文件所屬機構。 |
| R3 | K6 的差異筆數在配對寫入資料庫之後、結果狀態更新之前計算，而且沿用可取消的 token。計數期間取消或查詢失敗，action 回報失敗，配對卻已換掉，畫面仍顯示舊配對。`WorkflowResultStateSupport.cs:72` 已明文寫寫入後的讀取要用 `CancellationToken.None`。 | `ImportAccountMappingHandler.cs:80-84` | 把差異筆數當成寫入後的摘要，放在 `AfterMutationAsync` 之後，用 `CancellationToken.None`；計數失敗時寫支援日誌，兩個筆數回 null，畫面顯示「尚未核對」並可從清單重算。匯入已完成就不能因摘要失敗變成失敗，這和既有的結果狀態寫法一致。 |
| R4 | 「配對檔未列的科目」算進空白科目編號，審計員消不掉。GL 允許空白編號，配對檔卻拒絕空白；同一母體的科目清單 `AccountMappingEditorRepository.cs:35` 有排除空白，差異查詢沒有。 | `AccountMappingDifferenceQuery.cs:12-13` | 根本原因是兩處各寫一份科目母體 SQL。抽出一份共用的「配對科目母體」查詢（有效 GL 與 TB 聯集、排除空白編號），科目清單、兩種差異清單與筆數都用它；補一個 GL 含空白編號的合成測試。空白編號已由第三步「空白科目編號」明細負責揭露。 |
| R5 | K4 快捷在已有「傳票建立日」時，若選到正被另一個攸關資料元素欄位使用的來源欄，第 817 行直接丟掉那一欄，沒有提示；引用它的篩選情境變成未配對，識別碼救不回。前端測試還斷言這個結果。 | `mapping-step.js:811-817`；`frontend-mapping.test.cjs:1411` | 快捷要走和手動編輯相同的規則：一個來源欄只能給一個欄位，而且帶識別碼的欄位不能在審計員沒有選擇的情況下被刪。來源欄已被占用時，就地顯示「這個來源欄目前是某某欄位」，提供「把它改成傳票建立日」（沿用原識別碼）與取消兩個動作。改寫既有測試並寫明原因，補兩條路徑的測試。 |
| R6 | SQL Server 預覽的既有測試仍要求 8 欄，產品端已 9 欄。它標 `[SqlServerFact]`，Public 抓不到，下次 `Provider` 必失敗；開發現況只寫「只經編譯」。 | `SqlServerDataPreviewRepositoryTests.cs:76、80` | 依 K5 更新欄數、欄名與列值，在本計畫寫明原因；開發現況「SQL Server 只經編譯的修改」補一句這個測試已同步更新但未連線執行。三個資料庫實作的預覽欄位清單應來自同一份定義，若目前是各寫一份，順手收成共用常數。 |
| R7 | K7 把略過列數改成實際列號，也改到假日與補班日匯入（固定略過 1 列）。正式範本不受影響；第 1 列空白、第 3 列才是標頭的檔案以前碰巧可讀，現在會錯。一般總帳與試算表匯入略過 0 列，不受影響。 | `ImportCalendarHandlers.cs:157`；`OpenXmlSaxTableReader.cs:103` | 把「略過列數指實際列號」寫進讀取器的介面說明，Open XML 與二進位兩個讀取器都遵守；加一個讀取器測試：略過列數大於 0 且略過範圍內有空白列。假日匯入的行為變化記進本計畫與 `jet-guide.md` 的匯入段落。 |
| R8 | 兩處既有測試或夾具的變更沒寫原因：讀回文字從「季末前 5 天借記收入」改成「季底或查核期末前 5 天借記收入」；共用夾具改成預設明送 `blankValueKind = reject`（行為等價）。 | `FilterConditionRendererTests.cs:521-522`；`InlineWorkbookProject.cs:279、351` | 在第八節 K1 與 K5 段各補一句原因，不改測試。 |
| R9 | 提交訊息草稿漏寫兩個風險：舊案件開啟時整批改用 v18 規則，條件篩選報告與底稿標為過期；SQL Server 只經編譯。 | 本計畫「提交訊息草稿」 | 修正全部完成後重新定稿，內文寫結果、理由、風險與實際驗證，不逐檔重述。 |
| D1 | K1 前端另寫一份季底與期末視窗算法，只供讀回顯示，只有一個 JS 測試，沒有前後端一致測試；專案對其他顯示鏡像有 `FilterFrontendParityTests` 的做法。目前兩邊結果相同。 | `filter-step.js:1686-1707` | 比照 `FilterFrontendParityTests`，用同一張邊界表格（重疊、相連、跨年、期間內沒有季底、期末剛好是季底、天數 92）同時跑 JS 與 C#。顯示用的視窗仍由前端算，但不能沒有一致測試。 |
| D2 | 版本測試的方法名與註解仍寫 v17，舊版本清單沒列 v17。 | `AdvancedFilterAstContractTests.cs:289`；`TypedFieldFilterContractTests.cs:31` | 改名、改註解、把 `filter-2026-10-04-v17` 加進舊版本清單，和 R1 一起做。 |
| D3 | K5 私人案件比對政策只接受「差異恰在人工旗標欄、列數欄數一致、清單明示採用裁定」，其他欄不會放過；但該欄內人工與自動對調、整欄變空白也會被接受，因為比對器只拿到值的雜湊。這次兩輪 PrivateCase 沒配對該欄，政策沒被用到。 | `PrivateCaseReportDifferencePolicy.cs:85-134` | 把政策從「接受該欄任何差異」改成「比對前把舊版該欄的 1、0 換成人工、自動，再要求零差異」；裁定變成欄值正規化規則，而不是整欄放行。補合成測試：對調與整欄空白都要被抓到。 |
| D4 | K11 用鍵盤改年或月選單時，一改值就重畫日曆並把焦點移到日期格，無法連按方向鍵換年份；滑鼠不受影響。 | `filter-values.js:560、588` | 日曆重畫後把焦點還給觸發重畫的控制項；這是重畫的一般規則，不只修年月選單。補前端測試。 |
| D5 | 資料預覽每次都用目前的欄名判斷器重判暫存批次欄名，舊案若當初讀錯欄，預覽會顯示正確內容而已存配對是錯的。 | `LocalDataPreviewRepository.cs:158`；`SqlServerDataPreviewRepository.cs:143` | 預覽一律改讀已存的配對表，和直接編輯的配對（`PreviewSavedAsync`）用同一來源；配對表是唯一事實來源，不在讀取時重判。本機與 SQL Server 一起改，後者只經編譯並記錄。 |
| D6 | 配對檔欄名全部不認得時靜默退回欄位順序；新規則「已認領欄不再比對」拿掉舊的撞名安全網，兩組罕見欄名在標準欄序下舊版讀對、新版讀錯。 | `AccountMappingColumnMatcher.cs:27-37` | 判斷器回傳每一欄是「確切欄名」「關鍵字」還是「欄位順序」決定的；任一欄落到欄位順序時，匯入回應帶一則不擋人的提醒，寫出哪一欄被當成什麼，前端放在既有匯入提醒的位置。關鍵字比對改成由最專一的欄先認領（分類、科目編號、名稱），補這兩組欄名的固定答案。 |
| D7 | `export.calendarTemplates` 被列進「操作期間保持案件資料庫開啟」清單，但它不讀資料庫，和第 125 行註解不符。 | `ActionExecutionPolicy.cs:154` | 從清單移除；若有測試描述這份清單，同步更新。 |
| D8 | Gui 17 個情境沒有直接操作到 B 卡、A 到 E 預設動機或底稿人工旗標欄。 | `tools/harness/gui-driver/` | 擴充既有情境而不是新增：`filter-kct-editing` 加入 B 卡並確認動機文字；有匯出底稿的情境加讀回 Step 4-1 人工旗標欄為文字。情境總數維持 17。 |
| D9 | 交付說明第 36 行用「額外欄位」而畫面叫「攸關資料元素欄位」、第 41 行沒寫在第幾步、第 43 行的 `yyyy-MM-dd` 沒舉例；`jet-guide.md:413`、`action-contract-manifest.md:99`、`development-status.md:19` 出現 K5、K6、K9 代號；三處「上一份計畫」仍指 2026-09-17 那份。 | 各該檔 | 代號改成白話或附計畫連結；畫面用語與畫面一致；日期格式用實例；「上一份計畫」改成正確的指稱。依 `jet-readable-docs` 逐段重讀。 |
| D10 | `idea-replacement-scope.md:78` 刪掉「KCT 是否另有完整清單」的不確定性，`project-context.md:107` 還保留，兩份說法不一。 | `docs/idea-replacement-scope.md:78` | 補回一句「F 到 J 與完整清單仍待 KCT 提供」，與 project-context 一致。 |

#### 使用者裁定（2026-10-06）

使用者在本機勾選頁回覆，逐字如下：

```text
KCT 提交前複審裁定回覆（2026-10-06 清單）

## 提交前必須處理
- R1 action 契約仍寫 v17 是現行篩選版本：改成 v18 並補沿革（建議）。備註：請注意用詞和術語，輸出已經開始變的混亂且難以閱讀了。
- R2 簡報原始檔名含大學名稱與專題字樣：刪掉檔名（建議）。備註：請注意這部分不應該會有相關機敏資訊，因為你只需要紀錄KCT小組提供的執行步驟和期望優化的項目，這些應當視為KCT小組的需求，因此不需要你紀錄資料來源、文件所屬機構等資訊

## 建議提交前修
- R3 K6 差異計數放在寫入之後、狀態更新之前，且可被取消：移到狀態更新後並改不可取消（建議）。備註：對於發現的問題，你提出的修正方式應該考量到全局架構，而不只針對發現問題的局部做修復，避免整體開發方向偏離軌道。
- R4 K6「配對檔未列的科目」算進空白科目編號，審計員消不掉：排除空白並補測試（建議）。備註：對於發現的問題，你提出的修正方式應該考量到全局架構，而不只針對發現問題的局部做修復，避免整體開發方向偏離軌道。
- R5 K4 傳票建立日快捷會默默刪掉另一個攸關資料元素欄位：改成提示，由審計員決定（建議）。備註：對於發現的問題，你提出的修正方式應該考量到全局架構，而不只針對發現問題的局部做修復，避免整體開發方向偏離軌道，尤其是在遵循 JET 工具的審計業務以及使用者體驗優化這兩大目標上。
- R6 SQL Server 預覽的既有測試沒跟著 K5 改：現在更新測試並寫原因（建議）。備註：對於發現的問題，你提出的修正方式應該考量到全局架構，而不只針對發現問題的局部做修復，避免整體開發方向偏離軌道。
- R7 K7 讀取器修正也改到假日與補班日匯入，沒記錄也沒測試：補紀錄與測試（建議）。備註：對於發現的問題，你提出的修正方式應該考量到全局架構，而不只針對發現問題的局部做修復，避免整體開發方向偏離軌道。
- R8 兩處既有測試或夾具的變更沒寫原因：補寫原因（建議）。備註：對於發現的問題，你提出的修正方式應該考量到全局架構，而不只針對發現問題的局部做修復，避免整體開發方向偏離軌道。
- R9 提交訊息草稿漏寫兩個風險：補兩點風險（建議）。備註：對於發現的問題，你提出的修正方式應該考量到全局架構，而不只針對發現問題的局部做修復，避免整體開發方向偏離軌道。

## 不阻擋的事
- D1 K1 前端另寫一份季底與期末視窗算法，沒有前後端一致測試：這次一起修。備註：對於發現的問題，你提出的修正方式應該考量到全局架構，而不只針對發現問題的局部做修復，避免整體開發方向偏離軌道。
- D2 版本測試的名稱與註解仍寫 v17，舊版本清單沒列 v17：這次一起修（建議）。備註：對於發現的問題，你提出的修正方式應該考量到全局架構，而不只針對發現問題的局部做修復，避免整體開發方向偏離軌道。
- D3 K5 私人案件比對政策接受人工旗標欄內的任何差異：這次一起修。備註：對於發現的問題，你提出的修正方式應該考量到全局架構，而不只針對發現問題的局部做修復，避免整體開發方向偏離軌道，尤其是在遵循 JET 工具的審計業務以及使用者體驗優化這兩大目標上。
- D4 K11 用鍵盤改年或月選單時焦點跳到日期格：這次一起修。備註：對於發現的問題，你提出的修正方式應該考量到全局架構，而不只針對發現問題的局部做修復，避免整體開發方向偏離軌道，尤其是在遵循 JET 工具的審計業務以及使用者體驗優化這兩大目標上。
- D5 資料預覽在讀取時用新判斷器重判欄名，舊案可能與已存配對不一致：這次一起修。備註：對於發現的問題，你提出的修正方式應該考量到全局架構，而不只針對發現問題的局部做修復，避免整體開發方向偏離軌道，尤其是在遵循 JET 工具的審計業務以及使用者體驗優化這兩大目標上。
- D6 配對檔欄名全部不認得時靜默退回欄位順序；撞名安全網被拿掉：這次一起修。備註：對於發現的問題，你提出的修正方式應該考量到全局架構，而不只針對發現問題的局部做修復，避免整體開發方向偏離軌道，尤其是在遵循 JET 工具的審計業務以及使用者體驗優化這兩大目標上。
- D7 執行政策把假日範本 action 列進「保持資料庫開啟」清單：這次一起修（建議）。備註：對於發現的問題，你提出的修正方式應該考量到全局架構，而不只針對發現問題的局部做修復，避免整體開發方向偏離軌道，尤其是在遵循 JET 工具的審計業務以及使用者體驗優化這兩大目標上。
- D8 Gui 17 個情境沒有直接覆蓋 B 卡、預設動機或人工旗標欄：這次一起補情境。備註：對於發現的問題，你提出的修正方式應該考量到全局架構，而不只針對發現問題的局部做修復，避免整體開發方向偏離軌道。
- D9 面向人的文字有幾處不好讀或出現計畫代號：這次一起修（建議）。備註：對於發現的問題，你提出的修正方式應該考量到全局架構，而不只針對發現問題的局部做修復，避免整體開發方向偏離軌道，尤其是在遵循 JET 工具的審計業務以及使用者體驗優化這兩大目標上。
- D10 idea-replacement-scope.md 刪掉了「KCT 是否另有完整清單」的不確定性：補回（建議）

## 下一步
- N1 誰來修：Codex 修（建議）
- N2 修完後要不要再跑一次 Gui：修完再跑 Gui（建議）
- N3 這份複審紀錄寫回哪裡：Claude 寫回（建議）
```

#### 修正順序與完成條件

19 項全部在這一輪修，由 Codex 執行，順序依改動範圍分組，每組先寫會失敗的測試並保存第一次失敗；要改既有測試的預期時先在本節寫明原因：

1. 科目配對：R4 共用科目母體查詢、R3 計數位置、D5 預覽讀已存配對、D6 判斷器提醒與關鍵字順序。
2. 第三步攸關資料元素：R5。
3. KCT A 與版本：D1 一致測試、R1 與 D2 版本沿革與測試名稱。
4. 人工旗標：R6 SQL Server 測試、D3 比對政策改為欄值正規化。
5. 讀取器：R7。
6. 前端小項：D4 焦點、D7 執行政策清單。
7. Gui 情境：D8。
8. 文件：R8、D9、D10，最後 R9 重新定稿提交訊息與 Git 清單。

完成條件：Build、Contract（不帶情境、FrontendMapping、FrontendPreview）、Public、Package、Excel、Documentation 通過；
Gui 依使用者 N2 裁定重跑，開始前先告知並等同意。D3 改了 PrivateCase 使用的比對器，兩個本機資料庫的 PrivateCase 是否重跑，
由使用者另外授權。修正完成後由 Claude 這邊再複核一次，再重新定稿 Git 清單與提交訊息；是否暫存、提交或推送仍須使用者明示。

R2 已由 Claude 在寫回本節時完成：刪掉兩處檔名，`kct-gl-workflow.md` 的保密範圍改寫成不記檔案名稱與文件所屬機構。
計畫移回 `docs/specs/`，開發現況、文件導覽、歷史索引、專案背景、KCT 流程、交付說明與文件檢查清單的連結已同步。

#### 第二輪修正執行紀錄（2026-10-06）

本輪依上列八組順序修正。Gui 開始前另行告知並等待使用者同意；PrivateCase 等使用者另行決定，本輪不自行執行。
SQL Server 不啟動，不暫存、提交或推送。修正後仍由 Claude 複核，本計畫保持進行中。

第一組新增六個固定答案測試，第一次全部失敗，收據 `20261006-092113640-a00b046634b046e8b960ce052e6864ee`。
失敗分別是空白科目出現在未配對清單、預覽回傳原始空白分類與未整理的編號，以及分類欄被名稱關鍵字先認領。
接著補測匯入寫入成功後摘要查詢失敗與取消的情況，先保留失敗再修程式。

第一組既有預覽測試若原先要求暫存原貌，改成要求已存配對的原因：依 D5 裁定，預覽必須與實際用於篩選的配對一致。
空白分類顯示已存的 Others，重複科目只顯示最後儲存的一筆；不能再於讀取時用新版欄名規則重新解讀舊檔。
未匯入時仍沒有資料列，固定欄名則與已匯入時一致。這些變更只影響預覽，不更動原始暫存資料或分類留白的提醒。

匯入摘要故障的首次失敗收據為 `20261006-092203902-05b1e9959e2d4ab2baf16fa8c77c1e8e`。
修正後曾由架構檢查抓到處理器重做欄位判斷，收據 `20261006-092439030-4ac668e2c76a4ac5bd51ca895637d609`；
已將提醒的判定移回 AuditCore，不更改架構斷言。科目配對 Focused 通過，收據 `20261006-092545302-361a2bbb2e2d403f88a095d460f11741`。

第二組既有測試預期變更原因：原測試要求快捷直接改名或刪除被占用的攸關資料元素，與 R5 裁定相反。
改成先顯示來源欄目前用途，確認後只改被選欄的名稱與型別，沿用其識別碼；取消則保留全部欄位。
若已經有另一欄叫「傳票建立日」，保留該欄的來源、名稱與識別碼，不默默刪除或改名；提示一併說明另一欄會保留。
這讓原有情境仍能引用原欄位；審計員若不需要它，可用既有欄位編輯操作自行取消。

第一組前端提醒與差異筆數重試通過，收據 `20261006-092645914-87aa89f9f7e540dcadfecaf2aed9e896`。
第二組首次失敗收據 `20261006-092729504-7486eef963614eb59caf1ae676de35bc`；修正後 FrontendMapping 通過，
收據 `20261006-092759085-31cd3011768a4ecd89ecc7ddbf49f8da`。測試也確認案件切換或欄位另經修改後，舊確認按鈕不能更動新狀態。

第三組既有測試變更原因：篩選規則已在本計畫推進至 v18，但方法名稱與註解仍停在 v17，舊版本案例也漏了 v17。
本組同步名稱、註解與舊版案例，不改目前 v18 的固定答案，也不放寬舊版結果必須失效的斷言。
新增共用日期表，讓前端顯示與後端各自對照同一組手算邊界；另測契約不能同時聲稱兩個現行版本。

第三組首次收據 `20261006-092855118-bd84e23fac534a6ab72d3fe0f3c2a6e2`：契約版本不一致的測試失敗，日期表的後端測試第一次即通過。
後續架構檢查另抓到第二組新增確認操作，使共用來源欄清單多呼叫一次，收據 `20261006-092936232-eafcc82f770c4da88cde877aa0b81abe`。
既有預期由三次改為四次，原因是確認前必須再檢查來源欄是否已被核心欄位占用；共用規則與禁止前端產生識別碼的斷言均保留。

第三組修正後 Focused 通過，收據 `20261006-093021377-9cb63e59d41c47f78ca394f836d49903`；
FrontendMapping 通過，收據 `20261006-093035395-948e9e7559e447be80fb5330bfa50e89`。六組共用日期答案的前後端測試第一次都通過，沒有修改日期算法。

第四組既有測試變更原因：SQL Server 的有效分錄預覽已加入人工或自動欄，測試應明確要求九欄並核對欄名及值，不能等連線時才發現八欄預期過時。
私人報告比對的舊測試曾允許人工旗標欄任何值差異；依 D3 裁定改為只正規化舊端的 1、0，之後該欄仍有差異一律不接受。
對調、整欄空白與未明示裁定都用合成工作簿測試，不讀取私人案件。

第四組首次失敗收據 `20261006-093139940-ec1a2743faf648da904c5e07748b0545`：舊政策仍接受整欄差異及錯誤旗標。
改成比對前正規化指定工作表、指定欄的舊端 1、0，並保留空白位置；正規化後人工旗標必須逐列相同，不適用一般欄位的排序差異豁免。
合成比對測試通過，收據 `20261006-093248733-08056c37c0e345f49c3c17b53ad4501c`。PrivateCase 未執行，SQL Server 只更新測試與共用欄序，未連線執行。

第五組先補實際列號測試與文件檢查。首次收據 `20261006-093407299-685fbd65928944478cab76cb06c84cfc`：Open XML 的含空白列案例第一次通過，
失敗的是指南尚未說明假日匯入固定略過第一列。這次沒有改讀取算法；補上二進位讀取器同一邊界測試、介面說明與指南。
正式假日範本不受影響；第一列空白、第二列為說明、第三列才是標頭的非標準檔案，不能再依賴舊版碰巧略過第二列。

第五組含空白列的兩種 Excel 讀取測試通過，收據 `20261006-093448059-ab768ab263cc49898d7f9f78fa6f145a`；
文件與 Open XML 測試通過，收據 `20261006-093500009-382a3e7061b340c297c6f65fc93ddc8d`。二進位讀取器新增案例第一次即通過。
第六組首次失敗：日曆焦點收據 `20261006-093542005-741295c64f9346b5bb34b9efa2c990ba`，
範本資料庫保留設定收據 `20261006-093547014-ffe2a0e76b1f406283b046bdcd4c7506`。
修正後分別通過，收據 `20261006-093616127-a565058a909d491c9cf5c4cbe3b4999a`、`20261006-093622125-6df8fb8cfc9b46eb8d0007e6b5db5377`。
年月選單與前後月按鈕重畫後保留焦點；日期格仍按方向鍵移到新日期。範本輸出不再為沒用到的資料庫保留連線。

第七組先新增 Gui 程式的涵蓋範圍檢查，不啟動桌面。沿用既有兩個情境，補實際加入 B 卡、A 到 E 動機固定文字及讀回輸出的人工旗標。
原先預計不增加情境，也不調整操作或時間上限；操作次數核對後的必要調整記在下方。真正 Gui 執行仍等使用者另行同意。

第七組涵蓋範圍檢查首次失敗收據 `20261006-093725693-3bf5fe6685554c09b2f55b4907d2c885`。
加入操作與合成工作簿讀回後通過，收據 `20261006-093854229-cced19a5b0c547d99a01cd9fed41f70f`；這不是桌面 Gui 通過的證據。
後續核對操作次數，發現原先「不調整操作上限」的安排無法包含新增操作：A 到 E 各加入與移除一次，共新增十次點擊。
新檢查取得舊設定 88 次不符 98 次的首次失敗，收據 `20261006-094148040-a38c69a4bcf04f05b38d7d90c725cc6c`。
因此把該情境的預期次數及上限同步設為固定 98 次，仍要求精確相等，不能少做或多做；不提高時間與截圖上限，情境總數仍為 17。

第八組文件檢查首次失敗收據 `20261006-093928950-32ec0c6108a649d8af00c1814e667524`，指出畫面用語、範本入口位置與 KCT 清單的不確定性尚未寫明。
交付說明已改用畫面的「攸關資料元素欄位」，補第二步入口與日期範例；現行文件改用完整的計畫名稱，不以「上一份」指稱。
配對預覽、匯入提醒與快捷確認的操作說明也同步本輪修正。原有 K1 與 K5 測試改動原因補在各自紀錄，不改那些測試。

收尾補測同名傳票建立日欄已被改成文字型別的情況，首次失敗收據 `20261006-094613122-13bac2ac9df84112b5d6a11c0e29b289`。
已修成同樣先確認，然後改回日期型別並保留識別碼。另將新增的 Gui 工作簿讀回程式納入驗證工具的來源清單，避免收據漏記執行來源。
首次完整 Build 在 NuGet 還原受阻，收據 `20261006-094344818-6688e0c9dc144a4984777dde3679a9df`，不是測試失敗。
本輪沒有新增套件，但還原失敗使既有資產也受影響；`-NoRestore` 的 Build 仍失敗，收據 `20261006-094732075-6bbc1521792c4b67a69099e0d758d859`。
經允許網路存取後，正式 Build 完成還原及建置，後續 Public 與 Package 使用 `-NoRestore`。Excel 首次也因套件來源受阻，
收據 `20261006-095248753-1900f7e8f2594ed29eac66edc464a4ba`；以相同命令允許網路存取後通過。

#### 第二輪完整驗證

2026-10-06 使用者同意開始桌面驗證，原話：「可以，請開始驗證測試」。
本輪 Gui 首次失敗收據 `20261006-101537762-f51f60acc43b4d66a7dc442edecb98f6`：前七個情境通過，第八個 `filter-kct-editing`
在加入 A 卡後逾時，後九個情境未執行。測試程序已結束，暫存案件已清除，沒有讀取私人案件。
修正既有 Gui 預期的原因：`filter-step.js` 的 `kctScenarioRationale` 會在評估說明前加上「A：」「B：」等卡片字母，
既有前端測試也明確要求此格式。新加的 Gui 檢查漏了這個前綴，不是產品缺少動機。
檢查改成比對「卡片字母、全形冒號、完整評估說明」，仍要求逐字相等；不改產品、不提高逾時或操作上限。
另補不開桌面即可執行的檢查，避免 Gui 的預期再次漏掉前綴。完整重跑仍先取得使用者同意。
這項補測的首次失敗收據為 `20261006-102205961-750714a73b224147af307c86fb4cf2bc`。
使用者對重跑完整 17 個情境再次同意，原話：「可以，修正後直接重跑」。
前綴修正後，Focused 通過，收據 `20261006-102240698-5fe7ca90243e4957bd6638838d4b46c8`；
FrontendMapping 通過，收據 `20261006-102251525-e3361a0bdb2f457d8e81e8d8ffede61d`。
接著完整 Gui 通過，收據 `20261006-102310076-a712cba67f674dc7815d539cf64f071f`：17 個情境全數完成。
KCT 情境實際操作 A 到 E，逐字核對含字母前綴的動機，確認 B 卡加入條件；操作精確為 98 次。
`side-month-workflow` 從剛匯出的合成底稿讀回 Step 4-1，確認「人工」「自動」都有出現且儲存為文字，操作精確為 79 次。
本次續跑只修正 Gui 的預期文字與補測，沒有改產品程式或私人案件比對器。下表保留前一輪其他指定檢查的通過收據，不宣稱這次全部重跑。

| 檢查 | 結果 | 收據 |
|:---|:---|:---|
| Build（Debug） | 通過，包含套件還原 | `20261006-094749714-c9b75c7d653b4c8094ccca976b11bcfe` |
| Contract（不帶情境） | 通過 | `20261006-094822115-559c73b97f8943599cbea822a6b91593` |
| Contract `FrontendMapping` | 通過 | `20261006-094824902-a3416f6b8a7146f3a1cc9d2cfe3a9a35` |
| Contract `FrontendPreview` | 通過 | `20261006-094831011-1e467c4d517b4f859038b2bd27a9a416` |
| Public `-NoRestore` | .NET 4,618 個案例、前端 311 個案例及預覽檢查通過，沒有失敗或略過 | `20261006-094835986-8f06786a9e9243c6ac4bc738c600f28e` |
| Package（Release，`-NoRestore`） | 104 個案例及封裝檢查通過 | `20261006-095210933-885ed6ec4fe646888333a492f11fa7ce` |
| Excel（Release） | 六種合成報表的原生 Excel 檢查通過 | `20261006-095302017-38c9953c9a6149a6b8d10ec797af5db9` |
| Documentation | Gui 結果寫回後通過，46 份文件，沒有錯誤或警告；改動段落另行重讀 | `20261006-102944802-69c75380657b4772a0970f1644ffb7e4` |
| Gui（AgentGuiTest） | 完整 17 個情境通過；第一次失敗與修正原因保留於上方 | `20261006-102310076-a712cba67f674dc7815d539cf64f071f` |
| PrivateCase | 本輪未執行，依使用者要求等待另行決定 | 不沿用前輪結果作為本輪比對器的驗證 |
| SQL Server | 未啟動，只經編譯；對應測試已同步但未連線執行 | 無實機結果 |

首次失敗與後續結果均保留在 `artifacts/harness/runs/`，這輪沒有讀取私人案件。八組的補測或合成檢查不是實際 Gui 或私人案件驗收。
各項問題的修正及 D8 桌面操作驗證已完成，下一步交由 Claude 複核。計畫保持現行，不先歸檔；不自行執行 PrivateCase，也不暫存、提交或推送。
寫回收據後的 Documentation 再檢查也通過，收據 `20261006-095659100-3b6cd18b9ab74f519f7baab0027f814c`，沒有警告。
已完整重讀本節、交付說明及各現行文件的改動段落，保留私人案件未涵蓋人工旗標、SQL Server 未連線及 KCT 清單未齊的限制。
Gui 通過後再核對開發現況的 11 項「已知但延後的事項」，各項延後原因與重啟條件均未改變；仍依本計畫前面的「延後事項核對」表保留。
17 個情境的測試程序都已結束，暫存案件均已清除；沒有留下 SQL Server 程序，Git 暫存區為空。

#### 第二輪 Git 逐檔清單與提交訊息草稿（已作廢）

這份清單與草稿已作廢，只保留紀錄。第三輪改了檔名與內容，提交訊息第二點的條件也寫錯了；
現行版本見第八節「第四輪 Git 逐檔清單與提交訊息草稿」。

以下清單包含這次 KCT 兩批實作與第二輪修正，共 101 個明確檔案。已逐項確認都是檔案，沒有 `data/` 或 `artifacts/` 底下的路徑，暫存區為空。
Gui 通過後再次核對，檔案集合仍為下列 101 個路徑；這份清單與草稿都不是提交授權。

```text
docs/action-contract-manifest.md
docs/data-and-legacy.md
docs/development-status.md
docs/history/README.md
docs/idea-replacement-scope.md
docs/jet-frontend-description.md
docs/jet-guide.md
docs/kct-gl-workflow.md
docs/project-context.md
docs/README.md
docs/specs/2026-10-06-kct-gl-requirements-plan.md
docs/test-environment-notes.md
src/JET/JET/AppCompositionRoot.cs
src/JET/JET/AppCompositionRoot.Repositories.cs
src/JET/JET/Application/Handlers/ExportCalendarTemplatesHandler.cs
src/JET/JET/Application/Handlers/ExportReportHandlers.cs
src/JET/JET/Application/Handlers/ExportWorkpaperStreamHandler.cs
src/JET/JET/Application/Handlers/Import/ImportAccountMappingHandler.cs
src/JET/JET/Application/Handlers/Query/QueryAccountMappingDifferencePageHandler.cs
src/JET/JET/Application/Handlers/Query/QueryFilterVoucherHandlers.cs
src/JET/JET/Application/ProjectRepositories.cs
src/JET/JET/Application/Support/FilterConditionRenderer.cs
src/JET/JET/Application/Support/ProjectWorkFileWriter.cs
src/JET/JET/Application/Support/RuleLogicVersions.cs
src/JET/JET/AuditCore/IntakeMappingProgram.cs
src/JET/JET/AuditCore/WorkpaperProgram.cs
src/JET/JET/Domain/Abstractions/AccountMappingDifferenceRepository.cs
src/JET/JET/Domain/ActionExecutionPolicy.cs
src/JET/JET/Domain/Contracts/AccountMappingContracts.cs
src/JET/JET/Domain/Contracts/ICalendarTemplateSource.cs
src/JET/JET/Domain/Contracts/ImportContracts.cs
src/JET/JET/Domain/Rules/AccountMappingColumnMatcher.cs
src/JET/JET/Domain/Rules/QuarterEndWindows.cs
src/JET/JET/Infrastructure/Diagnostics/SupportRingBufferLoggerProvider.cs
src/JET/JET/Infrastructure/Export/CalendarTemplateSource.cs
src/JET/JET/Infrastructure/Export/WorkpaperWriter.Step2To41.cs
src/JET/JET/Infrastructure/FileIO/OpenXmlSaxTableReader.cs
src/JET/JET/Infrastructure/Persistence/AccountMappingDifferenceQuery.cs
src/JET/JET/Infrastructure/Persistence/AccountMappingEditorRepository.cs
src/JET/JET/Infrastructure/Persistence/AccountMappingPopulationQuery.cs
src/JET/JET/Infrastructure/Persistence/DataPreviewColumns.cs
src/JET/JET/Infrastructure/Persistence/Local/LocalAccountMappingDifferenceRepository.cs
src/JET/JET/Infrastructure/Persistence/Local/LocalDataPreviewRepository.cs
src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerAccountMappingDifferenceRepository.cs
src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerDataPreviewRepository.cs
src/JET/JET/wwwroot/js/app.js
src/JET/JET/wwwroot/js/data-preview.js
src/JET/JET/wwwroot/js/filter-values.js
src/JET/JET/wwwroot/js/jet-api.js
src/JET/JET/wwwroot/js/steps/filter-step.js
src/JET/JET/wwwroot/js/steps/import-step.js
src/JET/JET/wwwroot/js/steps/mapping-step.js
src/JET/JET/wwwroot/js/steps/validate-step.js
src/JET/JET/wwwroot/js/ui-core.js
src/JET/tests/JET.Tests/Application/AccountMappingTemplateExportTests.cs
src/JET/tests/JET.Tests/Application/ActionSerializationTests.cs
src/JET/tests/JET.Tests/Application/AdvancedFilterAstContractTests.cs
src/JET/tests/JET.Tests/Application/ExportCalendarTemplatesHandlerTests.cs
src/JET/tests/JET.Tests/Application/FilterConditionRendererTests.cs
src/JET/tests/JET.Tests/Application/ImportAccountMappingHandlerTests.cs
src/JET/tests/JET.Tests/Application/InlineWorkbookProject.cs
src/JET/tests/JET.Tests/Application/QueryDataPreviewHandlerTests.cs
src/JET/tests/JET.Tests/Application/RuleLogicVersionsTests.cs
src/JET/tests/JET.Tests/Application/SecondReviewAccountMappingTests.cs
src/JET/tests/JET.Tests/Application/TypedFieldFilterContractTests.cs
src/JET/tests/JET.Tests/Application/WorkpaperStep41ProviderParityTests.cs
src/JET/tests/JET.Tests/Architecture/FrontendUsabilityContractTests.cs
src/JET/tests/JET.Tests/Architecture/HandlerRepositoryScopeTests.cs
src/JET/tests/JET.Tests/Architecture/MappingProjectionPolicyFrontendTests.cs
src/JET/tests/JET.Tests/Architecture/ReportExportOwnershipTests.cs
src/JET/tests/JET.Tests/Architecture/ScreenWordingReviewFrontendTests.cs
src/JET/tests/JET.Tests/Architecture/SecondReviewDocumentationTests.cs
src/JET/tests/JET.Tests/Architecture/SecondReviewGuiCoverageTests.cs
src/JET/tests/JET.Tests/Architecture/SecondReviewVersionTests.cs
src/JET/tests/JET.Tests/Architecture/SupportedActionsParityTests.cs
src/JET/tests/JET.Tests/AuditCore/IntakeMappingProgramTests.cs
src/JET/tests/JET.Tests/AuditCore/WorkpaperProgramTests.cs
src/JET/tests/JET.Tests/Domain/AccountMappingColumnResolverTests.cs
src/JET/tests/JET.Tests/Domain/QuarterEndWindowsTests.cs
src/JET/tests/JET.Tests/Infrastructure/BinaryExcelTableReaderTests.cs
src/JET/tests/JET.Tests/Infrastructure/KctFilterPredicateTests.cs
src/JET/tests/JET.Tests/Infrastructure/LegacyWorkbookContentComparison.cs
src/JET/tests/JET.Tests/Infrastructure/OpenXmlSaxTableReaderTests.cs
src/JET/tests/JET.Tests/Infrastructure/PrivateCaseReportComparator.cs
src/JET/tests/JET.Tests/Infrastructure/PrivateCaseReportComparatorTests.cs
src/JET/tests/JET.Tests/Infrastructure/PrivateCaseReportDifferencePolicy.cs
src/JET/tests/JET.Tests/Infrastructure/SecondReviewReaderTests.cs
src/JET/tests/JET.Tests/Infrastructure/SqlServerDataPreviewRepositoryTests.cs
src/JET/tests/JET.Tests/Infrastructure/WorkpaperStep41LegacyBaselineBenchmarkTests.cs
src/JET/tests/JET.Tests/JET.Tests.csproj
tools/harness/documentation-check.json
tools/harness/gui-driver/GuiFilterWorkflowScenarios.cs
tools/harness/gui-driver/GuiSideMonthWorkflow.cs
tools/harness/gui-driver/GuiWorkpaperInspection.cs
tools/harness/JetHarness.psm1
tools/harness/lanes.json
tools/README.md
tools/tests/fixtures/quarter-end-windows.json
tools/tests/frontend-mapping.test.cjs
tools/tests/frontend-workflow.test.cjs
tools/tests/verify-contract.tests.ps1
```

提交訊息草稿如下，已補入 Gui 結果及兩項風險，不加署名：

```text
補齊 KCT 條件與科目配對，修正交付前複審問題

- 完成 KCT 條件、人工旗標顯示及操作入口，修正配對母體、匯入摘要、已存資料預覽與欄名提醒。
- 舊案件開啟時，通過相容檢查的已存情境會整批改用新版篩選規則，舊條件篩選報告與底稿標為過期，首次查看或匯出時重算。
- SQL Server 只經編譯，對應測試已同步，尚未連線驗證。
- Build、三種 Contract、Public、Release 封裝、Excel、17 個 Gui 情境及 Documentation 通過。PrivateCase 本輪未重跑，人工旗標目前僅有合成測試補證。
```

### 第二輪修正的複核與第三輪（2026-10-06）

第二輪修正完成後，Claude 這邊再派三個唯讀子代理複核：一個看科目配對與傳票建立日（R3、R4、D5、D6、R5 與驗證工具改動），
一個看版本、人工旗標、讀取器與 Gui（D1、R1、D2、R6、D3、R7、D4、D7、D8），一個看文件、收據與 Git 清單。都只讀工作樹差異、
本計畫與收據，沒有執行驗證命令，沒有讀私人資料目錄。主線另外親自核對匯入摘要的計數位置、共用科目母體查詢、傳票建立日
的確認流程、執行政策清單與訊息等級五處。

#### 複核結論

18 項都修到根本原因，既有測試沒有被放寬，改既有測試預期的地方都寫了原因。42 份收據都存在且與文字一致；101 個 Git 路徑與
工作樹完全相同，沒有 `data/`、`artifacts/` 或私人路徑。沒有發現機敏資料。Gui 首次失敗後只改了 Gui 檢查的預期文字與補測，
沒有改產品程式、逾時或操作上限；操作次數從 88 改成 98 是 A 到 E 各加入與移除一次，仍要求精確相等。

#### 第三輪要處理的問題

| 編號 | 問題 | 位置 | 做法 |
|:---|:---|:---|:---|
| T1 | 欄名提醒用 `'warning'` 當訊息等級，後端只收 `'info'` 與 `'warn'`。這筆訊息被拒收後卡在待存佇列最前面，同一次開啟案件後的所有訊息都存不進去，「複製紀錄」也會失敗；畫面也不會顯示成提醒樣式。現有測試只比對文字。 | `validate-step.js:1665`；`MessageLogHandlers.cs:15`；`app.js:1872-1879` | 改成 `'warn'`，前端測試同時斷言等級。 |
| T2 | 提交訊息第二點寫「通過相容檢查的已存情境會整批改用」，與 `jet-guide.md` 第 218 到 223 行的規則不符：全部情境都通過才整批改版，任一不通過就全部不動並列出原因。另漏了三個使用者看得到的變化：底稿 Step 4-1 人工旗標由數字改為文字；查核期末不是季底的案件 KCT A 命中可能變多；假日檔第一列空白時以前碰巧可讀、現在會讀錯。「配對母體」「已存資料預覽」是自創說法。 | 本計畫「第二輪 Git 逐檔清單與提交訊息草稿」 | 第三輪修正完成後重新定稿：條件寫對、補三個變化、改用畫面上的說法（可配對的科目清單、科目配對預覽）。 |
| T3 | 五個新測試檔都以「SecondReview」命名，檔名寫的是複審輪次而不是測的行為。其中 `SecondReviewDocumentationTests`、`SecondReviewReaderTests` 與 `SecondReviewVersionTests` 的部分案例逐字檢查交付說明、指南與 action 契約的句子；交付說明每次交付都整份改寫，之後會讓單元測試失敗，只改文件也不會發現。`documentation-check.json` 本來就有必含句與不得出現句的機制。 | `src/JET/tests/JET.Tests/**/SecondReview*.cs`；`tools/harness/documentation-check.json` | 依行為改名，例如 `QuarterEndWindowsFrontendParityTests`、`AccountMappingImportSummaryTests`、`AccountMappingColumnMatcherTests`；文件措辭的檢查搬進 `documentation-check.json`，單元測試裡刪掉。契約版本的檢查改成對程式常數（T5）後留在測試裡。 |
| T4 | 科目配對範本的匯出仍是自己一份查詢，沒有排除空白科目編號。總帳有空白編號時範本多一列空白，審計員填完再匯入會被判成「科目編號空白」而整份拒收；若空白存成空值，匯出讀字串那一行可能出錯（未實測）。`AccountMappingDifferenceQuery.cs:7` 的註解說與範本共用，與實際不符。 | `LocalAccountMappingExportRepository.cs:59-66`；`SqlServerAccountMappingExportRepository.cs:58-64` | 兩個匯出都改用 `AccountMappingPopulationQuery`，改正註解，補一個總帳含空白編號的合成測試（SQLite 與 DuckDB）。 |
| T5 | 防止契約寫兩個現行版本的測試只認「現行篩選版本為」一種寫法，這次出錯的第 375 行用的是「目前篩選計算版本為」；測試寫死 v18，沒拿程式常數比。 | `SecondReviewVersionTests.cs:22-27`；`action-contract-manifest.md:375、491` | 抓契約裡所有「現行」或「目前」句子的 `filter-…-vN`，逐一要求等於 `RuleLogicVersions.Filter`。 |
| T6 | 私人案件比對器新增的 `familyCoverage` 參數沒有用到，使幾個斷言只能得到「不接受」而什麼都沒驗；「空白位置移動也要抓到」與「只改指定工作表指定欄」程式有做但沒有合成案例。另一個取捨：人工旗標欄要求列序完全相同，列序與舊工具不同的案件，其他欄會被既有裁定放行、這欄會失敗。 | `PrivateCaseReportDifferencePolicy.cs:108`；`LegacyWorkbookContentComparison.cs:284-300`；`PrivateCaseReportComparatorTests.cs:13-30` | 刪掉沒用的參數與空斷言；補兩個合成案例；列序的取捨寫進本計畫與交付說明。 |
| T7 | 第八節有兩份複審前的 Git 清單與提交草稿，沒有標示作廢，列的歷史路徑已不存在、也少 23 個新檔。開發現況對 R4 的 SQL Server 紀錄沒寫已排除空白編號、科目清單也改用共用查詢。`project-context.md:33` 寫了會過期的進度。幾份收據沒列進計畫：最後一次 Documentation、範本匯出測試的首次失敗、同名欄補測通過。 | 本計畫第 625、712、945 行附近；`development-status.md:109-110`；`project-context.md:33` | 舊清單與舊草稿標成「已作廢」並指向現行；補開發現況那兩句；`project-context.md` 只留一句指向計畫；補列收據。 |
| L1 | 舊結構案件升級時仍用新判斷器重讀分類欄，與「已存配對是唯一來源」不一致；只用在回填「分類有沒有填」的標記。 | `AccountClassificationMigration.cs:94` | 改讀已存配對，與 D5 同一原則；補遷移測試。 |
| L2 | 日曆日期格的方向鍵沒有測試，整個前端測試找不到方向鍵案例。 | `filter-values.js:575-582`；`frontend-workflow.test.cjs:3559` | 補一個方向鍵移動日期格的案例。 |
| L3 | 把 1、0 轉成人工、自動的那一行在本機與 SQL Server 預覽各寫一份；欄名已共用。 | `LocalDataPreviewRepository.cs` 約第 300 行；`SqlServerDataPreviewRepository.cs:269` | 轉換收進 `DataPreviewColumns` 旁的共用定義，兩個實作都呼叫它；SQL Server 只經編譯並記錄。 |
| L4 | 傳票建立日確認後可能出現兩個同名欄位，第五步欄位清單看不出來源欄。 | `mapping-step.js:841-844`；第五步欄位清單 | 第五步的攸關資料元素欄位清單加註來源欄；只改顯示，不改識別碼。 |
| L5 | 測試專案第一次直接引用 `tools/` 底下的程式（Gui 的底稿讀回），是新慣例。 | `JET.Tests.csproj:38`；`tools/harness/gui-driver/GuiWorkpaperInspection.cs` | 使用者未裁定，標為待確認；第三輪不動，接手時向使用者確認接受或搬進測試專案。 |
| L6 | 契約第 491 行的「條件 A」可能被誤認成 2024 舊表的 A。 | `action-contract-manifest.md:491` | 改成「KCT 條件 A」。 |
| L7 | 開發現況仍有「前一份計畫」的相對說法，計畫歸檔後會指錯對象。 | `development-status.md:32、41` | 直接寫出計畫名稱。 |

#### 使用者裁定（2026-10-06）

使用者在本機勾選頁回覆，逐字如下：

```text
KCT 第三輪複核裁定回覆（2026-10-06 清單）

## 提交前必須處理
- T1 欄名提醒的訊息等級寫錯，會讓整個案件的訊息紀錄卡住：改成 warn 並補測試（建議）
- T2 提交訊息第二點把舊案自動換版的條件寫反，也漏了三個使用者看得到的變化：照建議重新定稿（建議）
- T3 五個新測試檔都叫「SecondReview⋯」，其中三個逐字檢查文件措辭：改名並把文件檢查搬進 documentation-check.json（建議）

## 同根因的尾巴
- T4 科目配對範本的匯出還是自己一份查詢，沒排除空白科目編號：改用共用查詢並補測試（建議）
- T5 防止契約寫兩個現行版本的測試太窄：改成對程式常數（建議）
- T6 私人案件比對器留了沒用到的參數，兩段邏輯沒有專門測試：刪參數、補測試、寫取捨（建議）
- T7 計畫與現況文件的小整理：照建議整理（建議）

## 可延後的事
- L1 舊結構案件升級時仍用新判斷器重讀分類欄：這次一起修
- L2 日曆日期格的方向鍵沒有測試：這次補測試（建議）
- L3 人工旗標轉成文字那一行在本機與 SQL Server 各寫一份：這次一起修
- L4 確認後可能出現兩個「傳票建立日」欄位，第五步看不出來源欄：這次一起修
- L6 契約第 491 行的「條件 A」可能被誤認成舊表 A：這次一起改（建議）
- L7 開發現況還有「前一份計畫」的相對說法：這次一起改（建議）

## 下一步
- N1 第三輪誰修：Claude 這邊修。備註：請讓我在新的 session 處理
- N2 第三輪修完要跑哪些驗證：Build、Contract、Public、Documentation，並授權 PrivateCase 各一次（建議）
```

L5 沒有在回覆中出現，視為尚未裁定，第三輪不動。

#### 第三輪的順序與完成條件

由 Claude Code 的新 session 執行，順序依改動範圍：

1. T1 訊息等級與測試。
2. T4 範本匯出改用共用查詢、L1 遷移改讀已存配對、L3 人工旗標轉換共用。
3. T5 契約版本測試改對程式常數、T3 測試檔改名與文件檢查搬家、L6 契約用語。
4. T6 比對器參數、合成案例與取捨說明。
5. L2 方向鍵測試、L4 第五步欄位清單加註來源欄。
6. T7 與 L7 文件整理，最後 T2 重新定稿 Git 逐檔清單與提交訊息。

每項先寫會失敗的測試並保存第一次失敗；T3 的改名與搬家屬於測試整理，不需要先失敗，但搬進 `documentation-check.json` 的句子要先確認
Documentation 會抓到。改既有測試預期時先在本節寫明原因。完成條件：Build、Contract（不帶情境、FrontendMapping、FrontendPreview）、
Public、Documentation 通過；PrivateCase 依 N2 裁定在 Release 組態對 SQLite 與 DuckDB 各執行一次，私人路徑與輸出照規則不記錄、
不保留。Gui 不重跑：這輪不動 Gui 情境與底稿輸出。SQL Server 不啟動。完成後回報使用者，不暫存、提交或推送。

#### 第三輪執行紀錄（2026-10-06）

使用者本次原話：

```text
請接手 je-tool 的現行計畫 docs/specs/2026-10-06-kct-gl-requirements-plan.md。
先執行 pwsh -NoProfile -File tools/verify.ps1 -Command Context，再讀計畫第八節「第二輪修正的複核與第三輪（2026-10-06）」。

你負責「第三輪的順序與完成條件」列的六組工作，依序做，L5 不動。每項照表中「做法」修根本原因，不做局部補丁；修正不能偏離 JET 的審計業務邏輯，也要顧到審計員的操作體驗。每項先寫會失敗的測試並保存第一次失敗；T3 的改名與搬家不需要先失敗，但搬進 documentation-check.json 的句子要先確認 Documentation 會抓到。要改既有測試的預期時，先在該節寫明原因。文件與畫面文字依 jet-readable-docs 重讀，讀者是非專業人員，少用代號與術語。

全部做完後執行 Build、Contract（不帶情境、FrontendMapping、FrontendPreview）、Public、Documentation。PrivateCase 本次授權在 Release 組態對 SQLite 與 DuckDB 各執行一次，私人路徑與輸出照規則不記錄、不保留。Gui 不重跑，SQL Server 不啟動。

結果、首次失敗與收據寫回該節下方；依 T2 重新定稿 Git 逐檔清單與提交訊息草稿，舊的兩份標成已作廢。不要暫存、提交或推送，回報我後停止。
```

開始時保留兩批與第二輪留下的全部未提交變更，HEAD 為 `a7f5982a7772c1ff6012946bec9faf5148e11264`。本輪只為了執行 PrivateCase 找出私人案件清單的位置，
沒有讀其中的資料；私人資料只交給授權的 PrivateCase 處理。改既有測試的原因先寫在下面兩段，再動測試。

T6 改既有測試的原因：`PrivateCaseReportComparatorTests` 的人工旗標裁定測試有四個斷言，把工作表各欄的比對摘要傳給比對政策，
期待結果是「不接受」。但比對政策在第二輪 D3 修正後已不再讀這個參數，四個斷言和同一測試的第一個斷言其實是同一件事，什麼都沒多驗。
依 T6 裁定刪掉這個參數，這四個斷言也一併刪除；第一個斷言保留，仍要求裁定本身不能放行人工旗標欄的差異。
這不放寬任何檢查，另補的合成案例見下方。

L4 改既有測試的原因：`TerminologyUnificationFrontendTests` 逐字找 `filter-values.js` 裡在欄位名稱後加「（攸關資料元素欄位）」的那段程式。
L4 要在第五步的兩個欄位選單都加註來源欄，顯示文字改由 `ui-core.js` 的一個共用函式產生，舊的那段程式因此不在了。
測試改成確認共用函式仍寫出「攸關資料元素欄位」，而且第五步的一般欄位選單確實呼叫它；用語要求不變。

##### 各項做法與首次失敗

T1：欄名依順序判斷的提醒改用 `warn` 等級，畫面會以提醒樣式顯示，也存得進訊息紀錄。
寫錯的根本原因是前端寫訊息時沒有對照後端接受的等級，所以另加一個前端測試，掃過前端所有寫訊息的地方，
等級都要在後端 `MessageLogHandlers.cs` 列出的範圍內。這次只找到這一處寫錯。
兩個新測試第一次都失敗，收據 `20261006-113300114-2e66d1c896234167b8ad801254d811a4`；
修正後 FrontendMapping 通過，收據 `20261006-113323181-0b08b48884f64eb58d49f7f0be808bd2`。

T4：本機與 SQL Server 產生空白科目配對範本時，都改用共用的可配對科目母體 `AccountMappingPopulationQuery`，
範本與「配對檔未列的科目」使用同一批可配對科目；科目清單另包含配對檔多出的科目。`AccountMappingDifferenceQuery.cs` 與匯出介面的註解一併改正。
新測試在 SQLite 與 DuckDB 各建一個總帳含空白科目編號的合成案件。第一次失敗證實了複審時沒實測的情況：
總帳的空白科目編號在資料庫裡是空值，範本匯出讀到時直接出錯，兩種資料庫都一樣，收據 `20261006-114111933-b8d7d4683dbd4dbe903cf8364372e001`。
修正後範本只列兩個實際科目，和「配對檔未列的科目」清單相同；填好分類再匯回，兩個科目都匯入，沒有剩下未配對的科目。
這項測試通過的收據是 `20261006-114404827-11ea8105cabb403d924ba325a60b593d`，
既有範本測試也通過，收據 `20261006-114501809-4109b6b7525a4979bfa9c15ae5d46c13`。

L1：舊結構案件升級時，要補記每個科目的分類有沒有填。原本是用目前的欄名判斷規則重讀舊檔欄名；
規則改過以後，可能選到不是當初匯入用的那一欄。現在改以已存配對為準：同一批匯入裡，只有和每個科目的已存分類
都一致的來源欄才算候選。留白要對應 Others，有值就要等於已存分類的代碼、名稱或舊版分類名。
已存分類不是 Others 的科目一定有填；Others 的科目看候選欄是否留白，候選欄說法不一致就不記，也不計入分類留白。
分類本身與已存情境都不改。SQL Server 走同一段程式，只經編譯。
新測試有兩組：目前規則會選錯欄的例子，在 SQLite 與 DuckDB 各跑一次；以及兩個候選欄說法不同的例子。
第一次三個案例都失敗，前一組把有填和留白讀成相反，收據 `20261006-114151938-6a599889dd24453a81e73cf27c399f55`。
修正後通過，原有的升級測試也仍通過，收據 `20261006-114429637-e93e8fc6071b4715abcd0a5ba4991a19`。

L3：把人工旗標的 1、0 轉成預覽代碼的那一行收進 `DataPreviewColumns.ManualAuto`，本機與 SQL Server 預覽都呼叫它。
新的架構測試要求兩個預覽實作都呼叫共用定義，不再自己寫代碼；第一次失敗收據
`20261006-114156883-5f7bc69159a44ba280cb807045bf60dd`，修正後通過收據 `20261006-114451265-3f0ae4df439c4462ad67e54d523b587e`。
預覽顯示的結果不變，既有預覽測試照舊通過；SQL Server 只經編譯。

T5：新測試找出 action 契約裡所有寫「現行」或「目前」的句子，句中的篩選版本逐一要等於程式常數
`RuleLogicVersions.Filter`；另一個測試用合成文字確認兩種說法都抓得到，版本沿革的句子不會被誤抓。
為了證明新測試比舊的強，先暫時把契約第 375 行改回複審抓到的 v17 寫法：新測試失敗，舊測試仍通過，
收據 `20261006-114744077-9888540353eb4e739cd4419a42f06b76`。契約隨後從備份還原，逐位元比對相同。

T3：五個以「SecondReview」開頭的測試檔，依測的行為改名或拆開：

| 原檔 | 現在的位置 |
|:---|:---|
| `Application/SecondReviewAccountMappingTests.cs` | 匯入摘要與預覽三個測試移到 `Application/AccountMappingImportSummaryTests.cs`；欄名判斷兩個測試併入既有的 `Domain/AccountMappingColumnResolverTests.cs` |
| `Architecture/SecondReviewVersionTests.cs` | 季底視窗的前後端一致測試移到 `Architecture/QuarterEndWindowsFrontendParityTests.cs`；契約版本測試依 T5 改寫成 `Architecture/ActionContractRuleVersionTests.cs` |
| `Infrastructure/SecondReviewReaderTests.cs` | 略過列數的讀取測試併入既有的 `Infrastructure/OpenXmlSaxTableReaderTests.cs`；指南措辭改由文件檢查負責 |
| `Architecture/SecondReviewGuiCoverageTests.cs` | 內容不變，改名為 `Architecture/GuiScenarioCoverageTests.cs` |
| `Architecture/SecondReviewDocumentationTests.cs` | 刪除，兩項措辭檢查改由文件檢查負責 |

文件措辭的七項檢查搬進 `tools/harness/documentation-check.json`：六句必須出現的句子，以及一句不得再出現的舊說法，
也就是把攸關資料元素欄位稱作額外欄位的寫法。搬家前，先暫時從交付說明、範圍文件與指南拿掉這六句，
並在交付說明加入舊說法；Documentation 回報七項全部失敗，收據 `20261006-114941939-e4071c9f5b964c379fc99d8c7f5087c1`。
三份文件隨後從備份還原，逐位元比對相同。改名後的測試都通過，收據依序為
`20261006-115011053-9c8b2a77b3bd42c7a2172c3d2cb0a3f8`、`20261006-115045884-b75a373d28ef43169be9d5a817a134ea`、
`20261006-115057838-00d67a2806a645a88b17c95701a1dac8`、`20261006-115109288-06c6674a7ac54340abe362e9daf9de2e`、
`20261006-115119886-3f94c279726e4d1eaaf3a35b87932cdc`。

L6：契約第 491 行改成「KCT 條件 A」，不會被誤認成 2024 舊表的 A。

T6：刪掉比對政策沒用到的參數與四個空斷言，原因見上。另補三個合成案例：空白位置在兩列之間移動要抓到；
同一頁其他 1、0 欄不被改寫；其他工作表的同名欄不被改寫。三個案例第一次就通過，因為程式本來就有這些處理，
收據 `20261006-115354821-80b1fa2d544343f6ac6895dbf75add5f`。為了確認它們抓得到錯，暫時拿掉保留空白位置、
限定欄位與限定工作表三段程式：三個新案例都失敗，原有案例都沒察覺，收據 `20261006-115422633-739a442341d641219aa8ebbf4112e014`。
程式隨後從備份還原，逐位元比對相同。

列序的取捨：比對器把舊工具的 1、0 換成人工、自動之後，要求這一欄逐列相同，不套用一般欄位的排序差異裁定。
這是 D3 裁定要的效果，因為旗標落在哪一張傳票上才是重點，只比整欄有哪些值會漏掉對調與空白移位。
代價是：底稿列的順序和舊工具不同的案件，其他欄只算排序不同，這一欄卻會判為不一致。
交付說明已提醒試用者，拿舊工具底稿對照時先用傳票號碼與項次對齊。

L2：補日曆方向鍵的案例。左右鍵移一天，上下鍵移一週，可以跨月；Home 與 End 移到週日與週六；
當時只確認目前日期在 Tab 順序裡，尚未檢查其他日期都被排除；第四輪 B3 補上唯一日期斷言。
移動不會選取日期，其他按鍵不攔截。方向鍵案例首次執行通過，但同次執行只有 L4 的新案例失敗，因此整體結果是失敗，
收據 `20261006-115645690-5d99db7280ce4977a6c4a48aab71859f`。暫時把上下鍵的位移對調後案例失敗，
收據 `20261006-115704504-e4ce99b99b614e168df41bcce2dc916f`，程式隨後還原。

L4：第五步的兩個欄位選單，在攸關資料元素欄位名稱後加註來源欄，例如「傳票建立日（攸關資料元素欄位，來源欄：CreateDate）」；
第三輪當時只列攸關資料元素欄位的選單不重複寫欄位種類，第四輪 B2 再統一五種選單的文字來源。選項值仍是原本的欄位識別碼，條件讀回的文字不變。
顯示文字由 `ui-core.js` 的 `rdeFieldOptionLabel` 產生，畫面說明已補進 `jet-frontend-description.md`。第一次失敗時兩個同名欄位看起來完全一樣，
收據同 L2 的第一次；修正後 FrontendMapping 通過，收據 `20261006-115748450-03090c3fefed45d4bbd77de63a77a27f`，
用語檢查通過，收據 `20261006-115753320-09d2bc7791f34f748f1b99ae25df7a13`。
沒有在瀏覽器實際操作：合成預覽的素材沒有攸關資料元素欄位，要看到得另建合成案件，這輪只用前端測試確認選單內容。

T7 與 L7：上方兩份第二輪複審前的 Git 清單與提交草稿，以及第二輪的清單與草稿，都標成已作廢並指向本節最後的現行版本。
開發現況補寫 SQL Server 的差異查詢已排除空白科目編號、科目清單也改用共用查詢，並列入第三輪只經編譯的 SQL Server 修改；
「前一份計畫」改成直接連到使用者回饋計畫。`project-context.md` 的 KCT 段落只留一句指向本計畫，不再寫進度。
`docs/README.md` 的計畫說明同步本輪狀態。第二輪沒有列進計畫的收據補在下表：

| 項目 | 結果 | 收據 |
|:---|:---|:---|
| 科目配對範本的往返測試，預覽改讀已存配對後的第一次失敗 | 範本往返兩個案例與匯入摘要兩個案例失敗 | `20261006-092310526-6fb1bd14956a4449a87eedd6d0d45d5f` |
| Gui 工作簿讀回程式的引用路徑寫錯 | 建置失敗，修正路徑後重跑 | `20261006-093825179-1a35fc5366c44e22a25557da3cf2f1f3` |
| 第二輪新測試合跑 | 通過 | `20261006-094247593-351e18849c914a42bd92c161c81c43b4` |
| 同名傳票建立日欄補測修正後 | Contract 不帶情境與 FrontendMapping 通過，前端 311 個案例 | `20261006-094618078-a9c82d99c49d4c4dab02f7fa6d84b9ed`、`20261006-094717685-94a0ab6a296847ca96589bd363c10f9b` |
| 第二輪其他 Documentation | 通過 | `20261006-095554095-468c85e4a1914026b55cf542da33802b`、`20261006-095820361-07658ea349424020800936d9b8212ef5` |
| 第二輪最後一次 Documentation | 通過 | `20261006-103019955-e1988b4385f14fa292257f2243eed647` |
| Claude 寫回複核結果後的 Documentation | 通過 | `20261006-112936311-7d850c9476b44760b4d7eb58b90d48d9` |

##### 驗證時另外發現的 DuckDB 問題

六組做完後第一次跑 Public，有一個案例失敗：T4 新加的範本測試在 DuckDB 跑「配對檔未列的科目」清單時，
DuckDB 回報自己的內部錯誤，收據 `20261006-120050942-af2e79980e4543d3bce11c21a8ae2f41`。這個案例單獨跑時通過。
這不在第三輪的 14 項裡，但它擋住了這輪要求的 Public，審計員在第四步按「列出科目」時也可能碰到，所以先查清楚再修。

主線先寫暫時的壓力測試，經正式路徑平行呼叫這份清單 400 次，有 7 次重現同一個錯誤，收據
`20261006-120607056-c07d447a007f4a829366ccd0b9237b12`。之後交給一個深度研究子代理做對照實驗，它只改暫時的實驗檔，沒有改產品程式。
保留下來的對照結果指向 DuckDB 1.5.3 在特定查詢形狀與執行緒設定下的問題，但不足以列出所有必要觸發條件。
依序執行也曾重現：`q-unmapped-seq` 為 400 次中 1 次內部錯誤；`s-full-window-threads2` 為 5,000 次中 33 次，
同次的 `s-full-window-threads1` 與 `s-full-scalar-subquery-total` 都是 5,000 次中 0 次。
之後 `par-small-window` 為 2,000 次中 1,627 次內部錯誤，`par-small-scalar-total` 為 2,000 次中 0 次。
這些是指定實驗的結果，不代表所有資料量都不會再發生。第一次 Public 失敗來自案例中的單次清單查詢，
不是必須讓畫面兩份清單同時展開才會觸發；並行讀取只用來增加重現機會。
錯誤訊息也記錄資料庫因先前的嚴重錯誤而失效，後續連線可能只回報連帶錯誤。

第四輪只核對既有收據與合成實驗輸出，沒有重跑歷史實驗。先前三次單獨執行及十次對照實驗如下。
對照實驗的整體收據都是失敗，其中部分變體的錯誤計數為 0；不能把該計數寫成整次檢查通過。
暫時實驗原始碼已刪除，收據與保留輸出未能確認「約四列」「四十列以上與二十萬列」的精確資料量，
也未能確認「獨立 DuckDB 檔可重現」。以上說法改列待確認，不從變體名稱推測資料量或資料庫來源。

| 執行 | 篩選 | 收據狀態 | 收據 |
|:---|:---|:---|:---|
| 範本測試單獨執行 | `Template_ExcludesBlankGlAccountCodes` | 通過 | `20261006-120434802-a19d808e085e4a83bd5130c7966a51d8` |
| 範本測試單獨執行 | `Template_ExcludesBlankGlAccountCodes` | 通過 | `20261006-120448356-fcc9f0efbbd944ee93ba3d8c422a0e22` |
| 範本測試單獨執行 | `Template_ExcludesBlankGlAccountCodes` | 通過 | `20261006-120501374-aa4622eb1ab54ac6921a7fe4e0495266` |
| 對照實驗 1 | `TempStress_Variant` | 失敗 | `20261006-120650047-3a39214521ff458483f27159be32b113` |
| 對照實驗 2 | `TempStress_Exp` | 失敗 | `20261006-121012353-1c4475b5bd404363b9b789abe24de937` |
| 對照實驗 3 | `TempSeq_Exp` | 失敗 | `20261006-121350644-c2d63ebbd12446c0aa14fcc6cc10807c` |
| 對照實驗 4 | `TempMem_Exp` | 失敗 | `20261006-121847806-77273ed205ab4f9f991eae2227ab977a` |
| 對照實驗 5 | `TempMem_Exp` | 失敗 | `20261006-122108952-c2dd976ff6cc47e9984d4387c34aa75b` |
| 對照實驗 6 | `TempBig_Exp` | 失敗 | `20261006-122601712-fcae84e905194e7fae40ff3d442b8718` |
| 對照實驗 7 | `TempBig_Exp` | 失敗 | `20261006-122846756-a470a8f9ff7c476ca47ba9eb232657ba` |
| 對照實驗 8 | `TempBig_Exp` | 失敗 | `20261006-123353748-20079c569a4e4dfdad815e455a4334f9` |
| 對照實驗 9 | `TempBig_Exp` | 失敗 | `20261006-124031966-368b3138333f40d9810fec5572c6479b` |
| 對照實驗 10 | `TempStress_Exp` | 失敗 | `20261006-124859629-116c5b933df643139e04ff577a6661e0` |

當時的 `AccountMappingDifferenceQuery.cs` 用 `COUNT(*) OVER ()`，三種資料庫共用這段 SQL。
第一頁的總數改成和同檔 `CountAsync` 一樣的純量子查詢，仍在同一個語句裡；SQLite、DuckDB 讀同一份資料快照，SQL Server 預設隔離設定不保證如此。
第一頁總數會另計一次科目母體，回傳的清單、總數與換頁方式都不變，SQL Server 照舊用 `COUNT_BIG`，只經編譯。
新測試 `AccountMappingImportSummaryTests.UnmappedFirstPageSurvivesConcurrentReadsOnDuckDb` 在極小合成資料上平行讀第一頁 2000 次，
要求每次都成功、總數為 2、列出兩個實際科目。修正前 2000 次有 1326 次失敗，後段都是資料庫已失效的連鎖錯誤，
收據 `20261006-125247842-c5809c1caac54f35ac1ee3434493de3d`；修正後全部成功，收據 `20261006-125312225-aacd343aa1c045329cd9e8bb1647a5c1`。
科目配對相關的 171 個案例也通過，收據 `20261006-125327834-80c13d00c78542b690936dc39bec7102`。暫時的實驗檔已刪除。

##### 第三輪完整驗證

修正 DuckDB 問題後，從 Build 起依序重跑。修正前的那一輪 Build 與三種 Contract 也通過，收據為
`20261006-120028111-be4fe61e52ca4d94a63d925405a6ae81`、`20261006-120036289-51e6ab6d035148ec80636c1b6693c8cc`、
`20261006-120038145-475ec65407e947259a1c843780218370`、`20261006-120042906-69055036751d4866a81a25036685be16`，下表只列最後有效的結果。

| 檢查 | 結果 | 收據 |
|:---|:---|:---|
| Build（Debug） | 通過 | `20261006-125419015-a51293ce91d8414f848e927dfa4babfb` |
| Contract（不帶情境） | 通過 | `20261006-125422343-9cbe1095f7bb43f29ea1f6f58c888087` |
| Contract FrontendMapping | 通過 | `20261006-125424217-66f8028d7bc244f4bc8602a08aa51af9` |
| Contract FrontendPreview | 通過 | `20261006-125429048-4ffa94d15791446b94f1f1b219cf4b92` |
| Public | .NET 4,626 個、前端 315 個、預覽 8 個案例通過，沒有失敗或略過 | `20261006-125436597-7c089d3d2dc14fbb90fae3d167e7bd52` |
| PrivateCase（Release、SQLite） | 執行一次，2 個案例通過，沒有略過；私人路徑未記錄，私人輸出已清除 | `20261006-125845399-eba14c7ebe644569aeeb75e2f6a68a51` |
| PrivateCase（Release、DuckDB） | 執行一次，2 個案例通過，沒有略過；私人路徑未記錄，私人輸出已清除 | `20261006-130005391-234210af2f434c19b372ddf81790b8eb` |
| Documentation | 寫回本節後通過，46 份文件、243 個連結、20 個錨點，沒有錯誤或警告；改動段落已完整重讀 | `20261006-130547598-333030325c9b48049728796f55c5f8ce` |
| Gui | 依裁定不重跑；這輪沒有改 Gui 情境與底稿輸出，最後一次是第二輪的 17 個情境通過 | 第二輪 `20261006-102310076-a712cba67f674dc7815d539cf64f071f` |
| SQL Server | 未啟動，相關修改只經編譯 | 無實機結果 |

PrivateCase 執行前，在本機找到唯一一份既有的私人案件清單，只在當次程序設定根目錄、清單與資料庫三項參數，執行後即清除；
私人資料與清單都沒有修改。既有清單沒有配對人工旗標，所以這兩次仍不涵蓋人工旗標欄，私人案件的該欄仍未驗證。
這兩次涵蓋 T6 改過的私人案件比對器。結束時確認沒有 `sqlservr.exe`，本機 SQL Server 服務為停止，Git 暫存區是空的。

#### 第三輪 Git 逐檔清單與提交訊息草稿

這份清單與草稿已作廢，只保留第三輪紀錄；現行版本見下方「第四輪 Git 逐檔清單與提交訊息草稿」。
以下 110 個路徑涵蓋 KCT 兩批實作、第二輪修正與第三輪修正，以儲存庫根目錄為起點逐一列出。
已逐項確認都是現存檔案，沒有目錄、萬用字元、`data/` 或 `artifacts/` 底下的路徑，也沒有其他範圍外的未提交變更；
第三輪的暫時實驗檔已刪除，不在清單內。這份清單與草稿都不是提交授權，暫存區目前是空的。

```text
docs/action-contract-manifest.md
docs/data-and-legacy.md
docs/development-status.md
docs/history/README.md
docs/idea-replacement-scope.md
docs/jet-frontend-description.md
docs/jet-guide.md
docs/kct-gl-workflow.md
docs/project-context.md
docs/README.md
docs/specs/2026-10-06-kct-gl-requirements-plan.md
docs/test-environment-notes.md
src/JET/JET/AppCompositionRoot.cs
src/JET/JET/AppCompositionRoot.Repositories.cs
src/JET/JET/Application/Handlers/ExportCalendarTemplatesHandler.cs
src/JET/JET/Application/Handlers/ExportReportHandlers.cs
src/JET/JET/Application/Handlers/ExportWorkpaperStreamHandler.cs
src/JET/JET/Application/Handlers/Import/ImportAccountMappingHandler.cs
src/JET/JET/Application/Handlers/Query/QueryAccountMappingDifferencePageHandler.cs
src/JET/JET/Application/Handlers/Query/QueryFilterVoucherHandlers.cs
src/JET/JET/Application/ProjectRepositories.cs
src/JET/JET/Application/Support/FilterConditionRenderer.cs
src/JET/JET/Application/Support/ProjectWorkFileWriter.cs
src/JET/JET/Application/Support/RuleLogicVersions.cs
src/JET/JET/AuditCore/IntakeMappingProgram.cs
src/JET/JET/AuditCore/WorkpaperProgram.cs
src/JET/JET/Domain/Abstractions/AccountMappingDifferenceRepository.cs
src/JET/JET/Domain/ActionExecutionPolicy.cs
src/JET/JET/Domain/Contracts/AccountMappingContracts.cs
src/JET/JET/Domain/Contracts/ICalendarTemplateSource.cs
src/JET/JET/Domain/Contracts/ImportContracts.cs
src/JET/JET/Domain/Contracts/WorkpaperReferenceContracts.cs
src/JET/JET/Domain/Rules/AccountMappingColumnMatcher.cs
src/JET/JET/Domain/Rules/QuarterEndWindows.cs
src/JET/JET/Infrastructure/Diagnostics/SupportRingBufferLoggerProvider.cs
src/JET/JET/Infrastructure/Export/CalendarTemplateSource.cs
src/JET/JET/Infrastructure/Export/WorkpaperWriter.Step2To41.cs
src/JET/JET/Infrastructure/FileIO/OpenXmlSaxTableReader.cs
src/JET/JET/Infrastructure/Persistence/AccountMappingDifferenceQuery.cs
src/JET/JET/Infrastructure/Persistence/AccountMappingEditorRepository.cs
src/JET/JET/Infrastructure/Persistence/AccountMappingPopulationQuery.cs
src/JET/JET/Infrastructure/Persistence/DataPreviewColumns.cs
src/JET/JET/Infrastructure/Persistence/DuckDb/DuckDbProjectDatabase.cs
src/JET/JET/Infrastructure/Persistence/Local/LocalAccountMappingDifferenceRepository.cs
src/JET/JET/Infrastructure/Persistence/Local/LocalAccountMappingExportRepository.cs
src/JET/JET/Infrastructure/Persistence/Local/LocalDataPreviewRepository.cs
src/JET/JET/Infrastructure/Persistence/Sqlite/SqliteProjectDatabase.cs
src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerAccountMappingDifferenceRepository.cs
src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerAccountMappingExportRepository.cs
src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerDataPreviewRepository.cs
src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerProjectDatabase.Schema.cs
src/JET/JET/Infrastructure/Sql/AccountClassificationMigration.cs
src/JET/JET/wwwroot/js/app.js
src/JET/JET/wwwroot/js/data-preview.js
src/JET/JET/wwwroot/js/filter-values.js
src/JET/JET/wwwroot/js/jet-api.js
src/JET/JET/wwwroot/js/steps/filter-step.js
src/JET/JET/wwwroot/js/steps/import-step.js
src/JET/JET/wwwroot/js/steps/mapping-step.js
src/JET/JET/wwwroot/js/steps/validate-step.js
src/JET/JET/wwwroot/js/ui-core.js
src/JET/tests/JET.Tests/Application/AccountMappingImportSummaryTests.cs
src/JET/tests/JET.Tests/Application/AccountMappingTemplateExportTests.cs
src/JET/tests/JET.Tests/Application/ActionSerializationTests.cs
src/JET/tests/JET.Tests/Application/AdvancedFilterAstContractTests.cs
src/JET/tests/JET.Tests/Application/ExportCalendarTemplatesHandlerTests.cs
src/JET/tests/JET.Tests/Application/FilterConditionRendererTests.cs
src/JET/tests/JET.Tests/Application/ImportAccountMappingHandlerTests.cs
src/JET/tests/JET.Tests/Application/InlineWorkbookProject.cs
src/JET/tests/JET.Tests/Application/QueryDataPreviewHandlerTests.cs
src/JET/tests/JET.Tests/Application/RuleLogicVersionsTests.cs
src/JET/tests/JET.Tests/Application/TypedFieldFilterContractTests.cs
src/JET/tests/JET.Tests/Application/WorkpaperStep41ProviderParityTests.cs
src/JET/tests/JET.Tests/Architecture/ActionContractRuleVersionTests.cs
src/JET/tests/JET.Tests/Architecture/DataPreviewColumnOwnershipTests.cs
src/JET/tests/JET.Tests/Architecture/FrontendUsabilityContractTests.cs
src/JET/tests/JET.Tests/Architecture/GuiScenarioCoverageTests.cs
src/JET/tests/JET.Tests/Architecture/HandlerRepositoryScopeTests.cs
src/JET/tests/JET.Tests/Architecture/MappingProjectionPolicyFrontendTests.cs
src/JET/tests/JET.Tests/Architecture/QuarterEndWindowsFrontendParityTests.cs
src/JET/tests/JET.Tests/Architecture/ReportExportOwnershipTests.cs
src/JET/tests/JET.Tests/Architecture/ScreenWordingReviewFrontendTests.cs
src/JET/tests/JET.Tests/Architecture/SupportedActionsParityTests.cs
src/JET/tests/JET.Tests/Architecture/TerminologyUnificationFrontendTests.cs
src/JET/tests/JET.Tests/AuditCore/IntakeMappingProgramTests.cs
src/JET/tests/JET.Tests/AuditCore/WorkpaperProgramTests.cs
src/JET/tests/JET.Tests/Domain/AccountMappingColumnResolverTests.cs
src/JET/tests/JET.Tests/Domain/QuarterEndWindowsTests.cs
src/JET/tests/JET.Tests/Infrastructure/BinaryExcelTableReaderTests.cs
src/JET/tests/JET.Tests/Infrastructure/FilterCompletenessTests.cs
src/JET/tests/JET.Tests/Infrastructure/KctFilterPredicateTests.cs
src/JET/tests/JET.Tests/Infrastructure/LegacyWorkbookContentComparison.cs
src/JET/tests/JET.Tests/Infrastructure/OpenXmlSaxTableReaderTests.cs
src/JET/tests/JET.Tests/Infrastructure/PrivateCaseReportComparator.cs
src/JET/tests/JET.Tests/Infrastructure/PrivateCaseReportComparatorTests.cs
src/JET/tests/JET.Tests/Infrastructure/PrivateCaseReportDifferencePolicy.cs
src/JET/tests/JET.Tests/Infrastructure/SqlServerDataPreviewRepositoryTests.cs
src/JET/tests/JET.Tests/Infrastructure/WorkpaperStep41LegacyBaselineBenchmarkTests.cs
src/JET/tests/JET.Tests/JET.Tests.csproj
tools/harness/documentation-check.json
tools/harness/gui-driver/GuiFilterWorkflowScenarios.cs
tools/harness/gui-driver/GuiSideMonthWorkflow.cs
tools/harness/gui-driver/GuiWorkpaperInspection.cs
tools/harness/JetHarness.psm1
tools/harness/lanes.json
tools/README.md
tools/tests/fixtures/quarter-end-windows.json
tools/tests/frontend-mapping.test.cjs
tools/tests/frontend-workflow.test.cjs
tools/tests/verify-contract.tests.ps1
```

提交訊息草稿如下，不加署名。依使用者下方 C1 的回覆改成精簡版，只寫主要變更與必要原因：

```text
補齊 KCT 條件與科目配對

- 依 KCT 需求調整條件 A、B 與 A 到 E 的預設動機；查核期末不是季底的案件，A 的命中可能變多。
- 底稿 Step 4-1 的人工旗標改為「人工」「自動」文字。
- 科目配對檔匯入後提醒差異科目；範本與科目清單不列空白科目編號。
- 舊案件的已存情境全部符合新規則才整批改版；假日與補班日檔固定略過第一列。
- SQL Server 只經編譯，未連線驗證。
```

#### 使用者對第三輪結果的回覆（2026-10-06）

使用者在本機回填頁回覆，逐字如下：

```text
KCT 第三輪結果回填（2026-10-06 清單）

## 要你決定的事
- X1 驗證時多修了一個不在清單裡的 DuckDB 問題：接受這次一併修正（建議）
- L5 測試專案直接引用 tools/ 底下的程式，仍待你確認：接受這個慣例（建議）
- J1 L1：舊案科目的分類無法判斷時，我選擇不記：維持：無法判斷就不記（建議）。備註：如之前說過的，現在還不是正式版本，不用顧慮到舊案件的結構，只要還沒納入正式坂本的發布我都可以直接刪除並重新創建案件
- J2 L4：第五步加註來源欄只用前端測試確認：不用另看（建議）
- C1 第三輪 Git 逐檔清單與提交訊息草稿：要修改（備註說明）。備註：Commit / push 訊息請保持精簡，只記錄主要變更與必要原因，避免詳細描述實作過程、驗證細節與逐項修改內容。保留足以追溯變更目的的資訊即可。

## 修正結果
- T1 欄名提醒的訊息等級：接受
- T4 科目配對範本改用共用的科目查詢：接受
- L1 舊案升級改以已存配對判斷分類有沒有填：接受
- L3 人工旗標的 1、0 轉換收成一處：接受
- T5 契約版本測試改成對程式常數：接受
- T3 五個 SecondReview 測試檔依行為改名，文件措辭檢查搬家：接受
- L6 契約第 491 行改成「KCT 條件 A」：接受
- T6 私人案件比對器：刪未用參數、補合成案例、寫列序取捨：接受
- L2 補日曆方向鍵測試：接受
- L4 第五步欄位選單加註來源欄：接受
- T7 計畫與現況文件整理（含 L7）：接受

## 仍有的限制
- R1 私人案件仍沒有涵蓋人工旗標欄：知悉，維持合成測試補證。備註：關於私人案件的測試請納入後續未完成的工作事項，我會在當前計畫全部處理後，接續處理
- R2 SQL Server 本輪多了四項只經編譯的修改：知悉
- R3 Gui 依裁定沒有重跑：維持不跑
- R4 11 項「已知但延後的事項」沒有變化：知悉

## 下一步
- N1 第三輪要不要再做獨立複核：請 Claude 另開 session 複核。備註：我會在當前 session 基於你回覆的指示繼續
```

依回覆處理如下。L5 的做法已接受，`JET.Tests.csproj` 維持直接引用 Gui 的底稿讀回程式。提交訊息改成上面的精簡版；
Git 逐檔清單不變，仍是 110 個檔案。R1 的私人案件人工旗標驗證已列入開發現況的「未完成項目」，等本計畫處理完再接續。
J1 備註說明目前不是正式版本，舊案件可以直接刪除重建；這句只記在這裡，現行文件尚未寫入這項原則。
（2026-10-07 補記：使用者同意後已寫入 `project-context.md`。）
下一步由使用者另開 Claude session，唯讀複核第三輪的修正與本節紀錄；複核結果與裁定寫回本節。暫存、提交與推送仍待使用者另外授權。
寫回這段之後 Documentation 通過，收據 `20261006-133444255-1915a0a180634eb2ba85e7393d20c67c`。

#### 第三輪的唯讀複核（2026-10-06）

使用者在第三輪結果回填頁的 N1 選「請 Claude 另開 session 複核」，備註寫「我會在當前 session 基於你回覆的指示繼續」，
隨後在同一個 session 貼上主線上一則回覆提供的複核指示，逐字如下：

```text
請唯讀複核 je-tool 的 KCT 計畫第三輪修正。先執行 pwsh -NoProfile -File tools/verify.ps1 -Command Context，再讀 docs/specs/2026-10-06-kct-gl-requirements-plan.md 第八節「第二輪修正的複核與第三輪（2026-10-06）」，包含「第三輪執行紀錄」「第三輪 Git 逐檔清單與提交訊息草稿」與「使用者對第三輪結果的回覆」。

複核範圍：
1. 第三輪 13 項（T1、T3 到 T7、L1 到 L4、L6、L7）與驗證時另外修的 DuckDB 問題，是否照表中「做法」修到根本原因，沒有偏離審計業務邏輯或傷到審計員操作。
2. 每項的首次失敗與收據是否存在、與計畫文字一致；改既有測試的兩處（T6、L4）原因是否成立，有沒有放寬斷言。
3. L1 的新判斷邏輯（以已存配對找一致的來源欄）與 DuckDB 的子查詢改寫是否有遺漏的邊界。
4. 110 個 Git 路徑是否與工作樹完全一致，沒有 data/、artifacts/ 或私人路徑；提交訊息是否符合使用者要求的精簡寫法。
5. 文件改動是否好讀、事實是否有出處。

只讀工作樹差異、計畫與 artifacts/harness/runs/ 的收據，不讀私人資料目錄，不執行 PrivateCase、Gui 或 SQL Server，不改產品程式與測試。發現的問題整理成本機回填頁（artifacts/review/，同之前的樣式）讓我裁定，並把複核結論寫回計畫第八節。不要暫存、提交或推送。
```

複核在同一個 Claude Code session 進行。主線寫過第三輪的修正，所以複核交給五個沒有參與修正的唯讀子代理，各看一塊：
後端的 T4 與 L3；前端與測試的 T1、T3、T5、L6、T6、L2、L4；收據與首次失敗；L1 與 DuckDB 改寫的邊界；文件、T7、L7 與提交訊息。
每個子代理的發現，再交給另一個子代理試著推翻。子代理只讀工作樹差異、本計畫與收據，沒有執行驗證命令，也沒有讀私人資料目錄；
PrivateCase 只看收據的狀態與計數欄位。主線另外親自做了四件事：逐條比對 Git 清單與工作樹；確認科目差異清單的第一頁沒有額外的篩選條件，
改用子查詢後總數和原寫法相同；確認第五步那三種選單仍只顯示欄位名稱；當時認為 Gui 都以欄位代碼選選項，不受文字影響，
第四輪實跑發現舊表範例 2 仍用舊顯示文字查找，已在第四輪補正；
確認上次提交的契約確實寫過「目前篩選計算版本為 v17」這種簡寫。

##### 複核結論

13 項與 DuckDB 修正都照表中「做法」修到根本原因，沒有偏離審計邏輯，也沒有發現會讓篩選結果、底稿或資料出錯的問題。
改既有測試的兩處原因成立，沒有放寬斷言。Public 收據記錄的斷言變化顯示，第三輪比第二輪只多刪一個斷言，就是 L4 改寫的那一個；
沒有新增略過，也沒有刪除案例。第三輪紀錄引用的 49 份收據都存在，命令、篩選、狀態與失敗原因都和文字一致，首次失敗都早於修正後的通過。
Git 清單的 110 個路徑和工作樹逐條相同，沒有 `data/`、`artifacts/` 或私人路徑，暫存區是空的；本計畫沒有私人路徑或機敏內容。
L1 的判斷規則和匯入時一致；空批次、找不到原始列、欄名重複、同一科目多列、多個批次與只升級一次，都檢查過。

找到的問題共 28 項，依處理方式分成下面四組。另有一項疑慮被推翻：
收據複核員原本認為私人案件「各 2 個案例、沒有略過」無法從收據確認，其實收據本身就記了案例數，和文字一致。

要使用者決定的程式、測試與提交訊息：

| 編號 | 發現 | 建議 |
|:---|:---|:---|
| B2 | 第五步「新增篩選條件」「加入子條件」，以及舊表條件 O、R 與範例 2 的欄位選單，仍只顯示欄位名稱，兩個同名的傳票建立日分不出來。前端說明卻寫第五步選欄位時分得出來。 | 欄位清單統一帶好顯示文字，五種選單共用，前端測試一次檢查全部選單。 |
| B5 | 後端拒收某一筆訊息時，這筆會留在佇列最前面重試，之後的訊息紀錄都存不進去。T1 已改正唯一寫錯的等級；這個弱點在第三輪之前就存在。 | 佇列遇到格式錯誤的拒收時，記到支援日誌後跳過；寫訊息的入口只接受 info、warn，並略過空白文字。 |
| B1 | 契約版本測試只認完整代號，抓不到「目前篩選計算版本為 v17」這種簡寫；上次提交的契約正是這種寫法。 | 「目前」「現行」句子裡單獨出現的 vN 也要抓，一律要求寫完整代號。 |
| B3 | 日曆方向鍵案例只檢查目前日期在 Tab 順序裡，沒有檢查其他日期被排除，比第三輪紀錄說的保證弱。 | 補一行斷言：只有一個日期在 Tab 順序裡，而且就是目前日期。 |
| E8 | 精簡版提交訊息漏了假日與補班日範本入口、傳票建立日快捷、值清單筆數、日曆年月選單，以及資料預覽的人工或自動欄。最後一行算驗證細節還是風險提醒，待使用者決定。 | 回填頁附改寫後的草稿。 |

文件與紀錄要修，程式行為正確，只是說法不一致或容易誤會：

| 編號 | 發現 | 建議 |
|:---|:---|:---|
| A1 | `jet-guide.md` 第 255 行說範本與科目清單是同一批科目；但科目清單另外列出配對檔多出的科目，範本沒有。第三輪紀錄的 T4 段與 `AccountMappingDifferenceQuery.cs` 的註解有同樣說法。 | 改成範本與「配對檔未列的科目」用同一批可配對科目，科目清單另含配對檔多出的科目。 |
| A2 | `WorkpaperReferenceContracts.cs` 第 17、18、49 行仍寫範本取完整性比對的科目、「尚未配對的母體」，沒提到排除空白科目編號。 | 改成可配對科目母體，排除空白科目編號。 |
| E2 | 開發現況「最後獨立複審留下的低嚴重度事項」仍把「有空白科目時產生範本可能失敗」列為待決定；T4 已修好。 | 改成已處理，並連到本計畫。 |
| E3 | 開發現況「KCT 後續條件」列寫「上方現行 KCT 計畫」，和 L7 要消除的是同一類相對說法。 | 換成直接連結。 |
| C2 | DuckDB 段落沒列出三次單獨執行與十次對照實驗的收據。「約四列」「四十列以上與二十萬列」「獨立 DuckDB 檔可重現」查不到收據。第一次失敗其實是單次查詢，依序讀取在極小資料上也會偶發；測試註解卻把兩份清單同時展開寫得像觸發條件。 | 補收據表；查不到出處的說法改寫或標成待確認；改測試註解。 |
| D2 | 「讀同一份資料快照」在 SQL Server 預設設定下不成立；第一頁總數也會多算一次科目母體。 | 只對 SQLite、DuckDB 這樣說；SQL Server 的情況列進只經編譯的清單。 |
| D7 | `jet-guide.md` 資料庫第 10 版的升級段落仍寫「分批回填」，那是已移除的分頁做法。 | 指向前面的新規則，拿掉「分批」。 |
| E5 | `jet-guide.md` 第 68、267 行是從「這次改了什麼」的角度寫，沒有交代前提與結果。 | 改寫成現況。 |
| E6 | 交付說明的「開發端的自動比對」可能被誤會成 JET 畫面上的檢查。 | 主詞改成開發時比對私人案件的工具。 |
| C3 | 第三輪紀錄說方向鍵案例第一次就通過，但那次執行因 L4 的新案例失敗而整體失敗。 | 寫明那次只有 L4 的案例失敗。 |
| B6 | T6 改測試的原因寫「比對政策從來沒有讀這個參數」；第一批時有讀，第二輪修 D3 之後才不讀。 | 拿掉「從來」，寫明是 D3 之後。 |

記錄即可：

| 編號 | 發現 | 建議 |
|:---|:---|:---|
| A3 | SQL Server 的範本查詢沒有測試防止改回舊寫法；跨資料庫範本比對用的示範資料沒有空白科目編號。 | 記進開發現況的 SQL Server 清單。 |
| A4 | 預覽人工旗標的架構測試只比對程式文字，改在 SQL 裡轉換就能繞過。 | 記錄即可。 |
| B4 | 交付說明的三句必含句綁著這一版，下次改寫交付說明時，文件檢查會失敗並指出是哪條規則。 | 下次改寫時一起更新。 |
| B7 | DuckDB 並行讀取的測試放在匯入摘要測試檔，類別說明沒提到。 | 下次動到這個檔案時補說明。 |
| B8 | 兩個同名欄位的條件，讀回文字看起來一樣；L4 已明寫這個取捨並獲接受。 | 記錄即可。 |
| D1 | 舊案升級時，如果整批都是 Others 且分類名稱改過兩次，無關的空白欄會成為唯一候選，分類留白筆數多算；不影響分類與篩選。 | 記錄即可，正式發布前的舊案件可以刪除重建。 |
| D5 | 第三步完整性科目表的查詢形狀和出錯的查詢相近，目前沒有證據顯示會出同樣的錯。 | 技術債補一句：升級 DuckDB 時一併重測。 |
| D6 | DuckDB 新測試失敗時，訊息只隨機帶一筆，多半是連帶錯誤。 | 建議順手改成優先顯示第一個真正的錯誤。 |

寫回本節時已處理：

| 編號 | 發現 | 處理 |
|:---|:---|:---|
| E1 | `docs/README.md` 仍寫只剩一項做法待使用者確認。 | 改成目前狀態。 |
| E7 | 本計畫開頭的下一步，指向已經寫完的小節。 | 改成指向本小節。 |
| C1 | 寫回使用者回覆之後的 Documentation 沒有記錄。 | 補在上一小節末尾；本節寫回後的檢查記在下方。 |
| E4 | 開發現況的狀態說明多了沒說明的代號 L5。 | 拿掉代號，只留白話說明。 |

問題與選項整理在本機回填頁，不進 Git；使用者的裁定回覆逐字記在本節下方。修正前不暫存、提交或推送。
寫回本節後 Documentation 通過，沒有錯誤或警告，收據 `20261006-140657226-e89c5c2198e14fe5a9e17e397d4686c3`。

##### 使用者對複核的裁定（2026-10-06）

使用者在本機回填頁回覆，逐字如下：

```text
KCT 第三輪複核裁定（2026-10-06 清單）

## 要你決定的事
- B2 第五步還有三種欄位選單沒有加註來源欄：這輪補上（建議）
- B5 訊息紀錄遇到一筆存不進去的訊息，後面的仍會全部卡住：這輪一起修（建議）
- B1 契約版本測試抓不到「v17」這種簡寫：這輪補強（建議）
- B3 方向鍵測試沒有確認其他日期都不在 Tab 順序裡：這輪補上（建議）
- E8 提交訊息漏了幾項看得到的新功能：用新草稿，不留 SQL Server 那行（建議）

## 文件與紀錄的修正
- A1 指南說「範本與科目清單是同一批科目」，不精確：照建議改
- A2 範本的程式說明只改了一半：照建議改
- E2 開發現況仍把已修好的範本問題列為待決定：照建議改
- E3 開發現況還有「上方現行 KCT 計畫」這種相對說法：照建議改
- C2 DuckDB 問題的說明比留下的證據肯定：照建議改
- D2 SQL Server 上「讀同一份資料」的說法不成立：照建議改
- D7 指南的資料庫升級段落仍寫「分批回填」：照建議改
- E5 指南兩句匯入說明是從「這次改了什麼」的角度寫的：照建議改
- E6 交付說明的「開發端的自動比對」容易被誤會成 JET 的檢查：照建議改
- C3 計畫說方向鍵案例「第一次就通過」，但那次執行整體顯示失敗：照建議改
- B6 改測試的原因寫「比對政策從來沒有讀這個參數」：照建議改

## 記錄即可的事
- A3 SQL Server 的範本查詢沒有測試防止改回舊寫法：記進開發現況（建議）
- A4 預覽人工旗標的架構測試只比對程式文字：記錄即可（建議）
- B4 交付說明的三句必含句綁著這一版的內容：記錄即可（建議）
- B7 DuckDB 並行讀取的測試放在「匯入摘要」測試檔：記錄即可（建議）
- B8 條件讀回文字只寫欄位名稱：記錄即可（建議）
- D1 L1 的少見邊界：整批都是 Others 時可能多算分類留白：記錄即可（建議）
- D5 第三步完整性科目表的查詢形狀相近：補進技術債說明（建議）
- D6 DuckDB 新測試失敗時，錯誤訊息只隨機帶一筆：這輪順手改（建議）

## 寫回時已處理
- E1 docs/README.md 仍寫「只剩一項做法待使用者確認」：知悉
- E7 計畫開頭的「下一步」指向已經寫完的小節：知悉
- C1 計畫沒列最後一次文件檢查：知悉
- E4 開發現況多一個沒說明的代號 L5：知悉

## 下一步
- N1 修正由誰來做：交給 Codex 修
- N2 修完要跑哪些驗證：另外加跑 Gui
```

##### 第四輪的範圍與完成條件

依上面的裁定，第四輪由 Codex 處理，範圍是：

1. 程式與測試：B2、B5、B1、B3，以及 D6。每項先寫會失敗的測試並保存第一次失敗；B1、B3、D6 是補強既有新測試，
   要先暫時改壞程式或文件確認新斷言抓得到，再逐位元還原，和第三輪的做法相同。
2. 提交訊息：改用回填頁的新草稿，不留 SQL Server 那一行。新草稿逐字如下：

   ```text
   補齊 KCT 條件、科目配對與操作入口

   - 依 KCT 需求調整條件 A、B 與 A 到 E 的預設動機；查核期末不是季底的案件，A 的命中可能變多。
   - 新增假日與補班日範本入口、傳票建立日快捷、值清單筆數與日曆年月選單；假日與補班日檔固定略過第一列。
   - 底稿 Step 4-1 的人工旗標改為「人工」「自動」文字，資料預覽也多一欄人工或自動。
   - 科目配對檔匯入後提醒差異科目；範本與科目清單不列空白科目編號。
   - 舊案件的已存情境全部符合新規則才整批改版。
   ```

3. 文件與紀錄：A1、A2、E2、E3、C2、D2、D7、E5、E6、C3、B6，照上方「複核結論」表中的建議改。
   C2 查不到出處的資料量說法，改寫或標成待確認，不補推測。
4. 記錄事項：A3 記進開發現況的 SQL Server 只經編譯清單；D5 補進開發現況的 DuckDB 技術債說明；
   A4、B4、B7、B8、D1 已記在本節，不另改。
5. Git 逐檔清單：第四輪若新增或刪除檔案，重新定稿並把第三輪的清單標成作廢；檔案集合不變時註明仍是同一份。

完成條件：Build、Contract（不帶情境、FrontendMapping、FrontendPreview）、Public、Documentation 通過，並依 N2 加跑 Gui。
Gui 每個情境都會在使用者桌面開 JET 視窗並搶前景，執行前先告訴使用者。PrivateCase 不重跑：這輪不動私人案件比對器與底稿輸出。
SQL Server 不啟動。結果、首次失敗與收據寫回本節下方；不暫存、提交或推送。

##### 第四輪執行紀錄（2026-10-06 開始）

Codex 已執行 Context 並讀取本節裁定，保留接手時的工作樹，不讀私人資料。
本輪不重跑 PrivateCase，也不啟動 SQL Server；不暫存、提交或推送。

測試調整原因：B2 統一五種欄位選單的文字來源，舊式 typed 選單也會帶「攸關資料元素欄位」及來源欄。
原有兩條選項文字斷言只調整顯示預期，保留欄位代碼、已選狀態與來源欄的檢查，另補其他入口。
B1 保留完整版本與沿革案例，增加簡寫反例；B3 保留按鍵及選取斷言，增加唯一 Tab 日期。
D6 不改 2,000 次讀取的筆數與科目預期，只補錯誤挑選的固定反例。

本輪於台灣時間 2026-10-06 接手，跨日於 2026-10-07 繼續。下列收據名稱使用 UTC 日期。

| 項目 | 實際修改與確認範圍 |
|:---|:---|
| B2 | `FilterValues.fields` 統一產生選項文字，五種選單共用；來源欄文字經跳脫，選項值、欄位型別、保存代碼與讀回文字不變。前端案例逐一檢查一般條件、typed、新增條件、子條件與舊表選單，含 O、R 及範例 2。 |
| B5 | 訊息入口只接受 info、warn，未指定時用 info，略過空白及非文字。佇列僅對 invalid_payload 跳過該筆；dispatcher 已在回覆前寫入去識別支援日誌，不另造訊息。其他失敗保留重試，仍按原順序保存；切案不寫入舊案訊息。新增前端行為測試及兩個後端日誌案例。 |
| B1 | 契約檢查也辨識「目前」「現行」句子中的 v17、v18，含有無反引號、空格及換行的版本；簡寫不能等於完整代號。版本沿革及較長識別字不誤判。 |
| B3 | 每次方向鍵、Home、End 移動後，都斷言僅目前日期的 tabindex 為 0；原有日期位移與不選取斷言保留。 |
| D6 | 失敗依收到順序排隊，優先顯示第一筆不是資料庫失效連帶錯誤的訊息；若全是連帶錯誤，保留第一筆。固定反例檢查混合、全連帶及空清單；原有 2,000 次讀取的總數與科目答案不變。 |
| A1、A2、E2、E3 | 指南、程式註解與第三輪 T4 紀錄分清可配對科目母體與編輯清單；開發現況標明空白科目範本已修正，KCT 條件列改用直接連結。 |
| C2、D2、D7、E5、E6、C3、B6 | DuckDB 段補三次單獨執行與十次實驗收據，未確認的說法不再作結論；快照保證只限本機資料庫，說明第一頁另算一次母體。指南改寫匯入前提及升級規則，交付說明分清私人案件比對工具與產品畫面；第三輪 L2 整次失敗與 T6 在 D3 後才不讀參數的時序已更正。 |
| A3、D5 | 開發現況補 SQL Server 範本測試缺口、第一頁總數的隔離限制，以及升級 DuckDB 時一併重測第三步完整性科目表。 |
| A4、B4、B7、B8、D1 | 沿用上方記錄與裁定。此次已修改匯入摘要測試檔，類別說明順帶補明 DuckDB 重疊讀取的範圍，未改產品行為。 |

首次失敗與還原紀錄：

| 項目 | 結果與原因 | 收據 |
|:---|:---|:---|
| B2、B5 初次執行 | B5 的入口與佇列兩例如預期失敗；B2 首次使用的簡化 DOM 不支援子元素選取，尚未走到標籤斷言。修正測試取法，保留此收據。 | `20261006-160047400-379d5c2f61ab41f28645b543766c3ed9` |
| B2 有效首次失敗、B5 再確認 | B2 讀到「傳票建立日」而缺來源欄；B5 接受無效訊息且佇列停在拒收那筆。 | `20261006-160130606-18625dff83a540e489c35b93131c5cb2` |
| B2、B5 修正後 | FrontendMapping 318 個案例通過。 | `20261006-160207543-aec921f8e6c6436c9d55479386cff3a3` |
| B3 刻意破壞 | 把所有日期的 tabindex 改為 0，新斷言失敗；finally 以原位元組還原並核對 SHA-256。 | `20261006-160225588-ed7d48e6cf72429cb45270ed0dbdaf86` |
| B1 首次執行受阻 | 沙箱拒絕 NuGet 連線，尚未執行測試；文件已還原。不是反例成功證據。 | `20261006-160314770-4aa696828f854c99a8212e155adf22e1` |
| B1 有效反例 | 文件加回「目前篩選計算版本為 v17」簡寫後，現行版本斷言失敗；finally 逐位元還原並核對 SHA-256。 | `20261006-160655804-6dfa3c2c6efb49e68b7cf53befb0af42` |
| D6 首次執行未到測試 | 新增支援日誌案例缺少測試命名空間，編譯失敗；先補引用，刻意破壞仍已還原。 | `20261006-160822929-e72585f4c19a4ba7b1ff820d60a1d960` |
| D6 有效反例 | 暫時改成不排除失效連帶錯誤，新斷言如預期讀到連帶錯誤而失敗；finally 逐位元還原並核對 SHA-256。 | `20261006-160857376-72525c426d494ffc8774447c90a7233c` |
| 還原後補測 | AccountMappingImportSummaryTests 所選案例通過，架構檢查要求 typed 保留 rdeFieldOptions 入口而失敗。保留既有斷言，改讓該入口使用共用欄位清單。 | `20261006-160918429-f3e767f30b0d4fe3bcecf46ea2e50089` |
| 日誌與架構補測 | SupportRingBufferLoggerProviderTests 及架構檢查通過，包含還原後 B1。 | `20261006-161040152-04591efa6dc9466998f50ab557409237` |
| 最後前端補測 | FrontendMapping 318 個案例通過，包含還原後 B3。 | `20261006-161149001-88be165cc365459a8c55c3c031bb6b0d` |

Gui 修正前的指定驗證結果，最後有效結果見下表之後的重跑紀錄：

| 檢查 | 結果 | 收據 |
|:---|:---|:---|
| Build（Debug） | 通過 | `20261006-161402077-3037cdc781df4f9fa282f12f2e7b50db` |
| Contract（不帶情境） | 通過 | `20261006-161412110-414a780b9fde490499415351044681ed` |
| Contract FrontendMapping | 318 個案例通過 | `20261006-161415702-9768d51f69af474698c485c824dc981e` |
| Contract FrontendPreview | 8 個案例通過 | `20261006-161422490-91f5fff21936475eaeb6bda40f519e72` |
| Public | .NET 4,632 個、前端 318 個、預覽 8 個案例通過，沒有失敗或略過 | `20261006-161436515-f11d934dba3545049cfaf80a342456d1` |
| Documentation | 46 份文件、245 個連結、20 個錨點通過，沒有錯誤或警告；最終回填後再重跑 | `20261006-161823571-4815221f19ad49ad8628d11f7bad2b2c` |
| Gui 首次完整執行 | 執行前已告知。legacy-form-workflow 回報 cdp_evaluation_failed；舊表範例 2 以 GUI部門 舊文字找選項，B2 加註來源欄後找不到。保留失敗，未當成產品篩選錯誤。 | `20261006-161845299-e9aa3d6e58054a4bbc951d0129bc4fef` |

Gui 測試修改原因（先記錄再修改）：`GuiLegacyFormWorkflow.cs` 第 88 行以完整顯示文字找「GUI部門」，
這與 B2 要加註來源欄的新顯示契約衝突，也推翻第三輪「Gui 都不依賴選單文字」的說法。
改從合成案件已確認配對的來源欄「GUI部門」取得欄位代碼，再用鍵盤選入；另核對選項完整新文字與已選代碼。
五個範例的固定結果 0、679、0、30、0，以及保存、重開、取消、匯出的既有斷言全部保留，不放寬。
修正後已重新執行完整檢查，下方逐檔清單同步加入這個修改過的既有檔案。最後有效結果如下：

| 檢查 | 結果 | 收據 |
|:---|:---|:---|
| Build（Debug） | 通過 | `20261006-162449925-230063a0a1634a62b9cb001b754b456a` |
| Contract（不帶情境） | 通過 | `20261006-162521980-9c0bdfbe956041519af2e0bbb37691fe` |
| Contract FrontendMapping | 318 個案例通過 | `20261006-162525657-36de4426a7844b3a97eb51b7819a878c` |
| Contract FrontendPreview | 8 個案例通過 | `20261006-162532531-685b9ca5d36645b0b92d62369180332b` |
| Public | .NET 4,632 個、前端 318 個、預覽 8 個案例通過，沒有失敗或略過 | `20261006-162546197-86c33f7a304e40d6a437827b1406c053` |
| Documentation | 通過，沒有錯誤或警告 | `20261006-162924611-388198a64ec147aaba56d61fd983d1f7` |
| Gui（AgentGuiTest） | 再次告知桌面影響後完整重跑，17 個情境全數通過，包含修正後的舊表範例；測試程序已退出 | `20261006-162942509-19a014bf6a154df4a60e969c7b0d34c2` |

本節回填後的最後文件檢查另保存在 `artifacts/harness/kct-round4-documentation-final/`。
該目錄下唯一執行目錄的 `receipt.json` 記錄最後結果，`documentation-report.json` 記錄文件與連結檢查。
此處先登錄收據位置；2026-10-07 複核時讀過該收據，結果是通過。
三份刻意破壞過的檔案已再次與本機備份逐位元組比較，B1、B3、D6 全部相同。
本輪完成後停止，待使用者確認，不開始其他延後工作。

Public 相對 HEAD 的斷言刪除數仍為 16，與第三輪相同，沒有再減少；沒有刪除案例或新增略過。
這項統計只涵蓋已追蹤而且有修改的 C# 測試檔；前端測試、Gui 驅動程式與新建檔案不在其中，B2 對它們的調整原因另寫在本節開頭。
本輪的 PrivateCase、Provider、獨立 Excel 與 Package 檢查未執行，沒有啟動 SQL Server。
已確認本機 MSSQLSERVER、MSSQLLaunchpad、SQLSERVERAGENT 都停止，沒有 sqlservr 程序。
文件、畫面選項文字與修改過的程式註解已依 jet-readable-docs 重讀；沒有另用瀏覽器或實體滑鼠做人工驗收。
還原比對與逐檔核對留在本機 `artifacts/review/kct-round4/`，不進 Git。

本輪逐項核對開發現況「已知但延後的事項」，沒有擅自擴大實作範圍：

| 事項 | 本輪狀態與重啟條件 |
|:---|:---|
| 完整性差異的審計指引工作表 | 未改；等使用者與 DPP 討論出新內容。 |
| SQL Server 實機驗證 | 未執行；補記 A3、D2，等使用者明示且測試資料庫與權限就緒。 |
| SQL Server 企業多人環境 | 未執行；等公司提供伺服器、帳號、DBA 支援及授權政策。 |
| 預篩選的後續收斂 | 未展開；等使用者要求另立計畫。 |
| 操作紀錄的查詢介面、匯出與保留政策 | 未新增；B5 只修既有訊息保存。仍等企業線重啟或使用者明示。 |
| KCT 後續條件 | A 到 E 沿用已核准實作；F 到 J 與正式全稱仍等 KCT 提供來源。 |
| 匯入前的資料前置處理 | 仍留在 JET 之外；等使用者決定納入。 |
| SQLite 單一語句取消 | 未修；交付後依既有裁定先評估，再做長語句取消測試。 |
| 驗證框架瘦身與收據保留期限 | 未處理；等使用者決定另立計畫。 |
| 最後獨立複審的低嚴重度事項 | 空白科目範本已依第三輪 T4 處理，其餘維持開發現況清單；等測試環境驗收後挑選。 |
| 本機單人案件上的多人機制 | 未改；等實際多人開同一本機案件，或使用者遇到衝突。 |

##### 第四輪 Git 逐檔清單與提交訊息草稿

2026-10-07：本小節的清單與草稿已由下方「提交前的最後定稿」取代。

本輪檔案集合共 113 個路徑。相較第三輪新增 3 個變更路徑：`src/JET/JET/wwwroot/js/state.js`、`src/JET/tests/JET.Tests/Infrastructure/SupportRingBufferLoggerProviderTests.cs`、`tools/harness/gui-driver/GuiLegacyFormWorkflow.cs`；沒有刪除路徑。
這些是原本已追蹤、第四輪才修改的檔案，不是新建產品檔。第三輪清單與草稿已作廢。
下列清單逐一對照目前工作樹，沒有 data/、artifacts/、私人路徑或目錄；不代表暫存或提交授權。

```text
docs/README.md
docs/action-contract-manifest.md
docs/data-and-legacy.md
docs/development-status.md
docs/history/README.md
docs/idea-replacement-scope.md
docs/jet-frontend-description.md
docs/jet-guide.md
docs/kct-gl-workflow.md
docs/project-context.md
docs/specs/2026-10-06-kct-gl-requirements-plan.md
docs/test-environment-notes.md
src/JET/JET/AppCompositionRoot.Repositories.cs
src/JET/JET/AppCompositionRoot.cs
src/JET/JET/Application/Handlers/ExportCalendarTemplatesHandler.cs
src/JET/JET/Application/Handlers/ExportReportHandlers.cs
src/JET/JET/Application/Handlers/ExportWorkpaperStreamHandler.cs
src/JET/JET/Application/Handlers/Import/ImportAccountMappingHandler.cs
src/JET/JET/Application/Handlers/Query/QueryAccountMappingDifferencePageHandler.cs
src/JET/JET/Application/Handlers/Query/QueryFilterVoucherHandlers.cs
src/JET/JET/Application/ProjectRepositories.cs
src/JET/JET/Application/Support/FilterConditionRenderer.cs
src/JET/JET/Application/Support/ProjectWorkFileWriter.cs
src/JET/JET/Application/Support/RuleLogicVersions.cs
src/JET/JET/AuditCore/IntakeMappingProgram.cs
src/JET/JET/AuditCore/WorkpaperProgram.cs
src/JET/JET/Domain/Abstractions/AccountMappingDifferenceRepository.cs
src/JET/JET/Domain/ActionExecutionPolicy.cs
src/JET/JET/Domain/Contracts/AccountMappingContracts.cs
src/JET/JET/Domain/Contracts/ICalendarTemplateSource.cs
src/JET/JET/Domain/Contracts/ImportContracts.cs
src/JET/JET/Domain/Contracts/WorkpaperReferenceContracts.cs
src/JET/JET/Domain/Rules/AccountMappingColumnMatcher.cs
src/JET/JET/Domain/Rules/QuarterEndWindows.cs
src/JET/JET/Infrastructure/Diagnostics/SupportRingBufferLoggerProvider.cs
src/JET/JET/Infrastructure/Export/CalendarTemplateSource.cs
src/JET/JET/Infrastructure/Export/WorkpaperWriter.Step2To41.cs
src/JET/JET/Infrastructure/FileIO/OpenXmlSaxTableReader.cs
src/JET/JET/Infrastructure/Persistence/AccountMappingDifferenceQuery.cs
src/JET/JET/Infrastructure/Persistence/AccountMappingEditorRepository.cs
src/JET/JET/Infrastructure/Persistence/AccountMappingPopulationQuery.cs
src/JET/JET/Infrastructure/Persistence/DataPreviewColumns.cs
src/JET/JET/Infrastructure/Persistence/DuckDb/DuckDbProjectDatabase.cs
src/JET/JET/Infrastructure/Persistence/Local/LocalAccountMappingDifferenceRepository.cs
src/JET/JET/Infrastructure/Persistence/Local/LocalAccountMappingExportRepository.cs
src/JET/JET/Infrastructure/Persistence/Local/LocalDataPreviewRepository.cs
src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerAccountMappingDifferenceRepository.cs
src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerAccountMappingExportRepository.cs
src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerDataPreviewRepository.cs
src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerProjectDatabase.Schema.cs
src/JET/JET/Infrastructure/Persistence/Sqlite/SqliteProjectDatabase.cs
src/JET/JET/Infrastructure/Sql/AccountClassificationMigration.cs
src/JET/JET/wwwroot/js/app.js
src/JET/JET/wwwroot/js/data-preview.js
src/JET/JET/wwwroot/js/filter-values.js
src/JET/JET/wwwroot/js/jet-api.js
src/JET/JET/wwwroot/js/state.js
src/JET/JET/wwwroot/js/steps/filter-step.js
src/JET/JET/wwwroot/js/steps/import-step.js
src/JET/JET/wwwroot/js/steps/mapping-step.js
src/JET/JET/wwwroot/js/steps/validate-step.js
src/JET/JET/wwwroot/js/ui-core.js
src/JET/tests/JET.Tests/Application/AccountMappingImportSummaryTests.cs
src/JET/tests/JET.Tests/Application/AccountMappingTemplateExportTests.cs
src/JET/tests/JET.Tests/Application/ActionSerializationTests.cs
src/JET/tests/JET.Tests/Application/AdvancedFilterAstContractTests.cs
src/JET/tests/JET.Tests/Application/ExportCalendarTemplatesHandlerTests.cs
src/JET/tests/JET.Tests/Application/FilterConditionRendererTests.cs
src/JET/tests/JET.Tests/Application/ImportAccountMappingHandlerTests.cs
src/JET/tests/JET.Tests/Application/InlineWorkbookProject.cs
src/JET/tests/JET.Tests/Application/QueryDataPreviewHandlerTests.cs
src/JET/tests/JET.Tests/Application/RuleLogicVersionsTests.cs
src/JET/tests/JET.Tests/Application/TypedFieldFilterContractTests.cs
src/JET/tests/JET.Tests/Application/WorkpaperStep41ProviderParityTests.cs
src/JET/tests/JET.Tests/Architecture/ActionContractRuleVersionTests.cs
src/JET/tests/JET.Tests/Architecture/DataPreviewColumnOwnershipTests.cs
src/JET/tests/JET.Tests/Architecture/FrontendUsabilityContractTests.cs
src/JET/tests/JET.Tests/Architecture/GuiScenarioCoverageTests.cs
src/JET/tests/JET.Tests/Architecture/HandlerRepositoryScopeTests.cs
src/JET/tests/JET.Tests/Architecture/MappingProjectionPolicyFrontendTests.cs
src/JET/tests/JET.Tests/Architecture/QuarterEndWindowsFrontendParityTests.cs
src/JET/tests/JET.Tests/Architecture/ReportExportOwnershipTests.cs
src/JET/tests/JET.Tests/Architecture/ScreenWordingReviewFrontendTests.cs
src/JET/tests/JET.Tests/Architecture/SupportedActionsParityTests.cs
src/JET/tests/JET.Tests/Architecture/TerminologyUnificationFrontendTests.cs
src/JET/tests/JET.Tests/AuditCore/IntakeMappingProgramTests.cs
src/JET/tests/JET.Tests/AuditCore/WorkpaperProgramTests.cs
src/JET/tests/JET.Tests/Domain/AccountMappingColumnResolverTests.cs
src/JET/tests/JET.Tests/Domain/QuarterEndWindowsTests.cs
src/JET/tests/JET.Tests/Infrastructure/BinaryExcelTableReaderTests.cs
src/JET/tests/JET.Tests/Infrastructure/FilterCompletenessTests.cs
src/JET/tests/JET.Tests/Infrastructure/KctFilterPredicateTests.cs
src/JET/tests/JET.Tests/Infrastructure/LegacyWorkbookContentComparison.cs
src/JET/tests/JET.Tests/Infrastructure/OpenXmlSaxTableReaderTests.cs
src/JET/tests/JET.Tests/Infrastructure/PrivateCaseReportComparator.cs
src/JET/tests/JET.Tests/Infrastructure/PrivateCaseReportComparatorTests.cs
src/JET/tests/JET.Tests/Infrastructure/PrivateCaseReportDifferencePolicy.cs
src/JET/tests/JET.Tests/Infrastructure/SqlServerDataPreviewRepositoryTests.cs
src/JET/tests/JET.Tests/Infrastructure/SupportRingBufferLoggerProviderTests.cs
src/JET/tests/JET.Tests/Infrastructure/WorkpaperStep41LegacyBaselineBenchmarkTests.cs
src/JET/tests/JET.Tests/JET.Tests.csproj
tools/README.md
tools/harness/JetHarness.psm1
tools/harness/documentation-check.json
tools/harness/gui-driver/GuiFilterWorkflowScenarios.cs
tools/harness/gui-driver/GuiLegacyFormWorkflow.cs
tools/harness/gui-driver/GuiSideMonthWorkflow.cs
tools/harness/gui-driver/GuiWorkpaperInspection.cs
tools/harness/lanes.json
tools/tests/fixtures/quarter-end-windows.json
tools/tests/frontend-mapping.test.cjs
tools/tests/frontend-workflow.test.cjs
tools/tests/verify-contract.tests.ps1
```

現行提交訊息採用第四輪裁定的新草稿，逐字如下；不留 SQL Server 那一行，不加署名。

```text
補齊 KCT 條件、科目配對與操作入口

- 依 KCT 需求調整條件 A、B 與 A 到 E 的預設動機；查核期末不是季底的案件，A 的命中可能變多。
- 新增假日與補班日範本入口、傳票建立日快捷、值清單筆數與日曆年月選單；假日與補班日檔固定略過第一列。
- 底稿 Step 4-1 的人工旗標改為「人工」「自動」文字，資料預覽也多一欄人工或自動。
- 科目配對檔匯入後提醒差異科目；範本與科目清單不列空白科目編號。
- 舊案件的已存情境全部符合新規則才整批改版。
```

##### 使用者對第四輪結果的回覆（2026-10-07）

Claude 把專案現況整理成本機回填頁，使用者回覆逐字如下：

```text
JET 專案現況回填（2026-10-07）

- Q1 第四輪的結果接不接受：接受（建議）
- Q2 提交前要不要再複核一次第四輪的改動：Claude 唯讀複核第四輪（建議）
- Q3 要不要重跑封裝與 Excel 檢查：交付前重跑兩者（建議）
- Q4 「正式發布前的舊案件可以刪除重建」要不要寫進專案文件：寫進 project-context.md（建議）
- Q5 AGENTS.md 的提交訊息規則要不要改成和你的要求一致：改 AGENTS.md（建議）
```

依回覆處理如下：

- Q1：第四輪結果已接受。依開發流程，計畫仍要等提交前複審、交付前檢查與使用者驗收後才關閉。
- Q4：`project-context.md` 的「這些背景如何影響設計」已加一條：正式發布前建立的案件都可以刪除重建，開發時不必顧慮舊案件的資料結構。
  原話仍以本節「使用者對第三輪結果的回覆」的 J1 備註為準。
- Q5：`AGENTS.md` 的 commit 訊息規則已改成使用者第三輪 C1 備註的要求：只寫主要變更與必要原因，
  不寫實作過程、驗證細節與逐項修改內容。文件檢查要求的 `Co-Authored-By` 字樣仍保留在禁止署名的句子裡。
- 因為 Q5，Git 清單比第四輪多一個路徑 `AGENTS.md`，共 114 個；`docs/project-context.md` 原本就在清單內。
  複核之後若再有增減，再重新定稿完整清單。提交訊息草稿暫時不動，這兩處文件修改要不要寫進去，複核後一併請使用者確認。
- Q2：由 Claude 派一個沒參與第四輪的唯讀子代理，複核第四輪的改動與上面兩處文件修改；結論寫回本節。
- Q3：交付前重跑 Package 與 Excel。等複核提出的修正完成後再跑；Excel 檢查會在這台電腦啟動 Excel，執行前先告訴使用者。

暫存、提交與推送仍待使用者另外授權。
寫回這段與兩處文件修改後 Documentation 通過，沒有錯誤或警告，收據 `20261006-232522077-6178a363ff7e494686086d1e20de9bf3`。

##### 第四輪的唯讀複核（2026-10-07）

Claude 派一個沒參與第四輪的深度研究子代理唯讀複核。它沒有執行任何驗證命令，沒有讀私人資料，也沒有修改檔案。
結論：第四輪可以提交。B2、B5、B1、B3、D6 都改在問題的源頭，新斷言抓得到原本的錯，沒有放寬或刪除既有斷言；
第四輪紀錄列出的 23 份收據、三份逐位元還原比對與 Git 清單都和紀錄一致。複核提出 13 項，處理如下：

| 代號 | 發現 | 處理 |
|:---|:---|:---|
| R1 | `.github/copilot-instructions.md` 的安全摘要仍是舊的提交訊息說法，和新版 `AGENTS.md` 矛盾。 | 已改成和 `AGENTS.md` 一致；Git 清單因此多一個路徑。 |
| R2 | `AGENTS.md` 寫成「不寫實作過程……」，比使用者原話「避免詳細描述……」嚴格。 | 已改回原話的說法。 |
| R3 | 開發現況與本計畫開頭摘要，用現在式寫第三輪複核列出的問題，讀起來像還沒修。 | 已改成「這些已在第四輪修正」。 |
| R4 | 開發現況的「第四輪複核補記」其實是第三輪複核的 A3。 | 已改成「第三輪複核提出、第四輪補記」。 |
| R5 | 摘要的路徑數沒更新；2026-10-07 的 Documentation 收據沒登錄；第四輪最後文件檢查只寫「以實際收據為準」。 | 已補路徑數與收據；最後文件檢查補上結果是通過。 |
| R6 | 提交訊息漏了第五步欄位選單加註來源欄、DuckDB 科目差異清單的修正，以及 2026-10-07 的兩處規則文件修改。 | 草稿是使用者逐字核准的，待使用者裁定。 |
| R7 | 開發現況與 `AccountMappingImportSummaryTests.cs` 的註解把 DuckDB 的觸發條件寫成確定，本計畫自己說證據不足。 | 已改成「目前觀察到」，並寫明完整觸發條件未確認。測試只改註解。 |
| R8 | 測試環境交付說明寫了開發端比對工具的行為，試用者可能以為自己要處理。 | 已刪掉那句，只留先用傳票號碼與項次對齊的提醒，並說明原因是兩邊列的順序可能不同。 |
| R9 | B1 的反例執行時測試檔只有 4 個案例，之後測試檔又改過才有第 5 個；依正規式推演結論不受影響，但反例驗證的不是最終版本。 | 記錄；是否在重跑 Package 時用 Focused 再做一次反例，待使用者裁定。 |
| R10 | 「斷言刪除數仍為 16」沒寫統計範圍。 | 已在第四輪紀錄補上範圍。 |
| R11 | 第三輪回覆那節寫「現行文件尚未寫入這項原則」，已過時。 | 原句保留，後面補記已寫入。 |
| R12 | `project-context.md` 把「我都可以直接刪除」寫成「案件都可以直接刪除」，主詞變成所有案件。 | 當時改成「使用者都可以直接刪除再重新建立」；使用者 2026-10-07 指出這是誤解，已依下方「使用者更正專案背景並要求進階篩選操作測試」重寫。 |
| R13 | B3 沒檢查剛開啟日曆時的 Tab 狀態；B1 的正規式日後可能把其他「目前某某版本 vN」誤報成篩選版本。 | 記錄即可；誤報時失敗訊息會指出是哪一句。 |

Git 清單是第四輪的 113 個路徑加上 `AGENTS.md` 與 `.github/copilot-instructions.md`，共 115 個；複核時已逐條核對前 114 個與工作樹相同。

##### 使用者對第四輪複核的裁定（2026-10-07）

Claude 在對話視窗用選項詢問三件事，使用者的選擇逐字如下：

```text
提交訊息要不要補上複核指出漏掉的變更？＝補三項（建議）
B1 的反例當時沒有跑在最終版本的測試上，要不要用 Focused 再做一次？＝重做一次（建議）
封裝和 Excel 檢查現在可以跑嗎？Excel 檢查會在這台電腦開啟 Excel。＝現在跑
```

使用者同時在對話中送出下列要求，逐字如下（2026-10-07 提交前核對時，由原本的改寫補成原文）：

```text
(f請紀錄，如果之後有問題要直接問我，請做到像上面一樣直接在claude code視窗中顯示相關問題，只有內容過長或需要詳細說明時才整理成網頁，但都絕對不會是單純輸出成文字讓我手動打字回填答案)
```

依裁定處理的結果：

| 項目 | 結果 | 收據 |
|:---|:---|:---|
| 複核修正後的文件檢查 | 通過，沒有錯誤或警告 | `20261006-234008949-0033e68001b04a4d9425d47e85f45711` |
| B1 反例重做 | 把契約第 375 行的完整版本暫時改成簡寫 `v17`，現行版本斷言如預期失敗；之後逐位元還原，SHA-256 與第四輪備份相同 | `20261006-234127724-35a8d0e283e2421d8a7cdca2f9507f93` |
| B1 還原後 | 同一個測試類別 5 個簡寫案例在內全部通過 | `20261006-234147754-db576f5b40c84370b8e6b14c799f2920` |
| Package（Release） | 通過 | `20261006-234222198-f139775b35f64ce9bea23838e111ddc6` |
| Excel（Release） | 通過；結束後沒有留下 Excel 程序 | `20261006-234310446-a8a1aefb46f2461ab2dc1d925d42d34d` |

Package 與 Excel 是第三、四輪改動後第一次重跑。本次沒有重跑 Build、Contract、Public 與 Gui：複核後只改了文件與一段測試註解，
改完之後的 Focused 與 Package 都重新建置過測試專案。PrivateCase 未執行，SQL Server 未啟動。

##### 提交前的最後定稿（2026-10-07）

本小節的清單與提交訊息已由下方「操作測試後的定稿」取代，只保留紀錄。

上方「第四輪 Git 逐檔清單與提交訊息草稿」已由本小節取代。檔案集合共 115 個路徑，是第四輪的 113 個加上
`AGENTS.md` 與 `.github/copilot-instructions.md`；已和目前工作樹逐條核對，沒有 data/、artifacts/ 或私人路徑。
這份清單不代表暫存或提交授權。

```text
.github/copilot-instructions.md
AGENTS.md
docs/README.md
docs/action-contract-manifest.md
docs/data-and-legacy.md
docs/development-status.md
docs/history/README.md
docs/idea-replacement-scope.md
docs/jet-frontend-description.md
docs/jet-guide.md
docs/kct-gl-workflow.md
docs/project-context.md
docs/specs/2026-10-06-kct-gl-requirements-plan.md
docs/test-environment-notes.md
src/JET/JET/AppCompositionRoot.Repositories.cs
src/JET/JET/AppCompositionRoot.cs
src/JET/JET/Application/Handlers/ExportCalendarTemplatesHandler.cs
src/JET/JET/Application/Handlers/ExportReportHandlers.cs
src/JET/JET/Application/Handlers/ExportWorkpaperStreamHandler.cs
src/JET/JET/Application/Handlers/Import/ImportAccountMappingHandler.cs
src/JET/JET/Application/Handlers/Query/QueryAccountMappingDifferencePageHandler.cs
src/JET/JET/Application/Handlers/Query/QueryFilterVoucherHandlers.cs
src/JET/JET/Application/ProjectRepositories.cs
src/JET/JET/Application/Support/FilterConditionRenderer.cs
src/JET/JET/Application/Support/ProjectWorkFileWriter.cs
src/JET/JET/Application/Support/RuleLogicVersions.cs
src/JET/JET/AuditCore/IntakeMappingProgram.cs
src/JET/JET/AuditCore/WorkpaperProgram.cs
src/JET/JET/Domain/Abstractions/AccountMappingDifferenceRepository.cs
src/JET/JET/Domain/ActionExecutionPolicy.cs
src/JET/JET/Domain/Contracts/AccountMappingContracts.cs
src/JET/JET/Domain/Contracts/ICalendarTemplateSource.cs
src/JET/JET/Domain/Contracts/ImportContracts.cs
src/JET/JET/Domain/Contracts/WorkpaperReferenceContracts.cs
src/JET/JET/Domain/Rules/AccountMappingColumnMatcher.cs
src/JET/JET/Domain/Rules/QuarterEndWindows.cs
src/JET/JET/Infrastructure/Diagnostics/SupportRingBufferLoggerProvider.cs
src/JET/JET/Infrastructure/Export/CalendarTemplateSource.cs
src/JET/JET/Infrastructure/Export/WorkpaperWriter.Step2To41.cs
src/JET/JET/Infrastructure/FileIO/OpenXmlSaxTableReader.cs
src/JET/JET/Infrastructure/Persistence/AccountMappingDifferenceQuery.cs
src/JET/JET/Infrastructure/Persistence/AccountMappingEditorRepository.cs
src/JET/JET/Infrastructure/Persistence/AccountMappingPopulationQuery.cs
src/JET/JET/Infrastructure/Persistence/DataPreviewColumns.cs
src/JET/JET/Infrastructure/Persistence/DuckDb/DuckDbProjectDatabase.cs
src/JET/JET/Infrastructure/Persistence/Local/LocalAccountMappingDifferenceRepository.cs
src/JET/JET/Infrastructure/Persistence/Local/LocalAccountMappingExportRepository.cs
src/JET/JET/Infrastructure/Persistence/Local/LocalDataPreviewRepository.cs
src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerAccountMappingDifferenceRepository.cs
src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerAccountMappingExportRepository.cs
src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerDataPreviewRepository.cs
src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerProjectDatabase.Schema.cs
src/JET/JET/Infrastructure/Persistence/Sqlite/SqliteProjectDatabase.cs
src/JET/JET/Infrastructure/Sql/AccountClassificationMigration.cs
src/JET/JET/wwwroot/js/app.js
src/JET/JET/wwwroot/js/data-preview.js
src/JET/JET/wwwroot/js/filter-values.js
src/JET/JET/wwwroot/js/jet-api.js
src/JET/JET/wwwroot/js/state.js
src/JET/JET/wwwroot/js/steps/filter-step.js
src/JET/JET/wwwroot/js/steps/import-step.js
src/JET/JET/wwwroot/js/steps/mapping-step.js
src/JET/JET/wwwroot/js/steps/validate-step.js
src/JET/JET/wwwroot/js/ui-core.js
src/JET/tests/JET.Tests/Application/AccountMappingImportSummaryTests.cs
src/JET/tests/JET.Tests/Application/AccountMappingTemplateExportTests.cs
src/JET/tests/JET.Tests/Application/ActionSerializationTests.cs
src/JET/tests/JET.Tests/Application/AdvancedFilterAstContractTests.cs
src/JET/tests/JET.Tests/Application/ExportCalendarTemplatesHandlerTests.cs
src/JET/tests/JET.Tests/Application/FilterConditionRendererTests.cs
src/JET/tests/JET.Tests/Application/ImportAccountMappingHandlerTests.cs
src/JET/tests/JET.Tests/Application/InlineWorkbookProject.cs
src/JET/tests/JET.Tests/Application/QueryDataPreviewHandlerTests.cs
src/JET/tests/JET.Tests/Application/RuleLogicVersionsTests.cs
src/JET/tests/JET.Tests/Application/TypedFieldFilterContractTests.cs
src/JET/tests/JET.Tests/Application/WorkpaperStep41ProviderParityTests.cs
src/JET/tests/JET.Tests/Architecture/ActionContractRuleVersionTests.cs
src/JET/tests/JET.Tests/Architecture/DataPreviewColumnOwnershipTests.cs
src/JET/tests/JET.Tests/Architecture/FrontendUsabilityContractTests.cs
src/JET/tests/JET.Tests/Architecture/GuiScenarioCoverageTests.cs
src/JET/tests/JET.Tests/Architecture/HandlerRepositoryScopeTests.cs
src/JET/tests/JET.Tests/Architecture/MappingProjectionPolicyFrontendTests.cs
src/JET/tests/JET.Tests/Architecture/QuarterEndWindowsFrontendParityTests.cs
src/JET/tests/JET.Tests/Architecture/ReportExportOwnershipTests.cs
src/JET/tests/JET.Tests/Architecture/ScreenWordingReviewFrontendTests.cs
src/JET/tests/JET.Tests/Architecture/SupportedActionsParityTests.cs
src/JET/tests/JET.Tests/Architecture/TerminologyUnificationFrontendTests.cs
src/JET/tests/JET.Tests/AuditCore/IntakeMappingProgramTests.cs
src/JET/tests/JET.Tests/AuditCore/WorkpaperProgramTests.cs
src/JET/tests/JET.Tests/Domain/AccountMappingColumnResolverTests.cs
src/JET/tests/JET.Tests/Domain/QuarterEndWindowsTests.cs
src/JET/tests/JET.Tests/Infrastructure/BinaryExcelTableReaderTests.cs
src/JET/tests/JET.Tests/Infrastructure/FilterCompletenessTests.cs
src/JET/tests/JET.Tests/Infrastructure/KctFilterPredicateTests.cs
src/JET/tests/JET.Tests/Infrastructure/LegacyWorkbookContentComparison.cs
src/JET/tests/JET.Tests/Infrastructure/OpenXmlSaxTableReaderTests.cs
src/JET/tests/JET.Tests/Infrastructure/PrivateCaseReportComparator.cs
src/JET/tests/JET.Tests/Infrastructure/PrivateCaseReportComparatorTests.cs
src/JET/tests/JET.Tests/Infrastructure/PrivateCaseReportDifferencePolicy.cs
src/JET/tests/JET.Tests/Infrastructure/SqlServerDataPreviewRepositoryTests.cs
src/JET/tests/JET.Tests/Infrastructure/SupportRingBufferLoggerProviderTests.cs
src/JET/tests/JET.Tests/Infrastructure/WorkpaperStep41LegacyBaselineBenchmarkTests.cs
src/JET/tests/JET.Tests/JET.Tests.csproj
tools/README.md
tools/harness/JetHarness.psm1
tools/harness/documentation-check.json
tools/harness/gui-driver/GuiFilterWorkflowScenarios.cs
tools/harness/gui-driver/GuiLegacyFormWorkflow.cs
tools/harness/gui-driver/GuiSideMonthWorkflow.cs
tools/harness/gui-driver/GuiWorkpaperInspection.cs
tools/harness/lanes.json
tools/tests/fixtures/quarter-end-windows.json
tools/tests/frontend-mapping.test.cjs
tools/tests/frontend-workflow.test.cjs
tools/tests/verify-contract.tests.ps1
```

提交訊息依使用者裁定補上三項，逐字如下，不加署名：

```text
補齊 KCT 條件、科目配對與操作入口

- 依 KCT 需求調整條件 A、B 與 A 到 E 的預設動機；查核期末不是季底的案件，A 的命中可能變多。
- 新增假日與補班日範本入口、傳票建立日快捷、值清單筆數與日曆年月選單；假日與補班日檔固定略過第一列。
- 第五步的欄位選單在攸關資料元素欄位後加註來源欄。
- 底稿 Step 4-1 的人工旗標改為「人工」「自動」文字，資料預覽也多一欄人工或自動。
- 科目配對檔匯入後提醒差異科目；範本與科目清單不列空白科目編號；修正 DuckDB 列出差異科目時偶發的內部錯誤。
- 舊案件的已存情境全部符合新規則才整批改版。
- 提交訊息規則改為精簡寫法，並寫明正式發布前的案件可刪除重建。
```

##### 使用者更正專案背景並要求進階篩選操作測試（2026-10-07）

使用者回覆 Claude 對 R12 的說明（「寫清楚可以刪除重建案件的是你，不是所有人建的案件都可以丟」），原話逐字如下：

```text
這部分你又會錯意了，這裡我會詳細解釋，因為目前是測試版本，所有功能都會逐步前進並完善，但仍有可能隨時進行大更動，這種情況都是默認直接刪除舊版本建立的專案，並更新 je-tool 版本後，重新建立新專案來測試案件資料，也因為目前是測試版本，所以不會有任何正式案件被操作，都只會是請指定的審計員為測試對象，看看有那些功能不符合預期，如果有要修改或重構的就直接處理並再次建立新案件來測試就好。只有上到正式版本並我有明確說明正式版本要發布部屬到正式環境時，才會需要考慮舊案的相容性問題，因為那已經屬於版本正式迭代的範疇了，但就如前面所說，現在還沒到那地步


如果我前面註解的部分沒有其他疑慮，那請你開始驗證並執行測試循環，包括 gui 的測試，尤其是進階篩選條件的部分需要你做更完整的操作測試，確保來回點擊條件按鈕時，對應的顯示文字和邏輯是有及時更新且正確的，比如如果我反覆點選篩選條件，但我取消A又選擇B再取消B又選擇A，那預設填入的文字是否也是正確的? 而已儲存的篩選情境是否也是正確的操作流程與邏輯? 而 "篩選與檢視" 頁面的結果是否正確更新至 "已儲存情境與矩陣" ? 或是我如果儲存了重複的情境那是否會影響唯一性? 請你確實整個進階條件篩選的操作完整性，每一個使用情境都要顧慮在內，確保程式運作不會有流程 bug 問題
```

`project-context.md` 已依這段重寫：目前是測試版本，大幅更動時預設刪除舊版本建立的案件，更新後重新建立；
測試版本不操作正式案件，只由指定審計員試用；使用者明確說明正式版本要發布部署後，才考慮舊案件相容性。

進階篩選操作測試的做法：

1. 盤點第五步的狀態流程與現有測試涵蓋，列出操作情境清單，包含反覆勾選與取消條件、自動帶入的名稱與動機、
   儲存與更新、另存副本、移除、取消編輯、「篩選與檢視」和「已儲存情境與矩陣」之間的同步，以及重複情境。
2. 用瀏覽器主機實際操作每個情境。它接正式後端與合成案件，不會開桌面視窗搶前景。
3. 發現的問題先寫會失敗的測試並保存第一次失敗，再修正。
4. 把主要情境補成自動化測試，避免之後退回。
5. 完整驗證：Build、三種 Contract、Public、Documentation、Gui（執行前先告知使用者）、Package 與 Excel。
   沒有改到底稿輸出時不重跑 PrivateCase；SQL Server 不啟動。

結果、首次失敗與收據寫回本節下方。不暫存、提交或推送。

###### 操作測試找到的問題（2026-10-07）

Claude 用瀏覽器主機開合成案件「合成示範-篩選就緒」實際操作第五步，同時派一個深度研究子代理唯讀審查第五步程式。
兩邊結果合併如下。確認正常的部分：A、B 反覆勾選與取消，單組或多組的自動名稱和動機都正確；手改名稱會保留，清空後重新帶入；
更新已存情境不新增一筆；另存副本、移除目前或其他情境、取消編輯正確；儲存後「已儲存情境與矩陣」的清單、三種矩陣、
「查看結果」與條件篩選報告狀態都同步；重新開案後卡片勾選和自動名稱還原。名稱唯一性由後端強制，不會存出兩個同名情境。

| 代號 | 問題 | 影響 | 處理 |
|:---|:---|:---|:---|
| F1 | KCT 自動名稱、常用範例與舊表範例的名稱遇到已存同名情境時不附數字。例如已存「A」，再存一個只改天數的 A，名稱仍是「A」，被後端以重名擋下。 | 儲存失敗 | 修正 |
| F2 | 重名、超過 10 個上限與已存清單已變更這類錯誤只寫進訊息紀錄，儲存區旁沒有提示；上限與清單已變更時，訊息紀錄還接著寫「儲存篩選情境完成」。 | 看起來像沒反應或已存成功 | 修正 |
| F3 | KCT 卡片帶入的條件在列內改成別的條件後仍算原卡片：G 改選其他預篩選條件、I 括號內改成週末，卡片仍勾選，名稱和動機仍是原卡片，儲存的命名來源也仍記成原字母。 | 底稿上的名稱、動機和實際條件不符 | 修正 |
| F4 | 沒有命名來源的舊自訂情境，在儲存或移除其他情境時被記成「名稱與動機由系統產生」，之後編輯時原文會被建議文字蓋掉。 | 舊情境原文遺失 | 修正 |
| F5 | 每次儲存都重驗全部已存情境；其他情境不合目前規則時，錯誤會標到目前草稿的列上，兩個以上情境同時不合時，逐一修改或移除都存不進去。 | 流程卡住、錯誤位置標錯 | 先用測試確認，再決定修法 |
| F6 | 草稿有空的條件組時，後端錯誤指的組號和畫面上的組號錯位，該標紅的列沒有標紅。 | 錯誤位置錯 | 修正 |
| F7 | 預覽過的「條件已變更」提示會帶到「新增情境」、「取消編輯」或套用範例後的新草稿。 | 提示錯誤 | 修正 |
| F8 | 在 KCT 草稿裡加入或移除純自訂條件時不重算自動名稱，編輯中情境的標頭會晚一步更新。 | 標頭顯示舊名 | 修正 |
| F9 | 按「移除」進入確認後改去編輯其他情境，確認區仍留著。 | 小問題 | 修正 |
| F10 | 2026-09-05 到 06 存下的舊情境若有排除區，下一次儲存會被默默丟掉。 | 只影響舊案件 | 依使用者「測試版本的舊案件可刪除重建」，只記錄不修 |

使用者 2026-10-07 在對話視窗用選項裁定四項設計取捨，選擇逐字如下：

```text
同一組裡同時有 KCT 卡片和自訂條件時（例如 G 加上「金額大於 40000」），自動帶入的名稱和動機要怎麼寫？＝加上「自訂」（建議）
套用「常用範例」後，如果再增減條件（例如拿掉範例裡的 I），範例原本的名稱和動機要不要跟著改？＝增減條件就改回自動（建議）
條件完全相同、只有名稱不同的兩個情境，目前可以同時存在，矩陣裡會各佔一欄、結果一樣。要怎麼處理？＝維持現狀
正在編輯某個已存情境時套用「常用範例」，目前會變成另存一個新情境，原情境不動。要維持嗎？＝維持另存新情境（建議）
```

依裁定：同組有 KCT 與自訂條件時，名稱寫成「G+自訂」，動機在 KCT 說明下多一行「自訂：」加上自訂條件的說明；
套用範例後只填值或改值時保留範例名稱，加入或移除條件就改用自動名稱，手改過的文字仍保留；條件相同、名稱不同的情境維持可並存；
編輯中套用範例仍另存新情境，兩種範例套用後都收起儲存區。

F5 經程式確認屬實：儲存與移除都會把全部已存情境重新檢查，遇到第一個不合目前規則的就整批失敗，錯誤細節沒有情境序號。
使用者 2026-10-07 在對話視窗裁定，選擇逐字如下：

```text
兩個以上已存情境因上游資料改變而無法套用時，逐一修改或移除都會被另一個擋下。要怎麼修？＝可逐一修改（建議）
```

選項說明是：儲存時，其他仍無法套用、而且沒被改過的已存情境原樣保留，不計算命中；全部修好前，矩陣、報告與匯出維持停用，
和開案時發現無效情境的狀態一致；錯誤會寫明是哪個情境；需要改後端，SQL Server 只經編譯。

###### 前端修正與首次失敗（2026-10-07）

F1 到 F4、F6 到 F9 與四項裁定都在前端修正，集中在 `filter-step.js` 一個命名收斂點：條件加入、移除、換掉或改比較方式後，
先確認 KCT 卡帶入的條件仍是原卡，再依目前條件重算自動名稱與動機。
原本只有勾選或取消卡片時才重算，所以 F3 與 F8 會漏；`state.js` 只在同一份草稿改條件時保留「條件已變更」提示。

- F1：KCT 自動名稱、常用範例與舊表範例的名稱，遇到已儲存的同名情境時都附數字，和自訂建議名稱同一個函式。
- F2：名稱和其他已儲存情境相同時不送出，名稱欄旁與儲存按鈕上方說明；超過十個與清單已變更寫在儲存按鈕上方，
  也不再記「儲存篩選情境完成」；後端錯誤對不到條件列時寫在儲存按鈕上方。
- F3：KCT 卡的身分依卡片規格比對型別、預篩選鍵、欄位、比較方式與 I 括號內子條件；天數、清單與尾數不影響。
- F4：只有編輯器草稿依旗標產生命名來源，已儲存但沒有命名來源的舊情境原樣送回。
- F6：後端組號先換回畫面上的組位置再標紅。F7：換成新草稿時預覽提示回到初始文字。F8：標頭名稱隨自動名稱更新。
  F9：改去編輯或複製情境時收起移除確認區。
- 裁定：同組有 KCT 與自訂條件時名稱寫「G+自訂」，動機每條自訂條件多一行「自訂：」；範例文字在增減條件後改回自動；
  舊表範例比照常用範例處理，兩者套用後都收起儲存區。建議動機改用讀回句的純文字產生，不再經過畫面元素。

前端測試新增 13 個，其中 7 個用合成畫面繪出正式第五步並以真實點擊操作。修正前的原始檔取自建置輸出資料夾
（第四輪最後修改時間 00:10，與工作樹逐位元比較只差這次的修改，SHA-256 記在 `artifacts/review/kct-filter-ops/original-sources.sha256`），
用同一批測試跑出的結果：新加的 12 個行為測試全部失敗，原因都是行為不符，例如 F1 的名稱仍是「G」、F2 的提示是空白、F4 送出了命名來源；
另有 7 個既有測試因證據副本沒附 CSS 與文件而失敗，和這次修改無關。修正後 241 個前端流程測試全部通過。
兩份輸出在 `artifacts/review/kct-filter-ops/`，不進 Git。「反覆點選 A、B」那一個測試在修正前就通過，用來固定已正確的行為。

既有測試只改了兩個測試夾具的呼叫：原本呼叫已移除的 `applyKctNaming`，改成呼叫新的 `refreshAutomaticMetadata`，斷言不變；
共用的合成 DOM 補上 `dataset`，讓多條件組的畫面可以在測試中繪出。
Gui 新增兩段：同名自動附數字後可儲存並寫出「G+自訂」；另存副本與移除後矩陣收起、重新載入並更新欄數，報告改為尚未產生目前版本。
Gui「filter-auditor-journey」第 302 與 309 行仍預期套用範例後再加條件時名稱維持「借現金、貸非現金」，和新裁定衝突；
要先跑 Gui 保存第一次失敗，再改成新行為並寫明原因。

###### F5 改成「上游修改時清除下游」（2026-10-07）

子代理設計「可逐一修改」時查到衝突：現行規格允許底稿只勾有效情境匯出，未選的無效情境不擋（2026-09-23 依使用者回饋修正，
`jet-guide.md` 第 7 節）。Claude 出選項時沒查到這條，說明寫錯，於是再問一次。使用者回覆逐字如下：

```text
如果執行流程是先匯入資料並進行相關配對與測試，然後設定好了篩選條件並且也儲存為篩選情境，只不過還沒有匯出底稿，而這時候使用者如果額外調整前面的設定，比如修改了欄位配對、科目配對、假日補班日等等，而導致篩選條件的情境因為資料修改而無效時，那麼被影響的選情境則應該同樣被視為無效，因為這樣就會有顯著的完整性差異，也就是說，被修改的資料應該重新設定篩選情境。在這樣的敘述下，則應該要有對應的機制是如果使用者更改了上游資料，那麼下游資料則應該直接被清除或重設至預設狀態，這樣就能最直接的避免版本差異問題，而這部分更不應該被 agent 視為修復的範圍，因為這只會徒增系統功能複雜性，但實際上對業務沒有任何幫助
```

依此，「可逐一修改」的後端設計不做，程式也還沒動。Claude 接著用選項詢問範圍，使用者回覆逐字如下：

```text
上游資料修改後，篩選情境要清到什麼程度？＝清掉全部已存情境（建議）
哪些上游修改要觸發清除？＝會讓篩選結果過期的修改（建議）
清除前要不要先讓審計員確認？＝這部分要注意你不需要過度解釋系統狀態，因為這部分是為了讓使用者理解它的操作會有那些影響，但不需要解釋到每一個元件被影響的狀態，那樣 ai 味就太明顯了，對此，你可以簡單的說明 "如果更改設定好的資料，則會清除後面的設定" 這樣較為簡單的敘述，當然你可以描述得更好(因為我這只是口頭說明的範例)，但根據你提供的選項來看，最應該避免的就是 "會清除N個已存情境" 這種細節敘述的狀態提示，你需要保留使用者體驗為開發基準，避免 agent 誤把每一個細節和功能都當作必要告知使用者的資訊，這只會讓使用者更難聚焦在他要處理的審計業務
這個機制要這次一起做，還是先記錄、等本計畫交付後再做？＝這次一起做
```

做法：
- 觸發條件沿用唯一的失效政策 `AuditDependencyPolicy`：凡是會讓篩選結果過期的上游修改（重新匯入或重新投影總帳、行事曆、科目配對、
  授權名單、財報準備日、分類設定，以及舊案件結構升級），同一筆交易內清掉全部已存情境與命中；試算表的修改不影響篩選，不清。
  （2026-10-07 補記：試算表這一句已由下方「TB 修改改成比照 GL」取代，TB 的修改現在也會清除。）
  已匯出的正式底稿檔不動，版本紀錄照舊標成先前資料或條件的版本。
- 不加確認關卡。只在第一到四步有已存情境時，用一句簡單的話提醒更改已設定的資料會清除後面的設定；清除後第五步回到預設狀態。
  不寫清掉幾個情境這類細節。
- 依「上游修改會清除下游」，分類仍被情境使用時不能刪除的限制、重新配對後保留情境並提示缺欄的流程都不再需要，一併移除或改寫；
  以舊行為為前提的既有測試與 Gui 情境，保留第一次失敗後改成新行為並寫明原因。

###### 上游清除的實作與驗證（2026-10-07）

後端由子代理實作，Claude 逐檔複核：
- `AuditDependencyPolicy` 讓情境定義與篩選命中同進退，`RuleRunResultReset` 在同一筆交易刪除命中後再刪全部已存情境。
  寫入類 action 的 `invalidatedResults` 多一個 `filterScenarios`，一律和 `filter` 同值。
- 情境清掉後 `staleState.filter` 回到「從未執行」，和送出空清單時一致，因為已經沒有可以重跑的情境。
- 沒有已存情境時，查詢與匯出改回「目前沒有已儲存的篩選情境」並說明下一步，不再誤說「尚未套用目前規則」。
- 分類只被情境使用時可以刪除；科目配對仍指到該分類時照舊不能刪除，訊息只講科目配對。
- 新增 `UpstreamMutationClearsFilterScenariosTests`，第一次執行 77 個案例中 69 個失敗（收據 `20261007-032603298-8e5757208f2546c7bc6929d4bc18c780`）。
  以舊行為為前提的 C# 測試都改成新行為，測試旁寫明裁定與第一次失敗收據。

前端與畫面：
- `mapping-step.js` 確認傳票建立日的提示原本寫「引用此欄位的情境仍保留」，和新規則不符，刪掉這半句。
- 前端測試的模擬回應改成後端現在會送的形狀，加上 `filterScenarios`。改完後 5 個測試失敗，原因都是仍預期篩選命中失效時保留情境、
  草稿或「情境設定仍保留」的訊息（`artifacts/review/kct-filter-ops/frontend-workflow-before-assertion-update-upstream-shape.txt`）。
  斷言改成新行為後，244 個前端流程測試全部通過。
- 5 個前端原始碼字串檢查跟著這次的前端修正更新，第一次失敗收據 `20261007-035823939-41943206b5f747f1b7fb8dfe79f987d7`。

Gui：
- 「approval-mapping-modes」連續失敗在全選攸關資料元素欄位後的焦點檢查。加診斷後確認：前端重畫後用延後一步的方式移動焦點，
  檢查卻在點擊後約 8 毫秒就執行，早於那一步。檢查改成最多等 2 秒再判斷焦點最後停在哪裡，第一次失敗收據
  `20261007-040407746-edd2654f66924d26b2a061f1aea020c8`。
- 「filter-auditor-journey」套用範例後又加第二組條件，名稱照裁定改回自動，原本預期仍是「借現金、貸非現金」
  （收據 `20261007-041440189-2762d661a1654592b1318cbe1b439b0d`）。新加的矩陣段落最後切回原本的分錄檢視，操作次數改為 112 次。
- 「kct-remap-recovery」原本驗證重新配對後情境保留、提示缺欄與返回補欄（收據 `20261007-041919444-f03fb3816dec4d5f9758ebd9b5d2a1f2`）。
  改成：取消配對修改時情境仍在；確認配對後情境清空、提醒句消失；缺核准人員欄位時 J 存不進去；補回欄位後重新儲存、匯出及重開。
- 「filter-kct-editing」加了同名附數字與「G+自訂」一段，操作次數超過原上限（收據 `20261007-041845311-99c28723509642669f6b05af9959b892`），
  單一情境的操作上限從 102 次調為 120 次。第四輪新增的 `GuiScenarioCoverageTests` 把這個情境鎖在 98 次，Public 因此失敗一次
  （收據 `20261007-044709251-bb65371e7b2c4aaead6dfd5bf4d571ca`），改成 115 次並確認新段落存在。
- 曾在「authorized-list-recovery」補上提醒句與清除的檢查，但這個合成案件只做到驗證、本來就沒有篩選情境，檢查沒有意義，已拿掉。
  提醒句、清除與訊息由「kct-remap-recovery」用實際儲存的情境驗證，授權名單匯入的清除由後端測試涵蓋。

###### 操作測試後的定稿（2026-10-07）

本小節的 Git 清單與提交訊息已由下方「本輪 Git 清單與提交訊息」取代，只保留歷史紀錄。

完整驗證的最後收據：

| 檢查 | 結果 | 收據 |
|:---|:---|:---|
| Contract | 通過 | `20261007-044659500-c0eec0a1b89e47d58958ac9102569d2f` |
| Contract FrontendMapping | 通過 | `20261007-044701273-8201e66aafbd475a9af207beea5b31ac` |
| Contract FrontendPreview | 通過 | `20261007-044706199-3d962900ef24444a9dbe52e6f8049be8` |
| Public（含建置與前端測試） | 通過 | `20261007-045106414-832c498dc44e469f8a376624a52a9efc` |
| Gui，17 個情境 | 通過 | `20261007-044138121-f82ed6d5794f49f7864a675a7687d33a` |
| Package（Release） | 通過 | `20261007-045427952-dc8ad5ee981e477fa826dbb0cd4f9bb7` |
| Excel（Release） | 通過 | `20261007-045459043-5f971925c6d2438fb83e685a460d0861` |

Gui 通過之後只改了 `GuiScenarioCoverageTests` 這個測試檔，Public 已在改後重跑通過。這一段沒有改到底稿與報告的輸出，
PrivateCase 沿用 2026-10-06 的通過結果，沒有重跑；SQL Server 只經編譯。Documentation 在最後的文件修改後重跑通過，
最後一次在寫入本段收據後執行。

Git 清單共 160 個路徑，是上一版的 115 個加上這次操作測試、上游清除與 Gui 情境改動的 45 個；已和目前工作樹逐條核對，
沒有 data/、artifacts/ 或私人路徑。這份清單不代表暫存或提交授權。

```text
.github/copilot-instructions.md
AGENTS.md
docs/README.md
docs/action-contract-manifest.md
docs/data-and-legacy.md
docs/development-status.md
docs/history/README.md
docs/idea-replacement-scope.md
docs/jet-frontend-description.md
docs/jet-guide.md
docs/kct-gl-workflow.md
docs/project-context.md
docs/specs/2026-10-06-kct-gl-requirements-plan.md
docs/test-environment-notes.md
src/JET/JET/AgentGuiTestProfile.cs
src/JET/JET/AppCompositionRoot.Repositories.cs
src/JET/JET/AppCompositionRoot.cs
src/JET/JET/Application/Handlers/ExportCalendarTemplatesHandler.cs
src/JET/JET/Application/Handlers/ExportReportHandlers.cs
src/JET/JET/Application/Handlers/ExportWorkpaperStreamHandler.cs
src/JET/JET/Application/Handlers/Import/ImportAccountMappingHandler.cs
src/JET/JET/Application/Handlers/MappingHandlers.cs
src/JET/JET/Application/Handlers/Query/QueryAccountMappingDifferencePageHandler.cs
src/JET/JET/Application/Handlers/Query/QueryFilterVoucherHandlers.cs
src/JET/JET/Application/ProjectRepositories.cs
src/JET/JET/Application/Support/FilterConditionRenderer.cs
src/JET/JET/Application/Support/FilterPopulationScopeParser.cs
src/JET/JET/Application/Support/ProjectWorkFileWriter.cs
src/JET/JET/Application/Support/ReportExportSupport.cs
src/JET/JET/Application/Support/RuleLogicVersions.cs
src/JET/JET/Application/Support/WorkflowResultStateSupport.cs
src/JET/JET/AuditCore/IntakeMappingProgram.cs
src/JET/JET/AuditCore/WorkpaperProgram.cs
src/JET/JET/Domain/Abstractions/AccountMappingDifferenceRepository.cs
src/JET/JET/Domain/ActionExecutionPolicy.cs
src/JET/JET/Domain/Contracts/AccountMappingContracts.cs
src/JET/JET/Domain/Contracts/ICalendarTemplateSource.cs
src/JET/JET/Domain/Contracts/ImportContracts.cs
src/JET/JET/Domain/Contracts/WorkpaperReferenceContracts.cs
src/JET/JET/Domain/Rules/AccountMappingColumnMatcher.cs
src/JET/JET/Domain/Rules/AuditDependencyPolicy.cs
src/JET/JET/Domain/Rules/QuarterEndWindows.cs
src/JET/JET/Infrastructure/Diagnostics/SupportRingBufferLoggerProvider.cs
src/JET/JET/Infrastructure/Export/CalendarTemplateSource.cs
src/JET/JET/Infrastructure/Export/WorkpaperWriter.Step2To41.cs
src/JET/JET/Infrastructure/FileIO/OpenXmlSaxTableReader.cs
src/JET/JET/Infrastructure/Persistence/AccountMappingDifferenceQuery.cs
src/JET/JET/Infrastructure/Persistence/AccountMappingEditorRepository.cs
src/JET/JET/Infrastructure/Persistence/AccountMappingPopulationQuery.cs
src/JET/JET/Infrastructure/Persistence/DataPreviewColumns.cs
src/JET/JET/Infrastructure/Persistence/DuckDb/DuckDbProjectDatabase.cs
src/JET/JET/Infrastructure/Persistence/Local/LocalAccountMappingDifferenceRepository.cs
src/JET/JET/Infrastructure/Persistence/Local/LocalAccountMappingExportRepository.cs
src/JET/JET/Infrastructure/Persistence/Local/LocalAccountTaxonomyStore.cs
src/JET/JET/Infrastructure/Persistence/Local/LocalDataPreviewRepository.cs
src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerAccountMappingDifferenceRepository.cs
src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerAccountMappingExportRepository.cs
src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerAccountTaxonomyStore.cs
src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerDataPreviewRepository.cs
src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerProjectDatabase.Schema.cs
src/JET/JET/Infrastructure/Persistence/Sqlite/SqliteProjectDatabase.cs
src/JET/JET/Infrastructure/Sql/AccountClassificationMigration.cs
src/JET/JET/Infrastructure/Sql/ResultStaleStateSql.cs
src/JET/JET/Infrastructure/Sql/RuleRunResultReset.cs
src/JET/JET/wwwroot/js/app.js
src/JET/JET/wwwroot/js/data-preview.js
src/JET/JET/wwwroot/js/filter-values.js
src/JET/JET/wwwroot/js/jet-api.js
src/JET/JET/wwwroot/js/state.js
src/JET/JET/wwwroot/js/steps/create-step.js
src/JET/JET/wwwroot/js/steps/filter-step.js
src/JET/JET/wwwroot/js/steps/import-step.js
src/JET/JET/wwwroot/js/steps/mapping-step.js
src/JET/JET/wwwroot/js/steps/validate-step.js
src/JET/JET/wwwroot/js/ui-core.js
src/JET/tests/JET.Tests/Application/AccountMappingImportSummaryTests.cs
src/JET/tests/JET.Tests/Application/AccountMappingTemplateExportTests.cs
src/JET/tests/JET.Tests/Application/AccountTaxonomySaveHandlerTests.cs
src/JET/tests/JET.Tests/Application/ActionSerializationTests.cs
src/JET/tests/JET.Tests/Application/AdvancedFilterAstContractTests.cs
src/JET/tests/JET.Tests/Application/AuthorizedPreparerWorkflowTests.cs
src/JET/tests/JET.Tests/Application/Batch4ProjectUpdateTests.cs
src/JET/tests/JET.Tests/Application/Batch8PreparationDateReadbackTests.cs
src/JET/tests/JET.Tests/Application/Batch9ArtifactStateTests.cs
src/JET/tests/JET.Tests/Application/Batch9MutationStateTests.cs
src/JET/tests/JET.Tests/Application/CompletenessBackendGateProviderTests.cs
src/JET/tests/JET.Tests/Application/ExportCalendarTemplatesHandlerTests.cs
src/JET/tests/JET.Tests/Application/FilterConditionRendererTests.cs
src/JET/tests/JET.Tests/Application/FilterHitsPageTests.cs
src/JET/tests/JET.Tests/Application/FilterQueryFreshnessTests.cs
src/JET/tests/JET.Tests/Application/FilterResultCursorRevisionTests.cs
src/JET/tests/JET.Tests/Application/ImportAccountMappingHandlerTests.cs
src/JET/tests/JET.Tests/Application/IndependentWorkpaperWorkflowTests.cs
src/JET/tests/JET.Tests/Application/InlineWorkbookProject.cs
src/JET/tests/JET.Tests/Application/KctPrerequisiteProviderTests.cs
src/JET/tests/JET.Tests/Application/PrescreenOptionalSourceWorkflowTests.cs
src/JET/tests/JET.Tests/Application/ProjectFolderPortabilityTests.cs
src/JET/tests/JET.Tests/Application/QueryDataPreviewHandlerTests.cs
src/JET/tests/JET.Tests/Application/ResultInvalidationNonWorkingDaysTests.cs
src/JET/tests/JET.Tests/Application/ResultInvalidationProjectionAndFilterTests.cs
src/JET/tests/JET.Tests/Application/ResultInvalidationTestSupport.cs
src/JET/tests/JET.Tests/Application/RuleLogicVersionsTests.cs
src/JET/tests/JET.Tests/Application/SavedScenarioReplay.cs
src/JET/tests/JET.Tests/Application/SelectedScenarioWorkpaperTests.cs
src/JET/tests/JET.Tests/Application/TagMatrixRowPageTests.cs
src/JET/tests/JET.Tests/Application/TagMatrixScenariosTests.cs
src/JET/tests/JET.Tests/Application/TagMatrixVoucherPageTests.cs
src/JET/tests/JET.Tests/Application/TypedFieldFilterContractTests.cs
src/JET/tests/JET.Tests/Application/UpstreamMutationClearsFilterScenariosTests.cs
src/JET/tests/JET.Tests/Application/WorkpaperStep41ProviderParityTests.cs
src/JET/tests/JET.Tests/Architecture/AccountTaxonomyFrontendTests.cs
src/JET/tests/JET.Tests/Architecture/ActionContractRuleVersionTests.cs
src/JET/tests/JET.Tests/Architecture/DataPreviewColumnOwnershipTests.cs
src/JET/tests/JET.Tests/Architecture/FilterAstFrontendContractTests.cs
src/JET/tests/JET.Tests/Architecture/FilterKctMetadataFrontendTests.cs
src/JET/tests/JET.Tests/Architecture/FilterKctNonBusinessDayFrontendTests.cs
src/JET/tests/JET.Tests/Architecture/FilterTemplatesFrontendTests.cs
src/JET/tests/JET.Tests/Architecture/FrontendUsabilityContractTests.cs
src/JET/tests/JET.Tests/Architecture/GuiScenarioCoverageTests.cs
src/JET/tests/JET.Tests/Architecture/HandlerRepositoryScopeTests.cs
src/JET/tests/JET.Tests/Architecture/MappingProjectionPolicyFrontendTests.cs
src/JET/tests/JET.Tests/Architecture/QuarterEndWindowsFrontendParityTests.cs
src/JET/tests/JET.Tests/Architecture/ReportExportOwnershipTests.cs
src/JET/tests/JET.Tests/Architecture/ScreenWordingReviewFrontendTests.cs
src/JET/tests/JET.Tests/Architecture/SupportedActionsParityTests.cs
src/JET/tests/JET.Tests/Architecture/TerminologyUnificationFrontendTests.cs
src/JET/tests/JET.Tests/AuditCore/IntakeMappingProgramTests.cs
src/JET/tests/JET.Tests/AuditCore/WorkpaperProgramTests.cs
src/JET/tests/JET.Tests/Domain/AccountMappingColumnResolverTests.cs
src/JET/tests/JET.Tests/Domain/AuditDependencyPolicyTests.cs
src/JET/tests/JET.Tests/Domain/QuarterEndWindowsTests.cs
src/JET/tests/JET.Tests/Infrastructure/BinaryExcelTableReaderTests.cs
src/JET/tests/JET.Tests/Infrastructure/FilterCompletenessTests.cs
src/JET/tests/JET.Tests/Infrastructure/KctFilterPredicateTests.cs
src/JET/tests/JET.Tests/Infrastructure/LegacyWorkbookContentComparison.cs
src/JET/tests/JET.Tests/Infrastructure/OpenXmlSaxTableReaderTests.cs
src/JET/tests/JET.Tests/Infrastructure/PrivateCaseReportComparator.cs
src/JET/tests/JET.Tests/Infrastructure/PrivateCaseReportComparatorTests.cs
src/JET/tests/JET.Tests/Infrastructure/PrivateCaseReportDifferencePolicy.cs
src/JET/tests/JET.Tests/Infrastructure/ResultStaleStateLifecycleTests.cs
src/JET/tests/JET.Tests/Infrastructure/SchemaV7MigrationTests.cs
src/JET/tests/JET.Tests/Infrastructure/SqlServerDataPreviewRepositoryTests.cs
src/JET/tests/JET.Tests/Infrastructure/SupportRingBufferLoggerProviderTests.cs
src/JET/tests/JET.Tests/Infrastructure/WorkpaperStep41LegacyBaselineBenchmarkTests.cs
src/JET/tests/JET.Tests/JET.Tests.csproj
tools/README.md
tools/harness/JetHarness.psm1
tools/harness/documentation-check.json
tools/harness/gui-driver/GuiDataRecoveryScenarios.cs
tools/harness/gui-driver/GuiFilterWorkflowScenarios.cs
tools/harness/gui-driver/GuiLegacyFormWorkflow.cs
tools/harness/gui-driver/GuiScenarios.cs
tools/harness/gui-driver/GuiSideMonthWorkflow.cs
tools/harness/gui-driver/GuiWorkpaperInspection.cs
tools/harness/gui-driver/OwnedGuiRun.cs
tools/harness/lanes.json
tools/tests/fixtures/quarter-end-windows.json
tools/tests/frontend-mapping.test.cjs
tools/tests/frontend-workflow.test.cjs
tools/tests/verify-contract.tests.ps1
```

使用者 2026-10-07 對提交訊息的選擇逐字如下：

```text
這輪多了進階篩選的命名修正與「上游修改清除下游」，提交訊息要怎麼更新？（只是定稿草稿，不代表暫存或提交）="補兩行並改標題（建議）"
```

提交訊息逐字如下，不加署名：

```text
補齊 KCT 條件、科目配對、操作入口與篩選情境流程

- 依 KCT 需求調整條件 A、B 與 A 到 E 的預設動機；查核期末不是季底的案件，A 的命中可能變多。
- 新增假日與補班日範本入口、傳票建立日快捷、值清單筆數與日曆年月選單；假日與補班日檔固定略過第一列。
- 第五步的欄位選單在攸關資料元素欄位後加註來源欄。
- 底稿 Step 4-1 的人工旗標改為「人工」「自動」文字，資料預覽也多一欄人工或自動。
- 科目配對檔匯入後提醒差異科目；範本與科目清單不列空白科目編號；修正 DuckDB 列出差異科目時偶發的內部錯誤。
- 舊案件的已存情境全部符合新規則才整批改版。
- 進階篩選的自動名稱遇到同名情境時附數字，同組混有自訂條件時寫「+自訂」；套用範例後增減條件，名稱改回自動；同名情境不能儲存。
- 修改會讓篩選結果過期的前面資料時，清除全部已存篩選情境，前面步驟只提醒一句。
- 提交訊息規則改為精簡寫法，並寫明正式發布前的案件可刪除重建。
```

###### 使用者對操作測試結果的回覆（2026-10-07）

使用者逐段回覆 Claude 的回報，原話逐字如下；引用的回報句子省略。

對「舊案件升級時改寫情境分類的程式，以及第五步的『返回欄位配對』按鈕，在新規則下幾乎沒有作用了」：

```text
可以，請收尾這部分沒有作用的程式，避免後續混淆系統開發
```

對「修改財報準備日時，會先清除再存檔。如果存檔失敗，情境已經清掉，重送一次修改即可」：

```text
可以，這部分請依照你對 je-tool 最佳的理解去改善
```

對子代理多做的兩個決定（情境清掉後篩選狀態回到從未執行、沒有已存情境時的訊息）：

```text
這部分的理解沒有問題
```

對這輪 Gui 情境的改寫：

```text
對於自動化的測試，我需要你考慮到所有的使用情境去操作，而不是針對過去提出的改動去設計測試機制，因為這很有可能只會執行無效操作，尤其是當操作情境早已被驗證成功時，後續新加進去的改動和重構有可能就沒辦法被固有的自動化測是涵蓋到，而導致開發 agent 與測試 agent 有認知差異而相互矛盾。
```

最後一段：

```text
基於以上註釋，請說明還有哪些是當前工作計畫尚未完成的事項? 或者是當前工作計畫額外延伸且未完成的事項? 請你先回顧當前專案進行中的工作計畫，簡要說明該工作計畫要處理的事項及主旨為何? 並摘要當前已經落實且處理的事項，我要追蹤目前為止尚未 commit & push 的事項有哪一些，我要整理工作階段，並且安排一些額外事項至下一輪工作階段處理，也就是說，我想要開始收尾當前的工作計畫，以便可以先在測試環境驗收需要我人工核對的改動，並逐步準備開啟下一階段的工作計畫。
```

處理：前兩項已獲同意，尚未實作。子代理的兩個決定確認接受。自動化測試的要求是新的方向：測試要依完整的使用情境設計，
不只針對個別改動補段落；本輪 Gui 新增的段落屬於後者。Claude 回報計畫現況後，用選項詢問時機，使用者回覆逐字如下：

```text
你同意的兩項修正（移除沒有作用的程式、財報準備日的存檔順序）要在什麼時候做？＝本輪做完再收尾（建議）
依完整使用情境盤點自動化測試（Gui 與前端）要怎麼安排？＝本輪先做
```

###### 收尾前的三項工作（2026-10-07）

1. 移除新規則下沒有作用的程式：舊案件結構升級時改寫情境分類的程式，以及第五步讀取已存情境結果失敗時的「返回欄位配對」按鈕。
   先追呼叫點，確認真的走不到再移除；一般的「重試讀取結果」保留。
2. 財報準備日的存檔：改成清除結果與存新設定在同一個時間窗內完成。資料庫交易先清除、不提交，存好案件設定檔後才提交；
   存檔失敗時資料庫回復，情境和結果都還在。提交失敗時把舊設定寫回。
3. 依使用情境盤點自動化測試：先列出審計員從第一到第六步的使用情境，包括每一步之後回頭修改前面資料、取消、失敗重試、
   重開案件與多個情境；再把現有 C# 測試、前端測試與 Gui 情境對到這份清單，找出沒有涵蓋的情境與只為某次改動而加的段落，
   依結果補測試或重整 Gui 情境。日常檢查優先用不開視窗的前端與後端測試，Gui 用來走完整的審計員旅程。

完成條件：三項做完後重跑 Build、三種 Contract、Public、Documentation、完整 Gui、Package 與 Excel，重新定稿 Git 清單與提交訊息。

進度：
- 第 1 項完成。第 7 版升級在改寫情境後，同一筆交易就以 `SchemaV7Migration` 清掉全部情境，改寫沒有效果；三種資料庫的呼叫、
  改寫程式與只為它存在的錯誤注入點一起移除，測試沒有用到那個注入點。第五步讀取結果失敗時只剩暫時性錯誤，
  拿掉「返回欄位配對」按鈕與「情境設定仍保留」的說明，保留「重試讀取結果」。
- 第 2 項完成。資料庫交易清除後先不提交，報告索引標過期、案件設定檔存好才提交；存檔失敗時交易回復；提交失敗時寫回舊設定。
  新增 `ChangedPreparationDate_WhenSettingsCannotBeSaved_KeepsScenariosResultsAndOldDate`，以鎖住設定檔模擬存檔失敗，
  修正前 SQLite 與 DuckDB 都失敗，情境已被清掉（收據 `20261007-054434864-f8066bf7f73c4b8592371152a7987128`）；修正後通過。
  提交失敗後寫回舊設定的分支沒有自動測試，因為無法穩定製造提交失敗。
- 第 3 項的盤點已完成：唯讀子代理列出 120 個使用情境，對照後端、前端與 Gui 測試，文件在
  `artifacts/review/kct-filter-ops/usage-scenario-coverage.md`（不進 Git）。結論是「上游修改清除情境」在後端 SQLite 與 DuckDB
  測得完整，缺口在畫面接線、第六步畫面行為與跨步驟的真實旅程。本輪補的範圍：
  - 前端：第六步勾選與停用、各修改入口把回應交給 `applyMutationEffects`、取消路徑、第一到四步提醒句的實際渲染與日曆匯入、
    第五步命名裁定（編輯中套用範例、條件相同名稱不同）、矩陣在清單改變後重設、第三步取消變更與試算表確認配對、
    規則升版後的問題清單、移除條件組。
  - 後端：每種清除動作都斷言條件篩選報告與底稿標成過期且檔案不改寫、上游修改失敗或取消時情境保留、
    試算表修改後的完整旅程、重名判斷去頭尾空白、條件相同名稱不同可以並存、沒有已存情境時匯出底稿。
  - Gui：新增「匯出後回頭修改前面步驟」與「空白案件做到匯出」兩條旅程；授權名單情境開頭先存一個情境；
    擴充條件情境匯出時只勾部分情境。本輪為單次改動補上的 Gui 段落（同名附數字、副本與移除後的矩陣），
    等前端測試補上後移除。
  - 不在本輪：會刪除既有 Gui 斷言的縮減（配對往返十次減為兩次、三個條件情境重複的匯出收尾、舊表目錄情境移到前端、
    A 到 E 動機逐張點選），以及約 50 個只比對原始碼字串的架構測試是否降級，要先和使用者確認再動。
  - 盤點發現 `SqlServerSchemaMigrationTests` 仍預期第 7 版升級後情境保留，和產品與本機版測試不符；
    SQL Server 測試平時不跑，依慣例記到開發現況，下次執行 `Provider` 時預期先失敗一次再更新。
    清除情境的結構升級只有第 7 版，屬於會讓篩選結果過期的修改；第 11 版的分類樹升級不讓結果過期，情境保留，和裁定一致。

###### 使用者對現況回報的裁定（2026-10-07，交接前）

Claude 回報計畫現況後，使用者逐段回覆，原話逐字如下；以 `>` 開頭的是引用 Claude 回報的句子，分段用的註解標記省略。

```text
> 現行計畫是〈KCT 總帳資料整理需求與新版 JET 對照〉，從 10/6 開始。起點是 KCT 小組的需求簡報，要回答兩件事：
> 
> 新版 JET 能不能相容舊版工具的 KCT 作業步驟。
> 新版能不能滿足 KCT 期望的 15 項優化。

沒錯，這就是當前工作計畫的主旨，也就是繼續完善短期目標，確認能滿足KCT小組的業務需求，而我有提供你一份PDF文件，那個基本上就是KCT小組提出的需求，在PDF文件中先是解釋了KCT小組在舊版 je tool 中的執行步驟，說明包括了KCT小組在處理每個案件時如何預處理案件資料、如何設定 je tool 、解釋完整性測試、說明審計員會使用的條件範例等等，而文件後半段則說明基於舊版 je tool 的介面與操作有那些預期可以被優化改善的項目，包括更好的日期選擇操作、移除產業別的屬性、內建傳票建立日期欄位與新增項次等功能。這也是目前工作計畫需要你完善的事項，請完整核對整個專案目前是否都有回答道KCT小組提出的議題? 如果尚有未落實的事項則應該提出來說明狀況，我會評估應該要在當前的工作計畫處理，還是移至下一個階段的工作計畫。

> 你剛同意、我還沒動手的兩項：
> 移除新規則下已經沒有作用的程式：舊案件升級時改寫情境分類的程式，以及「返回欄位配對」按鈕。
> 改善修改財報準備日時「先清除、再存檔」的順序。
> 兩項都會改程式，做完要重跑完整驗證，包括 Gui。

請併入當前的工作計畫，以便繼續處理

> 有配對人工旗標的案件，「人工」「自動」是否正確。

請你模擬現實的會計資料，做為測試用的假資料

> KCT A 在期末不是季底的案件，命中區間是否合理。

請你模擬現實的會計資料，做為測試用的假資料

> 依使用情境盤點自動化測試：就是你這次提的要求，是新的方向。

這個請併入現在的工作計畫

> 私人案件的人工旗標驗證：你 10/6 說過要在這份計畫處理完後接著做。

這個併入下一階段的工作計畫，不要合併到現在的

> 只記錄、沒修的事：
> F10：10 月以前存下、帶有排除區的舊情境，下次儲存時排除區會被丟掉。依「舊案件可以刪除重建」，只記錄不修。
> KCT F 到 J 的來源，要等 KCT 提供完整清單才能核對。

這個也是併入到下一階段的工作計畫，不要合併到現在的

> SQL Server：這份計畫裡所有 SQL Server 修改都只經過編譯，沒有連線驗證。

這個繼續擱置，不要閉入到現在和下一個階段的工作計畫，我近期都還沒有要處理 sql server 的業務

> Gui 視窗不搶前景，或改在另一個桌面執行。

這個事項請併入到下一個階段的工作計畫

> 期末後分錄的檢視。

延後處理

> 特定文字檔匯入失敗的原因。

這個已經解決了，請紀錄，這裡我就不額外提供詳細資訊了

> 驗證報告指引工作表改寫。

這個請併入下一階段的工作計畫

> 預篩選收斂、SQLite 取消、驗證框架瘦身，以及低嚴重度事項等。

這個請併入下一階段的工作計畫


雖然你仍然在指派子代理進行任務，但我這裡要先提交註釋，請基於我回覆的內容更新專案狀態，而未完成的工作計畫我將在新的 session 處理，因此請你幫我檢查整個專案狀態並基於我回覆的內容去更新現在進行中的工作計畫，並安排一個指示 prompt 用來指示新 session 接續處理現在的工作計畫。
```

處理：
- 併入本計畫：兩項程式收尾（已完成）、依使用情境盤點自動化測試、為人工核對準備擬真的合成會計資料
  （有配對人工旗標的案件；查核期末不是季底、用來看 KCT A 命中區間的案件），以及完整核對 KCT 簡報議題的落實狀況。
- 併入下一階段工作計畫，不併入本計畫：私人案件的人工旗標驗證、F10、KCT F 到 J 的來源、Gui 視窗不搶前景或改在另一個桌面、
  驗證報告指引工作表改寫、預篩選收斂、SQLite 取消、驗證框架瘦身，以及低嚴重度事項。已寫進開發現況「下一階段工作計畫要納入的事項」。
- SQL Server 繼續擱置，不納入本計畫與下一階段；期末後分錄延後處理。
- 特定原始文字檔無法匯入總帳（D07）依使用者說明記為已解決，已從開發現況的未完成項目移除；
  2026-09-14 原始 PBC 匯入失敗是否同一來源仍未確認，該列保留。
  （2026-10-07 補記：使用者已裁定 PBC 那一列和這件事相同，一起結案，見下方「使用者對 Codex 回報的裁定」。）

###### KCT 簡報議題的落實核對（2026-10-07）

依使用者要求，對照簡報的作業流程與後半段的 15 項優化，核對目前的程式與裁定。「已落實」表示已有程式與通過的測試；
需要使用者在測試環境人工核對的另外註明。

| 項 | KCT 原文 | 目前狀況 | 判斷 |
|:---|:---|:---|:---|
| 1 | 日期設定能否自己直接key in或用下拉式選單 選擇年/月/日 | 建案三個日期欄可以鍵盤逐段輸入，已實測；第五步日期區間可鍵入多種寫法；「指定日期」的日曆加了年、月選單（K11）。 | 已落實 |
| 2 | 產業別功能是否可以不用 | 新版沒有產業別。 | 已落實 |
| 3 | 資料欄位設定，TB設定錯誤, 需要先設完GL&跑完完整性才能回頭，可否增加一個【回上一步】的按鈕 | 左側流程可回到任何已開放的步驟，GL 與 TB 各自重新配對。依 2026-10-07 裁定，重新匯入或重新確認 GL、TB 配對，都會清除驗證、預篩選、篩選命中與全部已存情境，所以回頭改 TB 後要重新驗證並重新設定情境。本格在 2026-10-07 提交前核對時更正，原本寫重配 TB 只讓驗證失效。 | 已落實 |
| 4 | 能否直接新增【傳票建立日期】的欄位，而不要另外於分錄來源模組中手動點選來新增 | 第三步加了「傳票建立日」快捷，一鍵勾成日期型別的攸關資料元素欄位（K4）；依裁定不做固定欄位。 | 已落實，待人工核對 |
| 5 | 人工及自動分錄之欄位，tool目前設定是僅能匯入1(人工)/0(自動)，檢視時較不直觀，是否可以單純寫上【人工/自動】即可? | 底稿 Step 4-1 與資料預覽改寫「人工」「自動」（K5），只有合成資料驗證。本計畫要準備擬真的合成資料供人工核對；私人案件驗證移到下一階段。 | 已落實，待人工核對 |
| 6 | 文字篩選時，是以【包含】的概念進行，容易不小心篩選到錯誤資訊 | 文字條件可選關鍵字、完整相同、開頭、結尾與空白；人員與科目清單預設完全相等。 | 已落實 |
| 7 | 【新增項次】能否直接內建，而不要另外手動點選應用程式來新增 | 沒有配對項次時自動編號。 | 已落實 |
| 8 | 執行完步驟二~五後不要跳資料夾出來 | 匯出後不自動開檔案總管或 Excel。 | 已落實 |
| 9 | 科目配對的時候，目前是人工判斷，並將判斷結果填入acc mapping檔案中，是否有機會改成自動? | 依裁定不自動分類；可以沿用前一案件的配對檔，匯入後列出本案沒有與檔案沒列的科目（K6）。 | 依裁定部分落實 |
| 10 | 科目配對的時候，若檔案有使用【篩選】功能，就會匯不進去 | 補上自動篩選、隱藏列與第三列標頭的測試，並修正讀取器跳過列與欄名判斷的問題（K7）。 | 已落實 |
| 11 | 下圖項目是否有保留之必要(須視其餘案件需求) | 預篩選 #1 依裁定保留（K8），去留併入預篩選收斂，排在下一階段。 | 依裁定保留 |
| 12 | 進階篩選條件設定是否可以每個條件各自獨立? | 每個情境可個別編輯、另存與移除，情境內的條件可個別修改。 | 已落實 |
| 13 | 特定文字篩選，如果是想要放入較多的收入科目, 目前要把所有收入的科目編號都輸入，是否有其他作法? | KCT A、C、D 用收入分類；自訂條件可用科目分類，預設包含下層。 | 已落實 |
| 14 | 特定文字篩選，方向能否不要限定【橫向】或者是有卷軸or統計已key了幾個項目? | 文字清單可換行、可捲動，並顯示已輸入幾個值與上限（K9）。 | 已落實 |
| 15 | 步驟五，匯出底稿需要填寫條件設定的文字說明，能否直接預設文字? | A 到 E 的預設動機改用簡報的評估說明，F 到 J 沿用卡名（K10）；F 到 J 的來源核對移到下一階段。 | 已落實 |

簡報前半段的作業流程：
- 匯入 IDEA、補項次、合併同欄位總帳、金額整理、TB 本期變動、人工與自動欄、高階主管欄、每步跳出檔案總管等，新版都已內化。
- 完整性測試由驗證報告呈現，差異不擋後續步驟；KCT A 到 E 依簡報定義實作（K1、K2、K3、K10），A 加入查核期末，
  期末不是季底的案件結果可能變多，本計畫要準備擬真的合成資料供人工核對。
- 仍在 JET 之外：申請檔的確認，以及移除表頭表尾、往下填補、補科目名稱、合併兩份 TB、特殊日期與科目代碼整理等前置處理。
  依 K12 維持 2026-10-02 裁定，K13 維持不做。交還申請人時 Prepared by 維持建案者（K14）。
- 結論：簡報提出的議題都已有落實或裁定；本計畫還要完成的是擬真合成資料與使用者的人工核對，其餘移到下一階段或依裁定不做。

###### 交接前的狀態與接續順序（2026-10-07）

使用者 2026-10-07 決定把本計畫剩下的工作交給新的 session。交接時的狀態：
- 第 1、2 項（移除沒有作用的程式、財報準備日存檔）已完成，相關測試通過，但還沒跑完整驗證。
- 第 3 項：盤點完成。補前端與後端測試的兩個子代理在讀程式階段就收尾，沒有寫入任何檔案；它們整理的做法、預期值與入口位置，
  記在本小節下方。Gui 的部分：
  - 新情境 `upstream-change-after-export` 已寫好並登錄在驅動程式、`lanes.json` 與驗證框架，還沒有實際跑過；操作次數 40 是估計值。
  - `authorized-list-recovery` 開頭改成先存一個情境，並核對取消、匯入失敗保留與匯入成功清除；還沒跑過，操作次數 38 是估計值。
  - 「空白案件做到匯出」還沒開始。原生選檔視窗無法自動化，要比照 `AgentGuiTestFixtures.cs` 的 `AuthorizedListFixtureHandler`，
    新增一個代替 `host.selectFile` 回應 GL 與 TB 合成檔的夾具。
  - 本輪為單次改動補上的 Gui 段落（`kct_duplicate_name_suffix`、`matrix-after-copy`、`matrix-after-remove` 與切回分錄檢視那一步）
    還在，等前端測試補上同等的檢查後再移除，並把操作次數與 `GuiScenarioCoverageTests` 一起改回。
- 讀程式時找到、還沒用測試證實的事：
  - 第四步分類設定畫面仍把被已存情境使用的分類標成「已被篩選情境使用，不可刪除」並停用移除
    （`wwwroot/js/steps/validate-step.js` 的 `taxonomyCategoriesInUse`）。後端與文件已改成可以刪除，這是本輪漏改的地方，
    接續時先寫會失敗的前端測試，再修正。
  - 第一步的提醒句只在打開「修改案件資料」後出現，前端說明寫第一到四步在有情境時顯示，兩種讀法要請使用者確認。
  - 第六步鎖住時，步驟檢查列出的原因是「修改無法套用目前規則的篩選情境」，流程那一行的短句是「需先儲存篩選情境」，
    可能是刻意設計，要請使用者確認。
  - 重配 TB 後，程式會把條件篩選報告標成過期，文件只寫到底稿依賴驗證紀錄；測試確認行為後，請使用者決定要不要補進文件。
  - 既有的 `WorkpaperExportRequestValidationTests` 用 `*_WorkingPaper.xlsx` 確認沒有產生底稿，但現行檔名是
    `_WorkingPaper_<時間>.xlsx`，這個斷言永遠成立；新測試改用 `*WorkingPaper*.xlsx` 並搜尋子目錄，既有斷言要先寫明原因才改。
- 子代理整理的做法摘要：前端新測試要加進 `frontend-workflow.test.cjs` 或 `frontend-mapping.test.cjs`，因為
  `frontend-mapping-contract.tests.ps1` 只跑這兩個檔；第六步的合成畫面要讓核取方塊與下拉選單讀取標記上的勾選與選取狀態。
  後端的 GL 匯入讀檔失敗、取消、確認配對失敗、假日檔格式錯誤、授權名單整欄空白與分類版本衝突，都有可以穩定觸發的方法，
  詳細見盤點文件 `artifacts/review/kct-filter-ops/usage-scenario-coverage.md` 與交接筆記
  `artifacts/review/kct-filter-ops/handoff-test-notes.md`（兩份都在本機、不進 Git）。

接續順序：
1. 修正分類設定畫面仍擋刪除的問題，先寫會失敗的前端測試。
2. 補前端測試：第六步勾選與停用、各修改入口把回應交給 `applyMutationEffects`、取消路徑、第一到四步提醒句的實際渲染、
   日曆匯入、第五步命名裁定、矩陣在清單改變後重設、第三步取消變更與試算表確認、規則升版後的問題清單、移除條件組。
3. 補後端測試：清除後兩種報告標成過期且檔案不變、上游修改失敗或取消時情境保留、試算表修改後的完整旅程、重名判斷、
   沒有已存情境時匯出底稿。
4. Gui：跑新情境與改過的授權名單情境並校正操作次數；新增「空白案件做到匯出」；前端測試補上後移除本輪為單次改動補上的段落。
5. 準備擬真的合成會計資料供人工核對：一份有人工旗標欄的總帳，旗標用實務常見的代碼；一份查核期末不是季底的案件，
   在季底與查核期末前後放收入借方分錄。只用程式產生，不放真實資料；檔案放在儲存庫外或 `artifacts/` 等不進 Git 的位置，
   並在交付說明寫明怎麼取得與核對重點。
6. 把上面要請使用者確認的事集中，用 Claude Code 視窗的選項詢問。
7. 完整驗證：Build、三種 Contract、Public、Documentation、完整 Gui（先告知使用者會跳出視窗）、Package 與 Excel；
   重新定稿 Git 清單與提交訊息，請使用者確認。暫存、提交或推送仍須使用者另外授權。

###### 七步接續執行紀錄（2026-10-07）

本次依使用者指定的七步順序接續，範圍到完整驗證、Git 清單與提交訊息待確認；不暫存、不提交、不推送。
下一階段事項不在本輪處理，SQL Server 不啟動。開工已讀本節最新裁定、開發現況與兩份本機交接資料，
並執行 Context、git status 與 git diff。開始時有 145 個已追蹤檔案改動、24 個未追蹤項目；這些既有改動保留。

第 1 步：新增前端使用情境測試，從被已存情境引用的分類開始，確認能移除、取消、儲存失敗後重試，
只有成功回應才清除情境。首次 FrontendMapping 失敗收據為 `20261007-061416176-6d40ec87366143eebd26c829c229dc06`，
移除按鈕實際停用，預期應可操作。這是產品行為失敗，不是開工時命令工具的環境錯誤。

既有測試變更原因：`AccountTaxonomyFrontendTests.Editor_ProtectsBuiltInsAndCategoriesUsedBySavedScenarios`
仍要求畫面阻擋被情境引用的分類，與本計畫裁定和現行 `jet-guide.md` 相反。
將改為檢查內建分類保護仍在、舊的情境引用阻擋已移除；科目配對使用中的分類由既有後端測試繼續保護。
不放寬內建分類身分、分類用途、版本衝突或資料一致性斷言。產品修正後先保留這個舊斷言的失敗，再更新它。

舊架構斷言失敗收據為 `20261007-061503637-a8ad54d4ecdb4e859b12b3d4a7ee03df`。
修正後 FrontendMapping 通過，收據 `20261007-061525979-6ebab9d935ea44889b51a478d60c27de`。
第 2 步開始補使用情境測試。共用合成 DOM 將補上核取方塊、下拉選單與 template 的瀏覽器語意，
原因是舊替身沒有這些能力，會把第六步預設勾選誤讀成空集合；不更改既有測試的預期結果。

分類測試的架構檢查另外發現 `WorkflowControlsAccessibilityFrontendTests` 仍要求已移除的停用按鈕帶原因，
收據 `20261007-061531609-16649cbb7e60449ca9bbdd2e532e22bc`。該檢查應改為確認自訂分類提供移除按鈕，
而不是要求重新放回已裁定取消的阻擋；同一測試中其他停用原因檢查保留。

第 1 步完成。Focused `AccountTaxonomy` 與架構檢查通過，收據 `20261007-062654430-04e4e9d63d1b4b57a6241f6450b0575b`。
第 2 步前端測試已補入 `frontend-workflow.test.cjs`，FrontendMapping 共 371 個測試通過，
收據 `20261007-062648310-5012fc897fa1453a958324821812c737`。新增測試涵蓋第六步的情境勾選與鎖定、
GL 與 TB 匯入及確認配對的成功、失敗與取消、科目配對匯入與畫面儲存、日曆修改、第一到四步提醒、
規則升版問題清單、套用範例、同條件不同名稱、另存副本與移除後的矩陣重載，以及移除條件組。
既有案件資料與授權名單測試保留。第一次就通過的補測只算增加涵蓋，不宣稱修正產品。
新增夾具調整期間曾因錯把隱藏的儲存區當成已刪除，讓斷言展開整棵 DOM 而耗用 CPU；已停止該次程序，
改成檢查區塊 hidden 屬性後通過，臨時診斷碼已移除。這幾次環境與夾具失敗不列為產品首次失敗。

第 3 步既有測試變更原因：`WorkpaperExportRequestValidationTests` 的 `*_WorkingPaper.xlsx` 不符合目前有時間尾碼的檔名，
可能讓「未產生底稿」的斷言空泛通過。先新增能抓到合成時間尾碼檔的反例，再把搜尋改成 `*WorkingPaper*.xlsx`，
並包含子目錄；其餘過期版本拒絕的預期不改。

底稿搜尋反例首次失敗收據 `20261007-062848464-ae7200be73cc4231bc617b19d5305d3d`，確認舊搜尋找不到時間尾碼檔。
第 3 步新增 `UsageScenarioJourneyTests`，在 SQLite 與 DuckDB 測試全部 13 種清除操作的報告過期與原檔保留，
六種失敗或取消後的情境、命中及來源保留，TB 重匯及重配後重新驗證再匯出，名稱去空白但區分大小寫，
同條件不同名稱並存，以及無情境時拒絕匯出且不產檔。Focused 通過，收據 `20261007-063231799-f80b942bf52d49fc96147311a432bf4f`。
授權名單反例一開始誤用不支援的 CSV，改為合成 xlsx 後才能驗到整欄空白的拒絕，未更動產品規則。
TB 重配後兩種報告目前都會標過期，舊檔不變；是否把條件篩選報告的此行為補入文件，留待第 6 步確認。

第 4 步開始前已告知使用者 Gui 會跳出視窗並搶走前景。

`authorized-list-recovery` 已實跑通過，實際 38 次，收據 `20261007-063545433-ce02a0bc997b43c6b8afd5d85b69bc13`。
`upstream-change-after-export` 首先遇到封閉鍵盤不接受空白字元，收據 `20261007-063403699-7f34c58c63e846d3a1a5a2a06fdef518`；
合成輸入改為 `EDIT`，沒有放寬鍵盤限制。接著發現旅程誤以為 TB 重配後、尚未重新驗證即可進入第六步，
收據 `20261007-063511370-843e41050c204d2ea671920df6f8e0c6`。現行規格要求可用驗證，導覽也逐步檢查此前提；
因此改為確認第六步導覽仍停用，先重新驗證再匯出，不改產品放行規則。

移除既有 Gui 段落的原因：前端已實際操作同名自動附數字、另存副本及移除後的矩陣重載，
且 FrontendMapping 通過。依使用者本輪要求，移除 `kct_duplicate_name_suffix`、`matrix-after-copy`、
`matrix-after-remove` 與只為這兩段補的切回分錄檢視；其他旅程與斷言保留，對應架構斷言改核對前端替代測試。
`extended-conditions` 改為只選部分情境匯出，保留重開後五個情境及原有各條件的固定答案。

搬移 Gui 段落後的舊架構斷言首次失敗，收據 `20261007-063732913-8bf859309a2c475d9f56ac3d92c6c7a4`。
空白案件旅程第一次已走完全部行為，實際 51 次操作，原先估計 80 次不符而讓收據未通過，
收據 `20261007-064019296-cffd14e9a6c446cdb12f29f00e42630b`；已按實測值調整。
上游修改旅程的每週非工作日核取方塊本身不可見，應點可見的標籤；已修正測試定位，不更改勾選結果斷言。
目前已重跑通過：上游修改 39 次（`20261007-064154571-c1ef84e5c4c14b7aaac90f90f1df49a1`）、
空白案件到匯出 51 次（`20261007-064218325-b10d5a33896646bcb87b1026292623fc`）、
自訂條件旅程 99 次（`20261007-064233253-18c60411c30b42fda95a122c494593e2`），
KCT 編輯旅程 98 次（`20261007-064312264-6321a114bcf74db6aef751369c0a6c6a`）。單項診斷不代表完整 Gui 通過。

使用者本輪另外提供原始 KCT PDF。本次先確認原檔為 50 頁，並只對第 28、31、33、38 頁做本機主題核對，
沒有重新讀取全部截圖或輸出其中的真實資料。這不算重新完成整份簡報的逐頁審閱；詳細對照仍沿用已保存的來源與裁定。

第 4 步完成。`extended-conditions` 的部分情境匯出與重開通過，實際 102 次，收據
`20261007-064346007-4544679edf0b4fe3a1150cd01bdac668`。原來的 17 個情境全部保留，加入兩條完整旅程後共 19 個。
沒有處理下一階段的視窗不搶前景或另一個 Windows 桌面。

第 5 步完成。六份合成 xlsx 放在 `artifacts/review/kct-filter-ops/synthetic-acceptance/`，已確認由 Git 忽略。
人工旗標組有 24 筆、12 張傳票，人工 10 筆、自動 12 筆、空白 2 筆；非季底期末組有 58 筆、29 張傳票，
以 2025-11-30 為期末、5 天窗口，固定答案為 8 筆收入借方、8 張傳票、16 筆同傳票明細。
各組附相符的 TB、科目配對檔與預期答案。產生器和獨立讀取器都核對過平衡、日期型別及預期結果，
並檢視預覽；人工核對步驟寫入本機 README 與 `test-environment-notes.md`。尚未宣稱使用者驗收或私人案件驗證完成。

第 6 步已集中提出三個選項問題：第一步提醒位置、第六步不相容情境的短句、TB 修改後條件篩選報告過期的文件說明。
選項工具回傳未作答，三點維持待確認，不視為使用者接受建議。本輪先保留現有產品行為，繼續已授權的完整驗證。

第 7 步開始：依序執行 Build、Contract Normal、FrontendMapping、FrontendPreview、Public、Documentation、完整 Gui、Package、Excel。
先前單項通過不可沿用為整輪結論。沒有暫存、提交或推送。目前已取得的整輪收據如下，尚未執行完成的項目不預先標成通過。

| 檢查 | 結果 | 收據 |
|:---|:---|:---|
| Build Debug | 通過 | `20261007-065110481-153a6ca03cec431f8f75f5e832022e56` |
| Contract Normal | 通過 | `20261007-065118002-3114aa78de044a619e90c197a1b4facc` |
| Contract FrontendMapping | 371 個測試通過 | `20261007-065120187-d95bfacca8994b1f842048819cd95963` |
| Contract FrontendPreview | 通過 | `20261007-065126432-933facc5bb82442395a226ebc61270f1` |
| Public | .NET 4,758 個測試通過，沒有失敗或略過；另包含前端與預覽檢查 | `20261007-065130280-13ea7aa37dda453993ee2887624e3103` |
| Documentation | 46 個檔案、245 個連結、20 個錨點通過，沒有錯誤或警告；收尾回填後再跑 | `20261007-065457188-6d0cf38ce5684ba9803aec8ecf6611eb` |
| 完整 Gui | 19 個情境通過 | `20261007-065529567-c364d6fcef7c460780ecc3dab3d65067` |
| Package Release | 通過 | `20261007-070058124-4ca056d6876a4deb98d627cccd7c4a76` |
| Excel Release | 六種合成報告與工作檔通過 | `20261007-070132874-e2f42bf7feea45acbe08b7946734b249` |

使用者要求重新詢問未回答的問題後，本輪改用非同步選項，2026-10-07 收到的回覆逐字如下：

- 第一步提醒位置：「維持只在修改表單顯示，文件寫清楚（建議）」
- 第六步不相容情境短句：「維持短句，詳細原因仍由步驟檢查列出」
- TB 修改後的報告過期說明：「額外詳細說明該問題的脈絡，我看不太懂」

前兩項只補文件，不改產品行為。第三項已補充：TB 改動不清情境，但完整性驗證要重跑；
兩種報告都以產生時的驗證版本判定是否過期，因此即使命中不變，條件篩選報告也會標成舊版。
使用者 2026-10-07 接著回覆：「先評估：只改 TB 時，條件篩選報告是否需要標過期」。以下是唯讀評估，尚未授權修改規則。

**只改 TB 對條件篩選報告的影響**

- 計算結果不需要因 TB 改動而失效。`AuditDependencyPolicy` 對 `TbImport` 和 `TbProjection` 只讓完整性驗證失效，
  不讓預篩選或篩選命中失效。`UsageScenarioJourneyTests.TrialBalanceReplacement_PreservesScenariosThenRevalidationAllowsAnotherExport`
  已在本輪 Public 驗證 SQLite 與 DuckDB 的情境、命中保留，以及舊報告過期但檔案不改寫。
- 報告可見內容不直接使用 TB 金額。`LegacyReportWriter.Criteria.cs` 以情境、條件文字、命中計數及總帳的整張傳票明細產生工作表。
  因此，僅 TB 改動而總帳及其他篩選來源不變時，不能把「報告過期」解讀成篩選結果算錯。
- 整份檔案仍有 TB 來源紀錄。`ReportWorkbookMetadataFactory` 讀取當時的 TB 配對，
  `ReportWorkbookMetadataCodec` 把匯入批次、配對時間、欄位配對和金額模式寫入隱藏的 `JET_Metadata` 工作表。
  TB 重匯或重新配對後，舊報告保留的就是舊來源；這不代表舊報告損壞，但它不再完整對應目前案件。
- 現行過期判斷使用驗證版本，不是比較 TB 金額。`ReportExportSupport` 同時核對驗證版本、篩選資料版本、情境版本與位置。
  即使重新驗證後結果相同，舊報告仍因驗證版本不同而過期。`ExportCriteriaSelectionReportHandler` 也要求目前有效的完整性驗證才能匯出。
  前端的第五步、案件摘要和 `Ui.findCurrentReportArtifact` 配合這套判定；只刪掉後端的一個版本比較並不足以改變完整行為。

建議本輪保留報告過期規則，文件寫清楚「情境與命中保留；重新驗證後可用原情境產生對應新 TB 的報告；舊檔不改寫」。
理由是目前「有效報告」代表整份檔案對應目前案件，而不是只有可見的篩選結果相同。這是產品語意的建議，
不是聲稱審計規則要求只改 TB 就必須重算篩選。

另一種可行做法是分開表示「篩選結果仍有效」與「報告保存舊 TB 來源」，並明定未重新驗證時能否匯出、
重新驗證後如何看待舊報告，以及舊版報告如何判定。若使用者選擇這個方向，必須同步處理前後端判定、來源紀錄說明與使用情境測試，
不直接移除驗證版本檢查，也不改寫既有檔案。本輪尚未實作或驗證這個替代方案，等待使用者裁定。

本次評估只讀取程式與既有測試，沒有重跑 Build、Public、Gui、Package 或 Excel，也沒有把替代方案當成已驗證。
補入前兩項裁定及 TB 評估後，Documentation 通過，收據為 `20261007-070838113-8a22681747f141dbae281a24bec48948`。

（2026-10-07 補記：上方第三項說明與本段評估的前提「TB 改動不清情境」，已由下方「TB 修改改成比照 GL」取代。
文中的 `TrialBalanceReplacement_PreservesScenariosThenRevalidationAllowsAnotherExport` 已改名為
`TrialBalanceReplacement_ClearsScenariosThenRequiresResavingBeforeAnotherExport`，改成驗證情境清除。）

**目前完成範圍與下一步（2026-10-07）**

使用者指出前一則回覆只有 TB 評估，沒有交代整份計畫的狀態與下一步。本輪尚未完成全部收尾，不能把指定驗證通過當成計畫結案。

1. 第 1 到第 5 步的修正、補測、Gui 旅程與合成資料準備已完成；合成資料的人工核對仍待使用者執行。
2. 第 6 步已有兩項裁定落入文件。TB 報告過期的評估完成，但是否維持規則尚未裁定，不以未回答當成接受建議。
3. 第 7 步指定的 Build、三種 Contract、Public、完整 Gui、Package 與 Excel 都有通過收據。
   補入裁定與評估後，Documentation 也通過，收據 `20261007-070900177-3a9b39d7f39f4fb58fffbedbd76e1396`。
4. 175 個 Git 候選路徑與目前工作樹重新比對一致，沒有已暫存檔案；提交訊息仍是候選稿，尚未取得使用者確認。
5. 下一步先收取 TB 規則與 Git 候選稿的裁定。若 TB 維持現況，只補交付說明並重跑 Documentation；
   若要求改變行為，先確認新規則，再補首次失敗測試、修改程式及重跑受影響的指定驗證，之後重新列出 Git 清單。
6. 交付時提供兩組合成資料的取用位置和固定答案，逐項列出已知但延後事項。完成上述收尾以前，計畫保持進行中，
   不開始下一階段工作，不暫存、提交或推送。

###### 本輪 Git 清單與提交訊息（2026-10-07，內容已確認，未授權 Git 動作）

本輪候選清單共 189 個明確路徑，其中 154 個已追蹤檔案改動、35 個未追蹤檔案。分類為產品 67 個、測試 88 個、工具 19 個、文件 12 個、Agent 規則 3 個。
清單包含上次推送後累積的改動，不只本 session 修改；本節取代先前的 175 個路徑清單。已逐檔和目前 Git 差異核對，沒有已暫存檔案。
清單已包含 TB 規則與補測收尾的檔案，例如 `TbMutationParityTests.cs`；使用者的確認在該輪收尾之後。
合成工作簿、重新對照檔與收據都留在 Git 忽略的 artifacts。

使用者 2026-10-07 對當時 188 個路徑與下方提交訊息，在選項工具的回覆逐字如下，未授權暫存、提交或推送：

> 確認清單與訊息內容，不授權任何 Git 動作（建議）

之後依提交前核對的裁定，把兩條工作原則寫進 `.agents/harness/development-workflow.md`，清單多出這一個路徑。
使用者 2026-10-07 對 189 個路徑與提交訊息最後一行的回覆逐字如下，同樣未授權暫存、提交或推送：

> 確認清單，最後一行改寫（建議）

經過見下方「提交前的唯讀核對與文件修正（2026-10-07）」。

```text
.agents/harness/development-workflow.md
.github/copilot-instructions.md
AGENTS.md
docs/action-contract-manifest.md
docs/data-and-legacy.md
docs/development-status.md
docs/history/README.md
docs/idea-replacement-scope.md
docs/jet-frontend-description.md
docs/jet-guide.md
docs/kct-gl-workflow.md
docs/project-context.md
docs/README.md
docs/specs/2026-10-06-kct-gl-requirements-plan.md
docs/test-environment-notes.md
src/JET/JET/AgentGuiTestFixtures.cs
src/JET/JET/AgentGuiTestProfile.cs
src/JET/JET/AppCompositionRoot.cs
src/JET/JET/AppCompositionRoot.Repositories.cs
src/JET/JET/Application/Handlers/DevLogHandlers.cs
src/JET/JET/Application/Handlers/ExportCalendarTemplatesHandler.cs
src/JET/JET/Application/Handlers/ExportReportHandlers.cs
src/JET/JET/Application/Handlers/ExportWorkpaperStreamHandler.cs
src/JET/JET/Application/Handlers/Import/ImportAccountMappingHandler.cs
src/JET/JET/Application/Handlers/MappingHandlers.cs
src/JET/JET/Application/Handlers/Project/ProjectUpdateHandler.cs
src/JET/JET/Application/Handlers/Query/QueryAccountMappingDifferencePageHandler.cs
src/JET/JET/Application/Handlers/Query/QueryFilterVoucherHandlers.cs
src/JET/JET/Application/ProjectRepositories.cs
src/JET/JET/Application/Support/FilterConditionRenderer.cs
src/JET/JET/Application/Support/FilterPopulationScopeParser.cs
src/JET/JET/Application/Support/ProjectWorkFileWriter.cs
src/JET/JET/Application/Support/ReportExportSupport.cs
src/JET/JET/Application/Support/RuleLogicVersions.cs
src/JET/JET/Application/Support/WorkflowResultStateSupport.cs
src/JET/JET/AuditCore/IntakeMappingProgram.cs
src/JET/JET/AuditCore/WorkpaperProgram.cs
src/JET/JET/Domain/Abstractions/AccountMappingDifferenceRepository.cs
src/JET/JET/Domain/ActionExecutionPolicy.cs
src/JET/JET/Domain/Contracts/AccountMappingContracts.cs
src/JET/JET/Domain/Contracts/ICalendarTemplateSource.cs
src/JET/JET/Domain/Contracts/ImportContracts.cs
src/JET/JET/Domain/Contracts/RuleRunContracts.cs
src/JET/JET/Domain/Contracts/WorkpaperReferenceContracts.cs
src/JET/JET/Domain/Rules/AccountMappingColumnMatcher.cs
src/JET/JET/Domain/Rules/AuditDependencyPolicy.cs
src/JET/JET/Domain/Rules/QuarterEndWindows.cs
src/JET/JET/Infrastructure/Diagnostics/SupportRingBufferLoggerProvider.cs
src/JET/JET/Infrastructure/Export/CalendarTemplateSource.cs
src/JET/JET/Infrastructure/Export/WorkpaperWriter.Step2To41.cs
src/JET/JET/Infrastructure/FileIO/OpenXmlSaxTableReader.cs
src/JET/JET/Infrastructure/Persistence/AccountMappingDifferenceQuery.cs
src/JET/JET/Infrastructure/Persistence/AccountMappingEditorRepository.cs
src/JET/JET/Infrastructure/Persistence/AccountMappingPopulationQuery.cs
src/JET/JET/Infrastructure/Persistence/DataPreviewColumns.cs
src/JET/JET/Infrastructure/Persistence/DuckDb/DuckDbProjectDatabase.cs
src/JET/JET/Infrastructure/Persistence/Local/LocalAccountMappingDifferenceRepository.cs
src/JET/JET/Infrastructure/Persistence/Local/LocalAccountMappingExportRepository.cs
src/JET/JET/Infrastructure/Persistence/Local/LocalAccountTaxonomyStore.cs
src/JET/JET/Infrastructure/Persistence/Local/LocalDataPreviewRepository.cs
src/JET/JET/Infrastructure/Persistence/Local/LocalResultStaleStateStore.cs
src/JET/JET/Infrastructure/Persistence/Sqlite/SqliteProjectDatabase.cs
src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerAccountMappingDifferenceRepository.cs
src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerAccountMappingExportRepository.cs
src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerAccountTaxonomyStore.cs
src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerDataPreviewRepository.cs
src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerProjectDatabase.Schema.cs
src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerResultStaleStateStore.cs
src/JET/JET/Infrastructure/Sql/AccountClassificationMigration.cs
src/JET/JET/Infrastructure/Sql/ResultStaleStateSql.cs
src/JET/JET/Infrastructure/Sql/RuleRunResultReset.cs
src/JET/JET/wwwroot/js/app.js
src/JET/JET/wwwroot/js/data-preview.js
src/JET/JET/wwwroot/js/filter-values.js
src/JET/JET/wwwroot/js/jet-api.js
src/JET/JET/wwwroot/js/state.js
src/JET/JET/wwwroot/js/steps/create-step.js
src/JET/JET/wwwroot/js/steps/filter-step.js
src/JET/JET/wwwroot/js/steps/import-step.js
src/JET/JET/wwwroot/js/steps/mapping-step.js
src/JET/JET/wwwroot/js/steps/validate-step.js
src/JET/JET/wwwroot/js/ui-core.js
src/JET/tests/JET.Tests/Application/AccountMappingImportSummaryTests.cs
src/JET/tests/JET.Tests/Application/AccountMappingTemplateExportTests.cs
src/JET/tests/JET.Tests/Application/AccountTaxonomySaveHandlerTests.cs
src/JET/tests/JET.Tests/Application/ActionSerializationTests.cs
src/JET/tests/JET.Tests/Application/AdvancedFilterAstContractTests.cs
src/JET/tests/JET.Tests/Application/AuthorizedPreparerWorkflowTests.cs
src/JET/tests/JET.Tests/Application/Batch4ProjectUpdateTests.cs
src/JET/tests/JET.Tests/Application/Batch8PreparationDateReadbackTests.cs
src/JET/tests/JET.Tests/Application/Batch9ArtifactStateTests.cs
src/JET/tests/JET.Tests/Application/Batch9MutationStateTests.cs
src/JET/tests/JET.Tests/Application/CalendarTemplateRoundTripTests.cs
src/JET/tests/JET.Tests/Application/CaseHandoverPreparedByTests.cs
src/JET/tests/JET.Tests/Application/CompletenessBackendGateProviderTests.cs
src/JET/tests/JET.Tests/Application/EmptyReportStateTestData.cs
src/JET/tests/JET.Tests/Application/ExportCalendarTemplatesHandlerTests.cs
src/JET/tests/JET.Tests/Application/ExportWorkpaperTypedSeamTests.cs
src/JET/tests/JET.Tests/Application/FilterConditionRendererTests.cs
src/JET/tests/JET.Tests/Application/FilterHitsPageTests.cs
src/JET/tests/JET.Tests/Application/FilterQueryFreshnessTests.cs
src/JET/tests/JET.Tests/Application/FilterResultCursorRevisionTests.cs
src/JET/tests/JET.Tests/Application/FilterRunMaterializeServiceGateTests.cs
src/JET/tests/JET.Tests/Application/ImportAccountMappingHandlerTests.cs
src/JET/tests/JET.Tests/Application/IndependentWorkpaperWorkflowTests.cs
src/JET/tests/JET.Tests/Application/InlineWorkbookProject.cs
src/JET/tests/JET.Tests/Application/KctPrerequisiteProviderTests.cs
src/JET/tests/JET.Tests/Application/PrescreenOptionalSourceWorkflowTests.cs
src/JET/tests/JET.Tests/Application/ProjectFolderPortabilityTests.cs
src/JET/tests/JET.Tests/Application/QueryDataPreviewHandlerTests.cs
src/JET/tests/JET.Tests/Application/ResultInvalidationNonWorkingDaysTests.cs
src/JET/tests/JET.Tests/Application/ResultInvalidationProjectionAndFilterTests.cs
src/JET/tests/JET.Tests/Application/ResultInvalidationTestSupport.cs
src/JET/tests/JET.Tests/Application/ResultInvalidationUpstreamRewriteTests.cs
src/JET/tests/JET.Tests/Application/RuleLogicVersionsTests.cs
src/JET/tests/JET.Tests/Application/SavedScenarioReplay.cs
src/JET/tests/JET.Tests/Application/SelectedScenarioWorkpaperTests.cs
src/JET/tests/JET.Tests/Application/SupportLogUnavailableProjectTests.cs
src/JET/tests/JET.Tests/Application/TagMatrixRowPageTests.cs
src/JET/tests/JET.Tests/Application/TagMatrixScenariosTests.cs
src/JET/tests/JET.Tests/Application/TagMatrixVoucherPageTests.cs
src/JET/tests/JET.Tests/Application/TbMutationParityTests.cs
src/JET/tests/JET.Tests/Application/TypedFieldFilterContractTests.cs
src/JET/tests/JET.Tests/Application/UpstreamMutationClearsFilterScenariosTests.cs
src/JET/tests/JET.Tests/Application/UsageScenarioJourneyTests.cs
src/JET/tests/JET.Tests/Application/UsageScenarioMutationRollbackTests.cs
src/JET/tests/JET.Tests/Application/UsageScenarioProjectBoundaryTests.cs
src/JET/tests/JET.Tests/Application/UsageScenarioRetryJourneyTests.cs
src/JET/tests/JET.Tests/Application/WorkpaperCancellationFileDiscoveryTests.cs
src/JET/tests/JET.Tests/Application/WorkpaperExportCancellationTests.cs
src/JET/tests/JET.Tests/Application/WorkpaperExportCapacityJourneyTests.cs
src/JET/tests/JET.Tests/Application/WorkpaperExportRequestValidationTests.cs
src/JET/tests/JET.Tests/Application/WorkpaperExportTestSupport.cs
src/JET/tests/JET.Tests/Application/WorkpaperStep41ProviderParityTests.cs
src/JET/tests/JET.Tests/Architecture/AccountTaxonomyFrontendTests.cs
src/JET/tests/JET.Tests/Architecture/ActionContractRuleVersionTests.cs
src/JET/tests/JET.Tests/Architecture/DataPreviewColumnOwnershipTests.cs
src/JET/tests/JET.Tests/Architecture/FilterAstFrontendContractTests.cs
src/JET/tests/JET.Tests/Architecture/FilterKctMetadataFrontendTests.cs
src/JET/tests/JET.Tests/Architecture/FilterKctNonBusinessDayFrontendTests.cs
src/JET/tests/JET.Tests/Architecture/FilterTemplatesFrontendTests.cs
src/JET/tests/JET.Tests/Architecture/FrontendUsabilityContractTests.cs
src/JET/tests/JET.Tests/Architecture/GuiScenarioCoverageTests.cs
src/JET/tests/JET.Tests/Architecture/HandlerRepositoryScopeTests.cs
src/JET/tests/JET.Tests/Architecture/MappingProjectionPolicyFrontendTests.cs
src/JET/tests/JET.Tests/Architecture/QuarterEndWindowsFrontendParityTests.cs
src/JET/tests/JET.Tests/Architecture/ReportExportOwnershipTests.cs
src/JET/tests/JET.Tests/Architecture/ScreenWordingReviewFrontendTests.cs
src/JET/tests/JET.Tests/Architecture/SupportedActionsParityTests.cs
src/JET/tests/JET.Tests/Architecture/TerminologyUnificationFrontendTests.cs
src/JET/tests/JET.Tests/Architecture/WorkflowControlsAccessibilityFrontendTests.cs
src/JET/tests/JET.Tests/AuditCore/IntakeMappingProgramTests.cs
src/JET/tests/JET.Tests/AuditCore/WorkpaperProgramTests.cs
src/JET/tests/JET.Tests/Domain/AccountMappingColumnResolverTests.cs
src/JET/tests/JET.Tests/Domain/AuditDependencyPolicyTests.cs
src/JET/tests/JET.Tests/Domain/QuarterEndWindowsTests.cs
src/JET/tests/JET.Tests/Infrastructure/BinaryExcelTableReaderTests.cs
src/JET/tests/JET.Tests/Infrastructure/FilterCompletenessTests.cs
src/JET/tests/JET.Tests/Infrastructure/KctFilterPredicateTests.cs
src/JET/tests/JET.Tests/Infrastructure/LegacyWorkbookContentComparison.cs
src/JET/tests/JET.Tests/Infrastructure/OpenXmlSaxTableReaderTests.cs
src/JET/tests/JET.Tests/Infrastructure/PrivateCaseReportComparator.cs
src/JET/tests/JET.Tests/Infrastructure/PrivateCaseReportComparatorTests.cs
src/JET/tests/JET.Tests/Infrastructure/PrivateCaseReportDifferencePolicy.cs
src/JET/tests/JET.Tests/Infrastructure/ResultStaleStateLifecycleTests.cs
src/JET/tests/JET.Tests/Infrastructure/SchemaV7MigrationTests.cs
src/JET/tests/JET.Tests/Infrastructure/SqlServerDataPreviewRepositoryTests.cs
src/JET/tests/JET.Tests/Infrastructure/SupportRingBufferLoggerProviderTests.cs
src/JET/tests/JET.Tests/Infrastructure/WorkpaperStep41LegacyBaselineBenchmarkTests.cs
src/JET/tests/JET.Tests/JET.Tests.csproj
tools/harness/documentation-check.json
tools/harness/gui-driver/DriverContracts.cs
tools/harness/gui-driver/GuiCaseToWorkpaperScenario.cs
tools/harness/gui-driver/GuiDataRecoveryScenarios.cs
tools/harness/gui-driver/GuiFeedbackWorkflowScenario.cs
tools/harness/gui-driver/GuiFilterWorkflowScenarios.cs
tools/harness/gui-driver/GuiLegacyFormWorkflow.cs
tools/harness/gui-driver/GuiScenarios.cs
tools/harness/gui-driver/GuiSideMonthWorkflow.cs
tools/harness/gui-driver/GuiUpstreamChangeScenario.cs
tools/harness/gui-driver/GuiWorkpaperInspection.cs
tools/harness/gui-driver/OwnedGuiRun.cs
tools/harness/JetHarness.psm1
tools/harness/lanes.json
tools/README.md
tools/tests/fixtures/quarter-end-windows.json
tools/tests/frontend-mapping.test.cjs
tools/tests/frontend-workflow.test.cjs
tools/tests/verify-contract.tests.ps1
```

提交訊息候選稿如下，不加署名：

```text
完善 KCT 條件、科目配對與篩選操作流程

- 調整 KCT A、B 條件，補上 C 的說明與 A 到 E 的預設動機，納入非季底查核期末。
- 新增傳票建立日快捷、假日與補班日範本、值清單計數及日期選擇；欄位選單加註來源欄。
- 人工旗標在資料預覽與底稿顯示為「人工」「自動」。
- 科目配對匯入補差異清單，修正空白科目與 DuckDB 差異查詢；舊情境全部符合新規則才整批改版。
- 整理情境命名與儲存；總帳與試算表修改採同一失效規則，上游修改清除全部情境，分類不再因情境引用而擋刪除。
- 財報準備日存檔失敗時保留情境與結果，移除已無作用的舊升級與返回配對程式。
- 修正無法載入案件時的診斷日誌匯出，補齊使用情境測試、Gui 旅程與合成資料交付說明。
- 更新專案背景、提交訊息規範與 Agent 共用工作原則，說明正式發布前的案件可刪除重建。
```

最後一行原本是「更新專案背景與提交訊息規範，說明正式發布前的案件可刪除重建。」；多出
`.agents/harness/development-workflow.md` 後，依使用者 2026-10-07 的選擇改寫。

###### 使用者對 Codex 回報的裁定（2026-10-07）

使用者看過 Codex 的兩則回報後，請 Claude 核對進度並寫一份給 Codex 的修正指示。Claude 核對計畫紀錄、驗證收據與工作樹後，結論如下：
- 七步接續的程式修改、補測、Gui 旅程與合成資料都已完成。最後一次修改程式在完整驗證之前，所以那次完整驗證涵蓋最後的程式碼。
- 還沒收尾的是使用情境測試。盤點表沒有逐列更新，看不出每個情境現在由哪個測試負責。
  交接清單本身漏列了 G-L1、G-L3，也沒有把下面的 PBC 那一列列入要問的事。

使用者 2026-10-07 在 Claude Code 視窗的選項中回答，逐字如下：
- 補測試、驗證和收尾由哪一邊執行：「交給 Codex 的 ultra 模式」
- 只改 TB 時，條件篩選報告是否維持標成舊版：「改成篩選結果和報告分開判斷」
- 開發現況「2026-09-14 原始 PBC 匯入失敗」那一列，和已解決的特定文字檔匯入失敗是否同一件事：「是同一件事，一起結案」

Claude 已把 PBC 那一列從 `docs/development-status.md` 的未完成項目移除，並在現況段落記下這兩項裁定。

Claude 也以唯讀方式重新對照 120 個使用情境：8 組代理分頭對照現行測試，每組再由另一個代理反過來核對，
另有一個代理比對現行文件，找出盤點表漏列的情境。結果是 94 個情境已有會執行的行為測試，26 個只測到一部分或沒有測試；
漏列的情境有 33 項，其中 20 項已有測試、9 項依裁定排除、4 項是缺口，TB 規則就是其中一項。
逐列結果、測試草圖與預期值出處寫在本機的 `artifacts/review/kct-filter-ops/usage-scenario-remap-20261007.md`，不進 Git。
這次對照沒有執行建置或測試。

接續由 Codex 處理。Claude 之後不再修改這個工作樹，避免兩邊同時改檔。TB 規則的做法已由下一節取代，接續順序以下一節為準。

###### TB 修改改成比照 GL（2026-10-07）

使用者交給 Codex 之前，要求先和 Claude 討論「使用者修改 TB 資料後，是否要維持已經建立好的篩選情境」。
Claude 先說明現況：篩選條件與命中只讀總帳，改 TB 前後命中相同；TB 只影響資料驗證的逐科目比對與底稿的完整性工作表；
改 TB 後驗證失效，第五、六步要重跑驗證才能進入。接著列出「保留情境」與「比照其他上游修改一律清除」兩種做法，Claude 建議保留。
使用者 2026-10-07 在 Claude Code 視窗的選項中回答，逐字如下：

- 使用者修改 TB（重新匯入試算表或重新確認 TB 配對）後，已經建立好的篩選情境要怎麼處理：

  ```text
  我會傾向方案B，因為關卡查驗應該視階段步驟來分類，也就是說，儘管篩選條件本質是在篩選傳票分錄，但由於資料修改是屬於上游層級的範疇，為了避免系統流程交錯複雜，應當直接視資料改動為同一個層級的事件，所以改動TB應當視同改動GL，而改動GL視同改動TB，這樣才能確保資料真正的完整性，避免針對個案而又新增功能，最後導致 agent 混淆不同流程與功能間的意義，甚至因為誤解而偏離系統開發的邊界。另外一提，這類的問題應該已經發生在其他代碼中，而這類問題我也有請你在前面提出解決方案過，也就是務必以審計業務及使用者體驗為基準去評估是否有部分代碼是因為 agent 針對當下的個案而寫的技術債?
  ```

- 如果保留情境，第二、三步的提醒要怎麼處理：「不適用」
- 為當下個案寫的技術債盤點要放在哪個階段：「列入下一階段」

依此裁定：
- 修改 TB 與修改 GL 是同一層級的事件。重新匯入 TB 或重新確認 TB 配對時，資料驗證、預篩選與篩選命中都失效，
  同一筆交易清除全部已存情境，第五步回到預設狀態，提醒與訊息沿用其他上游修改的同一套做法。
  資料庫內的修改失敗時整筆回復，情境照舊保留。
- 先前「改成篩選結果和報告分開判斷」的裁定不再適用。兩種報告經由既有規則一起標成過期，已匯出的檔案不改寫，不新增分開判斷的機制。
- 實作只改唯一的失效政策 `AuditDependencyPolicy`，讓 TB 的兩種修改和 GL 走同一條規則，不在其他地方為 TB 另開例外。
  同步核對 TB 相關 action 回傳的 `invalidatedResults`、前端是否把回應交給既有的清除處理、`jet-guide.md` 第 6 節
  「試算表的匯入與配對確認不影響篩選，不清除」那句、`action-contract-manifest.md`、`jet-frontend-description.md` 與測試環境交付說明。
- 以「TB 修改保留情境」為前提的既有測試與 Gui 段落，要先保留第一次失敗，再改成新行為並寫明原因，例如
  `AuditDependencyPolicyTests`、`UpstreamMutationClearsFilterScenariosTests` 的保留清單、`UsageScenarioJourneyTests` 的 TB 旅程、
  前端 TB 匯入與確認配對的測試，以及 `upstream-change-after-export` 的 TB 段落。
- 技術債盤點已寫進 `docs/development-status.md`「下一階段工作計畫要納入的事項」，本計畫不處理。

接續順序：
1. TB 比照 GL：先寫會失敗的測試，再改失效政策、前端接線與文件，既有測試依上一點處理。
2. 補 `artifacts/review/kct-filter-ops/usage-scenario-remap-20261007.md` 裡 26 個情境與 3 項漏列情境的測試。
   和 TB 有關的缺口改依新規則設計；新測試第一次就通過只算補上涵蓋，抓到產品問題先保留失敗收據再修。
3. 依改到的檔案跑驗證。這輪會改產品程式，所以要跑 Build、三種 Contract、Public、Documentation、完整 Gui（先告知使用者）、
   Package 與 Excel。之後重新定稿 Git 清單與提交訊息，請使用者確認。

###### TB 規則與使用情境補測收尾（2026-10-07）

本輪依使用者要求接續到可交付狀態。先前七步接續的完整驗證保留為當時的結果，不為舊修改重跑。
本輪會改 TB 失效政策，完成後依序執行 Build、三種 Contract、Public、Documentation、完整 Gui、Package 與 Excel。
Gui 只在所有修改完成後執行完整路線，執行前告知會跳出視窗。PrivateCase 與 Provider 不執行，SQL Server 保持停止。
沒有暫存、提交或推送。開始時 `git status` 為 149 個已追蹤檔案改動與 26 個未追蹤項目，既有內容全部保留。

使用者本輪的設計原則逐字如下：

> 同一步驟層級的資料修改，視為同一層級的事件，用同一套處理。不要因為某個輸入沒被用到，就替它另開例外或新增功能。補測或修改時，如果發現需要為單一情況另寫特殊邏輯才能過，先停下來問我。

分工：後端 TB 新測試、其他後端缺口與前端缺口平行準備；主代理處理失效政策、Gui、文件、計畫與 Git 清單。
兩個前端測試檔由同一代理寫入，所有正式驗證由主代理依序執行。完成後另由代理反向核對需求、斷言與例外。

既有測試變更原因：TB 保留情境的預期已由本節前的使用者裁定取代。先以新測試保存政策未改前的失敗，
再修改唯一政策，保存既有後端測試的舊預期失敗，最後同步前端假回應和 Gui 旅程；不刪除失敗回復、控制總額、
其他事件、報告過期或原檔保留的斷言。Gui 的舊 TB 保留前提使用同一政策失敗證據，不在完成修改前另跑 Gui。

`WorkpaperExportCancellationTests` 仍使用不符合時間尾碼的 `*_WorkingPaper.xlsx`，會讓未產檔斷言空泛通過。
本輪先補固定合成檔名反例並保存失敗，再改用既有 `FindWorkpapers`；取消階段、暫存檔與舊報告保留的斷言不變。
檔名反例失敗收據為 `20261007-081855076-65ce597123cd42cfbb22cd708af5426e`，舊搜尋確實找不到有時間尾碼的合成檔。
詳細補測對照與收據寫回本機 `artifacts/review/kct-filter-ops/usage-scenario-remap-20261007.md`。

TB 新測試首次失敗收據為 `20261007-082053783-19b83716af8c44478af22300b8a3d95c`：兩個政策案例與
兩種資料庫共六條匯入、配對旅程失敗，四條失敗回復旅程通過。舊政策沒有清除預篩選、篩選命中與情境。
接著將 `AuditDependencyPolicy` 的 GL、TB 匯入與配對確認合併為同一分支；其他產品邏輯不加 TB 例外。
共用執行點的註解同步更新。完成與未完成項目依實際收據更新，不將新增測試直接視為已通過。

政策修改後，既有 TB 舊預期的失敗已分別保存：`AuditDependencyPolicyTests` 兩例
（`20261007-082207767-ff2b1ab2f3464df9a28f4cede5494c0e`）、`UpstreamMutationClearsFilterScenariosTests` 五例
（`20261007-082246139-080a0c47e3014118b29c1e2e96219ac9`）、`UsageScenarioJourneyTests` 兩例
（`20261007-082314376-8ead6523970a45f7b5d0e1fc7d591306`）、`Batch9MutationStateTests` 四例
（`20261007-082351239-c7263dd8a3664c7e840fcb693d2b6493`）、`ResultInvalidation` 八例
（`20261007-082414896-bff895834fb7451d8e71d01a4cee4a22`）。這些測試已改依新裁定，TB 從保留清單移到清除清單，
原有旅程保留並加上「重新驗證仍不能直接匯出，必須重新儲存情境」的檢查。
前端的 TB 假回應原本自行給 false，不會因後端改動自然失敗；依同一政策失敗證據改成正式回應的四個 true，
不捏造前端或 Gui 曾執行失敗。Gui 的 TB 段落改為清除、重新驗證、重新設定情境後匯出；操作增加六次。
窄版補測移除寬度大於門檻就略過的判斷，增加一次調整視窗，實際核對工作區不足 660px 時只有一欄。
空白案件到匯出的旅程，補上匯出後與重開後左側進度皆為 6/6。全部 Gui 待最後一起執行。

交接帳號的測試草圖要求底稿也顯示 `Prepared by`，但現行 Working Paper 沒有這個欄位，也沒有另存建案者的底稿中繼資料。
向使用者說明差異後，2026-10-07 回覆逐字為：「請維持底稿版面，不要擅自更改底稿樣式」。
因此不新增底稿欄位；測試核對既有報告的 `Prepared by` 使用建案者，交接後底稿仍正常產出，案件的 `operatorId` 保持建案者。
規格已寫清楚這個範圍，不把底稿沒有的欄位當成已驗證。

補測 S1-15 找到產品問題：`support.log.export` 先透過 `IProjectStore.FindAsync` 解析案件設定，
因此案件設定損壞造成載入失敗時，支援日誌也無法匯出。兩種資料庫的首次失敗已保留在
`20261007-082801786-fe8fabaea1c241e89585482e6ff4f837`。依現行 picker 與日誌契約修正共用診斷匯出，
不為壞日期另加例外；合法案件目錄的檢查與去識別規則仍保留。

同一份收據中另有 11 個新測試夾具問題：缺期間時根目錄尚未建立、測試故障約束妨礙後續讀回，
以及誤要求內部錯誤文字出現在已包裝的回應。容量測試也曾把「再重新匯出」誤判為沒有重試指引
（`20261007-082902510-149455e17e27414b918f15ce2da8ed22`）。以上修正測試夾具與語意斷言，不列為產品修正。
前端兩次診斷失敗均為合成 DOM、假摘要或選取器問題，修正後 411 個測試通過
（`20261007-083138284-6230560eeb9a48d9a46f92195ab99ddb`）；其中新增 40 個行為測試。
新後端測試最初另有一次把列舉值當成陣列的編譯錯誤，測試未執行，不算產品首次失敗。

日誌修正另先新增 32 個案例，首次 20 例失敗，收據為 `20261007-083535958-0195388bf0c845d8a49f7a1ce1a98765`。
除了不可載入案件，測試也發現從設定檔內的案件 ID 重新定位會繞過原有連結目錄檢查。
兩個診斷 handler 已一起移除設定檔解析前提，使用請求的 ID，既有 writer 保留合法 ID、目錄存在與連結拒絕。
修正後這 32 例通過（`20261007-083714666-99aa5884b39a43bfa1006cf9bb2792be`），
S1-01 與 S1-15 六例通過（`20261007-083731653-ec713200f00d4c2a9d061a0bdfd57129`），
既有日誌 handler 與 writer 檢查也通過。沒有新 action，也沒有特定壞日期的例外。

交叉複核已完成：TB 作者檢查其餘前後端補測，另一個後端代理檢查 TB 政策、舊測試、四份文件與 Gui 修改，
並複核日誌修正。全頁文字造成的弱斷言已改成指定操作區，多組條件替換及必填回填也補上檢查。
未發現剩餘的斷言放寬或單一情境特例；這是程式複核，不取代接下來的完整驗證。

最後一輪 Public 首次執行共 4,837 例，其中四例仍使用 TB 保留預篩選的舊預期，失敗收據
`20261007-084045226-312c261363114316b103196036a8e8ce`。修改原因同前述使用者裁定：
`IntakeMappingProgramTests` 的 TB append 改驗四種結果失效；`ResultInvalidationUpstreamRewriteTests` 的
TB 重匯改驗驗證與預篩選一起清除；`CompletenessBackendGateProviderTests` 的兩種本機資料庫改驗
預篩選為空，仍保留缺有效驗證時拒絕匯出的原檢查。共用方法的 SQL Server 測試名稱同步，但未連線執行。
本次不改產品，完成測試預期同步後重跑完整 Public。Gui 尚未開始，不會在修改未完成前另跑單項。

同步後 Build、三種 Contract、Public 與 Documentation 通過，Public 為 4,837 例，沒有失敗或略過。
完整 Gui 的首次收據為 `20261007-085008401-cbdb5ed3742d4add83fdfe3b8818a9ed`，前六個情境通過，
`filter-auditor-journey` 在第六次操作後等待逾時。新加的窄版欄數檢查已通過；依操作順序，停在既有新增條件後的焦點與可見位置檢查。
原探針沒有記錄作用中元素與條件列位置，尚不能判定原因，不能把它記成產品首次失敗或放寬斷言。
本輪只補這些診斷欄位，不改原斷言、時間上限或操作預算。使用者原要求完整 Gui 一次，因此已用選項詢問是否允許追加單項診斷與完整重跑，
或先由使用者人工確認畫面；未回答前不重跑 Gui，先完成 Package 與 Excel。

使用者 2026-10-07 回覆逐字為：「允許先單項診斷，修正後重跑完整 Gui（建議，會再次跳出視窗）」。
Package 與 Excel 已完成並通過；接著依授權單跑 `filter-auditor-journey`，增加條件是否加入、選單狀態與焦點位置的診斷。
原有斷言與時間上限保持不變。

單項診斷通過，收據 `20261007-085903933-9c013b667449455491d31274976af6c3`，實際 100 次操作，
包含新加的兩處單欄版面檢查。首次失敗未重現，現有證據不能確認根因；沒有為此修改產品、增加等待時間或放寬斷言。
之後依使用者授權重跑完整 Gui。最後 Public 涵蓋本輪產品程式；其後僅增加 Gui 診斷欄位，完整 Gui 另驗證修改後的驅動程式。

第二次完整 Gui 的 `filter-auditor-journey` 已通過，之後在既有 `feedback-workflow` 的科目拖曳選取檢查失敗，
收據 `20261007-090047718-fe1bfce55bca4658ac85c16e15d8f728`。依同一次追加診斷授權補記實際選取科目、
選取數字、儲存按鈕與表格捲動位置，再單跑該情境；原拖選及儲存限制斷言不變。

`feedback-workflow` 單項診斷通過，收據 `20261007-090506197-11861065b5af4474abe5a4da53b56c5f`。
這次同樣未重現先前失敗，沒有改產品或降低檢查要求，兩次偶發失敗的原因均未確認；再執行完整 Gui，單項收據不當作整體通過。

###### 本輪交付前逐項核對（2026-10-07）

本輪程式與測試修改已完成，下面各項結果都有本次執行證據。文件與本機對照回填後再跑 Documentation，
不改產品，也不以較早的 Public 或 Gui 代替本輪結果。

- Build Debug：`20261007-084531563-66299dd7760d4102a82a252d7c5bd13e`。
- Contract Normal：`20261007-084537826-310253353da343939d0cdbc1cacc7275`。
- Contract FrontendMapping：`20261007-084540041-3c87b205e1db492f80a0e1da4481ba98`，411 個測試通過。
- Contract FrontendPreview：`20261007-084547025-e40425955b00434798a64cfb711ff15c`。
- Public：`20261007-084550862-459b300bdd59498f8b65db616680793b`，4,837 個 .NET 測試通過，沒有失敗或略過，另包含前端與預覽檢查。
- Documentation：`20261007-091233740-5cd5dc17ce0f489a87a838607ec76378`，計畫與開發現況回填後通過；收據文字補入後再核對一次。
- 完整 Gui：`20261007-090620281-8be8a4d81d424cd6811e68c12e5529b3`，19 個情境通過；TB 旅程 45 次、窄版旅程 100 次、空白案件旅程 51 次。
- Package Release：`20261007-085629472-992a89fe62a5497f86d11fae1b9f5317`。
- Excel Release：`20261007-085700874-24fec128520f48c2951d47d9a35e6d12`，既有合成工作簿與原生來源檢查通過。

- 完成並附收據：TB 唯一政策、action 四個失效值、前端清除與一次訊息、失敗整筆回復、兩種報告過期及原檔保留，由上述 Public、FrontendMapping 與完整 Gui 驗證。
- 完成並附收據：26 個主清單缺口與 3 個漏列情境逐項回填實際測試與收據，TB 的三個舊主清單情境及漏列報告項也改依新規則。原盤點另存本機快照，不再把測試草圖當成完成證據。
- 完成並附收據：G-L3 的 4,096 筆清單已補 action 與前端行為。G-L1 的舊表 A 到 U 已由原有 legacy-form-catalog 完整 Gui 覆蓋；搬到前端是成本優化建議，不是缺口，本輪保留該情境。
- 完成並附收據：使用者已裁定 PBC 那一列結案，測試環境說明中仍稱未確認的舊句已同步移除；底稿維持版面的裁定已寫回規格。
- 完成並附收據：後端、前端、TB 與日誌修正已交叉複核，最後 Public 找到的四個舊 TB 預期也另由未修改者複核，沒有剩餘的斷言放寬或產品特例。
- 等使用者決定：兩組合成資料已備妥並補上 TB 接續核對，請人工操作後回覆是否接受結果。位置是 `artifacts/review/kct-filter-ops/synthetic-acceptance/README.md`。人工旗標應為人工 10 筆、自動 12 筆、空白 2 筆；非季底期末 A 條件應命中 8 筆收入借方、8 張傳票、16 筆同傳票明細。尚未收到人工驗收結果。
- 完成並附收據：上方 188 個 Git 路徑與提交訊息已在 2026-10-07 取得使用者內容確認，原話保存在該節；驗證收據如上，沒有新增 Git 執行授權。
- 依裁定延後：`development-status.md` 的下一階段事項全部保持原範圍，包括技術債盤點、私人人工旗標驗證、舊排除區、GUI 不搶前景與其他已列事項。SQL Server 近期不處理；期末後分錄另行延後。

本輪未執行 PrivateCase、Provider、SQL Server 或真實資料驗收，沒有重新掃描 KCT 原始簡報或私人資料。
SQL Server 服務維持停止，沒有 sqlservr.exe。沒有暫存、提交、推送，也沒有把人工驗收當成已完成。

###### 提交前的唯讀核對與文件修正（2026-10-07）

使用者看過 Codex 的收尾回報後，在 Claude Code 視窗提出下列問題，逐字如下：

```text
基於 codex 的回覆及處理結果，我要你幫我基於當前 session 的上下文脈絡並整理專案狀態，請問是否當前的工作計劃都已經處理完畢? 是否所有我提出過的意見和需求都有被列到工作計畫中? 是否有缺失的事項沒有被記錄於專案文件? 是否我可以直接基於 codex 的處理結果做 commit+push 並在測試環境驗收? KCT小組提出的執行步驟是否有確實記錄於專案文件? KCT小組預期優化的事項是否於當前工作計畫中處理好了?
```

Claude 以唯讀方式核對，另派兩個子代理：一個逐則比對 2026-10-06 起 Claude 與 Codex 對話裡的使用者訊息，
另一個核對 KCT 流程文件、15 項優化與 TB 規則的落實。結果如下：

- 程式與測試已完成，最後一輪驗證涵蓋目前的程式碼。最後一次 Public 之後只改過兩個 Gui 驅動檔的診斷欄位，
  完整 Gui 在其後執行。其中 `GuiFilterWorkflowScenarios.cs` 也會被兩個架構測試讀取，所以補跑
  Focused `JET.Tests.Architecture`，447 個測試通過，收據 `20261007-092852918-a768ad1d67e042b795253dd40245e57f`。
- 當時 188 個 Git 路徑和工作樹一致，沒有已暫存的檔案，改動的檔案裡沒有私人路徑。
- KCT 的作業步驟與條件 A 到 E 已記在 `kct-gl-workflow.md`。15 項優化都找得到程式與測試，或已有裁定。
- 文件問題：
  - 落實核對表第 3 項，以及第四節、上游清除做法和 TB 評估段，仍寫舊的 TB 規則。
  - `docs/README.md` 的計畫狀態停在 17 個 Gui 情境與交給新 session 接續。
  - 開發現況沒記 TB 規則對 SQL Server 的影響；「已知但延後的事項」的重啟條件，也沒反映 2026-10-07 併入下一階段的裁定。
  - Gui 兩次偶發失敗只記在本計畫。
  - `kct-gl-workflow.md` 說它記錄了期望優化的項目，但沒有列出 15 項。
- 沒有記錄或沒有處理的使用者需求：
  - Codex 對話裡的架構圖需求，沒有寫進任何文件。
  - 「收尾前的三項工作」寫明要先問使用者的 Gui 斷言縮減與架構測試降級，一直沒有詢問。
  - 「問題用選項問」與「測試依完整使用情境設計」兩條原則只在本計畫與 Claude 的記憶裡，前一條在本計畫原本只有改寫。
- 小差異：兩份文件列出會清除情境的修改時，少了每週非工作日；情境名稱是否區分大小寫，現行文件沒有寫；
  `filter-step.js` 第 106 到 107 行的註解仍寫「已存定義沒變」「TB 不在此集合」。

使用者 2026-10-07 在 Claude Code 視窗的選項中回答，逐字如下：

- 提交前要不要先修正這些文件問題？由誰來修？：「Claude 現在修（建議）」
- 下面兩件事目前沒寫進文件，要列入下一階段的工作計畫嗎？可以複選。：「架構圖需求,Gui 測試縮減」
- 「有問題用視窗內的選項問」和「測試依完整使用情境設計」這兩條原則，要寫進 Claude 和 Codex 共用的 Agent 規則嗎？：「這次就寫進去」
- KCT 流程文件第 159 行有兩個人員編號範例，用來說明舊工具包含比對的問題，會隨提交推送出去。要保留嗎？：「保留（建議）」

架構圖需求是使用者 2026-10-07 在 Codex 對話提出的，最後一則逐字如下：

````text
你同時提出了多個方法，請依照我的需求來建議:
```plaintext
我希望可以像是 archify 那樣透過簡單的指示讓 agent 可以根據當前專案的系統來生成對應的規格文件，只不過目前 archify 給的圖表不夠詳細，所以我需要請你研究可以產出更詳細圖表的方法，至少會希望可以有:
- c4 model (system context/container)
- data flow diagram(yourdon-demacro, level-0/1)
- uml diagram (deployment/component)
- entity-relationship diagram
並且你有提到 like-c4 可以選出不同範圍與深度的視圖，這對我來說很有幫助，因為目前 archify 就視圖表類型太粗略，而我希望除了至少有前面提到的圖表之外，我還希望可以呈現系統架構和功能模組之間的關係，尤其是直觀的知道類別之間呼叫的流程，而模組、類別和介面之間又由誰取使用
但是有個要點是，一般在繪製圖表時都會使用較為通用標準的格式，但 archify 我目前觀察到的優點是使用 html 來更直覺地呈現模型，這個也是我看中的方向之一，除了標準格式的視圖，如果能用 html 來自由呈現會更好，
```
````

處理方式只改文件，不改程式，也不動 Git：

- 本計畫：更正落實核對表第 3 項；在第四節、上游清除做法、PBC 那一列與 TB 評估段補記已被取代；
  第四輪複核裁定處補上使用者原話；Git 清單增為 189 個路徑，並附提交訊息最後一行的修改建議。
- `development-status.md`：
  - 改寫現況段落。
  - 兩項需求列入「下一階段工作計畫要納入的事項」，技術債盤點一項補上 `filter-step.js` 的過時註解。
  - 「已知但延後的事項」補上 2026-10-07 的裁定，「SQL Server 只經編譯的修改」補記 TB 規則的影響。
  - 「已知技術債」記下 Gui 兩次偶發失敗。
- `docs/README.md`：更新計畫狀態。
- `kct-gl-workflow.md`：新增「KCT 期望的 15 項優化」，逐字列出標題，並指向處理結果。
- `jet-frontend-description.md` 與 `test-environment-notes.md`：會清除情境的修改補上每週非工作日。
  `jet-frontend-description.md` 另寫明情境名稱大小寫不同時視為不同名稱；依據是前端 `duplicateScenarioName` 的完全相等比對，
  以及後端 `FilterHandlers` 的 `StringComparer.Ordinal`。
- `.agents/harness/development-workflow.md`：加入兩條原則，並附使用者原話。
- `filter-step.js` 的註解在程式檔裡，改了要重跑測試，所以本輪不改，記到下一階段的技術債盤點。
- 第 159 行的範例編號依裁定保留。

只改文件，所以只跑 Documentation；產品測試沿用上方「本輪交付前逐項核對」的收據，以及本節的架構檢查。
Documentation 通過，46 個檔案、246 個連結、20 個錨點，沒有錯誤或警告，收據 `20261007-095112614-ec3f073519494e208064989a2e8c5b53`；
收據文字補入後再核對一次。修正後 Git 清單和工作樹都是 189 個路徑，沒有已暫存檔案。
使用者接著確認 189 個路徑與改寫後的最後一行，回覆逐字為「確認清單，最後一行改寫（建議）」；只確認內容，未授權 Git 動作。
下一步等使用者依合成資料人工核對，並回覆驗收結果。

### 第二批實作（2026-10-06）

使用者本次原話：

```text
讀 docs/development-status.md 指向的現行計畫 docs/specs/2026-10-06-kct-gl-requirements-plan.md，依第七節「第二批：科目配對」的順序實作 K6、K7。每項先寫會失敗的測試並保存第一次失敗；K7 的 autoFilter 與隱藏列測試如果第一次就通過，照實記錄。要改既有測試的預期時，先在計畫寫明原因。完成後執行 Build、Public、Contract -ContractScenario FrontendMapping 與 Documentation，把結果與下一步寫回計畫第八節，並同步開發現況與 docs/README.md 的狀態。Gui、PrivateCase、Excel 與 SQL Server 先不跑。
```

K6 新增合成測試：有效 GL 與 TB 聯集包含 TB 獨有科目及零變動科目，排除只有期外 GL 的科目；分類留白與檔案未列分開。
匯入回傳 `mappingOnlyCount`、`unmappedCount`。清單沿 `query.accountMappingDifferencePage` 分頁，種類為 `mappingOnly` 或 `unmapped`，
只在首頁回傳總筆數。開案不計算差異；重開後由審計員展開清單時查詢。當時尚未開始 K7。

K6 首次失敗收據 `20261006-042317239-cc88f522d2ae4f6bad5f1dd79224605c`：SQLite 與 DuckDB 的匯入回應都缺差異筆數。
加入後兩個案例通過，但既有架構清單檢查失敗，收據 `20261006-042432990-ddf2ccca6df0492ea7c360655e91933c`。
變更既有預期的原因：匯入處理器需要新差異查詢介面，新查詢處理器也須納入 `HandlerRepositoryScopeTests`；
只增加這兩個明確依賴，不放寬整組存取限制。


K6：兩個本機資料庫的固定答案與架構檢查通過，收據 `20261006-042659224-cb75971d4caf460f959237c995ab2547`。
前端首次缺少差異清單入口，收據 `20261006-042606511-b8282bf73e5346678a4ff2a7386f81b5`；加入後 FrontendMapping 通過，
收據 `20261006-042645342-c391aa84bee04d158e9aaf8f71cc000d`。測試涵蓋按需查詢、分頁、重繪保留、失敗重試及切換案件時忽略舊回應。
下一項 K7：先一起執行 Excel 篩選、隱藏列、第三列標頭與欄序固定答案，分別記錄第一次結果。

K7 首次結果收據 `20261006-042836759-e6e5a04a4b684d39be73b5e2cd842b82`：45 個案例中 29 通過、16 失敗。
原始 autoFilter 與隱藏列讀取案例第一次就通過；第三列標頭的 SQLite、DuckDB 完整匯入各少一筆而失敗。
查明第二列空白時，Open XML 讀取器原本跳過兩個有內容的列，把第三列標頭也跳掉。K7 修成略過實際列號，
與 BinaryExcelTableReader 相同；不變更預設從第一個有內容列取標頭，也不增加一般匯入的前置處理選項。
另外 14 個失敗是欄序、干擾欄及部分未知欄的固定答案；共用判斷器會先保留確切欄名，再判斷未認領欄。
沒有更改既有測試預期，新增測試的固定答案保留。

K7 修正後 45 個案例與架構檢查均通過，收據 `20261006-043002259-3f7902dbd8494489ba4648627f6773ec`。
新舊英文欄名及中文欄名各六種欄序、干擾欄、部分未知欄與全未知欄都有固定答案；匯入與預覽共用同一判斷器。
現行操作文件已補齊，整批驗證結果如下。

#### 第二批收尾

| 檢查 | 結果 | 收據 |
|:---|:---|:---|
| Focused `AccountMapping` | 159 個案例通過，沒有失敗或略過 | `20261006-043130201-e93962510a274142a9cd3ca873ded77b` |
| Build（Debug） | 通過 | `20261006-043301064-45ae60f71da44ae0931023fc86ef27ac` |
| Public | .NET 4,591 個案例、前端 299 個案例及預覽 8 個案例通過，沒有失敗或略過 | `20261006-043306864-5f7cf4af47fd4b8ead2167c477a900ba` |
| Contract `-ContractScenario FrontendMapping` | 通過 | `20261006-043737660-656f5f175c73477c8f10cf4d99fa3269` |
| Documentation | 通過 | `20261006-043758617-83f23395d1f9407ebe1cadf3763662a2` |

收據、首次失敗與合成測試輸出保留在 `artifacts/harness/runs/<收據編號>/`，不含真實案件資料。
本輪依序完成 K6、K7，沒有改動第一批的既有成果。科目配對差異清單採有界分頁，只提醒、不阻擋匯入；
檔案額外的科目仍保留。K7 共用欄名判斷器，修正第三列標頭前有空白列時漏匯一筆的問題。
autoFilter 與隱藏列本來就會完整讀入，原始測試第一次通過，不列為這次修正的行為。

Gui、PrivateCase、Excel 與 SQL Server 均未執行。合成 `.xlsx` 的自動測試不是原生 Excel 驗收。
SQL Server 的差異查詢實作只經編譯，已補進開發現況的未驗證清單；本機 SQL Server 服務保持停止。
沒有暫存、提交或推送。開發現況與文件導覽已同步為兩批完成指定驗證、待使用者試用；第三批裁定紀錄已完成。

下一步是使用者試用第四步的兩種差異清單，確認沿用前案檔案時的提醒、分頁及重試，再確認變更欄序、第三列標頭、
篩選與隱藏列的科目配對檔都可完整匯入。計畫保持現行，未經使用者驗收不結案；本輪不接著執行桌面或私人資料驗收。
開發現況的 11 項延後事項已再次逐項核對，狀態與下方「延後事項核對」表相同；本輪只新增 K6 的 SQL Server 待實機驗證紀錄。

### 第一批實作（2026-10-06）

本次使用者原話：

```text
讀 docs/development-status.md 指向的現行計畫 docs/specs/2026-10-06-kct-gl-requirements-plan.md，依第七節「第一批：KCT 條件與小項」的順序實作 K1、K2、K3、K10、K5、K4、K9、K11、K15。每項先寫會失敗的測試並保存第一次失敗；要改既有測試的預期時，先在計畫寫明原因。完成後執行 Build、Public、Contract -ContractScenario FrontendMapping 與 Documentation，把結果與下一步寫回計畫。Gui 與 PrivateCase 先不跑，不要暫存、提交或推送。
```

K1 既有測試預期的變更原因：依 K1 裁定，跨年期間要多列查核期末；沒有季底的期間也要列查核期末。
92 天的四季視窗彼此相連或重疊，合併後應是單一區間，不再斷言四段。這三個案例會在新增測試首次失敗後
改成固定日期答案。篩選版本從 `filter-2026-10-04-v17` 推進，相關版本斷言同步更新，不改舊版本拒絕與升級的檢查。

K1：首次失敗收據 `20261006-025641564-dbc8bde295e042b183b01078bc47f13b`，五個案例失敗，原因是未納入期末及未合併。修正後 `Focused -Filter QuarterEnd -NoRestore` 通過，收據 `20261006-025755636-6873858a76de422c9e39af3a922a18bc`。篩選版本改為 `filter-2026-10-06-v18`；下一項 K2。首次還原因沙箱不能連 NuGet 而中止，不算需求測試失敗。

K1 讀回文字的既有預期變更原因：`FilterConditionRendererTests` 原先只寫「季末前 5 天借記收入」，但條件已納入查核期末。改成「季底或查核期末前 5 天借記收入」才能與實際規則一致；數值與命中斷言不變。

K2：B 卡停用的新測試先失敗，收據 `20261006-025845878-76a52aab418247089d7afc77c8b78f2f`。啟用並檢查雙側分類後，FrontendMapping 通過；兩個本機資料庫的固定資產、下層與獨立在建工程測試也通過，收據 `20261006-030004344-2d9d35a3dfbe4f469e83a21f2cc5d90d`。後端沿用既有述詞，所以新增的資料庫案例第一次就通過。下一項 K3。

K3：逐字說明測試首次失敗收據 `20261006-030038300-b79b8f5c1edd404f880a2c56319d2a7c`。加入卡片與編輯區說明後通過，收據 `20261006-030106807-07255ea5de144b118834266ab82e1d64`；報告讀回與 Cash 前置條件不變。下一項 K10。

K10 既有預期變更原因：`ScreenWordingReviewFrontendTests` 的舊斷言要求以卡名當動機，已被 K10 的 A 到 E 評估說明取代；只調整該斷言及註解，自動名稱與手寫保留檢查不變。

K10：預設理由未提供的首次失敗收據 `20261006-030134782-7a9175846bbc4af2913bc210f715a51e`，照來源加入後 FrontendMapping 通過，收據 `20261006-030148677-ed95cd490479441eaa956b181c2b50a4`。D 重字保留，下一項 K5。

K5 既有預期變更原因：人工旗標依裁定由數值 1、0 改為文字「人工」「自動」，`WorkpaperStep41ProviderParityTests` 對應欄的內容、型別及整表指紋必須更新；其餘欄值與排序斷言保留。先跑新增三態案例，再取得既有指紋的第一次失敗，才更新固定答案。

K5 補充：`WorkpaperProgramTests` 除表頭與來源外也鎖住數字型別，首次失敗收據 `20261006-030539603-0677e7cb31ee40899f918c47db5bd35e`；依同一裁定只改該欄為文字，表頭、來源與小數設定斷言不變。新三態測試第一次因夾具未選空白政策而失敗，修正夾具後取得產品未提供新欄與中文字的失敗，收據 `20261006-030309493-880d6f796f724b5da5357d348698c952`。修正後 K5 四個案例通過，收據 `20261006-030520084-995fe533f0324325b08850e009632936`。舊指紋首次失敗另留在 `20261006-030408405-e6eae8ec68514c75a7ed262e74fc8da3`。下一項 K4。

K5 共用夾具的既有設定變更原因：`InlineWorkbookProject` 現在明送 `blankValueKind = reject`，把原本相同的空白拒絕政策寫清楚，讓人工旗標三態測試可以自行指定空白處理；未更改其他測試的業務預期。

K5 擴充檢查：`Focused -Filter Workpaper` 執行 175 個案例，174 通過；底稿效能夾具仍要求人工欄為數字，失敗收據 `20261006-030557037-059b392306c24c818a2922d7ff31c261`。依 K5 改該欄固定答案為中文字與文字格式，其他欄位及串流、排序、效能檢查不變。

K4：快捷控制項不存在的首次失敗收據 `20261006-030733782-013f40b540324f21bfbdd6c388258cde`。新增入口並保留既有欄位識別碼後 FrontendMapping 通過，收據 `20261006-030754553-d8d48eab2a214953afd35099aae949d9`；涵蓋新選、既有來源改名改型、替換來源及來源已被另一個 RDE 勾選。下一項 K9。

K9：新計數不存在的首次失敗收據 `20261006-030827111-7e64fa7619014ea49b52b80c2e2d96ff`。計數依送出值去重，尾數也共用同一份清單；輸入時不重建編輯器。FrontendMapping 通過，收據 `20261006-030912446-ee973a8276cb415e881a4d74fcbd432c`。既有上限提示不改，下一項 K11。

K11：年月選單不存在的首次失敗收據 `20261006-030948070-90ff509727e548b18dddc04926f1bcab`，加入後 FrontendMapping 通過，收據 `20261006-031005761-d81a304691cc4c339dfc263faaec14bb`。測試包含期間前後五年、已選日期的額外年份、換月與 Shift 加 PageUp。

K11 實測：在 `.claude/launch.json` 指向的 JET browser host，用內建瀏覽器背景分頁操作第一步的三個原生日期欄，沒有使用 fill 或直接改 DOM 值。查核起始日逐段鍵入 2025、1、2，以方向鍵移到下一段；查核截止日鍵入 2025、11、30，月份滿兩位時自動跳至日期；期末財報準備日鍵入 2025、11、20。離開欄位後，畫面與 DOM 值分別是 `2025-01-02`、`2025-11-30`、`2025-11-20`，三者原生有效性均為 true。未按建立案件，未開桌面 JET、Excel 或檔案總管。截圖留在 `artifacts/review/kct-batch1/k11-date-keyboard.jpg`。下一項 K15。

K15：action 尚未註冊的首次失敗收據 `20261006-031201730-94300c878bab46c5b1ffa80de67dd9e2`。新 handler 的複製、保留原檔與不匯入測試已通過；架構檢查留下兩項舊計數，收據 `20261006-031314504-65019a07842d49ad9424f1d4d9a4eaa7`。變更原因：K15 新增明示開啟案件資料夾按鈕，所以 `FrontendUsabilityContractTests` 的入口數由 3 改為 4；K4 新增一次可用 RDE 來源查詢，所以 `MappingProjectionPolicyFrontendTests` 的次數由 2 改為 3，不改不可建立欄位識別碼的限制。

K15 的五個新增案例及架構檢查通過，收據 `20261006-031402114-ad61efba6d524c60952df6cae8acaaf9`。
Build 使用 `-NoRestore` 通過，收據 `20261006-031500715-6924410fe9284fe0b0829bf50ae5efd9`；本輪先前已成功還原套件。

### 第一批收尾

K5 差異分類補強：新增合成案例，先確認未定位的整表差異會被錯誤接受，失敗收據
`20261006-031846752-a1b679cdb6de4b5184f186101fa518e9`。修正後必須明示採用該裁定，且共有欄差異只涉及
人工旗標、列數及欄數一致，才能列為刻意差異；其他欄或未知範圍仍不通過。補強測試通過，收據
`20261006-031914520-351653310c6c4e4894a36d9da497fac7`。沒有讀取或執行私人案件。

本輪交付範圍是第一批九項，不包含第二批。產品修改如下：

| 項目 | 實作與確認 |
|:---|:---|
| K1 | 季底與查核期末視窗合併，前端及報告列出日期；篩選版本升為 v18。固定日期、SQLite 與 DuckDB 命中測試已通過。 |
| K2 | B 卡可加入，兩側分類留空且包含下層；新增分類的後端合成測試確認不選入獨立在建工程。 |
| K3 | 卡片與編輯區顯示已裁定的現金說明，條件與報告讀回不變。 |
| K10 | A 到 E 的動機逐字採用評估說明；D 重字保留，手改文字不覆寫。 |
| K5 | 底稿改為中文字，預覽新增最後一欄，空白保留；其他預覽及來源旗標不變。SQL Server 只經編譯。 |
| K4 | 傳票建立日快捷已加入，選新來源不自行建立識別碼；改既有欄及更換來源沿用原識別碼。 |
| K9 | 值清單及尾數的計數與送出內容一致，輸入時就地更新，上限提示保留。 |
| K11 | 指定日期日曆有年月選單；三個建案日期欄的鍵盤實測結果見上方紀錄。 |
| K15 | 範本由新介面取得，以工作檔寫入案件資料夾；同名檔保留、不自動匯入或開資料夾。 |

整批驗證：第一次 Public 通過 4,540 個 .NET 測試，另含兩組前端檢查，收據
`20261006-031507730-e28a59bc3c55454fa94e857e35471c75`。補強 K5 差異分類後再執行整批，最後結果列在下方。

- Build（`-NoRestore`）：通過，收據 `20261006-032004442-675a439c536b4002b91bbbf44670af4a`。
- Public（`-NoRestore`）：通過 4,541 個 .NET 測試、298 個 FrontendMapping 測試及 8 個 FrontendPreview 測試，沒有失敗或跳過；收據 `20261006-032008627-e3cce16c647944c59a54a1f335a7efa6`。
- Contract（`-ContractScenario FrontendMapping`）：通過，收據 `20261006-032344024-d4a8ac37234d43ebab581eb98e00caae`。
- Documentation：通過，收據 `20261006-032414117-aedae6ceadc24045bb7d79d502e778f5`；回填完成狀態後再檢查一次。
- 未執行：Gui、PrivateCase、Excel、Provider、Package 與 ReleaseCandidate。沒有啟動 SQL Server；收尾確認三個本機服務皆停止，沒有 `sqlservr.exe`。瀏覽器主機與測試分頁已關閉。沒有暫存、提交或推送。
- 第二批 K6、K7 尚未開始。本輪停在第一批；使用者授權下一批時，先寫 K6 的匯入差異提醒固定答案，再照第七節實作。

### 延後事項核對

交付收尾時依開發現況再次逐項核對。除了 KCT 已裁定項目，以及 SQL Server 預覽與科目差異查詢的編譯紀錄外，
本輪沒有處理下列事項；各項延後原因與重啟條件保留：

| 事項 | 目前狀態與重啟條件 |
|:---|:---|
| 驗證報告的「完整性測試出現差異時之指引」工作表改寫 | 維持原範本；等使用者與 DPP 確定新內容。 |
| SQL Server 實機驗證 | 未執行，日常仍以兩個本機資料庫為主；使用者明示授權並備妥專用測試環境後重啟。 |
| SQL Server 企業多人環境 | 四個安全缺口及多人範圍仍延後；等公司提供環境、帳號、DBA 支援與授權政策。 |
| 預篩選的後續收斂 | 規則目錄不變，財報準備日起核准仍保留；等使用者另行要求收斂。 |
| 操作紀錄的查詢介面、匯出與保留政策 | 未實作；隨企業部署重啟，或使用者明示先做本機介面。 |
| KCT 後續條件 | A、B、C 與預設理由由本批處理；F 到 J 不變，等完整來源後再核對。 |
| 匯入前的資料前置處理 | 仍在 JET 之外；等使用者認為值得納入，再先評估指定欄名列與往下填補。 |
| SQLite 執行中的單一語句無法取消 | 尚未修正；交付測試環境後，先建立長 SQL 取消測試並評估影響。 |
| 驗證框架瘦身與收據保留期限 | 尚未開始；等使用者另立計畫，先盤點命令用途與可合併處。 |
| 最後獨立複審留下的低嚴重度事項 | 保留既有清單；使用者測試環境驗收後，再選擇項目逐一重核。 |
| 本機單人案件上的多人機制 | 維持現況，避免影響 SQL Server；出現兩個 JET 同時開案或實際阻擋時再處理。 |

### 規格整理時的驗證

- 讀取：KCT 簡報 50 頁；`docs/` 的現行文件；上一份計畫中 D06、D12、P3-1、S2 的裁定原文；本計畫「依據」欄列出的程式。
- 子代理：三個唯讀子代理，沒有改檔、沒有執行測試、沒有讀私人資料目錄。主線另外讀原始碼確認 KCT A 與 C 的述詞、
  預篩選「未預期借貸組合」、舊工具 #3 與文字篩選、科目配對檔欄名判斷、KCT 情境理由的預設值與案件根目錄限制。
- 沒有執行：Build、Public、Gui、Excel、PrivateCase 與 SQL Server，因為本輪沒有改產品程式。
- 文件：新增本計畫與 `docs/kct-gl-workflow.md`，並更新開發現況、專案背景、IDEA 替代範圍、專案指南第 6 節、文件導覽
  與文件檢查清單；`Documentation` 的結果寫在下方。

Documentation 結果：2026-10-06 執行，通過。檢查 46 個檔案、234 個連結與 19 個錨點，沒有錯誤或警告；這只證明連結與
固定用語，句子是否好讀已另外逐段重讀。

使用者裁定之後，同日再做一輪：

- 三個唯讀子代理分別查第一批的 KCT 條件、第一批的小項與第二批的科目配對，找出改動位置、既有測試與風險；
  沒有改檔、沒有執行測試、沒有讀私人資料目錄。主線另讀 `LocalDataPreviewRepository` 的欄位清單，確認「納入測試的分錄」
  與「未納入測試的分錄」是兩份不同的欄位清單。
- 文件：第六節加入裁定原文與處理方式，第七節改寫成批次規格；開發現況、`jet-guide.md` 第 6 與第 7 節、文件導覽同步更新。
- 沒有執行：Build、Public、Gui、Excel、PrivateCase 與 SQL Server，仍然沒有改產品程式。
