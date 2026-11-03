using JET.Application;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;

namespace JET;

/// <summary>
/// 三種資料庫各自的 <see cref="ProjectRepositories"/> 組裝。SQLite 與 DuckDB 共用同一個本機工廠方法；
/// 三組共用同一個本機檔案鎖、案件外的報告 base store、作業互斥鎖與 <see cref="ProjectSession"/>。
/// 組裝必須留在 composition root（namespace JET），Infrastructure 不得依賴 Application。
/// </summary>
public static partial class AppCompositionRoot
{
    /// <summary>三組資料庫共用的依賴。</summary>
    internal sealed record ProjectRepositoryAssembly(
        IProjectStore ProjectStore,
        IProjectRegistry Registry,
        IProjectCreateBackendPort ProjectCreateBackend,
        LocalFileLockService LocalFileLock,
        ILockService SqlServerLock,
        IProjectDeletionLockService SqlServerDeletionLock,
        IReportStore BaseReportStore,
        ILoggerFactory LoggerFactory,
        ProjectSession Session,
        ActionExecutionGate ActionExecutionGate);

    /// <summary>組出三組資料庫與選組用的 catalog；正式組裝與測試都經過這裡。</summary>
    internal static ProjectRepositoryCatalog CreateProjectRepositoryCatalog(
        ProjectRepositoryAssembly assembly,
        SqliteProjectDatabase sqliteDatabase,
        SqlServerProjectDatabase sqlServerDatabase,
        DuckDbProjectDatabase duckDbDatabase) =>
        new(
            CreateLocalProjectRepositories(ProjectDocument.DefaultDatabaseProvider, sqliteDatabase, assembly),
            CreateSqlServerProjectRepositories(sqlServerDatabase, assembly),
            CreateLocalProjectRepositories(ProjectDocument.DuckDbDatabaseProvider, duckDbDatabase, assembly));

