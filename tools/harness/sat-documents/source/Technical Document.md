---
title: Software Technical Document
body: 內文2
toc: true
---
## 1. Document Overview & Control

| 項目 | 內容 |
|---|---|
| 軟體名稱 | JE TOOL（以下簡稱 JET） |
| 軟體版本 | v1.0.0.0 |
| 文件目的 | 記錄 JET 的功能範圍、技術架構、設計與相關參考資料，供開發、測試、維護及交接使用。 |

## 2. Software Overview & Scope

**工具用途與使用者。**JET 為總帳測試工具，供 KCT、ITA 等相關人員進行總帳資料測試與底稿產出作業。

**主要功能與範圍。**功能包含欄位配對、完整性測試、進階篩選條件及輸出底稿。各功能的需求與限制請參閱第 3 節，採用的技術與相依項目請參閱第 4 節。

**系統邊界、使用假設與限制**

| 項目 | 內容 |
|---|---|
| 系統邊界 | 範圍：執行檔、本機頁面與 Excel 範本，以及使用者電腦上的案件資料夾。<br>輸入：使用者選取的來源檔。<br>輸出：報告、工作底稿、總帳（GL）與試算表（TB）處理紀錄及支援日誌，寫入案件資料夾。<br>案件資料夾以外的檔案：未處理例外的紀錄寫入 `%LOCALAPPDATA%\JET\logs`，見第 14 節；WebView2 的使用者資料存放於 `%LOCALAPPDATA%\JET\WebView2`。<br>對外連線：只有讀取使用者全名時的網域查詢，見第 4 節。 |
| 使用假設 | JET 部署於 GD 環境，作業系統為 Windows 11 x64，已安裝 WebView2 Runtime（Windows 11 內建）。來源檔存放於使用者可讀取的位置。 |
| 限制 | 匯入 Access 資料表前，須先安裝 x64 版 Access Database Engine。案件根資料夾須位於本機磁碟。其餘見第 17 節。 |
| 不在範圍內的項目 | GL 與 TB 由查核團隊向受查者取得，取得來源檔的作業不在 JET 的範圍內。 |

**操作步驟**

JET 為 Windows 桌面應用程式，使用者依下表的六個步驟操作。

| 步驟 | 功能 |
|---|---|
| 建立案件 | 設定案件名稱、查核期間與期末財報準備日，建立案件資料夾與資料庫。 |
| 匯入資料 | 匯入 GL 與 TB，以及選用的科目配對、國定假日、補班日與授權編製人員清單。 |
| 欄位配對 | 將來源欄位對應至 JET 的標準欄位，確認後產生標準化的 GL 與 TB。 |
| 資料驗證與測試 | 執行完整性測試、借貸不平測試、資料缺漏輔助檢視與資料可靠性測試；可另執行預篩選。 |
| 進階條件篩選 | 以常用篩選條件、預篩選結果與欄位條件組成篩選情境，預覽並儲存命中的分錄。 |
| 匯出底稿 | 輸出資料驗證報告、資料可靠性抽樣清單、預篩選報告、條件篩選報告與工作底稿。 |

## 3. Requirements & Constraints

**需求狀態。**功能需求依舊版 JET 的功能與查核團隊的作業需求定義；查核程序的名稱，以及報告與工作底稿的格式，沿用舊版 JET。

**功能需求**

系統應支援下列功能：

- 讀取總帳、試算表及參考資料（XLSX、XLS、CSV 或 Access），並由審計員配對欄位。
- 依六個步驟執行資料驗證、預篩選與進階條件篩選。
- 保存案件資料、進度與設定，畫面以摘要與分頁查詢。
- 資料修改後，標示並清除需要重新計算的結果。
- 輸出資料驗證報告、預篩選報告、條件篩選報告與工作底稿；工作底稿每次匯出另存新檔。

**非功能需求與限制**

| 項目 | 內容 |
|---|---|
| 效能 | 未訂定效能門檻。資料處理的方式見 Design Principle.docx 第 1 節與第 4.3 節，量測方式見第 12 節。 |
| 可用性與擴充性 | 不適用，JET 為單機執行的桌面應用程式，無常駐服務。 |
| 無障礙 | 未訂定無障礙標準。 |
| 安全性 | 見第 10 節。 |
| 相容性 | 作業系統為 Windows 11 x64。匯入 Excel（.xlsx、.xlsm、.xls）、文字檔（.csv、.txt）與 Access（.mdb、.accdb）；輸出 Excel（.xlsx）。 |
| 其他限制 | 見第 2 節的限制與第 17 節。 |

**驗收與參考資料**

| 項目 | 內容 |
|---|---|
| 驗收標準 | 自動化測試全數通過，且經 Code Review 核准。 |
| 需求參考資料 | 舊版 JET 的報告與工作底稿範本，以及系統架構圖（第 6 節）。 |
| 正式需求文件 | 待填寫 |

## 4. Technology Stack & Dependencies

**主要技術與資料介面**

| 項目 | 內容 |
|---|---|
| 應用流程與畫面 | 系統使用 C# 處理應用流程，並以 WebView2 呈現資料及接收使用者操作。 |
| 畫面與應用流程的介面 | 畫面透過 WebView2 的 Web message 傳送請求名稱與 payload；Bridge 依名稱找到 handler 並呼叫 HandleAsync，由應用流程安排檢查、執行與回應。格式見下方的 JSON 訊息。 |
| 資料庫 | 以 SQL 處理案件資料並產生查詢結果；案件資料庫保存案件資料，供畫面按需讀取摘要與分頁。 |
| 案件進度與設定 | 另存於 project.json，與案件資料庫置於同一案件資料夾。 |
| 來源資料 | 總帳、試算表及參考資料，可採 XLSX、XLS、CSV 或 Access。 |

**內部模組與相依關係**

| 項目 | 內容 |
|---|---|
| 模組職責 | Application 負責安排操作流程。<br>AuditCore 與 Domain 定義查核邏輯，以及資料修改後需重新計算的結果。<br>Infrastructure 逐列讀取來源檔；產生工作簿時先建立暫存檔，再改名並更新報告清單。 |
| 輸出檔的保存 | 報告儲存於案件資料夾，再次匯出時取代同名的報告。<br>工作底稿每次匯出另存新檔，檔名附匯出時間，已匯出的工作底稿不覆寫。 |
| 專案組成 | 方案檔 JET.slnx 含應用程式專案 JET/JET.csproj 與測試專案 tests/JET.Tests/JET.Tests.csproj。應用程式專案編譯為單一 assembly，以資料夾與命名空間劃分為 Bridge、Application、AuditCore、Domain 與 Infrastructure 五個模組；JET 資料夾最上層的原始檔為 Host。各模組的職責見第 6 節。 |
| 模組介面 | Bridge 依請求名稱，將前端的請求分派至實作 IApplicationActionHandler 的 handler。各 handler 經 Domain 定義的資料存取介面與 Application 定義的介面，使用資料庫、檔案與視窗的功能。AuditCore 的入口為 JetAuditProgram。 |
| 相依方向 | Domain 不相依其他模組；AuditCore 不相依 Application、Infrastructure 與 Bridge；Application 與 Bridge 不相依 Infrastructure。Infrastructure 實作 Domain 定義的介面。各模組的物件由 AppCompositionRoot 組裝。規則見 Design Principle.docx 第 3 節，並由架構測試檢查。 |

