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
                HasCurrentValidationRun: true,
                IsCurrentLogicVersion: true,
                PartARowCountMatch: false,
                PartAAmountMatch: true,
                PartBApplicable: true,
                PartBDifferenceAccountCount: 0));
        var missingTb = JetAuditProgram.EvaluateCompletenessEligibility(
            new CompletenessEligibilityFacts(
                HasCurrentValidationRun: true,
                IsCurrentLogicVersion: true,
                PartARowCountMatch: true,
                PartAAmountMatch: true,
                PartBApplicable: false,
                PartBDifferenceAccountCount: 0));
        var accountDifference = JetAuditProgram.EvaluateCompletenessEligibility(
            new CompletenessEligibilityFacts(
                HasCurrentValidationRun: true,
                IsCurrentLogicVersion: true,
                PartARowCountMatch: true,
                PartAAmountMatch: true,
                PartBApplicable: true,
                PartBDifferenceAccountCount: 2));

        // 2026-10-05 V2 裁定：照實說明比對對象，並寫出下一步。預期句逐字寫在這裡。
        Assert.Equal(
            "完整性測試未通過：JET 存下的分錄筆數或借貸合計，和確認欄位配對時算出的不一致。"
            + "請再確認一次 GL 欄位配對，然後重新驗證。仍不一致時，請輸出支援日誌。"
            + " 審計員可說明差異原因並繼續篩選與匯出。",
            importControlMismatch.Warning);
        Assert.Contains("TB 欄位配對", missingTb.Warning, StringComparison.Ordinal);
        Assert.Contains("GL 與 TB", accountDifference.Warning, StringComparison.Ordinal);
        Assert.DoesNotContain("Part A", importControlMismatch.Warning, StringComparison.Ordinal);
        Assert.DoesNotContain("Part B", missingTb.Warning, StringComparison.Ordinal);
        Assert.DoesNotContain("Part B", accountDifference.Warning, StringComparison.Ordinal);
    }
}
