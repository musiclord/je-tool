using JET.Tests.TestInfrastructure;

namespace JET.Tests.Infrastructure;

internal static partial class TestRepositoryPaths
{
    private static readonly Lazy<string> DevelopmentRepositoryRoot = new(FindRepositoryRoot);

    /// <summary>
    /// 開發儲存庫的根目錄，用來讀方案資料夾以外的檔案。只有方案資料夾時找不到，
    /// 所以只給標記 <see cref="TestProfileTraits.DevelopmentRepository"/> 的測試使用。
    /// </summary>
    public static string RepositoryRoot => DevelopmentRepositoryRoot.Value;

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !(File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))
                && Directory.Exists(Path.Combine(directory.FullName, "src", "JET", "tests", "JET.Tests"))))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("向上尋不到 JET repository root。");
    }
}
