using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// 把前端條件編輯器送來的情境 JSON 解析成 FilterScenarioSpec。
/// 這裡只檢查並轉換資料的形狀：辨認條件類型，讀出比較值與分類清單。
/// 哪些值可以用、哪些欄位必填、上下限是多少，由 Domain 的 FilterScenarioValidator 檢查。
/// 形狀不對時一律丟出 invalid_scenario。只有存檔損毀或程式送錯資料才會出現的形狀錯誤，畫面只寫
/// <see cref="FilterScenarioCorruption.RuleMessage"/>，原因代號記進支援日誌；
/// 審計員看得懂、也做得到的原因（已停用的條件類型），畫面寫出這個原因。
/// 條件的錯誤帶「第 N 組第 M 條」位置，儲存時才標得到那一條條件。
/// </summary>
public static class FilterScenarioPayloadParser
{
    internal static CanonicalFilterDocument ParseDocument(
        JsonElement scenario,
        int moneyScale,
        int position) =>
        new(
            position,
            scenario.GetRawText(),
            Parse(scenario, moneyScale));

    public static FilterScenarioSpec Parse(JsonElement scenario, int moneyScale)
    {
        try
        {
            return ParseCore(scenario, moneyScale);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            // 存檔被改壞或畫面送來讀不出來的字串時，沿用條件內容損毀的錯誤，不讓底層轉型例外流到 Bridge。
            throw FilterScenarioCorruption.Exception(null, "definition_parse_failed");
        }
    }

    private static FilterScenarioSpec ParseCore(JsonElement scenario, int moneyScale)
    {
        if (scenario.ValueKind != JsonValueKind.Object)
        {
            throw FilterScenarioCorruption.Exception(null, "scenario_not_object");
        }

        var name = ReadString(scenario, "name") ?? string.Empty;
        var rationale = ReadString(scenario, "rationale") ?? string.Empty;

        // 情境的來源標記是選填的 source 欄位。ReadString 會去掉前後空白，全是空白時回 null。
        // Domain 只認得 "common"；其他值都和 null 一樣，當成審計員自己寫的情境。
        var source = ReadString(scenario, "source");

        var groups = new List<FilterGroupSpec>();
        if (scenario.TryGetProperty("groups", out var groupsElement)
            && groupsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var groupElement in groupsElement.EnumerateArray())
            {
                groups.Add(ParseGroup(groupElement, moneyScale, $"第 {groups.Count + 1} 組"));
            }
        }

