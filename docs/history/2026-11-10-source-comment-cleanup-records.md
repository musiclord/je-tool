# 2026-11-10 原始碼註解整理的做法與檢查紀錄

這份紀錄說明 2026-11-10 怎麼把 `src/JET` 原始碼註解裡的開發紀錄拿掉，以及做了哪些檢查。
依據是裁定 2026-11-10-11；計畫檔 [首次 SAT 文件製作計畫](../specs/2026-11-06-sat-initial-documents-plan.md)
的「同日追加：原始碼註解不寫開發紀錄」只留摘要，細節寫在這裡。整理前的註解原文在
[`2026-11-10-source-comment-records.txt`](2026-11-10-source-comment-records.txt)。
第一階段之後，同日到 2026-11-11 又做了三輪改寫與逐段核對，寫在最後一節「後續整理」。

## 為什麼做

送審只提交 `src/JET`（裁定 2026-11-10-09），審查者看到的原始碼不該帶著開發過程的紀錄。
整理前，`src/JET` 的註解裡有裁定編號、測試收據、開發日期、計畫與階段的名稱，以及 `docs/`、`tools/` 這些其他目錄的路徑。
這些內容在只有 `src/JET` 的代碼庫裡查不到出處。

## 做法

整理分成四段，每一段做完都比對一次，確認註解以外的內容沒有變。

1. **保存原文。** 先複製一份整理前的 `src/JET` 當比對基準，再把含裁定編號、測試收據或修改經過的註解原文匯出到原文紀錄：
   656 個檔、2,432 段註解，提到 318 則裁定。
2. **用規則刪除。** 括號裡只有裁定編號的地方，以及「第一次失敗」加收據編號的句子，用固定規則刪掉：491 個檔、1,529 段。
   刪除後有 71 行變成以標點開頭，把標點移回上一行。
3. **第一輪改寫。** 其餘還帶編號、日期、計畫用語或其他目錄路徑的 1,681 段，分成 12 批交給子代理改寫。
   子代理只拿到註解和前後幾行，把結果寫成純文字，由程式檢查格式後套用：改寫 1,599 段、刪除 38 段、保留 44 段，共 567 個檔。
4. **第二輪改寫。** 再挑出不帶編號、但用字像在講修改經過的 268 段，分成 4 批改寫：改寫 161 段、刪除 5 段、保留 102 段，改到 102 個檔。
   保留的是描述執行時狀態的句子，例如「焦點回到原本的按鈕」。這一輪改寫前的文字附在原文紀錄的第二部分。

改寫的規則是：歷史句改成現況句；整段只有歷史就刪除；沒有問題的句子不動；不加入原註解與前後文看不到的事實。

## 註解以外一起改的地方

下面這些不是註解，但同樣帶著編號或修改經過，一起改掉。除了第一項，都在測試專案。

| 檔案 | 位置 | 改了什麼 |
|:---|:---|:---|
| `JET/Infrastructure/Persistence/Sqlite/SqliteProjectDatabase.cs` | 建表語句裡的 SQL 註解 | 拿掉括號裡的裁定編號。新案件資料庫裡保存的建表語句文字因此少了這個括號；程式不比對這段文字 |
| `tests/JET.Tests/Application/PrescreenRuleFixedAnswerTests.cs` | 測試失敗時顯示的案例名稱 | 拿掉括號裡的裁定編號 |
| `tests/JET.Tests/Infrastructure/LegacyAppearanceRegistryTests.cs` 與 `Infrastructure/Fixtures/legacy-appearance-registry.json` | 五個項目的說明文字與核對它的斷言 | 說明改成不帶編號的句子，斷言跟著改；原因與第一次失敗記在 [`test-change-records.md`](test-change-records.md) |
| `tests/JET.Tests/Infrastructure/GlPopulationScopeGoldenTests.cs`、`RevenueCounterpartRelationshipTests.cs` | 測試資料 SQL 裡的註解 | 刪掉收據那一行，裁定編號與修改經過改成現況說法 |
| `tests/JET.Tests/Infrastructure/` 的 7 個測試檔與 `Application/FilterScenarioSaveBranchTests.cs` | 測試資料 SQL 裡的註解 | 拿掉開發日期與「原本」「改成」的說法；上一列的兩個檔也在這 7 個之內 |
| `tests/JET.Tests/Fixtures/date-normalization.json` | `description` 與 `note` 的文字 | 拿掉計畫名稱與裁定日期；「以前是某日期」改成「當成序列值是某日期」。`note` 不影響測試 |

## 之後怎麼防

- 新增 `SourceCommentPolicyTests`：掃描方案資料夾的 `.cs`、`.js`、`.css`、`.html`、`.csproj` 與 `.json`，
  不能出現裁定編號、測試執行的編號，或「第一次失敗」這類字樣。它不需要方案資料夾以外的檔案，只有 `src/JET` 時也會執行。
- `AGENTS.md` 與 `.agents/harness/development-workflow.md` 改成：註解只寫現在的行為與理由；
  修改既有測試的原因與第一次失敗的收據記在 [`test-change-records.md`](test-change-records.md)。
  2026-11-11 起第一次失敗改用文字寫，不寫收據編號（裁定 2026-11-11-12）。
- `docs/rulings.md` 的說明改成：`src/JET` 的註解不寫編號，要查某則裁定落實在哪裡看「落實位置」欄。

## 檢查

- **註解以外沒有變。** 用 Roslyn 把整理前後的 1,076 個 C# 檔逐檔轉成語彙比對，四種條件編譯的組合都比。
  有差異的只有上一節列的 11 個 C# 檔，差異都在字串裡；另 1 個是新增的 `SourceCommentPolicyTests`。其他 28 個檔（JavaScript、CSS、HTML 與專案檔）去掉註解後逐字相同；
  22 個 JavaScript 檔另外通過 `node --check`。
- **建置。** `Build` 通過。
- **完整公開測試。** 第一次執行時 `LocalProjectLockHandlerTests.Delete_LocalProjectHeldByOtherHost_RejectsWithoutDeletingData` 失敗 1 個，
  刪除案件後資料夾還在。當時同一台電腦正在副本上跑另一輪測試。
  這個測試檔的程式碼和整理前相同；用 `Focused` 單獨重跑這個類別通過。
  副本的測試結束後重跑 `Public` 通過：C# 5,029 個案例全部通過，兩個前端步驟通過。
  第一次失敗的原因沒有再追查。
- **契約檢查。** `Contract` 通過。
- **只有 `src/JET` 的副本。** 把 `src/JET` 的 1,180 個檔案複製到儲存庫以外的暫存資料夾，不含建置輸出。
  `dotnet build JET.slnx -c Debug` 成功，0 個警告。執行測試程式並排除三組標記：5,014 個案例全部通過，比整理前多的 5 個是 `SourceCommentPolicyTests`。
- **測試報告。** 照送審文件寫的命令在工作樹執行測試程式並收集涵蓋率：5,014 個案例全部通過，C# 行涵蓋率 40,859 行對 44,015 行。
  `source/test-record.json` 與三份 Excel 重新產生，讀回確認 Unit test Report 的案例數是 5,014。Word 文件沒有重新產生，內容沒有變。
- **文件檢查。** `Documentation` 通過，0 個錯誤；6 個用字提醒都在這次沒有改的段落。寫完這一行後再執行一次確認，結果相同。
- 沒有執行 `Gui`、`Excel`、`Package` 與 `PrivateCase`。沒有 stage、commit 或 push。

## 第一階段沒有處理的事

下面是第一階段結束時留下的事。後來處理到哪裡，寫在每一項的最後。

- 測試方法名稱 `SummariesSeparatorsStepNumbersAndFileSize_FollowEarlierUserRulings` 與
  `Defaults_RetainLegacySimplifiedVariants_WithoutAgentAddedKeyword` 沒有改名。後來在「測試名稱」一節改名。
- 測試程式裡為了讀取其他目錄而寫的路徑字串沒有動，例如 `GuiScenarioCoverageTests` 讀 `tools/harness/gui-driver`，這個測試帶 `DevelopmentRepository` 標記。
  `LegacyAuditParityComparator` 用 `docs/jet-guide.md §2` 這類字串標示比對依據，也沒有動。後續整理同樣沒有動。
- 還有 147 段註解提到已停用的 DuckDB 或 SQL Server。多數在說明程式為什麼這樣寫，有些說的程式可能已經不在；
  要逐段對照程式才能判斷，這次沒有處理。後來在第三輪處理。
- 用字像歷史、但判斷是在描述執行時狀態而保留的註解，沒有逐段再請第二個人看過。後來在第五輪逐段重新判斷。
- 改寫時子代理另外指出一些和歷史無關的問題，例如說明和程式可能對不上的註解、沒有呼叫端的輔助方法，
  以及 `SchemaIsolationGuardTests` 的說明仍在講已移除的 SQL Server。這些不是這次的範圍，沒有修改。
  後來逐段核對後修正，`SchemaIsolationGuardTests` 的類別說明也改成現況；這個測試後來刪除，見「刪掉的測試與樣式」。

## 後續整理

第一階段之後，同日到 2026-11-11 又做了三輪改寫，並逐段核對註解和程式是否相符。
依據是裁定 2026-11-10-16、2026-11-10-17 與 2026-11-10-18。改寫前的原文附在原文紀錄的第三到第五部分，以及各部分的補充。舊版程式的行號另依裁定 2026-11-11-17 改寫，原文在第六部分。
2026-11-11 另外查到原本就有的開發經過註解，改寫前的原文在第七部分。
註解裡引用文件章節的寫法依裁定 2026-11-12-01 拿掉章節編號，改寫前的原文在第八部分。
使用者 2026-11-12 回覆的幾件註解修改與註解檢查的擴充，依裁定 2026-11-12-16 到 2026-11-12-22 做，改寫前的原文在第九部分。

### 做法

1. **第三輪：已停用的資料庫。** 挑出提到 DuckDB、SQL Server 或多種資料庫並存的註解，共 140 個檔、242 段：
   改寫 181 段、刪除 31 段、保留 30 段。只為這兩種資料庫寫的說明刪掉，說明多種資料庫並存的句子改成只有 SQLite 的現況；
   說明 JET 現在怎麼處理以前建立的 DuckDB 或 SQL Server 案件的註解照舊保留。
   為了日後接回這兩種資料庫而保留的寫法，理由改寫在下面「為日後接回其他資料庫保留的寫法」。
2. **第四輪：測試裡的修改經過。** 挑出測試收據的「首敗」編號、裁定清單的條號，以及「斷言保留」這類句子，共 117 段：
   改寫 41 段、刪除 72 段、保留 4 段，改到 91 個檔。
3. **逐段核對。** 第四輪前後，逐段核對子代理指出可能和程式不符的註解，改成和現在的程式相符：59 個檔、78 段，
   另有 25 處逐項記錄的修正。同時刪掉沒有呼叫端的程式：四個已經沒有測試方法的測試檔、12 個測試檔裡的輔助方法，
   以及底稿寫入程式裡沒有用到的 `DerivedFieldFlags`。另外發現提交 `61d4b43` 連同 DuckDB 一起刪掉了五個 SQLite 固定答案的核對，
   補回成新的測試。測試的刪除與補回記在 [`test-change-records.md`](test-change-records.md)。
4. **XML 文件註解。** 用產生 XML 文件的設定建置一次，修正 16 處引用不存在的名稱或格式錯誤的文件註解。
5. **第五輪：用歷史語氣寫的註解。** 用「原本、不再、改成、改用、改為、改由、改回、已移除、以前、新版」這類字挑出 587 段：
   產品程式 390 段、測試 197 段。這些字大多在描述現在的行為，保留 399 段。產品程式改寫 114 段，在 44 個檔；
   測試改寫 72 段、刪除 2 段，在 60 個檔。每一段改寫結果都逐段核對過，子代理沿用過時說法的地方，對照程式後另外改寫。
6. **第五輪之後的殘留。** `SourceCommentPolicyTests` 加入「計畫與階段的名稱」之後，再改掉 7 段註解、7 則失敗訊息與例外訊息，
   以及測試資料裡的階段名稱；7 段註解裡有 1 段是英文的歷史說法，另外用英文字詞掃過一次才找到。
   另有 4 段說明註解在 `///` 後面補上空格。

### 註解以外一起改的地方

| 檔案 | 位置 | 改了什麼 |
|:---|:---|:---|
| `JET/Infrastructure/Export/WorkingPaperWriter.FixedSheets.cs` | 沒有用到的 `DerivedFieldFlags` | 刪除 |
| `tests/JET.Tests/Application/AccountMappingTemplateExportTests.cs`、`PrescreenDirectTemplatePackageTests.cs`、`ValidationDirectTemplatePackageTests.cs`，以及 `Infrastructure/WorkingPaperWriterTestSupport.cs` | 測試失敗時顯示的訊息與例外訊息 | 拿掉 `Stage 8`、`Track O`、`Track W` 這些開發階段的代號 |
| `tests/JET.Tests/Application/PrescreenDirectTemplatePackageTests.cs` | 合成資料裡的文字 | `Stage9` 換成同樣長度的 `Sample` |
| `tests/JET.Tests/Application/ReportExportTests.cs` | 合成情境的說明 `rationale` | 「鎖定 Phase 6 枚舉式可見差異」換成「核對條件說明的文字」；沒有斷言比對這段文字 |
| `tests/JET.Tests/Infrastructure/Fixtures/legacy-appearance-registry.json` | `extractionMode` 的說明 | 去掉 `Stage 3`；測試只核對它不是空白 |
| `tests/JET.Tests/Infrastructure/PrescreenExecutionInputTests.cs` | 一個測試方法 | 註解說會核對 0 的個數照請求帶出，原本沒有核對，補上這個斷言 |
| 其他測試檔 | 沒有呼叫端的輔助方法、已經沒有測試方法的檔案，以及補回的固定答案核對 | 都記在 [`test-change-records.md`](test-change-records.md) |

### 為日後接回其他資料庫保留的寫法

[短期上線只保留 SQLite 計畫](../specs/2026-11-06-sqlite-only-release-plan.md)原本把說明這些寫法的註解留在程式旁，留給日後恢復時用。
使用者 2026-11-10 回覆「這應該歸給專案紀錄，盡量避免原始代碼中過度註解說明」（裁定 2026-11-10-16），
第三輪因此把這些說明從註解拿掉，原文在原文紀錄第三部分。下面列出現在的 SQLite 程式裡仍為此保留的寫法，以及原本註解寫的理由；
行號是 2026-11-11 的行號，路徑以 `src/JET` 為起點。接回 DuckDB 或 SQL Server 時先看這裡，再看提交 `7e88b7b` 的實作。

