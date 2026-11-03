using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

/// <summary>六種正式報告的 manifest 字串鎖；oracle 是報告 store 固定契約。</summary>
public sealed class ReportKindValuesTests
{
    [Theory]
    [InlineData(ReportKind.ValidationReport, "validationReport")]
    [InlineData(ReportKind.AccountMapping, "accountMapping")]
    [InlineData(ReportKind.InfReport, "infReport")]
    [InlineData(ReportKind.PrescreenReport, "prescreenReport")]
    [InlineData(ReportKind.CriteriaSelectionReport, "criteriaSelectionReport")]
    [InlineData(ReportKind.WorkingPaper, "workingPaper")]
    public void ToValue_KnownKind_ReturnsStableManifestValue(
        ReportKind kind,
        string expected)
    {
        Assert.Equal(expected, ReportKindValues.ToValue(kind));
    }

    [Theory]
    [InlineData("validationReport", ReportKind.ValidationReport)]
    [InlineData("accountMapping", ReportKind.AccountMapping)]
    [InlineData("infReport", ReportKind.InfReport)]
    [InlineData("prescreenReport", ReportKind.PrescreenReport)]
    [InlineData("criteriaSelectionReport", ReportKind.CriteriaSelectionReport)]
    [InlineData("workingPaper", ReportKind.WorkingPaper)]
    public void TryParse_KnownValue_ReturnsKind(
        string value,
        ReportKind expected)
    {
        var parsed = ReportKindValues.TryParse(value, out var actual);

        Assert.True(parsed);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryParse_UnknownValue_ReturnsFalse()
    {
        Assert.False(ReportKindValues.TryParse("unknownReport", out _));
    }

    [Fact]
    public void ToValue_UndefinedEnum_ThrowsArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ReportKindValues.ToValue((ReportKind)999));
    }
}
