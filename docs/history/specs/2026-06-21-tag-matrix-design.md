# 多情境逐列 tag 矩陣設計(子專案 D2)

> **狀態:已實作,待 GUI 驗收。** 三 query action(`tagMatrixScenarios`／`tagMatrixVoucherPage`／`tagMatrixRowPage`)、`idx_result_filter_run_entry` 索引、提取 `FilterRunMaterializeService` 共用服務、前端矩陣預覽皆已落地;自動化測試全套本機綠(含 SQL Server parity 實跑)。GUI 目視驗收與提交都還沒做。 「匯出底稿」里程碑子專案 **D2**:把已落地的篩選命中(`result_filter_run`,D1)組成方法學 step4(傳票層 C1..CN 布林矩陣)與 step4-1(行層逐行 tag)的查詢基礎設施。依賴 D1(`result_filter_run` + Page 原語)、C/C補遺(C5/C6/C9 等 row-tag)、A(情境上限 10)。
>
> **盤查依據:** `.git/sdd/bcde-scope-draft.md`(D2 = 多情境 × 逐列 tag 矩陣,最高風險);2026-06-21 D2 前自我審查實機重解兩 WorkingPaper 樣本(step3/step4/step4-1)+ 對碼確認 `result_filter_run` 行層命中可同時導出兩矩陣(`SqliteFilterRunMaterializer.cs`、`GlRulePredicates.cs` C1 註解「tag 落在符合的那些分錄列上」)。

## 背景與動機

事務所方法學 step4/step4-1 是 JE 測試的核心產出:

- **step4「符合高風險條件傳票」(傳票層):** 每列一張命中 ≥1 高風險條件的去重傳票,欄位 = 傳票號/總帳日/編製者/傳票總額 + **C1..C10 命中布林**(該傳票是否命中各條件,Y/空白)+ 手填查核欄。樣本實證:福懋 step4 的 F–O 欄即 C1..C10。
- **step4-1「符合高風險條件傳票明細」(行層):** 命中傳票的**每一行** GL 明細,欄位 = 傳票號/項次/日期/人員/科目/金額/摘要 + **逐行 C*_TAG**(該行是否命中各條件)。樣本實證:同一傳票內只有命中那條件的特定行有 Y(如罕用科目 C9 只標在罕用科目的那一行)。

「C1..CN」只是位置槽,實質就是審計團隊存的篩選情境(`config_filter_scenario`,position 1..N,N ≤ 10)。JET 現況:`filter.preview` 一次只算單一情境、`query.filterHitsPage`(D1)一次只回單一 position 的命中行;**無「跑全部情境、逐傳票/逐行標記命中了哪些情境」的矩陣**。D2 補上。

## 已驗證的資料地基(D2 前自我審查結論)

`result_filter_run` 的 schema = `(scenario_position, entry_id)`,命中**存在 GL 行層**(filter.commit 對每個已存情境 `INSERT ... SELECT entry_id WHERE {述詞}`)。`GlRulePredicates` 全部述詞以單行為單位評估,跨行條件(C1 借貸組合、account_pair)用傳票範圍 EXISTS 子查詢。因此這一個行層結構**同時**導得出:

- **step4 傳票層:** 某傳票命中情境 S ⟺ 該傳票存在任一行於 `result_filter_run` 標記為 S(`EXISTS` / `JOIN ... GROUP BY document_number`)。
- **step4-1 行層:** 某行命中情境 S ⟺ `(S, 該行 entry_id)` 存在於 `result_filter_run`(直接成員判定)。

→ **D2 不需要新的命中儲存或新矩陣表**;只需從 `result_filter_run` 即時 pivot。

## 設計決策

### 即時算,不落地新矩陣表(Good Taste)

命中已落地於 `result_filter_run`;矩陣(pivot)是便宜的 `JOIN`/`GROUP BY`/keyset 查詢。另存一張 pivot 後的矩陣表會**重複資料**、引入新的失效來源、且可能與 `result_filter_run` 不一致。故 D2 **即時從 `result_filter_run` 算矩陣**,沿用 D1 的 Page 原語做 keyset 分頁。永遠與命中一致、零新失效不變量。

### 每頁兩段查詢(provider 中立,避免方言聚合)

