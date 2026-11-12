# TB 多檔匯入計畫

日期：2026-11-09。

狀態：已完成，使用者 2026-11-10 驗收，同日移到歷史文件（裁定 2026-11-10-05）。TB 的畫面說明、欄名不同時的錯誤訊息、測試與文件都已改完，驗證通過。使用者驗收時要 TB 的說明改成和 GL 相同，也已改完。

<!-- jet-context:start -->
使用者要 TB 像 GL 一樣一次選多個檔案。查過程式，TB 從 2026-06-11 起就和 GL 共用同一條多檔路徑，只是畫面說明沒寫。
使用者選定欄名相同的檔案往下疊加、同一科目照現行加總、共用一份欄位配對，期初與期末分檔仍不做（裁定 2026-11-09-19）。
這一輪沒有改匯入邏輯，只改了 TB 的畫面說明、補了 TB 多檔的測試，並更正文件裡「只接受單一 TB 檔」的寫法。
欄名不同時的錯誤訊息，照使用者的選擇改成追加與一次匯入多個檔案共用同一句（裁定 2026-11-09-22）。
審計員試用後把期初與期末兩個 TB 檔一起匯入，使用者決定保留多檔（裁定 2026-11-09-23）。
使用者驗收時要 TB 的三處說明改成和 GL 同一套句子，不提期初與期末（裁定 2026-11-09-24）。
已改完並通過驗證，待使用者驗收。
<!-- jet-context:end -->

## 使用者原話

2026-11-09，由首次 SAT 文件製作計畫的 session 轉交，原話照錄：

> 再幫我處理一個改動，我需要讓匯入TB可以做到像匯入GL那樣選取多個檔案，並且盡量確保不要額外複雜化他們的邏輯和代碼

同日使用者在首次 SAT 文件製作計畫的 session 另外說明多選匯入的方向，由那個 session 逐字轉達：

> 多選檔案匯入應該是垂直而非水平，因為我沒有要讓 `je-tool` 處理到額外的欄位 join 邏輯，所以應該維持相同資料綱要的情況去一次匯入多個檔案

這段話和下面第一題選的做法一致，一併記在裁定 2026-11-09-19。

同日審計員試用 TB 多檔匯入後回了意見。使用者在首次 SAT 文件製作計畫的 session 要求照這段意見修正，由那個 session 逐字轉達。使用者原話：

> 在你作業的途中得到以下反饋，請基於該反饋去修正:

審計員意見原文：

> 審計員測試後回傳的意見: 在匯入資料這邊，TB可以匯入多個檔案，但說明是說不會自動配成期初與期末的期間變動，想詢問一下這邊既然不協助幫忙Join兩期的TB 為什麼會需要可以匯入多個檔案?還是這邊的匯入多個檔案是有什麼其他作用?因為就我自己是沒有遇過這種需要垂直合併TB資料的情況，只是因為我在測試的時候看到TB可以匯多個檔案，讓我以為他有像我們在idea處理TB的時候有JOIN的這個功能，雖然工具上有寫不幫忙合併期初期末變動，但其實我腦補了! 我想說可能匯入之後還有可以給我們操作合併的地方，所以我一開始就分別把期初TB和期末TB兩個檔案匯入，後面才發現不能這樣，在一開始的時候就是要匯好完整的，可能這邊的文字敘述可以再更明確一點

這段意見記在裁定 2026-11-09-23。

同日使用者驗收，在首次 SAT 文件製作計畫的 session 要求改短 TB 卡片的說明。原話：

> 請繼續修正一些我驗收時發現要改進的地方:
>
> * 請修飾，這的文字註釋太長了，先前跟你說過關於欄位join的功能不考慮進去，不代表你要在成品中暗示到該事項，請避免你擅自把一些開發事項或動機納入到成品，尤其是你可能會過度解釋或過度使用文字來提示的部分: `尚未匯入。有期初與期末金額時，兩者要先放在同一個檔案的同一列；JET 不會把期初與期末兩個試算表檔案接起來。一次選多個檔案，只會把同一查核期間、欄位相同的檔案往下疊成一份 TB，例如同一期分成幾個檔案匯出的試算表。`

