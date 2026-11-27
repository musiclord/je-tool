# 2026-11-27 後端改名紀錄

使用者 2026-11-27 同意整理後端程式的名稱，畫面文字、報告與底稿的工作表名與欄名、action 名稱，以及存在案件資料庫與設定檔裡的值都不改
（裁定 2026-11-27-05 到 2026-11-27-11）。這份紀錄列出這次改了名稱的檔案與換名的規則，讓讀到較早的裁定、計畫或測試修改紀錄的人，
找得到現在的檔案。

取名的規則寫在[命名規則](../naming-conventions.md)，每個概念現在的名稱寫在[名詞表](../glossary.md)，
修改既有測試的原因與第一次失敗的情形寫在[測試修改紀錄](test-change-records.md)。裁定總表「落實位置」欄的檔案路徑已經換成新檔名，
說明欄裡記錄當時做法的舊檔名照原文保留，要對照時查這份紀錄。

## 名稱裡的工作表序號

後端程式原本用底稿工作表的序號取名，這次照名詞表「底稿工作表的程式名稱」換成工作表的內容（裁定 2026-11-27-08）：

| 原本名稱裡的序號 | 換成 |
|:---|:---|
| `Step1` | `Completeness` |
| `Step11` | `VoucherBalance` |
| `Step12` | `PreparerSummary` |
| `Step13` | `CompletenessDifference` |
| `Step2` | `InfSampling` |
| `Step3` | `HighRiskSummary` |
| `Step4` | `HighRiskVoucher` |
| `Step41` | `HighRiskVoucherDetail` |
| `Step5` | `PostClosingEntries` |

例如 `WorkingPaperSheetCatalog.Step13` 改成 `WorkingPaperSheetCatalog.CompletenessDifference`，`Step13LockedArea` 改成
`CompletenessDifferenceLockedArea`，`IWorkingPaperStep41PreparedSessionFactory` 改成 `IWorkingPaperHighRiskVoucherDetailPreparedSessionFactory`，
暫存表 `jet_step41_hit_voucher` 與 `jet_step41_row_tag` 改成 `jet_high_risk_voucher_detail_hit_voucher` 與 `jet_high_risk_voucher_detail_row_tag`。
測試方法也一樣，例如 `Step41_DateCellThatIsNotADate_IsWrittenAsTheStoredText` 改成 `HighRiskVoucherDetail_DateCellThatIsNotADate_IsWrittenAsTheStoredText`。
常數的值仍是範本上的工作表名稱，例如 `CompletenessDifference` 的值是「step1-3 完整性測試之差異說明」。

## 產品程式的檔案

| 原本的檔名 | 現在的檔名 | 依據 |
|:---|:---|:---|
| `src/JET/JET/AuditCore/FilterProgram.cs` | `src/JET/JET/AuditCore/JetAuditProgram.Filter.cs` | 裁定 2026-11-27-07 |
| `src/JET/JET/AuditCore/IntakeMappingProgram.cs` | `src/JET/JET/AuditCore/JetAuditProgram.IntakeMapping.cs` | 裁定 2026-11-27-07 |
| `src/JET/JET/AuditCore/PrescreenProgram.cs` | `src/JET/JET/AuditCore/JetAuditProgram.Prescreen.cs` | 裁定 2026-11-27-07 |
| `src/JET/JET/AuditCore/PrescreenReportProgram.cs` | `src/JET/JET/AuditCore/JetAuditProgram.PrescreenReport.cs` | 裁定 2026-11-27-07 |
| `src/JET/JET/AuditCore/ReportExportProgram.cs` | `src/JET/JET/AuditCore/JetAuditProgram.ReportExport.cs` | 裁定 2026-11-27-07 |
| `src/JET/JET/AuditCore/ValidationReportProgram.cs` | `src/JET/JET/AuditCore/JetAuditProgram.ValidationReport.cs` | 裁定 2026-11-27-07 |
| `src/JET/JET/AuditCore/WorkingPaperProgram.cs` | `src/JET/JET/AuditCore/JetAuditProgram.WorkingPaper.cs` | 裁定 2026-11-27-07 |
| `src/JET/JET/AuditCore/WorkingPaperStep4.cs` | `src/JET/JET/AuditCore/WorkingPaperHighRiskVoucher.cs` | 裁定 2026-11-27-08 |
| `src/JET/JET/AuditCore/WorkingPaperStep41.cs` | `src/JET/JET/AuditCore/WorkingPaperHighRiskVoucherDetail.cs` | 裁定 2026-11-27-08 |
| `src/JET/JET/Infrastructure/Export/WorkingPaperWriter.Step1.cs` | `src/JET/JET/Infrastructure/Export/WorkingPaperWriter.Completeness.cs` | 裁定 2026-11-27-08 |
| `src/JET/JET/Infrastructure/Export/WorkingPaperWriter.Step2To41.cs` | `src/JET/JET/Infrastructure/Export/WorkingPaperWriter.InfSamplingAndHighRisk.cs` | 裁定 2026-11-27-08 |
| `src/JET/JET/Infrastructure/Sql/WorkingPaperStep41PreparedSession.cs` | `src/JET/JET/Infrastructure/Sql/WorkingPaperHighRiskVoucherDetailPreparedSession.cs` | 裁定 2026-11-27-08 |
| `src/JET/JET/Infrastructure/Sql/WorkingPaperStep4StreamReader.cs` | `src/JET/JET/Infrastructure/Sql/WorkingPaperHighRiskVoucherStreamReader.cs` | 裁定 2026-11-27-08 |
| `src/JET/JET/Infrastructure/Persistence/Local/LocalUnbalancedVoucherPageRepository.cs` | `src/JET/JET/Infrastructure/Persistence/Local/LocalUnbalancedVoucherEntryStreamRepository.cs` | 裁定 2026-11-27-11 |

