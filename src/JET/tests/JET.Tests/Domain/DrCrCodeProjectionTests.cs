using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class DrCrCodeProjectionTests
{
    [Theory]
    [InlineData(GlAmountMode.AmountWithSide)]
    [InlineData(GlAmountMode.AmountWithFlag)]
    public void SideAndFlag_RequireAtLeastOneExplicitDirectionCode(GlAmountMode mode)
    {
        var mapping = Mapping();
        mapping.Remove("drCrCreditCode");
        var result = MappingValidator.ValidateGl(new GlMappingSpec(mapping, mode), Columns);
        // 2026-10-08 第 6 則取代雙邊必填；首次失敗 d38bbe8932664c579123f90e6d61fef7。
        Assert.Empty(result.MissingRequiredKeys);
        Assert.True(result.IsValid);
        Assert.Empty(result.UnknownColumns);
        mapping.Remove("drCrDebitCode");
        result = MappingValidator.ValidateGl(new GlMappingSpec(mapping, mode), Columns);
        Assert.Equal(new[] { "drCrDebitCode", "drCrCreditCode" }, result.MissingRequiredKeys);
        Assert.False(result.IsValid);
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

    // \u88c1\u5b9a 2026-11-05-37 \u7167\u820a\u7248\u4fdd\u7559\u4f86\u6e90\u91d1\u984d\u7684\u6b63\u8ca0\u865f\uff1a\u501f\u65b9\u4ee3\u78bc\u7684\u5217\u7167\u539f\u91d1\u984d\uff0c\u5176\u4ed6\u5217\u4e58\u4ee5 -1\u3002
    // \u9019\u88e1\u7684\u4f86\u6e90\u91d1\u984d\u662f -1.00\uff0c\u6240\u4ee5\u501f\u65b9\u4ee3\u78bc\u7684\u5217\u662f -10_000\u3001\u8cb8\u65b9\u4ee3\u78bc\u7684\u5217\u662f 10_000\uff1b\u539f\u672c\u5148\u53d6\u7d55\u5c0d\u503c\uff0c\u501f\u65b9 10_000\u3001\u8cb8\u65b9 -10_000\u3002
    // \u7b2c\u4e00\u6b21\u5931\u6557\uff1aFocused \u6536\u64da 20261105-111930631-bf1582b63a4f4a6284ddd6210ae6cc1b\u3002
    [Theory]
    [InlineData(GlAmountMode.AmountWithSide, " D ", -10_000L)]
    [InlineData(GlAmountMode.AmountWithSide, "\u3000d\u00a0", -10_000L)]
    [InlineData(GlAmountMode.AmountWithSide, " C ", 10_000L)]
    [InlineData(GlAmountMode.AmountWithSide, "c", 10_000L)]
    [InlineData(GlAmountMode.AmountWithFlag, " D ", -10_000L)]
    [InlineData(GlAmountMode.AmountWithFlag, "c", 10_000L)]
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
        ["voucherNumber"] = "傳票", ["glDate"] = "日期", ["accountNumber"] = "科目", ["accountName"] = "科目名稱",
        ["description"] = "摘要", ["amount"] = "金額", ["drCr"] = "借貸別",
        ["drCrDebitCode"] = "D", ["drCrCreditCode"] = "C"
    };

    private static StagingRow Row(string code, string amount) => new(17, new Dictionary<string, string>
    {
        ["傳票"] = "V1", ["日期"] = "2025-03-05", ["科目"] = "A", ["科目名稱"] = "合成科目",
        ["摘要"] = "一般分錄", ["金額"] = amount, ["借貸別"] = code
    });
}
