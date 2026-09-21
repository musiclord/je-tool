using JET.Application;
using JET.AuditCore;
using JET.Bridge;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace JET;

public static class AppCompositionRoot
{
    /// <summary>診斷日誌 ring buffer 容量（dev-only;滿則覆寫最舊）。</summary>
    private const int DiagnosticLogCapacity = 10_000;
    private const int SupportLogCapacity = 2_000;

    public static JetApplicationRuntime CreateRuntime(IHostShell hostShell, IJetEventPublisher? eventPublisher = null)
    {
        // 開發者工具（dev.db.*）只存在於 Debug 組建：Release 不註冊 action、
        // 前端依 system.ping.devToolsEnabled 隱藏開發面板。
#if DEBUG
        const bool enableDevTools = true;
#else
        const bool enableDevTools = false;
#endif
        var projectStorage = ProjectStoragePathResolver.ResolveEnvironment();
        return CreateRuntime(
            hostShell, projectStorage.ProjectsRootPath, enableDevTools, eventPublisher,
            diagnosticLogDirectory: GetDiagnosticLogDirectory(),
            // 正式 app：開啟時就測試 SQL Server 連線並確保單庫 JET 存在（不存在則以設定登入建立）。
            ensureDatabaseOnStartup: true);
    }

    /// <summary>測試可注入 temp projects root 與開發工具旗標（測試預設啟用以涵蓋 dev.db.* 契約）。</summary>
    public static JetApplicationRuntime CreateRuntime(
        IHostShell hostShell,
        string projectsRootPath,
        bool enableDevTools = true,
        IJetEventPublisher? eventPublisher = null,
        string? sqlServerConnectionString = null,
        RingBufferLoggerProvider? diagnosticLoggerProvider = null,
        string? diagnosticLogDirectory = null,
        string? singleDatabaseNameOverride = null,
        bool ensureDatabaseOnStartup = false,
        string? principalName = null,
        string? userProfileDirectory = null)
    {
#if JET_AGENT_GUI_TEST
        return CreateRuntimeCore(
            hostShell,
            projectsRootPath,
            enableDevTools,
            eventPublisher,
            sqlServerConnectionString,
            diagnosticLoggerProvider,
            diagnosticLogDirectory,
            singleDatabaseNameOverride,
            ensureDatabaseOnStartup,
            principalName,
            userProfileDirectory,
            agentGuiTestFixtures: null);
#else
        return CreateRuntimeCore(
            hostShell,
            projectsRootPath,
            enableDevTools,
            eventPublisher,
            sqlServerConnectionString,
            diagnosticLoggerProvider,
            diagnosticLogDirectory,
            singleDatabaseNameOverride,
            ensureDatabaseOnStartup,
            principalName,
            userProfileDirectory);
#endif
    }

#if JET_AGENT_GUI_TEST
    internal static JetApplicationRuntime CreateAgentGuiTestRuntime(
        IHostShell hostShell,
        AgentGuiTestProfile profile,
        IJetEventPublisher eventPublisher)
    {
        var fixtures = new AgentGuiTestFixtures(profile);
        var releaseVisibleSurface = profile.FixtureIds.Contains(
            AgentGuiTestFixtures.ReleaseVisibleSurfaceId,
            StringComparer.Ordinal);
        return CreateRuntimeCore(
            hostShell,
            profile.ProjectsRootPath,
            enableDevTools: !releaseVisibleSurface,
            eventPublisher: eventPublisher,
            sqlServerConnectionString: AgentGuiTestProfile.IsolatedSqlServerConnectionString,
            diagnosticLoggerProvider: null,
            diagnosticLogDirectory: profile.DiagnosticLogDirectory,
            singleDatabaseNameOverride: "JET_AGENT_GUI_TEST",
            ensureDatabaseOnStartup: false,
            principalName: AgentGuiTestProfile.IsolatedPrincipal,
            userProfileDirectory: profile.UserProfileDirectory,
            agentGuiTestFixtures: fixtures);
    }
#endif