| 位置 | 保留的寫法 | 理由 |
|:---|:---|:---|
| `JET/AuditCore/ISqlDialect.cs` | 方言介面只放各資料庫確實不同的 SQL 片段，例如週末判定、不分大小寫包含、轉大寫、去頭尾空白、整數商與取前 N 列 | 週末判定 SQLite 用 strftime、SQL Server 用 DATEPART；DuckDB 的 `/` 會回浮點，整數商要用 `//`；SQL Server 取前 N 列要寫 `OFFSET 0 ROWS FETCH NEXT`。各資料庫原生的 TRIM 去掉的字元不同，判斷空白與比較文字都要經過方言的 `Trim`，三種資料庫才會給相同答案 |
| 方言的 `ProviderName`，`JET/Infrastructure/Persistence/Local/` 的 7 個資料庫實作都用它 | 診斷紀錄上的資料庫標籤由方言提供，不寫死 | 這些實作原本由 SQLite 與 DuckDB 共用 |
| `JET/Infrastructure/Persistence/Local/LocalPrescreenRunRepository.cs` 第 294 行、`LocalVoucherBalancePageRepository.cs` 第 81 行，以及 `JET/Infrastructure/Sql/WorkingPaperStep4StreamReader.cs` 第 79 行 | 讀彙總值時也接受 `BigInteger` | DuckDB 的彙總與 SUM 可能回 `BigInteger`，SQLite 回 `Int64`。第三個檔沒有註解說明，寫法相同 |
| `JET/Infrastructure/Sql/CompletenessAccountPageQuery.cs` 第 61 行 | 旗標用 `Convert.ToInt64` 讀 | 本機的資料庫回 BIGINT，SQL Server 的常數 0 與 1 是 INT |
| `JET/AuditCore/ValidationProcedures.cs` 第 54 行 | 計數函式由參數 `countFunction` 傳入，預設 `COUNT` | 保留給 SQL Server 傳 `COUNT_BIG`，避免大量列計數溢位 |
| 名稱含 `schemaPrefix` 或 `tablePrefix` 的參數：`JET/AuditCore/` 的 `FilterCompilation.cs`、`GlRulePredicates*.cs`、`PrescreenRulePredicates.cs` 與 `ValidationProcedures.cs`，`JET/Infrastructure/Persistence/` 的 `DataPreviewGlQuery.cs`，以及 `Local/` 的 `LocalFilterCommitRepository.cs`、`LocalFilterHitMaterializer.cs` 與 `LocalFilterScenarioStore.cs`；另有 `LocalMappingStateStore.GlTotalsQuery` 的表名參數 | 共用 SQL 在資料表名稱前留限定詞的位置，SQLite 傳空字串 | 保留給 SQL Server 傳 schema 限定詞；SQL Server 是一個資料庫裡每個案件一個 schema。測試的 `SqlExecutionSnapshot` 也有 `schemaPrefix` 參數，目前沒有呼叫端傳值 |
| `JET/AuditCore/GlRulePredicates.Values.cs` 第 138 到 141 行 | LIKE 的條件值除了跳脫字元、`%` 與 `_`，也跳脫 `[` | SQL Server 把方括號當成萬用字元 |
| `JET/Infrastructure/Sql/FilterSqlPlanBinding.cs` 的 `BindParametersUsedBy`，以及 `LocalFilterScenarioStore.cs` 第 89 到 90 行 | 只綁定語句用到的參數 | DuckDB 遇到語句沒用到的參數會拒絕執行 |
| `JET/Infrastructure/Persistence/Local/LocalMappingStateStore.cs` 第 133 行起 | 第一個讀取器關掉之後，才讀控制總數 | DuckDB 與 SQL Server 不必在同一個連線上同時開兩個讀取器 |
| `JET/Infrastructure/Persistence/Local/IBulkRowWriter.cs` | 批量寫入分成 `AppendAsync` 與 `CompleteAsync` 兩步；SQLite 的 `CompleteAsync` 什麼都不做 | DuckDB 的實作用 appender 緩衝寫入，到 `CompleteAsync` 才把剩下的資料寫進交易 |
| `JET/Domain/Contracts/TextStorageLimits.cs` | 匯入文字上限：有索引的鍵 450 字、一般文字 400 字、假日名稱 256 字 | 上限照 SQL Server 的 `NVARCHAR(n)` 欄寬訂定，SQL Server 索引鍵上限是 900 位元組。SQLite 沒有欄寬，先套用同一上限，日後接回 SQL Server 時兩邊才會一致。系統規格第 8 節也寫了這一點 |

### 檢查

- **第五輪前後，註解以外沒有變。** 第五輪前另存一份 `src/JET` 當基準，2026-11-11 用 Roslyn 比對 1,071 個 C# 檔：
  註解以外不同的只有 7 個檔，也就是上一節列的 6 個測試檔，以及加入新樣式的 `SourceCommentPolicyTests`。
  其他 28 個前端與專案檔去掉註解後逐字相同；`src/JET` 的 22 個 JavaScript 檔都通過 `node --check`。
- **新加的樣式抓得到殘留的文字。** `SourceCommentPolicyTests` 加入「計畫與階段的名稱」後，`Focused` 失敗 1 個。
  失敗的是 `ProductAndTestSources_DoNotCarryDevelopmentRecords`，它列出符合「計畫與階段的名稱」的位置，包括 `JET/wwwroot/css/app.css` 與測試檔。改完殘留的文字後通過。
- **XML 文件註解。** 第四項修正後，`src/JET/Directory.Build.props` 讓每次建置都核對文件註解；先放一個引用不存在名稱的文件註解，
  `Build` 失敗，拿掉後通過。
- **第三輪起，註解以外的改動都有紀錄。** 用第三輪前另存的 `src/JET` 比對：1,070 個 C# 檔裡註解以外不同的有 32 個，另有 7 個搬走或刪掉的檔與 1 個新檔。
  逐檔對照，都是計畫檔「同日追加：Gui 測試內容移出、註解再整理與測試先行」與 [`test-change-records.md`](test-change-records.md) 記下的改動。
- **建置與測試。** `Public` 通過，C# 5,041 個案例；只有 `src/JET` 的副本建置 0 個警告，
  排除三組標記後 5,026 個案例全部通過。其他檢查寫在計畫檔的同一小節。
- 沒有執行 `Gui`、`Excel` 與 `PrivateCase`。沒有 stage、commit 或 push。

### 測試名稱

這一節原本列在「還沒處理」：測試的名稱帶著開發過程的說法，改名要先寫進名詞表，經使用者確認。
使用者 2026-11-11 回覆「照提議採用」（裁定 2026-11-11-03），照名詞表「C# 測試的名稱」一列改成下表的名稱。
只改名稱，測試內容與斷言不變；7 個測試類別的檔名跟著類別名稱改。

| 原名稱 | 新名稱 |
|:---|:---|
| `TerminologyUnificationFrontendTests.SummariesSeparatorsStepNumbersAndFileSize_FollowEarlierUserRulings` | `SummariesSeparatorsStepNumbersAndFileSize_MatchTheScreenWording` |
| `SuspiciousKeywordDefaultsTests.Defaults_RetainLegacySimplifiedVariants_WithoutAgentAddedKeyword` | `Defaults_RetainLegacySimplifiedVariants_WithoutOffBookKeyword`，這個測試核對預設關鍵字不含「帳外」 |
| `ReportExportSupportTests.IsSourceStale_SelectedWorkingPaperUsesItsDataRevision_AndLegacyKeepsPriorFlagRule` | `IsSourceStale_SelectedWorkingPaperUsesItsDataRevision_AndIsStaleWithoutOne`，沒有來源版本的底稿一律過期，和原本的名稱說的相反 |
| `LegacyAppearanceRegistryTests.Registry_ConditionsAndJetDestinations_ArePinnedForLaterParityStages` | `Registry_ConditionsAndJetDestinations_ArePinned` |
| `CompletenessBlankAccountPagingTests.AccountAndDiffPages_ReadBlankAccountRows_InTheSameOrderOnBothProviders` | `AccountAndDiffPages_ReadBlankAccountRows_InTheDefaultOrder` |
| `CommonFilterPredicateTests.NonQuarterPeriodEnd_HitsRevenueDebitOnBothProviders` | `NonQuarterPeriodEnd_HitsRevenueDebit` |
| `KeysetPagingProviderParityTests.SortedWalk_NullsLastAndStableKeyTieBreak_OnBothProviders` | `KeysetPagingFixedAnswerTests.SortedWalk_NullsLastAndStableKeyTieBreak` |
| `ProviderParityJourneyTests.GlAutoNumbering_LineItemUnmapped_IsEquivalentAcrossLocalProviders` | `JourneyFixedAnswerTests.GlAutoNumbering_LineItemUnmapped_RestartsAtOneForEachVoucher` |
| `AdvancedFilterAstProviderParityTests` | `AdvancedFilterAstFixedAnswerTests` |
| `KeysetPagingProviderParityTests` | `KeysetPagingFixedAnswerTests` |
| `MappingValueProfileProviderParityTests` | `MappingValueProfileFixedAnswerTests` |
| `ProviderLoggingParityTests` | `ProviderLoggingFixedAnswerTests` |
| `ProviderParityJourneyTests` | `JourneyFixedAnswerTests` |
| `TypedFieldProviderParityTests` | `TypedFieldFixedAnswerTests` |
| `WorkingPaperStep41ProviderParityTests` | `WorkingPaperStep41FixedAnswerTests` |
| `WorkingPaperWriterTestSupport.Stage8TrackWIds` | `WorkingPaperAppearanceIds` |
| `WorkingPaperWriterTestSupport.Stage8TrackWTargets` | `WorkingPaperAppearanceTargets` |
| `WorkingPaperWriterTestSupport.Stage8TrackWOutput` | `WorkingPaperAppearanceOutput` |
| `WorkingPaperWriterTestSupport.Stage8AppearanceMismatches` | `WorkingPaperAppearanceMismatches` |

改名時一起改了兩處引用舊名稱的地方：

- `TestEvidencePolicyTests.ProviderTests_KeepTheirCapabilityContracts` 讀的檔名改成 `ProviderLoggingFixedAnswerTests.cs`。
- `PreparerSummaryExportTests` 的類別說明原本寫「直接建 SQLite repo(比照 ProviderParityJourneyTests 直接建 repo),此 task 尚無 handler。」
  這個查詢現在由 `WorkingPaperWriter.Step1.cs` 與 `LegacyReportWriter.Prescreen.cs` 呼叫，「尚無 handler」已經不是現在的情形；
  旅程測試裡也有透過 `HandlerTestHost` 執行的測試，所以不再拿它對照。這一句改成「測試直接建立 SQLite 的 repository 呼叫這個查詢，不經過 action handler」。

同日另外查到 7 個帶開發階段代號的測試識別字，使用者回覆「照提議採用」（裁定 2026-11-11-04）。其中 5 個照下表改名，只改名稱：

| 原名稱 | 新名稱 |
|:---|:---|
| `WorkingPaperWriterTestSupport.OpenStage8Document` | `OpenWorkingPaper`，唯讀開啟底稿的輸出 |
| `WorkingPaperWriterTestSupport.ReadStage8CellText` | `ReadCellText`，讀一格的文字 |
| `WorkingPaperWriterTestSupport.TrackWEntries` | `WorkingPaperAppearanceEntries`，舊版外觀登記表裡底稿匯出的項目 |
| `ReportAppearanceRegistryTests.ExpectedTrackOIds` | `ExpectedReportAppearanceIds`，底稿以外報告的 26 個項目 |
| `LegacyCriteriaLogParser.RequiresStage5Ast` | `RequiresAdvancedFilterAst`，這個位置的舊版條件一定要轉成進階條件；這個類別在 `LegacyAuditParityProfile.cs` |

另外 2 個是 `LegacyAuditParityFilterScenarioBindings` 的 `ForCapturedStage6Case` 與 `ForCurrentStage6ProfileCase`。
第一次提交 `b9db4db` 裡，這兩個名稱只出現在它們自己的檔案，從那時起就沒有呼叫端，所以連同只給它們用的
`CaseA`、`CaseB`、`CurrentCaseA`、`CurrentCaseB` 四個欄位一起刪除。類別說明裡講這些固定位置的那一句跟著拿掉，原文是
「The fixed stage-6 maps contain positions only; the profile-derived guard proves they still describe the captured profiles.」

改名前把這 5 個檔另存一份，和改完的檔逐行比對：改名的 4 個檔共 35 行不同，每一行套用上表後都和改完的內容相同；
刪除的那個檔拿掉 41 行、新增 1 行，新增的是改寫後的類別說明。改完 `Build` 通過，0 個警告。

### 測試資料與夾具的名稱

`SourceCommentPolicyTests` 的「計畫與階段的名稱」原本抓得到 `Stage 8`、`Track O`、`Phase1` 與 `第二波` 這類寫法，
抓不到 `OpenStage8Document`、`stage-3`、`JET_STAGE10_APPEARANCE_COMPRESSION_MODE`、`phase11-tb-import`、`batch9-broken`、`batch8Fixture` 與 `TrackWEntries` 這 7 種。
使用者 2026-11-11 選「擴充檢查後一併提出」（裁定 2026-11-11-05）：先讓檢查抓得到，確認它先失敗，再把新名稱寫進名詞表，確認後才改。

- **檢查先失敗。** `PatternsRecogniseEachKindOfDevelopmentRecord` 加上這 7 種該抓的例子；`PatternsLeaveOrdinaryCommentsAndDataAlone` 加 4 個不該抓的例子：
  匯入批次的 `batch-1`、傳票號碼 `ROUND-1`、舊版的程序名稱 `Step5_Export_Excel_TW` 與類別名稱 `ReportBatchPublicationTests`。
  還沒改樣式時 `Focused` 失敗，11 個該抓的例子有 7 個沒抓到。
- **新的樣式。** 不分大小寫，也抓接在識別字中間的寫法。stage 與 phase 和數字之間可以有連字號、底線或空白；batch 要緊接數字，
  所以匯入批次 `batch-1` 那種數字前有連字號的寫法不算。改樣式後再跑 `Focused`，例子都通過，掃描 `src/JET` 的測試照預期失敗。
  用同一個樣式另外計算，C# 測試 23 個檔有 90 行、前端測試 10 個檔有 242 行抓得到，產品程式沒有。
- **在副本試改。** 把 `src/JET` 與 `tools/tests/frontend-development-repository.test.cjs` 複製到暫存資料夾，照下面三張表改：
  34 個檔裡每一條改法的次數都和預計相同，行數不變；用同一個樣式掃描副本的 1,147 個檔，沒有抓到。
  前端測試檔都通過 `node --check`，副本上 `src/JET/tests/frontend` 的 1,007 個前端測試全部通過。
  這只是診斷，沒有經過 `tools/verify.ps1`；C# 測試沒有在副本上執行，`tools/tests` 的那個檔也只檢查了語法。

下面的新名稱都是 Agent 取的。改法是拿掉批次或階段的編號；拿掉以後看不出用途的，改用這個測試在核對的事。
使用者同日回覆「照提議改」（裁定 2026-11-11-06），已照三張表改完，檢查寫在本節最後。

C# 測試，路徑從 `src/JET/tests/JET.Tests/` 起算：

