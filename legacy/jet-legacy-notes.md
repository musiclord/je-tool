# JET 舊版筆記歸檔（IDEA / VBA 時期）

> **這是歷史歸檔，只供對照業務語意與設計意圖之用，不是現行指引。** 內容來自上一代 JET 的開發筆記，也就是 Caseware IDEA 搭配 IDEAScript、以及 Excel VBA 搭配 Access 的那個時期。凡是與受查者有關的識別資訊，以及其他不宜進版控的內容，一律已經移除或改成佔位符，例如 `〈受查者〉`、`〈path〉`、`〈會計科目〉`、`〈金額〉`、`〈傳票號碼〉`。
>
> 現行的權威一律以 [`../docs/jet-guide.md`](../docs/jet-guide.md)、[`../docs/action-contract-manifest.md`](../docs/action-contract-manifest.md)、[`../docs/jet-frontend-description.md`](../docs/jet-frontend-description.md) 三份文件為準；需要回查遷移當時的完整章節與歷史代號時，才讀 [`../docs/history/superseded/jet-guide-2026-08.md`](../docs/history/superseded/jet-guide-2026-08.md)。至於棄用上一代的原因、VBA 的分層方式、以及 IDEAScript 的函式地圖，見 [`README.md`](README.md)。請不要逐行把這裡的腳本翻成 C#。這份筆記保存的是審計方法論與設計動機，可作為差異分析的歷史 oracle；它不是任何已不存在 skill 或 harness 的執行契約。文中引用的 R1–R8、V、A 等歷史代號，只供回查完整來源指南 §4 的登錄表。

---

## 1. 審計方法論:工作底稿五段結構（ISA / KAEG）

上一代是用一份 Excel 工作底稿（`WorkingPaper.xlsx`）來呈現整套會計分錄測試，分頁依審計實務排成五段。這個五段結構已落在現行六步驟流程的 Step 6「匯出報告」，不是尚待實作的雛形；目前流程與輸出定位分別見 [`../docs/jet-guide.md`](../docs/jet-guide.md) §3、§7，完整歷史實作狀態見來源指南 §7。以下列出的是通用欄位標題，實際資料一律以佔位符代替。

### Step 1 — 母體完整性 + 借貸不平 + 編製者

- **完整性測試。** 逐科目比對兩個數字：一個是「試算表本期變動金額」（記為 A），另一個是「該科目在會計分錄上的本期借貸彙總」（記為 C）。差異就是 A 減 C。凡是 `ABS(差異) > 0` 的科目，都必須在 Step 1-3 說明它的調節。
  - 欄位：`試算表科目編號 | 試算表科目名稱 | 變動金額(A) | 會計分錄本期借貸彙總(C) | 差異`
  - 範例列：`〈會計科目〉 | 〔銀存〕支票存款 | 〈金額〉 | 〈金額〉 | 0`
- **Step 1-1，借貸不平測試。** 對每一個傳票號碼分別小計，確認有沒有哪張傳票的借貸餘額不為 0。如果個別傳票不平、但整個母體的餘額加起來是 0，就要向受查者確認原因。
- **Step 1-2，分錄編製人員說明。** 彙總編製人員清單，以及每個人經手的傳票數與金額，再說明各人員是否適當。這對應現行的 `creator_summary`。
  - 欄位：`編製人員 | 自動或人工 | 經手傳票數 | 經手傳票金額彙總 | 部門 | 職稱 | 是否適當之說明`
- **Step 1-3，完整性差異的說明與調節。** 針對有差異的科目，檢視相關的傳票明細，說明造成差異的原因並做調節，使差異最後能視為 0，或者把剩餘的差異留下說明。

### Step 2 — RDE 可靠性測試

這部分依循 ISA 240 與 ISA 330（KAEG）。凡是被納入高風險條件（High-Risk Condition，HRC）的欄位，就稱為攸關資料元素（Relevant Data Element，RDE）。對這些欄位，必須先確認它的可靠性，也就是完整性加正確性，才能執行高風險篩選。但預篩選（也就是風險評估）這個階段不需要先確認可靠性。完整性大多已經在 Step 1 做完了，所以這裡主要測正確性，做法是依屬性抽樣表選樣，再核對傳票附件。

