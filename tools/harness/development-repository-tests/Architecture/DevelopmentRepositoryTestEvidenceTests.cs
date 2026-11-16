using JET.Tests.Infrastructure;
using JET.Tests.TestInfrastructure;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 放在驗證框架底下的測試和測試專案裡的測試守同一條規則：
/// 不能在任何斷言之前，因為環境不齊就提早結束而算通過。
/// </summary>
public sealed class DevelopmentRepositoryTestEvidenceTests
{
    [Fact]
    [Trait(TestProfileTraits.Key, TestProfileTraits.DevelopmentRepository)]
    public void DevelopmentRepositoryTests_DoNotTurnMissingEnvironmentIntoPassingEarlyReturn()
    {
        TestEvidencePolicyGuard.ValidateTree(Path.Combine(
            TestRepositoryPaths.RepositoryRoot, "tools", "harness", "development-repository-tests"));
    }
}
