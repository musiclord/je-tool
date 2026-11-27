# 發布副本的同步工具

這個資料夾的工具把這個儲存庫的產品程式、送審文件與範例工作簿整理成「發布副本」，寫進和這個儲存庫並排的
`je-tool-clean` 資料夾。發布副本是交給 GD 測試環境與公司私有 GitHub repo 的程式庫。它不帶 AI 工具的設定、
驗證框架與開發紀錄，也不帶這台電腦的使用者名稱、帳號與信箱。

開發一律在這個儲存庫做。這個儲存庫的 `src/JET` 本身保持乾淨，內容和發布副本的 `JET/` 相同（裁定 2026-11-26-01）。
使用者說要提交推送這個儲存庫時，一併把發布副本同步到同一個版本，再提交推送發布副本（裁定 2026-11-26-03）。
不要直接改發布副本裡的檔案，下次同步會把它蓋掉。

## 檔案

| 檔案 | 用途 |
|:---|:---|
| `JetReleaseCopy.ps1` | 主程式。依序組成發布副本、掃描、建置與測試、鏡像到發布副本的資料夾，再依參數提交、加標籤與推送。 |
| `manifest.json` | 發布副本由哪些檔案組成、送審文件的記錄人員設定在哪裡、哪些舊版範本照舊，以及發布副本本機要忽略哪些檔。 |
| `root-files/` | 只放在發布副本的檔案：根目錄的說明檔、`.gitignore`、`.gitattributes`、VS Code 設定、打包腳本 `JET/Package.ps1`，以及 `docs/` 底下的說明與安全檢查資料。說明檔的相對連結以發布副本的資料夾結構為準。 |
| `hooks/` | 裝進發布副本 `.git/hooks` 的提交與推送前檢查。`release-copy-patterns.ps1` 是主程式和檢查共用的樣式。 |

## 怎麼執行

命令都在這個儲存庫的根目錄執行，需要 PowerShell 7.4 以上。建置與測試用的 .NET SDK 與 Node.js 和平常開發相同。

### 先看會帶哪些檔案

```powershell
pwsh -NoProfile -File tools/harness/release-copy/JetReleaseCopy.ps1 -Source WorkingTree -SkipTests
```

這個命令用工作樹組成發布副本並掃描，不建置、不測試，也不動發布副本的資料夾，大約半分鐘。
改了 `manifest.json`、`root-files/` 或產品程式，想先確認沒有不該帶的東西時用它。組成的結果放在 `artifacts/release-copy/copy`。

### 提交推送時

1. 照 `AGENTS.md` 的 Git 規則提交並推送這個儲存庫。工具只取這個儲存庫已提交的版本，還沒提交的修改不會進發布副本。
2. 把發布副本的提交訊息寫進 `local/` 底下的 UTF-8 文字檔，檔名自訂，例如 `local/release-message.txt`。
   訊息只寫產品的變更，不寫開發過程、AI 工具與驗證的事。
3. 執行下面的命令。要加版本標籤時，另加 `-Tag` 與版本號，例如 `-Tag v1.0.0.0`。

```powershell
pwsh -NoProfile -File tools/harness/release-copy/JetReleaseCopy.ps1 -Mirror -CommitMessageFile local/release-message.txt -Push
```

整個流程大約 4 分鐘。建置或測試沒通過時，工具停在那一步，發布副本的資料夾不會被改動。
推送前，發布副本本機的檢查會再看一次要推送的每個提交。

### 只想先看鏡像結果

```powershell
pwsh -NoProfile -File tools/harness/release-copy/JetReleaseCopy.ps1 -Source WorkingTree -Mirror
```

這個命令在建置與測試通過後，把工作樹的版本鏡像到發布副本，但不提交。可以先在發布副本用 `git status` 看會有哪些變動；
正式提交時，照上一節從已提交的版本再做一次。

## 參數

