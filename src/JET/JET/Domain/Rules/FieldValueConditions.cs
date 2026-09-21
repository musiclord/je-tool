namespace JET.Domain;

/// <summary>
/// 通用值條件（type:"fieldValue"）：核心文字、日期、金額欄位與已配對的額外欄位共用同一份比較方式選單。
/// 日期另有「每月幾日屬於」與「每月幾日不屬於」（2026-09-04 裁定，值為 1 到 31）；文字的「包含任一文字」
/// 與「不包含任何文字」接受多個關鍵字（對齊 legacy 的包含就排除）。舊 typed 型別不變。
/// </summary>
public static class FieldValueConditions
{
    public const string DayOfMonthIn = "dayOfMonthIn";
    public const string DayOfMonthNotIn = "dayOfMonthNotIn";

    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        ["equals"] = "等於", ["notEquals"] = "不等於", ["contains"] = "包含任一文字", ["notContains"] = "不包含任何文字",
        ["startsWith"] = "開頭符合", ["endsWith"] = "結尾符合",
        ["notStartsWith"] = "開頭不符合", ["notEndsWith"] = "結尾不符合", ["in"] = "符合清單任一值", ["notIn"] = "不在清單中",
        ["on"] = "指定日期", ["before"] = "早於", ["onOrBefore"] = "當日或以前", ["after"] = "晚於", ["onOrAfter"] = "當日或以後",
        [DayOfMonthIn] = "每月幾日屬於", [DayOfMonthNotIn] = "每月幾日不屬於",
        ["monthStartDays"] = "每月月初天數", ["notMonthStartDays"] = "排除每月月初天數",
        ["monthEndDays"] = "每月月底天數", ["notMonthEndDays"] = "排除每月月底天數",
        ["isWeekend"] = "週末", ["isNotWeekend"] = "週末以外",
        ["isHoliday"] = "假日清單中的日期", ["isNotHoliday"] = "假日清單以外的日期",
        ["isMakeupDay"] = "補班日", ["isNotMakeupDay"] = "補班日以外",
        ["isNonBusinessDay"] = "非營業日（排除補班日）", ["isNotNonBusinessDay"] = "非營業日以外（含補班日）",
        ["endsWithDigits"] = "整數尾數符合", ["notEndsWithDigits"] = "整數尾數不符合",
        ["greaterThan"] = "大於", ["greaterThanOrEqual"] = "大於或等於", ["lessThan"] = "小於", ["lessThanOrEqual"] = "小於或等於",
        ["between"] = "介於", ["notBetween"] = "不介於", ["isBlank"] = "空白", ["isNotBlank"] = "非空白"
    };

    internal static IReadOnlyList<string>? CanonicalValues(FilterRuleSpec rule, GlRdeFieldMetadata field, int scale)
    {
        if (rule.TypedValues is null) return null;
        if (IsDayOfMonth(rule.TypedOperator) || IsTail(rule.TypedOperator))
            return rule.TypedValues.Select(value => value.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        return rule.TypedValues.Select(value => field.ValueType switch
        {
            "text" => value.Trim().ToUpperInvariant(),
            "date" when TypedFieldOperandRules.TryNormalizeDate(value, out var iso) => iso,
            "money" when TypedFieldOperandRules.TryNormalizeMoney(value, scale, out var scaled) =>
                ((decimal)scaled / scale).ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => value
        }).Distinct(StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<string> Operators(string? type) => type switch
    {
        "text" => ["equals", "notEquals", "contains", "notContains", "startsWith", "notStartsWith", "endsWith", "notEndsWith", "in", "notIn", "isBlank", "isNotBlank"],
        "date" => ["on", "notEquals", "before", "onOrBefore", "after", "onOrAfter", "between", "notBetween", "in", "notIn",
            DayOfMonthIn, DayOfMonthNotIn, "monthStartDays", "notMonthStartDays", "monthEndDays", "notMonthEndDays",
            "isWeekend", "isNotWeekend", "isHoliday", "isNotHoliday",
            "isMakeupDay", "isNotMakeupDay", "isNonBusinessDay", "isNotNonBusinessDay", "isBlank", "isNotBlank"],
        "money" => ["equals", "notEquals", "greaterThan", "greaterThanOrEqual", "lessThan", "lessThanOrEqual", "between", "notBetween", "in", "notIn", "endsWithDigits", "notEndsWithDigits", "isBlank", "isNotBlank"],
        _ => []
    };

    public static bool IsNegative(string? op) => op is "notEquals" or "notContains" or "notStartsWith" or "notEndsWith" or "notIn" or "notBetween" or DayOfMonthNotIn
        or "isNotWeekend" or "isNotHoliday" or "isNotMakeupDay" or "isNotNonBusinessDay" or "notEndsWithDigits"
        or "notMonthStartDays" or "notMonthEndDays";

    public static bool IsMonthWindow(string? op) => op is "monthStartDays" or "notMonthStartDays" or "monthEndDays" or "notMonthEndDays";

    public static bool TryParseMonthWindowDays(string? raw, out int days) =>
        int.TryParse(raw?.Trim(), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out days) && days is >= 1 and <= 31;

    public static bool IsDayOfMonth(string? op) => op is DayOfMonthIn or DayOfMonthNotIn;

    public static bool IsCalendar(string? op) => op is "isWeekend" or "isNotWeekend" or "isHoliday" or "isNotHoliday"
        or "isMakeupDay" or "isNotMakeupDay" or "isNonBusinessDay" or "isNotNonBusinessDay";
    public static bool IsTail(string? op) => op is "endsWithDigits" or "notEndsWithDigits";
    public static IReadOnlyList<string> TailPatterns(FilterRuleSpec rule) => rule.TypedValues is { Count: > 0 }
        ? rule.TypedValues : (rule.TypedValue ?? "").Split([',', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>文字的包含比對接受多個關鍵字；舊 wire 只帶單一 value 時視為一個關鍵字。</summary>
    public static bool IsContains(string? op) => op is "contains" or "notContains";

    public static IReadOnlyList<string> ContainsKeywords(FilterRuleSpec rule) =>
        rule.TypedValues is { Count: > 0 } ? rule.TypedValues
            : rule.TypedValue is null ? [] : [rule.TypedValue];

    /// <summary>每月幾日的值：1 到 31 的整數，去重；任一無法解析或越界即失敗。</summary>
    public static bool TryParseDaysOfMonth(IReadOnlyList<string>? raw, out IReadOnlyList<int> days)
    {
        var parsed = new List<int>();
        days = parsed;
        if (raw is null || raw.Count == 0) return false;
        foreach (var value in raw)
        {
            if (!int.TryParse(value.Trim(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var day) || day is < 1 or > 31)
                return false;
            if (!parsed.Contains(day)) parsed.Add(day);
        }
        return true;
    }

    public static GlRdeFieldMetadata? Resolve(FilterRuleSpec rule, IReadOnlyList<GlRdeFieldMetadata> extraFields)
    {
        if (rule.FieldId is not null)
            return rule.Field is null ? extraFields.FirstOrDefault(field => field.FieldId == rule.FieldId) : null;
        if (!GlFieldWhitelist.TryResolve(rule.Field, out var column)) return null;
        return new GlRdeFieldMetadata(rule.Field!, column.Column, rule.Field!, column.Kind switch
        {
            GlFieldKind.Text => "text", GlFieldKind.Date => "date", _ => "money"
        });
    }
}

public static partial class FilterScenarioValidator
{
    private static void ValidateFieldValue(FilterRuleSpec rule, string label,
        FilterValidationContext context, List<string> errors)
    {
        if (rule.DrCr is not null and not "debit" and not "credit")
            errors.Add($"{label}：分錄方向只能選借方或貸方；不限制時請移除方向設定。");
        var field = FieldValueConditions.Resolve(rule, context.RdeFields);
        if (field is null)
        {
            errors.Add($"{label}：欄位不存在，請回到欄位配對確認，或重新選擇篩選欄位。");
            return;
        }
        // 空白內容與沒有來源配對是兩件事。保留已配對空白值的查核用途，未配對時引導回第三步。
        if (rule.FieldId is null && rule.Field is { } coreField
            && rule.TypedOperator is "isBlank" or "isNotBlank"
            && context.AvailableGlFields is { } available && !available.Contains(coreField, StringComparer.Ordinal))
        {
            var fieldLabel = JetFieldCatalog.GlSemanticFieldMappingLabel(coreField);
            errors.Add($"{label}：{fieldLabel}尚未配對，無法判斷是否空白；請回「欄位配對」指派或改選其他欄位。");
            return;
        }
        if (rule.TypedOperator is null || !FieldValueConditions.Operators(field.ValueType).Contains(rule.TypedOperator))
        {
            errors.Add($"{label}：比較方式不適用此欄位，請重新選擇。");
            return;
        }
        if (field.ValueType == "money" && !TypedAmountBasisNames.IsCanonical(rule.AmountBasis))
            errors.Add($"{label}：請選擇比較含正負號或絕對值金額。");
        if (field.ValueType != "money" && rule.AmountBasis is not null)
            errors.Add($"{label}：只有金額欄位能設定含正負號或絕對值。");
        if (FieldValueConditions.IsMonthWindow(rule.TypedOperator))
        {
            if (!FieldValueConditions.TryParseMonthWindowDays(rule.TypedValue, out _))
                errors.Add($"{label}：每月月初或月底天數只能是 1 到 31 的整數。");
            if (rule.TypedValues is not null || rule.TypedFrom is not null || rule.TypedTo is not null)
                errors.Add($"{label}：每月月初或月底只需填天數，請移除日期清單或區間。");
            return;
        }
        if (FieldValueConditions.IsCalendar(rule.TypedOperator)) return;
        if (FieldValueConditions.IsTail(rule.TypedOperator))
        {
            var patterns = FieldValueConditions.TailPatterns(rule);
            if (patterns.Count > FilterScenarioLimits.MaxTypedInValuesPerRule)
                errors.Add($"{label}：尾數清單最多 {FilterScenarioLimits.MaxTypedInValuesPerRule} 個值。");
            ValidateTrailingDigits(rule with { Keywords = patterns }, label, errors);
            return;
        }
        if (FieldValueConditions.IsDayOfMonth(rule.TypedOperator))
        {
            if (!FieldValueConditions.TryParseDaysOfMonth(rule.TypedValues, out _))
                errors.Add($"{label}：每月幾日只能是 1 到 31 的整數，至少填一個，重複的會自動合併。");
            return;
        }
        if (field.ValueType == "text" && FieldValueConditions.IsContains(rule.TypedOperator))
        {
            var keywords = FieldValueConditions.ContainsKeywords(rule);
            if (keywords.Count is 0 or > FilterScenarioLimits.MaxTypedInValuesPerRule
                || keywords.Any(static keyword => string.IsNullOrWhiteSpace(keyword)))
                errors.Add($"{label}：包含比對需要 1 到 {FilterScenarioLimits.MaxTypedInValuesPerRule} 個非空白的文字。");
            return;
        }
        var normalized = rule with { TypedValues = FieldValueConditions.CanonicalValues(rule, field, context.MoneyScale) };
        ValidateTypedOperands(normalized.TypedOperator == "notBetween" ? normalized with { TypedOperator = "between" } : normalized,
            field, label, context, errors);
    }
}
