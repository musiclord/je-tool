using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// 前端 Query Builder 的 scenario JSON → FilterScenarioSpec。
/// 此層只處理「形狀」（型別字串、金額轉 scaled、關鍵字分割）；
/// 業務規則（白名單、必填、邊界）由 Domain FilterScenarioValidator 負責。
/// 形狀錯誤一律擲 invalid_scenario。只有存檔損毀或程式送錯資料才會出現的形狀錯誤，畫面只寫
/// <see cref="FilterScenarioCorruption.RuleMessage"/>，原因代號記進支援日誌（使用者 2026-10-13 裁定「損毀的情境」）；
/// 審計員看得懂、也做得到的原因（已移除的排除區域、已停用的條件類型與比較方式、金額不是數字）照確認的句子寫。
/// 條件的錯誤帶「第 N 組第 M 條」位置，儲存時才標得到那一條條件。
/// </summary>
public static class FilterScenarioPayloadParser
{
    /// <summary>舊情境帶已移除的排除區時的原因；第五步編輯器的唯讀「舊排除區」區塊照抄這一句。</summary>
    internal const string RemovedExclusionsMessage = "使用了已移除的「排除區域」，請改用條件中的「不屬於」或「不在區間」";

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
            throw FilterScenarioCorruption.Exception(null, "scenario_not_object");
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
                groups.Add(ParseGroup(groupElement, moneyScale, $"第 {groups.Count + 1} 組"));
            }
        }

        // 2026-09-07 裁定：情境層排除區域已移除；排除由條件本身的否定模式承擔。舊 wire 帶 `exclusions`
        // 時 fail-loud，讓審計員把它改成一般條件，而不是靜默忽略讓命中變多。
        if (scenario.TryGetProperty("exclusions", out var exclusionElements)
            && exclusionElements.ValueKind == JsonValueKind.Array && exclusionElements.GetArrayLength() > 0)
            throw Invalid(RemovedExclusionsMessage);
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
            || items.GetArrayLength() != groups.Count) throw FilterScenarioCorruption.Exception(null, "editor_origins_incomplete");
        if (origins.TryGetProperty("legacyKctSource", out var legacy)
            && legacy.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw FilterScenarioCorruption.Exception(null, "editor_origins_legacy_source_invalid");
        foreach (var key in new[] { "nameIsAutomatic", "rationaleIsAutomatic" })
            if (origins.TryGetProperty(key, out var automatic)
                && automatic.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw FilterScenarioCorruption.Exception(null, "editor_origins_automatic_flag_invalid");
        for (var i = 0; i < groups.Count; i++)
        {
            var item = items[i];
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("letters", out var letters)
                || letters.ValueKind != JsonValueKind.Array || letters.GetArrayLength() != groups[i].Rules.Count)
                throw FilterScenarioCorruption.Exception(null, "editor_origins_positions_mismatch");
            foreach (var letter in letters.EnumerateArray())
                if (letter.ValueKind != JsonValueKind.Null && (letter.ValueKind != JsonValueKind.String
                    || letter.GetString() is not { Length: 1 } value || value[0] < 'A' || value[0] > 'J'))
                    throw FilterScenarioCorruption.Exception(null, "editor_origins_kct_letter_invalid");
            // presetGroup 只會出現在 2026-10-02 以前儲存的情境：當時非營業日 I 自成一組。新版前端不再產生、
            // 也不再讀它，但已儲存的定義會原樣送回，所以仍接受並只檢查型別，不轉換舊情境。
            if (item.TryGetProperty("presetGroup", out var preset) && preset.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw FilterScenarioCorruption.Exception(null, "editor_origins_preset_group_invalid");
        }
    }

    private static FilterGroupSpec ParseGroup(JsonElement group, int moneyScale, string groupLabel)
    {
        var rules = new List<FilterRuleSpec>();
        if (group.ValueKind == JsonValueKind.Object
            && group.TryGetProperty("rules", out var rulesElement)
            && rulesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var ruleElement in rulesElement.EnumerateArray())
            {
                rules.Add(ParseRule(ruleElement, moneyScale, $"{groupLabel}第 {rules.Count + 1} 條"));
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

    private static FilterRuleSpec ParseRule(JsonElement rule, int moneyScale, string label, int depth = 0)
    {
        if (rule.ValueKind != JsonValueKind.Object) throw FilterScenarioCorruption.Exception(label, "rule_not_object");
        if (depth > FilterScenarioLimits.MaxNestingDepth) throw FilterScenarioCorruption.Exception(label, "nesting_too_deep");
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
            // 有寫類型但目前版本沒有的，是已停用的條件類型；完全沒寫類型只會是內容損毀。
            "" => throw FilterScenarioCorruption.Exception(label, "rule_type_missing"),
            _ => throw Positioned(label, "此條件的類型已停用，請刪除此條件")
        };

        var modeName = ReadClosedToken(rule, "mode") ?? "contains";
        var mode = modeName switch
        {
            "contains" => TextMatchMode.Contains,
            "exact" => TextMatchMode.Exact,
            "notContains" => TextMatchMode.NotContains,
            "notExact" => TextMatchMode.NotExact,
            _ => throw FilterScenarioCorruption.Exception(label, "text_mode_invalid")
        };

        var keywords = FieldValueConditions.SplitInputList(ReadString(rule, "keywords"));
        var values = type == FilterRuleType.TextSet
            ? ParseTextSetValues(rule, label)
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
            fromScaled = ParseAmount(ReadString(rule, "from"), moneyScale, label);
            toScaled = ParseAmount(ReadString(rule, "to"), moneyScale, label);
        }
        else if (type == FilterRuleType.DateRange)
        {
            fromDate = ReadString(rule, "from");
            toDate = ReadString(rule, "to");
        }

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
        if (type is FilterRuleType.TypedField or FilterRuleType.FieldValue)
        {
            // typed（2026-08-14 凍結）的 operand carrier 必須是非 null 字串；null、非字串或
            // 非字串陣列在形狀層就 fail loud。原始拼法（含空白）原樣保留，closed token 的
            // 正準性與 carrier 適用性由 Domain validator 依欄位型別裁定。
            fieldId = ReadTypedString(rule, "fieldId", label);
            typedOperator = ReadTypedString(rule, "operator", label);
            if (type == FilterRuleType.TypedField && typedOperator is not null && !TypedFieldOperatorSets.All.Contains(typedOperator))
            {
                throw Positioned(label, "此條件的比較方式已停用，請重新選擇");
            }

            typedValue = ReadTypedString(rule, "value", label);
            typedFrom = ReadTypedString(rule, "from", label);
            typedTo = ReadTypedString(rule, "to", label);
            amountBasis = ReadTypedString(rule, "amountBasis", label);
            typedValues = ParseTypedValues(rule, label, type == FilterRuleType.FieldValue);
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
            type == FilterRuleType.FieldValue ? ReadTypedString(rule, "drCr", label) : ReadString(rule, "drCr"),
            ParseManual(rule),
            PairMode: ReadString(rule, "pairMode"),
            Digits: ParseInt(rule, "digits"),
            MaxEntries: ParseInt(rule, "maxEntries"),
            WindowDays: ParseInt(rule, "windowDays"),
            UnknownJoin: unknownJoin)
        {
            Rules = ParseChildren(rule, type, moneyScale, depth, label),
            Quantifier = ReadConditionChoice(rule, "quantifier", label),
            Side = ReadConditionChoice(rule, "side", label),
            CategorySelection = ReadConditionChoice(rule, "categorySelection", label),
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
            IncludeBlank = ReadOptionalBoolean(rule, "includeBlank", label),
            CategoryMode = ReadClosedToken(rule, "categoryMode"),
            CountUnit = ReadClosedToken(rule, "countUnit"),
            CountOperator = ReadClosedToken(rule, "countOperator"),
            CountFrom = ParseInt(rule, "countFrom"),
            CountTo = ParseInt(rule, "countTo"),
            CategoryIds = AccountPairCategorySelection.Canonicalize(ParseCategoryIds(rule, "categoryIds", label) ?? [])
        };
    }

    private static IReadOnlyList<FilterRuleSpec> ParseChildren(JsonElement rule, FilterRuleType type, int scale, int depth, string label)
    {
        if (!rule.TryGetProperty("rules", out var children)) return [];
        if (type is not (FilterRuleType.Group or FilterRuleType.Voucher) || children.ValueKind != JsonValueKind.Array)
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
    /// typed 條件的字串屬性讀取：保留原始拼法與 presence（缺欄回 null）；present 但為
    /// null、數字、布林等非字串 JSON 值一律 fail loud——凍結契約要求 operand carrier 是
    /// 非 null 字串，這裡不得沿用 ReadString 的「空白正規化為 null」（會把「帶了空白值」
    /// 誤讀成「沒帶」，讓 blank operator 帶 operand 的錯誤靜默通過）。
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

    /// <summary>typed `in`／`notIn` 的 values：null＝沒帶陣列；1–100 個字串（形狀層先擋上限）。</summary>
    private static IReadOnlyList<string>? ParseTypedValues(JsonElement rule, string label, bool newValueRule = false)
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

            if (result.Count >= (newValueRule ? FilterScenarioLimits.MaxCompiledParameters : FilterScenarioLimits.MaxTypedInValuesPerRule))
            {
                throw FilterScenarioCorruption.Exception(label, "typed_values_too_many");
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

    private static IReadOnlyList<string> ParseTextSetValues(JsonElement rule, string label)
    {
        if (rule.ValueKind != JsonValueKind.Object
            || !rule.TryGetProperty("values", out var values))
        {
            return [];
        }

        if (values.ValueKind != JsonValueKind.Array)
        {
            throw FilterScenarioCorruption.Exception(label, "text_set_values_not_array");
        }

        var result = new List<string>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                throw FilterScenarioCorruption.Exception(label, "text_set_values_not_strings");
            }

            if (result.Count >= FilterScenarioLimits.MaxTextSetValuesPerRule)
            {
                throw FilterScenarioCorruption.Exception(label, "text_set_values_too_many");
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

    private static long? ParseAmount(string? text, int moneyScale, string label)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!MoneyScaling.TryParseAmount(text, out var value)
            || !MoneyScaling.TryToScaled(value, moneyScale, out var scaled))
        {
            throw Positioned(label, FilterScenarioValidator.AmountFormatReason(text));
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
