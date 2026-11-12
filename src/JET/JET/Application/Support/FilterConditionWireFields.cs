using System.Text.Json;

namespace JET.Application;

/// <summary>
/// 已存條件的格式只在這裡決定：後端儲存時只保留該條件類型用得到的欄位，已存的舊情境在下次儲存時整理，
/// 前端送出的格式不變。
/// 儲存一個情境時，每一條條件只保留 type、join 和下表列出的屬性；情境層與條件組層原樣保留。
/// </summary>
internal static class FilterConditionWireFields
{
    /// <summary>
    /// 每種 wire 條件類型除了 type、join 之外用得到的屬性。來源是解析、驗證、編譯、讀回文字、
    /// 底稿說明與前端讀取的位置；FilterConditionWireFieldsTests 核對整理前後這些結果完全相同，並核對表上的類型和解析器一一對應。
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> UsedByType =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["group"] = ["conditions"],
            ["voucher"] = ["conditions", "quantifier", "side"],
            ["prescreen"] = ["prescreenKey"],
            ["text"] = ["field", "keywords", "mode"],
            ["textSet"] = ["field", "values", "mode", "normalization"],
            ["dateRange"] = ["field", "from", "to"],
            ["numRange"] = ["field", "from", "to"],
            ["drCrOnly"] = ["drCr"],
            ["manualAuto"] = ["isManual"],
            ["accountPair"] = ["pairMode", "debitCategoryIds", "creditCategoryIds", "categorySelection"],
            ["specialAccountCategoryPair"] = ["pairMode", "debitCategoryIds", "creditCategoryIds", "categorySelection"],
            ["entityFrequency"] = ["field", "countUnit", "countOperator", "countFrom", "countTo"],
            ["revenueDebitNearQuarterEnd"] = ["windowDays"],
            ["revenueWithoutNormalCounterpart"] = [],
            ["manualRevenueEntry"] = [],
            ["preparerEqualsApprover"] = [],
            ["typed"] = ["fieldId", "operator", "value", "from", "to", "values", "amountBasis"],
            ["fieldValue"] = ["field", "fieldId", "operator", "value", "from", "to", "values", "amountBasis", "includeBlank", "drCr"],
            ["accountSide"] = ["categoryMode", "categoryIds", "categorySelection", "drCr"]
        };

    /// <summary>
    /// 整理一個已通過解析與驗證的情境：只刪不改，保留的屬性照原順序、原值寫出，groups[].conditions[] 與
    /// group、voucher 的子條件逐層整理。必須排在驗證之後：先整理會把損毀或已停用的寫法默默改成合格。
    /// </summary>
    internal static string KeepUsedRuleFields(JsonElement scenario)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteObject(writer, scenario, "groups", WriteGroups);
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteGroups(Utf8JsonWriter writer, JsonElement groups)
    {
        writer.WriteStartArray();
        foreach (var group in groups.EnumerateArray())
        {
            WriteObject(writer, group, "conditions", WriteRules);
        }

        writer.WriteEndArray();
    }

    private static void WriteRules(Utf8JsonWriter writer, JsonElement rules)
    {
        writer.WriteStartArray();
        foreach (var rule in rules.EnumerateArray())
        {
            WriteRule(writer, rule);
        }

        writer.WriteEndArray();
    }

    // 情境與條件組：只往下整理 nested 那一個陣列，其他屬性原樣寫出。
    private static void WriteObject(
        Utf8JsonWriter writer,
        JsonElement element,
        string nested,
        Action<Utf8JsonWriter, JsonElement> writeNested)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            element.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals(nested) && property.Value.ValueKind == JsonValueKind.Array)
            {
                writer.WritePropertyName(property.Name);
                writeNested(writer, property.Value);
            }
            else
            {
                property.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteRule(Utf8JsonWriter writer, JsonElement rule)
    {
        // 驗證過的條件不會是認不得的類型；萬一遇到就原樣保留，不猜要留哪些屬性。
        if (rule.ValueKind != JsonValueKind.Object
            || !rule.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || !UsedByType.TryGetValue(type.GetString()!, out var used))
        {
            rule.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        foreach (var property in rule.EnumerateObject())
        {
            if (property.NameEquals("type") || property.NameEquals("join"))
            {
                property.WriteTo(writer);
            }
            else if (!used.Contains(property.Name, StringComparer.Ordinal))
            {
                continue;
            }
            else if (property.NameEquals("conditions") && property.Value.ValueKind == JsonValueKind.Array)
            {
                writer.WritePropertyName(property.Name);
                WriteRules(writer, property.Value);
            }
            else
            {
                property.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }
}