**版本、外部相依項目與相容性**

| 項目 | 內容 |
|---|---|
| 程式語言版本 | C# 14。 |
| 應用框架、.NET runtime 與版本 | .NET 10（net10.0-windows）與 WinForms。發布檔內含 .NET 10 runtime，GD 環境無須另行安裝 .NET。 |
| WebView2 SDK 與 Runtime 版本及部署方式 | SDK 為 Microsoft.Web.WebView2 1.0.4258.31。Runtime 採用 GD 環境已安裝的 WebView2 Runtime（Windows 11 內建）。 |
| 前端語言、框架與建置工具 | 前端以 HTML、CSS 與 JavaScript 撰寫，未使用前端框架；圖表採用 Apache ECharts。前端無建置步驟，JET/wwwroot 的檔案於建置與發布時複製至輸出的資料夾。 |
| 資料庫產品、版本及存取套件 | SQLite 3.53.4，經 Microsoft.Data.Sqlite 10.0.15 與 SQLitePCLRaw.bundle_e_sqlite3 3.0.5 存取。每個案件一個資料庫檔。 |
| XLSX、XLS、CSV 讀寫套件及版本 | 以 DocumentFormat.OpenXml 3.5.1 讀取 .xlsx 與 .xlsm，並寫入報告與工作底稿；以 ExcelDataReader 3.9.0 讀取 .xls。文字檔（.csv 與 .txt）以 .NET 內建的功能讀取。ClosedXML 0.105.1 僅用於寫入 Debug 組態的示範資料檔。 |
| Access 存取方式、驅動程式與位元架構要求 | 以 System.Data.OleDb 經 Access Database Engine 的 OLE DB 提供者 Microsoft.ACE.OLEDB.16.0 讀取。電腦須安裝 x64 版 Access Database Engine。讀取 Access 資料表的查詢逾時為 30 秒。 |
| 其他直接與間接相依套件名稱及版本 | Microsoft.Extensions.Logging 10.0.15 用於寫入支援日誌與 Debug 組態的診斷日誌。其餘套件的名稱與版本見第 5.1 節的元件表。 |
| 外部服務及其用途 | JET 以 Windows 內建的 ADSI（Active Directory Service Interfaces）WinNT 提供者讀取登入帳號的使用者全名；登入帳號為網域帳號時，Windows 可能向網域控制站（domain controller）查詢。使用者全名於 JET 啟動時在背景查詢一次，建立案件時最多等候 3 秒；查詢失敗或逾時，建案者記為帳號短名。 |
| 支援的作業系統與部署環境 | Windows 11 x64 的 GD 環境。發布檔僅提供 win-x64。 |
| 技術選擇理由及相容性限制 | 技術選擇的理由見第 6 節的架構說明，相容性限制見第 2 節的使用假設與限制。 |

元件清單、授權資訊與相依項目風險評估請參閱第 5 節。

**請求種類**

前端送出的每種請求（action）對應 JET/Application/Handlers 內的一個 handler；handler 讀取並檢查 payload（請求的參數）後組成回應。請求依名稱分類如下：

| 請求 | 用途 |
|---|---|
| project.* | 建立、開啟、修改與刪除案件，列出案件，儲存目前的步驟，釋放案件鎖。 |
| import.* | 檢視與預覽來源檔；匯入 GL、TB、科目配對、國定假日、補班日與授權編製人員清單。 |
| mapping.* | 讀取來源欄位的值，確認 GL 與 TB 的欄位配對，由工作底稿帶入欄位配對。 |
| accountMapping.save、accountCategory.save、calendar.setNonWorkingDays | 儲存科目配對、科目分類與每週休息日。 |
| validate.run、prescreen.run | 執行資料驗證與預篩選。 |
| filter.* | 預覽篩選條件，儲存、重新檢查與刪除篩選情境。 |
| query.* | 分頁查詢資料預覽與各項結果的明細。 |
| export.* | 輸出報告、工作底稿與科目配對範本。 |
| log.*、support.log.export | 寫入與讀取「訊息」區的紀錄，輸出支援日誌。 |
| host.* | 開啟檔案選取對話框與案件資料夾，結束 JET。 |
| operation.cancel、system.* | 取消作業，確認前端與後端的連線，取得目前的 Windows 帳號。 |
| dev.*、demo.*、project.loadDemo | 檢視資料庫、輸出診斷日誌與示範資料；僅 Debug 組態註冊，見第 10.1 節。 |

**JSON 訊息**

前端與後端於同一台電腦內透過 WebView2 傳送 JSON 訊息，格式如下：

| JSON 訊息 | 內容 |
|---|---|
| 請求 | requestId：前端為每個請求產生的 UUID。<br>action：請求名稱，例如 validate.run。<br>payload：該請求的參數。 |
| 成功回應 | 相同的 requestId、ok 為 true、data，以及後端產生的 correlationId，支援日誌以同一值記錄該請求。 |
| 失敗回應 | 相同的 requestId、ok 為 false、correlationId；error 含錯誤碼 code 與中文的錯誤訊息 message，並可含欄位名稱 field 與逐條明細 details。 |
| 錯誤碼 | 定義於 JetErrorCodes，例如 invalid_payload、operation_in_progress、operation_cancelled 與 database_ 開頭的資料庫錯誤；處理規則見 Design Principle.docx 第 4.5 節。 |
| 事件 | import.progress、mapping.progress 與 export.progress 三種進度事件，含 event 與 data。 |
| 逾時與重試 | 請求：未設逾時，失敗後不自動重送；長時間作業可由使用者取消。<br>「訊息」區紀錄的寫入（log.append）：失敗時，留待下次狀態變更再送出。 |

## 5. Dependency Security: SBOM & SCA

### 5.1 SBOM Inventory & Traceability

| 項目 | 內容 |
|---|---|
| 涵蓋範圍 | repository：JET 的原始碼為單一 repository，含方案檔 JET.slnx、應用程式專案與測試專案；名稱待填寫。<br>清冊涵蓋：JET 的 NuGet 套件與存放於 JET/wwwroot 的前端元件。<br>未列入：測試專案另使用的 NuGet 套件。<br>容器：不適用，JET 不以容器發布。 |
| 排除項目與維護責任 | 清冊不含 Windows、Microsoft Office、Access Database Engine 與 WebView2 Runtime。發布檔內含的 .NET 10 runtime 亦未列入。開發人員於套件變更時更新清冊。 |
| 元件資訊 | 下方的元件表列出各元件的名稱、版本、引用方式與授權。 |
| 直接與間接相依項目 | JET 直接引用 9 個 NuGet 套件，連同間接引用的套件共 23 個。元件表的「引用方式」欄標示直接、間接或前端。 |
| 產生方式 | 清冊依套件還原結果整理，未使用 SBOM 產生工具，未產出 SPDX 或 CycloneDX 格式的 SBOM。 |
| 驗證與限制 | 元件與版本依套件還原結果核對，授權依各元件的宣告核對。清冊未列供應商與其他識別碼，也未掃描發布檔。 |
| 存放與保存期限 | 清冊列於本節；套件與版本宣告於 JET/JET.csproj 與 tests/JET.Tests/JET.Tests.csproj。存取與分發方式及保存期限：待填寫。 |
| 版本對應 | 清冊對應 JET 1.0.0.0。build ID：待填寫。 |

