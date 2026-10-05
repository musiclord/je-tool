using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using JET.Domain;

namespace JET.Application;

/// <summary>已儲存情境尚未套用目前規則時，查詢與匯出共用的下一步說明。</summary>
internal static class FilterScenarioRuleMessages
{
    internal const string NotYetUpgraded =
        "已儲存的篩選情境尚未套用目前規則。請重新開啟這個案件；若畫面列出無法套用的情境，請到「進階條件篩選」修改後儲存，再執行這個動作。";
}

/// <summary>開案時檢查已儲存篩選情境能否改用目前規則的四種結果。</summary>
internal enum FilterScenarioUpgradeStatus
{
    /// <summary>沒有情境，或每個情境都已是目前規則版本且母體為查核期間。</summary>
    Current,

    /// <summary>全部情境都能改用目前規則；<see cref="FilterScenarioUpgradeResult.Scenarios"/> 是改版後的整批。</summary>
    Upgraded,

    /// <summary>至少一個情境不符合目前規則；整批不動，逐筆列出原因。</summary>
    NeedsEdit,

    /// <summary>同一批的保存時間或位置不一致，或定義無法讀成物件；整批不動。</summary>
    Inconsistent
}

internal sealed record FilterScenarioUpgradeProblem(int Position, string Name, IReadOnlyList<string> Messages);

internal sealed record FilterScenarioUpgradeResult(
    FilterScenarioUpgradeStatus Status,
    IReadOnlyList<SavedFilterScenario>? Scenarios,
    IReadOnlyList<FilterScenarioUpgradeProblem> Problems);

/// <summary>
/// 規則版本升級後，判斷已儲存情境能否直接改用目前規則。這是純函式：不讀寫資料庫，
/// 檢查與 <see cref="FilterRunMaterializeService"/> 補算前相同（Parse 加 Validate，forSave 為 false），
/// 避免「判斷可改版、補算卻失敗」。只要一筆不符合，整批都不改版。
/// 母體不是查核期間的情境不自動改版，因為改版會把母體默默改成查核期間。
/// </summary>
internal static partial class FilterScenarioRuleUpgrade
{
    [GeneratedRegex(@"^filter-\d{4}-\d{2}-\d{2}-v(?<number>\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex KnownVersion();

    internal const string NonAuditPeriodMessage =
        "這個情境的分錄測試範圍不是查核期間，JET 不會自動把它改成查核期間";

    internal const string UnknownVersionMessage =
        "這個情境的規則版本無法辨識，JET 不會自動改用目前的規則";

    internal const string UnreadableDefinitionMessage =
        "這個情境的條件內容格式無法讀取";

    /// <param name="scenarios">資料庫中已儲存的整批情境。</param>
    /// <param name="validationContext">用目前案件事實建立、母體為查核期間的驗證環境。</param>
    /// <param name="moneyScale">案件金額小數位數，供解析金額條件。</param>
    /// <param name="nowUtc">改版後整批共用的新保存時間。</param>
    internal static FilterScenarioUpgradeResult Evaluate(
        IReadOnlyList<SavedFilterScenario> scenarios,
        FilterValidationContext validationContext,
        int moneyScale,
        DateTimeOffset nowUtc)
    {
        if (scenarios.Count == 0)
        {
            return Current();
        }

        var savedTimes = scenarios
            .Select(scenario => scenario.SavedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
            .Distinct(StringComparer.Ordinal)
            .Count();
        var positions = scenarios.Select(scenario => scenario.Position).ToHashSet();
        if (savedTimes != 1
            || positions.Count != scenarios.Count
            || positions.Any(position => position < 1 || position > FilterScenarioLimits.MaxSavedScenarios))
        {
            return Inconsistent();
        }

        // 已是目前版本的整批維持原狀，不在開案時重新驗證，行為與改版前相同。
        if (scenarios.All(RuleLogicVersions.IsCurrent))
        {
            return Current();
        }

        var currentNumber = VersionNumber(RuleLogicVersions.Filter)
            ?? throw new InvalidOperationException("目前的篩選規則版本不符合已知格式。");

        var problems = new List<FilterScenarioUpgradeProblem>();
        foreach (var scenario in scenarios)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(scenario.DefinitionJson);
            }
            catch (JsonException)
            {
                return Inconsistent();
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return Inconsistent();
                }

                var messages = new List<string>();
                var scope = ReadScope(root, out var scopeIsString);
                if (!scopeIsString)
                {
                    return Inconsistent();
                }

                var isAuditPeriod = scope is null
                    || string.Equals(scope, GlPopulationScopeValues.AuditPeriod, StringComparison.Ordinal);
                if (!isAuditPeriod)
                {
                    messages.Add(NonAuditPeriodMessage);
                }

                var version = root.TryGetProperty("logicVersion", out var versionElement)
                    && versionElement.ValueKind == JsonValueKind.String
                        ? versionElement.GetString()
                        : null;
                // 目前版本只缺母體欄位時，改版只是補上查核期間，與讀取時的預設相同。
                var number = version is null ? null : VersionNumber(version);
                if (number is null || number > currentNumber)
                {
                    messages.Add(UnknownVersionMessage);
                }

                messages.AddRange(CheckDefinition(root, validationContext, moneyScale));
                if (messages.Count > 0)
                {
                    problems.Add(new FilterScenarioUpgradeProblem(scenario.Position, scenario.Name, messages));
                }
            }
        }

        if (problems.Count > 0)
        {
            return new FilterScenarioUpgradeResult(FilterScenarioUpgradeStatus.NeedsEdit, null, problems);
        }

        var upgraded = scenarios
            .OrderBy(scenario => scenario.Position)
            .Select(scenario =>
            {
                using var document = JsonDocument.Parse(scenario.DefinitionJson);
                return new SavedFilterScenario(
                    scenario.Position,
                    scenario.Name,
                    scenario.Rationale,
                    RuleLogicVersions.StampFilterDefinition(document.RootElement),
                    nowUtc);
            })
            .ToArray();
        return new FilterScenarioUpgradeResult(FilterScenarioUpgradeStatus.Upgraded, upgraded, []);
    }

    private static IReadOnlyList<string> CheckDefinition(
        JsonElement definition,
        FilterValidationContext validationContext,
        int moneyScale)
    {
        FilterScenarioSpec spec;
        try
        {
            spec = FilterScenarioPayloadParser.Parse(definition, moneyScale);
        }
        catch (JetActionException exception)
        {
            return [exception.Message];
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or OverflowException)
        {
            return [UnreadableDefinitionMessage];
        }

        return FilterScenarioValidator.Validate(spec, validationContext, forSave: false);
    }

    /// <summary>缺漏、null 或空白都視為查核期間；不是字串時 <paramref name="isString"/> 為 false。</summary>
    private static string? ReadScope(JsonElement definition, out bool isString)
    {
        isString = true;
        if (!definition.TryGetProperty("populationScope", out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            isString = false;
            return null;
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static int? VersionNumber(string version)
    {
        var match = KnownVersion().Match(version);
        return match.Success
            && int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? number
                : null;
    }

    private static FilterScenarioUpgradeResult Current() =>
        new(FilterScenarioUpgradeStatus.Current, null, []);

    private static FilterScenarioUpgradeResult Inconsistent() =>
        new(FilterScenarioUpgradeStatus.Inconsistent, null, []);
}