    private static ProjectRepositories CreateLocalProjectRepositories(
        string provider,
        ILocalProjectDatabase database,
        ProjectRepositoryAssembly assembly)
    {
        var loggers = assembly.LoggerFactory;
        var imports = new LocalImportRepository(database, loggers.CreateLogger<LocalImportRepository>());
        var gl = new LocalGlRepository(database, loggers.CreateLogger<LocalGlRepository>());
        var tb = new LocalTbRepository(database, loggers.CreateLogger<LocalTbRepository>());
        var mappingStates = new LocalMappingStateStore(database);
        var accountCategories = new LocalAccountCategoryStore(database);
        var resultStaleStates = new LocalResultStaleStateStore(database);
        var calendar = new LocalCalendarStore(database);
        var accountMapping = new LocalAccountMappingRepository(database);
        var authorizedPreparer = new LocalAuthorizedPreparerRepository(database);
        var fieldInfoFacts = new LocalFieldInfoFactsPort(database);
        var filterHits = new LocalFilterHitRepository(database, loggers.CreateLogger<LocalFilterHitRepository>());
        var filterScenarios = new LocalFilterScenarioStore(database);
        var filterVouchers = new LocalFilterVoucherRepository(database);
        var filterHitMaterializer = new LocalFilterHitMaterializer(
            database, loggers.CreateLogger<LocalFilterHitMaterializer>());
        var filterCommits = new LocalFilterCommitRepository(
            database, loggers.CreateLogger<LocalFilterCommitRepository>());
        var completenessDiffPages = new LocalCompletenessDiffPageRepository(database);
        var completenessAccountPages = new LocalCompletenessAccountPageRepository(database);
        var voucherBalancePages = new LocalVoucherBalancePageRepository(database);
        var unbalancedGlEntryPages = new LocalUnbalancedVoucherPageRepository(database);
        var blankRecordsPages = new LocalBlankRecordsPageRepository(database);
        var sourceQualityPages = new LocalSourceQualityPageRepository(database);
        var prescreenPages = new LocalPrescreenPageRepository(database);
        var infSamplePages = new LocalInfSamplePageRepository(database);
        var resultPageRdeValues = new LocalResultPageRdeValuesPort(database);
        var rawGlExport = new LocalRawGlExportRepository(database);
        var preparerSummaryExport = new LocalPreparerSummaryExportRepository(database);
        var accountUsageExport = new LocalAccountUsageExportRepository(database);
        var calendarExport = new LocalCalendarExportRepository(database);
        var accountMappingExport = new LocalAccountMappingExportRepository(database);
        var highRiskSummaryScenarios = new LocalHighRiskSummaryScenariosRepository(database);
        var highRiskSummaryVoucherPages = new LocalHighRiskSummaryVoucherPageRepository(database);
        var highRiskSummaryRowPages = new LocalHighRiskSummaryRowPageRepository(database);
        // 本機案件寫操作紀錄失敗不讓已完成的作業失敗；報告清單與 handler 共用同一個包裝。
        IProjectOperationLog projectOperationLog = new BestEffortProjectOperationLog(
            new LocalProjectOperationLog(database),
            loggers.CreateLogger<BestEffortProjectOperationLog>());
        var legacyReportWriter = CreateLegacyReportWriter(
            completenessAccountPages, completenessDiffPages, unbalancedGlEntryPages, blankRecordsPages,
            infSamplePages, prescreenPages, rawGlExport, imports, mappingStates, preparerSummaryExport,
            accountUsageExport, highRiskSummaryScenarios, highRiskSummaryRowPages, fieldInfoFacts,
            resultPageRdeValues, sourceQualityPages);

        return new ProjectRepositories
        {
            Provider = provider,
            DatabaseInitializer = database,
            DatabaseDeleter = database,
            LockService = assembly.LocalFileLock,
            DeletionLockService = assembly.LocalFileLock,
            // 本機建案的初始化與回滾都在 backend attempt 內用 document 選定的本機資料庫，
            // 這裡的 initializer 只供 SQL Server 名稱預檢使用。
            ProjectCreateFacts = new ProjectCreateFactsPort(
                assembly.ProjectStore,
                database,
                assembly.Registry,
                assembly.ProjectCreateBackend,
                assembly.LocalFileLock),
            Imports = imports,
            Gl = gl,
            Tb = tb,
            MappingStates = mappingStates,
            MappingValueProfiles = new LocalMappingValueProfileRepository(database),
            AccountCategories = accountCategories,
            ResultStaleStates = resultStaleStates,
            Calendar = calendar,
            AccountMappings = accountMapping,
            AccountMappingImport = accountMapping,
            AuthorizedPreparers = authorizedPreparer,
            AuthorizedPreparerImport = authorizedPreparer,
            AccountMappingEditor = new AccountMappingEditorRepository(database),
            IntakeFacts = new IntakeFactsPort(imports),
            MappingFacts = new MappingFactsPort(gl, tb),
            ReferenceDataFacts = new ReferenceDataFactsPort(accountMapping, authorizedPreparer, calendar),
            Results = new LocalResultStore(database),
            ValidationFacts = new LocalValidationRunRepository(
                database, loggers.CreateLogger<LocalValidationRunRepository>()),
            ValidationReportPlanningFacts = new LocalValidationReportPlanningFactsPort(database),
            FieldInfoFacts = fieldInfoFacts,
            PrescreenFacts = new LocalPrescreenRunRepository(
                database, loggers.CreateLogger<LocalPrescreenRunRepository>()),
            PrescreenReportPlanningFacts = new PrescreenReportPlanningFactsPort(prescreenPages),
            FilterHits = filterHits,
            FilterScenarios = filterScenarios,
            FilterVouchers = filterVouchers,
            FilterHitMaterializer = filterHitMaterializer,
            FilterCommits = filterCommits,
            FilterFacts = new FilterFactsPort(filterHits, filterCommits),
            FilterHitMaterializeService = CreateFilterHitMaterializeService(
                filterHitMaterializer, filterScenarios, mappingStates, accountMapping,
                authorizedPreparer, accountCategories, resultStaleStates, assembly),
            FilterVoucherQueryService = CreateFilterVoucherQueryService(
                filterVouchers, filterScenarios, mappingStates, accountMapping,
                authorizedPreparer, accountCategories, resultPageRdeValues, assembly,
                CreateFilterHitMaterializeService(
                    filterHitMaterializer, filterScenarios, mappingStates, accountMapping,
                    authorizedPreparer, accountCategories, resultStaleStates, assembly),
                highRiskSummaryScenarios),
            DataPreview = new LocalDataPreviewRepository(database),
            CompletenessDiffPages = completenessDiffPages,
            CompletenessAccountPages = completenessAccountPages,
            AccountMappingBlankPages = new LocalAccountMappingBlankPageRepository(database),
            AccountMappingDifferences = new LocalAccountMappingDifferenceRepository(database),
            VoucherBalancePages = voucherBalancePages,
            UnbalancedVoucherPages = unbalancedGlEntryPages,
            BlankRecordsPages = blankRecordsPages,
            SourceQualityPages = sourceQualityPages,
            FilterHitsPages = new LocalFilterHitsPageRepository(database),
            PrescreenPages = prescreenPages,
            InfSamplePages = infSamplePages,
            ResultPageRdeValues = resultPageRdeValues,
            HighRiskSummaryScenarios = highRiskSummaryScenarios,
            HighRiskSummaryVoucherPages = highRiskSummaryVoucherPages,
            HighRiskSummaryRowPages = highRiskSummaryRowPages,
            RawGlExport = rawGlExport,
            PreparerSummaryExport = preparerSummaryExport,
            AccountUsageExport = accountUsageExport,
            CalendarExport = calendarExport,
            AccountMappingExport = accountMappingExport,
            WorkingPaperPlanningFacts = new WorkingPaperPlanningFactsPort(
                completenessDiffPages, voucherBalancePages, highRiskSummaryScenarios, fieldInfoFacts),
            WorkingPaperPlanWriter = CreateWorkingPaperWriter(
                completenessAccountPages, completenessDiffPages, voucherBalancePages, preparerSummaryExport,
                infSamplePages, filterScenarios, highRiskSummaryScenarios, highRiskSummaryVoucherPages, highRiskSummaryRowPages,
                mappingStates, calendarExport, accountMappingExport, accountMapping, rawGlExport,
                resultPageRdeValues),
            ValidationReportWriter = legacyReportWriter,
            InfReportWriter = legacyReportWriter,
            PrescreenReportWriter = legacyReportWriter,
            CriteriaSelectionReportWriter = legacyReportWriter,
            ReportStore = new AuditedReportStore(assembly.BaseReportStore, projectOperationLog,
                assembly.LoggerFactory.CreateLogger<AuditedReportStore>()),
            MessageLog = new LocalMessageLogStore(database),
            ProjectOperationLog = projectOperationLog,
            DevDatabaseInspector = new LocalDevDatabaseInspector(database),
        };
    }

