namespace JET.AuditCore;

/// <summary>
/// 完整性後端硬閘的 typed facts。HasCurrentValidationRun 代表目前資料世代仍有
/// 未被失效矩陣移除的 validation run；其餘欄位只承接該 run 已保存的 part A／B 結果。
/// </summary>
internal sealed record CompletenessEligibilityFacts(
    bool HasCurrentValidationRun,
    bool IsCurrentLogicVersion,
    bool? PartARowCountMatch,
    bool? PartAAmountMatch,
    bool? PartBApplicable,
    long? PartBDifferenceAccountCount);

internal sealed record CompletenessEligibilityDecision(
    bool IsEligible,
    string? Reason);

/// <summary>
/// 完整性適格性的唯一純裁定。JSON 解析、資料存取與 wire shaping 均留在外層；
/// 任何缺漏或不一致 facts 一律 fail-closed。
/// </summary>
internal static class CompletenessEligibility
{
    private const string MissingCurrentValidationReason =
        "目前資料尚無有效的完整性驗證結果，請先重新執行資料驗證。";

    private const string StaleValidationReason =
        "目前完整性驗證結果已過期，請重新執行資料驗證。";

    private const string IncompleteValidationReason =
        "目前完整性驗證結果不完整，請重新執行資料驗證。";

    private const string PartAMismatchReason =
        "完整性測試 Part A 未通過：來源資料與匯入後資料的總筆數或金額不一致。";

    private const string PartBNotApplicableReason =
        "完整性測試 Part B 無法執行：請先完成 TB 欄位配對並重新執行資料驗證。";

    internal static CompletenessEligibilityDecision Evaluate(CompletenessEligibilityFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (!facts.HasCurrentValidationRun)
        {
            return Ineligible(MissingCurrentValidationReason);
        }

        if (facts.PartARowCountMatch is null
            || facts.PartAAmountMatch is null
            || facts.PartBApplicable is null
            || facts.PartBDifferenceAccountCount is null or < 0)
        {
            return Ineligible(IncompleteValidationReason);
        }

        if (!facts.IsCurrentLogicVersion)
        {
            return Ineligible(StaleValidationReason);
        }

        if (facts.PartARowCountMatch is not true || facts.PartAAmountMatch is not true)
        {
            return Ineligible(PartAMismatchReason);
        }

        if (facts.PartBApplicable is not true)
        {
            return Ineligible(PartBNotApplicableReason);
        }

        if (facts.PartBDifferenceAccountCount is not 0)
        {
            return Ineligible(
                $"完整性測試 Part B 未通過：GL 與 TB 仍有 {facts.PartBDifferenceAccountCount.Value} 個科目差異。");
        }

        return new CompletenessEligibilityDecision(IsEligible: true, Reason: null);
    }

    private static CompletenessEligibilityDecision Ineligible(string reason) =>
        new(IsEligible: false, reason);
}

public static partial class JetAuditProgram
{
    /// <summary>由 facade 暴露完整性適格性的唯一 AuditCore 裁定入口。</summary>
    internal static CompletenessEligibilityDecision EvaluateCompletenessEligibility(
        CompletenessEligibilityFacts facts) =>
        CompletenessEligibility.Evaluate(facts);
}
