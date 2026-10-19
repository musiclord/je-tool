# 跨資料庫審計結果一致性整合計畫

更新日期：2026-10-19
狀態：第一階段、第二階段與之後的兩項補充都已完成，待使用者驗收。2026-10-20 使用者指示收尾並提交推送，計畫移到歷史文件；後續的 SQL Server 事項同日封存在 [SQL Server 封存事項](../2026-10-20-sqlserver-archive.md)。2026-10-19 依使用者裁定在本機 SQL Server
執行三次完整 `Provider`，最後一次全部通過，結果寫在文末「最後一次完整驗證」。

## 使用者需求原文

2026-10-16：

> 為了確保整體架構一致性，現在可以幫我「稍微」整合 sql server 的業務邏輯，不需要完善雲端的服務系統，只要先確保整個 `je-tool` 的資料庫邏輯標準一致就可以了，也就是說，目前是想要確保跨資料庫的審計業務可以達到標準一致的結果為先，避免各個資料庫因為特性差異而影響到審計業務差異，比如小數點、資料型別、資料庫引擎、資料庫語法等等。

## 目標與不做的事

同一份合成資料在 SQLite、DuckDB 與 SQL Server 要得到相同的審計結果，包括金額小數、日期、文字比對、空白判定、排序、
分頁與彙總。修正一律放在三種資料庫共用的定義，例如 `ISqlDialect` 方言、共用 SQL 或連線設定，不為單一資料庫特化業務規則。
每項修正先寫一個會失敗的跨資料庫測試，再修改產品；只有 SQL Server 才會出錯的項目，測試在本機 SQL Server 上先失敗。

本輪不做企業多人、雲端或部署相關工作，這些範圍原本記在 `docs/sqlserver-enterprise-deferred.md`，2026-10-20 已併入 [SQL Server 封存事項](../2026-10-20-sqlserver-archive.md)。
不改審計規則本身，也不新增會擋住審計員的錯誤。

## 審計員看到的結果

審計員的操作步驟與畫面不變。修正後，同一份資料不論存在哪一種資料庫，報告與摘要的列順序、只列前 50 列時列進哪些科目、
文字條件命中哪些分錄、編製者怎麼分組，以及週末判定都相同。SQL Server 案件的來源值清單也改成不分大小寫，
並能核對清單代碼是否出現在完整來源。

有兩處行為會改變：

- 文字欄超過共用上限時，SQLite 與 DuckDB 也改成匯入失敗。錯誤訊息寫出欄位、列號與上限，
  審計員縮短來源資料或改選正確的欄位後重新匯入即可。
- 既有 SQL Server 案件第一次開啟時升級到第 14 版，驗證、預篩選、抽樣、篩選命中與已存情境會清除，審計員重新執行即可。

## 盤點方法

- 讀 `ISqlDialect` 與三個方言實作、`AuditCore` 與 `Infrastructure/Sql` 的共用 SQL、本機倉儲，以及 SQL Server 的資料表定義。
- 另由一個唯讀研究代理逐檔比對 SQL Server 倉儲與本機倉儲各自寫的 SQL，結果寫在第四節。
- 用專案相同版本的 DuckDB.NET 與 Microsoft.Data.Sqlite 套件寫探針程式，實測兩個本機資料庫的引擎行為。
- 使用者啟動本機 SQL Server 後，用唯讀查詢量測版本、定序，以及兩種定序的 `UPPER` 和 .NET 逐字元比對。
  探針都放在本次 session 的暫存目錄，不進儲存庫。

## 盤點結果

### 一、已有共用定義或測試保護

