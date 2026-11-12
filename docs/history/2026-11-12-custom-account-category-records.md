# 2026-11-12 自訂科目分類：兩種配對方式都能新增分類

這份紀錄保存使用者 2026-11-12 的原話、查到的原因、這次的修改，以及 Agent 自己定、使用者同日確認的做法。
現行規則寫在 [`../jet-guide.md`](../jet-guide.md)、[`../action-contract-manifest.md`](../action-contract-manifest.md) 與
[`../jet-frontend-description.md`](../jet-frontend-description.md)；裁定編號見 [`../rulings.md`](../rulings.md) 的 2026-11-12-34 與 2026-11-12-35。

## 使用者原話

使用者 2026-11-12 的訊息，逐字：

> 在先前的工作中，我提出一些關於SAT的標準和要求，因此暫時移除了目前版本不需要上線的功能，並讓專案文件紀錄遵循該版本的狀態去重新撰寫符合的文件，不過在修改過程中我發現有個功能被意外移除了，就是 `科目配對` 的自定義科目，先前我請 agent 擱置並移至未來版本再發布的功能是 "讓使用者自定義上下階層科目配對"，但這之中同時把 "自定義科目配對" 給移除了，現在我想要你修正的是，請確保目前這個版本至少可以做回向 IDEA JET 那樣支援使用者自定義科目配對的功能，並且該設定同時可以設定於 `在JET配對` 與 `用Excel配對` 兩類，其一 `在JET配對`大概會跟先前已經設計過的模樣相似，但你可以優化改良他，尤其是在更簡潔且清晰易懂的使用者體驗，不過要注意得避免 ai slop 的設計樣式，比如過度說明非必要的文字或編號，其二是 `用Excel配對` 大概會跟 IDEA JET 的做法相似，讓使用者可以在 `List` 工作表去新增科目類別，然後回 `AccountMapping` 設定所新增的科目類別，並可以匯入回 je-tool，另外，按鈕 `準備科目配對範本` 或許有更直觀的名稱? 比如 `建立科目配對範本` 這樣剛好可以對齊右側的 `重建科目配對範本` 這樣在使用者體驗上更直觀知道基礎的操作邏輯，而不用過度依賴文字去理解，這樣也能避免 agent 設計時過度使用文字去解釋每一個操作邏輯。以上，修改途中如果你發現值得優化改良的地方，請確保在掌握背景脈絡和代碼關聯後，才處理，避免拆東牆補西牆的困境。

## 查到的原因

「在 JET 配對」的分類設定沒有被移除：隱藏上下層級（裁定 2026-11-03-27）之後，新增分類、移除分類與儲存分類設定都還在，
入口是科目配對卡右上角的「分類設定」。在「在 JET 配對」分頁裡看不到這個入口和新增分類有關，所以容易以為功能不見了。

「用 Excel 配對」則從儲存庫第一個提交（`b9db4db`，2026-08-30）起就不能新增分類，不是隱藏上下層級或 SAT 文件工作造成的。有三處限制：

- 匯入時，分類欄裡案件沒有的名稱會讓整份匯入失敗，訊息是「分類「…」不在本案件的科目分類中（…），請改用清單中的分類名稱。」。
- 範本 A2 的說明寫「允許分類僅限…」。
- 範本 C 欄下拉清單的來源只到 List 工作表目前的最後一個分類，審計員在清單下面加名稱也選不到。

裁定總表裡沒有任何一則要求這三處限制。舊版 IDEA JET 的範本說明寫明 List 工作表可以自行新增分類，
匯入時只讀 AccountMapping 工作表，分類欄寫什麼就收什麼，空白視為 Others。JET 內嵌的範本就是舊版的原檔，
它的下拉清單來源本來是 List 工作表 A 欄第 2 列以下整欄，是 JET 產生範本時改短的。

## 這次改了什麼

