# step4-1 Legacy 語意與可重現基準證據索引

日期：2026-07-27
狀態：已落地並通過自動驗證

> **歷史 oracle 邊界：** 本文件保存的是 2026-07-27 Legacy slow-path 與當時欄寬 fingerprint，不是 2026-07-28 顯示寬度修補後的 current golden。現行 no-wrap／CJK safety margin 與 provider parity receipt 見 `docs/specs/evidence/2026-07-28-acceptance-repair.md`。

本索引只保存 step4-1 Legacy 效能基準的可重現設定、結果摘要、raw evidence 路徑與 SHA-256。大型 workbook、DuckDB 與 TRX 留在 ignored `TestResults`，不搬進文件樹；現行行為仍以 `docs/jet-guide.md` 與 `docs/action-contract-manifest.md` 為權威。

## 正確性 oracle

- Legacy 小資料 oracle `step4-1-legacy-v1` 的 SQLite、DuckDB、SQL Server normalized logical fingerprint 都是 `67B9CA38FA17BD971FE13F00B5958450B59CF16848E026B8269D62B333FABE0A`。它鎖住 12 個優先欄、其餘 `_JE`／`_JE_S` ordinal 欄、row-hit tag、null／文字／數字 line item、`entry_id` tie-break、signed amount、Date／Time／Boolean／`1E+100` native cell kind、完整資料欄寬與 `voucherDate` 排除。
- Numeric cursor oracle `step4-1-numeric-line-cursor-v2` 的三 provider fingerprint 都是 `3DDFD652FD0C670005E7A6B88CAD710BA9C913BC76E0F151555ECFEE851E779D`。205 列把 `1E-20`／`2E-20` 放在預設 200-row page boundary 兩側，並含 `1E+100`–`4E+100`；它鎖住持久化 numeric sort key、跨頁接續與 Number／scale 20 的 native 輸出。
- SQLite／DuckDB／SQL Server schema 現行版為 6。v5→v6 只 additive 新增 nullable `line_item_numeric_sort_key`；舊列不從顯示字串猜測回填。非空 Number line item 若缺 key，step4-1 以 `stale_result` fail closed，要求重新匯入、配對與投影。

## 固定滿頁 baseline

