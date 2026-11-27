# SAT 文件的產生工具

`docs/SAT/` 只放交付的文件。這個資料夾放產生那些文件的腳本，`source` 放文字、表單資料與圖表來源。
哪些內容可以寫、文件該用什麼角度寫，見 [SAT 文件的製作與交接](../../../.agents/harness/sat-documents.md)。
文件的架構、版面與樣式以公司 2026-11-23 的更新版範本為準：Technical Document 共 17 節，章節對照也在那份文件。

## 來源與成品

| 成品（在 docs/SAT 底下） | 來源 | 產生腳本 |
|---|---|---|
| Technical Document.docx、Design Principle.docx、Code Review/Code review rule.docx、Code Review/Unit test rule.docx | source 的同名 Markdown；版面與樣式寫在 Build-Documents.py | Build-Documents.py |
| Code Review/Code review.xlsx | Build-ReviewData.py 內的檢查項目、核心查核邏輯與固定值清單 | Build-ReviewData.py、Build-Workbooks.mjs |
| Code Review/Unit test Report.xlsx | 測試程式的結果檔；「測試案例」的測試項目寫在 Build-ReviewData.py，每一項對應 src/JET/tests/JET.Tests/Fixtures/audit-logic-cases.json 的案例，各案例的結果在 source/test-record.json 的 reviewCases | Build-ReviewData.py、Export-SatEvidence.py、Build-Workbooks.mjs |
| 版本更新紀錄.xlsx | source/release-notes.json | Build-Workbooks.mjs |
| Workflow.xlsx | source/workflow.json | Build-Workflow.py |
| Diagrams/ERD.txt、ERD.png | 產品程式的建表語句，分組與關聯在 source/diagrams/erd.json | Build-Diagrams.mjs |
| Diagrams/Structure Diagrams/Class Diagram.txt 與同名 .png | source/diagrams/uml.json | Build-Diagrams.mjs |
| Diagrams/Structure Diagrams/Component Diagram.txt 與同名 .png | source/diagrams/component.json | Build-Diagrams.mjs |
| Diagrams/Behavior diagrams/Activity Diagram.txt 與同名 .png | source/diagrams/activity.json | Build-Diagrams.mjs |
| Technical Document 第 6 節的系統架構圖 source/diagrams/framework.png | source/diagrams/framework.json | Build-Diagrams.mjs |
| 程式修訂紀錄/git_code_changes_log.txt | 直接編輯，見下方「提交紀錄」 | 無 |
| Github Security Reports | GitHub 匯出的檔案，見下方「GitHub 安全報告」 | 無 |

成品的文件屬性裡，作者與上次儲存者寫在 source/document-properties.json，是使用者指定的值；Build-Documents.py、Update-WordFields.ps1、Set-WorkbookPrint.py 與 Build-Workflow.py 都照它寫，活頁簿沒有文件屬性時由 Set-WorkbookPrint.py 補上（裁定 2026-11-25-05）。要換人時只改這個檔，再重新產生。

Technical Document 第 6、7、9 節照範本指向架構文件的資料夾，寫成 `..\architecture`，從 docs/SAT 看就是 docs/architecture；這是文件裡唯一指向 docs/SAT 以外的路徑。

元件清冊是 source/dependencies.csv。送審只提交 `src/JET`，文件不能指向這個資料夾，所以 Technical Document 第 5.1 節的元件表直接列出直接與間接引用的元件、版本與授權；測試專案另外用到的套件只寫數量。更新 NuGet 套件後同步修改這份清冊、source/package-summary.json 的數量與第 5 節的表格，並重新查詢漏洞、核對授權；查詢結果存成 source/security-check.json，不要沿用先前的「未列出漏洞」。

## 執行環境

Python 腳本需要 Python 3。Build-Documents.py 用到 python-docx，Set-WorkbookPrint.py 與 Build-Workflow.py 用到 lxml；其他 Python 腳本只用內建模組。
Build-Workbooks.mjs 需要 Node.js 與 @oai/artifact-tool，Build-Diagrams.mjs 需要 Node.js、@viz-js/viz 與 sharp。
使用 Codex 提供的執行環境時，Python、Node.js 與這些套件可從 `%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies` 取得。
Node.js 找不到套件時，先確認這個目錄裡的 `node/node_modules` 是否有該套件；套件已存在，可能只是目前腳本的搜尋位置沒有連到它。
套件不裝進產品專案。另建 Python 虛擬環境時也放在儲存庫外，避免被誤加進 Git。下方命令的 python 指的是這些環境中的 python.exe；PowerShell 用引號包住執行檔路徑時，前面要加 `&` 才會執行。

