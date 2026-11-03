# Step 4-1 集合式 prepared-session 效能證據

這份是 2026-11-01 壓縮後的摘要。使用者原話、題目與回答、日期、數字與裁定照原文保留；實作經過、驗證收據與重跑過程壓成結果。
壓縮前的全文在提交 `85af454`，可用 `git show 85af454:docs/history/specs/2026-07-27-step4-1-prepared-performance.md` 取回。

日期：2026-07-27。狀態：已落地並通過自動驗證；不宣稱 Microsoft Excel、GUI、發布版 Release 或企業環境人工驗收。

這份凍結的是 2026-07-27 prepared-session 的效能與當時的顯示寬度 fingerprint。2026-07-28 人工驗收要求的 CJK 安全邊距與全 WorkingPaper 動態資料欄量寬，
會合理改變欄寬標準，但不改這份的 single-reader 效能結論；現行顯示寬度見 `2026-07-28-acceptance-repair.md`。

## 結論

WorkingPaper 正式匯出的 step4-1 路徑，由兩遍、每遍 5,243 頁的 typed paging，改成不分資料庫的 prepared export session。
固定種子 `20260727`、1,048,571 列的 DuckDB Release writer 測試資料，同機三次滿頁候選執行的中位數為 `40,789.2543 ms`；
凍結的 Legacy writer 基準中位數為 `2,114,888.8913 ms`，`baseline / candidate = 51.84916781624026`，高於 `5.0` 的硬門檻。

計時不含建立測試資料、handler 規劃、篩選落地或成品發布；這是固定 DuckDB writer 測試資料的硬門檻，不是 SQLite 或 SQL Server 的服務水準承諾。
候選三次的 normalized logical fingerprint 都是：

`D59613C88E6863AF0DDD65363D3D7C9B49C9A697AFCB05422F131D09B8FF1018`

三次輸出都精確為 1,048,571 data rows、rows 6–1,048,576、單一 step4-1 worksheet、dimension `A1:P1048576`、124,056,313 bytes。
沒有改公開 action、payload、response、event、artifact manifest schema、資料庫 schema、可見欄位、值、原生儲存格型別、數字格式、欄寬、BestFit、tag、排序、樣式或審計語意。

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

## 正式路徑的結構

- 每個正常完成的 step4-1 session 在一條專用連線與一個交易內執行：一次 schema 版本檢查（不做遷移或 `EnsureCreated`）、建立暫存集合、一次落地命中傳票、一次落地 row tag，最後以 `SequentialAccess` 與 `SingleResult` 開一個依傳票號碼、line item 排序鍵與 `entry_id` 排序的單向 reader。
- SQLite 與 SQL Server 用停用 pooling 的複製連線，DuckDB 用獨立連線。SQLite 與 DuckDB 用交易快照；SQL Server 用 `Serializable`，沿用產品的獨占 action 邊界，不宣稱 SQL Server MVCC snapshot。
- 完整資料欄寬不是抽樣：每列先投影成型別確定的列，更新欄寬彙總並寫入 DeleteOnClose 磁碟暫存；資料庫 reader、交易與連線在寫出 SAX 前已關閉。這不是額外的 SQL 命令，也不把百萬列載入 Application 或前端記憶體。
- 成功時在 reader 讀完後於交易內清除暫存物件再 commit；取消、例外或提早停止時先 rollback，再以 5 秒命令逾時、10 秒取消期限清理。
- 每次 session 的計量都是：1 次 schema 檢查、1 條連線、1 個交易、1 次暫存表初始化、1 次命中傳票落地、1 次 row tag 落地、1 個排序 reader、1 次清理、1 次欄寬彙總、0 次 typed page call。SQL 命令固定 6 個：`schemaReadiness`、`initializeTemporaryTables`、`materializeHitVouchers`、`materializeRowTags`、`orderedRows`、`cleanup`，數量不隨 Excel 頁數增加。這些計量只描述 step4-1 區段；公開的相容 writer 與 tag-matrix 查詢仍保留分頁。

環境是 Windows `10.0.26200` x64、.NET `10.0.10` x64、16 logical processors、server GC false、可用記憶體 33,946,124,288 bytes；資料庫、輸出與暫存都在 C:。
三次的 `PeakWorkingSet64` 分別記錄 3,436,204,032／5,614,219,264／7,032,655,872 bytes。這是同一測試程序從建立資料到讀回檢查期間累積的最高值，
不是每次執行各自的峰值，只作環境證據，不用來比較三次執行，也不據此宣稱固定記憶體上限或沒有洩漏。

## 驗證結果

- 候選證據在 `src/JET/tests/JET.Tests/TestResults/step4-1-baseline/step41-prepared-candidate-20260727-103506928-49cce36732734f0ba692f3392a931cd3/`，含 evidence JSON、`step41-baseline-v1/jet.duckdb`（173,027,328 bytes）與三份 `workingpaper-prepared-run-*.xlsx`；各檔 SHA-256 見壓縮前全文。效能基準測試 1 項通過。
- 正確性檢查 89 項通過、0 失敗、2 項略過；同一程序以本機 SQL Server 2022 Developer 實際比對 SQLite、DuckDB 與 SQL Server 的正確性。兩項略過只有需要明示基準資料夾的完整候選測試，以及凍結的歷史基準。
- 生命週期與架構檢查 16 項通過；落地過程中取消的測試 1 項通過。
- 以每頁 25 列的 Excel 上限產生多張 step4-1 時，仍精確只有 1 條連線、1 個交易、1 個排序 reader、6 個 SQL 命令、0 次 typed page call。
- SQLite 在落地命中傳票的 SQL 執行中觸發取消，觀察到 `OperationCanceledException`、`Pooling=false` 與已關閉的連線；SQLite 與 DuckDB 的 reader 取消另證明會 rollback、清理與釋放。成品儲存測試涵蓋 prepared 之後、串流、暫存與 step4-1 寫出，以及真實 handler 的 `export.progress` `finalizingWorkbook`，都保留舊成品、manifest 與目錄身分，不留下暫存、journal 或半套 xlsx。SQL Server 有正常形狀、清理與正確性一致的檢查；本階段不宣稱注入 SQL Server 準備途中取消。
- Release 建置 0 warning、0 error。
- 完整套件 2438 項通過、0 失敗、14 項略過，共 2452；本機 SQL Server 2022 Developer 測試實際執行，沒有因 SQL Server 不可用而略過。14 項具名略過是：7 項主機無法建立真實 filesystem link、4 項未提供私有 `JET_PBC_DIR`、1 項未提供明示 `JET_STEP41_BASELINE_ROOT` 的完整候選測試（已另外執行）、1 項凍結歷史基準、1 項缺真 Express 或 LocalDB 引擎的淘汰守衛。

## 本階段未執行

沒有執行 AgentGuiTest `All`、六本工作簿完整 render、FolderProfile 或發布版 Release、候選外 verifier、Microsoft Excel、企業 SQL Server、CFA、DLP、私有 PBC 或千萬列等級的真實案件。
三種資料庫的一致性是小型與 205 列的正確性檢查，不是三種資料庫的百萬列效能比較。理由是 step4-1 的公開介面、schema 與可見輸出不變，
normalized fingerprint 已精確涵蓋值、原生型別、數字格式、欄寬與 BestFit；整合與人工項目屬下一個或最後階段。
沒有開始「六報表與 GUI 自動化整合封板」，也沒有 commit、push 或 reset。
