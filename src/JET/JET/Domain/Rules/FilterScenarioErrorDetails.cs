using System.Text.RegularExpressions;

namespace JET.Domain;

/// <summary>
/// 把 <see cref="FilterScenarioValidator"/> 的逐條訊息轉成帶位置的錯誤細節。驗證器用固定前綴
/// 「條件群組 N 規則 M：」或「條件群組 N：」標示位置（Domain 自己產生，不是使用者輸入），
/// 這裡只解析該前綴；解析不到的訊息位置為 null，前端就以整段訊息呈現。
/// </summary>
public static partial class FilterScenarioErrorDetails
{
    [GeneratedRegex(@"^條件群組 (?<group>\d+)(?: 規則 (?<rule>\d+))?(?:：|\s)(?<message>.*)$", RegexOptions.Singleline)]
    private static partial Regex Positioned();

    public static IReadOnlyList<JetErrorDetail> Parse(IReadOnlyList<string> errors) =>
        errors.Select(error =>
        {
            var match = Positioned().Match(error);
            if (!match.Success) return new JetErrorDetail(null, null, error);
            int? rule = match.Groups["rule"].Success ? int.Parse(match.Groups["rule"].Value) : null;
            return new JetErrorDetail(int.Parse(match.Groups["group"].Value), rule, match.Groups["message"].Value.Trim());
        }).ToArray();

    public static JetActionException InvalidScenario(IReadOnlyList<string> errors) =>
        new(JetErrorCodes.InvalidScenario, string.Join("；", errors)) { Details = Parse(errors) };
}
