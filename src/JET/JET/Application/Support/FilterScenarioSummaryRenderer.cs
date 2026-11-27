using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// 把一個已儲存的篩選情境組成回應裡的情境摘要，只有這裡負責。儲存情境的回應與 project.load 都用這裡的輸出，
/// 情境來源、後端補上的名稱與動機，以及重新載入案件時拿到的內容，前後才會一致。
/// </summary>
internal static class FilterScenarioSummaryRenderer
{
    internal static object Render(SavedFilterScenario scenario)
    {
        JsonDocument? document = null;
        try
        {
            document = JetJsonStorage.Parse(scenario.DefinitionJson);
        }
        catch (JsonException)
        {
            // 條件內容讀不出來的情境仍列在清單上，名稱與動機照存，條件組是空的；
            // 待修正清單會寫出原因，使用者可以移除它。
            // 定義裡有讀不出來的文字時也走這裡（JetJsonStorage.Parse），條件組才不會原樣放進回應、到序列化時才失敗。
        }

        using (document)
        {
            var root = document?.RootElement;
            var readable = root is { ValueKind: JsonValueKind.Object }
                && FilterScenarioPayloadParser.HasReadableStructure(root.Value);
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
                // 回給前端的篩選範圍一律寫 auditPeriod。已儲存情境裡的其他範圍值由
                // FilterPopulationScopeParser.RequireCurrentRevision 判成過期，不送回前端再拿來執行。
                ["populationScope"] = GlPopulationScopeValues.AuditPeriod,
                ["savedUtc"] = scenario.SavedUtc
            };
            if (readable && root!.Value.TryGetProperty("editorOrigins", out var origins))
            {
                summary["editorOrigins"] = origins.Clone();
            }

            return summary;
        }
    }
}
