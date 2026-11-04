using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <param name="Revision">整批情境的版本，見 <see cref="FilterScenarioRevision"/>；任何一個情境存或移除都會改變。</param>
/// <param name="GeneratedUtc">最近一次儲存情境的時間，畫面寫成篩選結果的產生時間。</param>
internal sealed record CurrentFilterRevision(
    string Revision,
    GlPopulationScope PopulationScope,
    IReadOnlySet<int> Positions,
    string GeneratedUtc);

/// <summary>
/// 已存情境的版本。每個情境的版本是「情境編號@保存時間」，整批的版本依畫面順序用分號接起來；
/// 存、改或移除任何一個情境，整批的版本就不同。檢視一個情境的傳票時只核對這個情境自己那一段，
/// 存或移除別的情境不會讓正在看的頁面過期；移除前面的情境時，後面的情境換了位置，那一段跟著不同。
/// 上游修改清掉全部情境，所有版本都失效。
/// </summary>
internal static class FilterScenarioRevision
{
    private const char Separator = ';';

    internal static string Of(IReadOnlyList<SavedFilterScenario> scenarios) =>
        string.Join(Separator, scenarios.OrderBy(scenario => scenario.Position).Select(OfScenario));

    internal static string OfScenario(SavedFilterScenario scenario) =>
        scenario.ScenarioId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + "@" + SavedTime(scenario);

    internal static string SavedTime(SavedFilterScenario scenario) =>
        scenario.SavedUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// 畫面持有的版本是不是目前這個情境的版本：整批版本看 <paramref name="position"/> 那一段；
    /// 只有一段時就是這個情境自己的版本，例如傳票頁回應的 <c>scenarioRevision</c>。
    /// </summary>
    internal static bool Covers(string? revision, int position, SavedFilterScenario current)
    {
        if (string.IsNullOrEmpty(revision) || position < 1)
        {
            return false;
        }

        var segments = revision.Split(Separator);
        var segment = segments.Length == 1 ? segments[0] : position <= segments.Length ? segments[position - 1] : null;
        return string.Equals(segment, OfScenario(current), StringComparison.Ordinal);
    }
}

/// <summary>
/// filter payload／已存 definition 的 revision-level populationScope 單一解析點。
/// 使用者 payload 的未知值屬 invalid_payload；已存資料損壞或同 revision 不一致屬 stale_result。
/// </summary>
internal static class FilterPopulationScopeParser
{
    public static GlPopulationScope ReadPayload(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("populationScope", out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return GlPopulationScope.AuditPeriod;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw InvalidPayload();
        }

        var value = property.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return GlPopulationScope.AuditPeriod;
        }

        if (GlPopulationScopeValues.TryParse(value, out var scope))
        {
            return scope;
        }

        throw InvalidPayload();
    }

    /// <summary>舊 definition 缺欄時解讀為 auditPeriod；非唯一正準值或錯型別則視為 stale。</summary>
    public static GlPopulationScope ReadDefinition(JsonElement definition)
    {
        if (definition.ValueKind != JsonValueKind.Object
            || !definition.TryGetProperty("populationScope", out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return GlPopulationScope.AuditPeriod;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw StaleDefinition();
        }

        var value = property.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return GlPopulationScope.AuditPeriod;
        }

        if (GlPopulationScopeValues.TryParse(value, out var scope))
        {
            return scope;
        }

        throw StaleDefinition();
    }

    public static GlPopulationScope ResolveSaved(IReadOnlyList<SavedFilterScenario> scenarios)
    {
        if (scenarios.Count == 0)
        {
            throw NoSavedScenario();
        }

        GlPopulationScope? resolved = null;
        try
        {
            foreach (var scenario in scenarios)
            {
                using var document = JsonDocument.Parse(scenario.DefinitionJson);
                var current = ReadDefinition(document.RootElement);
                if (resolved is not null && resolved.Value != current)
                {
                    throw StaleDefinition();
                }

                resolved = current;
            }
        }
        catch (JsonException exception)
        {
            throw StaleDefinition(exception);
        }

        return resolved ?? GlPopulationScope.AuditPeriod;
    }

    public static GlPopulationScope RequireCurrentSaved(
        IReadOnlyList<SavedFilterScenario> scenarios,
        bool allowEmpty = false)
    {
        if (scenarios.Count == 0 && allowEmpty)
        {
            return GlPopulationScope.AuditPeriod;
        }

        return RequireCurrentRevision(scenarios).PopulationScope;
    }

    /// <summary>
    /// 沒有任何已存情境時的原因；上游修改清除全部情境後最常遇到。畫面把錯誤代碼 no_saved_scenario 當成前置條件不足，
    /// 寫成「〈作業名稱〉尚無法執行：〈原因〉。」（用字依裁定 2026-10-14-01）。
    /// </summary>
    internal const string NoSavedScenarios =
        "尚未儲存任何篩選情境，請先在「進階條件篩選」儲存情境";

    internal static JetActionException NoSavedScenario() => new(JetErrorCodes.NoSavedScenario, NoSavedScenarios);

    public static CurrentFilterRevision RequireCurrentRevision(
        IReadOnlyList<SavedFilterScenario> scenarios)
    {
        if (scenarios.Count == 0)
        {
            // 上游修改會清除下游（裁定 2026-10-07-03），所以沒有情境是常見狀態，不能誤導成「尚未套用目前規則」。
            throw NoSavedScenario();
        }

        if (!RuleLogicVersions.AreCurrent(scenarios))
        {
            throw new JetActionException(
                JetErrorCodes.StaleResult,
                FilterScenarioRuleMessages.NotYetUpgraded);
        }

        // 每個情境各自儲存，保存時間本來就不同；位置要是 1 到 n 且不重複，情境編號也不能重複。
        var positions = scenarios.Select(scenario => scenario.Position).ToHashSet();
        if (positions.Count != scenarios.Count
            || positions.Any(position => position < 1 || position > scenarios.Count)
            || scenarios.Select(scenario => scenario.ScenarioId).Distinct().Count() != scenarios.Count)
        {
            throw StaleDefinition();
        }

        return new CurrentFilterRevision(
            FilterScenarioRevision.Of(scenarios),
            ResolveSaved(scenarios),
            positions,
            scenarios.Select(FilterScenarioRevision.SavedTime).Max(StringComparer.Ordinal)!);
    }

    private static JetActionException InvalidPayload() => JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "population_scope_invalid");

    private static JetActionException StaleDefinition(Exception? innerException = null) => new(
        JetErrorCodes.StaleResult,
        "已儲存的篩選情境和目前版本不一致，請到第五步重新儲存篩選情境。",
        innerException: innerException);
}
