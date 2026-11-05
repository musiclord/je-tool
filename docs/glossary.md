# JET 名詞表

更新日期：2026-11-05

這份文件規定 JET 每個業務概念在畫面、報告、底稿、action、資料庫、程式與測試各用什麼名稱，一個概念一列。
Agent 取名或改名時只用這裡的名稱；新的名稱先寫進這裡，經使用者確認後才用。報告與底稿上照舊版範本的欄名與工作表名不改。

使用者 2026-10-30 在確認頁逐列確認了這份名詞表，回覆原文見 [專案紀錄收斂計畫](history/specs/2026-10-29-records-and-judgment-basis-plan.md) 附錄一「2026-10-30：第二階段確認頁的回覆」；最後 4 列同日在第四階段確認頁確認，見同一附錄「2026-10-30：第四階段確認頁的回覆」。
確認時程式還沒改名，所以當時「各層現在的名稱」寫的是舊名稱，「確認的改法」寫要改成什麼。第四階段把程式改名後，
「各層現在的名稱」換成了新名稱，做完的列「確認的改法」改回照舊；還沒做完的列，例如畫面用語或少數測試名稱，「確認的改法」只留還沒做的部分。

## 怎麼讀

| 欄位 | 寫什麼 |
|:---|:---|
| 概念 | 審計員理解的業務概念 |
| 意思 | 這個概念指的是什麼 |
| 畫面與報告 | 畫面、報告、底稿與檔名上的用語 |
| 各層現在的名稱 | action 與資料預覽的鍵、資料庫與案件檔、程式識別字與測試，目前各用什麼名稱 |
| 確認的改法 | 寫「照舊」的，各層名稱不改；其餘寫要改成什麼。寫「待使用者確認」的還不能照改 |
| 依據 | 使用者哪一天在哪一題確認；有理由或名稱來源的一併寫出 |

## 命名方式

下面是使用者確認的名稱共同遵守的取法，由 Agent 依使用者 2026-10-29 在確認頁 4-3、4-4 的備註整理。
之後要提新名稱時照同樣的方式提，仍要先寫進這份名詞表，經使用者確認後才用。

1. 同一個概念在每一層用同一個英文詞，只依各層慣例換大小寫：資料庫用小寫加底線，C# 型別與屬性用大寫開頭，
   JavaScript 與 action 欄位用小寫開頭。例如總帳日期是 `gl_date`、`GlDate`、`glDate`。
2. 表名用單數名詞，直接取畫面或舊版對這份資料的稱呼，不加 `staging_`、`target_`、`result_`、`config_` 這類分層字首，
   也就不必再經過 `JetSchemaCatalog` 對照成畫面名稱。
3. 審計員在資料預覽看得到的資料集，表名就是資料預覽名稱的轉寫，例如 PBC-GL 是 `pbc_gl`，GL 是 `gl`，科目配對是 `account_mapping`。
   GL(未納入測試) 和 GL 在同一張表，用欄位區分。
4. 匯入時原樣暫存的資料一律用 `pbc_` 開頭，例如 `pbc_account_mapping`。
5. 程式識別字先找舊版 VBA 或 IDEA 的用語，其次是使用者原話與已確認的畫面用語；兩者都沒有時才由 Agent 取英文，並在「依據」寫明。
6. 技術慣例保留：金額欄的 `_scaled` 後綴表示放大成整數存放，布林欄用 `is_` 開頭，時間欄用 `_utc` 結尾，識別碼用 `_id` 結尾。
7. 報告與底稿的欄名、工作表名與範本檔名照舊版範本，例如「總帳日期_JE」與 `WorkingPaper.xlsx`。
   底稿的英文名稱依審計部門統一的命名規範，一律寫 WorkingPaper，這是使用者 2026-10-30 的裁定。
8. 條件型別值存在已儲存的情境裡，`group`、`voucher`、`typed` 都不改。

## 資料庫表名

文件檢查會核對 SQLite、DuckDB 與 SQL Server 的案件資料庫，以及 SQL Server 共用資料庫建立的每張表都列在這裡，這裡列的表也都還存在。
SQL Server 共用資料庫登記案件、使用者、權限、鎖定與設定的管理表，使用者 2026-11-01 在第三個停點 2-2 選擇照現行名稱列進來（裁定 2026-11-01-02），2026-11-02 在第四個停點確認照現行名稱（裁定 2026-11-02-02）。

| 現在的表名 | 確認的表名 | 概念 |
|:---|:---|:---|
| `pbc_gl` | 照舊 | PBC-GL |
| `pbc_tb` | 照舊 | PBC-TB |
| `gl` | 照舊 | GL |
| `tb` | 照舊 | TB |
| `account_mapping` | 照舊 | 科目配對 |
| `pbc_account_mapping` | 照舊 | 科目配對 |
| `authorized_preparer` | 照舊 | 授權編製人員清單 |
| `pbc_authorized_preparer` | 照舊 | 授權編製人員清單 |
| `calendar_day` | 照舊 | 國定假日與補班日 |
| `account_category` | 照舊 | 科目分類（分類設定） |
| `account_category_path` | 照舊 | 科目分類（分類設定） |
| `field_mapping` | 照舊 | 欄位配對 |
| `field_mapping_previous` | 照舊 | 前次確認的配對 |
| `field_info` | 照舊 | 檔案欄位資訊（欄位定義） |
| `gl_rde_field` | 照舊 | 攸關資料元素欄位 |
| `gl_rde_value` | 照舊 | 攸關資料元素欄位 |
| `gl_control_total` | 照舊 | 存入筆數與合計核對 |
| `validation_result`、`prescreen_result` | 照舊 | 驗證與預篩選的執行結果 |
| `filter_hit` | 照舊 | 篩選命中 |
| `filter_hit_condition` | 照舊：附錄六的名稱照用（裁定 2026-11-01-01） | 篩選命中 |
| `inf_sample` | 照舊 | 資料可靠性測試 |
| `filter_scenario` | 照舊 | 篩選情境 |
| `result_stale_state` | 照舊 | 結果過期 |
| `message_log` | 照舊 | 狀態與訊息 |
| `operation_log` | 照舊 | 操作紀錄 |
| `schema_info`、`import_batch`、`import_batch_source` | 照舊 | 沒有業務名詞的系統表 |
| `project_registry` | 照舊（裁定 2026-11-02-02） | SQL Server 共用資料庫：登記有哪些案件 |
| `project_access` | 照舊（裁定 2026-11-02-02） | SQL Server 共用資料庫：誰可以開哪個案件 |
| `project_lock` | 照舊（裁定 2026-11-02-02） | SQL Server 共用資料庫：案件目前由誰開著 |
| `app_user` | 照舊（裁定 2026-11-02-02） | SQL Server 共用資料庫：使用者 |
| `app_config` | 照舊（裁定 2026-11-02-02） | SQL Server 共用資料庫：設定 |
| `operation_log`（共用資料庫） | 照舊（裁定 2026-11-02-02） | SQL Server 共用資料庫：案件以外的操作紀錄；和案件資料庫的操作紀錄同名 |

## 資料集

| 概念 | 意思 | 畫面與報告 | 各層現在的名稱 | 確認的改法 | 依據 |
|:---|:---|:---|:---|:---|:---|
| PBC-GL | 匯入的總帳明細原樣，還沒經過欄位配對整理。 | 資料預覽「PBC-GL」；第二步匯入項目「GL (總帳明細)」 | action：資料預覽 dataset `pbcGl`；匯入 `import.gl.fromFile`；`dataset_kind` 值 `gl`。資料庫：`pbc_gl`（batch_id、row_number、source_no、source_row_number、row_json）。程式：`DataPreviewDataset.PbcGl`、`DataPreviewDatasetLabels.PbcGl`；`JetSchemaCatalog` 把表名對照成 PBC-GL。測試：`DataPreviewFrontendTests`（鏡射檢查） | 照舊 | 2026-10-30 確認頁 1-1：照提議。理由：2026-10-29 確認頁 4-3 要求實體表名統一命名；表名直接轉寫畫面名稱，`JetSchemaCatalog` 的對照就可以拿掉。名稱來源：畫面名稱：使用者 2026-10-08 指定（DataPreviewDatasetLabels.cs:6）；舊版叫 JE_PBC（JetSchemaCatalog.cs:49 註解）；表名 Agent 自取 |
| PBC-TB | 匯入的試算表原樣，還沒經過欄位配對整理。 | 資料預覽「PBC-TB」；第二步匯入項目「TB (試算表)」 | action：資料預覽 dataset `pbcTb`；匯入 `import.tb.fromFile`；`dataset_kind` 值 `tb`。資料庫：`pbc_tb`。程式：`DataPreviewDataset.PbcTb`、`DataPreviewDatasetLabels.PbcTb` | 照舊 | 2026-10-30 確認頁 1-2：照提議。理由：同 PBC-GL 一列。名稱來源：畫面名稱：使用者 2026-10-08 指定；舊版叫 TB_PBC（JetSchemaCatalog.cs 註解）；表名 Agent 自取 |
| GL | 確認 GL 欄位配對後，納入測試的標準化分錄。 | 「GL」；條件句寫「GL 中」；底稿「JE測試母體」 | action：資料預覽 dataset `gl`；`DatasetKind.Gl`。資料庫：`gl`。程式：`IGlRepository`、`GlProjectedRow`、`GlRowProjector`、`DatasetKind.Gl`。測試：`SqlServerGlRepositoryTests` 等 | 照舊 | 2026-10-30 確認頁 1-3：照提議。理由：2026-10-29 確認頁 4-3；表名和畫面名稱相同。名稱來源：畫面名稱：使用者 2026-10-08 把舊版 JE 改叫 GL（JetSchemaCatalog.cs:49 註解）；舊版 VBA 表名 JE（legacy/VBA-legacy/DbSchema.cls）、IDEA `#GL#.IDM`；表名 Agent 自取 |
| GL 與 GL(未納入測試) 的區分 | 同一張分錄表依查核期間與過帳狀態分成納入測試與未納入測試兩群。 | 「GL」「GL(未納入測試)」「未納入原因」 | action：資料預覽 dataset `glExcluded`；`exclusionReason`；`validate.run` 回應 `populationSummary.gl`。資料庫：`gl.is_excluded`、`exclusion_reason`；`gl_control_total.gl_row_count`、`gl_debit_scaled`、`gl_credit_scaled`、`excluded_by_period_count`、`excluded_by_posting_status_count`。程式：`GlExcludedPopulation*`、`GlExcludedPopulationTotals`、`GlExclusionReason`、`ValidationGlTotals`。測試：`GlExcludedPopulationTests`、`GlExcludedPopulationReadGuardTests`、`GlExcludedPopulationProjectionProviderTests`、`GlQueryOwnershipTests` | 照舊 | 2026-10-30 確認頁 1-4：寫法 A。名稱來源：畫面名稱：使用者 2026-10-08 指定（jet-frontend-description.md:498）；舊版 VBA 分成 JE_IN_PERIOD 與 JE_NOT_IN_PERIOD（DbSchema.cls）；effective 與 excluded 兩詞是 Agent 自取 |
| GL 未納入原因 | 分錄沒有納入測試的四種原因。 | 「總帳日期空白」「早於查核期間」「晚於查核期間」「過帳狀態不符」 | action：`blankGlDate`、`beforePeriod`、`afterPeriod`、`postingStatus`。資料庫：`gl.exclusion_reason` 存同樣四個值。程式：`GlExclusionReason.BlankGlDate`、`BeforePeriod`、`AfterPeriod`、`PostingStatus`。測試：`BlankGlDateDetectionTests`、`BlankGlDateFrontendTests` | 照舊 | 2026-10-30 確認頁 1-5：照提議。理由：跟著已確認的 `postDate` 改 `glDate` 與 `nullPostDate` 改 `blankGlDate`。名稱來源：前三種畫面用語：使用者 2026-10-21 裁定（jet-frontend-description.md:498）；識別值 Agent 自取 |
| TB | 確認 TB 欄位配對後的標準化試算表。 | 「TB」 | action：資料預覽 dataset `tb`；`DatasetKind.Tb`。資料庫：`tb`（balance_id、change_amount_scaled 等）。程式：`ITbRepository`、`TbProjectedRow`、`TbRowProjector` | 照舊 | 2026-10-30 確認頁 1-6：照提議。理由：2026-10-29 確認頁 4-3。名稱來源：畫面名稱：使用者 2026-10-08 指定；舊版 VBA 表名 TB（DbSchema.cls）；表名 Agent 自取 |
| 科目配對 | 本案科目對應到標準化分類的清單，由配對檔匯入或在科目清單直接分類。 | 「科目配對」；底稿「自動化工具-科目配對資訊」 | action：`import.accountMapping.fromFile`、`accountMapping.save`、`query.accountMappingPage`、`query.accountMappingDifferencePage`、`query.accountMappingBlankPage`；資料預覽 dataset `accountMapping`；`dataset_kind` 值 `account_mapping`。資料庫：`account_mapping`、`pbc_account_mapping`。程式：`AccountMapping*`（約 40 個型別）。測試：`AccountMappingBlankCategoryTests` 等 | 照舊 | 2026-10-30 確認頁 1-7：照提議。理由：2026-10-29 確認頁 4-3；資料預覽鍵是全系統唯一的複數寫法。名稱來源：舊版 VBA 表 ACCOUNT_MAPPING 與 ServiceAccountMapping.cls、ImportAccountMapping（legacy/VBA-legacy）；IDEA「選擇特定科目配對」（idea-tool.bas:549） |
| 授權編製人員清單 | 審計員匯入、可以編製分錄的人員名單。 | 「授權編製人員清單」，第二步、流程總覽與錯誤訊息都用這一個名稱 | action：`import.authorizedPreparer.fromFile`、`import.authorizedPreparer.clear`；`authorizedPreparerState`、`matchedPreparerCount`；資料預覽 dataset `authorizedPreparer`。資料庫：`authorized_preparer`（name）、`pbc_authorized_preparer`。程式：`AuthorizedPreparer*`。測試：`AuthorizedPreparerWorkflowTests`、`AuthorizedPreparerColumnResolverTests`、`ImportAuthorizedPreparerHandlerTests`、`AuthorizedPreparerImportTests` | 畫面統一寫「授權編製人員清單」，第八階段改程式；各層照舊 | 2026-10-30 確認頁 1-8：照提議。理由：2026-10-29 確認頁 4-3；和科目配對同一套規則。畫面統一是 2026-11-03 第八階段確認頁 3-2 第 5 列（裁定 2026-11-03-10）。名稱來源：舊版 IDEA 與 VBA 查不到；Agent 自取 |
| 國定假日與補班日 | 案件上傳的國定假日清單與補班日清單。 | 資料預覽、第二步卡片與流程總覽「國定假日與補班日」；底稿「自動化工具-假期假日資訊」照舊版 | action：`import.holiday`、`import.makeupDay` 與各自的 `.fromFile`；資料預覽 dataset `calendar`。資料庫：`calendar_day`（day_type、date、day_name）。程式：`Calendar*`（CalendarDayEntry、ICalendarStore、CalendarFacts 等）；`JetSchemaCatalog` 對照 DATE_DIMENSION。測試：`CalendarContractsTests`、`CalendarImportProjectionTests`、`SqliteCalendarStoreTests`、`ImportCalendarHandlersTests` | 畫面改成「國定假日與補班日」，第八階段改程式；各層名稱照舊 | 2026-10-30 確認頁 1-9：寫法 A，當時畫面叫「行事曆（假日與補班日）」；2026-11-03 第八階段確認頁 3-1 改成「國定假日與補班日」（裁定 2026-11-03-10）。名稱來源：舊版 VBA 表 DATE_DIMENSION 與 ServiceDateDimension.cls（DbSchema.cls）；IDEA 與底稿寫「假期假日」；「行事曆」查不到使用者指定紀錄 |

## 行事曆與科目分類