「每傳票/每行命中了哪些情境位置」是一對多。要在單一 SQL 內把位置聚成一欄需 `group_concat`(SQLite)/`STRING_AGG`(SQL Server)等**方言相異**聚合。為維持 provider 中立、且鍵集分頁乾淨,採**每頁兩段查詢**:

1. **實體頁查詢(keyset):** 取本頁的去重傳票(step4,鍵 `document_number` ASC)或命中傳票之所有行(step4-1,鍵 `entry_id` ASC)+ 核心顯示欄,`ORDER BY 鍵 + Dialect.LimitClause`。回本頁鍵範圍 `(@cursor, @lastKey]`。
2. **位置查詢:** 對**同一鍵範圍**取 `(實體鍵, scenario_position)`,在 handler(C#)分組成每實體的「命中位置有序去重清單」。

兩段皆參數綁定、純 ANSI(除 LimitClause 走方言)、每頁有界(≤ pageSize 實體 × ≤10 位置)。位置→C1..CN 欄的對映屬 E(writer)。

### 輔助索引(加法,不升版)

`result_filter_run` PK = `(scenario_position, entry_id)`,`entry_id` 非前導 → 「以 entry_id join 回 target_gl_entry 算傳票」「行頁位置查詢 `WHERE entry_id` 範圍」未獲最佳索引。新增 `idx_result_filter_run_entry ON result_filter_run(entry_id, scenario_position)`(雙 provider,`IF NOT EXISTS`/`IF ... IS NULL`,**不升 schema 版本**,同 D1 的加法建表慣例)。`target_gl_entry.document_number` 已有索引,傳票分組 join 不慢。

### 惰性 materialize(沿用 D1)

矩陣須反映全部已存情境。沿用 `query.filterHitsPage` 的惰性補算:首次查詢若空(或 summary 全 0)且 `config_filter_scenario` 有定義,重用 `IFilterRunMaterializer` 對全部情境落地後重取一次。為免重複,把 `filterHitsPage` 既有的私有 `MaterializeAllAsync` 提取成共用 Application 服務 `FilterRunMaterializeService`,由 `filterHitsPage` + D2 三 handler 共用(DRY,單一事實來源)。

## 目標與範圍

### 做

1. **`query.tagMatrixScenarios`**(矩陣表頭 / step3 交叉參考):回傳全部已存情境的 `position`、`name` + **傳票層命中數**(`COUNT(DISTINCT document_number)`)+ **行層命中數**(`COUNT(*)`),依 position 排序;0 命中亦列(count=0)。含惰性 materialize 守門。
2. **`query.tagMatrixVoucherPage`**(step4 傳票層):keyset 分頁去重命中傳票,每列 = `documentNumber`/`postDate`/`createdBy`/`voucherTotal`(傳票總額,scaled 整數)+ `matchedPositions`(命中位置有序去重清單)。鍵 `document_number` ASC。
3. **`query.tagMatrixRowPage`**(step4-1 行層):keyset 分頁命中傳票之**所有行**,每列 = `documentNumber`/`lineItem`/`postDate`/`approvalDate`/`createdBy`/`approvedBy`/`accountCode`/`accountName`/`amount`(signed scaled)/`description` + `matchedPositions`(該行命中位置;非命中行為空清單)。鍵 `entry_id` ASC。
4. **前端「高風險條件矩陣」預覽**:情境摘要(C1..CN 名稱 + 命中數)+ 傳票矩陣(載入更多)+ 點傳票展開其行層 tag(載入更多)。沿用 D1 載入更多基礎設施,零商業邏輯。

### 不做(移交 / YAGNI)

- **不做匯出 writer**(step4/step4-1 寫進 .xlsx、C*_TAG 動態欄位集、傳票總額格式)— 屬 E。D2 只回矩陣資料。
- **不另存 pivot 矩陣表 / 不改 `result_filter_run` schema**(只加索引)。
- **不改既有規則述詞 / filter.commit / filterHitsPage 的命中語意**;D2 純讀 `result_filter_run`。
- **不做 step4-1 動態欄位決策**(哪些 C*_TAG 欄出現)— 屬 E;D2 回完整位置清單。
- 不新增規則 / 情境型別(A/C/C補遺已備齊 row-tag)。

## 開發原則與邊界(harness — 本 spec 的 Global Constraints)

- **分層**:`Bridge/Form1 → Application → Domain ← Infrastructure`。矩陣列型別/Page 原語在 Domain;**provider 分支只在 Infrastructure**(SELECT 骨架 + 方言);Application handler 編排(惰性 materialize、scaled→顯示換算)。
- **前端零商業邏輯**:不在前端/Form1/HTML/CSS/JS 做查詢、pivot、SQL、計算;只顯示與組 wire。
- **postMessage 邊界**:不在 `jet-api.js` 以外呼叫 `window.chrome.webview.postMessage`。
- **查詢 = 參數化 SQL**:識別字只來自常數/白名單;游標、position、鍵範圍一律參數綁定;`DbCommand` provider 中立;方言相異片段走 `ISqlDialect`(LimitClause/ParameterName)。
- **keyset 分頁**:展開布林式游標(`鍵 > @cursor`),不用 OFFSET 大表退化;排序鍵唯一有索引(`document_number` 對去重傳票需確保穩定排序——以 `document_number` 為鍵;`entry_id` 為行頁鍵,PK 唯一)。游標 opaque(Base64,`PageCursor`);壞游標 fail loud(`invalid_payload`,沿用 D1)。
- **契約先行**:動 wire 形狀前**先改 `docs/action-contract-manifest.md`**(三新 query action + facade);`ActionContractTests` 鎖回應形狀。
- **雙 provider 等價**:SQLite 與 SQL Server 兩側同步;每個 Page/summary 查詢**比照 D1 加 `[SqlServerFact]` parity**(雙 provider 走訪等價 / count 等價,無 LocalDB clean skip,非 sqlite-only)。
- **不破壞失效不變量**:`result_filter_run` 隨 `RuleRunResultReset` 失效(D1 已含);D2 純讀,無新失效源;新增索引隨表存在。
- **不編輯** WinForms designer 產生檔。
- **測試三層(jet-testing)**:Domain(Page 原語既有;矩陣列型別)+ Application 驗收(`HandlerTestHost`,對 manifest wire:summary count == 獨立 recount、voucher/row 矩陣命中位置 == 獨立 recount、惰性 materialize、壞游標 invalid_payload)+ Infrastructure/parity(真 SQLite + SQL Server LocalDB 閘控,走訪等價)。
- **TDD**;**不自行 commit**(版本控制由使用者親自下令;subagent 執行階段亦不 commit,tree-diff 隔離);所有 subagent `claude-opus-4-8` effort max。

---

## 動作契約(摘要;細節進 manifest)

### `query.tagMatrixScenarios`
- payload:無(用 session projectId)。
- response:`{ scenarios: [ { position, name, voucherHitCount, rowHitCount } ], … }`,依 position 升冪。
- 惰性 materialize:全部 count 為 0 且有已存情境 → 落地後重算一次。

### `query.tagMatrixVoucherPage`
- payload:`{ cursor?, pageSize? }`(壞 cursor → invalid_payload;pageSize 預設 200/上限 500)。
- response:`{ rows: [ { documentNumber, postDate, createdBy, voucherTotal, matchedPositions:[int] } ], nextCursor }`。
- `voucherTotal` = 該傳票 `SUM(debit_amount_scaled)`(傳票借方總額;顯示換算 scaled→decimal 由 handler;對齊樣本 step4「傳票總金額」為毛額正數)。
- 鍵 `document_number` ASC;惰性 materialize(首頁空 + 有情境 → 落地重取)。

### `query.tagMatrixRowPage`
- payload:`{ cursor?, pageSize? }`。
- response:`{ rows: [ { documentNumber, lineItem, postDate, approvalDate, createdBy, approvedBy, accountCode, accountName, amount, matchedPositions:[int], description } ], nextCursor }`。
- 列集 = 命中傳票(任一行入 `result_filter_run`)之**所有行**;`matchedPositions` 為該行(entry_id)直接命中的位置(非命中行為 `[]`)。
- 鍵 `entry_id` ASC;惰性 materialize。

## 受影響的現行碼與新增(盤查)

| 位置 | 動作 |
|:--|:--|
| `docs/action-contract-manifest.md` | 先行:三 query action + facade 對照 + 細節段 |
| `Domain/PageRows.cs`(或新檔) | 新增 `VoucherTagRow`、`RowTagRow`、`ScenarioTagSummary` 列型別 |
| `Infrastructure/{Jet,SqlServer}ProjectDatabase.cs` | 加 `idx_result_filter_run_entry(entry_id, scenario_position)`(IF NOT EXISTS,不升版) |
| `Application/FilterRunMaterializeService.cs`(新) | 提取 filterHitsPage 的 MaterializeAllAsync 為共用服務 |
| `Application/QueryFilterHitsPageHandler.cs` | 改用共用服務(DRY,語意不變) |
| `Domain/I*TagMatrix*Repository.cs`(三介面)+ `Infrastructure/{Sqlite,SqlServer,ProviderRouting}*` | summary + voucherPage + rowPage 三組查詢(兩段查詢、keyset、方言 LimitClause) |
| `Application/QueryTagMatrix{Scenarios,VoucherPage,RowPage}Handler.cs`(新) | 編排 + 惰性 materialize + scaled 換算 |
| `AppCompositionRoot.cs` | 註冊三 handler + 三 repo + 共用服務 |
| `wwwroot/js/jet-api.js` + `steps/*` + css | 三 action 字串 + 矩陣預覽(載入更多;零邏輯) |
| `docs/jet-guide.md`/`development-status.md`/`development-log.md`/`windows-handoff.md` | 回寫 + 待驗卡 |

## 新增/更新測試(TDD,三層)

- **Domain**:矩陣列型別位置式記錄(欄序對齊 wire);Page 原語沿用既有測試。
- **Application 驗收**(`HandlerTestHost`,對 manifest wire):
  - `tagMatrixScenarios`:各情境 voucherHitCount == `COUNT(DISTINCT document_number)` 獨立 recount、rowHitCount == `COUNT(*)` recount;0 命中情境列出;惰性 materialize(未跑 commit 也能算)。
  - `tagMatrixVoucherPage`:命中傳票集 == 獨立 recount;每傳票 matchedPositions == recount(該傳票命中的 distinct position);voucherTotal == SUM(debit) recount;走訪全部頁無重複、單一升冪;壞 cursor → invalid_payload。
  - `tagMatrixRowPage`:列集 == 命中傳票之所有行(含非命中行)recount;每行 matchedPositions == recount(該 entry_id 命中的 position;非命中行為空);走訪等價;壞 cursor。
- **Infrastructure/parity**:三查詢 SQLite vs SQL Server `[SqlServerFact]` 走訪等價(鍵序列 SequenceEqual + matchedPositions 等價;`Count > pageSize` 擋偽綠;clean skip)。
- **前端**:不寫 JS 業務測試;矩陣預覽 + 載入更多列 windows-handoff。

## 驗證指令

```bash
dotnet build src/JET/JET.slnx --no-restore --nologo
dotnet test src/JET/tests/JET.Tests/JET.Tests.csproj --no-build --nologo
```

SQL Server 等價測試需本機 LocalDB 並設 `JET_SQLSERVER_CONNECTION`(閘控)。

## Windows 端待驗任務(落地時寫入 windows-handoff.md)

- 「進階條件篩選」(或新檢視)出現「高風險條件矩陣」:情境摘要列 C1..CN(名稱 + 傳票/行命中數);傳票矩陣每列顯示命中了哪些 C(載入更多逐頁);點一張傳票展開其行層明細與逐行 tag(載入更多)。
- 多情境(存滿到 10)時矩陣欄位正確對位;大母體逐頁流暢、無爆版;未存任何情境時友善空狀態。

## 與其他子專案的邊界

- **D1**:沿用 Page 原語(PageCursor/PageRequest/PageResult)、`result_filter_run`、惰性 materialize;D2 即時 pivot,不另落地。
- **C/C補遺/A**:矩陣的情境來自使用者已存(可含 C5/C6/C9/backdated/週末/假日… 任何 row-tag);D2 不限定條件種類。
- **E(最後一棒)**:E 把 D2 的矩陣資料寫進 .xlsx step4/step4-1(C*_TAG 動態欄位集、傳票總額格式、手填欄留空、step3 定義表);D2 只提供查詢。
