using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

public sealed class ProjectLoadHandler : IApplicationActionHandler
{
    private readonly IProjectStore projectStore;
    private readonly ProjectRepositoryCatalog repositoryCatalog;
    private readonly CurrentPrincipal principal;
    private readonly ProjectSession session;
    private readonly IProjectDatabaseCheckpoint? databaseCheckpoint;
    private readonly ILogger? logger;
    private readonly ISupportCaseHistory? supportHistory;

    internal ProjectLoadHandler(
        IProjectStore projectStore,
        ProjectRepositoryCatalog repositoryCatalog,
        CurrentPrincipal principal,
        ProjectSession session,
        ILogger<ProjectLoadHandler>? logger = null,
        ISupportCaseHistory? supportHistory = null,
        IProjectDatabaseCheckpoint? databaseCheckpoint = null)
    {
        this.logger = logger;
        this.supportHistory = supportHistory;
        this.projectStore = projectStore;
        this.repositoryCatalog = repositoryCatalog;
        this.principal = principal;
        this.session = session;
        this.databaseCheckpoint = databaseCheckpoint;
    }

    public string Action => "project.load";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = PayloadReader.GetRequiredString(payload, "projectId");
        SupportOperationStage.Enter("metadata_read");
        var document = await projectStore.FindAsync(projectId, cancellationToken);

        if (document is null)
        {
            throw new JetActionException(
                JetErrorCodes.ProjectNotFound,
                ProjectFolderMessages.CannotOpen(projectId));
        }

        // 依 project.json 的資料庫種類選定這個案件的資料庫組。不支援的種類在取鎖之前回 unsupported_provider，
        // 不留下鎖，也不動案件資料夾；之後的讀取、取鎖與 session 都只用這一組。
        var repositories = repositoryCatalog.For(document.DatabaseProvider);

        var lockService = repositories.LockService;
        var reportStore = repositories.ReportStore;

        // 進入 session 前取工作鎖（跨程序檔案鎖）。
        // Held → project_locked；不設 session、不進 workflow、不戳 last_opened。
        SupportOperationStage.Enter("lock_acquire");
        var lockOutcome = await lockService.AcquireAsync(document.ProjectId, principal.Name, cancellationToken);
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
            // 程式開著時用舊的備份覆蓋已關閉的案件，這裡會重新檢查版本，版本較舊就拒絕。
            // 開案不建立新的資料庫：本機案件的資料庫不見或沒有內容時，比照無法開啟的案件回報。
            SupportOperationStage.Enter("database_open");
            await repositories.DatabaseInitializer.EnsureExistingAsync(document.ProjectId, cancellationToken);

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
            FilterScenarioDiagnostics.LogProblems(logger, Action, scenarioUpgrade.Problems);
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
                    roundingMode = document.RoundingMode,
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
                reports = reports.Select(ReportExportSupport.ReportWire).ToArray()
            };

            // response 已完整物化後才發布兩個可見副作用。SaveAsync 是 LastOpened 的 commit point；其後不再
            // 使用 caller cancellation，避免已戳記卻因後置 best-effort 工作取消而回報載入失敗。
            var openedDocument = document with { LastOpenedUtc = DateTimeOffset.UtcNow };
            SupportOperationStage.Enter("metadata_save");
            await projectStore.SaveAsync(openedDocument, cancellationToken);

            session.Enter(document.ProjectId, repositories);
            // 持有工作鎖時才建立案件的支援紀錄資料夾，並記下這次開啟。
            SupportCaseDiagnostics.CaseOpened(
                supportHistory, logger, document.ProjectId, document.DatabaseProvider, createdNow: false);
            return response;
        }
        catch
        {
            try
            {
                if (acquiredThisCall)
                {
                    await lockService.ReleaseAsync(document.ProjectId, principal.Name, CancellationToken.None);
                }
            }
            catch (Exception releaseError)
            {
                // 補償失敗不得遮蔽原始 resume／LastOpened 失敗。工作鎖沒放掉時，下次開案可能顯示已被開啟，
                // 支援日誌另記一筆。
                if (logger is not null) SupportStepDiagnostics.StepFailed(logger, "lock_release", releaseError);
            }

            if (acquiredThisCall && databaseCheckpoint is not null)
            {
                // 沒有進入這個案件：開案途中開過的資料庫連線一併關閉，JET 不再開著它的資料庫檔（見 IProjectDatabaseCheckpoint）。
                await ProjectDatabaseCheckpointSteps.TryCloseAsync(databaseCheckpoint, document.ProjectId, logger);
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

    private static string ToWireStatus(FilterScenarioUpgradeStatus status) => status switch
    {
        FilterScenarioUpgradeStatus.Current => "current",
        FilterScenarioUpgradeStatus.Upgraded => "recalculated",
        FilterScenarioUpgradeStatus.NeedsEdit => "needsEdit",
        FilterScenarioUpgradeStatus.Inconsistent => "inconsistent",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    /// <summary>
    /// validation resume 用：存入筆數與合計核對、GL 與 TB 逐科目比對的原始結果不重算，只由共用後端 renderer
    /// 補上或覆寫衍生的「可否繼續後續步驟」判定。
    /// </summary>
    private static object? ToValidationResultSummary(ResultRecord? record) =>
        record is null || !ValidationSummaryShapeValidator.IsValid(record.SummaryJson)
            ? null
            : CompletenessEligibilitySupport.ToWireSummary(record);

    /// <summary>prescreen resume 用：結果原樣回放，只由 AuditCore renderer 補上目前的定位說明。</summary>
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
