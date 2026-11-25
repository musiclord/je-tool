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
        // 提醒寫出是 JET 存下的 GL 有誤，以及重新確認配對這個下一步。
        Assert.Contains("JET 存下的 GL 分錄", decision.Warning, StringComparison.Ordinal);
        Assert.Contains("「重新確認配對」", decision.Warning, StringComparison.Ordinal);
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
