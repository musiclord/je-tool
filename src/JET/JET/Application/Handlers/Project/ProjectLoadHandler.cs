using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

public sealed class ProjectLoadHandler : IApplicationActionHandler
{
    private readonly IProjectStore projectStore;
    private readonly ProjectRepositoryCatalog repositoryCatalog;
    private readonly IProjectRegistry registry;
    private readonly IAppConfigStore appConfig;
    private readonly CurrentPrincipal principal;
    private readonly ProjectSession session;
    private readonly IProjectDatabaseRetention? databaseRetention;

    internal ProjectLoadHandler(
        IProjectStore projectStore,
        ProjectRepositoryCatalog repositoryCatalog,
        IProjectRegistry registry,
        IAppConfigStore appConfig,
        CurrentPrincipal principal,
        ProjectSession session,
        IProjectDatabaseRetention? databaseRetention = null)
    {
        this.projectStore = projectStore;
        this.repositoryCatalog = repositoryCatalog;
        this.registry = registry;
        this.appConfig = appConfig;
        this.principal = principal;
        this.session = session;
        this.databaseRetention = databaseRetention;
    }

    public string Action => "project.load";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = PayloadReader.GetRequiredString(payload, "projectId");
        var document = await projectStore.FindAsync(projectId, cancellationToken);
        var materializedFromServer = false;

        // 本機與線上兩種來源的載入前置；本地案件路徑不受影響，維持可攜性。
        if (document is null)
        {
            // 本機沒有案件資料夾時，只有前端明確標示是 SQL Server 案件，才去線上登錄找僅存在伺服器的案件，
            // 找到就物化快取後載入。SQLite 與 DuckDB 案件或沒帶提示的請求不查線上登錄，直接回找不到專案。
            var providerHint = PayloadReader.GetOptionalString(payload, "databaseProvider");
            var notFound = new JetActionException(
                JetErrorCodes.ProjectNotFound,
                $"找不到專案 '{projectId}'。案件資料夾可能已被移動或刪除，請確認案件資料夾仍在 JET 的案件位置。");
            if (providerHint != ProjectDocument.SqlServerDatabaseProvider)
            {
                throw notFound;
            }

            document = await TryMaterializeServerOnlyAsync(projectId, cancellationToken) ?? throw notFound;
            materializedFromServer = true;
        }

        // 依 project.json 的資料庫種類選定這個案件的資料庫組。未知種類在取鎖之前回 unsupported_provider，
        // 不留下鎖；之後的讀取、取鎖與 session 都只用這一組。
        var repositories = repositoryCatalog.For(document.DatabaseProvider);
        if (!materializedFromServer
            && document.DatabaseProvider == ProjectDocument.SqlServerDatabaseProvider)
        {
            // 本機 sqlServer doc 在:核對 registry 登記(缺登記 → schema 在則 lazy-heal、不在則擋幽靈)。
            await EnsureRegisteredOrRejectGhostAsync(document, repositories, cancellationToken);
        }

        var lockService = repositories.LockService;
        var reportArtifactStore = repositories.ReportArtifactStore;

        // 授權通過後、進入 session 前取工作鎖。sqlServer 走租約表，本地走跨程序檔案鎖。
        // Held → project_locked；不設 session、不進 workflow、不戳 last_opened。
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
            // 載入期間保持案件資料庫開啟（只有 DuckDB 有作用）。dispatcher 不替 project.load 持有，
            // 由這裡在取得工作鎖之後才開始持有；離開 try 區塊就釋放，早於載入失敗時的放鎖。
            using var retainedDatabase = databaseRetention?.TryRetain(document.ProjectId);

            // 載入時完整檢查一次建表與升版；之後同一程序內的讀寫只確認檔案仍在。
            // 程式開著時用備份覆蓋已關閉的案件，這裡會重新升版。
            await repositories.DatabaseInitializer.EnsureCreatedAsync(document.ProjectId, cancellationToken);

