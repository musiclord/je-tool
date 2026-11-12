# 一般使用者權限與單一執行檔的 Gui 測試計畫

日期：2026-11-09。

狀態：已完成，使用者 2026-11-09 驗收通過（裁定 2026-11-09-07），同日移到歷史文件。程式、文件與驗證都已完成，`Package` 的封裝前檢查也一併修好。

<!-- jet-context:start -->
SQLite 上線前只能人工確認的五項，其中「發布出去的單一執行檔，用一般使用者帳號建立 SQLite 案件、匯入、重開」，
使用者 2026-11-09 要求寫進驗證框架。做法照使用者的選擇：所有 Gui 情境改用拿掉系統管理員權限的身分啟動 JET，
並記下 JET 確實沒有管理員權限；另加一個情境，把 Gui 測試版照正式版的設定打包成單一執行檔，走完建案、匯入、
關閉 JET、重新啟動、開啟同一個案件。使用者 2026-11-09 驗收通過，這份計畫已結案。
<!-- jet-context:end -->

## 目標

- `Gui` 路線的每個情境，都用拿掉系統管理員權限的身分啟動 JET。驅動程式讀回 JET 的權杖，確認沒有提高權限、
  Administrators 群組只用於拒絕或不存在、完整性等級不高於中，三項都成立才讓 JET 開始執行，並把結果寫進情境紀錄。
- 新增 Gui 情境 `single-file-relaunch`：把 Gui 測試版用正式版的單一執行檔設定打包，建立 SQLite 案件、匯入 GL 與 TB，
  確認原生元件解壓到情境自己的暫存資料夾，按「結束 JET」，再啟動同一個執行檔，從案件清單開啟同一個案件，確認匯入的資料還在。

## 不做的事

- 不另建 Windows 帳號，也不保存任何帳號密碼（裁定 2026-11-09-01）。
- 不改產品程式的行為與畫面。正式 Release 的打包設定、範本與發布內容都不變。
- 不代替公司電腦的確認。公司的群組原則、AppLocker、防毒軟體與漫遊設定檔，這台電腦測不到，仍由使用者在公司確認。
  其中漫遊設定檔，使用者後來裁定不另外處理（裁定 2026-11-09-08）。
- 正式 Release 的單一執行檔沒有測試接點，驗證框架無法自動操作它。新情境測的是 Gui 測試版，和正式版只差在讀取畫面與提供合成檔案的測試接點。

## 審計員會看到的改變

沒有。這份計畫只改驗證框架，以及 Gui 測試版的打包限制；正式版 JET 的畫面、檔案與操作都不變。

## 使用者原話

2026-11-09，使用者逐項回覆開發現況報告裡「SQLite 只能人工確認的五項」，引文是報告裡的項目，下一行是使用者的回覆：

```text
> 一百萬筆資料的量測
沒有問題

> 防毒軟體掃描的影響
沒有問題

> 公司電腦是否使用漫遊設定檔
不太理解你說的漫遊設定檔

> 用一般帳號建立、匯入、重開案件
請你於 harness 寫上相關開發與測試循環
```

「磁碟快滿時匯入」這一項沒有回覆。

同日 Claude 在視窗選項問了五題，題目、選項與使用者的回答逐字如下。前四題一起問；第五題是查程式時發現 Gui 測試用的組態不能發布後另外問的。