## 更新測試紀錄

報告的數字要和技術文件第 12 節寫的命令一致。那三行命令寫的是只有 `src/JET` 時的做法；這個儲存庫用 `tools/verify.ps1` 建置出來的測試程式
另外編進了 `tools/harness/development-repository-tests/` 的測試，不能拿來算報告的數字。所以測試紀錄在儲存庫外、只有 `src/JET` 的副本產生：
把 `src/JET` 複製到儲存庫外的新資料夾，在副本裡建置、執行後端測試並排除 `Scale` 標記，再用 Node.js 執行前端測試，
最後回到專案根目錄，把三份結果檔交給轉換工具。副本的路徑要以 `\src\JET` 結尾，轉換工具靠路徑裡的 `/src/JET/JET/` 辨認涵蓋率檔的原始檔。
先清掉 `tools/harness/lanes.json` 的 `environmentVariablesToRemove` 列出的環境變數，然後在專案根目錄執行：

```powershell
$work = '<儲存庫外的新資料夾，完整路徑>'
$copy = Join-Path $work 'src\JET'
$out = Join-Path $work 'results'
robocopy src\JET $copy /E /XD bin obj node_modules .vs /NFL /NDL /NJH /NJS /NP   # 結束碼小於 8 都是成功
Push-Location $copy
dotnet build JET.slnx -c Debug
tests\JET.Tests\bin\Debug\net10.0-windows\JET.Tests.exe --results-directory $out `
  --report-trx --report-trx-filename result.trx `
  --coverage --coverage-output-format cobertura --coverage-output "$out/jet.cobertura.xml" `
  --filter-not-trait "TestProfile=Scale"
node --test --test-concurrency=4 --test-reporter=junit "--test-reporter-destination=$out/frontend.junit.xml" tests/frontend/
Pop-Location
python tools/harness/sat-documents/Export-SatEvidence.py --trx "$out/result.trx" --coverage "$out/jet.cobertura.xml" `
  --frontend-junit "$out/frontend.junit.xml"
```

結果資料夾要給完整路徑；給相對路徑時，測試結果檔會寫到測試程式所在的資料夾底下。副本與結果檔用完可以刪除。

副本裡沒有專案根目錄的 `global.json`。電腦上另外裝了更新的 .NET SDK 時，副本會改用那一版建置，和報告「測試環境」寫的 SDK 版本不一致。
建置前在副本裡執行 `dotnet --version`，確認和專案根目錄查到的版本相同；不同時，把專案根目錄的 `global.json` 複製到 `$work`，也就是 `src` 的上一層，再建置。

轉換工具也接受 `Public -CollectCoverage` 的收據，但那份結果多出 `tools/harness/development-repository-tests/` 的測試，
不用來產生送審的報告。

轉換工具另外把六項核心查核邏輯每個測試案例的結果寫進 source/test-record.json 的 `reviewCases`，Unit test Report.xlsx 的「測試案例」讀它；「核心查核邏輯」工作表各項邏輯的測試案例數也包含這些案例，依案例編號只算進所屬的那一項邏輯。測試結果檔記的時間是 UTC，執行日期換算成這台電腦的日期。
案例編號取自測試顯示名稱裡的 `caseId`，例如 `AuditLogicCaseTests.Case_ProducesTheExpectedResult(caseId: "POP-01")`；結果檔裡沒有的案例記為未執行。
`reviewCases` 另外記下案例檔的 SHA-256；Build-Workbooks.mjs 發現它和 source/code-review.json 記的雜湊不同時，全部案例寫未執行。案例檔在 Build-ReviewData.py 執行後又改過時，轉換工具停止。
轉換工具從 source/code-review.json 讀案例的編號，所以改過案例之後，先執行 Build-ReviewData.py 再轉換。
案例檔分成 `datasets` 與 `cases` 兩段：一份測試資料寫一次，案例寫用哪一份資料、執行什麼與每一筆分錄的預期歸屬。測試資料與預期結果留在案例檔，不印進活頁簿。
「測試案例」上方四列是自動化測試、測試資料、執行日期與結果；表格有測試項目、輸入條件、預期結果、案例數與結果五欄。每項查核邏輯一列有底色的標題，底下每個測試項目一列。測試項目、輸入條件與預期結果的文字寫在 Build-ReviewData.py 的 `test_items`，用自己的話寫，不抄案例檔的案例名稱；每一項用分類或案例編號指出核對它的案例，腳本會確認每個案例恰好屬於一項。案例數是那一項的案例個數，結果在全部通過時寫通過，有失敗或未執行的案例時寫失敗或未執行。表頭在每一頁重複，工作表不凍結窗格。
只改了案例、不重新量測全部測試時，用 `--cases-trx` 單獨更新案例的結果，測試總數、測試環境與涵蓋率都不動：

