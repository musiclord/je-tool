using System.Text.Json;
using JET.Domain;

namespace JET.Application;

internal static class ReportExportSupport
{
    /// <summary>
    /// 畫面持有的情境清單和已儲存的不一致（使用者 2026-10-14 第三輪勾選頁確認 R3-35，句型同已確認的 N3）。
    /// 產生報告、底稿與重新計算選錯情境位置時共用。
    /// </summary>
    internal const string ScenarioListOutOfDate =
        "畫面上的情境清單與已儲存的內容不一致，本次未產生。請重新開啟案件後再操作。";

    internal sealed record PublishedArtifactCatalog(object[]? Artifacts, string? Warning);

    /// <summary>
    /// 報告清單更新期限用的時間來源，只讓測試在同一個非同步流程內替換；正式程式一律用系統時間。
    /// 資料形狀紀錄的流程要確定走到清單更新完成的分支，不能隨機器快慢改走逾時分支（2026-10-19）。
    /// </summary>
    internal static AsyncLocal<TimeProvider?> CatalogRefreshClock { get; } = new();

    public static async Task<PublishedArtifactCatalog> ReadArtifactCatalogAfterPublicationAsync(
        IReportArtifactStore store, string projectId, TimeSpan? refreshTimeout = null,
        Func<CancellationToken, Task>? refreshSources = null, string? unavailableMessage = null)
    {
        // 正式檔與索引已保存。清單刷新失敗或遇到其他程序持鎖，不得把已完成的匯出改報失敗。
        // 此期限只約束可省略的清單刷新，與使用者的作業取消無關，不套用到正式檔案發布。
        using var timeout = new CancellationTokenSource(
            refreshTimeout ?? TimeSpan.FromSeconds(3), CatalogRefreshClock.Value ?? TimeProvider.System);
        var refreshToken = timeout.Token;
        try
        {
            // The deadline covers source refresh and stale marking as well as the final list read.
            // Refresh may begin with synchronous local database work, so start that optional phase off-thread.
            var pending = refreshSources is null ? store.ListAsync(projectId, refreshToken)
                : Task.Run(async () =>
                {
                    await refreshSources(refreshToken);
                    refreshToken.ThrowIfCancellationRequested();
                    return await store.ListAsync(projectId, refreshToken);
                }, CancellationToken.None);
            // WaitAsync 超時會解除等待；不配合取消的 store 日後失敗時，仍需觀察該 task 的例外。
            // 這不改變下方 await：期限內的程式錯誤仍照常拋出。
            _ = pending.ContinueWith(static completed => { _ = completed.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            var artifacts = await pending.WaitAsync(refreshToken);
            return new(artifacts.Select(ArtifactWire).ToArray(), null);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return CatalogUnavailable(unavailableMessage);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            || exception is JetActionException { Code: JetErrorCodes.FileReadError })
        {
            return CatalogUnavailable(unavailableMessage);
        }
    }

    private static PublishedArtifactCatalog CatalogUnavailable(string? message) => new(null, message ??
        "檔案已產生，但報告清單沒有更新；重新開啟案件就會更新，不必重新產生。");

    /// <summary>產生報告回應的 warnings：報告已發布、卻寫不進操作紀錄時放那一句提醒，其他時候是空陣列。</summary>
    public static string[] Warnings(string? auditLogWarning) => auditLogWarning is null ? [] : [auditLogWarning];

    public static void RequireRequestedValidationRun(
        RuleRunRecord current,
        string requestedRunId)
    {
        if (!string.Equals(current.RunId, requestedRunId, StringComparison.Ordinal))
        {
            throw new JetActionException(
                JetErrorCodes.StaleResult,
                "這份報告依據的資料驗證結果已不是目前的結果，請重新執行資料驗證後再產生。");
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
        catch (JetActionException exception) when (exception.Code == JetErrorCodes.StaleResult && scenarios.Count > 0)
        {
            throw new JetActionException(
                JetErrorCodes.StaleResult,
                FilterScenarioRuleMessages.NotYetUpgraded,
                innerException: exception);
        }

        if (!string.Equals(current.Revision, requestedRevision, StringComparison.Ordinal))
        {
            throw new JetActionException(
                JetErrorCodes.StaleResult,
                ScenarioListOutOfDate);
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
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "scenario_positions_missing");
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
                    ScenarioListOutOfDate);
            }

            selected.Add(position);
        }

        if (selected.Count == 0)
        {
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "scenario_positions_empty");
        }