| 概念 | 意思 | 畫面與報告 | 各層現在的名稱 | 確認的改法 | 依據 |
|:---|:---|:---|:---|:---|:---|
| 假日 | 國定假日等非工作的日期。 | 「國定假日」；第二步上傳的清單叫「國定假日清單」，狀態寫「國定假日 N 天」；第五步日曆選項「國定假日」，讀回「總帳日期在國定假日」；預篩選「假日過帳」「假日核准」與第四步卡名照舊版 | action：`import.holiday`；`holidayPosting`、`holidayApproval`、`holidayActivity`。資料庫：`calendar_day.day_type` 值 `holiday`。程式：`CalendarDayType.Holiday`；範本欄 `Date_of_Holiday`、`Holiday_Name`、`IS_Holiday`。測試：`ImportCalendarHandlersTests` | 畫面統一寫「國定假日」「國定假日清單」，第八階段改程式；各層名稱照舊 | 2026-10-30 確認頁 1-0：全部照舊；畫面用語 2026-11-03 第八階段確認頁 2-2、3-1 統一（裁定 2026-11-03-10）。名稱來源：舊版 VBA IsHolidays、MergeHolidays；範本 Holiday2025TW.xlsx |
| 補班日 | 週末但要上班的日期。 | 「補班日」；第五步日曆選項「補班日」，選單下方寫「第二步上傳的補班日清單，共 N 天」 | action：`import.makeupDay`。資料庫：`day_type` 值 `makeup`。程式：`CalendarDayType.Makeup`；範本欄 `Date_of_MakeUpday`、`MakeUpDay_Desc` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：舊版 VBA IsMakeupDays、MergeMakeupDays；範本 MakeUpDay2025TW.xlsx |
| 每週休息日 | 案件設定一週哪幾天算週末。 | 第二步「每週休息日」，預設寫出六、日；第五步日曆選項「週末」，選單下方寫「第二步設定的每週休息日：六、日」；預篩選「週末過帳」「週末核准」 | action：`calendar.setNonWorkingDays`；`weekendPosting`、`weekendApproval`、`weekendActivity`。資料庫：案件檔。程式：`NonWorkingDays*`、`CalendarSetNonWorkingDaysHandler`。測試：`CalendarNonWorkingDaysHandlerTests` | 畫面改成「每週休息日」「週末」，第八階段改程式；各層名稱 `NonWorkingDays*` 照舊 | 2026-10-30 確認頁 1-0：全部照舊，當時叫「每週非工作日」；2026-11-03 第八階段確認頁 2-2 與 3-2 第 1 列改成「每週休息日」（裁定 2026-11-03-10）。名稱來源：IDEA 對話框「步驟二 非工作日之週末設定作業」（idea-tool.bas:253）；VBA MergeWeekends |
| 非營業日 | 週末或假日，而且不是補班日。 | 「非營業日」「非營業日之分錄」；第五步日曆選項「非營業日」，選單下方寫「週末或國定假日，補班日除外」；卡片「非營業日之分錄」加入一條「總帳日期 在非營業日」，2026-11-03 以前存下的子條件組讀回「非營業日」 | action：運算子 `isNonBusinessDay`；常用篩選條件卡片 `nonBusinessDay`。程式：`FilterConditionLabels.NonBusinessDayAtom`、`FILTER_COMMON_ATOM_LABELS`；舊的子條件組寫法記在 `FILTER_COMMON_PRESETS` 的 `legacyOverrides`，由 `commonRuleKinds` 認回這張卡。測試：`FilterNonBusinessDayWorkflowTests` | 照舊；`legacyOverrides`、`commonRuleKinds` 是 2026-11-03 新取的名稱，使用者同日確認（裁定 2026-11-03-31） | 2026-10-30 確認頁 1-0：全部照舊；選項名稱 2026-11-03 第八階段確認頁 2-2 改成「非營業日」（裁定 2026-11-03-10）；卡片拿掉子條件組是確認頁 1-6（裁定 2026-11-03-09）。名稱來源：畫面名稱 2026-10-29 確認頁 5-21 使用者選保留；來自 KCT 小組的條件清單；`legacyOverrides`、`commonRuleKinds` 是 Agent 自取 |
| 科目分類（分類設定） | 案件自訂、有上下層的科目分類。第一個正式版隱藏上下層，審計員新增的分類都是平的一層。 | 「分類設定」「科目分類」「分類」；內建分類顯示「內建」；第五步條件的型別標籤、讀回與入口「科目分類」；第一個正式版不顯示「進階設定」與第五步的「選取依據」 | 前端開關 `FEATURES.categoryHierarchy`，判斷 `categoryHierarchyInUse`、`categorySelectionMatters`，後端讀回用 `CategorySelectionMatters`。action：`accountCategory.save`；`categoryId`、`categoryIds`、`categoryMode`、`categorySelection`。資料庫：`account_category`（category_id、label、ordinal、semantic_role、is_builtin、revision）、`account_category_path`、`account_mapping.category_id`。程式：`AccountCategory*`、`AccountCategoryLabels`、`AccountPairCategorySelection`。測試：`AccountCategoryContractsTests`、`AccountCategoryFrontendTests`、`AccountCategorySaveHandlerTests`、`AccountCategoryDraftSaveTests`、`AccountCategorySideSelectionTests` | 照舊；前端開關與兩個判斷的名稱是 2026-11-03 新取的，使用者同日確認（裁定 2026-11-03-31） | 隱藏上下層是 2026-11-03 第八階段途中補充（裁定 2026-11-03-27），開關與判斷的名稱是 Agent 自取。2026-10-30 確認頁 1-10：照提議。理由：畫面寫「分類」，欄位已叫 category_id，只有程式另用 taxonomy。名稱來源：畫面「分類設定」2026-10-29 確認頁 5-4 使用者選保留；taxonomy 是 Agent 自取；舊版只有 STANDARDIZED_ACCOUNT_NAME（jet-legacy-notes.md 第 1 節附錄） |
| 科目配對檔的標準化分類欄 | 科目配對每一列的舊版五類分類名稱。 | 收入、應收款項、現金、預收款、其他（ui-core.js:601 起）；內建標籤 Revenue、Receivables、Cash、Receipt in advance、Others | action：匯入欄名 `Standardized Account Name`、`STANDARDIZED_ACCOUNT_NAME`。資料庫：`account_mapping.standardized_category`（只接受五個英文值）；`builtin.revenue` 等分類代號。程式：`AccountCategoryBuiltIns`。測試：`AccountMappingBlankCategoryTests`、`TaiwanSmeAccountCategoryTests` | 照舊 | 2026-10-30 確認頁 1-11：寫法 A。名稱來源：舊版科目配對檔欄 STANDARDIZED_ACCOUNT_NAME、VBA AccountStandardized（DbSchema.cls） |
| 分類用途 | 分類在規則裡扮演的角色，例如收入、現金。 | 「分類用途」「相同分類用途」；第一個正式版不顯示，新增的分類用途是「其他」（裁定 2026-11-03-27） | action：`semanticRole`。資料庫：`account_category.semantic_role`。程式：`semanticRole` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：畫面名稱 2026-10-29 確認頁 5-5 使用者選保留；識別字 Agent 自取 |

## 欄位配對與各欄位

| 概念 | 意思 | 畫面與報告 | 各層現在的名稱 | 確認的改法 | 依據 |
|:---|:---|:---|:---|:---|:---|
| 欄位配對 | 審計員確認的 GL、TB 來源欄對應。 | 「GL 欄位配對」「TB 欄位配對」「已確認配對」；底稿「自動化工具-檔案欄位資訊」 | action：`mapping.commit.gl`、`mapping.commit.tb`、`mapping.valueProfile`；`project.load` 的 `mapping.gl`、`mapping.tb`。資料庫：`field_mapping`（dataset_kind、mapping_json、mode_name、source_batch_id、committed_utc、format_version、options_json）。程式：`GlMapping*`、`TbMapping*`、`MappingCommitGlHandler`、`CommittedMapping`。測試：`MappingCommitAuthorizedPreparerStateTests`、`MappingValueErrorReportTests` | 照舊 | 2026-10-30 確認頁 1-12：照提議。理由：2026-10-29 確認頁 4-3；只拿掉 config_ 字首。名稱來源：畫面名稱：使用者 2026-10-08 指定（jet-frontend-description.md:496）；舊版 VBA FIELD_MAPPING_INFO、FieldMapperJe、FieldMapperTb |
| 前次確認的配對 | 重新匯入前最後一次確認的配對，只用來帶回草稿。 | 畫面不另命名 | action：`project.load` 的 `previousMapping`。資料庫：`field_mapping_previous`。程式：`LocalMappingStateStore.RetireCommittedMappingSql` | 照舊 | 2026-10-30 確認頁 1-13：照提議。理由：2026-10-29 確認頁 4-3。名稱來源：Agent 自取 |
| 從報告帶入配對 | 從 JET 匯出的報告、抽樣清單、工作底稿或科目配對工作檔讀回匯出時確認的配對，填成第三步的草稿。 | 第三步按鈕「從報告帶入配對」；配對區頂端「報告裡這些欄位的來源欄在目前的資料找不到，請重新選擇：」 | action：`mapping.restoreDraft`，回應 `gl`、`tb`、`unmatchedFields`。報告：六份正式報表的隱藏工作表 `JET_Metadata`，驗證報告與工作底稿另有「自動化工具-檔案欄位資訊」。程式：`MappingRestoreDraftHandler`、`OpenXmlMappingMetadataReader`、`MappingMetadataCodec.DecodeReportWorkbook`、`MappingDraftMetadata`；前端 `restore-mapping-from-report`、`restoreFromReportHtml`、`bindRestoreFromReport`、`droppedFromReport`、`Store.restoreMappingDrafts`，`droppedFields` 項目的 `fromReport`。測試：`MappingRestoreDraftHandlerTests`、`MappingMetadataMirrorTests` | 照舊；`unmatchedFields`、`restore-mapping-from-report`、`restoreFromReportHtml`、`bindRestoreFromReport`、`droppedFromReport`、`fromReport`、`DecodeReportWorkbook`、`KeepMatchedColumns`、`FitOptions` 與畫面句子是 2026-11-05 新取的，使用者同日確認（裁定 2026-11-05-11） | 畫面名稱「從報告帶入配對」出自使用者 2026-11-05 選的「照建議恢復 (Recommended)」（裁定 2026-11-05-03）。`mapping.restoreDraft`、`MappingRestoreDraftHandler` 是既有名稱；新名稱是 Agent 自取，停點一確認（裁定 2026-11-05-11） |


| 檔案欄位資訊（欄位定義） | 匯入檔每一欄的名稱、型態與長度，以及每一欄的來歷與對應的 JET 欄位。 | 底稿工作表「自動化工具-檔案欄位資訊」；配對後欄位名稱照舊版加 `_JE`、`_TB`；來源欄本來就叫 JET 輸出欄名時，原始來源欄讓名寫成「原名（來源）」，說明欄寫原名 | 資料庫：`field_info`，每欄的來歷 `field_origin`（值 `source` 原始來源欄、`renamed` 配對後改名、`generated` 系統產生、`temp` 數字轉文字前的原欄）與對應的 JET 欄位 `jet_field`（語意欄名稱如 `glDate`，攸關資料元素是它的 `fieldId`）。程式：`FieldInfoOrigin`、`FieldInfoState.Origin`、`FieldInfoState.JetField`、`FieldInfo*`、`IFieldInfoFactsPort`、`FieldInfoProjection`、`FieldInfoRow`、`FormalFieldInfoAppearance`。測試：`FieldInfoProjectorTests`、`SqlServerFieldInfoTests`、`FieldInfoProjectionTests` | 照舊；來歷與 JET 欄位兩欄是 2026-11-03 新增，名稱使用者同日確認（裁定 2026-11-03-23） | 2026-10-30 確認頁 1-14：寫法 A；來歷記成資料與「原名（來源）」是 2026-11-03 第八階段確認頁 4-1、4-2（裁定 2026-11-03-11、2026-11-03-12）。名稱來源：工作表名照舊版範本；建表註解寫對應 IDEA 的 TableDef（SqliteProjectDatabase.cs:58）；`field_origin`、`jet_field` 與 `FieldInfoOrigin` 是 Agent 自取，`temp` 取自 IDEA 的 `_Temp` |
| 攸關資料元素欄位 | 欄位配對時勾選保留、可用於條件的其他來源欄。 | 「攸關資料元素欄位」；底稿「攸關資料元素(RDE)」；報告與底稿的欄名照舊版加後綴寫成「顯示名稱_JE」，顯示名稱重複時寫「顯示名稱（來源欄名）_JE」，欄位資訊標成已配對 | action：`rdeFields`、`fieldId`、`rde.` 開頭的識別值。資料庫：`gl_rde_field`、`gl_rde_value`。程式：`GlRdeFieldMetadata`、`GlRdeStorageLimits`、`FormalReportRdeValueRenderer` 等，C# 約 213 處 RDE。測試：`FormalReportRdeProjectionTests`、`RdeContainsLookupTests`、`ResultPageRdeValuesPageLimitTests` | 照舊 | 2026-10-29 確認頁 4-8；表名部分是 2026-10-29 確認頁 4-3 的延伸，屬提議；表名 2026-10-30 第四階段確認頁 3-2 確認。底稿與報告加 `_JE` 是 2026-11-03 第八階段確認頁 4-4（裁定 2026-11-03-14）。名稱來源：舊版也叫攸關資料元素（jet-legacy-notes.md 第 1 節）；IDEA 稱「自行定義欄位」（idea-tool.bas:109） |
| 攸關資料元素條件型別 | 第五步用攸關資料元素欄位組成的舊式條件。 | 「攸關資料元素條件」；畫面只在舊情境讀回時出現 | action：條件型別值 `typed`。資料庫：存在情境的 `definition_json`。程式：`TypedFieldOperandRules`、`TypedFieldOperatorSets`、`TypedAmountBasisNames`。測試：`TypedFieldCompilationTests`、`TypedFieldFilterContractTests`、`TypedFieldProviderParityTests`、`TypedFieldRuleValidationTests`、`TypedFilterFrontendTests` | 照舊 | 2026-10-30 確認頁 1-15：寫法 A。名稱來源：契約 2026-08-14 凍結（action-contract-manifest.md:627）；Agent 自取 |
| 攸關資料元素的值型別 | 攸關資料元素欄位是文字、日期或金額。 | 「文字」「日期」「金額」 | action：`rdeFields[].valueType` 值 `text`、`date`、`amount`。資料庫：`gl_rde_field.value_type`、`gl_rde_value.value_type` 的 CHECK 值。程式：`RdeFieldValueTypeNames.Amount` | 照舊 | 2026-10-30 確認頁 1-16：照提議。理由：畫面寫金額，GL 欄位型別叫 Amount，欄位叫 amount_scaled，只有這裡寫 money。名稱來源：Agent 自取 |
| 傳票號碼 | 分錄所屬傳票的號碼。 | 「傳票號碼」；報告與底稿「傳票號碼_JE」 | action：配對鍵 `voucherNumber`；查詢回應 `voucherNumber`。資料庫：`gl.voucher_number`、`inf_sample.voucher_number`。程式：`JetFieldCatalog.GlVoucherNumber` | 照舊 | 2026-10-30 確認頁 1-17：照提議。理由：只有配對鍵用縮寫，其他層已是 documentNumber。名稱來源：舊版 VBA FieldMapperJe.DocumentNumber（legacy/VBA-legacy/FieldMapperJe.cls）；IDEA 傳票號碼_JE |
| 傳票文件項次 | 同一張傳票內分錄的項次。 | 「傳票文件項次」，短表頭「項次」；報告與底稿「傳票文件項次_JE_S」 | action：配對鍵 `lineItem`；查詢回應 `lineItem`。資料庫：`gl.line_item`。程式：`JetFieldCatalog.GlLineItem`、`LineItemNumericSortKey` | 照舊 | 2026-10-30 確認頁 1-18：照提議。理由：和舊版及其他層一致。名稱來源：舊版 VBA FieldMapperJe.LineItem；IDEA 傳票文件項次_JE_S。短表頭「項次」：2026-11-02 第四個停點確認頁 1-1，只用在第五步的傳票表，第三步配對、資料預覽與底稿照舊寫全名（裁定 2026-11-02-01） |
| 總帳日期 | 界定案件期間用的分錄日期。 | 「總帳日期」；報告與底稿「總帳日期_JE」 | action：配對鍵 `glDate`；查詢回應 `glDate`。資料庫：`gl.gl_date`、索引 `ix_gl_gl_date`。程式：`JetFieldCatalog.GlDate`。測試：`BlankGlDateDetectionTests`、`BlankGlDateFrontendTests` | 照舊 | 2026-10-29 確認頁 4-4。名稱來源：畫面名稱：使用者 2026-10-08 裁定；舊版 VBA FieldMapperJe.PostDate；IDEA 把 EFFECTDATE 對到總帳日期_JE（jet-legacy-notes.md 第 2 節） |
| 傳票核准日 | 分錄核准的日期。 | 「傳票核准日」；報告與底稿「傳票核准日_JE」 | action：配對鍵 `approvalDate`；查詢回應 `approvalDate`。資料庫：`gl.approval_date`。程式：`JetFieldCatalog.GlApprovalDate` | 照舊 | 2026-10-29 確認頁 4-4。名稱來源：舊版 VBA FieldMapperJe.ApprovalDate；IDEA 把 POST_DATE 對到傳票核准日_JE |
| 會計科目編號 | 本案會計科目的識別欄。 | 「會計科目編號」，短表頭「科目編號」；報告與底稿「會計科目編號_JE」「會計科目編號_TB」 | action：GL 與 TB 配對鍵 `accountNumber`；查詢回應 `accountNumber`。資料庫：`gl.account_number`、`tb.account_number`、`account_mapping.account_number`。程式：`JetFieldCatalog.GlAccountNumber`、`TbAccountNumber`。測試：`RareAccountBlankCodeTests` | 照舊 | 2026-10-30 確認頁 1-19：寫法 A。名稱來源：舊版 VBA FieldMapperJe.AccountNumber、ACCOUNT_MAPPING.AccountNumber；科目配對檔欄 GL_NUMBER；Agent 改成 account_code |
| 會計科目名稱 | 會計科目的名稱。 | 「會計科目名稱」；報告與底稿「會計科目名稱_JE」「會計科目名稱_TB」 | action：配對鍵 `accountName`；查詢回應 `accountName`。資料庫：`account_name`。程式：`JetFieldCatalog.GlAccountName`、`TbAccountName` | 照舊 | 2026-10-30 確認頁 1-20：照提議。理由：和舊版及其他層一致。名稱來源：舊版 VBA FieldMapperJe.AccountName |
| 傳票摘要 | 分錄的摘要文字。 | 「傳票摘要」，短表頭「摘要」；報告與底稿「傳票摘要_JE」 | action：配對鍵 `description`；查詢回應 `description`。資料庫：`gl.description`。程式：`JetFieldCatalog.GlDescription` | 照舊 | 2026-10-30 確認頁 1-21：照提議。理由：配對鍵與多數回應已是 description；同一欄在回應裡有兩種寫法。名稱來源：舊版 VBA FieldMapperJe.EntryDescription、NULL_DESCRIPTION_RECORDS（DbSchema.cls） |
| 分錄來源模組 | 分錄由哪個系統模組產生。 | 「分錄來源模組」；底稿「分錄來源模組_JE」 | action：配對鍵 `sourceModule`。資料庫：`gl.source_module`。程式：`JetFieldCatalog.GlSourceModule` | 照舊 | 2026-10-30 確認頁 1-22：照提議。理由：和舊版及資料庫一致。名稱來源：舊版 VBA FieldMapperJe.SourceModule |
| 傳票建立人員 | 建立分錄的人員欄。 | 「傳票建立人員」；第四步、第五步與流程總覽的表頭也寫「傳票建立人員」，不寫「編製人員」；底稿「傳票建立人員_JE」 | action：配對鍵 `createdBy`；查詢回應 `createdBy`；`entityFrequency.field` 值 `createdBy`。資料庫：`gl.created_by`。程式：`JetFieldCatalog.GlCreatedBy` | 照舊 | 2026-10-30 確認頁 1-23：照提議。理由：和舊版及其他層一致。名稱來源：舊版 VBA FieldMapperJe.CreatedBy；IDEA CREATEDUSER 對到傳票建立人員_JE |
| 傳票核准人員 | 核准分錄的人員欄。 | 「傳票核准人員」；底稿「傳票核准人員_JE」 | action：配對鍵 `approvedBy`；查詢回應 `approvedBy`。資料庫：`gl.approved_by`。程式：`JetFieldCatalog.GlApprovedBy` | 照舊 | 2026-10-30 確認頁 1-24：照提議。理由：和舊版及其他層一致。名稱來源：舊版 VBA FieldMapperJe.ApprovedBy |
| 人工/自動分錄 | 分錄是人工還是自動產生。 | 「人工/自動分錄」「人工/自動」；第五步的入口、子條件組的類型選單、條件標題與型別標籤寫「人工/自動分錄」，讀回寫「人工分錄」「自動分錄」；底稿「人工傳票否_JE_S」 | action：配對鍵 `isManual`；條件 `manualAuto`、`isManual`；`manualAutoPolicy`。資料庫：`gl.is_manual`。程式：`JetFieldCatalog.GlManual`（值 `isManual`）、`GlManualAutoPolicy` | 照舊 | 2026-10-30 確認頁 1-25：照提議。理由：配對鍵是唯一沒有 is 字首的寫法。第五步的名稱 2026-11-03 第八階段第一個停點 1-9 統一（裁定 2026-11-03-22）。名稱來源：舊版 VBA FieldMapperJe.IsManual；IDEA 欄位「人工/自動分錄」（idea-tool.bas:94） |
| 金額 | 一筆分錄的單欄金額，借方為正、貸方為負。 | 「金額」；底稿「傳票金額_JE」 | action：配對鍵與回應 `amount`。資料庫：`amount_scaled`。程式：`JetFieldCatalog.GlAmount`、`MoneyScaling`；哪些配對鍵是金額寫在 `JetFieldCatalog.AmountMappingKeys`，前端 `GL_FIELDS`、`TB_FIELDS` 用 `amount` 屬性鏡射 | 照舊；`AmountMappingKeys` 與前端的 `amount` 屬性是 2026-11-05 新取的，使用者同日確認（裁定 2026-11-05-11） | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：畫面用語：使用者 2026-10-13 裁定（jet-frontend-description.md:493）；舊版 VBA FieldMapperJe.Amount |


