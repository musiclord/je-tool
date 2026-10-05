namespace JET.AuditCore;

/// <summary>
/// 驗證結果可用性的 typed facts。HasCurrentValidationRun 代表目前資料世代仍有
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
    string? Reason,
    string? Warning = null);

/// <summary>
/// 分開判定驗證結果可用性與審計差異。2026-09-17 使用者裁定：審計員可以說明差異並繼續篩選。
/// 只有缺漏、過期或損壞的結果禁止沿用；差異保留為警示，不冒充完整性通過。
/// </summary>
internal static class CompletenessEligibility
{
    private const string MissingCurrentValidationReason =
        "目前資料尚無可用的資料驗證結果，請重新執行資料驗證。";

    private const string StaleValidationReason =
        "目前完整性驗證結果已過期，請重新執行資料驗證。";

    private const string IncompleteValidationReason =
        "目前完整性驗證結果不完整，請重新執行資料驗證。";

    // 2026-10-05 V2：兩組數字都是 JET 在確認配對時與存下後算的，不比對來源檔本身，所以不寫「匯入前後」。
    private const string PartAMismatchReason =
        "完整性測試未通過：JET 存下的分錄筆數或借貸合計，和確認欄位配對時算出的不一致。"
        + "請再確認一次 GL 欄位配對，然後重新驗證。仍不一致時，請輸出支援日誌。";

    private const string PartAUnavailableWarning =
        "無法確認 JET 存下的分錄筆數與借貸合計：這個案件沒有確認欄位配對時算出的數字，通常是舊版建立的案件。"
        + "再確認一次 GL 欄位配對，就會補上這組數字。";

    private const string PartBNotApplicableReason =
        "完整性測試無法執行 GL 與 TB 的逐科目比對：TB 欄位配對尚未確認，補齊後可重新驗證。";

    internal static CompletenessEligibilityDecision Evaluate(CompletenessEligibilityFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (!facts.HasCurrentValidationRun)
        {
            return Ineligible(MissingCurrentValidationReason);
        }

        if ((facts.PartARowCountMatch is null) != (facts.PartAAmountMatch is null)
            || facts.PartBApplicable is null
            || facts.PartBDifferenceAccountCount is null or < 0)
        {
            return Ineligible(IncompleteValidationReason);
        }

        if (!facts.IsCurrentLogicVersion)
        {
            return Ineligible(StaleValidationReason);
        }

        var warnings = new List<string>();
        if (facts.PartARowCountMatch is null && facts.PartAAmountMatch is null)
        {
            warnings.Add(PartAUnavailableWarning);
        }
        else if (facts.PartARowCountMatch is not true || facts.PartAAmountMatch is not true)
        {
            warnings.Add(PartAMismatchReason);
        }

        if (facts.PartBApplicable is not true)
        {
            warnings.Add(PartBNotApplicableReason);
        }

        if (facts.PartBDifferenceAccountCount is not 0)
        {
            warnings.Add(
                $"完整性測試未通過：GL 與 TB 仍有 {facts.PartBDifferenceAccountCount.Value} 個科目金額不一致。");
        }

        return new CompletenessEligibilityDecision(IsEligible: true, Reason: null,
            Warning: warnings.Count == 0 ? null : string.Join(" ", warnings)
                + " 審計員可說明差異原因並繼續篩選與匯出。");
    }

    private static CompletenessEligibilityDecision Ineligible(string reason) =>
        new(IsEligible: false, reason);
}

public static partial class JetAuditProgram
{
    /// <summary>提供驗證結果可用性與審計差異提醒的共用裁定入口。</summary>
    internal static CompletenessEligibilityDecision EvaluateCompletenessEligibility(
        CompletenessEligibilityFacts facts) =>
        CompletenessEligibility.Evaluate(facts);
}