```powershell
pwsh -NoProfile -File tools/verify.ps1 -Command Focused -Filter 'AuditLogicCaseTests'
python tools/harness/sat-documents/Export-SatEvidence.py --cases-trx artifacts/harness/runs/<這次執行的資料夾>/test-results/focused-selected/result.trx
```

若另已完成原生 Excel 驗證，可在轉換命令加上 `--excel-receipt <該次 receipt.json 路徑>`，結果只記在 source/test-record.json，不寫進報告。

轉換工具讀 TRX 的案例數與每個案例的結果、前端測試 JUnit 結果的案例數，再從 Cobertura 彙整程式行涵蓋率。
報告列 `src/JET/tests/JET.Tests` 與 `src/JET/tests/frontend` 的結果；JUnit 結果裡的檔案路徑不寫進紀錄。留在 `tools/tests` 的前端測試與 Excel 驗證
由 `tools/` 的工具執行，不在送審範圍，不寫進報告。它不執行測試，也不會把失敗改成通過。
結果檔放在儲存庫外，不放真實案件資料。

報告的「測試環境」是轉換工具在轉換當下查的：作業系統的名稱與位元、`dotnet --version` 與 `node --version` 的主要版本，以及測試專案檔引用的 xUnit v3；不記作業系統的建置編號與套件的完整版本。Build-Workbooks.mjs 把兩個主要版本寫成 .NET 10 SDK、Node.js 26 的樣子。涵蓋率工具的名稱寫在「涵蓋率量測方式」那一列。
所以轉換要和執行測試在同一台電腦、同一天做。報告的「執行命令」寫在 Export-SatEvidence.py 的 `COMMANDS`，要和 Technical Document 第 12 節的命令一致。

涵蓋率依來源檔與行號去重，只計本次列出的產品 C# 程式，排除產生碼。

複核規則要求測試在送交複核的那個版本上通過。兩份活頁簿以「軟體與版本」欄寫出受測與複核的版本，不另外記 commit ID。

報告的標題、測試資料、驗收條件、涵蓋率量測方式與模組名稱「Host（JET 資料夾最上層的原始檔）」寫在 Export-SatEvidence.py，轉換時寫進 source/test-record.json。只改文字、不重新執行測試時，腳本與 JSON 兩邊要改成一樣。

## 更新 Word、Excel 與圖表

