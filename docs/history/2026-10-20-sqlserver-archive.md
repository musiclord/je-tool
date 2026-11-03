# SQL Server 封存事項

狀態：封存中，2026-10-20 起。使用者明示重新開發 SQL Server 前，這裡的事項不列入開發現況的待辦與延後事項。

使用者 2026-10-20 原話：「所有 sql server 的項目因為擱置時間很久，目前都沒有要開發 sql server 的系統，為了避免之後管理開發事項要一直提到他，請將他封存起來，之後有需要再把他提出來繼續開發。」

這份文件收齊 SQL Server 尚未完成的事項，內容從三處原文移入：開發現況「已知但延後的事項」裡和 SQL Server 有關的四列、
開發現況的「SQL Server 開發暫緩期間的修改」一節，以及原本的現行文件 `docs/sqlserver-enterprise-deferred.md`。
移入時只做三種調整：相對連結隨位置改寫、原本指向那份文件的連結改指本文件的段落、標題層級配合本文件；文字沒有改。
「操作紀錄的查詢介面、匯出與保留政策」一列，使用者 2026-10-19 回覆「這個和 sql server 併為一塊，目前繼續擱置」，所以一併封存。

SQL Server 的程式與測試照舊保留在儲存庫，規格仍以 [`jet-guide.md`](../jet-guide.md) 第 8 節為準。
SQL Server 服務與 `Provider` 只在使用者當次明示時執行，規則見 [`AGENTS.md`](../../AGENTS.md) 的「測試」。

## 重新啟動時

使用者明示要重新開發 SQL Server 時，先讀這份文件，把要做的事項移進新的現行計畫，再在這裡註明哪些已經移出。
實機驗證與企業多人環境各自的前提條件，寫在下表的「何時重啟」與下方的「企業環境延後範圍」。

## 封存的延後事項

