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
        // 使用者 2026-10-13 勾選頁確認（文字批次 C）：分類名稱寫第四步分類設定裡目前的名稱（這裡沒有帶分類設定，是內建名稱），改鎖確認後的整句。
        // 第一次失敗：收據 20261013-141745059-73ff4afda2d1464bbfbe918459a13666。
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
        var scenario = Batch7SqlSemanticsTests.Scenario(new { type = "prescreen", prescreenKey = "unexpectedAccountPair" });
        Assert.Empty(FilterScenarioValidator.Validate(scenario, new FilterValidationContext(true, true, false,
            HasAnyAccountCategory: true, HasRevenueCategory: true, HasCounterpartCategory: true)));
    }
}