- **財務類 RDE**（包括會計科目編號與名稱、借貸別、金額）：
  - 程序 A — 先確認這個欄位是不是已經在完整性測試裡完成過。如果還沒，就把金額核到附件，其餘欄位核到已核准的會計科目表。
- **非財務類 RDE**：
  - 程序 B — 過帳日期與時間：內部交易核到已核准的內部交易日期，外部交易則核對交易憑證上的日期。
  - 程序 C — 傳票建立日期與分錄時間：依案件與樣本，挑一項或多項程序來驗證。
  - 程序 D 到 G — 其餘的非財務欄位（例如摘要、人工或自動的註記、編製與核准人員等）的核對。實際要做哪些，視 HRC 當次納入了哪些欄位而定；沒納入的欄位，對應的程序就設為 N/A。
- 對應到現行：工具內用 `inf_sampling_test`（INF 抽樣）提供一個可重現的抽樣機制（見 [`../docs/jet-guide.md`](../docs/jet-guide.md) §4）。

### Step 3 — 高風險範圍條件（HRC）彙總

這一段要辨識與「管理階層凌駕控制」風險有關的分錄類型，並把高風險範圍條件彙總起來。

- 欄位：`條件 | 選擇此篩選條件的原因 | 符合條件之傳票數`
- 條件範例（都是通用範例，對應現行 §4 與 §5 的規則）：未預期借貸組合（`unexpected_account_pair`）、特定的編製或核准人員（由 `creator_summary` 衍生）、週末或假日過帳且要排除補班日（`weekend_posting` 與 `holiday_posting`）、人工分錄、摘要關鍵字（`suspicious_keywords`）。

上句「週末或假日過帳且要排除補班日」是舊摘要，不是這兩個預篩選鍵的現行共同定義。
現行排除補班日的 KCT I 使用「非營業日（排除補班日）」條件；既有週末與假日條件各自的語意不變，見現行指南第 6 節。

### Step 4 — 符合高風險條件之分錄測試

- 主表欄位：`編號 | 傳票號碼 | 總帳日期 | 編製者 | 傳票總金額`
- 明細表欄位：`傳票號碼 | 傳票文件項次 | 總帳日期 | 編製人員 | 核准人員 | 會計科目編號 | 會計科目名稱 | 傳票金額 | 傳票摘要 | 各條件 tag 欄`
- 測試程序有三項：核到複核紀錄確認核准、核到附件確認內容與會計期間、詢問編製的細節。

### Step 5 — 財務報表關帳後調整之分錄

這一段記錄關帳後才入帳的調整分錄（post-closing entries），或是沒入帳而直接調整的項目（other adjustments）；如果沒有這類情形，也要說明沒有。最後把這份底稿納入 eAudIT。

### 附：輔助檔案的欄位（通用，箭號表示配對前的欄名對應到配對後的欄名）

- **TB**：`K_科目代碼(文字) → 會計科目編號_TB`、`K_科目名稱(文字) → 會計科目名稱_TB`、`K_本期變動數(數字) → 試算表變動金額_TB`。
- **GL**：`K_傳票號碼(文字) → 傳票號碼_JE`、`K_項次(數字) → 傳票文件項次_JE_S`、`金額欄位＋借貸判斷 → 傳票金額_JE`。
- **假期假日**：`DAYOFWEEK | WORKDAY(Y/N)`、`DATE_OF_HOLIDAY | HOLIDAY_NAME | IS_HOLIDAY`、`DATE_OF_MAKEUPDAY | MAKEUPDAY_DESC`（對應現行的 DateDimension，見 [`../docs/jet-guide.md`](../docs/jet-guide.md) §2；精確欄位見完整來源指南 §2.4）。
- **科目配對**：`GL_NUMBER | GL_NAME | STANDARDIZED_ACCOUNT_NAME`（對應現行的 AccountMapping，見 §2.3）。

---

