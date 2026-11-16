using JET.Application;
using JET.Tests.Infrastructure;
using JET.Tests.TestInfrastructure;
using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

public sealed partial class ActionContractRuleVersionTests
{
    [Fact]
    [Trait(TestProfileTraits.Key, TestProfileTraits.DevelopmentRepository)]
    public void EveryCurrentFilterVersionClaimInTheManifestMatchesTheProgramConstant()
    {
        var manifest = File.ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot, "docs", "action-contract-manifest.md"));
        var claims = CurrentFilterVersionClaims(manifest);
        Assert.NotEmpty(claims);
        Assert.All(claims, claim => Assert.True(claim.Version == RuleLogicVersions.Filter,
            $"契約宣稱現行篩選版本為 {claim.Version}，程式為 {RuleLogicVersions.Filter}：{claim.Clause}"));
    }
}
