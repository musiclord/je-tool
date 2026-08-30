using System.Text.Json;
using System.Text;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// 持久化規則摘要、篩選情境與即時明細 SQL 的相容版本。只要 validation／prescreen／filter
/// 的判定或明細集合語意改變，必須推進對應常數；舊摘要／情境隨即視為 stale，避免升版後
/// 拿舊定義搭配新 SQL 產報告。
/// </summary>
internal static class RuleLogicVersions
{
    public const string Validation = "validation-2026-08-14-v4";
    public const string Prescreen = "prescreen-2026-08-14-v6";
    public const string Filter = "filter-2026-08-14-v10";

    public static string? ExpectedFor(string runKind) => runKind switch
    {
        RuleRunKinds.Validate => Validation,
        RuleRunKinds.Prescreen => Prescreen,
        _ => null
    };

    public static bool IsCurrent(RuleRunRecord? run)
    {
        if (run is null || ExpectedFor(run.RunKind) is not { } expected)
        {
            return false;
        }

        try
        {
            using var summary = JsonDocument.Parse(run.SummaryJson);
            return summary.RootElement.TryGetProperty("resultRef", out var resultRef)
                && resultRef.ValueKind == JsonValueKind.Object
                && resultRef.TryGetProperty("logicVersion", out var version)
                && version.ValueKind == JsonValueKind.String
                && string.Equals(version.GetString(), expected, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// 在已存 scenario JSON 內寫入後端權威的篩選邏輯版本。不相信 caller 同名欄位；
    /// 其他屬性原樣複製，resume 仍可回放使用者定義。
    /// </summary>
    public static string StampFilterDefinition(JsonElement scenario)
    {
        if (scenario.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Filter scenario must be a JSON object.", nameof(scenario));
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in scenario.EnumerateObject())
            {
                if (!string.Equals(property.Name, "logicVersion", StringComparison.Ordinal)
                    && !string.Equals(property.Name, "populationScope", StringComparison.Ordinal))
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteString("populationScope", GlPopulationScopeValues.AuditPeriod);
            writer.WriteString("logicVersion", Filter);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static bool IsCurrent(SavedFilterScenario scenario)
    {
        try
        {
            using var document = JsonDocument.Parse(scenario.DefinitionJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("logicVersion", out var version)
                && version.ValueKind == JsonValueKind.String
                && string.Equals(version.GetString(), Filter, StringComparison.Ordinal)
                && document.RootElement.TryGetProperty("populationScope", out var scope)
                && scope.ValueKind == JsonValueKind.String
                && string.Equals(
                    scope.GetString(),
                    GlPopulationScopeValues.AuditPeriod,
                    StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool AreCurrent(IReadOnlyList<SavedFilterScenario> scenarios) =>
        scenarios.Count > 0 && scenarios.All(IsCurrent);
}
