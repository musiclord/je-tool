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

    public static JetActionException InvalidScenario(IReadOnlyList<string> errors) =>
        new(JetErrorCodes.InvalidScenario, string.Join("；", errors)) { Details = Parse(errors) };
}
