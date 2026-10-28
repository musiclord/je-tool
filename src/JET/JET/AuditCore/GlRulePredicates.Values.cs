using JET.Domain;

namespace JET.AuditCore;

internal sealed partial class GlRulePredicates
{
    /// <summary>
    /// 通用值條件（fieldValue）。欄位空白（文字 NULL 或 TRIM 為空、日期與金額 NULL）預設不列入，除非
    /// `includeBlank` 為真（2026-09-04 裁定）；其餘比較都在 UPPER(TRIM()) 文字、ISO 日期字串或 scaled 金額上進行，
    /// 值全部參數綁定。每月幾日用 <see cref="ISqlDialect.DayOfMonth"/>；文字的包含比對接受多個關鍵字（OR 串接），
/// 空白、週末、假日、補班日與包含都呼叫預篩選規則同一組方法。
    /// </summary>
    public string FieldValue(FilterSqlParameterPlanBuilder parameters, FilterRuleSpec rule,
        FilterRuleContext context, string schemaPrefix)
    {
        var field = FieldValueConditions.Resolve(rule, context.RdeFields)
            ?? throw Invalid("field_value_field_missing");
        rule = rule with { TypedValues = FieldValueConditions.CanonicalValues(rule, field, context.MoneyScale, context.DateParseOptions) };
        var op = rule.TypedOperator;
        if (op is null || !FieldValueConditions.Operators(field.ValueType).Contains(op))
            throw Invalid("field_value_operator_unsupported");
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
        var blank = field.ValueType == "text" ? BlankText(column) : $"{column} IS NULL";
        if (op == "isBlank") return Complete($"({blank})", missingMatches: true);
        if (op == "isNotBlank") return Complete($"NOT ({blank})", missingMatches: false);

        var originalColumn = column;
        if (field.ValueType == "text") column = dialect.Upper(dialect.Trim(column));
        if (field.ValueType == "money") column = rule.AmountBasis switch
        {
            "signed" => column, "absolute" => $"ABS({column})",
            _ => throw Invalid("field_value_amount_basis_invalid")
        };
        object Normalize(string? raw) => field.ValueType switch
        {
            "text" when TypedFieldOperandRules.TryNormalizeText(raw, out var text) => TypedFieldOperandRules.TextComparisonKey(text),
            "date" when TypedFieldOperandRules.TryNormalizeDate(raw, context.DateParseOptions, out var date) => date,
            "money" when TypedFieldOperandRules.TryNormalizeMoney(raw, context.MoneyScale, out var money) => money,
            _ => throw Invalid("field_value_value_invalid")
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
            // 和第四步的週末、假日規則同一組方法；補班日是同一張假日檔的另一種日期。
            var weekend = Weekend(column, context.NonWorkingDays);
            var holiday = Holiday(column, schemaPrefix);
            var makeup = MakeupDay(column, schemaPrefix);
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
                throw Invalid("field_value_month_window_days_invalid");
            var distance = op is "monthStartDays" or "notMonthStartDays"
                ? dialect.DayOfMonth(column)
                : $"({dialect.DaysInMonth(column)} - {dialect.DayOfMonth(column)} + 1)";
            positive = $"{distance} <= {NextParam(parameters, (long)days)}";
            if (FieldValueConditions.IsNegative(op)) positive = $"NOT ({positive})";
        }
        else if (FieldValueConditions.IsDayOfMonth(op))
        {
            if (!FieldValueConditions.TryParseDaysOfMonth(rule.TypedValues, out var days))
                throw Invalid("field_value_day_of_month_invalid");
            var dayParameters = days.Select(day => NextParam(parameters, (long)day));
            positive = $"{dialect.DayOfMonth(column)} {(op == FieldValueConditions.DayOfMonthIn ? "IN" : "NOT IN")} ({string.Join(", ", dayParameters)})";
        }
        else if (op is "in" or "notIn")
        {
            if (rule.TypedValues is not { Count: > 0 and <= FilterScenarioLimits.MaxTypedInValuesPerRule })
                throw Invalid("field_value_set_count_invalid");
            var values = rule.TypedValues.Select(value => Normalize(value)).Distinct().Select(value => NextParam(parameters, value));
            positive = $"{column} {(op == "in" ? "IN" : "NOT IN")} ({string.Join(", ", values)})";
        }
        else if (op is "between" or "notBetween")
        {
            positive = $"({column} >= {Param(rule.TypedFrom)} AND {column} <= {Param(rule.TypedTo)})";
            if (op == "notBetween") positive = $"NOT ({positive})";
        }
        else if (op is "contains" or "notContains")
        {
            var keywords = FieldValueConditions.ContainsKeywords(rule);
            if (keywords.Count == 0) throw Invalid("field_value_keywords_empty");
            // 和第四步摘要特定描述同一個比對：逐字、不分大小寫、關鍵字去頭尾空白。
            positive = ContainsAnyKeyword(parameters, originalColumn, keywords);
            if (op == "notContains") positive = $"NOT ({positive})";
        }
        else if (op is "startsWith" or "notStartsWith" or "endsWith" or "notEndsWith")
        {
            var value = (string)Normalize(rule.TypedValue);
            // Escape literal wildcard characters for all supported providers, including SQL Server's brackets.
            var escaped = value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)
                .Replace("[", "\\[", StringComparison.Ordinal);
            var pattern = op is "startsWith" or "notStartsWith" ? escaped + "%" : "%" + escaped;
            positive = $"{column} LIKE {NextParam(parameters, pattern)} ESCAPE '\\'";
            if (FieldValueConditions.IsNegative(op)) positive = $"NOT ({positive})";
        }
        else
        {
            var comparison = op switch
            {
                "equals" or "on" => "=", "notEquals" => "<>",
                "before" or "lessThan" => "<", "onOrBefore" or "lessThanOrEqual" => "<=",
                "after" or "greaterThan" => ">", "onOrAfter" or "greaterThanOrEqual" => ">=",
                _ => throw Invalid("field_value_operator_invalid")
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
            null or "any" => "1 = 1", _ => throw Invalid("drcr_invalid")
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
            _ => throw Invalid("category_mode_invalid")
        };
    }

}
