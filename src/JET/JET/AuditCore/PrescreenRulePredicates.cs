using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// 十條逐筆預篩選規則各對應一個述詞方法，一條規則一個定義（裁定 2026-10-22-13）。
/// 第四步 prescreen.run 的計數、第五步「預篩選」條件的編譯與傳票明細的規則頁都從這裡取述詞，不再各列一份對應。
/// 尾零規則和第五步金額的「尾數連續多個 0」用同一個 <see cref="GlRulePredicates.ZeroRun"/>，0 的個數由 GL 算出（裁定 2026-11-05-25）。
/// </summary>
internal static class PrescreenRulePredicates
{
    internal static string Build(
        GlRulePredicates predicates,
        string key,
        FilterSqlParameterPlanBuilder parameters,
        FilterConditionContext context,
        string schemaPrefix = "")
    {
        ArgumentNullException.ThrowIfNull(predicates);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(context);
        return key switch
        {
            PrescreenRuleKeys.PrepPeriodApproval => predicates.PrepPeriodApproval(parameters, context.PrepStartDate!),
            PrescreenRuleKeys.SuspiciousKeywords => predicates.SuspiciousKeywords(parameters),
            PrescreenRuleKeys.UnexpectedAccountPair => predicates.UnexpectedAccountPair(parameters, context, schemaPrefix),
            PrescreenRuleKeys.TrailingZeros => predicates.ZeroRun(
                parameters,
                context.ZerosThreshold ?? throw new InvalidOperationException(
                    "預篩選尾數規則缺少 0 的個數：條件環境沒有帶入已確認 GL 配對的控制總數。"),
                context.MoneyScale),
            PrescreenRuleKeys.WeekendPosting => predicates.Weekend("g.gl_date", context.NonWorkingDays),
            PrescreenRuleKeys.WeekendApproval => predicates.Weekend("g.approval_date", context.NonWorkingDays),
            PrescreenRuleKeys.HolidayPosting => predicates.Holiday("g.gl_date", schemaPrefix),
            PrescreenRuleKeys.HolidayApproval => predicates.Holiday("g.approval_date", schemaPrefix),
            PrescreenRuleKeys.BlankDescription => predicates.BlankDescription(),
            PrescreenRuleKeys.NonAuthorizedPreparer => predicates.NonAuthorizedPreparer(schemaPrefix),
            _ => throw new InvalidOperationException($"未處理的預篩選鍵 {key}。")
        };
    }
}
