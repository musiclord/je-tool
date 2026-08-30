using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

public sealed class ProjectLoadHandler(
    IProjectStore projectStore,
    IImportRepository importRepository,
    IMappingStateStore mappingStore,
    IAccountTaxonomyStore accountTaxonomyStore,
    IResultStaleStateStore resultStaleStateStore,
    ICalendarStore calendarStore,
    IAccountMappingStore accountMappingStore,
    IAuthorizedPreparerStore authorizedPreparerStore,
    IRuleRunStore runStore,
    IFilterScenarioStore filterScenarioStore,
    IReportArtifactStore reportArtifactStore,
    IProjectDatabaseInitializer databaseInitializer,
    IProjectRegistry registry,
    ILockService lockService,
    IAppConfigStore appConfig,
    CurrentPrincipal principal,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "project.load";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = PayloadReader.GetRequiredString(payload, "projectId");
        var document = await projectStore.FindAsync(projectId, cancellationToken);

        // 雙來源載入前置(2026-07-07 雛形;sqlite 路徑零改動,維持可攜性不變式)。
        if (document is null)
        {
            // 本機無資料夾:可能是僅伺服器(serverOnly)案件 → 從 registry 物化快取後載入;否則 project_not_found。
            document = await TryMaterializeServerOnlyAsync(projectId, cancellationToken)
                ?? throw new JetActionException(
                    JetErrorCodes.ProjectNotFound, $"找不到專案 '{projectId}'。");
        }
        else if (document.DatabaseProvider == ProjectDocument.SqlServerDatabaseProvider)
        {
            // 本機 sqlServer doc 在:核對 registry 登記(缺登記 → schema 在則 lazy-heal、不在則擋幽靈)。
            await EnsureRegisteredOrRejectGhostAsync(document, cancellationToken);
        }

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
            // 心跳間隔（供前端計時器；伺服器驅動）：sqlServer 讀 app_config、缺鍵/失敗回程式常數；本地回預設。
            var heartbeatSeconds = await ResolveHeartbeatSecondsAsync(document, cancellationToken);

            var glBatch = await importRepository.GetLatestBatchAsync(document.ProjectId, DatasetKind.Gl, cancellationToken);
            var tbBatch = await importRepository.GetLatestBatchAsync(document.ProjectId, DatasetKind.Tb, cancellationToken);
            var accountMappingState = await accountMappingStore.FindStateAsync(document.ProjectId, cancellationToken);
            var authorizedPreparerState = await authorizedPreparerStore.FindStateAsync(document.ProjectId, cancellationToken);
            var glMapping = await mappingStore.FindAsync(document.ProjectId, DatasetKind.Gl, cancellationToken);
            var tbMapping = await mappingStore.FindAsync(document.ProjectId, DatasetKind.Tb, cancellationToken);
            var taxonomy = await accountTaxonomyStore.ReadAsync(document.ProjectId, cancellationToken);
            var staleState = await resultStaleStateStore.ReadAsync(document.ProjectId, cancellationToken);
            var holidayCount = await calendarStore.CountAsync(document.ProjectId, CalendarDayType.Holiday, cancellationToken);
            var makeupDayCount = await calendarStore.CountAsync(document.ProjectId, CalendarDayType.Makeup, cancellationToken);
            var latestValidate = await runStore.FindLatestAsync(document.ProjectId, RuleRunKinds.Validate, cancellationToken);
            var latestPrescreen = await runStore.FindLatestAsync(document.ProjectId, RuleRunKinds.Prescreen, cancellationToken);
            latestValidate = RuleLogicVersions.IsCurrent(latestValidate) ? latestValidate : null;
            latestPrescreen = RuleLogicVersions.IsCurrent(latestPrescreen) ? latestPrescreen : null;
            var savedScenarios = await filterScenarioStore.ListAsync(document.ProjectId, cancellationToken);
            CurrentFilterRevision? currentFilterRevision = null;
            if (savedScenarios.Count > 0)
            {
                try
                {
                    currentFilterRevision = FilterPopulationScopeParser.RequireCurrentRevision(savedScenarios);
                }
                catch (JetActionException exception) when (exception.Code == JetErrorCodes.StaleResult)
                {
                    // 舊版／不一致 definition 仍回放供使用者修正，但不發布可讀取舊命中的 resultRef。
                }
            }
            var filterPositions = savedScenarios.Select(item => item.Position).ToArray();

            await reportArtifactStore.MarkStaleAsync(
                document.ProjectId,
                artifact => ReportExportSupport.IsSourceStale(
                    artifact,
                    latestValidate,
                    latestPrescreen,
                    staleState.Filter,
                    currentFilterRevision?.Revision,
                    filterPositions),
                cancellationToken);
            var reportArtifacts = await reportArtifactStore.ListAsync(document.ProjectId, cancellationToken);

            var mappingReviewRequired = glMapping is { FormatVersion: < MappingMetadataFormat.CurrentVersion }
                                        || tbMapping is { FormatVersion: < MappingMetadataFormat.CurrentVersion };
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
                        formatVersion = glMapping.FormatVersion,
                        sourceBatchId = glMapping.SourceBatchId,
                        committedUtc = glMapping.CommittedUtc
                    },
                    tb = tbMapping is null ? null : (object)new
                    {
                        mapping = tbMapping.Mapping,
                        changeMode = tbMapping.ModeName,
                        formatVersion = tbMapping.FormatVersion,
                        sourceBatchId = tbMapping.SourceBatchId,
                        committedUtc = tbMapping.CommittedUtc
                    }
                },
                taxonomy = new
                {
                    revision = taxonomy.Revision,
                    categories = taxonomy.Categories
                },
                mappingReviewRequired,
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
                        hasCounterpart = accountMappingState.HasCounterpart
                    },
                    // 授權清單未入 import_batch（name 集合）→ resume 只需 rowCount，無 fileName/importedUtc。
                    authorizedPreparer = authorizedPreparerState is null ? null : (object)new
                    {
                        rowCount = authorizedPreparerState.RowCount
                    },
                    calendar = new
                    {
                        holidayCount,
                        makeupDayCount,
                        // 舊 project.json 沒有 marker 時才以既有筆數相容推斷；新案件明寫 false，
                        // 因此成功 replace 成零筆仍可與「從未匯入」區分。
                        calendarImported = document.CalendarImported
                            ?? holidayCount > 0
                            || makeupDayCount > 0,
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

            session.Enter(document.ProjectId);
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
    /// 心跳間隔（秒）：sqlServer 專案讀 <c>dbo.app_config</c> 的 <c>lock.heartbeatSeconds</c>（缺鍵／讀取失敗回程式常數 30）；
    /// 本地（sqlite/duckdb）專案不涉 app_config（sqlServer-only 控制面）→ 直接回程式常數。讀取失敗（伺服器抖動）
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
        catch (JetActionException ex) when (ex.Code == JetErrorCodes.FileReadError)
        {
            // registry 的 project_json 若能讀到但 INF seed／版本已損壞，必須明確阻斷；
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
    /// 本機 sqlServer doc 存在時的三路授權前置(spec §4;操作層強制):
    /// 對當前 principal **可見** → 放行;**登記存在但不可見** → not_authorized(他人建立的案件,即使本機已有快取資料夾也擋);
    /// **登記不存在** → schema 在則 lazy-heal(以本機 doc 補登記＋授權當前開啟者,涵蓋 registry 問世前的既有線上案)、
    /// schema 不在則 project_not_found(防幽靈快取無聲復活成新空 schema)。
    /// registry 不可達時不吞:讓連線錯誤照常浮現(維持既有 sqlServer load 的失敗行為,不偽裝可用也不偽裝安全,spec §7)。
    /// </summary>
    private async Task EnsureRegisteredOrRejectGhostAsync(
        ProjectDocument document, CancellationToken cancellationToken)
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

        if (await databaseInitializer.DatabaseExistsAsync(
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
    /// validation resume 用：raw part A／B 不重算，只由共用後端 renderer 補上或覆寫衍生 eligibility。
    /// </summary>
    private static object? ToValidationRunSummary(RuleRunRecord? record) =>
        record is null || !ValidationSummaryShapeValidator.IsValid(record.SummaryJson)
            ? null
            : CompletenessEligibilitySupport.ToWireSummary(record);

    /// <summary>prescreen resume 用：結果原樣回放，只由 AuditCore renderer 正規化退役 N/A 文案。</summary>
    private static object? ToPrescreenRunSummary(RuleRunRecord? record) =>
        record is null ? null : JetAuditProgram.RenderPrescreenSummary(record.SummaryJson);

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