| 項目 | 現況 | 保護 |
|:---|:---|:---|
| 金額與小數 | 三種資料庫的金額都存成整數（`*_scaled`，SQL Server 是 `BIGINT`）。小數轉整數與四捨五入只在 .NET 的 `MoneyScaling` 做一次；SQL 裡沒有 `ROUND`、`AVG`、浮點或小數型別。匯入時檢查借方與貸方合計不超過整數上限，所以之後任何部分合計都不會溢位。 | 金額轉換的單元測試，以及 `ProviderParityJourneyTests` 以整數金額比對整個工作流程。 |
| 整數除法 | DuckDB 的 `/` 會回小數，SQLite 與 SQL Server 回整數。SQL 裡的整數除法只出現在 `ISqlDialect.IntegerQuotient`，DuckDB 用 `//`。實測 DuckDB `7 / 2` 是 3.5、SQLite 是 3。 | `GlRuleSqlEquivalenceTests` 的連續零尾數固定答案。 |
| 日期 | 三種資料庫的日期都存成 `yyyy-MM-dd` 文字，由 .NET 的 `DateNormalizer` 解析；日期大小比較是文字比較。週末、每月幾日與當月天數走方言。 | `GlRuleSqlEquivalenceTests` 的週末與假日固定答案、`FilterSideAndMonthWorkflowTests` 的月初月底條件。 |
| 空白判定 | 判斷空白、比較文字與分組都經過 `ISqlDialect.Trim`，去掉的字元和 .NET `Trim()` 相同。讀檔程式在存入前也先用 .NET `Trim()` 修剪每個儲存格。 | 本機兩種資料庫的測試，以及 `Provider` 的 SQL Server 測試。 |
| 文字定序與尾端空白 | SQL Server 參與比對、分組與排序的文字欄都固定 `COLLATE Latin1_General_BIN2`，和 SQLite、DuckDB 一樣依字元碼排序並區分大小寫。實測 SQLite 與 DuckDB 的 `'1101' = '1101 '` 都不成立；SQL Server 的 `=` 會忽略尾端半形空白，但讀檔時已修剪，篩選條件值也先修剪，所以碰不到。 | `SqlServerCollationProbeTests`，在 `Provider` 執行。 |
| 文字條件的萬用字元 | 共用 `GlRulePredicates` 的 `LIKE` 一律跳脫 `%`、`_`、`[`。SQLite 的 `LIKE` 對英文字母不分大小寫，DuckDB 區分，但兩邊都先轉大寫，結果不受影響。空字串關鍵字在組 SQL 前就排除。 | `FilterSinglePurpose*EquivalenceTests`、`TypedFieldProviderParityTests`。 |
| 分頁 | 明細分頁的 `KeysetPaging` 用 `CASE WHEN ... IS NULL` 明確把空值排最後，不依賴引擎預設；完整性科目表也明確把空白科目排最前。 | `KeysetPagingProviderParityTests`、`DetailPaginationContractTests`。 |
| 小數轉整數 | 實測 DuckDB 的 `CAST(3.5 AS INTEGER)` 是 4，SQLite 是 3。JET 的 SQL 只對整數或日期字串的數字部分做這種轉換，目前碰不到。 | 沒有專門測試，靠目前的寫法。 |
| 整個工作流程 | 同一份示範案件走完建案到篩選，比對畫面回傳結果。 | `ProviderParityJourneyTests`：SQLite 對 DuckDB 每次 `Public` 都跑；SQLite 對 SQL Server 在 `Provider` 執行。 |
| 自訂欄位文字長度 | 攸關資料元素（RDE）的文字值在三種資料庫共用 450 字上限，超過時投影就回列錯誤。 | `GlRdeStorageLimits` 與投影測試。 |

### 二、確定不一致（已實測引擎行為）

**A. DuckDB 遞增排序把空值放最後。** 實測 `ORDER BY x` 時，SQLite 把 NULL 排最前，DuckDB 排最後；遞減時兩者都排最後。
SQL Server 的預設和 SQLite 相同。沒有明確指定空值位置的排序，遇到空白科目就會不同。從程式判讀，受影響的有三處：

- 驗證報告用的科目使用彙總（`LocalAccountUsageExportRepository`）：`ORDER BY COUNT(*), account_code`，空白科目和其他科目筆數相同時，列的先後不同。
- 預篩選的少用科目清單（`LocalPrescreenRunRepository`）：`ORDER BY COUNT(*) ASC, account_code` 再取前 50 列，超過 50 個少用科目時，被截掉的那一列可能不同。
- 驗證摘要的完整性差異前 50 列（`LocalValidationRunRepository`）：`ORDER BY ABS(tb_s - gl_s) DESC, account_code`，空白科目和其他科目差額相同時，順序與截斷可能不同。

**B. DuckDB 的 `upper` 對兩個字元和 .NET 不同。** 逐一比對全部 Unicode 字元，DuckDB `upper` 和 .NET `ToUpperInvariant()` 只差兩個字元：
`ß` 會變成 `ẞ`，`ı` 會變成 `I`；.NET 兩者都不變。SQLite 的連線已把 `upper` 換成 .NET 的轉換，所以 SQLite 和 .NET 一致。
影響範圍是所有先轉大寫再比較的條件，例如文字欄位條件、關鍵字、編製者與核准者是否同一人、授權人員比對與人員彙總。
實測換成 .NET 轉換後，對一百萬列做轉大寫比對，內建函式約 27 毫秒，.NET 轉換約 85 毫秒。

### 三、需要 SQL Server 實機確認的項目

2026-10-19 量測的本機執行個體是 SQL Server 2022 Developer，版本 16.0.1210.3，相容性層級 160。

**C. SQL Server 標準文字欄有長度上限，本機沒有。** SQL Server 的傳票號碼、科目編號是 `NVARCHAR(450)`；項次、科目名稱、來源模組、
編製者、核准者與過帳狀態是 `NVARCHAR(400)`。本機原本沒有上限，投影也只檢查自訂欄位的長度，所以超長值在 SQL Server 匯入失敗，本機會成功。
SQL Server 的參數也帶固定長度，依官方文件，值超過長度時會直接截斷，見第四節。

