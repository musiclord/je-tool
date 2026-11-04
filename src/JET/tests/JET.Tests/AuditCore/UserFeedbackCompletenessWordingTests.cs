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

        // 裁定 2026-10-05-01：照實說明比對對象，並寫出下一步。預期句逐字寫在這裡。
        Assert.Equal(
// 預期值換成裁定 2026-10-13-28 的句子（第一次失敗：收據 20261013-130645716-968482adb634494a97e2594c7e73decf）。
// 依裁定 2026-10-14-02，黃色提示不過度解釋，再照確認過的清單縮短（第一次失敗：收據 20261014-085536011-fede37aa52f440d68dd459ddba8df94f）。
// 完整性兩項檢查的名稱依裁定 2026-10-29-27，開頭泛稱的「完整性測試未通過」改寫哪一項沒通過，其餘字不動
// （第一次失敗：Focused 收據 20261031-112700448-a313cefdedd0468a92245a58276cff47）。
            "存入筆數與合計核對未通過：存下的分錄筆數或借貸合計與配對時不一致，請重新確認 GL 欄位配對後再驗證。"
            + " 審計員可說明差異原因並繼續篩選與匯出。",
            importControlMismatch.Warning);
        Assert.Contains("TB 欄位配對", missingTb.Warning, StringComparison.Ordinal);
        Assert.Equal("GL 與 TB 逐科目比對未通過：仍有 2 個科目金額不一致。 審計員可說明差異原因並繼續篩選與匯出。",
            accountDifference.Warning);
        // 裁定 2026-11-02-06 拿掉只為舊案件留下的「存入筆數與合計無法核對」提醒：新案件確認 GL 欄位配對時一定會算出這組數字。
        // 萬一資料異常缺了這組數字，提醒和核對未通過相同，請審計員重新確認 GL 欄位配對。
        // 原本斷言開頭是「存入筆數與合計無法核對：」；第一次失敗：Focused 收據 20261102-043719389-c2add9665b2b41e5ad3de35fbda4b8bd。
        Assert.Equal(importControlMismatch.Warning, withoutStoredTotals.Warning);
        Assert.DoesNotContain("Part A", importControlMismatch.Warning, StringComparison.Ordinal);
        Assert.DoesNotContain("Part B", missingTb.Warning, StringComparison.Ordinal);
        Assert.DoesNotContain("Part B", accountDifference.Warning, StringComparison.Ordinal);
    }
}
