using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class Stage8TrackOAppearanceRegistryTests
{
    private static readonly string[] ExpectedTrackOIds =
    [
        "validation-summary-missing-account-fill",
        "validation-missing-account-detail-autofit",
        "validation-summary-missing-voucher-fill",
        "validation-missing-voucher-detail-autofit",
        "validation-summary-blank-description-fill",
        "validation-blank-description-detail-autofit",
        "validation-summary-post-period-fill",
        "validation-post-period-detail-autofit",
        "validation-summary-completeness-fill",
        "validation-completeness-detail-autofit",
        "validation-summary-unbalanced-voucher-fill",
        "validation-unbalanced-voucher-detail-autofit",
        "validation-field-info-tb-header-bold",
        "validation-field-info-tb-header-font",
        "validation-field-info-tb-header-size",
        "validation-field-info-tb-header-fill",
        "validation-field-info-gl-header-bold",
        "validation-field-info-gl-header-font",
        "validation-field-info-gl-header-size",
        "validation-field-info-gl-header-fill",
        "validation-field-info-autofit",
        "inf-main-character-number-format",
        "prescreen-summary-description-wrap",
        "prescreen-detail-autofit",
        "criteria-summary-row-autofit",
        "criteria-detail-autofit"
    ];

    [Fact]
    public void Registry_TrackO_IsTheExactClosedTwentySixIdSet()
    {
        var actual = LegacyAppearanceRegistry.Load().Entries
            .Where(entry => !string.Equals(
                entry.Procedure,
                "Step5_Export_Excel_TW",
                StringComparison.Ordinal))
            .Select(entry => entry.Id)
            .ToArray();

        Assert.Equal(26, actual.Length);
        Assert.Equal(ExpectedTrackOIds, actual);
        Assert.DoesNotContain(
            actual,
            id => id.Contains("account-mapping", StringComparison.Ordinal));
    }
}
