using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// 一種資料庫的整組資料存取物件。程式啟動時每種資料庫各組一次，全程只有三組；建案或載入時依
/// project.json 的資料庫種類選定一組，和案件編號一起存進 <see cref="ProjectSession"/>。
/// repository 不保存案件狀態，案件編號仍是每次呼叫的參數，所以同一組可以服務同種資料庫的任何案件。
/// 屬性型別只用 Domain、AuditCore 介面與 Application 的組合服務；實際物件由 composition root 組裝。
/// </summary>
/// <remarks>
/// 這是 record 只為了讓測試能用 <c>with</c> 換掉少數幾個屬性；相等比較刻意改回參考相等，
/// 兩組內容相同的集合仍是不同的組。
/// </remarks>
internal sealed record ProjectRepositories
{
    /// <summary>這組對應的資料庫種類（與 project.json 的 databaseProvider 同值）。</summary>
    public required string Provider { get; init; }

    // 案件生命週期
    public required IProjectDatabaseInitializer DatabaseInitializer { get; init; }
    public required IProjectDatabaseDeleter DatabaseDeleter { get; init; }
    public required ILockService LockService { get; init; }
    public required IProjectDeletionLockService DeletionLockService { get; init; }
    public required IProjectCreateFactsPort ProjectCreateFacts { get; init; }

    // 匯入、欄位配對與參考資料
    public required IImportRepository Imports { get; init; }
    public required IGlRepository Gl { get; init; }
    public required ITbRepository Tb { get; init; }
    public required IMappingStateStore MappingStates { get; init; }
    public required IMappingValueProfileRepository MappingValueProfiles { get; init; }
    public required IAccountCategoryStore AccountCategories { get; init; }
    public required IResultStaleStateStore ResultStaleStates { get; init; }
    public required ICalendarStore Calendar { get; init; }
    public required IAccountMappingStore AccountMappings { get; init; }
    public required IAccountMappingImportPersistence AccountMappingImport { get; init; }
    public required IAuthorizedPreparerStore AuthorizedPreparers { get; init; }
    public required IAuthorizedPreparerImportPersistence AuthorizedPreparerImport { get; init; }
    public required IAccountMappingEditorRepository AccountMappingEditor { get; init; }
    public required IIntakeFactsPort IntakeFacts { get; init; }
    public required IMappingFactsPort MappingFacts { get; init; }
    public required IReferenceDataFactsPort ReferenceDataFacts { get; init; }

    // 驗證、預篩選與篩選
    public required IResultStore Results { get; init; }
    public required IValidationFactsPort ValidationFacts { get; init; }
    public required IValidationReportPlanningFactsPort ValidationReportPlanningFacts { get; init; }
    public required IFieldInfoFactsPort FieldInfoFacts { get; init; }
    public required IPrescreenFactsPort PrescreenFacts { get; init; }
    public required IPrescreenReportPlanningFactsPort PrescreenReportPlanningFacts { get; init; }
    public required IFilterHitRepository FilterHits { get; init; }
    public required IFilterScenarioStore FilterScenarios { get; init; }
    public required IFilterVoucherRepository FilterVouchers { get; init; }
    public required IFilterHitMaterializer FilterHitMaterializer { get; init; }
    public required IFilterCommitRepository FilterCommits { get; init; }
    public required IFilterFactsPort FilterFacts { get; init; }
    public required FilterHitMaterializeService FilterHitMaterializeService { get; init; }
    public required FilterVoucherQueryService FilterVoucherQueryService { get; init; }

    // 查詢分頁
    public required IDataPreviewRepository DataPreview { get; init; }
    public required ICompletenessDiffPageRepository CompletenessDiffPages { get; init; }
    public required ICompletenessAccountPageRepository CompletenessAccountPages { get; init; }
    public required IAccountMappingBlankPageRepository AccountMappingBlankPages { get; init; }
    public required IAccountMappingDifferenceRepository AccountMappingDifferences { get; init; }
    public required IVoucherBalancePageRepository VoucherBalancePages { get; init; }
    public required IUnbalancedVoucherPageRepository UnbalancedVoucherPages { get; init; }
    public required IBlankRecordsPageRepository BlankRecordsPages { get; init; }
    public required ISourceQualityPageRepository SourceQualityPages { get; init; }
    public required IFilterHitsPageRepository FilterHitsPages { get; init; }
    public required IPrescreenPageRepository PrescreenPages { get; init; }
    public required IInfSamplePageRepository InfSamplePages { get; init; }
    public required IResultPageRdeValuesPort ResultPageRdeValues { get; init; }
    public required IHighRiskSummaryScenariosRepository HighRiskSummaryScenarios { get; init; }
    public required IHighRiskSummaryVoucherPageRepository HighRiskSummaryVoucherPages { get; init; }
    public required IHighRiskSummaryRowPageRepository HighRiskSummaryRowPages { get; init; }

    // 匯出
    public required IRawGlExportRepository RawGlExport { get; init; }
    public required IPreparerSummaryExportRepository PreparerSummaryExport { get; init; }
    public required IAccountUsageExportRepository AccountUsageExport { get; init; }
    public required ICalendarExportRepository CalendarExport { get; init; }
    public required IAccountMappingExportRepository AccountMappingExport { get; init; }
    public required IWorkingPaperPlanningFactsPort WorkingPaperPlanningFacts { get; init; }
    public required IWorkingPaperPlanWriter WorkingPaperPlanWriter { get; init; }
    public required IValidationReportWriter ValidationReportWriter { get; init; }
    public required IInfReportWriter InfReportWriter { get; init; }
    public required IPrescreenReportWriter PrescreenReportWriter { get; init; }
    public required ICriteriaSelectionReportWriter CriteriaSelectionReportWriter { get; init; }

    /// <summary>報告清單：這組自己的稽核紀錄裝飾同一個案件外 base store。</summary>
    public required IReportStore ReportStore { get; init; }

    // 紀錄與開發工具
    public required IMessageLogStore MessageLog { get; init; }
    public required IProjectOperationLog ProjectOperationLog { get; init; }
    public required IDevDatabaseInspector DevDatabaseInspector { get; init; }

    public bool Equals(ProjectRepositories? other) => ReferenceEquals(this, other);

    public override int GetHashCode() =>
        System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    public override string ToString() => $"ProjectRepositories({Provider})";
}
