using System.Text.Json;
using JET.Domain;

namespace JET.Application;

internal static class ReportExportSupport
{
    public static void RequireRequestedValidationRun(
        RuleRunRecord current,
        string requestedRunId)
    {
        if (!string.Equals(current.RunId, requestedRunId, StringComparison.Ordinal))
        {
            throw new JetActionException(
                JetErrorCodes.StaleResult,
                "指定的 validate 執行結果已不是目前版本，請重新執行並使用最新結果。");
        }
    }

    public static async Task<RuleRunRecord> RequireCurrentRunAsync(
        IRuleRunStore store,
        string projectId,
        string runKind,
        string requestedRunId,
        CancellationToken cancellationToken)
    {
        var current = await store.FindLatestAsync(projectId, runKind, cancellationToken);
        if (current is null
            || !string.Equals(current.RunId, requestedRunId, StringComparison.Ordinal)
            || !RuleLogicVersions.IsCurrent(current))
        {
            throw new JetActionException(
                JetErrorCodes.StaleResult,
                "指定的規則執行結果已失效，請回到對應步驟重新執行後再產出報告。");
        }

        return current;
    }

    public static string RequireRevision(
        IReadOnlyList<SavedFilterScenario> scenarios,
        string requestedRevision) =>
        RequireRevisionState(scenarios, requestedRevision).Revision;

    public static CurrentFilterRevision RequireRevisionState(
        IReadOnlyList<SavedFilterScenario> scenarios,
        string requestedRevision)
    {
        CurrentFilterRevision current;
        try
        {
            current = FilterPopulationScopeParser.RequireCurrentRevision(scenarios);
        }
        catch (JetActionException exception) when (exception.Code == JetErrorCodes.StaleResult)
        {
            throw new JetActionException(
                JetErrorCodes.StaleResult,
                "已存篩選情境來自舊版或不一致的規則，請回到進階條件篩選重新保存後再產出報告。");
        }

        if (!string.Equals(current.Revision, requestedRevision, StringComparison.Ordinal))
        {
            throw new JetActionException(
                JetErrorCodes.StaleResult,
                "指定的篩選版本已失效，請以目前已保存的情境重新完成報告。");
        }

        return current;
    }

    public static IReadOnlyList<int> ReadScenarioPositions(
        JsonElement payload,
        IReadOnlyList<SavedFilterScenario> scenarios)
    {
        if (!payload.TryGetProperty("scenarioPositions", out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                "payload 缺少必填陣列 'scenarioPositions'。");
        }

        var valid = scenarios.Select(item => item.Position).ToHashSet();
        var selected = new List<int>();
        var seen = new HashSet<int>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number
                || !item.TryGetInt32(out var position)
                || !valid.Contains(position)
                || !seen.Add(position))
            {
                throw new JetActionException(
                    JetErrorCodes.InvalidPayload,
                    "scenarioPositions 必須是目前已保存且不重複的情境位置。");
            }