所列元件無須另購商業授權。授權聲明檔的現況見第 17 節。元件清冊如下：

| 元件 | 版本 | 引用方式 | 授權 |
|---|---|---|---|
| ClosedXML | 0.105.1 | 直接 | MIT |
| DocumentFormat.OpenXml | 3.5.1 | 直接 | MIT |
| ExcelDataReader | 3.9.0 | 直接 | MIT |
| Microsoft.Data.Sqlite | 10.0.15 | 直接 | MIT |
| Microsoft.Extensions.Logging | 10.0.15 | 直接 | MIT |
| Microsoft.Extensions.Logging.Abstractions | 10.0.15 | 直接 | MIT |
| Microsoft.Web.WebView2 | 1.0.4258.31 | 直接 | Microsoft WebView2 SDK 授權（BSD-3-Clause 形式） |
| SQLitePCLRaw.bundle_e_sqlite3 | 3.0.5 | 直接 | Apache-2.0；內含的 SQLite 3.53.4 為公有領域（public domain） |
| System.Data.OleDb | 10.0.15 | 直接 | MIT |
| ClosedXML.Parser | 2.0.0 | 間接 | MIT |
| DocumentFormat.OpenXml.Framework | 3.5.1 | 間接 | MIT |
| ExcelNumberFormat | 1.1.0 | 間接 | MIT |
| Microsoft.Data.Sqlite.Core | 10.0.15 | 間接 | MIT |
| Microsoft.Extensions.DependencyInjection | 10.0.15 | 間接 | MIT |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.15 | 間接 | MIT |
| Microsoft.Extensions.Options | 10.0.15 | 間接 | MIT |
| Microsoft.Extensions.Primitives | 10.0.15 | 間接 | MIT |
| RBush.Signed | 4.0.0 | 間接 | MIT |
| SixLabors.Fonts | 1.0.0 | 間接 | Apache-2.0 |
| SQLite | 3.53.4 | 間接 | 公有領域 |
| SQLitePCLRaw.config.e_sqlite3 | 3.0.5 | 間接 | Apache-2.0 |
| SQLitePCLRaw.core | 3.0.5 | 間接 | Apache-2.0 |
| SQLitePCLRaw.provider.e_sqlite3 | 3.0.5 | 間接 | Apache-2.0 |
| Apache ECharts（含 d3.js 子元件） | 5.5.0 | 前端 | Apache-2.0；d3.js 子元件為 BSD-3-Clause |
| Fluent UI System Icons | 1.1.344 | 前端 | MIT |
| Instrument Sans | Google Fonts v4 | 前端 | SIL Open Font License 1.1 |
| Noto Sans TC | 未標示 | 前端 | SIL Open Font License 1.1 |

### 5.2 SCA & Remediation

| 項目 | 內容 |
|---|---|
| 工具與範圍 | 工具：.NET 10 SDK 的 NuGet 套件弱點查詢（dotnet package list），查詢命令見本節最後。<br>範圍：JET 直接與間接引用的 NuGet 套件，不含發布檔內含的 .NET runtime。<br>資料來源：nuget.org 提供的公開弱點資料。<br>查詢結果：2026-11-24 查詢，未發現已知弱點。<br>前端元件：依各元件原廠的弱點公告核對。 |
| 檢查項目 | 弱點：查詢結果見上一項。<br>授權：逐一核對直接與間接引用的元件，各元件的結果見第 5.1 節。<br>停止維護元件：將查詢命令的 --vulnerable 改為 --deprecated，2026-11-24 查詢，所有套件均未標示停止維護。 |
| 已處置的發現事項 | GHSA-2m69-gcr7-jv3q（CVE-2025-6965）影響 SQLite 3.50.2 以前的版本，已將 SQLitePCLRaw.bundle_e_sqlite3 升級至 3.0.5（SQLite 3.53.4）。 |
| 其他發現事項 | 弱點：前端的 Apache ECharts 5.5.0 有已知弱點 CVE-2026-45249，影響 6.1.0 以前的版本。<br>影響條件：Lines series 搭配 tooltip、未自訂 tooltip 的 formatter，且資料項目設有 name 時，name 內的 HTML 可能於 tooltip 中執行。<br>JET 的使用方式：圖表只使用長條圖、折線圖與散布圖，每個 tooltip 皆自訂 formatter，並將顯示的文字轉為純文字。<br>處置：不符合影響條件，故未處置。 |
| 誤判 | 無誤判的發現事項。 |
| 處理責任 | 開發人員負責查詢與處置弱點，規則見 Design Principle.docx 第 8 節。複核與例外核准的責任：待填寫。 |
| 核准政策與期限 | 元件核准與授權政策、合併與發布的阻擋條件，以及處置期限：待填寫。 |
| 例外 | 無例外核准。 |

查詢命令：

```
dotnet package list --project JET/JET.csproj `
  --no-restore --vulnerable --include-transitive `
  --format json --output-version 1
```

### 5.3 CI/CD & Release Evidence

| 項目 | 內容 |
|---|---|
| 流程與檢查 | 更新套件後重新查詢弱點並更新元件清冊，規則見 Design Principle.docx 第 8 節。SBOM 與 SCA 於 CI/CD pipeline 的階段、觸發條件、必要檢查與報告的保存期限：待填寫。 |
| 新弱點的重新評估 | 待填寫。 |
| 各版本的佐證 | 1.0.0.0 的元件清冊見第 5.1 節，2026-11-24 的弱點查詢結果見第 5.2 節。無未結事項與例外核准。核准決定與簽核日期：待填寫。 |

整體交付流程請參閱第 13 節。

## 6. System Architecture

系統架構文件：請參閱下列資料夾（路徑相對於上層文件資料夾）：

..\architecture

.\Diagrams

![系統架構圖](diagrams/framework.png)