這段話記在裁定 2026-11-09-24。

## 使用者的決定

2026-11-09 在對話視窗選答一次，共四題。題目照錄，選項只錄使用者選的那一個與它的說明。四題合為裁定 2026-11-09-19。

- 「查過程式：TB 其實已經和 GL 共用同一條多檔路徑（選檔對話框可以多選，欄名相同的檔案往下疊成一份 TB，也能按「加入」追加），只是 TB 的畫面說明沒寫可以合併多個檔案。你要的「像 GL 那樣選多個檔案」是哪一種？」＝欄名相同往下疊加 (Recommended)。選項說明：「和 GL 一樣，把多個欄名相同的 TB 檔的列合併成一份 TB。匯入邏輯不改，只把 TB 的畫面說明改成和 GL 一樣寫出可以多選、合併，並補 TB 多檔的測試。」
- 「多個 TB 檔案出現同一個科目編號時（例如各分公司各一份 TB），要怎麼處理？目前的做法是 TB 每一列都保留，完整性比對時把同一科目的變動金額加總，再和 GL 比。」＝照現行加總不提醒 (Recommended)。選項說明：「維持目前的計算，不改程式邏輯。」
- 「多個 TB 檔案的欄位配對要怎麼做？」＝共用一份配對 (Recommended)。選項說明：「和 GL 一樣，所有檔案的欄名必須相同，第三步只配對一次；欄名不同時，匯入會說明差在哪幾欄，請先在 Excel 對齊。」
- 「「期初與期末試算表分成兩個檔案匯入」先前裁定不做（裁定 2026-10-01-01；2026-10-06 的 K13 也維持不做）。這次改動之後要維持這個裁定嗎？」＝維持不做 (Recommended)。選項說明：「多檔只合併同一查核期間、欄名相同的 TB；期初、期末分檔仍列在「已裁定不做的事」，畫面照舊說明不會自動配成期間變動。」

同日改完畫面說明與測試後再選答一題，記為裁定 2026-11-09-20；其中兩句畫面說明同日依審計員意見改寫，見裁定 2026-11-09-23：

- 「TB 多檔這次新取了兩個測試名稱，也改了兩句 TB 畫面說明，要照用嗎？測試名稱：後端 `TbImportBranchTests.TwoSourcesInOneImport_StackIntoOneTb_AndTheSharedAccountIsSummedForComparison`；前端「GL and TB both send several files picked at once in one import and can add more files later」。畫面說明：TB 還沒匯入時寫「尚未匯入。可合併同一查核期間、欄位相同的檔案、工作表或資料表。不會自動將期初與期末兩份檔案配成期間變動。」；選檔後寫「建立 TB：選擇一個或多個同一查核期間的試算表來源檔，支援 Excel、CSV、文字檔及 Access，可合併工作表或資料表。不會自動配成期初與期末的期間變動。」」＝照用 (Recommended)。選項說明：「兩個測試名稱與兩句說明都照用；名詞表「PBC-TB」列的待確認標記改成已確認，另記一則裁定。」

收尾時又問欄名不同時的錯誤訊息要不要一起改，使用者回覆「一起改」，接著在對話視窗選答兩題。三題合為裁定 2026-11-09-22：

