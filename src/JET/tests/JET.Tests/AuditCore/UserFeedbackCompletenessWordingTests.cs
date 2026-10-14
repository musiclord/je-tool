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
// 使用者 2026-10-13 勾選頁確認改字，預期值換成確認後的句子（第一次失敗：收據 20261013-130645716-968482adb634494a97e2594c7e73decf）。
// 使用者 2026-10-14 要求黃色提示不過度解釋，再依同日確認的清單縮短（第一次失敗：收據 20261014-085536011-fede37aa52f440d68dd459ddba8df94f）。
            "完整性測試未通過：存下的分錄筆數或借貸合計與配對時不一致，請重新確認 GL 欄位配對後再驗證。"
            + " 審計員可說明差異原因並繼續篩選與匯出。",
            importControlMismatch.Warning);
        Assert.Contains("TB 欄位配對", missingTb.Warning, StringComparison.Ordinal);
        Assert.Contains("GL 與 TB", accountDifference.Warning, StringComparison.Ordinal);
        Assert.DoesNotContain("Part A", importControlMismatch.Warning, StringComparison.Ordinal);
        Assert.DoesNotContain("Part B", missingTb.Warning, StringComparison.Ordinal);
        Assert.DoesNotContain("Part B", accountDifference.Warning, StringComparison.Ordinal);
    }
}