| 層級 | 模組 | 職責 |
|---|---|---|
| Host | Program、Form1、AppCompositionRoot | Program：進入點；先設定將未處理的例外記錄至檔案，再建立主視窗。<br>Form1：建立 WebView2，以 WebView2 的 virtual host name 功能將 JET/wwwroot 資料夾對應為 https://app.jet.local，再載入本機頁面。另處理檔案選取對話框、關閉視窗與 WebView2 處理程序的異常。<br>AppCompositionRoot：建立資料庫的存取物件、日誌、操作監看、處理紀錄與各 handler，交由 ActionDispatcher 分派請求；啟動時於背景查詢使用者全名。 |
| Presentation Layer | JET/wwwroot、Bridge | 前端：六個步驟的頁面、資料預覽與「訊息」區；所有請求經 jet-api.js 送出，畫面狀態由 state.js 保存。<br>Bridge：JetWebMessageBridge 接收並回覆 JSON 訊息；ActionDispatcher 依請求名稱找到 handler，於背景執行緒執行並管理取消，並將作業的開始、結束與結果通知操作監看與處理紀錄；WebViewEventPublisher 將進度事件送回前端。 |
| Application Layer | Application | Handler：每種請求一個。<br>處理順序：讀取並檢查 payload，取得案件資料，呼叫 AuditCore 的查核邏輯，寫入結果後回覆。<br>ProjectSession：保存目前開啟的案件。 |
| Domain Layer | AuditCore、Domain | AuditCore：資料驗證、預篩選、進階條件篩選與報告輸出的查核邏輯，並產生執行這些程序的 SQL 條件；入口為 JetAuditProgram。<br>Domain：定義 data contract，即各層之間傳遞資料使用的型別與欄位，例如分頁查詢的結果列、報告輸出的資料與資料存取介面的參數；另定義來源值的整理規則、欄位與篩選規則、資料存取介面、操作監看與處理紀錄的介面、錯誤碼，以及各請求是否須單獨執行。 |
| Infrastructure Layer | Infrastructure | Persistence 與 Sql：案件資料夾、案件設定檔與 SQLite 資料庫的存取；取消作業時中斷執行中的 SQL statement。<br>FileIO：讀取 Excel、文字檔與 Access 資料表。<br>Export：依範本輸出報告與工作底稿。<br>Diagnostics：支援日誌與 Debug 組態的診斷日誌。<br>Diagnostics/Monitoring：操作監看，將作業、SQL statement 與匯出進度的執行情形寫入支援日誌。<br>ProcessingLog：處理紀錄，作業成功後將 GL 與 TB 的處理經過附加至案件資料夾的文字檔。<br>操作監看與處理紀錄的設計規則見 Design Principle.docx 第 5 節，記錄的內容見第 14 節。 |

各模組的相依方向見 Design Principle.docx 第 3 節。

| 項目 | 內容 |
|---|---|
| 前端與後端的通訊 | 前端透過 WebView2 送出 JSON 格式的請求，Bridge 於背景執行緒執行對應的 handler，完成後以同一個 requestId 回覆。檔案選取等視窗操作與取消作業於 UI 執行緒執行。JSON 訊息的格式見第 4 節。 |
| 進度事件 | 匯入、欄位配對與匯出期間，後端主動送出進度事件；前端以事件更新處理中的提示，結果以請求的回應為準。 |
| 單獨執行的作業 | 變更案件資料、開啟案件與匯出的請求，同一時間只執行一項，規則見 Design Principle.docx 第 4.3 節；唯讀的查詢與檢視可同時執行。 |
| 關閉視窗 | Host 呼叫前端的結束流程：確認未儲存的變更、儲存目前的步驟並釋放案件鎖後關閉；作業執行中不關閉。<br>案件鎖：開啟案件時於案件根資料夾建立並持有的鎖定檔，另一個 JET 無法同時開啟同一案件。 |
| 部署模型 | 單機。每台電腦各自執行 JET，案件資料存放於同一台電腦；部署方式見第 13 節。 |
| 設計假設 | 見第 2 節的使用假設。 |

| 架構說明 | 理由 | 取捨 |
|---|---|---|
| 篩選、彙總與比對由 SQLite 以 SQL 對整張資料表執行 | 前端與後端的記憶體只保留摘要與分頁的明細，無須載入完整的 GL。 | 每次查詢與翻頁皆須存取資料庫。 |
| 前端採網頁技術，由 WebView2 載入 | GD 環境已有 WebView2 Runtime，無須另行安裝；畫面可使用 HTML 與 CSS 排版。 | 前端與後端只能透過 WebView2 傳送 JSON 訊息；每種請求的欄位須於前端與對應的 handler 保持一致。 |
| 單機執行，每個案件一個資料夾與資料庫檔 | 無須資料庫伺服器；案件以資料夾為單位複製與搬移。 | 案件只能在本機使用；同一案件同一時間只能由一個 JET 開啟。 |
| 查核邏輯集中於 AuditCore 與 Domain | 畫面、報告與工作底稿使用同一份判定，便於人工複核。 | 其他模組須引用 AuditCore 的定義，不得自行撰寫；前端須向後端取得金額與查核結果。 |
| 金額以 Int64 儲存 | SQL 的加總與比對以整數計算，不產生浮點誤差。 | 精度固定為小數第 4 位。 |
| 發布為 self-contained single-file，即 .NET runtime 與 JET 的程式合併為單一執行檔 JET.exe | GD 環境無須安裝 .NET runtime，部署只須複製發布資料夾。 | 發布檔較大；.NET runtime 的安全更新須重新發布 JET。 |
| 請求於背景執行緒執行，變更案件資料的作業同一時間只執行一項 | UI 不因長時間作業而停止回應；避免兩項作業同時改寫同一案件。 | 作業完成或取消前，其他變更案件資料的請求立即回覆 operation_in_progress。 |

## 7. Diagrams & Technical Design

圖表與技術設計文件：請參閱下列資料夾（路徑相對於上層文件資料夾）：

..\architecture

.\Diagrams

| 圖表 | 內容 | 檔案 |
|---|---|---|
| 系統架構圖 | 使用者、來源檔、案件資料庫、報告與日誌等輸出檔，以及各層的模組與呼叫方向。 | 第 6 節 |
| 元件圖 | 內部元件、元件之間的介面與外部元件。 | Diagrams/Structure Diagrams/Component Diagram |
| 類別圖 | 處理資料驗證與匯出工作底稿兩項請求的主要類別。 | Diagrams/Structure Diagrams/Class Diagram |
| 活動圖 | 「資料驗證與測試」步驟的作業流程。 | Diagrams/Behavior diagrams/Activity Diagram |
| 實體關聯圖（ERD） | 案件資料庫的資料表與欄位。圖中的連線表示查詢時用於關聯的欄位；資料庫未宣告外鍵。 | Diagrams/ERD |
| 開發至部署的流程圖 | 自開發、測試至正式部署各階段的作業與文件。 | Workflow.xlsx |

| 項目 | 內容 |
|---|---|
| 資料流程 | 匯入：FileIO 讀取來源檔，將每一列的原始值寫入案件資料庫的來源資料表。<br>欄位配對：依確認的欄位配對產生標準化的 GL 與 TB。<br>資料驗證、預篩選與進階條件篩選：AuditCore 產生 SQL 條件，於 SQLite 執行後寫入結果資料表。<br>匯出：Export 讀取結果，依範本輸出至案件資料夾。<br>日誌與處理紀錄：ActionDispatcher 通知作業的開始、結束與結果，SQLite 連線通知 SQL statement 的執行，進度事件帶出匯出進度；操作監看據以寫入支援日誌，處理紀錄據以寫入 GL 與 TB 處理紀錄。 |
| 信任邊界 | JET 與 WebView2 的處理程序於使用者登入 GD 環境的 Windows 工作階段內執行，前端與後端於同一台電腦內傳送 JSON 訊息。外部服務見第 4 節。 |
| 外部輸入 | 使用者選取的來源檔、前端送出的請求與 Windows 回傳的使用者全名；控制見第 10.1 節。 |