```text
【一般帳號】「用一般帳號建立、匯入、重開案件」要放進驗證框架。你的 Windows 帳號是系統管理員，但使用者帳戶控制讓一般啟動的程式只有一般使用者的權限，所以現有 Gui 測試其實已經用一般權限在跑，只是沒有檢查也沒有記錄。測試時要怎麼模擬一般帳號？
- 拿掉管理員權限 (Recommended)：驗證框架用目前的帳號，以拿掉系統管理員權限的身分啟動 JET，並檢查、記錄 JET 確實沒有管理員權限。不必另建帳號或輸入密碼，但用的是同一個使用者資料夾。
- 另建本機一般帳號：你自己建立一個本機一般帳號，驗證框架切換成那個帳號執行。比較接近公司實況，但測試要保存那個帳號的密碼；我不能代為建立帳號或輸入密碼。
回答：拿掉管理員權限 (Recommended)

【執行檔】這個測試要啟動哪一種 JET？正式發布的單一執行檔沒有測試用的接點，驗證框架無法自動操作它匯入檔案。
- 發布測試版單一執行檔 (Recommended)：用和正式版相同的單一執行檔設定，發布 Gui 測試用的版本，走完建案、匯入、重開。和正式版只差在多了讀取畫面與提供合成檔案的測試接點。
- 沿用現有 Gui 測試版：不另外發布，直接用現有 Gui 的建置結果。比較快，但測不到單一執行檔啟動時把原生元件解壓到暫存資料夾的部分。
回答：發布測試版單一執行檔 (Recommended)

【放在哪裡】這個測試要放在驗證框架的哪條路線？依 AGENTS.md，Gui 只在提交、交付或確認整合時才執行。
- 加進 Gui 路線 (Recommended)：Gui 多一個情境，提交前和 ReleaseCandidate 都會跑到；每次 Gui 會多花幾分鐘發布單一執行檔。
- 另開一個命令：新增獨立的命令，需要時才執行，不拉長 Gui 的時間；要不要放進 ReleaseCandidate 另外決定。
回答：加進 Gui 路線 (Recommended)

【其他情境】現有的 19 個 Gui 情境，也要一起改成用拿掉管理員權限的身分啟動 JET 嗎？
- 全部情境一起改 (Recommended)：不論終端機是否以系統管理員身分開啟，所有 Gui 情境都用一般權限跑，結果比較一致；如果 WebView2 在這種身分下出問題，會影響全部情境。
- 只改新情境：其他 18 個情境照現在的方式啟動 JET，只有新情境拿掉管理員權限。
回答：全部情境一起改 (Recommended)

【發布限制】你選了「發布測試版單一執行檔」。查程式時發現，Gui 測試用的組態在 JET.csproj 被刻意設成不能發布，避免把帶有測試接點與遠端偵錯的版本發出去。要照原方案，就得為驗證框架開一個例外。要怎麼做？
- 只開放驗證框架暫存打包 (Recommended)：只有驗證框架帶專用開關、輸出到它自己的暫存資料夾時，才能把測試版打包成單一執行檔，Gui 跑完就刪除。其他方式發布測試版照樣失敗。
- 改沿用現有 Gui 測試版：不動這道限制，新情境用現有的 Gui 建置結果；測不到單一執行檔啟動時把原生元件解壓到暫存資料夾的部分。
回答：只開放驗證框架暫存打包 (Recommended)
```

程式與文件做完後，Claude 同日再問一題，確認這份計畫新取的名稱。題目、選項與使用者的回答逐字如下：

```text
【新名稱】一般權限 Gui 計畫新取了一批驗證框架的名稱，包括 Gui 情境 single-file-relaunch、情境紀錄的 launchCount 與 identity、JET.csproj 的打包開關 JetAgentGuiOwnedBundle，以及 lanes.json 的 application 欄位。完整清單在計畫檔「待使用者確認的名稱」。要照用嗎？
- 照用 (Recommended)：計畫檔列的名稱全部照用，記一則裁定，計畫檔「待使用者確認的名稱」改成「使用者確認的名稱」。
- 我要先逐項看：名稱先維持待確認；我把每個名稱的用途與出現的位置整理出來，你逐項確認後再定案。
回答：照用 (Recommended)
```

第一個選項附了計畫檔清單的預覽，列的名稱和下面「使用者確認的名稱」一節的四項相同。

接著 Claude 發現上面的清單漏列了一些同一次新取的名稱，補登記後和驗證時發現的 `Package` 問題一起問。題目、選項與使用者的回答逐字如下：