**D. SQL Server 的 `UPPER` 和 .NET 不同。** 逐字元比對基本多語言平面：文字欄現用的 `Latin1_General_BIN2` 有 546 個字元和 .NET 不同，
`Latin1_General_100_BIN2` 降到 308 個。新版定序下，ASCII、全形英數、中文與基本西里爾字母都和 .NET 相同，Latin-1 與 Latin Extended-A
的字母只有 `µ`、`ı`、`ſ` 不同。其餘差異集中在喬治亞文、切羅基文、西里爾字母的補充與擴充區，以及擴充拉丁字母；
例如 `ı` 在 SQL Server 會轉成 `I`，`µ` 不轉成希臘字母 `Μ`，喬治亞文小寫字母不轉大寫。補充平面的字元 SQL Server 一律不轉大寫。
`Latin1_General_100_CI_AS_SC` 的差異數同樣是 308 個。舊定序轉錯、新版轉對的字元中，常見的是羅馬尼亞文的 `ș` 與 `ț`。
SQL Server 不能像 SQLite 與 DuckDB 那樣替換 `UPPER`，所以要由使用者裁定做法，見「使用者裁定」。

另外，SQL Server 的值概況原本依原值分組，區分大小寫；也沒有核對清單代碼是否出現在完整來源的查詢。這兩項見第四節。

**E. SQL Server 的週末判定對 1900 年以前的日期出錯。** `SqlServerDialect.WeekendPredicate` 原本用「距 1900-01-01 的天數除以 7 的餘數」判斷星期，
1900 年以前天數是負數，SQL Server 的餘數也是負數，週六、週日就判斷不到。`DateNormalizer` 接受西元 1 年起的日期，核准日寫錯成 1899 年時會碰到。
實測修正後 1899-12-31 得到週日。

**F. SQL Server 只顯示的文字欄沿用資料庫預設定序。** 摘要、科目名稱、來源模組與項次原本沒有指定定序。量測結果是 `JET_Test` 的預設定序
是 `Latin1_General_BIN2`，伺服器的預設定序是 `Chinese_Taiwan_Stroke_CI_AS`。JET 自己建立資料庫時一律指定 `Latin1_General_BIN2`，
只有事先用其他定序建好的資料庫，對這些欄位下文字條件時才會和本機不同。

**G. 罕用字的排序。** SQL Server 的 `Latin1_General_BIN2` 依 UTF-16 編碼排序，SQLite 與 DuckDB 依 UTF-8 位元組排序。
兩者只在補充平面的罕用字和全形符號比較時順序不同。影響很小，列出供參考。

### 四、SQL Server 與本機各自寫的查詢

研究代理逐對比較了 45 組同名的本機倉儲與 SQL Server 倉儲。多數 SQL Server 倉儲已改用共用 SQL，或照抄同一份 SQL，業務語意一致。
它找到的差異和前三節重疊的部分不重列，只補充下列幾項。重要的幾項已由主對話回到程式核對。

| 差異 | 位置 | 觸發條件與影響 | 判斷 |
|:---|:---|:---|:---|
| 欄位配對的值概況分組不同 | `LocalMappingValueProfileRepository` 依 `jet_mapping_ordinal_key` 分組，也就是 .NET 的修剪加不分大小寫；`SqlServerMappingValueProfileRepository` 原本直接依原值分組。 | 同一欄有 `a01` 和 `A01` 時，SQL Server 的相異值數與前幾名不同。 | 確定，本輪已修正。 |
| SQL Server 沒有來源值存在性核對 | SQL Server 倉儲原本沒有 `FindMissingValuesAsync`，畫面回「尚未核對」。 | 每次要求核對清單代碼時。 | 確定，本輪已補上。 |
| SQL Server 參數帶固定長度 | 科目配對的科目編號 450、科目名稱 400，授權人員名稱 450，情境名稱 400，分類名稱 400，假日名稱 256，TB 科目編號 450、科目名稱 400。 | 值超過長度時，SqlClient 依官方文件會直接截斷，本機保存完整值。截斷後的科目編號可能和 GL 對不上。 | 和第三節 C 一起處理。情境名稱是畫面輸入，列在延後項目；分類名稱的輸入框已限 400 字。 |
| 取最新一次驗證或預篩選結果的平手鍵不同 | 本機 `ORDER BY generated_utc DESC, rowid DESC`，SQL Server `ORDER BY generated_utc DESC, run_id DESC`。 | 只有兩次執行的時間完全相同才會不同。時間記錄到 100 奈秒，實際幾乎碰不到。 | 確定寫法不同，影響極低，只記錄。 |
| 匯入批次保存的來源路徑不同 | 本機 `source_file_path` 存檔名，SQL Server 存完整路徑。 | 找不到讀回這一欄的 SQL，不影響審計結果；但 SQL Server 資料庫會保存本機絕對路徑。 | 確定寫法不同，不屬於本輪的審計結果範圍，只記錄。 |

另外，假日檔同一天同時是假日與補班日時，兩邊的資料預覽都只依日期排序，換頁順序不固定；這是兩邊共同的問題，不是資料庫之間的差異。
研究代理沒有逐行比對共用 SQL 建構器內部，只確認兩邊都呼叫同一份。

## 使用者裁定

