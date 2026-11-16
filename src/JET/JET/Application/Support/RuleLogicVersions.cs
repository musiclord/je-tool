using System.Text.Json;
using System.Text;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// 規則邏輯的版本，記在已儲存的驗證結果、預篩選結果與篩選情境裡，用來判斷它們和目前查明細的 SQL 還對不對得上。
/// 資料驗證、預篩選或篩選的判斷方式改了，或明細包含哪些分錄改了，就要換掉對應的常數。
/// 換掉之後，舊的結果摘要與情境立刻算過期，不會拿舊的定義配新的 SQL 產生報告。
/// </summary>
internal static class RuleLogicVersions
{
    // 這一版在傳票核准日不可用時把「核准日不在期間」列為無法執行，不寫 0 筆；較舊的結果要重新驗證。
    public const string Validation = "validation-2026-10-23-v6";
    // 這一版沒有傳票日期與回溯過帳：預篩選沒有這條規則，篩選不接受回溯過帳條件，兩者的版本都跟著推進。
    public const string Prescreen = "prescreen-2026-10-08-v10";
    // 這一版每條條件只表達一件事；停用的寫法列為待修正，保留原文。
    public const string Filter = "filter-2026-10-14-v20";

    public static string? ExpectedFor(ResultKind kind) => kind switch
    {
        ResultKind.Validation => Validation,
        ResultKind.Prescreen => Prescreen,
        _ => null
    };

    public static bool IsCurrent(ResultRecord? run)
    {
        if (run is null || ExpectedFor(run.Kind) is not { } expected)
        {
            return false;
        }

        try
        {
            using var summary = JetJsonStorage.Parse(run.SummaryJson);
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
    /// 在要儲存的情境 JSON 寫入目前的篩選邏輯版本 logicVersion，並把 populationScope 寫成 auditPeriod。
    /// 傳進來的 JSON 即使已經有這兩個欄位也不採用，一律由這裡重寫。
    /// 其他屬性原樣複製，重新載入案件時仍能還原使用者設定的情境。
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

    /// <summary>
    /// 儲存一個情境時用：條件先只保留各類型用得到的屬性（<see cref="FilterConditionWireFields"/>），再蓋版本。
    /// 呼叫前必須已通過解析與驗證，已停用或損毀的寫法才不會被默默改成合格。照原樣寫回的其他情境、
    /// 移除、重新檢查與開案升級仍用 <see cref="StampFilterDefinition"/>，舊情境在下次儲存時才整理。
    /// </summary>
    public static string StampSavedFilterDefinition(JsonElement scenario)
    {
        using var kept = JsonDocument.Parse(FilterConditionWireFields.KeepUsedRuleFields(scenario));
        return StampFilterDefinition(kept.RootElement);
    }

    public static bool IsCurrent(SavedFilterScenario scenario)
    {
        try
        {
            using var document = JetJsonStorage.Parse(scenario.DefinitionJson);
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
