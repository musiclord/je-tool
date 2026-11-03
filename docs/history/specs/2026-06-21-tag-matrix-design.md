# 多情境逐列 tag 矩陣設計（子專案 D2）

這份是 2026-11-01 壓縮後的摘要。使用者原話、題目與回答、日期、數字與裁定照原文保留；實作經過、驗證收據與重跑過程壓成結果。
壓縮前的全文在提交 `85af454`，可用 `git show 85af454:docs/history/specs/2026-06-21-tag-matrix-design.md` 取回。

## 狀態與依據

原文狀態是「已實作，待 GUI 驗收」：三個 query action（`tagMatrixScenarios`、`tagMatrixVoucherPage`、`tagMatrixRowPage`）、
`idx_result_filter_run_entry` 索引、共用服務 `FilterRunMaterializeService` 與前端矩陣預覽都已落地；自動化測試全部在本機通過，
含 SQL Server 等價測試實際執行。GUI 目視驗收與提交都還沒做。

D2 是「匯出底稿」里程碑的子專案，把已落地的篩選命中（D1 的 `result_filter_run`）組成方法學 step4（傳票層 C1..CN 布林矩陣）
與 step4-1（行層逐行 tag）的查詢基礎設施。它依賴 D1、C 與 C 補遺的 row-tag，以及 A 的情境上限 10。

盤查依據是 `.git/sdd/bcde-scope-draft.md`（D2 = 多情境 × 逐列 tag 矩陣，最高風險），以及 2026-06-21 D2 前的自我審查：
實機重解兩份 WorkingPaper 樣本的 step3、step4、step4-1，並對碼確認 `result_filter_run` 的行層命中可同時導出兩種矩陣
（`SqliteFilterRunMaterializer.cs`、`GlRulePredicates.cs` 的 C1 註解）。

## 背景

- **step4「符合高風險條件傳票」（傳票層）**：每列一張命中至少一條高風險條件的去重傳票，欄位是傳票號、總帳日、編製者、傳票總額、C1..C10 命中布林（Y 或空白）與手填查核欄。樣本實證:一家公司的 step4 的 F–O 欄即 C1..C10。
- **step4-1「符合高風險條件傳票明細」（行層）**：命中傳票的每一行 GL 明細，欄位是傳票號、項次、日期、人員、科目、金額、摘要與逐行 C*_TAG。樣本中同一傳票只有命中該條件的那幾行有 Y，例如罕用科目 C9 只標在罕用科目那一行。

「C1..CN」只是位置，實質是審計團隊存的篩選情境（`config_filter_scenario`，position 1..N，N ≤ 10）。當時 `filter.preview` 一次只算一個情境，
`query.filterHitsPage` 一次只回一個 position，沒有「跑全部情境、逐傳票與逐行標出命中哪些情境」的矩陣。

## 資料基礎

`result_filter_run` 的 schema 是 `(scenario_position, entry_id)`，命中存在 GL 行層。`GlRulePredicates` 的述詞都以單行評估，
跨行條件（C1 借貸組合、account_pair）用傳票範圍的 EXISTS 子查詢。所以同一個行層結構可以導出：

- 傳票層：某傳票命中情境 S，等於該傳票有任一行在 `result_filter_run` 標記為 S。
- 行層：某行命中情境 S，等於 `(S, 該行 entry_id)` 存在於 `result_filter_run`。

因此 D2 不需要新的命中儲存或矩陣表，只要從 `result_filter_run` 即時轉置。

## 設計決定