## 8. Source Code Structure & Development Standards

原始碼結構與開發規範：請參閱下列檔案（路徑相對於上層文件資料夾）：.\Design Principle.docx

以下路徑以方案檔 JET.slnx 所在的資料夾為起點。各模組的職責見第 6 節。

| 位置 | 內容 |
|---|---|
| JET.slnx | 方案檔，含應用程式專案 JET/JET.csproj 與測試專案 tests/JET.Tests/JET.Tests.csproj。 |
| JET 資料夾最上層的原始檔 | Host：Program.cs 為進入點；Form1.cs 為主視窗與 WebView2 的初始化；AppCompositionRoot.cs 組裝各層的物件；另有案件根資料夾的解析與使用者全名的查詢。 |
| JET/wwwroot | 前端：index.html；css；js 為共用的程式，js/steps 為六個步驟的程式；vendor/echarts 為 ECharts；assets 為字型與圖示。 |
| JET/Bridge | 接收前端的 JSON 訊息、分派請求，並將事件送回前端。 |
| JET/Application | Handlers：各請求的 handler，案件、匯入、查詢與視窗操作各有子資料夾。<br>Support：handler 共用的 payload 讀取、篩選情境與報告輸出的處理。<br>Ports：handler 使用的介面。<br>Contracts：目前登入的使用者與來源檔參數。 |
| JET/AuditCore | 資料驗證、預篩選、進階條件篩選、分頁查詢與報告輸出使用的查核邏輯與 SQL 條件。 |
| JET/Domain | Contracts：data contract。<br>Primitives：來源值的整理規則。<br>Rules：欄位、篩選情境與預篩選規則的定義，以及結果過期的判定。<br>Abstractions：資料存取介面，以及操作監看與處理紀錄的介面。<br>其餘：錯誤碼、請求名稱、請求的執行規則與案件設定。 |
| JET/Infrastructure | Persistence：案件資料夾、案件設定檔與 SQLite 資料表的存取。<br>Sql：分頁查詢與結果寫入的 SQL。<br>FileIO：來源檔的讀取與輸出檔的寫入。<br>Export：報告、工作底稿與科目配對範本的輸出。<br>Diagnostics：支援日誌與 Debug 組態的診斷日誌；Monitoring 子資料夾為操作監看。<br>ProcessingLog：GL 與 TB 處理紀錄。 |
| JET/Templates | Excel 檔：資料驗證報告、資料可靠性抽樣清單、預篩選報告、條件篩選報告、工作底稿與科目配對的範本，以及國定假日與補班日的範例檔。 |
| JET/Properties/PublishProfiles | 發布設定檔 FolderProfile.pubxml。 |
| tests/JET.Tests | 後端測試；Architecture 為架構測試，Fixtures 為測試使用的固定資料。 |
| tests/frontend | 前端測試。 |

| 項目 | 內容 |
|---|---|
| 撰寫慣例 | 見 Design Principle.docx 第 4 節。 |
| 命名慣例 | 見 Design Principle.docx 第 4.1 節。 |
| Commit 慣例 | 見 Design Principle.docx 第 7 節。 |

## 9. Data Design & Management

資料設計與管理文件：請參閱下列資料夾（路徑相對於上層文件資料夾）：

..\architecture

.\Diagrams

每個案件一個資料夾，位於案件根資料夾之下，資料夾名稱即案件名稱。資料表與欄位見 ERD。

| 項目 | 內容 |
|---|---|
| 案件根資料夾 | 預設為 `%USERPROFILE%\JET`；可用環境變數 JET_PROJECTS_ROOT 指定。 |
| 案件資料夾 | project.json：案件設定檔，JSON 格式，寫入後設為唯讀。<br>jet.db：案件資料庫，SQLite 格式。<br>reports.json：已輸出的報告清單，記錄報告的檔名與狀態。<br>報告與工作底稿：匯出的 Excel 檔。<br>日誌：GL 與 TB 處理紀錄，以及 support-log 資料夾內的支援日誌，見第 14 節。 |
| 資料模型 | 分類：資料表分為四類，登錄於 JET/AuditCore/JetSchemaCatalog.cs。<br>來源資料：pbc_gl、pbc_tb、科目配對、授權編製人員清單與行事曆；GL 與 TB 以 JSON 保存每一列的原始值。<br>標準化與暫存資料：欄位配對確認後產生的 gl 與 tb、GL 攸關資料元素的值，以及科目配對與授權編製人員清單匯入時暫存的原始內容；gl 記錄每筆分錄是否納入測試，以及未納入的原因。<br>結果：資料驗證、資料可靠性抽樣、預篩選與篩選命中。<br>系統資料：欄位配對、科目分類、篩選情境、匯入批次、控制總數、結果的過期狀態、案件設定、「訊息」區紀錄與操作紀錄。<br>控制總數：確認欄位配對時記錄的存入筆數與借貸合計。 |
| 資料型別 | 金額：Int64，換算規則見 Design Principle.docx 第 4.3 節。<br>日期：以 yyyy-MM-dd 的文字儲存。<br>文字：去除前後空白後儲存；超過長度上限時回覆錯誤。<br>長度上限：鍵值 450 個字元；科目名稱等一般文字 400 個字元；國定假日名稱 256 個字元；摘要不設上限。 |
| 資料檢查 | 匯入：同一次匯入的來源檔於一個交易內寫入，任一來源檔讀取失敗時整批不寫入。<br>欄位配對：有無法辨識的金額、無任何分錄納入測試，或借貸合計皆為 0 時，整批不寫入。確認時記錄控制總數，資料驗證時以控制總數核對 gl 的筆數與借貸合計。<br>國定假日與補班日：來源檔有空白或無法辨識為日期的值時，整份清單不寫入，錯誤訊息列出該值所在的列號。<br>案件設定：讀取時檢查日期格式、查核期間與數值範圍，不符時視為資料庫損壞。 |
| 案件設定 | 存放：查核期間、金額存放倍率、民國年選項、隨機種子序號、建案者、期末財報準備日與每週休息日另存於資料庫。<br>金額存放倍率：金額轉為 Int64 時乘上的倍數。<br>民國年選項：日期是否接受民國年的寫法。<br>隨機種子序號：資料可靠性測試抽樣使用的隨機種子，建立案件時產生。<br>修改：查核期間、金額存放倍率、民國年選項、隨機種子序號與建案者於建立案件後不可修改，資料庫以 trigger 拒絕變更；期末財報準備日、客戶名稱、案件編號與每週休息日可修改。<br>不一致時：開啟案件時，案件設定檔與資料庫不同的項目依資料庫改回，並於「訊息」區提示。 |
| 結果的過期 | 判定：資料變更後哪些結果過期，集中定義於 JET/Domain/Rules 的 AuditDependencyPolicy 類別；寫入資料的程式依該定義清除受影響的結果與篩選情境。<br>匯入 GL 或 TB、確認欄位配對：清除全部結果與篩選情境，並清除科目配對。<br>變更行事曆、科目配對、科目分類、授權編製人員清單或期末財報準備日：清除預篩選、篩選命中與篩選情境。<br>報告：已輸出的報告於報告清單標示為過期。 |
| 交易 | 寫入案件資料的作業於交易內執行；取消或失敗時 rollback 交易，規則見 Design Principle.docx 第 4.3 節。 |
| 版本與移轉 | 版本：案件資料庫與案件設定檔各自記錄格式版本。<br>升版：資料庫結構變更時版本加一，建表程式改為新的結構，並新增一個將前一版升至新版的升版步驟。開啟較早版本建立的案件時，於單一交易內逐版升至現行版本；失敗時 rollback 交易，資料庫維持原版本。<br>首次發布：1.0.0.0 的資料庫版本為後續版本可升版的最早版本，目前無升版步驟。<br>較新版本的案件：由較新版本的 JET 建立或開啟過的案件，JET 不開啟，並提示改用較新版本。 |
| 保存 | 案件資料：存放於使用者電腦的案件根資料夾，由使用者保存或刪除；JET 不自動清除。<br>刪除案件：永久刪除整個案件資料夾；刪除前列出將一併刪除的報告與工作底稿數。 |
| 備份與復原 | JET 未提供備份功能。<br>備份：關閉 JET 後複製整個案件資料夾。<br>復原：將備份的資料夾以原名稱複製至案件根資料夾後開啟。 |
| 機密資訊的處理 | 範圍：案件資料可能含受查者的機密資訊與人員名稱；資料保護見第 10.1 節。<br>建案者：使用者全名或帳號短名記錄於案件設定檔與資料庫，並寫入資料驗證報告、預篩選報告與條件篩選報告的「Prepared by」欄位。<br>來源檔：資料庫記錄來源檔的檔名。 |

