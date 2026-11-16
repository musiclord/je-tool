using JET.Application;
using JET.AuditCore;
using JET.Bridge;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;

namespace JET;

/// <summary>
/// 程式啟動時建立各層物件並把它們接在一起的地方。這裡建立案件設定檔的存取、SQLite 案件資料庫、整組 repository、
/// 日誌、全部的 action handler 與 <see cref="ActionDispatcher"/>，再包成 <see cref="JetApplicationRuntime"/> 交給視窗。
/// 這些物件只在這個類別建立；視窗只呼叫 <c>CreateRuntime</c>。
/// </summary>
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

    /// <summary>
    /// 測試可以指定暫存的案件根目錄與開發工具開關；開關預設開啟，測試才涵蓋得到 dev.db.* 這些 action。
    /// 呼叫端也可以包裝每個 action handler，並交給 runtime 一段啟動後在背景執行的工作；桌面程式本身兩者都不用。
    /// </summary>
    public static JetApplicationRuntime CreateRuntime(
        IHostShell hostShell,
        string projectsRootPath,
        bool enableDevTools = true,
        IJetEventPublisher? eventPublisher = null,
        RingBufferLoggerProvider? diagnosticLoggerProvider = null,
        string? diagnosticLogDirectory = null,
        string? principalName = null,
        string? userProfileDirectory = null,
        Func<IApplicationActionHandler, ProjectSession, IApplicationActionHandler>? decorateHandler = null,
        Func<ActionDispatcher, CancellationToken, Task>? startupWork = null,
        string? userFullName = null,
        OperationMonitorOptions? operationMonitorOptions = null)
    {
        var folder = new JetProjectFolder(projectsRootPath);
        var projectStore = new JsonFileProjectStore(folder);

        // 專案工作鎖是跨程序檔案鎖。鎖的 handle 由 runtime 擁有，關閉視窗或測試的 host dispose 時一定釋放。
        var localFileLockService = new LocalFileLockService(folder);

        // 操作人員使用執行主機的 Windows 帳號，不接受前端 operatorId 覆寫。
        // 保留網域或機器名稱，避免不同主機的同名本機帳號被合併；目前不另設企業認證前置。
        // 建案者記的使用者全名在背景開始查，不拖慢視窗出現。
        // 測試以 principalName 覆寫成隔離身分時不查這台電腦的帳號，使用者全名只用 userFullName 傳入的合成值，
        // 開發者的真實姓名才不會寫進測試案件與報告。
        CurrentPrincipal currentPrincipal;
        if (principalName is null)
        {
            var windowsAccount = QualifiedWindowsName();
            currentPrincipal = new CurrentPrincipal(windowsAccount)
            {
                UserFullName = UserFullNameLookup.Start(windowsAccount)
            };
        }
        else
        {
            currentPrincipal = new CurrentPrincipal(principalName)
            {
                UserFullName = userFullName is null ? null : Task.FromResult<string?>(userFullName)
            };
        }

        // 支援日誌在 Debug 與 Release 都註冊，只收 Information 以上、列在允許清單裡的事件，寫進 buffer 前先去識別。
        // Debug 另外掛上完整的診斷日誌，寫到 ring buffer 與檔案。Release 的最低層級是 Information，
        // 不會格式化 sql、tx 這些 Debug 層級的內容。
        // 屬於案件的事件另外附加到案件資料夾的支援紀錄，跨次開啟仍匯出得到。
        var supportCaseHistory = new SupportCaseHistoryStore(folder);
        var supportDiagnostic = new SupportRingBufferLoggerProvider(SupportLogCapacity, supportCaseHistory);
        var diagnostic = enableDevTools
            ? diagnosticLoggerProvider ?? new RingBufferLoggerProvider(DiagnosticLogCapacity)
            : null;
        // 診斷日誌的檔案輸出只給開發用，和 ring buffer 同時收日誌；程式結束後可以直接讀這份 NDJSON 格式的執行紀錄。
        // 只有啟用開發工具而且指定了目錄才建立。Release 不建立這份沒有去識別的檔案，只有上面去識別過的支援日誌。
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
        // 操作監看模組從旁記錄長時間的操作、跑太久或沒有正常結束的 SQL 與匯出進度，不改變操作的流程與結果。
        // 它只接在 dispatcher、SQLite 命令、事件發布與進入步驟的紀錄這四個地方。
        var operationMonitor = new OperationMonitor(loggerFactory.CreateLogger<OperationMonitor>(), operationMonitorOptions);
        // 目前只支援 SQLite 案件，每個案件資料夾一個 jet.db。
        var sqliteDatabase = new SqliteProjectDatabase(folder, operationMonitor);
        var baseReportStore = new ProjectReportStore(
            folder,
            logger: loggerFactory.CreateLogger<ProjectReportStore>());
        var session = new ProjectSession();
        // LoggerFactory.Create 不會釋放以執行個體加入的 provider；支援日誌最後才釋放，先寫完排隊中的案件紀錄，
        // 包括放開工作鎖的那一筆。結束 JET 時先關閉目前案件的資料庫連線，再放開案件鎖。
        // 監看模組最先釋放：它另開的唯讀連線要在關閉案件資料庫之前結束，最後寫的紀錄也要在 logger 釋放前寫完。
        var runtimeResources = new RuntimeOwnedResources(
            supportDiagnostic,
            loggerFactory,
            localFileLockService,
            new ProjectDatabaseShutdown(session, sqliteDatabase, loggerFactory.CreateLogger<ProjectDatabaseShutdown>()),
            operationMonitor);

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
#if DEBUG
            var demoFileWriter = new DemoWorkbookWriter();
#endif
            var hostDialogProjectContext = new HostDialogProjectContext(session, folder);
            // 發給前端的事件照原樣送出後，匯出進度另外交給監看模組寫進日誌。
            var events = new MonitoredEventPublisher(eventPublisher ?? new NullEventPublisher(), operationMonitor);
            var cancellationRegistry = new RequestCancellationRegistry();

            List<IApplicationActionHandler> handlers =
            [
                // 正式契約 handlers
                new SystemPingHandler(enableDevTools),
            new OperationCancelHandler(cancellationRegistry),
            // 使用者身分徽章：回報合格化 Windows 帳號與短名，不連線，也不會失敗。
            new SystemWhoAmIHandler(currentPrincipal),
            new ProjectListLocalHandler(projectStore),
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
#if DEBUG
            new ProjectLoadDemoHandler(),
            new DemoExportGlFileHandler(demoFileWriter),
            new DemoExportTbFileHandler(demoFileWriter),
            new DemoExportAccountMappingFileHandler(demoFileWriter),
            new DemoExportAuthorizedPreparerFileHandler(demoFileWriter),
#endif
            new ImportGlFromFileHandler(fileReader, projectStore, session, events, loggerFactory.CreateLogger<ImportGlFromFileHandler>()),
            new ImportTbFromFileHandler(fileReader, projectStore, session, events, loggerFactory.CreateLogger<ImportTbFromFileHandler>()),
            new ImportAccountMappingFromFileHandler(fileReader, session, loggerFactory.CreateLogger<ImportAccountMappingFromFileHandler>()),
            new ImportAuthorizedPreparerFromFileHandler(fileReader, session, loggerFactory.CreateLogger<ImportAuthorizedPreparerFromFileHandler>()),
            new ImportAuthorizedPreparerClearHandler(session, loggerFactory.CreateLogger<ImportAuthorizedPreparerClearHandler>()),
            new ImportInspectFileHandler(fileReader, loggerFactory.CreateLogger<ImportInspectFileHandler>()),
            new ImportPreviewFileHandler(fileReader),
            new ImportHolidayHandler(projectStore, session, loggerFactory.CreateLogger<ImportHolidayHandler>()),
            new ImportMakeupDayHandler(projectStore, session, loggerFactory.CreateLogger<ImportMakeupDayHandler>()),
            new ImportHolidayFromFileHandler(fileReader, projectStore, session,
                loggerFactory.CreateLogger<ImportHolidayFromFileHandler>()),
            new ImportMakeupDayFromFileHandler(fileReader, projectStore, session,
                loggerFactory.CreateLogger<ImportMakeupDayFromFileHandler>()),
            new CalendarSetNonWorkingDaysHandler(
                projectStore, session, loggerFactory.CreateLogger<CalendarSetNonWorkingDaysHandler>()),
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
            new ProjectSaveProgressHandler(projectStore, session),
            new QueryDataPreviewHandler(projectStore, session),
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
                    folder,
                    MonitorSettings(operationMonitor.Options)));
            }

            if (decorateHandler is not null)
            {
                for (var index = 0; index < handlers.Count; index++)
                {
                    handlers[index] = decorateHandler(handlers[index], session);
                }
            }

            // 引擎錯誤映射：辨識 SQLite 強型樣；Bridge 只收 delegate，不依賴 Infrastructure，未知例外維持 bridge_error。
            var dispatcher = new ActionDispatcher(
                handlers, loggerFactory.CreateLogger<ActionDispatcher>(), session,
                exception => LocalEngineErrors.TryTranslate(exception),
                cancellationRegistry,
                actionExecutionGate,
                // SQLite 案件在變更型作業結束後把記錄檔併回資料庫檔。
                sqliteDatabase,
                operationMonitor);

            runtime = new JetApplicationRuntime(dispatcher, runtimeResources);
            // 啟動工作在背景執行，視窗照常立即顯示。
            runtime.Start(startupWork is null ? null : cancellationToken => startupWork(dispatcher, cancellationToken));
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
        // 診斷日誌檔(dev-only):每次啟動寫一檔到 %LOCALAPPDATA%\JET\logs\,供診斷時讀取執行時日誌。
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

    /// <summary>診斷日誌匯出檔第一行記下的監看門檻，讀日誌的人才知道多久算跑太久。</summary>
    private static IReadOnlyDictionary<string, object?> MonitorSettings(OperationMonitorOptions options) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["check_interval_ms"] = options.CheckInterval.TotalMilliseconds,
            ["slow_sql_threshold_ms"] = options.SlowSqlThreshold.TotalMilliseconds,
            ["slow_sql_repeat_ms"] = options.SlowSqlRepeat.TotalMilliseconds,
            ["still_running_after_ms"] = options.StillRunningAfter.TotalMilliseconds,
            ["still_running_repeat_ms"] = options.StillRunningRepeat.TotalMilliseconds,
            ["export_progress_interval_ms"] = options.ExportProgressInterval.TotalMilliseconds,
            ["summary_after_ms"] = options.SummaryAfter.TotalMilliseconds,
        };

    /// <summary>
    /// 先把事件照原樣交給原本的發布者，再讓監看模組讀匯出進度；監看模組出錯時吞掉，
    /// 前端收到的事件與發布的結果都不變。
    /// </summary>
    private sealed class MonitoredEventPublisher(IJetEventPublisher inner, IOperationMonitor monitor) : IJetEventPublisher
    {
        public void Publish(string eventName, object payload)
        {
            inner.Publish(eventName, payload);
            try
            {
                monitor.ObservePublishedEvent(eventName, payload);
            }
            catch (Exception)
            {
                // 監看只做記錄，不影響事件發布。
            }
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
