# 編製人員升級設計（子專案 C）

這份是 2026-11-01 壓縮後的摘要。使用者原話、題目與回答、日期、數字與裁定照原文保留；實作經過、驗證收據與重跑過程壓成結果。
壓縮前的全文在提交 `85af454`，可用 `git show 85af454:docs/history/specs/2026-06-20-preparer-escalation-design.md` 取回。

## 狀態與依據

原文狀態是「已實作，待 GUI 驗收」：實作完成，自動化測試全部通過（本機 618 passed，SQL Server 等價測試實際執行），
待使用者 GUI 人工驗收，當時尚未驗收。這是「匯出底稿」里程碑的子專案 C（編製人員升級）。權威描述當時已回寫
`docs/jet-guide.md`、`docs/action-contract-manifest.md`、`docs/development-status.md` 與 `docs/development-log.md`。

盤查依據是 `.git/sdd/bcde-scope-draft.md`（子專案 C），以及方法學 step3 C5、C6 與 step1-2 編製人員表（`.git/sdd/workpaper-synthesis.md`）。

## 背景與方法學

事務所方法學的編製人員測試有兩條高風險條件：

- **C5「非授權編製人員」**：不應出現非授權人員編製的分錄，且排除系統自動拋轉。授權清單由查核團隊維護，step3 C5 的寫法是「`傳票建立人員_JE` 值不包含〔名單〕」。
- **C6「低頻編製者」**：全年編製分錄少於 12 筆（即 ≤ 11）的人員所編分錄風險較高，step3 C6 寫成「數字欄位【編製人員張數】值小於(含) 11」。門檻是查核員可調的輸入，方法學樣本用 11。

當時 JET 只有 `creator_summary`（R5），是前 50 名編製者的彙總，屬 Aggregate，不能當進階篩選的列述詞（`RuleCatalog.cs:43`），
沒有授權清單，也沒有依張數篩低頻。D1 已完成全量明細分頁，所以 C5 只要是可篩選的 row-tag，存成篩選情境後就能用
`query.filterHitsPage` 取回全部命中分錄，C 不需另造升級用的基礎設施。

## 範圍

要做的四件事：

1. **授權編製人員清單匯入**：`.xlsx` 單欄姓名，英文標頭，每次匯入整份取代，沿用 F 的參考資料匯入模式。新增 `target_authorized_preparer` 表與 staging 表，以 `IF NOT EXISTS` 加表，不升 schema 版本。
2. **C5「非授權編製人員」預篩選規則**（RowTag）：`created_by` 不在授權清單；名單沒匯入時不執行；可當進階篩選的列述詞。
3. **C6「低頻編製者」**：預篩選 row-tag 是該 `created_by` 全期分錄筆數 ≤ 固定預設 11；另新增進階篩選條件型別「自訂編製人員張數」，可輸入門檻 ≤N。
4. 前端加授權清單匯入卡，並顯示兩條新規則。

不做的事：不改 `creator_summary`；step1-2 全名單匯出、step3 條件呈現與匯出 writer 屬 E；tag 矩陣屬 D2；授權清單只比對姓名，不納入職稱或部門。

## 設計決定

**授權清單匯入。** 標頭例如 `AUTHORIZED_PREPARER`，空白列略過，姓名 `TRIM` 後存入。讀檔走既有 `OpenXmlSaxTableReader`，不新增讀取器。

- `staging_authorized_preparer_raw_row` 與其他 staging 表同形；`target_authorized_preparer(name TEXT NOT NULL PRIMARY KEY)` 去重。
- action 是 `import.authorizedPreparer.fromFile`，只收 `.xlsx`，匯入即投影。
- 重匯入時在同一交易呼叫 `RuleRunResultReset.ClearWithinAsync`，讓依賴名單的 C5 結果失效。

**C5 規則。** 登錄為 `("non_authorized_preparer", "nonAuthorizedPreparer", "非授權編製人員", "R10", RuleShape.RowTag)`。述詞：

```
created_by IS NOT NULL AND TRIM(created_by) <> '' AND TRIM(created_by) NOT IN (SELECT name FROM target_authorized_preparer)
```

空白的 `created_by` 不命中，因為無從判定是否授權。名單未匯入時不執行，回應該鍵為 `status="na"`，`naReason` 說明尚未匯入名單。

**C6 規則與自訂條件。** Domain 常數 `PreparerFrequency.DefaultMaxEntries = 11`。固定規則登錄為
`("low_frequency_preparer", "lowFrequencyPreparer", "低頻編製者", "R11", RuleShape.RowTag)`，述詞是
`created_by IN (SELECT created_by FROM target_gl_entry GROUP BY created_by HAVING COUNT(*) <= 11)`，門檻以參數綁定。
自訂條件型別 `CustomPreparerEntryCount` 帶 `int? MaxEntries`，同一述詞改用使用者輸入的門檻，範圍須 ≥1。
這和 A 的 `customTrailingZeros` 一樣是「固定預設規則加上自訂條件型別」兩條路並行。

## 邊界與驗收

架構邊界沿用專案規則：provider 分支只在 Infrastructure，前端沒有商業邏輯，action 形狀先寫進 `docs/action-contract-manifest.md`，
SQLite 與 SQL Server 結果等價，先寫測試，不自行 commit。測試重點是匯入整份取代並使結果失效、C5 與 C6 命中數等於獨立重算、
名單未匯入時為 `na`，以及 C5 情境可經 `filterHitsPage` 取回全部分錄。

Windows 端待人工驗收：

- 「匯入資料」出現授權編製人員清單上傳卡，上傳 `.xlsx` 單欄姓名後整份取代；重匯入使相關規則結果失效。
- 「資料驗證與測試」的預篩選多「非授權編製人員」（名單未匯入時顯示未執行）與「低頻編製者」（命中全期 ≤11 筆編製者的分錄），兩者都能檢視並載入更多。
- 「進階條件篩選」可用「自訂編製人員張數 ≤N」條件，並可存「非授權編製人員」情境後以命中分頁看全部分錄。

## 與其他子專案的關係

D1 提供全部分錄的分頁；D2 可把這兩條 row-tag 當矩陣條件；E 負責 step1-2 全名單與 step3 C5、C6 的呈現；C 沿用 A 的兩條路並行與 B 的結果失效做法。
