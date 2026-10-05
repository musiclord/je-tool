using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>Writes return the backend impact and current state; the UI only mirrors these bounded facts.</summary>
internal static class WorkflowResultStateSupport
{
    internal sealed record Invalidation(bool Validation, bool Prescreen, bool Filter);

    internal sealed record CurrentSnapshot(
        AuditResultStaleState StaleState,
        RuleRunRecord? ValidationRun,
        RuleRunRecord? PrescreenRun,
        CurrentFilterRevision? FilterRevision,
        IReadOnlyList<int> ScenarioPositions,
        string FilterDataRevision);

    internal sealed record MutationState(
        Invalidation InvalidatedResults,
        AuditResultStaleState StaleState,
        object[]? ReportArtifacts,
        string? ReportArtifactWarning);

    private const string MutationCatalogWarning =
        "變更已儲存，報告清單暫時無法更新。請稍後重新開啟案件查看，無須重做變更。";

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
        IFilterScenarioStore filterScenarios, IReportArtifactStore artifactStore, AuditMutationEffects? effects)
    {
        // The mutation has committed. A late caller cancellation must not discard its authoritative state.
        var current = await ReadCurrentAsync(projectId, ruleRuns, staleStates, filterScenarios, CancellationToken.None);
        var catalog = await ReportExportSupport.ReadArtifactCatalogAfterPublicationAsync(
            artifactStore, projectId,
            refreshSources: token => MarkArtifactsAsync(projectId, artifactStore, current, token),
            unavailableMessage: MutationCatalogWarning);
        return new MutationState(
            new Invalidation(effects?.InvalidateValidation ?? false, effects?.InvalidatePrescreen ?? false,
                effects?.InvalidateFilterHits ?? false),
            current.StaleState, catalog.Artifacts, catalog.Warning);
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
