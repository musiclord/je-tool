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
        FilterRuleContext context,
        long zeroModulus,
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
        foreach (var group in scenario.Groups)
        {
            var groupSql = BuildGroup(parameters, group, context, zeroModulus, schemaPrefix);
            combined = combined is null
                ? groupSql
                : $"({combined} {Op(group.Join)} {groupSql})";
        }

        combined ??= "1 = 0";

        // 傳票明細的「本列符合哪些條件」：每條規則各自的述詞，兩值、不推算補分類後的可能性。
        // sameVoucher 群組的第一條是決定命中列的主要條件，其餘是同傳票佐證；absent 模式是傳票層條件。
        var evidence = new List<FilterRuleEvidenceSql>();
        if (includeEvidence)
        {
            for (var gi = 0; gi < scenario.Groups.Count; gi++)
            {
                var group = scenario.Groups[gi];
                for (var ri = 0; ri < group.Rules.Count; ri++)
                {
                    var rule = group.Rules[ri];
                    var sql = BuildRule(parameters, rule, context, zeroModulus, schemaPrefix);
                    var voucher = rule.IsVoucherCondition;
                    evidence.Add(new(new(gi + 1, ri + 1), group.MatchScope != FilterGroupMatchScope.SameVoucher || ri == 0,
                        voucher, $"({sql})"));
                }
            }
        }
        return parameters.Build(combined) with { EvidencePredicates = evidence };
    }

    private string BuildGroup(
        FilterSqlParameterPlanBuilder parameters,
        FilterGroupSpec group,
        FilterRuleContext context,
        long zeroModulus,
        string schemaPrefix)
    {
        if (group.MatchScope == FilterGroupMatchScope.SameVoucher)
        {
            return BuildSameVoucherGroup(parameters, group, context, zeroModulus, schemaPrefix);
        }

        string? combined = null;
        foreach (var rule in group.Rules)
        {
            var ruleSql = BuildRule(parameters, rule, context, zeroModulus, schemaPrefix);
            combined = combined is null
                ? ruleSql
                : $"({combined} {Op(rule.Join)} {ruleSql})";
        }

        return $"({combined ?? "1 = 0"})";
    }

    /// <summary>
    /// Legacy 多條件群組：第一條規則只決定哪些外層 g 列是命中錨點；後續規則各自形成
    /// 查核期間內的傳票號碼集合，再以 IN 半連接要求錨點傳票存在於每個集合。IN 不展開
    /// 佐證列，因此一張傳票有多筆佐證時也不會複製錨點。子查詢刻意重用 alias g，讓既有
    /// provider-neutral rule predicates 原封不動在子查詢 scope 內評估；使用者值仍全部參數化。
    /// </summary>
    private string BuildSameVoucherGroup(
        FilterSqlParameterPlanBuilder parameters,
        FilterGroupSpec group,
        FilterRuleContext context,
        long zeroModulus,
        string schemaPrefix)
    {
        if (group.Rules.Count == 0)
        {
            return "(1 = 0)";
        }

        var combined = BuildRule(parameters, group.Rules[0], context, zeroModulus, schemaPrefix);
        for (var index = 1; index < group.Rules.Count; index++)
        {
            var evidence = BuildRule(
                parameters,
                group.Rules[index],
                context,
                zeroModulus,
                schemaPrefix);
            var voucherSet = $"g.document_number IN ("
                + $"SELECT g.document_number FROM {schemaPrefix}target_gl_entry g "
                + $"WHERE {GlPopulationScopeSql.Predicate(context, "g")} AND ({evidence}))";
            combined = $"({combined} AND {voucherSet})";
        }

        return $"({combined})";
    }

    private string BuildRule(
        FilterSqlParameterPlanBuilder parameters,
        FilterRuleSpec rule,
        FilterRuleContext context,
        long zeroModulus,
        string schemaPrefix) => parameters.GetOrAddFragment("rule", rule,
            () => BuildRuleCore(parameters, rule, context, zeroModulus, schemaPrefix));

    private string BuildRuleCore(
        FilterSqlParameterPlanBuilder parameters,
        FilterRuleSpec rule,
        FilterRuleContext context,
        long zeroModulus,
        string schemaPrefix)
    {
        switch (rule.Type)
        {
            case FilterRuleType.Group:
                return BuildChildren(parameters, rule.Rules, context, zeroModulus, schemaPrefix);
            case FilterRuleType.Voucher:
                var body = BuildChildren(parameters, rule.Rules, context, zeroModulus, schemaPrefix);
                var side = rule.Side switch { "debit" => "g.amount_scaled >= 0", "credit" => "g.amount_scaled < 0", "all" => "1 = 1", _ => throw new InvalidOperationException("傳票判斷範圍無效。") };
                var population = $"{GlPopulationScopeSql.Predicate(context, "g")} AND {side}";
                string Set(string predicate) => $"SELECT g.document_number FROM {schemaPrefix}target_gl_entry g WHERE g.document_number IS NOT NULL AND {population} AND ({predicate})";
                // CASE is portable to SQL Server, whose predicates are not scalar booleans.
                var matches = $"CASE WHEN {body} THEN 1 ELSE 0 END = 1";
                var quantified = rule.Quantifier switch
                {
                    "any" => $"(g.document_number IN ({Set(matches)}))",
                    "none" => $"(g.document_number NOT IN ({Set(matches)}))",
                    "all" => $"(g.document_number IN ({Set("1 = 1")}) AND g.document_number NOT IN ({Set($"CASE WHEN {body} THEN 1 ELSE 0 END = 0")}))",
                    _ => throw new InvalidOperationException("傳票分錄條件無效。")
                };
                // 空白號碼不屬於任何傳票；尤其 NOT IN 空集合不能把它反轉成命中。
                return $"(g.document_number IS NOT NULL AND {quantified})";
            case FilterRuleType.FieldValue:
                var fieldValue = predicates.FieldValue(parameters, rule, context, schemaPrefix);
                return rule.DrCr is null ? fieldValue
                    : $"({predicates.DrCrOnly(parameters, rule.DrCr)} AND ({fieldValue}))";
            case FilterRuleType.AccountSide:
                return predicates.AccountSide(parameters, rule, context, schemaPrefix);
            case FilterRuleType.Prescreen:
                return BuildPrescreenRule(parameters, rule.PrescreenKey!, context, zeroModulus, schemaPrefix);

            case FilterRuleType.Text:
                GlFieldWhitelist.TryResolve(rule.Field, out var textColumn);
                return predicates.TextMatch(parameters, textColumn.Column, rule.Keywords, rule.Mode);

            case FilterRuleType.TextSet:
                GlFieldWhitelist.TryResolve(rule.Field, out var textSetColumn);
                var normalizedValues = rule.Values
                    .Select(value => TextSetValueNormalizer.Normalize(value, rule.Normalization))
                    .ToArray();
                return predicates.TextMatch(parameters, textSetColumn.Column, normalizedValues, rule.Mode);

            case FilterRuleType.DateRange:
                GlFieldWhitelist.TryResolve(rule.Field, out var dateColumn);
                return predicates.DateRange(parameters, dateColumn.Column, rule.FromDate, rule.ToDate);

            case FilterRuleType.NumRange:
                return predicates.AmountRange(parameters, rule.FromAmountScaled, rule.ToAmountScaled);

            case FilterRuleType.DrCrOnly:
                return predicates.DrCrOnly(parameters, rule.DrCr!);

            case FilterRuleType.ManualAuto:
                return predicates.ManualAuto(parameters, rule.IsManual!.Value);

            case FilterRuleType.AccountPair:
                // 雙側都是分類身分的多選集合；legacy scalar 由 Domain 投影成單元素集合。
                return predicates.AccountPair(
                    parameters,
                    rule.PairMode!,
                    rule.EffectiveDebitCategoryIds,
                    rule.EffectiveCreditCategoryIds,
                    context,
                    schemaPrefix, rule.CategorySelection);

            case FilterRuleType.SpecialAccountCategoryPair:
                // 考量特殊科目類別配對：顯式雙類別集合 + 否定（drAndCr/drNotCr/notDrCr，否定走述詞內 NOT EXISTS）。
                return predicates.SpecialAccountCategoryPair(
                    parameters,
                    rule.PairMode!,
                    rule.EffectiveDebitCategoryIds,
                    rule.EffectiveCreditCategoryIds,
                    context,
                    schemaPrefix, rule.CategorySelection);

            case FilterRuleType.CustomKeywords:
                return predicates.CustomKeywords(parameters, rule.Keywords);

            case FilterRuleType.CustomTrailingZeros:
                // 固定位數取代動態門檻（原 A4 語意）；先取主單位整數，再以 10^N 取模。
                return predicates.TrailingZeros(
                    parameters,
                    TrailingZeroThreshold.UnitModulus(rule.Digits!.Value),
                    context.MoneyScale);

            case FilterRuleType.CustomPreparerEntryCount:
                // 自訂低頻編製者門檻：maxEntries 取代固定預設 11，述詞同 lowFrequencyPreparer。
                return predicates.LowFrequencyPreparer(
                    parameters, rule.MaxEntries!.Value, context, schemaPrefix);

            case FilterRuleType.EntityFrequency:
                return predicates.EntityFrequency(parameters, rule, context, schemaPrefix);

            case FilterRuleType.CustomAccountEntryCount:
                // 自訂低頻科目門檻（C9 自訂軌）：maxEntries 取代固定預設 11，述詞同 lowFrequencyAccount。
                return predicates.LowFrequencyAccount(
                    parameters, rule.MaxEntries!.Value, context, schemaPrefix);

            case FilterRuleType.RevenueDebitNearQuarterEnd:
                // KCT 清單 A：季底視窗由 Domain 純函式自查核期間 + windowDays 算出（識別字不來自使用者）。
                return predicates.RevenueDebitNearQuarterEnd(
                    parameters,
                    QuarterEndWindows.Compute(context.PeriodStart, context.PeriodEnd, rule.WindowDays!.Value),
                    schemaPrefix);

            case FilterRuleType.RevenueWithoutNormalCounterpart:
                // KCT 清單 C：unexpected_account_pair 的否定面（不含 Cash 為一般對方科目）。
                return predicates.RevenueWithoutNormalCounterpart(parameters, context, schemaPrefix);

            case FilterRuleType.ManualRevenueEntry:
                // KCT 清單 D：科目 = Revenue ∧ is_manual = 1。
                return predicates.ManualRevenueEntry(parameters, schemaPrefix);

            case FilterRuleType.TrailingDigits:
                // KCT 清單 H：尾數樣態重用 Keywords；顯示金額主單位整數尾數比對。
                return predicates.TrailingDigits(parameters, rule.Keywords, context.MoneyScale);

            case FilterRuleType.PreparerEqualsApprover:
                // KCT 清單 J：created_by = approved_by（皆非空）。
                return predicates.PreparerEqualsApprover();

            case FilterRuleType.TypedField:
                // typed dynamic rule（2026-08-14 凍結）：fieldId 經 context registry 解析、
                // blank＝missing value row、負向 operator 不納入 blank；值全部參數綁定。
                return predicates.TypedField(parameters, rule, context, schemaPrefix);

            default:
                throw new InvalidOperationException($"未處理的規則型別 {rule.Type}。");
        }
    }

    private string BuildChildren(FilterSqlParameterPlanBuilder parameters, IReadOnlyList<FilterRuleSpec> rules,
        FilterRuleContext context, long zeroModulus, string schemaPrefix)
    {
        string? combined = null;
        foreach (var child in rules)
        {
            var sql = BuildRule(parameters, child, context, zeroModulus, schemaPrefix);
            combined = combined is null ? sql : $"({combined} {Op(child.Join)} {sql})";
        }
        return $"({combined ?? "1 = 0"})";
    }

    private string BuildPrescreenRule(
        FilterSqlParameterPlanBuilder parameters,
        string prescreenKey,
        FilterRuleContext context,
        long zeroModulus,
        string schemaPrefix)
    {
        return prescreenKey switch
        {
            PrescreenRuleKeys.PostPeriodApproval => predicates.PostPeriodApproval(parameters, context.LastPeriodStart!),
            PrescreenRuleKeys.SuspiciousKeywords => predicates.SuspiciousKeywords(parameters),
            PrescreenRuleKeys.UnexpectedAccountPair => predicates.UnexpectedAccountPair(parameters, context, schemaPrefix),
            PrescreenRuleKeys.TrailingZeros => predicates.TrailingZeros(parameters, zeroModulus, context.MoneyScale),
            PrescreenRuleKeys.WeekendPosting => predicates.Weekend("post_date", context.NonWorkingDays, schemaPrefix),
            PrescreenRuleKeys.WeekendApproval => predicates.Weekend("approval_date", context.NonWorkingDays, schemaPrefix),
            PrescreenRuleKeys.HolidayPosting => predicates.Holiday("post_date", schemaPrefix),
            PrescreenRuleKeys.HolidayApproval => predicates.Holiday("approval_date", schemaPrefix),
            PrescreenRuleKeys.BlankDescription => predicates.BlankDescription(),
            PrescreenRuleKeys.BackdatedPosting => predicates.Backdated(),
            PrescreenRuleKeys.NonAuthorizedPreparer => predicates.NonAuthorizedPreparer(schemaPrefix),
            PrescreenRuleKeys.LowFrequencyPreparer =>
                predicates.LowFrequencyPreparer(
                    parameters, PreparerFrequency.DefaultMaxEntries, context, schemaPrefix),
            PrescreenRuleKeys.LowFrequencyAccount =>
                predicates.LowFrequencyAccount(
                    parameters, AccountFrequency.DefaultMaxEntries, context, schemaPrefix),
            _ => throw new InvalidOperationException($"未處理的預篩選鍵 {prescreenKey}。")
        };
    }

    private static string Op(FilterJoin join) => join == FilterJoin.Or ? "OR" : "AND";
}

