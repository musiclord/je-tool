using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class AcceptanceFeedbackDebitCreditTests
{
    public static IEnumerable<object[]> SingleSideCases() =>
        from mode in new[] { GlAmountMode.AmountWithSide, GlAmountMode.AmountWithFlag }
        from debit in new[] { true, false }
        from value in new[] { " S ", "s", "", "\u3000", "OTHER", "0", "1" }
        select new object[] { mode, debit, value, value is " S " or "s" };

    // 使用者 2026-10-08 第 6 則裁定：只填一側時，所有未命中值（含空白）歸另一側。
    [Theory]
    [MemberData(nameof(SingleSideCases))]
    public void OneCode_ValidatesAndUsesTheComplementForEveryOtherValue(
        GlAmountMode mode, bool debit, string value, bool matches)
    {
        var mapping = Mapping();
        mapping[debit ? "dcDebitCode" : "dcCreditCode"] = " S ";
        var spec = new GlMappingSpec(mapping, mode);
        Assert.True(MappingValidator.ValidateGl(spec, Columns).IsValid);
        var row = new StagingRow(7, new Dictionary<string, string>
        {
            ["Doc"] = "SYN-1", ["Date"] = "2025-03-05", ["Account"] = "A",
            ["Name"] = "Synthetic", ["Description"] = "Synthetic entry", ["Amount"] = "-12.34", ["Side"] = value
        });
        Assert.True(GlRowProjector.TryProject(row, spec, 10_000, out var projected, out var error), error?.ToString());
        Assert.Equal(matches == debit ? 123_400L : -123_400L, projected!.AmountScaled);
    }

    [Theory]
    [InlineData(GlAmountMode.AmountWithSide)]
    [InlineData(GlAmountMode.AmountWithFlag)]
    public void NeitherCode_RemainsInvalid(GlAmountMode mode)
    {
        var mapping = Mapping();
        mapping["dcDebitCode"] = " "; mapping["dcCreditCode"] = "\u3000";
        Assert.False(MappingValidator.ValidateGl(new GlMappingSpec(mapping, mode), Columns).IsValid);
    }

    private static readonly string[] Columns = ["Doc", "Date", "Account", "Name", "Description", "Amount", "Side"];
    private static Dictionary<string, string> Mapping() => new()
    {
        ["docNum"] = "Doc", ["postDate"] = "Date", ["accNum"] = "Account", ["accName"] = "Name",
        ["description"] = "Description", ["amount"] = "Amount", ["dcField"] = "Side"
    };
}
