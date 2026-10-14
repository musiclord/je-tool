using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

/// <summary>
/// 損毀條件的支援日誌事件 <c>filter.scenario_unreadable</c>（使用者 2026-10-13 裁定「損毀的情境」）。
/// 畫面只寫通用句，哪一條、哪一種損毀記在這裡；欄位只有動作名稱、情境位置、組號、條號與程式寫死的原因代號，
/// 不含情境名稱、使用者輸入的值、科目、金額或本機路徑。
/// </summary>
internal static partial class FilterScenarioDiagnostics
{
    /// <summary>一次動作最多記這麼多筆，避免一個大情境把支援日誌的環狀緩衝擠滿。</summary>
    internal const int MaxEntriesPerAction = 20;

    [LoggerMessage(EventId = 2500, EventName = "filter.scenario_unreadable", Level = LogLevel.Warning,
        Message = "action {action} scenario {scenario_position} group {group_no} rule {rule_no} unreadable reason={reason_code}")]
    private static partial void ScenarioUnreadable(
        ILogger logger,
        string action,
        int? scenario_position,
        int? group_no,
        int? rule_no,
        string reason_code);

    /// <summary>
    /// 篩選條件編譯時的檢查沒過（使用者 2026-10-14 第三輪勾選頁確認 R3-62）：驗證器與執行時檢查不一致的程式錯誤，
    /// 不是畫面送錯資料，所以不沿用 <c>action.payload_rejected</c>。欄位只有動作名稱、組號、條號與程式寫死的檢查代號。
    /// </summary>
    [LoggerMessage(EventId = 2501, EventName = "filter.compile_check_failed", Level = LogLevel.Warning,
        Message = "action {action} group {group_no} rule {rule_no} compile check failed check={compile_check}")]
    private static partial void CompileCheckFailed(
        ILogger logger,
        string action,
        int? group_no,
        int? rule_no,
        string compile_check);

    /// <summary>待修正清單裡各情境的損毀原因（開案、一次處理一個情境的回應）。</summary>
    internal static void LogProblems(ILogger? logger, string action, IEnumerable<FilterScenarioUpgradeProblem> problems)
    {
        if (logger is null)
        {
            return;
        }

        var entries = problems
            .SelectMany(problem => problem.Corrupt.Select(reason => (Position: (int?)problem.Position, Reason: reason)))
            .Take(MaxEntriesPerAction);
        foreach (var (position, reason) in entries)
        {
            ScenarioUnreadable(logger, action, position, reason.Group, reason.Rule, reason.Code);
        }
    }

    /// <summary>失敗的動作帶出的損毀原因或編譯檢查代號（儲存、預覽與重新計算）；例外沒有帶這些資訊時不記。</summary>
    internal static void LogException(ILogger? logger, string action, Exception exception)
    {
        if (logger is null)
        {
            return;
        }

        if (FilterCompileCheck.CheckOf(exception) is { } compileCheck)
        {
            var (group, rule) = FilterCompileCheck.PositionOf(exception);
            CompileCheckFailed(logger, action, group, rule, compileCheck);
        }

        var reasons = FilterScenarioCorruption.ReasonsOf(exception);
        if (reasons.Count == 0)
        {
            return;
        }

        var position = (exception as JetActionException)?.Details?.FirstOrDefault()?.ScenarioPosition;
        foreach (var reason in reasons.Take(MaxEntriesPerAction))
        {
            ScenarioUnreadable(logger, action, position, reason.Group, reason.Rule, reason.Code);
        }
    }
}
