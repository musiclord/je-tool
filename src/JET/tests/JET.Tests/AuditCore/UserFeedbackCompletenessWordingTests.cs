using JET.AuditCore;
using Xunit;

namespace JET.Tests.AuditCore;

public sealed class UserFeedbackCompletenessWordingTests
{
    [Fact]
    public void ContinuationWarnings_ExplainTheChecksWithoutInternalPartNames()
    {
        var importControlMismatch = JetAuditProgram.EvaluateCompletenessEligibility(
            new CompletenessEligibilityFacts(
                HasCurrentValidationResult: true,
                IsCurrentLogicVersion: true,
                StoredRowCountMatch: false,
                StoredAmountMatch: true,
                AccountComparisonApplicable: true,
                AccountComparisonDifferenceAccountCount: 0));
        var missingTb = JetAuditProgram.EvaluateCompletenessEligibility(
            new CompletenessEligibilityFacts(
                HasCurrentValidationResult: true,
                IsCurrentLogicVersion: true,
                StoredRowCountMatch: true,
                StoredAmountMatch: true,
                AccountComparisonApplicable: false,
                AccountComparisonDifferenceAccountCount: 0));
        var accountDifference = JetAuditProgram.EvaluateCompletenessEligibility(
            new CompletenessEligibilityFacts(
                HasCurrentValidationResult: true,
                IsCurrentLogicVersion: true,
                StoredRowCountMatch: true,
                StoredAmountMatch: true,
                AccountComparisonApplicable: true,
                AccountComparisonDifferenceAccountCount: 2));
        var withoutStoredTotals = JetAuditProgram.EvaluateCompletenessEligibility(
            new CompletenessEligibilityFacts(
                HasCurrentValidationResult: true,
                IsCurrentLogicVersion: true,
                StoredRowCountMatch: null,
                StoredAmountMatch: null,
                AccountComparisonApplicable: true,
                AccountComparisonDifferenceAccountCount: 0));

        // 提醒照實說明比對對象，並寫出下一步。預期句逐字寫在這裡。
        Assert.Equal(
            // 黃色提示不過度解釋，句子保持簡短。
            // 開頭寫出完整性兩項檢查裡哪一項沒通過。
            "存入筆數與合計核對未通過：存下的分錄筆數或借貸合計與配對時不一致，請重新確認 GL 欄位配對後再驗證。"
            + " 審計員可說明差異原因並繼續篩選與匯出。",
            importControlMismatch.Warning);
        Assert.Contains("TB 欄位配對", missingTb.Warning, StringComparison.Ordinal);
        Assert.Equal("GL 與 TB 逐科目比對未通過：仍有 2 個科目金額不一致。 審計員可說明差異原因並繼續篩選與匯出。",
            accountDifference.Warning);
        // 確認 GL 欄位配對時一定會算出存入筆數與合計這組數字，沒有另一句「無法核對」的提醒。
        // 萬一資料異常缺了這組數字，提醒和核對未通過相同，請審計員重新確認 GL 欄位配對。
        Assert.Equal(importControlMismatch.Warning, withoutStoredTotals.Warning);
        Assert.DoesNotContain("Part A", importControlMismatch.Warning, StringComparison.Ordinal);
        Assert.DoesNotContain("Part B", missingTb.Warning, StringComparison.Ordinal);
        Assert.DoesNotContain("Part B", accountDifference.Warning, StringComparison.Ordinal);
    }
}