            selected.Add(position);
        }

        if (selected.Count == 0)
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                "scenarioPositions 至少要選擇一個情境。");
        }

        selected.Sort();
        return selected;
    }

    public static async Task<ReportArtifact> RequireCurrentCriteriaSelectionReportAsync(
        IReportArtifactStore store,
        string projectId,
        ReportArtifactCatalog refreshedCatalog,
        CancellationToken cancellationToken)
    {
        var current = refreshedCatalog.Artifacts
            .Where(artifact => artifact.Kind == ReportArtifactKind.CriteriaSelectionReport)
            .Where(artifact => !artifact.Stale)
            .OrderByDescending(artifact => artifact.GeneratedUtc)
            .FirstOrDefault();

        if (current is null)
        {
            throw MissingCurrentCriteriaSelectionReport();
        }

        try
        {
            _ = await store.ResolvePathAsync(projectId, current.ArtifactId, cancellationToken);
        }
        catch (JetActionException exception) when (
            exception.Code is JetErrorCodes.FileNotFound or JetErrorCodes.FileReadError)
        {
            throw MissingCurrentCriteriaSelectionReport();
        }

        return current;
    }

    private static JetActionException MissingCurrentCriteriaSelectionReport() => new(
        JetErrorCodes.StaleResult,
        "目前的驗證與篩選版本尚無有效的 CriteriaSelectionReport，請回到進階條件篩選重新產生報告。");

    public static ReportDocumentContext ProjectContext(ProjectDocument document) => new(
        document.ProjectId,
        document.EntityName,
        document.PeriodStart,
        document.PeriodEnd,
        document.LastAccountingPeriodDate,
        document.MoneyScale);

    public static object ArtifactWire(ReportArtifact artifact) => new
    {
        artifactId = artifact.ArtifactId,
        kind = ReportArtifactKindValues.ToValue(artifact.Kind),
        fileName = artifact.RelativeFileName,
        generatedUtc = artifact.GeneratedUtc,
        bytes = artifact.Bytes,
        sha256 = artifact.Sha256,
        sourceRef = new
        {
            validationRunId = artifact.SourceRef.ValidationRunId,
            prescreenRunId = artifact.SourceRef.PrescreenRunId,
            scenarioRevision = artifact.SourceRef.ScenarioRevision,
            scenarioPositions = artifact.SourceRef.ScenarioPositions
        },
        stale = artifact.Stale
    };

    public static bool IsSourceStale(
        ReportArtifact artifact,
        RuleRunRecord? latestValidate,
        RuleRunRecord? latestPrescreen,
        string? filterRevision,
        IReadOnlyCollection<int> currentScenarioPositions)
        => IsSourceStale(
            artifact,
            latestValidate,
            latestPrescreen,
            filterStale: false,
            filterRevision,
            currentScenarioPositions);

    public static bool IsSourceStale(
        ReportArtifact artifact,
        RuleRunRecord? latestValidate,
        RuleRunRecord? latestPrescreen,
        bool filterStale,
        string? filterRevision,
        IReadOnlyCollection<int> currentScenarioPositions)
    {
        var source = artifact.SourceRef;
        return artifact.Kind switch
        {
            ReportArtifactKind.ValidationReport
                or ReportArtifactKind.AccountMapping
                or ReportArtifactKind.InfReport
                => source.ValidationRunId is null
                    || !string.Equals(
                        source.ValidationRunId,
                        latestValidate?.RunId,
                        StringComparison.Ordinal),
            ReportArtifactKind.PrescreenReport
                => source.PrescreenRunId is null
                    || !string.Equals(
                        source.PrescreenRunId,
                        latestPrescreen?.RunId,
                        StringComparison.Ordinal),
            ReportArtifactKind.CriteriaSelectionReport
                => filterStale
                    || source.ValidationRunId is null
                    || !string.Equals(
                        source.ValidationRunId,
                        latestValidate?.RunId,
                        StringComparison.Ordinal)
                    || source.ScenarioRevision is null
                    || !string.Equals(
                        source.ScenarioRevision,
                        filterRevision,
                        StringComparison.Ordinal)
                    || !HasExactScenarioPositions(source.ScenarioPositions, currentScenarioPositions),
            ReportArtifactKind.WorkingPaper
                => filterStale
                    || source.ValidationRunId is null
                    || !string.Equals(
                        source.ValidationRunId,
                        latestValidate?.RunId,
                        StringComparison.Ordinal)
                    || source.ScenarioRevision is null
                    || !string.Equals(
                        source.ScenarioRevision,
                        filterRevision,
                        StringComparison.Ordinal)
                    || !HasValidSelectedPositions(source.ScenarioPositions, currentScenarioPositions),
            _ => true
        };
    }

    private static bool HasExactScenarioPositions(
        IReadOnlyList<int>? sourcePositions,
        IReadOnlyCollection<int> currentPositions)
        => sourcePositions is { Count: > 0 }
            && sourcePositions.Order().SequenceEqual(currentPositions.Order());

    private static bool HasValidSelectedPositions(
        IReadOnlyList<int>? sourcePositions,
        IReadOnlyCollection<int> currentPositions)
    {
        if (sourcePositions is not { Count: > 0 })
        {
            return false;
        }

        var current = currentPositions.ToHashSet();
        return sourcePositions.All(current.Contains);
    }
}