## 2. IDEA JE 資料管道（IDEAScript,已清洗）

上一代用 IDEAScript 把 JE 母體的建置流程串起來。這裡保留了通用的 ERP 欄位名（例如 `POST_DR`、`POST_CR`、`EFFECTDATE`、`POST_DATE`、`GL_ACCOUNT`、`UNIQUE` 等）當作 schema 對照；至於路徑、客戶、科目、金額、門檻則都已清洗掉。這份流程可以拿來當差異測試的 ground-truth 對照。

整個管道從 `JE.IDM` 起算，最後輸出到 `〈path〉`：

1. **匯入**:`ImportUTF8DelimFile`(來源 `JE.csv`,受查者 `〈受查者〉`)→ `JE.IDM`。
2. **新增虛擬欄位**:
   - `K_傳票編號 = @split(UNIQUE, "-", 1)`(自 `UNIQUE` 切出傳票號)。
   - `K_項次`(整數,預設 0)。
   - 借/貸方特定科目旗標:`@if(@match(GL_ACCOUNT, "〈會計科目〉") .AND. POST_DR >= 〈金額〉, 1, 0)`;以及 `@if(@match(@mid(GL_ACCOUNT, 4, 4), 〈特定現金/應收科目片段清單〉) .AND. POST_DR <> 0, 1, 0)`。
3. **索引**:`K_傳票編號`、`GL_ACCOUNT`。
4. **Join 科目配對**:`Sum_科目配對.IDM` by `K_傳票編號`,帶入科目配對情境欄。
5. **期間擷取**:`@BetweenDate(EFFECTDATE, "〈期初〉", "〈期末〉")` → 期間內 GL。
6. **標準化欄名 + 衍生金額**:
   - `K_傳票編號 → 傳票號碼_JE`、`GL_ACCOUNT → 會計科目編號_JE`、`ACCOUNT_DESCRIPTION → 會計科目名稱_JE`、`DESCRIPTION → 傳票摘要_JE`、`EFFECTDATE → 總帳日期_JE`、`POST_DATE → 傳票核准日_JE`、`CREATEDUSER → 傳票建立人員_JE`、`POSTUSER → 傳票核准人員_JE`、`K_項次 → 傳票文件項次_JE_S`。
   - `傳票金額_JE = POST_DR - POST_CR`(借貸淨額)。
   - `DEBIT_傳票金額_JE_T = @if(傳票金額_JE >= 0, 傳票金額_JE, 0)`、`CREDIT_傳票金額_JE_T = @if(傳票金額_JE < 0, 傳票金額_JE, 0)`、`DEBIT_CREDIT_JE_T = @if(傳票金額_JE >= 0, "DEBIT", "CREDIT")`。
7. **Join 預篩選結果**：分兩次 join。先以傳票號碼與日期 join 傳票層的彙總，把各個預篩選 tag 帶進來；再以傳票號碼與項次 join 分錄層的彙總，把明細層的 tag 帶進來。
8. **輸出檔命名（歷史）**：輸出多份檔案，命名與內容如下——`#GL#.IDM` 是期間內的標準化 GL、`#GL#In_Period_Doc_Sum` 是傳票層的合計與不平清單、`#GL_Account_Sum` 是會科層的期間合計、`#Completeness_calculate` 是 GL 與 TB 合併後加上差異、`#Completeness_Check` 是 `ABS(差異)>0` 的科目，依此類推。

**對應到現行架構。** 下列精確章節均指 [`../docs/history/superseded/jet-guide-2026-08.md`](../docs/history/superseded/jet-guide-2026-08.md)：標準化欄名見 §18；借貸淨額與 DEBIT/CREDIT 拆分見 §1.5.3、§2.1；科目配對見 §2.3，以及 `unexpected_account_pair`（§6.1）；期間擷取對應 `period_in_out`。現行規則一律以參數化的集合式 SQL 執行（§1.5.2），不會重現 IDEAScript 逐欄建虛擬欄位的做法。

---

## 3. Access SQL 完整性實作（已清洗路徑）