## 10. Application Security & GitHub Security

### 10.1 Application Security

應用程式安全性文件與佐證報告：請參閱下列資料夾（路徑相對於上層文件資料夾）：.\Github Security Reports

現行控制如下表。

| 項目 | 現行控制 |
|---|---|
| 身分驗證與授權 | 登入：JET 未另設登入機制。JET 為單機執行的桌面應用程式，不提供網路服務；使用者須先以 Windows 帳號登入 GD 環境才能執行 JET，身分由 Windows 驗證。<br>使用者識別：建案者與案件鎖的持有人取自目前登入的 Windows 帳號。<br>授權：案件資料夾的存取由 Windows 檔案權限控管。 |
| 資料保護 | 處理範圍：案件資料於本機處理。<br>案件設定：查核期間、金額存放倍率等建立案件時決定的設定不可修改，資料庫以 trigger 拒絕變更，見第 9 節。<br>輸出檔：暫存檔、檔名與不覆寫工作底稿的規則見 Design Principle.docx 第 4.4 節。 |
| 輸入檢查 | SQL：參數化與 allowlist 的規則見 Design Principle.docx 第 4.4 節；allowlist 指程式內預先列出的允許值。<br>案件名稱：只允許文字、數字、半形空白、底線、連字號與括號，最長 100 字，不得為 Windows 保留的裝置名稱。<br>檔案路徑：報告與工作底稿的完整路徑須位於案件資料夾內；案件根資料夾與案件資料夾的路徑不得含 reparse point，即符號連結等指向其他位置的檔案系統連結。<br>來源檔：副檔名限 .xlsx、.xlsm、.xls、.csv、.txt、.mdb、.accdb；以唯讀方式開啟，且不執行檔案內的巨集。<br>日期：payload 的日期須為 yyyy-MM-dd；建立案件時，查核起始日與查核截止日的年份須為 1900 至 2100 年，起始日不晚於截止日。<br>數量：抽樣筆數 1 至 1,048,524 筆；每項清單條件最多 100 個值；每個案件最多 10 個篩選情境。<br>文字長度：見第 9 節。<br>未通過時：請求不執行，回覆錯誤碼與錯誤訊息。<br>分頁：每頁最多 200 筆；未指定或超出範圍的筆數，以範圍內的筆數查詢。<br>使用者全名：Windows 回傳的值去除前後空白後為空字串、含控制字元或超過 256 個字元時，建案者記為帳號短名。 |
| 機密管理 | JET 不使用密碼、金鑰、token 或含密碼的連線字串。 |
| 介面與 runtime 的安全 | WebView2 的導覽：本機頁面的網址為 https://app.jet.local，見第 6 節；頁面導覽至其他網址時取消，要求開啟新視窗時不開啟。<br>WebView2 的 host objects：停用。host objects 是將後端物件提供給頁面的 JavaScript 直接呼叫的功能。<br>前端：未使用 iframe，也未引用外部網址的資源。<br>Debug 組態才啟用的功能：資料庫檢視、診斷日誌與示範資料的請求（dev.*、demo.*、project.loadDemo），診斷日誌的寫入，以及 WebView2 的開發人員工具、瀏覽器快速鍵與右鍵選單；Release 組態不註冊這些請求。<br>非預期的例外：例外內容不傳至前端，見 Design Principle.docx 第 4.5 節。 |
| 弱點通報與處置 | 待填寫 |

### 10.2 GitHub & Repository Security

GitHub 與程式碼儲存庫安全性文件與佐證報告：請參閱下列資料夾（路徑相對於上層文件資料夾）：.\Github Security Reports。SBOM/SCA 政策與佐證請參閱第 5 節。

| 項目 | 設定 |
|---|---|
| 存取權限 | 待填寫 |
| Branch protection、required reviews 與 required checks | 待填寫 |
| Secret scanning 與 push protection | 待填寫 |
| Code scanning | 待填寫 |
| Dependabot alerts 與 dependency review | 待填寫 |
| 安全警示的處理 | 待填寫 |
| Workflow permissions | 待填寫 |

## 11. Code Review

程式碼審查文件與佐證紀錄：請參閱下列資料夾：.\Code Review

| 項目 | 內容 |
|---|---|
| 複核流程 | 依 Code Review/Code review rule.docx 第 6 節：開發人員送交變更說明、需求依據、受影響的程式與版本、單元測試報告與固定值清單；複核人員確認測試結果並人工複核；開發人員修正發現事項後重新送交；複核人員記錄結論。 |
| 驗收依據與測試佐證 | 以自動化測試結果為主要驗收依據，詳閱 Code Review/Unit test Report.xlsx。 |
| 人工複核的範圍 | 程式內的固定值與規則（hard-coded content），即寫在程式內的六項核心查核邏輯，以及這些邏輯使用的固定值。範圍依風險決定：直接影響查核結果的邏輯由人工複核。 |
| 技術檢查表與紀錄 | Code Review/Code review.xlsx。六項核心查核邏輯的測試案例與執行結果列於 Code Review/Unit test Report.xlsx。 |
| 核准條件 | 依 Code Review/Code review rule.docx 第 5 節：必要的測試全數通過、人工複核完成、固定值與規則經接受或修正，且無未解決的發現事項。無法合理執行的檢查，須經指定的核准人例外核准。 |
| 安全與第三方元件變更的複核 | 安全：輸入檢查、例外處理與機密資訊的檢查項目見 Code Review/Code review.xlsx。<br>第三方元件：見第 5.2 節。 |
| 發現事項的處理 | 記錄於 Code Review/Code review.xlsx；修正後重新執行受影響的測試，複核人員複核修正後的程式。 |
| Pull Request | 待填寫 |

