using System.Data.Common;
using System.Text.Json;
using JET.Domain;

namespace JET.Application;

internal static class ReportExportSupport
{
    /// <summary>
    /// 畫面持有的情境清單和已儲存的不一致（用字依裁定 2026-10-14-01）。
    /// 產生報告、底稿與重新計算選錯情境位置時共用。
    /// </summary>
    internal const string ScenarioListOutOfDate =
        "畫面上的情境清單與已儲存的內容不一致，本次未產生。請重新開啟案件後再操作。";

    internal sealed record PublishedReportCatalog(object[]? Reports, string? Warning);

    /// <summary>
    /// 報告清單更新期限用的時間來源，只讓測試在同一個非同步流程內替換；正式程式一律用系統時間。
    /// 資料形狀紀錄的流程要確定走到清單更新完成的分支，不能隨機器快慢改走逾時分支（2026-10-19）。
    /// </summary>
    internal static AsyncLocal<TimeProvider?> CatalogRefreshClock { get; } = new();

    public static async Task<PublishedReportCatalog> ReadReportCatalogAfterPublicationAsync(
        IReportStore store, string projectId, TimeSpan? refreshTimeout = null,
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
            var reports = await pending.WaitAsync(refreshToken);
            return new(reports.Select(ReportWire).ToArray(), null);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return CatalogUnavailable(unavailableMessage);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DbException
            || exception is JetActionException
            {
                Code: JetErrorCodes.FileReadError or JetErrorCodes.DatabaseBusy or JetErrorCodes.DatabaseLocked
                    or JetErrorCodes.DatabaseStorageFull or JetErrorCodes.DatabaseCorrupt or JetErrorCodes.DatabaseReadOnly
                    or JetErrorCodes.DatabaseUnavailable or JetErrorCodes.DatabaseOutOfMemory
            })
        {
            return CatalogUnavailable(unavailableMessage);
        }
    }

    private static PublishedReportCatalog CatalogUnavailable(string? message) => new(null, message ??
        "檔案已產生，但報告清單沒有更新；重新開啟案件就會更新，不必重新產生。");

    /// <summary>產生報告回應的 warnings：報告已發布、卻寫不進操作紀錄時放那一句提醒，其他時候是空陣列。</summary>
    public static string[] Warnings(string? operationLogWarning) => operationLogWarning is null ? [] : [operationLogWarning];

    public static void RequireRequestedValidationResult(
        ResultRecord current,
        string requestedResultId)
    {
        if (!string.Equals(current.ResultId, requestedResultId, StringComparison.Ordinal))
        {
            throw new JetActionException(
                JetErrorCodes.StaleResult,
                "這份報告依據的資料驗證結果已不是目前的結果，請重新執行資料驗證後再產生。");
        }
    }

    public static async Task<ResultRecord> RequireCurrentRunAsync(
        IResultStore store,
        string projectId,
        ResultKind kind,
        string requestedResultId,
        CancellationToken cancellationToken)
    {
        var current = await store.FindLatestAsync(projectId, kind, cancellationToken);
        if (current is null
            || !string.Equals(current.ResultId, requestedResultId, StringComparison.Ordinal)
            || !RuleLogicVersions.IsCurrent(current))
        {
            var button = kind == ResultKind.Prescreen ? "重新執行預篩選" : "重新執行驗證";
            throw new JetActionException(
                JetErrorCodes.StaleResult,
                $"這份報告依據的結果已不是目前資料的結果。請回到「資料驗證與測試」按「{button}」，再回來產生報告。");
        }

        return current;
    }

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

    /// <summary>
    /// 底稿用的篩選版本。裁定 2026-10-29-16：還沒有儲存篩選情境也能產出底稿，這時版本是 null、範圍是查核期間；
    /// 已有情境時照 <see cref="RequireRevisionState"/> 比對畫面送來的版本。
    /// </summary>
    public static (string? Revision, GlPopulationScope PopulationScope) RequireWorkingPaperRevisionState(
        IReadOnlyList<SavedFilterScenario> scenarios,
        string? requestedRevision)
    {
        if (scenarios.Count == 0)
        {
            if (requestedRevision is not null)
            {
                throw new JetActionException(JetErrorCodes.StaleResult, ScenarioListOutOfDate);
            }

            return (null, GlPopulationScope.AuditPeriod);
        }

        if (requestedRevision is null)
        {
            throw new JetActionException(JetErrorCodes.StaleResult, ScenarioListOutOfDate);
        }

        var current = RequireRevisionState(scenarios, requestedRevision);
        return (current.Revision, current.PopulationScope);
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

        // 裁定 2026-10-30-15：有情境但一個都沒勾選時仍產出底稿，第三步與第四步留空。
        selected.Sort();
        return selected;
    }

    /// <summary>
    /// 依目前的 run 與篩選版本把過期報告標為 stale，不另讀取未使用的清單。stale 判定只委派
    /// <see cref="IsSourceStale(Report, ResultRecord?, ResultRecord?, bool, string?, IReadOnlyCollection{int}, string?)"/>，
    /// 不建立第二份 run／revision 規則。
    /// </summary>
    public static async Task RefreshReportsAsync(
        string projectId,
        ResultRecord? latestValidate,
        ResultRecord? latestPrescreen,
        bool filterStale,
        string? currentFilterRevision,
        IReadOnlyCollection<int> scenarioPositions,
        IReportStore reportStore,
        CancellationToken cancellationToken,
        string? currentFilterDataRevision = null)
    {
        await reportStore.MarkStaleAsync(
            projectId,
            report => IsSourceStale(
                report,
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

    /// <summary>驗證結果裡 GL 與 TB 逐科目比對無法執行的原因；能執行時是 null。</summary>
    public static string? CompletenessNaReason(ResultRecord validationResult)
    {
        ArgumentNullException.ThrowIfNull(validationResult);
        using var summary = JsonDocument.Parse(validationResult.SummaryJson);
        return summary.RootElement.TryGetProperty("completenessTest", out var completeness)
            && completeness.TryGetProperty("naReason", out var reason)
            && reason.ValueKind == JsonValueKind.String
                ? reason.GetString()
                : null;
    }

    public static string CompanyName(ProjectDocument document) =>
        string.IsNullOrWhiteSpace(document.ClientName) ? document.ProjectId : document.ClientName;

    public static object ReportWire(Report report) => new
    {
        reportId = report.ReportId,
        kind = ReportKindValues.ToValue(report.Kind),
        fileName = report.RelativeFileName,
        fullPath = report.FullPath,
        generatedUtc = report.GeneratedUtc,
        bytes = report.Bytes,
        fileState = ReportFileStateValues.ToValue(report.FileState),
        sourceRef = new
        {
            validationResultId = report.SourceRef.ValidationResultId,
            prescreenResultId = report.SourceRef.PrescreenResultId,
            scenarioRevision = report.SourceRef.ScenarioRevision,
            scenarioPositions = report.SourceRef.ScenarioPositions,
            filterDataRevision = report.SourceRef.FilterDataRevision
        },
        stale = report.Stale
    };

    public static bool IsSourceStale(
        Report report,
        ResultRecord? latestValidate,
        ResultRecord? latestPrescreen,
        string? filterRevision,
        IReadOnlyCollection<int> currentScenarioPositions,
        string? currentFilterDataRevision = null)
        => IsSourceStale(
            report,
            latestValidate,
            latestPrescreen,
            filterStale: false,
            filterRevision,
            currentScenarioPositions,
            currentFilterDataRevision);

    public static bool IsSourceStale(
        Report report,
        ResultRecord? latestValidate,
        ResultRecord? latestPrescreen,
        bool filterStale,
        string? filterRevision,
        IReadOnlyCollection<int> currentScenarioPositions,
        string? currentFilterDataRevision = null)
    {
        var source = report.SourceRef;
        return report.Kind switch
        {
            ReportKind.ValidationReport
                or ReportKind.InfReport
                => source.ValidationResultId is null
                    || !string.Equals(
                        source.ValidationResultId,
                        latestValidate?.ResultId,
                        StringComparison.Ordinal),
            ReportKind.PrescreenReport
                => source.PrescreenResultId is null
                    || !string.Equals(
                        source.PrescreenResultId,
                        latestPrescreen?.ResultId,
                        StringComparison.Ordinal),
            ReportKind.CriteriaSelectionReport
                => filterStale
                    || string.IsNullOrWhiteSpace(source.FilterDataRevision)
                    || !string.Equals(source.FilterDataRevision, currentFilterDataRevision, StringComparison.Ordinal)
                    || source.ValidationResultId is null
                    || !string.Equals(
                        source.ValidationResultId,
                        latestValidate?.ResultId,
                        StringComparison.Ordinal)
                    || source.ScenarioRevision is null
                    || !string.Equals(
                        source.ScenarioRevision,
                        filterRevision,
                        StringComparison.Ordinal)
                    || !HasExactScenarioPositions(source.ScenarioPositions, currentScenarioPositions),
            ReportKind.WorkingPaper
                // 底稿記錄實際重算的來源版本。未選情境仍過期，不代表這份已重算底稿過期。
                // 缺少來源版本時無法證明它的資料版本，一律當過期，和 CriteriaSelection 報告的判斷相同。
                => string.IsNullOrWhiteSpace(source.FilterDataRevision)
                    || !string.Equals(source.FilterDataRevision, currentFilterDataRevision, StringComparison.Ordinal)
                    || source.ValidationResultId is null
                    || !string.Equals(
                        source.ValidationResultId,
                        latestValidate?.ResultId,
                        StringComparison.Ordinal)
                    // 沒有篩選情境時產出的底稿沒有篩選版本；之後一儲存情境就過期。
                    || (source.ScenarioRevision is null
                        ? currentScenarioPositions.Count > 0
                        : !string.Equals(
                            source.ScenarioRevision,
                            filterRevision,
                            StringComparison.Ordinal))
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
        // 沒有勾選情境的底稿記錄空清單，仍是有效的選擇。
        if (sourcePositions is null)
        {
            return false;
        }

        var current = currentPositions.ToHashSet();
        return sourcePositions.All(current.Contains);
    }
}
