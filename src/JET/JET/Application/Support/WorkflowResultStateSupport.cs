using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

/// <summary>Writes return the backend impact and current state; the UI only mirrors these bounded facts.</summary>
internal static partial class WorkflowResultStateSupport
{
    /// <summary>
    /// 本次修改依失效政策作廢的結果。FilterScenarios 表示已存篩選情境也一併清除
    /// （使用者 2026-10-07 裁定上游修改清除下游），JSON 名稱為 filterScenarios。
    /// </summary>
    internal sealed record Invalidation(bool Validation, bool Prescreen, bool Filter, bool FilterScenarios, bool AccountMapping);

    internal sealed record CurrentSnapshot(
        AuditResultStaleState StaleState,
        RuleRunRecord? ValidationRun,
        RuleRunRecord? PrescreenRun,
        CurrentFilterRevision? FilterRevision,
        IReadOnlyList<int> ScenarioPositions,
        string FilterDataRevision);

    internal sealed record MutationState(
        Invalidation InvalidatedResults,
        AuditResultStaleState? StaleState,
        object[]? ReportArtifacts,
        string? ReportArtifactWarning);

    // 使用者 2026-10-13 勾選頁確認的句子。
    private const string MutationCatalogWarning =
        "變更已儲存，但報告清單沒有更新；重新開啟案件就會更新，不必重做變更。";

    // 變更已提交、卻讀不回畫面要用的狀態時的提醒，句型比照上一句；使用者 2026-10-19 在視窗內選擇題確認。
    internal const string MutationStateWarning =
        "變更已儲存，但畫面上的結果狀態與報告清單沒有更新；重新開啟案件就會更新，不必重做變更。";

    /// <summary>
    /// 變更已提交、但讀不回畫面要用的狀態：不能把已提交的變更報成失敗。結果狀態與報告清單回 null，
    /// 前端保留畫面上的值並顯示提醒，重新開啟案件時全部重讀（使用者 2026-10-19 裁定回報成功並提醒）。
    /// </summary>
    internal static MutationState StateUnavailable(Invalidation invalidation) =>
        new(invalidation, null, null, MutationStateWarning);

    /// <summary>
    /// 讀不回狀態時改成回報成功，原本由失敗動作留下的錯誤紀錄跟著消失；這裡在支援日誌補記讀取階段與錯誤類型，
    /// 不記訊息內容（獨立複審 2026-10-19 指出）。phase 是 result_state 或 authorized_preparer_state。
    /// </summary>
    [LoggerMessage(EventName = "workflow_state.read_failed", Level = LogLevel.Warning,
        Message = "Change committed; {phase} not read back. Error type: {error_type}")]
    internal static partial void StateReadFailed(ILogger logger, string phase, string error_type);

    internal static CurrentSnapshot Resolve(
        AuditResultStaleState stored,
        RuleRunRecord? validation,
        RuleRunRecord? prescreen,
        IReadOnlyList<SavedFilterScenario> scenarios,
        string filterDataRevision)
    {
        var oldValidation = validation is not null && !RuleLogicVersions.IsCurrent(validation);
        var oldPrescreen = prescreen is not null && !RuleLogicVersions.IsCurrent(prescreen);
        CurrentFilterRevision? revision = null;
        if (scenarios.Count > 0)
        {
            try { revision = FilterPopulationScopeParser.RequireCurrentRevision(scenarios); }
            catch (JetActionException exception) when (exception.Code == JetErrorCodes.StaleResult)
            {
                // Keep definitions available for correction, but never publish their old hits as current.
            }
        }
        return new CurrentSnapshot(
            new AuditResultStaleState(stored.Validation || oldValidation, stored.Prescreen || oldPrescreen,
                stored.Filter || scenarios.Count > 0 && revision is null),
            oldValidation ? null : validation,
            oldPrescreen ? null : prescreen,
            revision,
            scenarios.Select(item => item.Position).ToArray(),
            filterDataRevision);
    }

    internal static async Task<CurrentSnapshot> ReadCurrentAsync(
        string projectId, IRuleRunStore ruleRuns, IResultStaleStateStore staleStates,
        IFilterScenarioStore filterScenarios, CancellationToken cancellationToken)
    {
        var stale = await staleStates.ReadAsync(projectId, cancellationToken);
        var validation = await ruleRuns.FindLatestAsync(projectId, RuleRunKinds.Validate, cancellationToken);
        var prescreen = await ruleRuns.FindLatestAsync(projectId, RuleRunKinds.Prescreen, cancellationToken);
        var scenarios = await filterScenarios.ListAsync(projectId, cancellationToken);
        var revision = await staleStates.ReadFilterDataRevisionAsync(projectId, cancellationToken);
        return Resolve(stale, validation, prescreen, scenarios, revision);
    }

    internal static async Task<MutationState> AfterMutationAsync(
        string projectId, IRuleRunStore ruleRuns, IResultStaleStateStore staleStates,
        IFilterScenarioStore filterScenarios, IReportArtifactStore artifactStore, AuditMutationEffects? effects,
        ILogger? logger)
    {
        var invalidation = new Invalidation(effects?.InvalidateValidation ?? false, effects?.InvalidatePrescreen ?? false,
            effects?.InvalidateFilterHits ?? false, effects?.InvalidateFilterScenarioDefinitions ?? false,
            effects?.ClearAccountMapping ?? false);

        // The mutation has committed. A late caller cancellation must not discard its authoritative state.
        CurrentSnapshot current;
        try
        {
            current = await ReadCurrentAsync(projectId, ruleRuns, staleStates, filterScenarios, CancellationToken.None);
        }
        catch (Exception error)
        {
            if (logger is not null) StateReadFailed(logger, "result_state", error.GetType().Name);
            return StateUnavailable(invalidation);
        }

        var catalog = await ReportExportSupport.ReadArtifactCatalogAfterPublicationAsync(
            artifactStore, projectId,
            refreshSources: token => MarkArtifactsAsync(projectId, artifactStore, current, token),
            unavailableMessage: MutationCatalogWarning);
        return new MutationState(invalidation, current.StaleState, catalog.Artifacts, catalog.Warning);
    }

    internal static Task<ReportExportSupport.PublishedArtifactCatalog> AfterPublicationAsync(
        string projectId, IRuleRunStore ruleRuns, IResultStaleStateStore staleStates,
        IFilterScenarioStore filterScenarios, IReportArtifactStore artifactStore) =>
        ReportExportSupport.ReadArtifactCatalogAfterPublicationAsync(
            artifactStore, projectId,
            refreshSources: async token =>
            {
                var current = await ReadCurrentAsync(projectId, ruleRuns, staleStates, filterScenarios, token);
                await MarkArtifactsAsync(projectId, artifactStore, current, token);
            });

    private static Task MarkArtifactsAsync(string projectId, IReportArtifactStore store,
        CurrentSnapshot current, CancellationToken cancellationToken) =>
        ReportExportSupport.RefreshArtifactsAsync(projectId, current.ValidationRun, current.PrescreenRun,
            current.StaleState.Filter, current.FilterRevision?.Revision, current.ScenarioPositions,
            store, cancellationToken, current.FilterDataRevision);
}
