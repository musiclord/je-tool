using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

public sealed class ProjectLoadHandler : IApplicationActionHandler
{
    private readonly IProjectStore _projectStore;
    private readonly ProjectRepositoryCatalog _repositoryCatalog;
    private readonly CurrentPrincipal _principal;
    private readonly ProjectSession _session;
    private readonly IProjectDatabaseCheckpoint? _databaseCheckpoint;
    private readonly ILogger? _logger;
    private readonly ISupportCaseHistory? _supportHistory;
    private readonly ProjectSessionDeparture? _sessionDeparture;

    internal ProjectLoadHandler(
        IProjectStore projectStore,
        ProjectRepositoryCatalog repositoryCatalog,
        CurrentPrincipal principal,
        ProjectSession session,
        ILogger<ProjectLoadHandler>? logger = null,
        ISupportCaseHistory? supportHistory = null,
        IProjectDatabaseCheckpoint? databaseCheckpoint = null,
        ProjectSessionDeparture? sessionDeparture = null)
    {
        this._logger = logger;
        this._supportHistory = supportHistory;
        this._projectStore = projectStore;
        this._repositoryCatalog = repositoryCatalog;
        this._principal = principal;
        this._session = session;
        this._databaseCheckpoint = databaseCheckpoint;
        this._sessionDeparture = sessionDeparture;
    }

    public string Action => "project.load";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = PayloadReader.GetRequiredString(payload, "projectId");
        SupportOperationStage.Enter("metadata_read");
        var document = await _projectStore.FindAsync(projectId, cancellationToken);

        if (document is null)
        {
            throw new JetActionException(
                JetErrorCodes.ProjectNotFound,
                ProjectFolderMessages.CannotOpen(projectId));
        }

        // 依 project.json 的資料庫種類選定這個案件的資料庫組。不支援的種類在取鎖之前回 unsupported_provider，
        // 不留下鎖，也不動案件資料夾；之後的讀取、取鎖與 session 都只用這一組。
        var repositories = _repositoryCatalog.For(document.DatabaseProvider);

        var lockService = repositories.LockService;
        var reportStore = repositories.ReportStore;

        // 進入 session 之前先取案件鎖，它是跨程序的檔案鎖。
        // 鎖被別人持有時回 project_locked：不進入 session，不往下載入，也不更新上次開啟時間。
        SupportOperationStage.Enter("lock_acquire");
        var lockOutcome = await lockService.AcquireAsync(document.ProjectId, _principal.Name, cancellationToken);
        if (lockOutcome is LockOutcome.Held held)
        {
            throw new JetActionException(
                JetErrorCodes.ProjectLocked,
                ProjectLockErrorMessage.Format(held.LockedBy, held.MachineName, held.LockedUtc));
        }
        var acquiredThisCall = lockOutcome is LockOutcome.Acquired { NewlyAcquired: true };

