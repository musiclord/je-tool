using JET.AuditCore;
using Xunit;

namespace JET.Tests.AuditCore;

public sealed class UserFeedbackCompletenessWordingTests
{
    [Fact]
    public void IneligibleReasons_ExplainTheChecksWithoutInternalPartNames()
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

        Assert.Contains("匯入前後", importControlMismatch.Reason, StringComparison.Ordinal);
        Assert.Contains("TB 欄位配對", missingTb.Reason, StringComparison.Ordinal);
        Assert.Contains("GL 與 TB", accountDifference.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Part A", importControlMismatch.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Part B", missingTb.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Part B", accountDifference.Reason, StringComparison.Ordinal);
    }
}