    private static ProjectRepositories CreateSqlServerProjectRepositories(
        SqlServerProjectDatabase database,
        ProjectRepositoryAssembly assembly)
    {
        var loggers = assembly.LoggerFactory;
        var imports = new SqlServerImportRepository(database, loggers.CreateLogger<SqlServerImportRepository>());
        var gl = new SqlServerGlRepository(database, loggers.CreateLogger<SqlServerGlRepository>());
        var tb = new SqlServerTbRepository(database, loggers.CreateLogger<SqlServerTbRepository>());
        var mappingStates = new SqlServerMappingStateStore(database);
        var accountCategories = new SqlServerAccountCategoryStore(database);
        var resultStaleStates = new SqlServerResultStaleStateStore(database);
        var calendar = new SqlServerCalendarStore(database);
        var accountMapping = new SqlServerAccountMappingRepository(database);
        var authorizedPreparer = new SqlServerAuthorizedPreparerRepository(database);
        var fieldInfoFacts = new SqlServerFieldInfoFactsPort(database);
        var filterHits = new SqlServerFilterHitRepository(
            database, loggers.CreateLogger<SqlServerFilterHitRepository>());
        var filterScenarios = new SqlServerFilterScenarioStore(database);
        var filterVouchers = new SqlServerFilterVoucherRepository(database);
        var filterHitMaterializer = new SqlServerFilterHitMaterializer(
            database, loggers.CreateLogger<SqlServerFilterHitMaterializer>());
        var filterCommits = new SqlServerFilterCommitRepository(
            database, loggers.CreateLogger<SqlServerFilterCommitRepository>());
        var completenessDiffPages = new SqlServerCompletenessDiffPageRepository(database);
        var completenessAccountPages = new SqlServerCompletenessAccountPageRepository(database);
        var voucherBalancePages = new SqlServerVoucherBalancePageRepository(database);
        var unbalancedGlEntryPages = new SqlServerUnbalancedVoucherPageRepository(database);
        var blankRecordsPages = new SqlServerBlankRecordsPageRepository(database);
        var sourceQualityPages = new SqlServerSourceQualityPageRepository(database);
        var prescreenPages = new SqlServerPrescreenPageRepository(database);
        var infSamplePages = new SqlServerInfSamplePageRepository(database);
        var resultPageRdeValues = new SqlServerResultPageRdeValuesPort(database);
        var rawGlExport = new SqlServerRawGlExportRepository(database);
        var preparerSummaryExport = new SqlServerPreparerSummaryExportRepository(database);
        var accountUsageExport = new SqlServerAccountUsageExportRepository(database);
        var calendarExport = new SqlServerCalendarExportRepository(database);
        var accountMappingExport = new SqlServerAccountMappingExportRepository(database);
        var highRiskSummaryScenarios = new SqlServerHighRiskSummaryScenariosRepository(database);
        var highRiskSummaryVoucherPages = new SqlServerHighRiskSummaryVoucherPageRepository(database);
        var highRiskSummaryRowPages = new SqlServerHighRiskSummaryRowPageRepository(database);
        var projectOperationLog = new SqlServerProjectOperationLog(database);
        var legacyReportWriter = CreateLegacyReportWriter(
            completenessAccountPages, completenessDiffPages, unbalancedGlEntryPages, blankRecordsPages,
            infSamplePages, prescreenPages, rawGlExport, imports, mappingStates, preparerSummaryExport,
            accountUsageExport, highRiskSummaryScenarios, highRiskSummaryRowPages, fieldInfoFacts,
            resultPageRdeValues, sourceQualityPages);

        return new ProjectRepositories
        {
            Provider = ProjectDocument.SqlServerDatabaseProvider,
            DatabaseInitializer = database,
            DatabaseDeleter = database,
            LockService = assembly.SqlServerLock,
            // SQL Server 刪案的原子交易本身會清租約，刪案鎖維持 no-op。
            DeletionLockService = assembly.SqlServerDeletionLock,
            ProjectCreateFacts = new ProjectCreateFactsPort(
                assembly.ProjectStore,
                database,
                assembly.Registry,
                assembly.ProjectCreateBackend,
                assembly.SqlServerLock),
            Imports = imports,
            Gl = gl,
            Tb = tb,
            MappingStates = mappingStates,
            MappingValueProfiles = new SqlServerMappingValueProfileRepository(database),
            AccountCategories = accountCategories,
            ResultStaleStates = resultStaleStates,
            Calendar = calendar,
            AccountMappings = accountMapping,
            AccountMappingImport = accountMapping,
            AuthorizedPreparers = authorizedPreparer,
            AuthorizedPreparerImport = authorizedPreparer,
            AccountMappingEditor = new AccountMappingEditorRepository(database),
            IntakeFacts = new IntakeFactsPort(imports),
            MappingFacts = new MappingFactsPort(gl, tb),
            ReferenceDataFacts = new ReferenceDataFactsPort(accountMapping, authorizedPreparer, calendar),
            Results = new SqlServerResultStore(database),
            ValidationFacts = new SqlServerValidationRunRepository(
                database, loggers.CreateLogger<SqlServerValidationRunRepository>()),
            ValidationReportPlanningFacts = new SqlServerValidationReportPlanningFactsPort(database),
            FieldInfoFacts = fieldInfoFacts,
            PrescreenFacts = new SqlServerPrescreenRunRepository(
                database, loggers.CreateLogger<SqlServerPrescreenRunRepository>()),
            PrescreenReportPlanningFacts = new PrescreenReportPlanningFactsPort(prescreenPages),
            FilterHits = filterHits,
            FilterScenarios = filterScenarios,
            FilterVouchers = filterVouchers,
            FilterHitMaterializer = filterHitMaterializer,
            FilterCommits = filterCommits,
            FilterFacts = new FilterFactsPort(filterHits, filterCommits),
            FilterHitMaterializeService = CreateFilterHitMaterializeService(
                filterHitMaterializer, filterScenarios, mappingStates, accountMapping,
                authorizedPreparer, accountCategories, resultStaleStates, assembly),
            FilterVoucherQueryService = CreateFilterVoucherQueryService(
                filterVouchers, filterScenarios, mappingStates, accountMapping,
                authorizedPreparer, accountCategories, resultPageRdeValues, assembly,
                CreateFilterHitMaterializeService(
                    filterHitMaterializer, filterScenarios, mappingStates, accountMapping,
                    authorizedPreparer, accountCategories, resultStaleStates, assembly),
                highRiskSummaryScenarios),
            DataPreview = new SqlServerDataPreviewRepository(database),
            CompletenessDiffPages = completenessDiffPages,
            CompletenessAccountPages = completenessAccountPages,
            AccountMappingBlankPages = new SqlServerAccountMappingBlankPageRepository(database),
            AccountMappingDifferences = new SqlServerAccountMappingDifferenceRepository(database),
            VoucherBalancePages = voucherBalancePages,
            UnbalancedVoucherPages = unbalancedGlEntryPages,
            BlankRecordsPages = blankRecordsPages,
            SourceQualityPages = sourceQualityPages,
            FilterHitsPages = new SqlServerFilterHitsPageRepository(database),
            PrescreenPages = prescreenPages,
            InfSamplePages = infSamplePages,
            ResultPageRdeValues = resultPageRdeValues,
            HighRiskSummaryScenarios = highRiskSummaryScenarios,
            HighRiskSummaryVoucherPages = highRiskSummaryVoucherPages,
            HighRiskSummaryRowPages = highRiskSummaryRowPages,
            RawGlExport = rawGlExport,
            PreparerSummaryExport = preparerSummaryExport,
            AccountUsageExport = accountUsageExport,
            CalendarExport = calendarExport,
            AccountMappingExport = accountMappingExport,
            WorkingPaperPlanningFacts = new WorkingPaperPlanningFactsPort(
                completenessDiffPages, voucherBalancePages, highRiskSummaryScenarios, fieldInfoFacts),
            WorkingPaperPlanWriter = CreateWorkingPaperWriter(
                completenessAccountPages, completenessDiffPages, voucherBalancePages, preparerSummaryExport,
                infSamplePages, filterScenarios, highRiskSummaryScenarios, highRiskSummaryVoucherPages, highRiskSummaryRowPages,
                mappingStates, calendarExport, accountMappingExport, accountMapping, rawGlExport,
                resultPageRdeValues),
            ValidationReportWriter = legacyReportWriter,
            InfReportWriter = legacyReportWriter,
            PrescreenReportWriter = legacyReportWriter,
            CriteriaSelectionReportWriter = legacyReportWriter,
            ReportStore = new AuditedReportStore(assembly.BaseReportStore, projectOperationLog,
                assembly.LoggerFactory.CreateLogger<AuditedReportStore>()),
            MessageLog = new SqlServerMessageLogStore(database),
            ProjectOperationLog = projectOperationLog,
            DevDatabaseInspector = new SqlServerDevDatabaseInspector(database),
        };
    }

