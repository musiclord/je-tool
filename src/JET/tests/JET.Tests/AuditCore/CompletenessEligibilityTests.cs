using JET.AuditCore;
using Xunit;

namespace JET.Tests.AuditCore;

public sealed class CompletenessEligibilityTests
{
    [Fact]
    public void Evaluate_AllRequiredFactsPass_ReturnsEligible()
    {
        var decision = JetAuditProgram.EvaluateCompletenessEligibility(
            Facts(
                hasCurrentValidationResult: true,
                isCurrentLogicVersion: true,
                storedRowCountMatch: true,
                storedAmountMatch: true,
                accountComparisonApplicable: true,
                accountComparisonDifferenceAccountCount: 0));

        Assert.True(decision.IsEligible);
        Assert.Null(decision.Reason);
    }

    [Fact]
    public void Evaluate_WithoutCurrentValidationResult_FailsClosed()
    {
        var decision = JetAuditProgram.EvaluateCompletenessEligibility(
            Facts(hasCurrentValidationResult: false));

        Assert.False(decision.IsEligible);
        Assert.Contains("目前資料", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_WithStaleLogicVersion_FailsClosed()
    {
        var decision = JetAuditProgram.EvaluateCompletenessEligibility(
            Facts(isCurrentLogicVersion: false));

        Assert.False(decision.IsEligible);
        Assert.Contains("重新執行資料驗證", decision.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Evaluate_WhenStoredTotalsDoesNotMatch_AllowsContinuationWithWarning(
        bool rowCountMatch,
        bool amountMatch)
    {
        var decision = JetAuditProgram.EvaluateCompletenessEligibility(
            Facts(
                storedRowCountMatch: rowCountMatch,
                storedAmountMatch: amountMatch));

        Assert.True(decision.IsEligible);
        Assert.Null(decision.Reason);
        // 裁定 2026-10-05-01：兩組數字都是確認配對時與存下後算的，不是「匯入前後」，提醒照實改寫。
        // 裁定 2026-10-14-02 要求黃色提示不過度解釋，句子照確認過的清單縮短（第一次失敗：收據 20261014-085536011-fede37aa52f440d68dd459ddba8df94f）。
        Assert.Contains("存下的分錄筆數或借貸合計與配對時不一致", decision.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_WhenAccountComparisonIsNotApplicable_AllowsContinuationWithWarning()
    {
        var decision = JetAuditProgram.EvaluateCompletenessEligibility(
            Facts(accountComparisonApplicable: false));

        Assert.True(decision.IsEligible);
        Assert.Null(decision.Reason);
        Assert.Contains("TB 欄位配對", decision.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_WhenAccountComparisonHasDifferences_AllowsContinuationWithWarning()
    {
        var decision = JetAuditProgram.EvaluateCompletenessEligibility(
            Facts(accountComparisonDifferenceAccountCount: 3));

        Assert.True(decision.IsEligible);
        Assert.Null(decision.Reason);
        Assert.Contains("3", decision.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_WhenRequiredFactsAreMissing_FailsClosed()
    {
        var decision = JetAuditProgram.EvaluateCompletenessEligibility(
            Facts(storedRowCountMatch: null));

        Assert.False(decision.IsEligible);
        Assert.Contains("結果不完整", decision.Reason, StringComparison.Ordinal);
    }

    private static CompletenessEligibilityFacts Facts(
        bool hasCurrentValidationResult = true,
        bool isCurrentLogicVersion = true,
        bool? storedRowCountMatch = true,
        bool? storedAmountMatch = true,
        bool? accountComparisonApplicable = true,
        long? accountComparisonDifferenceAccountCount = 0) =>
        new(
            hasCurrentValidationResult,
            isCurrentLogicVersion,
            storedRowCountMatch,
            storedAmountMatch,
            accountComparisonApplicable,
            accountComparisonDifferenceAccountCount);
}