上一代在 Access 端用 SQL 做完整性測試，也就是 GL 與 TB 的勾稽。這段可以拿來當現行 `completeness_test` 的差異對照（differential oracle）。步驟如下：

- 跨資料庫載入 TB 與 JE（來源是 `〈path〉`），期間篩選的結果命名為 `JE_IN_PERIOD`，篩選條件是 `WHERE [EffectDate] Between #〈期初〉# And #〈期末〉#`。
- 新增並計算 `K_AMOUNT = Nz([Post Dr], 0) - Nz([Post Cr], 0)`。
- 依科目彙總：`SELECT [GL Account], SUM([K_AMOUNT]) AS K_AMOUNT_SUM INTO JE_ACCOUNT_SUM ... GROUP BY [GL Account]`。
- 外部連結：用 `JE_ACCOUNT_SUM LEFT JOIN TB ON [GL Account]=[GLCODE]` 再 UNION 一個 `RIGHT JOIN`，藉此模擬 FULL OUTER JOIN，結果命名為 `COMPLETENESS_CALCULATED`。

**對應到現行。** 對應現行 [`../docs/jet-guide.md`](../docs/jet-guide.md) §4 的完整性測試；精確 SQL 語意見完整來源指南 §4：以 `FULL OUTER JOIN` 把 GL_Sum_ByAccount 與 TB 依 AccountCode 接起來，差異算法是 `TB.ChangeAmountScaled - GL.AmountScaledSum`。Access 的 SQL 方言和 SQLite、SQL Server 並不相同（見 [`README.md`](README.md)）。

---

## 4. 日期維度篩選 SQL 樣式（已清洗）

上一代用一張 `DATE_DIMENSION` 表來承載各種日期屬性，篩選則靠子查詢的 `IN` 和 `NOT IN` 組出來。這對應到現行的 DateDimension（欄位 `IsWeekend`、`IsHoliday`、`IsMakeupDay`，見 [`../docs/jet-guide.md`](../docs/jet-guide.md) §2），以及週末和假日規則（排除補班日，見 §5）；精確歷史章節是完整來源指南 §2.4。

這段「週末和假日規則（排除補班日）」也是舊摘要。下面保留的是歷史 SQL 樣式，不能由它推定現行所有週末或假日規則都排除補班日。
現行 KCT I 與既有預篩選鍵的差異，以現行指南第 6 節為準。

- 總帳/核准日期在週末:`CDate([PostDate]) IN (SELECT [DateKey] FROM DATE_DIMENSION WHERE [IsWeekend] = True)`。
- 在國定假日:`... WHERE [IsHolidays] = True`。
- **排除補班日**:`CDate([PostDate]) NOT IN (SELECT [DateKey] FROM DATE_DIMENSION WHERE [IsMakeupDays] = True)`。
- 僅借方 / 僅貸方:`WHERE [DrCr] = "D"` / `<> "D"`。
- 人工編制:`WHERE [IsManual] = True`。
- 文字 / 日期區間 / 數值區間:`LIKE '*keyword*'` / `BETWEEN #起# AND #迄#` / `BETWEEN 起 AND 迄`。

---

## 5. VBA QueryBuilder 設計意圖（現行 AST Query Builder 的前身）

上一代在 `ServiceFilter` 裡用一個 `ServiceFilterCriteria` builder，先累積多個 criterion，再組成 `WHERE` 子句：

- 每個 criterion 帶六個欄位：`Type`（值有 `Date`、`Subquery`、數值、文字等）、`Field`、`Operator`、`Value`、`Logic`（`AND` 或 `OR`）、`Description`。
- 流程是多次呼叫 `AddCriterion` 把 criterion 加進去，最後呼叫 `BuildWhereClause()` 組出帶括號的 `WHERE`。其中 `Subquery` 這個型別，是用來把日期維度的子查詢（`IN (SELECT [DateKey] FROM DATE_DIMENSION WHERE …)`）當成一個條件嵌進去。
- 它的設計目標寫得很明確：要能一次組合多條條件的 AND/OR，而不是只支援單一條件。

