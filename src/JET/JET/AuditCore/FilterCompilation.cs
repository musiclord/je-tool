using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// 條件 AST → WHERE 片段的組譯（provider 中立：述詞經 <see cref="GlRulePredicates"/>，
/// 參數經純 ordered plan）。結合律為左折疊累積括號 ((c1 OP c2) OP c3)，
/// 與 manifest 文件化語意一致。SELECT 骨架（COUNT/LIMIT 等）仍由各 provider
/// repository 自寫——本類只負責 WHERE。
/// </summary>
internal sealed partial class GlFilterWhereBuilder(
    ISqlDialect dialect,
    GlRulePredicates predicates)
{
    public FilterSqlFragmentPlan BuildPlan(
        FilterScenarioSpec scenario,
        FilterConditionContext context,
        string schemaPrefix = "",
        bool includePopulationParameters = true,
        bool includeEvidence = false)
    {
        var parameters = new FilterSqlParameterPlanBuilder(
            dialect,
            FilterScenarioLimits.MaxCompiledParameters);
        if (includePopulationParameters)
        {
            GlPopulationScopeSql.AddParameters(parameters, context);
        }
        string? combined = null;
        for (var groupIndex = 0; groupIndex < scenario.Groups.Count; groupIndex++)
        {
            var group = scenario.Groups[groupIndex];
            var groupSql = BuildGroup(parameters, group, groupIndex + 1, context, schemaPrefix);
            combined = combined is null
                ? groupSql
                : $"({combined} {Op(group.Join)} {groupSql})";
        }

        combined ??= "1 = 0";

        // 傳票明細的「本列符合哪些條件」：每條規則各自的述詞，兩值、不推算補分類後的可能性。
        // 整張傳票條件是傳票層條件，其餘都是決定命中列的主要條件。
        var evidence = new List<FilterConditionEvidenceSql>();
        if (includeEvidence)
        {
            for (var gi = 0; gi < scenario.Groups.Count; gi++)
            {
                var group = scenario.Groups[gi];
                for (var ri = 0; ri < group.Conditions.Count; ri++)
                {
                    var rule = group.Conditions[ri];
                    var sql = BuildRule(parameters, rule, context, schemaPrefix);
                    var voucher = rule.IsVoucherCondition;
                    evidence.Add(new(new(gi + 1, ri + 1), voucher, $"({sql})"));
                }
            }
        }
        return parameters.Build(combined) with { EvidencePredicates = evidence };
    }

    private string BuildGroup(
        FilterSqlParameterPlanBuilder parameters,
        FilterGroupSpec group,
        int groupNo,
        FilterConditionContext context,
        string schemaPrefix)
    {
        string? combined = null;
        for (var ruleIndex = 0; ruleIndex < group.Conditions.Count; ruleIndex++)
        {
            var rule = group.Conditions[ruleIndex];
            var ruleSql = BuildLocatedRule(parameters, rule, groupNo, ruleIndex + 1, context, schemaPrefix);
            combined = combined is null
                ? ruleSql
                : $"({combined} {Op(rule.Join)} {ruleSql})";
        }

        return $"({combined ?? "1 = 0"})";
    }

    /// <summary>
    /// 第 N 組第 M 條的條件；編譯時的檢查沒過時把組號、條號記在例外上，供支援日誌使用
    /// （裁定 2026-10-14-01 確認了用字）。例外過濾器只記位置、不攔下例外。
    /// </summary>
    private string BuildLocatedRule(
        FilterSqlParameterPlanBuilder parameters,
        FilterConditionSpec rule,
        int groupNo,
        int ruleNo,
        FilterConditionContext context,
        string schemaPrefix)
    {
        try
        {
            return BuildRule(parameters, rule, context, schemaPrefix);
        }
        catch (JetActionException exception) when (FilterCompileCheck.Locate(exception, groupNo, ruleNo))
        {
            throw;
        }
    }

    private string BuildRule(
        FilterSqlParameterPlanBuilder parameters,
        FilterConditionSpec rule,
        FilterConditionContext context,
        string schemaPrefix) => parameters.GetOrAddFragment("rule", rule,
            () => BuildRuleCore(parameters, rule, context, schemaPrefix));

    private string BuildRuleCore(
        FilterSqlParameterPlanBuilder parameters,
        FilterConditionSpec rule,
        FilterConditionContext context,
        string schemaPrefix)
    {
        switch (rule.Type)
        {
            case FilterConditionType.Group:
                return BuildChildren(parameters, rule.Conditions, context, schemaPrefix);
            case FilterConditionType.Voucher:
                var body = BuildChildren(parameters, rule.Conditions, context, schemaPrefix);
                // 借貸範圍是已拿掉的舊寫法，驗證時就當成無法讀取的內容，走到這裡只會是程式送錯資料。
                if (rule.Side is not null) throw new InvalidOperationException("傳票判斷範圍無效。");
                var population = GlPopulationScopeSql.Predicate(context, "g");
                string Set(string predicate) => $"SELECT g.voucher_number FROM {schemaPrefix}gl g WHERE g.voucher_number IS NOT NULL AND {population} AND ({predicate})";
                // CASE is portable to SQL Server, whose predicates are not scalar booleans.
                var matches = $"CASE WHEN {body} THEN 1 ELSE 0 END = 1";
                var quantified = rule.Quantifier switch
                {
                    "any" => $"(g.voucher_number IN ({Set(matches)}))",
                    "none" => $"(g.voucher_number NOT IN ({Set(matches)}))",
                    "all" => $"(g.voucher_number IN ({Set("1 = 1")}) AND g.voucher_number NOT IN ({Set($"CASE WHEN {body} THEN 1 ELSE 0 END = 0")}))",
                    _ => throw new InvalidOperationException("整張傳票條件無效。")
                };
                // 空白號碼不屬於任何傳票；尤其 NOT IN 空集合不能把它反轉成命中。
                return $"(g.voucher_number IS NOT NULL AND {quantified})";
            case FilterConditionType.FieldValue:
                // 分錄方向是已拿掉的舊寫法，驗證時就當成無法讀取的內容，走到這裡只會是程式送錯資料。
                if (rule.DrCr is not null) throw new InvalidOperationException("欄位值條件不帶借貸方向。");
                return predicates.FieldValue(parameters, rule, context, schemaPrefix);
            case FilterConditionType.AccountSide:
                return predicates.AccountSide(parameters, rule, context, schemaPrefix);
            case FilterConditionType.Prescreen:
                // 十二條預篩選規則的述詞只在 PrescreenRulePredicates 對應一次，第四步計數與這裡同一個方法。
                return PrescreenRulePredicates.Build(predicates, rule.PrescreenKey!, parameters, context, schemaPrefix);

            case FilterConditionType.Text:
                GlFieldWhitelist.TryResolve(rule.Field, out var textColumn);
                return predicates.TextMatch(parameters, textColumn.Column, rule.Keywords, rule.Mode);

            case FilterConditionType.TextSet:
                GlFieldWhitelist.TryResolve(rule.Field, out var textSetColumn);
                var normalizedValues = rule.Values
                    .Select(value => TextSetValueNormalizer.Normalize(value, rule.Normalization))
                    .ToArray();
                return predicates.TextMatch(parameters, textSetColumn.Column, normalizedValues, rule.Mode);

            case FilterConditionType.DateRange:
                GlFieldWhitelist.TryResolve(rule.Field, out var dateColumn);
                return predicates.DateRange(parameters, dateColumn.Column, rule.FromDate, rule.ToDate);

            case FilterConditionType.NumRange:
                return predicates.AmountRange(parameters, rule.FromAmountScaled, rule.ToAmountScaled);

            case FilterConditionType.DrCrOnly:
                return predicates.DrCrOnly(parameters, rule.DrCr!);

            case FilterConditionType.ManualAuto:
                return predicates.ManualAuto(parameters, rule.IsManual!.Value);

            case FilterConditionType.AccountPair:
                // 雙側都是分類身分的多選集合；legacy scalar 由 Domain 投影成單元素集合。
                return predicates.AccountPair(
                    parameters,
                    rule.PairMode!,
                    rule.EffectiveDebitCategoryIds,
                    rule.EffectiveCreditCategoryIds,
                    context,
                    schemaPrefix, rule.CategorySelection);

            case FilterConditionType.SpecialAccountCategoryPair:
                // 考量特殊科目類別配對：顯式雙類別集合 + 否定（drAndCr/drNotCr/notDrCr，否定走述詞內 NOT EXISTS）。
                return predicates.SpecialAccountCategoryPair(
                    parameters,
                    rule.PairMode!,
                    rule.EffectiveDebitCategoryIds,
                    rule.EffectiveCreditCategoryIds,
                    context,
                    schemaPrefix, rule.CategorySelection);

            case FilterConditionType.EntityFrequency:
                return predicates.EntityFrequency(parameters, rule, context, schemaPrefix);

            case FilterConditionType.RevenueDebitNearQuarterEnd:
                // 季末前借記收入：季底視窗由 Domain 純函式自查核期間 + windowDays 算出（識別字不來自使用者）。
                return predicates.RevenueDebitNearQuarterEnd(
                    parameters,
                    QuarterEndWindows.Compute(context.PeriodStart, context.PeriodEnd, rule.WindowDays!.Value),
                    schemaPrefix);

            case FilterConditionType.RevenueWithoutNormalCounterpart:
                // 收入無一般對方科目：unexpected_account_pair 的否定面（不含 Cash 為一般對方科目）。
                return predicates.RevenueWithoutNormalCounterpart(parameters, context, schemaPrefix);

            case FilterConditionType.ManualRevenueEntry:
                // 收入之人工分錄：科目 = Revenue ∧ is_manual = 1。
                return predicates.ManualRevenueEntry(parameters, schemaPrefix);

            case FilterConditionType.PreparerEqualsApprover:
                // 編製與核准同一人：created_by = approved_by（皆非空）。
                return predicates.PreparerEqualsApprover();

            case FilterConditionType.TypedField:
                // typed dynamic rule（2026-08-14 凍結）：fieldId 經 context registry 解析、
                // blank＝missing value row、負向 operator 不納入 blank；值全部參數綁定。
                return predicates.TypedField(parameters, rule, context, schemaPrefix);

            default:
                throw new InvalidOperationException($"未處理的規則型別 {rule.Type}。");
        }
    }

    private string BuildChildren(FilterSqlParameterPlanBuilder parameters, IReadOnlyList<FilterConditionSpec> rules,
        FilterConditionContext context, string schemaPrefix)
    {
        string? combined = null;
        foreach (var child in rules)
        {
            var sql = BuildRule(parameters, child, context, schemaPrefix);
            combined = combined is null ? sql : $"({combined} {Op(child.Join)} {sql})";
        }
        return $"({combined ?? "1 = 0"})";
    }

    private static string Op(FilterJoin join) => join == FilterJoin.Or ? "OR" : "AND";
}