| 借方金額與貸方金額（GL） | GL 分別設定借貸金額時的兩個來源欄。 | 「借方金額」「貸方金額」 | action：配對鍵 `debitAmount`、`creditAmount`。資料庫：`debit_amount_scaled`、`credit_amount_scaled`。程式：`JetFieldCatalog.GlDebitAmount`、`GlCreditAmount` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：舊版 VBA FieldMapperJe.DebitAmount、CreditAmount |
| 借貸別 | 分錄是借方還是貸方，以及判斷用的代碼。 | 「借貸別」「借方代碼」「貸方代碼」 | action：配對鍵 `drCr`、`drCrDebitCode`、`drCrCreditCode`；回應與條件 `drCr`、`drCrOnly`。資料庫：`gl.dr_cr`（DEBIT、CREDIT）。程式：`JetFieldCatalog.GlDrCr`、`GlDrCrDebitCode`、`GlDrCrCreditCode`。測試：`DrCrCodeProjectionTests`、`DrCrCodeMappingWorkflowTests` | 照舊 | 2026-10-30 確認頁 1-26：照提議。理由：同一概念在配對鍵寫 dc、其他層寫 drCr。名稱來源：舊版 VBA FieldMapperJe.DrCr；IDEA DEBIT_CREDIT_JE_T 存 DEBIT、CREDIT |
| 過帳狀態 | 分錄是否已過帳，用來決定是否納入測試。 | 「過帳狀態」「過帳狀態不符」 | action：配對鍵 `postingStatus`。資料庫：`gl.posting_status`。程式：`JetFieldCatalog.GlPostingStatus`、`GlPostingStatusPolicy` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：IDEA「總帳傳票資料過帳否參數設定」（idea-tool.bas:510）；識別字 Agent 自取 |
| 過帳碼 | 配對了過帳狀態時，欄位資訊與底稿多出的一欄，值照過帳狀態的來源欄。 | 底稿「過帳碼_JE」；欄位資訊的說明寫「【來源欄】過帳判斷值「值」」 | 程式：`JetFieldCatalog` 裡過帳狀態欄的 `LegacyFieldName`；`jet_field` 值 `postingStatus` | 照舊 | 2026-11-03 第八階段確認頁 4-5（裁定 2026-11-03-15）。名稱來源：IDEA 過帳碼_JE（idea-tool.bas:12697） |
| 傳票核准日的配對方式 | 傳票核准日由來源欄提供，或取總帳日期。 | 「—」「由來源欄提供」「與總帳日期相同」；錯誤訊息寫「—」（沒有核准日） | action：`approvalDateMode` 值 `unmapped`、`mapped`、`sameAsGlDate`。資料庫：存在 `field_mapping.options_json`。程式：`ApprovalDateModeNames.SameAsGlDate`、`JetFieldDerivedSource.SameAsGlDate` | 照舊 | 2026-10-30 確認頁 1-27：照提議。理由：跟著已確認的 postDate 改 glDate。名稱來源：IDEA 勾選框「傳票核准日同總帳日」（idea-tool.bas:121）；識別值 Agent 自取 |
| GL 金額方式 | GL 金額從哪幾個來源欄算出。 | 「分別設定借貸金額」「單一帶號金額欄」「金額與借貸別」 | action：`dual`、`signed`、`side`、`flag`。資料庫：`field_mapping.mode_name`。程式：`GlAmountMode`、`GlAmountModeNames`。測試：`AccountingParenthesesAmountTests` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：畫面名稱：使用者 2026-10-21 定下不用加號連接（ui-core.js:137 註解）；IDEA「總帳傳票金額判斷邏輯設定」（idea-tool.bas:486） |
| 年度變動金額 | TB 每個科目的本期變動金額。 | 「年度變動金額」，第四步完整性測試的說明也用這個名稱；底稿「試算表變動金額_TB」；舊版底稿「試算表本期變動金額」 | action：TB 配對鍵 `changeAmount`，和語意欄同名。資料庫：`tb.change_amount_scaled`。程式：`JetFieldCatalog.TbChangeAmount` | 照舊 | 2026-10-30 確認頁 1-28：照提議。理由：TB 的 amount 和 GL 的 amount 同名不同義。名稱來源：舊版 VBA FieldMapperTb.ChangeAmount |
| 借方金額與貸方金額（TB） | TB 用借貸兩欄算變動金額時的兩個來源欄。 | 「借方金額」「貸方金額」 | action：TB 配對鍵 `debitAmount`、`creditAmount`。程式：`JetFieldCatalog.TbDebitAmount`、`TbCreditAmount` | 照舊 | 2026-10-30 確認頁 1-29：照提議。理由：GL 同一概念已寫 debitAmount。名稱來源：舊版 VBA FieldMapperTb.DebitAmount、CreditAmount |
| 期初與期末餘額（TB） | TB 用期初期末算變動金額時的來源欄。 | 「期初餘額」「期末餘額」「期初借方」「期初貸方」「期末借方」「期末貸方」 | action：`openingBalance`、`closingBalance`、`openingDebitBalance`、`openingCreditBalance`、`closingDebitBalance`、`closingCreditBalance`。程式：`JetFieldCatalog.TbOpening*`、`TbClosing*` | 照舊 | 2026-10-30 確認頁 1-30：照提議。理由：照舊版補齊。名稱來源：舊版 VBA FieldMapperTb.OpeningBalance、ClosingBalance、OpeningDebitBalance、OpeningCreditBalance、ClosingDebitBalance、ClosingCreditBalance |
| TB 金額方式 | TB 變動金額從哪幾個來源欄算出。 | 「設定借方與貸方金額」「設定年度變動金額」「設定期初與期末金額」「設定借方貸方之期初期末金額」 | action：`debitCredit`、`direct`、`openClose`、`openCloseBySide`。資料庫：`field_mapping.mode_name`。程式：`TbChangeMode`、`TbChangeModeNames` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：畫面名稱沿用舊版按鈕（ui-core.js:137 註解）；IDEA「試算表金額邏輯處理」（idea-tool.bas:465） |
| 往下填補空白 | 確認配對時把來源欄的空白補成上一列的值。 | 「往下填補空白」 | action：`fillDownFields`、`fillDownCount`、`fillDown`。資料庫：存在配對設定。程式：`GlStagingFillDown`。測試：`GlStagingFillDownTests` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：畫面名稱 2026-10-29 確認頁 5-2 使用者選保留 |
| 欄名在第幾列 | 來源檔的欄名從第幾列開始。 | 「欄名在第 N 列」 | action：`headerRow`、`sources[].headerRow`。資料庫：`import_batch_source.header_row` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：畫面名稱 2026-10-29 確認頁 5-1 使用者選保留 |

## 案件與查核期間

| 概念 | 意思 | 畫面與報告 | 各層現在的名稱 | 確認的改法 | 依據 |
|:---|:---|:---|:---|:---|:---|
| 案件 | 一次分錄測試的工作單位。 | 「案件」「建立案件」「案件清單」 | action：`project.*` 共 11 個 action；`projectId`；案件檔 `project.json`。資料庫：SQL Server `dbo.project_registry`、`dbo.project_access`、`dbo.project_lock`。程式：`Project*` 為主，另有 `ProjectCreate*`、`ProjectState`、`AuditProjectSnapshot`、`IProjectCreateBackendPort`。測試：`ProjectCreationValidationTests`、`ProjectMetadataUpdateTests`、`UnreadableProjectRecoveryTests` | 照舊 | 2026-10-30 確認頁 1-31：照提議。理由：action 與多數型別用 project，Case 是少數。名稱來源：舊版 VBA ServiceProject.cls、NewProject、SelectProject |
| 案件名稱 | 審計員替案件取的名稱。 | 「案件名稱」 | action：`projectName`；錯誤欄位 `error.field = "projectName"`。資料庫：`project.json` 的 `projectName`。程式：`ProjectNameRules` | 照舊 | 2026-10-30 確認頁 1-32：照提議。理由：同一份案件檔其他欄寫 project。名稱來源：Agent 自取 |
| 案件編號 | 審計員填的案件編號。 | 「案件編號」 | action：`projectCode`。資料庫：`project.json`。程式：`projectCode` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：畫面名稱 2026-10-29 確認頁 5-7 使用者選保留 |
| 客戶名稱 | 受查公司的名稱。 | 「客戶名稱」 | action：`clientName`。資料庫：`project.json` 的 `clientName`。程式：`clientName` | 照舊 | 2026-10-30 確認頁 1-33：照提議。理由：畫面寫客戶，舊版識別字也是 Client。名稱來源：舊版 VBA EngagementOverview 欄位 Client「客戶」（DbSchema.cls） |
| 查核期間 | 案件的查核起訖日。 | 「查核期間」「查核起始日」「查核截止日」 | action：`periodStart`、`periodEnd`；篩選範圍值 `auditPeriod`。資料庫：`project.json`。程式：`ProjectDocumentPeriodIntegrity`、`GlPopulationScope.AuditPeriod`。測試：`SqlServerProjectPeriodValidationTests` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：舊版 VBA PeriodStart、PeriodEnd「資料期間開始日」「資料期間結束日」 |
| 期末財報準備日 | 期末財務報表開始準備的日期。 | 「期末財報準備日」 | action：`prepStartDate`。資料庫：`project.json` 的 `prepStartDate`。程式：`JetRulePrerequisiteKind.PrepStartDate`。測試：`PrepStartDateReadbackTests` | 照舊 | 2026-10-30 確認頁 1-34：照提議。理由：last period start 讀起來像上一期起日，和畫面意思不同；舊版有現成的 PrepStartDate。名稱來源：畫面名稱 2026-10-29 確認頁 5-8 使用者選保留；舊版 VBA PrepStartDate「財報準備期間開始日」；IDEA「期末財務報表準備期間-開始日」（idea-tool.bas:391） |
| 資料儲存方式 | 案件用哪一種資料庫。 | 「資料儲存方式」 | action：`databaseProvider`。資料庫：`project.json`。程式：`databaseProvider` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：畫面名稱 2026-10-29 確認頁 5-6 使用者選保留 |
| 民國年解析 | 日期條件是否接受民國年。 | 民國年選項 | action：`rocDateEnabled`。資料庫：`project.json`。程式：`RocDateEnabled`。測試：`RocCalendarCultureTests` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：Agent 自取 |

## 資料驗證

