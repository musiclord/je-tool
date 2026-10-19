using System.Globalization;
using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// JSON payload 解析共用工具。缺必填欄位或格式不對時一律拋 invalid_payload。畫面只送已檢查過的值，
/// 走到這些錯誤表示程式送錯資料：畫面只顯示非預期錯誤那一句，固定的檢查代號記進支援日誌，
/// 不寫欄位名稱或收到的值（使用者 2026-10-19 要求前端不露出系統用詞）。
/// </summary>
public static class PayloadReader
{
    public static string GetRequiredString(JsonElement payload, string name)
    {
        var value = GetOptionalString(payload, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "payload_required_text_missing");
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
        return ValidateDate(value);
    }

    public static string? GetOptionalDate(JsonElement payload, string name)
    {
        var value = GetOptionalString(payload, name);
        return value is null ? null : ValidateDate(value);
    }

    private static string ValidateDate(string value)
    {
        if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "payload_date_invalid");
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
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "payload_required_object_missing");
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

    public static List<string> GetStringList(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "payload_required_array_missing");
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
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "payload_required_array_missing");
        }

        var values = new List<int>();

        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var number))
            {
                throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "payload_int_array_invalid");
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