/// <summary>
/// Filter／prescreen／高風險條件彙總共用的 GL 母體 SQL。scope 是 Domain 值；欄位別名只由
/// Infrastructure 常數傳入。期間與過帳政策已在投影形成 <c>is_excluded</c>，下游不得重算。
/// </summary>
internal static class GlPopulationScopeSql
{
    public static FilterSqlFragmentPlan Plan(
        ISqlDialect dialect,
        FilterConditionContext context,
        string tableAlias) =>
        Plan(
            dialect,
            new GlPopulationContext(
                context.PopulationScope,
                context.PeriodStart,
                context.PeriodEnd),
            tableAlias);

    public static FilterSqlFragmentPlan Plan(
        ISqlDialect dialect,
        GlPopulationContext context,
        string tableAlias)
    {
        var parameters = new FilterSqlParameterPlanBuilder(dialect);
        AddParameters(parameters, context);
        return parameters.Build(Predicate(context, tableAlias));
    }

    public static string Predicate(FilterConditionContext context, string tableAlias) =>
        Predicate(new GlPopulationContext(
            context.PopulationScope,
            context.PeriodStart,
            context.PeriodEnd), tableAlias);

    public static string Predicate(GlPopulationContext context, string tableAlias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableAlias);
        ValidateScope(context);
        return GlExcludedPopulation.SqlPredicate(tableAlias);
    }

    public static void AddParameters(
        FilterSqlParameterPlanBuilder parameters,
        FilterConditionContext context) =>
        AddParameters(parameters, new GlPopulationContext(
            context.PopulationScope,
            context.PeriodStart,
            context.PeriodEnd));

    public static void AddParameters(
        FilterSqlParameterPlanBuilder parameters,
        GlPopulationContext context)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ValidateScope(context);
    }

    private static void ValidateScope(GlPopulationContext context)
    {
        if (context.PopulationScope != GlPopulationScope.AuditPeriod)
        {
            throw new ArgumentOutOfRangeException(
                nameof(context), context.PopulationScope, "未知的 GL 母體範圍。");
        }
    }
}