        try
        {
            // 載入時完整檢查一次建表與版本；之後同一程序內的讀寫只確認檔案仍在。
            // 程式開著時用舊的備份覆蓋已關閉的案件，這裡會重新檢查版本，可以升版的就升版，無法升版的就拒絕。
            // 開案不建立新的資料庫：本機案件的資料庫不見或沒有內容時，比照無法開啟的案件回報。
            SupportOperationStage.Enter("database_open");
            // 上一次建案在資料庫建好之前中斷時，審計員沒有拿到這個案件，這裡照建案的做法把資料庫建完：
            // 種子重新產生，建案者取目前的 Windows 身分，金額存放倍率與民國年選項用建案的預設值。
            // 設定檔裡的這些值不採用，以免用記事本做出中斷的建案，就能指定種子或冒用建案者。
            // 查核期間與期末財報準備日是建案時審計員填的，照設定檔；每週休息日照建案時的沒有設定。
            var settingsCreatedHere = false;
            var openingDocument = document;
            await repositories.DatabaseInitializer.EnsureExistingAsync(
                document.ProjectId,
                async token =>
                {
                    settingsCreatedHere = true;
                    return new ProtectedProjectSettings(
                        openingDocument.PeriodStart,
                        openingDocument.PeriodEnd,
                        ProjectDocument.DefaultMoneyScale,
                        RocDateEnabled: true,
                        ProjectCreateHandler.NewSampleSeed(),
                        JetAuditProgram.CurrentInfSamplingAlgorithmVersion,
                        await _principal.ResolveCreatorNameAsync(token),
                        openingDocument.LastAccountingPeriodDate,
                        NonWorkingDays: null);
                },
                cancellationToken);

            // 查核期間、隨機種子這些設定以案件資料庫為準。設定檔在 JET 以外被改過時，之後整個開案都用資料庫的值，
            // 記下上次開啟時間的那一次存檔也一併把設定檔改回；回應列出改回的項目，畫面提醒審計員。
            // 剛補完建案時，資料庫的設定是這裡產生的，不算設定檔被改過，不提醒。
            var recordedSettings = await repositories.ProjectSettings.ReadAsync(document.ProjectId, cancellationToken);
            IReadOnlyList<string> settingsRestored = settingsCreatedHere ? [] : recordedSettings.DifferingItems(document);
            document = recordedSettings.ApplyTo(document);

            SupportOperationStage.Enter("state_read");
            var glBatch = await repositories.Imports.GetLatestBatchAsync(document.ProjectId, DatasetKind.Gl, cancellationToken);
            var tbBatch = await repositories.Imports.GetLatestBatchAsync(document.ProjectId, DatasetKind.Tb, cancellationToken);
            var accountMappingState = await repositories.AccountMappings.FindStateAsync(document.ProjectId, cancellationToken);
            var authorizedPreparerState = await repositories.AuthorizedPreparers.FindStateAsync(document.ProjectId, cancellationToken);
            var glMapping = await repositories.MappingStates.FindAsync(document.ProjectId, DatasetKind.Gl, cancellationToken);
            var tbMapping = await repositories.MappingStates.FindAsync(document.ProjectId, DatasetKind.Tb, cancellationToken);
            // 重新匯入後還沒重新確認時，帶回上次確認的配對讓畫面當草稿；有效配對存在時不需要它。
            var glPrevious = glMapping is null && glBatch is not null
                ? await repositories.MappingStates.FindPreviousAsync(document.ProjectId, DatasetKind.Gl, cancellationToken)
                : null;
            var tbPrevious = tbMapping is null && tbBatch is not null
                ? await repositories.MappingStates.FindPreviousAsync(document.ProjectId, DatasetKind.Tb, cancellationToken)
                : null;
            var categorySnapshot = await repositories.AccountCategories.ReadAsync(document.ProjectId, cancellationToken);
            var staleState = await repositories.ResultStaleStates.ReadAsync(document.ProjectId, cancellationToken);
            var filterDataRevision = await repositories.ResultStaleStates.ReadFilterDataRevisionAsync(document.ProjectId, cancellationToken);
            var holidayCount = await repositories.Calendar.CountAsync(document.ProjectId, CalendarDayType.Holiday, cancellationToken);
            var makeupDayCount = await repositories.Calendar.CountAsync(document.ProjectId, CalendarDayType.Makeup, cancellationToken);
            var unreadableValidation = false;
            var unreadablePrescreen = false;
            var latestValidate = await WorkflowResultStateSupport.ReadResultForStateAsync(
                document.ProjectId, ResultKind.Validation, repositories.Results, () => unreadableValidation = true, cancellationToken);
            var latestPrescreen = await WorkflowResultStateSupport.ReadResultForStateAsync(
                document.ProjectId, ResultKind.Prescreen, repositories.Results, () => unreadablePrescreen = true, cancellationToken);
            var savedScenarios = await repositories.FilterScenarios.ListAsync(document.ProjectId, cancellationToken);

            // 篩選規則更新後，先用目前規則逐一檢查已儲存情境。全部仍然有效才整批改用新規則：
            // 定義與名稱不變，只更新版本與母體欄位並換新保存時間，舊命中在同一交易清除，
            // 下面依新 revision 標記過期的步驟就會把依舊規則產生的報告與底稿標成過期。
            // 改版寫在戳記上次開啟時間之前；之後載入失敗時改版仍保留，重做的結果相同。
            SupportOperationStage.Enter("scenario_check");
            var scenarioUpgrade = await EvaluateScenarioUpgradeAsync(
                document, repositories, savedScenarios, glMapping, accountMappingState, categorySnapshot, cancellationToken);
            // 損毀的條件在清單上只寫「條件內容無法讀取」，哪一條、哪一種記進支援日誌。
            FilterScenarioDiagnostics.LogProblems(_logger, Action, scenarioUpgrade.Problems);
            if (scenarioUpgrade.Status == FilterScenarioUpgradeStatus.Upgraded)
            {
                await repositories.FilterScenarios.ReplaceAllAsync(
                    document.ProjectId, scenarioUpgrade.Scenarios!, cancellationToken);
                savedScenarios = await repositories.FilterScenarios.ListAsync(document.ProjectId, cancellationToken);
                staleState = await repositories.ResultStaleStates.ReadAsync(document.ProjectId, cancellationToken);
            }

            staleState = staleState with
            {
                Validation = staleState.Validation || unreadableValidation,
                Prescreen = staleState.Prescreen || unreadablePrescreen
            };
            var currentResults = WorkflowResultStateSupport.Resolve(
                staleState, latestValidate, latestPrescreen, savedScenarios, filterDataRevision);
            staleState = currentResults.StaleState;
            latestValidate = currentResults.ValidationRecord;
            latestPrescreen = currentResults.PrescreenRecord;
            var currentFilterRevision = currentResults.FilterRevision;
            // 開案也逐一檢查：已是目前版本、但因其他資料改變而不合格的情境，版本檢查會通過，
            // 這裡照待修正的規則把篩選結果視為不可用，畫面維持鎖住並列出要修改的情境，報告依此標成過期。
            if (scenarioUpgrade.Status == FilterScenarioUpgradeStatus.NeedsEdit && currentFilterRevision is not null)
            {
                currentFilterRevision = null;
                staleState = staleState with { Filter = true };
            }
            var filterPositions = currentResults.ScenarioPositions;

            SupportOperationStage.Enter("report_state");
            await reportStore.MarkStaleAsync(
                document.ProjectId,
                report => ReportExportSupport.IsSourceStale(
                    report,
                    latestValidate,
                    latestPrescreen,
                    staleState.Filter,
                    currentFilterRevision?.Revision,
                    filterPositions,
                    filterDataRevision),
                cancellationToken);
            var reports = await reportStore.ListAsync(document.ProjectId, cancellationToken);

            var glOptions = glMapping is null
                ? null
                : glMapping.GlOptions ?? GlMappingOptions.NormalizeLegacy(glMapping.Mapping);
            IReadOnlyList<GlDateNotice> glDateNotices = glMapping is null
                ? []
                : await ReadGlDateNoticesAsync(repositories.MappingStates, document.ProjectId, cancellationToken);

            var response = new
            {
                project = new
                {
                    projectId = document.ProjectId,
                    projectCode = document.ProjectCode,
                    clientName = document.ClientName,
                    operatorId = document.OperatorId,
                    periodStart = document.PeriodStart,
                    periodEnd = document.PeriodEnd,
                    prepStartDate = document.LastAccountingPeriodDate,
                    moneyScale = document.MoneyScale,
                    databaseProvider = document.DatabaseProvider,
                    rocDateEnabled = document.RocDateEnabled,
                    createdUtc = document.CreatedUtc,
                    currentStep = document.CurrentStep
                },
                mapping = new
                {
                    gl = glMapping is null ? null : new
                    {
                        mapping = glMapping.Mapping,
                        amountMode = glMapping.ModeName,
                        approvalDateMode = glOptions!.ApprovalDateMode,
                        postingStatusPolicy = glOptions!.PostingStatusPolicy,
                        manualAutoPolicy = glOptions!.ManualAutoPolicy,
                        rdeFields = glOptions!.RdeFields,
                        // 確認配對時往下填補空白的欄位，重開案件帶回摘要與草稿。
                        fillDownFields = glOptions!.FillDownFields,
                        // 確認配對時填了幾格隨配對保存，第三步摘要列出來。
                        fillDownCount = glOptions!.FillDownCount,
                        // 由 GL 借方平均算出的尾數 0 的個數；第四步卡片與第五步預設都讀這裡。
                        zerosThreshold = TrailingZeroThreshold.FromCommittedGl(glMapping, document.MoneyScale),
                        // 第五步依這份清單停用用到未配對欄位的入口；和驗證器、第四步讀同一份欄位目錄的答案。
                        availableGlFields = JetFieldCatalog.AvailableGlFields(glMapping.Mapping, glOptions),
                        // GL 中每一筆都有值的欄位；第五步不替它們提供「空白」的比較方式，和條件檢查讀同一份欄位目錄的答案。
                        neverBlankGlFields = JetFieldCatalog.NeverBlankGlFields(glMapping.Mapping, glOptions),
                        // 確認配對時存下的日期提醒，和那次確認配對的回應相同；第三步摘要照樣列出。
                        dateNotices = glDateNotices,
                        sourceBatchId = glMapping.SourceBatchId,
                        committedUtc = glMapping.CommittedUtc
                    },
                    tb = tbMapping is null ? null : (object)new
                    {
                        mapping = tbMapping.Mapping,
                        changeMode = tbMapping.ModeName,
                        sourceBatchId = tbMapping.SourceBatchId,
                        committedUtc = tbMapping.CommittedUtc
                    }
                },
                // 只在 mapping 對應的一側是 null 時才可能有值；不是有效配對，畫面只拿來預填草稿。
                previousMapping = new
                {
                    gl = PreviousGlMappingShape(glPrevious),
                    tb = tbPrevious is null ? null : (object)new
                    {
                        mapping = tbPrevious.Mapping,
                        changeMode = tbPrevious.ModeName,
                        committedUtc = tbPrevious.CommittedUtc
                    }
                },
                accountCategory = new
                {
                    revision = categorySnapshot.Revision,
                    categories = categorySnapshot.Categories
                },
                staleState = new
                {
                    validation = staleState.Validation,
                    prescreen = staleState.Prescreen,
                    filter = staleState.Filter
                },
                importState = new
                {
                    gl = ToImportState(glBatch),
                    tb = ToImportState(tbBatch),
                    accountMapping = accountMappingState is null ? null : (object)new
                    {
                        batchId = accountMappingState.BatchId,
                        rowCount = accountMappingState.RowCount,
                        fileName = accountMappingState.FileName,
                        importedUtc = accountMappingState.ImportedUtc,
                        hasAnyCategory = accountMappingState.HasAnyCategory,
                        hasRevenue = accountMappingState.HasRevenue,
                        hasCounterpart = accountMappingState.HasCounterpart,
                        blankCategoryCount = accountMappingState.BlankCategoryCount
                    },
                    // 授權集合與識別欄位一起恢復，不保存來源檔的私人路徑。
                    authorizedPreparer = authorizedPreparerState is null ? null : (object)new
                    {
                        rowCount = authorizedPreparerState.RowCount,
                        sourceColumn = authorizedPreparerState.SourceColumn,
                        sourceRowCount = authorizedPreparerState.SourceRowCount,
                        blankRowCount = authorizedPreparerState.BlankRowCount,
                        duplicateRowCount = authorizedPreparerState.DuplicateRowCount,
                        matchedPreparerCount = authorizedPreparerState.MatchedPreparerCount
                    },
                    calendar = new
                    {
                        holidayCount,
                        makeupDayCount,
                        // 新案件明寫 false，因此成功 replace 成零筆仍可與「從未匯入」區分。
                        calendarImported = document.CalendarImported ?? false,
                        nonWorkingDays = NonWorkingDays.Resolve(document.NonWorkingDays),
                        nonWorkingDaysConfigured = document.NonWorkingDays is not null
                    }
                },
                validationResult = ToValidationResultSummary(latestValidate),
                prescreenResult = ToPrescreenResultSummary(latestPrescreen),
                filterScenarios = savedScenarios.Select(FilterScenarioSummaryRenderer.Render).ToArray(),
                filterResultRef = currentFilterRevision is null ? null : (object)new
                {
                    revision = currentFilterRevision.Revision,
                    generatedUtc = currentFilterRevision.GeneratedUtc,
                    logicVersion = RuleLogicVersions.Filter,
                    populationScope = GlPopulationScopeValues.ToValue(currentFilterRevision.PopulationScope)
                },
                filterScenarioCheck = new
                {
                    status = ToWireStatus(scenarioUpgrade.Status),
                    recalculatedCount = scenarioUpgrade.Status == FilterScenarioUpgradeStatus.Upgraded
                        ? savedScenarios.Count
                        : 0,
                    problems = scenarioUpgrade.Problems.Select(problem => new
                    {
                        position = problem.Position,
                        name = problem.Name,
                        messages = problem.Messages
                    }).ToArray()
                },
                reports = reports.Select(ReportExportSupport.ReportWire).ToArray(),
                settingsRestored,
                // 第四步抽樣筆數的預設值與上限，以及第五步預填的摘要關鍵字；畫面照這裡顯示，不另存一份。
                infSampling = new
                {
                    defaultSampleSize = InfSampling.DefaultSampleSize,
                    maximumSampleSize = InfSampling.MaximumSampleSize
                },
                suspiciousKeywordDefaults = SuspiciousKeywordDefaults.Defaults
            };

            // 回應物件完全建好之後，才做兩件外面看得到的事：存下上次開啟時間，以及進入 session。
            // SaveAsync 成功就算記下了這次開啟；之後的步驟不再理會呼叫端的取消要求，
            // 以免時間已經記下，卻因為後面那些失敗也不影響結果的工作被取消，而回報載入失敗。
            var openedDocument = document with { LastOpenedUtc = DateTimeOffset.UtcNow };
            SupportOperationStage.Enter("metadata_save");
            await _projectStore.SaveAsync(openedDocument, cancellationToken);

            // 新的案件確定開成之後、進入 session 之前，才離開目前還開著的另一個案件，放掉它的案件鎖並關閉它的資料庫；
            // 重開同一個案件時不動。前面任何一步失敗時，目前的案件照樣開著。
            if (_sessionDeparture is not null)
            {
                await _sessionDeparture.LeaveOtherProjectAsync(document.ProjectId);
            }

            _session.Enter(document.ProjectId, repositories);
            // 持有案件鎖時才建立案件的支援紀錄資料夾，並記下這次開啟。
            SupportCaseDiagnostics.CaseOpened(
                _supportHistory, _logger, document.ProjectId, document.DatabaseProvider, createdNow: false);
            return response;
        }
        catch
        {
            try
            {
                if (acquiredThisCall)
                {
                    await lockService.ReleaseAsync(document.ProjectId, _principal.Name, CancellationToken.None);
                }
            }
            catch (Exception releaseError)
            {
                // 放鎖失敗時不往外丟，保留原本載入案件或存上次開啟時間時的例外。案件鎖沒放掉時，下次開案可能顯示已被開啟，
                // 支援日誌另記一筆。
                if (_logger is not null) SupportStepDiagnostics.StepFailed(_logger, "lock_release", releaseError);
            }

            if (acquiredThisCall && _databaseCheckpoint is not null)
            {
                // 沒有進入這個案件：開案途中開過的資料庫連線一併關閉，JET 不再開著它的資料庫檔（見 IProjectDatabaseCheckpoint）。
                await ProjectDatabaseCheckpointSteps.TryCloseAsync(_databaseCheckpoint, document.ProjectId, _logger);
            }

            throw;
        }
    }

    /// <summary>
    /// 用與補算相同的案件事實建立驗證環境，再交給純函式判斷能否改用目前規則。
    /// 沒有已儲存情境時不讀其他資料。母體固定為查核期間，非查核期間的情境由判斷函式擋下。
    /// </summary>
    private static async Task<FilterScenarioUpgradeResult> EvaluateScenarioUpgradeAsync(
        ProjectDocument document,
        ProjectRepositories repositories,
        IReadOnlyList<SavedFilterScenario> savedScenarios,
        CommittedMapping? glMapping,
        AccountMappingState? accountMappingState,
        AccountCategorySnapshot categorySnapshot,
        CancellationToken cancellationToken)
    {
        if (savedScenarios.Count == 0)
        {
            return FilterScenarioRuleUpgrade.Evaluate(
                savedScenarios, new FilterValidationContext(false, false, false) { DateParseOptions = document.DateParseOptions },
                document.MoneyScale, DateTimeOffset.UtcNow);
        }

        var hasAuthorizedPreparers =
            await repositories.AuthorizedPreparers.CountAsync(document.ProjectId, cancellationToken) > 0;
        var calendarLists = await FilterCalendarLists.ReadAsync(repositories.Calendar, document.ProjectId, cancellationToken);
        var validationContext = FilterValidationContextFactory.Create(
            document,
            glMapping ?? new CommittedMapping(DatasetKind.Gl, new Dictionary<string, string>(),
                string.Empty, string.Empty, DateTimeOffset.MinValue),
            accountMappingState,
            hasAuthorizedPreparers,
            GlPopulationScope.AuditPeriod,
            categorySnapshot,
            calendarLists);
        return FilterScenarioRuleUpgrade.Evaluate(
            savedScenarios, validationContext, document.MoneyScale, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 日期提醒只是讓審計員回頭看的說明，存下的內容讀不出來時當成沒有，不擋開案；重新確認配對就會再存一份。
    /// 支援日誌只記步驟代號與例外的型別、位置，不寫存下的內容。
    /// </summary>
    private async Task<IReadOnlyList<GlDateNotice>> ReadGlDateNoticesAsync(
        IMappingStateStore mappingStates,
        string projectId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await mappingStates.FindGlDateNoticesAsync(projectId, cancellationToken);
        }
        catch (JsonException error)
        {
            if (_logger is not null) SupportStepDiagnostics.StepFailed(_logger, "gl_date_notices_read", error);
            return [];
        }
    }

    private static string ToWireStatus(FilterScenarioUpgradeStatus status) => status switch
    {
        FilterScenarioUpgradeStatus.Current => "current",
        FilterScenarioUpgradeStatus.Upgraded => "recalculated",
        FilterScenarioUpgradeStatus.NeedsEdit => "needsEdit",
        FilterScenarioUpgradeStatus.Inconsistent => "inconsistent",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    /// <summary>
    /// 重新載入案件時回傳資料驗證結果：存入筆數與合計核對、GL 與 TB 逐科目比對的原始結果不重算，
    /// 只由 CompletenessEligibilitySupport.ToWireSummary 補上或覆寫「可否繼續後續步驟」這個推算出來的判定。
    /// </summary>
    private static object? ToValidationResultSummary(ResultRecord? record) =>
        record is null || !ValidationSummaryShapeValidator.IsValid(record.SummaryJson)
            ? null
            : CompletenessEligibilitySupport.ToWireSummary(record);

    /// <summary>重新載入案件時回傳預篩選結果：存下的結果照原樣回傳，只由 AuditCore 補上目前的定位說明。</summary>
    private static object? ToPrescreenResultSummary(ResultRecord? record) =>
        record is null ? null : JetAuditProgram.RenderPrescreenSummary(record.SummaryJson);

    private static object? PreviousGlMappingShape(CommittedMapping? previous)
    {
        if (previous is null)
        {
            return null;
        }

        var options = previous.GlOptions ?? GlMappingOptions.NormalizeLegacy(previous.Mapping);
        return new
        {
            mapping = previous.Mapping,
            amountMode = previous.ModeName,
            approvalDateMode = options.ApprovalDateMode,
            postingStatusPolicy = options.PostingStatusPolicy,
            manualAutoPolicy = options.ManualAutoPolicy,
            rdeFields = options.RdeFields,
            fillDownFields = options.FillDownFields,
            committedUtc = previous.CommittedUtc
        };
    }

    private static object? ToImportState(ImportBatchInfo? batch)
    {
        return batch is null
            ? null
            : new
            {
                batchId = batch.BatchId,
                rowCount = batch.RowCount,
                columns = batch.Columns,
                fileName = batch.SourceFileName,
                importedUtc = batch.ImportedUtc,
                sources = ImportStateShapes.ToSourceList(batch.Sources)
            };
    }
}
