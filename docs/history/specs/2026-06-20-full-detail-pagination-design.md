# 全量明細基礎設施設計(子專案 D1)

> **狀態:已實作,待 GUI 驗收。** 實作完成、自動化測試全綠(本機 588、SQL Server parity 實跑),GUI 五處「載入更多」待使用者人工驗收。屬「匯出底稿」里程碑的子專案 **D1(全量明細基礎設施)**。權威描述已轉移到 `docs/jet-guide.md`(§13 全量明細 keyset 分頁)、`docs/action-contract-manifest.md`、`docs/development-status.md`;GUI 驗收通過後依慣例去日期前綴或刪除。
>
> **盤查依據:** `.git/sdd/bcde-scope-draft.md`(D 拆 D1/D2)。**效率與雙 provider 實務**經 Microsoft Learn 文件查證(見「分頁機制」一節引用)。

## 背景與動機

匯出底稿需要「全部」明細,但 JET 現況各明細查詢一律截 `LIMIT 50` / `TOP (50)`、篩選命中不落地、INF 抽樣只回筆數+種子。D1 補上**全量取得的基礎設施**,給後續 D2(多情境 tag 矩陣)與 E(匯出 writer)使用,並讓 GUI 既有明細檢視能「載入更多」。**D1 不做 tag 矩陣**(屬 D2)、不做匯出 writer(屬 E)。

現況五個截斷點(驗證員核實):完整性全科目差異、借貸不平、空值紀錄、篩選命中(`filter.preview` ≤50)、INF 抽樣明細(無讀回路徑)。

## 目標與範圍

### 做

1. **keyset 游標分頁查詢**(新 `query.*Page` action,五個):完整性差異、借貸不平、空值紀錄、篩選命中、INF 抽樣明細——各支援全量走訪(一頁 + 下一頁游標)。
2. **`result_filter_run` 落地(精簡參照)**:已保存篩選情境(C1–C10)的命中 `entry_id` 持久化;`filterHitsPage` 讀它 join 回 `target_gl_entry`。
3. **INF 60 行明細回取**:`infSamplePage` 讀 `result_inf_sampling_test_sample` join 回 target,回 step2 攸關欄(借/貸拆欄、過帳日、核准日、編製人員、核准人員)。
4. **GUI「載入更多」**:五個明細檢視加游標式分頁(非同步接列、不整表載入)。

### 不做(移交 / YAGNI)

- **多情境 × 逐列 tag 矩陣**(step4 傳票層布林 / step4-1 行層 C*_TAG)→ D2。D1 只提供「單一情境命中的全量明細」與其落地。
- **匯出 writer / `*_JE` 正準名對照** → E。
- **跳頁碼(random-access offset 分頁)**:keyset 不支援任意跳頁;經評估底稿匯出與 GUI 審閱皆只需「下一頁」前進,YAGNI。
- 既有 `validate.run` / `filter.preview` 的 ≤50 快速預覽**保留不動**(互動審閱仍用它;Page 系列另負責全量走訪)。

## 開發原則與邊界(harness — 本 spec 的 Global Constraints)

- **分層**:`Bridge/Form1 → Application → Domain ← Infrastructure`。游標編解碼/契約型別為 Domain;SQL 與分頁述詞在 Infrastructure;**provider 分支只在 Infrastructure**。
- **前端零商業邏輯**:不在前端/Form1/HTML/CSS/JS 做 SQL、計算、命中判定;「載入更多」只呼叫 Page action、把回傳列接到既有清單。
- **postMessage 邊界**:不在 `jet-api.js` 以外呼叫 `window.chrome.webview.postMessage`。
- **查詢 = 參數化集合式 SQL**:識別字只來自白名單/常數,游標值與頁大小一律參數綁定;DbCommand,provider 中立(方言差異走 `ISqlDialect`)。
- **契約先行**:新 `query.*Page` action 形狀**先改 `docs/action-contract-manifest.md`**;ActionContractTests 鎖回應形狀。
- **雙 provider 等價**:SQLite 與 SQL Server 兩側同步;**游標述詞用展開布林式(見下),不得用元組比較**;SQL Server 測試走 LocalDB 閘控。
- **結果失效不變量**:`result_filter_run` 與既有失效集一致——重投影(清 `target_gl_entry`)時一併清除(納入 `RuleRunResultReset.ClearWithinAsync`,同 B 的 `gl_control_total` 模式);不破壞 `result_rule_run` 回放。
- **不編輯** WinForms designer 產生檔。
- **測試邊界(jet-testing 三層)**:Domain 純函式單元(游標編解碼、頁大小夾擠)+ Application 驗收(`HandlerTestHost`,對 manifest wire:分頁走訪到底無漏無重、result_filter_run 命中正確、INF 明細欄位)+ Infrastructure/parity(真 SQLite,SQL Server LocalDB 閘控)。
- **TDD**;**不自行 commit**(版本控制由使用者親自下令;subagent 執行階段亦不 commit,用 tree-diff 隔離)。

