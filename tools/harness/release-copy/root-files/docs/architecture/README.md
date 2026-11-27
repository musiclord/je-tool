# JET 架構與資料設計

本目錄對應技術文件第 6 節的系統架構、第 7 節的圖表與技術設計，以及第 9 節的資料設計。下圖與技術文件第 6 節嵌入的圖片相同，詳細說明可查閱該文件。[Technical Document](<../SAT/Technical Document.docx>)

![系統架構圖](system-architecture.png)

## 模組與程式位置

下表的程式路徑以本儲存庫為準，圖中的 `JET/` 路徑以方案檔所在的 `JET/` 資料夾為起點。

| 模組 | 職責 | 程式位置 |
|---|---|---|
| Host | 設定程式進入點、建立 WebView2 視窗及組裝各模組。 | [Program](../../JET/JET/Program.cs)、[Form1](../../JET/JET/Form1.cs)、[AppCompositionRoot](../../JET/JET/AppCompositionRoot.cs) |
| 前端與 Bridge | 呈現畫面，透過 JSON 傳送請求，由 dispatcher 分派給 handler。 | [wwwroot](../../JET/JET/wwwroot/)、[ActionDispatcher](../../JET/JET/Bridge/ActionDispatcher.cs) |
| Application | 協調匯入、配對、驗證、篩選與匯出等作業。 | [Handlers](../../JET/JET/Application/Handlers/) |
| AuditCore 與 Domain | 定義查核條件、資料契約及資料存取介面。 | [AuditCore](../../JET/JET/AuditCore/)、[Domain](../../JET/JET/Domain/) |
| Infrastructure | 讀取來源檔、存取 SQLite、輸出報告，並寫入日誌與處理紀錄。 | [Infrastructure](../../JET/JET/Infrastructure/) |

## 技術設計圖表

| 圖表 | 內容 | 圖檔與文字來源 |
|---|---|---|
| 系統架構圖 | 模組之間的呼叫方向，以及來源檔、案件資料庫、報告和日誌的關係。 | [PNG](system-architecture.png) |
| 元件圖 | 內部元件、介面與外部元件。 | [PNG](<../SAT/Diagrams/Structure Diagrams/Component Diagram.png>)、[文字來源](<../SAT/Diagrams/Structure Diagrams/Component Diagram.txt>) |
| 類別圖 | 處理資料驗證與匯出工作底稿兩項請求的主要類別。 | [PNG](<../SAT/Diagrams/Structure Diagrams/Class Diagram.png>)、[文字來源](<../SAT/Diagrams/Structure Diagrams/Class Diagram.txt>) |
| 活動圖 | 「資料驗證與測試」步驟的作業流程。 | [PNG](<../SAT/Diagrams/Behavior diagrams/Activity Diagram.png>)、[文字來源](<../SAT/Diagrams/Behavior diagrams/Activity Diagram.txt>) |
| 實體關聯圖 | 案件資料庫的資料表與欄位。連線表示查詢時使用的關聯欄位，資料庫未宣告外鍵。 | [PNG](../SAT/Diagrams/ERD.png)、[文字來源](../SAT/Diagrams/ERD.txt) |
| 開發至部署的流程圖 | 自開發、測試至正式部署的作業與文件。 | [Workflow](../SAT/Workflow.xlsx) |

## 資料流程與儲存

匯入時保留來源列的原始值，欄位配對確認後產生標準化的 GL 與 TB。驗證與篩選使用 AuditCore 定義的 SQL 條件，在 SQLite 執行後保存結果，匯出時再依範本產生 Excel 報告與工作底稿。

每個案件以獨立資料夾保存，包含 `project.json`、`jet.db`、`reports.json`、報告、工作底稿與日誌。資料表分為來源資料、標準化與暫存資料、結果及系統資料四類，定義可由資料表目錄核對。[JetSchemaCatalog](../../JET/JET/AuditCore/JetSchemaCatalog.cs)