        selected.Sort();
        return selected;
    }

    /// <summary>
    /// 依目前的 run 與篩選版本把過期報告標為 stale，不另讀取未使用的清單。stale 判定只委派
    /// <see cref="IsSourceStale(ReportArtifact, RuleRunRecord?, RuleRunRecord?, bool, string?, IReadOnlyCollection{int}, string?)"/>，
    /// 不建立第二份 run／revision 規則。
    /// </summary>
    public static async Task RefreshArtifactsAsync(
        string projectId,
        RuleRunRecord? latestValidate,
        RuleRunRecord? latestPrescreen,
        bool filterStale,
        string? currentFilterRevision,
        IReadOnlyCollection<int> scenarioPositions,
        IReportArtifactStore artifactStore,
        CancellationToken cancellationToken,
        string? currentFilterDataRevision = null)
    {
        await artifactStore.MarkStaleAsync(
            projectId,
            artifact => IsSourceStale(
                artifact,
                latestValidate,
                latestPrescreen,
                filterStale,
                currentFilterRevision,
                scenarioPositions,
                currentFilterDataRevision),
            cancellationToken);
    }

    public static ReportDocumentContext ProjectContext(ProjectDocument document) => new(
        document.ProjectId,
        CompanyName(document),
        document.PeriodStart,
        document.PeriodEnd,
        document.LastAccountingPeriodDate,
        document.MoneyScale);

    public static string CompanyName(ProjectDocument document) =>
        string.IsNullOrWhiteSpace(document.EntityName) ? document.ProjectId : document.EntityName;

    public static object ArtifactWire(ReportArtifact artifact) => new
    {
        artifactId = artifact.ArtifactId,
        kind = ReportArtifactKindValues.ToValue(artifact.Kind),
        fileName = artifact.RelativeFileName,
        fullPath = artifact.FullPath,
        generatedUtc = artifact.GeneratedUtc,
        bytes = artifact.Bytes,
        fileState = ReportArtifactFileStateValues.ToValue(artifact.FileState),
        sourceRef = new
        {
            validationRunId = artifact.SourceRef.ValidationRunId,
            prescreenRunId = artifact.SourceRef.PrescreenRunId,
            scenarioRevision = artifact.SourceRef.ScenarioRevision,
            scenarioPositions = artifact.SourceRef.ScenarioPositions,
            filterDataRevision = artifact.SourceRef.FilterDataRevision
        },
        stale = artifact.Stale
    };

    public static bool IsSourceStale(
        ReportArtifact artifact,
        RuleRunRecord? latestValidate,
        RuleRunRecord? latestPrescreen,
        string? filterRevision,
        IReadOnlyCollection<int> currentScenarioPositions,
        string? currentFilterDataRevision = null)
        => IsSourceStale(
            artifact,
            latestValidate,
            latestPrescreen,
            filterStale: false,
            filterRevision,
            currentScenarioPositions,
            currentFilterDataRevision);

    public static bool IsSourceStale(
        ReportArtifact artifact,
        RuleRunRecord? latestValidate,
        RuleRunRecord? latestPrescreen,
        bool filterStale,
        string? filterRevision,
        IReadOnlyCollection<int> currentScenarioPositions,
        string? currentFilterDataRevision = null)
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
                    || string.IsNullOrWhiteSpace(source.FilterDataRevision)
                    || !string.Equals(source.FilterDataRevision, currentFilterDataRevision, StringComparison.Ordinal)
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
                // 新底稿記錄實際重算的來源版本。未選情境仍過期，不代表這份已重算底稿過期。
                // 舊索引沒有來源版本，仍保留原本全案旗標判斷，不假裝可以證明它的資料版本。
                => (source.FilterDataRevision is null
                        ? filterStale
                        : !string.Equals(source.FilterDataRevision, currentFilterDataRevision, StringComparison.Ordinal))
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