| 檔案 | 原本 | 改成 | 用途 |
|:---|:---|:---|:---|
| `Application/AccountingParenthesesAmountTests.cs` | `jet-batch9-amount-` | `jet-amount-` | 暫存 CSV 的檔名開頭 |
| `Application/ActionBackgroundDispatchTests.cs` | `batch9-sync`、`batch9-thread` | `sync-request`、`thread-project` | `requestId`，以及 `ProjectSession` 進入的 `projectId` |
| `Application/DuplicateImportSourceWarningTests.cs` | `batch9-duplicates` | `duplicates` | 案件名稱 |
| `Application/ExcelNativeDateImportTests.cs` | `jet-batch9-date-` | `jet-date-` | 暫存 CSV 與 XLS 的檔名開頭 |
| `Application/FilterOperandParsingTests.cs` | 兩個 `batch7` | `filter-operands`、`gl-batch` | `ProjectDocument` 的 `ProjectId`，以及 GL 配對的 `SourceBatchId` |
| `Application/FilterProjectDateFormatWorkflowTests.cs` | `batch7` | `gl-batch` | 寫進 `gl` 資料表的 `batch_id` |
| `Application/FilterScenarioSaveBranchTests.cs` | `phase11-filter-save:` | `filter-save:` | `CaseTemplates` 的鍵 |
| `Application/MappingCaseTemplateFixture.cs` | `batch9-mapping:`、`Batch9 synthetic` | `mapping:`、`Mapping synthetic` | `CaseTemplates` 的鍵與案件名稱 |
| `Application/MutationInvalidationStateTests.cs` | `Batch9-Empty` | `Empty-Project` | 案件名稱 |
| `Application/PrerequisiteDisplayTextTests.cs` | `Batch8-Missing-Mapping` | `Missing-Mapping` | 案件名稱 |
| `Application/ReportClientNameHeaderTests.cs` | `BATCH4-ORIGINAL`、`BATCH4-UPDATED` | `ORIGINAL`、`UPDATED` | 修改前後的案件編號 |
| `Application/ReportOpenXmlParityTests.cs` | `phase2-openxml-` 開頭的 3 個暫存檔名，`phase2ParityProbe`、`phase2LiteralRelationshipId`、`rIdPhase2LiteralA`、`rIdPhase2LiteralB` | `openxml-` 開頭，`parityProbe`、`literalRelationshipId`、`rIdLiteralA`、`rIdLiteralB` | 暫存的報告副本，以及寫進副本的屬性名稱與值 |
| `Application/ReportRevisionStateTests.cs` | `batch9-artifact-state:` | `artifact-state:` | `CaseTemplates` 的鍵 |
| `Application/TbImportBranchTests.cs` | `phase11-tb-import:`、`PHASE11-TB` | `tb-import:`、`SYNTHETIC-TB` | `CaseTemplates` 的鍵與案件編號 |
| `Application/UnreadableProjectRecoveryTests.cs` | `batch9-` 開頭的 8 個案件名稱，例如 `batch9-broken` | 拿掉 `batch9-`，例如 `broken` | 案件名稱，也是 `projectId` 與案件資料夾的名稱 |
| `Application/WorkingPaperExportBranchTests.cs` | `phase11-working-paper-store`、`phase11-working-paper:` | `working-paper-store`、`working-paper:` | `projectId` 與 `CaseTemplates` 的鍵 |
| `Infrastructure/AccountCategorySideSelectionTests.cs` | `batch5-map`、`batch5-gl` | `mapping-batch`、`gl-batch` | 科目配對與 GL 的 `batch_id` |
| `Infrastructure/ImportApiSurfaceTests.cs` | `batch9-import-api` | `import-api` | `projectId` |
| `Infrastructure/LegacyAuditParityProfileTests.cs` | `changed from the stage-3 fixture baseline.` | `changed from the fixture baseline.` | 兩則斷言失敗時的訊息 |
| `Infrastructure/ReportBatchPublicationTests.cs` | `batch8`、`batch8_INFReport.xlsx`、`batch8_ValidationReport.xlsx` | `publication`、`publication_INFReport.xlsx`、`publication_ValidationReport.xlsx` | `projectId`；報告檔名以 `projectId` 開頭，所以兩個檔名一起改 |
| `Infrastructure/ReportPageAppearanceCoverageTests.cs` | 3 則 `Stage-9 ` 開頭的錯誤訊息 | 拿掉 `Stage-9 `，第一個字母改成大寫 | 外觀快照的資料不合規定時的錯誤訊息 |
| `Infrastructure/SpreadsheetAppearanceSnapshotCompressionTests.cs` | `JET_STAGE10_APPEARANCE_COMPRESSION_MODE` | `JET_APPEARANCE_COMPRESSION_MODE` | 環境變數；設成 `compress` 時，這個測試把 `src/JET` 裡的外觀快照改存成壓縮格式，沒有設定時什麼都不做 |
| `Infrastructure/WorkingPaperWriterTestSupport.cs` | `stage8-synthetic-account-mapping` | `synthetic-account-mapping` | 固定科目配對來源的 `BatchId` 與 `FileName`；底稿的寫出程式只用它判斷有沒有這份來源 |

前端測試的夾具函式與資料，路徑從 `src/JET/tests/frontend/` 起算：

| 原本 | 改成 | 用途 | 出現的檔 |
|:---|:---|:---|:---|
| `batch9TreeDom` | `treeDom` | 有父子節點的假 DOM，給真正的規則清單與畫面重繪程式使用 | `support/frontend-fixtures.cjs` 定義，4 個測試檔與 `tools/tests/frontend-development-repository.test.cjs` 引用 |
| `batch7Dom` | `parsedControlDom` | 把 HTML 字串裡的控制項拆成可以查詢的節點 | `support/frontend-fixtures.cjs`、`frontend-workflow.test.cjs` |
| `batch7ControlDom` | `lazyControlDom` | 依選擇器臨時建立節點的假 DOM | `support/frontend-fixtures.cjs`、`frontend-workflow.test.cjs` |
| `batch8Fixture` | `stepScreensFixture` | 載入各步驟畫面程式的夾具 | `support/frontend-fixtures.cjs`、`frontend-workflow.test.cjs`、`frontend-journey-tables.test.cjs` |
| `batch8-synthetic` | `screens-synthetic` | `stepScreensFixture` 的合成案件 `projectId` | `support/frontend-fixtures.cjs`、`frontend-workflow.test.cjs` |
| `batch8ValidationData` | `validationResultData` | 合成的資料驗證結果 | `support/frontend-fixtures.cjs`、`frontend-workflow.test.cjs` 與 4 個 `frontend-journey-*.test.cjs` |
| `batch8Reports` | `validationReportList` | 合成的資料驗證報告清單 | `support/frontend-fixtures.cjs`、`frontend-workflow.test.cjs` |
| `batch7Fixture` | `filterEditorsFixture` | 執行第五步條件編輯器的夾具 | `frontend-workflow.test.cjs` |
| `batch7-synthetic` | `filter-editors-synthetic` | `filterEditorsFixture` 的合成案件 `projectId` | `frontend-workflow.test.cjs` |
| 傳給 `FilterValues.render` 的 `batch7` | `value-editor` | 條件值編輯器裡 `data-focus-key` 的開頭 | `frontend-workflow.test.cjs` |
| `batch9Fixture` | `responseEffectsFixture` | 核對後端回應怎麼更新狀態與畫面的夾具 | `frontend-workflow.test.cjs` |
| `seedBatch9Results` | `seedResults` | 在夾具的狀態放入合成的各步驟結果 | `frontend-workflow.test.cjs` |
| `batch9PickerFixture` | `projectPickerFixture` | 案件清單的夾具 | `frontend-workflow.test.cjs` |
| `batch9PagerLifecycleFixture` | `pagerLifecycleFixture` | 第四步明細分頁讀取的夾具 | `frontend-workflow.test.cjs` |
| `batch9CancellationFixture` | `cancellationFixture` | 取消作業的夾具 | `frontend-workflow.test.cjs` |
| `batch9CreateStartupFixture` | `createStartupFixture` | 啟動時建立案件畫面與案件清單的夾具 | `frontend-workflow.test.cjs` |
| `confirmUnsaved` 的代號 `phase8`，以及它產生的 `phase8-modal` | `unsaved-sample`、`unsaved-sample-modal` | 未儲存提醒對話框的代號與 `data-bind` | `frontend-workflow.test.cjs` |
| `readyBatch6Fixture` | `readyGlMappingFixture` | GL 配對已經填好的夾具 | `frontend-mapping.test.cjs` |
| `batch6PendingPresenceFixture` | `pendingPresenceFixture` | 手動值查詢還在等回應的夾具 | `frontend-mapping.test.cjs` |
| `batchId` 的 `batch6` | `gl-batch` | 合成 GL 匯入結果的 `batchId` | `frontend-mapping.test.cjs` |

夾具在 VM 裡把步驟程式的內部函式掛到 `window`，這些名稱一律改成 `exposed` 開頭加上內容。
每個夾具各自建立 `window`，不同夾具用同一個名稱不會互相覆蓋：

| 原本 | 改成 |
|:---|:---|
| `batch8Validation` | `exposedValidation` |
| `batch8Filter`，以及 `filterEditorsFixture` 的 `batch7` | `exposedFilter` |
| `batch8Mapping`、`batch9Mapping` | `exposedMapping` |
| `batch8Import`、`batch9Import` | `exposedImport` |
| `batch8Create` | `exposedCreate` |
| `batch8App`、`batch9App`、`batch9PagerApp`、`batch9CreateStartup` | `exposedApp` |
| `batch8Preview` | `exposedPreview` |
| `batch9Pages` | `exposedPages` |
| `batch9ExportChain` | `exposedExportChain` |
| `batch9CorruptPicker` | `exposedPicker` |

留著不改的短代號：`validationResultData` 預設的結果代號 `v8`、`validationReportList` 的報告代號開頭 `b8-`、`responseEffectsFixture` 的案件 `b9-synthetic`，
以及 `seedResults` 放的 `v9`、`p9`、`f9`。這幾個看不出是開發批次，檢查也不抓。

改完的檢查：

- **只改了表上的名稱。** 改名前把 34 個檔另存一份，再把三張表的改法套到另存的檔，結果和改完的檔逐位元組相同；
  共 334 行不同，其中 `src/JET` 332 行、`tools/tests` 2 行。行數與換行字元都沒變，改完的檔裡也找不到舊名稱。
- **原始碼的檢查。** 用同一個樣式掃描 `src/JET`，沒有抓到；前端測試檔都通過 `node --check`。
  `Focused` 的 `SourceCommentPolicyTests` 14 個案例全部通過。
- **完整公開測試。** `Public` 通過。和上一次的 `Public`，
  也就是「測試名稱」一節改完 19 個名稱後跑的那一次逐筆比對：
  上一次 5,042 筆、這次 5,046 筆，多出的 4 筆是 `PatternsLeaveOrdinaryCommentsAndDataAlone` 新加的 4 個例子，沒有少掉或改名的測試。
- **斷言變動的數字。** 這次 `Public` 算出和 HEAD 比的斷言變動是刪掉 157 處、新增 31 處，比上一次多刪 20 處、多新增 20 處；
  少掉的測試方法兩次都是 16 個，新加的略過都是 0 個。
  多出的 20 處都是同一行斷言改了名稱，算成刪掉 1 處、新增 1 處：7 個識別字改名有 11 行，在 `WorkingPaperWriterAppearanceTests` 10 行、`ReportAppearanceRegistryTests` 1 行；
  測試資料改名有 9 行，在 `UnreadableProjectRecoveryTests` 8 行、`ReportBatchPublicationTests` 1 行。

### 舊版程式的行號

這一節原本列在「還沒處理」：改寫規則是把舊版程式的行號改成「舊版 JET」，但前幾輪保留了一部分。
舊版程式指 `legacy/idea-tool.bas` 與 `legacy/idea-script.bas`。英文註解裡 `ISM` 後面是 `idea-tool.bas` 的行號，`BAS` 後面是 `idea-script.bas` 的行號。
Agent 問「底稿鎖定與原始碼整理的七項要照建議處理嗎？」，使用者 2026-11-11 回覆「照建議處理」（裁定 2026-11-11-17）。
兩個舊版檔的行號都照下面的做法改掉。改寫前的原文附在原文紀錄的第六部分。

- 只寫出處的括號整個拿掉，例如「（idea-tool.bas 第 12702 行）」。括號裡還有理由的，只拿掉行號，例如留下「（舊版這兩種寫法相同）」。
- 檔名當主詞或依據時改成「舊版 JET」，例如「照舊版 idea-tool.bas 第 10558 到 10563 行寫」改成「照舊版 JET 寫」。
- 「舊版怎麼寫、新版怎麼寫」的對照句保留「舊版」與「新版」，只拿掉行號。
- 英文註解拿掉 `BAS`、`ISM` 與行號。原本寫了舊版步驟名稱的，改成 legacy JET 加上步驟名稱，例如 legacy JET Step5；沒有寫步驟名稱的只拿掉行號。行尾註解只有行號的，整段拿掉。
- 舊版的程序名稱或欄名，只在需要指出是哪一段時留下，例如 `Step1_Append_IsManual` 與 `Final_TAG`。
- 只寫檔名、沒有行號的 7 段一起改成「舊版 JET」。`FieldInfoProjector.cs` 原本寫「較新的 idea-tool.bas」，改成「舊版 JET 的較新版本」。
  較早的 `idea-script.bas` 在單一帶號金額時把來源欄改名，不保留來源欄，所以「較新」要留。
  `LegacyAuditParityLegacyObservationFactory.cs` 有一段英文註解用兩個檔名分辨兩個舊版，改成較新的版本寫 59 列、較早的版本寫 60 列。
- `WorkingPaperWriter.Step1.cs` 的 A3 說明原本寫 ideascript 的 `LastPeriod`，兩個舊版檔都查不到這個名稱。舊版寫 A3 時用的變數是 `sLast_Accounting_Period_Date`，改寫成這個名稱。
- 行號拿掉後說法不再成立的 1 段也改：`LegacyReportStyles.cs` 原本說行號範圍寫在上面的樣式常數，改成說上面 15 到 20 號樣式寫了來自舊版的哪一步。
- `WorkingPaperWriterStep1ToStep2Tests.cs` 一則註解的句首「原測試比對第 14 列表頭與範本格式。」講的是測試修改經過，一起拿掉。

共改 31 個檔、68 段註解。產品程式 21 個檔、56 段，改寫的範圍原本 77 行，改完是 68 行；測試 10 個檔、12 段，行數不變。

下面這些照舊保留：

- 測試讀舊版檔用的路徑字串 `legacy/idea-script.bas` 與 `legacy/idea-tool.bas`，在 `tests/JET.Tests/Infrastructure/LegacyAppearanceRegistry.cs`。
  `LegacyAuditParityComparator.cs` 標示比對依據時也用這兩個字串。它們是字串，不是註解。
- 識別字 `LegacyIdeaScript`、`LegacyIdeaModule`，以及測試方法名稱 `InfMembership_AllowsTheSixtiethSampleWrittenByIdeaScript`。
- 夾具 `legacy-appearance-registry.json` 每個項目的 `basLine` 與 `ismLine`。這是測試資料，`LegacyAppearanceRegistryTests` 用它們核對舊版檔那一行的內容。

底稿鎖定的三段註解同時改寫（裁定 2026-11-11-15、2026-11-11-16），原文也在第六部分：

- 舊版的 step1-2、step1-3 與 step4 會多鎖最後一筆資料的下一列，分別在 `idea-tool.bas` 第 10575、10637 與 10797 行。
  JET 只鎖到最後一筆資料，這是刻意的差異，程式不改。step1 與 step1-1 鎖的列和舊版相同。