| 參數 | 說明 |
|:---|:---|
| `-Source` | 預設值 `Head` 取這個儲存庫目前提交的版本。`WorkingTree` 取工作樹，包括還沒提交的修改與還沒加入 Git 的新檔。 |
| `-Destination` | 發布副本的資料夾，預設是和這個儲存庫並排的 `je-tool-clean`。 |
| `-Mirror` | 建置與測試通過後，鏡像到發布副本的資料夾。 |
| `-SkipTests` | 只組成與掃描，不建置也不測試。不能和 `-Mirror` 一起用。 |
| `-CommitMessageFile` | 鏡像後在發布副本提交，提交訊息讀這個 UTF-8 檔。要搭配 `-Mirror`，而且 `-Source` 要是 `Head`。 |
| `-Tag` | 提交後加上附註標籤。要搭配 `-CommitMessageFile`。 |
| `-Push` | 把發布副本的 `main` 推送到它的 `origin`，有 `-Tag` 時一起推送標籤。要搭配 `-CommitMessageFile`。 |
| `-WaitSeconds` | 共享鎖正被別的驗證使用時，最多等幾秒。預設 0 是不等；上限和 `tools/verify.ps1` 相同，目前是 3600 秒。 |

## 每一步做什麼

工具開頭先檢查發布副本的資料夾、提交訊息與 `origin`。加 `-Mirror` 時，發布副本要是 Git 儲存庫的根目錄、
在 `main` 分支，而且和這個儲存庫不重疊。這些有問題時馬上停下，不必等建置與測試。

檢查通過後，工具拿 `tools/verify.ps1` 的共享鎖，到結束才放開。同步的建置與測試因此不會和別的驗證同時執行而互搶 CPU 與記憶體，
兩次同步也不會同時改寫工作資料夾。同步期間，別的需要共享鎖的驗證會被擋下，結束碼是 2，等同步結束再重跑。
鎖正被別的驗證使用時，工具立刻停下並寫出是哪個命令拿著鎖；加 `-WaitSeconds` 可以等它結束。

之後工具在 `artifacts/release-copy/` 底下工作，每次執行前清空，任何一步沒通過就停下並列出原因。

1. 組成。照 `manifest.json` 把檔案複製到 `artifacts/release-copy/copy`：`src/JET` 放到 `JET/`，`docs/SAT` 照原路徑，
   系統架構圖放到 `docs/architecture/system-architecture.png`，`data/` 只帶十份範例工作簿，`root-files/` 的內容放到根目錄。
   兩個來源要放到同一個位置時停下。
2. 拿掉送審活頁簿記下的存檔資料夾。Excel 每次存檔都會在活頁簿裡記下存檔時所在的資料夾，這段文字會帶出這台電腦的使用者名稱。
   工具拿掉 `docs/SAT/` 底下活頁簿的這一段，這是它唯一改寫檔案內容的地方。
3. 掃描。逐檔檢查下列項目，結果只寫類別與位置，不寫出找到的名稱。
   - 發布副本不帶的路徑：AI 工具的設定與指引檔，例如 `AGENTS.md`、`.claude/` 與 `.github/copilot-instructions.md`；
     這個儲存庫才有的資料夾，例如 `tools/`、`local/`、`docs/specs/` 與 `docs/history/`；建置與測試的輸出；資料庫與記錄檔。
     `.github` 底下只擋 AI 工具的指引檔，Dependabot 設定與 workflow 可以放。
   - 文字裡的 AI 工具名稱、驗證框架的名稱、開發紀錄的寫法，以及看不見的字元。開發紀錄的寫法指裁定編號、測試執行的編號與開發文件的檔名；
     看不見的字元指不換行空白、零寬字元與改變文字方向的控制字元等。
   - 這台電腦的識別資訊：Windows 使用者名稱、電腦名稱、使用者資料夾、Git 的姓名與信箱、GitHub 帳號與 Office 的使用者名稱。
     送審文件的記錄人員是使用者指定的值（裁定 2026-11-25-05），不算在內。
   - Office 檔的文件屬性。作者與上次儲存者要是空的；送審文件可以是 `tools/harness/sat-documents/source/document-properties.json`
     指定的值。公司、主管、超連結基底、自訂屬性、存檔資料夾、指向本機或網路磁碟的連結、印表機設定、外部活頁簿連結與註解都不能有。
   - PNG 圖檔裡的文字與拍攝資訊，以及 PDF 的作者屬性。
   - 說明檔的相對連結要指到發布副本裡的檔案。

   程式隨附的 Excel 範本與 `data/` 的範例工作簿來自舊版範本，舊版留下的作者、上次儲存者、公司欄位、印表機設定、
   外部連結與超連結都照舊（裁定 2026-11-26-05）。掃描用 `manifest.json` 記的檔案雜湊認出這些檔，只檢查文字與識別資訊。
   檔案改過之後雜湊對不上，掃描會要求重新確認；確認沒有問題後，更新 `manifest.json` 裡那個檔的雜湊。