| 概念 | 意思 | 畫面與報告 | 各層現在的名稱 | 確認的改法 | 依據 |
|:---|:---|:---|:---|:---|:---|
| 資料驗證 | 第四步的完整性、借貸不平、空白資料與抽樣檢查。 | 「資料驗證與測試」；報告「資料驗證報告」 | action：`validate.run`。資料庫：`validation_result`。程式：`ResultKind.Validation`、`ValidationProcedures`、`ValidateRunHandler`、`ValidationReport*`。測試：`ValidationReportPublicationTests` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：舊版 VBA ServiceValidation.cls；範本 ValidationReport.xlsx |
| 驗證與預篩選的執行結果 | 每次資料驗證或預篩選的摘要紀錄。 | 畫面不另命名 | action：`project.load` 的 `validationResult`、`prescreenResult`；`resultRef.resultId`；`validationResultId`、`prescreenResultId`。資料庫：`validation_result`、`prescreen_result`（result_id、generated_utc、summary_json）。程式：`ResultRecord`、`IResultStore`、`LocalResultStore`；指執行結果的名稱用 result，例如 `CompletenessEligibility.HasCurrentValidationResult`，同一份目前結果裡的兩筆紀錄是 `ValidationRecord`、`PrescreenRecord`；指「執行這個動作」的照舊用 run，例如 `validate.run`、`ValidationRunResult`；前端 `state.results`、`Store.setResult` | 照舊 | 2026-10-30 確認頁 1-35：我另外取名；名稱 2026-10-30 第四階段確認頁 3-1 確認；指執行結果的 run 改成 result 是 2026-11-01 第三個停點 1-4（裁定 2026-11-01-01）。名稱來源：`JetSchemaCatalog` 對照舊版 VALIDATION_OVERVIEW；表名 Agent 自取 |
| 完整性測試 | GL 與 TB 的完整性檢查，含兩項。 | 「完整性測試」；底稿「step1 完整性測試」「step1-3 完整性測試之差異說明」 | action：`validate.run` 回應 `completenessTest`；`completenessTest.eligibility`。資料庫：summary_json 的 `completenessTest`。程式：`CompletenessEligibility*`、`CompletenessPopulationTotals` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：舊版 VBA CheckCompleteness、COMPLETENESS_DETAIL；IDEA「完整性測試」 |
| 存入筆數與合計核對 | 完整性第一項：存下的分錄和確認配對時算出的筆數與合計是否一致。 | 完整性測試的狀態「存入筆數與合計不一致」；明細表的欄名「確認配對時算出」「存下後重新計算」；差異提醒的開頭「存入筆數與合計核對未通過」 | action：`completenessTest.storedTotals`、`storedRowCountMatch`、`storedAmountMatch` 等。資料庫：`gl_control_total`（pbc_row_count、stored_row_count、stored_debit_scaled、stored_credit_scaled、gl_row_count 等）。程式：`CompletenessStoredTotals`、`StoredRowCountMatch`、`PbcRowCount` 等 | 照舊 | 2026-10-29 確認頁 4-1、5-11，2026-10-31 第七步照改；表名部分屬提議；表名與欄名 2026-10-30 第四階段確認頁 3-3 確認。名稱來源：第一項名稱：使用者 2026-10-29 確認（2026-10-29 確認頁 4-1、5-11）；舊版沒有這一項。只給舊案件用的狀態「存入筆數與合計無法核對」依裁定 2026-11-02-06 拿掉 |
| GL 與 TB 逐科目比對 | 完整性第二項：逐科目比較 TB 變動金額與 GL 借貸合計。 | 無法執行時的原因句與差異提醒都寫「GL 與 TB 逐科目比對」；報告與底稿照舊版範本的文字不改 | action：`accountComparisonDifferenceAccountCount`、`accountComparisonApplicable`、`completenessEligibility.accountComparison*`；查詢 `query.completenessDiffPage`。程式：`AccountComparisonProcedure`、`AccountComparisonPlan`、`AccountComparisonFacts`、`AccountComparisonRequest` | 照舊 | 2026-10-29 確認頁 4-1，2026-10-31 第七步照改。名稱來源：名稱：使用者 2026-10-29 確認沿用（2026-10-29 確認頁 4-1）；舊版 COMPLETENESS_CALCULATED、COMPLETENESS_DIFF |
| 借貸不平測試 | 找出借貸合計不為零的傳票。 | 「借貸不平測試」；底稿「step1-1 借貸不平測試」 | action：`validate.run` 回應 `voucherBalanceTest`；`query.voucherBalancePage`；規則代號 `voucher_balance_test`。程式：`UnbalancedVoucher`、`IUnbalancedVoucherPageRepository`、`UnbalancedVoucherDateRow`、`IVoucherBalancePageRepository`。測試：`VoucherBalancePageTests`、`UnbalancedVoucherDateRowsTests` | 照舊 | 2026-10-30 確認頁 1-36：照提議。理由：同一測試在型別裡混用 Document、Voucher、GlEntry 三種字根。名稱來源：舊版 VBA CheckDocumentBalance、DOCUMENT_BALANCE_DETAIL；IDEA「借貸不平測試」 |
| 空白資料檢查（資料缺漏輔助檢視） | 列出空白傳票號碼、空白科目編號與核准日不在期間的分錄。 | 「資料缺漏輔助檢視」「空白傳票號碼」「空白科目編號」；規則目錄的顯示名也是「資料缺漏輔助檢視」 | action：`validate.run` 回應 `blankRecordsTest`，各類筆數 `blankAccountNumberCount`、`blankVoucherNumberCount`、`blankDescriptionCount`，明細列 `blankRows`，每列的 `issues` 值是 `accountNumber`、`voucherNumber`、`description`、`approvalDate`；`query.blankRecordsPage`，分類值 `blankAccountNumber`、`blankVoucherNumber`、`blankDescription`、`approvalDateNotInPeriod`；規則代號 `blank_records_test`。資料庫：存在 `validation_result.summary_json`。程式：`BlankRecordCategory`（`BlankAccountNumber`、`BlankVoucherNumber`、`BlankDescription`、`ApprovalDateNotInPeriod`）、`BlankRecordRow`、`BlankRecordsCategoryPredicate`、`ValidationReportDetailKind.BlankAccountNumber` 等、`BlankAccountNumberCount`、`BlankVoucherNumberCount`、`BlankDescriptionCount`。測試：`BlankRecordsPageTests`、`UserFeedbackBlankDetailsTests` | 照舊 | 2026-10-30 確認頁 1-37：寫法 B；規則目錄的顯示名照確認頁對寫法 B 的建議一併改。筆數鍵、分類值與明細列的名稱，2026-11-03 使用者在視窗選項選「這份計畫內改完」，照已確認的字根把 null 改成 blank、Document 改成 VoucherNumber，案件資料庫一併升版（裁定 2026-11-03-02）。名稱來源：畫面名稱 2026-10-29 確認頁 5-9、5-10 使用者選保留；舊版 VBA CheckNullRecords、NULL_ACCOUNT_RECORDS、NULL_DOCUMENT_RECORDS；舊版報告「無傳票號碼之分錄筆數」 |
| 總帳日期空白（原「來源品質」標題） | 總帳日期空白、不能界定案件期間的來源列。 | 第四步標題「總帳日期空白」 | action：`sourceQuality` 的分類值 `blankGlDate`；`query.sourceQualityPage`。資料庫：summary_json。程式：`SourceQualityFindingRow`、`SourceQualityPageReader`。測試：`BlankGlDateDetectionTests`、`BlankGlDateFrontendTests` | 照舊 | 2026-10-29 確認頁 4-5。名稱來源：標題：使用者 2026-10-29 選定（2026-10-29 確認頁 4-5）；舊版沒有這一類 |
| Source_Quality 工作表 | 資料驗證報告裡列總帳日期空白來源列的工作表。 | 工作表名 `Source_Quality`，七個英文欄名 | action：`export.validationReports`。程式：`LegacyReportWriter.Validation` | 照舊 | 2026-10-29 確認頁 5-30。名稱來源：使用者 2026-10-04 裁定維持英文；2026-10-29 確認頁 5-30 |
| 核准日不在期間 | 傳票核准日不在查核期間內的分錄。 | 「核准日不在期間」 | action：`blankRecordsTest.approvalDateNotInPeriodCount`、`approvalDateNotInPeriodNaReason`。資料庫：summary_json。程式：同名欄位 | 照舊 | 2026-10-30 確認頁 1-38：照提議。理由：現名沒寫出是哪一種日期；舊版有 NotInPeriod。名稱來源：舊版 VBA NotInPeriodCount「傳票核准日不在會計期間內筆數」；IDEA `#NotinPeriod-ApprovalDate`（idea-tool.bas:1742） |
| 同一傳票號碼出現在不同總帳日期 | 同一非空白傳票號碼出現在兩個以上總帳日期的提醒。 | 「同一傳票號碼出現在不同總帳日期」 | action：`voucherNumberAcrossGlDates: { voucherNumberCount, entryCount }`。資料庫：summary_json。程式：`VoucherNumberAcrossGlDatesCounts`。測試：`VoucherNumberAcrossGlDatesTests` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊；鍵名 2026-11-01 第三個停點 1-2 改用 voucher（裁定 2026-11-01-01）。名稱來源：Agent 自取 |
| 金額分布 | 流程總覽的金額分布圖。 | 流程總覽圖表 | action：`validate.run` 回應 `amountDistribution`。資料庫：summary_json。程式：`ValidationAmountDistribution*` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：Agent 自取 |
| 資料可靠性測試 | 抽樣核對攸關資料元素是否可靠。 | 「資料可靠性測試」，規則目錄的顯示名相同；報告工作表「INF Testing 可靠性測試」 | action：`validate.run` 回應 `infSamplingTest`；`query.infSamplePage`。資料庫：`inf_sample`。程式：`InfSampling`、`InfSamplingPrf`、`InfSampleRow`。測試：`InfSamplingPrfTests`、`InfSamplePageTests`、`InfDirectTemplatePackageTests` | 照舊 | 2026-10-29 確認頁 4-2；表名部分屬提議；表名 2026-10-30 第四階段確認頁 3-4 確認。名稱來源：IDEA「INF Testing 可靠性測試」（idea-tool.bas:6829）；VBA CheckINF、INF_SAMPLE_DETAIL |
| 資料可靠性抽樣清單 | 資料可靠性測試輸出的報告檔。 | 「資料可靠性抽樣清單」；檔案 INF Report；工作表「INF Testing 可靠性測試」「可靠性樣本_所有欄位」 | action：報告種類 `infReport`；由 `export.validationReports` 產生。程式：`IInfReportWriter`、`InfReportContext`、`ReportTemplatePackage.Inf`。測試：`InfDirectTemplatePackageTests` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：畫面名稱 2026-10-29 確認頁 5-28 使用者選保留；工作表名與範本 INFReport.xlsx 是舊版 |
| 結果過期 | 上游資料改了以後，已保存的驗證、預篩選或篩選結果標成過期。 | 流程總覽「目前版本需重新執行」 | action：`staleState`。資料庫：`result_stale_state`（validation_stale、prescreen_stale、filter_stale）。程式：`AuditResultStaleState`、`IResultStaleStateStore`、`ResultStaleStateSql` | 照舊 | 2026-10-30 確認頁 1-39：照提議。理由：2026-10-29 確認頁 4-3；只拿掉 config_ 字首。名稱來源：Agent 自取 |
| 清除下游結果 | 上游修改時清掉的結果範圍。 | 「會清除後面步驟的設定」 | action：`invalidatedResults`。程式：`Invalidation`、`AuditDependencyPolicy`。測試：`UpstreamMutationClearsFilterScenariosTests` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：使用者 2026-10-07 裁定「上游修改時清除下游」 |

## 預篩選

| 概念 | 意思 | 畫面與報告 | 各層現在的名稱 | 確認的改法 | 依據 |
|:---|:---|:---|:---|:---|:---|
| 預篩選 | 第四步的標準化初步篩選程序。 | 「預篩選」；報告「預篩選報告」 | action：`prescreen.run`；條件型別 `prescreen`。資料庫：`prescreen_result`。程式：`ResultKind.Prescreen`、`Prescreen*`、`PrescreenRuleDefinitions`。測試：`PrescreenSummaryCountTests` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：IDEA「步驟三 預先篩選程序」、Pre-screening Report（idea-tool.bas:124、:7836） |
| 期末財報準備日起核准之分錄 | 預篩選 R1：傳票核准日在期末財報準備日當日或之後。 | 「期末財報準備日起核准之分錄」；條件列「財報準備日起核准」 | action：`prepPeriodApproval`；代號 `prep_period_approval`。程式：`PrescreenRuleKeys.PrepPeriodApproval` | 照舊 | 2026-10-30 確認頁 1-40：照提議。理由：post period 讀起來像期後；和期末財報準備日一列同用 prep 字根。名稱來源：畫面名稱 2026-10-29 確認頁 5-12 使用者選保留；IDEA「於期末財務報表準備期間核准之分錄」（idea-tool.bas:125） |
| 分錄摘要出現特定描述 | 預篩選 R2：摘要含預設關鍵字。 | 「分錄摘要出現特定描述」；條件列「摘要特定描述」 | action：`suspiciousKeywords`；代號 `suspicious_keywords`。程式：`SuspiciousKeywordDefaults` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：IDEA「分錄摘要出現特定描述」（idea-tool.bas:126）；識別字 Agent 自取 |
| 未預期出現之特定借貸組合 | 預篩選 R3：貸方為收入但同張傳票沒有一般對方科目借方。 | 「未預期出現之特定借貸組合」；條件列與第五步預篩選分頁的名稱都是「未預期借貸組合」 | action：`unexpectedAccountPair`；代號 `unexpected_account_pair`。程式：`PrescreenRuleKeys.UnexpectedAccountPair`；單獨成情境時列整張命中傳票標在 `PrescreenRuleDefinition.ListsWholeHitVouchersWhenAlone`。測試：`UnexpectedPairPrerequisiteTests` | 照舊；`ListsWholeHitVouchersWhenAlone` 是 2026-11-05 新取的，使用者同日確認（裁定 2026-11-05-11） | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：IDEA「未預期出現之特定借貸組合」（idea-tool.bas:127） |


