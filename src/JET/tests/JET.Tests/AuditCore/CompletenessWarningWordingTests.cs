using JET.AuditCore;
using Xunit;

namespace JET.Tests.AuditCore;

/// <summary>
/// 完整性測試沒通過時的提醒文字：寫出是哪一項檢查、比對的對象與下一步，不出現 Part A、Part B 這類內部名稱。
/// </summary>
public sealed class CompletenessWarningWordingTests
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

        // 開頭寫出完整性兩項檢查裡哪一項沒通過；其餘核對提醒寫出是 JET 存下的 GL 有誤，
        // 以及回第三步「GL 欄位配對」按「重新確認配對」、再重新執行驗證。
        // JET 自己存錯時審計員沒有差異原因可說明，所以不接「可說明差異原因並繼續」那句。
        Assert.StartsWith("存入筆數與合計核對未通過：", importControlMismatch.Warning, StringComparison.Ordinal);
        foreach (var expected in new[] { "JET 存下的 GL 分錄", "第三步", "「GL 欄位配對」", "「重新確認配對」", "重新執行驗證" })
        {
            Assert.Contains(expected, importControlMismatch.Warning, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("可說明差異原因", importControlMismatch.Warning, StringComparison.Ordinal);
        Assert.DoesNotContain("與配對時不一致", importControlMismatch.Warning, StringComparison.Ordinal);
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
