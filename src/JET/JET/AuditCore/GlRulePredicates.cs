using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// GL 規則述詞的單一事實來源（方言相異片段經 <see cref="ISqlDialect"/> 取得，
/// 其餘為 ANSI 共通；guide §13）。每個方法回傳針對別名 g 的 WHERE 片段，
/// 並把值依序累積到純參數計畫；DbCommand 建立與綁定只由 Infrastructure 負責。
/// prescreen.run 的計數與 filter 條件組合共用同一份片段。
/// 識別字一律出自 GlFieldWhitelist 或本檔常數；使用者值只進參數。
/// </summary>
internal sealed partial class GlRulePredicates(
    ISqlDialect dialect,
    Func<FilterRuleContext, string, string> populationScopePredicate)
{
    /// <summary>期末後核准（post_period_approval）：核准日 ≥ 期末財報準備日。</summary>
    public string PostPeriodApproval(FilterSqlParameterPlanBuilder command, string lastPeriodStart)
    {
        var p = NextParam(command, lastPeriodStart);
        return $"g.approval_date >= {p}";
    }

    /// <summary>摘要特定描述（suspicious_keywords）：摘要含任一預設關鍵字。</summary>
    public string SuspiciousKeywords(FilterSqlParameterPlanBuilder command)
    {
        return TextContainsAny(command, "g.document_description", SuspiciousKeywordDefaults.Defaults);
    }

    /// <summary>連續零尾數（trailing_zeros）：主單位整數為 10^N 倍數；小數位不參與，0 不命中。</summary>
    public string TrailingZeros(FilterSqlParameterPlanBuilder command, long unitModulus, int moneyScale)
    {
        var scale = NextParam(command, (long)moneyScale);
        var modulus = NextParam(command, unitModulus);
        var intAmount = dialect.IntegerQuotient("ABS(g.amount_scaled)", scale);
        return $"({intAmount} <> 0 AND {intAmount} % {modulus} = 0)";
    }

    /// <summary>
    /// 未預期借貸組合（unexpected_account_pair，guide §5：否定面）：本列為 Revenue 貸方
    /// （amount_scaled &lt; 0），但其所在傳票**無任何**「借方側（amount_scaled >= 0）且分類 ∈
    /// {Receivables, Cash, Receipt in advance}」的分錄——收入貸記卻缺正常對方科目才命中，
    /// 正常銷售（貸收入、借應收/現金/預收）不命中。tag 只落在 Revenue 貸方列（與 KCT 條件 C
    /// 標記慣例一致；2026-07-03 裁決＋2026-07-08 spec §1 定案）。對方集合含 Cash，故較 KCT C
    /// （不含 Cash）嚴，命中集 ⊆ C（現銷傳票 C 命中、本規則不命中）。
    /// 零元邊界（本規則專屬）：正常對方科目的借方 counterpart 採 `amount_scaled > 0`，
    /// 0 元分錄不算「已收到對價」，因此不足以消解「收入貸記缺正常對方科目」的疑慮。
    /// 其他借貸組合規則（§6.1 accountPair、specialAccountCategoryPair、KCT C）維持
    /// `amount_scaled >= 0` 屬借方側的統一判定（2026-06-11 裁決），本例外只在此處。
    /// 需科目配對已匯入。ANSI 共通（EXISTS + NOT EXISTS）。
    /// </summary>
    public string UnexpectedAccountPair(
        FilterSqlParameterPlanBuilder command,
        FilterRuleContext context,
        string schemaPrefix = "")
    {
        var revenue = NextParam(command, AccountTaxonomyBuiltIns.RevenueRole);
        var counterparts = CategoryListParams(command);

        return $"""
            (EXISTS (SELECT 1 FROM {schemaPrefix}target_account_mapping m
                     {TaxonomyJoin(schemaPrefix, "m", "tm")}
                     WHERE m.account_code = g.account_code AND tm.semantic_role = {revenue})
             AND g.amount_scaled < 0
             AND NOT EXISTS (
                 SELECT 1 FROM {schemaPrefix}target_gl_entry d
                 JOIN {schemaPrefix}target_account_mapping md ON md.account_code = d.account_code
                 {TaxonomyJoin(schemaPrefix, "md", "td")}
                 WHERE d.document_number = g.document_number
                   AND {populationScopePredicate(context, "d")}
                   AND d.amount_scaled > 0
                   AND td.semantic_role IN ({counterparts})))
            """;
    }

    private string CategoryListParams(FilterSqlParameterPlanBuilder command)
    {
        return string.Join(", ",
            new[]
            {
                AccountTaxonomyBuiltIns.ReceivablesRole,
                AccountTaxonomyBuiltIns.CashRole,
                AccountTaxonomyBuiltIns.ReceiptInAdvanceRole
            }.Select(role => NextParam(command, role)));
    }

    /// <summary>
    /// 科目配對分析（account_pair，guide §6.1 三模式）。借貸側判定統一：
    /// `amount_scaled >= 0` 屬借方側、`&lt; 0` 屬貸方側。錨定模式輸出
    /// 錨定分錄與同傳票的對方側分錄。ANSI 共通。
    /// </summary>
    public string AccountPair(
        FilterSqlParameterPlanBuilder command,
        string pairMode,
        IReadOnlyList<string> debitCategoryIds,
        IReadOnlyList<string> creditCategoryIds,
        FilterRuleContext context,
        string schemaPrefix = "", string? categorySelection = null)
    {
        var side = CategorySides(command, debitCategoryIds, creditCategoryIds, context, schemaPrefix, categorySelection);

        return pairMode switch
        {
            AccountPairModes.Exact =>
                $"({side.DocHasDebitSide()} AND {side.DocHasCreditSide()} AND ({side.RowIsDebitSide()} OR {side.RowIsCreditSide()}))",
            AccountPairModes.DebitAnchor =>
                $"({side.DocHasDebitSide()} AND ({side.RowIsDebitSide()} OR g.amount_scaled < 0))",
            AccountPairModes.CreditAnchor =>
                $"({side.DocHasCreditSide()} AND ({side.RowIsCreditSide()} OR g.amount_scaled >= 0))",
            _ => throw new InvalidOperationException($"未處理的配對模式 {pairMode}。")
        };
    }

    /// <summary>
    /// 考量特殊科目類別配對（special_account_category_pair）：顯式雙類別 + 否定。
    /// A = 借方類別、B = 貸方類別，借貸側判定與 §6.1 一致（`amount_scaled >= 0` 借方、`&lt; 0` 貸方）。
    /// 否定模式以 NOT EXISTS（即 NOT DocHasCreditSide(B) / NOT DocHasDebitSide(A)）藏在述詞內，
    /// 不洩漏到呼叫端：
    ///   drAndCr → 傳票同時有 A 借與 B 貸；tag「A 借 或 B 貸」的列。
    ///   drNotCr → 傳票有 A 借、且無任何 B 貸；tag「A 借」的列。
    ///   notDrCr → 傳票有 B 貸、且無任何 A 借；tag「B 貸」的列。
    /// 取捨（顯式陳述）：drAndCr 的 SQL 與 AccountPair 的 exact 模式邏輯重疊，但這是不同的
    /// 使用者面向條件（不同的模式標籤與否定語意），重複是刻意的——共用的是四個側別 closure，
    /// 而非條件本身。ANSI 共通（EXISTS / NOT EXISTS，全參數綁定），SQLite 與 SQL Server 由構造等價。
    /// </summary>
    public string SpecialAccountCategoryPair(
        FilterSqlParameterPlanBuilder command,
        string pairMode,
        IReadOnlyList<string> debitCategoryIds,
        IReadOnlyList<string> creditCategoryIds,
        FilterRuleContext context,
        string schemaPrefix = "", string? categorySelection = null)
    {
        var side = CategorySides(command, debitCategoryIds, creditCategoryIds, context, schemaPrefix, categorySelection);

        return pairMode switch
        {
            SpecialAccountCategoryPairModes.DrAndCr =>
                $"({side.DocHasDebitSide()} AND {side.DocHasCreditSide()} AND ({side.RowIsDebitSide()} OR {side.RowIsCreditSide()}))",
            SpecialAccountCategoryPairModes.DrNotCr =>
                $"({side.DocHasDebitSide()} AND NOT {side.DocHasCreditSide()} AND {side.RowIsDebitSide()})",
            SpecialAccountCategoryPairModes.NotDrCr =>
                $"({side.DocHasCreditSide()} AND NOT {side.DocHasDebitSide()} AND {side.RowIsCreditSide()})",
            _ => throw new InvalidOperationException($"未處理的特殊科目類別配對模式 {pairMode}。")
        };
    }

    /// <summary>
    /// 借方類別集合 A / 貸方類別集合 B 的四個側別片段（AccountPair 與 SpecialAccountCategoryPair 共用）。
    /// RowIs* 判定「本列 g」是否落在該側的任一選定分類；DocHas* 判定「同傳票」是否存在該側的任一
    /// 選定分類（否定模式對 DocHas* 取 NOT EXISTS）。每個 closure 每次呼叫綁定該側全部分類身分，
    /// 呼叫順序即參數順序；分類集合已由 Domain 去重排序，故任何排列都產生逐字相同的 SQL。
    /// 兩側都是集合成員判定（`IN`），不做集合交叉：同一 GL 列最多由外層的 OR 判斷一次，
    /// 不會因為多選而輸出重複列，也不產生 category 的笛卡兒積。
    /// 抽出共用片段以消滅重複，但兩個述詞各自決定如何組合（含否定），故對外 SQL 行為互不影響。
    /// </summary>
    private CategorySidePredicates CategorySides(
        FilterSqlParameterPlanBuilder command,
        IReadOnlyList<string> debitCategoryIds,
        IReadOnlyList<string> creditCategoryIds,
        FilterRuleContext context,
        string schemaPrefix = "", string? categorySelection = null)
    {
        string RowIsDebitSide() =>
            $"""
            (EXISTS (SELECT 1 FROM {schemaPrefix}target_account_mapping m
                     {TaxonomyJoin(schemaPrefix, "m", "tm")}
                     WHERE m.account_code = g.account_code
                       AND {CategorySelectionRoles(command, "tm", debitCategoryIds, "借方", schemaPrefix, categorySelection)})
             AND g.amount_scaled >= 0)
            """;

        string RowIsCreditSide() =>
            $"""
            (EXISTS (SELECT 1 FROM {schemaPrefix}target_account_mapping m
                     {TaxonomyJoin(schemaPrefix, "m", "tm")}
                     WHERE m.account_code = g.account_code
                       AND {CategorySelectionRoles(command, "tm", creditCategoryIds, "貸方", schemaPrefix, categorySelection)})
             AND g.amount_scaled < 0)
            """;

        string DocHasDebitSide() =>
            $"""
            EXISTS (SELECT 1 FROM {schemaPrefix}target_gl_entry d
                    JOIN {schemaPrefix}target_account_mapping md ON md.account_code = d.account_code
                    {TaxonomyJoin(schemaPrefix, "md", "td")}
                    WHERE d.document_number = g.document_number
                      AND {populationScopePredicate(context, "d")}
                      AND {CategorySelectionRoles(command, "td", debitCategoryIds, "借方", schemaPrefix, categorySelection)}
                      AND d.amount_scaled >= 0)
            """;

        string DocHasCreditSide() =>
            $"""
            EXISTS (SELECT 1 FROM {schemaPrefix}target_gl_entry c
                    JOIN {schemaPrefix}target_account_mapping mc ON mc.account_code = c.account_code
                    {TaxonomyJoin(schemaPrefix, "mc", "tc")}
                    WHERE c.document_number = g.document_number
                      AND {populationScopePredicate(context, "c")}
                      AND {CategorySelectionRoles(command, "tc", creditCategoryIds, "貸方", schemaPrefix, categorySelection)}
                      AND c.amount_scaled < 0)
            """;

        return new CategorySidePredicates(
            RowIsDebitSide, RowIsCreditSide, DocHasDebitSide, DocHasCreditSide);
    }

    /// <summary>
    /// 依明示選取方式比對分類本身、下層或審計角色。省略方式的舊條件仍比對 semantic role，
    /// 因此相同 role 的自訂分類保留相同結果。分類身分一律綁定參數，改顯示 label 不改命中。
    /// 空集合一律 fail loud：否定模式的 NOT EXISTS 遇到空集合會反轉成全命中，
    /// 屬於必須擋在編譯前的 fail-open 缺口（validator 是第一道，本層是最後一道）。
    /// </summary>
    private static string CategorySelectionRoles(
        FilterSqlParameterPlanBuilder command,
        string taxonomyAlias,
        IReadOnlyList<string> categoryIds,
        string sideLabel,
        string schemaPrefix, string? categorySelection = null)
    {
        if (categoryIds.Count == 0)
        {
            throw new InvalidOperationException($"科目配對條件的{sideLabel}分類集合為空，無法編譯。");
        }

        var parameters = string.Join(", ", categoryIds.Select(categoryId => NextParam(command, categoryId)));
        if (categorySelection == "node") return $"{taxonomyAlias}.category_id IN ({parameters})";
        if (categorySelection == "subtree") return $"{taxonomyAlias}.category_id IN (SELECT descendant_id FROM {schemaPrefix}config_account_taxonomy_path WHERE ancestor_id IN ({parameters}))";
        if (categorySelection is not (null or "role")) throw new InvalidOperationException("分類選取方式無效。");
        return $"{taxonomyAlias}.semantic_role IN ("
            + $"SELECT ts.semantic_role FROM {schemaPrefix}config_account_taxonomy ts "
            + $"WHERE ts.category_id IN ({parameters}))";
    }

    /// <summary>四個側別片段建構器（借/貸 × 本列/同傳票）；呼叫時才綁參數。</summary>
    private sealed record CategorySidePredicates(
        Func<string> RowIsDebitSide,
        Func<string> RowIsCreditSide,
        Func<string> DocHasDebitSide,
        Func<string> DocHasCreditSide);

    /// <summary>
    /// typed dynamic rule（type:"typed"，2026-08-14 契約凍結）：committed RDE 欄位的有型別述詞。
    /// blank＝沒有 <c>target_gl_rde_value</c> row；所有比較 operator（含負向 notEquals／notContains／
    /// notIn）都以「存在 value row 且 row 值滿足述詞」的 EXISTS 表達——(entry_id, field_id) 是主鍵、
    /// 每列至多一筆值，因此負向 operator 是 evidence row 自身的述詞、blank 永不命中；`isBlank`／
    /// `isNotBlank` 專門判 missing／存在 value row。text 比較 trim＋不分大小寫（同 TextMatch 家族的
    /// UPPER(TRIM(...))）；date 以正規化 ISO 字串精確比較；money 在 scaled integer 域比較並依
    /// amountBasis 決定 signed／ABS。fieldId 與所有 operand 一律參數綁定，永不成為 SQL identifier。
    /// registry 缺欄位或 operator 型別不相容時 fail loud 擲 invalid_scenario 並指名 field——
    /// validator 是第一道，本層是未經驗證回放路徑的最後一道。ANSI 共通（EXISTS／NOT EXISTS）。
    /// </summary>
    public string TypedField(
        FilterSqlParameterPlanBuilder command,
        FilterRuleSpec rule,
        FilterRuleContext context,
        string schemaPrefix = "")
    {
        if (string.IsNullOrEmpty(rule.FieldId))
        {
            throw Invalid("typed 條件必須指定 fieldId。");
        }

        var field = context.RdeFields.FirstOrDefault(candidate =>
                string.Equals(candidate.FieldId, rule.FieldId, StringComparison.Ordinal))
            ?? throw Invalid($"RDE 欄位「{rule.FieldId}」不存在於目前案件已提交的欄位配對。");
        var op = rule.TypedOperator
            ?? throw Invalid($"RDE 欄位「{field.FieldId}」的 typed 條件必須指定 operator。");
        if (!TypedFieldOperatorSets.ForValueType(field.ValueType).Contains(op, StringComparer.Ordinal))
        {
            throw Invalid($"operator「{op}」與 RDE 欄位「{field.FieldId}」的型別 "
                + $"{field.ValueType} 不相容。");
        }

        var fieldParam = NextParam(command, field.FieldId);
        if (TypedFieldOperatorSets.IsBlankFamily(op))
        {
            var existence = op == TypedFieldOperatorSets.IsNotBlank ? "EXISTS" : "NOT EXISTS";
            return $"{existence} (SELECT 1 FROM {schemaPrefix}target_gl_rde_value v "
                + $"WHERE v.entry_id = g.entry_id AND v.field_id = {fieldParam})";
        }

        var typeParam = NextParam(command, field.ValueType);
        var valuePredicate = field.ValueType switch
        {
            RdeFieldValueTypeNames.Text => TypedTextPredicate(command, rule, op),
            RdeFieldValueTypeNames.Date => TypedDatePredicate(command, rule, op),
            RdeFieldValueTypeNames.Money => TypedMoneyPredicate(command, rule, op, context.MoneyScale),
            _ => throw Invalid($"RDE 欄位「{field.FieldId}」的型別 {field.ValueType} 不支援 typed 條件。")
        };

        return $"EXISTS (SELECT 1 FROM {schemaPrefix}target_gl_rde_value v "
            + $"WHERE v.entry_id = g.entry_id AND v.field_id = {fieldParam} "
            + $"AND v.value_type = {typeParam} AND {valuePredicate})";
    }

    private string TypedTextPredicate(
        FilterSqlParameterPlanBuilder command,
        FilterRuleSpec rule,
        string op)
    {
        // 儲存的 text_value 恆非空白但可能帶前後空白（投影原樣保存）；比較兩側都 trim＋upper。
        const string columnExpr = "TRIM(v.text_value)";
        var upperExpr = $"UPPER({columnExpr})";
        switch (op)
        {
            case "equals":
                return $"{upperExpr} = {NextParam(command, TypedTextKey(rule.TypedValue))}";
            case "notEquals":
                return $"{upperExpr} <> {NextParam(command, TypedTextKey(rule.TypedValue))}";
            case "contains":
                return dialect.ContainsIgnoreCase(
                    columnExpr, NextParam(command, TypedTextKey(rule.TypedValue)));
            case "notContains":
                return $"NOT {dialect.ContainsIgnoreCase(columnExpr, NextParam(command, TypedTextKey(rule.TypedValue)))}";
            case TypedFieldOperatorSets.In:
            case TypedFieldOperatorSets.NotIn:
            {
                var keys = TypedSetOperandKeys(
                    rule,
                    raw => TypedFieldOperandRules.TryNormalizeText(raw, out var normalized)
                        ? TypedFieldOperandRules.TextComparisonKey(normalized)
                        : throw Invalid("typed 文字值 trim 後不可為空。"));
                var parameters = string.Join(", ", keys.Select(key => NextParam(command, key)));
                var membership = op == TypedFieldOperatorSets.In ? "IN" : "NOT IN";
                return $"{upperExpr} {membership} ({parameters})";
            }

            default:
                throw Invalid($"不支援的 typed text operator「{op}」。");
        }
    }

    private string TypedTextKey(string? raw) =>
        TypedFieldOperandRules.TryNormalizeText(raw, out var normalized)
            ? TypedFieldOperandRules.TextComparisonKey(normalized)
            : throw Invalid("typed 文字值 trim 後不可為空。");

    private string TypedDatePredicate(
        FilterSqlParameterPlanBuilder command,
        FilterRuleSpec rule,
        string op)
    {
        // date_value 由投影正規化為 yyyy-MM-dd；ISO 字串比較三 provider 等價。
        const string columnExpr = "v.date_value";
        string DateParam(string? raw) =>
            TypedFieldOperandRules.TryNormalizeDate(raw, out var iso)
                ? NextParam(command, iso)
                : throw Invalid($"typed 日期「{raw}」格式須為 yyyy-MM-dd。");

        return op switch
        {
            "on" => $"{columnExpr} = {DateParam(rule.TypedValue)}",
            "before" => $"{columnExpr} < {DateParam(rule.TypedValue)}",
            "onOrBefore" => $"{columnExpr} <= {DateParam(rule.TypedValue)}",
            "after" => $"{columnExpr} > {DateParam(rule.TypedValue)}",
            "onOrAfter" => $"{columnExpr} >= {DateParam(rule.TypedValue)}",
            TypedFieldOperatorSets.Between =>
                $"({columnExpr} >= {DateParam(rule.TypedFrom)} AND {columnExpr} <= {DateParam(rule.TypedTo)})",
            _ => throw Invalid($"不支援的 typed date operator「{op}」。")
        };
    }

    private string TypedMoneyPredicate(
        FilterSqlParameterPlanBuilder command,
        FilterRuleSpec rule,
        string op,
        int moneyScale)
    {
        // amountBasis 是凍結契約的必填 closed token：signed 比較帶號 scaled 值、absolute 比較 ABS。
        var columnExpr = rule.AmountBasis switch
        {
            TypedAmountBasisNames.Signed => "v.amount_scaled",
            TypedAmountBasisNames.Absolute => "ABS(v.amount_scaled)",
            _ => throw Invalid("typed money 條件必須明示 amountBasis（signed 或 absolute）。")
        };
        long MoneyScaled(string? raw) =>
            TypedFieldOperandRules.TryNormalizeMoney(raw, moneyScale, out var scaled)
                ? scaled
                : throw Invalid($"typed 金額「{raw}」格式無效。");
        string MoneyParam(string? raw) => NextParam(command, MoneyScaled(raw));

        return op switch
        {
            "equals" => $"{columnExpr} = {MoneyParam(rule.TypedValue)}",
            "notEquals" => $"{columnExpr} <> {MoneyParam(rule.TypedValue)}",
            "greaterThan" => $"{columnExpr} > {MoneyParam(rule.TypedValue)}",
            "greaterThanOrEqual" => $"{columnExpr} >= {MoneyParam(rule.TypedValue)}",
            "lessThan" => $"{columnExpr} < {MoneyParam(rule.TypedValue)}",
            "lessThanOrEqual" => $"{columnExpr} <= {MoneyParam(rule.TypedValue)}",
            TypedFieldOperatorSets.Between =>
                $"({columnExpr} >= {MoneyParam(rule.TypedFrom)} AND {columnExpr} <= {MoneyParam(rule.TypedTo)})",
            _ => throw Invalid($"不支援的 typed money operator「{op}」。")
        };
    }

    /// <summary>
    /// `in`／`notIn` 的 operand 正規化與去重（依該型別的正規化語意，保留首次出現順序），
    /// 因此同一組值的重複輸入綁定同一份參數序列。空集合 fail loud——`NOT IN (空)` 會反轉語意。
    /// </summary>
    private static IReadOnlyList<T> TypedSetOperandDistinct<T>(
        IReadOnlyList<string> rawValues,
        Func<string, T> normalize)
        where T : notnull
    {
        var seen = new HashSet<T>();
        var result = new List<T>(rawValues.Count);
        foreach (var raw in rawValues)
        {
            var normalized = normalize(raw);
            if (seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    private static IReadOnlyList<string> TypedSetOperandKeys(
        FilterRuleSpec rule,
        Func<string, string> normalize)
    {
        var values = rule.TypedValues;
        if (values is null || values.Count == 0
            || values.Count > FilterScenarioLimits.MaxTypedInValuesPerRule)
        {
            throw Invalid($"typed 條件的 values 必須是 1–"
                + $"{FilterScenarioLimits.MaxTypedInValuesPerRule} 個字串的陣列。");
        }

        return TypedSetOperandDistinct(values, normalize);
    }

    private static JetActionException Invalid(string message) =>
        new(JetErrorCodes.InvalidScenario, message);

    /// <summary>週末過帳/核准（weekend_*）：依設定的星期判斷，補班日仍納入。dateColumn 僅接受本層常數。</summary>
    public string Weekend(string dateColumn, IReadOnlyList<int>? nonWorkingDays, string schemaPrefix = "")
    {
        return dialect.WeekendPredicate(
            $"g.{dateColumn}",
            NonWorkingDays.Resolve(nonWorkingDays));
    }

    /// <summary>假日過帳/核准（holiday_*）：日期落在已上傳的假日曆。</summary>
    public string Holiday(string dateColumn, string schemaPrefix = "")
    {
        return $"""
            EXISTS (
                SELECT 1 FROM {schemaPrefix}staging_calendar_raw_day d
                WHERE d.day_type = 'holiday' AND d.date = g.{dateColumn})
            """;
    }

    /// <summary>摘要空白（blank_description）。</summary>
    public string BlankDescription()
    {
        return "(g.document_description IS NULL OR TRIM(g.document_description) = '')";
    }

    /// <summary>回溯過帳:過帳日早於傳票日。voucher_date 為 NULL 不命中;
    /// post_date 為 NULL 時 &lt; 為未知亦不命中。純 ANSI 欄位比較,雙 provider 相同。</summary>
    public string Backdated() =>
        "(g.voucher_date IS NOT NULL AND g.post_date < g.voucher_date)";

    /// <summary>非授權編製人員（non_authorized_preparer）：created_by 非空白且不在授權清單。
    /// 純 ANSI(EXISTS 守門 + TRIM/NOT IN 子查詢),雙 provider 相同。
    /// 前綴 EXISTS 自保:授權清單為空時 `x NOT IN (空集合)` 會反轉成全命中,
    /// 故名單空 → 整體述詞 FALSE(無命中,與 prescreen.run 的 na 語意對齊),
    /// 即便 validator/handler 閘控被繞過仍安全。</summary>
    public string NonAuthorizedPreparer(string schemaPrefix = "") =>
        $"(EXISTS (SELECT 1 FROM {schemaPrefix}target_authorized_preparer) " +
        "AND g.created_by IS NOT NULL AND TRIM(g.created_by) <> '' " +
        $"AND TRIM(g.created_by) NOT IN (SELECT name FROM {schemaPrefix}target_authorized_preparer))";

    /// <summary>低頻編製者（low_frequency_preparer）：created_by 在所選母體內的分錄筆數 ≤ maxEntries。
    /// 門檻參數綁定;子查詢與外層共用 scope, GROUP BY/HAVING/COUNT(*) 皆 ANSI 共通。</summary>
    public string LowFrequencyPreparer(
        FilterSqlParameterPlanBuilder command,
        int maxEntries,
        FilterRuleContext context,
        string schemaPrefix = "")
    {
        var p = NextParam(command, maxEntries);
        return $"g.created_by IN (SELECT f.created_by FROM {schemaPrefix}target_gl_entry f "
            + $"WHERE {populationScopePredicate(context, "f")} "
            + $"GROUP BY f.created_by HAVING COUNT(*) <= {p})";
    }

    /// <summary>低頻科目(low_frequency_account,C9):account_code 在所選母體內的分錄筆數 ≤ maxEntries。
    /// 門檻參數綁定;子查詢與外層共用 scope, GROUP BY/HAVING/COUNT(*) 皆 ANSI 共通。
    /// 與 rareAccounts(R6 彙總)並存,本述詞為其可作列述詞的版本。</summary>
    public string LowFrequencyAccount(
        FilterSqlParameterPlanBuilder command,
        int maxEntries,
        FilterRuleContext context,
        string schemaPrefix = "")
    {
        var p = NextParam(command, maxEntries);
        return $"g.account_code IN (SELECT a.account_code FROM {schemaPrefix}target_gl_entry a "
            + $"WHERE {populationScopePredicate(context, "a")} "
            + $"GROUP BY a.account_code HAVING COUNT(*) <= {p})";
    }

    /// <summary>filter text 條件：關鍵字以 OR 串接；NOT 模式整體取反（COALESCE 保住 NULL 列）。</summary>
    public string TextMatch(
        FilterSqlParameterPlanBuilder command,
        string column,
        IReadOnlyList<string> keywords,
        TextMatchMode mode)
    {
        var positive = mode is TextMatchMode.Contains or TextMatchMode.NotContains
            ? TextContainsAny(command, $"g.{column}", keywords)
            : TextEqualsAny(command, $"g.{column}", keywords);

        return mode is TextMatchMode.NotContains or TextMatchMode.NotExact
            ? $"NOT {positive}"
            : positive;
    }

    /// <summary>filter 自訂關鍵字條件（custom_keywords）：同摘要特定描述述詞，關鍵字為使用者輸入。</summary>
    public string CustomKeywords(FilterSqlParameterPlanBuilder command, IReadOnlyList<string> keywords)
    {
        return TextContainsAny(command, "g.document_description", keywords);
    }

    /// <summary>filter dateRange 條件（單邊界允許；ISO 字串比較）。</summary>
    public string DateRange(FilterSqlParameterPlanBuilder command, string column, string? from, string? to)
    {
        var parts = new List<string>();
        if (from is not null)
        {
            parts.Add($"g.{column} >= {NextParam(command, from)}");
        }

        if (to is not null)
        {
            parts.Add($"g.{column} <= {NextParam(command, to)}");
        }

        return $"({string.Join(" AND ", parts)})";
    }

    /// <summary>filter numRange 條件：|scaled 金額| 區間（單邊界允許）。</summary>
    public string AmountRange(FilterSqlParameterPlanBuilder command, long? fromScaled, long? toScaled)
    {
        var parts = new List<string>();
        if (fromScaled is not null)
        {
            parts.Add($"ABS(g.amount_scaled) >= {NextParam(command, fromScaled.Value)}");
        }

        if (toScaled is not null)
        {
            parts.Add($"ABS(g.amount_scaled) <= {NextParam(command, toScaled.Value)}");
        }

        return $"({string.Join(" AND ", parts)})";
    }

    /// <summary>filter drCrOnly 條件。</summary>
    public string DrCrOnly(FilterSqlParameterPlanBuilder command, string drCr)
    {
        var p = NextParam(command, drCr == "debit" ? "DEBIT" : "CREDIT");
        return $"g.dr_cr = {p}";
    }

    /// <summary>filter manualAuto 條件；is_manual 為 NULL（來源未提供旗標）的列永不匹配。</summary>
    public string ManualAuto(FilterSqlParameterPlanBuilder command, bool isManual)
    {
        var p = NextParam(command, isManual ? 1 : 0);
        return $"g.is_manual = {p}";
    }

    /// <summary>
    /// 季末前借記收入(revenue_debit_near_quarter_end,KCT 清單 A):科目分類 = Revenue 且借方側
    /// (amount_scaled >= 0),且 post_date 落在任一季底視窗內。視窗由 Domain QuarterEndWindows 算出,
    /// 邊界參數綁定。視窗為空(X 非法或與期間無交集)→ 零命中。需科目配對已匯入。ANSI 共通。
    /// </summary>
    public string RevenueDebitNearQuarterEnd(
        FilterSqlParameterPlanBuilder command,
        IReadOnlyList<QuarterEndWindows.Window> windows,
        string schemaPrefix = "")
    {
        if (windows.Count == 0)
        {
            return "1 = 0";
        }

        var revenue = NextParam(command, AccountTaxonomyBuiltIns.RevenueRole);
        var windowClauses = string.Join(" OR ", windows.Select(w =>
            $"(g.post_date >= {NextParam(command, w.FromIso)} AND g.post_date <= {NextParam(command, w.ToIso)})"));

        return $"""
            (EXISTS (SELECT 1 FROM {schemaPrefix}target_account_mapping m
                     {TaxonomyJoin(schemaPrefix, "m", "tm")}
                     WHERE m.account_code = g.account_code AND tm.semantic_role = {revenue})
             AND g.amount_scaled >= 0
             AND ({windowClauses}))
            """;
    }

    /// <summary>
    /// 收入無一般對方科目(revenue_without_normal_counterpart,KCT 清單 C):本列為 Revenue 貸方
    /// (amount_scaled &lt; 0),但其所在傳票無任何「借方側(amount_scaled >= 0)且分類 ∈
    /// {Receivables, Receipt in advance}」的分錄(不含 Cash)——unexpected_account_pair 的否定面。
    /// ANSI 共通(EXISTS + NOT EXISTS)。需科目配對已匯入。
    /// </summary>
    public string RevenueWithoutNormalCounterpart(
        FilterSqlParameterPlanBuilder command,
        FilterRuleContext context,
        string schemaPrefix = "")
    {
        var revenue = NextParam(command, AccountTaxonomyBuiltIns.RevenueRole);
        var receivables = NextParam(command, AccountTaxonomyBuiltIns.ReceivablesRole);
        var receiptInAdvance = NextParam(command, AccountTaxonomyBuiltIns.ReceiptInAdvanceRole);

        return $"""
            (EXISTS (SELECT 1 FROM {schemaPrefix}target_account_mapping m
                     {TaxonomyJoin(schemaPrefix, "m", "tm")}
                     WHERE m.account_code = g.account_code AND tm.semantic_role = {revenue})
             AND g.amount_scaled < 0
             AND NOT EXISTS (
                 SELECT 1 FROM {schemaPrefix}target_gl_entry d
                 JOIN {schemaPrefix}target_account_mapping md ON md.account_code = d.account_code
                 {TaxonomyJoin(schemaPrefix, "md", "td")}
                 WHERE d.document_number = g.document_number
                   AND {populationScopePredicate(context, "d")}
                   AND d.amount_scaled >= 0
                   AND td.semantic_role IN ({receivables}, {receiptInAdvance})))
            """;
    }

    /// <summary>
    /// 收入之人工分錄(manual_revenue_entry,KCT 清單 D):科目分類 = Revenue 且 is_manual = 1
    /// (來源未提供人工旗標的列為 NULL,永不匹配,同 manualAuto)。需科目配對已匯入。ANSI 共通。
    /// </summary>
    public string ManualRevenueEntry(FilterSqlParameterPlanBuilder command, string schemaPrefix = "")
    {
        var revenue = NextParam(command, AccountTaxonomyBuiltIns.RevenueRole);
        return $"""
            (EXISTS (SELECT 1 FROM {schemaPrefix}target_account_mapping m
                     {TaxonomyJoin(schemaPrefix, "m", "tm")}
                     WHERE m.account_code = g.account_code AND tm.semantic_role = {revenue})
             AND g.is_manual = 1)
            """;
    }

    /// <summary>
    /// category_id 是 v7 權威 join；第二支只供已存在但尚未跑 v6→v7 backfill 的 legacy 測試／案檔讀回。
    /// 進入 taxonomy 後的所有商業比較一律看 semantic_role，不比較顯示 label。
    /// </summary>
    private static string TaxonomyJoin(
        string schemaPrefix,
        string mappingAlias,
        string taxonomyAlias) =>
        $"JOIN {schemaPrefix}config_account_taxonomy {taxonomyAlias} ON "
        + $"{taxonomyAlias}.category_id = {mappingAlias}.category_id "
        + $"OR ({mappingAlias}.category_id IS NULL AND {taxonomyAlias}.is_builtin = 1 "
        + $"AND {taxonomyAlias}.label = {mappingAlias}.standardized_category)";

    /// <summary>
    /// 特定金額尾數(trailing_digits):純機械式尾數比對——把金額主單位整數(捨去小數)的末 k 位,
    /// 與審計員指定的 k 位樣態逐字比對,相等即命中;多組樣態任一相等即命中。工具只回答「尾數是否相符」,
    /// 不判斷風險、門檻或舞弊,也不把小數位、顯示補零、scale 或格式化結果納入比對——某個尾數是否值得篩、
    /// 在特定案件代表什麼,一律由審計員自行判斷。
    ///
    /// 等價於 legacy IDEA:@Right(@Str(@int(amount),1,0), k) = pattern。以整數運算表達:
    ///   整數化金額 intAmount = ABS(amount_scaled) / scale   ——整數除法,等同 @int(ABS(amount)),捨去小數;
    ///   尾數比對   intAmount % 10^k = 樣態之數值             ——末 k 位相等。
    /// 字尾長度:數字要「以 k 位樣態結尾」須本身至少有 k 位(等同 @Right 在字串短於 k 時回傳整串、
    /// 長度不符即不相等)。故 k≥2 時加 intAmount ≥ 10^(k-1);k=1 時每個整數(含 0)都 ≥1 位、不設下界
    /// (與 legacy @Right(@Str(0,1,0),1) = "0" 對整數 0 命中樣態 "0" 一致)。整數 / 與 % 皆 ANSI,三 provider 等價。
    /// 樣態已由 validator 保證為純數字。
    /// </summary>
    public string TrailingDigits(
        FilterSqlParameterPlanBuilder command,
        IReadOnlyList<string> patterns,
        int moneyScale,
        string amountColumn = "g.amount_scaled")
    {
        var scale = NextParam(command, (long)moneyScale);

        var clauses = patterns
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p =>
            {
                var trimmed = p.Trim();
                var digitCount = trimmed.Length;
                var tenK = 1L;
                for (var i = 0; i < digitCount; i++)
                {
                    tenK *= 10;
                }

                var modulus = NextParam(command, tenK);
                var tail = NextParam(command, long.Parse(trimmed, System.Globalization.CultureInfo.InvariantCulture));
                // 整數化金額：捨去小數的主單位整數(= @int(ABS(amount)));DuckDB 的 / 會回浮點，必須走方言縫。
                var intAmount = dialect.IntegerQuotient($"ABS({amountColumn})", scale);
                var tailMatch = $"{intAmount} % {modulus} = {tail}";

                if (digitCount == 1)
                {
                    // k=1：整數末位比對,無長度下界(每個整數含 0 都 ≥1 位)。
                    return $"({tailMatch})";
                }

                // k≥2：整數須至少 k 位(≥ 10^(k-1)),否則字串短於樣態、長度不符即不相等。
                var minKDigits = NextParam(command, tenK / 10);
                return $"({intAmount} >= {minKDigits} AND {tailMatch})";
            })
            .ToList();

        return clauses.Count == 0 ? "1 = 0" : $"({string.Join(" OR ", clauses)})";
    }

    /// <summary>
    /// 編製與核准同一人(preparer_equals_approver,KCT 清單 J):created_by 與 approved_by 皆非空白
    /// 且(忽略大小寫與前後空白)相等。createBy/approveBy 未配對時對應欄為 NULL,零命中。
    /// 純 ANSI 欄位比較,雙 provider 相同。
    /// </summary>
    public string PreparerEqualsApprover() =>
        "(g.created_by IS NOT NULL AND TRIM(g.created_by) <> '' " +
        "AND g.approved_by IS NOT NULL " +
        "AND UPPER(TRIM(g.created_by)) = UPPER(TRIM(g.approved_by)))";

    private string TextContainsAny(
        FilterSqlParameterPlanBuilder command,
        string columnExpr,
        IReadOnlyList<string> keywords)
    {
        var clauses = keywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => dialect.ContainsIgnoreCase(
                columnExpr, NextParam(command, k.Trim().ToUpperInvariant())));

        return $"({string.Join(" OR ", clauses)})";
    }

    private string TextEqualsAny(
        FilterSqlParameterPlanBuilder command,
        string columnExpr,
        IReadOnlyList<string> keywords)
    {
        var clauses = keywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => $"UPPER(TRIM(COALESCE({columnExpr}, ''))) = {NextParam(command, k.Trim().ToUpperInvariant())}");

        return $"({string.Join(" OR ", clauses)})";
    }

    /// <summary>參數名以現有計畫項目數量遞增，避免跨片段衝突。</summary>
    private static string NextParam(FilterSqlParameterPlanBuilder command, object value) =>
        command.AddValue(value);
}