盤點後，Agent 建議先做不改變對外行為、改一處共用定義就能一致的項目，也就是 A、B、E；C 的做法會改變某一種資料庫目前的行為，
D、F 與第四節的值概況要 SQL Server 實機才能確認。2026-10-19 使用者在對話視窗的選項中選定：

- 本輪範圍，四項全選：「DuckDB 空值排序與大寫（建議）」「SQL Server 週末判定（建議）」「文字欄長度三方一致」「SQL Server 實機才能確認的項」。
  其中文字欄長度的做法，選項說明是「投影時三種資料庫共用同一上限，超長值改成列錯誤（本機目前能匯入的超長值會變成匯入失敗）」。
- 計畫並存：「兩份並列，交付後整理（建議）」。這份計畫放在 `docs/specs/`，開發現況的目前大型計畫補一句指向它；
  分頁計畫的檔案與狀態不動，等使用者決定 Git 交付後再移回歷史。
- SQL Server：「執行一次並收尾」。選項說明是啟動 `MSSQLSERVER`，以 Windows 整合驗證連本機 `JET_Test`，連線字串只放在當次程序環境；
  跑完不論成敗都停止服務並確認沒有 `sqlservr.exe`；開發現況已預期會先失敗的 SQL Server 測試，以及範圍外的失敗，只記錄，不在本輪修。
- 實作前的量測：「先短暫量測（建議）」。選項說明是只執行唯讀查詢，量出版本、`JET_Test` 的預設定序，以及兩種定序的 `UPPER` 和 .NET 逐字元比對。
- 服務啟停：Agent 的 shell 沒有系統管理員權限，啟動服務被 Windows 拒絕。使用者選「我啟動，一路做完再停（建議）」：
  使用者以系統管理員權限啟動服務，Agent 量測、完成大寫對照表與值概況、執行 `Provider`，結束後請使用者停止服務，Agent 再確認沒有 `sqlservr.exe`。
- SQL Server 的大寫規則，問題是「SQL Server 的 UPPER 無法和 .NET 完全一致。三種資料庫的大寫規則要怎麼統一？」。
  使用者選「SQL Server 改用新版定序（建議）」，選項說明是「SQL Server 的轉大寫改走方言、用 Latin1_General_100_BIN2，差異從 546 個降到 308 個；ASCII、中文、全形英數與常見歐洲字母三方一致。本機行為不變；μ、ı、ſ 與罕用文字記為已知差異。值概況不分大小寫與來源核對也用同一規則補上。」
  沒有選的兩個選項是三方共用一份大寫對照表，以及維持現狀只記錄。

## 實作與驗證

| 範圍 | 做法 | 測試 |
|:---|:---|:---|
| A 空值排序 | `DuckDbProjectDatabase` 的一般與唯讀連線字串都加上 `default_null_order=nulls_first_on_asc_last_on_desc`。 | `CrossDatabaseConsistencyTests` 核對連線本身的排序，以及科目使用彙總在空白科目與其他科目同筆數時的列序，三種資料庫都執行。 |
| B 本機轉大寫 | 新增 `DuckDbInvariantUpper`，DuckDB 轉接器每次開啟連線時檢查 `upper('ß')`，還是內建版本就換成 .NET 的轉換。 | 含 `ß`、`ı`、`café`、`ș` 的摘要，核對等於與包含條件命中的傳票。 |
| D SQL Server 轉大寫 | `ISqlDialect` 新增 `Upper`。SQLite 與 DuckDB 照舊寫 `UPPER(x)`；SQL Server 寫成 `UPPER((x) COLLATE Latin1_General_100_BIN2)`。共用述詞、不分大小寫的包含比對、授權人員比對、編製者彙總與值概況都改走它，SQL 裡不再直接寫 `UPPER`。 | 上一列的大寫案例在 SQL Server 也執行，`ı` 的兩個案例依裁定只在本機執行；編製者彙總核對 `Ștefan` 與 `ștefan` 合為一人。`SqlDialectTests`、`KeysetPagingTests` 與 `FilterCompilationTests` 固定 SQL 文字，本機的 SQL 文字不變。 |
| 值概況與來源核對 | SQL Server 的值概況改成去空白後依方言轉大寫分組，顯示值取同組碼位最小的寫法；新增 `FindMissingValuesAsync`，比對鍵和分組相同。 | `MappingValueProfileProviderParityTests` 的 SQL Server 版改成和本機同一組固定答案，另新增三種資料庫共用的來源核對案例。 |
| C 文字長度 | 新增 `TextStorageLimits`：傳票號碼、科目編號、授權人員名稱與自訂欄位文字 450 字，項次、科目名稱、來源模組、編製者、核准者與過帳狀態 400 字，假日名稱 256 字，摘要不限。GL 與 TB 投影超過上限回列錯誤，科目配對、授權人員名單與假日檔整批不儲存並寫出列號；SQL Server 的參數長度改用同一組常數。錯誤訊息裡過長的值只顯示前 50 字加「…」。 | `CrossDatabaseConsistencyTests` 在三種資料庫核對超過上限的錯誤與剛好等於上限的完整讀回；`SqlServerTextStorageLimitTests` 核對建表語法的欄寬和常數一致。 |
| E 週末判定 | `SqlServerDialect.WeekendPredicate` 改成先加 7 再取餘數。 | `SqlDialectTests` 兩個既有測試的預期 SQL 跟著修改，原因寫在測試旁；跨資料庫測試用 1899-12-29 到 1900-01-08 的核准日，三種資料庫都執行。 |
| F 文字欄定序 | SQL Server 結構升到第 14 版：GL 的項次、科目名稱、摘要、來源模組，TB 與科目配對的科目名稱，以及抽樣結果的項次固定 `Latin1_General_BIN2`。既有結構逐欄改定序，並新增 `TextComparisonMigration` 變更種類，比照第 7 版升級清除驗證、預篩選、抽樣、篩選命中與已存情境；D 的轉大寫同版更換，舊結果也由這次清除涵蓋。 | 純文字測試 `TextComparisonMigration_PinsAuditTextColumnsAtVersionFourteen`；`SqlServerCollationProbeTests` 登記這 7 欄；四處要連線的版本預期改成 14。從第 7 版升級的測試原本預期驗證結果保留，第一次 `Provider` 時失敗，改成預期結果清除並標成待重跑，原因寫在測試旁。 |

