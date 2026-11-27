using System.Text.Json;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

public sealed record FilterHitMaterializationSnapshot(
    ProjectDocument Document,
    IReadOnlyList<SavedFilterScenario> Scenarios);

/// <summary>把已儲存篩選情境命中的分錄寫進 filter_hit 與 filter_hit_condition。這裡把情境的 definition JSON 解析成
/// <see cref="FilterScenarioSpec"/>、檢查能不能套用目前規則，再交給 <see cref="IFilterHitMaterializer"/> 寫入。
/// 平常儲存情境時已經存下結果，這裡只在結果標成過期時重算：查詢高風險條件彙總與傳票重算全案，底稿只重算所選情境。
/// 解析放在 Application；<see cref="IFilterHitMaterializer"/> 只收 Domain 的型別，Infrastructure 就不必依賴 Application。</summary>
public sealed class FilterHitMaterializeService(
    IFilterHitMaterializer materializer,
    IProjectStore projectStore,
    IFilterScenarioStore scenarioStore,
    IMappingStateStore mappingStore,
    ActionExecutionGate executionGate,
    ProjectSession? session = null,
    IAccountMappingStore? accountMappingStore = null,
    IAuthorizedPreparerStore? authorizedPreparerStore = null,
    IAccountCategoryStore? accountCategoryStore = null,
    IResultStaleStateStore? resultStaleStateStore = null,
    ICalendarStore? calendarStore = null,
    IProjectDatabaseCheckpoint? databaseCheckpoint = null,
    ILogger? logger = null)
{
    /// <summary>
    /// 呼叫端已經持有 dispatcher 的閘時用這個入口，例如正式匯出；這把閘由會改資料的作業共用。
    /// 這個方法不再取閘，否則同一個作業會被自己擋住。
    /// </summary>
    public Task MaterializeAllAsync(
        string projectId,
        ProjectDocument document,
        IReadOnlyList<SavedFilterScenario> scenarios,
        CancellationToken cancellationToken) =>
        MaterializeAsync(projectId, document, scenarios, null, cancellationToken);

    /// <summary>底稿只依選定情境重算；未選情境的來源缺漏不阻擋本次輸出。</summary>
    public Task MaterializeSelectedAsync(
        string projectId,
        ProjectDocument document,
        IReadOnlyList<SavedFilterScenario> scenarios,
        IReadOnlyCollection<int> selectedPositions,
        CancellationToken cancellationToken) =>
        MaterializeAsync(projectId, document, scenarios, selectedPositions, cancellationToken);

    /// <summary>
    /// 匯出直接用存好的結果時，照重算前的同一套檢查確認情境仍能套用目前規則，不重算。
    /// <paramref name="selectedPositions"/> 為 null 時檢查全部情境；底稿只檢查選定的情境。
    /// </summary>
    public async Task RequireUsableAsync(
        string projectId,
        ProjectDocument document,
        IReadOnlyList<SavedFilterScenario> scenarios,
        IReadOnlyCollection<int>? selectedPositions,
        CancellationToken cancellationToken) =>
        _ = await PrepareAsync(projectId, document, scenarios, selectedPositions, cancellationToken);

    private async Task MaterializeAsync(
        string projectId,
        ProjectDocument document,
        IReadOnlyList<SavedFilterScenario> scenarios,
        IReadOnlyCollection<int>? selectedPositions,
        CancellationToken cancellationToken)
    {
        var (materializable, context, replaceAll) =
            await PrepareAsync(projectId, document, scenarios, selectedPositions, cancellationToken);
        await materializer.MaterializeAsync(
            projectId,
            materializable,
            context,
            cancellationToken,
            replaceAll);
    }

    private async Task<(IReadOnlyList<MaterializableScenario> Scenarios, FilterConditionContext Context, bool ReplaceAll)> PrepareAsync(
        string projectId,
        ProjectDocument document,
        IReadOnlyList<SavedFilterScenario> scenarios,
        IReadOnlyCollection<int>? selectedPositions,
        CancellationToken cancellationToken)
    {
        var revision = FilterPopulationScopeParser.RequireCurrentRevision(scenarios);

        var selected = selectedPositions?.ToHashSet();
        if (selected is not null && (selected.Count == 0 || selected.Count != selectedPositions!.Count
            || selected.Any(position => !scenarios.Any(scenario => scenario.Position == position))))
        {
            throw new JetActionException(JetErrorCodes.InvalidPayload, ReportExportSupport.ScenarioListOutOfDate);
        }
        var replaceAll = selected is null || selected.Count == scenarios.Count;

        var materializable = new List<MaterializableScenario>(scenarios.Count);
        foreach (var saved in scenarios)
        {
            if (selected is not null && !selected.Contains(saved.Position)) { continue; }
            using var doc = JsonDocument.Parse(saved.DefinitionJson);
            FilterScenarioSpec spec;
            try
            {
                spec = FilterScenarioPayloadParser.Parse(doc.RootElement, document.MoneyScale);
            }
            catch (JetActionException exception) when (exception.Code == JetErrorCodes.InvalidScenario)
            {
                var corrupt = FilterScenarioCorruption.ReasonsOf(exception);
                throw FilterScenarioCorruption.Attach(
                    FilterScenarioErrorDetails.InvalidScenario(
                    [
                        FilterScenarioRuleMessages.CannotRecalculate(
                            saved.Position,
                            corrupt.Count > 0
                                ? [FilterScenarioRuleUpgrade.UnreadableDefinitionMessage]
                                : [exception.Message])
                    ]),
                    corrupt);
            }

            materializable.Add(new MaterializableScenario(saved.Position, spec) { ScenarioId = saved.ScenarioId });
        }

        // 欄位值條件能選的攸關資料元素欄位，是已確認的 GL 配對裡的欄位。
        // 沒有配對或配對裡沒有這類欄位時是空集合；用到這類欄位的條件在編譯成 SQL 之前就回錯誤，不會編出錯誤的 SQL。
        var glMapping = await mappingStore.FindAsync(projectId, DatasetKind.Gl, cancellationToken);
        var accountMapping = accountMappingStore is null ? null
            : await accountMappingStore.FindStateAsync(projectId, cancellationToken);
        var hasAuthorizedPreparers = authorizedPreparerStore is not null
            && await authorizedPreparerStore.CountAsync(projectId, cancellationToken) > 0;
        var categorySnapshot = accountCategoryStore is null ? null
            : await accountCategoryStore.ReadAsync(projectId, cancellationToken);
        FilterCalendarLists? calendarLists = calendarStore is null ? null
            : await FilterCalendarLists.ReadAsync(calendarStore, projectId, cancellationToken);
        var validationContext = FilterValidationContextFactory.Create(
            document,
            glMapping ?? new CommittedMapping(DatasetKind.Gl, new Dictionary<string, string>(),
                string.Empty, string.Empty, DateTimeOffset.MinValue),
            accountMapping, hasAuthorizedPreparers, revision.PopulationScope, categorySnapshot, calendarLists);
        foreach (var scenario in materializable)
        {
            var errors = FilterScenarioValidator.Validate(scenario.Spec, validationContext, forSave: false);
            if (errors.Count == 0) { continue; }
            // 一句寫完情境、原因與下一步；損毀的條件照待修正清單只寫一次「條件內容無法讀取」。
            throw FilterScenarioCorruption.Attach(
                FilterScenarioErrorDetails.InvalidScenario(
                    [FilterScenarioRuleMessages.CannotRecalculate(scenario.Position, FilterScenarioRuleUpgrade.ListReasons(errors))]),
                FilterScenarioCorruption.ReasonsOf(errors));
        }

        return (
            materializable,
            new FilterConditionContext(
                document.MoneyScale,
                document.LastAccountingPeriodDate,
                document.PeriodStart,
                document.PeriodEnd,
                document.NonWorkingDays,
                revision.PopulationScope)
            {
                RdeFields = glMapping?.GlOptions?.RdeFields ?? [],
                DateParseOptions = document.DateParseOptions,
                ZerosThreshold = TrailingZeroThreshold.FromCommittedGl(glMapping, document.MoneyScale)
            },
            replaceAll);
    }

    /// <summary>
    /// 給可以和其他請求同時執行的查詢在讀第一頁時呼叫。先讀資料庫裡記的篩選結果過期旗標；沒有過期就回 null，不重算。
    /// 命中零筆或搜尋不到結果，不表示需要重算。確實過期才去取 dispatcher 共用的閘，取不到就回 operation_in_progress；
    /// 取到之後再確認一次目前案件與過期旗標，然後重算全部情境。
    /// </summary>
    public async Task<FilterHitMaterializationSnapshot?> MaterializeForConcurrentQueryAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        RequireActiveProject(projectId);
        var staleStore = resultStaleStateStore
            ?? throw new InvalidOperationException("Concurrent filter materialization requires the result stale state store.");
        if (!(await staleStore.ReadAsync(projectId, cancellationToken)).Filter)
        {
            return null;
        }

        using var executionLease = executionGate.TryAcquire()
            ?? throw new JetActionException(
                JetErrorCodes.OperationInProgress,
                "另一項作業正在進行中，請待其完成後再操作。");

        // 查詢在取得閘之前可能已經讀過一次資料，並記下當時的 projectId。這段期間 project.releaseLock 可能先取得同一把閘，
        // 放掉案件鎖並離開 session。所以取得閘之後要先再確認這個案件仍是目前案件；否則另一個 JET 已經開啟這個案件之後，
        // 這個舊的查詢還會對它寫入。持有閘的期間，放鎖與載入案件都改不了 session。
        RequireActiveProject(projectId);
        var isStale = (await staleStore.ReadAsync(projectId, cancellationToken)).Filter;

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.ProjectNotFound,
                ProjectFolderMessages.ActiveProjectMissing);
        var scenarios = await scenarioStore.ListAsync(projectId, cancellationToken);
        _ = FilterPopulationScopeParser.RequireCurrentRevision(scenarios);

        if (isStale)
        {
            try
            {
                await MaterializeAllAsync(projectId, document, scenarios, cancellationToken);
            }
            finally
            {
                // 補算在併行查詢裡寫入，dispatcher 只在變更型作業結束後併回，所以這裡在放開閘之前自己併回記錄檔
                // （見 IProjectDatabaseCheckpoint）。
                if (databaseCheckpoint is not null)
                {
                    await ProjectDatabaseCheckpointSteps.TryCheckpointAsync(databaseCheckpoint, projectId, logger);
                }
            }
        }
        return new FilterHitMaterializationSnapshot(document, scenarios);
    }

    private void RequireActiveProject(string projectId)
    {
        if (session is null)
        {
            throw new InvalidOperationException("Concurrent filter materialization requires the shared project session.");
        }
        if (!string.Equals(session.CurrentProjectId, projectId, StringComparison.Ordinal))
        {
            throw new JetActionException(JetErrorCodes.NoActiveProject,
                "目前案件已離開或切換，已取消舊案件的篩選結果補算。");
        }
    }

    internal Task<string> ReadQueryDataRevisionAsync(string projectId, CancellationToken cancellationToken)
    {
        RequireActiveProject(projectId);
        return QueryStaleStore.ReadFilterDataRevisionAsync(projectId, cancellationToken);
    }

    internal async Task EnsureQueryCurrentAsync(string projectId, string expectedDataRevision,
        string expectedScenarioRevision, CancellationToken cancellationToken)
    {
        RequireActiveProject(projectId);
        var stale = await QueryStaleStore.ReadAsync(projectId, cancellationToken);
        var dataRevision = await QueryStaleStore.ReadFilterDataRevisionAsync(projectId, cancellationToken);
        var scenarios = await scenarioStore.ListAsync(projectId, cancellationToken);
        var revision = FilterPopulationScopeParser.RequireCurrentRevision(scenarios);
        RequireActiveProject(projectId);
        if (stale.Filter || dataRevision != expectedDataRevision || revision.Revision != expectedScenarioRevision)
        {
            throw FilterResultQuerySnapshot.Stale();
        }
    }

    private IResultStaleStateStore QueryStaleStore => resultStaleStateStore
        ?? throw new InvalidOperationException("Concurrent filter queries require the result stale state store.");
}
