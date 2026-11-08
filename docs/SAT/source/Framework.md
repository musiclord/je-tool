---
title: 系統架構
english: Framework
---
## 1. 系統組成

JET 是本機執行的 Windows 桌面工具。審計員透過畫面匯入資料及設定篩選條件；C# 程式安排審計程序，SQLite 處理大量資料，結果輸出成 Excel。

![JET 系統組成](source/diagrams/framework.png)

圖中呈現主要職責。實際程式會透過既有介面合作，並非每次操作都依序呼叫全部模組。

## 2. 各部分負責什麼

| 部分 | 職責 |
|---|---|
| 操作畫面 | WinForms 承載 WebView2，顯示隨程式提供的網頁。畫面負責輸入與結果呈現。 |
| 操作處理 | Bridge 接收 JetApi 訊息，Application 安排程序及回應。 |
| 審計規則 | AuditCore 與 Domain 定義計算、納入範圍及資料意義。關鍵規則交由具審計知識的人複核。 |
| 資料與檔案處理 | Infrastructure 執行 SQLite 查詢、資料匯入及 Excel 輸出。 |
| 案件資料庫 | SQLite 保存來源資料、配對結果及計算結果。畫面只讀摘要與分頁資料。 |
| 報告與底稿 | 報告可重算；正式工作底稿每次匯出保留新版本，不覆寫舊檔。 |

## 3. 部署方式

正式版僅支援 SQLite，不需另設資料庫伺服器。發布內容包括 JET.exe、wwwroot、Templates 與設定檔，必須一起交付。電腦另需可用的 WebView2 Runtime；匯入 Access 時還需要相容的 Access Database Engine。

案件資料與程式分開保存。搬移案件前先正常關閉 JET，再複製整個案件資料夾。公司端仍需確認執行權限、電腦規格與封裝相容性。

## 4. 文件關係

Workflow.xlsx 說明操作及送審流程；Technical Document 說明開發環境、元件與授權；Code Review.xlsx 保留人工審查欄位。圖表來源保存在 source/diagrams，送審檔已嵌入圖片，可直接用 Office 閱讀。