**對應到現行。** 進階篩選用的是 AST 式 Query Builder（見 [`../docs/action-contract-manifest.md`](../docs/action-contract-manifest.md) 的 Filter/Criteria AST schema，以及 [`../docs/jet-frontend-description.md`](../docs/jet-frontend-description.md) 的 Step 4 Query Builder）。它正是上面這個設計意圖的演進與正式化。

---

## 6. 遷移動機（摘要）

遷移有幾個動機。一是要取代 IDEA，因為它有訂閱授權成本，也有專有技術的依賴。二是要降低對 SQL 與外部 IT 環境的依賴。整體目標是把審計流程做到更簡化、更自動化、更優化。設計基準是涵蓋約八成案件所面對的百萬筆級資料量；至於十億筆級或雲端的情境，則保留 SQL Server 這道更換縫隙來因應。完整的棄用理由見 [`README.md`](README.md) 的「為什麼棄用舊技術棧」一節。

---

## 7. 完整性測試與相關報表的科目名稱取法（IDEAScript）

這一節記錄 2026-11-23 讀 `idea-script.bas` 與 `idea-tool.bas` 看到的做法。兩份腳本在這幾段的寫法相同，下面的行號先寫 `idea-script.bas`，括號裡是 `idea-tool.bas`。腳本看不出來的地方照實寫看不出來。現行 JET 怎麼做、哪些地方和舊版不同，見 [`../docs/jet-guide.md`](../docs/jet-guide.md) 第 4 節與第 17 節。

- **GL 彙總不帶名稱。** 完整性測試先把 GL 只依「會計科目編號_JE」彙總金額，結果是 `#GL_Account_Sum.IDM`，裡面沒有科目名稱。出處是 `Step1_Validation` 的第 4507 到 4518 行（第 6524 到 6535 行）。
- **名稱只來自 TB。** 彙總後的 GL 用科目編號接整份 TB，接法是兩邊的列都保留（`WI_JOIN_ALL_REC`），帶進來的名稱欄只有「會計科目名稱_TB」。出處是第 4522 到 4535 行（第 6539 到 6552 行）。舊版沒有任何一處用 GL 的「會計科目名稱_JE」補上 TB 缺的名稱。
- **全科目表。** `#Completeness_Check.IDM` 只留 `ACCOUNT_NUM_ALL`、「會計科目名稱_TB」、「試算表變動金額_TB」、「傳票金額_JE_SUM」與 `DIFF` 五欄，出處是第 4581 到 4593 行（第 6598 到 6610 行）。資料驗證報告的 `V_Report 5` 直接匯出這張表，在第 3732 到 3733 行（第 5692 到 5693 行）；底稿「step1 完整性測試」逐欄寫出同一張表，在第 9039 到 9065 行（第 10418 行起）。
- **差異表。** `#List_of_accounts_with_variance.IDM` 是從 `#Completeness_Check.IDM` 挑出 `@Abs( DIFF ) > 0` 的列，在第 998 到 999 行（第 1751 到 1752 行），所以名稱同樣只有 TB 的。資料驗證報告與底稿的「step1-3 完整性測試之差異說明」各寫 `ACCOUNT_NUM_ALL`、「會計科目名稱_TB」與 `DIFF` 三欄，在第 3761 到 3775 行與第 9234 到 9248 行（第 5722 到 5736 行與第 10613 到 10627 行）。
- **科目只在一邊出現。** 科目只在 GL 時，接不到 TB 的列，名稱欄沒有值，全科目表與差異表都顯示空白。科目只在 TB 時顯示 TB 的名稱。`ACCOUNT_NUM_ALL` 是 GL 的科目編號有值就用 GL 的、否則用 TB 的，在第 4561 到 4572 行（第 6578 到 6589 行）。
- **TB 與 GL 的名稱都空白。** 完整性測試不看 GL 的名稱，TB 的名稱空白時全科目表與差異表就是空白，沒有替代文字。
- **科目配對範本。** `Step1_Export_AccountMapFile` 逐列讀 `#Completeness_Check.IDM`，第一欄寫科目編號，第二欄的名稱去掉空白後不是空的就照寫，是空的就寫「Not in TB」，在第 4433 到 4453 行（第 6442 到 6462 行）。判斷的是名稱有沒有內容，不是科目在不在 TB，所以 TB 有這個科目但名稱空白時，舊版同樣寫「Not in TB」。
- **底稿的科目配對資訊。** 底稿「自動化工具-科目配對資訊」把審計員上傳的科目配對檔 `#AccountMapping.IDM` 逐欄逐列照寫，在第 9725 到 9766 行，沒有另外判斷名稱。所以審計員拿範本填好再上傳時，範本上的「Not in TB」會原樣出現在底稿。
- **同一個科目編號在 TB 有多列。** 腳本接 TB 之前沒有先把 TB 依科目彙總。這時 IDEA 取哪一列的名稱與金額，從腳本看不出來，要在 IDEA 實際執行才能確認。GL 同一個科目編號有多個名稱的情形不影響完整性測試，因為 GL 的名稱沒有帶進來。
- **預篩選「較少使用之科目」。** 依「會計科目編號_JE」加「會計科目名稱_JE」一起彙總，在第 6762 到 6767 行（第 8773 到 8778 行）。名稱來自 GL；同一個科目編號有多個名稱時各成一列，不合併。