```text
【補登記名稱】一般權限 Gui 計畫剛才確認的名稱清單，漏列了同一次新取的四十多個名稱，已補在計畫檔「使用者確認的名稱」底下的「補登記的名稱」，例如步驟名稱 bundle_reopen_case、錯誤碼 bundle_extraction_too_large、方法 GuiRunner.ConnectAsync、函式 Invoke-JetGuiBundleStep、JET.csproj 的檢查 RestrictAgentGuiOwnedBundle。要照用嗎？
- 照用 (Recommended)：補登記的名稱全部照用，記一則裁定，「補登記的名稱」改成已確認。
- 我要先逐項看：補登記的名稱先維持待確認；我把每個名稱的用途與出現的位置整理出來，你逐項確認後再定案。
回答：照用 (Recommended)

【Package】驗證時發現 Package 停在封裝前檢查：src/JET/JET 已沒有任何 appsettings*.json，而 JetPackageVerifier.ps1 的 New-PreflightManifest 把設定檔清單設為必填，收到空清單就停止。這是提交 61d4b43 刪掉 appsettings.json 後就有的問題，和這份計畫無關。要在這裡修嗎？
- 在這裡修 (Recommended)：讓這個參數接受空清單，在驗證框架的自身測試補一個沒有設定檔的情況，再跑 Package，順便確認 JET.csproj 的改動沒有影響正式打包。自身測試的這一段排在需要 Node.js 的那組之後，這台電腦跑不到，我會另寫暫存腳本執行同樣的檢查。
- 先不修：維持記在開發現況「已知但延後的事項」，這次不動。
回答：在這裡修 (Recommended)
```

第一題的第一個選項附了「補登記的名稱」的預覽，列的名稱和那一小節相同。

Claude 回報做完的結果後，在回覆末尾的方框問了一題，使用者按了按鈕。題目與回答逐字如下：

```text
Does this plan pass your acceptance?
回答：Accept
```

同一時間，使用者引用 Claude 對漫遊設定檔的說明，回覆如下：

```text
請維持案件運作在 C:\Users|<username>\JET 也就是 %LOCALAPPDATA%\JET 就好，不需要擔心其他事情
```

這兩個資料夾其實不同，Claude 再問一題確認。題目、選項與使用者的回答逐字如下：

```text
【案件位置】你寫「維持案件運作在 C:\Users\<username>\JET 也就是 %LOCALAPPDATA%\JET」，但這是兩個不同的資料夾：C:\Users\<username>\JET 就是 %USERPROFILE%\JET，JET 現在把案件放在這裡；%LOCALAPPDATA%\JET 是 C:\Users\<username>\AppData\Local\JET，現在只放 WebView2 資料與記錄檔。案件要放在哪一個？
- 維持 C:\Users\<username>\JET (Recommended)：不改程式，案件位置照裁定 2026-08-01-01。公司電腦如果使用漫遊設定檔，案件會跟著同步，照你說的不另外處理；五項人工確認裡的漫遊這一項也就結束。
- 改到 %LOCALAPPDATA%\JET：案件改放 C:\Users\<username>\AppData\Local\JET，不會隨漫遊設定檔同步。要改程式、文件與測試，也要決定已建立的案件怎麼處理；AppData 在檔案總管預設是隱藏的，審計員要從 JET 的「開啟案件資料夾」進去。
回答：維持 C:\Users\<username>\JET (Recommended)
```

## 使用者的決定