- `WorkingPaperWriter.Step1.cs` 與 `WorkingPaperWriter.Step2To41.cs` 的註解原本寫範圍照舊版。現在寫明欄與起始列照舊版、列只鎖到最後一筆資料，也寫出舊版多鎖的那一列。
- `WorkingPaperWriter.Template.cs` 的鎖定範圍說明拿掉舊版的行號，「範圍照舊版」改成「欄與起始列照舊版」，另外加一句：續頁照同一個範圍鎖定，其他格子和第一頁一樣可以填。

檢查：

- **註解以外沒有變。** 改寫前把 31 個檔另存一份，用腳本逐行比對改寫前後的檔案，註解以外的程式行完全相同。
- **重新掃描。** 改完後在 `src/JET` 搜尋兩個舊版檔名、`ideascript`、接著數字的 `BAS` 與 `ISM`，以及四到五位數的「第 N 行」，
  只找到上面照舊保留的路徑字串、識別字與測試方法名稱。
- **建置與測試。** 這一節與底稿鎖定的程式都改完後，`Focused` 篩選 `WorkingPaper`：選到的 224 個案例與固定補跑的架構檢查 387 個都通過，
  架構檢查包含原始碼註解的政策檢查。這次要改的程式都改完後跑 `Public`，C# 5,048 個案例全部通過。
- **複核後補改。** `LegacyAuditParityLegacyObservationFactory.cs` 那段原本照舊保留。複核時對照使用者回覆「照建議處理」的那則建議：寫到 `idea-tool.bas` 的 37 行只留 2 行，
  就是測試讀舊版檔的路徑與比對結果的出處文字，所以這段也改掉。`WorkingPaperWriter.Appearance.cs` 有一段拿掉行號後寫成 FieldInfo 的版本、TB 與 GL 格，
  但下面的程式只處理 TB 與 GL 的表頭，改成只寫這兩種表頭。改完後 `Focused` 篩選 `LegacyAuditParity` 選到的 174 個案例、
  篩選 `WorkingPaper` 選到的 224 個案例，以及兩次固定補跑的架構檢查 386 個都通過。架構檢查比上面少的 1 個，是下一節刪掉的 `SchemaIsolationGuardTests`。
- **再次核對後補改。** 上面的測試跑完之後，又查到 `WorkingPaperWriter.Step2To41.cs` 有一段說明寫 step3 鎖定的 B7:E28 在情境超過十個時往下延伸，但每個案件最多只能存十個篩選情境。
  鎖定範圍實際上是在條件說明超過 Excel 單格容量、拆成多列而寫過第 28 列時才往下延伸，所以照裁定 2026-11-10-17 改成寫這個情形，也寫明多出的列鎖 B 到 E 欄，舊版只鎖到第 28 列。
  同一行的「照舊版鎖的」也改成「照舊版 JET 的」。這段註解不是行號的改寫，不算在上面的 68 段。
  整理前的原文在原文紀錄第一部分；這次修正前的那一行，是第一部分的原文拿掉括號裡的行號與裁定編號。
  多出的列要不要鎖定等使用者決定，見首次 SAT 文件製作計畫「2026-11-11 追加：底稿鎖定與原始碼整理的七項」的「還沒處理」。
  改完後 `Focused` 篩選 `SourceCommentPolicyTests` 選到的 14 個案例與固定補跑的架構檢查 386 個都通過。

### 刪掉的測試與樣式

這兩項原本列在「還沒處理」，和上一節是同一則回覆（裁定 2026-11-11-18、2026-11-11-19）。

- `SchemaIsolationGuardTests` 整個刪除。它原本掃描的 SQL Server 資料夾已經不在，只剩核對掃描函式本身。
  `SupportedActionsParityTests` 說明裡拿它當例子的那一句跟著改。
- `AccountMappingImportSummaryTests` 的輔助方法 `FirstUsefulReadFailure`，和核對它的 `ReadFailureDiagnosticPrefersTheFirstRootErrorOverInvalidatedConnections` 一起刪除。
  這個輔助方法只有這個測試呼叫，測試用 `DuckDBException` 開頭的字串當合成的錯誤訊息。
- 刪除的原因記在 [`test-change-records.md`](test-change-records.md)。要找回時，兩個檔在提交 `7e88b7b` 都還在。
- Agent 的建議寫明刪除既有測試要使用者明確同意；把這一題的「照建議處理」當成同意，是 Agent 的解讀；使用者 2026-11-12 確認同意刪除。
- 架構圖的來源清單 `docs/architecture/diagrams/evidence/source-manifest.json` 也列著 `SchemaIsolationGuardTests.cs` 的路徑，這次沒有改。
  這份清單是提交 `40440b9` 的封存快照，改了會讓架構圖的檢查失敗；Agent 的建議寫了這份清單也要跟著改，所以這一點等使用者確認。
- `JET/wwwroot/css/app.css`「最近訊息欄」的 9 條樣式刪除：`.overview-msg` 一組 5 條，以及 `.overview__col-head`、`.overview__col-all`、`.overview__col-all:hover` 與 `.overview__msg-list`。
  前端程式與前端測試都沒有用到這些 class。刪除前後用瀏覽器主機開流程總覽比對，截圖與每個元素的計算後樣式都沒有變化。

檢查：刪除後 `src/`、`tools/`、`.agents/`、`.claude/`、`.codex/` 與 `.github/` 都找不到 `SchemaIsolation`、`FirstUsefulReadFailure` 與這兩個測試方法的名稱，
`app.css` 也找不到這 9 條規則的選擇器。`Contract -ContractScenario FrontendMapping` 的 1,016 個前端測試、`Focused` 篩選 `DesignIntegrationFrontendTests`
的 19 個案例與固定補跑的架構檢查 386 個都通過；`Public` 和上一次比，少的 2 個案例就是刪掉的兩個測試。

### 用計畫編號取的名稱

測試裡另有 4 組名稱用計畫的事項編號開頭：`K3`、`K6`、`K7` 與 `K9`，程式裡的 `k6`、`k9` 寫成小寫。
這些編號是 [KCT 需求計畫](specs/2026-10-06-kct-gl-requirements-plan.md)第六節「待使用者裁定」的事項編號，只有 `src/JET` 時查不到出處。
`SourceCommentPolicyTests` 的「計畫與階段的名稱」只抓 stage、phase、batch、Track 與「第二波」這類寫法，抓不到這幾個名稱。
使用者 2026-11-11 回覆「照建議處理」（裁定 2026-11-11-20）。`K3_CASH_NOTE` 照 Agent 在建議裡提的名稱改。
同一則建議也寫了新名稱要先寫進名詞表、經使用者確認才改；把「照建議處理」當成確認了 `COUNTERPART_CASH_NOTE` 這個名稱，是 Agent 的解讀；使用者 2026-11-12 確認照用。
其他三組由 Agent 讀過用途後提出下表的新名稱，寫進名詞表，經使用者確認才改。
使用者 2026-11-12 回覆「照提議改」（裁定 2026-11-12-09），同日照下表改完。

路徑從 `src/JET/tests/` 起算：

| 檔案 | 原本 | 改成 | 用途 | 現況 |
|:---|:---|:---|:---|:---|
| `frontend/frontend-journey-filter-build.test.cjs` | `K3_CASH_NOTE` | `COUNTERPART_CASH_NOTE` | 收入對方科目那張卡加入後，條件列寫的現金說明；同一個檔的 `COUNTERPART` 是這張卡的代號 | 已改，使用者 2026-11-12 確認照用 |
| `JET.Tests/Domain/AccountMappingColumnResolverTests.cs` | `K7HeaderOrders` | `HeaderOrders` | 科目配對檔的標頭排成不同順序，以及預期認出的科目編號、科目名稱與分類欄；`JET.Tests/AuditCore/IntakeMappingProgramTests.cs` 也用 | 已改，使用者 2026-11-12 回覆「照提議改」 |
| `frontend/frontend-workflow.test.cjs` | `k6MappingDifferenceFixture` | `accountMappingCardFixture` | 畫出第四步「用 Excel 配對」的科目配對區塊，操作匯入與兩份差異清單的夾具 | 已改，使用者 2026-11-12 回覆「照提議改」 |
| `frontend/frontend-workflow.test.cjs` | 掛到 `window` 的 `k6` | `exposedAccountMappingCard` | `validate-step.js` 的 `accountMappingCardHtml` 與 `bindAccountMappingCard` | 已改，使用者 2026-11-12 回覆「照提議改」 |
| `frontend/frontend-workflow.test.cjs` | `projectId` 的 `k6` | `account-mapping-card-synthetic` | `accountMappingCardFixture` 的合成案件 | 已改，使用者 2026-11-12 回覆「照提議改」 |
| `frontend/frontend-workflow.test.cjs` | 掛到 `window` 的 `k9` | `exposedFilter` | 值清單計數那則測試取用的 `filter-step.js` 函式；其他夾具掛同一個檔時也用這個名稱 | 已改，使用者 2026-11-12 回覆「照提議改」 |
| `frontend/frontend-workflow.test.cjs` | `projectId` 的 `k9-entry-synthetic` | `value-count-synthetic` | 值清單計數那則測試的合成案件 | 已改，使用者 2026-11-12 回覆「照提議改」 |

`K3_CASH_NOTE` 改完的檢查：改名前另存原檔，用純文字取代後和改完的檔逐位元組比對，兩者相同，只改了第 212 行的定義與兩處使用；
同一個檔的失敗訊息沒有改。改完後 `src/`、`tools/`、`.agents/`、`.claude/`、`.codex/` 與 `.github/` 都找不到 `K3_CASH_NOTE`，
`Contract -ContractScenario FrontendMapping` 的 1,016 個前端測試全部通過。

另外三組改完的檢查：`AccountMappingColumnResolverTests.cs` 2 處、`IntakeMappingProgramTests.cs` 1 處，`frontend-workflow.test.cjs` 12 處，和 2026-11-11 調查時數到的位置相同。這三組和同一天改的其他名稱一起比對與驗證，見「2026-11-12 改的測試名稱與失敗訊息」。

### 原本就有的開發經過註解

2026-11-11 核對「舊版程式的行號」那一輪的成果時，另外查到 `src/JET` 還有 9 條註解寫著開發任務的用語：
brief、task、Task 加編號、鐵律、User-approved、本機參考樣本，`.git/sdd/` 底下的腳本路徑，以及已經不存在的 jet-testing 章節。
這些寫法在提交 `512af50` 就有，不是前幾輪造成的。用 `git log -S` 查這些字句，都是第一次提交 `b9db4db` 就有的寫法；
`filter-step-saved.js` 那一句後來只改過 action 的名稱。
依據是裁定 2026-11-10-11。使用者 2026-11-10 對子代理順帶指出的問題回覆「可以修，但前提是你要摸清楚所有背景脈絡，避免抓到過時的資訊而影響上下文」（裁定 2026-11-10-17）；
這 9 條是 2026-11-11 另外查到的，照同一個前提整理：每一條都先對照現在的程式，再決定字句，查不到依據的說法拿掉。
同一次也在 `src/JET` 搜尋同類的寫法，並查證另一個工作階段順帶指出的幾件事，成立而且只需要改註解的一起改。
改完後請一個沒有參與改寫的唯讀子代理逐段複查，它另外指出的同類寫法與一段掛錯位置的說明也一起改，下面一併寫。
改寫前的原文附在原文紀錄的第七部分。

開發任務的用語：

- **底稿寫出測試的輔助程式。** `WorkingPaperWriterTestSupport.cs` 的類別說明拿掉 brief 與鐵律，改成寫測試端可以用 ClosedXML、寫出器本身不用。
  封面與固定文字的比對依據原本寫 `.git/sdd/` 的腳本與本機參考樣本，改成寫測試裡寫死的儲存格內容、合併範圍與表名；
  儲存格內容與合併範圍的預期值直接寫在 `WorkingPaperWriterCoverAndReferenceSheetTests` 裡，表名是這個輔助程式的常數。
  組裝寫出器那一段的標題原本寫「此 task 尚無 handler/DI 入口」，
  `AppCompositionRoot.Repositories.cs` 已經用 `CreateWorkingPaperWriter` 組裝寫出器，所以改成寫測試直接建立 SQLite 的 repository 注入、不經過 DI。
- **分段標題。** `WorkingPaperWriterStep1ToStep2Tests.cs`、`WorkingPaperWriterCoverAndReferenceSheetTests.cs` 與 `WorkingPaperWriterStep3Step4Tests.cs`
  共 13 行分段標題拿掉「Task 2:」到「Task 5:」，只留工作表名稱與說明。其中兩行另外改字：step2 那一行原本用 legacy 與全形加號串接借貸代號和帶號金額，改成「舊版 JET 的借貸代號與帶號金額」，
  「母體不影響,沿用既有逐字 oracle」改成「內容不隨母體改變」。
- **jet-testing 的引用。** `HandlerTestHost.cs`、`InspectAndSelectFilesHandlerTests.cs`、`ImportAndMappingHandlersTests.cs`、`WebViewEventPublisherTests.cs`、
  `GlProjectionGuardTests.cs` 與 `ActionContractTests.cs` 共 6 行拿掉 jet-testing 與章節編號，理由留下。
  `WebViewEventPublisherTests.cs` 與產品程式 `WebViewEventPublisher.cs` 原本都寫 WebView2 的部分「走 GUI 手動驗證」，查不到依據，兩處都拿掉。
  測試的類別說明原本寫「只測序列化」，但這個檔另外有兩則測試，核對 `Publish` 在綁定前不動作、綁定後把工作排進 UI 的 SynchronizationContext；
  說明照測試的內容改，並寫明實際呼叫 WebView2 送出訊息不在這個測試的範圍。
  `ActionContractTests.cs` 的類別說明另外拿掉「harness TDD 強化,做法一」，「這裡會紅」改成「這裡會失敗」。
- **底稿寫出程式。** `WorkingPaperWriter.cs` 類別說明的「資料表共用骨架(DRY 閾值=3,本 task step1 / step1-2 / step1-3 達標)」改成「step1 家族資料表的共用部分」。
  `WorkingPaperStyles.cs` 的「本 task 三表無框線」改成「這份樣式表的樣式都沒有框線」，因為這份樣式表每個樣式的 `BorderId` 都是 0。
  `WorkingPaperWriter.Appearance.cs` 拿掉「User-approved final schema:」，只留整張 step4-1 都沒有框線。
  `WorkingPaperWriter.FixedSheets.cs` 的「對齊本機參考樣本中的固定字串」改成「照舊版 JET 寫固定字串」；兩個舊版檔都寫 `Not in TB`。
  `WorkingPaperWriter.Step2To41.cs` 的分段標題「資料表共用骨架(DRY=3)」拿掉，底下接的是 step2 的固定文字，不是共用骨架。
