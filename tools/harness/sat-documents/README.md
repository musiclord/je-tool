# SAT 文件的產生工具

`docs/SAT/` 只放交付的文件。這個資料夾放產生那些文件的腳本，`source` 放文字、表單資料與圖表來源。
哪些內容可以寫、文件該用什麼角度寫，見 [SAT 文件的製作與交接](../../../.agents/harness/sat-documents.md)。

## 來源與成品

| 成品（在 docs/SAT 底下） | 來源 | 產生腳本 |
|---|---|---|
| Technical Document.docx、Design Principle.docx、Code Review/Code review rule.docx | source 的同名 Markdown | Build-Documents.py |
| Code Review/Code review.xlsx | Build-ReviewData.py 內的檢查項目、核心審計規則與固定值清單 | Build-ReviewData.py、Build-Workbooks.mjs |
| Code Review/Unit test Report.xlsx | 測試程式的結果檔 | Export-SatEvidence.py、Build-Workbooks.mjs |
| 版本更新紀錄.xlsx | source/release-notes.json | Build-Workbooks.mjs |
| Workflow.xlsx | source/workflow.json | Build-Workflow.py |
| Diagrams/ERD.txt、ERD.png | 產品程式的建表語句，分組與關聯在 source/diagrams/erd.json | Build-Diagrams.mjs |
| Diagrams/Structure Diagrams/Class Diagram.txt 與同名 .png | source/diagrams/uml.json | Build-Diagrams.mjs |
| Diagrams/Structure Diagrams/Component Diagram.txt 與同名 .png | source/diagrams/component.json | Build-Diagrams.mjs |
| Diagrams/Behavior diagrams/Activity Diagram.txt 與同名 .png | source/diagrams/activity.json | Build-Diagrams.mjs |
| Technical Document 第 6 節的系統組成圖 source/diagrams/framework.png | source/diagrams/framework.json | Build-Diagrams.mjs |
| 程式修訂紀錄/git_code_changes_log.txt | 直接編輯，見下方「提交紀錄」 | 無 |
| Github Security Reports | GitHub 匯出的檔案，見下方「GitHub 安全報告」 | 無 |

元件清冊是 source/dependencies.csv。送審只提交 `src/JET`，文件不能指向這個資料夾，所以 Technical Document 第 5 節直接列出直接引用的元件、版本與授權。更新 NuGet 套件後同步修改這份清冊、source/package-summary.json 的數量與第 5 節的表格，並重新查詢漏洞、核對授權；查詢結果存成 source/security-check.json，不要沿用先前的「未列出漏洞」。

## 執行環境

Python 腳本需要 Python 3。Build-Documents.py 用到 python-docx，Set-WorkbookPrint.py 與 Build-Workflow.py 用到 lxml；其他 Python 腳本只用內建模組。
Build-Workbooks.mjs 需要 Node.js 與 @oai/artifact-tool，Build-Diagrams.mjs 需要 Node.js、@viz-js/viz 與 sharp。
使用 Codex 提供的執行環境時，Python、Node.js 與這些套件可從 `%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies` 取得。
Node.js 找不到套件時，先確認這個目錄裡的 `node/node_modules` 是否有該套件；套件已存在，可能只是目前腳本的搜尋位置沒有連到它。
套件不裝進產品專案。另建 Python 虛擬環境時也放在儲存庫外，避免被誤加進 Git。下方命令的 python 指的是這些環境中的 python.exe；PowerShell 用引號包住執行檔路徑時，前面要加 `&` 才會執行。

## 更新測試紀錄

報告的數字要和技術文件第 14 節寫的命令一致，所以直接執行測試程式並排除三組標記，再用 Node.js 執行 `src/JET/tests/frontend` 的前端測試，
把兩份結果檔交給轉換工具。
先清掉 `tools/harness/lanes.json` 的 `environmentVariablesToRemove` 列出的環境變數，然後在專案根目錄執行：

```powershell
pwsh -NoProfile -File tools/verify.ps1 -Command Build -Configuration Debug
$out = Join-Path (Get-Location) 'artifacts/sat-test-record'
src\JET\tests\JET.Tests\bin\Debug\net10.0-windows\JET.Tests.exe --results-directory $out `
  --report-trx --report-trx-filename result.trx `
  --coverage --coverage-output-format cobertura --coverage-output "$out/jet.cobertura.xml" `
  --filter-not-trait "TestProfile=PrivateCase" "TestProfile=Scale" "TestProfile=DevelopmentRepository"
Remove-Item Env:JET_FRONTEND_TEST_WWWROOT -ErrorAction SilentlyContinue
node --test --test-concurrency=4 --test-reporter=junit "--test-reporter-destination=$out/frontend.junit.xml" src/JET/tests/frontend/
python tools/harness/sat-documents/Export-SatEvidence.py --trx "$out/result.trx" --coverage "$out/jet.cobertura.xml" `
  --frontend-junit "$out/frontend.junit.xml"
```

結果資料夾要給完整路徑；給相對路徑時，測試結果檔會寫到測試程式所在的資料夾底下。

第三組標記 `DevelopmentRepository` 是要讀 `src/JET` 以外檔案的測試，只有 `src/JET` 時執行不了，所以不列進報告；
開發時這些測試仍由 `Public` 執行。轉換工具也接受 `Public -CollectCoverage` 的收據，但那份結果多出這一組測試，
不用來產生送審的報告。

