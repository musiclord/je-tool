using System.Security.Cryptography;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class Stage9TrackedLegacyAppearanceSnapshotTests
{
    private static readonly IReadOnlyDictionary<string, string> ExpectedSha256 =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["case-A-account-mapping.appearance.json"] =
                "8CB1217EC5F16940356F9B00EC4602B880606EAF1D5BD9534E7EF77643E74607",
            ["case-A-criteria-selection-report.appearance.json"] =
                "5182E80E726A32FC873A182C28361C28EF0A4F7E407E0E2435830E021448E502",
            ["case-A-inf-report.appearance.json"] =
                "BAAA36CC7561AF6433394F13564ED025600291035ABDC79AB0FCCA8CEB95C46B",
            ["case-A-prescreen-report.appearance.json"] =
                "62F07AEDD7326FBFB2E312917B92EC1D50A3B73F59AB53AF018BF36A7765DC13",
            ["case-A-validation-report.appearance.json"] =
                "90A7E79EED53F69E592B74F0CE491344BA2C09CEA1262D1885B8C7D4AA52CF74",
            ["case-A-working-paper.appearance.json"] =
                "89A1353BC11DCAC437FB287510DA0BFE93DB739B52EDAD07A1277F65B686C766",
            ["case-B-account-mapping.appearance.json"] =
                "B46ADD447D46D8A393A15AFF1E0FF93570A3E8979EEB07B679A14FFB7D4D05E5",
            ["case-B-criteria-selection-report.appearance.json"] =
                "417DCC214B798AA4E8547923ED194E726FCA4D0EFA5069CF343BE36DAD607412",
            ["case-B-inf-report.appearance.json"] =
                "A34428CB8792FF05FB7EB17955B7E6543D379F9B6CC75D2B172D2F1E3330C133",
            ["case-B-prescreen-report.appearance.json"] =
                "35D9F57775C3A50B37D9FF74791C83C1AAA0F93F7BD9C680BFA2A38424A100A6",
            ["case-B-validation-report.appearance.json"] =
                "7DC545E44D0B8C60D0F8D334300AABF7AAF272098B8D0B91263710FA1B6B9972",
            ["case-B-working-paper.appearance.json"] =
                "C1B9D84223C131F3A70EAE7CF9A4B2C7A1071551C6B54A7817CB41953FCC2951",
        };

    [Fact]
    public void RepositorySnapshots_AreTheExactClosedStage9GoldenSet()
    {
        SpreadsheetAppearanceSnapshotSchemaGuard.ValidateRepositoryInventory(
            TestRepositoryPaths.RepositoryRoot);
        Assert.Equal(
            SpreadsheetAppearanceSnapshotSchemaGuard.ExpectedSnapshots
                .Select(item => item.FileName)
                .Order(StringComparer.Ordinal),
            ExpectedSha256.Keys.Order(StringComparer.Ordinal));

        var fixtureDirectory = Path.Combine(
            TestRepositoryPaths.RepositoryRoot,
            SpreadsheetAppearanceSnapshotSchemaGuard.FixtureRelativeDirectory.Replace(
                '/',
                Path.DirectorySeparatorChar));
        foreach (var (fileName, expectedHash) in ExpectedSha256)
        {
            var actualHash = Convert.ToHexString(SHA256.HashData(
                File.ReadAllBytes(Path.Combine(fixtureDirectory, fileName))));
            Assert.Equal(expectedHash, actualHash);
        }
    }
}
