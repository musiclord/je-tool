using JET.Application;
using JET.AuditCore;
using JET.Bridge;
using JET.Domain;
using JET.Infrastructure;
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
            diagnosticLogDirectory: GetDiagnosticLogDirectory());
    }

    /// <summary>測試可注入 temp projects root 與開發工具旗標（測試預設啟用以涵蓋 dev.db.* 契約）。</summary>
    public static JetApplicationRuntime CreateRuntime(
        IHostShell hostShell,
        string projectsRootPath,
        bool enableDevTools = true,
        IJetEventPublisher? eventPublisher = null,
        RingBufferLoggerProvider? diagnosticLoggerProvider = null,
        string? diagnosticLogDirectory = null,
        string? principalName = null,
        string? userProfileDirectory = null)
    {
#if JET_AGENT_GUI_TEST
        return CreateRuntimeCore(
            hostShell,
            projectsRootPath,
            enableDevTools,
            eventPublisher,
            diagnosticLoggerProvider,
            diagnosticLogDirectory,
            principalName,
            userProfileDirectory,
            agentGuiTestFixtures: null);
#else
        return CreateRuntimeCore(
            hostShell,
            projectsRootPath,
            enableDevTools,
            eventPublisher,
            diagnosticLoggerProvider,
            diagnosticLogDirectory,
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
            diagnosticLoggerProvider: null,
            diagnosticLogDirectory: profile.DiagnosticLogDirectory,
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
        RingBufferLoggerProvider? diagnosticLoggerProvider,
        string? diagnosticLogDirectory,
        string? principalName,
        string? userProfileDirectory
#if JET_AGENT_GUI_TEST
        , AgentGuiTestFixtures? agentGuiTestFixtures
#endif
        )
    {
        var folder = new JetProjectFolder(projectsRootPath);
        var projectStore = new JsonFileProjectStore(folder);
        // 目前只支援 SQLite 案件（裁定 2026-11-06-08），每個案件資料夾一個 jet.db。
        var sqliteDatabase = new SqliteProjectDatabase(folder);

        // 專案工作鎖是跨程序檔案鎖。鎖 handle 由 runtime 擁有，關窗／測試 host dispose 時一定釋放。
        var localFileLockService = new LocalFileLockService(folder);

        // 操作人員使用執行主機的 Windows 帳號，不接受前端 operatorId 覆寫。
        // 保留網域或機器名稱，避免不同主機的同名本機帳號被合併；目前不另設企業認證前置。
        // 測試以 principalName 覆寫成隔離身分。
        var currentPrincipal = new CurrentPrincipal(principalName ?? QualifiedWindowsName());

        // Release-safe 支援日誌永遠註冊，只收 Information+ allowlist 並在寫入 buffer 前去識別；Debug
        // 另掛原有完整 ring/file sink。Release minimum level=Information，不格式化 sql/tx 的 Debug 內容。
        // 屬於案件的事件另外附加到案件資料夾的支援紀錄，跨次開啟仍匯出得到（裁定 2026-10-21-08）。
        var supportCaseHistory = new SupportCaseHistoryStore(folder);
        var supportDiagnostic = new SupportRingBufferLoggerProvider(SupportLogCapacity, supportCaseHistory);
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
        var baseReportStore = new ProjectReportStore(
            folder,
            logger: loggerFactory.CreateLogger<ProjectReportStore>());
        var session = new ProjectSession();
        // LoggerFactory.Create 不會釋放以執行個體加入的 provider；支援日誌最後才釋放，先寫完排隊中的案件紀錄，
        // 包括放開工作鎖的那一筆。結束 JET 時先關閉目前案件的資料庫連線，再放開案件鎖。
        var runtimeResources = new RuntimeOwnedResources(
            supportDiagnostic,
            loggerFactory,
            localFileLockService,
            new ProjectDatabaseShutdown(session, sqliteDatabase, loggerFactory.CreateLogger<ProjectDatabaseShutdown>()));

        JetApplicationRuntime? runtime = null;
        try
        {
            // dispatcher 與 concurrent query 的條件式補算共用同一個非阻塞的作業互斥鎖。
            var actionExecutionGate = new ActionExecutionGate();

            // SQLite 的整組 repository 只組一次。建案與載入依案件的資料庫種類從 catalog 選組，連同案件編號存進 session；
            // 其他動作在一開始取 session 的快照，只用快照裡那一組。刪除非作用中案件時依該案件自己的 project.json
            // 從 catalog 選組，不碰 session。catalog 只認得 SQLite，其他種類回 unsupported_provider。
            var repositoryCatalog = CreateProjectRepositoryCatalog(
                new ProjectRepositoryAssembly(
                    projectStore,
                    new ProjectCreateBackendPort(sqliteDatabase),
                    localFileLockService,
                    baseReportStore,
                    loggerFactory,
                    session,
                    actionExecutionGate),
                sqliteDatabase);
            // 科目配對範本(空白範本供審計員填分類)寫出器：範本列由資料庫組的 AccountMappingExport 讀出。
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
            IApplicationActionHandler projectListLocalHandler =
                new ProjectListLocalHandler(projectStore);
            IApplicationActionHandler calendarSetNonWorkingDaysHandler =
                new CalendarSetNonWorkingDaysHandler(
                    projectStore,
                    session,
                    loggerFactory.CreateLogger<CalendarSetNonWorkingDaysHandler>());
            IApplicationActionHandler projectSaveProgressHandler =
                new ProjectSaveProgressHandler(projectStore, session);
            IApplicationActionHandler queryDataPreviewHandler =
                new QueryDataPreviewHandler(projectStore, session);
#if JET_AGENT_GUI_TEST
            if (agentGuiTestFixtures is not null)
            {
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
            // 使用者身分徽章：回報合格化 Windows 帳號與短名，不連線，也不會失敗。
            new SystemWhoAmIHandler(currentPrincipal),
            projectListLocalHandler,
            new ProjectCreateHandler(
                projectStore, repositoryCatalog, currentPrincipal, session,
                supportCaseHistory, loggerFactory.CreateLogger<ProjectCreateHandler>()),
            new ProjectUpdateHandler(projectStore, session, loggerFactory.CreateLogger<ProjectUpdateHandler>()),
            new ProjectLoadHandler(
                projectStore, repositoryCatalog, currentPrincipal, session,
                loggerFactory.CreateLogger<ProjectLoadHandler>(), supportCaseHistory, sqliteDatabase),
            new AccountCategorySaveHandler(session, loggerFactory.CreateLogger<AccountCategorySaveHandler>()),
            new QueryAccountMappingPageHandler(session),
            new AccountMappingSaveHandler(session, loggerFactory.CreateLogger<AccountMappingSaveHandler>()),
            new ProjectDeleteHandler(
                projectStore, repositoryCatalog, currentPrincipal, session,
                loggerFactory.CreateLogger<ProjectDeleteHandler>(), supportCaseHistory),
            new ProjectDeletePreviewHandler(projectStore, repositoryCatalog),
            new ProjectReleaseLockHandler(
                currentPrincipal,
                session,
                actionExecutionGate,
                sqliteDatabase,
                loggerFactory.CreateLogger<ProjectReleaseLockHandler>()),
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
            new ImportAuthorizedPreparerFromFileHandler(fileReader, session, loggerFactory.CreateLogger<ImportAuthorizedPreparerFromFileHandler>()),
            new ClearAuthorizedPreparerHandler(session, loggerFactory.CreateLogger<ClearAuthorizedPreparerHandler>()),
            new ImportInspectFileHandler(fileReader, loggerFactory.CreateLogger<ImportInspectFileHandler>()),
            new ImportPreviewFileHandler(fileReader),
            new ImportHolidayHandler(projectStore, session, loggerFactory.CreateLogger<ImportHolidayHandler>()),
            new ImportMakeupDayHandler(projectStore, session, loggerFactory.CreateLogger<ImportMakeupDayHandler>()),
            new ImportHolidayFromFileHandler(fileReader, projectStore, session,
                loggerFactory.CreateLogger<ImportHolidayFromFileHandler>()),
            new ImportMakeupDayFromFileHandler(fileReader, projectStore, session,
                loggerFactory.CreateLogger<ImportMakeupDayFromFileHandler>()),
            calendarSetNonWorkingDaysHandler,
            new MappingRestoreDraftHandler(mappingMetadataReader, mappingRestoreAuthorizations, session),
            new MappingValueProfileHandler(session),
            new MappingCommitGlHandler(mappingRestoreAuthorizations, projectStore, session, events, loggerFactory.CreateLogger<MappingCommitGlHandler>()),
            new MappingCommitTbHandler(projectStore, session, events, loggerFactory.CreateLogger<MappingCommitTbHandler>()),
            new ValidateRunHandler(projectStore, session, loggerFactory.CreateLogger<ValidateRunHandler>()),
            new PrescreenRunHandler(projectStore, session, loggerFactory.CreateLogger<PrescreenRunHandler>()),
            new FilterPreviewHandler(projectStore, session),
            new FilterCommitHandler(projectStore, session, loggerFactory.CreateLogger<FilterCommitHandler>()),
            new FilterScenarioSaveHandler(projectStore, session, loggerFactory.CreateLogger<FilterScenarioSaveHandler>()),
            new FilterScenarioRemoveHandler(projectStore, session, loggerFactory.CreateLogger<FilterScenarioRemoveHandler>()),
            new FilterScenarioRecheckHandler(projectStore, session, loggerFactory.CreateLogger<FilterScenarioRecheckHandler>()),
            projectSaveProgressHandler,
            queryDataPreviewHandler,
            new QueryCompletenessDiffPageHandler(projectStore, session),
            new QueryAccountMappingBlankPageHandler(session),
            new QueryAccountMappingDifferencePageHandler(session),
            new QueryVoucherBalancePageHandler(projectStore, session),
            new QueryBlankRecordsPageHandler(projectStore, session),
            new QuerySourceQualityPageHandler(session),
            new QueryFilterHitsPageHandler(projectStore, session),
            new QueryFilterVoucherPageHandler(session),
            new QueryFilterVoucherRowsPageHandler(session),
            new QueryPrescreenPageHandler(projectStore, session),
            new QueryInfSamplePageHandler(projectStore, session),
            new QueryHighRiskSummaryScenariosHandler(projectStore, session),
            new QueryHighRiskSummaryVoucherPageHandler(projectStore, session),
            new QueryHighRiskSummaryRowPageHandler(projectStore, session),
            new ExportWorkingPaperStreamHandler(projectStore, session, events),
            new ExportValidationReportsHandler(projectStore, session, events),
            new ExportPrescreenReportHandler(projectStore, session, events),
            new ExportCriteriaSelectionReportHandler(projectStore, session, events),
            // 帳戶對應範本是給審計員填寫的工作檔，直接寫進案件資料夾，不經報告 store。
            new ExportAccountMappingTemplateHandler(
                accountMappingTemplateWriter, projectStore, folder, session, events),
            new LogAppendHandler(session),
            new LogRecentHandler(session),
            new SupportLogExportHandler(
                supportDiagnostic,
                folder,
                // 省略 projectId 時的輸出位置：%LOCALAPPDATA%\JET\logs\；測試釘 temp 目錄，不碰真的 %LOCALAPPDATA%。
                Path.Combine(userProfileDirectory ?? GetUserProfileDirectory(), "logs"),
                supportCaseHistory),
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
                    handlers[index] = agentGuiTestFixtures.DecorateBlankSearch(handlers[index]);
                    handlers[index] = agentGuiTestFixtures.DecorateAuthorizedList(handlers[index]);
                }
            }
#endif

            // 開發者工具 action 只在 Debug 組建註冊；Release 呼叫 dev.db.* 得到 unknown action
            if (enableDevTools)
            {
                handlers.Add(new DevDbOverviewHandler(projectStore, session));
                handlers.Add(new DevDbTableDataHandler(session));
                // dev.log.exportFile：單鍵把完整診斷日誌篩成目前案件後，直接寫入該案件目錄。
                // sink 不可讀時退回同一程序的 ring buffer；不另選路徑，也不退到 %LOCALAPPDATA%。
                handlers.Add(new DevLogExportFileHandler(
                    diagnostic!,
                    diagnosticFile?.FilePath,
                    folder));
            }

            // 引擎錯誤映射：辨識 SQLite 強型樣；Bridge 只收 delegate，不依賴 Infrastructure，未知例外維持 bridge_error。
            var dispatcher = new ActionDispatcher(
                handlers, loggerFactory.CreateLogger<ActionDispatcher>(), session,
                exception => LocalEngineErrors.TryTranslate(exception),
                cancellationRegistry,
                actionExecutionGate,
                // SQLite 案件在變更型作業結束後把記錄檔併回資料庫檔。
                sqliteDatabase);

            // 啟動工作在背景執行，視窗照常立即顯示。正式程式沒有啟動工作；Gui 測試組態在這裡建立測試案件。
            Func<CancellationToken, Task>? startupWork = null;
#if JET_AGENT_GUI_TEST
            if (agentGuiTestFixtures is { SeedsProjectState: true })
            {
                startupWork = cancellationToken => agentGuiTestFixtures.SeedProjectStateAsync(
                    dispatcher,
                    cancellationToken);
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
        // 使用者層級的 JET 目錄 %LOCALAPPDATA%\JET\；省略 projectId 的支援日誌寫到它底下的 logs。
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JET");
    }

    private static string QualifiedWindowsName()
    {
        // 合格化 Windows 帳號(網域\帳號)：避免不同機器的同名本機帳號被誤併，案件鎖記錄的持有人也用這個形狀。
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
