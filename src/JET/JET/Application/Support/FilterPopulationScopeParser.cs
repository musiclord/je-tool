using System.Text.Json;
using JET.Domain;

namespace JET.Application;

internal sealed record CurrentFilterRevision(
    string Revision,
    GlPopulationScope PopulationScope,
    IReadOnlySet<int> Positions);

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
    /// 寫成「〈作業名稱〉尚無法執行：〈原因〉。」（使用者 2026-10-14 第三輪勾選頁確認 R3-34，照已確認的 18）。
    /// </summary>
    internal const string NoSavedScenarios =
        "尚未儲存任何篩選情境，請先在「進階條件篩選」儲存情境";

    internal static JetActionException NoSavedScenario() => new(JetErrorCodes.NoSavedScenario, NoSavedScenarios);

    public static CurrentFilterRevision RequireCurrentRevision(
        IReadOnlyList<SavedFilterScenario> scenarios)
    {
        if (scenarios.Count == 0)
        {
            // 使用者 2026-10-07 裁定上游修改清除下游後，沒有情境是常見狀態，不能誤導成「尚未套用目前規則」。
            throw NoSavedScenario();
        }

        if (!RuleLogicVersions.AreCurrent(scenarios))
        {
            throw new JetActionException(
                JetErrorCodes.StaleResult,
                FilterScenarioRuleMessages.NotYetUpgraded);
        }

        var revisions = scenarios
            .Select(scenario => scenario.SavedUtc.ToUniversalTime().ToString("O"))
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        if (revisions.Length != 1)
        {
            throw StaleDefinition();
        }

        var positions = scenarios.Select(scenario => scenario.Position).ToHashSet();
        if (positions.Count != scenarios.Count
            || positions.Any(position => position < 1 || position > FilterScenarioLimits.MaxSavedScenarios))
        {
            throw StaleDefinition();
        }

        return new CurrentFilterRevision(
            revisions[0],
            ResolveSaved(scenarios),
            positions);
    }

    private static JetActionException InvalidPayload() => JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "population_scope_invalid");

    private static JetActionException StaleDefinition(Exception? innerException = null) => new(
        JetErrorCodes.StaleResult,
        "已儲存的篩選情境和目前版本不一致，請到第五步重新儲存篩選情境。",
        innerException: innerException);
}