    private static FilterHitMaterializeService CreateFilterHitMaterializeService(
        IFilterHitMaterializer materializer,
        IFilterScenarioStore filterScenarios,
        IMappingStateStore mappingStates,
        IAccountMappingStore accountMapping,
        IAuthorizedPreparerStore authorizedPreparer,
        IAccountCategoryStore accountCategories,
        IResultStaleStateStore resultStaleStates,
        ProjectRepositoryAssembly assembly) =>
        new(
            materializer,
            assembly.ProjectStore,
            filterScenarios,
            mappingStates,
            assembly.ActionExecutionGate,
            assembly.Session,
            accountMapping,
            authorizedPreparer,
            accountCategories,
            resultStaleStates);

    private static FilterVoucherQueryService CreateFilterVoucherQueryService(
        IFilterVoucherRepository filterVouchers,
        IFilterScenarioStore filterScenarios,
        IMappingStateStore mappingStates,
        IAccountMappingStore accountMapping,
        IAuthorizedPreparerStore authorizedPreparer,
        IAccountCategoryStore accountCategories,
        IResultPageRdeValuesPort resultPageRdeValues,
        ProjectRepositoryAssembly assembly,
        FilterHitMaterializeService materializeService,
        IHighRiskSummaryScenariosRepository scenarioCounts) =>
        new(
            filterVouchers,
            filterScenarios,
            mappingStates,
            accountMapping,
            authorizedPreparer,
            accountCategories,
            assembly.ProjectStore,
            assembly.Session,
            resultPageRdeValues,
            materializeService,
            scenarioCounts);

