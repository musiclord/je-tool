using System.Text.RegularExpressions;

namespace JET.Domain;

/// <summary>
/// 把 <see cref="FilterScenarioValidator"/> 的逐條訊息轉成帶位置的錯誤細節。驗證器用固定前綴
/// 「第 N 組第 M 條：」或「第 N 組：」標示位置；仍可讀舊版的「條件群組 N 規則 M」前綴，
/// 這裡只解析該前綴；解析不到的訊息位置為 null，前端就以整段訊息呈現。
/// </summary>
public static partial class FilterScenarioErrorDetails
{
    [GeneratedRegex(@"^(?:第 (?<group>\d+) 組(?:第 (?<rule>\d+) 條)?|條件群組 (?<group>\d+)(?: 規則 (?<rule>\d+))?)(?:：|\s)(?<message>.*)$", RegexOptions.Singleline)]
    private static partial Regex Positioned();

    public static IReadOnlyList<JetErrorDetail> Parse(IReadOnlyList<string> errors) =>
        errors.Select(error =>
        {
            var match = Positioned().Match(error);
            if (!match.Success) return new JetErrorDetail(null, null, error);
            int? rule = match.Groups["rule"].Success ? int.Parse(match.Groups["rule"].Value) : null;
            return new JetErrorDetail(int.Parse(match.Groups["group"].Value), rule, match.Groups["message"].Value.Trim());
        }).ToArray();

    /// <summary>驗證器回的清單帶有損毀條件的原因代號時一併附上，支援日誌才記得到（訊息只有通用句）。</summary>
    public static JetActionException InvalidScenario(IReadOnlyList<string> errors) =>
        FilterScenarioCorruption.Attach(
            new(JetErrorCodes.InvalidScenario, string.Join("；", errors)) { Details = Parse(errors) },
            FilterScenarioCorruption.ReasonsOf(errors));

    /// <summary>
    /// 同 <see cref="InvalidScenario(IReadOnlyList{string})"/>，每一項另外標上錯誤屬於清單上第幾個情境與它的名稱，
    /// 前端才不會把別的情境的組號條號對到目前草稿的條件列。訊息文字不變。
    /// </summary>
    public static JetActionException InvalidScenario(
        IReadOnlyList<string> errors, int scenarioPosition, string? scenarioName) =>
        FilterScenarioCorruption.Attach(
            new(JetErrorCodes.InvalidScenario, string.Join("；", errors))
            {
                Details = Tag(Parse(errors), scenarioPosition, scenarioName)
            },
            FilterScenarioCorruption.ReasonsOf(errors));

    /// <summary>
    /// 情境形狀錯誤（例如已移除的排除區域）原本沒有細節；補成一筆沒有組號條號、帶情境位置的項目，訊息與錯誤碼不變。
    /// 已有細節的就把每一項標上同一個情境。
    /// </summary>
    public static JetActionException ForScenario(
        JetActionException exception, int scenarioPosition, string? scenarioName)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var details = exception.Details ?? [new JetErrorDetail(null, null, exception.Message)];
        return new JetActionException(exception.Code, exception.Message, exception.Field, exception)
        {
            Details = Tag(details, scenarioPosition, scenarioName)
        };
    }

    private static IReadOnlyList<JetErrorDetail> Tag(
        IReadOnlyList<JetErrorDetail> details, int scenarioPosition, string? scenarioName) =>
        details.Select(detail => detail with { ScenarioPosition = scenarioPosition, ScenarioName = scenarioName }).ToArray();
}
