# 匯出底稿 writer 設計（子專案 E1）

這份是 2026-11-01 壓縮後的摘要。使用者原話、題目與回答、日期、數字與裁定照原文保留；實作經過、驗證收據與重跑過程壓成結果。
壓縮前的全文在提交 `85af454`，可用 `git show 85af454:docs/history/specs/2026-06-21-workpaper-export-writer-design.md` 取回。

## 狀態與依據

原文狀態是「已實作，待 GUI 驗收」：程式已落地，自動化測試全部 700 項在本機通過（0 failed、0 skipped，設 `JET_SQLSERVER_CONNECTION` 時 SQL Server 等價測試實際執行）。
逐表在 Excel 目視對齊樣本、大母體匯出不爆記憶體等 GUI 互動仍待 Windows 端人工驗收，當時尚未驗收、尚未提交。

E1 是「匯出底稿」里程碑最後一棒 E 的第一階段：用 OpenXML SAX 串流寫出 15 張工作表（其中有條件表）的 `.xlsx` 匯出底稿，
含三類參考資料工作表。**欄位配對匯入（round-trip）延後到 E2，這是使用者裁定。** E1 依賴 A、B、C、C 補遺、D1、D2 與 F 全部完成。

盤查依據是 `.git/sdd/workpaper-synthesis.md`（15 表結構）、`.git/sdd/bcde-scope-draft.md`（E 範圍），以及 2026-06-21 E 前的自我審查：
實機逐格重解兩份 WorkingPaper 樣本全 15 表，並對碼確認每張表的資料來源（D1 分頁、D2 矩陣、F 與 B 的 store）。

## 背景

事務所「JE Testing Tool」的最終產出是一份 `.xlsx` 工作底稿（WorkingPaper），固定命名 `{客戶}_{yyyymmddHHmmss}_WorkingPaper.xlsx`，
含封面、step1~step5 測試底稿與三張「自動化工具」參考資料表。當時 JET 的 `export.*` 全是空殼，前面子專案已備齊每張表需要的查詢與資料。

寫出器走 `DocumentFormat.OpenXml` 的 SAX（`OpenXmlWriter`），不可用 ClosedXML（ClosedXML 只限 dev fixture `DemoWorkbookWriter`）——因真實母體可達百萬列等級，必須串流、不可全載入記憶體。

15 張表逐格對碼後全部對得上現有查詢與 store，只有兩個小缺口：

1. step1-2「分錄編製人員說明」需要全名單（編製人員、傳票數、金額彙總），但 `creator_summary` 截 **LIMIT 50**。E1 新增一條不截斷的編製人員查詢；distinct `created_by` 實務上數十到數百，所以回完整清單即可，**不需分頁**。
2. step2「分錄來源」欄（J，方法學標「若有」）在 `infSamplePage` 沒有 `source_module`，E1 這欄留空，不擴充已驗證的 D1 查詢。

金額一律以 `amount_scaled` 整數計，顯示值是 `(decimal)scaled / moneyScale`。

## 設計決定

- **寫出器是 deep module。** `WorkpaperWriter` 對外只有 `WriteAsync(Stream, WorkpaperContext) → ExportStats`，內部藏起 OpenXML SAX 的細節。資料表共用單一 `EmitTableSheet` 原語，以 keyset 逐頁從 repository 取資料、逐列寫出；封面、固定文字與手填骨架是各自的小 emitter。
- **條件表由編排層決定。** step1-3-1 只有差異不為 0 時才產出，寫成編排層的判斷，emitter 本身沒有「我是第幾張表」的分支。
- **寫出器只消費查詢，不重做業務邏輯。** 百萬列逐頁流過寫出器，記憶體有界；E1 不在寫出器內重算規則。
- **正準中文名只有一份。** 新增 Domain `GlCanonicalNames`（logical key 對正準中文名，例如 `docNum` 對 `傳票號碼_JE`、`postDate` 對 `總帳日期_JE`，TB 側如 `會計科目編號_TB`），匯出與日後 E2 讀回共用。
- **step2 金額 = 借、貸兩欄（使用者裁定）。** `InfSampleRow` 已有 `DebitScaled` 與 `CreditScaled`，step2 直接寫 E 欄借方、F 欄貸方，不寫借貸代號加單欄。

## 範圍

E1 要做：

1. OpenXML SAX 寫出器核心，含樣式、合併儲存格與數字格式。
2. 15 張工作表（含條件表）逐表對齊樣本：封面 ×2、step1、step1-1、step1-2、step1-3、step1-3-1（條件）、step2、step3、step4、step4-1、step5、Field Mapping Info、假期假日資訊、科目配對資訊。
3. 手填欄位一律留空：step1-2 部門、職稱、說明；step1-3 原因、調節、調節後；step1-3-1 前期損益金額；step2 結果 A–G 與詳細說明；step4 P–U；step5 內文。
4. 不截斷的全編製人員彙總查詢。
5. `export.workpaperStream` action 取代空殼：`{ sheets?, outputPath }`（省略 sheets 表示全部）回 `{ ok, bytesWritten, sheetStats }`。
6. host 存檔對話框 `host.selectSavePath`（WinForms `SaveFileDialog`），預設檔名 `{客戶}_{yyyymmddHHmmss}_WorkingPaper.xlsx`。
7. 前端匯出步驟接線：選表、觸發與完成回饋。

不做的事：欄位配對匯入 round-trip 屬 **E2**；CAATs .docx 不產生，封面只寫檔名字串 `{客戶}_CAATS_JE_WP_{yyyymmdd}.docx`（由查核員自備）；
step1-3-1 前期損益自動計算（B 已裁定手填）；step5 內文手填；不擴充 `infSamplePage`；不改既有規則或查詢語意；不做排程、多檔批次或 PDF。

