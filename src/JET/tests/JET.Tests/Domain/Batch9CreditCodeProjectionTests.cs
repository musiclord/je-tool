using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class Batch9CreditCodeProjectionTests
{
    [Theory]
    [InlineData(GlAmountMode.AmountWithSide)]
    [InlineData(GlAmountMode.AmountWithFlag)]
    public void SideAndFlag_MustExplicitlyRequireCreditCode(GlAmountMode mode)
    {
        var mapping = Mapping();
        mapping.Remove("dcCreditCode");
        var result = MappingValidator.ValidateGl(new GlMappingSpec(mapping, mode), Columns);
        Assert.Equal(new[] { "dcCreditCode" }, result.MissingRequiredKeys);
        Assert.Empty(result.UnknownColumns);
    }

    [Theory]
    [InlineData(GlAmountMode.AmountWithSide)]
    [InlineData(GlAmountMode.AmountWithFlag)]
    public void CreditCode_IsALiteralRatherThanASourceColumn(GlAmountMode mode)
    {
        var result = MappingValidator.ValidateGl(new GlMappingSpec(Mapping(), mode), Columns);
        Assert.Empty(result.MissingRequiredKeys);
        Assert.Empty(result.UnknownColumns);
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(GlAmountMode.AmountWithSide, " D ", 10_000L)]
    [InlineData(GlAmountMode.AmountWithSide, "\u3000d\u00a0", 10_000L)]
    [InlineData(GlAmountMode.AmountWithSide, " C ", -10_000L)]
    [InlineData(GlAmountMode.AmountWithSide, "c", -10_000L)]
    [InlineData(GlAmountMode.AmountWithFlag, " D ", 10_000L)]
    [InlineData(GlAmountMode.AmountWithFlag, "c", -10_000L)]
    public void KnownSideCodes_CompareTrimmedAndCaseInsensitively(GlAmountMode mode, string code, long expected)
    {
        var row = Row(code, "-1.00");
        Assert.True(GlRowProjector.TryProject(row, new GlMappingSpec(Mapping(), mode), 10_000,
            out var projected, out var error), error?.ToString());
        Assert.Equal(expected, projected!.AmountScaled);
    }

    [Theory]
    [InlineData(GlAmountMode.AmountWithSide, "X", "1.00", "dc_unlisted")]
    [InlineData(GlAmountMode.AmountWithSide, "", "1.00", "dc_blank")]
    [InlineData(GlAmountMode.AmountWithSide, "\u3000", "0", "dc_blank")]
    [InlineData(GlAmountMode.AmountWithSide, "X", "0", "dc_unlisted")]
    [InlineData(GlAmountMode.AmountWithFlag, "X", "1.00", "dc_unlisted")]
    [InlineData(GlAmountMode.AmountWithFlag, "", "0", "dc_blank")]
    public void UnknownOrBlankSideCode_IsARowErrorEvenWhenAmountIsZero(
        GlAmountMode mode, string code, string amount, string reasonCode)
    {
        Assert.False(GlRowProjector.TryProject(Row(code, amount), new GlMappingSpec(Mapping(), mode), 10_000,
            out var projected, out var error));
        Assert.Null(projected);
        Assert.NotNull(error);
        Assert.Equal(17, error.SourceRowNumber);
        Assert.Equal("借貸別", error.Field);
        Assert.Equal(reasonCode, error.ReasonCode);
    }

    private static readonly string[] Columns = ["傳票", "日期", "科目", "科目名稱", "摘要", "金額", "借貸別"];

    private static Dictionary<string, string> Mapping() => new()
    {
        ["docNum"] = "傳票", ["postDate"] = "日期", ["accNum"] = "科目", ["accName"] = "科目名稱",
        ["description"] = "摘要", ["amount"] = "金額", ["dcField"] = "借貸別",
        ["dcDebitCode"] = "D", ["dcCreditCode"] = "C"
    };

    private static StagingRow Row(string code, string amount) => new(17, new Dictionary<string, string>
    {
        ["傳票"] = "V1", ["日期"] = "2025-03-05", ["科目"] = "A", ["科目名稱"] = "合成科目",
        ["摘要"] = "一般分錄", ["金額"] = amount, ["借貸別"] = code
    });
}
