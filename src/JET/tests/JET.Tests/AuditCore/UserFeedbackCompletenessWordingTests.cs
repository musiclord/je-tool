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

        Assert.Contains("匯入前後", importControlMismatch.Warning, StringComparison.Ordinal);
        Assert.Contains("TB 欄位配對", missingTb.Warning, StringComparison.Ordinal);
        Assert.Contains("GL 與 TB", accountDifference.Warning, StringComparison.Ordinal);
        Assert.DoesNotContain("Part A", importControlMismatch.Warning, StringComparison.Ordinal);
        Assert.DoesNotContain("Part B", missingTb.Warning, StringComparison.Ordinal);
        Assert.DoesNotContain("Part B", accountDifference.Warning, StringComparison.Ordinal);
    }
}