    private static JetApplicationRuntime CreateRuntimeCore(
        IHostShell hostShell,
        string projectsRootPath,
        bool enableDevTools,
        IJetEventPublisher? eventPublisher,
        string? sqlServerConnectionString,
        RingBufferLoggerProvider? diagnosticLoggerProvider,
        string? diagnosticLogDirectory,
        string? singleDatabaseNameOverride,
        bool ensureDatabaseOnStartup,
        string? principalName,
        string? userProfileDirectory
#if JET_AGENT_GUI_TEST
        , AgentGuiTestFixtures? agentGuiTestFixtures
#endif
        )
    {
        var folder = new JetProjectFolder(projectsRootPath);
        var projectStore = new JsonFileProjectStore(folder);
        var sqliteDatabase = new SqliteProjectDatabase(folder);
        // 第二本地引擎（每專案一個 jet.duckdb；與 sqlite 同 folder、共用 Local* repository 家族）。
        var duckDbDatabase = new DuckDbProjectDatabase(folder);

        // appsettings.json 只保存非機密的資料庫名稱與啟動選項。完整連線由
        // JET_SQLSERVER_CONNECTION 提供；設定檔缺少時仍可使用本機 provider。
        IConfiguration config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        // 每個專案依 databaseProvider 使用 SQLite、DuckDB 或 SQL Server。SQL Server 測試可以直接傳入
        // 連線字串；正式程式則讀取 JET_SQLSERVER_CONNECTION。兩者都不會退回設定檔尋找憑證。
        var envOverride = sqlServerConnectionString ?? Environment.GetEnvironmentVariable("JET_SQLSERVER_CONNECTION");
        var sqlServerConnString = SqlConnectionStringFactory.Build(config, envOverride);
        // 測試固定使用隔離資料庫；正式程式從 Sql:Database 取得資料庫名稱。兩者都沒有設定時使用 JET。
        // SQL Server provider 會以這個名稱覆寫連線字串中的 InitialCatalog；本機 provider 不受影響。
        var singleDatabaseName = singleDatabaseNameOverride ?? config["Sql:Database"] ?? "JET";
        // master 依賴最小化（控制面第四輪 §4）：AssumeDatabaseExists=true 時所有存在性/就緒檢查跳過 master
        // （庫由 DBA 預建,供 jetapp 無 master 權限的鎖定環境）。預設 false（沿用「開啟即建庫」）。
        var assumeDatabaseExists = bool.TryParse(config["Sql:AssumeDatabaseExists"], out var assume) && assume;
        var sqlServerConnectionOptions =
            new SqlServerConnectionOptions(sqlServerConnString, singleDatabaseName, assumeDatabaseExists);
        var sqlServerDatabase = new SqlServerProjectDatabase(sqlServerConnectionOptions);
        var providerResolver = new ProjectProviderResolver(projectStore);
        // dbo.app_config 跨專案系統設定 store（隨控制面 bootstrap 建表,控制面第四輪 §3）：控制面第六輪的消費者
        // ——專案租約鎖的心跳（project.load 讀）／逾時（SqlServerLockService 取鎖讀）參數存此、缺鍵回程式常數。
        var appConfigStore = new SqlServerAppConfigStore(sqlServerConnectionOptions);

        // 專案工作鎖：SQL Server 維持租約表＋心跳；SQLite／DuckDB 共用同一個跨程序檔案鎖實例。
        // 本地鎖 handle 由 runtime 擁有，關窗／測試 host dispose 時一定釋放。
        var localFileLockService = new LocalFileLockService(folder);
        var lockService = new ProviderRoutingLockService(
            providerResolver,
            localFileLockService,
            new SqlServerLockService(sqlServerConnectionOptions, appConfigStore),
            localFileLockService);
        // 刪案互斥只對本地 provider 取同一把檔案鎖；SQL Server 維持既有刪案交易內清租約，
        // 不把本地 hardening 擴張成線上 maintenance lease／principal／fencing 變更。
        var deletionLockService = new ProviderRoutingProjectDeletionLockService(
            providerResolver,
            localFileLockService,
            new NoOpProjectDeletionLockService(),
            localFileLockService);

        // 線上專案登記簿(雙來源雛形 2026-07-07):天生只屬 sqlServer、不走 ProviderRouting;持有同一組單庫連線設定。
        // 連線未設定的環境不在組裝期爆炸(惰性)——失敗留到使用時(list 降級、create/load 明確錯誤)。
        var projectRegistry = new SqlServerProjectRegistry(sqlServerConnectionOptions);
        IProjectRegistry projectListRegistry = projectRegistry;
        ILockService projectListLockService = lockService;
#if JET_AGENT_GUI_TEST
        if (agentGuiTestFixtures is not null)
        {
            (projectListRegistry, projectListLockService) =
                agentGuiTestFixtures.SelectProjectListDependencies(projectRegistry, lockService);
        }
#endif
        // 當前身分:client 自報的合格化 Windows 帳號(網域\帳號),由 QualifiedWindowsName() 以 WindowsIdentity 取得
        // (spec §1;取代雛形的裸 Environment.UserName——共用單庫時避免不同機器的同名本機帳號被誤併)。
        // 測試以 principalName 覆寫成隔離身分,避免共用 JET_Test 登記簿的跨測試可見性汙染。
        var currentPrincipal = new CurrentPrincipal(principalName ?? QualifiedWindowsName());
        // 線上單庫使用者目錄(dbo.app_user):發使用者編號、記錄「誰來過」,名單授權基座;天生只屬 sqlServer,持同一組單庫連線設定。
        var userDirectory = new SqlServerUserDirectory(sqlServerConnectionOptions);
        // 使用者編號本機離線快取(user-profile.json):線上不可達時 whoAmI 據此退階;測試釘 temp 目錄,不碰真 %LOCALAPPDATA%。
        var userProfileCache = new UserProfileCache(userProfileDirectory ?? GetUserProfileDirectory());

        // Release-safe 支援日誌永遠註冊，只收 Information+ allowlist 並在寫入 buffer 前去識別；Debug
        // 另掛原有完整 ring/file sink。Release minimum level=Information，不格式化 sql/tx 的 Debug 內容。
        var supportDiagnostic = new SupportRingBufferLoggerProvider(SupportLogCapacity);
        var diagnostic = enableDevTools
            ? diagnosticLoggerProvider ?? new RingBufferLoggerProvider(DiagnosticLogCapacity)
            : null;
        // 診斷日誌檔案 sink(dev-only):與 ring buffer 並列,讓 agent 跑完 app 後直接讀 NDJSON 執行時日誌。
        // 僅在啟用 dev 工具且指定目錄時建立；Release 不建立 raw sink，另有去識別 support buffer。
        var diagnosticFile = diagnostic is not null && diagnosticLogDirectory is not null
            ? new NdjsonFileLoggerProvider(diagnosticLogDirectory)
            : null;
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(diagnostic is null ? LogLevel.Information : LogLevel.Trace);
            builder.AddProvider(supportDiagnostic);
            if (diagnostic is not null)
            {
                builder.AddProvider(diagnostic);
                if (diagnosticFile is not null)
                {
                    builder.AddProvider(diagnosticFile);
                }
            }
        });
        var baseReportArtifactStore = new ProjectReportArtifactStore(
            folder,
            logger: loggerFactory.CreateLogger<ProjectReportArtifactStore>());
        var runtimeResources = new RuntimeOwnedResources(loggerFactory, localFileLockService);

        // 啟動健康檢查（非阻斷、Task 9）：SQL Server 已設定（base 連線字串非空）時，連一次 master
        // 跑 SELECT @@VERSION, DB_NAME(), SUSER_SNAME() 並把去敏結果寫進啟動日誌。連 master（而非單庫
        // JET）避免「庫尚未建立」誤判失敗。失敗只記日誌、不丟例外、不中止 dispatcher——純 SQLite
        // 使用者（未設定 SQL Server）整段略過。ProbeAsync 已把例外收斂成去敏 HealthResult，訊息永不含密碼。
        //
        // 必須在背景執行緒 fire-and-log，不可同步等待：本方法在 Form1 建構式（UI 主執行緒、Application.Run
        // 訊息迴圈尚未啟動）被呼叫，而 Control 基底建構式此時已安裝 WindowsFormsSynchronizationContext。
        // 若在此 .GetAwaiter().GetResult() 同步等待 async 探測，ProbeAsync 的續行會被 Post 回尚未 pump 的
        // 主執行緒 → 死鎖、視窗永不顯示。背景探測讓視窗立即顯示，慢速/連不上的伺服器也不拖延啟動（非阻斷本意）。
        Func<CancellationToken, Task>? startupWork = null;
        if (!string.IsNullOrWhiteSpace(sqlServerConnString))
        {
            var startupLogger = loggerFactory.CreateLogger(typeof(SqlServerHealthCheck));
            var probeConnString = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(sqlServerConnString)
            {
                InitialCatalog = "master"
            }.ConnectionString;
            startupWork = SqlServerStartupOrchestration.Create(
                probeConnString,
                singleDatabaseName,
                ensureDatabaseOnStartup,
                startupLogger,
                SqlServerHealthCheck.ProbeAsync,
                sqlServerDatabase.EnsureDatabaseReadyAsync);
        }

        JetApplicationRuntime? runtime = null;
        try
        {
            var databaseInitializer = new ProviderRoutingProjectDatabaseInitializer(
                providerResolver, sqliteDatabase, sqlServerDatabase, duckDbDatabase);
            var databaseDeleter = new ProviderRoutingProjectDatabaseDeleter(
                providerResolver, sqliteDatabase, sqlServerDatabase, duckDbDatabase);
            var importRepository = new ProviderRoutingImportRepository(
                providerResolver,
                new LocalImportRepository(sqliteDatabase, loggerFactory.CreateLogger<LocalImportRepository>()),
                new SqlServerImportRepository(sqlServerDatabase, loggerFactory.CreateLogger<SqlServerImportRepository>()),
                new LocalImportRepository(duckDbDatabase, loggerFactory.CreateLogger<LocalImportRepository>()));
            var glRepository = new ProviderRoutingGlRepository(
                providerResolver,
                new LocalGlRepository(sqliteDatabase, loggerFactory.CreateLogger<LocalGlRepository>()),
                new SqlServerGlRepository(sqlServerDatabase, loggerFactory.CreateLogger<SqlServerGlRepository>()),
                new LocalGlRepository(duckDbDatabase, loggerFactory.CreateLogger<LocalGlRepository>()));
            var tbRepository = new ProviderRoutingTbRepository(
                providerResolver,
                new LocalTbRepository(sqliteDatabase, loggerFactory.CreateLogger<LocalTbRepository>()),
                new SqlServerTbRepository(sqlServerDatabase, loggerFactory.CreateLogger<SqlServerTbRepository>()),
                new LocalTbRepository(duckDbDatabase, loggerFactory.CreateLogger<LocalTbRepository>()));
            var mappingStore = new ProviderRoutingMappingStateStore(
                providerResolver, new LocalMappingStateStore(sqliteDatabase), new SqlServerMappingStateStore(sqlServerDatabase),
                new LocalMappingStateStore(duckDbDatabase));
            var mappingValueProfileRepository = new ProviderRoutingMappingValueProfileRepository(
                providerResolver,
                new LocalMappingValueProfileRepository(sqliteDatabase),
                new SqlServerMappingValueProfileRepository(sqlServerDatabase),
                new LocalMappingValueProfileRepository(duckDbDatabase));
            var accountTaxonomyStore = new ProviderRoutingAccountTaxonomyStore(
                providerResolver,
                new LocalAccountTaxonomyStore(sqliteDatabase),
                new SqlServerAccountTaxonomyStore(sqlServerDatabase),
                new LocalAccountTaxonomyStore(duckDbDatabase));
            var resultStaleStateStore = new ProviderRoutingResultStaleStateStore(
                providerResolver,
                new LocalResultStaleStateStore(sqliteDatabase),
                new SqlServerResultStaleStateStore(sqlServerDatabase),
                new LocalResultStaleStateStore(duckDbDatabase));
            var calendarStore = new ProviderRoutingCalendarStore(
                providerResolver, new LocalCalendarStore(sqliteDatabase), new SqlServerCalendarStore(sqlServerDatabase),
                new LocalCalendarStore(duckDbDatabase));
            var accountMappingStore = new ProviderRoutingAccountMappingRepository(
                providerResolver, new LocalAccountMappingRepository(sqliteDatabase), new SqlServerAccountMappingRepository(sqlServerDatabase),
                new LocalAccountMappingRepository(duckDbDatabase));
            var authorizedPreparerStore = new ProviderRoutingAuthorizedPreparerRepository(
                providerResolver, new LocalAuthorizedPreparerRepository(sqliteDatabase), new SqlServerAuthorizedPreparerRepository(sqlServerDatabase),
                new LocalAuthorizedPreparerRepository(duckDbDatabase));
            IIntakeFactsPort intakeFactsPort = new IntakeFactsPort(importRepository);
            IMappingFactsPort mappingFactsPort = new MappingFactsPort(glRepository, tbRepository);
            IReferenceDataFactsPort referenceDataFactsPort = new ReferenceDataFactsPort(
                accountMappingStore,
                authorizedPreparerStore,
                calendarStore);
            ICaseCreateFactsPort caseCreateFactsPort = new CaseCreateFactsPort(
                projectStore,
                databaseInitializer,
                projectRegistry,
                new ProviderRoutingCaseCreateBackendPort(
                    databaseInitializer,
                    sqliteDatabase,
                    sqlServerDatabase,
                    duckDbDatabase),
                lockService);
            var ruleRunStore = new ProviderRoutingRuleRunStore(
                providerResolver, new LocalRuleRunStore(sqliteDatabase), new SqlServerRuleRunStore(sqlServerDatabase),
                new LocalRuleRunStore(duckDbDatabase));
            IValidationFactsPort validationFactsPort = new ProviderRoutingValidationFactsPort(
                providerResolver,
                new LocalValidationRunRepository(sqliteDatabase, loggerFactory.CreateLogger<LocalValidationRunRepository>()),
                new SqlServerValidationRunRepository(sqlServerDatabase, loggerFactory.CreateLogger<SqlServerValidationRunRepository>()),
                new LocalValidationRunRepository(duckDbDatabase, loggerFactory.CreateLogger<LocalValidationRunRepository>()));
            IValidationReportPlanningFactsPort validationReportPlanningFactsPort =
                new ProviderRoutingValidationReportPlanningFactsPort(
                    providerResolver,
                    new LocalValidationReportPlanningFactsPort(sqliteDatabase),
                    new SqlServerValidationReportPlanningFactsPort(sqlServerDatabase),
                    new LocalValidationReportPlanningFactsPort(duckDbDatabase));
            ILegacyFieldDefinitionFactsPort fieldDefinitionFactsPort =
                new ProviderRoutingFieldDefinitionFactsPort(
                    providerResolver,
                    new LocalFieldDefinitionFactsPort(sqliteDatabase),
                    new SqlServerFieldDefinitionFactsPort(sqlServerDatabase),
                    new LocalFieldDefinitionFactsPort(duckDbDatabase));
            IPrescreenFactsPort prescreenFactsPort = new ProviderRoutingPrescreenFactsPort(
                providerResolver,
                new LocalPrescreenRunRepository(sqliteDatabase, loggerFactory.CreateLogger<LocalPrescreenRunRepository>()),
                new SqlServerPrescreenRunRepository(sqlServerDatabase, loggerFactory.CreateLogger<SqlServerPrescreenRunRepository>()),
                new LocalPrescreenRunRepository(duckDbDatabase, loggerFactory.CreateLogger<LocalPrescreenRunRepository>()));
            var filterRepository = new ProviderRoutingFilterRunRepository(
                providerResolver,
                new LocalFilterRunRepository(sqliteDatabase, loggerFactory.CreateLogger<LocalFilterRunRepository>()),
                new SqlServerFilterRunRepository(sqlServerDatabase, loggerFactory.CreateLogger<SqlServerFilterRunRepository>()),
                new LocalFilterRunRepository(duckDbDatabase, loggerFactory.CreateLogger<LocalFilterRunRepository>()));
            var filterScenarioStore = new ProviderRoutingFilterScenarioStore(
                providerResolver, new LocalFilterScenarioStore(sqliteDatabase), new SqlServerFilterScenarioStore(sqlServerDatabase),
                new LocalFilterScenarioStore(duckDbDatabase));
            var filterVoucherRepository = new ProviderRoutingFilterVoucherRepository(providerResolver,
                new LocalFilterVoucherRepository(sqliteDatabase), new SqlServerFilterVoucherRepository(sqlServerDatabase),
                new LocalFilterVoucherRepository(duckDbDatabase));
            var filterRunMaterializer = new ProviderRoutingFilterRunMaterializer(
                providerResolver,
                new LocalFilterRunMaterializer(sqliteDatabase, loggerFactory.CreateLogger<LocalFilterRunMaterializer>()),
                new SqlServerFilterRunMaterializer(sqlServerDatabase, loggerFactory.CreateLogger<SqlServerFilterRunMaterializer>()),
                new LocalFilterRunMaterializer(duckDbDatabase, loggerFactory.CreateLogger<LocalFilterRunMaterializer>()));
            var filterCommitRepository = new ProviderRoutingFilterCommitRepository(
                providerResolver,
                new LocalFilterCommitRepository(sqliteDatabase, loggerFactory.CreateLogger<LocalFilterCommitRepository>()),
                new SqlServerFilterCommitRepository(sqlServerDatabase, loggerFactory.CreateLogger<SqlServerFilterCommitRepository>()),
                new LocalFilterCommitRepository(duckDbDatabase, loggerFactory.CreateLogger<LocalFilterCommitRepository>()));
            IFilterFactsPort filterFactsPort =
                new FilterFactsPort(filterRepository, filterCommitRepository);
            var session = new ProjectSession();
            // dispatcher 與 concurrent query 的條件式補算共用同一個非阻塞 exclusive 閘。
            var actionExecutionGate = new ActionExecutionGate();
            // 共用「全情境落地」編排：export 已由 dispatcher 保護，四支 query 只有空結果補算分支自行取共用閘。
            var filterRunMaterializeService = new FilterRunMaterializeService(
                filterRunMaterializer,
                projectStore,
                filterScenarioStore,
                mappingStore,
                actionExecutionGate,
                session,
                accountMappingStore,
                authorizedPreparerStore,
                accountTaxonomyStore);
            var devInspector = new ProviderRoutingDevDatabaseInspector(
                providerResolver, new LocalDevDatabaseInspector(sqliteDatabase), new SqlServerDevDatabaseInspector(sqlServerDatabase),
                new LocalDevDatabaseInspector(duckDbDatabase));
            var dataPreviewRepository = new ProviderRoutingDataPreviewRepository(
                providerResolver, new LocalDataPreviewRepository(sqliteDatabase), new SqlServerDataPreviewRepository(sqlServerDatabase),
                new LocalDataPreviewRepository(duckDbDatabase));
            var completenessDiffPageRepository = new ProviderRoutingCompletenessDiffPageRepository(
                providerResolver, new LocalCompletenessDiffPageRepository(sqliteDatabase), new SqlServerCompletenessDiffPageRepository(sqlServerDatabase),
                new LocalCompletenessDiffPageRepository(duckDbDatabase));
            var accountMappingBlankPageRepository = new ProviderRoutingAccountMappingBlankPageRepository(
                providerResolver, new LocalAccountMappingBlankPageRepository(sqliteDatabase), new SqlServerAccountMappingBlankPageRepository(sqlServerDatabase),
                new LocalAccountMappingBlankPageRepository(duckDbDatabase));
            // 完整性「全科目」(含 diff=0)分頁:匯出底稿 step1 的資料源(diff repo 只回差異科目,不足以列全科目)。
            // E1 Task 3 新增;消費者(匯出 writer / handler)隨後續 task 落地。
            var completenessAccountPageRepository = new ProviderRoutingCompletenessAccountPageRepository(
                providerResolver, new LocalCompletenessAccountPageRepository(sqliteDatabase), new SqlServerCompletenessAccountPageRepository(sqlServerDatabase),
                new LocalCompletenessAccountPageRepository(duckDbDatabase));
            var docBalancePageRepository = new ProviderRoutingDocBalancePageRepository(
                providerResolver, new LocalDocBalancePageRepository(sqliteDatabase), new SqlServerDocBalancePageRepository(sqlServerDatabase),
                new LocalDocBalancePageRepository(duckDbDatabase));
            var unbalancedGlEntryPageRepository = new ProviderRoutingUnbalancedGlEntryPageRepository(
                providerResolver,
                new LocalUnbalancedGlEntryPageRepository(sqliteDatabase),
                new SqlServerUnbalancedGlEntryPageRepository(sqlServerDatabase),
                new LocalUnbalancedGlEntryPageRepository(duckDbDatabase));
            var nullRecordsPageRepository = new ProviderRoutingNullRecordsPageRepository(
                providerResolver, new LocalNullRecordsPageRepository(sqliteDatabase), new SqlServerNullRecordsPageRepository(sqlServerDatabase),
                new LocalNullRecordsPageRepository(duckDbDatabase));
            var sourceQualityPageRepository = new ProviderRoutingSourceQualityPageRepository(
                providerResolver, new LocalSourceQualityPageRepository(sqliteDatabase), new SqlServerSourceQualityPageRepository(sqlServerDatabase),
                new LocalSourceQualityPageRepository(duckDbDatabase));
            var filterHitsPageRepository = new ProviderRoutingFilterHitsPageRepository(
                providerResolver, new LocalFilterHitsPageRepository(sqliteDatabase), new SqlServerFilterHitsPageRepository(sqlServerDatabase),
                new LocalFilterHitsPageRepository(duckDbDatabase));
            var prescreenPageRepository = new ProviderRoutingPrescreenPageRepository(
                providerResolver, new LocalPrescreenPageRepository(sqliteDatabase), new SqlServerPrescreenPageRepository(sqlServerDatabase),
                new LocalPrescreenPageRepository(duckDbDatabase));
            IPrescreenReportPlanningFactsPort prescreenReportPlanningFactsPort =
                new PrescreenReportPlanningFactsPort(prescreenPageRepository);
            var infSamplePageRepository = new ProviderRoutingInfSamplePageRepository(
                providerResolver, new LocalInfSamplePageRepository(sqliteDatabase), new SqlServerInfSamplePageRepository(sqlServerDatabase),
                new LocalInfSamplePageRepository(duckDbDatabase));
            var resultPageRdeValuesPort = new ProviderRoutingResultPageRdeValuesPort(
                providerResolver,
                new LocalResultPageRdeValuesPort(sqliteDatabase),
                new SqlServerResultPageRdeValuesPort(sqlServerDatabase),
                new LocalResultPageRdeValuesPort(duckDbDatabase));
            var rawGlExportRepository = new ProviderRoutingRawGlExportRepository(
                providerResolver, new LocalRawGlExportRepository(sqliteDatabase), new SqlServerRawGlExportRepository(sqlServerDatabase),
                new LocalRawGlExportRepository(duckDbDatabase));
            // 匯出底稿 step1-2 的全編製人員查詢(不截斷)。E1 Task 1 先行註冊;消費者(匯出 handler)隨 Task 6 落地。
            var creatorSummaryExportRepository = new ProviderRoutingCreatorSummaryExportRepository(
                providerResolver, new LocalCreatorSummaryExportRepository(sqliteDatabase), new SqlServerCreatorSummaryExportRepository(sqlServerDatabase),
                new LocalCreatorSummaryExportRepository(duckDbDatabase));
            var accountUsageExportRepository = new ProviderRoutingAccountUsageExportRepository(
                providerResolver, new LocalAccountUsageExportRepository(sqliteDatabase), new SqlServerAccountUsageExportRepository(sqlServerDatabase),
                new LocalAccountUsageExportRepository(duckDbDatabase));
            // 匯出底稿三參考表(E1 Task 5)的唯讀查詢:行事曆逐日讀回 + 科目配對全列(含 Not-in-TB 旗標)。
            // 先行註冊;消費者(WorkpaperWriter 經匯出 handler)隨 Task 6 落地。
            var calendarExportRepository = new ProviderRoutingCalendarExportRepository(
                providerResolver, new LocalCalendarExportRepository(sqliteDatabase), new SqlServerCalendarExportRepository(sqlServerDatabase),
                new LocalCalendarExportRepository(duckDbDatabase));
            var accountMappingExportRepository = new ProviderRoutingAccountMappingExportRepository(
                providerResolver, new LocalAccountMappingExportRepository(sqliteDatabase), new SqlServerAccountMappingExportRepository(sqlServerDatabase),
                new LocalAccountMappingExportRepository(duckDbDatabase));
            var tagMatrixScenariosRepository = new ProviderRoutingTagMatrixScenariosRepository(
                providerResolver, new LocalTagMatrixScenariosRepository(sqliteDatabase), new SqlServerTagMatrixScenariosRepository(sqlServerDatabase),
                new LocalTagMatrixScenariosRepository(duckDbDatabase));
            var tagMatrixVoucherPageRepository = new ProviderRoutingTagMatrixVoucherPageRepository(
                providerResolver, new LocalTagMatrixVoucherPageRepository(sqliteDatabase), new SqlServerTagMatrixVoucherPageRepository(sqlServerDatabase),
                new LocalTagMatrixVoucherPageRepository(duckDbDatabase));
            var tagMatrixRowPageRepository = new ProviderRoutingTagMatrixRowPageRepository(
                providerResolver, new LocalTagMatrixRowPageRepository(sqliteDatabase), new SqlServerTagMatrixRowPageRepository(sqlServerDatabase),
                new LocalTagMatrixRowPageRepository(duckDbDatabase));
            var workpaperPlanningFactsPort = new WorkpaperPlanningFactsPort(
                completenessDiffPageRepository,
                docBalancePageRepository,
                tagMatrixScenariosRepository,
                fieldDefinitionFactsPort,
                mappingStore);
            var messageLogStore = new ProviderRoutingMessageLogStore(
                providerResolver, new LocalMessageLogStore(sqliteDatabase), new SqlServerMessageLogStore(sqlServerDatabase),
                new LocalMessageLogStore(duckDbDatabase));
            var projectAuditLog = new ProviderRoutingProjectAuditLog(
                providerResolver,
                new LocalProjectAuditLog(sqliteDatabase),
                new SqlServerProjectAuditLog(sqlServerDatabase),
                new LocalProjectAuditLog(duckDbDatabase));
            IReportArtifactStore reportArtifactStore = new AuditedReportArtifactStore(
                baseReportArtifactStore,
                projectAuditLog);

            // 匯出底稿寫出器(E1 Task 2-5;deep module):唯讀查詢 repo(step1 家族 / step2 抽樣 /
            // step3-4-1 tag 矩陣 / 三參考表)與科目配對 presence store 注入,對外只 WriteAsync。
            var workpaperWriter = new WorkpaperWriter(
                completenessAccountPageRepository,
                completenessDiffPageRepository,
                docBalancePageRepository,
                creatorSummaryExportRepository,
                infSamplePageRepository,
                filterScenarioStore,
                tagMatrixScenariosRepository,
                tagMatrixVoucherPageRepository,
                tagMatrixRowPageRepository,
                mappingStore,
                calendarExportRepository,
                accountMappingExportRepository,
                accountMappingStore,
                rawGlExportRepository,
                resultPageRdeValuesPort);

            // 科目配對範本(空白範本供審計員填分類)寫出器:消費 accountMappingExportRepository 的 GL∪TB diff。
            var accountMappingTemplateWriter = new AccountMappingTemplateWriter();
            var legacyReportWriter = new LegacyReportWriter(
                completenessAccountPageRepository,
                completenessDiffPageRepository,
                unbalancedGlEntryPageRepository,
                nullRecordsPageRepository,
                infSamplePageRepository,
                prescreenPageRepository,
                rawGlExportRepository,
                importRepository,
                mappingStore,
                creatorSummaryExportRepository,
                accountUsageExportRepository,
                tagMatrixScenariosRepository,
                tagMatrixRowPageRepository,
                fieldDefinitionFactsPort,
                resultPageRdeValuesPort,
                sourceQualityPageRepository);

            var fileReader = new CompositeTabularFileReader(new OpenXmlSaxTableReader(), new CsvTableReader(),
                new BinaryExcelTableReader(), new AccessTableReader());
            var mappingMetadataReader = new OpenXmlMappingMetadataReader();
            var mappingRestoreAuthorizations = new MappingRestoreDraftAuthorizationStore();
#if DEBUG || JET_AGENT_GUI_TEST
#if JET_AGENT_GUI_TEST
            var demoFileWriter = agentGuiTestFixtures is { SeedsProjectState: true }
                ? new DemoWorkbookWriter(agentGuiTestFixtures.DemoWorkbookRootPath)
                : new DemoWorkbookWriter();
#else
            var demoFileWriter = new DemoWorkbookWriter();
#endif
#endif
            var hostDialogProjectContext = new HostDialogProjectContext(session, folder);
            var events = eventPublisher ?? new NullEventPublisher();
#if JET_AGENT_GUI_TEST
            if (agentGuiTestFixtures is not null)
            {
                events = agentGuiTestFixtures.DecorateEventPublisher(events);
            }
#endif
            var cancellationRegistry = new RequestCancellationRegistry();
            IApplicationActionHandler projectListHandler = new ProjectListHandler(
                projectStore,
                projectListRegistry,
                projectListLockService,
                currentPrincipal);
            IApplicationActionHandler projectListLocalHandler =
                new ProjectListLocalHandler(projectStore);
            IApplicationActionHandler calendarSetNonWorkingDaysHandler =
                new CalendarSetNonWorkingDaysHandler(
                    projectStore,
                    projectRegistry,
                    referenceDataFactsPort,
                    session);
            IApplicationActionHandler projectSaveProgressHandler =
                new ProjectSaveProgressHandler(projectStore, session);
            IApplicationActionHandler queryDataPreviewHandler =
                new QueryDataPreviewHandler(dataPreviewRepository, projectStore, session);
#if JET_AGENT_GUI_TEST
            if (agentGuiTestFixtures is not null)
            {
                projectListHandler = agentGuiTestFixtures.DecorateProjectList(projectListHandler);
                projectListLocalHandler =
                    agentGuiTestFixtures.DecorateProjectListLocal(projectListLocalHandler);
                calendarSetNonWorkingDaysHandler =
                    agentGuiTestFixtures.DecorateCalendarSetNonWorkingDays(
                        calendarSetNonWorkingDaysHandler);
                projectSaveProgressHandler =
                    agentGuiTestFixtures.DecorateProjectSaveProgress(
                        projectSaveProgressHandler);
                queryDataPreviewHandler =
                    agentGuiTestFixtures.DecorateDataPreview(queryDataPreviewHandler, session);
            }
#endif

            var filterVoucherQueryService = new FilterVoucherQueryService(filterVoucherRepository, filterScenarioStore,
                mappingStore, accountMappingStore, authorizedPreparerStore, accountTaxonomyStore, projectStore, session, resultPageRdeValuesPort);
            List<IApplicationActionHandler> handlers =
            [
                // 正式契約 handlers
                new SystemPingHandler(enableDevTools),
            new OperationCancelHandler(cancellationRegistry),
            // SQL Server 後端身分（去敏）：前端啟動後查一次,在訊息面板顯示連到哪台/版本/是否 Express。
            new SystemDatabaseInfoHandler(new SqlServerBackendProbe(sqlServerConnString, singleDatabaseName)),
            // 使用者身分徽章：回報合格化帳號＋線上單庫使用者編號(冪等註冊)；線上不可達時退階本機快取,永不失敗。
            new SystemWhoAmIHandler(currentPrincipal, userDirectory, userProfileCache),
            projectListLocalHandler,
            projectListHandler,
            new ProjectCreateHandler(
                projectStore, caseCreateFactsPort, currentPrincipal, session),
            new ProjectLoadHandler(
                projectStore, importRepository, mappingStore, accountTaxonomyStore, resultStaleStateStore,
                calendarStore, accountMappingStore,
                authorizedPreparerStore, ruleRunStore, filterScenarioStore, reportArtifactStore,
                databaseInitializer, projectRegistry, lockService, appConfigStore, currentPrincipal, session),
            new AccountTaxonomySaveHandler(accountTaxonomyStore, session),
            new ProjectDeleteHandler(
                projectStore, databaseDeleter, projectRegistry,
                currentPrincipal, reportArtifactStore, deletionLockService, session),
            new ProjectHeartbeatHandler(lockService, currentPrincipal, session),
            new ProjectReleaseLockHandler(
                lockService,
                currentPrincipal,
                session,
                actionExecutionGate),
#if DEBUG || JET_AGENT_GUI_TEST
            new ProjectLoadDemoHandler(),
            new DemoExportGlFileHandler(demoFileWriter),
            new DemoExportTbFileHandler(demoFileWriter),
            new DemoExportAccountMappingFileHandler(demoFileWriter),
            new DemoExportAuthorizedPreparerFileHandler(demoFileWriter),
#endif
            new ImportGlFromFileHandler(
                fileReader, intakeFactsPort, projectStore, session, events, importRepository, projectAuditLog),
            new ImportTbFromFileHandler(
                fileReader, intakeFactsPort, projectStore, session, events, importRepository, projectAuditLog),
            new ImportAccountMappingHandler(fileReader, referenceDataFactsPort, accountTaxonomyStore, session),
            new ImportAuthorizedPreparerFromFileHandler(fileReader, referenceDataFactsPort, session),
            new ClearAuthorizedPreparerHandler(authorizedPreparerStore, session),
            new ImportInspectFileHandler(fileReader),
            new ImportPreviewFileHandler(fileReader),
            new ImportHolidayHandler(projectStore, projectRegistry, referenceDataFactsPort, session),
            new ImportMakeupDayHandler(projectStore, projectRegistry, referenceDataFactsPort, session),
            new ImportHolidayFromFileHandler(fileReader, projectStore, projectRegistry, referenceDataFactsPort, session),
            new ImportMakeupDayFromFileHandler(fileReader, projectStore, projectRegistry, referenceDataFactsPort, session),
            calendarSetNonWorkingDaysHandler,
            new MappingRestoreDraftHandler(
                mappingMetadataReader, importRepository, mappingRestoreAuthorizations, session),
            new MappingValueProfileHandler(importRepository, mappingValueProfileRepository, session),
            new MappingCommitGlHandler(
                importRepository, mappingFactsPort, mappingStore, mappingRestoreAuthorizations,
                projectStore, session, events, projectAuditLog),
            new MappingCommitTbHandler(
                importRepository, mappingFactsPort, mappingStore, projectStore, session, events, projectAuditLog),
            new ValidateRunHandler(
                validationFactsPort, sourceQualityPageRepository,
                mappingStore, ruleRunStore, projectStore, session),
            new PrescreenRunHandler(
                prescreenFactsPort, mappingStore, calendarStore, accountMappingStore, authorizedPreparerStore,
                ruleRunStore, projectStore, session),
            new FilterPreviewHandler(
                filterFactsPort, mappingStore, accountMappingStore, authorizedPreparerStore,
                accountTaxonomyStore, projectStore, session),
            new FilterCommitHandler(
                filterFactsPort, mappingStore, accountMappingStore, authorizedPreparerStore,
                accountTaxonomyStore, ruleRunStore, projectStore, session),
            projectSaveProgressHandler,
            queryDataPreviewHandler,
            new QueryCompletenessDiffPageHandler(completenessDiffPageRepository, projectStore, session),
            new QueryAccountMappingBlankPageHandler(accountMappingBlankPageRepository, session),
            new QueryDocBalancePageHandler(docBalancePageRepository, projectStore, session),
            new QueryNullRecordsPageHandler(nullRecordsPageRepository, projectStore, session),
            new QuerySourceQualityPageHandler(sourceQualityPageRepository, session),
            new QueryFilterHitsPageHandler(
                filterHitsPageRepository, filterScenarioStore, filterRunMaterializeService,
                mappingStore, resultPageRdeValuesPort, projectStore, session),
            new QueryFilterVoucherPageHandler(filterVoucherQueryService),
            new QueryFilterVoucherRowsPageHandler(filterVoucherQueryService),
            new QueryPrescreenPageHandler(prescreenPageRepository, projectStore, session),
            new QueryInfSamplePageHandler(
                infSamplePageRepository, ruleRunStore, mappingStore,
                resultPageRdeValuesPort, projectStore, session),
            new QueryTagMatrixScenariosHandler(
                tagMatrixScenariosRepository, filterScenarioStore, filterRunMaterializeService, projectStore, session),
            new QueryTagMatrixVoucherPageHandler(
                tagMatrixVoucherPageRepository, filterScenarioStore, filterRunMaterializeService, projectStore, session),
            new QueryTagMatrixRowPageHandler(
                tagMatrixRowPageRepository, filterScenarioStore, filterRunMaterializeService, projectStore, session),
            new ExportWorkpaperStreamHandler(
                workpaperWriter, workpaperPlanningFactsPort, filterScenarioStore, filterRunMaterializeService, ruleRunStore,
                resultStaleStateStore, projectStore, reportArtifactStore, session, events, mappingStore, accountTaxonomyStore),
            new ExportValidationArtifactsHandler(
                legacyReportWriter, legacyReportWriter, ruleRunStore, projectStore, reportArtifactStore, session, events,
                validationReportPlanningFactsPort, fieldDefinitionFactsPort, accountTaxonomyStore, mappingStore),
            new ExportPrescreenReportHandler(
                legacyReportWriter, ruleRunStore, projectStore, reportArtifactStore, session, events,
                prescreenReportPlanningFactsPort, mappingStore, accountTaxonomyStore),
            new ExportCriteriaSelectionReportHandler(
                legacyReportWriter, filterScenarioStore, filterRunMaterializeService,
                ruleRunStore, projectStore, reportArtifactStore, session, events, accountTaxonomyStore,
                mappingStore),
            // 帳戶對應範本是給審計員填寫的工作檔，直接寫進案件資料夾，不經報告 store。
            new ExportAccountMappingTemplateHandler(
                accountMappingExportRepository, accountMappingTemplateWriter, ruleRunStore,
                projectStore, folder, session, events, accountTaxonomyStore, mappingStore),
            new LogAppendHandler(messageLogStore, session),
            new LogRecentHandler(messageLogStore, session),
            new SupportLogExportHandler(supportDiagnostic, projectStore, folder),
            new HostSelectFileHandler(hostShell, hostDialogProjectContext),
            new HostSelectFilesHandler(hostShell, hostDialogProjectContext),
            new HostSelectSavePathHandler(hostShell, hostDialogProjectContext),
            new HostOpenFolderHandler(hostShell, reportArtifactStore, folder, session),
            new HostExitAppHandler(hostShell)
            ];

#if JET_AGENT_GUI_TEST
            if (agentGuiTestFixtures is not null)
            {
                for (var index = 0; index < handlers.Count; index++)
                {
                    handlers[index] =
                        agentGuiTestFixtures.DecorateExportAction(handlers[index]);
                    handlers[index] = agentGuiTestFixtures.DecorateNullSearch(handlers[index]);
                    handlers[index] = agentGuiTestFixtures.DecorateAuthorizedList(handlers[index]);
                }
            }
#endif

            // schema-v7 新邏輯的 production actions 在進入各自 handler facts／materialization／
            // artifact staging 前先統一檢查舊 mapping。project.load／import／mapping repair 路徑
            // 沒有被裝飾，使用者仍能 recommit 後解除 gate。
            for (var index = 0; index < handlers.Count; index++)
            {
                handlers[index] = MappingReviewPrerequisite.DecorateProductionAction(
                    handlers[index],
                    mappingStore,
                    session);
            }

            // 開發者工具 action 只在 Debug 組建註冊；Release 呼叫 dev.db.* 得到 unknown action
            if (enableDevTools)
            {
                handlers.Add(new DevDbOverviewHandler(devInspector, projectStore, session));
                handlers.Add(new DevDbTableDataHandler(devInspector, session));
                // dev.db.reconcile（控制面第四輪 §5）：單庫控制面三方對帳＋失效 provider 解析快取。
                handlers.Add(new DevDbReconcileHandler(
                    new SqlServerControlPlaneReconciler(sqlServerConnectionOptions, projectStore), providerResolver));
                handlers.Add(new DevLogExportHandler(diagnostic!));
                // dev.log.exportFile：單鍵把完整診斷日誌篩成目前案件後，直接寫入該案件目錄。
                // sink 不可讀時退回同一程序的 ring buffer；不另選路徑，也不退到 %LOCALAPPDATA%。
                handlers.Add(new DevLogExportFileHandler(
                    diagnostic!,
                    diagnosticFile?.FilePath,
                    projectStore,
                    folder));
            }

            // 引擎錯誤映射：先辨識 SQLite／DuckDB 強型樣，再辨識 SqlException；
            // Bridge 只收 delegate，不依賴 Infrastructure，未知例外維持 bridge_error。
            var dispatcher = new ActionDispatcher(
                handlers, loggerFactory.CreateLogger<ActionDispatcher>(), session,
                exception => LocalEngineErrors.TryTranslate(exception)
                    ?? SqlServerEngineErrors.TryTranslate(exception),
                cancellationRegistry,
                actionExecutionGate);
#if JET_AGENT_GUI_TEST
            if (agentGuiTestFixtures is { SeedsProjectState: true })
            {
                var platformStartupWork = startupWork;
                startupWork = async cancellationToken =>
                {
                    var seedWork = agentGuiTestFixtures.SeedProjectStateAsync(
                        dispatcher,
                        cancellationToken);
                    if (platformStartupWork is null)
                    {
                        await seedWork.ConfigureAwait(false);
                        return;
                    }

                    await Task.WhenAll(
                        platformStartupWork(cancellationToken),
                        seedWork).ConfigureAwait(false);
                };
            }
#endif
            runtime = new JetApplicationRuntime(dispatcher, runtimeResources);
            runtime.Start(startupWork);
            return runtime;
        }
        catch
        {
            if (runtime is null)
            {
                runtimeResources.Dispose();
            }
            else
            {
                runtime.Dispose();
            }

            throw;
        }
    }

    private static string GetDiagnosticLogDirectory()
    {
        // 診斷日誌檔(dev-only):每次啟動寫一檔到 %LOCALAPPDATA%\JET\logs\,供 agent 讀執行時真相。
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JET", "logs");
    }

    private static string GetUserProfileDirectory()
    {
        // 使用者編號離線快取(user-profile.json)的目錄：%LOCALAPPDATA%\JET\(比照 GetDiagnosticLogDirectory)。
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JET");
    }

    private static string QualifiedWindowsName()
    {
        // 合格化 Windows 帳號(網域\帳號):共用單庫時避免不同機器的同名本機帳號被誤併(spec §1),
        // 且與控制面 §4.1 的 AD 整合終態(SUSER_SNAME() 回傳形)一致,名單授權輪不必再遷移身分格式。
        // 受限環境取不到 token 時退回 UserDomainName\UserName——仍是合格化形狀,不退回裸名。
        try
        {
            return System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        }
        catch
        {
            return Environment.UserDomainName + "\\" + Environment.UserName;
        }
    }

    private sealed class RuntimeOwnedResources(params IDisposable[] resources) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            // 後列出的本地鎖先釋放，再關 logger；即使未來資源清單擴充也維持 stack-like ownership。
            for (var index = resources.Length - 1; index >= 0; index--)
            {
                resources[index].Dispose();
            }
        }
    }
}