    private static WorkingPaperWriter CreateWorkingPaperWriter(
        ICompletenessAccountPageRepository completenessAccountPages,
        ICompletenessDiffPageRepository completenessDiffPages,
        IVoucherBalancePageRepository voucherBalancePages,
        IPreparerSummaryExportRepository preparerSummaryExport,
        IInfSamplePageRepository infSamplePages,
        IFilterScenarioStore filterScenarios,
        IHighRiskSummaryScenariosRepository highRiskSummaryScenarios,
        IHighRiskSummaryVoucherPageRepository highRiskSummaryVoucherPages,
        IHighRiskSummaryRowPageRepository highRiskSummaryRowPages,
        IMappingStateStore mappingStates,
        ICalendarExportRepository calendarExport,
        IAccountMappingExportRepository accountMappingExport,
        IAccountMappingStore accountMapping,
        IRawGlExportRepository rawGlExport,
        IResultPageRdeValuesPort resultPageRdeValues) =>
        new(
            completenessAccountPages,
            completenessDiffPages,
            voucherBalancePages,
            preparerSummaryExport,
            infSamplePages,
            filterScenarios,
            highRiskSummaryScenarios,
            highRiskSummaryVoucherPages,
            highRiskSummaryRowPages,
            mappingStates,
            calendarExport,
            accountMappingExport,
            accountMapping,
            rawGlExport,
            resultPageRdeValues);

