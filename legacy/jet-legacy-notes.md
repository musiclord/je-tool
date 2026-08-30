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