修改既有測試時，原因與第一次失敗的證據寫在各測試旁。D 與值概況的 SQL Server 測試，第一次失敗是修改前直接執行測試程式連本機 SQL Server 取得，
沒有收據，結果同樣寫在測試旁；第 7 版升級測試的第一次失敗留在第一次 `Provider` 的收據。

第一次 `Provider` 的本機平行比對 26 項全部通過；SQL Server 269 項中 256 項通過，本輪新增或修改的 SQL Server 測試都通過。
失敗的 13 項出在既有測試過時，由第二階段處理；最後一段的 LocalDB 檢查排在 SQL Server 測試之後，那次沒有執行。

## 延後與只記錄的項目

- SQL Server 轉大寫仍和 .NET 不同的 308 個字元，以及補充平面字元不轉大寫（第三節 D），依使用者裁定記為已知差異；規格寫在 `jet-guide.md` 第 8 節。
- 第五步情境名稱是審計員在畫面輸入的文字，前後端都沒有長度上限，SQL Server 的參數會截斷到 400 字。不屬於匯入資料，本輪沒有處理。
  第二階段已改成三種資料庫共用 400 字上限。
- G 的罕用字排序、取最新結果的平手鍵、匯入批次的來源路徑，只記錄。第二階段已修正平手鍵與來源路徑，G 維持已知差異。
- 本機 `JET_Test` 有 109 個 `prj_` 開頭的資料結構，是先前的測試執行留下的；本輪沒有刪除。第二階段清除時實際有 110 個，已依裁定全部清除。

## 第二階段：三種資料庫都滿足審計業務（2026-10-19，原 session 接手）

使用者 2026-10-19 原話：「我想先在這裡直接修復 sql server 和 duckdb 等相關問題，我沒有要完善整個 sql server 雲端的管理系統，而是要先將目前支援的三個資料庫 sql server, duckdb, sqlite 都能滿足 `je-tool` 的審計業務」。

同日在對話視窗的選項中裁定：

- 情境名稱：「三種共用 400 字上限 (Recommended)」。選項說明是情境名稱輸入框最多輸入 400 字，後端用共用常數檢查，做法和已限 400 字的分類名稱相同；不改資料表結構，SQL Server 不再截斷。
- SQLite 取消：「這輪一起修 (Recommended)」。選項說明是先用真實的長語句寫一個會失敗的取消測試，再把取消接到 SQLite 的中斷機制，並確認不影響既有功能。
- `JET_Test` 的殘留資料結構：「刪除殘留 (Recommended)」。選項說明是只刪 `prj_` 開頭、而且不在任何案件登錄裡的測試資料結構，刪除前先列出數量。
- SQL Server 啟動時機：「現在就啟動」。使用者以系統管理員權限啟動服務，一路開著直到做完，再由使用者停止，Agent 確認沒有殘留程序。

本階段範圍：

1. 第一階段 `Provider` 失敗的 13 項。依根因分成三類：測試自建的案件設定缺少抽樣種子版本（10 項）、第 6 版升級測試仍預期保留情境（1 項）、
   驗證與預篩選日誌的 SQL 比對字串與指紋過時（2 項）。都先核對產品行為是否正確，再決定改測試或改產品。
