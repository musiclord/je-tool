---
title: 技術文件
english: Technical Document
---
## 1. 簡介

JET 協助審計員匯入總帳與試算表，完成欄位配對、資料驗證及條件篩選，再產生 Excel 工作底稿。本文件供開發、維護、ITS 與 NITSO 人員了解程式的組成、使用環境及檢查方式。

正式版僅支援 SQLite，資料存放在本機案件資料夾。JET 是獨立 Windows 桌面程式；執行審計作業不需要連線 AI 服務或遠端資料庫。

## 2. 開發環境與工具

| 項目 | 用途與需求 |
|---|---|
| Windows x64 | 開發及執行桌面程式。 |
| .NET 10 SDK | 編譯 C#；global.json 指定 10.0.201，允許同主要版本的新功能版本。 |
| Visual Studio 或 VS Code | 閱讀、偵錯及發布程式。 |
| PowerShell 7.4 以上 | 執行專案驗證工具。 |
| Node.js | 執行前端測試；使用 JET 不需要安裝。 |
| Microsoft Excel | 檢查產出工作簿的實際操作及相容性。 |

以 Visual Studio 開啟 src/JET/JET.slnx，或以 VS Code 開啟專案目錄。建置使用：

```
pwsh -NoProfile -File tools/verify.ps1 -Command Build -Configuration Debug
```

日常偵錯可由開發工具啟動 JET。公司電腦的最低硬體需求與大量資料處理時間仍需實測。

## 3. 程式基礎與結構

C# 負責資料處理，HTML、CSS 與 JavaScript 負責畫面。WinForms 是 Windows 視窗，WebView2 在視窗內顯示隨程式提供的網頁。

畫面按鈕送出操作名稱與參數，例如 validate.run 要求執行資料驗證。Bridge 接收訊息，Application 安排處理，AuditCore 提供規則，Infrastructure 執行 SQLite 或檔案作業。完成後回傳摘要；詳細資料分頁讀取。

| 位置 | 內容 |
|---|---|
| Form1.cs、Program.cs | 啟動視窗及 WebView2。 |
| wwwroot | 畫面、字型與圖示。 |
| Bridge、Application | 接收操作與安排處理。 |
| AuditCore、Domain | 審計規則與基本資料定義。 |
| Infrastructure | SQLite、匯入及報告輸出。 |
| Templates | 八份隨程式交付的 Excel 範本。 |
| tests/JET.Tests | C# 單元、整合及架構測試。 |

以上位置除測試外，均位於 src/JET/JET。圖解見 Framework.docx；代表程式與例子見 Code and Explanation.xlsx。

## 4. 功能設計與開發

總帳與試算表先保存來源內容，再依審計員確認的欄位配對整理。金額以整數保留到小數第四位。SQLite 負責大量資料的篩選與彙總，畫面不載入整份總帳。

資料驗證包含存入筆數與合計核對、總帳與試算表逐科目比對、借貸不平及可靠性抽樣。抽樣預設 59 筆，可由審計員調整；設定與實際抽出筆數保存在該次驗證結果。樣本仍需由人核對附件。

前端不自行決定審計結論。新增功能應同時維護操作契約、測試及文件。SQL 查詢值使用參數，允許的欄位與排序由程式清單決定。

## 5. 錯誤處理與偵錯

可處理的錯誤以固定錯誤碼及中文訊息回傳，說明下一步。非預期錯誤不顯示內部例外原文；支援日誌記錄錯誤類型、系統錯誤碼及程式位置。

重新匯入後，上次確認的配對只用來帶回草稿；這份紀錄讀不出來時，開案照常，審計員重新選擇欄位。已存的驗證或預篩選摘要不是合法 JSON、缺少必要內容、型別不對，或含有讀不出來的文字時，結果標成過期，重新執行即可恢復。JSON 層數過深時也依相同規則處理。篩選情境的文字或結構讀不出來時，列為「條件內容無法讀取」，審計員仍可移除該情境。

預篩選的舊摘要只核對當時已提供區塊的必要欄位與型別，沒有提供的集中度檢視或預篩選結果分布不補造。畫面說明使用現行文字。匯出所需資訊不足時，請審計員重新執行預篩選。

驗證或預篩選結果的產生時間讀不出來時，開案仍可繼續，結果標成過期。重跑成功後，只在保存新結果的同一交易內清除時間無效的舊結果列，有效歷史結果保留。篩選情境的日期或編號、匯入批次的欄位損壞時，仍回報既有的資料庫損壞提示，不以猜測的值繼續。

案件資料夾名稱與設定檔的案件識別值不一致，或設定檔缺少有效的金額倍率時，案件清單列出設定檔無法讀取的案件，仍可刪除實際對應的資料夾。資料庫本身開不了時照常回報錯誤，不當成沒有紀錄。

篩選情境已儲存或報告已產生後，附帶的結果狀態或報告清單讀回失敗，操作仍回報成功並附提醒，請審計員重新開啟案件更新畫面，不必重做已完成的操作。

審計員可在「訊息」區輸出支援日誌。維護人員先依日誌定位，再以合成資料重現。案件內容與真實輸出不得放入公開測試、文件或外部 AI 服務。