4. 建置與測試。在組成的資料夾建置 `JET/JET.slnx`，再執行 C# 測試與前端測試。C# 測試的排除條件、順序種子與要移除的環境變數，
   和 `tools/verify.ps1 -Command Public` 讀同一份 `tools/harness/lanes.json`。移除環境變數是為了不讓測試讀到這台電腦實際的案件資料夾。
   完整輸出寫在 `artifacts/release-copy/logs/`。
5. 鏡像，加 `-Mirror` 時才做。Git 看得到、但這次發布副本沒有的檔會刪掉，新增與改過的檔會寫入；
   Git 忽略的檔與 `.git` 的內容不動。同時做三件事：
   - 把發布副本本機的提交身分，設成和這個儲存庫提交時用的姓名與信箱相同（裁定 2026-11-26-06）。
   - 在發布副本的 `.git/info/exclude` 補上 AI 工具的設定檔與 `/local/`，在發布副本用 AI 工具時產生的檔才不會被加入 Git。
   - 把 `hooks/` 的檢查裝進發布副本的 `.git/hooks`。
6. 提交、標籤與推送，加 `-CommitMessageFile` 時才做。工具只暫存這次發布副本的檔案，確認暫存的內容和發布副本完全相同才提交，
   並在 `local/release-copy/commits.md` 記下兩邊提交的對應。內容和上次提交相同時不提交，照樣加標籤與推送，
   所以推送失敗後可以直接重跑。標籤名稱已經用在別的提交上時停下。

## 發布副本本機的檢查

同步工具裝在發布副本 `.git/hooks` 的三個檢查，只在這台電腦的發布副本有效。clone 不會帶走 hook，GD 測試環境不需要它們。

| 檢查 | 什麼時候執行 | 檢查什麼 |
|:---|:---|:---|
| `commit-msg` | 寫好提交訊息後 | 不能有 AI 工具的署名，例如 `Co-Authored-By` 行、`Agent-Logs-Url` 行、`Generated with` 字樣與機器人表情符號；也不能有 AI 工具與驗證框架的名稱、開發紀錄的寫法與看不見的字元。 |
| `pre-commit` | 提交前 | 作者與提交者要是發布副本本機設定的姓名與信箱，暫存的檔案不能有發布副本不帶的路徑。 |
| `pre-push` | 推送前 | 要推送的每個提交照前兩項再檢查一次，附註標籤的標記者也要是本機設定的身分。 |

沒通過時，檢查列出原因並停止提交或推送，訊息不寫出姓名與信箱。改了 `hooks/` 之後要再鏡像一次，發布副本本機的檢查才會換成新版。

`release-copy-patterns.ps1` 裡 AI 工具名稱、驗證框架名稱與看不見字元的樣式，和開發專用測試
`tools/harness/development-repository-tests/Architecture/SourceCommentPolicyTests.cs` 的同類樣式相同，改一邊時另一邊一起改。
出現新的 AI 工具時，也要把它的設定檔加進樣式的「發布副本不帶的路徑」與 `manifest.json` 的 `localExclude`。

## GD 測試環境與公司 GitHub

GD 測試環境從個人 GitHub 上的發布副本取得程式，再推到公司的私有 GitHub repo（裁定 2026-11-26-02）。
GD 只轉送，不在發布副本上提交。下面的 `<個人帳號>`、`<發布副本的 repo>` 與 `<公司 repo 的網址>` 要換成實際的值。

第一次在 GD 取得程式並推到公司 repo：

```powershell
git clone https://github.com/<個人帳號>/<發布副本的 repo>.git je-tool-clean
cd je-tool-clean
git remote add company <公司 repo 的網址>
git push company main
git push company --tags
```