---

## 分頁機制(keyset 游標)— 效率與雙 provider 的核心

### 為何 keyset 而非 OFFSET/FETCH

Microsoft Learn(EF Core Pagination、T-SQL ORDER BY OFFSET/FETCH)明示:OFFSET 分頁要資料庫「先處理被略過的列再丟棄」,略過越多越慢,且並發變動會跳列/重複;**keyset(seek)以 `WHERE` 帶上一頁最後一列的鍵跳過**,有索引時非常高效且對並發穩定。JET 母體達 140 萬列,故一律用 keyset。

### 跨 provider 游標述詞:展開布林式(不可用元組)

多數資料庫(含 SQLite)支援 row-value 元組比較 `WHERE (k1, k2) > (@k1, @k2)`,但 **SQL Server(T-SQL)不支援**(MS-TSQLISO F641:row/table constructor「limited support」)。因此 D1 的游標述詞**一律寫成展開布林式**,兩 provider 共用:

```sql
-- 單鍵游標(entry_id / account_code / document_number):
WHERE <key> > @cursorKey
-- 多鍵游標(若日後需要 k1 排序、k2 tiebreaker):
WHERE k1 > @c1 OR (k1 = @c1 AND k2 > @c2)
```

頁取數用各 provider 既有方言:SQLite `LIMIT @pageSize`、SQL Server `ORDER BY ... OFFSET 0 ROWS FETCH NEXT @pageSize ROWS ONLY`(OFFSET 恆 0,僅作 TOP-N;非 offset 分頁)。`ISqlDialect` 出 limit 子句。

### 排序鍵唯一性與索引(必要)

- **排序鍵必須唯一**(否則翻頁漏/重)。各查詢採天然唯一鍵(見下表)。
- **每個游標鍵須有對應索引**(seek 不全掃)。`target_gl_entry` 既有索引:PK `entry_id`、`ix_..._account(account_code)`、`ix_..._doc(document_number)`。`result_filter_run` 以 `(scenario_position, entry_id)` 為 PK(天然覆蓋)。不足者於本輪補索引。

### 五個分頁查詢的鍵與排序

| Page action | 資料 | 排序/游標鍵(唯一) | 索引 |
|:--|:--|:--|:--|
| `query.completenessDiffPage` | TB+GL 逐科目差異(diff≠0) | `account_code` ASC | 既有 account 索引 |
| `query.docBalancePage` | 借貸不平傳票(GROUP BY 傳票 HAVING 淨額≠0) | `document_number` ASC | 既有 doc 索引 |
| `query.nullRecordsPage` | 空值紀錄(分類:日期區間外/空白摘要/空白傳票號·科目) | `entry_id` ASC(+ 分類參數) | PK |
| `query.filterHitsPage` | 單一已存情境命中(行層) | `entry_id` ASC | result_filter_run PK |
| `query.infSamplePage` | INF 抽樣 60 行明細 | `entry_id` ASC | PK + sample PK |

> 注:`completenessDiffPage` 全量走訪用 `account_code` ASC(可索引、唯一、穩定);既有 ≤50 預覽維持 `ABS(diff) DESC`(最大差異優先)語意不變——預覽看重點、Page 走全量,職責分離。

### 游標契約(opaque)