## 6. 執行與部署

| 項目 | 說明 |
|---|---|
| 作業系統 | Windows 64 位元。公司核准環境需另行確認。 |
| .NET 與 SQLite | 隨發布內容提供，不需另設資料庫伺服器。 |
| WebView2 Runtime | 使用電腦已安裝的 Runtime；版本由公司環境管理。 |
| Access 匯入 | 需要相容的 64 位元 Access Database Engine。 |
| 權限 | 以一般使用者操作為目標，公司端需實測。 |
| 案件位置 | 預設 %USERPROFILE%\JET；每案保存設定、SQLite 資料庫、報告及日誌。 |

使用 FolderProfile 產生 Release、win-x64 的自含式發布內容。主程式為 JET.exe，另附 wwwroot、Templates 與設定檔；交付整個資料夾。移除程式時，案件資料另行保留或依公司規定處理。

案件正常關閉後，複製整個資料夾即可交接。更新及復原使用公司核准的發布檔。首次評估只記錄一個送審基準，尚不安排主要、次要或修補版本的分類。

## 7. 測試與交接

公開測試使用合成資料，包含單元、整合、架構與前端測試。C# 使用 xUnit v3 與 Microsoft Testing Platform。所有正式驗證均由 tools/verify.ps1 執行。

```
pwsh -NoProfile -File tools/verify.ps1 -Command Public -CollectCoverage
pwsh -NoProfile -File tools/verify.ps1 -Command Focused -Filter ConfigurableInfSamplingTests
```

CollectCoverage 另外產生 C# 的 Cobertura 涵蓋率檔。數字只表示執行過的程式行，不包含 JavaScript、原生 SQLite、WebView2、Excel 操作或人工審計判斷。

執行結果保存於 artifacts/harness/runs。測試紀錄表.xlsx 列出本次結果、涵蓋範圍與公司接手欄位。完整交接須保留 receipt.json、result.trx、test-summary.json 與 coverage.cobertura.xml。產生方式見 scripts/README.md。

## 8. 第三方元件與授權

目前應用程式直接引用 9 個 NuGet 套件，含間接引用共 23 個；另有 34 個僅供測試的套件。完整版本與條款列在「第三方元件.xlsx」，維護來源為 source/dependencies.csv。

| 元件 | 版本與授權 | 商業使用與交付 |
|---|---|---|
| Microsoft.Web.WebView2 SDK | 1.0.4258.31，Microsoft BSD 類條款 | 可依條款散布，保留著作權與免責聲明。Runtime 另適用 Microsoft 條款。 |
| Microsoft.Data.Sqlite | 10.0.13，MIT | 可商用，保留授權聲明。 |
| SQLitePCLRaw.bundle_e_sqlite3 | 3.0.5，Apache-2.0 | 可商用，保留授權及適用的 NOTICE。 |
| SQLite 原生套件 | 3.53.4，公眾領域 | 不需購買 SQLite 商用授權。送審時再核對實際載入版本。 |
| Open XML、ClosedXML、ExcelDataReader | 版本見清單，MIT | 保留授權聲明。 |
| Apache ECharts、Fluent 圖示 | Apache-2.0、MIT | 隨附授權；ECharts 內含片段另有 BSD 條款。 |
| Noto Sans TC、Instrument Sans | SIL Open Font License 1.1 | 可隨程式散布，保留授權，不單獨販售字型。 |

目前列出的產品元件沒有另購商用版的要求，仍須遵守各元件的散布條件。NuGet 授權聲明尚需納入正式發布包。Windows、Office 與 Access Database Engine 的使用權由公司確認，不能從開源套件授權推論。

## 9. 資訊安全

本次已查詢產品 NuGet 套件的公開漏洞資料，並限定檢視 SQL 輸入、WebView2 操作通道及報告輸出。完整結果、來源與待辦見「安全檢查摘要.docx」。

SQLitePCLRaw 舊原生套件 2.1.11 及以前受到 CVE-2025-6965 影響，公告分數為 CVSS 4.0 的 7.2，SQLite 修正版本為 3.50.2。目前組合為 bundle_e_sqlite3 3.0.5 與 SQLite 原生套件 3.53.4，已高於該漏洞的修正版本。這項版本比對不等於完整漏洞掃描。

security-audit 可協助檢視原始碼並整理問題；CodeQL、Dependabot、秘密掃描及推送保護仍需各自的工具與設定。公司 GitHub 的實際啟用情況、正式發布檔與部署環境尚未查核。

## 10. 參考資料

[Microsoft 測試涵蓋率](https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-code-coverage)；[NuGet 漏洞查詢](https://learn.microsoft.com/dotnet/core/tools/dotnet-package-list)；[SQLite 漏洞公告](https://github.com/advisories/GHSA-2m69-gcr7-jv3q)；[SQLite 授權](https://www.sqlite.org/copyright.html)；[WebView2 SDK 授權](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4258.31/License)；[NIST 安全開發框架](https://csrc.nist.gov/pubs/sp/800/218/final)。