2. `Provider` 最後一段的 LocalDB 檢查要實際跑出結果。
3. SQL Server 測試結束後沒有刪除自己建立的資料結構，先修好收尾，再刪除既有殘留；一併查清第一次刪除資料結構逾時的原因。
4. 情境名稱共用 400 字上限。
5. 取最新一次驗證或預篩選結果時，三種資料庫改用同一個平手鍵。
6. 匯入批次保存的來源路徑，SQL Server 改成和本機一樣只存檔名，不在資料庫留下本機絕對路徑。
7. 開發現況記載的 SQL Server 重新啟用前置項目：空白科目配對範本補防止退回舊寫法的測試，跨資料庫範本比對的合成資料加入空白科目編號。
8. SQLite 長語句的取消。
9. `tools/README.md` 的 SQL Server 收尾步驟補上全文檢索服務 `MSSQLFDLauncher`；第一階段結束時它仍在執行。

維持已裁定的已知差異，不在本階段處理：SQL Server 轉大寫與 .NET 不同的 308 個罕用字元，以及第三節 G 的罕用字排序。

同日另一題裁定 `JET_Test` 的清除範圍：「全部清除 (Recommended)」。選項說明是刪除 19 個無登錄的資料結構，以及 143 筆測試登錄和
它們的 91 個資料結構與授權列，`JET_Test` 回到沒有案件的狀態；只動 `JET_Test`，不碰其他資料庫。

## 第二階段結果（2026-10-19）

編號對應上面的範圍。

1. 13 項失敗都出在測試本身過時，產品行為正確，所以改的是測試。第一次失敗的收據是 `20261019-030507727-5b8944f69c1749f28f33146ea1c9369a`。
   - 自建案件缺少抽樣種子版本的 10 項：測試的案件設定補上種子與現行演算法版本，手寫的 `project.json` 也補上。
     `DualSourceProjectTests` 另有兩處預期訊息，改成 2026-10-14 第三輪確認的句子。
   - 第 6 版升級的 1 項：改成和本機第 7 版升級測試相同的預期，情境清空，篩選回到從未執行。
   - 日誌的 2 項：預篩選的分組比對字串改成依資料庫方言判斷；兩項的 SQL Server 指紋依實際 SQL 更新，測試內寫明原因。
2. LocalDB 檢查：這次 `Provider` 跑到最後一段，`SqlServerExpressPhaseOutTests.EnsureCreated_OnExpressEngine_ThrowsUnsupported` 通過。
3. 測試留下的資料結構：清除前有 110 個 `prj_` 資料結構。其中 19 個不在登錄裡，依建立日期，18 個是 7 到 8 月建立的，
   1 個是 10 月 19 日建立的；後者可能是當天刪除逾時留下的，未確認。另有 143 筆登錄、143 筆授權與 91 筆案件鎖，
   登錄都是 7 到 8 月建立的。這些全部是測試的合成案件，依裁定全部清除；`audit_log`、`app_user` 與 `app_config` 保留。
   - 現行測試都會刪除自己建立的資料：之後的單項診斷與完整 `Provider` 跑完，四項計數都是 0，所以測試的收尾不需要修改。
   - 第一次刪除資料結構逾時的原因仍未確認。這次 277 項 SQL Server 測試與多次單項診斷都沒有再出現；SQL Server 的 `system_health`
     沒有記到超過 30 秒的鎖等待，預設追蹤也沒有資料檔或記錄檔自動成長的紀錄。
   - 另外記到兩次寫入 GL 暫存表時等待查詢記憶體配給，分別等了 106 秒與 21 秒，都在單獨執行 SQL Server 測試的時段；
     稍後在 `Provider` 執行時查詢，這台電腦的 32 GB 記憶體只剩約 2 GB 可用。暫存表的叢集主鍵是 `(batch_id, row_number)`，`SqlBulkCopy` 沒有給排序提示，
     SQL Server 寫入前會先排序，所以需要記憶體配給；大量寫入不設逾時，只會等待，不會失敗。這和刪除逾時有沒有關係沒有證據。
     等待屬於效能問題，不影響審計結果，本輪不修。
4. 情境名稱：`FilterScenarioLimits.MaxNameLength` 改用 `TextStorageLimits` 的 400 字。`FilterScenario.Validate` 在儲存時檢查這個上限，
   SQL Server 的情境名稱參數也改用同一個常數；畫面的名稱輸入欄加上 `maxlength`。第一次失敗的收據是
   `20261019-050518934-099cf72d6ca647908fca9e6604e03b33`，修正後 `20261019-050644184-0d614c9426ee4a1b849a3fd7761c30de` 通過，
   `ScenarioNameLimitProviderTests` 的 SQL Server 版也在實機通過。`SqlServerTextStorageLimitTests` 另核對資料表欄寬等於這個上限。
5. 平手鍵：`LocalRuleRunStore` 由 `rowid DESC` 改成 `run_id DESC`，和 `SqlServerRuleRunStore` 相同。`RuleRunLatestTieBreakProviderTests`
   三種資料庫都通過；暫時改回舊排序時本機兩項失敗，收據 `20261019-052347684-4acf4b7cc32e4d1ca578ed215e848e84`。
