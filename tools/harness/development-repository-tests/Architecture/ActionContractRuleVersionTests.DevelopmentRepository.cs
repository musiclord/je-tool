using JET.Application;
using JET.Tests.Infrastructure;
using JET.Tests.TestInfrastructure;
using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

public sealed partial class ActionContractRuleVersionTests
{
    // action 契約裡凡是說「現行」或「目前」篩選版本的句子，版本都要等於程式常數 RuleLogicVersions.Filter。
    // 版本沿革可以列舊版本；只有宣稱現行的句子要跟程式一致，避免契約同時寫出兩個現行版本。
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
