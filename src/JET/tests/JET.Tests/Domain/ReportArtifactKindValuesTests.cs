using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

/// <summary>六種正式報告的 manifest 字串鎖；oracle 是 artifact store 固定契約。</summary>
public sealed class ReportArtifactKindValuesTests
{
    [Theory]
    [InlineData(ReportArtifactKind.ValidationReport, "validationReport")]
    [InlineData(ReportArtifactKind.AccountMapping, "accountMapping")]
    [InlineData(ReportArtifactKind.InfReport, "infReport")]
    [InlineData(ReportArtifactKind.PrescreenReport, "prescreenReport")]
    [InlineData(ReportArtifactKind.CriteriaSelectionReport, "criteriaSelectionReport")]
    [InlineData(ReportArtifactKind.WorkingPaper, "workingPaper")]
    public void ToValue_KnownKind_ReturnsStableManifestValue(
        ReportArtifactKind kind,
        string expected)
    {
        Assert.Equal(expected, ReportArtifactKindValues.ToValue(kind));
    }

    [Theory]
    [InlineData("validationReport", ReportArtifactKind.ValidationReport)]
    [InlineData("accountMapping", ReportArtifactKind.AccountMapping)]
    [InlineData("infReport", ReportArtifactKind.InfReport)]
    [InlineData("prescreenReport", ReportArtifactKind.PrescreenReport)]
    [InlineData("criteriaSelectionReport", ReportArtifactKind.CriteriaSelectionReport)]
    [InlineData("workingPaper", ReportArtifactKind.WorkingPaper)]
    public void TryParse_KnownValue_ReturnsKind(
        string value,
        ReportArtifactKind expected)
    {
        var parsed = ReportArtifactKindValues.TryParse(value, out var actual);

        Assert.True(parsed);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryParse_UnknownValue_ReturnsFalse()
    {
        Assert.False(ReportArtifactKindValues.TryParse("unknownReport", out _));
    }

    [Fact]
    public void ToValue_UndefinedEnum_ThrowsArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ReportArtifactKindValues.ToValue((ReportArtifactKind)999));
    }
}