- **前端。** `filter-step-saved.js` 的「(brief：依 highRiskSummaryScenarios 回的 position 升冪)」拿掉「brief：」。
- **approved 與裁決。** `ReportWorkbookPresentationTests.cs` 拿掉「User-approved」；`FilterSideAndMonthWorkflowTests.cs` 與
  `TerminologyUnificationFrontendTests.cs` 共 3 行英文註解拿掉 approved；`TerminologyUnificationFrontendTests.cs` 那 2 行的 approved 是前幾輪改寫時寫的，
  更早的原文在原文紀錄第一部分。`ApprovedUxFrontendTests.cs` 類別說明的「已核准 UX 項目」拿掉「已核准」，類別名稱沒有動；類別與檔名後來在 2026-11-12 改成 `InteractionAndPresentationFrontendTests`，見「2026-11-12 改的測試名稱與失敗訊息」。
  `ExportReportHandlers.cs` 的「凍結裁決：」與 `GlRuleSqlEquivalenceTests.cs` 的「`>= 0` 裁決」改成直接寫規則。
- **其他編號。** `DispatcherDiagnosticsTests.cs`、`RingBufferLoggerProviderTests.cs` 與 `SqlExecutionLoggingTests.cs` 共 4 行拿掉「TDD #1」這類編號，
  `UsageScenarioMutationRollbackTests.cs` 與 `UsageScenarioRetryJourneyTests.cs` 各 1 行拿掉句首的「X-03:」與「X-16:」，說明的內容照舊。
  這兩種編號在 `docs/` 與 `.agents/` 受版本控制的檔案裡查不到定義。

對照程式後一起改的說明：

- **掛錯位置的說明。** 底稿寫出程式有四段說明沒有接在它說明的程式前面。
  `WorkingPaperWriter.FixedSheets.cs` 的 step1 分段標題與說明接在 `ConfigureCalendarLayout` 前面，搬到 `WorkingPaperWriter.Step1.cs` 的 `EmitStep1Async`，文字沒有改。
  `WorkingPaperWriter.Step1.cs` 的 step2 分段標題與說明接在 `ConfigureStep1Layout` 前面，搬到 `WorkingPaperWriter.Step2To41.cs` 的 `EmitStep2Async`；
  同一個檔欄標列工廠的說明接在 `Step1LockedArea` 前面，搬到 `WorkingPaperWriter.cs` 的 `HeaderCells`。
  `WorkingPaperWriter.Step2To41.cs` 續頁骨架的說明接在 `Step2HeaderLines` 前面，搬到 `WorkingPaperWriter.Streaming.cs` 的 `EmitContinuedRowsAsync`。
- **step2 的說明。** 原本寫「逐字對齊本機參考樣本」，也寫借方與貸方兩欄直接用 `InfSampleRow` 的 `DebitScaled` 與 `CreditScaled`，括號裡註明這是 brief 的設計決策。
  現在 E 欄寫借貸代號、F 欄寫借方減貸方的帶號金額，`ProjectStep2Sample` 的說明已經這樣寫，所以第二句整句拿掉；第一句拿掉查不到依據的「逐字對齊本機參考樣本」。
  「emitter 自累計」也拿掉：序號由寫出程式依報告的順序從 1 編起，`ProjectStep2Sample` 的說明已經寫了，這裡不重複。
- **共同表頭的張數。** `WorkingPaperWriter.cs` 與 `WorkingPaperWriter.Step1.cs` 原本寫五張 step1 表都有 A1 到 A4 的共同表頭；
  現在呼叫 `WriteCommonHeader` 的是 step1、step1-1、step1-2 與 step1-3 四張，改成四張。
  `WorkingPaperWriter.Step1.cs` 的「逐字相同」也改成「共用」，因為 A4 的說明由各表傳入，每張表不同。
- **攸關資料元素欄位不存在時的寫法。** `ExportReportHandlers.cs` 原本寫欄位不存在時退回 fieldId 原字串；
  `FilterConditionRenderer` 現在寫 `FilterConditionLabels.MissingRdeField` 的說明，不印 fieldId，照這個改。
- **科目配對欄位辨識的測試說明。** `AccountMappingColumnResolverTests.cs` 原本寫關鍵字命中優先、三欄不同才採用、否則整組照順序。
  `AccountMappingColumnMatcher` 是三欄各自判定：確切欄名優先，再比對還沒被認領的欄的關鍵字，最後照剩下的欄位順序。說明照程式改。
- **只剩 SQLite。** `UnbalancedVoucherDateRowsTests.cs` 的「兩個本機資料庫都要得到同一份結果」拿掉，這個測試現在只跑 SQLite。
- **`TbOpenCloseMappingTests.cs` 的類別說明。** 用箭號與全形加號串接的三行改成完整的句子，「新模式」改成「這兩種模式」。同一段的「guide §2.2」這一輪沒有改，後來的處理見「註解裡的文件章節引用」。
- **標點。** `WorkingPaperWriter.FixedSheets.cs` 提示句那一行的半形逗號與括號改成全形，文字只把「、不參與」改成「，也不參與」。

共改 33 個檔：產品程式 10 個、測試 23 個。原文紀錄第七部分收 32 個檔、49 段，產品程式 9 個檔、13 段，測試 23 個檔、36 段；
`WorkingPaperWriter.Streaming.cs` 只接收搬來的說明，沒有改寫前的原文。

查證後照舊保留：

- `LayerDependencyTests.cs` 的「依賴方向鐵律」說的是各層的相依方向，是這個測試核對的內容；同一個檔的「harness 自我驗證」說的是這個測試確認分析工具真的讀到相依。
  `LegacyAuditParityObservations.cs` 的 parity harness 說的是比對程式本身。這三處都不是開發任務的用語。
- 識別字與失敗訊息沒有動，例如 `TerminologyUnificationFrontendTests.cs` 的變數 `approvedSentence`；這個變數與三則失敗訊息後來在 2026-11-12 改掉，見「2026-11-12 改的測試名稱與失敗訊息」。
- 註解裡的 `created_by`、`approved_by`、`approvedBy` 是欄位名稱。`ReportExportSupport.cs` 的「該 task」說的是執行中的工作。

檢查：

- **註解以外沒有變。** 改寫前把 33 個檔另存一份。用腳本把改寫前後的檔案拆成註解與註解以外兩部分，註解以外的程式逐行相同；
  另外逐行比對，有差異的行不是註解行就是空行：註解行拿掉 71 行、加上 70 行，空行少 7 行、多 2 行。這是用腳本逐字元拆出註解的近似判斷，不是 Roslyn。
- **重新掃描。** 改完後用同一支腳本只掃註解，搜尋 brief、接在「此、本」後面的 task、Task 加數字、TDD 加編號、鐵律、參考樣本、skill、approved、已核准、手動驗證、`.git`、jet-testing、sdd、DRY、harness 與裁決，
  只剩上面照舊保留的幾處，以及 `LegacyAuditParityJourneyTests.cs` 與 `PrivateCaseAcceptanceRunner.cs` 的「內容裁決」；那是比對時傳入的清單。
- **獨立複查。** 第一批 26 個檔改完後，請一個沒有參與改寫的唯讀子代理逐段比對改寫前後的檔案，並對照程式。
  它確認這 26 個檔只改了註解，原文紀錄第七部分的原文、行號與數字都對得上。它另外指出的事已經改：`WebViewEventPublisherTests.cs` 的新說明和測試內容不符，
  `WebViewEventPublisher.cs` 還留著同一句「走 GUI 手動驗證」，step1 的說明掛錯位置，共同表頭寫「逐字相同」不成立，
  以及 TDD 加編號、情境編號與「已核准」這三種同類寫法。檔數因此從 26 個變成 33 個，原文紀錄第七部分重新產生。
  它提出、這次沒有改的事寫在「還沒處理」。
- **第一批改完後的測試。** `Focused` 篩選 `SourceCommentPolicyTests` 選到的 14 個案例通過。改到的測試類別各跑一次 `Focused`，選到的案例都通過：
  `WorkingPaper` 224 個、`ActionContractTests` 22 個、`ImportAndMappingHandlersTests` 25 個、`InspectAndSelectFilesHandlerTests` 8 個、
  `ReportWorkbookPresentationTests` 4 個、`TbOpenCloseMappingTests` 3 個、`TerminologyUnificationFrontendTests` 10 個、`WebViewEventPublisherTests` 3 個、
  `AccountMappingColumnResolverTests` 27 個、`GlProjectionGuardTests` 7 個、`GlRuleSqlEquivalenceTests` 26 個、`UnbalancedVoucherDateRowsTests` 1 個、
  `FilterSideAndMonthWorkflowTests` 1 個。每一次固定補跑的架構檢查 386 個也都通過。
  `Contract -ContractScenario FrontendMapping` 的 1,016 個前端測試全部通過。
- **全部改完後的完整公開測試。** 複查後的修改做完再跑 `Public`：C# 5,048 個案例，前端測試 FrontendMapping 1,016 個、FrontendPreview 12 個，全部通過，建置 0 個警告。
  複查後才改到的 7 個檔沒有另外跑 `Focused`，由這一次 `Public` 涵蓋。
- **文件檢查。** 文件改完後跑 `Documentation`：80 份文件、0 個錯誤，6 則提醒都在這次沒有改的段落。寫完這一行後再執行一次確認，結果相同。
- 沒有執行 `Gui`、`Excel` 與 `PrivateCase`，沒有重新產生送審文件。沒有 stage、commit 或 push。

### 註解裡的文件章節引用

上一輪整理完，`src/JET` 的註解還有 65 行引用文件的章節，分在 37 個檔；產品程式只有 `LegacyReportWriter.Prescreen.cs` 1 行，其餘在測試。
前幾輪的整理紀錄與原文紀錄查不到保留或改寫這類引用的決定。現行的 `docs/jet-guide.md` 小節沒有編號，
`§2.2`、`§3.1.5`、`§6.1` 與 `§7.2` 這些編號對得上的是 `docs/history/superseded/jet-guide-2026-08.md`。
Agent 問「註解裡 65 行 guide 與 spec 的章節引用，要拿掉編號只留說明嗎？」，使用者 2026-11-12 回覆「拿掉章節編號」（裁定 2026-11-12-01）。
改寫前的原文附在原文紀錄的第八部分。

這 65 行有六種寫法：

| 寫法 | 行數 | 例子 |
|:---|---:|:---|
| guide 加章節編號 | 25 | `guide §3.1.5`、`guide 3.1.5 的規則` |
| 只寫章節符號加數字 | 17 | `§2`、`§7.2` |
| jet-guide 加章節 | 10 | `jet-guide §7.2`、`jet-guide section 7` |
| spec 加章節或條目編號 | 7 | `spec §1`、`spec C.3`、`§2 spec` |
| master spec 加節名 | 5 | `master spec「匯出進度契約」` |
| 設計加章節編號 | 1 | `設計 §39` |

改法：

- **拿掉編號與文件名稱，說明照舊。** 大部分的行只拿掉括號裡的引用或句首的引用，其餘文字沒有動。
  文件名稱跟著編號一起拿掉，是 Agent 照題目「只留說明」的解讀。
- **引用後面接著說明的，留下說明。** 例如「guide §3.1.1 分隔符偵測規則」留下「分隔符偵測規則」，「guide §3.1.4 多來源模型」留下「多來源模型」。
  master spec 那 5 行有 4 行留下節名當說明：`LegacyReportWriter.Prescreen.cs`、`ExportProgressEventTests.cs`、`FieldInfoPersistenceTests.cs` 與 `FieldInfoReaderTests.cs`。
- **括號裡只有文件、章節與節名的，整個括號拿掉。** `PrescreenPreviewParityTests.cs`、`PrescreenReportSourceLineageTests.cs`、`DemoDataFactoryTests.cs` 第 97 行與 `DemoRuleOracleTests.cs` 各 1 行。
  `ImportAccountMappingHandlerTests.cs` 的「guide §5 否定面；」也整個拿掉，「否定面」指的是那一節的內容，拿掉編號後讀不出意思。
- **句子靠引用才成立的，改寫句子。** `BlankRecordsPageTests.cs` 的「正是 §2 spec 定案語意」拿掉，句子停在「故不計」。
  `PeriodBoundingDemoTests.cs` 的「這正是 §2 spec 定案語意」改成「規則是」，後面引號裡的規則沒有動。
  `CurrentPrincipalTests.cs` 的「oracle：spec §1 定義，手算」改成「oracle：上面的定義，手算」，定義寫在上一行。
  `LegacyWorkbookContentComparison.cs` 第 33 行原本只寫兩個章節編號的正規化，改成「含匯入可接受格式的正規化與零值摒除」，用的是同一個檔另外兩處原本就有的說法。
- **英文註解。** `CaseHandoverPreparedByTests.cs`、`UsageScenarioMutationRollbackTests.cs`、`UsageScenarioProjectBoundaryTests.cs`、`UsageScenarioRetryJourneyTests.cs`、
  `WorkingPaperExportCapacityJourneyTests.cs` 與 `LegacyAuditParityObservationFactory.cs` 的 jet-guide section 引用拿掉，句子維持英文。
  `UsageScenarioProjectBoundaryTests.cs` 那一句同時寫 jet-guide section 2 與 frontend description，兩個一起拿掉，改成「Both audit dates are required.」。
- **跟著調整斷句的 3 行。** `LegacyWorkbookContentComparison.cs` 第 31 與 315 行、`LegacyAuditParityObservationFactory.cs` 第 350 行本身沒有章節編號，
  因為和相鄰的行是同一句，拿掉引用後跟著調整。所以一共改 68 行。
- **沒有改的。** `ExcelDateFormatDetector.cs` 與 `ExcelDateFormatDetectorTests.cs` 各 1 行引用外部標準 ECMA-376 的條文編號，照舊保留。
- 這一輪只拿掉引用，沒有逐句核對留下的說明和現在的程式是否相符。

檢查：

- **註解以外沒有變。** 改寫前把 37 個檔另存一份。用腳本把改寫前後的檔案拆成註解與註解以外兩部分，註解以外的程式逐行相同。
  另外逐行比對：每個檔的行數、換行與檔頭都沒有變，有差異的 68 行在改寫前後都是註解行。這是用腳本逐字元拆出註解的近似判斷，不是 Roslyn。
- **重新掃描。** 改完後用同一支腳本只掃註解，搜尋章節符號、jet-guide、guide 加章節、master spec、spec 加章節或條目編號，只剩引用 ECMA-376 的 2 行。
- **完整公開測試。** 68 行改完後跑 `Public`：C# 5,048 個案例，前端測試 FrontendMapping 1,016 個、FrontendPreview 12 個，全部通過，建置 0 個警告。
  `SourceCommentPolicyTests` 包含在這一次裡，沒有另外跑 `Focused`。
- **文件檢查。** 文件改完後跑 `Documentation`：80 份文件、0 個錯誤，6 則提醒都在這次沒有改的段落。寫完這一行後再執行一次確認，結果相同。
- 這一輪沒有請子代理獨立複查，靠上面的腳本比對與 `Public` 確認。沒有執行 `Gui`、`Excel` 與 `PrivateCase`，沒有重新產生送審文件。沒有 stage、commit 或 push。

### 2026-11-12 改的測試名稱與失敗訊息

使用者 2026-11-12 對另外五件事的回覆，也是改測試裡的名稱、訊息或斷言，和上面「用計畫編號取的名稱」的三組同一天改完。
回覆原文見首次 SAT 文件製作計畫的「2026-11-12 追加：提交前的五題與後續事項」。下面各表的路徑從 `src/JET/tests/` 起算。