若另已完成原生 Excel 驗證，可在轉換命令加上 `--excel-receipt <該次 receipt.json 路徑>`，結果只記在 source/test-record.json，不寫進報告。

轉換工具讀 TRX 的案例數與每個案例的結果、前端測試 JUnit 結果的案例數，再從 Cobertura 彙整程式行涵蓋率。
報告列 `src/JET/tests/JET.Tests` 與 `src/JET/tests/frontend` 的結果；JUnit 結果裡的檔案路徑不寫進紀錄。留在 `tools/tests` 的前端測試與 Excel 驗證
由 `tools/` 的工具執行，不在送審範圍，不寫進報告。它不執行測試，也不會把失敗改成通過。
結果檔放在 Git 忽略的 artifacts，不放真實案件資料。

報告的「測試環境」是轉換工具在轉換當下查的：作業系統、`dotnet --version`、`node --version`，以及測試專案檔裡 xUnit 與涵蓋率工具的版本。
所以轉換要和執行測試在同一台電腦、同一天做。報告的「執行命令」寫在 Export-SatEvidence.py 的 `COMMANDS`，要和 Technical Document 第 14 節的命令一致。

涵蓋率依來源檔與行號去重，只計本次列出的產品 C# 程式，排除產生碼。

複核規則要求測試在提交複核的那個版本上通過。Unit test Report 的「提交識別碼（commit ID）」欄留給部門儲存庫的提交：首次提交完成後，在那個提交上重新執行測試、重新產生報告，再填入識別碼。直接執行測試程式時，source/test-record.json 不記提交識別碼。

## 更新 Word、Excel 與圖表

1. 修改 source 的 Markdown 或資料來源。Build-ReviewData.py 會確認固定值清單的每一項仍在程式裡，也確認複核範圍列的測試類別仍在測試專案裡；找不到時腳本停止。複核人員填過的 Code review.xlsx 要另存，重新產生會蓋掉填寫的內容。
2. 執行 Build-Documents.py，產生三份 Word 文件。再執行 Update-WordFields.ps1，在背景用 Word 更新目錄與功能變數、重新分頁並存檔。Word 存檔時會把這台電腦的 Office 使用者名稱寫進「上次儲存者」，所以腳本在 Word 結束後把文件屬性的作者與上次儲存者改成 JET，再檢查每份文件的內部 XML；找到 Windows 使用者名稱、Office 使用者名稱、電腦名稱或本機路徑時報錯。最後轉成 PDF，逐頁檢查排版。要先看結果再覆蓋 docs/SAT 時，Build-Documents.py 加 `--output <資料夾>`，Update-WordFields.ps1 加 `-Root <資料夾>`。
3. 執行 Build-Workbooks.mjs，產生三份 Excel 文件，再執行 Set-WorkbookPrint.py 設定列印範圍與分頁；活頁簿多了沒有列印規則的工作表時，Set-WorkbookPrint.py 會停止。產生套件會用自己的產品名稱替活頁簿內嵌的佈景主題命名，Set-WorkbookPrint.py 把主題名稱改成 Office，並確認每個內部檔案都搜尋不到那幾個產品名稱，找到時停止。它也替沒有檢視設定的工作表補上 sheetViews，少了這一段時 Excel 會把列高讀成一半；把產生套件留下的預設字型改成 Microsoft JhengHei，並把欄寬換算回原設計，因為換字型後 Excel 會把欄寬放大約 6%；每張工作表加上置中的頁尾「第 N 頁，共 M 頁」。用 Excel 核對下拉選單、公式及列印版面。
4. 執行 Build-Diagrams.mjs，產生實體關聯圖、類別圖、元件圖、活動圖與系統組成圖。資料表與欄位直接讀產品程式的建表語句；每個類別、元件、關係與活動節點都在來源的 JSON 記了可核對的檔案與字串，在程式裡找不到時腳本停止。元件圖另外掃描程式裡各元件之間的引用：每一組引用都要畫在圖上、寫在註記，或列在 component.json 的 undrawn 並說明原因，否則腳本停止。系統組成圖要先產生，Build-Documents.py 才嵌得到新圖。
5. 執行 Build-Workflow.py，產生 Workflow.xlsx。來源 source/workflow.json 記錄泳道、階段帶、每個圖形的種類、文字與位置，以及每條連接線的起點、終點、折點與 Pass、Fail 標籤；座標的寫法在檔案開頭的「說明」。流程圖是 Excel 的原生圖形與肘形接點，版面是橫向 A4 縮成一頁。圖形重疊、連接線的起點或終點不存在、連接線穿過圖形時，腳本停止，不產生檔案。產生後用 Excel 在背景開啟並轉成 PDF，確認沒有修復提示、箭頭方向正確。
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

五張圖都由 Build-Diagrams.mjs 以 Graphviz 繪製。系統組成圖的來源是 source/diagrams/framework.json，嵌入 Technical Document 第 6 節，不另外放進 docs/SAT。其餘四張同時輸出 PlantUML 格式的文字定義；這台電腦沒有 PlantUML，文字定義沒有實際轉過圖，改語法時要對照 plantuml.com 的官方說明。

## 提交紀錄

對部門來說，JET 1.0.0.0 是儲存庫的第一筆提交，所以 git_code_changes_log.txt 只記一筆「首次提交」，照 `git log` 的樣子寫 commit、Author、Date 三行，值先寫待填。
開發期間在個人儲存庫的提交不列進這份紀錄。首次提交完成後，在部門儲存庫執行下面的命令，用輸出取代檔案內容：

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