| 裁定 | 內容 |
|:---|:---|
| 2026-11-09-01 | 一般使用者帳號的確認寫進 `Gui` 路線，用目前的帳號、拿掉系統管理員權限的身分啟動 JET，並記錄 JET 沒有管理員權限 |
| 2026-11-09-02 | 現有的 Gui 情境全部改用拿掉系統管理員權限的身分啟動 JET |
| 2026-11-09-03 | 新情境啟動 Gui 測試版的單一執行檔；Gui 測試用的組態照舊不能發布，只有驗證框架帶專用開關、輸出到自己這次執行的暫存資料夾時才能打包，用完刪除 |
| 2026-11-09-05 | 計畫原本列的新名稱照用，見「使用者確認的名稱」；同一節「補登記的名稱」不在這次確認的範圍 |
| 2026-11-09-06 | 「補登記的名稱」照用 |
| 2026-11-09-07 | 這份計畫驗收通過 |
| 2026-11-09-08 | 公司電腦是否使用漫遊設定檔不另外處理，案件根目錄維持 `%USERPROFILE%\JET`；SQLite 只能人工確認的五項裡，漫遊這一項結束 |

修正 `Package` 封裝前檢查的回覆，屬於裁定總表說的「用完就結束的指示」，不另記裁定，做法見下面「`Package` 的封裝前檢查」。

## 做法

### 拿掉系統管理員權限的啟動

驅動程式 `OwnedGuiRun` 用 Windows SAFER 的一般使用者等級，從驅動程式目前的權杖算出一份拿掉系統管理員權限的權杖，
把完整性等級設成中，再用 `CreateProcessAsUserW` 以暫停狀態啟動 JET。JET 開始執行前，驅動程式讀回它的權杖，
三項都成立才繼續：沒有提高權限、Administrators 群組只用於拒絕或不存在、完整性等級不高於中。任何一項不成立，
驅動程式結束 JET，情境以「無法判斷產品正誤」結束，錯誤碼 `standard_user_token_not_applied`。
WebView2 的子程序沿用 JET 的權杖。每次啟動的結果寫進情境紀錄的 `process.identity`，驗證框架只在每次啟動都確認過時才算通過。

### Gui 測試版的單一執行檔

`JET.csproj` 原本禁止發布 `AgentGuiTest` 組態。改成：帶 `JetAgentGuiOwnedBundle=true`，而且輸出資料夾在
`artifacts/harness/runs/` 底下時，才允許用 `FolderProfile` 打包；其他情況照舊失敗。不帶開關時仍由 `RejectAgentGuiTestPublish` 擋下；
帶開關但輸出在別處，或不是單一執行檔時，由 `RestrictAgentGuiOwnedBundle` 在複製任何檔案前擋下。`Gui` 路線選到 `single-file-relaunch` 時，
建置完先把測試版打包到這次執行的 `scratch` 資料夾，命令結束時隨 `scratch` 一起刪除。

### 新情境 `single-file-relaunch`

1. 用打包出來的 `JET.exe` 啟動，建立 SQLite 案件，確認案件資料夾只有 `jet.db`。
2. 從合成夾具匯入兩個工作表的 GL 與一份 TB，確認列數。
3. 確認 `e_sqlite3.dll` 與 `WebView2Loader.dll` 已解壓到情境自己的 `bundle` 資料夾。驅動程式每次啟動都把
   `DOTNET_BUNDLE_EXTRACT_BASE_DIR` 設到這裡；一般建置不解壓，所以這一步也確認這個情境真的啟動了單一執行檔。
4. 按「結束 JET」，確認結束碼是 0。等 WebView2 放開使用者資料夾後，用同一個執行檔、同一個案件根目錄再啟動一次，
   同樣讀回權杖確認沒有系統管理員權限。
5. 從案件清單開啟同一個案件，確認 GL、TB 的列數與資料庫種類，再按「結束 JET」。

### `Package` 的封裝前檢查

`Package` 第一步由 `JetPackageVerifier.ps1` 檢查 `src/JET/JET` 底下的 `appsettings*.json` 沒有寫入憑證，並把檢查過的設定檔列進紀錄。
提交 `61d4b43` 刪掉 `appsettings.json` 後，這份清單是空的。寫紀錄的 `New-PreflightManifest` 把清單設為必填，
而 PowerShell 的必填參數預設不接受空的清單，所以封裝前檢查寫不出紀錄，`Package` 以驗證框架本身的錯誤結束，原因碼 `package_verifier_exception`。
2026-11-09 第一次失敗的那一次 `Package`，就停在第一步的封裝前檢查，原因碼和上面相同。

