using System.Text.Json;
using JET.Domain;

namespace JET.Application;

internal static class ReportExportSupport
{
    public static async Task<object[]> ReadArtifactCatalogAfterPublicationAsync(
        IReportArtifactStore store, string projectId)
    {
        // 正式檔與索引已保存；回應必須包含容量整理後的清單，最後一刻的取消不隱藏已完成成果。
        var artifacts = await store.ListAsync(projectId, CancellationToken.None);
        return artifacts.Select(ArtifactWire).ToArray();
    }

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
            var button = string.Equals(runKind, RuleRunKinds.Prescreen, StringComparison.Ordinal) ? "重新執行預篩選" : "重新執行驗證";
            throw new JetActionException(
                JetErrorCodes.StaleResult,
                $"這份報告依據的結果已不是目前資料的結果。請回到「資料驗證與測試」按「{button}」，再回來產生報告。");
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
                "已保存的情境來自較早的版本。請回到「進階條件篩選」按「以查核期間重新保存」，再按「重新產生條件篩選報告」。");
        }

        if (!string.Equals(current.Revision, requestedRevision, StringComparison.Ordinal))
        {
            throw new JetActionException(
                JetErrorCodes.StaleResult,
                "畫面上的情境版本和已保存的不一致。請重新載入這個案件，再用目前已保存的情境產生一次。");
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

    /// <summary>
    /// 先依目前的 run 與篩選版本把過期報告標為 stale，再列出清單。stale 判定只委派
    /// <see cref="IsSourceStale(ReportArtifact, RuleRunRecord?, RuleRunRecord?, bool, string?, IReadOnlyCollection{int})"/>，
    /// 不建立第二份 run／revision 規則。
    /// </summary>
    public static async Task<IReadOnlyList<ReportArtifact>> RefreshArtifactsAsync(
        string projectId,
        RuleRunRecord? latestValidate,
        RuleRunRecord? latestPrescreen,
        bool filterStale,
        string? currentFilterRevision,
        IReadOnlyCollection<int> scenarioPositions,
        IReportArtifactStore artifactStore,
        CancellationToken cancellationToken)
    {
        await artifactStore.MarkStaleAsync(
            projectId,
            artifact => IsSourceStale(
                artifact,
                latestValidate,
                latestPrescreen,
                filterStale,
                currentFilterRevision,
                scenarioPositions),
            cancellationToken);
        return await artifactStore.ListAsync(projectId, cancellationToken);
    }

    public static async Task<ReportArtifact> RequireCurrentCriteriaSelectionReportAsync(
        IReportArtifactStore store,
        string projectId,
        IReadOnlyList<ReportArtifact> refreshedArtifacts,
        CancellationToken cancellationToken)
    {
        var current = refreshedArtifacts
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
        "還沒有目前資料的條件篩選報告。請回到「進階條件篩選」按「重新產生條件篩選報告」，系統會用目前資料重新計算，然後回來匯出。");

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
        fileState = ReportArtifactFileStateValues.ToValue(artifact.FileState),
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