6. 來源路徑：SQL Server 的 GL 與 TB 匯入、科目配對匯入都改存檔名。SQL Server 的匯入、科目配對與授權人員三個存放層原本不收斂檔名，
   現在和本機一樣先只取最後一段。`ImportSourcePortabilityProviderTests` 三種資料庫都通過；暫時拿掉 SQL Server 的收斂時，
   SQL Server 存進完整路徑，測試失敗。沒有任何查詢讀回這一欄。
7. 空白科目範本：`AccountMappingTemplateExportTests` 的空白科目範本測試改成三種資料庫共用同一段檢查，範本列的編號與名稱都對同一組固定答案。
   SQL Server 版暫時改回舊查詢時，讀到空白科目編號丟出 `SqlNullValueException`，測試失敗。
8. SQLite 取消：
   - 第一次失敗的收據是 `20261019-053544183-355616838b7e4e6aaab0832e02473f2f`：SQLite 的讀取、純量與寫入三種執行方式，
     都要等測試自己的 30 秒中斷才停，丟出 `SqliteException`；DuckDB 三項通過。
   - 原因：Microsoft.Data.Sqlite 10.0.13 的 `SqliteCommand.Cancel()` 不做任何事。讀取的非同步執行只在開始前檢查一次取消；
     其餘非同步執行沿用基底類別，取消時呼叫的就是這個空的 `Cancel()`。
   - 做法：新增 `CancellableSqliteConnection`，`SqliteProjectDatabase` 建立的連線都改用它。它建立的命令在執行期間把取消接到
     `sqlite3_interrupt`，中斷錯誤轉成 `OperationCanceledException`，執行呼叫返回前解除登記。
   - 影響評估：SQLite 連線只在一處建立，型別仍是 `SqliteConnection`，既有的型別判斷照常成立。每次執行登記一次取消，
     和基底類別原本的做法相同。交易內被中斷的寫入由 SQLite 整筆回復，`SqliteTransaction` 認得這種外部回復，不會再回復一次；
     同一條連線之後照常可用。回復與連線可用都由新測試確認。
   - 修正後 SQLite 三種執行方式都在取消送出後立即停下，整個案例約 0.21 到 0.24 秒（取消在 0.2 秒送出）。
   - 同一組測試在 SQL Server 半秒內停下，但驅動丟出 `SqlException`，經 bridge 會變成非預期錯誤。dispatcher 在 action 出口加一條規則：
     已要求取消時，業務錯誤以外的例外都回報成取消。暫時拿掉這條規則時，新的單元測試失敗，收據
     `20261019-054538590-e2f8242e1f8e46b5a126b19fa9b8a66b`。
   - 測試：`LongStatementCancellationProviderTests` 在三種資料庫核對停下時間、交易回復、連線可用，以及經 dispatcher 到畫面的錯誤碼；
     `SqlServerEngineErrorsTests` 新增取消後的驅動例外與業務錯誤兩項。
   - SQLite 連線的確切型別因此改變。既有的 `WorkpaperStep41PreparedExportSessionTests.PreparedSession_CancellationInsideMaterialization_DisposesDedicatedConnection`
     原本斷言 `SqliteConnection`，改成斷言 `CancellableSqliteConnection`，原因與第一次失敗的收據寫在測試旁。
9. `tools/README.md` 的收尾步驟補上 `MSSQLFDLauncher`。它是全文檢索的啟動服務，Windows 沒有把它登記成引擎的相依服務，
   停止引擎不會連帶停止它。

同步更新 `jet-guide.md` 第 8 節、`action-contract-manifest.md`、`jet-frontend-description.md` 與開發現況。

第二階段的 `Provider` 全部通過：本機平行比對 26 項、SQL Server 277 項、LocalDB 與 Express 檢查 1 項。

## 第二階段之後：資料形狀測試的時間假設（2026-10-19）

使用者 2026-10-19 原話：「在這裡執行此任務：「移除資料形狀測試的 3 秒時間假設」」。任務來自第二階段完整驗證時 `Public` 的第一次執行：
`ActionShapeContractTests` 因 DuckDB 那條流程的報告清單更新超過 3 秒而失敗。

- 原因：變更與匯出 action 回應前會重新整理報告清單，期限是 3 秒，超過就回 `reportArtifactWarning` 警告字串，`reportArtifacts` 為 null。
  `ActionShapeContractTests` 的合成流程假設清單一定在期限內更新完；機器慢時改走逾時分支，記到的形狀就和契約檔不同。
- 重現：`ReportExportSupport` 的清單更新期限改用可替換的時間來源 `CatalogRefreshClock`，只在同一個非同步流程內生效，
  正式程式一律用系統時間。資料形狀測試改成在「期限一開始就到期」的時間來源下執行，第一次失敗的收據是
  `20261019-064658409-72601a7febfc4fb0b8a43dc95ee61455`：`export.validationArtifacts` 走到清單沒有更新的分支，宣告走不到的型別被記到。
- 修正：`ActionShapeJourney.RecordAsync` 把清單更新期限固定成不會到期，流程一定走到清單更新完成；報告清單檔被其他程式占用的情況
  照常由 `accountTaxonomy.save` 那一步走到。修正後同一個測試通過，收據 `20261019-064859740-3872862e5eb04d88ae8ff4ccb8cfba3c`；
  記到的形狀和契約檔逐字相同，契約檔與前端核對都不必改。