改法是讓這個參數接受空的清單；檢查本身不變，有設定檔時照樣找憑證。驗證框架的自身測試另加一個沒有設定檔的來源，
確認封裝前檢查通過，紀錄的設定檔清單是空的。同一支程式與驗證框架其他必填的清單參數都查過，沒有別處會正常收到空的清單。

## 步驟

| 步驟 | 內容 | 狀態 |
|:---|:---|:---|
| 1 | 驅動程式改用拿掉系統管理員權限的身分啟動 JET，讀回權杖並寫進情境紀錄 | 完成 |
| 2 | 驅動程式支援同一情境內關閉後重新啟動，新增 `single-file-relaunch` | 完成 |
| 3 | `JET.csproj` 只開放驗證框架的暫存打包；`Gui` 路線在需要時打包並把路徑交給新情境 | 完成 |
| 4 | `lanes.json`、驗證框架的判定與自身測試 | 完成 |
| 5 | `tools/README.md`、Gui 情境說明與 `.agents/harness/harness.md` | 完成 |
| 6 | 單跑新情境與 `startup-smoke`，再跑完整 `Gui`、驗證框架的自身測試與 `Documentation` | 完成，自身測試在使用者安裝 Node.js 後才完整跑完 |
| 7 | 使用者同日要求：修正 `Package` 的封裝前檢查，補驗證框架的自身測試，再跑 `Package` | 完成 |

做的過程中查到的事：驅動程式不是以系統管理員身分執行時，SAFER 算出的權杖和 Windows 平常給的一般權限相同，
Administrators 群組只用於拒絕，完整性等級是中。所以這台電腦上，改動前的 Gui 其實已經用一般權限在跑，差別是現在會檢查並記錄。

## 使用者確認的名稱

照名詞表的規定，新取的名稱要經使用者確認才算定案。下面四項使用者 2026-11-09 回覆照用，記為裁定 2026-11-09-05：

- Gui 情境 `single-file-relaunch`，情境紀錄的判定 `singleFileRelaunchVerified`。
- 情境紀錄 `process` 底下的 `launchCount` 與 `identity`，以及 `identity` 的 `standardUser`、`standardUserLaunches`、
  `elevated`、`administratorsGroup`、`integrityLevel`。
- 驅動程式的 `GuiApplicationIdentity`、`OwnedGuiRun.RestartApplicationAsync`、`BundleExtractionPath`、`LaunchCount`、
  `StandardUserLaunchCount`、`LastIdentity`，錯誤碼 `standard_user_token_failed`、`standard_user_token_not_applied`、
  `webview_user_data_still_in_use`、`bundle_native_library_missing`。
- `JET.csproj` 的打包開關 `JetAgentGuiOwnedBundle`，`lanes.json` 情境欄位 `application` 的值 `build` 與 `ownedBundle`，
  以及 `Gui` 路線的打包步驟 `gui-app-bundle`。

### 補登記的名稱

上面四項漏列了同一次新取的下列名稱。2026-11-09 補登記後，使用者同日回覆照用，記為裁定 2026-11-09-06：

- 情境紀錄的步驟名稱 `bundle_case_creation`、`bundle_import_gl_and_tb`、`bundle_native_libraries_extracted`、
  `bundle_exit_and_relaunch`、`bundle_reopen_case`。
- `identity.administratorsGroup` 的值 `deny_only`、`enabled`、`disabled`、`absent`，以及 `identity.integrityLevel` 的值
  `untrusted`、`low`、`medium`、`medium_plus`、`high`、`system`。
- 錯誤碼 `single_file_relaunch_failed`、`single_file_project_id_missing`、`single_file_case_database_missing`、
  `single_file_first_exit_code_nonzero`、`application_still_running_before_restart`、`application_token_unreadable`、
  `bundle_extraction_too_large`。