- **匯入**：分類欄裡案件還沒有的名稱，匯入時新增成自訂分類，和科目配對在同一筆交易寫入；匯入失敗時一起回復。
  新分類沒有上層，分類用途是「其他」，排在既有分類之後，照檔案裡第一次出現的順序；同一個名稱不分大小寫只算一個。
  JET 的範本、CSV、工作底稿的「自動化工具-科目配對資訊」工作表與前一個案件的檔案都一樣。JET 不讀 List 工作表，
  只新增匯入後真的有科目使用的分類。回應多一個 `accountCategory`，是匯入後案件的分類清單。
- **範本**：C 欄下拉清單的來源改回 List 工作表 A 欄第 2 列以下整欄；A2 的說明改成
  「請在 C 欄選擇標準科目分類；要新增分類，請在 List 工作表的清單最後加上名稱，原有的名稱不要更改；A、B 欄由系統產生，不應修改。」。
  這次修改以前建立的範本不會自動更新：「建立科目配對範本」在檔案已經存在時保留原檔，要在「更多」選「重建科目配對範本」才會換成新的，
  重建會清空檔案內已填的分類。
- **在 JET 配對**：「套用分類」下拉選單旁多一個加號按鈕「新增分類」，按了就打開分類設定並停在新的一列；存好後回到科目配對，
  新分類已經選好，原本勾選的科目還在，可以直接套用。從右上角「分類設定」進去的操作不變。
  一開始做成下拉選單的最後一項「新增分類…」，在瀏覽器主機用鍵盤測試時發現：選單上按方向鍵或 End 移到那一項，
  就會直接跳到分類設定，所以改成選單旁的按鈕。按鈕只放加號，名稱寫在滑鼠提示與螢幕閱讀器讀的文字裡；
  放成文字按鈕時，勾選科目後這一排在 1366 寬的畫面放不下，會被擠到第二行。
- **用 Excel 配對**：按鈕「準備科目配對範本」改名為「建立科目配對範本」，狀態句與訊息面板的操作名稱一起改。
  匯入新增了分類時，完成句寫出新增幾個、叫什麼；分類設定裡還沒儲存的修改不會因此不見。
- 分類設定的提示原本寫「顯示名稱」，欄位標題寫「分類名稱」，統一成「分類名稱」。

## Agent 定的做法，使用者已確認

下面幾項是 Agent 依「做回 IDEA JET 那樣」與現有程式自己定的。Agent 2026-11-12 回報後問「紀錄檔列的八項 Agent 做法，要照現在這樣定案嗎？」，
使用者同日回覆「照現在定案」：

1. **分類欄是照欄位順序認出來的時候，不新增分類。** 分類欄的欄名認不出來時，JET 把還沒認領的欄照順序當成分類欄；認得出來是指欄名是 `Standardized Account Name*`、
   `STANDARDIZED_ACCOUNT_NAME`，或含有「分類」、category、standardized。
   這時選到的可能不是科目配對的檔案，所以案件沒有的名稱照舊讓匯入失敗，訊息多一句
   「要新增分類，請先把這一欄的欄名改成「Standardized Account Name*」。」。舊版 IDEA JET 要求欄名完全相符，沒有這種情況。
2. **案件沒有同名的分類時，內建分類原本的英文名稱指向那個內建分類。** 內建分類在 JET 可以改名；IDEA 版 JET 的範本，
   以及沒有改過名的前一個案件，檔案寫的是 Revenue、Receivables、Cash、Receipt in advance、Others，
   這些名稱仍歸到對應的內建分類，不另外新增。案件裡另外有一個分類剛好叫這個名稱時，以案件的分類為準。
