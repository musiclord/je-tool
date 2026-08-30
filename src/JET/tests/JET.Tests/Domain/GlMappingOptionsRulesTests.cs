using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class GlMappingOptionsRulesTests
{
    [Theory]
    [InlineData(ApprovalDateModeNames.Unmapped, false, true)]
    [InlineData(ApprovalDateModeNames.Mapped, true, true)]
    [InlineData(ApprovalDateModeNames.SameAsPostDate, false, true)]
    [InlineData(ApprovalDateModeNames.Mapped, false, false)]
    [InlineData(ApprovalDateModeNames.Unmapped, true, false)]
    [InlineData(ApprovalDateModeNames.SameAsPostDate, true, false)]
    public void ApprovalDateMode_ThreeStatesAreMutuallyExclusive(
        string mode,
        bool mapApprovalDate,
        bool expectedValid)
    {
        var mapping = new Dictionary<string, string>();
        if (mapApprovalDate)
        {
            mapping[GlMappingKeys.DocDate] = "approval";
        }
        var options = GlMappingOptions.NormalizeLegacy(mapping) with { ApprovalDateMode = mode };

        if (expectedValid)
        {
            var canonical = GlMappingOptionsRules.NormalizeAndValidate(
                mapping,
                ["approval"],
                options);
            Assert.Equal(mode, canonical.ApprovalDateMode);
        }
        else
        {
            Assert.Throws<ArgumentException>(() => GlMappingOptionsRules.NormalizeAndValidate(
                mapping,
                ["approval"],
                options));
        }
    }

    [Fact]
    public void ManualAutoPolicy_TrimsDeduplicatesAndRejectsCaseInsensitiveOverlap()
    {
        var canonical = GlMappingOptionsRules.NormalizeAndValidate(
            new Dictionary<string, string>(),
            [],
            GlMappingOptions.NormalizeLegacy(new Dictionary<string, string>()) with
            {
                ManualAutoPolicy = new GlManualAutoPolicy([" M ", "m"], [" A ", "a"])
            });

        Assert.Equal(["M"], canonical.ManualAutoPolicy.ManualValues);
        Assert.Equal(["A"], canonical.ManualAutoPolicy.AutomaticValues);

        Assert.Throws<ArgumentException>(() => GlMappingOptionsRules.NormalizeAndValidate(
            new Dictionary<string, string>(),
            [],
            canonical with
            {
                ManualAutoPolicy = new GlManualAutoPolicy(["manual"], ["MANUAL"])
            }));
    }

    [Fact]
    public void RdeField_RequiresCanonicalStableIdAndUnusedExistingSourceColumn()
    {
        var mapping = new Dictionary<string, string>
        {
            [GlMappingKeys.Description] = "description"
        };
        var options = GlMappingOptions.NormalizeLegacy(mapping) with
        {
            RdeFields =
            [
                new GlRdeFieldMetadata(
                    "rde.00000000000000000000000000000001",
                    "description",
                    "Custom",
                    RdeFieldValueTypeNames.Text)
            ]
        };

        Assert.Throws<ArgumentException>(() => GlMappingOptionsRules.NormalizeAndValidate(
            mapping,
            ["description"],
            options));
    }
}
