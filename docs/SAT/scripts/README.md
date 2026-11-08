# SAT 文件維護

主要交付檔放在上一層，審查人員使用 Word 和 Excel 即可。source 保存文字、表單資料與圖表圖片；不直接把原始測試紀錄貼入 Word。

## 更新測試紀錄

在專案根目錄執行：

```powershell
pwsh -NoProfile -File tools/verify.ps1 -Command Public -CollectCoverage
python docs/SAT/scripts/Export-SatEvidence.py <本次 receipt.json 路徑>
```

若另已完成原生 Excel 驗證，可在轉換命令加上 `--excel-receipt <該次 receipt.json 路徑>`，一併列出工作簿及匯入格式的結果。

轉換工具核對收據、測試摘要與 TRX，再從 Cobertura 彙整程式行涵蓋率。它不執行測試，也不會把失敗改成通過。只接受公開合成測試；原始證據留在 artifacts/harness/runs，不放真實案件資料。

涵蓋率依來源檔與行號去重，只計本次列出的產品 C# 程式，排除產生碼。JavaScript、原生套件、桌面互動與審計判斷另行檢查。公司送審時，保留整個執行資料夾，另填公司環境、送審版本及接手結論。

## 更新 Word 與 Excel

1. 修改 source 的 Markdown 或資料來源。Build-ReviewData.py 會從現行程式重新擷取程式節錄；Code Review 的人工填寫結果應另存為已完成的審查紀錄，避免重新產生時被覆蓋。
2. 執行 Build-Documents.py，產生五份 Word 文件。用 Word 更新頁碼，轉 PDF 檢查每頁排版。
3. 執行 Build-Workbooks.mjs，產生五份 Excel 文件。它需要 Node.js 與 @oai/artifact-tool；使用 Codex 提供的套件環境，不安裝到產品專案。
4. 執行 Set-WorkbookPrint.py 設定列印範圍。用 Excel 核對圖表、下拉選單、公式及列印版面。
5. 執行文件檢查。

```powershell
python docs/SAT/scripts/Build-ReviewData.py
python docs/SAT/scripts/Build-Documents.py
node docs/SAT/scripts/Build-Workbooks.mjs <專案根目錄>
python docs/SAT/scripts/Set-WorkbookPrint.py
pwsh -NoProfile -File tools/verify.ps1 -Command Documentation
```

若套件由外部執行環境提供，可將 Build-Workbooks.mjs 複製到該次暫存工作目錄，在該處的 node_modules 連接到提供的套件目錄後執行，第二個參數仍填本專案根目錄。舊 Build-Workbooks.py 已停用。

## 圖表與安全檢查

Workflow 的三張圖和 Framework 圖由 Archify 產生，來源在 source/diagrams。Office 嵌入 PNG；HTML 與工具檢查紀錄只供製作時使用，不列為 SAT 閱讀需求。這些是本次需求及現行分工的概觀圖，沒有把未提交工作冒充為某個已提交版本。

安全檢查摘要列出實際執行範圍。更新 NuGet 套件後，重新查詢漏洞並核對授權；不要沿用先前的「未列出漏洞」。完整 security-audit 稽核依 docs/security/README.md 另外執行。

文件文字參考 [humanizer](https://github.com/blader/humanizer) 去除重複解釋與空泛說法，事實仍以專案程式、使用者裁定與官方來源為準。
