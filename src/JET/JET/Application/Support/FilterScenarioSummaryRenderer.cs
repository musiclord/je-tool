using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// 已保存 filter scenario 的唯一 wire summary renderer。Commit response 與 project.load
/// 共用此輸出，避免 KCT source、後端替補名稱／動機及 resume 形狀在同一 session 前後分歧。
/// </summary>
internal static class FilterScenarioSummaryRenderer
{
    internal static object Render(SavedFilterScenario scenario)
    {
        using var document = JsonDocument.Parse(scenario.DefinitionJson);
        var root = document.RootElement;
        var groups = root.TryGetProperty("groups", out var groupsElement)
            ? groupsElement.Clone()
            : default;
        var source = root.TryGetProperty("source", out var sourceElement)
            && sourceElement.ValueKind == JsonValueKind.String
            && FilterScenarioSources.IsKct(sourceElement.GetString())
                ? FilterScenarioSources.Kct
                : null;

        return new
        {
            source,
            name = scenario.Name,
            rationale = scenario.Rationale,
            groups,
            // 回放路徑刻意正規化為目前唯一母體；舊 scope 由 current-revision guard
            // 判為 stale，不把退役值重新送回前端成為可執行狀態。
            populationScope = GlPopulationScopeValues.AuditPeriod,
            savedUtc = scenario.SavedUtc
        };
    }
}
