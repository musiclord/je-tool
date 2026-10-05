using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Domain;

public sealed class Batch7UnexpectedPairPrerequisiteTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void MissingClassificationFacts_RejectUnexpectedPairAndExplainWhatIsMissing(bool imported, bool revenue, bool counterpart)
    {
        var scenario = Batch7SqlSemanticsTests.Scenario(new { type = "prescreen", prescreenKey = "unexpectedAccountPair" });
        var context = new FilterValidationContext(true, imported, false, HasAnyAccountCategory: imported,
            HasRevenueCategory: revenue, HasCounterpartCategory: counterpart);
        var errors = FilterScenarioValidator.Validate(scenario, context);
        Assert.NotEmpty(errors);
        var message = string.Join(" ", errors);
        if (!imported) Assert.Contains("科目配對", message, StringComparison.Ordinal);
        else
        {
            if (!revenue) Assert.Contains("收入", message, StringComparison.Ordinal);
            if (!counterpart)
            {
                Assert.Contains("應收", message, StringComparison.Ordinal);
                Assert.Contains("現金", message, StringComparison.Ordinal);
                Assert.Contains("預收", message, StringComparison.Ordinal);
                Assert.Contains("至少", message, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void RevenueAndAnyCounterpartFact_IsEnough_NoAllThreeCounterpartRequirement()
    {
        var scenario = Batch7SqlSemanticsTests.Scenario(new { type = "prescreen", prescreenKey = "unexpectedAccountPair" });
        Assert.Empty(FilterScenarioValidator.Validate(scenario, new FilterValidationContext(true, true, false,
            HasAnyAccountCategory: true, HasRevenueCategory: true, HasCounterpartCategory: true)));
    }
}
