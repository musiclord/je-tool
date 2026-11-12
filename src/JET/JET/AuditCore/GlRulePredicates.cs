using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// GL 規則述詞的單一事實來源（部分片段經 <see cref="ISqlDialect"/> 取得）。每個方法回傳針對別名 g 的 WHERE 片段，
/// 並把值依序累積到純參數計畫；DbCommand 建立與綁定只由 Infrastructure 負責。
/// prescreen.run 的計數與 filter 條件組合共用同一份片段。
/// 識別字一律出自 GlFieldWhitelist 或本檔常數；使用者值只進參數。
/// </summary>
internal sealed partial class GlRulePredicates(
    ISqlDialect dialect,
    Func<FilterConditionContext, string, string> populationScopePredicate)
{
    /// <summary>期末後核准（prep_period_approval）：核准日 ≥ 期末財報準備日。</summary>
    public string PrepPeriodApproval(FilterSqlParameterPlanBuilder command, string prepStartDate)
    {
        var p = NextParam(command, prepStartDate);
        return $"g.approval_date >= {p}";
    }

    /// <summary>摘要特定描述（suspicious_keywords）：摘要含任一預設關鍵字，和第五步「傳票摘要」的「包含任一文字」同一個比對。</summary>
    public string SuspiciousKeywords(FilterSqlParameterPlanBuilder command)
    {
        return ContainsAnyKeyword(command, "g.description", SuspiciousKeywordDefaults.Defaults);
    }

    /// <summary>
    /// 未預期借貸組合（unexpected_account_pair）照舊版 IDEA R3 寫：本列是收入貸方（amount_scaled &lt; 0），
    /// 同一張傳票、同一個母體範圍裡，沒有應收款項、現金或預收款且金額大於 0 的分錄，
    /// 但至少有一筆金額大於 0 的分錄（舊版先取這類傳票裡金額大於 0 的分錄，再依傳票號碼和收入貸方對上）。
    /// 整張傳票沒有正金額分錄時借貸一定不平，借貸不平測試會列出來，這條規則不挑。
    /// 正常對方科目要大於 0，0 元的應收或現金不算有正常對方科目，和舊版相同。
    /// 直接命中的只有收入貸方這一列；第四步與預篩選報告要列整張傳票，由列出命中分錄的地方處理，判斷式不變。
    /// 「收入無一般對方科目」是另一條常用篩選條件：對方科目不含現金，借方側採 amount_scaled >= 0，也不要求正金額分錄。
    /// 現銷傳票與沒有正金額分錄的傳票只有它挑中；傳票的應收或預收借方只有 0 元、另有其他正金額分錄時，只有這條規則挑中。
    /// 其他借貸組合條件（accountPair、specialAccountCategoryPair）照樣以 amount_scaled >= 0 判定借方側。
    /// 需科目配對已匯入。
    /// </summary>
    public string UnexpectedAccountPair(
        FilterSqlParameterPlanBuilder command,
        FilterConditionContext context,
        string schemaPrefix = "")
    {
        var revenue = NextParam(command, AccountCategoryBuiltIns.RevenueRole);
        var counterparts = CategoryListParams(command);

        return $"""
            (g.voucher_number IS NOT NULL
             AND EXISTS (SELECT 1 FROM {schemaPrefix}account_mapping m
                     {CategoryJoin(schemaPrefix, "m", "tm")}
                     WHERE m.account_number = g.account_number AND tm.semantic_role = {revenue})
             AND g.amount_scaled < 0
             AND EXISTS (
                 SELECT 1 FROM {schemaPrefix}gl p
                 WHERE p.voucher_number = g.voucher_number
                   AND {populationScopePredicate(context, "p")}
                   AND p.amount_scaled > 0)
             AND NOT EXISTS (
                 SELECT 1 FROM {schemaPrefix}gl d
                 JOIN {schemaPrefix}account_mapping md ON md.account_number = d.account_number
                 {CategoryJoin(schemaPrefix, "md", "td")}
                 WHERE d.voucher_number = g.voucher_number
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
                AccountCategoryBuiltIns.ReceivablesRole,
                AccountCategoryBuiltIns.CashRole,
                AccountCategoryBuiltIns.ReceiptInAdvanceRole
            }.Select(role => NextParam(command, role)));
    }

    /// <summary>
    /// 科目配對分析（account_pair）的三種模式。借貸側判定統一：
    /// `amount_scaled >= 0` 屬借方側、`&lt; 0` 屬貸方側。錨定模式輸出
    /// 錨定分錄與同傳票的對方側分錄。
    /// </summary>
    public string AccountPair(
        FilterSqlParameterPlanBuilder command,
        string pairMode,
        IReadOnlyList<string> debitCategoryIds,
        IReadOnlyList<string> creditCategoryIds,
        FilterConditionContext context,
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
    /// A = 借方類別、B = 貸方類別，借貸側判定與 <see cref="AccountPair"/> 一致（`amount_scaled >= 0` 借方、`&lt; 0` 貸方）。
    /// 否定模式以 NOT EXISTS（即 NOT DocHasCreditSide(B) / NOT DocHasDebitSide(A)）藏在述詞內，
    /// 不洩漏到呼叫端：
    ///   drAndCr → 傳票同時有 A 借與 B 貸；tag「A 借 或 B 貸」的列。
    ///   drNotCr → 傳票有 A 借、且無任何 B 貸；tag「A 借」的列，以及同一張傳票貸方那一側的全部列。
    ///   notDrCr → 傳票有 B 貸、且無任何 A 借；tag「B 貸」的列，以及同一張傳票借方那一側的全部列。
    /// 兩個否定模式照舊版連對方那一側一起標記，和 <see cref="AccountPair"/> 的錨定模式標記方式相同。
    /// 取捨（顯式陳述）：drAndCr 的 SQL 與 AccountPair 的 exact 模式邏輯重疊，但這是不同的
    /// 使用者面向條件（不同的模式標籤與否定語意），重複是刻意的——共用的是四個側別 closure，
    /// 而非條件本身。
    /// </summary>
    public string SpecialAccountCategoryPair(
        FilterSqlParameterPlanBuilder command,
        string pairMode,
        IReadOnlyList<string> debitCategoryIds,
        IReadOnlyList<string> creditCategoryIds,
        FilterConditionContext context,
        string schemaPrefix = "", string? categorySelection = null)
    {
        var side = CategorySides(command, debitCategoryIds, creditCategoryIds, context, schemaPrefix, categorySelection);

        return pairMode switch
        {
            SpecialAccountCategoryPairModes.DrAndCr =>
                $"({side.DocHasDebitSide()} AND {side.DocHasCreditSide()} AND ({side.RowIsDebitSide()} OR {side.RowIsCreditSide()}))",
            SpecialAccountCategoryPairModes.DrNotCr =>
                $"({side.DocHasDebitSide()} AND NOT {side.DocHasCreditSide()} AND ({side.RowIsDebitSide()} OR g.amount_scaled < 0))",
            SpecialAccountCategoryPairModes.NotDrCr =>
                $"({side.DocHasCreditSide()} AND NOT {side.DocHasDebitSide()} AND ({side.RowIsCreditSide()} OR g.amount_scaled >= 0))",
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
        FilterConditionContext context,
        string schemaPrefix = "", string? categorySelection = null)
    {
        string RowIsDebitSide() =>
            $"""
            (EXISTS (SELECT 1 FROM {schemaPrefix}account_mapping m
                     {CategoryJoin(schemaPrefix, "m", "tm")}
                     WHERE m.account_number = g.account_number
                       AND {CategorySelectionRoles(command, "tm", debitCategoryIds, "借方", schemaPrefix, categorySelection)})
             AND g.amount_scaled >= 0)
            """;

        string RowIsCreditSide() =>
            $"""
            (EXISTS (SELECT 1 FROM {schemaPrefix}account_mapping m
                     {CategoryJoin(schemaPrefix, "m", "tm")}
                     WHERE m.account_number = g.account_number
                       AND {CategorySelectionRoles(command, "tm", creditCategoryIds, "貸方", schemaPrefix, categorySelection)})
             AND g.amount_scaled < 0)
            """;

        string DocHasDebitSide() =>
            $"""
            EXISTS (SELECT 1 FROM {schemaPrefix}gl d
                    JOIN {schemaPrefix}account_mapping md ON md.account_number = d.account_number
                    {CategoryJoin(schemaPrefix, "md", "td")}
                    WHERE d.voucher_number = g.voucher_number
                      AND {populationScopePredicate(context, "d")}
                      AND {CategorySelectionRoles(command, "td", debitCategoryIds, "借方", schemaPrefix, categorySelection)}
                      AND d.amount_scaled >= 0)
            """;

        string DocHasCreditSide() =>
            $"""
            EXISTS (SELECT 1 FROM {schemaPrefix}gl c
                    JOIN {schemaPrefix}account_mapping mc ON mc.account_number = c.account_number
                    {CategoryJoin(schemaPrefix, "mc", "tc")}
                    WHERE c.voucher_number = g.voucher_number
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
        string categoryAlias,
        IReadOnlyList<string> categoryIds,
        string sideLabel,
        string schemaPrefix, string? categorySelection = null)
    {
        if (categoryIds.Count == 0)
        {
            throw new InvalidOperationException($"科目配對條件的{sideLabel}分類集合為空，無法編譯。");
        }

        var parameters = string.Join(", ", categoryIds.Select(categoryId => NextParam(command, categoryId)));
        if (categorySelection == "node") return $"{categoryAlias}.category_id IN ({parameters})";
        if (categorySelection == "subtree") return $"{categoryAlias}.category_id IN (SELECT descendant_id FROM {schemaPrefix}account_category_path WHERE ancestor_id IN ({parameters}))";
        if (categorySelection is not (null or "role")) throw new InvalidOperationException("分類選取方式無效。");
        return $"{categoryAlias}.semantic_role IN ("
            + $"SELECT ts.semantic_role FROM {schemaPrefix}account_category ts "
            + $"WHERE ts.category_id IN ({parameters}))";
    }

    /// <summary>四個側別片段建構器（借/貸 × 本列/同傳票）；呼叫時才綁參數。</summary>
    private sealed record CategorySidePredicates(
        Func<string> RowIsDebitSide,
        Func<string> RowIsCreditSide,
        Func<string> DocHasDebitSide,
        Func<string> DocHasCreditSide);

    /// <summary>
    /// typed dynamic rule（type:"typed"）：committed RDE 欄位的有型別述詞。
    /// blank＝沒有 <c>gl_rde_value</c> row；所有比較 operator（含負向 notEquals／notContains／
    /// notIn）都以「存在 value row 且 row 值滿足述詞」的 EXISTS 表達——(entry_id, field_id) 是主鍵、
    /// 每列至多一筆值，因此負向 operator 是 evidence row 自身的述詞、blank 永不命中；`isBlank`／
    /// `isNotBlank` 專門判 missing／存在 value row。text 比較 trim＋不分大小寫（同 TextMatch 家族的
    /// UPPER(TRIM(...))）；date 以正規化 ISO 字串精確比較；amount 在 scaled integer 域比較並依
    /// amountBasis 決定 signed／ABS。fieldId 與所有 operand 一律參數綁定，永不成為 SQL identifier。
    /// registry 缺欄位或 operator 型別不相容時 fail loud 擲 invalid_scenario 並指名 field——
    /// validator 是第一道，本層是未經驗證回放路徑的最後一道。
    /// </summary>
    public string TypedField(
        FilterSqlParameterPlanBuilder command,
        FilterConditionSpec rule,
        FilterConditionContext context,
        string schemaPrefix = "")
    {
        if (string.IsNullOrEmpty(rule.FieldId))
        {
            throw Invalid("typed_field_unselected");
        }

        var field = context.RdeFields.FirstOrDefault(candidate =>
                string.Equals(candidate.FieldId, rule.FieldId, StringComparison.Ordinal))
            ?? throw Invalid("typed_field_not_mapped");
        var op = rule.TypedOperator
            ?? throw Invalid("typed_operator_unselected");
        if (!TypedFieldOperatorSets.ForValueType(field.ValueType).Contains(op, StringComparer.Ordinal))
        {
            throw Invalid("typed_operator_type_mismatch");
        }

        var fieldParam = NextParam(command, field.FieldId);
        if (TypedFieldOperatorSets.IsBlankFamily(op))
        {
            var existence = op == TypedFieldOperatorSets.IsNotBlank ? "EXISTS" : "NOT EXISTS";
            return $"{existence} (SELECT 1 FROM {schemaPrefix}gl_rde_value v "
                + $"WHERE v.entry_id = g.entry_id AND v.field_id = {fieldParam})";
        }

        var typeParam = NextParam(command, field.ValueType);
        var valuePredicate = field.ValueType switch
        {
            RdeFieldValueTypeNames.Text => TypedTextPredicate(command, rule, op),
            RdeFieldValueTypeNames.Date => TypedDatePredicate(command, rule, op, context.DateParseOptions),
            RdeFieldValueTypeNames.Amount => TypedMoneyPredicate(command, rule, op, context.MoneyScale),
            _ => throw Invalid("typed_value_type_unsupported")
        };

        return $"EXISTS (SELECT 1 FROM {schemaPrefix}gl_rde_value v "
            + $"WHERE v.entry_id = g.entry_id AND v.field_id = {fieldParam} "
            + $"AND v.value_type = {typeParam} AND {valuePredicate})";
    }

    private string TypedTextPredicate(
        FilterSqlParameterPlanBuilder command,
        FilterConditionSpec rule,
        string op)
    {
        // 儲存的 text_value 恆非空白但可能帶前後空白（投影原樣保存）；比較兩側都 trim＋upper，去空白的字元集合和 .NET 相同。
        var columnExpr = dialect.Trim("v.text_value");
        var upperExpr = dialect.Upper(columnExpr);
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
                        : throw Invalid("typed_text_value_blank"));
                var parameters = string.Join(", ", keys.Select(key => NextParam(command, key)));
                var membership = op == TypedFieldOperatorSets.In ? "IN" : "NOT IN";
                return $"{upperExpr} {membership} ({parameters})";
            }

            default:
                throw Invalid("typed_text_operator_unsupported");
        }
    }

    private string TypedTextKey(string? raw) =>
        TypedFieldOperandRules.TryNormalizeText(raw, out var normalized)
            ? TypedFieldOperandRules.TextComparisonKey(normalized)
            : throw Invalid("typed_text_value_blank");

    private string TypedDatePredicate(
        FilterSqlParameterPlanBuilder command,
        FilterConditionSpec rule,
        string op,
        DateParseOptions dateOptions)
    {
        // date_value 由投影正規化為 yyyy-MM-dd。
        const string columnExpr = "v.date_value";
        string DateParam(string? raw) =>
            TypedFieldOperandRules.TryNormalizeDate(raw, dateOptions, out var iso)
                ? NextParam(command, iso)
                : throw Invalid("typed_date_value_invalid");

        return op switch
        {
            "on" => $"{columnExpr} = {DateParam(rule.TypedValue)}",
            "before" => $"{columnExpr} < {DateParam(rule.TypedValue)}",
            "onOrBefore" => $"{columnExpr} <= {DateParam(rule.TypedValue)}",
            "after" => $"{columnExpr} > {DateParam(rule.TypedValue)}",
            "onOrAfter" => $"{columnExpr} >= {DateParam(rule.TypedValue)}",
            TypedFieldOperatorSets.Between =>
                $"({columnExpr} >= {DateParam(rule.TypedFrom)} AND {columnExpr} <= {DateParam(rule.TypedTo)})",
            _ => throw Invalid("typed_date_operator_unsupported")
        };
    }

    private string TypedMoneyPredicate(
        FilterSqlParameterPlanBuilder command,
        FilterConditionSpec rule,
        string op,
        int moneyScale)
    {
        // 每條 money 條件都要帶 amountBasis，值只能是 signed 或 absolute：signed 比較帶號 scaled 值、absolute 比較 ABS。
        var columnExpr = rule.AmountBasis switch
        {
            TypedAmountBasisNames.Signed => "v.amount_scaled",
            TypedAmountBasisNames.Absolute => "ABS(v.amount_scaled)",
            _ => throw Invalid("typed_amount_basis_missing")
        };
        long MoneyScaled(string? raw) =>
            TypedFieldOperandRules.TryNormalizeMoney(raw, moneyScale, out var scaled)
                ? scaled
                : throw Invalid("typed_amount_value_invalid");
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
            _ => throw Invalid("typed_amount_operator_unsupported")
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
        FilterConditionSpec rule,
        Func<string, string> normalize)
    {
        var values = rule.TypedValues;
        if (values is null || values.Count == 0
            || values.Count > FilterScenarioLimits.MaxTypedInValuesPerRule)
        {
            throw Invalid("typed_set_value_count_invalid");
        }

        return TypedSetOperandDistinct(values, normalize);
    }

    // 驗證器已先檢查過同樣的條件；走到這裡是驗證器與執行時檢查不一致的程式錯誤。畫面只顯示非預期錯誤那一句，
    // 檢查代號記進支援日誌。check 只能是程式寫死的代號。
    private static JetActionException Invalid(string check) => FilterCompileCheck.Create(check);

    /// <summary>週末（weekend_*）：日期落在案件設定的週末日，補班日仍納入。dateExpr 只接受本層常數或欄位目錄解析出的欄位。</summary>
    public string Weekend(string dateExpr, IReadOnlyList<int>? nonWorkingDays)
    {
        return dialect.WeekendPredicate(dateExpr, NonWorkingDays.Resolve(nonWorkingDays));
    }

    /// <summary>假日（holiday_*）：日期落在已上傳假日檔的假日。</summary>
    public string Holiday(string dateExpr, string schemaPrefix = "") => CalendarDay(dateExpr, "holiday", schemaPrefix);

    /// <summary>補班日：日期落在已上傳假日檔的補班日。</summary>
    public string MakeupDay(string dateExpr, string schemaPrefix = "") => CalendarDay(dateExpr, "makeup", schemaPrefix);

    private static string CalendarDay(string dateExpr, string dayType, string schemaPrefix) =>
        $"EXISTS (SELECT 1 FROM {schemaPrefix}calendar_day d WHERE d.day_type = '{dayType}' AND d.date = {dateExpr})";

    /// <summary>文字欄位空白：NULL 或去空白後為空。去空白的字元集合和 .NET 相同（dialect.Trim）。</summary>
    public string BlankText(string columnExpr) => SqlBlankText.IsBlank(dialect, columnExpr);

    /// <summary>空白摘要（blank_description）：和第五步「傳票摘要」的「空白」同一個判斷。</summary>
    public string BlankDescription() => BlankText("g.description");

    /// <summary>非授權編製人員（non_authorized_preparer）：created_by 非空白且不在授權清單。
    /// 人員識別值的比對去頭尾空白、不分大小寫，和人員清單、編製等於核准同一規則。
    /// 前綴 EXISTS 自保:授權清單為空時 `x NOT IN (空集合)` 會反轉成全命中,
    /// 故名單空 → 整體述詞 FALSE(無命中,與 prescreen.run 的 na 語意對齊),
    /// 即便 validator 或 handler 的前置檢查被繞過仍安全。</summary>
    public string NonAuthorizedPreparer(string schemaPrefix = "") =>
        $"(EXISTS (SELECT 1 FROM {schemaPrefix}authorized_preparer) " +
        $"AND {SqlBlankText.IsNotBlank(dialect, "g.created_by")} " +
        $"AND {dialect.Upper(dialect.Trim("g.created_by"))} NOT IN (SELECT {dialect.Upper(dialect.Trim("name"))} FROM {schemaPrefix}authorized_preparer))";

    /// <summary>filter text 條件：關鍵字以 OR 串接；NOT 模式整體取反（COALESCE 保住 NULL 列）。</summary>
    public string TextMatch(
        FilterSqlParameterPlanBuilder command,
        string column,
        IReadOnlyList<string> keywords,
        TextMatchMode mode)
    {
        var positive = mode is TextMatchMode.Contains or TextMatchMode.NotContains
            ? ContainsAnyKeyword(command, $"g.{column}", keywords)
            : TextEqualsAny(command, $"g.{column}", keywords);

        return mode is TextMatchMode.NotContains or TextMatchMode.NotExact
            ? $"NOT {positive}"
            : positive;
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
    /// 季末前借記收入(revenue_debit_near_quarter_end):科目分類 = Revenue 且借方側
    /// (amount_scaled >= 0),且 gl_date 落在任一季底視窗內。視窗由 Domain QuarterEndWindows 算出,
    /// 邊界參數綁定。視窗為空(X 非法或與期間無交集)→ 零命中。需科目配對已匯入。
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

        var revenue = NextParam(command, AccountCategoryBuiltIns.RevenueRole);
        var windowClauses = string.Join(" OR ", windows.Select(w =>
            $"(g.gl_date >= {NextParam(command, w.FromIso)} AND g.gl_date <= {NextParam(command, w.ToIso)})"));

        return $"""
            (EXISTS (SELECT 1 FROM {schemaPrefix}account_mapping m
                     {CategoryJoin(schemaPrefix, "m", "tm")}
                     WHERE m.account_number = g.account_number AND tm.semantic_role = {revenue})
             AND g.amount_scaled >= 0
             AND ({windowClauses}))
            """;
    }

    /// <summary>
    /// 收入無一般對方科目(revenue_without_normal_counterpart):本列為 Revenue 貸方
    /// (amount_scaled &lt; 0),但其所在傳票無任何「借方側(amount_scaled >= 0)且分類 ∈
    /// {Receivables, Receipt in advance}」的分錄(不含 Cash)。
    /// 和未預期借貸組合(<see cref="UnexpectedAccountPair"/>)不同：對方科目不含現金、0 元的借方也算對方科目，
    /// 也不要求傳票裡有正金額分錄。現銷傳票與沒有正金額分錄的傳票只有這條挑中；應收或預收借方只有 0 元、另有其他正金額分錄時，只有未預期借貸組合挑中。
    /// 需科目配對已匯入。
    /// </summary>
    public string RevenueWithoutNormalCounterpart(
        FilterSqlParameterPlanBuilder command,
        FilterConditionContext context,
        string schemaPrefix = "")
    {
        var revenue = NextParam(command, AccountCategoryBuiltIns.RevenueRole);
        var receivables = NextParam(command, AccountCategoryBuiltIns.ReceivablesRole);
        var receiptInAdvance = NextParam(command, AccountCategoryBuiltIns.ReceiptInAdvanceRole);

        return $"""
            (g.voucher_number IS NOT NULL
             AND EXISTS (SELECT 1 FROM {schemaPrefix}account_mapping m
                     {CategoryJoin(schemaPrefix, "m", "tm")}
                     WHERE m.account_number = g.account_number AND tm.semantic_role = {revenue})
             AND g.amount_scaled < 0
             AND NOT EXISTS (
                 SELECT 1 FROM {schemaPrefix}gl d
                 JOIN {schemaPrefix}account_mapping md ON md.account_number = d.account_number
                 {CategoryJoin(schemaPrefix, "md", "td")}
                 WHERE d.voucher_number = g.voucher_number
                   AND {populationScopePredicate(context, "d")}
                   AND d.amount_scaled >= 0
                   AND td.semantic_role IN ({receivables}, {receiptInAdvance})))
            """;
    }

    /// <summary>
    /// 收入之人工分錄(manual_revenue_entry):科目分類 = Revenue 且 is_manual = 1
    /// (來源未提供人工旗標的列為 NULL,永不匹配,同 manualAuto)。需科目配對已匯入。
    /// </summary>
    public string ManualRevenueEntry(FilterSqlParameterPlanBuilder command, string schemaPrefix = "")
    {
        var revenue = NextParam(command, AccountCategoryBuiltIns.RevenueRole);
        return $"""
            (EXISTS (SELECT 1 FROM {schemaPrefix}account_mapping m
                     {CategoryJoin(schemaPrefix, "m", "tm")}
                     WHERE m.account_number = g.account_number AND tm.semantic_role = {revenue})
             AND g.is_manual = 1)
            """;
    }

    /// <summary>
    /// 科目配對一律以 category_id 連到分類表；兩條寫入路徑都一定寫入 category_id。
    /// 進入科目分類後的所有商業比較一律看 semantic_role，不比較顯示 label。
    /// </summary>
    private static string CategoryJoin(
        string schemaPrefix,
        string mappingAlias,
        string categoryAlias) =>
        $"JOIN {schemaPrefix}account_category {categoryAlias} ON "
        + $"{categoryAlias}.category_id = {mappingAlias}.category_id";

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
    /// (與 legacy @Right(@Str(0,1,0),1) = "0" 對整數 0 命中樣態 "0" 一致)。
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
                // 整數化金額：捨去小數的主單位整數(= @int(ABS(amount)))。
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
    /// 整數部分包含任一串數字：舊版 JET「特定數字篩選」的「包含」。等價於 legacy IDEA 的
    /// <c>@RegExpr(@AllTrim(@Upper(@str(@int(amount),1,0))), "888|28")</c>，不同的是 JET 先取絕對值、輸入當成一般數字比對，
    /// 和整數尾數相同地忽略正負號及小數：整數化金額 ABS(amount_scaled) / scale 轉成文字，包含任一串數字即命中。
    /// 樣態已由 validator 保證為純數字；文字轉換用 VARCHAR(40)（BIGINT 最多 19 位）。
    /// </summary>
    public string IntegerPartContains(
        FilterSqlParameterPlanBuilder command,
        IReadOnlyList<string> patterns,
        int moneyScale,
        string amountColumn = "g.amount_scaled")
    {
        var scale = NextParam(command, (long)moneyScale);
        var integerText = $"CAST({dialect.IntegerQuotient($"ABS({amountColumn})", scale)} AS VARCHAR(40))";
        var clauses = patterns
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => dialect.ContainsIgnoreCase(integerText, NextParam(command, p.Trim())))
            .ToList();

        return clauses.Count == 0 ? "1 = 0" : $"({string.Join(" OR ", clauses)})";
    }

    /// <summary>
    /// 金額尾數連續 N 個 0：預篩選「分錄金額中有連續 0 的尾數」與第五步金額的「尾數連續多個 0」共用。
    /// 就是 N 個 0 的尾數樣態，另加整數部分不是 0。N 至少 2 時尾數樣態本身要求整數至少 N 位，這個下界不改變結果；
    /// N 為 1 時尾數樣態會讓整數部分為 0 的金額命中，由下界排除。
    /// </summary>
    public string ZeroRun(
        FilterSqlParameterPlanBuilder command,
        int zeros,
        int moneyScale,
        string amountColumn = "g.amount_scaled")
    {
        var tail = TrailingDigits(command, [TrailingZeroThreshold.ZeroPattern(zeros)], moneyScale, amountColumn);
        return $"({tail} AND ABS({amountColumn}) >= {NextParam(command, (long)moneyScale)})";
    }

    /// <summary>
    /// 編製與核准同一人(preparer_equals_approver):created_by 與 approved_by 皆非空白
    /// 且(忽略大小寫與前後空白)相等。createdBy/approvedBy 未配對時對應欄為 NULL,零命中。
    /// </summary>
    public string PreparerEqualsApprover() =>
        $"({SqlBlankText.IsNotBlank(dialect, "g.created_by")} " +
        "AND g.approved_by IS NOT NULL " +
        $"AND {dialect.Upper(dialect.Trim("g.created_by"))} = {dialect.Upper(dialect.Trim("g.approved_by"))})";

    /// <summary>
    /// 文字欄位包含任一關鍵字：逐字比對、不分大小寫、關鍵字去頭尾空白，NULL 視為空字串。摘要特定描述、第五步「包含任一文字」
    /// 與舊式自訂關鍵字條件都用這一個方法；沒有可用的關鍵字時不命中。
    /// </summary>
    public string ContainsAnyKeyword(
        FilterSqlParameterPlanBuilder command,
        string columnExpr,
        IReadOnlyList<string> keywords)
    {
        var clauses = keywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => dialect.ContainsIgnoreCase(
                columnExpr, NextParam(command, k.Trim().ToUpperInvariant())))
            .ToList();

        return clauses.Count == 0 ? "1 = 0" : $"({string.Join(" OR ", clauses)})";
    }

    private string TextEqualsAny(
        FilterSqlParameterPlanBuilder command,
        string columnExpr,
        IReadOnlyList<string> keywords)
    {
        var clauses = keywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => $"{dialect.Upper(dialect.Trim($"COALESCE({columnExpr}, '')"))} = {NextParam(command, k.Trim().ToUpperInvariant())}");

        return $"({string.Join(" OR ", clauses)})";
    }

    /// <summary>參數名以現有計畫項目數量遞增，避免跨片段衝突。</summary>
    private static string NextParam(FilterSqlParameterPlanBuilder command, object value) =>
        command.AddValue(value);
}
