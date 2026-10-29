using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// 已保存 filter scenario 的唯一 wire summary renderer。Commit response 與 project.load
/// 共用此輸出，避免情境來源、後端替補名稱／動機及 resume 形狀在同一 session 前後分歧。
/// </summary>
internal static class FilterScenarioSummaryRenderer
{
    internal static object Render(SavedFilterScenario scenario)
    {
        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(scenario.DefinitionJson);
        }
        catch (JsonException)
        {
            // 條件內容讀不出來的情境仍列在清單上，名稱與動機照存，條件組是空的；
            // 待修正清單會寫出原因，審計員可以移除它（使用者 2026-10-13「損毀的情境」與「情境卡住」裁定）。
        }

        using (document)
        {
            var root = document?.RootElement;
            var readable = root is { ValueKind: JsonValueKind.Object };
            var groups = readable && root!.Value.TryGetProperty("groups", out var groupsElement)
                ? groupsElement.Clone()
                : JsonSerializer.SerializeToElement(Array.Empty<object>());
            var source = readable
                && root!.Value.TryGetProperty("source", out var sourceElement)
                && sourceElement.ValueKind == JsonValueKind.String
                && FilterScenarioSources.IsCommon(sourceElement.GetString())
                    ? FilterScenarioSources.Common
                    : null;

            var summary = new Dictionary<string, object?>
            {
                ["source"] = source,
                ["name"] = scenario.Name,
                ["rationale"] = scenario.Rationale,
                ["groups"] = groups,
                // 回放路徑刻意正規化為目前唯一母體；舊 scope 由 current-revision guard
                // 判為 stale，不把退役值重新送回前端成為可執行狀態。
                ["populationScope"] = GlPopulationScopeValues.AuditPeriod,
                ["savedUtc"] = scenario.SavedUtc
            };
            if (readable && root!.Value.TryGetProperty("editorOrigins", out var origins))
            {
                summary["editorOrigins"] = origins.Clone();
            }

            // 10 月以前存下的情境可能還帶已移除的排除區：照原樣帶給編輯器畫成唯讀區塊，審計員看得到也能移除；
            // 沒移除就儲存時由 FilterScenarioPayloadParser 擋下。原本不帶，修改這個情境後存回去就默默丟掉
            //（預篩選收斂計畫階段四，使用者 2026-10-22 勾選要修）。
            if (readable && root!.Value.TryGetProperty("exclusions", out var exclusions))
            {
                summary["exclusions"] = exclusions.Clone();
            }

            return summary;
        }
    }
}
