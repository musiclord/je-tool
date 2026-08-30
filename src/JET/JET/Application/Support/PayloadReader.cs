using System.Globalization;
using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// JSON payload 解析共用工具。缺必填欄位一律拋 invalid_payload 並指名欄位。
/// </summary>
public static class PayloadReader
{
    public static string GetRequiredString(JsonElement payload, string name)
    {
        var value = GetOptionalString(payload, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                $"payload 缺少必填欄位 '{name}'。");
        }

        return value;
    }

    public static string? GetOptionalString(JsonElement payload, string name)
    {
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String)
        {
            var text = property.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        return null;
    }

    public static int? GetOptionalInt(JsonElement payload, string name)
    {
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(name, out var property))
        {
            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
            {
                return number;
            }

            if (property.ValueKind == JsonValueKind.String
                && int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    public static string GetRequiredDate(JsonElement payload, string name)
    {
        var value = GetRequiredString(payload, name);
        return ValidateDate(value, name);
    }

    public static string? GetOptionalDate(JsonElement payload, string name)
    {
        var value = GetOptionalString(payload, name);
        return value is null ? null : ValidateDate(value, name);
    }

    private static string ValidateDate(string value, string name)
    {
        if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                $"欄位 '{name}' 必須是 yyyy-MM-dd 格式的日期，收到 '{value}'。");
        }

        return value;
    }

    /// <summary>讀取 mapping object：略過 null / 空字串值。</summary>
    public static Dictionary<string, string> GetStringMap(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.Object)
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                $"payload 缺少必填物件欄位 '{name}'。");
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var item in property.EnumerateObject())
        {
            if (item.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = item.Value.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                map[item.Name] = value.Trim();
            }
        }

        return map;
    }

    /// <summary>讀取 fields 陣列（{key, label, ...} 物件；多餘屬性忽略）。</summary>
    public static List<MappingFieldDefinition> GetFieldDefinitions(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                $"payload 缺少必填陣列欄位 '{name}'。");
        }

        var fields = new List<MappingFieldDefinition>();

        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var key = item.TryGetProperty("key", out var keyProp) && keyProp.ValueKind == JsonValueKind.String
                ? keyProp.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var label = item.TryGetProperty("label", out var labelProp) && labelProp.ValueKind == JsonValueKind.String
                ? labelProp.GetString() ?? string.Empty
                : string.Empty;

            fields.Add(new MappingFieldDefinition(key.Trim(), label.Trim()));
        }

        return fields;
    }

    public static List<string> GetStringList(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                $"payload 缺少必填陣列欄位 '{name}'。");
        }

        var values = new List<string>();

        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    values.Add(text.Trim());
                }
            }
        }

        return values;
    }

    /// <summary>讀取整數陣列(必填)。非陣列或含非整數元素 → invalid_payload。</summary>
    public static List<int> GetIntList(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                $"payload 缺少必填陣列欄位 '{name}'。");
        }

        var values = new List<int>();

        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var number))
            {
                throw new JetActionException(
                    JetErrorCodes.InvalidPayload,
                    $"欄位 '{name}' 必須是整數陣列。");
            }

            values.Add(number);
        }

        return values;
    }

    public static List<string>? GetOptionalStringList(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return GetStringList(payload, name);
    }
}
