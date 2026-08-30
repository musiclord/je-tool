using System.Text.Json;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class PrivateCaseInfVerifierTests
{
    [Fact]
    public void Verify_FiftyNineFromEffectivePopulation_PassesWithoutMemberIdentity()
    {
        var facts = new PrivateCaseInfVerificationFacts(
            RawPopulationRowCount: 100,
            EffectivePopulationRowCount: 80,
            ExcludedPopulationRowCount: 20,
            ExcludedByPeriodRowCount: 15,
            ExcludedByPostingStatusRowCount: 5,
            ReportedSampleSize: 59,
            WalkedSampleRowCount: 59);

        var result = PrivateCaseInfVerifier.Verify(
            facts,
            PrivateCaseAcceptancePolicy.Current);

        Assert.True(result.Passed);
        Assert.True(result.PopulationPartitionMatches);
        Assert.True(result.ExcludedPartitionMatches);
        Assert.True(result.SampleSizeMatchesRule);
        Assert.True(result.EffectivePopulationPageMatchesRun);
        Assert.Equal(59, result.RequiredSampleLimit);
        Assert.Equal("private case INF verification", result.ToString());
    }

    [Fact]
    public void Verify_PopulationSmallerThanLimit_RequiresTheWholeEffectivePopulation()
    {
        var result = PrivateCaseInfVerifier.Verify(
            new PrivateCaseInfVerificationFacts(40, 40, 0, 0, 0, 40, 40),
            PrivateCaseAcceptancePolicy.Current);

        Assert.True(result.Passed);
        Assert.Equal(40, result.ReportedSampleSize);
    }

    [Theory]
    [InlineData(101, 80, 20, 15, 5, 59, 59)]
    [InlineData(100, 80, 20, 16, 5, 59, 59)]
    [InlineData(100, 80, 20, 15, 5, 58, 58)]
    [InlineData(100, 80, 20, 15, 5, 59, 58)]
    public void Verify_RuleOrPopulationMismatch_ReturnsFailedSummary(
        long raw,
        long effective,
        long excluded,
        long byPeriod,
        long byPosting,
        long reportedSample,
        long walkedSample)
    {
        var result = PrivateCaseInfVerifier.Verify(
            new PrivateCaseInfVerificationFacts(
                raw,
                effective,
                excluded,
                byPeriod,
                byPosting,
                reportedSample,
                walkedSample),
            PrivateCaseAcceptancePolicy.Current);

        Assert.False(result.Passed);
    }

    [Fact]
    public void Capture_ReadsOnlyAggregateCountsFromValidationResponse()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "populationSummary": {
                "raw": { "rowCount": 100, "privateValue": "do-not-copy" },
                "effective": { "rowCount": 80 },
                "excluded": {
                  "rowCount": 20,
                  "byPeriodCount": 15,
                  "byPostingStatusCount": 5
                }
              },
              "infSamplingTest": {
                "sampleSize": 59,
                "privateMember": "do-not-copy"
              }
            }
            """);

        var facts = PrivateCaseInfVerificationFacts.Capture(document.RootElement, 59);
        var json = JsonSerializer.Serialize(
            PrivateCaseInfVerifier.Verify(facts, PrivateCaseAcceptancePolicy.Current));

        Assert.DoesNotContain("do-not-copy", json, StringComparison.Ordinal);
        Assert.DoesNotContain("privateMember", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_NegativeEvidence_IsRejected()
    {
        var error = Assert.Throws<PrivateCaseInfVerificationException>(() =>
            PrivateCaseInfVerifier.Verify(
                new PrivateCaseInfVerificationFacts(-1, 0, 0, 0, 0, 0, 0),
                PrivateCaseAcceptancePolicy.Current));

        Assert.Equal(PrivateCaseInfVerificationFailure.InvalidEvidence, error.Failure);
    }
}
