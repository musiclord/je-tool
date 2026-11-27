# JET 安全性與檢查佐證

本目錄整理技術文件第 5 節的元件與弱點檢查資料，以及第 10 節的應用程式與儲存庫安全性佐證。安全控制與已知限制分別見技術文件第 10.1 節及第 17 節，各項檢查的日期記在各份紀錄裡。[Technical Document](<../SAT/Technical Document.docx>)

## 元件與查詢紀錄

| 資料 | 範圍與來源 |
|---|---|
| [元件清冊](components.csv) | 從技術文件第 5.1 節的元件表匯出，保留元件名稱、版本、引用方式與授權。清冊涵蓋應用程式的直接與間接相依項目，以及隨附的前端元件。 |
| [安全檢查摘要](security-check.json) | 保留 2026-11-24 的 NuGet 弱點、停止維護套件及前端元件評估結果。JSON 裡的檔案路徑以儲存庫根目錄為起點；`tool` 記的是在 `JET/` 資料夾執行的命令。 |
| [程式碼複核紀錄](<../SAT/Code Review/Code review.xlsx>) | 技術文件第 11 節所列的輸入檢查、例外處理與機密資訊等複核項目。 |
| [GitHub 安全報告](<../SAT/Github Security Reports/>) | 技術文件第 10.1 節與第 10.2 節引用的 GitHub 原始匯出報告，在 GitHub 執行掃描後匯出到這個目錄。 |

NuGet 摘要記錄的應用程式與測試專案均未列出已知弱點。應用程式套件未列出停止維護項目，測試專案間接引用的 `Microsoft.Testing.Extensions.Telemetry 2.5.2` 則標示為停止維護。查詢範圍為專案的 NuGet 套件，發布檔內含的 .NET runtime 未列入該次查詢。[安全檢查摘要](security-check.json)

技術文件第 5.1 節採用表格清冊，並列明清冊的範圍、產生方式與限制。CSV 保留同一份表格的內容，元件版本可與專案檔及套件還原結果核對。[應用程式專案](../../JET/JET/JET.csproj)

## 前端元件的弱點評估

Apache ECharts 5.5.0 落在 CVE-2026-45249 公告的受影響版本範圍。公告描述的影響條件為使用 `lines` series 與 tooltip、未指定 `tooltip.formatter`，且資料項目的 `name` 含有 HTML。[Apache ECharts 安全公告](https://security.apache.org/projects/echarts/)

JET 的圖表使用 `bar`、`line` 與 `scatter`，四個 tooltip 都指定 formatter，文字經 `Ui.esc` 跳脫。依這些使用方式推論，目前未符合該公告的影響條件，2026-11-24 的評估與處置理由已記錄於摘要。[圖表程式](../../JET/JET/wwwroot/js/overview-bi.js)、[安全檢查摘要](security-check.json)