## 12. Testing & Quality Assurance

測試與品質保證文件：請參閱下列檔案（路徑相對於上層文件資料夾）：

.\New JE Tool_SAT Test Scenario & Results.xlsx

.\New JE Tool_SAT Test Scenario & Results_進階篩選測試.xlsx

| 測試類型 | 內容 |
|---|---|
| 單元測試 | tests/JET.Tests，採 xUnit v3；核對查核邏輯、來源值的整理規則與 handler 的輸出。部分規則另以 FsCheck 產生隨機輸入核對。 |
| 整合測試 | tests/JET.Tests；以暫存的 SQLite 案件資料庫執行匯入至匯出的流程，並讀回輸出的活頁簿核對內容。 |
| 架構測試 | tests/JET.Tests/Architecture；以 NetArchTest 檢查各模組的相依方向，並檢查 AuditCore 定義的查核條件未於其他模組另行撰寫。 |
| 前端測試 | tests/frontend，採 Node.js 內建的 test runner；以模擬的 DOM 與後端回應核對畫面內容與送出的請求，並核對前端送出的請求與後端 handler 的欄位一致。 |
| 回歸測試 | 變更後執行受影響的測試；發布前執行全部自動化測試。 |
| 效能測試 | 以 1,000,000 筆合成 GL 量測匯入至匯出的耗時與記憶體用量，未訂定通過門檻。效能測試以 TestProfile=Scale 的測試屬性（trait）標記，由下列命令的 --filter-not-trait 參數排除。 |
| 安全測試 | NuGet 套件弱點查詢，見第 5.2 節。 |

測試的設計、撰寫、執行與報告依 Code Review/Unit test rule.docx 的規定。自動化測試於開發環境以 Debug 組態執行，建置環境見第 13 節；測試使用程式產生的合成測試資料。

```
dotnet build JET.slnx -c Debug
tests/JET.Tests/bin/Debug/net10.0-windows/JET.Tests.exe --filter-not-trait "TestProfile=Scale"
node --test --test-concurrency=4 tests/frontend/
```

驗收條件為上列命令執行的測試案例全數通過，無失敗與略過。2026-11-24 的執行結果如下，明細見 Code Review/Unit test Report.xlsx。

| 項目 | 結果 |
|---|---|
| 後端測試 | 5,448 個測試案例全數通過。 |
| 前端測試 | 1,095 個測試案例全數通過。 |
| 程式碼涵蓋率 | 後端的行涵蓋率（line coverage）為 93.2%，共 48,144 行中執行 44,857 行；以 Microsoft.Testing.Extensions.CodeCoverage 量測，命令見下方。未訂定涵蓋率門檻。 |
| 涵蓋缺口 | 前端的 JavaScript 未量測涵蓋率。<br>Host 的行涵蓋率為 61.9%，Bridge 為 79.7%；主視窗 Form1 與 JetWebMessageBridge 中操作 WebView2 的程式，大部分未由自動化測試執行。 |

量測程式碼涵蓋率時，後端測試命令另加下列參數：

```
--coverage --coverage-output-format cobertura --coverage-output <輸出檔>
```

## 13. Build, CI/CD & Deployment

建置、CI/CD 與部署文件：請參閱下列檔案（路徑相對於上層文件資料夾）：.\Workflow.xlsx

| 項目 | 內容 |
|---|---|
| 建置環境 | 工具：Windows 11 x64、Visual Studio 2026 與 .NET 10 SDK；建置時自動還原 NuGet 套件。前端測試以 Node.js 26 執行。<br>建置組態：開發環境以 Debug 組態執行，GD 環境使用 Release 組態的發布檔。 |
| 建置與產物 | 以發布設定檔 JET/Properties/PublishProfiles/FolderProfile.pubxml 發布 Release 組態、win-x64 的 self-contained single-file JET.exe。<br>JET.exe：內含 .NET runtime、SQLite 與 WebView2 的原生程式庫，以及內嵌的偵錯符號。<br>發布資料夾：另含 wwwroot 與 Templates 兩個資料夾。 |
| Pipeline 階段與各環境的部署順序 | 見 Workflow.xlsx。 |
| 發布前檢查 | 自動化測試全數通過後產生發布檔；第三方元件的弱點查詢見第 5.2 節。 |
| 核准 | 待填寫 |
| 先決條件 | 見第 2 節的使用假設。無安裝程序，發布資料夾複製至 GD 環境的電腦後執行 JET.exe。 |
| 移轉順序 | 1.0.0.0 為首次發布，無須移轉。後續版本的案件資料庫於開啟案件時升版，見第 9 節。 |
| 復原 | 以上一版的發布資料夾取代現行的發布資料夾。1.0.0.0 為首次發布，無上一版。 |
| 部署後確認 | 以一般權限的 Windows 帳號執行 JET，建立案件並完成六個步驟。 |

以 Visual Studio 2026 開啟 JET.slnx 後建置與發布。命令列的發布命令如下：

```
dotnet publish JET/JET.csproj -p:PublishProfile=FolderProfile
```

## 14. Operations, Monitoring & Troubleshooting

維運、監控與故障排除文件：請參閱下列檔案（路徑相對於上層文件資料夾）：.\Workflow.xlsx