### 和現行 JET 的差異與歸類

現行 JET 的規則以 [`../docs/jet-guide.md`](../docs/jet-guide.md) 第 4 節與第 7 節為準。使用者 2026-11-23 先決定同一個科目編號有多個名稱時取哪一個（裁定 2026-11-23-13），
同日再決定：科目名稱最低限制是不能有空白，在這個限制下和舊版的邏輯相同（裁定 2026-11-23-15）。
所以舊版本來就不會空白的表完全照舊版，舊版會空白的表才用 GL 的名稱補上。這裡補上那兩節沒有展開的細節。

- **哪些名稱算空白、怎麼排序。** 空白的名稱指沒有值、空字串或只有空白字元的名稱。升冪照文字的編碼由小到大比較，不看筆畫或注音，
  大小寫與全形半形算不同的字，一個名稱是另一個名稱的開頭時短的排前面。GL 只看納入測試的分錄。取到的名稱照來源的樣子顯示。
  用 GL 的名稱補上時，GL 同一個科目編號有多個名稱也照這個取法。
- **全科目表與差異表。** 全科目表是資料驗證報告的 `V_Report 5` 與底稿的 Step 1。差異表是畫面上的完整性差異清單、
  資料驗證報告的「step1-3 完整性測試之差異說明」與底稿的 Step 1-3。兩張表都先取 TB 的名稱，TB 沒有名稱或科目只在 GL 出現時取 GL 的名稱。
  欄名照舊版範本不改，例如 `V_Report 5` 仍是「會計科目名稱_TB」。
- **還會出現空白名稱的情形。** 同一個科目在 TB 與 GL 都沒有名稱時，全科目表與差異表的名稱是空白。沒有名稱可以顯示，舊版也是空白，
  JET 不另外補文字；資料預覽的科目配對也一樣。資料預覽另外在只出現在科目配對檔、檔案也沒有名稱的科目是空白，這種科目在 TB 與 GL 都沒有。
  除此之外，這一節提到的表都不會出現空白的科目名稱。
- **科目配對範本。** 名稱欄只看 TB 的名稱：TB 有名稱就寫，TB 沒有名稱或科目只在 GL 出現時寫「Not in TB」，不寫 GL 的名稱。
- **底稿「自動化工具-科目配對資訊」。** 和舊版一樣寫已儲存的科目配對的名稱，科目只在 GL 出現也一樣；已儲存的科目配對沒有名稱時寫「Not in TB」。
  匯入科目配對時「Not in TB」當成沒有科目名稱（裁定 2026-11-05-10），所以審計員用範本填好再匯入，底稿寫回的字和範本相同，
  審計員在範本自己填的名稱也照樣寫出。