- Runner：Release、DuckDB、fixed seed `20260727`。
- Fixture：恰好 1,048,571 筆資料，setup 10,159.1909 ms 且排除於 export timing；DuckDB 與三份輸出都在 `C:\` NTFS，同一個實體 drive。
- Excel 範圍：資料精確佔 rows 6–1,048,576；step4-1 恰好一張，dimension `A1:P1048576`，沒有遺失或重複。
- 刻意保留的 legacy 路徑：page size 200、每次 export 兩遍各 5,243 頁、10,486 typed page calls、20,972 reader data commands、第二 workbook merge；`preparedSet=false`、`singleReader=false`、`directTemplateSax=false`。
- 三次 elapsed：2,222,087.6258 ms、2,114,888.8913 ms、2,066,202.9736 ms。
- 中位數：2,114,888.8913 ms（35 分 14.889 秒）。
- 三次輸出大小皆為 124,055,972 bytes；normalized logical fingerprint 皆為 `D59613C88E6863AF0DDD65363D3D7C9B49C9A697AFCB05422F131D09B8FF1018`。Package hash 因 volatile package metadata 合理不同，normalized fingerprint 明示納入 values、native cell kinds、number formats、column widths 與 BestFit。
- 欄寬摘要：A:P 全部 `BestFit=true`；`LATE_WIDE_JE` 由末段完整資料推到 Excel 上限 255，`C1_TAG`／`C10_TAG` 分別為 8／9，其餘 exact widths 保存於 JSON。
- 環境：Microsoft Windows `10.0.26200` x64、.NET `10.0.10` x64、16 logical processors、AMD64 Family 25 Model 97；Server GC false，總可用記憶體 33,946,124,288 bytes，測試記錄的 peak working set 5,346,033,664 bytes。

## Raw evidence 與 SHA-256

Baseline root：

`src/JET/tests/JET.Tests/TestResults/step4-1-baseline/step41-legacy-baseline-20260727-042134917-0f1ece0c0ea542b5be4323355d9db72e/`

| Evidence | Bytes | SHA-256 |
|:---|---:|:---|
| `baseline-evidence.json` | 8,177 | `F543AFFD5ECAE62DBF3C8A66DE146CE57160CA8C348746D9DAB763DD7E3F4FF6` |
| `workingpaper-run-1.xlsx` | 124,055,972 | `53B06E08FEF1B22F1BF21EE490A043506334FF8883DF585AA49ECA242C5B38A4` |
| `workingpaper-run-2.xlsx` | 124,055,972 | `57903D38484ADA42A96BC8D5AE83C133138EC5430F43123EA362E4F13EBFA2BF` |
| `workingpaper-run-3.xlsx` | 124,055,972 | `881F75439575BF223F3044EA28FBCE9714F0F3A617207CCF2C1409141D341031` |
| `step41-baseline-v1/jet.duckdb` | 173,289,472 | `590AE6C39B745015A82978443EA808863467237AC9ED4C3842860F463F899276` |

Provider evidence root：`src/JET/tests/JET.Tests/TestResults/step4-1-provider-parity/`

| Evidence | SHA-256 |
|:---|:---|
| `provider-parity-local.json` | `6D56BAEE426EC77F5761FB8C6F6AD565C631ADB1331C9B734D7AD4CBA5D24839` |
| `provider-parity-sqlserver.json` | `28A9FEE2B92A30624D261A35703909107F55698280858EA12643EFD34B9151B1` |
| `provider-numeric-cursor-local.json` | `03B7A440230A396EE1AE5CEEDE26566145D911B22F7365279B0BC1FC1C2438E8` |
| `provider-numeric-cursor-sqlserver.json` | `5F5221B908E2E6B16483698F661A507E1790129EC06CE7C7699122C38744B663` |

Final TRX root：`src/JET/tests/JET.Tests/TestResults/step4-1-final/`

| Evidence | Result | SHA-256 |
|:---|:---|:---|
| `20260727-step41-numeric-final-local-green.trx` | 61 pass／0 fail／4 SQL-only skip | `7F9F9B24F53DC37F08576295C96DB2AA939C206A4BD3DFD11F78C39312355110` |
| `20260727-step41-provider-sqlserver-final-green.trx` | 7 pass／0 fail／0 skip | `D1EA642BEFAE1978A65918BE168434F1794754853C69433BDBB65996AF1E03AC` |
| `20260727-step41-full-baseline-final.trx` | 1 pass／0 fail／0 skip；1 h 47 m | `1399A662B45E96EB0E21ACD5C5988C93B5B6DD4BF855E26974EEF436FD7EB35B` |

Artifact-tool verification root：

`<user-profile>\.codex\visualizations\2026\07\27\019fa110-69ef-74e3-adf5-4f0c9ad69472\step4-1-workbook-verification\`

- `artifact-tool-verification.json`：SHA-256 `C4228AACF92F42A9D113B306EDA3D1D57F9343038DB47EA0C9CA4DC1CDC595E1`；14 張 worksheet 可讀，公式錯誤搜尋 0 命中。
- `step4-1 符合高風險條件傳票明細.png`：SHA-256 `79D1A8AEEDAD018DFB7BFD1A283330E9812C4777B0A1903A8E809959BC77A7A4`；最終 SQLite 小資料 workbook 的 Step4-1 已目視 read-back，沒有重要裁切或重疊。

## 執行結果與邊界

- `dotnet build src/JET/JET.slnx --no-restore --nologo -c Release`：0 warning／0 error。
- 本機定向輪的 4 個 skip 全是 `[SqlServerFact]`；同一 current-tree 另以可用的 SQL Server 2022 Developer 隔離庫執行，7／7 通過、0 skip，已關閉 provider parity 與 v5→v6 migration 的環境缺口。
- 診斷期 `20260727-step41-numeric-final-local.trx` 曾有 2 個預期修正前失敗；修正後的 `...local-green.trx` 取代它。較早兩個 `step41-legacy-baseline-20260727-021732430-*`、`step41-legacy-baseline-20260727-031759960-*` 目錄是不完整嘗試，不是 baseline evidence，也不納入中位數。
- 本階段沒有跑 full suite、AgentGuiTest `All`、FolderProfile／published Release、Microsoft Excel、企業 CFA／DLP、私有 PBC 或真實 20,260,435 列案件；不更新 `docs/development-status.md` 檔首的完整套件基線，也不宣稱人工驗收。
- 本階段沒有實作 prepared set、single reader、WorkingPaper 直接 SAX 或五倍效能 gate。唯一可開始的下一階段是「WorkingPaper 直接 SAX 填範本」。