| 分錄金額中有連續 0 的尾數 | 預篩選 R4：金額整數部分尾數連續 6 個 0。第五步的常用查法「尾數連續多個 0」是同一條述詞的欄位條件，個數可改成 1 到 12。 | 「分錄金額中有連續 0 的尾數」；條件列「金額尾數連續 6 個 0」；第五步常用查法「尾數連續多個 0」，比較方式「尾數連續」「尾數沒有連續」，讀回「金額 尾數連續「6」個 0」 | action：`trailingZeros`；`zerosThreshold`；代號 `trailing_zeros`；第五步的比較方式 `endsWithZeros`、`notEndsWithZeros`。程式：`TrailingZeroThreshold`、`FieldValueConditions.IsZeroRun`、`TryParseZeroRun`；錯誤碼 `field_value_zero_run_invalid`、`field_value_zero_run_extra`。測試：`TrailingZeroLabelReadbackTests`、`TrailingZeroRunConditionsTests` | 照舊；第五步比較方式的程式名稱、兩個方法、錯誤碼與測試名稱是 2026-11-03 新取的，使用者同日確認（裁定 2026-11-03-31）；畫面上的比較方式「尾數連續」「尾數沒有連續」與讀回寫法，使用者同日確認照現在（裁定 2026-11-03-33） | 2026-10-29 確認頁 5-13。第五步改成一條條件是 2026-11-03 第八階段確認頁 1-4（裁定 2026-11-03-07），新名稱是 Agent 自取。名稱來源：IDEA「分錄金額中有連續0的尾數」（idea-tool.bas:128） |
| 依分錄編製者彙總 | 預篩選 R5：依編製人員彙總分錄筆數與金額。 | 「依分錄編製者彙總」；底稿「step1-2 分錄編製人員說明」 | action：`preparerSummary`、`preparerSummary.preparers`、`totalPreparerCount`；代號 `preparer_summary`。程式：`PreparerSummaryRow`、`PreparerSummaryDisplay`、`IPreparerSummaryExportRepository`；同族另有 `PreparerConcentration`。測試：`PreparerSummaryExportTests`、`WorkingPaperPreparerRowTests`、`PreparerCountTests` | 照舊 | 2026-10-30 確認頁 1-41：照提議。理由：同一個人員角色在程式裡有 creator 與 preparer 兩詞；舊版 VBA 用 Preparer。名稱來源：IDEA「依分錄編製者彙總分錄」（idea-tool.bas:129）；VBA ShowPreparerSummary（ServiceExport.cls:224） |
| 較少使用之科目 | 預篩選 R6：依筆數排序的科目彙總。第五步欄位條件另有「較少使用的科目」，加入時預設挑出分錄 11 筆以下的科目，筆數可以改。 | 「較少使用之科目」；第五步欄位條件裡會計科目編號的常用查法「較少使用的科目」 | action：`rareAccounts`、`rareAccounts.accounts`、`rareAccounts.distinctAccountCount`；代號 `rare_accounts`。第五步的條件型別 `entityFrequency`，`field` 值 `accountNumber`。程式：`JetRuleRequirements.RareAccounts`、`AccountUsageRow`、`PRESCREEN_DEFAULTS.accountMaxEntries`。測試：`RareAccountBlankCodeTests` | 照舊 | 2026-10-29 確認頁 4-9。第四步原本另外挑出 11 筆以下的科目分錄，2026-10-29 確認頁 2-7 照舊版拿掉。名稱來源：IDEA「較少使用之科目」（idea-tool.bas:130） |
| 編製分錄較少的人員 | 第五步欄位條件裡傳票建立人員的常用查法：加入時預設挑出分錄 11 筆以下的人員，筆數可以改。 | 「編製分錄較少的人員」 | action：條件型別 `entityFrequency`，`field` 值 `createdBy`。程式：`PRESCREEN_DEFAULTS.preparerMaxEntries`。測試：`RarePreparersAndAccounts_ElevenEntriesHitTwelveDoNot` | 照舊 | 2026-10-29 確認頁 4-9。第四步原本有同名的預篩選規則，2026-10-29 確認頁 2-7 照舊版拿掉。名稱來源：Agent 自取 |
| 空白摘要 | 預篩選 R7：摘要欄位空白的分錄。 | 「空白摘要」 | action：`blankDescription`；代號 `blank_description`；常用篩選條件卡片 `blankDescription`。程式：`PrescreenRuleKeys.BlankDescription` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：IDEA 預篩選報告併入空白摘要結果（idea-tool.bas:132）；jet-frontend-description.md:509 |
| 週末過帳與週末核准 | 總帳日期或傳票核准日落在週末。 | 「週末過帳／核准之分錄」；卡片狀態「總帳日期 N 筆，傳票核准日 M 筆」，明細按鈕「依總帳日期」「依傳票核准日」；條件列「週末過帳」「週末核准」 | action：`weekendPosting`、`weekendApproval`、`weekendActivity`。程式：`PrescreenRuleKeys.WeekendPosting`、`WeekendApproval` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：畫面名稱 2026-10-29 確認頁 5-14 使用者選保留，確認頁把畫面上的斜線寫成「或」；識別字 Agent 自取 |
| 假日過帳與假日核准 | 總帳日期或傳票核准日落在假日。 | 「假日過帳／核准之分錄」；卡片狀態「總帳日期 N 筆，傳票核准日 M 筆」，明細按鈕「依總帳日期」「依傳票核准日」，說明寫「依第二步上傳的國定假日清單」；條件列「假日過帳」「假日核准」 | action：`holidayPosting`、`holidayApproval`、`holidayActivity`。程式：`PrescreenRuleKeys.HolidayPosting`、`HolidayApproval` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：畫面名稱 2026-10-29 確認頁 5-14 使用者選保留，確認頁把畫面上的斜線寫成「或」 |
| 非授權編製人員之分錄 | 傳票建立人員不在授權編製人員清單裡的分錄。 | 「非授權編製人員之分錄」；條件列「非授權編製人員」 | action：`nonAuthorizedPreparer`；代號 `non_authorized_preparer`。程式：`PrescreenRuleKeys.NonAuthorizedPreparer` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：畫面名稱 2026-10-29 確認頁 5-15 使用者選保留 |
| 集中度檢視 | 流程總覽的人員與科目集中度圖表。 | 「集中度檢視」 | action：`prescreen.run` 回應 `concentration`。資料庫：summary_json。程式：`PrescreenConcentration`、`PreparerConcentration` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：畫面名稱 2026-10-29 確認頁 5-27 使用者選保留 |

## 條件篩選

| 概念 | 意思 | 畫面與報告 | 各層現在的名稱 | 確認的改法 | 依據 |
|:---|:---|:---|:---|:---|:---|
| 進階條件篩選 | 第五步依條件挑出要測試的分錄。 | 「進階條件篩選」；報告「條件篩選報告」 | action：`filter.preview`、`filter.scenario.save`、`filter.scenario.remove`、`filter.scenario.recheck`、`filter.commit`。程式：`Filter*`。測試：`Batch7Filter*Tests` 等 | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：IDEA「步驟四 進階篩選作業」（idea-tool.bas:139）；VBA ServiceFilter.cls |
| 篩選情境 | 審計員儲存的一組篩選條件，附名稱與動機。 | 「篩選情境」「已儲存情境」「另存為新情境」 | action：`filterScenarios`、`savedScenarios`、`filterScenarioCheck`、`scenario_position`。資料庫：`filter_scenario`（scenario_id、position、name、rationale、definition_json、saved_utc）。程式：`FilterScenario*`、`SavedFilterScenario`。測試：`FilterScenarioStoreTests`、`FilterScenarioValidatorTests` 等 | 照舊；2026-10-31 新增的 `scenario_id` 照用（裁定 2026-11-01-01） | 2026-10-30 確認頁 1-42：照提議。理由：2026-10-29 確認頁 4-3。名稱來源：畫面名稱「另存為新情境」2026-10-29 確認頁 5-19 使用者選保留；`JetSchemaCatalog` 對照 FILTER_CRITERIA；IDEA「已保存之篩選條件與測試結果」（idea-tool.bas:350） |
| 篩選動機 | 審計員說明為什麼選這個情境。 | 「篩選動機」；底稿「選擇此篩選條件的原因」 | action：`rationale`。資料庫：`filter_scenario.rationale`。程式：`defaultRationale` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：IDEA 對話框 RationaleForWP「高風險條件考量說明」（idea-tool.bas:384） |
| 條件 | 情境裡的一條篩選條件。 | 「條件」「篩選條件」 | action：`groups[].conditions[]`；`type` 欄位。資料庫：存在 `definition_json`。程式：`FilterConditionSpec`、`FilterConditionType`、`FilterConditionContext`；另有 `FilterConditionLabels`、`FilterConditionRenderer`、`FilterConditionPosition`、`ConditionAtom`；錯誤碼 `condition_not_object`、`condition_type_missing`、`condition_type_unsupported`、`condition_join_invalid`；支援日誌欄位 `condition_no`；前端 `effectiveConditionJoin`。畫面結構的 `data-rule-*` 屬性、`rule-row` 類別、檔名 `filter-step-rules.js` 與區域變數照舊用 rule。測試：`FilterConditionRendererTests` 等 | 照舊 | 2026-10-30 確認頁 1-43：寫法 B；錯誤碼、日誌欄位與型別 2026-11-01 第三個停點 1-3 改用 condition（裁定 2026-11-01-01）。名稱來源：舊版 VBA ServiceFilterCriteria.cls 用 Criterion（AddCriterion）；rule 與 condition 都是 Agent 自取 |
| 條件組 | 條件的分組，含子條件組。 | 「條件組」「子條件組」，組標頭的按鈕只寫名稱、前面放灰色加號；組內與組間的連接寫 AND、OR；型別標籤「子條件組」 | action：條件型別值 `group`；`groups`。資料庫：`definition_json`。程式：`FilterGroupSpec`。測試：`FilterNestedVoucherTests` | 照舊 | 2026-10-29 確認頁 4-7；2026-10-30 追加調整確認頁 2-4，2026-10-31 第六步照改。名稱來源：子條件組 2026-10-29 確認頁 5-17 使用者選保留；型別標籤 2026-10-29 確認頁 4-7 |
| 整張傳票條件 | 對整張傳票設定至少一筆、全部或不存在的條件。 | 「整張傳票條件」；組標頭按鈕「整張傳票」；型別標籤「整張傳票條件」；傳票明細狀態「整張傳票條件成立」 | action：條件型別值 `voucher`；`voucherConditions`。資料庫：`definition_json`。程式：`FilterNestedVoucher*`。測試：`FilterNestedVoucherTests`、`FilterNestedVoucherWorkflowTests`、`FilterSinglePurposeVoucherEquivalenceTests` | 照舊 | 2026-10-29 確認頁 4-7；2026-10-30 追加調整確認頁 2-4，2026-10-31 第六步照改。名稱來源：整張傳票條件：使用者從選項選定（附錄二來源分布）；2026-10-29 確認頁 4-7 |
| 常用篩選條件 | KCT 小組提出的十張常用條件卡片。 | 「常用篩選條件」；卡片名稱照 KCT 清單；條件列的樣態寫卡片名稱；型別標籤與讀回和卡片同名：「貸方為收入但借方非一般對方科目」「編製人員與核准人員相同」「在該季度前 X 天借記收入」 | action：情境 `source` 值 `common`。資料庫：`definition_json`。程式：`FILTER_COMMON_CONDITIONS`、`FILTER_COMMON_PRESETS`；條件型別 `revenueDebitNearQuarterEnd`、`revenueWithoutNormalCounterpart`、`manualRevenueEntry`、`preparerEqualsApprover`。測試：`CommonFilterPredicateTests`、`CommonFilterPredicateProviderTests`、`CommonFilterPrerequisiteProviderTests` | 照舊 | 2026-10-29 確認頁 4-6；2026-10-30 確認頁 1-47 確認照改現在的三個測試檔。名稱來源：「常用篩選條件」：使用者從選項選定；卡片名稱來自 KCT 小組；2026-10-29 確認頁 4-6 |
| 常用篩選條件的卡片代號 | 十張卡片在程式裡的身分標記。 | 卡片名稱照 KCT 清單，不顯示代號 | action：`revenueDebitNearQuarterEnd`、`fixedAssetDebitExpenseCredit`、`revenueWithoutNormalCounterpart`、`manualRevenueEntry`、`specificPreparer`、`specificKeywords`、`blankDescription`、`specificTrailingDigits`、`nonBusinessDay`、`preparerEqualsApprover`。資料庫：`definition_json` 的編輯來源。程式：`FILTER_COMMON_CONDITIONS[].key`。測試：`FilterEditorOriginsParserTests` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：Agent 自取，對應 KCT 清單（docs/kct-gl-workflow.md） |
| 欄位值比較 | 第五步用欄位值組成的條件。 | 「欄位值比較」；欄位按鈕 | action：條件型別值 `fieldValue`。資料庫：`definition_json`。程式：`FieldValueConditions` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：Agent 自取 |
| 欄位條件 | 第五步依欄位加入一條條件的分頁，取代原本的「自訂篩選條件」；依日期、數字、文字、類別四組排列，常用查法收在欄位「加入」打開的選單裡。 | 分頁「欄位條件」；組名「日期」「數字」「文字」「類別」，類別組放借貸別、人工/自動分錄與科目分類；每列右邊「加入」；條件開頭的方向詞「符合」「排除」，例如「總帳日期 符合 國定假日」 | 分頁識別 `field`（`data-condition-source="field"`）。程式：`fieldPickerHtml`、`fieldListHtml`、`fieldRowHtml`、`FIELD_GROUPS`（類別組是 `choice`）、`CHOICE_FIELDS`、`choiceFields`；按鈕 `data-add-field`；樣式 `filter-field`、`filter-field-group`、`filter-field-groups`、`filter-field-note`。測試：`frontend-journey-filter-build.test.cjs` | 分頁識別 `field` 照裁定；其餘程式名稱是 2026-11-03 新取的，使用者同日確認（裁定 2026-11-03-31）；四組共用的白紙 `filter-field-groups` 是第三個停點之後新取的，使用者同日確認（裁定 2026-11-03-36） | 2026-11-03 第八階段確認頁 1-2、1-3、2-1（裁定 2026-11-03-05、2026-11-03-06、2026-11-03-10）；組名「類別」是第一個停點在視窗回答的題目（裁定 2026-11-03-18），分頁識別是第一個停點 1-8（裁定 2026-11-03-21）。原稱「自訂篩選條件」，方向詞原稱「保留」；程式名稱是 Agent 自取 |
| 常用查法 | 欄位條件裡收在欄位「加入」選單裡的一鍵查法，按下只加入那一條條件（裁定 2026-11-03-32、2026-11-03-37）。 | 選單項目寫短名稱，例如「週末」「大額」；滑鼠提示寫完整名稱，例如「總帳日期在週末」「大額分錄」；選單第一項是一般條件，寫欄位名稱；開選單的「加入」與選單的報讀名稱「加入總帳日期的條件」 | 程式：`FIELD_SHORTCUT_GROUPS`、`findFieldShortcut`、`addFieldShortcut`、`fieldShortcuts`、`fieldShortcutsHtml`；選單項目 `data-add-shortcut`；選單 `filter-field__shortcuts`，用共用的 `jet-menu`，識別 `filter-field-menu-` 接欄位識別；Gui 驅動程式的步驟種類 `shortcut:` | 名稱「常用查法」照裁定；程式名稱 2026-11-03 從按審計目的分類時留下的 `FILTER_PURPOSE_CHECK_GROUPS`、`findPurposeCheck`、`addPurposeCheck`、`data-add-check` 改成常用查法的字根（裁定 2026-11-03-30）；選單識別 `filter-field-menu-` 是第三個停點之後新取的，使用者同日確認（裁定 2026-11-03-36）；報讀名稱「加入總帳日期的條件」是選單改從「加入」打開時新取的，取代原本的「總帳日期的常用查法」，使用者 2026-11-04 確認（裁定 2026-11-04-01） | 2026-11-03 第八階段確認頁 1-1、1-2（裁定 2026-11-03-05、2026-11-03-06）；第一個停點 1-3 決定借貸別與人工/自動分錄底下不放（裁定 2026-11-03-20）。程式名稱是 Agent 自取 |
| 條件總覽的加入鍵 | 第五步條件總覽三個分頁每列右邊的鍵。常用篩選條件與預篩選的條件還不在作用中的組時寫「加入」，已在這一組時往下凹、寫「已加入」（裁定 2026-11-05-08）；欄位條件分頁永遠是「加入」（裁定 2026-11-05-12）。 | 鍵「加入」「已加入」；報讀名稱「加入「空白摘要」」「移出「空白摘要」」 | 程式：`conditionKeyHtml`、`refreshConditionKeys`、`bindConditionKeys`、`removeFromActiveGroup`、`activeOwnConditions`、`prescreenAdded`；樣式 `condition-key`、`condition-key--added`、`filter-common-list`、`filter-check__elsewhere`；停用原因的識別 `filter-common-note-` 接條件代號。測試：`frontend-journey-filter-build.test.cjs` | 程式名稱、樣式名稱與畫面句子是 2026-11-05 第 8 步新取的，使用者同日確認（裁定 2026-11-05-15）。欄位「已加入」選單用到的 `fieldAddedMenuHtml`、`conditionFieldId`、`fieldAdded`、`data-remove-field`、`jet-menu__item--remove` 與兩個選單項目，照裁定 2026-11-05-12 一起拿掉。常用篩選條件原本的卡片樣式 `picker-card`、`condition-picker__grid` 已拿掉 | 停點一使用者否決勾選框外觀的整列開關、要求三個分頁一致，欄位條件選「保留版 (Recommended)」（裁定 2026-11-05-08）；停點二使用者認為欄位的「已加入」設計過度（裁定 2026-11-05-12）。名稱是 Agent 自取 |
| 加入到第 N 組 | 第五步有兩組以上條件時，選定條件總覽加入哪一組。只有這一套組別選擇（裁定 2026-09-09-08）。 | 條件總覽標題同一列「加入到第 1、2、3 組」，組號是分段切換；報讀名稱「加入到」「第 N 組」 | 程式：`conditionTargetHtml`、`chooseTargetGroup`、`activeEditableGroup`、`setActiveGroup`；單選鈕 `data-condition-target`，值是組的順序，名稱 `filter-condition-target`；樣式 `filter-target`、`filter-target__lead`、`filter-target__groups`、`filter-target__tail`；草稿欄位 `__active`。測試：`frontend-journey-filter-build.test.cjs` | 待使用者確認：`filter-target__lead`、`filter-target__groups`、`filter-target__tail` 與單選鈕名稱 `filter-condition-target` 是 2026-11-05 第 12 步新取的；`data-condition-target` 原本掛在下拉選單上，改掛在單選鈕上 | 停點三使用者指定網址 `?sample-target=segment&sample-join=seam` 那一組（裁定 2026-11-05-16）。名稱是 Agent 自取 |
| 條件之間的連接 | 第五步兩條條件之間、兩組之間與兩條子條件之間的 AND 或 OR。一組一種連接方式，要混用時開子條件組。 | 分隔線上的分段切換「AND」「OR」；組內第一條分隔線是切換，後面的分隔線只寫同一個字；報讀名稱「第 N 組條件之間的關係」「組與組之間的關係」「子條件連接方式」 | action：條件的 `join`。程式：`joinToggleHtml`、`ruleJoinHtml`、`interSetConnectorHtml`；樣式 `rule-join`、`rule-join--switch`、`rule-join--child`、`rule-join__word`、`set-rail`；單選鈕 `data-set-combinator`、`data-set-join`、`data-child-join`。測試：`frontend-journey-filter-build.test.cjs` | 待使用者確認：`ruleJoinHtml`、`rule-join`、`rule-join--switch`、`rule-join--child`、`rule-join__word` 是 2026-11-05 第 12 步新取的。組標頭的 `filter-group-settings`、`filter-combinator` 與條件列只給連接詞用的 `data-join` 已拿掉 | 停點三使用者指定網址 `?sample-target=segment&sample-join=seam` 那一組（裁定 2026-11-05-16）。名稱是 Agent 自取，`join` 沿用 action 的連接欄位 |
| 從情境範例開始 | 右側條件組合上方放十一個情境範例的托盤，套用後替換整份條件。 | 「從情境範例開始」；預設收起，按標題打開 | 程式：`FILTER_SCENARIO_TEMPLATES`、`exampleListHtml`；開合記在 `viewState.disclosures.templates`；樣式 `filter-template-tray`。測試：`frontend-journey-filter-build.test.cjs` | 照舊；樣式名稱 `filter-template-tray` 是 2026-11-03 新取的，使用者同日確認（裁定 2026-11-03-31） | 2026-11-03 第八階段確認頁 1-1、1-3（裁定 2026-11-03-05）；預設收起是第一個停點在視窗回答的題目（裁定 2026-11-03-19）；第十一個範例見裁定 2026-11-03-08 |
| 借貸科目組合 | 第五步用借方與貸方分類組成的條件。 | 「借貸科目組合」「借方與貸方科目編號組合」「借方與貸方分類組合」 | action：條件型別值 `specialAccountCategoryPair`、`accountPair`；`pairMode`。資料庫：`definition_json`。程式：`SpecialAccountCategoryPairModes`、`AccountPairModes`。測試：`SpecialAccountCategoryPairPredicateTests`、`AccountPairCategorySelectionPredicateTests` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：畫面名稱 2026-10-29 確認頁 5-23 使用者選保留；IDEA「選擇特定科目配對」；識別字 Agent 自取 |
| 科目與人員統計 | 依科目或人員的分錄筆數或傳票張數篩選。 | 「科目與人員統計」 | action：條件型別值 `entityFrequency`；`countUnit`（`entries`、`vouchers`）。資料庫：`definition_json`。程式：`EntityFrequencyConditions` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：畫面名稱 2026-10-29 確認頁 5-20 使用者選保留 |
| 符合條件的分錄與參考分錄 | 傳票明細裡命中條件的分錄，與同張傳票的其他分錄。 | 「符合條件的分錄」「參考分錄」；狀態後面接著的「主要」「傳票條件」 | action：`isHit`、`hitRowCount`、`primaryConditions`。程式：`FilterVoucherDetail`、`FilterConditionEvidenceSql` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：「符合條件的分錄」：jet-frontend-description.md:500；參考分錄 2026-10-29 確認頁 5-18 使用者選保留。佐證條件的顯示已經用不到，第四階段第二步連同 `evidenceConditions` 一起拿掉 |
| 篩選命中 | 每個已存情境命中的分錄清單。 | 畫面以「符合條件的分錄」呈現；傳票表的「符合情形」欄，傳票列寫「符合 M 筆」（2026-10-31 新增，使用者選照現在，裁定 2026-11-01-03） | action：`query.filterHitsPage`。資料庫：`filter_hit`（scenario_id、entry_id）；命中傳票裡每筆分錄符合第幾組第幾條存在 `filter_hit_condition`（scenario_id、entry_id、group_no、condition_no）。程式：`FilterHitRow`、`FilterHitMaterializeService`、`IFilterHitRepository`、`IFilterHitMaterializer` | 照舊；2026-10-31 新增的 `filter_hit_condition` 與欄位 `scenario_id` 照用（裁定 2026-11-01-01） | 2026-10-30 確認頁 1-44：照提議。理由：表與型別混用 run 與 hit；查詢 action 已叫 filterHits。名稱來源：`JetSchemaCatalog` 對照 FILTER_HITS；表名 Agent 自取 |
| 傳票 | 同一號碼的一組分錄。 | 「傳票」「傳票張數」「傳票明細」 | action：`query.filterVoucherPage`、`query.filterVoucherRowsPage`；`voucherTotal`；`countUnit` 值 `vouchers`；條件型別 `voucher`；`voucherNumber`、`voucherBalanceTest`、`voucherNumberAcrossGlDates`。資料庫：`voucher_number`。程式：`FilterVoucher*`、`TaggedVoucherRow`、`UnbalancedVoucherDateRow`；另有 `UnbalancedVoucher`、`VoucherNumberAcrossGlDatesCounts`。測試：`HighRiskSummaryVoucherPageTests`、`WholeVoucherRowCountTests`、`WorkingPaperBlankVoucherNumberTests` | 照舊 | 2026-10-30 確認頁 1-45：寫法 B。名稱來源：舊版 VBA DocumentNumber、DOCUMENT_BALANCE（DbSchema.cls）；IDEA `_Doc_Sum`、K_傳票編號 |
| 傳票總金額 | 一張傳票的金額合計。 | 底稿「傳票總金額」 | action：`voucherTotal`。程式：`FilterVoucherSummary` | 照舊：跟著「傳票」一列用 voucher，`voucherTotal` 不改 | 2026-10-30 確認頁 1-46：寫法 B。名稱來源：舊版底稿 Step 4 主表欄「傳票總金額」（jet-legacy-notes.md 第 1 節） |
| 高風險條件彙總 | 每個已儲存情境符合幾張傳票、幾筆分錄，以及每張傳票同時符合哪些情境；底稿第四步的各條件欄是同一份資料。 | 第五步頁籤「已儲存情境」、「高風險條件彙總」與「查看彙總」，頁籤「符合條件摘要」「傳票符合的條件」，內部標題和頁籤同名，表頭「表頭代號」；底稿「step4 符合高風險條件傳票」「step4-1 符合高風險條件傳票明細」的各條件欄 | action：`query.highRiskSummaryScenarios`、`query.highRiskSummaryVoucherPage`、`query.highRiskSummaryRowPage`。程式：`HighRiskSummary*`、`ScenarioTagMask`、`TagColumnSet`。測試：`HighRiskSummaryScenariosTests`、`HighRiskSummaryVoucherPageTests`、`ScenarioTagMaskTests` | 照舊 | 2026-10-30 追加調整確認頁 5-1、2026-10-29 確認頁 5-2，2026-10-31 第六步照改。名稱來源：舊版底稿 Step 4 明細表「各條件 tag 欄」；IDEA R1_Tag 這類欄（idea-tool.bas:8124）；「高風險條件」出自舊版底稿第三步「高風險範圍條件（HRC）彙總」；「矩陣」是 Agent 2026-06 自取 |
| 篩選範圍 | 第五步篩選依據的分錄範圍。 | 「GL：查核期間內的分錄」；底稿「篩選測試母體 :」 | action：`populationScope` 值 `auditPeriod`。程式：`GlPopulationScope`、`GlPopulationScopeSql`、`FilterPopulationScopeParser`。測試：`GlPopulationScopeGoldenTests` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：舊版底稿用「母體」（JE測試母體）；畫面依使用者 2026-10-08 不用「母體」 |

