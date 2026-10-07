using JET.Application;
using JET.AuditCore;
using JET.Bridge;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace JET;

public static partial class AppCompositionRoot
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
        // 盡量不依賴 master：AssumeDatabaseExists=true 時所有存在性/就緒檢查跳過 master
        // （庫由 DBA 預建,供 jetapp 無 master 權限的鎖定環境）。預設 false（沿用「開啟即建庫」）。
        var assumeDatabaseExists = bool.TryParse(config["Sql:AssumeDatabaseExists"], out var assume) && assume;
        var sqlServerConnectionOptions =
            new SqlServerConnectionOptions(sqlServerConnString, singleDatabaseName, assumeDatabaseExists);
        var sqlServerDatabase = new SqlServerProjectDatabase(sqlServerConnectionOptions);
        // dbo.app_config 跨專案系統設定 store，與其他 dbo 管理表一起建表。
        // 專案租約鎖的心跳（project.load 讀）與逾時（SqlServerLockService 取鎖讀）參數存此，缺鍵時回程式常數。
        var appConfigStore = new SqlServerAppConfigStore(sqlServerConnectionOptions);

        // 專案工作鎖：SQL Server 維持租約表＋心跳；SQLite／DuckDB 共用同一個跨程序檔案鎖實例。
        // 本地鎖 handle 由 runtime 擁有，關窗／測試 host dispose 時一定釋放。
        var localFileLockService = new LocalFileLockService(folder);
        var sqlServerLockService = new SqlServerLockService(sqlServerConnectionOptions, appConfigStore);
        // 刪案互斥只對本地 provider 取同一把檔案鎖；SQL Server 維持既有刪案交易內清租約，
        // 不把本地 hardening 擴張成線上 maintenance lease／principal／fencing 變更。
        var sqlServerDeletionLockService = new NoOpProjectDeletionLockService();

        // 線上專案登記簿只屬 sqlServer，不在資料庫組裡；持有同一組單庫連線設定。
        // 連線未設定的環境不在組裝期爆炸(惰性)——失敗留到使用時(list 降級、create/load 明確錯誤)。
        var projectRegistry = new SqlServerProjectRegistry(sqlServerConnectionOptions);
        IProjectRegistry projectListRegistry = projectRegistry;
        // project.list 的持鎖者清單只存在 SQL Server 租約表（本機檔案鎖不列清單），直接用 SQL Server 的鎖服務。
        ILockService projectListLockService = sqlServerLockService;
#if JET_AGENT_GUI_TEST
        if (agentGuiTestFixtures is not null)
        {
            (projectListRegistry, projectListLockService) =
                agentGuiTestFixtures.SelectProjectListDependencies(projectRegistry, sqlServerLockService);
        }