- Page 回應:`{ rows: [...], nextCursor: string|null }`。`nextCursor` 為 opaque 字串(Domain 純函式編碼末列鍵;`null` 表已到底)。首頁傳 `cursor: null`。
- 頁大小:`pageSize` 預設 **200**、上限 **500**(Domain 夾擠);防一次拉太多。
- 游標編解碼為 Domain 純函式(可單元測試):編碼末列鍵 → 字串;解碼 → 強型別鍵;格式不符回錯(讓 handler 報參數錯,不靜默)。

---

## `result_filter_run` 落地(精簡參照)

- **新表**(雙 provider,隨基底 schema `IF NOT EXISTS` 建,不需升版——同 `app_message_log`/`gl_control_total` 先例):

  ```sql
  CREATE TABLE IF NOT EXISTS result_filter_run (
      scenario_position INTEGER NOT NULL,
      entry_id          INTEGER NOT NULL,
      PRIMARY KEY (scenario_position, entry_id)
  );
  ```

  只存「哪個情境、命中哪些 `entry_id`」(行層參照);傳票層 = `filterHitsPage` 取 distinct `document_number`。**不存去正規化整列**(避免重複與失同步)。
- **落地時機**:`filter.commit`(保存情境)時,以既有 `GlFilterWhereBuilder` 對該情境 AST 組命中述詞,把命中 `entry_id` 寫入 `result_filter_run`(該 `scenario_position` 先刪後插,冪等)。情境刪除/覆寫時同步刪該 position 的列。
- **惰性補materialize**:`filterHitsPage` 讀取時若該 position 無落地列(如 D1 之前保存的舊情境),即時以同述詞補算並落地後再回(robust;一次性)。
- **失效**:重投影清 `target_gl_entry` 時,於 `RuleRunResultReset.ClearWithinAsync` 一併 `DELETE FROM result_filter_run`(同交易),確保命中參照永不指向已失效母體。

## INF 60 行明細回取

- `infSamplePage` 讀既有 `result_inf_sampling_test_sample`(已存抽樣 `entry_id`)join `target_gl_entry`,ORDER BY `entry_id` ASC、keyset。
- 回傳 step2 攸關欄:傳票號、科目編號/名稱、**借方金額、貸方金額(由 signed amount 拆兩欄)**、過帳日、核准日、編製人員、核准人員、摘要。金額以既有 `debit_amount_scaled`/`credit_amount_scaled` 出、顯示層 ToDisplay。
- 不重抽樣(seed 已固定落地);只讀回。

## GUI「載入更多」(效率與不卡頓)

- 五個明細檢視(完整性差異、借貸不平、空值、篩選情境命中明細、INF 抽樣)在既有 ≤50 預覽下方加「載入更多」鈕。
- 行為:點擊 → 帶當前 `nextCursor` 呼叫對應 `query.*Page`(經 `jet-api`)→ 把回傳 `rows` **接在**現有列之後 → 更新 `nextCursor`;`null` 時隱藏鈕(已到底)。
- **不卡頓**:游標增量載入、不整表拉取;載入期間鈕顯示載入中態(disabled + 旋圈)、非同步、不凍結 UI;沿用 `minimalist-ui` 既有清單樣式,不新增重樣式。
- **前端零邏輯**:不在前端判命中/算差異/組 SQL;只發 action、接列、管游標與載入態。

## 受影響的現行碼與新增(盤查)

| 位置 | 動作 |
|:--|:--|
| `docs/action-contract-manifest.md` | 先行:5 個 `query.*Page` action(請求 `{cursor,pageSize,...}`、回應 `{rows,nextCursor}`)、各 row 形狀 |
| `Domain/`(新)`PageCursor` 編解碼 + `PageRequest`/`PageResult<T>` 契約 + pageSize 夾擠常數 | keyset 游標純函式 |
| `Domain/` 各 Page 的 row record(完整性/借貸不平/空值/篩選命中/INF 明細) | 強型別回傳 |
| `Infrastructure/{Sqlite,SqlServer}` 各 Page 查詢倉儲 | keyset SQL(展開布林式 + 方言 limit);雙 provider |
| `Infrastructure/{Jet,SqlServer}ProjectDatabase.cs` | `result_filter_run` 建表(IF NOT EXISTS / IF OBJECT_ID);必要索引 |
| `Infrastructure/RuleRunResultReset.cs` | 失效集加 `DELETE FROM result_filter_run` |
| `Infrastructure/`(filter.commit 落地路徑) | 保存情境時 materialize 命中 entry_id |
| `Application/` 5 個 Page handler + filter.commit 擴充 | 對 manifest wire |
| `Application/` INF handler | infSamplePage |
| `wwwroot/js/steps/*`、對應 css | 五處「載入更多」(零邏輯) |
| `docs/jet-guide.md` / `development-status.md` / `development-log.md` | 落地回寫 |