## 工作表與資料來源

表頭固定文字、欄標、合併儲存格與數字格式逐表對齊樣本工作底稿，細節在實作時落實。

| # | 工作表 | 來源 | 內容規則 |
|---|---|---|---|
| 1 | 資料預先整理之說明 | 專案 metadata | A1 公司名稱、A2 測試資料期間、A5 固定說明、A6 `請詳:{客戶}_CAATS_JE_WP_{yyyymmdd}.docx` |
| 2 | JE WorkingPaper說明 | 固定文字 | A1「說明:」與 B1 固定說明（合併 B1:O1） |
| 3 | step1 完整性測試 | `completenessDiffPage`（全科目，逐頁） | 表頭與第19列欄標（科目編號、名稱、TB變動(A)、GL本期借貸彙總(C)、差異(B)-(A)），再接全科目列 |
| 4 | step1-1 借貸不平測試 | 固定文字與 `docBalancePage` | 結論文字；有不平傳票才產出例外表 |
| 5 | step1-2 分錄編製人員說明 | 全編製人員查詢（新） | 第11列欄標；B 編製人員、C 自動人工（空）、D 傳票數、E 金額彙總；F 部門、G 職稱、H 說明留空 |
| 6 | step1-3 完整性差異說明 | `completenessDiffPage` 中 diff≠0 | 第16列欄標；科目與差異金額自動；E 原因、F 調節、G 調節後留空 |
| 7 | step1-3-1 完整性差異調節 | diff≠0 科目與固定說明 | 條件表（diff≠0 才產出）；科目自動；前期損益金額手填；A4–A6 固定調節說明 |
| 8 | step2 可靠性測試 | `infSamplePage`（抽樣行） | 表頭跨 49–52 列；A 樣本號、B 傳票號、C 科目編號、D 名稱、**E 借方、F 貸方**、G 總帳日、H 核准日、I 編製人員、J 來源（空）、K 摘要、L 核准人員；M–S 結果A–G 與 T 說明留空 |
| 9 | step3 高風險條件彙總 | `tagMatrixScenarios` | 固定測試說明與 C1..CN：B 代號、C 條件描述（情境 name）、D 原因（情境 rationale）、E 符合傳票數（voucherHitCount） |
| 10 | step4 符合高風險條件傳票 | `tagMatrixVoucherPage`（逐頁） | 第11列欄標；A 編號、B 傳票號、C 總帳日、D 編製者、E 傳票總額；F 起的 C1..CN 依 matchedPositions 標 Y；P–U 手填留空 |
| 11 | step4-1 符合高風險條件傳票明細 | `tagMatrixRowPage`（逐頁） | 第5列欄標（傳票號、項次、總帳日、建立與核准人員、科目、金額、摘要），加動態 C*_TAG 欄 |
| 12 | step5 財務報表關帳後調整 | 固定橫幅 | A1 黃色固定說明（合併 A1:R1），其餘手填 |
| 13 | 自動化工具-檔案欄位資訊 | 欄位配對設定與 `GlCanonicalNames` | A1 版本標籤；TB 段與 GL 段列出配對前來源欄、型態、長度、小數（缺則空）、配對後正準名；列出 K_ 衍生旗標 |
| 14 | 自動化工具-假期假日資訊 | 行事曆 store | 固定週末表（Mon–Sun，六日=Y）、假日表（DATE_OF_HOLIDAY、HOLIDAY_NAME、IS_HOLIDAY）與補班段 |
| 15 | 自動化工具-科目配對資訊 | 科目配對 store 與 B 的 Not-in-TB | GL_NUMBER、GL_NAME、STANDARDIZED_ACCOUNT_NAME；在 GL 不在 TB 者 GL_NAME 寫字面「Not in TB」 |

step4-1 的動態欄位只產出該客戶有行層命中的 C*_TAG 欄，由 `tagMatrixScenarios` 中 rowHitCount>0 的 position 決定，標頭為 `C{position}_TAG`；樣本 Q–Z 的固定範本殘留欄不複製。

## 邊界與驗收

架構邊界沿用專案規則：正準名表與匯出契約在 Domain，寫出器與查詢在 Infrastructure，handler 在 Application，存檔對話框在 host；
action 形狀先寫進 `docs/action-contract-manifest.md`；新查詢在 SQLite 與 SQL Server 等價；先寫測試，不自行 commit。
原文另列一組工程品味準則（先想資料結構再寫分支、不過度抽象、重複三次才抽共用、註解寫為什麼、不順手改無關程式），作為本棒的實作約束。

測試以讀回寫出的 `.xlsx` 斷言，不靠目視：工作表數與名稱、封面資料、step1 數值等於獨立重算、step1-2 筆數等於 distinct created_by、
step2 借貸兩欄、step3 到 step4-1 與矩陣一致、條件表在有差異時才存在、手填欄空白、Not-in-TB 字面值與 `sheetStats` 列數。

Windows 端待人工驗收：「匯出底稿」選表後觸發 `export.workpaperStream`，跳出存檔對話框（預設檔名 `{客戶}_{yyyymmddHHmmss}_WorkingPaper.xlsx`），
寫出並回饋 bytesWritten 與各表列數；Excel 開啟後逐表目視對齊樣本；diff≠0 案件出現 step1-3-1、無差異案件不出現；百萬列等級的大母體匯出不爆記憶體、可完成。

## 與其他子專案的關係

D1 與 D2 的查詢由寫出器逐頁消費，不改語意；B、C、C 補遺、F 與 A 的資料來源都已落地，E1 只讀；E2（延後）用 `GlCanonicalNames` 反向對照讀回 Field Mapping Info。