            // 心跳間隔（供前端計時器；伺服器驅動）：sqlServer 讀 app_config、缺鍵/失敗回程式常數；本地回預設。
            var heartbeatSeconds = await ResolveHeartbeatSecondsAsync(document, cancellationToken);

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
            var taxonomy = await repositories.AccountTaxonomy.ReadAsync(document.ProjectId, cancellationToken);
            var staleState = await repositories.ResultStaleStates.ReadAsync(document.ProjectId, cancellationToken);
            var filterDataRevision = await repositories.ResultStaleStates.ReadFilterDataRevisionAsync(document.ProjectId, cancellationToken);
            var holidayCount = await repositories.Calendar.CountAsync(document.ProjectId, CalendarDayType.Holiday, cancellationToken);
            var makeupDayCount = await repositories.Calendar.CountAsync(document.ProjectId, CalendarDayType.Makeup, cancellationToken);
            var latestValidate = await repositories.RuleRuns.FindLatestAsync(document.ProjectId, RuleRunKinds.Validate, cancellationToken);
            var latestPrescreen = await repositories.RuleRuns.FindLatestAsync(document.ProjectId, RuleRunKinds.Prescreen, cancellationToken);
            var savedScenarios = await repositories.FilterScenarios.ListAsync(document.ProjectId, cancellationToken);

            // 篩選規則更新後，先用目前規則逐一檢查已儲存情境。全部仍然有效才整批改用新規則：
            // 定義與名稱不變，只更新版本與母體欄位並換新保存時間，舊命中在同一交易清除，
            // 下面依新 revision 標記過期的步驟就會把依舊規則產生的報告與底稿標成過期。
            // 改版寫在戳記上次開啟時間之前；之後載入失敗時改版仍保留，重做的結果相同。
            var scenarioUpgrade = await EvaluateScenarioUpgradeAsync(
                document, repositories, savedScenarios, glMapping, accountMappingState, taxonomy, cancellationToken);
            if (scenarioUpgrade.Status == FilterScenarioUpgradeStatus.Upgraded)
            {
                await repositories.FilterScenarios.ReplaceAllAsync(
                    document.ProjectId, scenarioUpgrade.Scenarios!, cancellationToken);
                savedScenarios = await repositories.FilterScenarios.ListAsync(document.ProjectId, cancellationToken);
                staleState = await repositories.ResultStaleStates.ReadAsync(document.ProjectId, cancellationToken);
            }

            var currentResults = WorkflowResultStateSupport.Resolve(
                staleState, latestValidate, latestPrescreen, savedScenarios, filterDataRevision);
            staleState = currentResults.StaleState;
            latestValidate = currentResults.ValidationRun;
            latestPrescreen = currentResults.PrescreenRun;
            var currentFilterRevision = currentResults.FilterRevision;
            var filterPositions = currentResults.ScenarioPositions;

            await reportArtifactStore.MarkStaleAsync(
                document.ProjectId,
                artifact => ReportExportSupport.IsSourceStale(
                    artifact,
                    latestValidate,
                    latestPrescreen,
                    staleState.Filter,
                    currentFilterRevision?.Revision,
                    filterPositions,
                    filterDataRevision),
                cancellationToken);
            var reportArtifacts = await reportArtifactStore.ListAsync(document.ProjectId, cancellationToken);

            var glOptions = glMapping is null
                ? null
                : glMapping.GlOptions ?? GlMappingOptions.NormalizeLegacy(glMapping.Mapping);

