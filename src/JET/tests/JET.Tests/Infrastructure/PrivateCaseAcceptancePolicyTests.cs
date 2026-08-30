using System.Text.Json;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class PrivateCaseAcceptancePolicyTests
{
    [Fact]
    public void Current_UsesTheThreeApprovedRules()
    {
        var policy = PrivateCaseAcceptancePolicy.Current;

        Assert.Equal(
            PrivateCaseReportComparisonMode.ContentAndAppearance,
            policy.ReportComparison);
        Assert.Equal(
            PrivateCaseInfVerificationMode.RulesAndEffectivePopulation,
            policy.InfVerification);
        Assert.Equal(PrivateCaseCleanupMode.AlwaysDelete, policy.CleanupMode);
        Assert.Equal(59, policy.InfSampleSize);

        var json = JsonSerializer.Serialize(policy);
        Assert.Contains("content-and-appearance", json, StringComparison.Ordinal);
        Assert.Contains("rules-and-effective-population", json, StringComparison.Ordinal);
        Assert.Contains("always-delete", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ContentAndAppearance", json, StringComparison.Ordinal);
        Assert.Equal("private case acceptance policy", policy.ToString());
    }

    [Theory]
    [InlineData(0, 1, 1, 59)]
    [InlineData(1, 0, 1, 59)]
    [InlineData(1, 1, 0, 59)]
    [InlineData(1, 1, 1, 60)]
    public void Create_AnyUnapprovedAlternative_IsRejected(
        int reportComparison,
        int infVerification,
        int cleanupMode,
        int sampleSize)
    {
        var error = Assert.Throws<PrivateCaseAcceptancePolicyException>(() =>
            PrivateCaseAcceptancePolicy.Create(
                (PrivateCaseReportComparisonMode)reportComparison,
                (PrivateCaseInfVerificationMode)infVerification,
                (PrivateCaseCleanupMode)cleanupMode,
                sampleSize));

        Assert.Equal(PrivateCaseAcceptancePolicyFailure.UnsupportedPolicy, error.Failure);
    }
}