- **即時計算，不落地矩陣表。** 另存轉置後的矩陣會重複資料、多一個失效來源，也可能和 `result_filter_run` 不一致。
- **每頁兩段查詢。** 單一 SQL 把位置聚成一欄需要 `group_concat` 或 `STRING_AGG` 這類各資料庫不同的聚合，所以改成兩段：先以 keyset 取本頁實體（傳票層鍵 `document_number`，行層鍵 `entry_id`）與核心欄位，再對同一鍵範圍取 `(實體鍵, scenario_position)`，由 handler 分組成「命中位置的有序去重清單」。每頁最多 pageSize 個實體乘上 10 個位置。位置對應到 C1..CN 欄屬 E。
- **加輔助索引，不升版。** `result_filter_run` 的 PK 以 `scenario_position` 開頭，用 `entry_id` 接回明細時沒有好索引，因此新增 `idx_result_filter_run_entry ON result_filter_run(entry_id, scenario_position)`。
- **沿用惰性落地。** 首次查詢若沒有結果（或摘要全為 0）而有已存情境，就對全部情境落地後重取一次。原本 `filterHitsPage` 私有的 `MaterializeAllAsync` 提取成共用服務 `FilterRunMaterializeService`，由 `filterHitsPage` 與 D2 三個 handler 共用。
- 游標沿用 D1 的 Base64 `PageCursor`，壞游標回 `invalid_payload`；`pageSize` 預設 200、上限 500。

## 三個 action

| action | payload | 回應 |
|:--|:--|:--|
| `query.tagMatrixScenarios` | 無，用 session 的 projectId | `{ scenarios: [ { position, name, voucherHitCount, rowHitCount } ] }`，依 position 升冪；0 命中也列出 |
| `query.tagMatrixVoucherPage` | `{ cursor?, pageSize? }` | `{ rows: [ { documentNumber, postDate, createdBy, voucherTotal, matchedPositions:[int] } ], nextCursor }`，鍵 `document_number` ASC |
| `query.tagMatrixRowPage` | `{ cursor?, pageSize? }` | `{ rows: [ { documentNumber, lineItem, postDate, approvalDate, createdBy, approvedBy, accountCode, accountName, amount, matchedPositions:[int], description } ], nextCursor }`，鍵 `entry_id` ASC |

- `voucherHitCount` 是 `COUNT(DISTINCT document_number)`，`rowHitCount` 是 `COUNT(*)`。
- `voucherTotal` 是該傳票 `SUM(debit_amount_scaled)`，也就是借方總額；對齊樣本 step4「傳票總金額」為毛額正數。
- 行層的列集是命中傳票的所有行；`matchedPositions` 是該行直接命中的位置，沒命中的行為空清單。

前端「高風險條件矩陣」預覽顯示情境摘要（C1..CN 名稱與命中數）、傳票矩陣（載入更多），點傳票可展開行層 tag。

不做的事：匯出 writer、C*_TAG 動態欄位集與傳票總額格式屬 E；不另存矩陣表，也不改 `result_filter_run` schema（只加索引）；
不改既有規則述詞、`filter.commit` 與 `filterHitsPage` 的命中語意；不新增規則或情境型別。

## 邊界與驗收

架構邊界沿用專案規則：provider 分支只在 Infrastructure，前端只顯示不計算，action 形狀先寫進 `docs/action-contract-manifest.md`，
SQLite 與 SQL Server 結果等價，先寫測試，不自行 commit。測試重點是各情境命中數、每傳票與每行的命中位置都等於獨立重算，
行層列集包含非命中行，走訪全部頁沒有重複，以及惰性落地與壞游標的處理。

Windows 端待人工驗收：出現「高風險條件矩陣」，情境摘要列出 C1..CN 與命中數，傳票矩陣每列顯示命中哪些 C 並能逐頁載入，
點一張傳票可展開行層明細與逐行 tag；存滿 10 個情境時欄位對位正確；大母體逐頁流暢；沒有存任何情境時顯示空狀態。

## 與其他子專案的關係

D2 沿用 D1 的分頁原語、`result_filter_run` 與惰性落地；矩陣的情境可以含任何 row-tag；最後由 E 把矩陣寫進 .xlsx 的 step4 與 step4-1
（C*_TAG 動態欄位集、傳票總額格式、手填欄留空、step3 定義表）。