- 「一次匯入多個檔案、欄名不同時的錯誤訊息，要在這裡一起改嗎？」＝一起改。
- 「一次匯入多個檔案時，如果某個檔案的欄名和第一個檔案不同，錯誤訊息要怎麼寫？現在寫的是「…與已匯入的資料不一致…請改用取代匯入，或先在 Excel 把兩份檔案的欄位對齊。」，可是這時審計員本來就在做取代匯入。追加匯入時的句子照舊不改。」＝兩種情況共用一句。選項說明：「追加和一次匯入多個檔案都寫「…與其他來源不一致（…）。本次未匯入，先前匯入的資料不受影響。請先在 Excel 把欄位對齊後再匯入。」。程式最簡單，但追加時不再提示可以改用取代匯入，既有測試的斷言也要改。」
- 「這次要新取幾個名稱，要照用嗎？名詞表新增一列概念「欄位不一致的來源」；`LocalImportRepository.EnsureColumnSetsMatch` 加一個參數 `againstFirstSource`；新增兩則測試：`TbImportBranchTests.TwoSourcesInOneImport_WithDifferentColumns_AreRejected_AndWriteNothing`（一次匯入兩個 TB 檔、欄名不同），以及 `SqliteImportAppendTests.ReplaceBatch_PlaceholderWithDataOnlyInSecondSource_FailsAfterStreamingAndKeepsTheOldBatch`（沒有欄名的欄只有第二個來源有資料，要讀完資料才發現不同）。如果上一題選「兩種情況共用一句」，就不加這個參數。」＝照用 (Recommended)。選項說明：「上面的名稱都照用，兩則測試都加。」上一題選了共用一句，所以參數 `againstFirstSource` 沒有加。

同日依審計員意見再選答三題，合為裁定 2026-11-09-23：

- 「審計員把期初 TB 和期末 TB 當成兩個檔案一起匯入，以為 JET 會依科目接起來，也問 TB 為什麼需要多檔。查到的出處只有 2026-06-11 的設計：一個 GL 或 TB 資料集可以由多份檔案，或同一活頁簿的多張工作表合併而成，沒有寫 TB 的具體用途；2026-07-31 也已定期初與期末要在匯入前先合成一份。TB 要保留一次選多個檔案與「加入」嗎？」＝保留，改清楚說明 (Recommended)。選項說明：「照裁定 2026-11-09-19 保留往下疊加，GL 與 TB 仍共用同一段程式。TB 的三處說明改成先寫期初與期末要放在同一個檔案，再寫多檔只會往下疊、不會接起來。」
- 「TB 還沒匯入時，卡片說明改成下面這句，可以嗎？「尚未匯入。有期初與期末金額時，兩者要先放在同一個檔案的同一列；JET 不會把期初與期末兩個試算表檔案接起來。一次選多個檔案，只會把同一查核期間、欄位相同的檔案往下疊成一份 TB，例如同一期分成幾個檔案匯出的試算表。」現在寫的是「尚未匯入。可合併同一查核期間、欄位相同的檔案、工作表或資料表。不會自動將期初與期末兩份檔案配成期間變動。」上一題選 TB 只能選一個檔案時，只留第一句。」＝照用 (Recommended)。選項說明：「卡片照上面這句改。GL 的卡片照舊。」
- 「選檔時的兩句 TB 說明改成這樣，可以嗎？建立 TB 時改成「建立 TB：選擇同一查核期間的試算表來源檔，支援 Excel、CSV、文字檔及 Access。選多個檔案、工作表或資料表時，欄位相同的會往下疊成一份 TB，不會把期初與期末兩個試算表檔案接起來；期初與期末金額要先放在同一個檔案。」（現在是「…可合併工作表或資料表。不會自動配成期初與期末的期間變動。」）按「加入」時，TB 那一句由「請只追加同一查核期間的試算表資料，不會自動配成期初與期末的期間變動。」改成「請只加入同一查核期間的試算表資料，JET 不會依科目把新檔和現有資料接起來。」」＝兩句都照用 (Recommended)。選項說明：「建立 TB 與按「加入」時的說明都照上面改。GL 的說明照舊。」

## 驗收後把 TB 說明改成和 GL 相同

使用者引用的是還沒匯入時的卡片。建立 TB 時的說明也寫了期初、期末與接起來，按「加入」時寫了同一查核期間與接起來，所以三處一起問。
2026-11-09 在對話視窗選答兩題，合為裁定 2026-11-09-24：