    private static LegacyReportWriter CreateLegacyReportWriter(
        ICompletenessAccountPageRepository completenessAccountPages,
        ICompletenessDiffPageRepository completenessDiffPages,
        IUnbalancedVoucherPageRepository unbalancedGlEntryPages,
        IBlankRecordsPageRepository blankRecordsPages,
        IInfSamplePageRepository infSamplePages,
        IPrescreenPageRepository prescreenPages,
        IRawGlExportRepository rawGlExport,
        IImportRepository imports,
        IMappingStateStore mappingStates,
        IPreparerSummaryExportRepository preparerSummaryExport,
        IAccountUsageExportRepository accountUsageExport,
        IHighRiskSummaryScenariosRepository highRiskSummaryScenarios,
        IHighRiskSummaryRowPageRepository highRiskSummaryRowPages,
        IFieldInfoFactsPort fieldInfoFacts,
        IResultPageRdeValuesPort resultPageRdeValues,
        ISourceQualityPageRepository sourceQualityPages) =>
        new(
            completenessAccountPages,
            completenessDiffPages,
            unbalancedGlEntryPages,
            blankRecordsPages,
            infSamplePages,
            prescreenPages,
            rawGlExport,
            imports,
            mappingStates,
            preparerSummaryExport,
            accountUsageExport,
            highRiskSummaryScenarios,
            sourceQualityPages,
            highRiskSummaryRowPages,
            fieldInfoFacts,
            resultPageRdeValues);
}