        ValidateEditorOrigins(scenario, groups);
        return new FilterScenarioSpec(name.Trim(), rationale.Trim(), groups, source);
    }

    /// <summary>畫面會逐組逐條列出條件。結構讀不出來時保留情境，但不把壞掉的陣列直接送到畫面。</summary>
    internal static bool HasReadableStructure(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var group in groups.EnumerateArray())
            if (group.ValueKind != JsonValueKind.Object
                || !group.TryGetProperty("conditions", out var conditions) || !ConditionsReadable(conditions))
                return false;
        if (!root.TryGetProperty("editorOrigins", out var origins)) return true;
        if (origins.ValueKind != JsonValueKind.Object
            || !origins.TryGetProperty("groups", out var originGroups) || originGroups.ValueKind != JsonValueKind.Array
            || originGroups.GetArrayLength() != groups.GetArrayLength()) return false;
        for (var index = 0; index < groups.GetArrayLength(); index++)
        {
            var origin = originGroups[index];
            if (origin.ValueKind != JsonValueKind.Object
                || !origin.TryGetProperty("cards", out var cards) || cards.ValueKind != JsonValueKind.Array
                || cards.GetArrayLength() != groups[index].GetProperty("conditions").GetArrayLength()) return false;
        }
        return true;

        static bool ConditionsReadable(JsonElement conditions)
        {
            if (conditions.ValueKind != JsonValueKind.Array) return false;
            foreach (var rule in conditions.EnumerateArray())
            {
                if (rule.ValueKind != JsonValueKind.Object) return false;
                if (rule.TryGetProperty("conditions", out var children))
                {
                    if (!ConditionsReadable(children)) return false;
                }
                else if (rule.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                    && type.GetString() is "group" or "voucher")
                {
                    return false;
                }
            }
            return true;
        }
    }

    // editorOrigins 記的是每一條條件在編輯器裡由哪一張卡片加入。這裡只檢查它的結構，編譯條件時不會用到它。
    // 只接受第 2 版，每一條條件記一個卡片代號或 null。
    private static void ValidateEditorOrigins(JsonElement scenario, IReadOnlyList<FilterGroupSpec> groups)
    {
        if (!scenario.TryGetProperty("editorOrigins", out var origins)) return;
        if (origins.ValueKind != JsonValueKind.Object || !origins.TryGetProperty("version", out var version)
            || !version.TryGetInt32(out var number) || number != 2
            || !origins.TryGetProperty("groups", out var items) || items.ValueKind != JsonValueKind.Array
            || items.GetArrayLength() != groups.Count) throw FilterScenarioCorruption.Exception(null, "editor_origins_incomplete");
        foreach (var key in new[] { "nameIsAutomatic", "rationaleIsAutomatic" })
            if (origins.TryGetProperty(key, out var automatic)
                && automatic.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw FilterScenarioCorruption.Exception(null, "editor_origins_automatic_flag_invalid");
        for (var i = 0; i < groups.Count; i++)
        {
            var item = items[i];
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("cards", out var positions)
                || positions.ValueKind != JsonValueKind.Array || positions.GetArrayLength() != groups[i].Conditions.Count)
                throw FilterScenarioCorruption.Exception(null, "editor_origins_positions_mismatch");
            foreach (var position in positions.EnumerateArray())
                if (position.ValueKind != JsonValueKind.Null && !IsCardPosition(position))
                    throw FilterScenarioCorruption.Exception(null, "editor_origins_card_invalid");
        }
    }

    // 卡片代號是前端 FILTER_COMMON_CONDITIONS 的 key，只有英文字母。
    private static bool IsCardPosition(JsonElement position)
    {
        if (position.ValueKind != JsonValueKind.String || position.GetString() is not { Length: > 0 } value) return false;
        if (value.Length > 64) return false;
        foreach (var character in value)
            if (!char.IsAsciiLetter(character)) return false;
        return true;
    }

    private static FilterGroupSpec ParseGroup(JsonElement group, int moneyScale, string groupLabel)
    {
        var rules = new List<FilterConditionSpec>();
        if (group.ValueKind == JsonValueKind.Object
            && group.TryGetProperty("conditions", out var rulesElement)
            && rulesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var ruleElement in rulesElement.EnumerateArray())
            {
                rules.Add(ParseRule(ruleElement, moneyScale, $"{groupLabel}第 {rules.Count + 1} 條"));
            }
        }

        var (join, unknownJoin) = ParseJoin(group);
        return new FilterGroupSpec(join, rules, unknownJoin)
        {
            UnknownMatchScope = ParseUnknownMatchScope(group)
        };
    }

    private static FilterConditionSpec ParseRule(JsonElement rule, int moneyScale, string label, int depth = 0)
    {
        if (rule.ValueKind != JsonValueKind.Object) throw FilterScenarioCorruption.Exception(label, "condition_not_object");
        if (depth > FilterScenarioLimits.MaxNestingDepth) throw FilterScenarioCorruption.Exception(label, "nesting_too_deep");
        var typeName = ReadClosedToken(rule, "type") ?? string.Empty;
        var type = typeName switch
        {
            "group" => FilterConditionType.Group,
            "voucher" => FilterConditionType.Voucher,
            "prescreen" => FilterConditionType.Prescreen,
            "drCrOnly" => FilterConditionType.DrCrOnly,
            "manualAuto" => FilterConditionType.ManualAuto,
            "accountPair" => FilterConditionType.AccountPair,
            "specialAccountCategoryPair" => FilterConditionType.SpecialAccountCategoryPair,
            "entityFrequency" => FilterConditionType.EntityFrequency,
            "revenueDebitNearQuarterEnd" => FilterConditionType.RevenueDebitNearQuarterEnd,
            "revenueWithoutNormalCounterpart" => FilterConditionType.RevenueWithoutNormalCounterpart,
            "manualRevenueEntry" => FilterConditionType.ManualRevenueEntry,
            "preparerEqualsApprover" => FilterConditionType.PreparerEqualsApprover,
            "fieldValue" => FilterConditionType.FieldValue,
            "accountSide" => FilterConditionType.AccountSide,
            // 有寫類型但不在上面清單裡的，是已停用的條件類型，畫面請審計員刪除這一條；完全沒寫類型只會是內容損毀。
            "" => throw FilterScenarioCorruption.Exception(label, "condition_type_missing"),
            _ => throw Positioned(label, "此條件的類型已停用，請刪除此條件")
        };

        var (join, unknownJoin) = ParseJoin(rule);
        var debitCategoryIds = ParseCategoryIds(rule, "debitCategoryIds", label);
        var creditCategoryIds = ParseCategoryIds(rule, "creditCategoryIds", label);

        string? fieldId = null;
        string? typedOperator = null;
        string? typedValue = null;
        string? typedFrom = null;
        string? typedTo = null;
        string? amountBasis = null;
        IReadOnlyList<string>? typedValues = null;
        if (type == FilterConditionType.FieldValue)
        {
            // 欄位值條件裡放比較值的屬性（value、from、to、values）必須是字串或字串陣列；
            // 是 null 或其他型別時，這裡就丟出條件內容損毀的錯誤。字串照原樣保留，前後空白也不去掉。
            // 運算子這類固定代號寫得對不對、這個運算子該帶哪一種比較值，由 Domain 的驗證依欄位型別判斷。
            fieldId = ReadTypedString(rule, "fieldId", label);
            typedOperator = ReadTypedString(rule, "operator", label);
            typedValue = ReadTypedString(rule, "value", label);
            typedFrom = ReadTypedString(rule, "from", label);
            typedTo = ReadTypedString(rule, "to", label);
            amountBasis = ReadTypedString(rule, "amountBasis", label);
            typedValues = ParseTypedValues(rule, label);
        }

        return new FilterConditionSpec(
            join,
            type,
            ReadString(rule, "prescreenKey"),
            ReadString(rule, "field"),
            [],
            type == FilterConditionType.FieldValue ? ReadTypedString(rule, "drCr", label) : ReadString(rule, "drCr"),
            ParseManual(rule),
            PairMode: ReadString(rule, "pairMode"),
            WindowDays: ParseInt(rule, "windowDays"),
            UnknownJoin: unknownJoin)
        {
            Conditions = ParseChildren(rule, type, moneyScale, depth, label),
            Quantifier = ReadConditionChoice(rule, "quantifier", label),
            Side = ReadConditionChoice(rule, "side", label),
            CategorySelection = ReadConditionChoice(rule, "categorySelection", label),
            DebitCategoryIds = debitCategoryIds ?? [],
            CreditCategoryIds = creditCategoryIds ?? [],
            FieldId = fieldId,
            TypedOperator = typedOperator,
            TypedValue = typedValue,
            TypedFrom = typedFrom,
            TypedTo = typedTo,
            TypedValues = typedValues,
            AmountBasis = amountBasis,
            IncludeBlank = ReadOptionalBoolean(rule, "includeBlank", label),
            CategoryMode = ReadClosedToken(rule, "categoryMode"),
            CountUnit = ReadClosedToken(rule, "countUnit"),
            CountOperator = ReadClosedToken(rule, "countOperator"),
            CountFrom = ParseInt(rule, "countFrom"),
            CountTo = ParseInt(rule, "countTo"),
            CategoryIds = AccountPairCategorySelection.Canonicalize(ParseCategoryIds(rule, "categoryIds", label) ?? [])
        };
    }

    private static IReadOnlyList<FilterConditionSpec> ParseChildren(JsonElement rule, FilterConditionType type, int scale, int depth, string label)
    {
        if (!rule.TryGetProperty("conditions", out var children)) return [];
        if (type is not (FilterConditionType.Group or FilterConditionType.Voucher) || children.ValueKind != JsonValueKind.Array)
            throw FilterScenarioCorruption.Exception(label, "children_not_allowed");
        return children.EnumerateArray()
            .Select((child, index) => ParseRule(child, scale, $"{label} 子條件 {index + 1}", depth + 1))
            .ToArray();
    }

    private static string? ReadConditionChoice(JsonElement rule, string name, string label)
    {
        if (!rule.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) throw FilterScenarioCorruption.Exception(label, "condition_choice_not_string");
        return value.GetString();
    }

    private static bool? ReadOptionalBoolean(JsonElement element, string property, string label)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true, JsonValueKind.False => false,
            _ => throw FilterScenarioCorruption.Exception(label, "boolean_expected")
        };
    }

    /// <summary>
    /// 讀欄位值條件的字串屬性。字串照原樣回傳；沒有這個屬性時回 null。
    /// 屬性存在但值是 null、數字、布林等不是字串的 JSON 值時，丟出條件內容損毀的錯誤，因為這些條件的比較值必須是字串。
    /// 這裡不能用 ReadString：ReadString 會把全是空白的字串當成 null，「帶了空白值」就會被讀成「沒帶」，
    /// 「是空白」這類不需要比較值的運算子帶了比較值時就檢查不出來。
    /// </summary>
    private static string? ReadTypedString(JsonElement rule, string name, string label)
    {
        if (rule.ValueKind != JsonValueKind.Object || !rule.TryGetProperty(name, out var property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw FilterScenarioCorruption.Exception(label, "typed_string_expected");
        }

        return property.GetString() ?? string.Empty;
    }

    /// <summary>
    /// 欄位值條件的 values 陣列，元素都必須是字串。沒有這個屬性時回 null。
    /// 個數超過 <see cref="FilterScenarioLimits.MaxCompiledParameters"/> 時在這裡就丟出條件內容損毀的錯誤；
    /// 每條清單畫面看得懂的上限由 Domain 的驗證檢查。
    /// </summary>
    private static IReadOnlyList<string>? ParseTypedValues(JsonElement rule, string label)
    {
        if (rule.ValueKind != JsonValueKind.Object || !rule.TryGetProperty("values", out var values))
        {
            return null;
        }

        if (values.ValueKind != JsonValueKind.Array)
        {
            throw FilterScenarioCorruption.Exception(label, "typed_values_not_array");
        }

        var result = new List<string>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                throw FilterScenarioCorruption.Exception(label, "typed_values_not_strings");
            }

            if (result.Count >= FilterScenarioLimits.MaxCompiledParameters)
            {
                throw FilterScenarioCorruption.Exception(label, "typed_values_too_many");
            }

            result.Add(value.GetString() ?? string.Empty);
        }

        return result;
    }

    /// <summary>
    /// 科目配對條件借方或貸方一側所選的分類 ID 陣列。回傳 null 代表 JSON 裡沒有這個屬性，呼叫端當成空集合。
    /// 帶了空陣列代表使用者沒有選任何分類，回傳空集合，再由驗證回 invalid_scenario。
    /// 兩種情形都不讀 debitCategory、creditCategory，這兩個欄位照未知欄位處理。
    /// </summary>
    private static IReadOnlyList<string>? ParseCategoryIds(JsonElement rule, string property, string label)
    {
        if (rule.ValueKind != JsonValueKind.Object
            || !rule.TryGetProperty(property, out var element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw FilterScenarioCorruption.Exception(label, "category_ids_not_array");
        }

        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw FilterScenarioCorruption.Exception(label, "category_ids_not_strings");
            }

            if (values.Count >= FilterScenarioLimits.MaxCategoryIdsPerSide)
            {
                throw FilterScenarioCorruption.Exception(label, "category_ids_too_many");
            }

            values.Add(item.GetString() ?? string.Empty);
        }

        return values;
    }

    private static int? ParseInt(JsonElement rule, string name)
    {
        if (rule.ValueKind != JsonValueKind.Object || !rule.TryGetProperty(name, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(property.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    /// <summary>
    /// 讀連接詞 join：AND 與 OR 不分大小寫；沒有這個屬性、是 null 或空白時當成 AND，讓省略這個欄位的條件照 AND 連接。
    /// 其他值不在這裡丟錯誤：已儲存的 JSON 在重算結果與查詢時還會由這裡重新解析，而且不再經過驗證，所以解析要能容忍。
    /// 認不得的值放在 UnknownJoin 原樣帶回，由 Domain 的驗證回 invalid_scenario。
    /// </summary>
    private static (FilterJoin Join, string? UnknownJoin) ParseJoin(JsonElement element)
    {
        var join = ReadString(element, "join");
        if (join is null || string.Equals(join, "AND", StringComparison.OrdinalIgnoreCase))
        {
            return (FilterJoin.And, null);
        }

        return string.Equals(join, "OR", StringComparison.OrdinalIgnoreCase)
            ? (FilterJoin.Or, null)
            : (FilterJoin.And, join);
    }

    /// <summary>
    /// 條件組只有同一分錄列一種比對範圍：沒寫或寫 row 時回 null，其他值原樣帶回，由驗證層當成無法讀取的內容。
    /// </summary>
    private static string? ParseUnknownMatchScope(JsonElement group)
    {
        var matchScope = ReadClosedToken(group, "matchScope");
        return matchScope is null or "row" ? null : matchScope;
    }

    private static bool? ParseManual(JsonElement rule)
    {
        if (rule.ValueKind != JsonValueKind.Object || !rule.TryGetProperty("isManual", out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String)
        {
            var text = property.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        return null;
    }

    /// <summary>
    /// 讀只接受固定幾個代號的屬性，例如 type 與 categoryMode。代號照原始 JSON 的寫法保存，供讀回文字使用。
    /// 不是空白的值在這裡不去掉前後空白：如果執行時把前後多了空白的代號當成標準寫法接受，保存的卻是原始寫法，
    /// 讀回文字與重新解析時就可能顯示成另一個意思。
    /// 沒有這個屬性、不是字串、是 null 或全是空白時回 null，由呼叫端套用預設值；
    /// 其他不是標準寫法的值原樣交給後面的 switch 或 Domain 的驗證，在那裡回錯誤。
    /// </summary>
    private static string? ReadClosedToken(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String)
        {
            var text = property.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        return null;
    }

    private static JetActionException Invalid(string message) =>
        new(JetErrorCodes.InvalidScenario, message);

    /// <summary>帶「第 N 組第 M 條」位置的原因；細節帶組號條號，儲存時標在那一條條件下方。</summary>
    private static JetActionException Positioned(string label, string reason)
    {
        var message = $"{label}：{reason}";
        return new JetActionException(JetErrorCodes.InvalidScenario, message)
        {
            Details = FilterScenarioErrorDetails.Parse([message])
        };
    }
}