- 「TB 的三處說明（還沒匯入時的卡片、建立 TB 選檔時、按「加入」時）都拿掉期初與期末、接起來的提示，要改成哪一套？」＝和 GL 同一套說法 (Recommended)。選項說明：「TB 三處直接用 GL 現在的句子，不寫期初期末，也不寫同一查核期間；按「加入」時 TB 不另加句子。」
- 複審時發現一則前端測試的名稱和改過的斷言相反，再問：「前端測試「TB card and both import modes explain the same-period boundary positively」改完後，檢查的是 TB 的卡片、建立與「加入」說明都不提查核期間與期初期末，名稱已經和內容相反。要改名嗎？」＝改名 (Recommended)。選項說明：「改成「TB card and both import modes use the same wording as GL」，記進裁定 2026-11-09-24；斷言不變。」

改完後 GL 與 TB 的說明只差在寫 GL 或 TB，例如「建立 GL」與「建立 TB」、「這份 GL」與「這份 TB」，見「審計員在哪一步做什麼、看到什麼」。

## 查到的現況

- 第二步的 GL 與 TB 用同一段前端程式：`src/JET/JET/wwwroot/js/steps/import-step.js` 的 `pickSources` 對兩者都打開可多選的檔案對話框，
  逐檔檢視後列在待匯入清單；`confirmWizard` 把勾選的來源放進 `sources`，以一個匯入 action 送出。
- 後端的 `import.gl.fromFile` 與 `import.tb.fromFile` 共用 `ImportFromFileHandler`，沒有限制 TB 只能一個來源。
  一批裡的每個來源欄名集合要相同，不同時以 `column_mismatch` 拒絕，訊息寫出多出與缺少的欄。
- 開發日誌 2026-06-11 記載多來源附加匯入、匯入前檔案檢視與多檔選取同日完成。`TbImportBranchTests` 已有 TB 一次兩個來源的測試。
- 不同的只有畫面說明：GL 寫「可合併欄位相同的檔案、工作表或資料表」，TB 只寫「請提供同一查核期間的試算表」，看不出可以多選。
  `docs/development-status.md`「已裁定不做的事」第一列也寫成「目前只接受單一 TB 檔」，和程式不符。
- TB 資料表不限制科目編號唯一。GL 與 TB 逐科目比對先把 TB 依科目加總變動金額，再和 GL 比，
  定義在 `src/JET/JET/AuditCore/ValidationProcedures.cs` 的 `CompletenessDiffCteFor`。

## 目標與範圍

這一輪要交付：

- 第二步的 TB 卡片與選檔說明寫出可以一次選多個欄位相同的檔案合併，和 GL 的說法一致；照舊寫不會自動把期初與期末兩份檔案配成期間變動。這後半句後來由裁定 2026-11-09-23 改寫，再由裁定 2026-11-09-24 拿掉。
- 前端測試確認 GL 與 TB 都能一次選兩個檔案、只送一個匯入 action，之後按「加入」再追加；後端測試用兩份合成 TB 確認同一科目照現行加總。
- 現行文件改成和程式一致：前端說明、系統規格、開發現況、裁定總表與名詞表。
- 欄名不同時的錯誤訊息，追加與一次匯入多個檔案共用同一句（裁定 2026-11-09-22）。只改訊息文字，比對規則不變。
- 審計員試用後的意見：TB 保留多檔，三處說明先寫期初與期末要放在同一個檔案，再寫多檔只會往下疊成一份 TB（裁定 2026-11-09-23）。這三句後來由裁定 2026-11-09-24 改成和 GL 相同。
- 使用者驗收後：TB 的三處說明改成和 GL 同一套句子，不提期初、期末與查核期間（裁定 2026-11-09-24）。

不做：

- 不改匯入、欄位配對與完整性比對的程式邏輯，也不改 action 的欄位。
- 不做期初與期末兩份 TB 依科目接合（裁定 2026-10-01-01 維持）。
- 同一科目出現在多個檔案時不另外提醒。

## 審計員在哪一步做什麼、看到什麼

