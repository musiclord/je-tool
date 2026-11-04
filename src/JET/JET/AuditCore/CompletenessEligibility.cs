using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// 驗證結果可用性的 typed facts。HasCurrentValidationResult 代表目前資料世代仍有
/// 未被失效矩陣移除的 validation run；其餘欄位只承接該 run 已保存的存入筆數與合計核對、逐科目比對結果。
/// </summary>
internal sealed record CompletenessEligibilityFacts(
    bool HasCurrentValidationResult,
    bool IsCurrentLogicVersion,
    bool? StoredRowCountMatch,
    bool? StoredAmountMatch,
    bool? AccountComparisonApplicable,
    long? AccountComparisonDifferenceAccountCount,
    string? AccountComparisonNaReason = null);

internal sealed record CompletenessEligibilityDecision(
    bool IsEligible,
    string? Reason,
    string? Warning = null);

/// <summary>
/// 分開判定驗證結果可用性與審計差異。裁定 2026-09-17-01：審計員可以說明差異並繼續篩選。
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

    // 兩組數字都是 JET 在確認配對時與存下後算的，不比對來源檔本身，所以不寫「匯入前後」。
    // 用字依裁定 2026-10-13-28：不再請審計員輸出支援日誌。黃色提示不過度解釋（裁定 2026-10-14-02），
    // 依裁定 2026-10-14-01 的句子縮短。
    // 兩項檢查的名稱依裁定 2026-10-29-27，提醒的開頭寫哪一項沒通過，和卡片上的狀態字一致。
    private const string StoredTotalsMismatchReason =
        "存入筆數與合計核對未通過：存下的分錄筆數或借貸合計與配對時不一致，請重新確認 GL 欄位配對後再驗證。";

    internal static CompletenessEligibilityDecision Evaluate(CompletenessEligibilityFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (!facts.HasCurrentValidationResult)
        {
            return Ineligible(MissingCurrentValidationReason);
        }

        if ((facts.StoredRowCountMatch is null) != (facts.StoredAmountMatch is null)
            || facts.AccountComparisonApplicable is null
            || facts.AccountComparisonDifferenceAccountCount is null or < 0)
        {
            return Ineligible(IncompleteValidationReason);
        }

        if (!facts.IsCurrentLogicVersion)
        {
            return Ineligible(StaleValidationReason);
        }

        var warnings = new List<string>();
        // 新案件確認 GL 欄位配對時一定會算出存入筆數與合計；萬一資料異常缺了這組數字，提醒和核對未通過相同，
        // 請審計員重新確認 GL 欄位配對。只為舊案件留下的「無法核對」提醒已拿掉（裁定 2026-11-02-06）。
        if (facts.StoredRowCountMatch is not true || facts.StoredAmountMatch is not true)
        {
            warnings.Add(StoredTotalsMismatchReason);
        }

        // 裁定 2026-10-29-16：沒有 TB 不擋後續步驟，提醒直接沿用逐科目比對無法執行的原因句。
        if (facts.AccountComparisonApplicable is not true)
        {
            warnings.Add(facts.AccountComparisonNaReason ?? AccountComparisonProcedure.MissingTbMappingReason);
        }

        if (facts.AccountComparisonDifferenceAccountCount is not 0)
        {
            warnings.Add(
                $"GL 與 TB 逐科目比對未通過：仍有 {CountText.Format(facts.AccountComparisonDifferenceAccountCount.Value)} 個科目金額不一致。");
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