`JetAuditProgram` 的 7 個檔只改檔名，類別沒有拆開。總入口另外有 3 個檔以檔裡主要的類別命名，名稱不變：
`ValidationProcedures.cs`、`CompletenessEligibility.cs`、`FieldInfoProjection.cs`。

案件資料庫不設結構版本之後（裁定 2026-11-27-01），`src/JET/JET/Infrastructure/Persistence/Local/ProjectSchemaVersions.cs` 拿掉，
比對與補齊結構的程式寫在新的 `src/JET/JET/Infrastructure/Persistence/Sqlite/ProjectSchema.cs`。
畫面步驟的列舉寫在新的 `src/JET/JET/Domain/WorkflowStep.cs`（裁定 2026-11-27-11）。

## 測試的檔案

下面的測試類別和檔名一起改，依據是裁定 2026-11-27-09；名稱裡有工作表序號的另照裁定 2026-11-27-08。

| 原本的檔名 | 現在的檔名 |
|:---|:---|
| `Application/FilterAcceptanceJourneyTests.cs` | `Application/AdvancedFilterJourneyTests.cs` |
| `Application/FilterAcceptanceReportLifecycleTests.cs` | `Application/FilterReportLifecycleTests.cs` |
| `Application/FilterSinglePurposeRetirementTests.cs` | `Application/RetiredFilterConditionTypeTests.cs` |
| `Application/WorkingPaperStep41FixedAnswerTests.cs` | `Application/WorkingPaperHighRiskVoucherDetailFixedAnswerTests.cs` |
| `Architecture/AuditCoreDataCommonMigrationTests.cs` | `Architecture/AuditCoreDataAccessBoundaryTests.cs` |
| `Architecture/ScreenWordingReviewFrontendTests.cs` | `Architecture/ScreenWordingFrontendTests.cs` |
| `Domain/AcceptanceFeedbackDebitCreditTests.cs` | `Domain/DrCrCodeValidationTests.cs` |
| `Domain/FilterSinglePurposeListLimitTests.cs` | `Domain/FilterConditionListLimitTests.cs` |
| `Infrastructure/FilterSinglePurposeEquivalenceTests.cs` | `Infrastructure/FilterConditionEquivalenceTests.cs` |
| `Infrastructure/FilterSinglePurposeSideEquivalenceTests.cs` | `Infrastructure/FilterSideConditionEquivalenceTests.cs` |
| `Infrastructure/FilterSinglePurposeVoucherEquivalenceTests.cs` | `Infrastructure/FilterVoucherConditionEquivalenceTests.cs` |
| `Infrastructure/SchemaV7MigrationTests.cs` | `Infrastructure/NewProjectDatabaseContentTests.cs` |
| `Infrastructure/WorkingPaperStep41PreparedExportSessionTests.cs` | `Infrastructure/WorkingPaperHighRiskVoucherDetailPreparedExportSessionTests.cs` |
| `Infrastructure/WorkingPaperStep41RowPageRepositoryTests.cs` | `Infrastructure/WorkingPaperHighRiskVoucherDetailRowPageRepositoryTests.cs` |
| `Infrastructure/WorkingPaperStep4StreamRepositoryTests.cs` | `Infrastructure/WorkingPaperHighRiskVoucherStreamRepositoryTests.cs` |
| `Infrastructure/WorkingPaperWriterStep1ToStep2Tests.cs` | `Infrastructure/WorkingPaperWriterValidationSheetsTests.cs` |
| `Infrastructure/WorkingPaperWriterStep3Step4Tests.cs` | `Infrastructure/WorkingPaperWriterHighRiskSheetsTests.cs` |

上表的路徑都在 `src/JET/tests/JET.Tests/` 底下。測試固定檔 `src/JET/tests/JET.Tests/Fixtures/project-schema-v21.sql` 改名為
`project-schema-first-release.sql`，內容不變（裁定 2026-11-27-11）。`Infrastructure/ProjectSchemaUpgradeTests.cs` 與
`Infrastructure/LegacySchemaRejectionTests.cs` 隨資料庫版本一起拿掉，核對結構比對的測試寫在新的
`Infrastructure/ProjectSchemaCompatibilityTests.cs`（裁定 2026-11-27-01）。`Architecture/ProjectStorageRootFrontendTests.cs`
只檢查已刪程式沒有回來，照裁定 2026-11-27-10 刪掉。新加的命名檢查寫在 `Architecture/IdentifierNamingTests.cs`（裁定 2026-11-27-05）。

測試方法的改名沒有逐一列在這裡。改名的原因寫在測試修改紀錄 2026-11-27 的各列，名詞表各列寫的是現在的名稱。
2026-11-26 改名的六個測試類別，例如 `ProjectStorageDirectCutoverTests` 改成 `ProjectStorageRootTests`，見名詞表「C# 測試的名稱」。
