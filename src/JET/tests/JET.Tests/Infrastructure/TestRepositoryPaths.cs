namespace JET.Tests.Infrastructure;

internal static partial class TestRepositoryPaths
{
    /// <summary>方案檔 JET.slnx 所在的資料夾。產品專案在 JET，測試專案在 tests/JET.Tests。</summary>
    public static string SolutionRoot { get; } = FindSolutionRoot();

    /// <summary>測試專案 Fixtures 資料夾裡的固定答案檔；前端測試讀同一份。</summary>
    public static string Fixture(string fileName) =>
        Path.Combine(SolutionRoot, "tests", "JET.Tests", "Fixtures", fileName);

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JET.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("向上尋不到 JET.slnx。");
    }
}
