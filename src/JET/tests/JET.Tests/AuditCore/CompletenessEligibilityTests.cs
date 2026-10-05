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
                hasCurrentValidationRun: true,
                isCurrentLogicVersion: true,
                partARowCountMatch: true,
                partAAmountMatch: true,
                partBApplicable: true,
                partBDifferenceAccountCount: 0));

        Assert.True(decision.IsEligible);
        Assert.Null(decision.Reason);
    }

    [Fact]
    public void Evaluate_WithoutCurrentValidationRun_FailsClosed()
    {
        var decision = JetAuditProgram.EvaluateCompletenessEligibility(
            Facts(hasCurrentValidationRun: false));

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
    public void Evaluate_WhenPartADoesNotMatch_AllowsContinuationWithWarning(
        bool rowCountMatch,
        bool amountMatch)
    {
        var decision = JetAuditProgram.EvaluateCompletenessEligibility(
            Facts(
                partARowCountMatch: rowCountMatch,
                partAAmountMatch: amountMatch));

        Assert.True(decision.IsEligible);
        Assert.Null(decision.Reason);
        // 2026-10-05 V2 裁定：兩組數字都是確認配對時與存下後算的，不是「匯入前後」，提醒照實改寫。
        Assert.Contains("和確認欄位配對時算出的不一致", decision.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_WhenPartBIsNotApplicable_AllowsContinuationWithWarning()
    {
        var decision = JetAuditProgram.EvaluateCompletenessEligibility(
            Facts(partBApplicable: false));

        Assert.True(decision.IsEligible);
        Assert.Null(decision.Reason);
        Assert.Contains("TB 欄位配對", decision.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_WhenPartBHasDifferences_AllowsContinuationWithWarning()
    {
        var decision = JetAuditProgram.EvaluateCompletenessEligibility(
            Facts(partBDifferenceAccountCount: 3));

        Assert.True(decision.IsEligible);
        Assert.Null(decision.Reason);
        Assert.Contains("3", decision.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_WhenRequiredFactsAreMissing_FailsClosed()
    {
        var decision = JetAuditProgram.EvaluateCompletenessEligibility(
            Facts(partARowCountMatch: null));

        Assert.False(decision.IsEligible);
        Assert.Contains("結果不完整", decision.Reason, StringComparison.Ordinal);
    }

    private static CompletenessEligibilityFacts Facts(
        bool hasCurrentValidationRun = true,
        bool isCurrentLogicVersion = true,
        bool? partARowCountMatch = true,
        bool? partAAmountMatch = true,
        bool? partBApplicable = true,
        long? partBDifferenceAccountCount = 0) =>
        new(
            hasCurrentValidationRun,
            isCurrentLogicVersion,
            partARowCountMatch,
            partAAmountMatch,
            partBApplicable,
            partBDifferenceAccountCount);
}