- 驅動程式的檔案 `GuiSingleFileRelaunchScenario.cs` 與它的方法 `ExecuteSingleFileRelaunchAsync`，`GuiRunner.ConnectAsync`、
  `GuiRunner.RecordLaunch` 與 `OwnedGuiRun.HasExtractedNativeLibraries`。
- `OwnedGuiRun` 內部的 `WebViewProfilePath`、`IsFileHeld`、`CreateStandardUserToken`、`SetMediumIntegrity`、
  `ReadApplicationIdentity`、`ReadAdministratorsGroup`、`ReadTokenInformation`、`IntegrityLevelName`、
  `MaximumBundleSearchEntries`、`WebViewReleaseTimeout`。呼叫 Windows 用的常數與結構，照 Windows API 的名稱改成 C# 的寫法，
  例如 `SidAndAttributes`、`TokenMandatoryLabel`；其中 `TokenGroupsHeader`、`MediumIntegritySid`、`AdministratorsSid` 是這次取的。
- C# 的 `GuiScenarioCatalog.SingleFileRelaunch`、`GuiProcessEvidence.StandardUserLaunches` 與
  `GuiAssertions.SingleFileRelaunchVerified`，分別對應上面已確認的 `single-file-relaunch`、`standardUserLaunches` 與
  `singleFileRelaunchVerified`。
- 驗證框架的函式 `Invoke-JetGuiBundleStep` 與它的參數 `-BundleDirectory`、這次執行的暫存資料夾 `gui-bundle`，
  以及 `JET.csproj` 擋下發布的檢查 `RestrictAgentGuiOwnedBundle`。

## 驗證

- 完整 `Gui` 通過：20 個情境共啟動 JET 21 次，每次都確認沒有提高權限、Administrators 群組只用於拒絕、完整性等級是中。
  `single-file-relaunch` 啟動兩次，原生元件解壓到情境自己的資料夾，重新啟動後開啟同一個案件，GL 4 列與 TB 2 列都在；
  打包出來的單一執行檔隨這次執行的 `scratch` 一起刪除。
- 打包限制：不帶開關發布測試版，以及帶開關但輸出到 `artifacts/harness/runs/` 以外，都在複製任何檔案前失敗。
- `lanes.json` 的 `application` 寫成 `build`、`ownedBundle` 以外的值時，驗證框架讀設定就會擋下，用暫存副本確認過。
- 正式 Release 照 `FolderProfile` 直接發布到暫存資料夾，產出單一 `JET.exe` 與 `Templates`、`wwwroot`，確認後刪除。
  這是診斷用的發布，不是 `Package` 路線。
- `Documentation` 通過。
- 驗證框架的自身測試 `tools/tests/verify-contract.tests.ps1` 第一次執行到第 10 組 `FrontendMapping` 時，因為這台電腦沒有安裝 Node.js 而停止。
  使用者同日安裝 Node.js 26.13.0 後重跑，36 組共 541 項檢查全部通過，包括新加的 Gui 檢查與「沒有設定檔」的封裝前檢查。
- `Package` 原本停在第一步的封裝前檢查，原因見上面「`Package` 的封裝前檢查」。修正後先用暫存腳本對四個來源直接執行封裝前檢查：
  寫有合成密碼的設定檔照樣失敗，原因碼 `tracked_credential_present`；空白密碼的設定檔、沒有設定檔的空資料夾與 `src/JET/JET` 都通過，
  後兩者紀錄的設定檔清單是空的。接著完整的 `Package` 通過：建置、110 項範本與報告測試、發布，發布結果的 77 個檔案都和預期相同。
- 安裝 Node.js 後，完整 `Public` 通過：4,782 項 .NET 測試、1,003 項前端行為測試與 12 項前端預覽測試都通過，沒有略過的測試。
- 沒有執行 `Excel`、`PrivateCase` 與 `ReleaseCandidate`。

## 下一步

無。使用者 2026-11-09 驗收通過，這份計畫已結案。