/// <summary>
/// Filter／prescreen／tag-matrix 共用的 GL 母體 SQL。scope 是 Domain 值；欄位別名只由
/// Infrastructure 常數傳入。期間與過帳政策已在投影形成 <c>is_effective</c>，下游不得重算。
/// </summary>
internal static class GlPopulationScopeSql
{
    public static FilterSqlFragmentPlan Plan(
        ISqlDialect dialect,
        FilterRuleContext context,
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

    public static string Predicate(FilterRuleContext context, string tableAlias) =>
        Predicate(new GlPopulationContext(
            context.PopulationScope,
            context.PeriodStart,
            context.PeriodEnd), tableAlias);

    public static string Predicate(GlPopulationContext context, string tableAlias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableAlias);
        ValidateScope(context);
        return GlEffectivePopulation.SqlPredicate(tableAlias);
    }

    public static void AddParameters(
        FilterSqlParameterPlanBuilder parameters,
        FilterRuleContext context) =>
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
/// 空值/期外日期紀錄分頁的 category → WHERE 述詞對映(白名單封閉集合,無任意字串注入)。
/// 兩 provider 僅空白判定不同:SQLite <c>TRIM(x)=''</c>、SQL Server <c>LTRIM(RTRIM(x))=''</c>;
/// outOfRangeDate 以 @periodStart/@periodEnd 綁參(述詞文字兩 provider 相同);
/// 「日期區間外」以**核准日 approval_date**判定(2026-06-23 決策,對齊舊 JET 工具的
/// 「Approval date out of period」;非過帳日)。核准日未配對(NULL)則不命中。
/// </summary>
internal static class NullRecordsCategoryPredicate
{
    /// <summary>四類述詞的固定順序；全部限有效母體。</summary>
    public static readonly IReadOnlyList<NullRecordCategory> All =
    [
        NullRecordCategory.NullAccount,
        NullRecordCategory.NullDocument,
        NullRecordCategory.NullDescription,
        NullRecordCategory.OutOfRangeDate
    ];

    /// <summary>空白判定去掉的字元集合由方言決定（和 .NET Trim 相同），三個 provider 答案一致。</summary>
    public static string For(NullRecordCategory category, ISqlDialect dialect) => Build(category, dialect.Trim);

    public static string Scoped(NullRecordCategory category, ISqlDialect dialect) =>
        Scope(category, For(category, dialect));

    private static string Build(NullRecordCategory category, Func<string, string> trim) => category switch
    {
        NullRecordCategory.NullAccount =>
            $"(account_code IS NULL OR {trim("account_code")} = '')",
        NullRecordCategory.NullDocument =>
            $"(document_number IS NULL OR {trim("document_number")} = '')",
        NullRecordCategory.NullDescription =>
            $"(document_description IS NULL OR {trim("document_description")} = '')",
        NullRecordCategory.OutOfRangeDate =>
            "(approval_date IS NOT NULL AND (approval_date < @periodStart OR approval_date > @periodEnd))",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "未知的 null 紀錄 category。")
    };

    private static string Scope(NullRecordCategory category, string predicate) =>
        $"({GlEffectivePopulation.SqlPredicate()}) AND ({predicate})";
}