之後每次更新：

```powershell
git pull --ff-only origin main
git fetch --tags origin
git push company main
git push company --tags
```

`--ff-only` 只接受快轉。GD 上不小心多了自己的提交時，`git pull` 會停下，不會自動合併出新的提交。

### GD 連不上 GitHub 時

可以改用 Git 的 bundle 檔轉送。bundle 把提交與標籤打包成一個檔案，送進 GD 的方式照公司的規定。

1. 在這台電腦的發布副本資料夾建立 bundle：`git bundle create <bundle 檔的路徑> main --tags`。
2. 第一次在 GD 用 bundle 檔 clone：`git clone -b main <bundle 檔的路徑> je-tool-clean`。clone 時 Git 會檢查 bundle 是否完整。
   之後照上一節加上 `company` 並推送。
3. 之後每次更新，把新的 bundle 檔放到同一個路徑。先在 GD 的發布副本執行 `git bundle verify <bundle 檔的路徑>` 確認完整，
   再執行 `git pull --ff-only` 與 `git fetch --tags`，最後推到公司 repo。`git bundle verify` 要在 Git 儲存庫裡執行。

## 公司 repo 的 GitHub 安全功能

下面是 2026-11-26 讀到的 GitHub 官方說明。方案與功能常調整，開啟前以官方頁面為準。

- 私有 repo 的程式碼掃描要有 GitHub Code Security，秘密掃描要有 GitHub Secret Protection。這兩個產品要在 GitHub Team
  或 GitHub Enterprise 方案下購買。Dependabot alerts 與相依套件圖，所有方案都有。
- CodeQL 的預設設定（default setup）對 C# 用不需要建置的 `none` 模式。開啟的位置是 repo 的 Settings，
  側欄「Security and quality」底下的 Advanced Security。在「Code Security」區段「CodeQL analysis」那一列的 Set up 選 Default，
  再按 Enable CodeQL。前提是已經啟用 GitHub Code Security 與 GitHub Actions，開啟的步驟不需要在 repo 加檔案。
- `none` 模式不分析建置時才產生的程式碼。官方把它的準確度評為 Good，自己寫建置步驟的 `manual` 模式評為 Best。
- CodeQL CLI 2.24.0 起對 .NET 10 有基本支援，也支援 `.slnx` 方案檔，但 C# 14 新增的語法還沒有完整支援。
- 相依套件圖讀 NuGet 的 `.csproj` 時，列不出間接的相依套件。要列出時，可以在 repo 開啟自動相依提交（automatic dependency submission）；
  它用 GitHub Actions 執行，會用到 Actions 的使用分鐘數。支援表在 NuGet 列的檔案是 `.csproj`、`.vbproj`、`.nuspec`、
  `.vcxproj`、`.fsproj` 與 `packages.config`，沒有列出 `packages.lock.json`。

發布副本目前沒有放 GitHub 的設定檔。以後要放 `.github/dependabot.yml` 或 workflow 時，放進 `root-files/.github/`，掃描允許這些檔。

出處：

- [About GitHub Advanced Security](https://docs.github.com/en/get-started/learning-about-github/about-github-advanced-security)
- [GitHub Advanced Security 的計費](https://docs.github.com/en/billing/concepts/product-billing/github-advanced-security)
- [Configuring default setup for code scanning](https://docs.github.com/en/code-security/how-tos/find-and-fix-code-vulnerabilities/configure-code-scanning/configuring-default-setup-for-code-scanning)
- [CodeQL code scanning for compiled languages](https://docs.github.com/en/code-security/code-scanning/creating-an-advanced-setup-for-code-scanning/codeql-code-scanning-for-compiled-languages)
- [CodeQL CLI 2.24.0 的變更紀錄](https://codeql.github.com/docs/codeql-overview/codeql-changelog/codeql-cli-2.24.0/)
- [Dependency graph data](https://docs.github.com/en/code-security/concepts/supply-chain-security/dependency-graph-data)
- [Dependency graph supported package ecosystems](https://docs.github.com/en/code-security/reference/supply-chain-security/dependency-graph-supported-package-ecosystems)
- [git-bundle](https://git-scm.com/docs/git-bundle)