## 報告、底稿與其他畫面

| 概念 | 意思 | 畫面與報告 | 各層現在的名稱 | 確認的改法 | 依據 |
|:---|:---|:---|:---|:---|:---|
| 工作底稿 | 第六步匯出的正式底稿。 | 「工作底稿」「匯出底稿」；檔案 WorkingPaper | action：`export.workingPaperStream`；報告種類 `workingPaper`。程式：`WorkingPaper*`（29 個型別）。測試：`WorkingPaper*Tests`（32 個類別） | 照舊 | 2026-10-30 確認頁 1-48：寫法 B。使用者備註：「儘管 workPaper 或許比較正確，但由於審計部門之間已經有統一的標準命名規範，而規範中定義底稿即是 "WorkingPaper" 所以需要你遵循這個定義去修改所有相關名稱」。名稱來源：範本 WorkingPaper.xlsx 與報告種類 workingPaper 是舊版名稱；Workpaper 是 Agent 自取 |
| 底稿工作表 | 工作底稿的固定工作表。 | 「資料預先整理之說明」「JE WorkingPaper說明」「step1 完整性測試」到「step5 財務報表關帳後調整之分錄」「自動化工具-檔案欄位資訊」「自動化工具-假期假日資訊」「自動化工具-科目配對資訊」 | 程式：`WorkingPaperSheetCatalog.Step1` 到 `Step5`、`FieldInfo`、`CalendarInfo`、`AccountMapping`。測試：`WorkingPaperSheetCatalogTests` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：舊版範本 WorkingPaper.xlsx；jet-legacy-notes.md 第 1 節 |
| 資料驗證報告 | 資料驗證輸出的報告檔。 | 「資料驗證報告」；工作表「V_Report 1」到「V_Report 6」、`Source_Quality` | action：`export.validationReports`；報告種類 `validationReport`。程式：`IValidationReportWriter`、`ValidationReportPlan`。測試：`ExportValidationReportsTypedSeamTests` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：畫面名稱 2026-10-29 確認頁 5-28 使用者選保留；範本 ValidationReport.xlsx 是舊版 |
| 預篩選報告 | 預篩選輸出的報告檔。 | 「預篩選報告」；工作表「R1」到「R7」 | action：`export.prescreenReport`；報告種類 `prescreenReport`。程式：`IPrescreenReportWriter`、`PrescreenReportPlan`；範本常數 `PrescreeningReport.xlsx`。測試：`ExportPrescreenReportTypedSeamTests` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：畫面名稱 2026-10-29 確認頁 5-28 使用者選保留；範本 PrescreeningReport.xlsx 是舊版 |
| 條件篩選報告 | 第五步輸出的報告檔。 | 「條件篩選報告」 | action：`export.criteriaSelectionReport`；報告種類 `criteriaSelectionReport`。程式：`ICriteriaSelectionReportWriter`、`CriteriaSelectionReportContext`。測試：`PrescreenCriteriaReportCancellationTests` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：畫面名稱 2026-10-29 確認頁 5-28 使用者選保留；範本 CriteriaSelectionReport.xlsx 是舊版 |
| 科目配對範本 | 給審計員填分類的 Excel 範本。 | 「科目配對範本」；按鈕「準備科目配對範本」「匯入科目配對範本」「重建科目配對範本」；結果清單「已儲存的科目配對」 | action：`export.accountMappingTemplate`；報告種類 `accountMapping`。程式：`AccountMappingTemplateWriter` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：範本 AccountMapping.xlsx 是舊版 |
| 報告與輸出紀錄 | JET 記錄已匯出的報告與底稿檔，供第六步清單與過期判斷。 | 「報告清單」「工作底稿版本紀錄」；建立時間 | action：`reports`、`reportWarning`、`fileState`；`export.validationReports`；匯出回應的 `publishedReport`、`publishedReports`；`reportId`、`reportKind`。資料庫：案件資料夾的報告索引 `reports.json`。程式：`Report*`、`IReportStore`、`ProjectReportStore`、`PublishedReportCatalog`、`AuditedReportStore`。測試：`ReportBatchPublicationTests`、`ReportRevisionStateTests` | 照舊 | 2026-10-30 確認頁 1-49：寫法 B。名稱來源：畫面名稱 2026-10-29 確認頁 5-29 使用者選保留；artifact 是 Agent 自取 |
| 操作紀錄 | 重新匯入或重新配對時寫下的留痕。 | 「操作紀錄」（例如「這次變更沒有寫入操作紀錄」） | 資料庫：`operation_log`；SQL Server `dbo.operation_log`。程式：`IProjectOperationLog`、`ProjectOperationLog`、`ProjectOperationEvent`、`ProjectOperations`、`SqlServerOperationLog` | 照舊 | 2026-10-30 確認頁 1-50：照提議。理由：畫面寫操作紀錄；audit 和審計業務同字容易混淆。名稱來源：使用者 2026-10-19 裁定寫入失敗的提醒句；識別字 Agent 自取 |
| 狀態與訊息 | 前端訊息面板的保存紀錄。 | 「狀態與訊息」 | action：`log.append`、`log.recent`。資料庫：`message_log`。程式：`IMessageLogStore`、`MessageLogEntry` | 照舊 | 2026-10-30 確認頁 1-51：照提議。理由：2026-10-29 確認頁 4-3；和型別同名。名稱來源：Agent 自取 |
| 資料表摘要 | 資料預覽列出全部資料表的頁籤。 | 「資料表摘要」 | action：資料預覽 dataset `schemaOverview`。程式：`DataPreviewSchemaOverview`、`JetSchemaCatalog` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：Agent 自取 |
| 分錄測試流程總覽 | 流程總覽面板與完成度。 | 「分錄測試流程總覽」「案件流程完成度」；預篩選規則缺資料時寫「無法執行」，科目的筆數寫「分錄筆數」，和第四步同名 | action：`workflow.milestone`、`project.saveProgress`。程式：`overview-bi.js`、`WorkflowResultStateSupport`。測試：`frontend-journey-overview.test.cjs` | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：畫面名稱 2026-10-29 確認頁 5-27 使用者選保留 |
| 已完成的步驟 | 視窗寬度不到 1536px 時，目前步驟上方的已完成步驟收成的一列，點它在原地展開（裁定 2026-11-05-09）。 | 「已完成的步驟」，後面寫各步驟名稱，以頓號分開 | 程式：`doneStepsAbove`、`doneGroupHtml`、`doneGroupApplies`、`DONE_GROUP_MEDIA`、`stepRowHtml`；屬性 `data-done-group-toggle`；樣式 `stepflow-item--done-group`。測試：`frontend-journey-step4.test.cjs` | 程式名稱與樣式名稱是 2026-11-05 第 8 步新取的，使用者同日確認（裁定 2026-11-05-15）；畫面名稱「已完成的步驟」寫在使用者選的選項裡 | 停點一使用者選「乙：圖示按鈕、可展開」（裁定 2026-11-05-09），選項說明寫「已完成步驟收成「已完成的步驟」一列」。程式名稱是 Agent 自取 |
| 用 Excel 配對 | 第四步科目配對改用 Excel 填科目分類的入口；也能匯入工作底稿的「自動化工具-科目配對資訊」工作表。 | 「用 Excel 配對」；選工作表時「「檔名」有多個工作表，請選擇科目配對所在的工作表。」「工作表」「匯入這個工作表」「取消」 | 畫面：`toggle-account-excel`。選工作表：action `import.accountMapping.fromFile` 的 `sheetName`；前端 `accountSheetPick`、`WORKING_PAPER_ACCOUNT_MAPPING_SHEET`、`importAccountMappingSource`、`import-account-mapping-sheet`、`cancel-account-mapping-sheet`、`account-mapping-sheet-pick`、`data-account-mapping-sheet`；認出 JET 範本與略過中繼資料工作表的 `ACCOUNT_MAPPING_TEMPLATE_SHEET`、`REPORT_METADATA_SHEET`；程式 `AccountMappingExportRow.NotInTbName`。測試：`AccountMappingWorkingPaperSheetTests` | 照舊；2026-11-05 新取的選工作表名稱與畫面句子，使用者同日確認（裁定 2026-11-05-11）。`ACCOUNT_MAPPING_TEMPLATE_SHEET`、`REPORT_METADATA_SHEET` 是同日提交前複審後新取的，使用者同日確認（裁定 2026-11-05-15） | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：畫面名稱 2026-10-29 確認頁 5-3 使用者選保留。意思一欄原本寫「第三步改用 Excel 填欄位配對的入口」，2026-11-05 改正為第四步，畫面一直在第四步科目配對；選工作表是裁定 2026-11-05-07 |

