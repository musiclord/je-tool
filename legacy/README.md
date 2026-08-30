# Legacy — JET 歷史實作索引

`legacy/` 保存舊系統的規則與架構對照，不參與現行 .NET 產品建置。文字來源已依 `data/` 的權威檔名
正規化，並去除私人使用者路徑、組織信箱與硬編碼秘密。目前整理後的檔名與結構就是最終保留版本；
不保留 `JE_Tool.ism`、重複 VBA exports、舊路徑副本、來源 Git history 或逐位 raw originals，也不把
來源 repo 的存在列為後續查核前提。

## 目前結構

| 路徑 | 內容 | 使用方式 |
|:---|:---|:---|
| `idea-tool.bas` | IDEA 時期正式工具的 UTF-8 文字來源 | 規則有歧義時作窄範圍對照 |
| `idea-script.bas` | 較完整的 IDEAScript 歷史來源 | 查舊流程與邊界，不逐段翻譯 |
| `VBA-legacy/` | 兩套整理後的 VBA source、reference workbooks 與說明 | 對照 UI、mapping、service 與 Access 演進 |
| `JET-legacy/` | 早期 .NET prototype | 只看架構演進，不作現行 implementation source |
| `drawio/` | 舊流程圖原始檔 | 需要流程背景時開啟 |
| `SqlBuilder.xlsm` | SQL／Excel 輔助工具 | 歷史參考，不執行巨集 |
| `jet-legacy-notes.md` | 舊審計方法與資料管道摘要 | 先於大檔原始碼閱讀 |

三份 legacy Office 容器已做唯讀 package 掃描，未發現本輪被取代的四個檔名字串，因此沒有改寫其
二進位內容或執行巨集。

`VBA-legacy/ServiceExport.cls` 已在嚴格 CP950 解碼與逐位 round-trip 成功後轉為無 BOM UTF-8；轉碼前後
文字完全相同，另將唯一硬編碼秘密改為 `vbNullString`。這項變更只服務可追蹤性與安全，不代表舊 VBA
仍是可執行驗收介面。

## 使用原則

- 先查 [`../docs/jet-guide.md`](../docs/jet-guide.md)、現行 contract 與 `src/`；只有明確歧義才回查 legacy。
- legacy 只能提供歷史證據，不能自行覆蓋新的安全、資料模型或使用者裁定。
- 不把 Access SQL、IDEA API 或 VBA Presenter 分層原樣搬進現行 .NET 架構。
- 不執行 `.xlsm` 巨集、不連線舊資料庫，也不把歷史輸出放回 source tree。
- 引用時標示來源檔與用途，不把舊結果寫成目前通過狀態。

## 為什麼棄用舊技術棧

- IDEA／IDEAScript 帶有訂閱成本與專有執行環境依賴，不適合作為可攜、可自動驗證的產品核心。
- Access 有檔案大小、併發與 SQL 方言限制；現行架構改由 SQLite、DuckDB 與可替換的 SQL Server provider 承擔集合式運算。
- VBA 難以建立穩定的型別、測試、封裝與資安邊界，因此只保留業務語意、欄位映射及輸出方法，不保留舊執行棧。
- 這些限制是技術遷移理由，不代表舊審計方法失效；有爭議時仍應回查 legacy 證據，再由現行 contract 裁決。

現行 data／runtime 分工見 [`../docs/data-and-legacy.md`](../docs/data-and-legacy.md)。