- 第二步展開 TB，還沒匯入時看到：「尚未匯入。可合併欄位相同的檔案、工作表或資料表。」，和 GL 相同。
- 按「選擇來源檔」打開可多選的檔案對話框。選好後的說明是：「建立 TB：選擇一個或多個來源檔，支援 Excel、CSV、文字檔及 Access，可合併工作表或資料表。」，GL 只差在寫「建立 GL」。
- 待匯入清單逐檔列出，「開始匯入」寫出幾個來源，按下後整批匯入成一份 TB；匯入後來源清單列出每個檔案與列數。之後可以按「加入」再追加，
  說明是「加入：新檔的資料會附加到現有 N 列（欄位名稱需與現有一致）。成功加入後，這份 TB 需要重新確認配對，受影響的測試結果需要重算。」，和 GL 只差在「這份 TB」，TB 不另加句子。
- 檔案的欄名不同時，匯入失敗。訊息寫出是哪個檔案、多出與缺少哪些欄，並說明本次未匯入、先前匯入的資料不受影響。
  審計員先在 Excel 把欄名對齊，再重新匯入。
- 第三步只配對一次，所有來源共用這份配對。第四步的 GL 與 TB 逐科目比對把同一科目的變動金額加總後再比。

## 改了哪些檔案

- 畫面：`src/JET/JET/wwwroot/js/steps/import-step.js` 裡 TB 卡片還沒匯入時的說明、建立 TB 與按「加入」時的說明。程式邏輯沒有改。
- 錯誤訊息：`src/JET/JET/Infrastructure/Persistence/Local/LocalImportRepository.cs` 的 `EnsureColumnSetsMatch` 只改訊息文字，見「欄名不同時的錯誤訊息」一節。
- 新增的後端測試：`src/JET/tests/JET.Tests/Application/TbImportBranchTests.cs` 兩則。
  一則用兩份合成 TB 一次匯入，確認兩份的列都留下，同一科目在逐科目比對時加總；另一則一次匯入兩份欄名不同的 TB，確認整批不寫入、原本的 TB 不變，並比對新訊息。
- 新增的資料庫層測試：`src/JET/tests/JET.Tests/Infrastructure/SqliteImportAppendTests.cs` 一則。一次取代匯入兩個來源，第二個來源沒有欄名的欄有資料，讀完才發現不同；確認訊息相同，先前匯入的批次不變。
- 新增的前端測試：`tools/tests/frontend-journey-case-setup.test.cjs` 一則。確認 GL 與 TB 一次選兩個檔案只送一個匯入 action，之後按「加入」再追加。
- 改動的既有測試：`tools/tests/frontend-workflow.test.cjs` 兩則，以及 `TbImportBranchTests.cs` 一則，原因見下一節。前端新加的那一則也依裁定 2026-11-09-23 改過斷言。
  裁定 2026-11-09-24 再改了四則前端測試的斷言，也見下一節。
- 文件：`docs/jet-frontend-description.md`、`docs/jet-guide.md`、`docs/action-contract-manifest.md`、`docs/development-status.md`、`docs/rulings.md`、`docs/glossary.md` 與這份計畫。

## 改動的既有測試

`tools/tests/frontend-workflow.test.cjs` 的「source preview states its ten-row boundary and TB merge guidance does not imply period-file reconciliation」，
原本斷言 TB 的兩處說明不寫「可合併欄位相同」，也不寫「可合併工作表或資料表」。
這兩個斷言出自[使用者回饋計畫](2026-09-17-user-feedback-and-workflow-review-plan.md)第 8 批的 L11，用意是避免暗示能處理期初與期末兩檔。
L 開頭的編號是那份計畫引用的複審清單 `low-unverified.md` 的行號，不是使用者的裁定；裁定總表裡也查不到關於這兩句說明的裁定。
裁定 2026-11-09-19 要 TB 和 GL 一樣寫出可以合併，所以改成檢查兩處都寫出可以合併，也都明寫不會自動配成期初與期末的期間變動。原本的用意由後一項檢查保住。
第一次失敗出現在 `Contract -ContractScenario FrontendMapping`。