1. 修改 source 的 Markdown 或資料來源。Build-ReviewData.py 會確認固定值清單的每一項仍在程式裡，也確認複核範圍列的測試類別仍在測試專案裡；找不到時腳本停止。它也讀測試專案的案例定義 Fixtures/audit-logic-cases.json，把每個案例的編號與所屬的查核邏輯，以及腳本裡寫的測試項目，寫進 source/code-review.json，並在複核範圍每一項邏輯的測試類別加上 AuditLogicCaseTests；案例定義只有這一份，測試類別 AuditLogicCaseTests 執行的也是它。案例的查核邏輯不在複核範圍、某一項邏輯沒有案例、案例編號重複、案例指到清單沒有的固定值、固定值清單有一項既沒有案例也不在「未列案例」的名單，或測試類別不在測試專案時，腳本停止。複核人員填過的 Code review.xlsx 要另存，重新產生會蓋掉填寫的內容。
2. 執行 Build-Documents.py，產生四份 Word 文件。版面與樣式照公司範本：A4、四邊邊界 2.54 公分、沒有頁首與頁尾，字型是 Office 佈景主題的 Aptos 與新細明體，標題、目錄與內文用範本的樣式；設定值寫在腳本開頭的說明與常數，範本檔不放進儲存庫。第一頁是封面：Markdown 開頭的 `title` 是封面標題，也是文件屬性的標題；有 `subtitle` 時加一行副標題；`toc: true` 時下面是置中粗體的「Table of Contents」與目錄，正文從第二頁開始。`body` 指定一般段落的樣式，Technical Document 與 Design Principle 用範本的內文2；段落開頭寫 `{內文1} ` 時改用內文1。獨立一行的 `**名稱**` 是段落的小標。段落裡寫 `<br>` 會在同一段裡換行。Technical Document 第 2 到第 5 節項目多的段落寫成小標加「項目、內容」兩欄的表格。`cover: plain` 標記公司提供的 Code review rule，它的文件屬性不寫主旨；`cover: rules` 標記自己寫的 Unit test rule，目錄標籤的版面和 Code review rule 相同。除了 Code review rule，其他文件的主旨是腳本開頭的 `PRODUCT` 與 `VERSION`。Markdown 裡連續的「1. 」編號行會變成一份 Word 自動編號的清單，數字由 Word 產生，段落文字不含數字；公司的 Code review rule 靠這個做法讓段落文字和原文相同。「- 」項目前面縮排兩個以上的空白時，是上一個項目底下的第二層項目；設計原則的 UX 用它把各點放在四個類別底下。圖片寫成 `![圖名](檔案)`，圖名同時是圖片的替代文字與圖下方的圖名。表格的儲存格裡寫 `<br>` 會換一行，每一行是儲存格裡的一個段落。再執行 Update-WordFields.ps1，在背景用 Word 更新目錄與功能變數、重新分頁並存檔。Word 存檔時會把這台電腦的 Office 使用者名稱寫進「上次儲存者」，所以腳本在 Word 結束後把文件屬性的作者與上次儲存者改回 source/document-properties.json 寫的值，再檢查每份文件的內部 XML；找到 Windows 使用者名稱、Office 使用者名稱、電腦名稱或本機路徑時報錯。最後轉成 PDF，逐頁檢查排版。要先看結果再覆蓋 docs/SAT 時，Build-Documents.py 加 `--output <資料夾>`，Update-WordFields.ps1 加 `-Root <資料夾>`。
3. 執行 Build-Workbooks.mjs，產生三份 Excel 文件，再執行 Set-WorkbookPrint.py 設定列印範圍與分頁；只要重新產生其中一份活頁簿時，兩支腳本都在最後加上它的名稱，例如 `"Code Review/Code review"`，其餘兩份不會被改寫；活頁簿多了沒有列印規則的工作表時，Set-WorkbookPrint.py 會停止。產生套件會用自己的產品名稱替活頁簿內嵌的佈景主題命名，Set-WorkbookPrint.py 把主題名稱改成 Office，並確認每個內部檔案都搜尋不到那幾個產品名稱，找到時停止。它也替沒有檢視設定的工作表補上 sheetViews，少了這一段時 Excel 會把列高讀成一半；把產生套件留下的預設字型改成 Microsoft JhengHei，並把欄寬換算回原設計，因為換字型後 Excel 會把欄寬放大約 6%；每張工作表加上置中的頁尾「第 N 頁，共 M 頁」。分頁點放在分組之間，合併儲存格的一組不會被切到兩頁；「測試案例」的一個測試項目不跨頁；不能再切開的一組列比一頁還高時，Set-WorkbookPrint.py 停止。用 Excel 核對下拉選單、公式及列印版面。
4. 執行 Build-Diagrams.mjs，產生實體關聯圖、類別圖、元件圖、活動圖與系統架構圖。資料表與欄位直接讀產品程式的建表語句；每個類別、元件、關係與活動節點都在來源的 JSON 記了可核對的檔案與字串，在程式裡找不到時腳本停止。元件圖另外掃描程式裡各元件之間的引用：每一組引用都要畫在圖上、寫在註記，或列在 component.json 的 undrawn 並說明原因，否則腳本停止。系統架構圖要先產生，Build-Documents.py 才嵌得到新圖。
5. 執行 Build-Workflow.py，產生 Workflow.xlsx。流程圖最上面一列寫各欄的負責角色，最左邊一欄寫階段。來源 source/workflow.json 記錄角色名稱、階段名稱、每個圖形的種類、文字與位置，以及每條連接線的起點、終點、折點與 Pass、Fail 標籤；座標的寫法在檔案開頭的「說明」。角色名稱只寫角色，不寫機構、部門或團隊的名稱；產生後解開 Workflow.xlsx，確認內部檔案搜尋不到這些名稱。`cells` 記錄公司範本裡不屬於角色名稱、階段名稱與連接線標籤的儲存格，可以只合併不寫字，也可以指定靠左或用標籤的字級；圖形文字裡的換行會寫成同一個段落裡的分行，和公司範本相同。流程圖是 Excel 的原生圖形與肘形接點，版面是橫向 A4 縮成一頁。圖形重疊、連接線的起點或終點不存在、連接線穿過圖形時，腳本停止，不產生檔案。產生後用 Excel 在背景開啟並轉成 PDF，確認沒有修復提示、箭頭方向正確。
6. 執行文件檢查。