            var response = new
            {
                project = new
                {
                    projectId = document.ProjectId,
                    projectCode = document.ProjectCode,
                    entityName = document.EntityName,
                    operatorId = document.OperatorId,
                    periodStart = document.PeriodStart,
                    periodEnd = document.PeriodEnd,
                    lastPeriodStart = document.LastAccountingPeriodDate,
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
                taxonomy = new
                {
                    revision = taxonomy.Revision,
                    categories = taxonomy.Categories
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
                latestRuns = new
                {
                    validate = ToValidationRunSummary(latestValidate),
                    prescreen = ToPrescreenRunSummary(latestPrescreen)
                },
                filterScenarios = savedScenarios.Select(FilterScenarioSummaryRenderer.Render).ToArray(),
                filterResultRef = currentFilterRevision is null ? null : (object)new
                {
                    revision = currentFilterRevision.Revision,
                    generatedUtc = currentFilterRevision.Revision,
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
                reportArtifacts = reportArtifacts.Select(ReportExportSupport.ArtifactWire).ToArray(),
                heartbeatSeconds
            };

            // response 已完整物化後才發布兩個可見副作用。SaveAsync 是 LastOpened 的 commit point；其後不再
            // 使用 caller cancellation，避免已戳記卻因後置 best-effort 工作取消而回報載入失敗。
            var openedDocument = document with { LastOpenedUtc = DateTimeOffset.UtcNow };
            await projectStore.SaveAsync(openedDocument, cancellationToken);

            if (document.DatabaseProvider == ProjectDocument.SqlServerDatabaseProvider)
            {
                try
                {
                    await registry.TouchLastOpenedAsync(document.ProjectId, CancellationToken.None);
                }
                catch (Exception)
                {
                    // Registry 戳記是 commit 後的真正 best-effort：失敗或取消都不改變已完成的本機載入。
                }
            }

            session.Enter(document.ProjectId, repositories);
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
            catch (Exception)
            {
                // 補償失敗不得遮蔽原始 resume／LastOpened 失敗。
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
        AccountTaxonomySnapshot taxonomy,
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
        var validationContext = FilterValidationContextFactory.Create(
            document,
            glMapping ?? new CommittedMapping(DatasetKind.Gl, new Dictionary<string, string>(),
                string.Empty, string.Empty, DateTimeOffset.MinValue),
            accountMappingState,
            hasAuthorizedPreparers,
            GlPopulationScope.AuditPeriod,
            taxonomy);
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
    /// 心跳間隔（秒）：sqlServer 專案讀 <c>dbo.app_config</c> 的 <c>lock.heartbeatSeconds</c>（缺鍵／讀取失敗回程式常數 30）；
    /// 本地（sqlite/duckdb）專案不涉 app_config（只屬 sqlServer 的設定表）→ 直接回程式常數。讀取失敗（伺服器抖動）
    /// 不阻斷載入——退回預設，前端仍能起計時器（本地與 sqlServer 的 heartbeat action 皆為輕量、no-op 亦無害）。
    /// </summary>
    private async Task<int> ResolveHeartbeatSecondsAsync(ProjectDocument document, CancellationToken cancellationToken)
    {
        if (document.DatabaseProvider != ProjectDocument.SqlServerDatabaseProvider)
        {
            return ProjectLockDefaults.HeartbeatSeconds;
        }

        try
        {
            var raw = await appConfig.GetAsync(ProjectLockDefaults.HeartbeatSecondsKey, cancellationToken);
            return ProjectLockDefaults.ParsePositive(raw, ProjectLockDefaults.HeartbeatSeconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return ProjectLockDefaults.HeartbeatSeconds;
        }
    }

    /// <summary>
    /// 本機無資料夾時:查 registry 是否有當前 principal 可見的登記(serverOnly)。有 → 把 project_json 物化為
    /// projects/{id}/project.json 後回傳供載入;無、或登記簿不可達/未設定 → 回 null(呼叫端轉 project_not_found,
    /// 維持「本機找不到又無法線上確認即不存在」的既有語意)。物化目標資料夾已被占用 → invalid_payload。
    /// </summary>
    private async Task<ProjectDocument?> TryMaterializeServerOnlyAsync(
        string projectId, CancellationToken cancellationToken)
    {
        RegisteredProject? registered;
        try
        {
            registered = await registry.FindVisibleAsync(projectId, principal.Name, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JetActionException ex) when (
            ex.Code is JetErrorCodes.FileReadError or JetErrorCodes.InvalidProjectSchema)
        {
            // registry 的 project_json 若能讀到但 INF seed／版本已損壞或是舊版案件，必須明確阻斷；
            // 不能降級成 project_not_found，更不能物化後另生 seed。
            throw;
        }
        catch (Exception)
        {
            return null; // 登記簿不可達/未設定 → 無從確認線上 → 交回 project_not_found（不偽裝存在）。
        }

        if (registered is null)
        {
            return null;
        }

        // Remote documents have not passed the local file reader; reject invalid periods before creating a cache or taking a lock.
        ProjectDocumentPeriodIntegrity.Validate(registered.Document, $"線上案件『{projectId}』的登錄資料");

        // 物化快取:寫 projects/{id}/project.json。目標資料夾已被(其他 provider 的)同名案件占用 → invalid_payload。
        try
        {
            await projectStore.CreateAsync(registered.Document, cancellationToken);
        }
        catch (ProjectStoreCollisionException)
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                $"本地已有同名案件『{projectId}』,無法物化此線上(僅伺服器)案件的快取。");
        }

        return registered.Document;
    }

    /// <summary>
    /// 本機 sqlServer doc 存在時的三路授權前置(在操作層強制):
    /// 對當前 principal **可見** → 放行;**登記存在但不可見** → not_authorized(他人建立的案件,即使本機已有快取資料夾也擋);
    /// **登記不存在** → schema 在則 lazy-heal(以本機 doc 補登記＋授權當前開啟者,涵蓋 registry 問世前的既有線上案)、
    /// schema 不在則 project_not_found(防幽靈快取無聲復活成新空 schema)。
    /// registry 不可達時不吞:讓連線錯誤照常浮現(維持既有 sqlServer load 的失敗行為,不偽裝可用也不偽裝安全)。
    /// </summary>
    private async Task EnsureRegisteredOrRejectGhostAsync(
        ProjectDocument document, ProjectRepositories repositories, CancellationToken cancellationToken)
    {
        // 可見 → 放行(登記存在且當前 principal 已獲授權)。
        if (await registry.FindVisibleAsync(document.ProjectId, principal.Name, cancellationToken) is not null)
        {
            return;
        }

        // 不可見但登記存在 → 他人建立的線上案件,操作層擋下(即使本機已有快取資料夾)。
        if (await registry.ExistsAsync(document.ProjectId, cancellationToken))
        {
            throw new JetActionException(
                JetErrorCodes.NotAuthorized,
                "此線上案件由其他使用者建立，您沒有存取權。");
        }

        if (await repositories.DatabaseInitializer.DatabaseExistsAsync(
                document.ProjectId, ProjectDocument.SqlServerDatabaseProvider, cancellationToken))
        {
            // schema 在、registry 缺 → 以本機 doc 補登記＋授權當前 principal。
            await registry.RegisterAsync(document, principal.Name, cancellationToken);
        }
        else
        {
            // schema 不在 → 幽靈快取:擋下,避免無聲復活成新空 schema。
            throw new JetActionException(
                JetErrorCodes.ProjectNotFound,
                "此線上案件已不在伺服器上（可能已被刪除）。");
        }
    }

    /// <summary>
    /// validation resume 用：控制總數核對與 GL、TB 逐科目比對的原始結果不重算，只由共用後端 renderer
    /// 補上或覆寫衍生的「可否繼續後續步驟」判定。
    /// </summary>
    private static object? ToValidationRunSummary(RuleRunRecord? record) =>
        record is null || !ValidationSummaryShapeValidator.IsValid(record.SummaryJson)
            ? null
            : CompletenessEligibilitySupport.ToWireSummary(record);

    /// <summary>prescreen resume 用：結果原樣回放，只由 AuditCore renderer 補上目前的定位說明。</summary>
    private static object? ToPrescreenRunSummary(RuleRunRecord? record) =>
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
