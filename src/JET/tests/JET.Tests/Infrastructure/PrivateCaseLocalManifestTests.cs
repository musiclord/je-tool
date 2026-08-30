using System.Text.Json;
using JET.Tests.TestInfrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class PrivateCaseLocalManifestTests
{
    private const string RootEnvironmentVariable = "JET_PRIVATE_CASE_ROOT";
    private const string ManifestEnvironmentVariable = "JET_PRIVATE_CASE_MANIFEST";

    [Fact]
    [Trait(TestProfileTraits.Key, "PrivateCase")]
    public void Load_AuthorizedLocalManifest_ProducesOnlyTheExpectedSafeSummary()
    {
        var root = Environment.GetEnvironmentVariable(RootEnvironmentVariable);
        var manifestPath = Environment.GetEnvironmentVariable(ManifestEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new InvalidOperationException(
                "PrivateCase 需要由獨立驗證入口明確提供本機案件根目錄與清單。"
                + "一般測試不得自行尋找私人案件。");
        }

        var manifest = PrivateCaseManifestLoader.Load(root, manifestPath);

        Assert.Equal(PrivateCaseManifestLoader.CurrentSchemaVersion, manifest.SchemaVersion);
        Assert.Equal(1, manifest.GlSourceCount);
        Assert.Equal(1, manifest.TbSourceCount);
        Assert.Equal(6, manifest.LegacyReportCount);
        Assert.InRange(manifest.ScenarioCount, 1, 10);
        Assert.False(string.IsNullOrWhiteSpace(manifest.Project.ProjectCode));
        Assert.False(string.IsNullOrWhiteSpace(manifest.Project.EntityName));
        Assert.False(string.IsNullOrWhiteSpace(manifest.Project.OperatorId));

        var summary = JsonSerializer.Serialize(manifest);
        Assert.DoesNotContain(root, summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(manifest.Project.ProjectCode, summary, StringComparison.Ordinal);
        Assert.DoesNotContain(manifest.Project.EntityName, summary, StringComparison.Ordinal);
        Assert.DoesNotContain(manifest.Project.OperatorId, summary, StringComparison.Ordinal);
    }
}
