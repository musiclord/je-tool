# Step 4-1 集合式 prepared-session 效能證據

日期：2026-07-27
狀態：已落地並通過自動驗證；不宣稱 Microsoft Excel、GUI、published Release 或企業環境人工驗收。

> **歷史 oracle 邊界：** 本文件凍結的是 2026-07-27 prepared-session 效能與當時顯示寬度 fingerprint。2026-07-28 人工場要求的 CJK safety margin／全 WorkingPaper 動態資料欄量寬會合理改變欄寬 golden，但不改本文件的 single-reader 效能結論；現行顯示寬度 receipt 見 `docs/specs/evidence/2026-07-28-acceptance-repair.md`。

## 驗收結論

WorkingPaper finalized production 的 step4-1 路徑已由兩遍、每遍 5,243 頁的 typed paging 改成 provider-neutral prepared export session。固定 seed `20260727`、1,048,571 列的 DuckDB Release writer fixture 在同機三次 full-page candidate run 的中位數為 `40,789.2543 ms`；凍結 Legacy writer baseline 中位數為 `2,114,888.8913 ms`，`baseline / candidate = 51.84916781624026`，高於 `5.0` 硬門檻。計時不含 fixture setup、handler planning、filter materialization 或 artifact publication；這是固定 DuckDB writer fixture 的硬門檻，不是 SQLite／SQL Server SLA。candidate 三次都維持 normalized logical fingerprint：

`D59613C88E6863AF0DDD65363D3D7C9B49C9A697AFCB05422F131D09B8FF1018`

三次輸出也都精確為 1,048,571 data rows、rows 6–1,048,576、單一 step4-1 worksheet、dimension `A1:P1048576`、124,056,313 bytes。沒有改 public action、payload、response、event、artifact manifest schema、資料庫 schema、可見欄位、值、native cell type、number format、欄寬、BestFit、tag、排序、樣式或審計語意。

## Production 結構與命令形狀

- AuditCore 提供 internal provider-neutral prepared factory／session／metrics contract；production writer 只消費 finalized Legacy schema 與 session row stream。
- 每個正常完成的 finalized production step4-1 session 在一條 dedicated physical connection 與一個 transaction 內執行一次 schema-version readiness probe、建立暫存集合、一次 materialize 命中傳票、一次 materialize row tags，最後以 `SequentialAccess`／`SingleResult` 開一個依傳票號碼、native line-item ordinal key、`entry_id` 排序的 forward-only reader。這個 readiness probe 不執行 schema migration 或 `EnsureCreated`。
- SQLite 以停用 pooling 的 cloned connection、DuckDB 以獨立 physical connection、SQL Server 以停用 pooling 的 cloned connection 執行。SQLite／DuckDB 使用 transaction snapshot；SQL Server 使用 `Serializable`，並沿用產品 exclusive action 邊界，不宣稱 SQL Server MVCC snapshot。
- 完整資料欄寬不是 sample。ordered reader 的每列先投影成 exact native typed row，一次更新完整欄寬 aggregate 並寫入 DeleteOnClose typed disk spool；資料庫 reader、transaction 與 connection 在 SAX replay 前已關閉。這是一次 in-process exact projection／spool pass，不是額外 SQL command，也不把百萬列載入 Application 或前端記憶體。
- 成功路徑在 reader EOF 後先於 transaction 內清除 temp objects，再 commit；取消、例外或提早停止先 rollback，再以 5 秒 command timeout／10 秒 cancellation deadline bounded cleanup。所有建立與 dispose 路徑都以 nested fallback 嘗試 transaction、reader、connection 與 spool cleanup。
- 每次正常完成 candidate session 的 metrics 都是：1 schema-version readiness probe、1 connection、1 transaction、1 temp-table initialization、1 hit-voucher materialization、1 row-tag materialization、1 ordered-reader、1 cleanup、1 width aggregation、0 typed page calls。SQL command attempts 固定為 6 個：`schemaReadiness`、`initializeTemporaryTables`、`materializeHitVouchers`、`materializeRowTags`、`orderedRows`、`cleanup`；數量不隨 Excel page／continuation 數增加。這些 metrics 只描述 finalized production 的 step4-1 區段，不描述整本 WorkingPaper action；public plan-less compatibility writer 與 public tag-matrix query 仍保留既有 paging。

## 固定 baseline 與 candidate

Frozen baseline 詳見 `docs/specs/evidence/2026-07-27-step4-1-legacy-baseline.md`：

| 項目 | 數值 |
|:---|:---|
| Baseline 三次 elapsed | 2,222,087.6258 / 2,114,888.8913 / 2,066,202.9736 ms |
| Baseline median | 2,114,888.8913 ms |
| Candidate 允許的最大 median | 422,977.77826 ms |
| Candidate setup（排除計時） | 9,715.5454 ms |
| Candidate 三次 elapsed | 40,953.1581 / 40,789.2543 / 40,576.9437 ms |
| Candidate median | 40,789.2543 ms |
| Baseline / candidate | 51.84916781624026x |
| 最低門檻 | 5.0x |
| 結果 | 通過 |

Candidate runner 環境是 Windows `10.0.26200` x64、.NET `10.0.10` x64、16 logical processors、server GC false、可用記憶體 33,946,124,288 bytes；database、output 與 temp 都在 C:。三次 process `PeakWorkingSet64` 分別記錄 3,436,204,032／5,614,219,264／7,032,655,872 bytes。這是同一 test process 自 fixture 建立、輸出到讀回檢查期間的 lifetime 累積 high-water mark，不是每次 run 隔離後的獨立 peak，因此只作環境證據，不作三次 run 間的記憶體比較，也不據此宣稱固定 memory ceiling 或無 leak。