| 第五步的其他畫面名稱 | 只在畫面出現、使用者選保留的名稱。 | 「情境範例」「大額分錄」「總帳日期在指定日期區間」「收入之人工分錄」「特定人員(財務長/執行長/高階主管等)建立之分錄」「編製人員與核准人員相同」「非營業日之分錄」 | action：對應的條件型別見各列 | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：畫面名稱 2026-10-29 確認頁 5-16、5-21、5-22、5-24、5-25、5-26 使用者選保留。「單項條件」2026-11-03 拿掉（裁定 2026-11-03-20），「指定日期區間」改成常用查法的完整名稱「總帳日期在指定日期區間」（裁定 2026-11-03-20 照第一個停點的草稿） |
| 輸出檔名規則 | 輸出檔名加查核起訖日與時間。 | 檔名規則 | 程式：未逐一確認；`ProjectFileNames` 只管案件前綴 | 照舊 | 2026-10-30 確認頁 1-0：全部照舊。名稱來源：2026-10-29 確認頁 5-31 使用者選保留 |

## 不再使用的名稱

第四階段照這份名詞表改名後，下面這些舊名稱不再使用。文件檢查會在產品程式 `src/JET/JET` 裡找這些名稱，第三方套件除外，註解也算；找到就失敗，要改成「現在的名稱」。
「比對方式」寫「不自動比對」的是一般英文詞，程式裡另有正常用法，只列出來供查。
測試與驗證工具會為了確認舊名稱已經消失而寫出它們，修改既有測試時留下的說明也會提到舊名稱，所以不在檢查範圍；歷史文件照原文保留，也不檢查。

寫「整個詞」的，前後緊接英文字母、數字或底線時不算，因為那是另一個名稱的一部分；寫「字串中出現」的，只要出現就算。現在的名稱包含舊名稱時，現在名稱裡的那一段不算，例如「整張傳票條件成立」不會被當成舊的「傳票條件成立」。

