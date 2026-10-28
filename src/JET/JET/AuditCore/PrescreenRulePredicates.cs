using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// 十二條逐筆預篩選規則各對應一個述詞方法（使用者 2026-10-22 裁定「一條規則一個定義」）。
/// 第四步 prescreen.run 的計數、第五步「預篩選」條件的編譯與傳票明細的規則頁都從這裡取述詞，不再各列一份對應。
/// 尾零規則和第五步「整數尾數符合」用同一個 <see cref="GlRulePredicates.TrailingDigits"/>；
/// 編製分錄較少的人員與使用較少的科目和「科目與人員統計」用同一個 <see cref="GlRulePredicates.EntityFrequency"/>，
/// 固定門檻 11 只是參數。
/// </summary>
internal static class PrescreenRulePredicates
{
    internal static string Build(
        GlRulePredicates predicates,
        string key,
        FilterSqlParameterPlanBuilder parameters,
        FilterRuleContext context,
        string schemaPrefix = "")
    {
        ArgumentNullException.ThrowIfNull(predicates);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(context);
        return key switch
        {
            PrescreenRuleKeys.PostPeriodApproval => predicates.PostPeriodApproval(parameters, context.LastPeriodStart!),
            PrescreenRuleKeys.SuspiciousKeywords => predicates.SuspiciousKeywords(parameters),
            PrescreenRuleKeys.UnexpectedAccountPair => predicates.UnexpectedAccountPair(parameters, context, schemaPrefix),
            PrescreenRuleKeys.TrailingZeros => predicates.TrailingDigits(
                parameters, [TrailingZeroThreshold.ZeroPattern(TrailingZeroThreshold.DefaultZerosThreshold)], context.MoneyScale),
            PrescreenRuleKeys.WeekendPosting => predicates.Weekend("g.post_date", context.NonWorkingDays),
            PrescreenRuleKeys.WeekendApproval => predicates.Weekend("g.approval_date", context.NonWorkingDays),
            PrescreenRuleKeys.HolidayPosting => predicates.Holiday("g.post_date", schemaPrefix),
            PrescreenRuleKeys.HolidayApproval => predicates.Holiday("g.approval_date", schemaPrefix),
            PrescreenRuleKeys.BlankDescription => predicates.BlankDescription(),
            PrescreenRuleKeys.NonAuthorizedPreparer => predicates.NonAuthorizedPreparer(schemaPrefix),
            PrescreenRuleKeys.LowFrequencyPreparer => predicates.EntityFrequency(
                parameters, JetFieldCatalog.GlCreateBy, "entries", "lessThanOrEqual",
                PreparerFrequency.DefaultMaxEntries, null, context, schemaPrefix),
            PrescreenRuleKeys.LowFrequencyAccount => predicates.EntityFrequency(
                parameters, JetFieldCatalog.GlAccNum, "entries", "lessThanOrEqual",
                AccountFrequency.DefaultMaxEntries, null, context, schemaPrefix),
            _ => throw new InvalidOperationException($"未處理的預篩選鍵 {key}。")
        };
    }
}
