using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class Batch9MoneyDateParsingTests
{
    [Theory]
    [InlineData("1,234.50", 12_345_000L)]
    [InlineData("(1,234.50)", -12_345_000L)]
    [InlineData(" (1234.50) ", -12_345_000L)]
    [InlineData("1,234,567", 12_345_670_000L)]
    [InlineData("-1,234.50", -12_345_000L)]
    [InlineData("+1,234.50", 12_345_000L)]
    [InlineData("0.00005", 1L)]
    [InlineData("(0.00005)", -1L)]
    [InlineData("-", 0L)]
    public void Money_ValidGroupingAndAccountingParentheses_HaveFixedScaledAnswers(string raw, long expected)
    {
        Assert.True(MoneyScaling.TryParseAmount(raw, out var amount));
        Assert.True(MoneyScaling.TryToScaled(amount, 10_000, out var scaled));
        Assert.Equal(expected, scaled);
    }

    [Theory]
    [InlineData("1500,50")]
    [InlineData("12,34")]
    [InlineData("1,,234")]
    [InlineData("1234,567")]
    [InlineData("1,234,")]
    [InlineData(",123")]
    [InlineData("1,23,456")]
    [InlineData("(1500,50)")]
    [InlineData("(-123)")]
    [InlineData("(+123)")]
    [InlineData("((123))")]
    public void Money_MalformedGroupingOrMultipleSigns_IsRejected(string raw) =>
        Assert.False(MoneyScaling.TryParseAmount(raw, out _));

    [Theory]
    [InlineData("2", "1900-01-01")]
    [InlineData("45292", "2024-01-01")]
    [InlineData("73415", "2100-12-31")]
    [InlineData("73415.5", "2100-12-31")]
    public void ExcelSerial_WithinYearBoundary_HasFixedAnswer(string raw, string expected)
    {
        Assert.True(DateNormalizer.TryNormalize(raw, DateParseOptions.Default, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("73416")]
    [InlineData("240115")]
    [InlineData("2958465")]
    [InlineData("1140611")]
    [InlineData("1141315")]
    public void ExcelSerial_Outside1900Through2100_IsRejectedWithRocDisabled(string raw) =>
        Assert.False(DateNormalizer.TryNormalize(raw, new DateParseOptions(false), out _));

    [Theory]
    [InlineData("01/02/2025")]
    [InlineData("02-01-2025")]
    [InlineData("1.2.2025")]
    [InlineData("13/2/2025")]
    [InlineData("01/02/25")]
    public void NumericThreePartDatesWithYearLast_AreRejectedRatherThanGuessed(string raw)
    {
        Assert.False(DateNormalizer.TryNormalize(raw, DateParseOptions.Default, out _));
        Assert.False(DateNormalizer.TryNormalize(raw, new DateParseOptions(false), out _));
    }

    [Theory]
    [InlineData("01/02/2025 12:00:00")]
    [InlineData("01/02/2025T12:00:00")]
    [InlineData(" 01/02/2025 12:00:00 ")]
    [InlineData("02-01-2025 12:00")]
    [InlineData("1.2.2025 12:00:00")]
    [InlineData("01/02/25 12:00:00")]
    public void NumericYearLast_WithTimeSuffix_RemainsRejected(string raw)
    {
        Assert.False(DateNormalizer.TryNormalize(raw, DateParseOptions.Default, out _));
        Assert.False(DateNormalizer.TryNormalize(raw, new DateParseOptions(false), out _));
    }

    [Theory]
    [InlineData("2025/01/02 12:00:00", "2025-01-02")]
    [InlineData("2025-01-02T12:00:00", "2025-01-02")]
    [InlineData(" 2025/01/02 12:00:00 ", "2025-01-02")]
    public void ExplicitYearFirst_WithTimeSuffix_KeepsExistingAcceptedMeaning(string raw, string expected)
    {
        Assert.True(DateNormalizer.TryNormalize(raw, DateParseOptions.Default, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("2110-12-31", "2110-12-31")]
    [InlineData("2110/12/31", "2110-12-31")]
    [InlineData("21101231", "2110-12-31")]
    [InlineData("199/12/31", "2110-12-31")]
    [InlineData("1140611", "2025-06-11")]
    [InlineData("June 11, 2025", "2025-06-11")]
    public void ExplicitYearAndExistingRocFormats_AreNotRestrictedBySerialGuard(string raw, string expected)
    {
        Assert.True(DateNormalizer.TryNormalize(raw, DateParseOptions.Default, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(TbChangeMode.DirectChange, -12_345_000L)]
    [InlineData(TbChangeMode.DebitCredit, -13_347_500L)]
    [InlineData(TbChangeMode.OpenClose, 13_347_500L)]
    [InlineData(TbChangeMode.OpenCloseBySide, 13_347_500L)]
    public void TbAllFourAmountModes_UseTheSameAccountingParenthesesParser(TbChangeMode mode, long expected)
    {
        var mapping = new Dictionary<string, string>
        {
            ["amount"] = "negative", ["debitAmt"] = "negative", ["creditAmt"] = "positive",
            ["openingBalance"] = "negative", ["closingBalance"] = "positive",
            ["openingDebit"] = "negative", ["openingCredit"] = "zero",
            ["closingDebit"] = "positive", ["closingCredit"] = "zero"
        };
        var row = new StagingRow(17, new Dictionary<string, string>
        { ["negative"] = "(1,234.50)", ["positive"] = "100.25", ["zero"] = "0" });
        Assert.True(TbRowProjector.TryProject(row, new TbMappingSpec(mapping, mode), 10_000, out var projected, out var error),
            error?.ToString());
        Assert.Equal(expected, projected!.ChangeAmountScaled);
    }
}