`src/JET/tests/JET.Tests/Application/TbImportBranchTests.cs` 的 `Append_WithDifferentColumns_IsRejected_AndWritesNothing` 原本逐字比對追加時的舊句。
裁定 2026-11-09-22 要追加與一次匯入多個檔案共用同一句，所以改成比對新句。錯誤碼、差集的寫法與「原本的 TB 不變」的檢查都照舊。
第一次失敗出現在 `Focused -Filter TbImportBranchTests`，17 個測試裡只有這一則失敗。

審計員試用後，裁定 2026-11-09-23 改寫 TB 的三處說明，斷言舊句的三則前端測試跟著改。
三則的第一次失敗都出現在同一次 `FrontendMapping`：

- `tools/tests/frontend-workflow.test.cjs` 的「source preview states its ten-row boundary and TB merge guidance does not imply period-file reconciliation」，
  就是上面改過一次的那一則。改成比對新句子的三個意思：期初與期末要放在同一個檔案、不會把兩個檔案接起來、只會往下疊成一份 TB。
- 同一個檔案的「TB card and both import modes explain the same-period boundary positively」原本斷言「不會自動…期初與期末…期間變動」。
  新句子不再用「期間變動」的說法，所以改成比對不會把期初與期末接起來；建立 TB 與按「加入」各有一句，分開比對。
- `tools/tests/frontend-journey-case-setup.test.cjs` 這次新加的那一則，TB 卡片與選檔說明改成比對新句子，GL 的斷言不變。

使用者驗收後，裁定 2026-11-09-24 把 TB 的三處說明改成和 GL 相同，斷言舊句的四則前端測試跟著改。
四則的第一次失敗都出現在同一次 `FrontendMapping`。第四則的名稱經使用者確認改掉，其他三則的名稱不變。

- `tools/tests/frontend-journey-case-setup.test.cjs` 的「choosing GL and TB source files offers exactly the seven supported file types and the guidance names them」，
  原本斷言 TB 的選檔說明寫「同一查核期間」。改成確認 GL 與 TB 的選檔說明只差在「建立 GL」與「建立 TB」，而且 TB 不提期初、期末與查核期間。
- 同一個檔案的「GL and TB both send several files picked at once in one import and can add more files later」，TB 卡片與選檔說明改成和 GL 比對同一句，
  另外確認卡片不提期初、期末與查核期間。
- `tools/tests/frontend-workflow.test.cjs` 的「source preview states its ten-row boundary and TB merge guidance does not imply period-file reconciliation」，
  改成確認 TB 的卡片、建立與「加入」時的說明和 GL 只差在 GL 與 TB，而且不提期初、期末與接起來。「加入」的比對是複審時發現沒有測試鎖住後補上的。
- 同一個檔案的「TB card and both import modes explain the same-period boundary positively」，改成確認 TB 卡片、建立與「加入」的說明都不提查核期間、期初、期末與接起來，
  建立時是 GL 也用的那一句，「加入」時逐字比對使用者選定的句子。名稱和新的斷言相反，使用者同日確認改成「TB card and both import modes use the same wording as GL」。

## 驗證結果

- `Focused -Filter TbImportBranchTests`：16 個測試都通過，包括新加的一則。
- `Contract -ContractScenario FrontendMapping`：第一次因上一節的既有測試失敗，改完重跑通過。
- `Build` 與 `Public`：都通過。
- `Public` 開始以後，只在 `import-step.js` 加了一行註解，也改了文件，所以重跑 `FrontendMapping` 與 `Documentation`。
  兩項都通過。
- 瀏覽器主機：用合成案件展開第二步的 GL 與 TB，看到新的 TB 說明，主控台沒有錯誤。選檔後的說明要先開系統的檔案對話框，沒有在瀏覽器主機上看，由前端測試確認。
- 改錯誤訊息之後：`Focused -Filter TbImportBranchTests` 第一次只有上一節那則既有測試失敗，新加的測試通過；改完斷言重跑 17 個測試都通過。
  `Focused -Filter SqliteImportAppendTests` 14 個測試都通過。
  接著 `Documentation` 與 `Public` 都通過。
