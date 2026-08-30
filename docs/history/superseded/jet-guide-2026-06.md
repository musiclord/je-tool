# JET 開發指南 (Journal Entry Testing — Single Source of Truth)

本文件為 JET 新系統的**唯一深度參考**。涵蓋業務領域、規則規格、系統架構、資料策略、AI 協作方式與遷移計畫。

> **讀這份文件，不需要讀 `legacy/idea-script.bas` 的 11,000 行原始碼。**
> 如果你發現本文件有歧義或缺口，修本文件，不要去翻 VBA。

---

## 目錄

- [A. 業務與領域](#a-業務與領域)
  - [1. JET 是什麼](#1-jet-是什麼)
  - [2. 核心資料實體](#2-核心資料實體)
  - [3. 審計工作流程](#3-審計工作流程)
  - [4. 資料驗證規則](#4-資料驗證規則)
  - [5. 預篩選規則聲明式規格](#5-預篩選規則聲明式規格)
  - [6. 進階篩選邏輯](#6-進階篩選邏輯)
  - [7. 審計工作底稿](#7-審計工作底稿)
  - [8. 台灣在地化](#8-台灣在地化)
- [B. 技術決策](#b-技術決策)
  - [9. 技術約束與排除選項](#9-技術約束與排除選項)
  - [10. 為什麼選 .NET 10 + WinForms + WebView2 + HTML](#10-為什麼選-net-10--winforms--webview2--html)
- [C. 系統架構](#c-系統架構)
  - [11. 架構總覽](#11-架構總覽)
  - [12. 層級職責](#12-層級職責)
  - [13. SQLite / SQL Server Provider 策略](#13-sqlite--sql-server-provider-策略)
  - [14. 專案結構規劃](#14-專案結構規劃)
  - [15. 命名與分層原則](#15-命名與分層原則)
- [D. 開發與協作](#d-開發與協作)
  - [16. AI-agent 開發工作流](#16-ai-agent-開發工作流)
  - [17. 從 idea-script.bas 遷移的做法](#17-從-idea-scriptbas-遷移的做法)
  - [18. 欄位對照表](#18-欄位對照表)
  - [19. 術語對照](#19-術語對照)

---

# A. 業務與領域

## 1. JET 是什麼

**日記帳分錄測試 (Journal Entry Testing, JET)** 是依 ISA 240 / ISA 330 執行的**實質性審計程序**，用以因應「**管理階層凌駕控制 (Management Override of Controls)**」風險。

實務上，JET 針對一個會計期間內的**全部日記帳分錄**，依風險導向條件進行**全母體篩選**，辨識高風險或異常的分錄，供審計人員後續調查。

管理階層通常可透過下列方式操縱財務報表，這也正是 JET 要鎖定的風險面向：

- 記錄虛構或不當分錄（特別是期末結帳前後）
- 不當的估計調整
- 隱匿或延遲認列

一言以蔽之，JET 的工作就是**從數十萬筆分錄中，把「有風險的分錄」篩出來**。

---

## 1.5 資料量規模與處理原則 (Non-Negotiable)

JET 的所有設計決策都由**母體規模**驅動。本節列出的原則沒有妥協空間：違反它們的程式碼，無論寫得多漂亮，在真實案件上都會崩潰，必須拒絕合入。

### 1.5.1 規模假設

下表界定設計時必須涵蓋的資料量範圍。「典型」是常見案件規模，「上界」是系統在設計上必須能承受的極端情形。

| 維度 | 典型 | 上界 (必須能跑) |
|:---|:---|:---|
| GL rows | 約 **1,000 萬** 以下的 local persistent case | **10 億** rows 的 large-data / cloud case 設計上限 |
| TB accounts | 約 **1,000** | **1 萬** |
| AccountMapping rows | 數百 ～ 數千 | 1 萬 |
| SQLite path | 小於 1,000 萬 GL rows 的本機持久案件 | 不作為 10 億 row 執行引擎 |
| SQL Server path | 大於 1,000 萬 GL rows 或 cloud / shared data case | 10 億 row 等級的 set-based execution target，需採分析型索引策略 |
| Workpaper Excel 大小 | 數 MB ～ 數百 MB | 需以 OpenXML SAX writer 控制記憶體 |

ISA 240 要求對**全母體**執行 JET，不允許抽樣替代規則篩選；因此資料驗證、預篩選與自訂 filter 都必須以**全母體**為計算基底。

### 1.5.2 唯一允許的計算位置：DB 引擎 (Set-Based Pushdown)

**禁止**在 Application 層 (C# / LINQ) 對 GL/TB row 集合執行 V/R/Filter 規則。所有規則都必須以 SQL 表達，由 SQLite / SQL Server 引擎以 set-based 方式執行。

其背後有三個理由：
1. **記憶體**：1,000 萬 GL rows × 12 欄 × 平均 50 byte 已是數 GB 等級；C# Dictionary 額外 overhead 再 ×2~3。Application 程序會 OOM。10 億 row 等級只能交給資料庫引擎與分頁/匯出管線處理。
2. **效能**：DB 引擎的 hash join、index scan、parallel aggregation 比手寫 LINQ 快 1-3 個數量級。
3. **可重現**：SQL 規則可以單獨在 DB tool 中重跑驗證，C# LINQ 規則無法。

**正確形狀**（對應 §13 設計）：

```csharp
public interface IGlRepository
{
    Task<RuleResult> RunValidationAsync(ProjectId id, ValidationKind kind, CancellationToken ct);
    Task<RuleResult> RunPrescreenAsync(ProjectId id, RuleSpec rule, CancellationToken ct);
    Task<FilterResult> RunFilterAsync(ProjectId id, ScenarioSpec scenario, CancellationToken ct);
}
```

Repository 內部生成 SQL，例如借貸不平測試：

```sql
SELECT doc_num, SUM(amount_scaled) AS net_scaled
FROM target_gl_entry
WHERE project_id = @projectId
GROUP BY doc_num
HAVING ABS(SUM(amount_scaled)) > 0;
```

**禁止形狀**：

```csharp
var v2NullDocNums = gl.Count(r => string.IsNullOrWhiteSpace(GetGlVal(r, "docNum", mapping)));
// GL rows 被載入 Application 記憶體後以 LINQ 計算 → 規模禁忌
```

### 1.5.3 金額精度：資料庫計算一律使用 Scaled Integer

JET 的金額計算屬於審計證據，不接受浮點近似。SQLite 沒有原生 `decimal` storage class；若把金額存成 `REAL`，`SUM` / `ABS(diff)` 可能出現浮點誤差；若以文字保存 decimal，跨 provider 聚合語意也不穩定。

**DB 權威計算欄位一律使用 `BIGINT` scaled integer**：

| 欄位 | 型別 | 說明 |
|:---|:---|:---|
| `AmountScaled` | `BIGINT` | GL 標準金額，正借負貸 |
| `DebitAmountScaled` | `BIGINT` | 借方金額，非負 |
| `CreditAmountScaled` | `BIGINT` | 貸方金額，非負或依 schema 統一為非負 |
| `ChangeAmountScaled` | `BIGINT` | TB 期間變動額 |
| `MoneyScale` | `INTEGER` | 專案層級金額 scale，例如 `10000` |

規則：

1. 匯入時將來源金額轉成 `decimal` 驗證，再乘以固定 `MoneyScale`，四捨五入策略必須寫入 project config。
2. SQLite / SQL Server 的 V/R/Filter SQL 只對 scaled integer 做 `SUM`、`ABS`、比較與取模。
3. 前端、報表與工作底稿顯示金額時再除以 `MoneyScale` 還原。
4. 所有 provider 必須用同一組 fixture 驗證 scaled 結果一致。

連續零尾數測試也應使用整數取模，不使用 `RIGHT` / `REPEAT` 這類 provider 方言差異大的字串函式。

### 1.5.4 Bridge 不得搬運完整 row 集合

WebView2 ↔ .NET 的 `postMessage` 通道是 JSON-over-string；對 100 萬 row × 數十欄做 `JSON.stringify` 會：

- 在 JS 端 OOM
- 序列化耗時 10+ 秒、阻塞 UI thread
- 反序列化在 .NET 端再耗一次

**規則**：

| 動作 | 正式契約要求 |
|:---|:---|
| `import.gl.fromFile` / `import.tb.fromFile` | `{ filePath, fileName?, mode? }`；handler 透過 file reader streaming 直入 DB；payload 不帶 rows |
| `validate.run` | 回 summary 數字 + `resultRef`；明細透過 `query.validationDetailsPage` 分頁拉 |
| `prescreen.run` | 回每條規則命中數 + `resultRef`；明細走 `query.prescreenPage` |
| `filter.preview` | 回 count、voucherCount、summary、`previewRows` 上限 1000、`resultRef` |
| `query.*Page` | 使用 keyset paging：`{ projectId, cursor?, pageSize? }` → `{ rows, nextCursor }` |

Row-based demo/import action 只屬於歷史相容路徑，不得出現在正式 UI、測試資料管線或新程式碼。

### 1.5.5 Excel Workpaper 採 OpenXML SAX Writer

`export.workpaper` 預期輸出多工作表、合計可達數百 MB（明細層）。正式實作必須走 **OpenXML SAX writer**（例如 `DocumentFormat.OpenXml` `OpenXmlWriter`），大型明細列使用 inline string，禁止把整份 result set 載入 `DataTable` / `List<>` / DOM workbook 後再寫。

ClosedXML 可用於小型 summary sheet 實驗，但不得作為 large-data workpaper writer。

### 1.5.6 Session State 只保存輕量指標

Session store 只能保存 current projectId、current mappings、UI 暫態與最新 resultRef。它不得持有 GL/TB rows。GL/TB raw rows 一律落地 `staging_*`，標準化後的資料落地 `target_*`，規則結果落地 `result_*`。

### 1.5.7 自我檢查清單

每次新增或修改 handler 時，請逐項自問：

1. 我有沒有把任何 GL/TB row 集合載入 `List<>` / `Dictionary<>` 然後跑 LINQ？
2. 我有沒有讓 bridge payload 或 response 攜帶超過 1000 row 的明細？
3. 我有沒有建立 in-memory cache 取代 DB 查詢？
4. 我的金額計算是否全程使用 scaled integer，而不是 SQLite `REAL` 或文字 decimal 聚合？
5. 我寫的 SQL 在 1,000 萬筆本機 GL 與更大型 SQL Server case 上有合理執行形狀嗎？（有沒有用 index、有沒有避免 SELECT *、有沒有 keyset 分頁？）
6. SQL Server large-data table 是否評估 columnstore / rowstore 輔助索引？
7. 我的 Excel 寫入是否使用 OpenXML SAX writer？

任何一題違反上述安全形狀 → 設計需要重做。

---

## 2. 核心資料實體

整個 JET 系統只圍繞 **5 個核心實體**運作：GL、TB、AccountMapping、DateDimension 與 RuleResult。理解這五者的欄位與關係，就掌握了全系統的資料模型。

### 2.1 總帳分錄 (GL — General Ledger)

每一筆 GL 代表一張傳票中的一個分錄行，是 JET 的**主要分析對象**。

#### 必要欄位

| 標準欄位 | 型別 | 必填 | 說明 |
|:---|:---|:---|:---|
| `DocumentNumber` | string | ✅ | 傳票號碼 |
| `LineItem` | string |  | 同傳票內的分錄序號 |
| `Amount` | decimal | ✅ | 顯示 / DTO 用金額；DB 權威計算使用 `AmountScaled` |
| `AmountScaled` | long | ✅ | DB 標準金額，正借負貸，依 project `MoneyScale` 縮放 |
| `AccountCode` | string | ✅ | 會計科目編號 |
| `AccountName` | string |  | 會計科目名稱 |
| `DocumentDescription` | string |  | 傳票摘要 |
| `ApprovalDate` | date |  | 傳票核准日 (期末後核准、週末/假日核准規則必要) |
| `PostDate` | date |  | 總帳日期 |
| `VoucherDate` | date |  | 傳票日期 (選填；回溯過帳規則必要，比對過帳日是否早於傳票日) |
| `CreatedBy` | string |  | 編製人 (編製者彙總必要) |
| `ApprovedBy` | string |  | 核准人 |
| `SourceModule` | string |  | 來源子系統 (AP、AR、GL…) |
| `IsManual` | bool |  | 是否人工傳票 |

#### 衍生欄位 (匯入後計算)

| 欄位 | 計算 | 用途 |
|:---|:---|:---|
| `DebitAmountScaled` | `AmountScaled >= 0 ? AmountScaled : 0` | 編製者/罕用科目彙總 |
| `CreditAmountScaled` | `AmountScaled < 0 ? ABS(AmountScaled) : 0` | 編製者/罕用科目彙總 |
| `DrCr` | `AmountScaled >= 0 ? "DEBIT" : "CREDIT"` | 借貸方向 |

#### 金額模式 (四選一)

不同 ERP 記錄金額的方式不同，匯入時由使用者指定：

| 模式 | 來源欄位 | 轉換為標準 `Amount` 的規則 |
|:---|:---|:---|
| `SignedAmount` | 單一金額欄 | 直接使用 (正=借、負=貸) |
| `AmountWithSide` | 絕對值 + 借貸別欄 | 借貸別 = "D" 取正，"C" 取負 |
| `AmountWithFlag` | 絕對值 + 借方標誌 (0/1) | flag=1 取正，flag=0 取負 |
| `DualAmount` | 借方金額 + 貸方金額 | `Amount = Debit - Credit` |

### 2.2 試算表 (TB — Trial Balance)

各科目在會計期間的餘額彙總。用於**完整性測試 (completeness_test)**。

| 標準欄位 | 型別 | 說明 |
|:---|:---|:---|
| `AccountCode` | string | 科目代號 |
| `AccountName` | string | 科目名稱 |
| `ChangeAmount` | decimal | 顯示 / DTO 用期間淨變動額 |
| `ChangeAmountScaled` | long | DB 權威計算用期間淨變動額 |
| `OpeningBalance` / `ClosingBalance` | decimal | 顯示 / DTO 用期初 / 期末 |
| `OpeningBalanceScaled` / `ClosingBalanceScaled` | long | DB 權威計算用期初 / 期末 |
| `OpeningDebitBalanceScaled` / `OpeningCreditBalanceScaled` | long | DB 權威計算用期初借 / 期初貸 |
| `ClosingDebitBalanceScaled` / `ClosingCreditBalanceScaled` | long | DB 權威計算用期末借 / 期末貸 |
| `DebitAmountScaled` / `CreditAmountScaled` | long | DB 權威計算用本期借方 / 貸方合計 |

#### TB 變動金額計算模式 (匯入時決定)

| 模式 | 可用欄位 | `ChangeAmount` 計算 |
|:---|:---|:---|
| `DirectChange` | 變動金額 | 直接採用 |
| `OpenClose` | 期初 + 期末 | `Closing - Opening` |
| `DebitCredit` | 借方 + 貸方 | `Debit - Credit` |
| `OpenCloseBySide` | 期初借貸 + 期末借貸 | `(ClosingDr - ClosingCr) - (OpeningDr - OpeningCr)` |

> **領域背景:為何 TB 比對的是「本期變動」而非餘額。** 會計科目編號的開頭碼段決定它的時間語義。開頭 `1`–`3` 是資產／負債／權益,屬資產負債表科目,有期初／期末的**累計餘額**概念,本期變動 = 期末 − 期初。開頭 `4`–`7` 是收入／費用之類的損益表科目,記的是**當期數值**、每期歸零,當期數本身就是本期變動。
> 這正是上表 `OpenClose` / `OpenCloseBySide` 兩個模式存在的理由:把資產負債表科目的期初／期末推算成「本期變動」,讓它和 GL 的本期借貸彙總落在同一基礎上比對(完整性測試,§4);損益表科目的當期數則可直接視為變動。
> 這裡講的是領域語義。變動模式是在匯入時決定的(見上表),不是規則程式裡的科目碼分支。

### 2.3 科目配對表 (AccountMapping)

將企業科目對應至**標準化分類**，用於未預期借貸組合規則與科目配對分析（§6.1）。

| 欄位 | 說明 |
|:---|:---|
| `AccountCode` | GL 科目代號 |
| `AccountName` | 科目名稱 |
| `StandardizedCategory` | `Revenue` / `Receivables` / `Cash` / `Receipt in advance` / `Others` |

### 2.4 日期維度 (DateDimension)

審計期間每一天的屬性表，用於週末/假日過帳核准規則。

| 欄位 | 說明 |
|:---|:---|
| `DateKey` | YYYYMMDD |
| `FullDate` | 日期 |
| `DayOfWeek` | 1=日 … 7=六 |
| `IsWeekend` | 系統自動 (Sat / Sun) |
| `IsHoliday` | 由使用者上傳的假日曆覆寫 |
| `IsMakeupDay` | 補班日 (假日曆的例外) |
| `HolidayDesc` / `MakeupDayDesc` | 說明 |

假日／補班日的**名稱**（`HolidayDesc` / `MakeupDayDesc`）存於 `staging_calendar_raw_day.day_name`，由行事曆檔案匯入（§3.1）一併帶入；以 `dates` 陣列匯入的相容路徑名稱為 null。

### 2.5 規則結果 (RuleResult)

每條規則執行後的**標記 + 狀態**。

| 欄位 | 型別 | 說明 |
|:---|:---|:---|
| `RuleSlug` | string | 命名登錄表的 slug（例 `post_period_approval`，見 §4） |
| `DocumentNumber` | string | 被標記的傳票 |
| `LineItem` | string | 被標記的分錄行 (可為空，表示整張傳票) |
| `Status` | enum | `V` (有結果) / `N/A` (無結果或未執行) |
| `TagColumn` | string | 結果標記欄名，由 slug 推導 (例 `tag_post_period_approval`) |

**結果失效不變量（衍生資料,Non-Negotiable）**：規則結果（`result_rule_run`、`result_inf_sampling_test_sample`）是依當前 `target_*`、科目配對與行事曆算出的衍生資料,**必須永遠對應當前資料**。

有幾種操作會改寫這些上游:GL/TB re-import、re-commit mapping（兩者都會重投影）、科目配對匯入、行事曆匯入。這些操作**必須在改寫的同一交易內**清除規則結果,讓「資料已換、舊結果還在」的中間態不可能出現。上游若 rollback,清除也跟著回退,因此也不會有「結果已清、資料未換」的反向半態。清除後 `project.load.latestRuns.*` 回 null,前端顯示「未執行」並要求重跑。

實作上只有一個事實來源:在各上游交易內呼叫 `RuleRunResultReset.ClearWithinAsync`。日後新增任何會改寫上游 target 的路徑,都必須一併接上這個清除。

`source_row_number` 是唯一可以跨重投影持久化的批次穩定鍵。`entry_id`（AUTOINCREMENT）會在重投影後重新編號,不得當成跨重投影的持久參照——INF 抽樣即以 `source_row_number` 排序（見 §4）。

---

## 3. 審計工作流程

JET 的審計作業由五個循序步驟組成，從資料匯入一路推進到工作底稿產出：

```
Step 1  資料匯入與驗證   →  GL/TB → 標準化 → 四項資料驗證
Step 2  輔助檔案設定     →  AccountMapping / Holiday / MakeupDay → DateDimension
Step 3  預篩選           →  預篩選規則 (全母體打標)
Step 4  進階篩選         →  組合 tag + 自訂條件 → 最終測試母體
Step 5  工作底稿產出     →  Excel 多工作表
```

每個步驟的輸入 / 處理 / 產出已併入各節說明。

> **審計方法論對照(這套流程在服務什麼)。** 上述工具流程服務的是 ISA 對齊的會計分錄測試方法論。依審計實務,最終工作底稿(Step 5 產出)分成五段:① 母體完整性 + 借貸不平 + 編製者彙總、② RDE 可靠性測試(§4)、③ 高風險範圍條件彙總、④ 符合高風險條件之分錄測試、⑤ 財務報表關帳後調整分錄,最後納入 eAudIT。工具的匯入→驗證→預篩選→進階篩選→匯出,產出的就是這五段底稿所需的資料。
> **狀態:工作底稿匯出(Step 5)後端 writer 已實作,待 GUI 驗收**(子專案 E1:OpenXML SAX 串流寫出 15(條件)工作表,見 §7.2;前端現況詳 `docs/jet-frontend-description.md`)。這五段底稿的詳細分頁版面是設計目標;完整(已清洗)的版面與歷史實作見 `legacy/jet-legacy-notes.md`。

### 3.1 匯入格式與案件設定

正式匯入格式支援 `.xlsx`、`.csv` 與 `.txt`（`.txt` 內容視為 CSV 文字）。若未來新增格式，必須先更新本文件與 `docs/action-contract-manifest.md`，再補 reader/handler。

#### 3.1.1 文字檔（.csv / .txt）讀取規則

- **編碼偵測（確定性鏈，不做啟發式猜測）**：BOM（UTF-8 / UTF-16 LE/BE）→ 無 BOM 時以嚴格 UTF-8 解碼驗證取樣段，合法即 UTF-8 → 否則視為 Big5（CP950）。Big5 仍解不開 → 報 `file_read_error` 並建議來源另存 UTF-8。匯入參數可明確指定編碼（`utf-8` / `big5` / `utf-16`）覆寫偵測。
- **分隔符偵測**：候選 `,`、Tab、`;`、`|`；以引號感知方式取樣前若干邏輯列統計引號外出現次數，取各列一致者；並列時依固定優先序 `,` > Tab > `;` > `|`。全為 0 → 單欄檔（合法）。匯入參數可覆寫。
- **引號**：依 RFC 4180 處理——雙引號包覆的欄位內，分隔符與換行不切欄、`""` 跳脫為 `"`。這是金額含千分位逗號的 CSV 不會錯位的關鍵保證。
- 標頭正規化規則與 `.xlsx` 共用同一套（trim、空白標頭 → `COL_{n}`、重複標頭加 `_2`/`_3` 字尾），確保 mapping 階段欄名一字不差。

#### 3.1.2 金額欄位接受格式

匯入投影階段以 InvariantCulture `decimal` 解析（寬鬆策略，與審計員先行初步清理的工作習慣對齊）：

- 接受：千分位逗號（`4,353,170.00`）、前置正負號（`-50`、`+50`）、小數點。
- 接受：**會計格式零**——trim 後恰為單獨一個半形連字號 `-` 視為 0。這是 Excel 會計數字格式（內建 numFmtId 43 等，格式碼 `_-* #,##0.00_-;…;_-* "-"??_-`）對零的標準顯示，真實 PBC 的 TB 匯出普遍如此。僅此一個字元成立：全形 `－`、em-dash `—`、`--`、`-.`、`- 0` 仍拒絕。
- 不處理：貨幣記號（`NT$`、`$`、`元`…）、括號負數、全形數字、歐陸格式（`1.234,56`）——遇到即報投影錯誤（逐列指出欄位與原值），期待來源資料先完成初步清理。
- 解析成功後乘以專案 `MoneyScale` 轉 scaled integer（§1.5.3），權威計算只在 scaled BIGINT 上進行。

#### 3.1.3 日期欄位接受格式

判定順序固定（先到先得）；輸出一律 `yyyy-MM-dd` 字串：

1. ISO `yyyy-MM-dd`。
2. 顯式西元格式：`yyyy/M/d`、`yyyy.M.d`、8 位數 `yyyyMMdd`。
3. **民國年**（專案設定 `rocDateEnabled`，預設啟用）：3 位數年 + `/` 或 `.` 分隔（`114/6/11`），以及 7 位數 `1140611`（`1yy MMdd`）→ 年 +1911 轉西元。7 位數民國年與 Excel 日期序列值的數字範圍重疊，**民國年判定優先於序列值**；關閉 `rocDateEnabled` 時 7 位數回歸序列值判定。
4. Excel 日期序列值 `1`–`2958465`（OADate）。
5. 寬鬆 fallback（InvariantCulture `DateTime.TryParse`），但解析結果年份必須落在 1900–2100，否則視為解析失敗——避免 `114/6/11` 在民國年關閉時被默默吞成西元 0114 年。
6. 兩位數年（如 `11/05/06`）無法分辨民國/西元/日月序，**一律拒絕**並報投影錯誤。

#### 3.1.4 多來源合併（一個資料集可由多個檔案／工作表組成）

實務上一個年度的 GL 可能拆成多份交付：季別工作表（同一活頁簿的 Q1–Q4）、逐月 CSV、或多個單表 `.xlsx`。匯入模型維持「**一個資料集一個匯入批次**」不變式，批次由一到多個**來源**（source）組成：

- 第一個來源以 `mode:"replace"` 開立批次；其後的來源以 `mode:"append"` 加入同一批次。每個來源記錄於 `import_batch_source`（檔名、工作表、編碼、分隔符、列數、匯入時間），供介面顯示與審計留痕。
- 所有來源的**有效欄位集合必須一致**（順序無關；有效欄位的定義見 §3.1.5 收斂規則），不一致即拒絕（`column_mismatch`）——欄位配對是批次層級的概念，不允許「半個資料集換了欄位結構」。
- 附加的欄位驗證分**兩階段**：串流寫入前先比對雙方的**具名標頭**集合（快速失敗，不浪費一次大檔讀取）；串流完成後再以收斂後的有效欄位集合終檢（涵蓋「無標頭欄帶資料」的情形——有資料的欄位永不靜默消失）。終檢不符時 rollback 本次來源，既有批次完全不變。
- 附加會使下游失效（target 投影與已提交配對清除），與 replace 相同：母體變了,配對與規則結果必須重做。
- 暫存列保有兩個列號概念：`source_row_number` = 來源檔內的實際列號（投影錯誤訊息指回使用者看得到的列），`row_number` = 批次內單調遞增的排序鍵（INF 抽樣的穩定基礎；附加來源從既有最大值續編）。

#### 3.1.5 Excel（.xlsx）讀取規則與欄位集合收斂

`.xlsx` 一律以 **OpenXML SAX 串流讀取**（forward-only、不建 worksheet DOM）。只有單一讀取器,不按檔案大小分支:百萬列等級的真實 PBC 活頁簿與小檔走同一條路（實證:114MB、兩工作表合計 1,403,327 列）。檔案檢視（inspect）與讀欄名只讀到標頭列就停,因此檢視的回應時間與檔案大小無關。

讀取行為（與 CSV 讀取器共用標頭正規化，staging 輸出形狀一字不差）：

- **標頭列** = 第一個含非空萃取值的列（只有樣式、沒有值的列不算）。cell 依實際欄號對應；OOXML 允許省略列號／cell 參考（`r` 屬性），缺席時以連續計數遞補。
- **共用字串**：`sharedStrings.xml` 一次串流載入；只取 `<t>` 與 rich text run `<r><t>` 的文字，**排除 `<rPh>`（注音讀音 run）與 `<phoneticPr>`**——台灣 ERP 匯出的活頁簿常帶注音標記，混入會污染欄名與摘要。
- **cell 型別**：`t="s"`（共用字串）、`t="inlineStr"`、`t="str"`（公式字串結果）、`t="b"` → `true`/`false`、`t="e"` → 錯誤原文；無型別屬性 = 數值。公式 cell 取快取值 `<v>`；無快取值視為空（串流讀取不重算公式）。
- **數值正規化**：`<v>` 以 `double` 解析（容許科學記號）→ `(decimal)` 轉換 → InvariantCulture 字串（超出 decimal 範圍時退 round-trip `"R"` 格式）。double→decimal 轉換會吸收浮點殘影（`535.04999999999995` → `"535.05"`），這是 staging 顯示值與金額解析穩定的關鍵。
- **日期樣式判定**：`styles.xml` 一次解析；數值 cell 的樣式 numFmtId 屬日期格式（內建日期 id 集，或自訂格式碼在引號字面值／`[...]` 區段／反斜線跳脫**之外**含 y/m/d 記號）→ `FromOADate` 轉 `yyyy-MM-dd`；僅含 h/m/s → 時間值。**型別優先於樣式**（共用字串 cell 套了日期樣式仍是字串）。`date1904` 活頁簿序列值 +1462 校正。未套日期樣式的日期序列值留待投影階段的 `DateNormalizer` 以 OADate 規則兜底（§3.1.3 第 4 順位）。
- 只有樣式沒有值的 cell（`<c s="2"/>`）視為空；全空列跳過；`SourceRowNumber` = 工作表內實際列號。
- 工作表以名稱解析（不分大小寫）、缺省取第一個；chartsheet 不是資料工作表，從清單剔除。

**欄位集合收斂（批次有效欄位）**：staging 以欄名為鍵，因此「欄位是否存在」必須有跨來源穩定的定義——

- **具名標頭一律屬於批次欄位**，即使整欄無資料（具名空欄是來源 schema 的聲明）。
- 空白標頭的 **`COL_{n}` 佔位欄只在串流中實際觀察到 ≥1 個非空值時才屬於批次欄位**。標頭列縫隙產生的佔位欄如此，標頭範圍之外出現資料 cell 時 lazy 合成的佔位欄也如此（後者保證「有資料的欄絕不靜默丟棄」，且不依賴 `<dimension>` 元素的正確性）。
- 收斂在倉儲串流寫入結束後、於同一交易內完成,並回寫批次 `columns_json`。真實的動機案例是這樣:同一活頁簿的「上半年」表在 S、U 兩欄之間夾了一個空標頭、整欄無資料的 T 欄（佔位欄 `COL_20`），「下半年」表則沒有這一欄。兩張表的具名標頭集合完全相同,收斂後就能正常合併為一個批次。反過來,若 T 欄真有資料,終檢會誠實報 `column_mismatch`,而不是默默把欄丟掉。
- 收斂是 provider 中立的純函式（Domain `TabularHeaderNormalizer.FinalizeBatchColumns`）；SQL Server／DuckDB 倉儲實作直接重用。本規則生效前建立的批次 `columns_json` 可能殘留無資料佔位欄，無需遷移——對全空欄做配對本來就無意義。

#### 3.1.6 行事曆檔案匯入（事務所假日／補班表）

事務所行事曆表（假日表、補班/結帳日表）以 `import.holiday.fromFile` / `import.makeupDay.fromFile` 匯入，僅支援 `.xlsx`（範本帶樣式標題列；非 `.xlsx` → `unsupported_file_type`）。與一般匯入不同，這類範本的**標頭固定在第 2 列**（第 1 列為樣式標題），後端以 reader 的 `LeadingRowsToSkip=1` 略過第 1 列。

- 欄位辨識沿用既有的關鍵字命中：日期欄（`Date_of_Holiday` / `Date_of_MakeUpday`）必有，缺則 `projection_failed`；名稱欄（`Holiday_Name` / `MakeUpDay_Desc`）與 `IS_Holiday` 選用。假日在 `IS_Holiday` 欄存在時只收 `Y`（缺欄則全收），補班無此過濾。
- 多年度照單全收（不依檔名年度過濾）、同日去重；任一資料列日期非 `yyyy-MM-dd` → `projection_failed`（前 10 筆，不寫入）。寫入 `staging_calendar_raw_day`（含 `day_name`）為 replace 語意，並在同交易清除規則結果（§2.5 不變量）。完整契約見 `docs/action-contract-manifest.md`。

#### 3.1.7 授權編製人員清單匯入（子專案 C）

授權編製人員清單是查核團隊維護的單欄姓名名單，供「非授權編製人員」規則（§5、R10）比對 `created_by`。以 `import.authorizedPreparer.fromFile` 匯入，僅支援 `.xlsx`（單欄、英文標頭，如 `AUTHORIZED_PREPARER`）。

- **解析**：沿用既有 OpenXML SAX 讀取器與關鍵字命中欄位解析（鏡射科目配對／行事曆匯入範式），只認姓名欄；空白列略過；姓名 `TRIM` 正規化後存。
- **語意**：replace-only（重匯入全換）；匯入即投影。不寫 `import_batch`（不是 GL/TB 母體匯入，毋須批次概念）。
- **schema**（雙 provider，`IF NOT EXISTS` / `IF OBJECT_ID`，**不升 schema 版本**，沿用 `app_message_log`／`gl_control_total`／`result_filter_run` 先例）：`staging_authorized_preparer_raw_row`（staging）+ `target_authorized_preparer(name PRIMARY KEY)`（去重、TRIM 後存）。
- **失效**：重匯入時依賴名單的 R10 規則結果必須失效——授權清單 replace 與 `RuleRunResultReset.ClearWithinAsync` 在同一交易內執行，使相關 prescreen／filter 結果重算（§2.5 不變量）。
- **resume**：`project.load` 的 importState 輸出 `authorizedPreparer.rowCount`（取自 store 計數），供重開續作時還原名單匯入狀態。完整契約見 `docs/action-contract-manifest.md`。

Case config 必須能重新載入案件參數，至少保存：

- case metadata（客戶、期間、操作者、產業、報表準備基準日）
- field mapping（原始欄位到標準欄位）
- account pairing / classification settings（科目配對與分類設定）
- saved query / filter scenarios（已保存查詢與篩選情境）
- reloadable case parameters（validation / prescreen / export 可重跑所需參數）

### 3.2 專案組態持久化

JET 需要保存使用者在單一專案中的操作脈絡，避免重新開啟應用程式後遺失前次輸入。這項能力屬於架構基礎，不等同於 GL/TB 規則計算。

初期雛形只需建立本機 project configuration persistence：

- 保存 project metadata、欄位 mapping 草稿、流程狀態、使用者選項與未來可重跑參數。
- 儲存位置由 Infrastructure provider 管理；前端不得成為唯一狀態來源。
- SQLite 先承擔本機組態儲存雛形，後續若新增 DuckDB 或其他 provider，仍應透過同一組 application/domain 邊界替換。
- `docs/development-status.md` 記錄開發現況與未決事項、`docs/development-log.md` 記錄跨 session 的決策脈絡；正式領域規則與架構決策仍回寫本文件與 action manifest（文件體系見 `docs/README.md`）。

---

## 4. 資料驗證規則

四項資料驗證執行於 Step 1，目的是在進入風險篩選之前，先確認資料母體完整、平衡且可信。任何一項驗證失敗，都代表後續測試的基礎資料可能不可靠。

### 規則命名登錄表 (Rule Naming Registry)

JET 的規則一律以**具體名稱**識別，不使用 V/R/A 流水代號（歷史代號跨世代歧義：legacy 系統的 V1–V4 是空值計數、V5 是完整性測試，與後來重新編號的 V1–V4 指涉不同測試）。命名公約：

- **wire key**（JSON 屬性、filter `prescreenKey`）= lowerCamelCase
- **slug**（資料表、命名追溯）= snake_case
- **UI 與工作底稿分頁** = 中文名
- 程式內單一事實來源：`Domain/RuleCatalog.cs`

| slug | wire key | 中文名 | 類別 | 歷史代號 |
|:---|:---|:---|:---|:---|
| `completeness_test` | `completenessTest` | 完整性測試 | 資料驗證 | V1（legacy V5） |
| `doc_balance_test` | `docBalanceTest` | 借貸不平測試 | 資料驗證 | V2（legacy V6） |
| `inf_sampling_test` | `infSamplingTest` | INF 抽樣測試 | 資料驗證 | V3 |
| `null_records_test` | `nullRecordsTest` | 空值紀錄測試 | 資料驗證 | V4（legacy V1–V4） |
| `post_period_approval` | `postPeriodApproval` | 期末財報準備日後核准之分錄 | 預篩選（row tag） | R1 |
| `suspicious_keywords` | `suspiciousKeywords` | 分錄摘要出現特定描述 | 預篩選（row tag） | R2 |
| `unexpected_account_pair` | `unexpectedAccountPair` | 未預期出現之特定借貸組合 | 預篩選（row tag） | R3 |
| `trailing_zeros` | `trailingZeros` | 分錄金額中有連續零的尾數 | 預篩選（row tag） | R4 |
| `creator_summary` | `creatorSummary` | 依分錄編製者彙總 | 預篩選（彙總） | R5 |
| `rare_accounts` | `rareAccounts` | 較少使用之科目 | 預篩選（彙總） | R6 |
| `weekend_posting` / `weekend_approval` | `weekendPosting` / `weekendApproval` | 週末過帳／週末核准 | 預篩選（row tag） | R7 |
| `holiday_posting` / `holiday_approval` | `holidayPosting` / `holidayApproval` | 假日過帳／假日核准 | 預篩選（row tag） | R8 |
| `backdated_posting` | `backdatedPosting` | 回溯過帳（過帳日早於傳票日） | 預篩選（row tag） | R9 |
| `non_authorized_preparer` | `nonAuthorizedPreparer` | 非授權編製人員 | 預篩選（row tag） | R10 |
| `low_frequency_preparer` | `lowFrequencyPreparer` | 低頻編製者 | 預篩選（row tag） | R11 |
| `low_frequency_account` | `lowFrequencyAccount` | 低頻科目 | 預篩選（row tag） | R12 |
| `blank_description` | `blankDescription` | 摘要空白 | 預篩選（row tag） | （舊 wire 鍵 descNull） |
| `account_pair` | `accountPair`（filter type） | 科目配對分析 | 進階篩選條件 | A3＋§6.1 |
| `period_in_out` | `periodInOut`（filter type） | 期內／期外 | 進階篩選條件 | — |
| `custom_keywords` | `customKeywords`（filter type） | 自訂關鍵字 | 進階篩選條件 | A2 |
| `custom_trailing_zeros` | `customTrailingZeros`（filter type） | 自訂尾數位數 | 進階篩選條件 | A4 |
| `custom_preparer_entry_count` | `customPreparerEntryCount`（filter type） | 自訂編製人員張數 | 進階篩選條件 | — |
| `custom_account_entry_count` | `customAccountEntryCount`（filter type） | 自訂科目張數 | 進階篩選條件 | — |

歷史代號欄僅供回查 legacy 文件與舊版工作底稿，**不得**出現在 UI、wire contract、資料表名或新文件敘述中。

### 完整性測試 (completeness_test)

| 項目 | 內容 |
|:---|:---|
| **目的** | 兩段核對。part(a)：匯入母體的列數與金額合計在投影後未增減（控制總數對帳）。part(b)：GL 按科目加總應等於 TB 期間變動額；不等則代表 GL 母體不完整 |
| **邏輯（part a）** | 投影（`mapping.commit`）時一次累計並落地兩端控制總數：來源端（staging 匯入列數 + 原始金額欄合計）與母體端（`target_gl_entry` 列數 + 借方總額 + 貸方總額，scaled）；核對列數須相等、金額合計須相等 |
| **邏輯（part b）** | `FULL OUTER JOIN` GL_Sum_ByAccount × TB ON AccountCode；差異 = `TB.ChangeAmountScaled - GL.AmountScaledSum` |
| **異常條件** | part(a)：列數不一致 OR 金額不一致；part(b)：`ABS(差異) > 0` |
| **產出** | part(a)：來源／投影列數、借方總額、貸方總額，與「列數一致」「金額一致」布林（總額層級，不需明細分頁）。part(b)：差異科目清單 (科目 / TB 金額 / GL 金額 / 差異)，差異列附 `notInTb` 記號 |

> **Not-in-TB 具名化。** part(b) 差異中「在 GL 出現、TB 無對應科目」(GL 加總非零、TB 變動為 0) 的那一支，差異列加 `notInTb` 布林記號。這是字面值記號(供查核員辨識與底稿呈現)，不是獨立旗標欄。完整列舉全部 Not-in-TB 科目屬分頁能力(跨子專案)，本測試只負責逐列判斷與記號。

### 借貸不平測試 (doc_balance_test)

| 項目 | 內容 |
|:---|:---|
| **目的** | 每張傳票借貸應平衡；不平衡可能為資料品質問題 |
| **邏輯** | 按 `DocumentNumber` 加總 `AmountScaled`，篩 `SUM <> 0` 的傳票，取出完整明細 |
| **產出** | 不平衡傳票 + 其所有分錄 |

除了回報不平衡傳票的筆數，本測試在投影後額外提供最多 50 張不平衡傳票的明細樣本，供 UI 展開檢視。這份明細是衍生的顯示值，不得用作任何規則計算或抽樣的依據。SQLite 與 SQL Server 兩個 provider 的產出等價。

### INF 抽樣測試 (inf_sampling_test)

| 項目 | 內容 |
|:---|:---|
| **目的** | 隨機抽樣供人工驗證非財務欄位 (摘要、日期、科目名等) 可靠性（INF = Information Produced by the Entity） |
| **邏輯** | 以審計期間 GL 為母體，以 `(source_row_number × seed) mod 2147483647`（tiebreak `entry_id`）的確定性整數 hash 排序取 N 筆（預設 60，可設定；seed 預設 48271）。`source_row_number` 為跨重投影穩定鍵（`entry_id` 為 AUTOINCREMENT、重投影會重編，故不作主鍵），確保重投影後抽中的樣本一致。實作與 manifest `validate.run` 細節一致 |
| **產出** | 抽樣明細 |

INF 抽樣必須可重現。每次抽樣要保存 seed、抽中 key 與 runId，禁止使用不可重現的隨機函式或 SQL Server `TABLESAMPLE` 作為正式審計抽樣依據。

> **審計方法論背景:INF 抽樣對應 RDE 可靠性測試。** INF 抽樣是**攸關資料元素(Relevant Data Elements,RDE)可靠性測試**在審計上目前的實作形式。RDE 指被納入高風險篩選條件的欄位,分兩類。**財務類**包括會計科目編號／名稱、借貸別、金額,可靠性大多已在完整性測試比對 TB 時取得。**非財務類**包括過帳日期、摘要關鍵字、人工／自動註記、特定編製或核准人員,需要抽樣核對傳票附件來確認。
> 依 ISA 240 / ISA 330(KAEG),納入篩選的 RDE 必須在執行高風險篩選**之前**確認可靠性;但若還只是風險評估的預篩選(screening)階段,就不需要先確認。完整的逐欄可靠性核對程序(財務類 A、非財務類 B–G)屬於審計方法論,詳見 `legacy/jet-legacy-notes.md`;這裡的 INF 抽樣是它在工具內的可重現抽樣機制。

### 空值紀錄測試 (null_records_test)

| 項目 | 內容 |
|:---|:---|
| **目的** | 找出關鍵欄位為空的分錄 |
| **邏輯** | 分別篩 `AccountCode IS NULL` / `DocumentNumber IS NULL` / `DocumentDescription IS NULL`；另含第四旗標「日期區間外」：以**核准日**判定 `ApprovalDate IS NOT NULL AND (ApprovalDate < PeriodStart OR ApprovalDate > PeriodEnd)`（2026-06-23 決策，對齊舊 JET 工具的「Approval date out of period」；用核准日 `approval_date`／配對 `docDate`，非過帳日。核准日未配對則此旗標不命中） |
| **產出** | 三類空值清單與計數，加「日期區間外（核准日離期）」計數與明細 |

除了回報各類空值的筆數，本測試在投影後額外提供最多 50 列命中的明細樣本，供 UI 展開檢視。每列以 `issues` 標明命中了哪幾項檢查（同一列可能同時命中多項）。這份明細是衍生的顯示值，不得用作任何規則計算或抽樣的依據。SQLite 與 SQL Server 兩個 provider 的產出等價。

---

## 5. 預篩選規則聲明式規格

本節以**聲明式規格**描述每一條預篩選規則。實作（Command Handler / Repository Query / SQL）必須能單憑這份規格直接生成，完全無需回頭翻閱 `idea-script.bas`。規則識別一律用命名登錄表（§4）的 slug／wire key／中文名。

各規格欄位的意義如下：
- **Name**：規則名稱 (中文)
- **Slug / Wire key**：命名登錄表識別字
- **Rationale**：風險意義 (為何這是風險指標)
- **Preconditions**：執行前必須滿足的條件，未滿足則 `Status = N/A`
- **Input**：資料來源
- **Predicate / Aggregation**：核心邏輯 (以 SQL-ish 偽碼表示)
- **Output**：回傳結果形式 (tag / 彙總)
- **N/A When**：明確為 N/A 的條件
- **Work Paper Sheet**：匯出工作底稿的分頁名（中文規則名）

### 期末財報準備日後核准之分錄

| | |
|:---|:---|
| Name | 於期末財務報表準備日後核准之分錄 |
| Slug / Wire key | `post_period_approval` / `postPeriodApproval` |
| Rationale | 期末後才核准的分錄可能是操縱期末數字 |
| Preconditions | GL 含 `ApprovalDate`；專案設定 `LastAccountingPeriodDate` |
| Input | GL |
| Predicate | `WHERE ApprovalDate >= LastAccountingPeriodDate` |
| Output | Tag |
| N/A When | 無 `ApprovalDate` 欄位 OR 0 筆符合 |
| WP Sheet | `期末後核准` |

### 分錄摘要出現特定描述

| | |
|:---|:---|
| Name | 分錄摘要出現特定描述 |
| Slug / Wire key | `suspicious_keywords` / `suspiciousKeywords` |
| Rationale | 摘要含調整 / 沖銷 / 錯誤等關鍵字可能是異常 |
| Preconditions | GL 含 `DocumentDescription` |
| Input | GL |
| Predicate | `WHERE REGEX_MATCH(UPPER(TRIM(DocumentDescription)), KEYWORDS)` |
| Keywords | 見 [附錄：摘要關鍵字預設清單](#附錄摘要關鍵字預設清單) |
| Output | Tag |
| 自訂變體 | 進階篩選條件 `customKeywords`（使用者關鍵字，§6.2） |
| WP Sheet | `摘要特定描述` |

### 未預期出現之特定借貸組合

| | |
|:---|:---|
| Name | 未預期出現之特定借貸組合 |
| Slug / Wire key | `unexpected_account_pair` / `unexpectedAccountPair` |
| Rationale | 「Revenue 貸方 + Receivables/Cash/Receipt in advance 借方」為虛假收入最常見模式 |
| Preconditions | AccountMapping 已匯入且包含 Revenue 與至少一個 (Receivables / Cash / Receipt in advance) 類 |
| Input | GL ⨝ AccountMapping |
| Predicate | **Step 1**：子集 CreditSet = `WHERE Category = 'Revenue' AND AmountScaled < 0`<br>**Step 2**：子集 DebitSet = `WHERE Category IN ('Receivables','Cash','Receipt in advance') AND AmountScaled >= 0`<br>**Step 3**：同一 `DocumentNumber` 同時出現在 CreditSet 與 DebitSet |
| Output | Tag (標記在符合的那些分錄行上) |
| 借貸側判定 | `AmountScaled >= 0` 屬借方側、`< 0` 屬貸方側——與 `DrCr` 推導及科目配對分析（§6.1）一致（2026-06-11 統一裁決：0 元分錄歸借方側） |
| 自訂變體 | 進階篩選條件 `accountPair`（使用者指定分類與模式，§6.1） |
| WP Sheet | `未預期借貸組合` |

### 分錄金額中有連續零的尾數

| | |
|:---|:---|
| Name | 分錄金額中有連續零的尾數 |
| Slug / Wire key | `trailing_zeros` / `trailingZeros` |
| Rationale | 整數金額 (如 10,000、1,000,000) 可能為估計或人為捏造。有效高風險條件須與授權金額門檻組合(圓整數**且**大額);以進階篩選 `customTrailingZeros(N)` AND `NumRange(金額≥門檻)` 表達。 |
| Preconditions | 無 |
| Input | GL |
| Predicate | **Step 1**：固定預設 6 個尾數 0(`TrailingZeroThreshold.DefaultZerosThreshold`;可設定性走進階篩選 `customTrailingZeros` 1–12)。<br>**Step 2**：整數取模 `amount_scaled % @zeroModulus = 0`；`@zeroModulus = MoneyScale × 10^N`(N 個 0;6 個 0 ⟺ 可被 10^6 整除)。 |
| Output | Tag |
| 自訂變體 | 進階篩選條件 `customTrailingZeros`（固定位數 1–12 取代動態計算，§6.2） |
| WP Sheet | `連續零尾數` |

### 依分錄編製者彙總

| | |
|:---|:---|
| Name | 依分錄編製者彙總分錄 |
| Slug / Wire key | `creator_summary` / `creatorSummary` |
| Rationale | 分錄集中於少數人員可能代表職能分離不足 |
| Preconditions | GL 含 `CreatedBy` |
| Input | GL |
| Aggregation | `GROUP BY CreatedBy` (若有 `IsManual` 則 `GROUP BY CreatedBy, IsManual`)<br>`SELECT SUM(DebitAmountScaled), SUM(CreditAmountScaled), COUNT(*)` |
| Output | 彙總表 (非 tag) — 供審計人員判讀，不可作進階篩選列述詞 |
| WP Sheet | `編製者彙總` |

### 較少使用之科目

| | |
|:---|:---|
| Name | 較少使用之科目 |
| Slug / Wire key | `rare_accounts` / `rareAccounts` |
| Rationale | 低頻率使用的科目可能被用來隱藏不當分錄 |
| Preconditions | 無 |
| Input | GL |
| Aggregation | `GROUP BY AccountCode, AccountName`；`SELECT COUNT(*), SUM(DebitAmountScaled), SUM(CreditAmountScaled)`；`ORDER BY COUNT(*) ASC` |
| Output | 彙總表 (非 tag)，不可作進階篩選列述詞 |
| WP Sheet | `罕用科目` |

### 週末過帳 / 核准之分錄

| | |
|:---|:---|
| Name | 於週末過帳或核准的分錄 |
| Slug / Wire key | `weekend_posting`（總帳日）與 `weekend_approval`（核准日）/ `weekendPosting`、`weekendApproval`（wire 摘要物件 `weekendActivity`） |
| Rationale | 正常企業運作不應週末處理傳票 |
| Preconditions | GL 含 `ApprovalDate` 或 `PostDate` |
| Input | GL ⨝ DateDimension |
| Predicate | `WHERE DateDimension.IsWeekend = TRUE AND DateDimension.IsMakeupDay = FALSE` (台灣：補班日排除) |
| Output | Tag（過帳／核准各一） |
| WP Sheet | `週末過帳核准` |

### 假日過帳 / 核准之分錄

| | |
|:---|:---|
| Name | 於國定假日過帳或核准的分錄 |
| Slug / Wire key | `holiday_posting`（總帳日）與 `holiday_approval`（核准日）/ `holidayPosting`、`holidayApproval`（wire 摘要物件 `holidayActivity`） |
| Rationale | 假日不應處理傳票；含彈性假日 |
| Preconditions | 使用者已上傳假日曆；GL 含 `ApprovalDate` 或 `PostDate` |
| Input | GL ⨝ DateDimension |
| Predicate | `WHERE DateDimension.IsHoliday = TRUE` |
| Output | Tag（過帳／核准各一）+ 假日名稱（契約擴充待定，見 manifest `import.holiday` 備註） |
| WP Sheet | `假日過帳核准` |

### 回溯過帳之分錄

| | |
|:---|:---|
| Name | 過帳日早於傳票日之分錄（回溯過帳） |
| Slug / Wire key | `backdated_posting` / `backdatedPosting` |
| Rationale | 過帳日早於傳票（憑證）日，可能是回溯記帳以操縱會計期間歸屬，為公認舞弊跡象（屬非財務 RDE，對應事務所底稿高風險條件「過帳日期早於傳票日期」） |
| Preconditions | GL 含選填 `VoucherDate` 欄；傳票日為空者不命中（NULL 安全） |
| Input | GL |
| Predicate | `WHERE voucher_date IS NOT NULL AND post_date < voucher_date`（日期比較沿用既有過帳／核准日的正規化日期作法） |
| Output | Tag — 落地 `result_rule_run`，可作進階篩選列述詞 |
| N/A When | 無 `VoucherDate` 欄位（來源未配對傳票日）OR 0 筆符合 |
| WP Sheet | `回溯過帳` |

### 非授權編製人員

| | |
|:---|:---|
| Name | 由非授權人員編製之分錄 |
| Slug / Wire key | `non_authorized_preparer` / `nonAuthorizedPreparer` |
| Rationale | 分錄由不在查核團隊維護之授權名單內的人員編製，可能是越權或不當記帳（屬非財務 RDE，對應事務所底稿「傳票建立人員不在授權清單」） |
| Preconditions | 已匯入授權編製人員清單（見下文「授權編製人員清單匯入」）；GL 含 `CreatedBy` |
| Input | GL ⨝ 授權編製人員清單 |
| Predicate | `WHERE created_by IS NOT NULL AND TRIM(created_by) <> '' AND TRIM(created_by) NOT IN (SELECT name FROM target_authorized_preparer)`（`created_by` 空白者不命中——無從判定授權與否） |
| Output | Tag — 落地 `result_rule_run`，可作進階篩選列述詞 |
| N/A When | 授權清單尚未匯入（`status="na"`、`naReason` 說明未匯入名單）OR 0 筆符合 |
| 雙重閘控 | 預篩選端以 `HasAuthorizedPreparers`（名單表非空）放行，未匯入則整條 `na`；filter 端 validator 對空名單回 `invalid_scenario`，述詞另以 `EXISTS` 自保，避免「`NOT IN` 空集合 → 全部命中」的反轉風險（鏡射 `unexpectedAccountPair` 需科目配對的閘控模式） |
| escalation | 此 row-tag 可入篩選情境；查核員存「非授權編製人員」情境後，用 D1 `query.filterHitsPage` 取回**全部命中分錄**（C 不另造 escalation 基礎設施） |
| WP Sheet | `非授權編製人員` |

### 低頻編製者

| | |
|:---|:---|
| Name | 由低頻編製者編製之分錄 |
| Slug / Wire key | `low_frequency_preparer` / `lowFrequencyPreparer` |
| Rationale | 全年編製分錄筆數過少之人員（< 12 筆，即 ≤ 11），其編製之分錄風險較高（對應事務所底稿「編製人員張數 ≤ 11」） |
| Preconditions | GL 含 `CreatedBy` |
| Input | GL |
| Predicate | `WHERE created_by IN (SELECT created_by FROM target_gl_entry GROUP BY created_by HAVING COUNT(*) <= 11)`（門檻取 Domain 常數 `PreparerFrequency.DefaultMaxEntries = 11`，參數綁定） |
| Output | Tag — 落地 `result_rule_run`，可作進階篩選列述詞 |
| N/A When | 0 筆符合 |
| 自訂變體 | 進階篩選條件 `customPreparerEntryCount`（門檻 `maxEntries ≥ 1` 由查核員輸入，取代固定的 11；同一述詞、單一事實，§6.2） |
| WP Sheet | `低頻編製者` |

### 低頻科目

| | |
|:---|:---|
| Name | 屬低頻科目之分錄 |
| Slug / Wire key | `low_frequency_account` / `lowFrequencyAccount` |
| Rationale | 全期分錄筆數過少之科目（< 12 筆,即 ≤ 11),其分錄較易藏匿違規或錯誤,風險較高(對應事務所底稿高風險條件 step3 C9「較少使用之科目」,科目張數 ≤ 11) |
| Preconditions | GL 含 `AccountCode` |
| Input | GL |
| Predicate | `WHERE account_code IN (SELECT account_code FROM target_gl_entry GROUP BY account_code HAVING COUNT(*) <= 11)`(門檻取 Domain 常數 `AccountFrequency.DefaultMaxEntries = 11`,參數綁定;純 ANSI,自足無外部清單,無 `NOT IN` 反轉風險) |
| Output | Tag — 落地 `result_rule_run`,可作進階篩選列述詞 |
| N/A When | 0 筆符合 |
| 自訂變體 | 進階篩選條件 `customAccountEntryCount`(門檻 `maxEntries ≥ 1` 由查核員輸入,取代固定的 11;同一述詞、單一事實,§6.2) |
| escalation | 此 row-tag 可入篩選情境;查核員存「低頻科目」情境後,用 D1 `query.filterHitsPage` 取回**全部命中分錄**(本補遺不另造分頁) |
| 與 `rare_accounts`(R6)關係 | 兩者並存不取代:`rare_accounts` 為 top-50 **彙總視圖**(`RuleShape.Aggregate`,供判讀,**不可作進階篩選列述詞**);`low_frequency_account` 為同維度的 **RowTag 版本**(可作列述詞、可入情境)。鏡射 `low_frequency_preparer`(R11)與 `creator_summary`(R5)彙總並存的模式 |
| WP Sheet | `罕用科目` |

### 摘要空白

| | |
|:---|:---|
| Name | 摘要空白之分錄 |
| Slug / Wire key | `blank_description` / `blankDescription` |
| Rationale | 摘要缺漏的分錄無法說明交易性質，屬資料品質與風險雙重指標 |
| Preconditions | 無 |
| Input | GL |
| Predicate | `WHERE DocumentDescription IS NULL OR TRIM(DocumentDescription) = ''` |
| Output | Tag |
| WP Sheet | `摘要空白` |

> 原 A2–A4「自訂變體」不再是獨立的預篩選規則：自訂關鍵字（`customKeywords`）、自訂科目配對（`accountPair`）、自訂尾數位數（`customTrailingZeros`）一律以**進階篩選條件**呈現（§6），與預設規則可同時使用、不再互斥——預篩選跑預設規格供初判，進階篩選用自訂參數收斂母體。

#### 與 legacy 的已知差異（待業務確認是否補齊）

對照 `legacy/idea-script.bas`，現行預篩選規則涵蓋了 legacy 的對應意圖（罕見人員/科目、週末/假日、人工傳票等），下列兩處為**刻意記錄、尚未補齊**的細微差異，非缺陷；若業務確認需要，屬規則新增，須先更新本 §5 與 `docs/action-contract-manifest.md` 後另案實作（不在資料流程加固範圍內）：

1. **編製者列級標記已補齊（子專案 C）**：`creator_summary` 仍是 top-50 彙總表供判讀（不可作進階篩選列述詞，本輪不動）；「篩出某低頻人員的全部分錄」與「篩出非授權人員的全部分錄」現由兩條 RowTag 規則 `low_frequency_preparer`（R11，固定 ≤ 11）與 `non_authorized_preparer`（R10，名單比對）支援，並可存成篩選情境後以 D1 `filterHitsPage` 取回全部分錄。低頻門檻另有進階篩選條件 `customPreparerEntryCount` 可自訂。
2. **尾數比對僅限「連續零」**：`trailing_zeros` 與進階篩選 `customTrailingZeros` 只判尾端連續零；legacy 另有**任意尾數樣式**（如結尾 9、99）比對。若要支援，需在進階篩選新增尾數樣式條件。

### 結果狀態碼

| Status | 意義 |
|:---|:---|
| `V` | 已執行且有結果 |
| `N/A` | 未執行 (缺欄位/設定檔) 或已執行但 0 筆命中 |

### 附錄：摘要關鍵字預設清單

| 關鍵字 | 語言 | 審計意義 |
|:---|:---|:---|
| ADJ | EN | Adjustment |
| REV | EN | Reversal |
| RECLASS | EN | Reclassification |
| SUSPENSE | EN | Suspense |
| ERROR | EN | Error |
| WRONG | EN | Wrong |
| 調整 | ZH | 調整分錄 |
| 迴轉 | ZH | 迴轉分錄 |
| 沖銷 | ZH | 沖銷分錄 |
| 重分類 | ZH | 科目重分類 |
| 避險 | ZH | 避險交易 |
| 重編 | ZH | 重新編製 |
| 錯誤 | ZH | 錯誤更正 |
| 計畫外 | ZH | 計畫外的調整 |
| 預算外 | ZH | 超出預算的調整 |
| 帳外 | ZH | 帳外交易 |

比對方式：`REGEX_MATCH(UPPER(TRIM(DocumentDescription)), '<關鍵字 1>|<關鍵字 2>|…')`

---

## 6. 進階篩選邏輯

進階篩選位於 Step 4。審計人員以預篩選結果為基礎，疊加各種條件進行組合，最終收斂出**要實際測試的分錄母體**。條件型別與 AST 形狀的 wire 契約見 `docs/action-contract-manifest.md` Filter / Criteria 章節。

### 6.1 科目配對分析 (三種模式)

filter 條件型別 `accountPair`（slug `account_pair`）。前置條件：科目配對表已匯入；分類值限 §2.3 白名單。借貸側判定統一為 `AmountScaled >= 0` 屬借方側、`< 0` 屬貸方側（與 `DrCr` 推導一致）。

| pairMode | 邏輯 | 使用時機 |
|:---|:---|:---|
| **exact 精確** | 同一傳票同時有「指定借方分類 + AmountScaled >= 0」與「指定貸方分類 + AmountScaled < 0」的分錄 → 兩類分錄行都輸出 | 已知可疑的借+貸組合 |
| **debitAnchor 借方錨定** | 先找出含「指定借方分類 + AmountScaled >= 0」的傳票 → 輸出借方分錄 + 同傳票所有貸方分錄 (AmountScaled < 0) | 已知可疑的借方，想看對方科目 |
| **creditAnchor 貸方錨定** | 先找出含「指定貸方分類 + AmountScaled < 0」的傳票 → 輸出貸方分錄 + 同傳票所有借方分錄 (AmountScaled >= 0) | 已知可疑的貸方，想看對方科目 |

### 6.2 可疊加的自訂條件

| 條件（filter type） | 邏輯 |
|:---|:---|
| 預篩選標記組合（`prescreen`） | row-tag 規則的述詞即時計算，如 `期末後核准 AND 未預期借貸組合`、`摘要特定描述 OR 連續零尾數` |
| 日期區間（`dateRange`） | `PostDate / ApprovalDate BETWEEN … AND …` |
| 金額區間（`numRange`） | `ABS(AmountScaled) BETWEEN … AND …` |
| 文字比對（`text`） | 指定欄位 contains / exact / notContains / notExact，不分大小寫 |
| 借 / 貸限定（`drCrOnly`） | `DrCr = 'DEBIT'` 或 `'CREDIT'` |
| 人工 / 自動（`manualAuto`） | `IsManual = TRUE/FALSE`（NULL 永不匹配） |
| 期內 / 期外（`periodInOut`） | `PostDate` 在 / 不在會計期間（含邊界；NULL 兩側皆不命中） |
| 自訂關鍵字（`customKeywords`，原 A2） | 同摘要特定描述述詞，關鍵字為使用者輸入 |
| 自訂尾數位數（`customTrailingZeros`，原 A4） | 同連續零尾數述詞，位數可設定 1–12（取代 prescreen 的固定預設 6）；與 `NumRange` 組合即授權金額門檻閘 |
| 自訂編製人員張數（`customPreparerEntryCount`） | 同低頻編製者述詞（`COUNT(*) <= @maxEntries`，依 `created_by` 分組），門檻 `maxEntries ≥ 1` 由使用者輸入，取代 prescreen 的固定預設 11 |
| 自訂科目張數（`customAccountEntryCount`） | 同低頻科目述詞(`COUNT(*) <= @maxEntries`,依 `account_code` 分組),門檻 `maxEntries ≥ 1` 由使用者輸入,取代 prescreen 的固定預設 11 |
| 科目配對分析（`accountPair`，原 A3） | 見 §6.1 三模式 |

---

## 7. 審計工作底稿

Step 5 將完整的測試結果匯出為 Excel 檔案，作為審計證據的書面留存。檔案包含下列工作表：

| Sheet | 內容 |
|:---|:---|
| `Engagement Overview` | 客戶、期間、財報準備起始日、編製人與編製日 |
| `Data Overview` | GL 筆數/借方/貸方/淨額、TB 科目數 |
| `Validation Overview` | 四項資料驗證異常計數 |
| `完整性測試明細` | 完整性測試差異明細 |
| `借貸不平明細` | 借貸不平測試的不平衡傳票 |
| `INF 抽樣明細` | INF 抽樣測試的抽中樣本 |
| 各預篩選規則分頁 | 分頁名 = 命名登錄表的中文 WP Sheet 名（`期末後核准`、`摘要特定描述`…，見 §5；Status = V 才有資料） |
| 各篩選情境分頁 | 已保存篩選情境的命中明細（情境名 + 查核理由） |
| `Account Mapping Info` | 使用的科目配對表 |
| `Field Mapping Info` | 原始欄位 → 標準欄位對應、規則 slug ↔ 中文分頁名對照（命名追溯） |

### 7.1 欄位名稱與可追溯性

審計員最終底稿應在需要追溯來源資料時顯示**原始資料欄位名稱**。標準化欄位主要用於內部查詢、完整性測試、預篩選規則、進階篩選與 provider-independent execution。`Field Mapping Info` 必須清楚連結原始欄位與標準欄位，避免底稿只剩內部欄位名稱而難以回查客戶資料。

### 7.2 匯出底稿 writer(子專案 E1：已實作，待 GUI 驗收）

上面 §7 的 Sheet 清單是底稿的**邏輯內容目標**。實際產出的 `.xlsx`(固定命名 `{客戶}_{yyyymmddHHmmss}_WorkingPaper.xlsx`)由 Infrastructure 的 `WorkpaperWriter` 串流寫出，對齊事務所「JE Testing Tool」樣本的版面，含封面 + step1~step5 測試底稿 + 三張參考資料表。`export.*` 不再是委派前端的 stub：`export.workpaperStream` 由 Application handler 編排、由 writer 串流寫檔。**程式碼已落地、自動化測試全綠，GUI 目視驗收尚未進行。**

> **狀態誠實聲明：** 本節描述的是已實作並通過自動化測試的後端能力。逐表 Excel 目視對齊樣本、大母體匯出不爆記憶體等 GUI 互動仍待 Windows 端人工驗收（`docs/windows-handoff.md` 任務卡），尚未驗收。欄位配對匯入 round-trip（讀回 Field Mapping Info 重建配對）延後 **E2**，不在本棒範圍。

#### writer 是 deep module，sheet 是資料不是分支

`WorkpaperWriter` 對外只暴露窄介面 `WriteAsync(Stream, context) → ExportStats`，內部隱藏全部 OpenXML SAX 細節（shared strings inline、styles、merged cells、number formats、串流）。資料表沿用單一 `EmitTableSheet` 原語（表頭區塊 + 欄標 + 一個「列來源」委派，委派內部 keyset 逐頁取 repo、yield cell 陣列）；封面/固定文字/手填骨架是各自的小 emitter。**條件表（step1-3-1 差異≠0 才出）是 orchestration 層的 guard，不是 god-function 內的特例分支**——sheet 清單由 orchestrator 依資料動態決定要 emit 哪些，emitter 本身無「我是第幾張表」的分支。

#### 串流不全載入（與 §1.5.5 一致）

writer 走 `DocumentFormat.OpenXml` 的 SAX（`OpenXmlWriter`），大型明細列用 inline string，逐頁逐列寫，**不把整份 result set 載入 `DataTable` / `List<>` / DOM workbook**。百萬列傳票/明細逐頁流過寫出器，記憶體有界。**ClosedXML 僅限 dev fixture（`DemoWorkbookWriter`），不得作為底稿寫出器**（鐵律，§1.5.5）。

#### writer 是查詢的消費者，不重做業務邏輯

writer 注入既有 ProviderRouting 查詢 repo 與 store，對 repo keyset 逐頁取、逐列寫；scaled→顯示值換算（`(decimal)scaled / moneyScale`）在 writer 內，屬純算術，**不在 writer 內重算任何規則**。各表的 JET 資料來源：

| 工作表 | 來源 |
|:---|:---|
| step1 完整性測試（全科目） | `query.completenessDiffPage`（逐頁，全科目） |
| step1-1 借貸不平測試 | 結論文字 + `query.docBalancePage`（**有不平傳票才 emit 例外表**） |
| step1-2 分錄編製人員說明 | **全編製人員彙總查詢（E1 新增，去 LIMIT 不截斷）** |
| step1-3 完整性差異說明 / step1-3-1 完整性差異調節 | `query.completenessDiffPage` WHERE diff≠0；**step1-3-1 為條件表，差異≠0 才 emit** |
| step2 可靠性測試 | `query.infSamplePage`（抽樣行）；**金額寫借/貸兩欄**（E=借方、F=貸方），對齊 D1 已回的 `DebitScaled`/`CreditScaled`，不寫借貸代號+單欄 |
| step3 高風險條件彙總 | `query.tagMatrixScenarios` |
| step4 符合高風險條件傳票 | `query.tagMatrixVoucherPage`（逐頁；C1..CN 以 matchedPositions 標 Y） |
| step4-1 符合高風險條件傳票明細 | `query.tagMatrixRowPage`（逐頁）+ **動態 `C{position}_TAG` 欄**（僅 emit 有行層命中的 position 之欄；以 matchedPositions 標 Y） |
| 自動化工具-檔案欄位資訊 | 欄位配對設定 + `GlCanonicalNames`（正準中文名） |
| 自動化工具-假期假日資訊 | 行事曆 store（週末表 + 假日/補班表） |
| 自動化工具-科目配對資訊 | 科目配對 store；**GL 有、TB 無的科目 `GL_NAME` 寫字面值 `Not in TB`**（B 已落地的 Not-in-TB 記號） |

#### 正準中文名單一事實來源（Domain）

Field Mapping Info 與（E2）round-trip 都需要 logical key ↔ 正準中文名（`docNum`↔`傳票號碼_JE`、`postDate`↔`總帳日期_JE`…；TB 側 `會計科目編號_TB` 等）的對照。`Domain/GlCanonicalNames`（logical key → 正準中文名）為單一資料結構、匯出與日後 round-trip 共用，與既有 `GlFieldWhitelist`（logical key → 實體欄）分立但概念相鄰。

#### 多情境矩陣的惰性 materialize 由 handler 負責

step3/4/4-1 的高風險矩陣來自 D2 的 `tagMatrix*` 查詢，其「全部已存情境須先落地命中」的惰性補算由 handler（共用 `FilterRunMaterializeService`）負責，writer 只讀已備妥的查詢結果，不觸發 materialize。

#### 手填欄一律留空

方法學標為手填的欄位（step1-2 部門/職稱/說明、step1-3 原因/調節/調節後、step1-3-1 前期損益金額、step2 結果 A–G/詳細說明、step4 P–U、step5 內文）由 writer 寫出空白骨架，供查核員填寫；E1 不自動計算。封面 CAATs 段只寫**檔名字串** `{客戶}_CAATS_JE_WP_{yyyymmdd}.docx`，不產生 docx（外部、查核員自備）。

完整逐表內容規格、來源對碼與設計理由見 `docs/specs/2026-06-21-workpaper-export-writer-design.md`；wire 契約（`export.workpaperStream`、`host.selectSavePath`）見 `docs/action-contract-manifest.md`。

---

## 8. 台灣在地化

JET 針對台灣審計環境需要處理彈性假日、補班日與中文關鍵字等在地化規則。

### 8.1 彈性假日與補班日

- **彈性假日**：國定假日遇週二/週四時，前/後的週一或週五放假
- **補班日**：因彈性假日而改為上班的週六

實作：
- 週末規則必須排除 `IsMakeupDay = TRUE` 的日期 (補班週六實為工作日)
- 假日規則必須納入 `IsHoliday = TRUE` 的工作日 (彈性放假的平日)

### 8.2 中文摘要關鍵字

摘要特定描述規則的預設關鍵字清單已含台灣審計常見中文字彙 — 見上文附錄。

### 8.3 專案元資料

| 欄位 | 值 |
|:---|:---|
| `Version` | `TW` |
| `Language` | `CHT` |
| `PeriodStartDate` | 會計期間起始 |
| `PeriodEndDate` | 會計期間結束 |
| `LastAccountingPeriodDate` | 期末後核准規則的基準日 |

---

# B. 技術決策

## 9. 技術約束與排除選項

JET 的正式方案受三個硬限制約束：

1. 前端必須能以 HTML / CSS / JS 快速迭代審計工作流程。
2. V/R/Filter 計算必須由資料庫以 set-based SQL 執行。
3. 正式方案必須符合 Windows / .NET / 企業資安與部署限制；公司禁用 Python 作為正式產品路徑。

基於這三個限制，下列選項均不作為正式架構：

| 選項 | 不採用原因 |
|:---|:---|
| Caseware IDEA / IDEAScript | 不再訂閱 IDEA 授權；`.IDM` 與 `client.OpenDatabase` 是專有執行環境 |
| Excel VBA + Access | Access 單檔 2GB 上限、巨集安全政策、測試與 AI 協作能力不足 |
| Python pipeline | 公司資安規範不允許作為正式產品路徑 |
| Electron | HTML UI 強，但額外帶 Chromium / Node.js，部署與資安審查成本高 |
| Tauri | Windows UI 可用 WebView2，但後端主力是 Rust，會破壞 .NET / SQL Server / Visual Studio 主線 |
| Blazor Hybrid | 可行但會引入 Razor/component runtime，與「靜態 HTML 模板 + JetApi contract」方向不一致 |
| Local ASP.NET Core server | 架構乾淨，但會變成本機 server / port / firewall 問題，違反不架 server、不開 port 的部署限制 |

`legacy/` 內容只作為規則語意對照與歷史參考，不作為實作來源。正式開發依本文件、`docs/action-contract-manifest.md` 與 `docs/jet-frontend-description.md`。

## 10. 為什麼選 .NET 10 + WinForms + WebView2 + HTML

下表逐項說明技術選型，並列出被排除的替代選項與原因：
| 項目 | 選擇 | 被排除的選項與理由 |
|:---|:---|:---|
| 語言 | **C#** | VB.NET — 社群小、AI 品質低 |
| 執行時 | **.NET 10 LTS** | .NET Framework — 不支援現代 CLI / AI workflow |
| 桌面 Host | **WinForms** | WPF / WinUI 3 對薄 host 沒有足夠收益；Blazor Hybrid 會改變前端形態 |
| UI 引擎 | **WebView2** | 原生 WinForms 控件 — AI 不擅長生成，UI 迭代慢 |
| 前端語言 | **HTML / CSS / JS** | AI 生產力最高；`docs/jet-template.html` 已有設計模板 |
| 本機資料庫 | **SQLite** | 散落 JSON — 不利狀態管理與查詢統一；DuckDB 列為分析型 provider 候選但不作為起始依賴 |
| 主資料庫 | **SQL Server** | Access — 資料量天花板；PostgreSQL / MySQL — 企業 Windows 環境已標配 SQL Server |
| IDE | **Visual Studio 2026** | VS Code — 對 WinForms Designer 支援不足 |
| AI 主力 | **GitHub Copilot Agent Mode** | — |
| AI 輔助 | Codex CLI / Claude Code | 跨檔重構與大規模改動 |

**為什麼是「WinForms 包 WebView2 再載 HTML」這層夾心結構**：
- WinForms 提供**單一 .exe 打包** — 符合資安與部署限制 (不架 server、不開 port)
- WebView2 承載**AI 最擅長產生的 HTML 前端**
- `Form1` 保持**極薄** — 只當 WebView2 的容器，不放業務邏輯

核心決策一句話：**WinForms 只當薄桌面殼，HTML 只當 UI，Bridge 只當 JSON action 通道，Application 只做 orchestration，所有規則計算都在 Infrastructure provider 的資料庫 SQL 中完成。**

---

# C. 系統架構

## 11. 架構總覽

```
┌─ WinForms Host (.exe) ──────────────────────────────┐
│ ┌─ WebView2 Runtime ─────────────────────────────┐ │
│ │ ┌─ HTML / CSS / JS Frontend ─────────────────┐ │ │
│ │ │ (source: docs/jet-template.html,           │ │ │
│ │ │  packaged under src/JET/JET/wwwroot/)      │ │ │
│ │ └─────────────────┬──────────────────────────┘ │ │
│ └──────────────────┬┼─────────────────────────────┘ │
│                    ↕ action + payload (JSON)        │
│ ┌─ Thin Bridge + Action Dispatcher ────────────────┐│
│ │   postMessage handler + action → handler map     ││
│ └──────────────────┬───────────────────────────────┘│
│                    ↕                                │
│ ┌─ Application (CQRS) ────────────────────────────┐ │
│ │   Commands / Queries / Handlers                 │ │
│ │   (depend only on Domain interfaces)            │ │
│ └──────────────────┬──────────────────────────────┘ │
│                    ↕                                │
│ ┌─ Domain (pure) ──────────────────────────────────┐│
│ │   Entities / Value Objects / Rule Specs          ││
│ │   IGlRepository / ITbRepository / ...            ││
│ └──────────────────┬───────────────────────────────┘│
│                    ↕                                │
│ ┌─ Infrastructure ────────────────────────────────┐ │
│ │   SqliteProvider     │   SqlServerProvider      │ │
│ │   FileReader (Excel/CSV) / OpenXML Exporter     │ │
│ └──────────────────┬──────────────────────────────┘ │
└────────────────────┼────────────────────────────────┘
                     ↕
            ┌────────┴────────┐
         SQLite (本機)    SQL Server (企業)
```

### 架構模式總結

| 模式 | 套用範圍 |
|:---|:---|
| **Thin-Bridge** | WebView2 ↔ .NET 之間只有 `postMessage` + JSON，不夾邏輯 |
| **Action Dispatcher** | 單一進入點 (字典查表) 把 `action` 分派到 Handler |
| **Application CQRS** | Commands (變更) 與 Queries (讀取) 分離，各自有 Handler |
| **Clean Core** | `Domain` 不依賴任何框架、I/O 或 UI；`Application` 只依賴 `Domain`；`Infrastructure` 實作 `Domain` 介面 |
| **Repository 雙 Provider** | 單一 `IGlRepository` 介面，`SqliteGlRepository` 與 `SqlServerGlRepository` 兩個實作同時存在；執行期依設定切換 |

> **不採 Hexagonal / Onion 全套五層**。上面五個角色 (Host / Bridge / Application / Domain / Infrastructure) 已夠用。再多就是為論文服務，不是為 JET 服務。

---

## 12. 層級職責

本節逐層說明 Host、Bridge、Action Dispatcher、Application、Domain 與 Infrastructure 的職責邊界，以及各層明確不該做的事。

### Host (WinForms)

- 管理 WebView2 生命週期
- 處理系統視窗 / 檔案對話框
- 透過同一條 WebMessage action 通道提供少數 host 能力，例如檔案選擇
- `Form1.cs` **極薄**，永遠不放業務邏輯

### Bridge (WebView2 ↔ .NET)

Bridge 主通道固定為 WebView2 `postMessage` / `WebMessageReceived` / `PostWebMessageAsJson`。前端以 JSON envelope 送出：
```json
{ "requestId": "<uuid>", "action": "import.gl.fromFile", "payload": { "filePath": "..." } }
```

Bridge 做三件事：
1. 反序列化
2. 呼叫 `ActionDispatcher.Dispatch(action, payload)`
3. 包成 `{ "requestId", "ok", "data"/"error" }` 回傳

**不內嵌任何 SQL、規則、檔案操作**。

`AddHostObjectToScript` / host object 不作為一般 bridge 模式。若未來需要使用，只能限縮在極小、明確、不可被 UI 高頻呼叫的 host capability，且不得繞過 action manifest。

### Action Dispatcher

一個 `Dictionary<string, IActionHandler>`，依 `action` 分派到 Command/Query Handler。建議的 action 命名空間：

| Namespace | 範例 |
|:---|:---|
| `project.*` | `project.create`、`project.load` |
| `import.*` | `import.gl`、`import.tb`、`import.accountMapping`、`import.holiday` |
| `validate.*` | `validate.run` |
| `prescreen.*` | `prescreen.run`、`prescreen.status` |
| `filter.*` | `filter.preview`、`filter.commit` |
| `export.*` | `export.workpaper` |
| `query.*` | `query.glPage`、`query.validationSummary` |

不要引入 MediatR 作為 in-process mediator；此處的 CQRS 是程式碼組織原則，不是套件需求。Action Dispatcher 手寫即可，便於稽核、測試與控制授權風險。

### Application (CQRS)

```csharp
public sealed record ImportGlCommand(ProjectId ProjectId, string FilePath, GlAmountMode Mode);

public sealed class ImportGlCommandHandler(IGlRepository gl, IGlFileReader reader, IProjectRepository projects)
{
    public async Task<ImportResult> HandleAsync(ImportGlCommand cmd, CancellationToken ct)
    {
        // 1. 驗證 project 存在
        // 2. reader.ReadAsync(cmd.FilePath, cmd.Mode) → IAsyncEnumerable<GlEntry>
        // 3. gl.BulkInsertAsync(...)
        // 4. 回傳 ImportResult
    }
}
```

**每類規則執行一個 Handler**（`validate.run` / `prescreen.run` / `filter.preview`），規則述詞集中於 Infrastructure 的單一事實來源（`GlRulePredicates`）；新增規則 = 登錄表加一列 + 述詞加一段 + handler 摘要欄位，不另開 action。

### Domain (pure)

- `GlEntry` / `TbEntry` / `AccountMapping` / `DateDimension` 等 entity
- `GlAmountMode` / `TbChangeMode` 等 enum
- `RuleSpec` (聲明式規則描述，與第 5 節的表格同構)
- Repository 介面 (`IGlRepository`、`ITbRepository`、`IDateDimensionRepository`、`IProjectRepository`…)
- **無任何 `using System.Data.*`、`Microsoft.Data.*`、`System.IO.*` 之類的框架依賴**

### Infrastructure

- `SqliteGlRepository` / `SqlServerGlRepository`
- `ExcelGlFileReader` / `CsvGlFileReader` (正式匯入格式支援 `.xlsx` / `.csv`)
- `StreamingWorkPaperExporter` (OpenXML SAX writer)
- `IConnectionFactory` (SQLite / SQL Server 各一個實作)

---

## 13. SQLite / SQL Server Provider 策略

JET 同時支援兩個資料庫 provider，依案件規模與部署情境選用，但共用同一組 repository contract。本節說明兩者的定位、設計落實、方言差異處理與等價測試。

SQLite 與 SQL Server 兩個 provider 皆已全面落地（2026-06-14）。**所有每專案 repository** 都有 `Sqlite*` 與 `SqlServer*` 兩套實作,經 `ProviderRouting*` 包裝依案件 provider 路由;涵蓋的 repository 為 Gl、Tb、Import、MappingState、Calendar、AccountMapping、ValidationRun、PrescreenRun、FilterRun、RuleRun、FilterScenario、DataPreview、MessageLog 與 DevDatabaseInspector。完整工作流程（建案 → 匯入 → 配對 → 驗證 → 預篩選 → 進階篩選,含 resume 與刪除）在兩個 provider 下端到端等價,由 LocalDB 閘控的 golden journey 測試實跑驗證。DuckDB 仍保留為未來候選的分析型 provider,須先通過本節的 benchmark gate;初期設計也持續避免把 SQLite 寫死進 Application 或 frontend。

### 核心原則

SQLite 是本機持久案件 provider；SQL Server 是 large-data / shared-data provider。兩者不是雙寫架構，而是同一組 repository contract 的不同執行實作。

**單次案件執行只使用一個 Provider**。Provider 可由案件設定指定，也可依資料量門檻建議切換。

Provider 的案件定位：

- **SQLite**：小於 1,000 萬 GL rows 的本機持久案件，提供 local persistent DB path。
- **SQL Server**：大於 1,000 萬 GL rows、多人 / shared-data、或 10 億 row 等級 large-data case 的執行 path。
- **DuckDB**：列為本機分析 provider 候選，只能在 benchmark 通過且資安治理可接受後新增，不取代起始 SQLite path。

### 設計落實

```csharp
public interface IGlRepository
{
    Task<int> BulkInsertAsync(ProjectId id, IAsyncEnumerable<GlEntry> rows, CancellationToken ct);
    Task<IReadOnlyList<GlEntry>> QueryAsync(ProjectId id, GlQuerySpec spec, CancellationToken ct);
    Task<RuleResult> RunRuleAsync(ProjectId id, RuleSpec rule, CancellationToken ct);
    // ...
}
```

**兩個實作同時存在**：
- `SqliteGlRepository` — 採用 `Microsoft.Data.Sqlite`
- `SqlServerGlRepository` — 採用 `Microsoft.Data.SqlClient`

Repository contract 回傳 summary、resultRef、paged rows，不回完整母體。Application 層不得感知 provider 方言或資料庫型別。

### 執行期選擇（實作現況）

Provider 於**案件建立時**選定、寫入 `project.json` 的 `databaseProvider`（`"sqlite"` 預設 / `"sqlServer"`；`project.create` 接受此欄位，**建立後不可改**）。選擇不走全域 DI 切換，而是**每個 repository 一個 `ProviderRouting*` 包裝**：

- `ProjectProviderResolver` 以 projectId 為鍵快取該案件的 provider（provider 不可變，快取安全），所有路由包裝共用，避免每次呼叫重讀 `project.json`。
- 每個 `ProviderRouting*` 方法先 `ResolveAsync(projectId)`，再以 `ProviderSelection.Pick` 委派給對應的 `Sqlite*` 或 `SqlServer*` 實作（未知 provider → `unsupported_provider`）。
- Application 層 handler 只注入 repository 介面，完全不感知 provider 或方言。

隔離模型是**每專案一個資料庫**。SQLite 以 `projects/{projectId}/jet.db` 落地,檔案本身就是 scope;SQL Server 則是共用 instance 上的 `JET_{projectId}` 資料庫,資料表不帶 `project_id` 欄,忠實對應「每專案一個 jet.db」。SQL Server 的 base 連線字串取自環境變數 `JET_SQLSERVER_CONNECTION`，`InitialCatalog` 由 provider 依專案覆寫。未設定時 sqlite 專案不受影響;選 sqlServer 的案件則會在連線時得到明確的 `sql_server_not_configured` 錯誤。**SQL Server Express（開發,如 LocalDB `(localdb)\MSSQLLocalDB`）與 Standard／Enterprise（生產）共用同一套實作,差異僅在連線字串**,不為 Express 另建獨立 provider。

已建立案件不得在同一次規則執行中混用 SQLite 與 SQL Server。專案刪除（`project.delete`）連同資料庫一併移除：SQLite 刪 `jet.db`（先 `SqliteConnection.ClearAllPools()` 釋放檔鎖）、SQL Server `DROP DATABASE JET_{projectId}`（先 `SET SINGLE_USER WITH ROLLBACK IMMEDIATE`），同樣經 `ProviderRouting` 路由。

### 金額儲存與計算

SQLite / SQL Server 兩個 provider 的權威金額欄位都使用 scaled integer：

```sql
amount_scaled BIGINT NOT NULL
```

顯示用 decimal 只存在 DTO、UI 與 export format 邊界。Provider SQL 不得以 SQLite `REAL` 或文字 decimal 聚合做審計判斷。

### SQL Server Large-Data 索引策略

SQL Server path 的 `target_gl_entry`、大型 `result_*` fact tables 應評估 columnstore：

- 匯入後主要為 append/read-heavy 的分析型負載，適合 clustered columnstore index。
- 依 `PostDate` / project loading order 排序匯入可提升 segment elimination 機率。
- `config`、mapping、small lookup tables、keyset paging 所需查詢鍵仍可使用 rowstore index。
- Columnstore 是 large-data path 的預設設計方向；若某表不用，必須在 provider 設計中說明原因。

### SQL Server 匯入暫存寫入（SqlBulkCopy 串流）

GL/TB 投影（staging → target）與**匯入暫存寫入**（`SqlServerImportRepository` 寫入 `staging_*_raw_row`）都採用 `SqlBulkCopy` + 串流 `DbDataReader`，這是本節指定的大資料寫入形狀。

匯入路徑原本是 row-by-row prepared insert，每一列一次 `ExecuteNonQueryAsync` round-trip；1,403,327 列實測約 212 秒，是主要瓶頸。2026-06-14 改為 `SqlBulkCopy`（`EnableStreaming = true`、`BulkCopyTimeout = 0`、沿用既有 transaction），由一個串流 reader 餵入。這個 reader（`StagingBulkCopyDataReader`）包住 `IAsyncEnumerable<StagingRow>`，曝出 staging 的 5 個欄位：`batch_id` / `row_number` / `source_no` / `source_row_number` / `row_json`。

`ReplaceBatchAsync` 與 `AppendToBatchAsync` 共用同一個 reader 形狀（kind-agnostic，GL/TB 都受惠；AccountMapping、Calendar 走獨立 store，屬小量參考資料,不在此範圍）。SQLite 路徑（transaction + prepared batch，約 44k 列/秒）不需改動，維持原狀。

**async → sync 橋接**：來源是 `IAsyncEnumerable<StagingRow>`，但 `SqlBulkCopy` 需要同步的 `DbDataReader`。中間以一個有界 `Channel` 做 producer-consumer:背景 task 餵 channel，reader **覆寫 `ReadAsync`** 走 `WaitToReadAsync` / `TryRead` 消費（`SqlBulkCopy.WriteToServerAsync` 的 async 路徑就是以 `ReadAsync` 推進列），**不在 `Read()` 內阻塞 async**。

producer **必須以 `Task.Run` 在獨立執行緒上跑**。xlsx SAX 解析是 CPU 密集的同步工作,留在呼叫緒會與 bulk copy 爭用,退化成序列執行;實測:不用 `Task.Run` 時解析與寫入無重疊,用了才重疊。

取消或失敗時:linked CTS 會解除卡在 `WriteAsync` 的 producer，再 `await producerTask` 收束（producer 不會洩漏），例外則交給呼叫端的 `await using transaction` rollback。重匯入清除與附加失效那種百萬列 `DELETE` 設 `CommandTimeout = 0`（長批次,對齊 `BulkCopyTimeout = 0`）。

語意與原 row-by-row 版逐項一致（特徵化測試鎖定）：空來源 rollback、附加自最大 `row_number` 續編、欄位集合收斂於串流結束後同交易回寫、取消整批 rollback 且 `JET_` 庫無已提交殘留。

**實測（LocalDB、1,403,327 列）與比值的本質**：匯入從 211.9s（row-by-row）降到 **約 38s**（SqlBulkCopy 串流）。耗時分解:Excel SAX 解析加 JSON 序列化的 floor ≈ 29.7s，bulk copy 本身 ≈ 15.7s，兩者重疊後 ≈ 38s。**staging 寫入已經不是瓶頸,主導成本上移到 xlsx 解析——那屬於上游,不在本輪範圍。** GL 投影 ≈ 17–20s。

**比值（匯入 / 投影）≈ 2.0–2.2**,且因投影耗時浮動而在 1.9–2.2 間擺動。1.5 是結構下限（≈ 解析 floor / 投影）而非可超越的目標:匯入必須解析 114MB 的 xlsx,投影卻只讀已提交的 staging。因此規模測試（`SqlServerImportScaleSmokeTests`）只斷言列數正確,耗時與比值僅記錄供人工判讀,對齊既有 scale 測試「不做 wall-clock 斷言」的慣例。

### SQL 方言差異的處理

- **盡量用 ANSI SQL** (`INNER JOIN`、`GROUP BY`、`SUM`)
- **方言差異由 Provider 自行封裝**，不讓 Application 層感知：
  - 參數占位符：SQLite `@p`、SQL Server 也可 `@p` (兩者都支援，方便統一)
  - `BULK INSERT`：SQL Server 用 `SqlBulkCopy`；SQLite 用 `transaction + prepared statement batch`
  - 正則 (`REGEX_MATCH`)：SQLite 需 `regexp()` UDF；SQL Server 用 `PATINDEX` + `LIKE` 或 CLR function
  - `FULL OUTER JOIN`：SQLite 需用 `LEFT JOIN UNION ALL` 等等價形狀封裝；SQL Server 可直接使用
  - 連續零尾數判斷：優先使用 scaled integer + modulo，避免 provider 字串函式差異
- **有差異時優先寫兩版 SQL**，分別放在 `SqliteGlRepository` 與 `SqlServerGlRepository`，不要在 Application 寫動態方言切換

### 全量明細 keyset 分頁（query.*Page）

匯入母體可達 140 萬列以上，明細不可能一次回給前端。互動審閱用 `validate.run` / `filter.preview` 的 ≤50 / ≤1000 預覽即可；**走訪全量明細**（GUI「載入更多」、未來底稿匯出）走一組 keyset 游標分頁 action：

| Page action | 資料 | 排序／游標鍵（唯一） | 其他參數 |
|:---|:---|:---|:---|
| `query.completenessDiffPage` | 完整性逐科目差異（diff≠0） | `account_code` ASC | — |
| `query.docBalancePage` | 借貸不平傳票 | `document_number` ASC | — |
| `query.nullRecordsPage` | 空值紀錄 | `entry_id` ASC | `category`（空值分類） |
| `query.filterHitsPage` | 單一已存情境命中（行層） | `entry_id` ASC | `scenarioPosition` |
| `query.infSamplePage` | INF 抽樣 60 行明細 | `entry_id` ASC | — |

契約形狀（權威逐字定義在 `docs/action-contract-manifest.md`）：

- **請求** `{ projectId, cursor?, pageSize?, ... }`：`cursor` 首頁傳 null；`pageSize` 預設 **200**、上限 **500**（Domain `PageCursor` / 頁大小常數夾擠，0／負→預設、>500→500）。
- **回應** `{ rows, nextCursor }`：`nextCursor` 為 opaque 字串（Domain 純函式編碼末列排序鍵）；已到底回 null（前端據此隱藏「載入更多」）。游標格式不符讓 handler 報參數錯，不靜默。

為何 keyset 而非 OFFSET/FETCH：OFFSET 分頁要 DB 先處理被略過的列再丟棄，略過越多越慢，且並發變動會跳列／重複；keyset 以 `WHERE` 帶上一頁最後一列的鍵跳過，有索引時高效且對並發穩定（Microsoft Learn：EF Core Pagination、T-SQL ORDER BY OFFSET/FETCH）。

**游標述詞一律寫成展開布林式，不得用元組比較。** 多數 DB（含 SQLite）支援 row-value 元組 `WHERE (k1,k2) > (@k1,@k2)`，但 SQL Server（T-SQL）不支援；故兩 provider 共用展開布林式：

```sql
-- 單鍵游標（entry_id / account_code / document_number）：
WHERE <key> > @cursor
-- 多鍵游標（若日後需 tiebreaker）：
WHERE k1 > @c1 OR (k1 = @c1 AND k2 > @c2)
```

頁取數沿用各 provider 方言，由 `ISqlDialect.LimitClause` 出子句：SQLite `LIMIT @pageSize`、SQL Server `ORDER BY ... OFFSET 0 ROWS FETCH NEXT @pageSize ROWS ONLY`（OFFSET 恆 0，僅作 TOP-N、需 ORDER BY；非 offset 分頁）。**排序鍵必須唯一且有索引**（否則翻頁漏／重、且 seek 退化成全掃）：completeness 用既有 account 索引、docBalance 用 doc 索引、其餘以 PK `entry_id`；`result_filter_run` 以 `(scenario_position, entry_id)` PK 天然覆蓋。

各查詢的 ≤50 預覽語意與 Page 走訪職責分離：例如 completeness 預覽維持 `ABS(diff) DESC`（最大差異優先），Page 走全量用 `account_code` ASC（可索引、穩定）。隨機跳頁碼（offset 任意跳頁）不支援，亦不需要——底稿匯出與 GUI 審閱皆只需「下一頁」前進。

### `result_filter_run`：已存篩選情境命中的行層落地

`filterHitsPage` 需要「某情境命中了哪些 `entry_id`」。命中以新表 `result_filter_run(scenario_position, entry_id, PK(scenario_position, entry_id))` 持久化，只存行層參照、不存去正規化整列（避免重複與失同步）；傳票層由 distinct `document_number` 推得。

- **建表**：隨基底 schema 以 `IF NOT EXISTS`（SQLite）／`IF OBJECT_ID(...) IS NULL`（SQL Server）建立，**不升 schema 版本**——同 `app_message_log` / `gl_control_total` 先例。
- **落地時機**：`filter.commit` 保存情境時，以既有 `GlFilterWhereBuilder` 對該情境 AST 組命中述詞，把命中 `entry_id` 寫入（該 `scenario_position` 先刪後插，冪等）。
- **惰性補算**：`filterHitsPage` 讀取時若該 position 尚無落地列（如此機制前保存的舊情境），即時以同述詞補算並落地後再回（一次性、robust）。
- **失效不變量**：`result_filter_run` 屬依當前母體算出的衍生資料，納入結果失效集——重投影清 `target_gl_entry` 時，於 `RuleRunResultReset.ClearWithinAsync` 同交易一併 `DELETE FROM result_filter_run`（同 §2.5、同 `gl_control_total` 模式），命中參照永不指向已失效母體；不破壞 `result_rule_run` 回放。

### INF 60 行明細回取（infSamplePage）

INF 抽樣（§4）原本只回筆數與 seed，沒有讀回明細的路徑。`infSamplePage` 讀既有 `result_inf_sampling_test_sample`（已落地的抽樣 `entry_id`）join `target_gl_entry`，限定**最新一次 validate run** 的樣本、`entry_id` ASC keyset。回 step2 攸關欄：傳票號、科目編號／名稱、**借方金額／貸方金額（由 signed `amount_scaled` 拆兩欄）**、過帳日、核准日、編製人員、核准人員、摘要；金額以 scaled BIGINT 出、顯示層 ToDisplay。不重抽樣（seed 已固定落地），只讀回。

### 多情境 tag 矩陣（query.tagMatrix*；對應方法學 step4／step4-1）

事務所方法學 step4「符合高風險條件傳票」（傳票層 C1..CN 命中布林矩陣）與 step4-1「符合高風險條件傳票明細」（行層逐行 C*_TAG）需要「跑全部已存情境、逐傳票／逐行標記命中了哪些情境位置」的矩陣。D1 的 `filterHitsPage` 一次只回單一情境的命中行；D2 補上跨情境矩陣的查詢基礎設施（matrix 餵 E（writer）；D2 只提供查詢，不做匯出）。

| Page action | 資料 | 排序／游標鍵（唯一） | 列形狀 |
|:---|:---|:---|:---|
| `query.tagMatrixScenarios` | 矩陣表頭／step3 交叉參考：全部已存情境 + 命中數 | 依 `position` 升冪（非分頁） | `{ position, name, voucherHitCount, rowHitCount }` |
| `query.tagMatrixVoucherPage` | step4 傳票層：去重命中傳票 | `document_number` ASC | `{ documentNumber, postDate, createdBy, voucherTotal, matchedPositions:[int] }` |
| `query.tagMatrixRowPage` | step4-1 行層：命中傳票之**所有行**（含非命中行） | `entry_id` ASC | `{ documentNumber, lineItem, postDate, approvalDate, createdBy, approvedBy, accountCode, accountName, amount, matchedPositions:[int], description }` |

- **即時從 `result_filter_run` 算矩陣，不落地新矩陣表**：命中已落地於 `result_filter_run`（行層 `(scenario_position, entry_id)`），矩陣（pivot）是便宜的 `JOIN`／`GROUP BY`／keyset 查詢。`result_filter_run` 行層結構同時導得出兩矩陣——step4 傳票層（某傳票命中情境 S ⟺ 該傳票存在任一行於 `result_filter_run` 標記為 S）與 step4-1 行層（某行命中 S ⟺ `(S, 該行 entry_id)` 存在）。另存 pivot 矩陣表會重複資料、引入新失效來源、可能與 `result_filter_run` 不一致;故 D2 即時算,永遠與命中一致、零新失效不變量。沿用 D1 的 Page 原語（PageCursor／PageRequest／PageResult）做 keyset 分頁。
- **每頁兩段查詢（provider 中立，避免方言聚合）**：「每傳票／每行命中了哪些情境位置」是一對多，要在單一 SQL 內把位置聚成一欄需 `group_concat`（SQLite）／`STRING_AGG`（SQL Server）等**方言相異**聚合。為維持 provider 中立、鍵集分頁乾淨,採每頁兩段查詢：(1) **實體頁查詢（keyset）** 取本頁去重傳票（voucher，鍵 `document_number` ASC）或命中傳票之所有行（row，鍵 `entry_id` ASC）+ 核心顯示欄，`ORDER BY 鍵 + Dialect.LimitClause`,回本頁鍵範圍;(2) **位置查詢** 對**同一鍵範圍**取 `(實體鍵, scenario_position)`,在 handler（C#）分組成每實體的「命中位置有序去重清單」（`matchedPositions`）。兩段皆參數綁定、純 ANSI（除 LimitClause 走方言）、每頁有界（≤ pageSize 實體 × ≤10 位置）。位置→C1..CN 欄的對映屬 E。
- **`voucherTotal` = 該傳票 `SUM(debit_amount_scaled)`**（傳票借方總額,顯示換算 scaled→decimal 由 handler;對齊樣本 step4「傳票總金額」為毛額正數）。row 矩陣 `amount` 為 signed scaled;非命中行 `matchedPositions` 為空清單 `[]`。
- **輔助索引（加法,不升版）**：`result_filter_run` PK = `(scenario_position, entry_id)`,`entry_id` 非前導 → 「以 entry_id join 回 `target_gl_entry` 算傳票」「行頁位置查詢 `WHERE entry_id` 範圍」未獲最佳索引。新增 `idx_result_filter_run_entry ON result_filter_run(entry_id, scenario_position)`（雙 provider,`IF NOT EXISTS`／`IF ... IS NULL`,**不升 schema 版本**,同前述加法建表慣例）。
- **惰性 materialize（沿用 D1，提取共用服務）**：矩陣須反映全部已存情境。沿用 `filterHitsPage` 的惰性補算——首次查詢若空（或 summary 全 0）且 `config_filter_scenario` 有定義,重用 `IFilterRunMaterializer` 對全部情境落地後重取一次。為免重複,把 `filterHitsPage` 既有的私有 `MaterializeAllAsync` 提取成共用 Application 服務 `FilterRunMaterializeService`,由 `filterHitsPage` + D2 三 handler 共用（DRY,單一事實來源）。

### Schema 分層 (兩 Provider 共通)

| Schema | 內容 |
|:---|:---|
| `staging` | 原始匯入 (未處理) |
| `target` | 標準化後的 GL / TB / AccountMapping / DateDimension |
| `result` | 規則執行結果與彙總表 |
| `config` | 專案設定、欄位映射、規則參數 |

### Provider 等價測試

雙方言維護稅必須用 golden tests 支付：

1. 同一組 `data/` fixture 同時跑 SQLite 與 SQL Server provider。
2. 每條 V/R/Filter 規則比對 summary、resultRef metadata、paged detail rows。
3. 金額比對以 scaled integer 為準。
4. INF 抽樣比對 seed 與抽中 keys。
5. 若公司環境不能跑 containerized SQL Server，需提供固定 integration database 或明確的替代驗證路徑。

### DuckDB Benchmark Gate

DuckDB 只能作為候選 provider 進入 benchmark，不直接進正式架構。評估時至少比較：

| 項目 | 比較內容 |
|:---|:---|
| 正確性 | scaled integer / decimal 匯入、完整性測試、借貸不平測試、連續零尾數結果是否一致 |
| 效能 | 100 萬 / 1,000 萬 GL rows 下完整性測試、借貸不平測試、期末後核准、連續零尾數、filter.preview |
| 方言成本 | 與 SQL Server 的 SQL 差異是否少於 SQLite |
| 治理 | .NET client 維護狀態、授權、資安審查、部署方式 |

未通過上述 gate 前，正式本機 provider 仍是 SQLite。

---

## 14. 專案結構規劃

下列為建議的起步目錄配置。JET 是單一 WinForms 專案，資料夾本身不會強制 Clean Core；真正重要的是邊界規則。因此一開始只保留主要邊界，不預先建立過多空子目錄。

```
src/JET/
├── JET.slnx
└── JET/
    ├── JET.csproj
    ├── Program.cs                  # Main + DI composition root
    ├── Form1.cs / Form1.Designer.cs # WebView2 host — 僅此而已
    ├── wwwroot/                    # 從 docs/jet-template.html 衍生的 HTML/CSS/JS
    │   ├── index.html
    │   ├── css/
    │   ├── js/
    │   │   ├── jet-api.js          # 唯一前端 JetApi action channel
    │   │   ├── state.js            # UI 暫態，不保存 GL/TB row population
    │   │   └── app.js              # bootstrap / event binding
    │   └── assets/
    ├── Bridge/
    ├── Application/
    ├── Domain/
    └── Infrastructure/

src/JET/tests/JET.Tests/            # xUnit
    ├── Domain/                     # Rule spec 單元測試 (純邏輯)
    ├── Application/                # Handler 測試 (mock repository)
    └── Infrastructure/             # 每個 Provider 的 integration / golden test
```

子目錄拆分採延後原則：當同一層檔案超過 3-5 個，或已有明確群組時再拆。例如 `Application` 真的有多個 command/query handler 後，再建立 `Commands/`、`Queries/`、`Contracts/`；`Infrastructure` 開始同時實作 SQLite / SQL Server / FileIO / Exporting 後，再建立 `Persistence/Sqlite/`、`Persistence/SqlServer/`、`FileIO/`、`Exporting/`。

---

## 15. 命名與分層原則

下列十二項原則是維護 JET 分層邊界與可測試性的基本約定，適用於所有新增與修改的程式碼：

1. **Form1 只做 host** — `Form1.cs` 內不碰業務邏輯，只初始化 WebView2 與 Bridge
2. **前端只送 `action + payload`** — 不拼 SQL、不呼叫資料庫
3. **Bridge 不做業務** — 只有協定解析與分派
4. **每條規則一個 Handler** — 不要一個大 `ProcessAllRulesCommand`
5. **Command/Query record 為 `sealed record`** — 不可變、易序列化、易測試
6. **Handler 建構子注入介面** — 不 new 具體 class
7. **Domain 無 I/O 依賴** — 任何 `System.IO` / `System.Data` / `Microsoft.Data.*` / `ClosedXml` 等 using 出現在 Domain 就是錯
8. **參數化查詢** — 所有使用者輸入進 SQL 一律走 `SqlParameter` / `SqliteParameter`；拒絕字串拼接
9. **金額 scaled integer** — DB schema、規則 SQL、測試與 export 都以 scaled 欄位為權威
10. **測試金字塔 + golden tests** — Domain 單元測試；Application handler mock repo 測試；Infrastructure 兩 Provider 做整合 / golden tests
11. **不要引入不必要 mediator / mapper 套件** — 不使用 MediatR / AutoMapper；手寫 dispatcher 與明確 DTO mapping
12. **action 契約穩定** — action 名稱與 payload schema 列入文件化；前端改版不可改 action 契約

### 15.1 業務規則語意的權威位置

- 業務規則語意由 `docs/jet-guide.md` 的 RuleSpec、`Domain` model/spec、以及 `Application` use cases 定義。
- `Infrastructure` 負責把這些語意轉成 SQLite / SQL Server 的 parameterized set-based SQL。
- Repository implementation 不應成為 business rule source of truth。
- SQL 字串不應成為唯一的 business rule source of truth；若 SQL 與 RuleSpec 語意不一致，先修規格與測試，再修 SQL。
- 前端 JavaScript 不得承載 authoritative business rules，只能顯示狀態、收集輸入、呼叫 `JetApi`、呈現 summary/preview/page/export 結果。

### 15.2 資料存取與匯出原則

- 規則 SQL 以 raw parameterized SQL 為主，資料存取可用 ADO.NET 或 Dapper 等薄層；不要用 EF Core LINQ 表達 V/R/Filter。
- Bulk import 由 provider 實作：SQL Server 使用 `SqlBulkCopy`；SQLite 使用 transaction + prepared statement batch。
- Schema migration 採 SQL script-based migrator；不用 EF migrations 管理本專案主要資料庫 schema。
- Export 明細使用 OpenXML SAX writer；ClosedXML 不得作為大型工作底稿明細 writer。
- 所有長時間 import / query / export 都必須串接 `CancellationToken`。

---

# D. 開發與協作

## 16. AI-agent 開發工作流

JET 以 AI agent 為主要開發助力，但只有在邊界與契約穩定的前提下才能發揮生產力。本節說明工具鏈分工、穩定契約的位置、AI 不該做的事，以及驗證與 UI 的 guardrails。

### 16.1 工具鏈分工

| 任務 | 工具 | 備註 |
|:---|:---|:---|
| 主場開發 (WinForms / WebView2 整合、Designer 相關) | **Visual Studio 2026 + Copilot Agent Mode** | 必須用 VS；Designer.cs 不讓外部 AI 動 |
| 跨檔重構、Rule Handler 批次生成 | Copilot Agent Mode / Codex CLI / Claude Code | 三選一都可；優先 Copilot (IDE 原生)，跨 repo 級時用 Codex / Claude Code |
| 單元測試生成 | Copilot / Claude Code | 依 RuleSpec + 小型 fixture 生成 |
| HTML/CSS/JS 前端 | Copilot / Claude Code | 但**不可改 action 契約**與 fixed binding ID |
| SQL 方言調整 | Copilot | SQLite ↔ SQL Server 方言差 |

目前 repo 的 AI customization 分工：

1. `AGENTS.md` — 所有 agent 的短索引與不可違反邊界
2. `CLAUDE.md` — Claude Code 入口，指向 `AGENTS.md` 與相關 docs
3. `.claude/skills/minimalist-ui/` — Claude Code 前端視覺設計 skill
4. `.github/copilot-instructions.md` — GitHub Copilot repository-wide rules

若未來真的需要 path-specific Copilot instructions、prompt files 或 custom agents，再新增對應 `.github/` 子目錄；不要預先建立空資料夾或引用不存在的設定檔。

### 16.2 讓 AI 提速的關鍵：**穩定契約**

AI 能快是因為**邊界清楚**；AI 會爛是因為**邊界模糊**。

| 穩定邊界 | 放哪 |
|:---|:---|
| Action 名稱與 payload JSON schema | `docs/action-contract-manifest.md` |
| Frontend action contract / step data outline | `docs/action-contract-manifest.md` |
| RuleSpec 表 | 本檔第 5 節 |
| 欄位標準名稱 | 本檔第 18 節欄位對照 |
| Repository 介面 | `Domain/` 內的 contract；檔案量增加後再拆子目錄 |
| Frontend fixed binding ID | `docs/jet-template.html` / packaged `wwwroot/index.html` 的 `data-bind="*"` 屬性 |

建議採用 `AGENTS.md` 當跨工具短索引，`CLAUDE.md` / `.claude/` / `.github/` 只放工具專用規則。不要把所有規則都塞進單一 instruction blob。

### 16.3 AI 不該做的事

- **不可自行更改** Action 名稱或 payload 欄位
- **不可自行更改** RuleSpec 的語意 (只能補實作，不能改規則)
- **不可動** `Form1.Designer.cs` 與 `Form1.resx` 以外的 Designer 生成檔
- **不可在 Application 層** 寫 provider 判斷 (`if (isSqlite) ...`) — 方言差異在 Infrastructure 處理
- **不可引入 MediatR / AutoMapper** 作為預設解法；本專案以手寫 dispatcher、明確 command/query handler 與 DTO mapping 控制授權與複雜度
- **不可在 SQLite 使用 `REAL` 或文字 decimal 聚合作為金額權威計算**
- **不可用 ClosedXML / DOM workbook 寫大型明細工作底稿**
- **不可為了一次性對話新增零散文件**。若需 persistent AI context，優先維護 `AGENTS.md`、`CLAUDE.md`、`docs/action-contract-manifest.md`、`.claude/skills/` 與 `.github/copilot-instructions.md`

### 16.4 驗證與測試的 loop

AI 每次產碼後應執行：
```
dotnet restore src/JET/JET.slnx
dotnet build src/JET/JET.slnx --no-restore --nologo
dotnet test src/JET/tests/JET.Tests/JET.Tests.csproj --no-build --nologo
```

若 agent 當前不在具備 .NET SDK / Windows Desktop targeting / 可用 package restore 的環境，應明確回報哪些命令失敗或跳過，不要假裝已驗證。

在 VS 2026，Copilot Agent Mode 會自動跑；在 Claude Code / Codex CLI，手動或由 agent 自己觸發。

### 16.5 UI/UX guardrails

JET UI 的目標是讓審計員清楚完成案件建立、檔案匯入、欄位配對、科目配對、完整性測試、進階篩選與底稿輸出。優先清楚、穩定、可追蹤，不追求華麗效果。

- 所有 long-running actions 應有 loading / busy / error / success state。
- validation / import / filter 結果應有可理解的 summary。
- 欄位配對畫面應清楚區分 source column、standard field、mapping status。
- 進階篩選 UI 應清楚呈現 AND / OR grouping。
- clickable elements 應有明確 affordance。
- keyboard focus state 不應被移除。
- data table 不應一次載入完整 GL/TB rows。
- preview / pagination / export 應走 backend-controlled path。
- UI 可以改善互動體驗，但不能改變資料處理與 business rule 邊界。

不要加入 marketing landing page pattern、glassmorphism / neumorphism / cyberpunk 等 aesthetic prescriptions、大量前端套件，或與 WinForms + WebView2 + static HTML 不相容的 UI stack。

---

## 17. 從 idea-script.bas 遷移的做法

遷移原則只有一句話：**不要逐段翻譯。** 改採「**聲明式規格 + 重新實作**」——以本文件的 RuleSpec 為準，而非回頭複製 VBA 的實作細節。

### 遷移四分類

| 類別 | 舊位置 (idea-script.bas) | 新位置 |
|:---|:---|:---|
| **Domain Rules** | 預篩選規則與自訂變體（legacy 代號 R1-R8 / A2-A4）的業務邏輯 | 本檔第 5 節 RuleSpec → `Domain/Rules/*.cs` |
| **Application Use Cases** | `Step1_Validation` / `Step2_*` / `Step3_Routines` / `Step4` / `Step5_*` | `Application/Commands/*` |
| **Infrastructure** | `Z_DirectExtractionTable` / `Z_renameFields` / `Z_Rename_DB` / 檔案 I/O | `Infrastructure/*` |
| **UI Workflow** | `Intro_Dlg` / `TBDetail_Dlg` / `GLDetail_Dlg` / `Criteria_Dlg` | `wwwroot/*` + action 綁定 |

### 實作優先序

1. **鎖定資料決策** — schema 使用 scaled integer 金額、staging/target/result/config 分層、resultRef 與 keyset paging。
2. **Domain 先行** — 把 5 個核心實體與 RuleSpec 寫成 C# 類別，建立單元測試。
3. **建立 WebMessage Bridge 最小閉環** — `system.ping` → 回傳 host 狀態，前端驗證 request/response envelope。
4. **接通 file-based ImportGl + ImportTb** — 檔案路徑 → reader streaming → staging → target，一條真實資料流打通。
5. **加一條垂直規則切片** — 例如借貸不平測試或期末後核准，走完 Command/Query → Repository → SQL → result table → resultRef → 前端 summary/page。
6. **建立雙 Provider golden test** — 同一 fixture 跑 SQLite 與 SQL Server，比對 scaled 金額、summary 與 detail rows。
7. **依 RuleSpec 量產其餘規則** — AI 可批次生成，但每條都要通過 provider golden tests。
8. **實作進階篩選與 workpaper export** — filter.preview / filter.commit 走 resultRef；export 使用 OpenXML SAX writer。
9. **SQL Server large-data hardening** — 補 columnstore、bulk import、query plan 與取消機制。
10. **DuckDB benchmark** — 只作為候選 provider 研究，不阻塞 SQLite / SQL Server 主線。

### 舊程式 → 新結構對照 (摘要)

若需要查舊規則名稱與來源欄位，可參考 [`legacy/README.md`](../../../legacy/README.md)。不要從舊程式逐段翻譯；以本文件 RuleSpec 重新實作。

---

## 18. 欄位對照表

下表列出標準 C# 欄位與舊系統（IDEA / VBA）欄位的對應關係，供匯入映射與可追溯性參考。

### GL (General Ledger)

| 標準 (C#) | IDEA 原名 | VBA 名 |
|:---|:---|:---|
| `DocumentNumber` | 傳票號碼_JE | FLD_DOCUMENT_NUMBER |
| `LineItem` | 傳票文件項次_JE_S | FLD_LINE_ITEM |
| `Amount` | 傳票金額_JE | FLD_AMOUNT |
| `AccountCode` | 會計科目編號_JE | FLD_ACCOUNT_CODE |
| `AccountName` | 會計科目名稱_JE | FLD_ACCOUNT_NAME |
| `DocumentDescription` | 傳票摘要_JE | FLD_DOCUMENT_DESCRIPTION |
| `ApprovalDate` | 傳票核准日_JE | FLD_APPROVAL_DATE |
| `PostDate` | 總帳日期_JE | FLD_POST_DATE |
| `VoucherDate` | 傳票日期_JE | FLD_VOUCHER_DATE |
| `CreatedBy` | 傳票建立人員_JE | FLD_CREATED_BY |
| `ApprovedBy` | 傳票核准人員_JE | FLD_APPROVED_BY |
| `SourceModule` | 分錄來源模組_JE | FLD_SOURCE_MODULE |
| `IsManual` | 人工傳票否_JE_S | FLD_IS_MANUAL |
| `DebitAmount` | DEBIT_傳票金額_JE_T | FLD_DEBIT_AMOUNT |
| `CreditAmount` | CREDIT_傳票金額_JE_T | FLD_CREDIT_AMOUNT |
| `DrCr` | DEBIT_CREDIT_JE_T | FLD_DR_CR |

> **`LineItem`（傳票文件項次）的來源與補編。** 如果來源沒有提供這個欄位，也就是欄位配對沒有對應到 `lineID`，投影時會在同一交易裡替每張傳票自動編號補齊，用的是 `ROW_NUMBER() OVER (PARTITION BY 傳票號 ORDER BY source_row_number)`。這個補出來的值是衍生顯示值，不是客戶的原始資料。如果來源有對應，就照來源逐字保留。此值僅供資料集完整性與底稿明細排序，**不得**作為任何規則計算或抽樣的鍵；抽樣一律依 `source_row_number`。SQLite 與 SQL Server 等價。

### TB (Trial Balance)

| 標準 (C#) | IDEA 原名 | VBA 名 |
|:---|:---|:---|
| `AccountCode` | 會計科目編號_TB | FLD_ACCOUNT_CODE |
| `AccountName` | 會計科目名稱_TB | FLD_ACCOUNT_NAME |
| `ChangeAmount` | 試算表變動金額_TB | FLD_CHANGE_AMOUNT |
| `OpeningBalance` | Opening_Balance_TB | FLD_OPENING_BALANCE |
| `ClosingBalance` | Ending_Balance_TB | FLD_CLOSING_BALANCE |
| `OpeningDebitBalance` | — | FLD_OPENING_DEBIT_BALANCE |
| `OpeningCreditBalance` | — | FLD_OPENING_CREDIT_BALANCE |
| `ClosingDebitBalance` | — | FLD_CLOSING_DEBIT_BALANCE |
| `ClosingCreditBalance` | — | FLD_CLOSING_CREDIT_BALANCE |
| `DebitAmount` | — | FLD_DEBIT_AMOUNT |
| `CreditAmount` | — | FLD_CREDIT_AMOUNT |

---

## 19. 術語對照

下表整理 JET 領域內的中英術語與簡要說明，作為跨文件用語的一致基準。
| 中文 | English | 說明 |
|:---|:---|:---|
| 日記帳分錄 | Journal Entry (JE) | 會計系統的逐筆交易記錄 |
| 總帳 | General Ledger (GL) | 所有 JE 的匯總帳簿 |
| 試算表 | Trial Balance (TB) | 各科目期間餘額彙總 |
| 傳票 | Voucher / Document | 一組同時借貸的分錄集合 |
| 會計科目 | Account | 分類交易性質的編碼 |
| 借方 / 貸方 | Debit (Dr) / Credit (Cr) | 資產增加為借；負債收入增加為貸 |
| 過帳 | Posting | 將分錄記入總帳 |
| 完整性測試 | Completeness Test (`completeness_test`) | GL ↔ TB 勾稽 |
| 借貸不平測試 | Document Balance Test (`doc_balance_test`) | 逐傳票借貸平衡檢查 |
| INF 抽樣測試 | INF Sampling Test (`inf_sampling_test`) | 可重現抽樣驗證非財務欄位（INF = Information Produced by the Entity） |
| 空值紀錄測試 | Null Records Test (`null_records_test`) | 關鍵欄位空值檢查 |
| 預篩選 | Pre-Screening | 基於風險指標的自動篩選（規則名稱見 §4 命名登錄表） |
| 進階篩選 | Advanced Filtering | 組合條件得出最終測試母體 |
| 工作底稿 | Work Paper | 審計證據的書面記錄 |
| 管理階層凌駕 | Management Override | 管理層繞過內部控制 |
| 職能分離 | Segregation of Duties | 不同人執行不同職能 |
| 科目配對 | Account Mapping | 企業科目對應標準分類 |
| 母體 | Population | 測試範圍內的全部資料 |
| 實質性程序 | Substantive Procedure | 直接測試財報金額的審計程序 |

> **傳票號碼的地區／系統命名差異。** 同一個「傳票號碼」概念,不同會計系統的稱呼不一樣:亞洲會計系統常用 **Voucher Number**;SAP 等 ERP 常用 **Document Number**,泛指原始單據編號,涵蓋發票、收據、訂單等;西方會計系統常用 **Journal Number**,指日記帳分錄的識別碼。PBC 來源欄位不論用哪個名稱,欄位配對後一律映射到標準欄位 `DocumentNumber`(見 §2.1 與 §18 對照表),下游規則只認標準欄位。