```powershell
python tools/harness/sat-documents/Build-ReviewData.py
python tools/harness/sat-documents/Build-Documents.py
pwsh -NoProfile -File tools/harness/sat-documents/Update-WordFields.ps1
node tools/harness/sat-documents/Build-Workbooks.mjs <專案根目錄>
python tools/harness/sat-documents/Set-WorkbookPrint.py
node tools/harness/sat-documents/Build-Diagrams.mjs <專案根目錄>
python tools/harness/sat-documents/Build-Workflow.py
pwsh -NoProfile -File tools/verify.ps1 -Command Documentation
```

若套件由外部執行環境提供，將兩支 .mjs 腳本複製到本次暫存目錄，並在該處建立指向套件目錄的 node_modules 連接點，再執行腳本。參數仍填本專案根目錄。例如在專案根目錄執行：

```powershell
$repositoryRoot = (Resolve-Path '.').Path
$runtimeDependencies = Join-Path $env:USERPROFILE '.cache/codex-runtimes/codex-primary-runtime/dependencies'
$satTemporaryDirectory = Join-Path $env:TEMP ('jet-sat-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $satTemporaryDirectory | Out-Null
New-Item -ItemType Junction -Path (Join-Path $satTemporaryDirectory 'node_modules') -Target (Join-Path $runtimeDependencies 'node/node_modules') | Out-Null
Copy-Item -LiteralPath 'tools/harness/sat-documents/Build-Workbooks.mjs', 'tools/harness/sat-documents/Build-Diagrams.mjs' -Destination $satTemporaryDirectory
& (Join-Path $runtimeDependencies 'node/bin/node.exe') (Join-Path $satTemporaryDirectory 'Build-Workbooks.mjs') $repositoryRoot
& (Join-Path $runtimeDependencies 'node/bin/node.exe') (Join-Path $satTemporaryDirectory 'Build-Diagrams.mjs') $repositoryRoot
```

這個連接點只讓暫存腳本讀取既有套件，不變更套件目錄。

五張圖都由 Build-Diagrams.mjs 以 Graphviz 繪製。系統架構圖的來源是 source/diagrams/framework.json，嵌入 Technical Document 第 6 節，不另外放進 docs/SAT。其餘四張同時輸出 PlantUML 格式的文字定義；這台電腦沒有 PlantUML，文字定義沒有實際轉過圖，改語法時要對照 plantuml.com 的官方說明。

## 提交紀錄

對部門來說，JET 1.0.0.0 是儲存庫的第一筆 commit，所以 git_code_changes_log.txt 只記一筆，commit message 寫「首次提交：JET 1.0.0.0」，照 `git log` 的樣子寫 commit、Author、Date 三行。commit 與 Date 照公司更新版範本寫 `XXX` 與 `yyyy-MM-dd`；範本的 Author 是人員姓名，不抄進儲存庫，寫待填寫。
開發期間在個人儲存庫的 commit 不列進這份紀錄。首次 commit 完成後，在部門儲存庫執行下面的命令，用輸出取代檔案內容：

```powershell
git log --date=short --stat
```

## GitHub 安全報告

Github Security Reports 資料夾放 GitHub 匯出的檔案，不放自己寫的摘要。下表依 2026-11-10 讀到的 GitHub 官方文件整理；GitHub 沒有替單一儲存庫產出 PDF 或 Word 報告的功能。

| 功能 | 拿得到的檔案 | 取得方式 |
|---|---|---|
| 相依套件清冊 | SPDX 格式的 SBOM | 儲存庫的 Insights、Dependency graph、Export SBOM |
| 程式碼掃描（CodeQL） | SARIF 2.1.0 的分析結果；警示清單為 JSON | REST API 的 code-scanning analyses 與 alerts |
| 相依套件警示（Dependabot） | 警示清單，JSON | REST API 的 dependabot alerts |
| 機密掃描 | 警示清單，JSON；加上 hide_secret 參數避免輸出機密原文 | REST API 的 secret-scanning alerts |
| 組織的安全總覽 | CSV | 組織的 Security 頁面，Export CSV；不是單一儲存庫的功能 |

程式碼掃描、機密掃描與相依套件審查在公開儲存庫預設可用，私人儲存庫要另外購買授權；相依套件清冊與 Dependabot 警示各方案都有。部門的儲存庫啟用了哪些功能，要看到實際設定才知道，啟用前不要在文件裡寫成已掃描。

文件文字參考 [humanizer](https://github.com/blader/humanizer) 去除重複解釋與空泛說法，事實仍以專案程式、系統規格與官方來源為準。