- **在畫面的科目清單直接分類時存下的名稱。** 舊版沒有這個做法。存下的名稱和範本匯回時相同，只取 TB 的名稱；
  TB 沒有名稱或科目只在 GL 出現時不存名稱，畫面清單顯示的 GL 名稱不存進去，所以底稿和用範本配對一樣寫「Not in TB」。
  已儲存的名稱有內容時保留，審計員上傳的檔案寫的名稱不因為在畫面改分類而消失（裁定 2026-11-23-18）。
- **畫面上的科目配對清單與「配對檔未列的科目」。** 舊版沒有對應的畫面。名稱和差異表相同，TB 沒有名稱時顯示 GL 的名稱。
  資料預覽的科目配對先顯示已儲存的名稱，沒有存名稱時也照這個取法。
- **不套用的地方。** 預篩選「較少使用之科目」、逐筆分錄的明細，以及存進科目配對的名稱不套用這個取法。

歸類用 `docs/jet-guide.md` 第 17 節的五類：

| 項目 | 舊版 | 現行 JET | 歸類 |
|:---|:---|:---|:---|
| 全科目表的名稱 | 只帶 TB 的名稱；TB 沒有名稱或科目只在 GL 出現時空白 | 先取 TB 的名稱；TB 沒有名稱或科目只在 GL 出現時用 GL 的名稱補上；TB 與 GL 都沒有名稱時空白 | 已裁定的刻意差異（裁定 2026-11-23-15）：只有舊版會空白、而 GL 有名稱的科目不同 |
| 差異表的名稱 | 只有 TB 的名稱；TB 沒有名稱或科目只在 GL 出現時空白 | 和全科目表相同 | 已裁定的刻意差異（裁定 2026-11-23-15） |
| TB 同一個科目編號有多個名稱時取哪一個 | 腳本接 TB 之前沒有先依科目彙總，看不出取哪一列 | 排除空白的名稱後，取文字升冪排序的第一個 | 已裁定的刻意差異（裁定 2026-11-23-13） |
| 科目配對範本的名稱欄 | TB 的名稱有內容就照寫，沒有內容就寫「Not in TB」；不看 GL 的名稱，也不看科目在不在 TB | 相同 | 沒有差異（裁定 2026-11-05-40、2026-11-23-15） |
| 底稿科目配對資訊裡沒有名稱的科目 | 照審計員上傳的檔案寫；檔案來自舊版範本時是「Not in TB」，審計員自己把名稱留空時是空白 | 寫「Not in TB」 | 檔案來自範本時沒有差異；審計員自己留空的名稱，JET 照不能有空白的限制寫「Not in TB」（裁定 2026-11-23-15） |
| 底稿科目配對資訊裡，只在 GL 出現、審計員在上傳的檔案寫了名稱的科目 | 照檔案寫這個名稱 | 相同 | 沒有差異（裁定 2026-11-23-18） |
| 底稿科目配對資訊裡，TB 沒有名稱、GL 有名稱，而且是在畫面的科目清單直接分類的科目 | 沒有在畫面直接分類的做法；科目配對只由審計員上傳的 AccountMapping 檔案匯入（`Step2_Upload_AccountMapping_File`，`idea-script.bas` 第 4948 行起），檔案來自範本時這種科目寫「Not in TB」 | 畫面的科目清單照樣顯示 GL 的名稱，但儲存時只存 TB 的名稱，這種科目不存名稱，底稿寫「Not in TB」，和用範本配對相同 | Legacy 無對應項；結果和舊版用範本配對相同（裁定 2026-11-23-18） |
| 匯入科目配對檔之後，在畫面的科目清單改分類的科目 | 沒有在畫面改分類的做法；底稿照審計員上傳的檔案寫名稱（第 9725 到 9766 行） | 已儲存的名稱保留，底稿照樣寫檔案裡的名稱 | Legacy 無對應項；名稱和舊版照上傳的檔案寫相同（裁定 2026-11-23-18） |
| 預篩選「較少使用之科目」的分組 | 依科目編號加科目名稱分組 | 只依科目編號分組 | 已裁定的刻意差異（裁定 2026-11-05-49） |