兩個帶 approved 的識別字（裁定 2026-11-12-10）。新名稱是 Agent 讀過用途後取的，使用者 2026-11-12 確認照用：

| 檔案 | 原本 | 改成 | 用途 |
|:---|:---|:---|:---|
| `JET.Tests/Architecture/TerminologyUnificationFrontendTests.cs` | 變數 `approvedSentence` | `completenessMismatchSentence` | 完整性測試說明裡「金額不符時，要確認 GL 分錄是否完整。」這一句；測試確認它在 `validate-step.js` 只出現一次 |
| `JET.Tests/Architecture/ApprovedUxFrontendTests.cs` | 類別 `ApprovedUxFrontendTests` | `InteractionAndPresentationFrontendTests`，檔名跟著改 | 讀前端原始碼，核對刪除案件對話框與切換步驟時的鍵盤焦點、Gui 測試假選檔器的標題，以及圓角與陰影的樣式 |

掛到 `window` 的名稱（裁定 2026-11-12-11）。使用者作答時題目寫 17 個名稱、20 處；動手前重新搜尋 `src/JET/tests/frontend` 與 `tools/tests`，
不是 `exposed` 開頭的有 21 個名稱、25 處，多出的是 `step4App`、`bindGroupTest`、`bindReviewedGlMapping` 與 `reviewMetadataRule`。
掛同一個前端檔的一組函式時，沿用既有的 `exposedFilter`、`exposedImport` 與 `exposedApp`；只掛一個函式或一個區塊時另外取名，這 7 個新名稱使用者 2026-11-12 確認照用。
每個夾具各自建立 `window`；在別的夾具的 `window` 上再掛一次時，用的名稱和已經掛上的不同。

| 檔案 | 原本 | 改成 | 掛的內容 |
|:---|:---|:---|:---|
| `frontend/frontend-journey-filter-build.test.cjs` | `filterBuild` | `exposedFilter` | `filter-step.js` 的 `syncViewState`、`render` 與 `toWireDraft` |
| `frontend/frontend-journey-filter-saved.test.cjs` | `importOps` | `exposedImport` | `import-step.js` 的 `applyResponse` |
| `frontend/frontend-journey-step4.test.cjs` | `step4App` | `exposedApp` | `app.js` 的 `renderStepNav`、`renderContent` 與 `overviewStepFactsHtml` |
| `frontend/frontend-workflow.test.cjs`、`tools/tests/frontend-development-repository.test.cjs` | `workflow` | `exposedFilter` | `scenarioLifecycleFixture` 取用的 `filter-step.js` 函式 |
| `frontend/frontend-workflow.test.cjs` | `bindGroupTest` | `exposedBindGroupSelection` | `FilterStep.bindGroupSelection` 一個函式 |
| `frontend/frontend-workflow.test.cjs` | `testSave` | `exposedFilter` | 儲存情境那則測試取用的 `filter-step.js` 函式 |
| `frontend/frontend-workflow.test.cjs` | `bindReviewedGlMapping`，兩則測試各掛一次 | `exposedBindGlMapping` | 包住 `mapping-step.js` 的 `bindMappingSection`、只綁 GL 配對區塊的函式 |
| `frontend/frontend-workflow.test.cjs` | `commonCards` | `exposedFilter` | `commonCardFixture` 取用的 `filter-step.js` 函式 |
| `frontend/frontend-workflow.test.cjs` | `reviewMetadataRule` | `exposedRuleControlsHtml` | `FilterStep.ruleControlsHtml` 一個函式；這則測試用的 `window` 已經有 `exposedFilter` |
| `frontend/frontend-workflow.test.cjs` | `categoryReview` | `exposedCategoryCard` | `validate-step.js` 科目分類卡片的 `categoryCardHtml`、`categoryRows`、`bindCategoryCard` 與 `bind` |
| `frontend/frontend-workflow.test.cjs` | `categoryFilterReview` | `exposedFilter` | `categoryReviewFixture` 取用的 `filter-step.js` 函式 |
| `frontend/frontend-workflow.test.cjs` | `calendarTest` | `exposedImport` | `import-step.js` 的 `calendarCard`、`bindCalendarCard` 與 `resetWizard` |
| `frontend/frontend-workflow.test.cjs` | `naming` | `exposedFilter` | `namingFixture` 取用的 `filter-step.js` 函式 |
| `frontend/frontend-workflow.test.cjs`、`frontend/support/frontend-fixtures.cjs`、`tools/tests/frontend-development-repository.test.cjs` | `remapApp` | `exposedApp` | 三個 `remapAppFixture` 各自取用的 `app.js` 函式 |
| `frontend/support/frontend-fixtures.cjs` | `ops` | `exposedFilter` | `filterOperationFixture` 取用的 `filter-step.js` 函式；`frontend-workflow.test.cjs` 有一處讀它 |
| `frontend/support/frontend-fixtures.cjs` | `overviewApp` | `exposedApp` | `overviewDialogFixture` 取用的 `app.js` 函式 `renderOverview`，以及開啟流程總覽的綁定 |
| `frontend/support/frontend-fixtures.cjs` | `mappingWire` | `exposedGlCommitPayload` | `mapping-step.js` 的 `glCommitPayload` 一個函式；`frontend-mapping.test.cjs` 有 23 處用它 |
| `frontend/support/frontend-fixtures.cjs` | `mappingEligibility` | `exposedMappingCommitEligibility` | `mapping-step.js` 的 `mappingCommitEligibility` 一個函式；`frontend-mapping.test.cjs` 有 2 處用它 |
| `frontend/support/frontend-fixtures.cjs` | `mappingRenderApp` | `exposedApp` | `mappingRenderFixture` 取用的 `app.js` 函式 `render` 與 `renderContent` |
| `frontend/support/frontend-fixtures.cjs` | `messagePanelApp` | `exposedApp` | `messagePanelFixture` 取用的 `app.js` 訊息面板函式，以及複製按鈕的綁定 |
| `tools/tests/frontend-development-repository.test.cjs` | `drawMapping` | `exposedMappingRender` | `mapping-step.js` 的 `render` 一個函式 |

有三個指派到 `window` 的名稱沒有改，待使用者確認。`frontend-workflow.test.cjs` 的 `commonCardFixture` 用 `window.deferred` 記下 `JetFocus.defer` 收到的回呼，它是測試替身自己的紀錄。
`frontend-support.test.cjs` 核對 `exposeAt` 時用 `window.seam = 1;` 與 `window.after = 1;` 當插入的範例字串，這兩段字串不會被執行。三個掛的都不是產品程式的內部函式。
瀏覽器與產品自己的名稱也照舊，例如測試替身設定的 `window.JetApi`、`window.JetFocus` 與 `window.getComputedStyle`。

往上找 `JET.slnx` 的方法與例外訊息（裁定 2026-11-12-12）。28 個測試檔各有一個私有方法 `RepoRoot`，共 71 處，改成 `SolutionRoot`：
`JET.Tests/Architecture` 25 個檔，`JET.Tests/Application` 的 `GlobalExceptionFallbackLogTests.cs` 與 `WebViewSecurityPolicyTests.cs`，以及 `JET.Tests/Infrastructure` 的 `ReportTemplateExternalRelationshipTests.cs`。
這個方法回傳 `JET.slnx` 所在的 `src/JET`；測試專案共用的 `TestRepositoryPaths.SolutionRoot` 指的是同一個資料夾，所以沿用這個名稱。
例外訊息「向上尋不到 JET.slnx，無法定位 repo 根。」在 `JET.Tests/Architecture` 的 15 個檔各 1 處，改成其他測試原本就用的「向上尋不到 JET.slnx。」。
其中 `FilterTemplatesFrontendTests.cs`、`PagedTableFrontendTests.cs` 與 `PrescreenRuleRegistryTests.cs` 沒有 `RepoRoot` 方法，只改訊息。
改之前搜尋過，這句訊息只出現在丟出例外的那一行，沒有測試比對它。
`FilterConditionLabelMirrorTests.cs` 說明這個方法的註解原本寫「定位 repo 根」，改成「回傳方案檔所在的資料夾」，和 `SupportedActionsParityTests.cs` 的寫法一致。

前端測試的三則失敗訊息（裁定 2026-11-12-13）。只改斷言失敗時印出的訊息，比對的內容不變：

| 檔案 | 原本 | 改成 |
|:---|:---|:---|
| `frontend/frontend-journey-filter-build.test.cjs` | condition row shows the approved sentence | condition row shows the cash note |
| `frontend/frontend-workflow.test.cjs` | saving categories clears saved scenarios as ruled on 2026-10-07 | saving categories clears the saved scenarios |
| `frontend/frontend-workflow.test.cjs` | one match is enough; no unapproved ratio threshold | one match is enough; no ratio threshold |

流程總覽測試的資料庫欄位（裁定 2026-11-12-14）。`frontend/frontend-journey-overview.test.cjs` 原本接受 SQLite、DuckDB 與 SQL Server 三種文字，收緊成只接受 SQLite。
這是修改既有斷言，原因與第一次失敗的情形記在 [`test-change-records.md`](test-change-records.md)。

改完的檢查：

- **只改了上面列的地方。** 改名前把 `src/JET/tests/JET.Tests` 的 C# 原始檔、`src/JET/tests/frontend` 與 `tools/tests` 的前端測試檔另存一份。改名用腳本做，每一項都先核對出現的次數，次數不符就不寫檔。
  改完和另存的檔比對，連同「用計畫編號取的名稱」的三組，共 41 個檔、193 行不同：C# 33 個檔，前端測試 8 個檔。每一行不同的地方都是上面列的名稱、訊息、那一句註解，或流程總覽測試的那一則斷言。
- **找不到舊名稱。** 搜尋 `src/`、`tools/`、`.agents/`、`.claude/`、`.codex/`、`.github/` 與 `docs/`，舊名稱只剩在 `docs/history/` 的紀錄、首次 SAT 文件製作計畫記錄當時狀況的段落與使用者原話、裁定總表與名詞表寫明由哪個舊名稱改過來的地方，
  以及架構圖來源清單裡 `ApprovedUxFrontendTests.cs` 的路徑；那份清單是提交 `40440b9` 的封存快照，下次重繪架構圖時再重新產生。
  `GuiScenarioCoverageTests.cs` 有一個 C# 區域變數也叫 `filterBuild`，它存的是讀進來的測試檔內容，和掛到 `window` 的名稱無關，沒有改。
- **測試。** 驗證的命令與結果見首次 SAT 文件製作計畫的「2026-11-12 追加：測試名稱、訊息與流程總覽測試的六件事」。

### 2026-11-12 照使用者回覆改的註解與註解檢查

使用者 2026-11-12 的回覆裡有七件和 `src/JET` 的註解有關：六件改註解或空行，一件擴充註解檢查。這一節記錄照回覆做了什麼。
回覆原文見首次 SAT 文件製作計畫的「2026-11-12 追加：提交前的五題與後續事項」，七件依序登錄成裁定 2026-11-12-16 到 2026-11-12-22。
下面的路徑從 `src/JET/` 起算，行號是改寫前的行號。改寫前的原文附在原文紀錄的第九部分：88 個檔、136 段、146 行。

| 事項 | 檔數 | 改了多少 | 裁定 |
|:---|---:|:---|:---|
| 「對齊樣本」這類說法 | 6 | 18 行 | 2026-11-12-16 |
| 「凍結契約」 | 4 | 5 行 | 2026-11-12-17 |
| 「舊版 JET」指哪一版 | 40 | 62 行 | 2026-11-12-18 |
| 只寫文件名稱、沒有章節編號的註解 | 37 | 54 行 | 2026-11-12-19 |
| 底稿寫出測試的分段標題 | 3 | 改 7 行，新加 3 行 | 2026-11-12-20 |
| 說明和方法之間的空行 | 2 | 拿掉 2 行空行 | 2026-11-12-21 |
| 擴充註解檢查 | 1 | 只有 `SourceCommentPolicyTests.cs` | 2026-11-12-22 |

有幾個檔同時在兩件裡。改註解與空行的六件不重複計算是 88 個檔：產品程式 27 個，測試 61 個。
做的順序照測試先行：先擴充註解檢查，確認它因為現有的註解而失敗，再改註解。

#### 擴充註解檢查

`SourceCommentPolicyTests` 加了兩類檢查，原有的樣式與斷言沒有改。

- **開發任務的用語。** 加進原有的樣式清單 `DevelopmentRecords`，和原本四類一樣整行都看，註解、字串與識別字都算。
  抓的字樣有：接在「對齊」「皆照」「和」「舊版」後面的「樣本」、「本機參考樣本」、「樣本顯示形式」與「樣本全表」；「凍結契約」與「凍結裁決」；
  Task 加數字再加冒號、接在「此」「本」後面的 task、TDD 加井號與數字、jet-testing、`.git/sdd`、user-approved、
  brief 後面接冒號、「設計」或「允許」，以及 DRY 加等號與數字。Task 之後這幾種是「原本就有的開發經過註解」那一輪改掉的寫法，當時的檢查抓不到。
- **文件章節的寫法。** 新的一組樣式 `DocumentSections`，由新方法 `Comments_DoNotCiteDocumentSections` 掃描，只看每一行從註解記號開始的部分。
  抓的寫法有：章節符號，接在 ECMA-376 後面的不算；jet-guide、master spec、guide section 加數字；guide 或 spec 加帶小數點的編號；spec 加字母與編號，例如 `spec C.3`；
  FD 加數字；action-contract-manifest；以及 manifest、「前端說明」、「action 契約」、frontend description 後面直接接上引號的寫法，也就是文件名稱加節名。
  只看註解、不看字串，是因為測試輔助程式 `LegacyAuditParityComparator.cs` 用 `docs/jet-guide.md` 加章節的字串標示比對依據，那是比對時用的資料。
- **不該擋的例子。** `PatternsLeaveOrdinaryCommentsAndDataAlone` 加 7 個例子，確認正常的用語不會被擋：「用字已定案」「可靠性樣本」「摘要樣本」「介面凍結」、`Task.WhenAll`、「該 task」與 briefing。
  新方法 `DocumentSectionPatternsLeaveOrdinaryCommentsAndStringsAlone` 也有 7 個例子：ECMA-376 的條文編號、指報告清單的 manifest、寫到短表頭「項次」的句子、「規格手算」、
  程式檔 `action-contract.cjs`，以及兩個寫在字串裡的文件名稱。
- **該擋的例子。** `PatternsRecogniseEachKindOfDevelopmentRecord` 加 12 個「開發任務的用語」的例子；新方法 `DocumentSectionPatternsRecogniseEachWayOfCitingASection` 有 12 個引用章節的例子。

第一次失敗分兩步確認，命令都是 `Focused` 篩選 `SourceCommentPolicyTests`：

