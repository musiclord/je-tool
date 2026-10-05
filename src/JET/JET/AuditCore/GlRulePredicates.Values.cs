using JET.Domain;

namespace JET.AuditCore;

internal sealed partial class GlRulePredicates
{
    /// <summary>
    /// 通用值條件（fieldValue）。欄位空白（文字 NULL 或 TRIM 為空、日期與金額 NULL）預設不列入，除非
    /// `includeBlank` 為真（2026-09-04 裁定）；其餘比較都在 UPPER(TRIM()) 文字、ISO 日期字串或 scaled 金額上進行，
    /// 值全部參數綁定。每月幾日用 <see cref="ISqlDialect.DayOfMonth"/>；文字的包含比對接受多個關鍵字（OR 串接）。
    /// </summary>
    public string FieldValue(FilterSqlParameterPlanBuilder parameters, FilterRuleSpec rule,
        FilterRuleContext context, string schemaPrefix)
    {
        var field = FieldValueConditions.Resolve(rule, context.RdeFields)
            ?? throw Invalid("篩選欄位不存在，請重新確認欄位配對。");
        rule = rule with { TypedValues = FieldValueConditions.CanonicalValues(rule, field, context.MoneyScale, context.DateParseOptions) };
        var op = rule.TypedOperator;
        if (op is null || !FieldValueConditions.Operators(field.ValueType).Contains(op))
            throw Invalid("比較方式不適用此欄位，請重新選擇。");
        string column;
        string? rdeFieldParameter = null;
        if (rule.FieldId is null)
            column = $"g.{field.SourceColumn}";
        else
        {
            var valueColumn = field.ValueType switch { "text" => "text_value", "date" => "date_value", _ => "amount_scaled" };
            rdeFieldParameter = NextParam(parameters, field.FieldId);
            column = $"v.{valueColumn}";
        }
        // RDE 的整個判定放在同一次相關查詢內；關鍵字再多，也不逐詞重查同一個值。
        string Complete(string predicate, bool missingMatches) => rdeFieldParameter is null ? predicate
            : $"(COALESCE((SELECT CASE WHEN ({predicate}) THEN 1 ELSE 0 END "
              + $"FROM {schemaPrefix}target_gl_rde_value v WHERE v.entry_id = g.entry_id "
              + $"AND v.field_id = {rdeFieldParameter}), {(missingMatches ? 1 : 0)}) = 1)";
        var blank = field.ValueType == "text" ? $"({column} IS NULL OR {dialect.Trim(column)} = '')" : $"{column} IS NULL";
        if (op == "isBlank") return Complete($"({blank})", missingMatches: true);
        if (op == "isNotBlank") return Complete($"NOT ({blank})", missingMatches: false);

        var originalColumn = column;
        if (field.ValueType == "text") column = $"UPPER({dialect.Trim(column)})";
        if (field.ValueType == "money") column = rule.AmountBasis switch
        {
            "signed" => column, "absolute" => $"ABS({column})",
            _ => throw Invalid("請選擇含正負號或絕對值金額。")
        };
        object Normalize(string? raw) => field.ValueType switch
        {
            "text" when TypedFieldOperandRules.TryNormalizeText(raw, out var text) => TypedFieldOperandRules.TextComparisonKey(text),
            "date" when TypedFieldOperandRules.TryNormalizeDate(raw, context.DateParseOptions, out var date) => date,
            "money" when TypedFieldOperandRules.TryNormalizeMoney(raw, context.MoneyScale, out var money) => money,
            _ => throw Invalid("條件值無效，請檢查文字、日期格式或金額。")
        };
        string Param(string? raw) => NextParam(parameters, Normalize(raw));
        string positive;
        if (FieldValueConditions.IsTail(op))
        {
            positive = TrailingDigits(parameters, FieldValueConditions.TailPatterns(rule), context.MoneyScale, originalColumn);
            if (FieldValueConditions.IsNegative(op)) positive = $"NOT ({positive})";
        }
        else if (FieldValueConditions.IsCalendar(op))
        {
            var weekend = dialect.WeekendPredicate(column, NonWorkingDays.Resolve(context.NonWorkingDays));
            var holiday = $"EXISTS (SELECT 1 FROM {schemaPrefix}staging_calendar_raw_day d WHERE d.day_type = 'holiday' AND d.date = {column})";
            var makeup = $"EXISTS (SELECT 1 FROM {schemaPrefix}staging_calendar_raw_day d WHERE d.day_type = 'makeup' AND d.date = {column})";
            positive = op switch
            {
                "isWeekend" or "isNotWeekend" => weekend,
                "isHoliday" or "isNotHoliday" => holiday,
                "isMakeupDay" or "isNotMakeupDay" => makeup,
                _ => $"(({weekend} OR {holiday}) AND NOT ({makeup}))"
            };
            if (op.StartsWith("isNot", StringComparison.Ordinal)) positive = $"NOT ({positive})";
        }
        else if (FieldValueConditions.IsMonthWindow(op))
        {
            if (!FieldValueConditions.TryParseMonthWindowDays(rule.TypedValue, out var days))
                throw Invalid("每月月初或月底天數只能是 1 到 31 的整數。");
            var distance = op is "monthStartDays" or "notMonthStartDays"
                ? dialect.DayOfMonth(column)
                : $"({dialect.DaysInMonth(column)} - {dialect.DayOfMonth(column)} + 1)";
            positive = $"{distance} <= {NextParam(parameters, (long)days)}";
            if (FieldValueConditions.IsNegative(op)) positive = $"NOT ({positive})";
        }
        else if (FieldValueConditions.IsDayOfMonth(op))
        {
            if (!FieldValueConditions.TryParseDaysOfMonth(rule.TypedValues, out var days))
                throw Invalid("每月幾日只能是 1 到 31 的整數。");
            var dayParameters = days.Select(day => NextParam(parameters, (long)day));
            positive = $"{dialect.DayOfMonth(column)} {(op == FieldValueConditions.DayOfMonthIn ? "IN" : "NOT IN")} ({string.Join(", ", dayParameters)})";
        }
        else if (op is "in" or "notIn")
        {
            if (rule.TypedValues is not { Count: > 0 and <= FilterScenarioLimits.MaxTypedInValuesPerRule })
                throw Invalid("清單需要 1 到 100 個值，請檢查輸入。");
            var values = rule.TypedValues.Select(value => Normalize(value)).Distinct().Select(value => NextParam(parameters, value));
            positive = $"{column} {(op == "in" ? "IN" : "NOT IN")} ({string.Join(", ", values)})";
        }
        else if (op is "between" or "notBetween")
        {
            positive = $"({column} >= {Param(rule.TypedFrom)} AND {column} <= {Param(rule.TypedTo)})";
            if (op == "notBetween") positive = $"NOT ({positive})";
        }
        else if (op is "contains" or "notContains" or "startsWith" or "notStartsWith" or "endsWith" or "notEndsWith")
        {
            var keywords = FieldValueConditions.IsContains(op) ? FieldValueConditions.ContainsKeywords(rule) : [rule.TypedValue ?? string.Empty];
            if (keywords.Count == 0) throw Invalid("包含比對至少需要一個文字。");
            var likes = keywords.Select(keyword =>
            {
                var value = (string)Normalize(keyword);
                // Escape literal wildcard characters for all supported providers, including SQL Server's brackets.
                var escaped = value.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)
                    .Replace("[", "\\[", StringComparison.Ordinal);
                var pattern = (op is "startsWith" or "notStartsWith" ? "" : "%") + escaped
                    + (op is "endsWith" or "notEndsWith" ? "" : "%");
                return $"{column} LIKE {NextParam(parameters, pattern)} ESCAPE '\\'";
            }).ToArray();
            positive = likes.Length == 1 ? likes[0] : "(" + string.Join(" OR ", likes) + ")";
            if (FieldValueConditions.IsNegative(op)) positive = $"NOT ({positive})";
        }
        else
        {
            var comparison = op switch
            {
                "equals" or "on" => "=", "notEquals" => "<>",
                "before" or "lessThan" => "<", "onOrBefore" or "lessThanOrEqual" => "<=",
                "after" or "greaterThan" => ">", "onOrAfter" or "greaterThanOrEqual" => ">=",
                _ => throw Invalid("比較方式無效，請重新選擇。")
            };
            positive = $"{column} {comparison} {Param(rule.TypedValue)}";
        }
        // 2026-09-04 裁定：日期（與其他欄位）空白的分錄不列入，除非審計員勾選「空白也符合」。
        var includeBlank = rule.IncludeBlank ?? false;
        return Complete($"(CASE WHEN ({blank}) THEN {(includeBlank ? 1 : 0)} WHEN ({positive}) THEN 1 ELSE 0 END = 1)", includeBlank);
    }

    /// <summary>
    /// 借貸科目分類（accountSide）：兩值語意。分類留白的科目依 legacy 與 `AccountMappingContracts` 的
    /// 既有投影落到 Others（`Selected` 只看 taxonomy role，不看 `classification_explicit`）；不在配對檔的
    /// 科目不屬於任何分類。`is`／`isNot` 判本列（借方 `amount_scaled >= 0`、貸方 `< 0`），`absent` 是傳票層
    /// 條件：整張傳票的該側都不屬於指定分類。ANSI 共通（EXISTS／NOT EXISTS），值全部參數綁定。
    /// </summary>
    public string AccountSide(FilterSqlParameterPlanBuilder parameters, FilterRuleSpec rule,
        FilterRuleContext context, string schemaPrefix)
    {
        string Side(string alias) => rule.DrCr switch
        {
            "debit" => $"{alias}.amount_scaled >= 0", "credit" => $"{alias}.amount_scaled < 0",
            "any" => "1 = 1", _ => throw Invalid("請選擇借方、貸方或不限借貸。")
        };
        string Selected(string alias) => $"EXISTS (SELECT 1 FROM {schemaPrefix}target_account_mapping m "
            + $"{TaxonomyJoin(schemaPrefix, "m", "t")} WHERE m.account_code = {alias}.account_code "
            + $"AND {CategorySelectionRoles(parameters, "t", rule.CategoryIds, "指定", schemaPrefix, rule.CategorySelection)})";
        return rule.CategoryMode switch
        {
            "is" => $"({Side("g")} AND {Selected("g")})",
            "isNot" => $"({Side("g")} AND NOT {Selected("g")})",
            "absent" => $"(g.document_number IS NOT NULL AND NOT EXISTS (SELECT 1 FROM {schemaPrefix}target_gl_entry s "
                + $"WHERE s.document_number = g.document_number AND {populationScopePredicate(context, "s")} "
                + $"AND {Side("s")} AND {Selected("s")}))",
            _ => throw Invalid("請選擇科目分類的判斷方式。")
        };
    }

}
