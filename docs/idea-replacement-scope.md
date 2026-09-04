# CaseWare IDEA JET 替代範圍

更新日期：2026-08-30  
狀態：現行能力已整理；完整責任清單仍待使用者確認

這份文件用來判斷「目前 JET 是否已能負責原本 CaseWare IDEA 的 JET 工作」。它不重新定義欄位、規則或
報告內容，只把現行能力、證據入口與尚未確認的責任放在同一張表中。

早期筆記與已清洗的 legacy 歸檔可確認最初主線至少包含：匯入 CSV／TXT／XLSX、因應不同來源欄名進行
欄位配對、GL／TB 完整性與有效母體判定、Account Mapping、組合簡單條件，以及輸出高風險結果與底稿。
這些內容用來補足責任輪廓；精確欄位、條件與報表仍以現行契約、程式、測試及使用者驗收為準。

## 已知責任與目前落點

| IDEA 工作責任 | 目前 JET 落點 | 驗證入口 | 尚待確認 |
|:---|:---|:---|:---|
| 建立本機案件並保存設定 | WinForms／WebView2 六步驟流程；SQLite 與 DuckDB 本機儲存 | `Public`、`Gui` | 公司正式環境允許的安裝與本機資料庫條件 |
| 匯入 CSV／TXT／XLSX 的 GL／TB 並做欄位配對 | `Application`、`AuditCore`、`Domain` 與 provider 實作 | `Public`；SQL Server 實跑另用 `Provider` | IDEA 原流程是否還有未列入的來源格式 |
| 資料驗證與有效母體判定 | 現行驗證、INF 與失效規則 | `Public`、明示授權的 `PrivateCase` | 使用者認定的完整 IDEA 驗收案例 |
| 預篩選與多條件篩選 | 後端規則及集合式 SQL，不由前端計算 | `Public`、明示執行的 `Provider`／`PrivateCase` | IDEA 原本全部必要條件清單 |
| 產生審計底稿 | 五份報告、一份科目配對工作檔與原生 Excel 檢查 | `Package`、`Excel`、`PrivateCase` | 各底稿是否還有未列入的人工判讀要求 |
| 不同資料庫得到相同業務結果 | SQLite、DuckDB、SQL Server provider 邊界 | live SQL Server 就緒時明示執行完整 `Provider` | 正式環境是否實際啟用 SQL Server |
| 大量資料不進入前端或 Application 全量記憶體 | 資料庫集合式處理、摘要與分頁 | 架構測試、`Public` | 目標資料量及公司硬體基準 |

精確流程見 [`jet-guide.md`](jet-guide.md)，驗證路線和私人案件邊界見 [`harness.md`](harness.md)。

## 完成條件

IDEA 替代範圍只有在下列條件都成立時才能標為完成：

1. 使用者確認原本 IDEA JET 的必要工作、條件與底稿清單；每項都能在上表找到現行實作或明確的不做決定。
2. 每項現行實作都有對應的公開測試、需要時另行執行的 provider 比較、GUI、Excel 或明示授權的私人案件
   驗證；不能用單一 `ReleaseCandidate` 名稱代替未執行的 SQL Server 或私人案件驗收。
3. 本機作業在公司允許的最低權限與網路條件下通過部署及操作驗收，不需要 AI 網路服務。
4. 尚未完成的 KCT 新條件與 SQL Server 中心化工作明確留在後續範圍，不混入 IDEA 完成宣告。
5. 文件、程式、測試與本次有效收據沒有互相矛盾；私人內容沒有進入 Git 或可同步證據。

## 目前不能自行補完的內容

- CaseWare IDEA JET 的完整功能、條件、報表與人工驗收清單。
- KCT 的正式全稱、方法來源，以及尚未提供的條件分類。
- 公司正式環境最低 Windows／Office、安裝權限、本機資料庫與檔案傳遞條件。

在上述資料補齊前，可以繼續修正已確認的產品缺口，但不能宣稱整個 IDEA 替代範圍已驗收完成。
