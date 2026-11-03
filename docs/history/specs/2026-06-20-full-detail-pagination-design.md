# 全量明細基礎設施設計（子專案 D1）

這份是 2026-11-01 壓縮後的摘要。使用者原話、題目與回答、日期、數字與裁定照原文保留；實作經過、驗證收據與重跑過程壓成結果。
壓縮前的全文在提交 `85af454`，可用 `git show 85af454:docs/history/specs/2026-06-20-full-detail-pagination-design.md` 取回。

## 狀態與依據

原文狀態是「已實作，待 GUI 驗收」：實作完成，自動化測試全部通過（本機 588 項，SQL Server 等價測試實際執行），
GUI 五處「載入更多」待使用者人工驗收。這是「匯出底稿」里程碑的子專案 D1（全量明細基礎設施）。權威描述當時已轉到
`docs/jet-guide.md`（§13 全量明細 keyset 分頁）、`docs/action-contract-manifest.md` 與 `docs/development-status.md`。

盤查依據是 `.git/sdd/bcde-scope-draft.md`（D 拆成 D1 與 D2）。分頁效率與兩種資料庫的做法經 Microsoft Learn 文件查證。

## 背景

匯出底稿需要全部明細，但當時各明細查詢一律截 `LIMIT 50` 或 `TOP (50)`，篩選命中不落地，INF 抽樣只回筆數與種子。
當時有五個截斷點：完整性全科目差異、借貸不平、空值紀錄、篩選命中（`filter.preview` ≤50）、INF 抽樣明細（沒有讀回路徑）。
D1 補上全量取得的基礎設施，給後續 D2（多情境 tag 矩陣）與 E（匯出 writer）使用，並讓 GUI 既有明細檢視能「載入更多」。

## 範圍

要做的四件事：

1. 五個 keyset 游標分頁查詢，也就是新的 `query.*Page` action：完整性差異、借貸不平、空值紀錄、篩選命中、INF 抽樣明細，都能一頁接一頁走訪到底。
2. `result_filter_run` 落地：已保存篩選情境（C1–C10）的命中 `entry_id` 寫進資料庫，`filterHitsPage` 讀它再接回 `target_gl_entry`。
3. INF 60 行明細回取：`infSamplePage` 讀 `result_inf_sampling_test_sample`，回傳 step2 需要的欄位。
4. GUI 五個明細檢視加「載入更多」。

不做的事：多情境逐列 tag 矩陣屬 D2；匯出 writer 與 `*_JE` 正準名對照屬 E；不做任意跳頁，因為底稿匯出與 GUI 審閱都只需要往下一頁。
既有 `validate.run` 與 `filter.preview` 的 ≤50 快速預覽保留不動，互動審閱仍用它。

## 設計決定

**一律用 keyset，不用 OFFSET。** Microsoft Learn 說明 OFFSET 分頁要先處理被略過的列再丟棄，略過越多越慢，並發變動還會跳列或重複。JET 母體可達百萬列等級。

**游標述詞寫成展開布林式。** SQL Server 不支援 row-value 元組比較（MS-TSQLISO F641），所以兩種資料庫共用下面的寫法：

```sql
-- 單鍵游標(entry_id / account_code / document_number):
WHERE <key> > @cursorKey
-- 多鍵游標(若日後需要 k1 排序、k2 tiebreaker):
WHERE k1 > @c1 OR (k1 = @c1 AND k2 > @c2)
```

取一頁的筆數用各自的方言：SQLite 用 `LIMIT @pageSize`，SQL Server 用 `ORDER BY ... OFFSET 0 ROWS FETCH NEXT @pageSize ROWS ONLY`。
OFFSET 固定是 0，只當取前幾筆用，不是 offset 分頁。方言差異由 `ISqlDialect` 處理。

**排序鍵必須唯一，而且要有索引。** `target_gl_entry` 既有索引是 PK `entry_id`、`account_code` 與 `document_number`；`result_filter_run` 以 `(scenario_position, entry_id)` 為 PK。