| 舊名稱 | 用在哪裡 | 現在的名稱 | 比對方式 |
|:---|:---|:---|:---|
| `query.docBalancePage` | action | `query.voucherBalancePage` | 整個詞 |
| `export.validationArtifacts` | action | `export.validationReports` | 整個詞 |
| `export.workpaperStream` | action | `export.workingPaperStream` | 整個詞 |
| `accountTaxonomy.save` | action | `accountCategory.save` | 整個詞 |
| `query.nullRecordsPage` | action | `query.blankRecordsPage` | 整個詞 |
| `empty_effective_population` | action 欄位、錯誤碼與支援日誌事件 | `empty_gl` | 整個詞 |
| `creatorEntryCounts` | action 欄位、錯誤碼與支援日誌事件 | `preparerEntryCounts` | 整個詞 |
| `creatorSummary` | action 欄位、錯誤碼與支援日誌事件 | `preparerSummary` | 整個詞 |
| `creator_summary` | action 欄位、錯誤碼與支援日誌事件 | `preparer_summary` | 整個詞 |
| `creator_summary_detail` | action 欄位、錯誤碼與支援日誌事件 | `preparer_summary_detail` | 整個詞 |
| `creditAmt` | action 欄位、錯誤碼與支援日誌事件 | `creditAmount` | 整個詞 |
| `debitAmt` | action 欄位、錯誤碼與支援日誌事件 | `debitAmount` | 整個詞 |
| `docBalanceTest` | action 欄位、錯誤碼與支援日誌事件 | `voucherBalanceTest` | 整個詞 |
| `doc_balance` | action 欄位、錯誤碼與支援日誌事件 | `voucher_balance` | 整個詞 |
| `doc_balance_test` | action 欄位、錯誤碼與支援日誌事件 | `voucher_balance_test` | 整個詞 |
| `unbalancedDocuments` | action 欄位、錯誤碼與支援日誌事件 | `unbalancedVouchers` | 整個詞 |
| `dcCreditCode` | action 欄位、錯誤碼與支援日誌事件 | `drCrCreditCode` | 整個詞 |
| `dcDebitCode` | action 欄位、錯誤碼與支援日誌事件 | `drCrDebitCode` | 整個詞 |
| `dcField` | action 欄位、錯誤碼與支援日誌事件 | `drCr` | 整個詞 |
| `createBy` | action 欄位、錯誤碼與支援日誌事件 | `createdBy` | 整個詞 |
| `lineID` | action 欄位、錯誤碼與支援日誌事件 | `lineItem` | 整個詞 |
| `approveBy` | action 欄位、錯誤碼與支援日誌事件 | `approvedBy` | 整個詞 |
| `docDate` | action 欄位、錯誤碼與支援日誌事件 | `approvalDate` | 整個詞 |
| `docDateNonBusinessDay` | action 欄位、錯誤碼與支援日誌事件 | `approvalDateNonBusinessDay` | 整個詞 |
| `docNum` | action 欄位、錯誤碼與支援日誌事件 | `voucherNumber` | 整個詞 |
| `documentNumber` | action 欄位、錯誤碼與支援日誌事件 | `voucherNumber` | 整個詞 |
| `jeSource` | action 欄位、錯誤碼與支援日誌事件 | `sourceModule` | 整個詞 |
| `reportArtifactWarning` | action 欄位、錯誤碼與支援日誌事件 | `reportWarning` | 整個詞 |
| `reportArtifacts` | action 欄位、錯誤碼與支援日誌事件 | `reports` | 整個詞 |
| `completenessTest.partA` | action 欄位、錯誤碼與支援日誌事件 | `completenessTest.storedTotals` | 整個詞 |
| `partA` | action 欄位、錯誤碼與支援日誌事件 | `storedTotals` | 整個詞 |
| `part_a` | action 欄位、錯誤碼與支援日誌事件 | `stored_totals` | 整個詞 |
| `entityName` | action 欄位、錯誤碼與支援日誌事件 | `clientName` | 整個詞 |
| `case_to_workpaper_failed` | action 欄位、錯誤碼與支援日誌事件 | `case_to_working_paper_failed` | 整個詞 |
| `feedback_difference_workpaper_export` | action 欄位、錯誤碼與支援日誌事件 | `feedback_difference_working_paper_export` | 整個詞 |
| `gui_workpaper_manual_flag_not_text` | action 欄位、錯誤碼與支援日誌事件 | `gui_working_paper_manual_flag_not_text` | 整個詞 |
| `gui_workpaper_manual_flags_incomplete` | action 欄位、錯誤碼與支援日誌事件 | `gui_working_paper_manual_flags_incomplete` | 整個詞 |
| `gui_workpaper_outside_owned_root` | action 欄位、錯誤碼與支援日誌事件 | `gui_working_paper_outside_owned_root` | 整個詞 |
| `gui_workpaper_part_missing` | action 欄位、錯誤碼與支援日誌事件 | `gui_working_paper_part_missing` | 整個詞 |
| `totalWorkpaperCount` | action 欄位、錯誤碼與支援日誌事件 | `totalWorkingPaperCount` | 整個詞 |
| `workpaperCount` | action 欄位、錯誤碼與支援日誌事件 | `workingPaperCount` | 整個詞 |
| `workpaperExportEnabled` | action 欄位、錯誤碼與支援日誌事件 | `workingPaperExportEnabled` | 整個詞 |
| `workpaper_history_after_export_failed` | action 欄位、錯誤碼與支援日誌事件 | `working_paper_history_after_export_failed` | 整個詞 |
| `workpaper_history_before_export_failed` | action 欄位、錯誤碼與支援日誌事件 | `working_paper_history_before_export_failed` | 整個詞 |
| `workpaper_manual_flag_path_missing` | action 欄位、錯誤碼與支援日誌事件 | `working_paper_manual_flag_path_missing` | 整個詞 |
| `workpaper_manual_flag_text` | action 欄位、錯誤碼與支援日誌事件 | `working_paper_manual_flag_text` | 整個詞 |
| `workpaper_payload_not_object` | action 欄位、錯誤碼與支援日誌事件 | `working_paper_payload_not_object` | 整個詞 |
| `workpaper_sheets_or_output_path` | action 欄位、錯誤碼與支援日誌事件 | `working_paper_sheets_or_output_path` | 整個詞 |
| `authorizedPreparers` | action 欄位、錯誤碼與支援日誌事件 | `authorizedPreparer` | 整個詞 |
| `money` | action 欄位、錯誤碼與支援日誌事件 | `amount` | 不自動比對 |
| `manual` | action 欄位、錯誤碼與支援日誌事件 | `isManual` | 不自動比對 |
| `accName` | action 欄位、錯誤碼與支援日誌事件 | `accountName` | 整個詞 |
| `accNum` | action 欄位、錯誤碼與支援日誌事件 | `accountNumber` | 整個詞 |
| `accountCode` | action 欄位、錯誤碼與支援日誌事件 | `accountNumber` | 整個詞 |
| `accountCodePair` | action 欄位、錯誤碼與支援日誌事件 | `accountNumberPair` | 整個詞 |
| `closingCredit` | action 欄位、錯誤碼與支援日誌事件 | `closingCreditBalance` | 整個詞 |
| `closingDebit` | action 欄位、錯誤碼與支援日誌事件 | `closingDebitBalance` | 整個詞 |
| `openingCredit` | action 欄位、錯誤碼與支援日誌事件 | `openingCreditBalance` | 整個詞 |
| `openingDebit` | action 欄位、錯誤碼與支援日誌事件 | `openingDebitBalance` | 整個詞 |
| `lastPeriodStart` | action 欄位、錯誤碼與支援日誌事件 | `prepStartDate` | 整個詞 |
| `postPeriodApproval` | action 欄位、錯誤碼與支援日誌事件 | `prepPeriodApproval` | 整個詞 |
| `post_period_approval` | action 欄位、錯誤碼與支援日誌事件 | `prep_period_approval` | 整個詞 |
| `post_period_approval_detail` | action 欄位、錯誤碼與支援日誌事件 | `prep_period_approval_detail` | 整個詞 |
| `outOfRangeDate` | action 欄位、錯誤碼與支援日誌事件 | `approvalDateNotInPeriod` | 整個詞 |
| `outOfRangeDateCount` | action 欄位、錯誤碼與支援日誌事件 | `approvalDateNotInPeriodCount` | 整個詞 |
| `outOfRangeDateNaReason` | action 欄位、錯誤碼與支援日誌事件 | `approvalDateNotInPeriodNaReason` | 整個詞 |
| `out_of_range_approval_date_detail` | action 欄位、錯誤碼與支援日誌事件 | `approval_date_not_in_period_detail` | 整個詞 |
| `caseName` | action 欄位、錯誤碼與支援日誌事件 | `projectName` | 整個詞 |
| `feedback_taxonomy_cancel` | action 欄位、錯誤碼與支援日誌事件 | `feedback_category_cancel` | 整個詞 |
| `feedback_taxonomy_caret_edit` | action 欄位、錯誤碼與支援日誌事件 | `feedback_category_caret_edit` | 整個詞 |
| `feedback_taxonomy_composition` | action 欄位、錯誤碼與支援日誌事件 | `feedback_category_composition` | 整個詞 |
| `feedback_taxonomy_continuous_typing` | action 欄位、錯誤碼與支援日誌事件 | `feedback_category_continuous_typing` | 整個詞 |
| `feedback_taxonomy_reopen` | action 欄位、錯誤碼與支援日誌事件 | `feedback_category_reopen` | 整個詞 |
| `feedback_taxonomy_return` | action 欄位、錯誤碼與支援日誌事件 | `feedback_category_return` | 整個詞 |
| `feedback_taxonomy_saved` | action 欄位、錯誤碼與支援日誌事件 | `feedback_category_saved` | 整個詞 |
| `mapping_taxonomy` | action 欄位、錯誤碼與支援日誌事件 | `mapping_category` | 整個詞 |
| `nested_taxonomy_parent` | action 欄位、錯誤碼與支援日誌事件 | `nested_category_parent` | 整個詞 |
| `path_taxonomy` | action 欄位、錯誤碼與支援日誌事件 | `path_category` | 整個詞 |
| `taxonomyRevision` | action 欄位、錯誤碼與支援日誌事件 | `categoryRevision` | 整個詞 |
| `taxonomy_built_in_removed` | action 欄位、錯誤碼與支援日誌事件 | `category_built_in_removed` | 整個詞 |
| `taxonomy_custom_category_id_invalid` | action 欄位、錯誤碼與支援日誌事件 | `category_custom_category_id_invalid` | 整個詞 |
| `taxonomy_draft_id_duplicate` | action 欄位、錯誤碼與支援日誌事件 | `category_draft_id_duplicate` | 整個詞 |
| `taxonomy_integer_expected` | action 欄位、錯誤碼與支援日誌事件 | `category_integer_expected` | 整個詞 |
| `taxonomy_revision_conflict` | action 欄位、錯誤碼與支援日誌事件 | `category_revision_conflict` | 整個詞 |
| `taxonomy_string_expected` | action 欄位、錯誤碼與支援日誌事件 | `category_string_expected` | 整個詞 |
| `taxonomy_string_missing` | action 欄位、錯誤碼與支援日誌事件 | `category_string_missing` | 整個詞 |
| `accountMappings` | action 欄位、錯誤碼與支援日誌事件 | `accountMapping` | 整個詞 |
| `nullRecordsTest` | action 欄位、錯誤碼與支援日誌事件 | `blankRecordsTest` | 整個詞 |
| `null_records` | action 欄位、錯誤碼與支援日誌事件 | `blank_records` | 整個詞 |
| `null_records_category_invalid` | action 欄位、錯誤碼與支援日誌事件 | `blank_records_category_invalid` | 整個詞 |
| `null_records_test` | action 欄位、錯誤碼與支援日誌事件 | `blank_records_test` | 整個詞 |
| `blankPostDate` | action 欄位、錯誤碼與支援日誌事件 | `blankGlDate` | 整個詞 |
| `blankPostDateCount` | action 欄位、錯誤碼與支援日誌事件 | `blankGlDateCount` | 整個詞 |
| `blank_post_date` | action 欄位、錯誤碼與支援日誌事件 | `blank_gl_date` | 整個詞 |
| `blank_post_date_detail` | action 欄位、錯誤碼與支援日誌事件 | `blank_gl_date_detail` | 整個詞 |
| `first_post_date` | action 欄位、錯誤碼與支援日誌事件 | `first_gl_date` | 整個詞 |
| `nullPostDate` | action 欄位、錯誤碼與支援日誌事件 | `blankGlDate` | 整個詞 |
| `nullPostDateCount` | action 欄位、錯誤碼與支援日誌事件 | `blankGlDateCount` | 整個詞 |
| `postDate` | action 欄位、錯誤碼與支援日誌事件 | `glDate` | 整個詞 |
| `postDateHoliday` | action 欄位、錯誤碼與支援日誌事件 | `glDateHoliday` | 整個詞 |
| `postDateMax` | action 欄位、錯誤碼與支援日誌事件 | `glDateMax` | 整個詞 |
| `postDateMin` | action 欄位、錯誤碼與支援日誌事件 | `glDateMin` | 整個詞 |
| `postDateNonBusinessDay` | action 欄位、錯誤碼與支援日誌事件 | `glDateNonBusinessDay` | 整個詞 |
| `postDateRange` | action 欄位、錯誤碼與支援日誌事件 | `glDateRange` | 整個詞 |
| `postDateWeekend` | action 欄位、錯誤碼與支援日誌事件 | `glDateWeekend` | 整個詞 |
| `sameAsPostDate` | action 欄位、錯誤碼與支援日誌事件 | `sameAsGlDate` | 整個詞 |
| `sourceQuality.nullPostDate` | action 欄位、錯誤碼與支援日誌事件 | `sourceQuality.blankGlDate` | 整個詞 |
| `dateDimension` | action 欄位、錯誤碼與支援日誌事件 | `calendar` | 整個詞 |
| `low_frequency_account` | action 欄位、錯誤碼與支援日誌事件 | `rare_account` | 整個詞 |
| `lowFrequencyAccount` | action 欄位、錯誤碼與支援日誌事件 | `rareAccount` | 整個詞 |
| `lowFrequencyAccountCount` | action 欄位、錯誤碼與支援日誌事件 | `rareAccountCount` | 整個詞 |
| `lowFrequencyPreparer` | action 欄位、錯誤碼與支援日誌事件 | `rarePreparer` | 整個詞 |
| `low_frequency_preparer` | action 欄位、錯誤碼與支援日誌事件 | `rare_preparer` | 整個詞 |
| `latestRuns` | action 欄位、錯誤碼與支援日誌事件 | `validationResult`、`prescreenResult` | 整個詞 |
| `prescreenRunId` | action 欄位、錯誤碼與支援日誌事件 | `prescreenResultId` | 整個詞 |
| `resultRef.runId` | action 欄位、錯誤碼與支援日誌事件 | `resultRef.resultId` | 整個詞 |
| `runId` | action 欄位、錯誤碼與支援日誌事件 | `resultId` | 不自動比對 |
| `runKind` | action 欄位、錯誤碼與支援日誌事件 | 拿掉，不再使用 | 不自動比對 |
| `run_id_missing` | action 欄位、錯誤碼與支援日誌事件 | `result_id_missing` | 整個詞 |
| `saved_rule_run` | action 欄位、錯誤碼與支援日誌事件 | `saved_result` | 整個詞 |
| `synthetic_saved_rule_run` | action 欄位、錯誤碼與支援日誌事件 | `synthetic_saved_result` | 整個詞 |
| `validationRunId` | action 欄位、錯誤碼與支援日誌事件 | `validationResultId` | 整個詞 |
| `validation_run_id_missing` | action 欄位、錯誤碼與支援日誌事件 | `validation_result_id_missing` | 整個詞 |
| `ix_target_gl_entry_account` | 資料表、索引與觸發程序 | `ix_gl_account` | 整個詞 |
| `ix_target_gl_entry_approval_date` | 資料表、索引與觸發程序 | `ix_gl_approval_date` | 整個詞 |
| `target_gl_entry` | 資料表、索引與觸發程序 | `gl` | 整個詞 |
| `target_gl_entry_archive` | 資料表、索引與觸發程序 | `gl_archive` | 整個詞 |
| `staging_gl_raw_row` | 資料表、索引與觸發程序 | `pbc_gl` | 整個詞 |
| `staging_gl_raw_row_unavailable` | 資料表、索引與觸發程序 | `pbc_gl_unavailable` | 整個詞 |
| `staging_tb_raw_row` | 資料表、索引與觸發程序 | `pbc_tb` | 整個詞 |
| `ix_target_tb_balance_account` | 資料表、索引與觸發程序 | `ix_tb_account` | 整個詞 |
| `target_tb_balance` | 資料表、索引與觸發程序 | `tb` | 整個詞 |
| `ix_target_gl_entry_doc` | 資料表、索引與觸發程序 | `ix_gl_voucher` | 整個詞 |
| `config_field_mapping_previous` | 資料表、索引與觸發程序 | `field_mapping_previous` | 整個詞 |
| `staging_authorized_preparer_raw_row` | 資料表、索引與觸發程序 | `pbc_authorized_preparer` | 整個詞 |
| `target_authorized_preparer` | 資料表、索引與觸發程序 | `authorized_preparer` | 整個詞 |
| `audit_event_log` | 資料表、索引與觸發程序 | `operation_log` | 整個詞 |
| `audit_log` | 資料表、索引與觸發程序 | `operation_log` | 整個詞 |
| `ix_audit_event_log_occurred` | 資料表、索引與觸發程序 | `ix_operation_log_occurred` | 整個詞 |
| `trg_audit_event_log_append_only` | 資料表、索引與觸發程序 | `trg_operation_log_append_only` | 整個詞 |
| `trg_audit_event_log_no_delete` | 資料表、索引與觸發程序 | `trg_operation_log_no_delete` | 整個詞 |
| `trg_audit_event_log_no_update` | 資料表、索引與觸發程序 | `trg_operation_log_no_update` | 整個詞 |
| `config_gl_rde_field` | 資料表、索引與觸發程序 | `gl_rde_field` | 整個詞 |
| `ix_target_gl_rde_value_amount` | 資料表、索引與觸發程序 | `ix_gl_rde_value_amount` | 整個詞 |
| `ix_target_gl_rde_value_date` | 資料表、索引與觸發程序 | `ix_gl_rde_value_date` | 整個詞 |
| `ix_target_gl_rde_value_text` | 資料表、索引與觸發程序 | `ix_gl_rde_value_text` | 整個詞 |
| `target_gl_rde_value` | 資料表、索引與觸發程序 | `gl_rde_value` | 整個詞 |
| `ix_target_account_mapping_code` | 資料表、索引與觸發程序 | `ix_account_mapping_number` | 整個詞 |
| `import_field_definition` | 資料表、索引與觸發程序 | `field_info` | 整個詞 |
| `saved_field_definition` | 資料表、索引與觸發程序 | `saved_field_info` | 整個詞 |
| `config_field_mapping` | 資料表、索引與觸發程序 | `field_mapping` | 整個詞 |
| `app_message_log` | 資料表、索引與觸發程序 | `message_log` | 整個詞 |
| `config_account_taxonomy` | 資料表、索引與觸發程序 | `account_category` | 整個詞 |
| `config_account_taxonomy_path` | 資料表、索引與觸發程序 | `account_category_path` | 整個詞 |
| `staging_account_mapping_raw_row` | 資料表、索引與觸發程序 | `pbc_account_mapping` | 整個詞 |
| `target_account_mapping` | 資料表、索引與觸發程序 | `account_mapping` | 整個詞 |
| `idx_result_filter_run_entry` | 資料表、索引與觸發程序 | `idx_filter_hit_entry` | 整個詞 |
| `result_filter_run` | 資料表、索引與觸發程序 | `filter_hit` | 整個詞 |
| `result_filter_run_source` | 資料表、索引與觸發程序 | `filter_hit_source` | 整個詞 |
| `config_filter_scenario` | 資料表、索引與觸發程序 | `filter_scenario` | 整個詞 |
| `config_result_stale_state` | 資料表、索引與觸發程序 | `result_stale_state` | 整個詞 |
| `ix_target_gl_entry_post_date` | 資料表、索引與觸發程序 | `ix_gl_gl_date` | 整個詞 |
| `staging_calendar_raw_day` | 資料表、索引與觸發程序 | `calendar_day` | 整個詞 |
| `result_inf_sampling_test_sample` | 資料表、索引與觸發程序 | `inf_sample` | 整個詞 |
| `ix_result_rule_run_kind` | 資料表、索引與觸發程序 | 拿掉，不再使用 | 整個詞 |
| `result_rule_run` | 資料表、索引與觸發程序 | `validation_result`、`prescreen_result` | 整個詞 |
| `document_number` | 資料欄 | `voucher_number` | 整個詞 |
| `effective_credit_scaled` | 資料欄 | `gl_credit_scaled` | 整個詞 |
| `effective_debit_scaled` | 資料欄 | `gl_debit_scaled` | 整個詞 |
| `effective_row_count` | 資料欄 | `gl_row_count` | 整個詞 |
| `raw_row_count` | 資料欄 | `pbc_row_count` | 整個詞 |
| `source_row_count` | 資料欄 | `pbc_row_count` | 整個詞 |
| `target_credit_scaled` | 資料欄 | `stored_credit_scaled` | 整個詞 |
| `target_debit_scaled` | 資料欄 | `stored_debit_scaled` | 整個詞 |
| `target_row_count` | 資料欄 | `stored_row_count` | 整個詞 |
| `account_code` | 資料欄 | `account_number` | 整個詞 |
| `post_date` | 資料欄 | `gl_date` | 整個詞 |
| `run_id` | 資料欄 | `result_id` | 整個詞 |
| `run_kind` | 資料欄 | 拿掉，不再使用 | 整個詞 |
| `POST_DATE` | 畫面與報告用語 | `GL_DATE` | 整個詞 |
| 「分錄金額中有連續零的尾數」 | 畫面與報告用語 | 「分錄金額中有連續 0 的尾數」 | 字串中出現 |
| 「自訂篩選條件」 | 第五步的分頁名稱 | 「欄位條件」（裁定 2026-11-03-05） | 字串中出現 |
| 「依欄位設定」 | 第五步自訂篩選條件分頁的區塊名稱 | 「欄位條件」的四組欄位（裁定 2026-11-03-06） | 字串中出現 |
| 「依審計目的」 | 第五步自訂篩選條件分頁的區塊名稱 | 「常用查法」，掛在各欄位底下（裁定 2026-11-03-06） | 字串中出現 |
| 「單項條件」 | 第五步常用篩選條件分頁的小標 | 拿掉，分頁只放十張卡片（裁定 2026-11-03-20） | 字串中出現 |
| 「存下的分錄數字不一致」 | 畫面與報告用語 | 「存入筆數與合計不一致」 | 字串中出現 |
| 「存下的分錄無法核對」 | 畫面與報告用語 | 改名後的「存入筆數與合計無法核對」也依裁定 2026-11-02-06 拿掉 | 字串中出現 |
| 「額外欄位」 | 畫面與報告用語 | 「攸關資料元素欄位」 | 字串中出現 |
| 「傳票分錄條件」 | 畫面與報告用語 | 「整張傳票條件」 | 字串中出現 |
| 「傳票條件成立」 | 畫面與報告用語 | 「整張傳票條件成立」 | 字串中出現 |
| 「條件括號」 | 畫面與報告用語 | 「子條件組」 | 字串中出現 |
| 「空值紀錄測試」 | 畫面與報告用語 | 「資料缺漏輔助檢視」 | 字串中出現 |
| 「來源品質」 | 畫面與報告用語 | 「總帳日期空白」 | 字串中出現 |
| 「INF 抽樣測試」 | 畫面與報告用語 | 「資料可靠性測試」 | 字串中出現 |
| 「低頻」 | 畫面與報告用語 | 「較少使用」 | 字串中出現 |
| `Workpaper` | 程式與註解的字根 | `WorkingPaper` | 字串中出現 |
| `workpaper` | 程式與註解的字根 | `workingPaper` | 字串中出現 |
| `Taxonomy` | 程式與註解的字根 | `Category` | 字串中出現 |
| `taxonomy` | 程式與註解的字根 | `category` | 字串中出現 |
| 「有效母體」 | 程式與註解的字根 | 「GL」；說明範圍時寫「納入測試的分錄」 | 字串中出現 |
| 「空值紀錄」 | 程式與註解的字根 | 「資料缺漏輔助檢視」 | 字串中出現 |
| `TagMatrix` | 程式與註解的字根 | `HighRiskSummary` | 字串中出現 |
| 「高風險條件矩陣」 | 程式與註解的字根 | 「高風險條件彙總」 | 字串中出現 |
| 「篩選條件矩陣」 | 程式與註解的字根 | 「高風險條件彙總」 | 字串中出現 |
| `documentDateReuse` | action 欄位與驗證摘要 | `voucherNumberAcrossGlDates` | 字串中出現 |
| `DocumentDateReuse` | 程式 | `VoucherNumberAcrossGlDates` | 字串中出現 |
| `documentNumberCount` | action 欄位與驗證摘要 | `voucherNumberCount` | 整個詞 |
| `DocumentNumberCount` | 程式 | `VoucherNumberCount` | 整個詞 |
| `nullAccountCount` | action 欄位與驗證摘要 | `blankAccountNumberCount` | 整個詞 |
| `nullDocumentCount` | action 欄位與驗證摘要 | `blankVoucherNumberCount` | 整個詞 |
| `nullDescriptionCount` | action 欄位與驗證摘要 | `blankDescriptionCount` | 整個詞 |
| `nullRows` | action 欄位與驗證摘要 | `blankRows` | 整個詞 |
| `nullAccount` | action 欄位 | `blankAccountNumber` | 整個詞 |
| `nullDocument` | action 欄位 | `blankVoucherNumber` | 整個詞 |
| `nullDescription` | action 欄位 | `blankDescription` | 整個詞 |
| `NullAccountCount` | 程式 | `BlankAccountNumberCount` | 整個詞 |
| `NullDocumentCount` | 程式 | `BlankVoucherNumberCount` | 整個詞 |
| `NullDescriptionCount` | 程式 | `BlankDescriptionCount` | 整個詞 |
| `NullAccount` | 程式 | `BlankAccountNumber` | 整個詞 |
| `NullDocument` | 程式 | `BlankVoucherNumber` | 整個詞 |
| `NullDescription` | 程式 | `BlankDescription` | 整個詞 |
| `NullRowIssues` | 程式 | `BlankRowIssues` | 整個詞 |
| `GetBlankDocumentHitRowCountsAsync` | 程式 | `GetBlankVoucherNumberHitRowCountsAsync` | 整個詞 |
| `ReadBlankDocumentRowCountsAsync` | 程式 | `ReadBlankVoucherNumberRowCountsAsync` | 整個詞 |
| `blankDocumentRows` | 程式 | `blankVoucherNumberRows` | 整個詞 |
| `blankDocumentHitRows` | 程式 | `blankVoucherNumberHitRows` | 整個詞 |
| `EmitNullDetailFromPlanAsync` | 程式 | `EmitBlankDetailFromPlanAsync` | 整個詞 |
| `ReadNullDetailAsync` | 程式 | `ReadBlankDetailAsync` | 整個詞 |
| `nullDetail` | 程式 | `blankDetail` | 整個詞 |
| `ValidationNullDetailMaximumRows` | 程式 | `ValidationBlankDetailMaximumRows` | 整個詞 |
| `FailNullSearchOnceId` | 程式 | `FailBlankSearchOnceId` | 整個詞 |
| `DecorateNullSearch` | 程式 | `DecorateBlankSearch` | 整個詞 |
| `FailNullSearchHandler` | 程式 | `FailBlankSearchHandler` | 整個詞 |
| `fail-null-search-once` | 程式 | `fail-blank-search-once` | 整個詞 |
| `nullPageSpec` | 前端 | `blankPageSpec` | 整個詞 |
| `nullSubItem` | 前端 | `blankSubItem` | 整個詞 |
| `NULL_PRESCREEN_ITEMS` | 前端 | `BLANK_PRESCREEN_ITEMS` | 整個詞 |
| `rule_not_object` | 錯誤碼 | `condition_not_object` | 整個詞 |
| `rule_type_missing` | 錯誤碼 | `condition_type_missing` | 整個詞 |
| `rule_type_unsupported` | 錯誤碼 | `condition_type_unsupported` | 整個詞 |
| `rule_join_invalid` | 錯誤碼 | `condition_join_invalid` | 整個詞 |
| `rule_no` | 支援日誌欄位 | `condition_no` | 整個詞 |
| `RuleAtom` | 程式 | `ConditionAtom` | 整個詞 |
| `effectiveRuleJoin` | 前端 | `effectiveConditionJoin` | 整個詞 |
| `hasMixedEffectiveRuleJoins` | 前端 | `hasMixedEffectiveConditionJoins` | 整個詞 |
| `FILTER_PURPOSE_CHECK_GROUPS` | 前端 | `FIELD_SHORTCUT_GROUPS`（裁定 2026-11-03-30） | 整個詞 |
| `findPurposeCheck` | 前端 | `findFieldShortcut`（裁定 2026-11-03-30） | 整個詞 |
| `addPurposeCheck` | 前端 | `addFieldShortcut`（裁定 2026-11-03-30） | 整個詞 |
| `data-add-check` | 前端 | `data-add-shortcut`（裁定 2026-11-03-30） | 整個詞 |
| `EffectiveRuleJoin` | 程式 | `EffectiveConditionJoin` | 整個詞 |
| `HasMixedEffectiveRuleJoins` | 程式 | `HasMixedEffectiveConditionJoins` | 整個詞 |
| `no_target_data` | 錯誤碼 | `gl_mapping_not_confirmed` | 整個詞 |
| `NoTargetData` | 程式 | `GlMappingNotConfirmed` | 整個詞 |
| `HasCurrentValidationRun` | 程式 | `HasCurrentValidationResult` | 整個詞 |
| `ToValidationRunSummary` | 程式 | `ToValidationResultSummary` | 整個詞 |
| `ToPrescreenRunSummary` | 程式 | `ToPrescreenResultSummary` | 整個詞 |
| `RequireRequestedValidationRun` | 程式 | `RequireRequestedValidationResult` | 整個詞 |
