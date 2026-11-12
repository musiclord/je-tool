using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Domain;

public sealed class UnexpectedPairPrerequisiteTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void MissingClassificationFacts_RejectUnexpectedPairAndExplainWhatIsMissing(bool imported, bool revenue, bool counterpart)
    {
        var scenario = VoucherConditionSqlSemanticsTests.Scenario(new { type = "prescreen", prescreenKey = "unexpectedAccountPair" });
        var context = new FilterValidationContext(true, imported, false, HasAnyAccountCategory: imported,
            HasRevenueCategory: revenue, HasCounterpartCategory: counterpart);
        var errors = FilterScenarioValidator.Validate(scenario, context);
        Assert.NotEmpty(errors);
        // 分類名稱寫第四步分類設定裡目前的名稱（這裡沒有帶分類設定，是內建名稱）；這句的用字已定案，核對整句。
        if (!imported) Assert.Contains("第 1 組第 1 條：科目配對中尚無任何科目設定分類，請先在第四步設定", errors);
        else
        {
            if (!revenue) Assert.Contains("第 1 組第 1 條：科目配對中尚無歸類為「Revenue」的科目，請先在第四步設定", errors);
            if (!counterpart)
            {
                Assert.Contains(
                    "第 1 組第 1 條：科目配對中尚無歸類為「Receivables」「Cash」或「Receipt in advance」的科目，請先在第四步設定",
                    errors);
            }
        }
    }

    [Fact]
    public void RevenueAndAnyCounterpartFact_IsEnough_NoAllThreeCounterpartRequirement()
    {
        var scenario = VoucherConditionSqlSemanticsTests.Scenario(new { type = "prescreen", prescreenKey = "unexpectedAccountPair" });
        Assert.Empty(FilterScenarioValidator.Validate(scenario, new FilterValidationContext(true, true, false,
            HasAnyAccountCategory: true, HasRevenueCategory: true, HasCounterpartCategory: true)));
    }
}
