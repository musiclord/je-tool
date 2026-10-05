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
        ICaseCreateBackendPort CaseCreateBackend,
        LocalFileLockService LocalFileLock,
        ILockService SqlServerLock,
        IProjectDeletionLockService SqlServerDeletionLock,
        IReportArtifactStore BaseReportArtifactStore,
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
        var accountTaxonomy = new LocalAccountTaxonomyStore(database);
        var resultStaleStates = new LocalResultStaleStateStore(database);
        var calendar = new LocalCalendarStore(database);
        var accountMappings = new LocalAccountMappingRepository(database);
        var authorizedPreparers = new LocalAuthorizedPreparerRepository(database);
        var fieldDefinitionFacts = new LocalFieldDefinitionFactsPort(database);
        var filterRuns = new LocalFilterRunRepository(database, loggers.CreateLogger<LocalFilterRunRepository>());
        var filterScenarios = new LocalFilterScenarioStore(database);
        var filterVouchers = new LocalFilterVoucherRepository(database);
        var filterRunMaterializer = new LocalFilterRunMaterializer(
            database, loggers.CreateLogger<LocalFilterRunMaterializer>());
        var filterCommits = new LocalFilterCommitRepository(
            database, loggers.CreateLogger<LocalFilterCommitRepository>());
        var completenessDiffPages = new LocalCompletenessDiffPageRepository(database);
        var completenessAccountPages = new LocalCompletenessAccountPageRepository(database);
        var docBalancePages = new LocalDocBalancePageRepository(database);
        var unbalancedGlEntryPages = new LocalUnbalancedGlEntryPageRepository(database);
        var nullRecordsPages = new LocalNullRecordsPageRepository(database);
        var sourceQualityPages = new LocalSourceQualityPageRepository(database);
        var prescreenPages = new LocalPrescreenPageRepository(database);
        var infSamplePages = new LocalInfSamplePageRepository(database);
        var resultPageRdeValues = new LocalResultPageRdeValuesPort(database);
        var rawGlExport = new LocalRawGlExportRepository(database);
        var creatorSummaryExport = new LocalCreatorSummaryExportRepository(database);
        var accountUsageExport = new LocalAccountUsageExportRepository(database);
        var calendarExport = new LocalCalendarExportRepository(database);
        var accountMappingExport = new LocalAccountMappingExportRepository(database);
        var tagMatrixScenarios = new LocalTagMatrixScenariosRepository(database);
        var tagMatrixVoucherPages = new LocalTagMatrixVoucherPageRepository(database);
        var tagMatrixRowPages = new LocalTagMatrixRowPageRepository(database);
        // 本機案件寫操作紀錄失敗不讓已完成的作業失敗；報告清單與 handler 共用同一個包裝。
        IProjectAuditLog projectAuditLog = new BestEffortProjectAuditLog(
            new LocalProjectAuditLog(database),
            loggers.CreateLogger<BestEffortProjectAuditLog>());
        var legacyReportWriter = CreateLegacyReportWriter(
            completenessAccountPages, completenessDiffPages, unbalancedGlEntryPages, nullRecordsPages,
            infSamplePages, prescreenPages, rawGlExport, imports, mappingStates, creatorSummaryExport,
            accountUsageExport, tagMatrixScenarios, tagMatrixRowPages, fieldDefinitionFacts,
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
            CaseCreateFacts = new CaseCreateFactsPort(
                assembly.ProjectStore,
                database,
                assembly.Registry,
                assembly.CaseCreateBackend,
                assembly.LocalFileLock),
            Imports = imports,
            Gl = gl,
            Tb = tb,
            MappingStates = mappingStates,
            MappingValueProfiles = new LocalMappingValueProfileRepository(database),
            AccountTaxonomy = accountTaxonomy,
            ResultStaleStates = resultStaleStates,
            Calendar = calendar,
            AccountMappings = accountMappings,
            AccountMappingImport = accountMappings,
            AuthorizedPreparers = authorizedPreparers,
            AuthorizedPreparerImport = authorizedPreparers,
            AccountMappingEditor = new AccountMappingEditorRepository(database),
            IntakeFacts = new IntakeFactsPort(imports),
            MappingFacts = new MappingFactsPort(gl, tb),
            ReferenceDataFacts = new ReferenceDataFactsPort(accountMappings, authorizedPreparers, calendar),
            RuleRuns = new LocalRuleRunStore(database),
            ValidationFacts = new LocalValidationRunRepository(
                database, loggers.CreateLogger<LocalValidationRunRepository>()),
            ValidationReportPlanningFacts = new LocalValidationReportPlanningFactsPort(database),
            FieldDefinitionFacts = fieldDefinitionFacts,
            PrescreenFacts = new LocalPrescreenRunRepository(
                database, loggers.CreateLogger<LocalPrescreenRunRepository>()),
            PrescreenReportPlanningFacts = new PrescreenReportPlanningFactsPort(prescreenPages),
            FilterRuns = filterRuns,
            FilterScenarios = filterScenarios,
            FilterVouchers = filterVouchers,
            FilterRunMaterializer = filterRunMaterializer,
            FilterCommits = filterCommits,
            FilterFacts = new FilterFactsPort(filterRuns, filterCommits),
            FilterRunMaterializeService = CreateFilterRunMaterializeService(
                filterRunMaterializer, filterScenarios, mappingStates, accountMappings,
                authorizedPreparers, accountTaxonomy, resultStaleStates, assembly),
            FilterVoucherQueryService = CreateFilterVoucherQueryService(
                filterVouchers, filterScenarios, mappingStates, accountMappings,
                authorizedPreparers, accountTaxonomy, resultPageRdeValues, assembly),
            DataPreview = new LocalDataPreviewRepository(database),
            CompletenessDiffPages = completenessDiffPages,
            CompletenessAccountPages = completenessAccountPages,
            AccountMappingBlankPages = new LocalAccountMappingBlankPageRepository(database),
            DocBalancePages = docBalancePages,
            UnbalancedGlEntryPages = unbalancedGlEntryPages,
            NullRecordsPages = nullRecordsPages,
            SourceQualityPages = sourceQualityPages,
            FilterHitsPages = new LocalFilterHitsPageRepository(database),
            PrescreenPages = prescreenPages,
            InfSamplePages = infSamplePages,
            ResultPageRdeValues = resultPageRdeValues,
            TagMatrixScenarios = tagMatrixScenarios,
            TagMatrixVoucherPages = tagMatrixVoucherPages,
            TagMatrixRowPages = tagMatrixRowPages,
            RawGlExport = rawGlExport,
            CreatorSummaryExport = creatorSummaryExport,
            AccountUsageExport = accountUsageExport,
            CalendarExport = calendarExport,
            AccountMappingExport = accountMappingExport,
            WorkpaperPlanningFacts = new WorkpaperPlanningFactsPort(
                completenessDiffPages, docBalancePages, tagMatrixScenarios, fieldDefinitionFacts, mappingStates),
            WorkpaperPlanWriter = CreateWorkpaperWriter(
                completenessAccountPages, completenessDiffPages, docBalancePages, creatorSummaryExport,
                infSamplePages, filterScenarios, tagMatrixScenarios, tagMatrixVoucherPages, tagMatrixRowPages,
                mappingStates, calendarExport, accountMappingExport, accountMappings, rawGlExport,
                resultPageRdeValues),
            ValidationReportWriter = legacyReportWriter,
            InfReportWriter = legacyReportWriter,
            PrescreenReportWriter = legacyReportWriter,
            CriteriaSelectionReportWriter = legacyReportWriter,
            ReportArtifactStore = new AuditedReportArtifactStore(assembly.BaseReportArtifactStore, projectAuditLog),
            MessageLog = new LocalMessageLogStore(database),
            ProjectAuditLog = projectAuditLog,
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
        var accountTaxonomy = new SqlServerAccountTaxonomyStore(database);
        var resultStaleStates = new SqlServerResultStaleStateStore(database);
        var calendar = new SqlServerCalendarStore(database);
        var accountMappings = new SqlServerAccountMappingRepository(database);
        var authorizedPreparers = new SqlServerAuthorizedPreparerRepository(database);
        var fieldDefinitionFacts = new SqlServerFieldDefinitionFactsPort(database);
        var filterRuns = new SqlServerFilterRunRepository(
            database, loggers.CreateLogger<SqlServerFilterRunRepository>());
        var filterScenarios = new SqlServerFilterScenarioStore(database);
        var filterVouchers = new SqlServerFilterVoucherRepository(database);
        var filterRunMaterializer = new SqlServerFilterRunMaterializer(
            database, loggers.CreateLogger<SqlServerFilterRunMaterializer>());
        var filterCommits = new SqlServerFilterCommitRepository(
            database, loggers.CreateLogger<SqlServerFilterCommitRepository>());
        var completenessDiffPages = new SqlServerCompletenessDiffPageRepository(database);
        var completenessAccountPages = new SqlServerCompletenessAccountPageRepository(database);
        var docBalancePages = new SqlServerDocBalancePageRepository(database);
        var unbalancedGlEntryPages = new SqlServerUnbalancedGlEntryPageRepository(database);
        var nullRecordsPages = new SqlServerNullRecordsPageRepository(database);
        var sourceQualityPages = new SqlServerSourceQualityPageRepository(database);
        var prescreenPages = new SqlServerPrescreenPageRepository(database);
        var infSamplePages = new SqlServerInfSamplePageRepository(database);
        var resultPageRdeValues = new SqlServerResultPageRdeValuesPort(database);
        var rawGlExport = new SqlServerRawGlExportRepository(database);
        var creatorSummaryExport = new SqlServerCreatorSummaryExportRepository(database);
        var accountUsageExport = new SqlServerAccountUsageExportRepository(database);
        var calendarExport = new SqlServerCalendarExportRepository(database);
        var accountMappingExport = new SqlServerAccountMappingExportRepository(database);
        var tagMatrixScenarios = new SqlServerTagMatrixScenariosRepository(database);
        var tagMatrixVoucherPages = new SqlServerTagMatrixVoucherPageRepository(database);
        var tagMatrixRowPages = new SqlServerTagMatrixRowPageRepository(database);
        var projectAuditLog = new SqlServerProjectAuditLog(database);
        var legacyReportWriter = CreateLegacyReportWriter(
            completenessAccountPages, completenessDiffPages, unbalancedGlEntryPages, nullRecordsPages,
            infSamplePages, prescreenPages, rawGlExport, imports, mappingStates, creatorSummaryExport,
            accountUsageExport, tagMatrixScenarios, tagMatrixRowPages, fieldDefinitionFacts,
            resultPageRdeValues, sourceQualityPages);

        return new ProjectRepositories
        {
            Provider = ProjectDocument.SqlServerDatabaseProvider,
            DatabaseInitializer = database,
            DatabaseDeleter = database,
            LockService = assembly.SqlServerLock,
            // SQL Server 刪案的原子交易本身會清租約，刪案鎖維持 no-op。
            DeletionLockService = assembly.SqlServerDeletionLock,
            CaseCreateFacts = new CaseCreateFactsPort(
                assembly.ProjectStore,
                database,
                assembly.Registry,
                assembly.CaseCreateBackend,
                assembly.SqlServerLock),
            Imports = imports,
            Gl = gl,
            Tb = tb,
            MappingStates = mappingStates,
            MappingValueProfiles = new SqlServerMappingValueProfileRepository(database),
            AccountTaxonomy = accountTaxonomy,
            ResultStaleStates = resultStaleStates,
            Calendar = calendar,
            AccountMappings = accountMappings,
            AccountMappingImport = accountMappings,
            AuthorizedPreparers = authorizedPreparers,
            AuthorizedPreparerImport = authorizedPreparers,
            AccountMappingEditor = new AccountMappingEditorRepository(database),
            IntakeFacts = new IntakeFactsPort(imports),
            MappingFacts = new MappingFactsPort(gl, tb),
            ReferenceDataFacts = new ReferenceDataFactsPort(accountMappings, authorizedPreparers, calendar),
            RuleRuns = new SqlServerRuleRunStore(database),
            ValidationFacts = new SqlServerValidationRunRepository(
                database, loggers.CreateLogger<SqlServerValidationRunRepository>()),
            ValidationReportPlanningFacts = new SqlServerValidationReportPlanningFactsPort(database),
            FieldDefinitionFacts = fieldDefinitionFacts,
            PrescreenFacts = new SqlServerPrescreenRunRepository(
                database, loggers.CreateLogger<SqlServerPrescreenRunRepository>()),
            PrescreenReportPlanningFacts = new PrescreenReportPlanningFactsPort(prescreenPages),
            FilterRuns = filterRuns,
            FilterScenarios = filterScenarios,
            FilterVouchers = filterVouchers,
            FilterRunMaterializer = filterRunMaterializer,
            FilterCommits = filterCommits,
            FilterFacts = new FilterFactsPort(filterRuns, filterCommits),
            FilterRunMaterializeService = CreateFilterRunMaterializeService(
                filterRunMaterializer, filterScenarios, mappingStates, accountMappings,
                authorizedPreparers, accountTaxonomy, resultStaleStates, assembly),
            FilterVoucherQueryService = CreateFilterVoucherQueryService(
                filterVouchers, filterScenarios, mappingStates, accountMappings,
                authorizedPreparers, accountTaxonomy, resultPageRdeValues, assembly),
            DataPreview = new SqlServerDataPreviewRepository(database),
            CompletenessDiffPages = completenessDiffPages,
            CompletenessAccountPages = completenessAccountPages,
            AccountMappingBlankPages = new SqlServerAccountMappingBlankPageRepository(database),
            DocBalancePages = docBalancePages,
            UnbalancedGlEntryPages = unbalancedGlEntryPages,
            NullRecordsPages = nullRecordsPages,
            SourceQualityPages = sourceQualityPages,
            FilterHitsPages = new SqlServerFilterHitsPageRepository(database),
            PrescreenPages = prescreenPages,
            InfSamplePages = infSamplePages,
            ResultPageRdeValues = resultPageRdeValues,
            TagMatrixScenarios = tagMatrixScenarios,
            TagMatrixVoucherPages = tagMatrixVoucherPages,
            TagMatrixRowPages = tagMatrixRowPages,
            RawGlExport = rawGlExport,
            CreatorSummaryExport = creatorSummaryExport,
            AccountUsageExport = accountUsageExport,
            CalendarExport = calendarExport,
            AccountMappingExport = accountMappingExport,
            WorkpaperPlanningFacts = new WorkpaperPlanningFactsPort(
                completenessDiffPages, docBalancePages, tagMatrixScenarios, fieldDefinitionFacts, mappingStates),
            WorkpaperPlanWriter = CreateWorkpaperWriter(
                completenessAccountPages, completenessDiffPages, docBalancePages, creatorSummaryExport,
                infSamplePages, filterScenarios, tagMatrixScenarios, tagMatrixVoucherPages, tagMatrixRowPages,
                mappingStates, calendarExport, accountMappingExport, accountMappings, rawGlExport,
                resultPageRdeValues),
            ValidationReportWriter = legacyReportWriter,
            InfReportWriter = legacyReportWriter,
            PrescreenReportWriter = legacyReportWriter,
            CriteriaSelectionReportWriter = legacyReportWriter,
            ReportArtifactStore = new AuditedReportArtifactStore(assembly.BaseReportArtifactStore, projectAuditLog),
            MessageLog = new SqlServerMessageLogStore(database),
            ProjectAuditLog = projectAuditLog,
            DevDatabaseInspector = new SqlServerDevDatabaseInspector(database),
        };
    }

    private static FilterRunMaterializeService CreateFilterRunMaterializeService(
        IFilterRunMaterializer materializer,
        IFilterScenarioStore filterScenarios,
        IMappingStateStore mappingStates,
        IAccountMappingStore accountMappings,
        IAuthorizedPreparerStore authorizedPreparers,
        IAccountTaxonomyStore accountTaxonomy,
        IResultStaleStateStore resultStaleStates,
        ProjectRepositoryAssembly assembly) =>
        new(
            materializer,
            assembly.ProjectStore,
            filterScenarios,
            mappingStates,
            assembly.ActionExecutionGate,
            assembly.Session,
            accountMappings,
            authorizedPreparers,
            accountTaxonomy,
            resultStaleStates);

    private static FilterVoucherQueryService CreateFilterVoucherQueryService(
        IFilterVoucherRepository filterVouchers,
        IFilterScenarioStore filterScenarios,
        IMappingStateStore mappingStates,
        IAccountMappingStore accountMappings,
        IAuthorizedPreparerStore authorizedPreparers,
        IAccountTaxonomyStore accountTaxonomy,
        IResultPageRdeValuesPort resultPageRdeValues,
        ProjectRepositoryAssembly assembly) =>
        new(
            filterVouchers,
            filterScenarios,
            mappingStates,
            accountMappings,
            authorizedPreparers,
            accountTaxonomy,
            assembly.ProjectStore,
            assembly.Session,
            resultPageRdeValues);

    private static WorkpaperWriter CreateWorkpaperWriter(
        ICompletenessAccountPageRepository completenessAccountPages,
        ICompletenessDiffPageRepository completenessDiffPages,
        IDocBalancePageRepository docBalancePages,
        ICreatorSummaryExportRepository creatorSummaryExport,
        IInfSamplePageRepository infSamplePages,
        IFilterScenarioStore filterScenarios,
        ITagMatrixScenariosRepository tagMatrixScenarios,
        ITagMatrixVoucherPageRepository tagMatrixVoucherPages,
        ITagMatrixRowPageRepository tagMatrixRowPages,
        IMappingStateStore mappingStates,
        ICalendarExportRepository calendarExport,
        IAccountMappingExportRepository accountMappingExport,
        IAccountMappingStore accountMappings,
        IRawGlExportRepository rawGlExport,
        IResultPageRdeValuesPort resultPageRdeValues) =>
        new(
            completenessAccountPages,
            completenessDiffPages,
            docBalancePages,
            creatorSummaryExport,
            infSamplePages,
            filterScenarios,
            tagMatrixScenarios,
            tagMatrixVoucherPages,
            tagMatrixRowPages,
            mappingStates,
            calendarExport,
            accountMappingExport,
            accountMappings,
            rawGlExport,
            resultPageRdeValues);

    private static LegacyReportWriter CreateLegacyReportWriter(
        ICompletenessAccountPageRepository completenessAccountPages,
        ICompletenessDiffPageRepository completenessDiffPages,
        IUnbalancedGlEntryPageRepository unbalancedGlEntryPages,
        INullRecordsPageRepository nullRecordsPages,
        IInfSamplePageRepository infSamplePages,
        IPrescreenPageRepository prescreenPages,
        IRawGlExportRepository rawGlExport,
        IImportRepository imports,
        IMappingStateStore mappingStates,
        ICreatorSummaryExportRepository creatorSummaryExport,
        IAccountUsageExportRepository accountUsageExport,
        ITagMatrixScenariosRepository tagMatrixScenarios,
        ITagMatrixRowPageRepository tagMatrixRowPages,
        ILegacyFieldDefinitionFactsPort fieldDefinitionFacts,
        IResultPageRdeValuesPort resultPageRdeValues,
        ISourceQualityPageRepository sourceQualityPages) =>
        new(
            completenessAccountPages,
            completenessDiffPages,
            unbalancedGlEntryPages,
            nullRecordsPages,
            infSamplePages,
            prescreenPages,
            rawGlExport,
            imports,
            mappingStates,
            creatorSummaryExport,
            accountUsageExport,
            tagMatrixScenarios,
            tagMatrixRowPages,
            fieldDefinitionFacts,
            resultPageRdeValues,
            sourceQualityPages);
}