3. **不讀 List 工作表。** 只在清單上、沒有任何科目選用的名稱不會變成分類；審計員在清單改掉或刪掉的名稱也不影響案件既有的分類。
4. **新增分類的數量沒有上限。** 分類名稱超過 400 個字時，那一列算無法讀取，整份匯入不儲存。
5. **「科目配對範本尚未準備好」這句沒有改。** 它是「在 JET 配對」分頁上範本建立失敗時的狀態句，不是按鈕名稱。
6. **存好後自動回到科目配對，只在從「套用分類」旁的「新增分類」按鈕進去時發生。** 這次儲存剛好新增一個分類時才自動選好它。
7. **「新增分類」做成只有加號的按鈕。** 理由見上面「這次改了什麼」；右上角的「分類設定」照舊是文字按鈕。
8. **另一個案件改過名的內建分類名稱，會被當成新分類。** 前一個案件把內建的 Receivables 改成別的名稱，
   再把它的範本或工作底稿匯進新案件時，新案件認不得那個名稱，照那個名稱新增一個分類用途是「其他」的分類。
   這次修改以前，同一份檔案會讓匯入失敗並寫出原因；現在匯入成功，完成句只寫新增了哪些分類。
   那些科目不算應收，預篩選的未預期借貸組合會把借記應收、貸記收入的分錄算成命中。第一個正式版隱藏分類用途，
   畫面上改不了新分類的用途；要修正，可以在「在 JET 配對」把科目套回內建分類，或先在新案件把內建分類改成同一個名稱再匯入。
   這一項是審查時發現的；使用者回覆照現在定案，不另外提醒。

名稱相近但不同的分類不會合併：審計員在 List 寫 Receivable，JET 會新增一個用途是「其他」的 Receivable，
不會把它當成內建的 Receivables，預篩選的未預期借貸組合也不會把它算成應收。IDEA 版 JET 的範本同樣要求這幾個名稱不可更動。

## 審查後補的修改

修改完成後另外請四個代理各從一個面向審查，再對每一則發現試著反駁。成立的幾則已經處理：

- 科目配對寫進案件之後才送到的取消，原本會讓畫面寫「已取消」，但科目配對與新增的分類其實已經存好，前端的分類清單停在匯入前。
  寫入後讀匯入狀態改成不受取消影響。這個時間差原本就有，這次新增分類讓後果多了一項，所以一起修。
- 每按一次加號，分類設定就多一列空白分類，按「返回科目配對」也不會拿掉，之後不能儲存。改成：沒有命名就返回時把那一列拿掉；
  最後已經有一列還沒命名的新分類時，加號沿用它。
- 分類設定裡還沒儲存的新分類，和匯入新增的分類同名時，原本會出現兩列同名而不能儲存。改成那一列換成已經存好的分類。
- 從加號新增分類、存好回到科目配對後，鍵盤焦點原本掉到整個頁面。改成交給「套用至已選科目」，不能按時交給「套用分類」選單。
- 文件把內建分類原本的英文名稱寫成「一律」歸到內建分類，程式是案件現有的同名分類優先；文件補上條件，並補上面第 8 項。
- 補兩個測試，分別核對「只有分類欄照欄位順序」與「分類欄認得出來、另外兩欄照欄位順序」，把規則看的是分類欄這件事定下來。

處理完再請三個代理複查這些新加的修改，同樣對每一則發現試著反駁。成立的幾則也已處理：

- 匯入把草稿裡同名的新列換成已存好的分類之後，草稿和已儲存的分類完全相同，分類設定卻仍寫「分類設定尚未儲存。」。
  改成草稿和已儲存的分類相同時放掉草稿。
- 還沒命名的新列不在草稿最後時，加號不會沿用它。改成沿用草稿裡最後一列還沒命名的新分類，不管它在哪個位置。
- 返回時拿掉還沒命名的新列，沒有處理掛在它底下的分類。上下層級的開關現在關著，畫面走不到；仍照移除分類的做法，
  把底下的分類改接到上一層。
- 測試環境說明補上：這次修改以前建立的範本要先重建，才能在 List 新增分類。
- 前端說明、系統規格與 action 契約裡屬於 Agent 定的做法的句子，出處改寫成裁定總表 2026-11-12-35，不掛在使用者的裁定底下。

