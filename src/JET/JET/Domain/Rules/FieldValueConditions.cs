namespace JET.Domain;

/// <summary>
/// 通用值條件（type:"fieldValue"）：核心文字、日期、金額欄位與已配對的攸關資料元素欄位共用同一份比較方式選單。
/// 日期另有「每月幾日屬於」與「每月幾日不屬於」（裁定 2026-09-04-01，值為 1 到 31）；文字的「包含任一文字」
/// 與「不包含任何文字」接受多個關鍵字（對齊 legacy 的包含就排除），開頭符合與結尾符合也一樣（裁定 2026-11-09-09）。舊 typed 型別不變。
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
        // 日曆選項的入口、選項與讀回用同一個詞（裁定 2026-11-03-10）。
        ["isWeekend"] = "在週末", ["isNotWeekend"] = "不在週末",
        ["isHoliday"] = "在國定假日", ["isNotHoliday"] = "不在國定假日",
        ["isMakeupDay"] = "在補班日", ["isNotMakeupDay"] = "不在補班日",
        ["isNonBusinessDay"] = "在非營業日", ["isNotNonBusinessDay"] = "不在非營業日",
        // 舊版「特定數字篩選」的包含與排除（裁定 2026-11-05-46），名稱照用（裁定 2026-11-05-67）。
        ["containsDigits"] = "整數部分包含", ["notContainsDigits"] = "整數部分不包含",
        ["endsWithDigits"] = "整數尾數符合", ["notEndsWithDigits"] = "整數尾數不符合",
        // 尾數連續多個 0（裁定 2026-11-03-07）：值是 0 的個數，讀回寫「尾數連續「6」個 0」。
        ["endsWithZeros"] = "尾數連續", ["notEndsWithZeros"] = "尾數沒有連續",
        ["greaterThan"] = "大於", ["greaterThanOrEqual"] = "大於或等於", ["lessThan"] = "小於", ["lessThanOrEqual"] = "小於或等於",
        ["between"] = "介於", ["notBetween"] = "不介於", ["isBlank"] = "空白", ["isNotBlank"] = "非空白"
    };

    internal static IReadOnlyList<string>? CanonicalValues(FilterConditionSpec rule, GlRdeFieldMetadata field, int scale,
        DateParseOptions? dateOptions = null)
    {
        if (rule.TypedValues is null) return null;
        if (IsDayOfMonth(rule.TypedOperator) || IsTail(rule.TypedOperator) || IsDigitContains(rule.TypedOperator))
            return rule.TypedValues.Select(value => value.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        return rule.TypedValues.Select(value => field.ValueType switch
        {
            "text" => value.Trim().ToUpperInvariant(),
            "date" when TypedFieldOperandRules.TryNormalizeDate(value, dateOptions ?? DateParseOptions.Default, out var iso) => iso,
            "amount" when TypedFieldOperandRules.TryNormalizeMoney(value, scale, out var scaled) =>
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
        "amount" => ["equals", "notEquals", "greaterThan", "greaterThanOrEqual", "lessThan", "lessThanOrEqual", "between", "notBetween", "in", "notIn",
            "containsDigits", "notContainsDigits", "endsWithDigits", "notEndsWithDigits", "endsWithZeros", "notEndsWithZeros", "isBlank", "isNotBlank"],
        _ => []
    };

    public static bool IsNegative(string? op) => op is "notEquals" or "notContains" or "notStartsWith" or "notEndsWith" or "notIn" or "notBetween" or DayOfMonthNotIn
        or "isNotWeekend" or "isNotHoliday" or "isNotMakeupDay" or "isNotNonBusinessDay" or "notContainsDigits" or "notEndsWithDigits" or "notEndsWithZeros"
        or "notMonthStartDays" or "notMonthEndDays";

    public static bool IsMonthWindow(string? op) => op is "monthStartDays" or "notMonthStartDays" or "monthEndDays" or "notMonthEndDays";

    public static bool TryParseMonthWindowDays(string? raw, out int days) =>
        int.TryParse(raw?.Trim(), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out days) && days is >= 1 and <= 31;

    public static bool IsDayOfMonth(string? op) => op is DayOfMonthIn or DayOfMonthNotIn;

    public static bool IsCalendar(string? op) => op is "isWeekend" or "isNotWeekend" or "isHoliday" or "isNotHoliday"
        or "isMakeupDay" or "isNotMakeupDay" or "isNonBusinessDay" or "isNotNonBusinessDay";
    public static bool IsTail(string? op) => op is "endsWithDigits" or "notEndsWithDigits";

    /// <summary>
    /// 整數部分包含任一串數字（舊版 JET 的「特定數字篩選」，裁定 2026-11-05-46）：和整數尾數一樣只看絕對值的整數部分，
    /// 忽略正負號及小數；值是 1 到 <see cref="TrailingZeroThreshold.MaxCustomDigits"/> 位的數字，可以多串，以 <see cref="TailPatterns"/> 讀取。
    /// </summary>
    public static bool IsDigitContains(string? op) => op is "containsDigits" or "notContainsDigits";

    /// <summary>
    /// 只看國定假日清單的兩個比較方式。沒有上傳國定假日清單時第五步停用它們並寫原因（裁定 2026-11-05-55）；
    /// 「非營業日」也看週末，沒有清單時仍可使用。
    /// </summary>
    public static bool IsHoliday(string? op) => op is "isHoliday" or "isNotHoliday";

    /// <summary>只看補班日清單的兩個比較方式；沒有上傳補班日清單時第五步比照國定假日停用並寫原因（裁定 2026-11-05-66）。</summary>
    public static bool IsMakeupDay(string? op) => op is "isMakeupDay" or "isNotMakeupDay";

    /// <summary>
    /// 尾數連續多個 0（裁定 2026-11-03-07）：整數部分的末 N 位都是 0，整數部分為 0 的金額不符合；忽略正負號及小數。
    /// 值是 0 的個數，和尾數樣態同樣是 1 到 <see cref="TrailingZeroThreshold.MaxCustomDigits"/>。
    /// </summary>
    public static bool IsZeroRun(string? op) => op is "endsWithZeros" or "notEndsWithZeros";

    public static bool TryParseZeroRun(string? raw, out int zeros) =>
        int.TryParse(raw?.Trim(), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out zeros)
        && zeros is >= TrailingZeroThreshold.MinCustomDigits and <= TrailingZeroThreshold.MaxCustomDigits;
    /// <summary>String-carried lists share the editor delimiters. Structured values preserve literal punctuation.</summary>
    public static IReadOnlyList<string> SplitInputList(string? value) => (value ?? "")
        .Split([',', '，', '、', '\t', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public static IReadOnlyList<string> TailPatterns(FilterConditionSpec rule) => rule.TypedValues is { Count: > 0 }
        ? rule.TypedValues : SplitInputList(rule.TypedValue);

    /// <summary>文字的包含比對接受多個關鍵字；舊 wire 只帶單一 value 時視為一個關鍵字。</summary>
    public static bool IsContains(string? op) => op is "contains" or "notContains";

    public static IReadOnlyList<string> ContainsKeywords(FilterConditionSpec rule) =>
        rule.TypedValues is { Count: > 0 } ? rule.TypedValues
            : rule.TypedValue is null ? [] : [rule.TypedValue];

    /// <summary>
    /// 文字的開頭符合、結尾符合和包含一樣接受多個值（裁定 2026-11-09-09）：符合任一個就算，排除時每一個都不能符合。
    /// 以前只收一個值，已儲存情境只帶單一 value 時視為一個值。
    /// </summary>
    public static bool IsAffix(string? op) => op is "startsWith" or "notStartsWith" or "endsWith" or "notEndsWith";

    public static IReadOnlyList<string> AffixValues(FilterConditionSpec rule) => ContainsKeywords(rule);

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

    public static GlRdeFieldMetadata? Resolve(FilterConditionSpec rule, IReadOnlyList<GlRdeFieldMetadata> extraFields)
    {
        if (rule.FieldId is not null)
            return rule.Field is null ? extraFields.FirstOrDefault(field => field.FieldId == rule.FieldId) : null;
        if (!GlFieldWhitelist.TryResolve(rule.Field, out var column)) return null;
        return new GlRdeFieldMetadata(rule.Field!, column.Column, rule.Field!, column.Kind switch
        {
            GlFieldKind.Text => "text", GlFieldKind.Date => "date", _ => "amount"
        });
    }
}

public static partial class FilterScenarioValidator
{
    /// <param name="fieldUnavailable">核心欄位沒有來源欄；原因已由規則需求表寫出，比較方式與值不必再檢查。</param>
    private static void ValidateFieldValue(FilterConditionSpec rule, string label,
        FilterValidationContext context, List<string> errors, bool fieldUnavailable)
    {
        // 分錄方向與核心金額的「含正負號」是已拿掉的舊寫法，讀到時當成無法讀取的內容
        // （裁定 2026-10-30-25：已儲存情境的舊寫法不再讀回）。攸關資料元素的金額欄位仍可選含正負號。
        if (rule.DrCr is not null)
            FilterScenarioCorruption.Add(errors, label, "field_value_side_invalid");
        if (rule.FieldId is null && rule.Field == JetFieldCatalog.GlAmount && rule.AmountBasis == TypedAmountBasisNames.Signed)
            FilterScenarioCorruption.Add(errors, label, "field_value_core_amount_signed");
        var field = FieldValueConditions.Resolve(rule, context.RdeFields);
        if (field is null)
        {
            errors.Add($"{label}：{MissingFieldReason(rule.Field)}");
            return;
        }
        if (fieldUnavailable)
        {
            return;
        }
        if (rule.TypedOperator is null || !FieldValueConditions.Operators(field.ValueType).Contains(rule.TypedOperator))
        {
            errors.Add($"{label}：此欄位不適用此比較方式");
            return;
        }
        if (field.ValueType == "amount" && !TypedAmountBasisNames.IsCanonical(rule.AmountBasis))
            FilterScenarioCorruption.Add(errors, label, "field_value_amount_basis_missing");
        if (field.ValueType != "amount" && rule.AmountBasis is not null)
            FilterScenarioCorruption.Add(errors, label, "field_value_amount_basis_not_amount");
        if (FieldValueConditions.IsMonthWindow(rule.TypedOperator))
        {
            if (!FieldValueConditions.TryParseMonthWindowDays(rule.TypedValue, out _))
                errors.Add($"{label}：每月月初或月底天數須為 1 到 31 的整數");
            if (rule.TypedValues is not null || rule.TypedFrom is not null || rule.TypedTo is not null)
                FilterScenarioCorruption.Add(errors, label, "field_value_month_window_extra");
            return;
        }
        if (FieldValueConditions.IsZeroRun(rule.TypedOperator))
        {
            if (!FieldValueConditions.TryParseZeroRun(rule.TypedValue, out _))
                errors.Add($"{label}：連續 0 的個數須為 {TrailingZeroThreshold.MinCustomDigits} 到 {TrailingZeroThreshold.MaxCustomDigits} 的整數");
            if (rule.TypedValues is not null || rule.TypedFrom is not null || rule.TypedTo is not null)
                FilterScenarioCorruption.Add(errors, label, "field_value_zero_run_extra");
            return;
        }
        if (FieldValueConditions.IsCalendar(rule.TypedOperator)) return;
        if (FieldValueConditions.IsTail(rule.TypedOperator))
        {
            var patterns = FieldValueConditions.TailPatterns(rule);
            if (patterns.Count > FilterScenarioLimits.MaxTypedInValuesPerRule)
                errors.Add($"{label}：{FieldValueListLimitReason(rule.TypedOperator)}");
            ValidateTrailingDigits(rule with { Keywords = patterns }, label, errors);
            return;
        }
        if (FieldValueConditions.IsDigitContains(rule.TypedOperator))
        {
            var patterns = FieldValueConditions.TailPatterns(rule);
            if (patterns.Count > FilterScenarioLimits.MaxTypedInValuesPerRule)
                errors.Add($"{label}：{FieldValueListLimitReason(rule.TypedOperator)}");
            ValidateTrailingDigits(rule with { Keywords = patterns }, label, errors, "數字");
            return;
        }
        if (FieldValueConditions.IsDayOfMonth(rule.TypedOperator))
        {
            if (!FieldValueConditions.TryParseDaysOfMonth(rule.TypedValues, out _))
                errors.Add($"{label}：每月日期須為 1 到 31 的整數，至少填寫一個");
            return;
        }
        if (field.ValueType == "text" && (FieldValueConditions.IsContains(rule.TypedOperator) || FieldValueConditions.IsAffix(rule.TypedOperator)))
        {
            var keywords = FieldValueConditions.IsAffix(rule.TypedOperator)
                ? FieldValueConditions.AffixValues(rule) : FieldValueConditions.ContainsKeywords(rule);
            if (keywords.Count > FilterScenarioLimits.MaxTypedInValuesPerRule)
                errors.Add($"{label}：{FieldValueListLimitReason(rule.TypedOperator)}");
            else if (keywords.Count == 0 || keywords.Any(static keyword => string.IsNullOrWhiteSpace(keyword)))
                errors.Add($"{label}：{ValueCountReason(FilterScenarioLimits.MaxTypedInValuesPerRule)}");
            return;
        }
        var normalized = rule with { TypedValues = FieldValueConditions.CanonicalValues(rule, field, context.MoneyScale, context.DateParseOptions) };
        if (TypedFieldOperatorSets.IsSetOperator(rule.TypedOperator)
            && normalized.TypedValues?.Count > FilterScenarioLimits.MaxTypedInValuesPerRule)
        {
            errors.Add($"{label}：{FieldValueListLimitReason(rule.TypedOperator)}");
            return;
        }
        ValidateTypedOperands(normalized.TypedOperator == "notBetween" ? normalized with { TypedOperator = "between" } : normalized,
            field, label, context, errors);
    }

    // 子條件組的連接在畫面上只寫 AND 與 OR（裁定 2026-10-30-32），提示照畫面上的字寫；前端 filter-values.js 同一句。
    private static string FieldValueListLimitReason(string? op) =>
        $"每條最多 {FilterScenarioLimits.MaxTypedInValuesPerRule} 個值。請拆成多條條件，放進子條件組，用 "
        + (op is "notIn" or "notContains" or "notStartsWith" or "notEndsWith" or "notContainsDigits" or "notEndsWithDigits" ? "AND" : "OR") + " 連接。";
}