/// <summary>
/// 資料缺漏輔助檢視分頁的 category → WHERE 述詞對映(白名單封閉集合,無任意字串注入)。
/// 空白判定由方言的去空白方法產生，三種資料庫去掉的字元集合和 .NET 相同;
/// approvalDateNotInPeriod 以 @periodStart/@periodEnd 綁參(述詞文字兩 provider 相同);
/// 「核准日不在期間」以核准日 approval_date 判定，不看總帳日期，和舊版 JET 的「Approval date out of period」相同
/// （裁定 2026-06-23-01）。核准日空白的分錄不命中。
/// </summary>
internal static class BlankRecordsCategoryPredicate
{
    /// <summary>四類述詞的固定順序；全部只看納入測試的分錄。</summary>
    public static readonly IReadOnlyList<BlankRecordCategory> All =
    [
        BlankRecordCategory.NullAccount,
        BlankRecordCategory.NullDocument,
        BlankRecordCategory.NullDescription,
        BlankRecordCategory.ApprovalDateNotInPeriod
    ];

    /// <summary>空白判定去掉的字元集合由方言決定（和 .NET Trim 相同），三個 provider 答案一致。</summary>
    public static string For(BlankRecordCategory category, ISqlDialect dialect) => Build(category, dialect);

    public static string Scoped(BlankRecordCategory category, ISqlDialect dialect) =>
        Scope(category, For(category, dialect));

    private static string Build(BlankRecordCategory category, ISqlDialect dialect) => category switch
    {
        BlankRecordCategory.NullAccount => SqlBlankText.IsBlank(dialect, "account_number"),
        BlankRecordCategory.NullDocument => SqlBlankText.IsBlank(dialect, "voucher_number"),
        BlankRecordCategory.NullDescription => SqlBlankText.IsBlank(dialect, "description"),
        BlankRecordCategory.ApprovalDateNotInPeriod =>
            "(approval_date IS NOT NULL AND (approval_date < @periodStart OR approval_date > @periodEnd))",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "未知的 null 紀錄 category。")
    };

    private static string Scope(BlankRecordCategory category, string predicate) =>
        $"({GlExcludedPopulation.SqlPredicate()}) AND ({predicate})";
}