被反駁、沒有處理的三則：裁定總表 2026-11-12-34 的原話用「；」接起幾段摘錄，和總表其他列的寫法相同，沒有改。另外兩則是範本往返測試沒有核對只寫在 List 工作表的名稱不會變成分類，以及沒有測試走到分類寫入後再回復。
兩則都要先有人改掉現有的程式才會出錯，現有的測試已經核對「只新增有科目使用的名稱」與交易回復。

## 這次改到的檔案

共 37 個。

- 產品程式 13 個：`AccountCategoryContracts.cs`、`AccountMappingContracts.cs`、`AccountMappingColumnMatcher.cs`、`IntakeMappingProgram.cs`、
  `AccountCategorySaveHandler.cs`、`ImportAccountMappingHandler.cs`、`LocalAccountCategoryStore.cs`、`LocalAccountMappingRepository.cs`、
  `ReferenceDataFactsPort.cs`、`AccountMappingTemplateWriter.cs`、`validate-step.js`、`account-mapping-editor.js`、`app.css`。
- 測試 11 個：`AccountCategoryContractsTests.cs`、`IntakeMappingProgramTests.cs`、`ImportAccountMappingHandlerTests.cs`、
  `AccountMappingTemplateExportTests.cs`、`AccountMappingTemplatePackageDiffTests.cs`、`ResultInvalidationProjectionAndFilterTests.cs`、
  `UpstreamMutationClearsFilterScenariosTests.cs`、`ReferenceDataFactsPortTests.cs`（新檔）、`action-shapes.json`、
  `frontend-journey-step4.test.cjs`、`frontend-workflow.test.cjs`。
- 工具 3 個：`tools/harness/gui-driver/GuiCaseListCancelScenario.cs`、`tools/harness/gui-driver/README.md`、`tools/harness/documentation-check.json`。
- 文件 10 個：`docs/rulings.md`、`docs/glossary.md`、`docs/jet-guide.md`、`docs/action-contract-manifest.md`、`docs/jet-frontend-description.md`、
  `docs/test-environment-notes.md`、`docs/development-status.md`、`docs/history/README.md`、`docs/history/test-change-records.md` 與這份紀錄。

## 驗證

- 相關的 C# 測試以 `tools/verify.ps1 -Command Focused` 分批執行，篩選 `AccountMapping`、`AccountCategory`、
  `IntakeMappingProgramTests`、`ReferenceDataFactsPortTests`、`ResultInvalidationProjectionAndFilterTests`、
  `UpstreamMutationClearsFilterScenariosTests` 與 `ActionShapeContractTests`，全部通過；每一批都連同架構測試一起跑。
- `Build`、`Contract -ContractScenario FrontendMapping` 與 `Documentation` 通過。
- 在 `src/JET` 用 `node --test` 執行 `tests/frontend` 的前端測試，1,016 個全部通過，其中 9 個是這次新增的。
  `Contract -ContractScenario FrontendMapping` 另外連同 `tools/tests` 的前端測試一起執行，也通過。
- 在瀏覽器主機的合成案件實際操作：新增並套用分類；匯入帶新分類名稱的 CSV；重建範本。改成加號按鈕後再確認：
  按加號會打開分類設定並停在新的一列，在選單上按 End 只選到最後一個分類，勾選科目後這一排在 1366 寬的畫面仍是一排。
- 用看不到視窗的 Excel 開啟 JET 產生的範本副本：下拉清單來源是 List 工作表整欄，在清單最後加上的名稱可以選用，
  清單以外的名稱仍被 Excel 擋下；存檔後匯回 JET，新分類建立成功。
- 沒有執行完整的 `Public`、`Package`、`Excel` 與 `Gui`；Gui 情境比對的操作名稱已經跟著改，但還沒有實際跑過。
- 修改既有測試的原因與第一次失敗的情形見 [`test-change-records.md`](test-change-records.md)。
