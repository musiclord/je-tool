using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// 前端 Query Builder 的 scenario JSON → FilterScenarioSpec。
/// 此層只處理「形狀」（型別字串、金額轉 scaled、關鍵字分割）；
/// 業務規則（白名單、必填、邊界）由 Domain FilterScenarioValidator 負責。
/// 形狀錯誤一律擲 invalid_scenario。
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
        if (scenario.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("scenario 必須是物件。");
        }

        var name = ReadString(scenario, "name") ?? string.Empty;
        var rationale = ReadString(scenario, "rationale") ?? string.Empty;

        // 選填來源標記（wire 欄位 scenario.source）：ReadString 已 trim 並把空白正規化為 null，
        // 故未知/空白來源自然落為「查核員手寫」（Domain 只認得 "kct"，其餘等同 null）。
        var source = ReadString(scenario, "source");

        var groups = new List<FilterGroupSpec>();
        if (scenario.TryGetProperty("groups", out var groupsElement)
            && groupsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var groupElement in groupsElement.EnumerateArray())
            {
                groups.Add(ParseGroup(groupElement, moneyScale));
            }
        }

        // 2026-09-07 裁定：情境層排除區域已移除；排除由條件本身的否定模式承擔。舊 wire 帶 `exclusions`
        // 時 fail-loud，讓審計員把它改成一般條件，而不是靜默忽略讓命中變多。
        if (scenario.TryGetProperty("exclusions", out var exclusionElements)
            && exclusionElements.ValueKind == JsonValueKind.Array && exclusionElements.GetArrayLength() > 0)
            throw Invalid("這個情境使用了已移除的「排除區域」；請改用日期、文字或金額條件的「不屬於」「不在區間」等模式後重新儲存。");
        ValidateEditorOrigins(scenario, groups);
        return new FilterScenarioSpec(name.Trim(), rationale.Trim(), groups, source);
    }

    // Editing provenance is structurally checked, but never used by the rule compiler.
    private static void ValidateEditorOrigins(JsonElement scenario, IReadOnlyList<FilterGroupSpec> groups)
    {
        if (!scenario.TryGetProperty("editorOrigins", out var origins)) return;
        if (origins.ValueKind != JsonValueKind.Object || !origins.TryGetProperty("version", out var version)
            || !version.TryGetInt32(out var number) || number != 1
            || !origins.TryGetProperty("groups", out var items) || items.ValueKind != JsonValueKind.Array
            || items.GetArrayLength() != groups.Count) throw Invalid("條件編輯來源不完整，請重新開啟情境後再儲存。");
        if (origins.TryGetProperty("legacyKctSource", out var legacy)
            && legacy.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Invalid("舊情境來源資訊無效，請重新開啟情境。");
        foreach (var key in new[] { "nameIsAutomatic", "rationaleIsAutomatic" })
            if (origins.TryGetProperty(key, out var automatic)
                && automatic.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw Invalid("情境名稱或動機的編輯來源無效，請重新開啟情境後再儲存。");
        for (var i = 0; i < groups.Count; i++)
        {
            var item = items[i];
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("letters", out var letters)
                || letters.ValueKind != JsonValueKind.Array || letters.GetArrayLength() != groups[i].Rules.Count)
                throw Invalid("條件編輯來源與條件位置不一致，請重新開啟情境。");
            foreach (var letter in letters.EnumerateArray())
                if (letter.ValueKind != JsonValueKind.Null && (letter.ValueKind != JsonValueKind.String
                    || letter.GetString() is not { Length: 1 } value || value[0] < 'A' || value[0] > 'J'))
                    throw Invalid("這個情境的 KCT 標記已損毀，請重新開啟情境，或重新加入條件後儲存。");
            // presetGroup 只會出現在 2026-10-02 以前儲存的情境：當時非營業日 I 自成一組。新版前端不再產生、
            // 也不再讀它，但已儲存的定義會原樣送回，所以仍接受並只檢查型別，不轉換舊情境。
            if (item.TryGetProperty("presetGroup", out var preset) && preset.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw Invalid("KCT 群組來源無效，請重新開啟情境。");
        }
    }

    private static FilterGroupSpec ParseGroup(JsonElement group, int moneyScale)
    {
        var rules = new List<FilterRuleSpec>();
        if (group.ValueKind == JsonValueKind.Object
            && group.TryGetProperty("rules", out var rulesElement)
            && rulesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var ruleElement in rulesElement.EnumerateArray())
            {
                rules.Add(ParseRule(ruleElement, moneyScale));
            }
        }

        var (join, unknownJoin) = ParseJoin(group);
        var (matchScope, unknownMatchScope) = ParseMatchScope(group);
        return new FilterGroupSpec(join, rules, unknownJoin)
        {
            MatchScope = matchScope,
            UnknownMatchScope = unknownMatchScope
        };
    }

    private static FilterRuleSpec ParseRule(JsonElement rule, int moneyScale, int depth = 0)
    {
        if (rule.ValueKind != JsonValueKind.Object) throw Invalid("條件必須是物件。");
        if (depth > FilterScenarioLimits.MaxNestingDepth) throw Invalid("條件最多可巢狀八層，請減少括號層數。");
        var typeName = ReadClosedToken(rule, "type") ?? string.Empty;
        var type = typeName switch
        {
            "group" => FilterRuleType.Group,
            "voucher" => FilterRuleType.Voucher,
            "prescreen" => FilterRuleType.Prescreen,
            "text" => FilterRuleType.Text,
            "textSet" => FilterRuleType.TextSet,
            "dateRange" => FilterRuleType.DateRange,
            "numRange" => FilterRuleType.NumRange,
            "drCrOnly" => FilterRuleType.DrCrOnly,
            "manualAuto" => FilterRuleType.ManualAuto,
            "accountPair" => FilterRuleType.AccountPair,
            "specialAccountCategoryPair" => FilterRuleType.SpecialAccountCategoryPair,
            "customKeywords" => FilterRuleType.CustomKeywords,
            "customTrailingZeros" => FilterRuleType.CustomTrailingZeros,
            "customPreparerEntryCount" => FilterRuleType.CustomPreparerEntryCount,
            "customAccountEntryCount" => FilterRuleType.CustomAccountEntryCount,
            "entityFrequency" => FilterRuleType.EntityFrequency,
            "revenueDebitNearQuarterEnd" => FilterRuleType.RevenueDebitNearQuarterEnd,
            "revenueWithoutNormalCounterpart" => FilterRuleType.RevenueWithoutNormalCounterpart,
            "manualRevenueEntry" => FilterRuleType.ManualRevenueEntry,
            "trailingDigits" => FilterRuleType.TrailingDigits,
            "preparerEqualsApprover" => FilterRuleType.PreparerEqualsApprover,
            "typed" => FilterRuleType.TypedField,
            "fieldValue" => FilterRuleType.FieldValue,
            "accountSide" => FilterRuleType.AccountSide,
            _ => throw Invalid($"不支援的條件型別「{typeName}」。")
        };

        var modeName = ReadClosedToken(rule, "mode") ?? "contains";
        var mode = modeName switch
        {
            "contains" => TextMatchMode.Contains,
            "exact" => TextMatchMode.Exact,
            "notContains" => TextMatchMode.NotContains,
            "notExact" => TextMatchMode.NotExact,
            _ => throw Invalid($"不支援的文字比對模式「{modeName}」。")
        };

        var keywords = FieldValueConditions.SplitInputList(ReadString(rule, "keywords"));
        var values = type == FilterRuleType.TextSet
            ? ParseTextSetValues(rule)
            : Array.Empty<string>();
        var (normalization, unknownNormalization) = type == FilterRuleType.TextSet
            ? ParseTextSetNormalization(rule)
            : (TextSetNormalization.Preserve, null);

        long? fromScaled = null;
        long? toScaled = null;
        string? fromDate = null;
        string? toDate = null;

        if (type == FilterRuleType.NumRange)
        {
            fromScaled = ParseAmount(ReadString(rule, "from"), moneyScale);
            toScaled = ParseAmount(ReadString(rule, "to"), moneyScale);
        }
        else if (type == FilterRuleType.DateRange)
        {
            fromDate = ReadString(rule, "from");
            toDate = ReadString(rule, "to");
        }

        var (join, unknownJoin) = ParseJoin(rule);
        var debitCategoryIds = ParseCategoryIds(rule, "debitCategoryIds");
        var creditCategoryIds = ParseCategoryIds(rule, "creditCategoryIds");

        string? fieldId = null;
        string? typedOperator = null;
        string? typedValue = null;
        string? typedFrom = null;
        string? typedTo = null;
        string? amountBasis = null;
        IReadOnlyList<string>? typedValues = null;
        if (type is FilterRuleType.TypedField or FilterRuleType.FieldValue)
        {
            // typed（2026-08-14 凍結）的 operand carrier 必須是非 null 字串；null、非字串或
            // 非字串陣列在形狀層就 fail loud。原始拼法（含空白）原樣保留，closed token 的
            // 正準性與 carrier 適用性由 Domain validator 依欄位型別裁定。
            fieldId = ReadTypedString(rule, "fieldId");
            typedOperator = ReadTypedString(rule, "operator");
            if (type == FilterRuleType.TypedField && typedOperator is not null && !TypedFieldOperatorSets.All.Contains(typedOperator))
            {
                throw Invalid($"不支援的 typed operator「{typedOperator}」。");
            }

            typedValue = ReadTypedString(rule, "value");
            typedFrom = ReadTypedString(rule, "from");
            typedTo = ReadTypedString(rule, "to");
            amountBasis = ReadTypedString(rule, "amountBasis");
            typedValues = ParseTypedValues(rule, type == FilterRuleType.FieldValue);
        }

        return new FilterRuleSpec(
            join,
            type,
            ReadString(rule, "prescreenKey"),
            ReadString(rule, "field"),
            keywords,
            mode,
            fromDate,
            toDate,
            fromScaled,
            toScaled,
            type == FilterRuleType.FieldValue ? ReadTypedString(rule, "drCr") : ReadString(rule, "drCr"),
            ParseManual(rule),
            PairMode: ReadString(rule, "pairMode"),
            Digits: ParseInt(rule, "digits"),
            MaxEntries: ParseInt(rule, "maxEntries"),
            WindowDays: ParseInt(rule, "windowDays"),
            UnknownJoin: unknownJoin)
        {
            Rules = ParseChildren(rule, type, moneyScale, depth),
            Quantifier = ReadConditionChoice(rule, "quantifier", "符合方式"),
            Side = ReadConditionChoice(rule, "side", "傳票判斷範圍"),
            CategorySelection = ReadConditionChoice(rule, "categorySelection", "分類選取方式"),
            Values = values,
            Normalization = normalization,
            UnknownNormalization = unknownNormalization,
            DebitCategoryIds = debitCategoryIds ?? [],
            CreditCategoryIds = creditCategoryIds ?? [],
            FieldId = fieldId,
            TypedOperator = typedOperator,
            TypedValue = typedValue,
            TypedFrom = typedFrom,
            TypedTo = typedTo,
            TypedValues = typedValues,
            AmountBasis = amountBasis,
            IncludeBlank = ReadOptionalBoolean(rule, "includeBlank"),
            CategoryMode = ReadClosedToken(rule, "categoryMode"),
            CountUnit = ReadClosedToken(rule, "countUnit"),
            CountOperator = ReadClosedToken(rule, "countOperator"),
            CountFrom = ParseInt(rule, "countFrom"),
            CountTo = ParseInt(rule, "countTo"),
            CategoryIds = AccountPairCategorySelection.Canonicalize(ParseCategoryIds(rule, "categoryIds") ?? [])
        };
    }

    private static IReadOnlyList<FilterRuleSpec> ParseChildren(JsonElement rule, FilterRuleType type, int scale, int depth)
    {
        if (!rule.TryGetProperty("rules", out var children)) return [];
        if (type is not (FilterRuleType.Group or FilterRuleType.Voucher) || children.ValueKind != JsonValueKind.Array)
            throw Invalid("子條件只能放在條件括號或傳票條件內，請重新選擇條件。");
        return children.EnumerateArray().Select(child => ParseRule(child, scale, depth + 1)).ToArray();
    }

    private static string? ReadConditionChoice(JsonElement rule, string name, string label)
    {
        if (!rule.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) throw Invalid($"{label}無效，請重新選擇。");
        return value.GetString();
    }

    private static bool? ReadOptionalBoolean(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true, JsonValueKind.False => false,
            _ => throw Invalid($"{property} 必須是布林值。")
        };
    }

    /// <summary>
    /// typed 條件的字串屬性讀取：保留原始拼法與 presence（缺欄回 null）；present 但為
    /// null、數字、布林等非字串 JSON 值一律 fail loud——凍結契約要求 operand carrier 是
    /// 非 null 字串，這裡不得沿用 ReadString 的「空白正規化為 null」（會把「帶了空白值」
    /// 誤讀成「沒帶」，讓 blank operator 帶 operand 的錯誤靜默通過）。
    /// </summary>
    private static string? ReadTypedString(JsonElement rule, string name)
    {
        if (rule.ValueKind != JsonValueKind.Object || !rule.TryGetProperty(name, out var property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw Invalid($"typed 條件的 {name} 必須是非 null 字串。");
        }

        return property.GetString() ?? string.Empty;
    }

    /// <summary>typed `in`／`notIn` 的 values：null＝沒帶陣列；1–100 個字串（形狀層先擋上限）。</summary>
    private static IReadOnlyList<string>? ParseTypedValues(JsonElement rule, bool newValueRule = false)
    {
        if (rule.ValueKind != JsonValueKind.Object || !rule.TryGetProperty("values", out var values))
        {
            return null;
        }

        if (values.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("typed 條件的 values 必須是字串陣列。");
        }

        var result = new List<string>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                throw Invalid("typed 條件的 values 每個元素都必須是字串。");
            }

            if (result.Count >= (newValueRule ? FilterScenarioLimits.MaxCompiledParameters : FilterScenarioLimits.MaxTypedInValuesPerRule))
            {
                throw Invalid(
                    $"typed 條件的 values 最多 {FilterScenarioLimits.MaxTypedInValuesPerRule} 個值。");
            }

            result.Add(value.GetString() ?? string.Empty);
        }

        return result;
    }

    /// <summary>
    /// 科目配對單側的分類身分陣列。回傳 null 代表 wire 沒有帶這個 key（此時才回退 legacy scalar）；
    /// 明示帶了空陣列是「使用者沒有選任何分類」，回傳空集合並由 validator 以 invalid_scenario 擋下，
    /// 不得靜默回退到可能過期的 scalar。帶了陣列時 scalar 一律不參與判定。
    /// </summary>
    private static IReadOnlyList<string>? ParseCategoryIds(JsonElement rule, string property)
    {
        if (rule.ValueKind != JsonValueKind.Object
            || !rule.TryGetProperty(property, out var element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Invalid($"{property} 必須是字串陣列。");
        }

        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw Invalid($"{property} 的每個元素都必須是字串。");
            }

            if (values.Count >= FilterScenarioLimits.MaxCategoryIdsPerSide)
            {
                throw Invalid(
                    $"{property} 最多 {FilterScenarioLimits.MaxCategoryIdsPerSide} 個分類。");
            }

            values.Add(item.GetString() ?? string.Empty);
        }

        return values;
    }

    private static IReadOnlyList<string> ParseTextSetValues(JsonElement rule)
    {
        if (rule.ValueKind != JsonValueKind.Object
            || !rule.TryGetProperty("values", out var values))
        {
            return [];
        }

        if (values.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("textSet.values 必須是字串陣列。");
        }

        var result = new List<string>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                throw Invalid("textSet.values 的每個元素都必須是字串。");
            }

            if (result.Count >= FilterScenarioLimits.MaxTextSetValuesPerRule)
            {
                throw Invalid(
                    $"textSet.values 最多 {FilterScenarioLimits.MaxTextSetValuesPerRule} 個值。");
            }

            result.Add(value.GetString() ?? string.Empty);
        }

        return result;
    }

    private static (TextSetNormalization Normalization, string? UnknownNormalization)
        ParseTextSetNormalization(JsonElement rule)
    {
        var normalization = ReadClosedToken(rule, "normalization");
        return normalization switch
        {
            null or "preserve" => (TextSetNormalization.Preserve, null),
            "removeAsciiSpaces" => (TextSetNormalization.RemoveAsciiSpaces, null),
            _ => (TextSetNormalization.Preserve, normalization)
        };
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
    /// join 結合方式：AND/OR 不分大小寫；缺欄、null、空白預設 AND（向後相容省略欄位的編碼）。
    /// 其他值不在這裡擲錯——materialize 會重新解析已存 JSON 且不經驗證，解析層必須容忍；
    /// 未知值以 UnknownJoin 帶回原始字串，由 Domain 驗證層擋成 invalid_scenario。
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

    private static (FilterGroupMatchScope MatchScope, string? UnknownMatchScope)
        ParseMatchScope(JsonElement group)
    {
        var matchScope = ReadClosedToken(group, "matchScope");
        return matchScope switch
        {
            null or "row" => (FilterGroupMatchScope.Row, null),
            "sameVoucher" => (FilterGroupMatchScope.SameVoucher, null),
            _ => (FilterGroupMatchScope.Row, matchScope)
        };
    }

    private static long? ParseAmount(string? text, int moneyScale)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!MoneyScaling.TryParseAmount(text, out var value)
            || !MoneyScaling.TryToScaled(value, moneyScale, out var scaled))
        {
            throw Invalid($"金額「{text}」格式無效。");
        }

        return scaled;
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
    /// Closed discriminator tokens are persisted from the original wire JSON for read-back.
    /// Do not trim a nonblank token here: accepting a padded canonical token for execution while
    /// preserving its raw spelling would let renderer/replay display a different meaning.
    /// Missing, non-string, null, and whitespace-only values keep their documented defaults;
    /// any other non-canonical spelling reaches the closed switch/Domain validator and fails loud.
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
}
