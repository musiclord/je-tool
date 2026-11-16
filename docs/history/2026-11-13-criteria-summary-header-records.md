# 2026-11-13 條件篩選報告摘要頁的第 4 列欄名

這份紀錄保存使用者 2026-11-13 的原話、查到的原因與這次的修改。現行規則寫在 [`../jet-guide.md`](../jet-guide.md)
第 7 節的報告表格；裁定編號見 [`../rulings.md`](../rulings.md) 的 2026-11-13-03。

## 使用者原話

使用者 2026-11-13 的問題，逐字：

> XXX_CriteriaSelectionReport 的標題列是不是消失了? 目前看好像只有出現 "條件的內容"

Agent 回報原因後問「要把三個欄名改回和舊版一樣保留嗎？」，使用者 2026-11-13 回覆：

> 改回和舊版一樣

## 查到的原因

內嵌範本 `CriteriaSelectionReport.xlsx` 的 `Summary Inforamtion` 工作表第 4 列有四個欄名：A4「編號」、B4「條件的內容」、
C4「符合條件的傳票數量」、D4「符合條件的分錄筆數」。它和 `data/CriteriaSelectionReport.xlsx` 是同一個檔案。

JET 匯出時清掉 A4、C4、D4 的文字，只留 B4；格子的深藍底格式還在，所以看起來是一排空的標題格。
這段程式從儲存庫第一個提交（`b9db4db`，2026-08-30）起就在，兩個測試也要求這三格是空的。
已被取代的舊版規格 `history/superseded/jet-guide-2026-08.md` 寫了「row 4 只有 B4 的 `條件的內容`」，沒有寫原因；
裁定總表裡沒有任何一則要求清掉這三格。

舊版 IDEA JET 的 `legacy/idea-tool.bas` 第 10191 到 10218 行只寫 C1、C2、D1、D2，再從第 5 列寫各情境，不碰第 4 列，
所以舊版的報告四個欄名都在。

## 這次改了什麼

- 匯出不再清除 A4、C4、D4，第 4 列的四個欄名照範本保留。
- `ReportExportTests.ExportCriteriaSelectionReport_CurrentRevision_WritesSummaryAndOriginalFieldDetail` 與
  `PrescreenDirectTemplatePackageTests.Criteria_MultipleScenariosExpandWholeVouchersAndRepeatHeadersOnContinuation`
  改成核對四個欄名，修改原因與第一次失敗記在 [`test-change-records.md`](test-change-records.md)。

## 交接前複審後一併修正

第 4 列的欄名從此完全來自範本。複審時查到，讀範本儲存格的程式在範本缺了那一格時，丟出的是一般的程式錯誤，
匯出不會顯示「無法填入 JET 內附的範本」。這是原本就有的行為，條件篩選報告的 B4 與預篩選報告的 C8、C11 都走同一段程式。

- `LegacyReportWriter.ReadTemplateCellText` 改成：範本缺了要讀的儲存格，或儲存格指到共用字串表裡沒有的項目時，
  丟出範本內容有問題的例外，匯出因此回報無法填入內附的範本。正常範本的輸出不變。
- 新增 `ReportTemplateCellTextTests` 核對這兩種情況與正常讀值。先確認兩則測試因為例外型別不對而失敗，再改程式。
- 這一項是 Agent 在複審後加的，不在使用者回答過的題目裡。