| Page action | 資料 | 排序與游標鍵 |
|:--|:--|:--|
| `query.completenessDiffPage` | TB 與 GL 逐科目差異（diff≠0） | `account_code` ASC |
| `query.docBalancePage` | 借貸不平傳票 | `document_number` ASC |
| `query.nullRecordsPage` | 空值紀錄（日期區間外、空白摘要、空白傳票號或科目） | `entry_id` ASC，另帶分類參數 |
| `query.filterHitsPage` | 單一已存情境的行層命中 | `entry_id` ASC |
| `query.infSamplePage` | INF 抽樣 60 行明細 | `entry_id` ASC |

完整性差異的全量走訪用 `account_code` 排序；既有 ≤50 預覽維持 `ABS(diff) DESC`，最大差異排前面。預覽看重點，分頁走全量。

**游標格式。** 回應是 `{ rows: [...], nextCursor: string|null }`。`nextCursor` 是 Domain 純函式把末列鍵編成的字串，`null` 表示已到底；首頁傳 `cursor: null`。
`pageSize` 預設 **200**、上限 **500**。游標格式不符時回錯誤，讓 handler 報參數錯，不靜默略過。

**`result_filter_run` 只存參照。** 新表隨基底 schema 以 `IF NOT EXISTS` 建立，不升版：

```sql
CREATE TABLE IF NOT EXISTS result_filter_run (
    scenario_position INTEGER NOT NULL,
    entry_id          INTEGER NOT NULL,
    PRIMARY KEY (scenario_position, entry_id)
);
```

- 只存哪個情境命中哪些 `entry_id`；傳票層由 `filterHitsPage` 取 distinct `document_number`。不存整列，避免重複與不同步。
- `filter.commit` 保存情境時，以既有 `GlFilterWhereBuilder` 組命中述詞寫入；同一 `scenario_position` 先刪後插。情境刪除或覆寫時同步刪掉該 position 的列。
- `filterHitsPage` 讀取時若該 position 沒有落地列，例如 D1 之前保存的舊情境，當場以同一述詞補算並落地。
- 重投影清掉 `target_gl_entry` 時，在 `RuleRunResultReset.ClearWithinAsync` 的同一交易一併 `DELETE FROM result_filter_run`。

**INF 明細只讀回，不重抽樣。** 依 `entry_id` 排序分頁，回傳傳票號、科目編號與名稱、借方金額、貸方金額（由 signed amount 拆兩欄）、
過帳日、核准日、編製人員、核准人員與摘要。金額取既有 `debit_amount_scaled` 與 `credit_amount_scaled`。

**GUI「載入更多」。** 按下後帶目前的 `nextCursor` 呼叫對應的 `query.*Page`，把回傳列接在現有列之後；`nextCursor` 為 `null` 時隱藏按鈕。
載入中按鈕停用並顯示旋圈，不凍結畫面。前端不判命中、不算差異、不組 SQL。

## 邊界與驗收

架構邊界沿用專案規則：provider 分支只在 Infrastructure，前端沒有商業邏輯，action 形狀先寫進 `docs/action-contract-manifest.md`，
SQLite 與 SQL Server 結果等價，先寫測試，不自行 commit。測試分 Domain、Application 驗收與資料庫等價三層，
重點是逐頁串接的結果等於一次全量、沒有漏也沒有重複，以及重投影後 `result_filter_run` 會清空。

Windows 端待人工驗收：五個明細檢視出現「載入更多」，可以接續載入、到底隱藏、載入中不卡頓；百萬列等級的大母體下載入流暢。

## 與其他子專案的關係

- D2 用 `result_filter_run` 組「每傳票或每行 × C1–C10」的 tag 矩陣。
- E 用分頁查詢走訪全量，寫 15 表（step1 全科目、step2 INF 60、step4 與 step4-1 命中明細）。
- A 與 B 已完成；B 的回溯 row-tag 可以當情境之一。
- INF 最少 60 樣本已固定，本輪只回取，不改抽樣。
