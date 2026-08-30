using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class LineItemNumericSortKeyTests
{
    [Fact]
    public void TryCreate_OrdinalOrderMatchesNumericOrderAcrossScale28AndHugeFiniteValues()
    {
        var values = new[]
        {
            ("positiveHuge", "1E+100"),
            ("negativeScaleLow", "-1.0000000000000000000000000001"),
            ("positiveTiny", "1E-28"),
            ("negativeTwo", "-2"),
            ("zero", "0"),
            ("positiveScaleHigh", "1.0000000000000000000000000002"),
            ("negativeHuge", "-1E+100"),
            ("positiveTwo", "2"),
            ("negativeTiny", "-1E-28"),
            ("positiveScaleLow", "1.0000000000000000000000000001"),
            ("negativeScaleHigh", "-1.0000000000000000000000000002")
        };

        var ordered = values
            .Select(item =>
            {
                Assert.True(LineItemNumericSortKey.TryCreate(item.Item2, out var key));
                Assert.Equal(LineItemNumericSortKey.KeyLength, key.Length);
                return (item.Item1, Key: key);
            })
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item.Item1)
            .ToArray();

        Assert.Equal(
        [
            "negativeHuge",
            "negativeTwo",
            "negativeScaleHigh",
            "negativeScaleLow",
            "negativeTiny",
            "zero",
            "positiveTiny",
            "positiveScaleLow",
            "positiveScaleHigh",
            "positiveTwo",
            "positiveHuge"
        ],
            ordered);
    }

    [Fact]
    public void TryCreate_EquivalentLexemesAndNativeBooleansShareKeys()
    {
        AssertSameKey("1", "1.0", "01E0", "+1.000", "true");
        AssertSameKey("-1", "-1.0", "-01E0");
        AssertSameKey("0", "-0.000", "0E+100", "false");
        AssertSameKey("1000", "1E3", "10.00E2");
    }

    [Theory]
    [InlineData("")]
    [InlineData("+")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1.2.3")]
    [InlineData("1E")]
    [InlineData("123456789012345678901234567890")]
    public void TryCreate_RejectsNonCanonicalOrOverCapacityValues(string raw)
    {
        Assert.False(LineItemNumericSortKey.TryCreate(raw, out var key));
        Assert.Empty(key);
    }

    private static void AssertSameKey(string first, params string[] equivalents)
    {
        Assert.True(LineItemNumericSortKey.TryCreate(first, out var expected));
        foreach (var equivalent in equivalents)
        {
            Assert.True(LineItemNumericSortKey.TryCreate(equivalent, out var actual));
            Assert.Equal(expected, actual);
        }
    }
}