## Raw evidence 與 SHA-256

Candidate root：

`src/JET/tests/JET.Tests/TestResults/step4-1-baseline/step41-prepared-candidate-20260727-103506928-49cce36732734f0ba692f3392a931cd3/`

| 檔案 | Bytes | SHA-256 |
|:---|---:|:---|
| `prepared-candidate-evidence.json` | 8,071 | `11227365D278967346C57E364203642B4C45546C5A5FDE077943E8C9158581B7` |
| `step41-baseline-v1/jet.duckdb` | 173,027,328 | `AA595A91BAAD6DC1C942558A8C6E0C375FA05D8272B54A8480A30D00CCBAAA81` |
| `workingpaper-prepared-run-1.xlsx` | 124,056,313 | `9195D3CFEE199A0460526F8DEF672E61F946FDA33E9629D7DDA6D6FCDEA24492` |
| `workingpaper-prepared-run-2.xlsx` | 124,056,313 | `30360F895A3CF602189666B122B59D3C4950E77FAC34256AD1703F237BCE3765` |
| `workingpaper-prepared-run-3.xlsx` | 124,056,313 | `C11F732EC60C2EA40BFC37AD6D4340455FFF0C939D1BC45396BA89E97AB2F121` |

Benchmark TRX：

- `src/JET/tests/JET.Tests/TestResults/20260727-step41-prepared-full-candidate-final-current-tree.trx`：1 pass／0 fail／0 skip；SHA-256 `9B529B2848C61213F25DB4C1112BB149F9D636EC8910051A3188E0C275221BAB`。

## Correctness、provider 與取消證據

- Final hard gates：`20260727-step41-final-hard-gates-current-tree.trx`，89 pass／0 fail／2 skip；SHA-256 `927962AD3703B294114A4A2BD682BFB54D2CF1CA931AA7BBEF2A4D5CB6BDF72B`。同一 process 以本機 SQL Server 2022 Developer 實跑 SQLite／DuckDB／SQL Server correctness parity；兩個 skip 只有需明示 benchmark root 的完整 candidate 與 frozen historical baseline。
- Lifecycle／architecture：`20260727-step41-lifecycle-architecture-current-tree.trx`，16 pass／0 fail／0 skip；SHA-256 `DD1A2B756110D48FA8A91BCC81CE68C866F9EAA843BD3D154CB4006EE6EC35B7`。
- Materialization 內取消：`20260727-step41-materialization-cancel-current-tree.trx`，1 pass／0 fail／0 skip；SHA-256 `A95D22E0ACFF2E34D5013ABD703FC6CF083C92CC64066FD4BA5E0ADA1D6CCF88`。
- Continuation test 以 25-row Excel page limit 產生多張 step4-1，仍精確只有 1 connection、1 transaction、1 ordered reader、6 SQL command attempts、0 typed page calls，證明命令數不隨 worksheet page 數成長。
- SQLite deterministic UDF 在 hit-voucher materialization SQL 執行中觸發取消，精確觀察 `OperationCanceledException`、`Pooling=false` 與 closed connection；SQLite／DuckDB ordered-reader cancellation 另證明 rollback／cleanup／dispose。Artifact-store tests 分別覆蓋 prepared 後邊界、streaming、spool／step4-1 emission 與真實 handler `export.progress` 的 `finalizingWorkbook`，都保留舊 artifact、manifest、catalog identity／source refs，不留下 temp、journal 或半套 xlsx。SQL Server 有正常 constant-shape／cleanup 與 production correctness parity gate；本階段不宣稱注入 SQL Server mid-prepare cancellation。

## Build、完整套件與具名略過

- `dotnet build src/JET/JET.slnx -c Release --no-restore --nologo`：0 warning／0 error。
- `src/JET/tests/JET.Tests/TestResults/20260727-step41-full-suite-final-current-tree-with-sql.trx`：2438 pass／0 fail／14 skip，共 2452；SHA-256 `4521C4603A81F2665522254E1BD313397BB8850CE17492DC0F680CC46E52C4BD`。本機 SQL Server 2022 Developer tests 實際執行，沒有 SQL Server availability skip。
- 14 個具名 skip：7 個目前主機無法建立真實 filesystem link；4 個未提供私有 `JET_PBC_DIR`；1 個未提供明示 `JET_STEP41_BASELINE_ROOT` 的完整 candidate gate（已由上列獨立 benchmark TRX 執行）；1 個 frozen historical baseline（只讀封存 evidence，不以 current writer 重跑）；1 個缺真 Express／LocalDB 引擎的淘汰守衛。沒有 final failure 或無法歸因的 skip。

## 本階段未執行

本階段沒有執行 AgentGuiTest `All`、六工作簿完整 render、FolderProfile／published Release、候選外 verifier、Microsoft Excel、企業 SQL Server／CFA／DLP、私有 PBC 或真實 20,260,435 列案件。三 provider parity 是小型／205-row correctness，不是三 provider 百萬列 benchmark。原因是 step4-1 public／wire／schema／可見輸出不變，normalized fingerprint 已精確覆蓋值、native cell kind、number format、欄寬與 BestFit；上述整合及人工項目屬下一個或最終階段。沒有開始「六報表與 GUI 自動化整合封板」，也沒有 commit、push 或 reset。