1. 只加例子、還沒加樣式時，有 2 則失敗：`PatternsRecogniseEachKindOfDevelopmentRecord` 與 `DocumentSectionPatternsRecogniseEachWayOfCitingASection`，各 12 個該擋的例子都沒有被擋。
2. 加上樣式、還沒改註解時，選到的 30 個案例有 28 個通過、2 個失敗。`ProductAndTestSources_DoNotCarryDevelopmentRecords` 列出 21 行「開發任務的用語」：
   寫樣本的 18 行與寫「凍結契約」的 3 行，都在下面要改的檔案裡。`Comments_DoNotCiteDocumentSections` 列出 11 行：
   `DevDbHandlersTests.cs`、`ImportProgressEventTests.cs`、`ProjectHandlersTests.cs`、`ProjectSaveProgressHandlerTests.cs`、`SupportLogUnavailableProjectTests.cs`、
   `WebViewEventPublisherTests.cs` 與 `frontend-mapping.test.cjs` 各 1 行，另外 4 行是這個測試檔自己的例子。
   測試檔那 4 行把章節符號直接寫成字元，所以被自己掃到；改成跳脫寫法後不再出現。其餘 7 行照下面「只寫文件名稱的註解」改寫。
3. 註解改完後通過：選到的 30 個案例全部通過，固定補跑的架構檢查 402 個也通過。

這兩類檢查擋不到的寫法：跨兩行寫的「凍結」「契約」；只寫 manifest、後面沒有接節名的句子；單獨的「參考樣本」，因為 `SensitiveDataPolicyTests.cs` 有一筆測試資料的字串含這四個字。

#### 「對齊樣本」這類說法

題目寫 17 行。動手前照新加的樣式重新搜尋是 18 行：四個底稿寫出的程式檔 14 行，`LocalAccountMappingExportRepository.cs` 1 行，`WorkingPaperWriterCoverAndReferenceSheetTests.cs` 3 行。
程式檔比先前數的 13 行多 1 行，多的是哪一行沒有回頭比對。
逐行找「樣本」指什麼時，對照的是內嵌範本 `JET/Templates/WorkingPaper.xlsx` 與 `legacy/` 的兩份 IDEA 版程式，沒有讀 `data/` 底下的檔案。結果分三種：

- **範本裡查得到，改成寫範本。** step2 表頭、step3 與 step4 固定文字的合併範圍，內嵌範本同一張工作表的同樣位置都有，註解改成「範本裡也有」。
  step2 那一行另外補一句：攸關資料元素欄的合併範圍依欄數另外加。三張自動化工具表接在 step5 後面的順序和範本的工作表順序相同，改成「照範本的工作表順序」。
- **和範本對不上，拿掉。** step2 的固定文字與欄標原本寫「逐字對齊樣本」。對照內嵌範本：範本的說明標題是「說明：」，程式寫「測試說明：」；註 3 在範本是第 46 列，程式寫在第 47 列；
  第 51 列的 E、F 兩欄，程式寫「借方金額」「貸方金額」，範本是借貸方代號與「金額 - 借(貸)」。所以「逐字對齊樣本」拿掉，只留這段程式寫什麼。
  這裡比的是程式碼和範本，沒有產生底稿看實際輸出；兩者為什麼不同，列在「還沒處理」。
- **查不到依據，拿掉。** 日期顯示成 yyyy/MM/dd、整張表靠上對齊、step4 依情境算出欄位、科目配對依科目編號升冪、假日表的標頭，以及假日的 IS_HOLIDAY 一律寫 Y，
  在範本與 IDEA 版程式裡都找不到能叫「樣本」的對照。這幾行只拿掉「樣本」的說法，程式做的事照寫。
  `WorkingPaperWriter.FixedSheets.cs` 說封面 A6 的那一行原本寫「照舊版樣本底稿的外觀」，查不到是哪一份底稿，改成「A6 只保留儲存格格式，不寫字」；後半句「兩份舊版 IDEA 程式都沒有寫這一格」照舊。
  `WorkingPaperStyles.cs` 黃色底的色碼那一行也只拿掉「對齊樣本」。內嵌範本的樣式表有同一個色碼，但沒有查是哪一格在用，所以沒有寫成照範本。

| 檔案與行號 | 原本 | 改成 |
|:---|:---|:---|
| `JET/Infrastructure/Export/WorkingPaperWriter.Step2To41.cs` 第 2078 行 | step3 固定文字合併範圍(逐字對齊樣本)。 | step3 固定文字合併範圍,範本裡也有這幾個。 |
| `JET/Infrastructure/Export/WorkingPaperWriter.cs` 第 367 行 | 三張「自動化工具」參考資料表(對齊樣本順序,接在 step5 後)。 | 三張「自動化工具」參考資料表(照範本的工作表順序,接在 step5 後)。 |
| `JET/Infrastructure/Export/WorkingPaperWriter.Step2To41.cs` 第 108 行 | 測試說明 + 測試程序固定文字(逐字對齊樣本;列號/欄/合併皆照樣本)。 | 測試說明 + 測試程序固定文字。 |
| `JET/Infrastructure/Export/WorkingPaperWriter.FixedSheets.cs` 第 269 行 | 日期由 yyyy-MM-dd 轉樣本顯示形式 yyyy/MM/dd; | 日期由 yyyy-MM-dd 轉成 yyyy/MM/dd 顯示; |
| `JET/Infrastructure/Persistence/Local/LocalAccountMappingExportRepository.cs` 第 10 行 | 排序鍵 account_number ASC，和樣本一樣依科目升冪。 | 排序鍵 account_number ASC，依科目編號升冪。 |

#### 「凍結契約」

題目寫產品程式 3 行。測試 `TypedFieldRuleValidationTests.cs` 另有一處跨兩行寫「凍結」「契約」，一起改，共 5 行。每一處換成那條規定實際要求什麼：

| 檔案與行號 | 原本 | 改成 |
|:---|:---|:---|
| `JET/Application/Support/FilterScenarioPayloadParser.cs` 第 334 行 | 凍結契約要求 operand carrier 是 | typed 條件的 operand carrier 必須是 |
| `JET/AuditCore/GlRulePredicates.cs` 第 365 行 | amountBasis 是凍結契約的必填 closed token | 每條 money 條件都要帶 amountBasis，值只能是 signed 或 absolute |
| `JET/Domain/Rules/FilterScenario.cs` 第 69 行 | values 上限（凍結契約：1–100 個字串） | values 上限：每條要有 1–100 個字串 |
| `tests/JET.Tests/Domain/TypedFieldRuleValidationTests.cs` 第 55 到 56 行 | 凍結契約要求「每條 money 規則明示 amountBasis」 | 每條 money 規則都要明示 amountBasis |

`FilterScenario.cs` 那一行的 1 到 100，對過同一個檔裡檢查個數的程式。

#### 「舊版 JET」指哪一版

用腳本只掃註解，含「舊版」的有 376 行。逐段讀前後文，分成四種：

- **指 IDEA 版的 JET，照舊。** 大部分是這一種，例如 `ResultPageSorting.cs` 說可靠性樣本的順序照舊版依傳票號碼升冪。
- **指新版 JET 較早的版本，改寫。** 連同同一句裡跟著調整的行，共 62 行、40 個檔。講案件的改成「較早版本的 JET 建立的案件」；講版本比較的改成「版本較舊」，或直接寫第幾版；
  講以前的程式怎麼做的句子，改成只寫現在的做法。
- **指資料或檔案的版本，照舊。** 14 行。說的是已存情境的規則版本、報告的版本這類資料自己的版本，不是 JET 的版本：
  `validate-step.js` 第 1210 行，`frontend-journey-export.test.cjs` 第 157 行，`frontend-journey-filter-saved.test.cjs` 第 656 行，
  `AdvancedFilterAstContractTests.cs` 第 302 行，`FilterDirectQueryStaleGuardTests.cs` 第 7 行，`FilterHandlersTests.cs` 第 932 行，`FilterHitPerScenarioTests.cs` 第 364 行，
  `FilterScenarioRuleUpgradeTests.cs` 第 175 行，`FilterScenarioSingleChangeTests.cs` 第 793 行，`ReportRevisionStateTests.cs` 第 51 與 76 行，
  `WorkingPaperDirectTemplateReportAtomicityTests.cs` 第 196 行，`ActionContractRuleVersionTests.cs` 第 11 行，`FilterScenarioStoreTests.cs` 第 10 行。
- **分不出來，沒有改。** 3 行，等使用者決定：
  - `ImportAccountMappingHandler.cs` 第 79 行說「舊版 JET 產物」與一般 CSV 仍維持第 1 列標頭。前後兩行另外寫「正式 legacy AccountMapping」與「舊範本」，看不出這三個是不是同一樣東西。
  - `JetSchemaCatalog.cs` 第 8 行「畫面或舊版對這份資料的稱呼」。同一個檔另外提到 legacy Excel-VBA 工具，可能是 IDEA 版以外的另一個舊工具。
  - `FieldInfoSheetRoundTripTests.cs` 第 109 行「舊版占位文字」。

| 檔案與行號 | 原本 | 改成 |
|:---|:---|:---|
| `JET/Domain/JetActionException.cs` 第 116 行 | 找不到案件資料夾，以及舊版 JET 建立的案件時的句子。 | 找不到案件資料夾，以及案件是較早版本的 JET 建立時的句子。 |
| `JET/Application/Handlers/Project/ProjectLoadHandler.cs` 第 73 行 | 這裡會重新檢查版本並拒絕舊版。 | 這裡會重新檢查版本，版本較舊就拒絕。 |
| `JET/wwwroot/js/steps/filter-step.js` 第 105 行 | 舊版情境重新編輯後一律改存第 2 版 | 第 1 版的情境重新編輯後一律改存第 2 版 |
| `JET/wwwroot/js/ui-core.js` 第 1205 行 | 未提交或舊版配對時為空陣列。 | 還沒提交，或提交的配對沒有 rdeFields 時為空陣列。 |
| `tests/JET.Tests/Application/ProjectHandlersTests.cs` 第 228 行 | 舊版 project.json（databaseProvider 欄位出現前的形狀）要明確拒絕，不猜成 sqlite。 | 沒有 databaseProvider 欄位的 project.json 要明確拒絕，不猜成 sqlite。 |
| `tests/JET.Tests/Application/FilterConditionRendererTests.cs` 第 277 行 | 舊版非營業日(I) 獨立條件組：照一般條件組讀回 | 已存情境裡兩條預篩選自成一組的非營業日條件(I)：照一般條件組讀回 |
| `tests/JET.Tests/Application/SupportLogDiagnosisTests.cs` 第 236 行 | 舊版每個例外只留前五層方法；這裡八層巢狀呼叫全部保留。 | 八層巢狀呼叫全部保留，不只留前幾層。 |

畫面訊息也寫「舊版 JET」。`ProjectFolderMessages.OldVersionProject` 這一句告訴審計員案件「是舊版 JET 建立的」，指的是較早版本的 JET。它是產品的字串，這次沒有改；
`JetActionException.cs` 說明這一句的註解改成「較早版本的 JET 建立的案件，訊息裡寫『舊版 JET』」。畫面用字要不要跟著改，等使用者決定。
英文或中英混寫的 legacy 註解只掃過、沒有改，見「還沒處理」。

#### 只寫文件名稱、沒有章節編號的註解

重新用腳本只掃註解。提到 manifest 的仍是 59 行、41 個檔，逐行分辨：

- **指 action 契約文件的 42 行，改寫。** 大部分只拿掉括號裡或句首的文件名稱，說明照舊；文件名稱後面接著錯誤碼的，改成寫「錯誤碼」加那個代碼。
  句子靠文件名稱才成立的，改成直接寫內容，例如 `ImportProgressEventTests.cs` 類別說明裡括號寫的文件名稱與節名，改成「由後端送給前端」。
- **指程式自己的東西的 17 行，照舊。** 說的是報告資料夾裡那份清單，或名稱裡有 Manifest 的類別：`AuditedReportStore.cs`、`JetAuditProgram.cs`、`PrescreenProgram.cs` 3 行、`ValidationProgram.cs`、
  `IReportStore.cs` 3 行、`ReportContracts.cs`、`ProjectReportStore.cs`、`WorkingPaperExportCancellationTests.cs`、`ReportExportOwnershipTests.cs`、`ReportKindValuesTests.cs`、
  `EmbeddedReportTemplateFailureTests.cs`、`PrivateCaseManifest.cs` 與 `ProjectReportStoreTests.cs`。

其他文件名稱另外找到 10 行，也改寫：寫「名詞表」的 4 行，`filter-vouchers.js`、`filter-step-rules.js`、`frontend-journey-step4.test.cjs` 與 `frontend-journey-tables.test.cjs` 各 1 行；
寫 action contract 的 `WorkingPaperProgram.cs` 1 行；同時寫「前端說明」與「action 契約」的 `SupportLogUnavailableProjectTests.cs` 1 行；
英文註解 4 行，`UsageScenarioProjectBoundaryTests.cs` 與 `frontend-workflow.test.cjs` 各 1 行，`WorkingPaperExportCapacityJourneyTests.cs` 2 行。
另有 2 行本身沒有文件名稱，因為和相鄰的行是同一句，跟著調整，所以一共 54 行。

| 檔案與行號 | 原本 | 改成 |
|:---|:---|:---|
| `tests/JET.Tests/Application/ImportAndMappingHandlersTests.cs` 第 202 行 | 來源多出 c、來源缺少 b（manifest column_mismatch） | 來源多出 c、來源缺少 b（錯誤碼 column_mismatch） |
| `tests/JET.Tests/Application/ProjectHandlersTests.cs` 第 219 行 | manifest「資料庫 provider 歸屬」：本地專案固定 sqlite。 | 本地專案的資料庫 provider 固定是 sqlite。 |
| `tests/JET.Tests/Infrastructure/EncodingDetectorTests.cs` 第 10 行 | oracle：手寫 bytes fixture 與 manifest encoding 白名單。 | oracle：手寫 bytes fixture 與 encoding 白名單（utf-8、big5、utf-16）。 |
| `JET/AuditCore/WorkingPaperProgram.cs` 第 35 行 | 上限與順序沿用既有 action contract。 | 取自案件已存的情境，個數與順序跟著已存情境。 |
| `JET/wwwroot/js/filter-vouchers.js` 第 202 行 | 表頭寫名詞表的短表頭「項次」 | 表頭寫短表頭「項次」 |
| `tests/frontend/frontend-workflow.test.cjs` 第 5574 行 | Expected behavior comes from the frontend description and the action contract. | The creation page marks both audit dates as required inputs. |

照舊保留的還有幾行，說的是測試讀的檔案本身，不是引用文件當依據：提到程式檔 `action-contract.cjs` 的 5 行，`ActionContractRuleVersionTests.cs` 第 10 行與 `TestProfileTraits.cs` 第 10 行，以及 `ui-core.js` 第 70 行。

#### 底稿寫出測試的分段標題

三個 `WorkingPaperWriter` 開頭的測試檔，逐段讀標題底下的測試方法，標題和內容不相符的改標題，測試沒有改。

