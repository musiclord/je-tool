using System.Text.Json;
using JET.Domain;

namespace JET.Application;

public sealed record FilterRunMaterializationSnapshot(
    ProjectDocument Document,
    IReadOnlyList<SavedFilterScenario> Scenarios);

/// <summary>把已存篩選情境(definition JSON → spec)落地到 result_filter_run 的共用編排。
/// 查詢矩陣重算全案，底稿只重算所選情境。解析屬 Application 層,
/// materializer 只吃 Domain 型別,維持 Infrastructure 不反向依賴 Application。</summary>
public sealed class FilterRunMaterializeService(
    IFilterRunMaterializer materializer,
    IProjectStore projectStore,
    IFilterScenarioStore scenarioStore,
    IMappingStateStore mappingStore,
    ActionExecutionGate executionGate,
    ProjectSession? session = null,
    IAccountMappingStore? accountMappingStore = null,
    IAuthorizedPreparerStore? authorizedPreparerStore = null,
    IAccountTaxonomyStore? accountTaxonomyStore = null,
    IResultStaleStateStore? resultStaleStateStore = null)
{
    /// <summary>
    /// 已由 dispatcher exclusive 閘保護的呼叫路徑（正式匯出）使用此入口；本方法不重複取閘，
    /// 避免同一作業重入自鎖。
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

    private async Task MaterializeAsync(
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
            throw new JetActionException(JetErrorCodes.InvalidPayload, "請選擇目前已儲存且不重複的情境位置。");
        }
        var replaceAll = selected is null || selected.Count == scenarios.Count;

        var materializable = new List<MaterializableScenario>(scenarios.Count);
        foreach (var saved in scenarios)
        {
            if (selected is not null && !selected.Contains(saved.Position)) { continue; }
            using var doc = JsonDocument.Parse(saved.DefinitionJson);
            var spec = FilterScenarioPayloadParser.Parse(doc.RootElement, document.MoneyScale);
            materializable.Add(new MaterializableScenario(saved.Position, spec));
        }

        // typed 條件（type:"typed"）的編譯 registry：目前 committed GL mapping 的 RDE definitions。
        // mapping 缺席或無 RDE 時為空集合，typed 條件在編譯前 fail loud，不會編出錯誤 SQL。
        var glMapping = await mappingStore.FindAsync(projectId, DatasetKind.Gl, cancellationToken);
        var accountMapping = accountMappingStore is null ? null
            : await accountMappingStore.FindStateAsync(projectId, cancellationToken);
        var hasAuthorizedPreparers = authorizedPreparerStore is not null
            && await authorizedPreparerStore.CountAsync(projectId, cancellationToken) > 0;
        var taxonomy = accountTaxonomyStore is null ? null
            : await accountTaxonomyStore.ReadAsync(projectId, cancellationToken);
        var validationContext = FilterValidationContextFactory.Create(
            document,
            glMapping ?? new CommittedMapping(DatasetKind.Gl, new Dictionary<string, string>(),
                string.Empty, string.Empty, DateTimeOffset.MinValue),
            accountMapping, hasAuthorizedPreparers, revision.PopulationScope, taxonomy);
        foreach (var scenario in materializable)
        {
            var errors = FilterScenarioValidator.Validate(scenario.Spec, validationContext, forSave: false);
            if (errors.Count == 0) { continue; }
            throw FilterScenarioErrorDetails.InvalidScenario(
                [$"已儲存的情境 {scenario.Position} 暫時無法重新計算。情境設定仍保留；請回到「欄位配對」補回所需欄位，或到「進階條件篩選」修改該情境後重新儲存。", .. errors]);
        }

        await materializer.MaterializeAsync(
            projectId,
            materializable,
            new FilterRuleContext(
                document.MoneyScale,
                document.LastAccountingPeriodDate,
                document.PeriodStart,
                document.PeriodEnd,
                document.NonWorkingDays,
                revision.PopulationScope)
            {
                RdeFields = glMapping?.GlOptions?.RdeFields ?? [],
                DateParseOptions = document.DateParseOptions
            },
            cancellationToken,
            replaceAll);
    }

    /// <summary>
    /// concurrent query 首頁先檢查持久化失效狀態；零筆命中或搜尋無結果不代表需要補算。
    /// 只有實際失效才取 dispatcher 共用閘，鎖內重新確認案件與失效狀態。
    /// </summary>
    public async Task<FilterRunMaterializationSnapshot?> MaterializeForConcurrentQueryAsync(
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

        // Query 在進閘前可能已完成一次純讀並捕捉 projectId；其間 releaseLock 可能先取得同一把閘、
        // 成功放鎖並離開 session。取得閘後必須先重驗 active project，否則舊 query 會在第二程序
        // 已接手案件後才對 former project 惰性寫入。持有閘期間 release/load 都不能改變 session。
        RequireActiveProject(projectId);
        var isStale = (await staleStore.ReadAsync(projectId, cancellationToken)).Filter;

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.ProjectNotFound,
                $"找不到專案 '{projectId}'。");
        var scenarios = await scenarioStore.ListAsync(projectId, cancellationToken);
        _ = FilterPopulationScopeParser.RequireCurrentRevision(scenarios);

        if (isStale)
        {
            await MaterializeAllAsync(projectId, document, scenarios, cancellationToken);
        }
        return new FilterRunMaterializationSnapshot(document, scenarios);
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
