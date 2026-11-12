using JET.Domain;

namespace JET.AuditCore;

internal sealed partial class GlRulePredicates
{
    /// <summary>
    /// 通用值條件（fieldValue）。欄位空白（文字 NULL 或 TRIM 為空、日期與金額 NULL）預設不列入，除非
    /// `includeBlank` 為真；其餘比較都在 UPPER(TRIM()) 文字、ISO 日期字串或 scaled 金額上進行，
    /// 值全部參數綁定。每月幾日用 <see cref="ISqlDialect.DayOfMonth"/>；文字的包含比對接受多個關鍵字（OR 串接），
    /// 空白、週末、假日、補班日、包含與尾數連續多個 0 都呼叫預篩選規則同一組方法。
    /// </summary>
    public string FieldValue(FilterSqlParameterPlanBuilder parameters, FilterConditionSpec rule,
        FilterConditionContext context, string schemaPrefix)
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
              + $"FROM {schemaPrefix}gl_rde_value v WHERE v.entry_id = g.entry_id "
              + $"AND v.field_id = {rdeFieldParameter}), {(missingMatches ? 1 : 0)}) = 1)";
        var blank = field.ValueType == "text" ? BlankText(column) : $"{column} IS NULL";
        if (op == "isBlank") return Complete($"({blank})", missingMatches: true);
        if (op == "isNotBlank") return Complete($"NOT ({blank})", missingMatches: false);

        var originalColumn = column;
        if (field.ValueType == "text") column = dialect.Upper(dialect.Trim(column));
        if (field.ValueType == "amount") column = rule.AmountBasis switch
        {
            "signed" => column, "absolute" => $"ABS({column})",
            _ => throw Invalid("field_value_amount_basis_invalid")
        };
        object Normalize(string? raw) => field.ValueType switch
        {
            "text" when TypedFieldOperandRules.TryNormalizeText(raw, out var text) => TypedFieldOperandRules.TextComparisonKey(text),
            "date" when TypedFieldOperandRules.TryNormalizeDate(raw, context.DateParseOptions, out var date) => date,
            "amount" when TypedFieldOperandRules.TryNormalizeMoney(raw, context.MoneyScale, out var money) => money,
            _ => throw Invalid("field_value_value_invalid")
        };
        string Param(string? raw) => NextParam(parameters, Normalize(raw));
        string positive;
        if (FieldValueConditions.IsTail(op))
        {
            positive = TrailingDigits(parameters, FieldValueConditions.TailPatterns(rule), context.MoneyScale, originalColumn);
            if (FieldValueConditions.IsNegative(op)) positive = $"NOT ({positive})";
        }
        else if (FieldValueConditions.IsDigitContains(op))
        {
            // 整數部分包含任一串數字：和整數尾數一樣只看絕對值的整數部分，不接金額比較基準。
            positive = IntegerPartContains(parameters, FieldValueConditions.TailPatterns(rule), context.MoneyScale, originalColumn);
            if (FieldValueConditions.IsNegative(op)) positive = $"NOT ({positive})";
        }
        else if (FieldValueConditions.IsZeroRun(op))
        {
            // 尾數連續 N 個 0和第四步預篩選規則「分錄金額中有連續 0 的尾數」用同一個 ZeroRun，
            // 0 的個數由審計員指定，加入時預設是由 GL 算出的個數。結果和子條件組
            // 「整數尾數符合 N 個 0 且金額絕對值大於或等於 1」相同。
            if (!FieldValueConditions.TryParseZeroRun(rule.TypedValue, out var zeros))
                throw Invalid("field_value_zero_run_invalid");
            positive = ZeroRun(parameters, zeros, context.MoneyScale, originalColumn);
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
        else if (FieldValueConditions.IsAffix(op))
        {
            // 開頭符合與結尾符合接受多個值，OR 串接；排除時每一個都不能符合。
            // 只有一個值時 SQL 不多包括號。
            var values = FieldValueConditions.AffixValues(rule);
            if (values.Count == 0) throw Invalid("field_value_value_invalid");
            var clauses = values.Select(raw => (string)Normalize(raw)).Distinct(StringComparer.Ordinal).Select(value =>
            {
                // 跳脫字元、%、_ 與 [ 都先加上跳脫，值裡的這些字元照字面比對。
                var escaped = value.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)
                    .Replace("[", "\\[", StringComparison.Ordinal);
                var pattern = op is "startsWith" or "notStartsWith" ? escaped + "%" : "%" + escaped;
                return $"{column} LIKE {NextParam(parameters, pattern)} ESCAPE '\\'";
            }).ToList();
            positive = clauses.Count == 1 ? clauses[0] : $"({string.Join(" OR ", clauses)})";
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
        // 日期（與其他欄位）空白的分錄不列入，除非審計員勾選「空白也符合」。
        // 文字欄位的否定比較在畫面上預設勾選，畫面一律送出勾選結果；沒有帶這個值時照舊當成不列入。
        var includeBlank = rule.IncludeBlank ?? false;
        return Complete($"(CASE WHEN ({blank}) THEN {(includeBlank ? 1 : 0)} WHEN ({positive}) THEN 1 ELSE 0 END = 1)", includeBlank);
    }

    /// <summary>
    /// 借貸科目分類（accountSide）：兩值語意。分類留白的科目依 legacy 與 `AccountMappingContracts` 的
    /// 既有投影落到 Others（`Selected` 只看科目分類的 role，不看 `classification_explicit`）；不在配對檔的
    /// 科目不屬於任何分類。`is`／`isNot` 只判本列；要限定借方或貸方時另加借貸限定條件。
    /// 值全部參數綁定。
    /// </summary>
    public string AccountSide(FilterSqlParameterPlanBuilder parameters, FilterConditionSpec rule,
        FilterConditionContext context, string schemaPrefix)
    {
        // 借貸方向是已拿掉的舊寫法，驗證時就當成無法讀取的內容，走到這裡只會是程式送錯資料。
        if (rule.DrCr is not null) throw Invalid("drcr_invalid");
        string Selected(string alias) => $"EXISTS (SELECT 1 FROM {schemaPrefix}account_mapping m "
            + $"{CategoryJoin(schemaPrefix, "m", "t")} WHERE m.account_number = {alias}.account_number "
            + $"AND {CategorySelectionRoles(parameters, "t", rule.CategoryIds, "指定", schemaPrefix, rule.CategorySelection)})";
        return rule.CategoryMode switch
        {
            "is" => $"({Selected("g")})",
            "isNot" => $"(NOT {Selected("g")})",
            _ => throw Invalid("category_mode_invalid")
        };
    }

}