- 依審計員意見改 TB 說明之後：`FrontendMapping` 第一次因斷言舊句的三則測試失敗，改完重跑通過。
  這次只改前端說明、前端測試與文件，沒有重跑 `Public`；改完文件後 `Documentation` 通過。
  瀏覽器主機重新載入後，用同一個合成案件展開第二步的 TB，新的說明完整顯示，沒有蓋到「選擇來源檔」按鈕。重新載入後的請求都成功，主控台也沒有程式錯誤。
- 驗收後把 TB 說明改成和 GL 相同之後：`FrontendMapping` 第一次因斷言舊句的四則測試失敗；
  改完斷言重跑，1,006 個前端測試都通過。這次只改前端說明、前端測試與文件，沒有重跑 `Public`。
  瀏覽器主機重新載入後，用合成案件展開第二步的 GL 與 TB，兩張卡片的說明逐字相同，TB 的說明一行放得下，主控台沒有錯誤。
  獨立複審確認的問題改完後，包括補上「加入」的比對與測試改名，再跑 `FrontendMapping` 與 `Documentation`，結果見下一項。
- 複審修正後：`FrontendMapping` 的 1,006 個前端測試都通過。
  另外在暫存資料夾複製一份前端，故意在 TB 按「加入」的說明多加一句，改過的兩則 `frontend-workflow.test.cjs` 測試都失敗，表示這一處有測試鎖住。
  改完文件後 `Documentation` 通過。
- 沒有跑 `Gui`、`PrivateCase` 與 `Excel`。這一輪沒有改匯出與桌面程式；`Gui` 照規定留到提交或交付前再跑。

## 欄名不同時的錯誤訊息

收尾時查到：一次匯入多個來源、其中一個來源的欄名不同時，錯誤訊息寫「請改用取代匯入，或先在 Excel 把兩份檔案的欄位對齊。」。
這時審計員本來就在做取代匯入，訊息裡的「已匯入的資料」其實是同一批的第一個來源。GL 與 TB 都一樣，訊息寫在 `LocalImportRepository.EnsureColumnSetsMatch`。
使用者選定追加與一次匯入多個檔案共用同一句（裁定 2026-11-09-22），所以只改這一句，比對規則與呼叫的地方都不變：

> 檔案「檔名」的欄位與其他來源不一致（來源多出：欄名；來源缺少：欄名）。本次未匯入，先前匯入的資料不受影響。請先在 Excel 把欄位對齊後再匯入。

兩種情況失敗時都整批不寫入，所以後半句對兩者都成立。取代匯入在開始寫入前先比對欄名，讀完資料後的比對失敗會還原整筆交易；追加的兩次比對都在同一筆交易裡。

## 目前步驟

1. 已完成：查清現況，使用者選定做法。
2. 已完成：改 TB 的畫面說明，使用者確認新句子照用。
3. 已完成：補前端與後端測試，並改一則既有的前端測試。
4. 已完成：同步前端說明、系統規格、開發現況、裁定總表與名詞表。
5. 已完成：依範圍跑 `Build`、相關 `Focused`、`Contract -ContractScenario FrontendMapping`，整合後跑 `Public` 與 `Documentation`。
6. 已完成：照使用者的選擇改欄名不同時的錯誤訊息，補兩則測試，改一則既有測試，再跑相關 `Focused`、`Public` 與 `Documentation`。
7. 已完成：依審計員意見，使用者決定保留 TB 多檔並確認三句新說明；改畫面說明與三則前端測試，再跑 `FrontendMapping` 與 `Documentation`。
8. 已完成：使用者驗收後，TB 的三處說明改成和 GL 相同；改畫面說明與四則前端測試，再跑 `FrontendMapping`、瀏覽器主機與 `Documentation`。

中斷後的下一個動作：等使用者驗收。這一輪沒有暫存、提交或推送。

2026-11-10 使用者手動新建案件測試後驗收通過，回覆裡寫的是「GL和TB多個檔案合併」（裁定 2026-11-10-05）；修改已在提交 `a0e7a13`。
