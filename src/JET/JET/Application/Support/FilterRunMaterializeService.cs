using System.Text.Json;
using JET.Domain;

namespace JET.Application;

public sealed record FilterRunMaterializationSnapshot(
    ProjectDocument Document,
    IReadOnlyList<SavedFilterScenario> Scenarios);

/// <summary>把全部已存篩選情境(definition JSON → spec)落地到 result_filter_run 的共用編排
/// (filterHitsPage 與 D2 tag 矩陣 handler 共用;materializer 為 replace-all)。解析屬 Application 層,
/// materializer 只吃 Domain 型別,維持 Infrastructure 不反向依賴 Application。</summary>
public sealed class FilterRunMaterializeService(
    IFilterRunMaterializer materializer,
    IProjectStore projectStore,
    IFilterScenarioStore scenarioStore,
    IMappingStateStore mappingStore,
    ActionExecutionGate executionGate,
    ProjectSession? session = null)
{
    /// <summary>
    /// 已由 dispatcher exclusive 閘保護的呼叫路徑（正式匯出）使用此入口；本方法不重複取閘，
    /// 避免同一作業重入自鎖。
    /// </summary>
    public async Task MaterializeAllAsync(
        string projectId,
        ProjectDocument document,
        IReadOnlyList<SavedFilterScenario> scenarios,
        CancellationToken cancellationToken)
    {
        var revision = FilterPopulationScopeParser.RequireCurrentRevision(scenarios);

        var materializable = new List<MaterializableScenario>(scenarios.Count);
        foreach (var saved in scenarios)
        {
            using var doc = JsonDocument.Parse(saved.DefinitionJson);
            var spec = FilterScenarioPayloadParser.Parse(doc.RootElement, document.MoneyScale);
            materializable.Add(new MaterializableScenario(saved.Position, spec));
        }

        // typed 條件（type:"typed"）的編譯 registry：目前 committed GL mapping 的 RDE definitions。
        // mapping 缺席或無 RDE 時為空集合，typed 條件在編譯前 fail loud，不會編出錯誤 SQL。
        var glMapping = await mappingStore.FindAsync(projectId, DatasetKind.Gl, cancellationToken);

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
                RdeFields = glMapping?.GlOptions?.RdeFields ?? []
            },
            cancellationToken);
    }

    /// <summary>
    /// concurrent query 只有在空結果需要補算時走此入口。非阻塞取得與 dispatcher 相同的閘後，
    /// 重新讀取目前專案與情境，避免用進閘前可能已失效的 snapshot 寫回結果。
    /// </summary>
    public async Task<FilterRunMaterializationSnapshot> MaterializeForConcurrentQueryAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        using var executionLease = executionGate.TryAcquire()
            ?? throw new JetActionException(
                JetErrorCodes.OperationInProgress,
                "另一項作業正在進行中，請待其完成後再操作。");

        // Query 在進閘前可能已完成一次純讀並捕捉 projectId；其間 releaseLock 可能先取得同一把閘、
        // 成功放鎖並離開 session。取得閘後必須先重驗 active project，否則舊 query 會在第二程序
        // 已接手案件後才對 former project 惰性寫入。持有閘期間 release/load 都不能改變 session。
        if (session is null)
        {
            throw new InvalidOperationException(
                "Concurrent filter materialization requires the shared project session.");
        }

        if (!string.Equals(session.CurrentProjectId, projectId, StringComparison.Ordinal))
        {
            throw new JetActionException(
                JetErrorCodes.NoActiveProject,
                "目前案件已離開或切換，已取消舊案件的篩選結果補算。");
        }

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.ProjectNotFound,
                $"找不到專案 '{projectId}'。");
        var scenarios = await scenarioStore.ListAsync(projectId, cancellationToken);
        _ = FilterPopulationScopeParser.RequireCurrentRevision(scenarios);

        await MaterializeAllAsync(projectId, document, scenarios, cancellationToken);
        return new FilterRunMaterializationSnapshot(document, scenarios);
    }
}
