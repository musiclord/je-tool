# step4-1 Legacy 語意與可重現基準證據索引

這份是 2026-11-01 壓縮後的摘要。使用者原話、題目與回答、日期、數字與裁定照原文保留；實作經過、驗證收據與重跑過程壓成結果。
壓縮前的全文在提交 `85af454`，可用 `git show 85af454:docs/history/specs/2026-07-27-step4-1-legacy-baseline.md` 取回。

日期：2026-07-27。狀態：已落地並通過自動驗證。

這份保存的是 2026-07-27 Legacy 慢路徑的效能基準與當時的欄寬 fingerprint，不是 2026-07-28 顯示寬度修補後的現行標準。
現行的不換行、CJK 安全邊距與跨資料庫一致性見 `2026-07-28-acceptance-repair.md`。大型 workbook、DuckDB 與 TRX 留在被忽略的 `TestResults`，
現行行為以 `docs/jet-guide.md` 與 `docs/action-contract-manifest.md` 為準。

## 正確性基準

- Legacy 小資料基準 `step4-1-legacy-v1` 在 SQLite、DuckDB、SQL Server 的 normalized logical fingerprint 都是 `67B9CA38FA17BD971FE13F00B5958450B59CF16848E026B8269D62B333FABE0A`。它鎖住 12 個優先欄、其餘 `_JE` 與 `_JE_S` 依序排列的欄、row-hit tag、null、文字與數字 line item、`entry_id` 作為同值排序依據、signed amount、Date、Time、Boolean 與 `1E+100` 的原生儲存格型別、完整資料欄寬，以及排除 `voucherDate`。
- 數字游標基準 `step4-1-numeric-line-cursor-v2` 的三種資料庫 fingerprint 都是 `3DDFD652FD0C670005E7A6B88CAD710BA9C913BC76E0F151555ECFEE851E779D`。205 列把 `1E-20` 與 `2E-20` 放在預設 200 列一頁的分界兩側，並含 `1E+100`–`4E+100`；它鎖住持久化的數字排序鍵、跨頁接續與 Number、scale 20 的原生輸出。
- 三種資料庫的 schema 現行版為 6。v5 到 v6 只新增可為 null 的 `line_item_numeric_sort_key`，舊列不從顯示字串猜測回填。非空的 Number line item 若缺排序鍵，step4-1 以 `stale_result` 停止，要求重新匯入、配對與投影。

## 固定滿頁基準

- 執行設定：Release、DuckDB、固定種子 `20260727`。
- 測試資料：恰好 1,048,571 筆，建立耗時 10,159.1909 ms，不計入匯出時間；DuckDB 與三份輸出都在 `C:\` 同一個 NTFS 實體磁碟。
- Excel 範圍：資料精確佔 rows 6–1,048,576；step4-1 恰好一張，dimension `A1:P1048576`，沒有遺失或重複。
- 刻意保留的 Legacy 路徑：page size 200、每次匯出兩遍各 5,243 頁、10,486 次 typed page call、20,972 次 reader data command、第二本 workbook 合併；`preparedSet=false`、`singleReader=false`、`directTemplateSax=false`。
- 三次耗時：2,222,087.6258 ms、2,114,888.8913 ms、2,066,202.9736 ms。
- 中位數：2,114,888.8913 ms（35 分 14.889 秒）。
- 三次輸出都是 124,055,972 bytes，normalized logical fingerprint 都是 `D59613C88E6863AF0DDD65363D3D7C9B49C9A697AFCB05422F131D09B8FF1018`。Package hash 因封裝中繼資料而不同，這是合理的；normalized fingerprint 納入值、原生儲存格型別、數字格式、欄寬與 BestFit。
- 欄寬：A:P 全部 `BestFit=true`；`LATE_WIDE_JE` 由末段完整資料推到 Excel 上限 255，`C1_TAG` 與 `C10_TAG` 分別為 8 與 9，其餘精確欄寬存在 JSON。
- 環境：Microsoft Windows `10.0.26200` x64、.NET `10.0.10` x64、16 logical processors、AMD64 Family 25 Model 97；Server GC false，總可用記憶體 33,946,124,288 bytes，記錄到的 peak working set 5,346,033,664 bytes。

## 原始證據

基準資料夾是 `src/JET/tests/JET.Tests/TestResults/step4-1-baseline/step41-legacy-baseline-20260727-042134917-0f1ece0c0ea542b5be4323355d9db72e/`，
內含 `baseline-evidence.json`、三份 `workingpaper-run-*.xlsx` 與 `step41-baseline-v1/jet.duckdb`（173,289,472 bytes）。跨資料庫一致性證據在
`src/JET/tests/JET.Tests/TestResults/step4-1-provider-parity/`。各檔的 SHA-256 與 Excel 讀回檢查的路徑見壓縮前全文。

驗證結果：

- 本機定向測試 61 項通過、0 失敗，4 項只能在 SQL Server 執行而略過；同一份程式另在 SQL Server 2022 Developer 隔離庫執行，7 項全部通過、0 略過，補上跨資料庫一致性與 v5 到 v6 遷移的環境缺口。
- 完整基準測試 1 項通過，耗時 1 h 47 m。
- Release 建置 0 warning、0 error。
- Excel 讀回檢查：14 張 worksheet 可讀，公式錯誤搜尋 0 命中；最終 SQLite 小資料 workbook 的 Step4-1 已目視讀回，沒有重要裁切或重疊。
- 診斷期間曾有 2 個預期中的修正前失敗，修正後的結果取代它。較早兩次 `step41-legacy-baseline-20260727-021732430-*` 與 `step41-legacy-baseline-20260727-031759960-*` 是不完整嘗試，不是基準證據，也不納入中位數。

## 邊界

本階段沒有跑完整測試套件、AgentGuiTest `All`、FolderProfile 或發布版 Release、Microsoft Excel、企業 CFA 或 DLP、私有 PBC，也沒有跑千萬列等級的真實案件；
不更新 `docs/development-status.md` 檔首的完整套件基線，也不宣稱人工驗收。

本階段沒有實作 prepared set、single reader、WorkingPaper 直接 SAX 或五倍效能門檻。唯一可開始的下一階段是「WorkingPaper 直接 SAX 填範本」。