## 新增/更新測試(TDD,三層)

- **Domain**:`PageCursor` 編碼→解碼 round-trip(entry_id / account_code / document_number);壞游標字串回錯;`pageSize` 夾擠(0/負→預設、>500→500)。
- **Application 驗收**(`HandlerTestHost`,對 manifest wire):
  - 各 Page action 分頁走訪到底:逐頁串接 == 一次性全量集合(**無漏、無重、order 穩定**);`nextCursor` 末頁為 null。
  - `result_filter_run`:filter.commit 後該情境命中 entry_id 落地正確(對 demo 獨立 recount);filterHitsPage 行層 == recount、傳票層 distinct doc 正確;惰性補materialize 對舊情境成立。
  - `infSamplePage`:回 60 行(或實際樣本數)、借/貸拆欄與日期/人員欄正確。
  - 失效:重投影後 `result_filter_run` 清空(同 gl_control_total 不變量測試模式)。
- **Infrastructure/parity**:每個 Page 查詢 SQLite vs SQL Server 逐頁等價(同游標→同頁、同 nextCursor);游標述詞展開布林式兩 provider 同結果(LocalDB 閘控)。
- **前端**:不寫 JS 業務規則測試(邊界);「載入更多」屬 GUI 行為,列 windows-handoff 人工驗收。

## 交付物

| 檔案/位置 | 變更 |
|:--|:--|
| `docs/action-contract-manifest.md` | 5 個 query.*Page + filter.commit 落地說明 |
| `src/JET/JET/Domain/`(新增 PageCursor / PageRequest / PageResult / row records) | 游標與契約 |
| `src/JET/JET/Infrastructure/{Sqlite,SqlServer}*`(Page 查詢、result_filter_run 建表/落地/失效、索引) | keyset SQL 雙 provider |
| `src/JET/JET/Application/`(5 Page handler、filter.commit 擴充、INF) | wire |
| `src/JET/JET/wwwroot/js/steps/*` + css | 五處「載入更多」 |
| `src/JET/tests/JET.Tests/...` | 上述三層測試 |
| `docs/jet-guide.md`、`docs/development-status.md`、`docs/development-log.md` | 落地回寫 |

## 驗證指令

```bash
dotnet build src/JET/JET.slnx --no-restore --nologo
dotnet test src/JET/tests/JET.Tests/JET.Tests.csproj --no-build --nologo
```

SQL Server 等價測試需本機 LocalDB 並設 `JET_SQLSERVER_CONNECTION`(閘控)。

## Windows 端待驗任務(落地時寫入 windows-handoff.md)

- 五個明細檢視(完整性差異、借貸不平、空值、篩選情境命中明細、INF 抽樣)出現「載入更多」,點擊接續載入下一頁、到底隱藏、載入中不卡頓。
- 大母體(140 萬列)下載入流暢,無整表卡頓。

## 與其他子專案的邊界

- **D2**:用 D1 的 `result_filter_run`(單情境命中)組「每傳票/每行 × C1–C10」tag 矩陣;D1 不做矩陣。
- **E**:用 D1 的 Page 系列走訪全量寫 15 表(step1 全科目、step2 INF 60、step4/4-1 命中明細)。
- **A/B**:已完成;回溯 row-tag(B)可作為情境之一,其命中經 D1 落地。
- **方法學**:INF 最少 60 樣本(已固定);本輪只回取,不改抽樣。
