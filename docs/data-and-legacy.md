# `data/`、程式隨附範本與 `legacy/`

更新日期：2026-09-21

## `data/` 是檔名權威

`data/` 目前有十份參考工作簿，名稱與大小寫如下：

- `AccountMapping.xlsx`
- `CriteriaSelectionReport.xlsx`
- `Holiday2024CN.xlsx`
- `Holiday2025TW.xlsx`
- `INFReport.xlsx`
- `MakeupDay2024CN.xlsx`
- `MakeUpDay2025TW.xlsx`
- `PrescreeningReport.xlsx`
- `ValidationReport.xlsx`
- `WorkingPaper.xlsx`

這十份是使用者裁定保留的 JET 業務範例。內容逐位沿用 `new-je-tool/data/` 的對應來源，本次只更正檔名；
不清除 Office 文件屬性、外部關聯或嵌入物，也不以匯入／匯出方式重存。業務邏輯有歧義時，應回查
[`business-logic-provenance.md`](business-logic-provenance.md) 指定的儲存庫內證據，不從檔案封裝痕跡
自行推導新規則，也不要求來源專案繼續存在。

本輪以唯讀方式檢查工作簿結構、工作表清單與封裝中繼資料，確認其角色涵蓋科目配對、日期參考、
驗證／篩選／抽樣報告與工作底稿；十份檔案的 SHA-256 也逐一等於 `new-je-tool/data/` 對應來源。
`JE.xlsx` 與 `TB.xlsx` 沒有進入 `je-tool/data/`，因為它們是來源資料角色，不屬於這十份範例模板。

這項裁定只適用於上述十份頂層範例，不放寬真實案件、客戶資料或私人資料根目錄的限制。

2026-09-18 使用者另提供 `XXX_2024_JE篩選條件_1210.xlsm` 供規則查核。它不是上述十份版控範例或程式範本；
原件保留本機並由精確忽略規則排除，未改寫或刪除。現行計畫保留來源版本、雜湊、儲存格對照及已裁定差異，
程式條件目錄與合成固定答案納入 Git。建立、測試及發布 JET 不依賴此原件；文件中的來源連結只供持有原件者查閱。

## 程式隨附範本

程式發布的八份模板位於 `src/JET/JET/Templates/`：

| `data/` 名稱 | 程式隨附狀態 |
|:---|:---|
| `AccountMapping.xlsx` | 同名打包 |
| `CriteriaSelectionReport.xlsx` | 同名打包 |
| `Holiday2025TW.xlsx` | 同名打包 |
| `MakeUpDay2025TW.xlsx` | 同名打包 |
| `INFReport.xlsx` | 同名打包；程式隨附內容不以 `data/` 版本覆蓋 |
| `PrescreeningReport.xlsx` | 同名打包 |
| `ValidationReport.xlsx` | 同名打包 |
| `WorkingPaper.xlsx` | 同名打包；程式隨附內容不以 `data/` 版本覆蓋 |
| `Holiday2024CN.xlsx` | 只保留在 `data/` |
| `MakeupDay2024CN.xlsx` | 只保留在 `data/` |

這次只統一名稱，沒有改寫 `data/` 工作簿內容，也沒有替 2024 CN 檔案臆造程式用途。新產生的 INF 與
預篩選報告同樣採 `INFReport`、`PrescreeningReport` 後綴；既有案件設定仍可讀取先前保存的
相對檔名，不會批次改動使用者資料夾。

`INFReport.xlsx` 與 `WorkingPaper.xlsx` 的程式隨附版本曾在來源專案另做安全清理，因此刻意不與
`data/` 中的參考檔逐位相同。兩者用途不同，不是遷移漏檔。

## `legacy/`

`legacy/` 保存 IDEA、VBA、早期 .NET、流程圖與輔助工具。其文字來源已依目前權威檔名正規化，
目前整理後的結構與重新命名檔案就是最終保留版本；不再補回 `JE_Tool.ism`、重複的 VBA 匯出檔、舊路徑
副本或來源專案的逐位原文。詳細索引見
[`../legacy/README.md`](../legacy/README.md)。

真實案件與由真實案件產生的輸出不屬於 `data/` 或 `legacy/`。它們只能留在儲存庫外的私人資料根目錄，
且沒有當次明確授權時不得讀取或掃描。

舊專案的 `TestResults`、`StrykerOutput`、執行產物、`bin`／`obj`、執行階段的 `projects` 與私人資料根目錄，
都不屬於 `data/`、程式隨附範本或 `legacy/`，也不是新驗證框架的輸入、判定基準或執行紀錄。它們可以暫留
舊的本機工作目錄；若日後要刪除該目錄，應先由使用者另行確認是否仍有需保留的私人或專案資料，
但不得為了關庫把這些內容加入 Git 或搬進 `je-tool`。