#endif
        // 操作人員使用執行主機的 Windows 帳號，不接受前端 operatorId 覆寫。
        // 保留網域或機器名稱，避免不同主機的同名本機帳號被合併；目前不另設企業認證前置。
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

        // 啟動健康檢查（非阻斷）：SQL Server 已設定（base 連線字串非空）時，連一次 master
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
            var session = new ProjectSession();
            // dispatcher 與 concurrent query 的條件式補算共用同一個非阻塞的作業互斥鎖。
            var actionExecutionGate = new ActionExecutionGate();

            // 每種資料庫各組一次整組 repository，全程只有三組。建案與載入依案件的資料庫種類從 catalog 選組，
            // 連同案件編號存進 session；其他動作在一開始取 session 的快照，只用快照裡那一組。刪除非作用中案件時
            // 依該案件自己的 project.json 從 catalog 選組，不碰 session。
            var repositoryCatalog = CreateProjectRepositoryCatalog(
                new ProjectRepositoryAssembly(
                    projectStore,
                    projectRegistry,
                    new CaseCreateBackendPort(sqliteDatabase, sqlServerDatabase, duckDbDatabase),
                    localFileLockService,
                    sqlServerLockService,
                    sqlServerDeletionLockService,
                    baseReportArtifactStore,
                    loggerFactory,
                    session,
                    actionExecutionGate),
                sqliteDatabase,
                sqlServerDatabase,
                duckDbDatabase);
            // 科目配對範本(空白範本供審計員填分類)寫出器：不分資料庫，範本列由資料庫組的 AccountMappingExport 讀出。
            var accountMappingTemplateWriter = new AccountMappingTemplateWriter();

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
                    session);
            IApplicationActionHandler projectSaveProgressHandler =
                new ProjectSaveProgressHandler(projectStore, session);
            IApplicationActionHandler queryDataPreviewHandler =
                new QueryDataPreviewHandler(projectStore, session);
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
                projectStore, repositoryCatalog, currentPrincipal, session),
            new ProjectUpdateHandler(projectStore, projectRegistry, session),
            new ProjectLoadHandler(
                projectStore, repositoryCatalog, projectRegistry, appConfigStore, currentPrincipal, session,
                duckDbDatabase),
            new AccountTaxonomySaveHandler(session),
            new QueryAccountMappingPageHandler(session),
            new AccountMappingSaveHandler(session),
            new ProjectDeleteHandler(
                projectStore, repositoryCatalog, projectRegistry, currentPrincipal, session),
            new ProjectDeletePreviewHandler(projectStore, repositoryCatalog, projectRegistry, currentPrincipal),
            new ProjectHeartbeatHandler(currentPrincipal, session),
            new ProjectReleaseLockHandler(
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
            new ImportGlFromFileHandler(fileReader, projectStore, session, events, loggerFactory.CreateLogger<ImportGlFromFileHandler>()),
            new ImportTbFromFileHandler(fileReader, projectStore, session, events, loggerFactory.CreateLogger<ImportTbFromFileHandler>()),
            new ImportAccountMappingHandler(fileReader, session, loggerFactory.CreateLogger<ImportAccountMappingHandler>()),
            new ImportAuthorizedPreparerFromFileHandler(fileReader, session),
            new ClearAuthorizedPreparerHandler(session),
            new ImportInspectFileHandler(fileReader, loggerFactory.CreateLogger<ImportInspectFileHandler>()),
            new ImportPreviewFileHandler(fileReader),
            new ImportHolidayHandler(projectStore, projectRegistry, session),
            new ImportMakeupDayHandler(projectStore, projectRegistry, session),
            new ImportHolidayFromFileHandler(fileReader, projectStore, projectRegistry, session),
            new ImportMakeupDayFromFileHandler(fileReader, projectStore, projectRegistry, session),
            calendarSetNonWorkingDaysHandler,
            new MappingRestoreDraftHandler(mappingMetadataReader, mappingRestoreAuthorizations, session),
            new MappingValueProfileHandler(session),
            new MappingCommitGlHandler(mappingRestoreAuthorizations, projectStore, session, events, loggerFactory.CreateLogger<MappingCommitGlHandler>()),
            new MappingCommitTbHandler(projectStore, session, events, loggerFactory.CreateLogger<MappingCommitTbHandler>()),
            new ValidateRunHandler(projectStore, session, loggerFactory.CreateLogger<ValidateRunHandler>()),
            new PrescreenRunHandler(projectStore, session, loggerFactory.CreateLogger<PrescreenRunHandler>()),
            new FilterPreviewHandler(projectStore, session),
            new FilterCommitHandler(projectStore, session, loggerFactory.CreateLogger<FilterCommitHandler>()),
            projectSaveProgressHandler,
            queryDataPreviewHandler,
            new QueryCompletenessDiffPageHandler(projectStore, session),
            new QueryAccountMappingBlankPageHandler(session),
            new QueryAccountMappingDifferencePageHandler(session),
            new QueryDocBalancePageHandler(projectStore, session),
            new QueryNullRecordsPageHandler(projectStore, session),
            new QuerySourceQualityPageHandler(session),
            new QueryFilterHitsPageHandler(projectStore, session),
            new QueryFilterVoucherPageHandler(session),
            new QueryFilterVoucherRowsPageHandler(session),
            new QueryPrescreenPageHandler(projectStore, session),
            new QueryInfSamplePageHandler(projectStore, session),
            new QueryTagMatrixScenariosHandler(projectStore, session),
            new QueryTagMatrixVoucherPageHandler(projectStore, session),
            new QueryTagMatrixRowPageHandler(projectStore, session),
            new ExportWorkpaperStreamHandler(projectStore, session, events),
            new ExportValidationArtifactsHandler(projectStore, session, events),
            new ExportPrescreenReportHandler(projectStore, session, events),
            new ExportCriteriaSelectionReportHandler(projectStore, session, events),
            // 帳戶對應範本是給審計員填寫的工作檔，直接寫進案件資料夾，不經報告 store。
            new ExportAccountMappingTemplateHandler(
                accountMappingTemplateWriter, projectStore, folder, session, events),
            new ExportCalendarTemplatesHandler(new CalendarTemplateSource(), folder, session),
            new LogAppendHandler(session),
            new LogRecentHandler(session),
            new SupportLogExportHandler(supportDiagnostic, folder),
            new HostSelectFileHandler(hostShell, hostDialogProjectContext),
            new HostSelectFilesHandler(hostShell, hostDialogProjectContext),
            new HostOpenFolderHandler(hostShell, folder, session),
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

            // 開發者工具 action 只在 Debug 組建註冊；Release 呼叫 dev.db.* 得到 unknown action
            if (enableDevTools)
            {
                handlers.Add(new DevDbOverviewHandler(projectStore, session));
                handlers.Add(new DevDbTableDataHandler(session));
                // dev.db.reconcile：開發用的資料庫漂移檢查，比對單庫 schema、專案登錄與本機資料夾。
                handlers.Add(new DevDbReconcileHandler(
                    new SqlServerControlPlaneReconciler(sqlServerConnectionOptions, projectStore)));
                // dev.log.exportFile：單鍵把完整診斷日誌篩成目前案件後，直接寫入該案件目錄。
                // sink 不可讀時退回同一程序的 ring buffer；不另選路徑，也不退到 %LOCALAPPDATA%。
                handlers.Add(new DevLogExportFileHandler(
                    diagnostic!,
                    diagnosticFile?.FilePath,
                    folder));
            }

            // 引擎錯誤映射：先辨識 SQLite／DuckDB 強型樣，再辨識 SqlException；
            // Bridge 只收 delegate，不依賴 Infrastructure，未知例外維持 bridge_error。
            var dispatcher = new ActionDispatcher(
                handlers, loggerFactory.CreateLogger<ActionDispatcher>(), session,
                exception => LocalEngineErrors.TryTranslate(exception)
                    ?? SqlServerEngineErrors.TryTranslate(exception),
                cancellationRegistry,
                actionExecutionGate,
                // DuckDB 案件在一次操作期間保持資料庫開啟；SQLite 案件的資料夾沒有 jet.duckdb，自然不持有。
                duckDbDatabase);
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
        // 合格化 Windows 帳號(網域\帳號)：共用單庫時避免不同機器的同名本機帳號被誤併，
        // 且與 SQL Server SUSER_SNAME() 回傳的形狀一致，日後改用 AD 整合時不必再遷移身分格式。
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
