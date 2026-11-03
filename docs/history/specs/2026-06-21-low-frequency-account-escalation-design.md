# 低頻科目升級設計（子專案 C 補遺）

這份是 2026-11-01 壓縮後的摘要。使用者原話、題目與回答、日期、數字與裁定照原文保留；實作經過、驗證收據與重跑過程壓成結果。
壓縮前的全文在提交 `85af454`，可用 `git show 85af454:docs/history/specs/2026-06-21-low-frequency-account-escalation-design.md` 取回。

## 狀態與依據

原文狀態是「已實作，待 GUI 驗收」：自動化測試全部通過（本機 628 passed，SQL Server 等價測試實際執行），待使用者 GUI 人工驗收，當時尚未驗收。
這份是子專案 C（編製人員升級）的補遺。C 主體只做了編製人員維度（C5、C6），科目維度的 step3 **C9「罕用科目（科目張數 ≤ 11）」**
在 C 範圍外被漏掉，本補遺把它補成可當進階篩選列述詞的規則。權威描述當時已回寫 `docs/jet-guide.md`、`docs/action-contract-manifest.md`、
`docs/development-status.md` 與 `docs/development-log.md`。

盤查依據是 `.git/sdd/bcde-scope-draft.md`（修正 #4 把「科目張數」列為衍生旗標）、方法學 step3 C9（`.git/sdd/workpaper-synthesis.md`），
以及 2026-06-21 D2 前的自我審查：實機重解兩份 WorkingPaper 樣本，並對碼確認 `rareAccounts` 是彙總、不能當列述詞（`PrescreenRules.cs:5-6`、`RuleCatalog.cs:44`）。

## 背景與方法學

step3 C9「較少使用之科目」是實際在用的高風險條件：某科目全年度分錄筆數少於 12 筆（即 ≤ 11）時，它的分錄較容易藏匿違規或錯誤。
方法學寫成「數字欄位【科目張數】值小於(含) 11」，門檻是查核員可調的輸入，樣本用 11。
C9 另有「會計科目名稱不包含〔長串例外清單〕」的子條件，由使用者在情境裡以既有的 Text NotContains（`accName` 已在 `GlFieldWhitelist`）組合，本補遺不另做。

當時只有 `rare_accounts`（R6），是前 50 名的彙總，屬 `RuleShape.Aggregate`，不能當列述詞。這和 C 開工前 `creator_summary` 的處境相同，
當時 C 新增 `low_frequency_preparer`（R11, RowTag）與彙總並存。本補遺照同一模式新增 `low_frequency_account`（RowTag），
讓 C9 能存成篩選情境，也能被 D2 的 tag 矩陣納入。

## 範圍

要做的三件事：

1. **C9「低頻科目」預篩選規則**（RowTag）：某 `account_code` 全期分錄筆數 ≤ 固定預設 11；可當進階篩選列述詞；與 `rare_accounts` 並存，不取代。
2. **「自訂科目張數」進階篩選條件型別**：可輸入門檻 ≤N。
3. 前端在預篩選清單加「低頻科目之分錄」，進階篩選加「自訂科目張數」條件型別。

不做的事：不改 `rare_accounts`；不另造升級用的基礎設施，命中分錄走 C5、C6 已驗證的路徑，存情境後用 `query.filterHitsPage` 取回；
匯出 writer、step4-1 動態欄位集與 step1 全科目表屬 E；tag 矩陣屬 D2；科目名稱例外清單不做專屬畫面；不新增 schema 表或欄位。

## 設計決定

- 登錄為 `("low_frequency_account", "lowFrequencyAccount", "低頻科目", "R12", RuleShape.RowTag)`，wire key `LowFrequencyAccount = "lowFrequencyAccount"` 加入 `FilterableKeys`。
- Domain 常數 `AccountFrequency.DefaultMaxEntries = 11`。述詞以參數綁定門檻，兩種資料庫寫法相同：
  ```
  g.account_code IN (SELECT account_code FROM target_gl_entry GROUP BY account_code HAVING COUNT(*) <= @maxEntries)
  ```
- `PrescreenRunResult` 末位加 `LowFrequencyAccountCount`，回應為 `lowFrequencyAccount { status, count }`。這條規則沒有外部依賴，永遠可執行。
- 自訂條件型別 `CustomAccountEntryCount` 重用 C6 已加的 `FilterRuleSpec.MaxEntries`，因為一條規則只有一種型別，不另開欄位；驗證同樣要求 `MaxEntries >= 1`。
- 規則只讀 `target_gl_entry`，不依賴外部清單，沒有 C5 那種 `NOT IN` 空集合的風險，也不新增結果失效來源。

## 邊界與驗收

架構邊界沿用專案規則：provider 分支只在 Infrastructure，前端沒有商業邏輯，action 形狀先寫進 `docs/action-contract-manifest.md`，
SQLite 與 SQL Server 結果等價，先寫測試，不自行 commit。測試重點是固定規則與自訂門檻的命中數都等於獨立重算，
以及 C9 情境能經 `filterHitsPage` 取回全部分錄。

Windows 端待人工驗收：

- 預篩選新增「低頻科目之分錄」（命中全期 ≤11 筆科目的分錄），可以檢視並載入更多。
- 「進階條件篩選」可用「自訂科目張數 ≤N」條件（預設 11、最小 1）；預覽命中合理；已儲存情境摘要顯示「科目全期張數 ≤ N」。
- 可存「低頻科目」情境後以命中分頁看全部分錄；與科目名稱 Text NotContains 組合（重現 step3 C9）也正常。

## 與其他子專案的關係

本補遺照 C 主體的 C6（`lowFrequencyPreparer` 與 `customPreparerEntryCount`）做；D1 提供全部分錄的分頁；D2 可把 C9 當矩陣條件；E 負責 step1 全科目表與 step3 C9 的呈現。