| 項目 | 內容 |
|---|---|
| 日誌 | 「訊息」區：記錄每項作業的結果與耗時；畫面顯示最近 30 則，每個案件於資料庫保存最近 500 則，可複製最近 100 則。<br>GL 與 TB 處理紀錄：案件資料夾內的「案件名稱_GL處理紀錄.txt」與「案件名稱_TB處理紀錄.txt」；作業成功後附加步驟、時間、執行人員、條件與筆數。<br>支援日誌：記錄請求的結果、錯誤碼與操作監看的事件，寫入案件資料夾的 support-log/events.ndjson；可由「訊息」區輸出為 JET-support 開頭的文字檔。記錄的範圍見 Design Principle.docx 第 5 節。<br>未處理的例外：寫入 `%LOCALAPPDATA%\JET\logs` 內 jet-unhandled 開頭的檔案，記錄例外的類型、例外訊息與呼叫堆疊。<br>診斷日誌：僅 Debug 組態，寫入 `%LOCALAPPDATA%\JET\logs`。 |
| 指標、警示與健康檢查 | 不適用，JET 無常駐服務。作業的執行情形由操作監看寫入支援日誌：<br>執行中的作業：超過 30 秒時記錄，之後每 30 秒再記錄一次。<br>SQL statement：執行超過 5 秒、被取消或失敗時記錄。<br>匯出進度：換階段或換工作表時記錄；同一階段內最多每 10 秒記錄一次。<br>作業摘要：作業結束時記錄，限執行超過 5 秒、含執行超過 5 秒或失敗的 SQL statement，或曾要求取消的作業。 |
| 操作手冊與常見故障 | 報告檔無法寫入：關閉開啟該檔案的程式，確認檔案非唯讀後重新匯出。<br>案件已在另一個 JET 視窗開啟：於該視窗離開案件後再開啟。<br>案件由較新版本的 JET 建立或開啟過：改用較新版本的 JET 開啟。<br>篩選情境消失：重新匯入資料、變更欄位配對，或變更行事曆、科目配對等設定後，JET 清除已儲存的篩選情境。<br>畫面暫時沒有回應：JET 顯示提示；持續無法操作時，關閉後重新開啟 JET。<br>WebView2 處理程序中止：JET 顯示提示，取消進行中的作業並關閉視窗；重新開啟 JET 後確認最近一次作業的結果。 |
| 診斷 | 問題回報附錯誤訊息、操作步驟、複製的「訊息」區紀錄與輸出的支援日誌。開發人員依支援日誌的請求、錯誤碼與 SQL statement 的耗時找出發生問題的位置，再以合成測試資料重現。 |
| 復原 | 作業取消或失敗：執行中的寫入不保留，可重新執行該作業；規則見 Design Principle.docx 第 4.3 節。<br>結果過期：重新執行資料驗證、預篩選或篩選情境。<br>案件資料：以備份的案件資料夾復原，見第 9 節。 |
| 問題呈報（escalation） | 待填寫 |

## 15. Source Code Change Log

原始碼修訂紀錄：請參閱下列資料夾（路徑相對於上層文件資料夾）：.\程式修訂紀錄

JET 1.0.0.0 為 repository 的首次 commit，無先前的修訂。Git commit 紀錄見該資料夾的 git_code_changes_log.txt。

| 項目 | 內容 |
|---|---|
| 日期 | 待填寫 |
| 變更單 | 待填寫 |
| Commit | 首次 commit。 |
| 受影響模組 | 全部原始碼。 |
| 原因與實作摘要 | 首次發布，功能見第 2 節。 |
| 第三方元件變更 | 不適用，無先前版本；元件清冊見第 5.1 節。 |
| 測試佐證 | Code Review/Unit test Report.xlsx |
| 相容性影響 | 不適用，無先前版本。 |

## 16. Release Notes

版本發行說明：請參閱下列檔案（路徑相對於上層文件資料夾）：.\版本更新紀錄.xlsx

| 項目 | 內容 |
|---|---|
| 版本與日期 | 1.0.0.0，首次發布。日期：待填寫 |
| 新功能 | 六個步驟的功能，見第 2 節。 |
| 修正、第三方元件與安全更新、不相容變更、升級說明 | 不適用，1.0.0.0 為首次發布。 |
| 已知問題 | 見第 17 節。 |
| 發布檔與佐證 | 待填寫 |

## 17. Known Issues, Technical Debt & References

**限制**

下表為 1.0.0.0 已實作功能的限制。

| 事項 | 現況 |
|---|---|
| 版本號 | JET/JET.csproj 未設定版本屬性；assembly 版本 1.0.0.0 為 .NET SDK 的預設值。 |
| 未安裝 Access Database Engine | 匯入 Access 資料表時顯示來源檔讀取失敗，錯誤訊息未提示安裝 Access Database Engine。 |
| 案件資料夾名稱 | 案件資料夾的名稱與案件名稱不符時無法開啟，錯誤訊息未說明是名稱不符。 |
| 工作底稿的檔案數 | 每次匯出新增一個檔案，JET 不自動清除；案件的報告清單達 4,096 筆時無法再匯出。 |
| 日期提醒 | 確認 GL 欄位配對後列出的日期提醒未隨案件保存，重新開啟案件後不再列出。 |
| 金額輸入的檢查 | 進階條件篩選的金額輸入，前端與後端接受的寫法不同；篩選結果以後端的判斷為準。 |

**替代處理方式**

- 工作底稿的檔案數達上限時，於案件資料夾刪除不需要的舊工作底稿後再匯出。
- 日期提醒於重新確認 GL 欄位配對後再次列出。

**延後工作**

- 資料驗證報告的「完整性測試出現差異時之指引」工作表沿用舊版 JET 的範本內容，修訂內容尚未確定。
- NuGet 套件的授權聲明檔尚未納入發布資料夾。

預定完成的版本：待填寫。

**未解決風險**

下表的事項只有部分控制。

| 事項 | 現況 |
|---|---|
| 使用者全名的查詢 | 自動化測試未涵蓋向 Windows 查詢使用者全名的步驟。 |
| JSON 訊息的來源 | 後端未檢查 JSON 訊息的來源網址。現行控制：WebView2 僅允許導覽至本機頁面。 |
| 不分大小寫的比對 | 程式內的比對與 SQL 的比對使用不同的方法；英文字母的結果相同。 |

**詞彙表**

本文件使用的用語如下表。

| 用語 | 說明 |
|---|---|
| GL | 總帳。欄位配對確認後，指納入測試的標準化分錄。 |
| TB | 試算表。欄位配對確認後，指標準化的試算表。 |
| 欄位配對 | 使用者確認的 GL、TB 來源欄位對應。 |
| 資料驗證 | 「資料驗證與測試」步驟的完整性測試、借貸不平測試、資料缺漏輔助檢視與資料可靠性測試。 |
| 預篩選 | 「資料驗證與測試」步驟的標準化初步篩選程序。 |
| 資料可靠性測試 | 抽樣核對攸關資料元素是否可靠的測試。 |
| 篩選情境 | 使用者儲存的一組篩選條件，附名稱與動機。 |
| 工作底稿 | 「匯出底稿」步驟匯出的正式底稿。 |
| 建案者 | 建立案件的使用者；名稱於建立案件時記錄。 |
| 帳號短名 | Windows 登入帳號中，網域或電腦名稱之後的帳號名稱。 |
| GD 環境 | JET 正式部署使用的環境。 |

**相關規格與佐證參考資料**

相關文件為 Design Principle.docx、Code Review/Code review rule.docx 與 Code Review/Unit test rule.docx。外部參考資料如下：

- [ADSI WinNT 提供者](https://learn.microsoft.com/en-us/windows/win32/adsi/adsi-winnt-provider)
- [Apache ECharts 弱點公告](https://security.apache.org/projects/echarts/)
- [Microsoft 程式碼涵蓋率](https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-code-coverage)
- [NuGet 套件弱點查詢](https://learn.microsoft.com/dotnet/core/tools/dotnet-package-list)
- [SQLite 弱點公告](https://github.com/advisories/GHSA-2m69-gcr7-jv3q)
- [SQLite 授權](https://www.sqlite.org/copyright.html)
- [WebView2 SDK 授權](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4258.31/License)
- [WebView2 Runtime 的發布方式](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)
- [WebView2 安全建議](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security)