- 開發現況「已知但延後的事項」的「資料形狀契約測試依賴報告清單在 3 秒內更新」已移除。

## 第二階段之後：篩選條件等價測試的 SQL Server 版（2026-10-19）

使用者 2026-10-19 在對話視窗的選項中選「文件與測試都補」，送出的內容是：「修正計畫文件，並補上 SQL Server 版的篩選條件等價測試，
逐項核對「只經編譯」清單哪些已有 SQL Server 測試涵蓋。要執行 SQL Server 測試時再請我啟動服務。」

- 原因：`FilterSinglePurposeEquivalenceTests`、`FilterSinglePurposeSideEquivalenceTests` 與 `FilterSinglePurposeVoucherEquivalenceTests`
  核對 2026-10-14 篩選條件改為每條只表達一件事之後，改寫前後的條件命中相同的分錄。這三個類別原本只在 SQLite 與 DuckDB 執行。
- 做法：三個類別的檢查改成共用，SQL Server 版用同一份合成資料，比對同一組固定答案。金額尾數零的固定答案原本逐列寫在
  本機版的測試參數裡，現在移到檔案中的共用清單，答案不變。`FilterPredicateProviderFixture` 的 SQL Server 版補上傳票明細的查詢，
  原本只有本機版有。
- 結果：新增的 7 項 SQL Server 測試第一次執行就全部通過。SQL Server 的命中分錄、傳票摘要與明細列，都和本機的固定答案相同；
  產品程式沒有修改。本機版的 14 項照常通過。
- 只經編譯清單的核對：逐項比對 `Provider` 實際執行的 SQL Server 測試與測試程式，結果寫在開發現況的
  「SQL Server 開發暫緩期間的修改」。開發現況原本有兩處說 SQL Server 測試「沒有執行」，實際上都已在第二次 `Provider` 執行並通過，
  已經更正。
  直接使用篩選條件測試夾具的 20 個測試類別中，仍有 11 個只在 SQLite 與 DuckDB 執行，記在開發現況「已知但延後的事項」的
  「SQL Server 實機驗證」。

## 最後一次完整驗證

2026-10-19 補上篩選條件等價測試的 SQL Server 版之後執行：

- `Provider` 在本機 SQL Server 2022 Developer 的 `JET_Test` 執行，以 Windows 整合驗證連線，連線字串不含帳號密碼。
  連線字串用環境變數傳給當次程序，但設定它的命令文字直接寫出了連線字串，不符合 `tools/README.md`「不得寫入儲存庫、設定範例、
  命令列、聊天或長期保存的收據」的規定。這次 `Provider` 的收據沒有留下連線字串。
  本機平行比對 26 項、SQL Server 284 項、LocalDB 與 Express 檢查 1 項，全部通過；SQL Server 的 284 項包含新增的 7 項。
  執行後 `JET_Test` 的測試資料結構、案件登錄、授權與案件鎖都是 0。
- `Public`：.NET 測試 5,120 項全部執行並通過，前端的欄位對應與預覽檢查也通過。
- `Contract -ContractScenario FrontendMapping` 在最後一次修改產品程式的行為之後執行，通過。之後除了測試與文件，
  還把 `SqlDialect.cs`、`SqlServerProjectDatabase.Schema.cs` 與 `SqlServerMessages.cs` 三處「只經編譯」的註解，
  改成這次核對的實際涵蓋。這三處只改註解，改完後 `Build` 通過，沒有重跑測試。
- `Documentation` 在文件改完後執行，通過；唯一的寫作提醒在沒有修改的歷史文件 `docs/history/specs/2026-10-06-kct-gl-requirements-plan.md`，
  先前幾次檢查就有。
- 沒有執行 `Gui`、`ReleaseCandidate`、`Package`、`Excel` 與 `PrivateCase`。
- 驗證結束後，使用者以系統管理員權限停止服務。第一次停止後 `MSSQLFDLauncher` 仍在執行，原因未確認；引擎停止後，
  使用者再執行 `Stop-Service -Name MSSQLFDLauncher -Force` 才停下。Agent 重新讀取狀態：`MSSQLSERVER`、`SQLSERVERAGENT`、
  `MSSQLLaunchpad` 與 `MSSQLFDLauncher` 都是停止、手動啟動，也沒有 `sqlservr.exe` 或 `fdlauncher.exe`。

## 進度與下一步

- 2026-10-19 完成第一階段的盤點、使用者裁定、實作與 SQL Server 實機驗證。
- 2026-10-19 完成第二階段九項，`Provider` 全部通過。
- 2026-10-19 移除資料形狀測試的 3 秒時間假設；補上篩選條件等價測試的 SQL Server 版，並逐項核對 SQL Server 開發暫緩期間的修改。
- 下一步：使用者驗收；Git 交付由使用者決定，交付後再整理這份計畫與分頁計畫。
