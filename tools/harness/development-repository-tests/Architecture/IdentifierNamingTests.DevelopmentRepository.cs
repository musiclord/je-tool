using JET.Tests.Infrastructure;

namespace JET.Tests.Architecture;

public sealed partial class IdentifierNamingTests
{
    // 這個資料夾的原始檔在開發儲存庫建置時編進 JET.Tests，檔名與暫存名稱也照同一套規則檢查。
    static partial void AddCompiledTestRoots(List<string> roots) =>
        roots.Add(Path.Combine(TestRepositoryPaths.RepositoryRoot, "tools", "harness", "development-repository-tests"));
}