| 檔案與行號 | 原本的標題 | 改成 | 原因 |
|:---|:---|:---|:---|
| `WorkingPaperWriterCoverAndReferenceSheetTests.cs` 第 21 行 | 封面 / 固定文字三表(內容不隨母體改變) | 封面、JE WorkingPaper說明與 step5 三張固定文字的表;另有一則核對封面與 step3 的查核期間 | 寫明是哪三張表；這一段另有一則核對查核期間的測試 |
| 同一個檔第 191 行 | 自動化工具-假期假日資訊(週末固定 + 假日/補班) | 自動化工具-假期假日資訊(週末表照案件的每週休息日,加上假日與補班) | 週末表跟著案件的每週休息日，不是固定的 |
| 同一個檔第 378 行前 | 沒有標題 | 整本活頁簿 | 最後一則測試核對整本活頁簿，原本接在假期假日那一段底下 |
| `WorkingPaperWriterStep1ToStep2Tests.cs` 第 20 行 | step2 可靠性(舊版 JET 的借貸代號與帶號金額) | step2 可靠性(借貸代號、帶號金額、欄寬與樣本順序);最後一則核對 step1 表頭的 A3 | 這一段還核對欄寬與樣本順序，最後一則是 step1 的 |
| 同一個檔第 242 行 | step1 完整性(全科目) | step1 完整性(全科目);中間一則核對 step1 家族各表的欄寬 | 中間有一則核對幾張表的欄寬 |
| 同一個檔第 385 行 | step1-1 借貸不平(條件例外表) | step1-1 借貸不平(有不平傳票才列明細);另有 step1 與 step1-1 的結論文字、step1-3 的表頭說明 | 這一段另有結論文字與 step1-3 表頭的測試 |
| 同一個檔第 742 行 | step1-3 正準條件表 | step1-3 只在有差異科目時產生,以及鎖定範圍 | 底下的測試核對的是這兩件事 |
| `WorkingPaperWriterStep3Step4Tests.cs` 第 20 行前 | 沒有標題 | 案件資料庫的內容讀不出來時的回報 | 檔案開頭兩則測試原本沒有標題 |
| 同一個檔第 216 行前 | 沒有標題 | 每張工作表的列號 | `WorkingPaperSheets_RowNumbers_AreUniqueAndAscending` 原本接在 step3 那一段底下 |
| 同一個檔第 240 行 | step4 符合高風險條件傳票(動態全 position 欄) | step4 符合高風險條件傳票(所選情境的條件欄) | 換成說明欄位是什麼 |

三個新加的標題，有兩個後面各加一行空行。step2 那一行標題裡的「樣本」說的是可靠性測試抽出的樣本，不是範本。

#### 說明和方法之間的空行

`WorkingPaperWriter.FixedSheets.cs` 的 `EmitFieldInfoSheetAsync` 與 `WorkingPaperWriter.Step2To41.cs` 的 `WriteSubstantiveHeader`，說明和方法之間各有一行空行，拿掉。
用腳本找整個 `src/JET` 裡說明後面隔一行空行才接成員的地方，只有這兩處。

#### 檢查

- **只動了註解與空行。** 動手前把 `src/JET` 的原始檔另存一份，共 1,149 個。改註解用腳本做，145 項各自先核對出現的次數，次數不是 1 就不寫檔。
  改完用腳本把前後的檔案拆成註解與註解以外兩部分：比對的 1,125 個檔裡，註解以外不同的只有 `SourceCommentPolicyTests.cs`。
  另外逐行比對有改的 88 個檔：有差異的行去掉註解後都相同，換行與檔頭沒有變。改寫前有差異的是 148 行，其中空行 2 行；改寫後是 151 行，其中空行 2 行。
  這是用腳本逐字元拆出註解的近似判斷，不是 Roslyn。有 2 行的註解寫在程式後面，同一行的程式沒有變。
- **註解檢查。** `Focused` 篩選 `SourceCommentPolicyTests`：選到的 30 個案例全部通過，固定補跑的架構檢查 402 個也通過。
- **完整公開測試。** 註解與測試都改完後跑 `Public`：C# 5,075 個案例，前端測試 FrontendMapping 1,016 個、FrontendPreview 12 個，全部通過，建置 0 個警告。
  C# 比上一次多 16 個，都是註解檢查新加的例子與方法。文件改完後再跑一次，結果相同。
- **文件檢查。** 文件改完後跑 `Documentation`，結果見首次 SAT 文件製作計畫的「2026-11-12 追加：原始碼註解的七件修改」。
- 這一輪沒有請子代理獨立複查，靠上面的腳本比對與測試確認。沒有逐句拿改寫後的註解再對一次程式，只在改寫當時讀過前後文。
  沒有執行 `Gui`、`Excel` 與 `PrivateCase`，沒有重新產生送審文件。沒有 stage、commit 或 push。

### 2026-11-12 再分辨一次的「舊版」與 legacy 註解

上一節留下 3 行分不出指哪一版的「舊版」，英文或中英混寫的 legacy 註解也只掃過。使用者 2026-11-12 同意再分辨一次，分得出來的改（裁定 2026-11-12-31）。

- **怎麼查的。** 子代理只讀檔案，對照 `src/JET` 的程式與測試、`legacy/` 的程式與說明、系統規格、名詞表與裁定總表，沒有讀 `data/`，沒有跑測試。
  除了點名的 3 行，另外在 `src/JET` 的註解裡搜尋 legacy、old version、previous version、formerly、used to 等英文說法，一共看了 176 行。
- **照舊的。** 101 行指 IDEA 版的 JET、它的範本或輸出；42 行和版本無關，例如只是提到名稱帶 Legacy 的識別字，或傳票代號剛好寫成 V1。
  `legacyOverrides` 的 3 行等 6 行說的是較早存下的資料，但已經寫成程式現在怎麼處理，不用改。
- **改寫成現況的 14 行。** 指新版 JET 較早的版本、資料格式或寫法。其中 7 行寫 legacy scalar 或「legacy 單選欄位」，說科目配對條件的單選分類欄位 `debitCategory`、`creditCategory` 還會被讀；
  程式現在只讀兩個分類身分的陣列，單選欄位照未知欄位處理，所以這 7 行照現在的程式改寫。Agent 另外搜尋過 `src/JET/JET` 的 C# 與 JavaScript，沒有讀這兩個欄位的程式。
  `FieldInfoSheetRoundTripTests.cs` 的「舊版占位文字」指較早版本的 JET 在沒有借方代碼時寫進說明句的配對鍵 `drCrDebitCode`，改寫時保留「這是為了讀較早版本寫出的檔案」這件事。
- **寫明是哪一個的 4 行。** `JetSchemaCatalog.cs` 有 2 行只寫 legacy，補上 Excel-VBA 工具；說表名取自「舊版」的那一行改成「舊版工具(Excel-VBA 工具或 IDEA 版 JET)」，這個用字是 Agent 照名詞表「程式識別字先找舊版 VBA 或 IDEA 的用語」推的。
  `SixReportWorkflowJourneyTests.cs` 的 legacy entry catalogue 指 2024 年篩選條件表的條件目錄，改成 the 2024 entry catalogue。
- **分不出來、當時沒有改的 2 行。** `ImportAccountMappingHandler.cs` 的「舊版 JET 產物」：標頭在第 1 列的檔案有兩種，IDEA 版與新版底稿的「自動化工具-科目配對資訊」工作表，以及較早版本的 JET 產生的科目配對範本，查不到當時指哪一種。
  `JetFieldCatalog.cs` 的「空白 legacy 值」：可以讀成較早版本存下的配對裡留著的空字串，也可以讀成泛指沒填的配對值。兩行的原句已經列給使用者。使用者 2026-11-12 決定改成不必分辨也成立的寫法（裁定 2026-11-12-33）：前一行改成直接寫哪些檔案的標頭在第 1 列，後一行改成「配對值空白一律當成沒有來源」。
- **不在範圍、沒有改的 4 行。** `FilterFactsPort.cs` 2 行、`PrescreenProgram.cs` 與 `ValidationProgram.cs` 各 1 行用 compatibility 說「留著原本的做法」。它們是用 legacy 以外的字找到的，子代理也沒有核對使用端。使用者 2026-11-12 確認這次不改。
- **原文。** 改寫前的 18 行收在原文紀錄第十部分，共 13 個檔；後來改寫的 2 行，以及畫面訊息改用字時跟著改的 1 行註解，收在第十部分的補充。
- **檢查。** 套用時腳本逐行核對原文與縮排，對不上就不寫檔。驗證的結果寫在首次 SAT 文件製作計畫「2026-11-12 追加：使用者對十九件待決定的事的回覆」。

### 還沒處理

- 三個沒有改的 `window` 指派，使用者 2026-11-12 確認不改（裁定 2026-11-12-11），見「2026-11-12 改的測試名稱與失敗訊息」。同一節新取的 9 個名稱，使用者 2026-11-12 已經確認照用。
- 測試裡還有其他帶 approved 的名稱，不在使用者 2026-11-12 第一次回覆的範圍，當時沒有改。這一點列的 9 個 C# 測試方法與 `tools/tests` 的測試名稱，使用者同日後來同意改名，已經改完，新舊名稱見名詞表「C# 測試的名稱」一列（裁定 2026-11-12-28）；測試輔助程式的名稱照使用者的決定先不動（裁定 2026-11-12-24）。當時的紀錄如下。C# 測試方法有 `PreparationDate_WarnsAtApprovedBounds_ButAlwaysCreates`、`ApprovedOperations_AppendExactMinimalAuditEvents`、`CriteriaUsesApprovedConditionText_WhileWorkingPaperStep3EndsAtColumnE`、`MainTabs_ExposeSourceAndStandardizedPairsWithApprovedExactLabels`、`PrescreenLabels_UseTheAuthorityConstantsAndRetainExactApprovedSentences`、`Current_UsesTheThreeApprovedRules`、`Create_AnyUnapprovedAlternative_IsRejected`、`ManualFlagNormalizationRequiresExactApprovedValues` 與 `DifferencePolicy_FontScaledRowHeights_OnlyAcceptsTheApprovedHeightPerSheet`；測試輔助程式有 `LegacyAuditParityBasis.ApprovedIntentionalDifference` 與 `PrivateCaseReportDifferencePolicy` 的 `Approved` 開頭常數；`tools/tests/frontend-development-repository.test.cjs` 有一則測試名稱寫 approved assessment text。要不要改，等使用者決定。
- 往上找 `JET.slnx` 的私有方法在 28 個測試檔各寫一份，做的事和 `TestRepositoryPaths.SolutionRoot` 相同。這次只改名稱與訊息，沒有改成共用同一份。
- `TerminologyUnificationFrontendTests.cs` 檢查停用字樣時，先把 `completenessMismatchSentence` 那一句從 `validate-step.js` 的內容裡拿掉再掃描，旁邊的英文註解說這是唯一的例外。現在這一句不含清單上任何停用字樣，例外有沒有作用、註解要不要改，這次沒有查。
- 架構圖的來源清單沒有刪掉 `SchemaIsolationGuardTests.cs` 的路徑；使用者 2026-11-12 決定下次重繪架構圖時再重新產生，見「刪掉的測試與樣式」。
- 使用者 2026-11-12 回覆的七件註解修改已經做完，見「2026-11-12 照使用者回覆改的註解與註解檢查」。下面幾項是那一輪留下的：
  - 註解檢查新加的名稱，使用者 2026-11-12 確認照用（裁定 2026-11-12-22）：測試方法 `Comments_DoNotCiteDocumentSections`、`DocumentSectionPatternsLeaveOrdinaryCommentsAndStringsAlone` 與 `DocumentSectionPatternsRecogniseEachWayOfCitingASection`，
    樣式清單 `DocumentSections`，以及兩類樣式的名稱「開發任務的用語」與「文件章節的寫法」。
  - （後來已經再分辨一次，見「2026-11-12 再分辨一次的『舊版』與 legacy 註解」。）當時「舊版」分不出指哪一版的 3 行沒有改：`ImportAccountMappingHandler.cs` 第 79 行、`JetSchemaCatalog.cs` 第 8 行與 `FieldInfoSheetRoundTripTests.cs` 第 109 行。
  - 畫面訊息 `ProjectFolderMessages.OldVersionProject` 寫案件「是舊版 JET 建立的」，指的是較早版本的 JET。它是產品的字串，當時沒有改；使用者 2026-11-12 決定改成「較早版本的 JET」，已經改完（裁定 2026-11-12-32）。
  - （後來已經再分辨一次，見同一節。）當時英文或中英混寫的 legacy 註解只掃過、沒有改。`legacy scalar` 說的是較早的條件寫法：`FilterScenarioPayloadParser.cs` 第 386 行、`FilterScenario.cs` 第 778 行、`ui-core.js` 第 2703 行、
    `filter-step-rules.js` 第 1189 行與 `AccountPairCategorySelectionCompilationTests.cs` 第 12 行。`legacyOverrides` 在 `ui-core.js` 第 198 行與 `filter-step-sources.js` 第 250 行。
    `JetSchemaCatalog.cs` 另外提到 legacy Excel-VBA 工具。這些行號是改寫前的行號。
  - 底稿 step2 的程式和內嵌範本有三處不同：說明標題的文字、註 3 所在的列號，以及金額欄的欄標。這是讀 `WorkingPaperWriter.Step2To41.cs` 和範本得出的，沒有產生底稿看實際輸出，也沒有查原因。
    這次只拿掉註解裡「逐字對齊樣本」的說法，程式沒有動。
    使用者 2026-11-12 同意另外查原因。查到三處都只存在於程式的字串，匯出的底稿照內嵌範本，不受影響；結果記在裁定 2026-11-12-30。
  - 其他寫「凍結」的註解不在回覆的範圍，沒有改：`BulkRowWriterTests.cs` 第 216 行的「行為凍結」，`LegacyFormWorkflowTests.cs` 第 19 與 30 行的「凍結的目錄」。
  - 讀前後文時另外看到、沒有改的：`ui-core.js` 第 13 行的「第一個正式版先隱藏」；`ActionShapeJourney.cs` 第 602 行的字串「合成：舊版存下的條件。」，它是字串不是註解；
    測試方法名稱 `Step13_ExistsWhenDiffPresent_WithoutRemovedStep131` 帶著已拿掉的工作表。
  - 註解檢查擋不到的寫法見同一節的「擴充註解檢查」。
- `OpenXmlSaxTableReaderTests.cs` 兩處提到真實 PBC 檔的註解已經改寫（裁定 2026-11-12-03）；原文帶有取自真實檔案的數值，不收進原文紀錄。
  提交前複審另外指出 4 處註解也寫「真實 PBC」，只提格式的特徵，沒有數值：`MoneyScalingTests.cs` 與 `TbRowProjectorTests.cs` 各 1 處，`ExcelDateFormatDetectorTests.cs` 2 處。
  這 4 處照同一則裁定改成不提真實檔案，只改註解；原文也沒有另外收進原文紀錄。
- 拿掉章節編號的 68 行只拿掉引用，沒有逐句核對留下的說明和現在的程式是否相符。
- 前幾輪改寫時採用的「用字已定案」這類寫法還在，含「定案」的註解共 185 行、77 個檔，其中產品程式 18 行。
  原本是 187 行、79 個檔；拿掉章節編號時，`BlankRecordsPageTests.cs` 與 `PeriodBoundingDemoTests.cs` 各 1 行的「定案語意」跟著引用一起拿掉。
  它說的是畫面文字不能隨意改，和拿掉 approved 的做法不完全一致。使用者 2026-11-12 決定照舊保留；註解檢查有一個例子確認這種寫法不會被擋。