| 事項 | 背景與目前邊界 | 何時重啟 |
|:---|:---|:---|
| SQL Server 實機驗證 | 使用者 2026-09-02 確認 SQL Server 開發暫緩，日常測試集中在 SQLite 與 DuckDB。既有相容性保留；一般 `ReleaseCandidate` 不要求連線，也不能代表 SQL Server 已通過。本機服務平時關閉，使用後依 [`../../tools/README.md`](../../tools/README.md#日常資料庫測試與服務收尾) 收尾。2026-10-19 跨資料庫一致性計畫依使用者裁定執行三次完整 `Provider`：第一次有 13 項測試過時而失敗，修正測試後第二次全部通過，第三次加入篩選條件等價測試的 SQL Server 版，也全部通過；結果寫在計畫的「第二階段結果」與「最後一次完整驗證」。暫緩期間的修改有沒有 SQL Server 測試核對，逐項寫在下方「SQL Server 開發暫緩期間的修改」。直接使用篩選條件測試夾具 `FilterPredicateProviderFixture` 的 20 個測試類別中，仍有 11 個只在 SQLite 與 DuckDB 執行，例如 `FilterCompletenessTests`、`FilterNestedVoucherTests` 與 `KctFilterPredicateTests`。本機 `JET_Test` 先前測試留下的資料結構，依使用者裁定全部清除；之後兩次執行都沒有新的殘留。 | 使用者 2026-10-07 裁定近期不處理，不納入現行與下一階段計畫。2026-10-19 使用者看過三次 `Provider` 的結果後回覆：「可以，目前 sql server 的部分這樣就好，後面仍然繼續以 sqlite 為主 duckdb 為輔。」重啟時須使用者當次明示要驗證，且已準備專用的 `JET_Test` 資料庫、最低必要權限與只存在於當次程序的連線資訊，再完整執行 `Provider`，並先核對下方清單。 |
| SQL Server 企業多人環境 | 四個已確認的多人安全缺口與多人共用案件、使用鎖、容量資訊的驗收範圍，記在下方的[企業環境延後範圍](#企業環境延後範圍)。只記錄，不推測實作。 | 使用者 2026-10-07 裁定近期不處理，不納入現行與下一階段計畫；2026-10-19 回覆「這個還不會處理，繼續擱置」。之後公司能提供 SQL Server 2022、至少兩個真實帳號、DBA 支援與已核定的授權政策時，另立短期驗收計畫。 |
| 操作紀錄的查詢介面、匯出與保留政策 | 本機案件已有最小的只能附加的操作紀錄（DuckDB 沒有 trigger，靠程式紀律維持）。查詢介面、匯出與保留政策屬企業部署範圍；本機刪案留痕已裁定不做。 | 隨 SQL Server 企業線一併重啟，或使用者明示要先做本機查詢介面時另立計畫。2026-10-19 使用者回覆：「這個和 sql server 併為一塊，目前繼續擱置」。 |
| SQL Server 匯入暫存表時等待查詢記憶體配給 | 2026-10-19 在 SQL Server 的 `system_health` 記到兩次寫入 GL 暫存表時等待查詢記憶體配給，分別等了 106 秒與 21 秒，都在單獨執行 SQL Server 測試的時段；稍後執行 `Provider` 時量到這台電腦只剩約 2 GB 可用記憶體。暫存表的叢集主鍵是 `(batch_id, row_number)`，`SqlBulkCopy` 沒有給排序提示，SQL Server 寫入前會先排序；大量寫入不設逾時，所以只會等待，不會失敗。屬於效能問題，不影響審計結果。同一天第一次刪除測試資料結構逾時的原因也仍未確認，見跨資料庫一致性計畫的「第二階段結果」。 | SQL Server 匯入的速度成為問題，或重新處理 SQL Server 企業多人環境時。先在實機確認排序提示能否省掉記憶體配給，再決定是否修改。 |

## 企業環境延後範圍

原文件的更新日期是 2026-08-30。

JET 目前以 SQLite／DuckDB 本機流程為優先。SQL Server 的資料存取與控制面已有自動測試，程式碼也保留
完整 provider 相容性，但真正的多人、公司帳號與企業政策只能在公司環境確認。這份文件保存已確認的
安全缺口與驗收範圍，讓企業線重啟時不必重新追查前因後果。

### 何時才需要重啟

同時具備以下條件後，再另立短期驗收計畫：

- 可連線的 SQL Server 2022。
- 至少兩個不同的 Windows／網域帳號。
- DBA 或具備查詢專案登錄、schema 與 audit log 權限的人員。
- 已核定的專案授權、AD 整合與管理員操作規則。

### 已確認押後的四個多人安全缺口

以下四項在 2026-08-01 確認仍未閉環，只記錄、不在本地優先開發輪推測實作。企業線必須先取得真實帳號、
SQL Server 與管理政策再設計及驗收：

1. **真實身分驗證**：目前 `system.whoAmI` 與 registry ACL 使用 host 端 `WindowsIdentity` 形成的
   client 自報 principal；共用 SQL 登入下，資料庫端尚未把連線身分與該 Windows／網域帳號做不可偽造的
   綁定。企業線需選定 AD／整合驗證或等價信任邊界，並以至少兩個真實帳號驗證授權與 audit attribution。
2. **serverOnly 刪除**：`project.delete` 目前先要求本機 `project.json`，只有 registry 條目而尚未物化
   本機快取的案件會回 `project_not_found`。企業線需定義從線上清單直接刪除的授權、鎖、確認與交易
   邊界，不能用前端偷偷先載入來繞過。
3. **noAccess metadata 隱藏**：持有他人案件本機快取時，`project.list` 可標示 `noAccess`，但本機文件的
   案件編號、實體名稱、期間等 metadata 仍可能出現在列上。企業線需裁決未授權使用者可見的最小欄位，
   並驗證清單、錯誤與本機殘留都不洩漏超出政策的資訊。
4. **SQL Server 2022 版本硬閘**：目前會明確拒絕 Express 並顯示產品版本，但尚未在所有 production
   進入點強制 `ProductMajorVersion >= 16`；較舊的非 Express 版本不是完整硬擋。企業線需在連線與建案前
   建立 SQL Server 2022（16.x）一致的 fail-closed gate，並驗證 Developer／Standard／Enterprise 的
   允許矩陣。

### 重啟時要驗收的操作面

**多人共用案件**

- 使用者 A 建立的線上案件，已授權的使用者 B 可以在清單看到。
- 刪除案件後，所有使用者的清單、專案 schema 與登錄資料都一致消失。
- 建立與刪除動作可在 audit log 查到操作者與電腦。

**同一案件的使用鎖**

- 使用者 A 開啟案件後，使用者 B 不能同時修改，且看得到由誰使用。
- A 正常離開後，B 可以立即接手；A 異常關閉後，鎖逾時才允許 B 接手。
- A 停留在案件內或切換步驟時，鎖不會誤釋放。

**後端容量資訊**

- 系統可以顯示 SQL Server 資料庫大小與線上案件數，案件數隨建立或刪除正確增減。
- 容量接近門檻時顯示提醒；正常範圍不誤報。

**audit log 的企業面**

- 本機案件的最小 audit log 已落地（schema v9 起由資料庫層強制 append-only；DuckDB 因引擎沒有
  trigger 維持程式紀律）。查詢介面、匯出與保留政策屬於企業線範圍，本地輪不做。
- SQLite／DuckDB 的刪案留痕已於 2026-08-20 裁決不做：本地案件屬個人工作性質，持久刪案證據是
  中心化管理議題；SQL Server 既有的刪案紀錄維持。

### 不在範圍內

- 尚未核定的專案授權名單。
- Active Directory 的最終整合方式。
- 管理員強制釋放他人鎖的規則。
- 企業 CFA、DLP、備份與憑證政策。

這些項目需要新的公司環境決策，不能依舊設計草稿直接實作或宣稱已驗收。重啟時依當時程式碼另寫短期
驗收計畫，完成後把結果回寫 [`development-status.md`](../development-status.md)。live SQL Server 的
相容性驗證入口是 `tools/verify.ps1` 的 `Provider` 命令，條件見 [`../../tools/README.md`](../../tools/README.md)。

## SQL Server 開發暫緩期間的修改

這一節列出 SQL Server 開發暫緩期間改過的 SQL Server 程式。這一節原名「SQL Server 只經編譯的修改」，歷史計畫提到的是舊名稱；
2026-10-19 逐項核對測試涵蓋後改成現名。同日跨資料庫一致性計畫依使用者裁定，在本機 SQL Server
執行三次完整 `Provider`：第一次有 13 項失敗，都出在測試本身過時；修正測試後第二次全部通過，包括最後一段的 LocalDB 檢查；
第三次加入篩選條件等價測試的 SQL Server 版，也全部通過。結果與原因寫在
[計畫](specs/2026-10-19-cross-database-consistency-plan.md)的「第二階段結果」與「最後一次完整驗證」。

同日依使用者要求，逐項比對 `Provider` 實際執行的 SQL Server 測試與測試程式，在每一項寫明屬於下列哪一種：

- SQL Server 測試核對了這項行為。
- SQL Server 測試執行到這段程式，但沒有核對這項行為。
- 沒有 SQL Server 測試執行到。

後兩種在 SQL Server 上的結果是否正確，仍沒有測試證明。測試執行到卻沒有核對，主要有四種原因：測試資料沒有空白傳票號碼；
分頁測試只比對列序，沒有核對完整查詢筆數；有些測試只核對 SQL 文字；空白判定的測試只用半形空白。

- 2026-10-16 明細分頁統一為每頁最多 200 筆。SQL Server 的資料預覽新增分頁位移，完整性、空白紀錄、
  傳票平衡、預篩選、篩選與樣本頁面及矩陣共用查詢補上完整查詢筆數。同日再改成總筆數只在第一頁計算，
  `SqlServerDataPreviewRepository` 的總筆數與統計只在 `offset` 為 0 時查詢。
  SQL Server 測試核對了資料預覽第一頁的列與總筆數，但沒有測試送出大於 0 的 `offset`，所以分頁位移本身，以及續頁不再計算總數，
  都沒有核對。
  其他頁面的分頁查詢，除了預篩選明細頁，都有 SQL Server 測試執行到，但沒有核對完整查詢筆數；預篩選明細頁沒有 SQL Server 測試執行到。

- 2026-10-14 篩選條件改為每條只表達一件事。共用 `FilterCompilation` 在傳票條件未帶方向時判斷整張傳票，
  `GlRulePredicates.Values.AccountSide` 未帶方向時只比對分類；常用查核與 KCT E、F、G、H 改用共用欄位條件。
  舊式組合由共用驗證列為待修正，篩選版本為 `filter-2026-10-14-v20`。2026-10-19 補上三個篩選條件等價測試的 SQL Server 版，
  核對傳票條件與借貸方條件未帶方向時的命中，以及摘要關鍵字、空白摘要、編製者與金額尾數零的舊條件和改寫後的欄位條件；
  SQL Server 都和本機得到相同的固定答案。KCT E、F、G、H 各自對應哪一條改寫，這次沒有逐條核對。

下列 SQL Server 程式在 SQL Server 開發暫緩後改過，批次名稱指已歸檔計畫裡的段落。

- 整體複審第 5 階段第 3、4 批改了幾處只在 SQL Server 路線執行的程式與測試，清單寫在已歸檔計畫文末整體複審的第 3 批與第 4 批，
  例如科目分類連接、預篩選 SQL 指紋、建案回滾的測試資料、開案時改版寫回情境，以及只存在伺服器上的案件要由請求帶 `sqlServer`
  才會去登錄找。科目分類連接、建案回滾與只存在伺服器上的案件，有 SQL Server 測試核對。預篩選 SQL 指紋的預期值取自實際執行的 SQL，
  只能發現日後的改變，不能證明 SQL 正確。開案時改版寫回情境，這次沒有找到對應的程式，未確認。
- 第 5 批之後的補充修正另加了 `config_field_mapping_previous` 資料表，以及重新匯入時保存上次確認配對的 SQL。
  資料表由第 12 版升級測試核對；保存上次配對的 SQL 在重新匯入的 SQL Server 測試中執行到，但沒有測試讀回上次的配對。
- 第二遍回饋審閱第 2 批把空白判定與人員比對改成走方言的 `Trim`，字元集合和 .NET 相同，並對人員不分大小寫。SQL Server 端改到：
  - `SqlDialect.cs` 的 `SqlServerDialect.Trim`，寫成 `TRIM(NCHAR(...) FROM x)`，需要相容性層級 140 以上。
  - `SqlServerGlRepository.cs` 的必填文字欄整欄空白偵測。
  - `SqlServerPrescreenRunRepository.cs` 的編製者彙總與相異人數，以及 `SqlServerCreatorSummaryExportRepository.cs`。
  - `SqlServerNullRecordsPageRepository.cs` 與 `SqlServerValidationRunRepository.cs` 的空白紀錄述詞，原本是 `LTRIM(RTRIM())`。
  - `AccountMappingEditorRepository.cs` 的 SQL Server 路徑，以及 `GlRulePredicates` 共用述詞在 SQL Server 方言下的輸出。
  - SQL Server 測試核對了半形空白的修剪、人員不分大小寫、編製者彙總、必填文字欄整欄空白，以及值為 NULL 的空白紀錄；
    `GlRulePredicates` 在 SQL Server 方言下的輸出也有固定答案核對。全形空白、不換行空格與 Tab，以及只含空白字元的空白紀錄，
    只有 SQLite 與 DuckDB 的測試核對。科目配對編輯頁的讀取與儲存，沒有 SQL Server 測試執行到。
- 第 3 批在既有 `schema_info` 保存授權清單的原始列數、空白與重複數；`SqlServerAuthorizedPreparerRepository.cs` 與
  `AuthorizedPreparerMetadataSql.cs` 的 GL 人員比對查詢同步修改。SQL Server 測試在建案匯入授權清單時執行到這段寫入，
  但沒有核對原始列數、空白數、重複數或比對到的人數。
- 第 4 批新增 `project.update` 的 SQL Server 登錄同步，以及 `SqlServerResultStaleStateStore.InvalidateForPreparationDateChangeAsync`
  的同交易結果清除。登錄同步的底層方法有 SQL Server 測試核對；由 `project.update` 觸發的流程與財報準備日變更時的清除，
  沒有 SQL Server 測試執行到。
- 第 5 批的 `AccountMappingEditorRepository` 分類含下層查詢，以及共用 `GlRulePredicates.Values.AccountSide` 不限借貸的 SQL。
  分類含下層的查詢沒有 SQL Server 測試執行到；借貸方條件不限借貸，2026-10-19 由新補的篩選條件等價測試在 SQL Server 核對。
- 第 6 批同步全列錯誤彙總與 `TbMappedColumnAudit` 必填文字全空查詢。GL 的全列錯誤彙總有 SQL Server 測試核對；
  TB 的錯誤彙總沒有 SQL Server 測試執行到；TB 必填文字全空的查詢有執行到，但沒有核對提醒內容。
- 第 7 批的 `FilterCompilation` 傳票量詞、`GlRulePredicates.Values` 分類 absent 與 `GlRulePredicates` 未預期借貸組合，
  都補上空白傳票號碼的排除；攸關資料元素（RDE）的多關鍵字改為單次欄值查詢，日期條件改用案件的解析選項。
  SQL Server 測試的資料沒有空白傳票號碼，三處排除都沒有在 SQL Server 核對；傳票量詞與分類 absent 的條件，
  2026-10-19 起由新補的篩選條件等價測試在 SQL Server 執行。攸關資料元素的多關鍵字查詢只有不連線的 SQL 文字測試。
  從程式判讀，日期條件的解析選項在 C# 端轉換，和資料庫無關。
- 第 8 批 `SqlServerPrescreenRunRepository` 新增完整分錄測試範圍內的相異科目數查詢，沿用既有 11 筆以下述詞，
  對應新的 `rareAccounts.lowFrequencyAccountCount`。SQL Server 測試執行到這段查詢，但沒有核對 `lowFrequencyAccountCount` 的值。
- 第 9 批：
  - `SqlServerProjectDatabase.Schema` 升到第 12 版，讓既有第 11 版案件補建 `config_field_mapping_previous`；已新增缺表遷移測試。
  - 共用 `ValidationProcedures.UnbalancedCore` 排除空白傳票號碼；`WorkpaperStep4StreamReader`、`TagMatrixRowPageReader.ReadWorkpaperAsync`
    與 `WorkpaperStep41PreparedSession` 補入空白號碼命中分錄。`SqlServerValidationRunRepository` 新增同號多入帳日的兩項摘要計數，
    沿共用 SQL 使用 `COUNT_BIG`。
  - 中低項目把 TB 投影、欄位資訊與配對狀態移入同一交易，驗證的 INF 樣本、有界來源品質資料與摘要也改為同一交易；修改包括
    `SqlServerTbRepository`、`SqlServerValidationRunRepository`、`SqlServerRuleRunStore`、共用 `SourceQualityPageReader` 與
    `ResultStaleStateSql`。已移除 `SqlServerImportRepository` 的單一來源舊多載，正式批次 SQL 不變；有界摘要改用共用 50 列常數。
    只存在伺服器的案件另在本機物化前核對查核期間，該分支用假登錄測試，未連線。
  - 第 12 版補表、驗證的 INF 樣本與摘要同一交易，以及有界摘要的 50 列上限，有 SQL Server 測試核對。空白傳票號碼的排除與補入、
    同號多入帳日的兩項計數，以及 TB 投影的交易範圍，SQL Server 測試有執行到，但沒有核對。
- 2026-10-05 交付前修正：
  - `SqlServerCompletenessAccountPageRepository.cs` 與 `SqlServerCompletenessDiffPageRepository.cs` 改用共用的
    `CompletenessAccountPageQuery`，先依固定順序給每列序號，換頁只比較序號。
  - `GlProjectionDataReader.cs` 與 `SqlServerGlRepository.cs` 加上「只列一側」的人工或自動代碼在來源裡一筆都沒有時的提醒。
  - 完整性科目表的分頁有 SQL Server 測試跨頁核對，結果和 SQLite 相同；只列一側代碼的提醒沒有 SQL Server 測試核對。

- 2026-10-06：`SqlServerDataPreviewRepository` 的有效分錄預覽新增人工或自動欄，三種資料庫共用欄序。既有 SQL Server 測試已同步九欄及人工、自動值，2026-10-19 在 `Provider` 執行並通過。
- 2026-10-06：科目配對預覽改讀已存配對，與本機共用讀取方法；不再用新版欄名判斷器解讀舊匯入批次。有 SQL Server 測試核對。

- 2026-10-06：新增 `SqlServerAccountMappingDifferenceRepository`，匯入回兩項差異筆數，差異科目使用有界分頁。
  與本機實作共用 SQL，採完整性比對的有效 GL 與 TB 科目聯集，並排除空白科目編號；SQL Server 的科目配對清單
  也改用同一份 `AccountMappingPopulationQuery`。GL 或 TB 有、科目配對沒有的科目清單，以及空白科目編號的排除，有 SQL Server 測試核對；
  科目配對有、GL 與 TB 都沒有的科目清單與它的分頁，沒有 SQL Server 測試執行到。
- 2026-10-06 第三輪：`SqlServerAccountMappingExportRepository` 產生空白科目配對範本時改用同一份科目母體，不再列出空白編號；
  `SqlServerDataPreviewRepository` 的人工或自動欄改呼叫與本機共用的轉換；舊結構案件升級補記「分類有沒有填」時，
  SQL Server 也改用和已存分類一致的來源欄判斷。三種資料庫共用的差異清單查詢，第一頁總數改用純量子查詢，
  SQL Server 照舊用 `COUNT_BIG`。空白科目配對範本與預覽的人工或自動欄有 SQL Server 測試核對；舊結構升級補記「分類有沒有填」
  與差異清單第一頁的總數，SQL Server 測試有執行到，但沒有核對。

- 第三輪複核提出、第四輪補記的兩項測試，2026-10-19 已補上：SQL Server 的空白科目配對範本查詢有了防止退回舊寫法的測試，
  三種資料庫也用同一份含空白科目編號的合成資料核對範本列，都在 `Provider` 通過。
- 差異清單第一頁的總數會另計一次科目母體。SQL Server 預設隔離設定下，同一語句不保證清單與總數讀到同一份資料；這個限制只記錄，沒有測試核對。
- 2026-10-07 上游修改清除下游：共用的 `RuleRunResultReset` 多刪 `config_filter_scenario`，`ResultStaleStateSql` 在情境清除時把篩選標回從未執行，
  `SqlServerAccountTaxonomyStore` 不再因情境使用分類而擋刪除。同日收尾時，`SqlServerResultStaleStateStore` 改成存好財報準備日設定才提交清除，
  `SqlServerProjectDatabase.Schema` 的第 7 版升級拿掉已無作用的情境改寫。SQL Server 路徑都帶 schema 前綴。
  情境清除與第 7 版升級拿掉情境改寫，有 SQL Server 測試核對；分類被情境使用時不再擋刪除，只有 SQLite 的測試核對；
  存好財報準備日設定才提交清除，沒有 SQL Server 測試執行到。
  `SqlServerSchemaMigrationTests` 原本仍預期第 7 版升級後情境保留、篩選標成待重跑，和本機版測試及產品行為不符；
  2026-10-19 第二階段改成情境清空、篩選回到從未執行，並在 `Provider` 通過。
- 2026-10-07 TB 比照 GL：失效政策由三種資料庫共用，`SqlServerImportRepository` 的 TB 匯入與 `SqlServerTbRepository` 的
  TB 配對確認，現在也清除驗證、預篩選、篩選命中與全部情境。SQL Server 程式本身沒改，行為隨共用政策改變。
  下列 SQL Server 測試的共用預期已跟著修改，2026-10-19 都在 `Provider` 執行並通過：
  `CompletenessBackendGateProviderTests.ReimportedTb_ClearsPrescreenRunAndRejectsPrescreenReport_SqlServer`、
  `ResultInvalidationSqlServerTests` 經 `ResultInvalidationTestSupport` 讀到的 TB 三列，以及
  `ResultStaleStateLifecycleTests.SqlServer_ResultLifecycle_PreservesExactStaleTransitions` 的 TB 步驟。
- 2026-10-08 驗收回饋修正：移除傳票日期時，`SqlServerProjectDatabase.Schema` 的 GL 建表與舊版升級拿掉 `voucher_date`，
  `GlProjectionDataReader` 少一欄、後面的欄位往前移，`SqlServerPrescreenRunRepository` 拿掉回溯過帳的計數查詢。
  資料預覽改名時，`SqlServerDataPreviewRepository` 改用新的資料集代號，SQL 沒有變。2026-10-19 的 `Provider` 逐列核對了
  SQL Server 的 GL 投影值，資料預覽的 SQL Server 測試也都通過；拿掉回溯過帳計數後的預篩選 SQL 指紋，預期值取自實際執行的 SQL。
- 2026-10-08 Codex 後續註解：共用 `RuleRunResultReset` 在 GL、TB 匯入及配對確認時，多清除科目配對、原始配對列及匯入狀態，保留分類定義。
  `GlRowProjector` 同時支援單邊借貸代碼的補集判定。SQL Server 行為隨共用程式改變。結果清除的 SQL Server 測試有執行到，
  但沒有核對科目配對、原始配對列與匯入狀態是否清除；`GlRowProjector` 是 C# 共用程式，和資料庫無關。
- 2026-10-13 訊息分三級：`SqlServerProjectDatabase.Schema` 的 `app_message_log.level` 限制加上 `error`，版本升到第 13 版。
  既有案件由第 12 版升級時，從系統目錄找出建表時未命名的等級限制再換掉。從舊版升級的 SQL Server 測試會執行這段 SQL，
  但測試建出的表本來就是新的限制；從程式判讀，找出舊限制再換掉的那一段沒有執行到，也沒有 SQL Server 測試寫入 `error` 等級的訊息。
  `SqlServerSchemaMigrationTests` 的版本預期後來隨結構升到第 14 版一起修改，2026-10-19 在 `Provider` 執行並通過。
- 2026-10-13 第五步一次只存或刪一個情境：新增的 `filter.scenario.save`、`filter.scenario.remove` 與 `filter.scenario.recheck`
  在 SQL Server 案件沿用既有的 `SqlServerFilterScenarioStore.ReplaceAllAsync` 與 `SqlServerFilterCommitRepository.CommitAsync`，
  三種資料庫共用的 `FilterFactsPort` 多認這三個動作名稱，沒有新增 SQL。底層的情境儲存與篩選提交有 SQL Server 測試核對交易；
  這三個新動作本身沒有 SQL Server 測試執行到。
- 2026-10-14 第三輪訊息文字：未設定連線、Express 版、死結、登入失敗、重複資料、逾時、同名案件、找不到案件與損壞的抽樣設定，
  改成使用者確認的句子。改到 `SqlServerAppConfigStore`、`SqlServerControlPlaneReconciler`、`SqlServerLockService`、`SqlServerUserDirectory`、
  `SqlServerProjectDatabase` 與 `SqlServerProjectRegistry` 的連線字串建立，`SqlServerProjectDatabase.Readiness` 的
  `EnsureSingleDatabaseAndControlPlaneAsync`，`SqlServerEngineErrors.TranslateSqlException`（重複資料不再帶資料庫引擎的原文），
  `SqlServerProjectDatabase.CreateRollback` 的 `BeginCreateProjectOnceAsync`，`SqlServerProjectRegistry` 的 `UpdateDocumentOnceAsync` 與
  `ReadRegistered`，`SqlServerImportRepository.AppendToBatchAsync`，以及會走到 SQL Server 的 `ProjectLoadHandler` 與 `CaseCreateFactsPort`。
  `DualSourceProjectTests` 2026-10-19 改成這一輪確認的句子後在 `Provider` 通過，同名案件、找不到案件與損壞的抽樣設定這幾句
  有 SQL Server 測試核對；未設定連線、Express 版、死結、登入失敗、重複資料與逾時的句子，沒有 SQL Server 測試核對。
- 2026-10-16 第五步分錄筆數改成和條件篩選報告同一口徑：`SqlServerFilterRunRepository.CountWholeVoucherRowsAsync`、
  `SqlServerTagMatrixScenariosRepository.GetWholeVoucherRowCountsAsync`（共用 `TagMatrixScenarioCounts`，用 `COUNT_BIG(*)`），
  以及 `SqlServerFilterVoucherRepository` 轉給共用 `FilterVoucherPageReader` 的整張傳票計數參數。
  從程式判讀，`ProviderParityJourneyTests` 的篩選預覽會執行整張傳票計數，但沒有核對這個值；矩陣情境與傳票清單的整張傳票計數，
  沒有 SQL Server 測試執行到。
- 2026-10-19 延後事項清理計畫：條件篩選報告的說明列新增 `SqlServerTagMatrixScenariosRepository.GetBlankDocumentHitRowCountsAsync`
  （共用 `TagMatrixScenarioCounts`，用 `COUNT_BIG(*)`），計算各情境傳票號碼空白的命中分錄筆數。沒有 SQL Server 測試執行到。
- 同一份計畫：重新匯入、重新配對與匯出報告寫不進操作紀錄時，改成回報成功並附提醒（`CommittedAuditLog`），SQL Server 案件也適用；
  原本 SQL Server 案件會回報整個動作失敗。失敗記在支援日誌的 `audit_log.append_failed` 事件。
  應用層測試用一定失敗的替身核對了提醒句與支援日誌，沒有 SQL Server 測試執行到。
- 2026-10-21 架構文件與圖表維護計畫：使用者裁定 GL(未納入測試) 的日期不符分成總帳日期空白、早於查核期間與晚於查核期間三種原因。
  `SqlServerProjectDatabase.Schema` 升到第 15 版，既有案件升級時和本機第 13 版共用 `GlExclusionReasonMigration.SplitPeriodReasonAsync` 的 SQL，
  帶 schema 前綴。`SqlServerDataPreviewRepository` 改成回傳四種原因與四個筆數，並接受只看一種原因；`SqlServerValidationRunRepository`
  的日期不符筆數改成三種日期原因的合計。`SqlServerDataPreviewRepositoryTests` 的原因與筆數預期，以及 `SqlServerSchemaMigrationTests`
  四處要連線的版本預期，都已改成新的記法，沒有執行；`SqlServerSchemaMigrationTests` 不連線的版本檢查在 Public 執行並通過。
  `ValidationRunLoggingTests` 的 SQL Server digest 仍是舊值，下一次執行 `Provider` 時預期失敗，要依實際 SQL 更新。
  這些 SQL Server 程式都沒有 SQL Server 測試執行到。

同一份計畫處理最後獨立複審的低嚴重度事項時，下列只屬於 SQL Server 的部分沒有改，等 SQL Server 開發重啟時一併處理：

- 設定檔損壞、舊版或期間無效的 SQL Server 案件，仍無法從案件清單刪除；本機案件已經可以刪除。
  SQL Server 案件還要先決定怎麼清掉伺服器上的資料與核對授權。
- SQL Server 刪案時，`SqlServerProjectDatabase.Deletion.cs` 的提交仍接受取消。伺服器已經提交、程式才收到取消時，
  畫面會回報已取消，案件資料夾卻留著。這是讀程式推論，沒有重現；本機兩種資料庫刪案時不理會取消，不會發生。
- 兩句只在 SQL Server 案件出現的畫面文字：建案時伺服器已有同名資料的訊息（`CaseCreateFactsPort.cs`）仍寫「專案 schema」
  與「projects 資料夾」等工程用語；案件已被他人開啟的訊息（`ProjectLockErrorMessage`）寫的開啟時間是 UTC，沒有換成本機時間。

原本尚未實作的兩項，SQL Server 的值概況不分大小寫與清單代碼的完整來源存在性核對，2026-10-19 已由跨資料庫一致性計畫補上，
並在 `Provider` 通過固定答案；罕用字元的大小寫仍和本機不同，規格見 `jet-guide.md` 第 8 節。`ValidationRunLoggingTests` 與 `PrescreenRunLoggingTests`
的 SQL Server digest 原本仍是舊值，2026-10-19 第二階段依實際 SQL 更新，並在 `Provider` 通過。

2026-10-22 技術債一輪把空白判定收成 AuditCore 的 `SqlBlankText`，共用的 `FilterCompilation`、`GlRulePredicates`、`ValidationProcedures`、
`AccountMappingPopulationQuery` 與 `TbMappedColumnAudit` 都改呼叫它，三種資料庫經由共用程式產生的 SQL 文字都跟著變。
SQL Server 專屬倉儲這輪只改了三處：`SqlServerNullRecordsPageRepository` 一句舊註解改成現況、`SqlServerValidationRunRepository`
把方言傳給共用的傳票號碼跨日重用查詢、`SqlServerPrescreenRunRepository` 刪掉只有測試在用的 `RunAsync` 相容入口；
倉儲裡手寫的空白判定沒有動。同類的舊註解還有一句，在 `SqlServerValidationRunRepository` 的空白紀錄計數，這輪的清單漏列，
2026-10-28 複審時改成現況。SQLite 與 DuckDB 的驗證與預篩選 SQL 快照摘要值已依第一次失敗重算；SQL Server 的沿用 2026-10-19 的值，沒有啟動服務，
`ValidationRunLoggingTests` 與 `PrescreenRunLoggingTests` 的註解寫明維持原值待核對，下次執行 `Provider` 時預期先失敗一次。
使用者 2026-10-22 裁定：「sql server 的部分要先繼續擱置了，目前都不會回去處理他的相關事項，如果你開發途中有什麼發現都請記錄在 sql server 相關的部分」，
所以從這一輪起，開發途中和 SQL Server 有關的發現都記在這一節。

2026-10-23 預篩選收斂計畫階段四和 SQL Server 有關的修改如下，都沒有啟動服務執行：

- `SqlServerGlRepository` 確認配對時的必填文字欄整欄空白偵測，原本手寫傳票號碼、會計科目編號、會計科目名稱與傳票摘要四欄，
  改成呼叫和本機 GL 倉儲共用的 `GlMappedColumnAudit.ReadAsync`，欄位清單由欄位目錄產生，SQL 文字因此和原本不同。
  `CrossDatabaseConsistencyTests` 新增的合成資料測試在 SQLite 與 DuckDB 核對了提醒內容與順序；
  同一則測試另寫了 SQL Server 版，下次執行 `Provider` 才會跑到。
- 預篩選改讀科目分類設定，讓未預期借貸組合的原因句寫內建分類目前的名稱。`PrescreenMonthlyAggregationBenchmarkTests`
  的 SQL Server 版因此補上 `SqlServerAccountTaxonomyStore`，只改測試的組裝，沒有執行。
- 第五步改成條件用到未配對的欄位一律擋下、第四步的原因句改由同一張需求表產生，都在三種資料庫共用的程式裡，
  SQL Server 的行為隨共用程式改變，沒有 SQL Server 測試核對。
- 使用者 2026-10-23 裁定併入階段四的「核准日不在期間」：傳票核准日不可用時改成無法執行，筆數由 0 改成 null 並寫原因，
  驗證報告改讀驗證結果寫 N/A。判斷寫在三種資料庫共用的驗證規劃與報告程式，`SqlServerValidationRunRepository` 與它的 SQL 沒有改；
  SQL Server 的行為隨共用程式改變，沒有 SQL Server 測試核對。驗證邏輯版本推進到 `validation-2026-10-23-v6`，
  SQL Server 案件的舊驗證結果同樣要重新驗證。

2026-10-23 預篩選收斂計畫階段五和 SQL Server 有關的修改如下，也都沒有啟動服務執行：

- `SqlServerDataPreviewRepository` 的 GL 與 GL(未納入測試) 頁改成依已確認的 GL 配對列欄位。查詢的欄位清單、攸關資料元素值的
  `LEFT JOIN` 與逐欄讀值，都由和本機共用的 `DataPreviewGlQuery` 依欄位計畫產生；表名帶 schema 前綴，分頁用 `OFFSET … FETCH NEXT`。
  原本手寫的 GL 九欄與 GL(未納入測試) 十欄查詢已移除，只共用 GL 九欄的 `DataPreviewColumns` 也刪掉了。
  過帳狀態與人工或自動分錄都改成有配對才列，兩頁一致。GL 頁統計列的兩個金額改由三種資料庫共用的處理器轉成十進位字串，
  SQL Server 倉儲的統計查詢沒有改。
- `SqlServerDataPreviewRepositoryTests` 的欄位與列預期隨新的欄序修改，種子資料另寫入一個文字型攸關資料元素值，核對它出現在 GL 頁；
  這些修改都沒有執行。SQLite 與 DuckDB 由新增的合成資料測試核對三種配對情況與三種攸關資料元素型別，SQL Server 的同一段查詢沒有測試執行到。

2026-10-24 預篩選收斂計畫階段六和 SQL Server 有關的修改如下，也都沒有啟動服務執行：

- 匯入來源表 `import_batch_source` 加 `header_row` 欄，保存審計員指定的欄名在第幾列；SQL Server 的建表與既有結構補欄都寫了，
  版本常數升到第 16 版。版本常數與 SQL 文字有不連線的測試核對，其餘沒有 SQL Server 測試執行到。
- 往下填補空白在 `GlProjectionDataReader` 與攸關資料元素第二遍的 SQL Server 路徑都包了同一層 Domain 狀態，和本機倉儲相同，
  沒有 SQL Server 測試執行到。

2026-10-24 預篩選收斂計畫階段七和 SQL Server 有關的修改如下，也都沒有啟動服務執行：

- 三種資料庫共用的 `DateNormalizer` 拿掉 Excel 序列值那一關，純數字不再當成日期。SQL Server 確認配對時的兩條投影路徑，
  `GlProjectionDataReader` 和 `SqlServerGlRepository`，都經由 `GlRowProjector` 用它；第五步的日期操作元走 `TypedFieldConditions`，同樣受影響。
  SQL Server 專屬的程式沒有改，行為隨共用程式改變，沒有 SQL Server 測試核對；SQLite 與 DuckDB 由 `PlainNumberDateWorkflowTests`
  與共用的日期固定答案核對。

2026-10-25 預篩選收斂計畫階段九和 SQL Server 有關的修改如下，也都沒有啟動服務執行：

- `SqlServerPrescreenRunRepository` 的 12 條逐筆規則改經 `PrescreenRulePredicates.Build` 依規則鍵取述詞，和本機倉儲同一張對應表；
  較少使用的科目的相異科目數改成去掉頭尾空白後計數並排除空白科目。`SqlServerFilterCommitRepository`、`SqlServerFilterRunMaterializer`、
  `SqlServerFilterRunRepository` 與 `SqlServerPrescreenPageRepository` 呼叫 `GlFilterWhereBuilder.BuildPlan` 時不再帶尾零模數；
  `PrescreenExecutionInput` 不再帶尾零門檻，門檻是規則定義的一部分。
- `ProviderParityJourneyTests` 裡兩種舊式筆數條件的情境改成 `entityFrequency`，`PrescreenExecutionInputTests` 的 SQL Server 測試改名並改預期；
  `PrescreenRunLoggingTests` 的 SQL Server 診斷摘要值沒有更新，重啟 SQL Server 時第一次執行會失敗，依當時輸出更新即可。
  以上都只改沒有執行，SQLite 與 DuckDB 的同一段改動由新增的固定答案測試核對。

2026-10-28 預篩選收斂計畫整體複審和 SQL Server 有關的修改如下，也都沒有啟動服務執行：

- `SqlServerDataPreviewRepository` 的 GL 與 GL(未納入測試) 頁改成把頁別交給共用的 `DataPreviewGlQuery`，分錄表名直接寫在 SQL 裡，
  母體條件由頁別決定，不再由呼叫端用字串傳入表名。GL 範圍的掃描檢查因此看得到這段查詢。
- `SqlServerValidationRunRepository` 的母體摘要，日期不符那組未納入原因改用 `GlEffectivePopulation.ExcludedByPeriodSqlPredicate`，
  和本機驗證倉儲同一份定義。SQLite 與 DuckDB 的驗證 SQL 快照摘要值已依第一次失敗重算；SQL Server 的維持原值，
  `ValidationRunLoggingTests` 的註解寫明待核對，下次執行 `Provider` 時預期先失敗一次。同一個倉儲空白紀錄計數的舊註解也在這次改成現況，
  見上面 2026-10-22 那一段。
- `SqlServerFilterRunRepository` 與 `SqlServerFilterRunMaterializer` 刪掉描述已移除的尾零模數的舊註解，只改註解。
- 第三步的往下填補改成配到同一個來源欄的欄位一起填補並一起列出，正規化寫在三種資料庫共用的配對選項；
  `SqlServerGlRepository` 確認配對時把填了幾格存進配對選項，和本機 GL 倉儲相同。
- `SqlServerInfSamplePageRepository` 多讀 `source_module` 與 `dr_cr` 兩欄。INF Report 主表與底稿 Step 2 經由共用的
  `InfSampleDisplayValues` 寫整理後的分錄來源與借貸代號，SQL Server 案件的報告隨共用程式改變。
- `SqlServerMappingValueProfileRepository` 的值概況多收往下填補參數，勾了時接上和本機共用的 `MappingValueProfileNormalization.FillDownCtes`，
  用 `COUNT … OVER` 與 `MAX … OVER` 在每份來源內依列序填補後再計數。`MappingValueProfileProviderParityTests` 新增的 SQL Server 版
  和本機同一組固定答案，下次執行 `Provider` 才會跑到；值是 `NVARCHAR(MAX)` 時視窗函數的寫法也要在那時確認。
- 以上 SQL Server 的部分都沒有測試執行到；SQLite 與 DuckDB 由 `GlFillDownWorkflowTests`、`MappingMetadataCodecTests`、
  `MappingValueProfileProviderParityTests` 與 GL 範圍的掃描檢查核對。

2026-10-30 專案紀錄收斂計畫階段四第一步和 SQL Server 有關的修改如下，依使用者裁定只經編譯，都沒有啟動服務執行：

- `SqlServerProjectDatabase.EnsureCreatedAsync` 遇到既有 schema 時只檢查版本，不再升版：拿掉 `MigrateExistingSchemaToCurrentAsync`
  的分版補資料段，以及它用到的 `BumpSchemaVersionSql`、`WidenMessageLogLevelCheckSql`、`PinAuditTextCollationSql`、
  `BackfillAccountMappingCategoryIdsSql` 與測試用的 `MigrationFaultHookForTests`。版本比現行第 16 版舊時回舊版案件的訊息，
  請審計員刪除案件後重建；版本值讀不出來或比現行版本新時，回版本資訊無法辨識。兩句訊息和本機資料庫共用 `ProjectFolderMessages`。
  新 schema 的建表語句 `SchemaSql` 沒有改，裡面守欄補欄的寫法留到統一改名時一起整理。
- 本機與 SQL Server 共用的升版程式 `AccountClassificationMigration`、`GlExclusionReasonMigration` 整個刪除，
  `AuditMutation` 拿掉升版專用的 `SchemaV7Migration` 與 `TextComparisonMigration`。
- `SqlServerProjectRegistry` 讀線上登錄的案件設定時，和本機設定檔一樣先檢查 `schemaVersion` 是第 2 版，不是就回舊版案件的訊息。
- `SqlServerSchemaMigrationTests` 刪掉只測升版補資料的五則連線測試，以及不連線測試裡對已刪除常數的三處檢查，
  新增一則連線測試核對舊版本被拒絕且資料不變；以上只經編譯，沒有執行。
- 匯入批次表的舊欄名 `source_file_path`（實際只存檔名）這次沒有改，留到統一改名時處理。

2026-10-30 專案紀錄收斂計畫階段四第二步統一改名時，和 SQL Server 有關的修改如下。依使用者裁定只經編譯，
都沒有啟動服務執行，也沒有在 SQL Server 上實際驗證；SQLite 與 DuckDB 的同一段改動由合成資料測試核對。

- `gl` 表的 `is_effective` 改成意思相反的 `is_excluded`：建表語句與補欄語句改名，`GlProjectionDataReader` 寫入時改寫
  「沒有納入測試」為 1，`SqlServerValidationRunRepository` 的母體摘要與 `SqlServerDataPreviewRepository` 的 GL(未納入測試) 統計跟著反轉條件。
  共用的 `GlExcludedPopulation.SqlPredicate` 與 `DataPreviewGlQuery` 也已反轉，SQL Server 的查詢隨共用程式改變。
- `gl_control_total` 原本存同一個數字的 `source_row_count` 與 `raw_row_count` 併成 `pbc_row_count`：建表語句只留一欄，
  `SqlServerGlRepository` 的 MERGE 只寫一次，拿掉這一欄的補欄語句；`GlProjectionDataReader.SourceRowCount` 改名 `PbcRowCount`。
- 驗證與預篩選的執行結果從 `result_rule_run` 分成 `validation_result` 與 `prescreen_result` 兩張表，欄位是 `result_id`、
  `generated_utc` 與 `summary_json`，拿掉 `run_kind` 與它的索引。`SqlServerResultStore` 依結果種類寫入與讀取各自的表，
  清除與失效的共用程式 `ResultReset`、`ResultStaleStateSql` 改成直接清或查各自的表。抽樣表 `inf_sample` 的 `run_id` 改名 `result_id`，
  `SqlServerInfSamplePageRepository` 跟著改。
- 完整性差異的共用 CTE 改由 `ValidationProcedures.CompletenessDiffCteFor` 在定義處組好 schema 限定的表名，不再用字串取代
  `FROM gl` 加前綴；CTE 的中間結果改名 `gl_by_account` 與 `tb_by_account`，避開和表名 `gl`、`tb` 相同。
  `SqlServerAccountMappingExportRepository`、兩個完整性分頁倉儲與 `SqlServerValidationRunRepository` 隨共用程式改變。
- SQL Server 的資料表隔離檢查改成只看 SQL 裡表引用的位置，`dbo.` 開頭的控制平面表視為已限定；這次沒有找到真的少了 schema 限定的地方。
- `SqlServerCollationProbeTests`、`SqlServerGlRepositoryTests` 等 SQL Server 測試的表名與欄名跟著改，以上只經編譯，沒有執行。
- 科目分類的程式名稱改掉 taxonomy：`SqlServerAccountCategoryStore` 記錄修改事件改用 `AuditMutation.AccountCategory`，
  分類仍被科目配對使用時回的錯誤碼改成 `category_in_use`；`PrescreenMonthlyAggregationBenchmarkTests` 的 SQL Server 版
  改用 `ProjectRepositories.AccountCategories` 組裝。SQL 沒有變，以上只經編譯，沒有執行。
- `gl` 表的傳票摘要欄 `document_description` 改名 `description`：建表語句、`GlProjectionDataReader` 的寫入欄清單，以及
  空值明細、篩選命中、INF 樣本、預篩選分頁與驗證摘要的 SQL Server 查詢跟著改；共用的 `ResultPageSorting`、`GlRulePredicates`、
  `FilterCompilation` 與欄位目錄也改用新欄名。這些查詢都只讀 `gl` 一張表，沒有和欄位資訊表 `field_info` 的 `description` 欄合併查詢。
- `SqlServerGlRepository` 寫入 `gl_control_total` 時，GL 筆數的參數 `@effective` 改名 `@glRowCount`。
- 攸關資料元素值型別的程式值從 `money` 改成 `amount`，和上一項已改的建表 CHECK 一致；SQL Server 的寫入與條件隨共用程式改變。
- `SqlServerSchemaMigrationTests` 的定序檢查改成只看指定資料表的建表與補欄語句，避免把 `field_info.description` 算進 `gl` 的欄；
  `SqlServerCollationProbeTests` 與 `SqlServerDataPreviewRepositoryTests` 的欄名與回應鍵跟著改，以上只經編譯，沒有執行。
- 改名改了表名、欄名與 CHECK 值，`SqlServerProjectDatabase.SchemaVersion` 由第 16 版升到第 17 版，`SchemaSql` 寫入的版本跟著改，
  改名前建立的 SQL Server 案件開啟時會看到舊版案件的訊息。`SqlServerSchemaMigrationTests` 兩則不連線的版本檢查改成 17，
  在本機的全部 C# 測試裡執行並通過；要連線的測試只經編譯，沒有執行。
- SQL Server 的資料表隔離檢查補上兩種寫法：逗號分隔的 FROM 清單裡逗號後面的表，以及省略 INTO 的 `INSERT 表名`。
  補上後掃描現有的 SQL Server 程式，沒有找到少了 schema 限定的地方。
- 三種資料庫共用的傳票明細不再回 `evidenceConditions`，條件述詞的 `Primary` 旗標也拿掉，因為現行寫法只產生主要條件與整張傳票條件；
  SQL Server 的傳票明細隨共用程式改變，沒有 SQL Server 測試執行到。
- `FilterHitLoggingTests`、`ValidationRunLoggingTests` 與 `PrescreenRunLoggingTests` 的 SQLite 與 DuckDB 摘要值依改名後的 SQL 更新；
  SQL Server 的摘要值維持原值，下次執行 `Provider` 時預期先失敗一次，再依實際 SQL 更新。
- 控制平面的操作紀錄表 `dbo.audit_log` 改名 `dbo.operation_log`，寫入它的 `SqlServerAuditLog` 改名 `SqlServerOperationLog`，和本機資料庫的
  `operation_log` 同名。建表語句仍是表不存在才建立，所以已經建好控制平面的 SQL Server 會另外建一張空的 `dbo.operation_log`，
  舊的 `dbo.audit_log` 留在資料庫裡，不再寫入；重新啟動 SQL Server 開發時，要決定舊紀錄搬不搬、舊表刪不刪。
  支援日誌的寫入失敗事件 `audit_log.append_failed` 也改成 `operation_log.append_failed`。
- 操作種類的常數類別 `ProjectAuditOperations` 改名 `ProjectOperations`，`SqlServerProjectOperationLog` 跟著改；
  建表語句裡高風險條件彙總那個索引的註解，不再寫「tag 矩陣」。以上只經編譯，沒有執行。

2026-10-31 專案紀錄收斂計畫階段四第三步把三條預篩選規則改成和舊版 IDEA 相同，和 SQL Server 有關的修改如下。
依使用者裁定只經編譯，都沒有啟動服務執行，也沒有在 SQL Server 上實際驗證；SQLite 與 DuckDB 的同一段改動由合成資料測試核對。

- 第四步拿掉編製分錄較少的人員與使用較少的科目兩條規則：`SqlServerPrescreenRunRepository` 刪掉這兩條的計數查詢，
  以及在全部 GL 計算 11 筆以下相異科目數的查詢，回應不再帶這三個數字。
- 未預期借貸組合要求同張傳票至少有一筆金額大於 0 的分錄，這個條件加在共用的 `GlRulePredicates.UnexpectedAccountPair`，
  SQL Server 的第四步計數、第四步明細、預篩選報告與第五步條件隨共用程式改變。
- 第四步計數、明細與預篩選報告的 R3 改成列出命中傳票的全部分錄：`SqlServerPrescreenRunRepository` 與
  `SqlServerPrescreenPageRepository` 改用和本機相同的 `LocalPrescreenPageRepository.HitRowsPredicate` 包住規則述詞，
  子查詢的表名帶 schema 限定。
- `ProviderParityJourneyTests` 刪掉只在 SQL Server 執行的使用較少的科目跨資料庫比對，`DemoRuleOracleTests` 的 SQL Server 版
  拿掉兩條規則的斷言，未預期借貸組合改數整張傳票；`SqlServerGlPopulationScopeGoldenTests` 的未預期借貸組合改成不命中，
  和本機版相同。以上只經編譯，沒有執行。
- `PrescreenRunLoggingTests` 的 SQLite 與 DuckDB 摘要值依新的 SQL 更新；SQL Server 的摘要值維持原值，
  下次執行 `Provider` 時預期先失敗一次，再依實際 SQL 更新。

2026-10-31 專案紀錄收斂計畫階段四第四步拿掉三處依業務擋下審計員的檢查：沒有 TB 也能往下做、情境名稱與動機留白時自動補上、
沒有勾選或沒有儲存篩選情境也能匯出底稿。這一步沒有改到只給 SQL Server 用的程式，SQL Server 隨三種資料庫共用的程式一起改變，
依使用者裁定只經編譯，沒有啟動服務執行，也沒有在 SQL Server 上實際驗證；SQLite 與 DuckDB 的同一段改動由合成資料測試核對。

- 資料驗證多讀 TB 的匯入紀錄，用來分辨沒有匯入 TB 與 TB 欄位配對還沒確認；逐科目比對無法執行時，資料驗證報告與底稿的
  `WorkingPaperPlanningFactsPort` 不再讀差異科目，底稿也不再要求 TB 的欄位定義。這些都是 SQL Server 與本機共用的程式。
- 底稿沒有勾選情境時，第三步與第四步的查詢沿用共用的 `HighRiskSummaryScenarioSqlScope`，它本來就把空的情境清單當成沒有資料。
- 情境名稱與動機的自動文字在 Application 層產生，和資料庫無關。

2026-10-31 專案紀錄收斂計畫階段四第五步照裁定 2026-10-30-20 改寫畫面上的「可能」，其中一句只在 SQL Server 案件出現：
建立 SQL Server 案件時，名稱在 SQL Server 上已有資料、這台電腦卻沒有登記。`ProjectCreateFactsPort` 的訊息改成
「…但這台電腦沒有這個案件的登記，例如案件資料夾被刪除過，或案件是在其他電腦建立的。請改用其他名稱；要沿用這個名稱，先清除 SQL Server 上的舊資料。」
`DualSourceProjectTests` 與 `ProjectHandlersTests` 裡三個 SQL Server 測試的比對字串跟著改成「這台電腦沒有這個案件的登記」。
依使用者裁定只經編譯，沒有啟動服務執行這三個測試，也沒有在 SQL Server 上實際驗證。
同一步照裁定 2026-10-30-19 讓畫面上的數量加千分位，SQL Server 連線後的容量提醒「資料庫容量偏大：單庫 … MB」裡的容量、線上案件數與門檻也改經 `Ui.countText`，
這一句同樣沒有在 SQL Server 上實際看過。

2026-10-31 專案紀錄收斂計畫階段四第六步讓儲存情境時只計算並存下這一個情境的結果，檢視、移除與匯出都讀存好的結果。
SQL Server 照同樣的規則改，依使用者裁定只經編譯，沒有啟動服務執行，也沒有在 SQL Server 上實際驗證；
SQLite 與 DuckDB 的同一段改動由合成資料測試核對。

- 建表程式 `SqlServerProjectDatabase.Schema.cs`：`filter_scenario` 改用不變的情境編號 `scenario_id` 當主鍵，`position` 只記畫面順序；
  `filter_hit` 的 `scenario_position` 改成 `scenario_id`，索引 `idx_filter_hit_entry` 跟著改；新增 `filter_hit_condition`，
  存命中傳票裡每筆分錄符合第幾組第幾條。結構版本由 17 升到 18，舊案件照現行訊息請審計員刪除後重新建立。
- `SqlServerFilterCommitRepository` 新增只儲存一個情境的寫法，整批發布也一起寫 `filter_hit_condition`；
  `SqlServerFilterScenarioStore` 新增移除一個情境的寫法，同一個交易刪掉它的定義與結果，後面的情境順序往前補；
  `SqlServerFilterHitMaterializer` 改用和本機共用的 `FilterHitInsertSql` 寫入兩張結果表。
- `SqlServerFilterVoucherRepository` 新增讀已儲存情境的傳票頁，SQL 和本機共用 `FilterVoucherPageReader`。
- 讀 `filter_hit` 的查詢，包括 `SqlServerFilterHitsPageRepository`、`SqlServerHighRiskSummaryScenariosRepository`、
  共用的高風險條件彙總與底稿第四步查詢，都改成接上 `filter_scenario` 取情境位置。
- 記錄 SQL 雜湊的測試只鎖預覽、資料驗證與預篩選的 SQL，這一步沒有改到這些 SQL；SQLite 與 DuckDB 的雜湊照舊通過，
  SQL Server 的雜湊沒有改，也沒有執行核對。

2026-10-31 專案紀錄收斂計畫階段四第七步拿掉建表程式 `SqlServerProjectDatabase.Schema.cs` 裡剩下的 16 處補欄語句，
也就是欄位不存在時用 `ALTER TABLE` 補上的寫法；這照第四階段確認頁 5-2 的回答「改成一份建表程式，只留版本檢查」與 5-3「一起改，只經編譯並記錄」。
其中 14 處的欄位本來就寫在建表語句裡。`account_category.parent_category_id` 與 `account_mapping.classification_explicit`
原本只靠補欄語句加上，先照 SQLite 的寫法併進建表語句的最後一欄，再拿掉補欄語句。新建案件的資料表和改之前相同，所以結構版本不變。
依使用者裁定只經編譯，沒有啟動服務執行，也沒有在 SQL Server 上實際驗證。

2026-11-01 專案紀錄收斂計畫照第三個停點的回覆改名：驗證摘要裡同一傳票號碼出現在不同總帳日期的鍵名改用 voucher，
條件的錯誤碼與支援日誌欄位改用 condition，錯誤碼 `no_target_data` 改成 `gl_mapping_not_confirmed`。
SQL Server 的驗證讀取程式 `SqlServerValidationRunRepository` 跟著改名；存好的驗證摘要和舊案件不同，
所以建表程式 `SqlServerProjectDatabase.Schema.cs` 的結構版本由 18 升到 19，舊案件照現行訊息請審計員刪除後重新建立。
同一次照第三個停點 2-2 的回覆，共用資料庫的六張管理表照現行名稱列進名詞表，名稱檢查一併核對共用資料庫的建表程式
`SqlServerControlPlaneSchema.cs` 與 `SqlServerUserDirectory.cs`。依使用者裁定只經編譯，沒有啟動服務執行，也沒有在 SQL Server 上實際驗證。
