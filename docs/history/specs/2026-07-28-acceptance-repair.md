# 大案 Excel 與 WorkingPaper 驗收缺陷修補證據

這份是 2026-11-01 壓縮後的摘要。使用者原話、題目與回答、日期、數字與裁定照原文保留；實作經過、驗證收據與重跑過程壓成結果。
壓縮前的全文在提交 `85af454`，可用 `git show 85af454:docs/history/specs/2026-07-28-acceptance-repair.md` 取回。

## 狀態

2026-07-28 已落地並通過自動驗證；本輪結束時仍待使用者以原始的千萬列等級案件與 Microsoft Excel 人工重驗。
後續修補已納入 `FolderProfile-20260731-131350`，並於 2026-07-31 通過人工驗收。這份只記當時工作樹的修補與證據，
不把合成案件、自動 render 或 AgentGuiTest 當成人工驗收。

## 使用者回報與修補範圍

使用者的失敗場已完成千萬列等級的 GL 與數百列的 TB 匯入；GL 匯入與標準化各花了數分鐘，TB 幾乎不花時間，日期維度的假日與補班日也已建立。Validation、Pre-screening 與 Criteria 均完成，情境命中上千列、數百張傳票。WorkingPaper 最後停在 `step3 高風險條件彙總` 完成，當時已寫出數張工作表、數百列，花了數分鐘，等待一小時仍無產出後取消。Excel 另出現資料欄換行、欄寬不足與 `###`；`app_message_log` 則缺少一致的總耗時與最後進度。

本修補只處理這三項：

- **欄寬與換行**：六份報表的動態資料欄依完整格式化資料計算 CJK 寬度，資料格明示不換行、不縮排、不縮小；舊版說明、方法學與表頭刻意的換行保留。WorkingPaper Step1、Step1-1、Step1-2、Step2、Step3、Step4 以 DeleteOnClose 暫存檔只讀一次來源，再寫進範本；Step4 正式路徑仍只開一次資料庫串流。
- **Step4 卡住**：正式 WorkingPaper 的 Step4 改成一條參數化、先取命中、排序好的單向串流；公開的 `query.tagMatrixVoucherPage` 與相容分頁不變。Step4 與 Step4-1 在昂貴查詢前先送出既有形狀的 `rowsWritten=0` 工作表開始進度。
- **訊息紀錄**：指定長作業的成功、取消與失敗訊息加上總耗時，取消與失敗另附最後的可見進度。訊息面板新增「複製紀錄」，等持久化佇列寫完後複製最近 100 筆、依時間排序的 TSV；不把每筆進度寫進 `app_message_log`。

沒有修改 KCT、審計規則、公開 action、payload、response 形狀、資料庫 schema、artifact manifest schema 或固定範本檔。

## 自動驗證結果

- Release 建置 0 warning、0 error。
- 定向測試（晚頁長文字、Step4 串流、Step1-1 舊版樣式）4 項通過；受影響範圍的回歸測試 108 項通過、0 失敗，4 項只能在 SQL Server 執行的測試在沙盒內略過，已由沙盒外的完整套件實際執行。
- 完整套件 2,459 項通過、0 失敗、13 項具名略過，共 2,472；本機 SQL Server 2022 Developer 測試實際執行。13 項略過是：7 項主機無法建立真實 filesystem link、4 項缺私有 `JET_PBC_DIR`、1 項缺明示 `JET_STEP41_BASELINE_ROOT`、1 項凍結歷史基準。
- 六份報表的流程測試 1 項通過；Agent GUI `All` 15 個情境全部通過，289 個操作、0 張截圖，結束後 `Status=inactive`。
- 以和失敗場同樣張數的傳票做的正式 writer 回歸測試，要求公開分頁呼叫 0 次、串流呼叫 1 次，Step4 輸出列數等於傳票張數。晚頁長文字測試把長文字放在資料尾端，驗證只讀一次來源後的完整欄寬與不換行。最後的獨立複審抓出 Step1-1 第 14 列是舊版欄標、資料從第 15 列開始；修正後 B14:E14 與來源範本樣式相同，B15:E15 為單行資料樣式。

## 六份報表與視覺檢查

同一次正式流程產生 6 本工作簿、32 張工作表。每張完整使用範圍都做了區域與樣式檢查並 render，32 張 PNG 都以原始解析度人工讀回：

- 可見儲存格的標準錯誤字串與 `###`：0。
- 樣式紀錄 977 筆；非零縮排 0；`shrinkToFit=true` 0。
- `wrapText=true` 227 筆，都是保留的舊版說明、方法學或表頭。
- WorkingPaper 動態資料範圍樣式紀錄 63 筆，換行、縮排、縮小違規 0。
- 32 張 render 都沒有重要裁切、非預期重疊、缺圖或 hash 佔位。

AccountMapping 來源範本與輸出各有 83 個 workbook 層級的 `=#REF!` defined name，來源範本 SHA-256 是 `20A592907609933D837C8A617FABD11BD046C7D271AE55522545AC4CB61444AB`。
這是逐位元保留的舊版定義，不是這次新增的可見錯誤；因此只宣稱可見儲存格與顯示檢查為 0，不宣稱整個檔案沒有舊版 `#REF!` 定義文字。
各證據檔的路徑與 SHA-256 見壓縮前全文。

## FolderProfile 候選

- 候選 `artifacts/convergence/FolderProfile-20260728-145315`，由 `RawPublish-20260728-145315` 產生，未由 agent 啟動。
- 檔案 26 個，共 74,302,643 bytes；reparse point 0；兩者逐檔 hash 差異 0。
- `JET.exe` 73,162,575 bytes，SHA-256 `F30C9A2D303190F8D8FD19703CBFD9C89DA22669FD6F64685A8CEFC8032F588B`。
- 候選外的檢查通過來源允許清單、精確檔案集合、沒有散落的 DLL、PDB、XML、執行資料或 reparse point，以及 x64 PE32+ .NET 單檔檢查。

`FolderProfile-20260728-0845` 已被使用者的失敗場取代；`FolderProfile-20260728-134350` 因 WorkingPaper 動態資料格仍保留範本換行樣式被取代；
`FolderProfile-20260728-142903` 又因 Step1-1 第 14 列舊版欄標被誤當成動態資料格而被取代。三者只保留為歷史，不得再作人工候選。

## 本輪未執行與停止點（2026-07-28）

本輪沒有 Microsoft Excel 桌面重驗、沒有重新執行原始的千萬列等級案件、沒有企業 SQL Server、CFA、DLP 或私有 PBC，也沒有 commit、push、reset 或啟動候選。
當時的人工重驗只能使用上列候選；修補尚未 commit 與 push，所以跨主機取得版本暫停。

本修補在證據與候選完成後停止，沒有在同輪開始「千萬列等級案件與 Excel 最終驗收」，也沒有處理 KCT 或新增功能。
其後變更已由使用者推送，舊候選也已被 `FolderProfile-20260731-131350` 取代；最終人工結果見 `development-log.md`。
