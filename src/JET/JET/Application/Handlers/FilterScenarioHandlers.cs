using System.Text.Json;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

// 第五步一次只存、刪或重新檢查一個情境（使用者 2026-10-13 對「情境卡住」選「本計畫內修好」）。
// 三個 handler 只負責取出用到的資料庫介面；讀出整批、決定寫回內容與組回應都在 FilterScenarioBatchWriter。
// filter.commit 保留整批取代，正式畫面不再呼叫，供程式化佈置與既有測試使用。

/// <summary>filter.scenario.save：新增、另存或取代一個情境，其他情境照資料庫原樣保留。</summary>
public sealed class FilterScenarioSaveHandler : IApplicationActionHandler
{
    private readonly IProjectStore projectStore;
    private readonly ProjectSession session;
    private readonly ILogger? logger;

    internal FilterScenarioSaveHandler(IProjectStore projectStore, ProjectSession session, ILogger? logger = null)
    {
        this.projectStore = projectStore;
        this.session = session;
        this.logger = logger;
    }

    public string Action => FilterScenarioBatchWriter.SaveAction;

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var stores = new FilterScenarioStores(
            repositories.FilterScenarios,
            repositories.FilterFacts,
            repositories.RuleRuns,
            repositories.ResultStaleStates,
            repositories.MappingStates,
            repositories.AccountMappings,
            repositories.AuthorizedPreparers,
            repositories.AccountTaxonomy);
        var prepared = await FilterScenarioBatchWriter.LoadAsync(projectStore, projectId, stores, payload, cancellationToken);
        var saved = await stores.Scenarios.ListAsync(projectId, cancellationToken);
        var nowUtc = DateTimeOffset.UtcNow;
        var change = FilterScenarioBatchWriter.PlanSave(prepared, saved, payload, nowUtc);
        return await FilterScenarioBatchWriter.PublishAsync(
            Action, projectStore, stores, prepared, change, skipWhenEmpty: false, nowUtc, logger, cancellationToken);
    }
}

/// <summary>filter.scenario.remove：移除一個情境，其他情境照資料庫原樣保留。</summary>
public sealed class FilterScenarioRemoveHandler : IApplicationActionHandler
{
    private readonly IProjectStore projectStore;
    private readonly ProjectSession session;
    private readonly ILogger? logger;

    internal FilterScenarioRemoveHandler(IProjectStore projectStore, ProjectSession session, ILogger? logger = null)
    {
        this.projectStore = projectStore;
        this.session = session;
        this.logger = logger;
    }

    public string Action => FilterScenarioBatchWriter.RemoveAction;

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var stores = new FilterScenarioStores(
            repositories.FilterScenarios,
            repositories.FilterFacts,
            repositories.RuleRuns,
            repositories.ResultStaleStates,
            repositories.MappingStates,
            repositories.AccountMappings,
            repositories.AuthorizedPreparers,
            repositories.AccountTaxonomy);
        var prepared = await FilterScenarioBatchWriter.LoadAsync(projectStore, projectId, stores, payload, cancellationToken);
        var saved = await stores.Scenarios.ListAsync(projectId, cancellationToken);
        var change = FilterScenarioBatchWriter.PlanRemove(saved, payload);
        return await FilterScenarioBatchWriter.PublishAsync(
            Action, projectStore, stores, prepared, change, skipWhenEmpty: false, DateTimeOffset.UtcNow, logger,
            cancellationToken);
    }
}

/// <summary>
/// filter.scenario.recheck：用目前規則重新檢查全部已存情境，能套用的改用目前規則，不能套用的列出原因，不整批失敗。
/// </summary>
public sealed class FilterScenarioRecheckHandler : IApplicationActionHandler
{
    private readonly IProjectStore projectStore;
    private readonly ProjectSession session;
    private readonly ILogger? logger;

    internal FilterScenarioRecheckHandler(IProjectStore projectStore, ProjectSession session, ILogger? logger = null)
    {
        this.projectStore = projectStore;
        this.session = session;
        this.logger = logger;
    }

    public string Action => FilterScenarioBatchWriter.RecheckAction;

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var stores = new FilterScenarioStores(
            repositories.FilterScenarios,
            repositories.FilterFacts,
            repositories.RuleRuns,
            repositories.ResultStaleStates,
            repositories.MappingStates,
            repositories.AccountMappings,
            repositories.AuthorizedPreparers,
            repositories.AccountTaxonomy);
        var prepared = await FilterScenarioBatchWriter.LoadAsync(projectStore, projectId, stores, payload, cancellationToken);
        var saved = await stores.Scenarios.ListAsync(projectId, cancellationToken);
        // 清單是空的就不寫入；畫面在沒有情境時本來就不會送出。
        return await FilterScenarioBatchWriter.PublishAsync(
            Action, projectStore, stores, prepared, FilterScenarioBatchWriter.PlanRecheck(saved), skipWhenEmpty: true,
            DateTimeOffset.UtcNow, logger, cancellationToken);
    }
}
