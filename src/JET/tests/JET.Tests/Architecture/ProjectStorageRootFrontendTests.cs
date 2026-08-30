using Xunit;

namespace JET.Tests.Architecture;

/// <summary>Picker 與 JetApi 不再保留舊專案根搬遷契約、狀態或樣式。</summary>
public sealed class ProjectStorageRootFrontendTests
{
    [Fact]
    public void ProductSource_HasNoLegacyStorageMigrationContractOrImplementation()
    {
        var productRoot = Path.Combine(RepoRoot(), "JET");
        var sources = Directory
            .EnumerateFiles(productRoot, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => Path.GetExtension(path) is ".cs" or ".js" or ".css" or ".html")
            .Select(File.ReadAllText)
            .ToArray();

        AssertForbidden(sources, "JET_LEGACY_PROJECTS_ROOT");
        AssertForbidden(sources, "LegacyProjectsRootPath");
        AssertForbidden(sources, "LegacyProjectStorageMigration");
        AssertForbidden(sources, "ProjectStorageMigration");
        AssertForbidden(sources, "project_storage_migration_failed");
        AssertForbidden(sources, "project.migrateLegacyStorage");
        AssertForbidden(sources, "storageMigration");
    }

    [Fact]
    public void Frontend_HasNoLegacyStorageMigrationContractOrUi()
    {
        var frontendRoot = Path.Combine(RepoRoot(), "JET", "wwwroot");
        var sources = Directory
            .EnumerateFiles(frontendRoot, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".js" or ".css" or ".html")
            .Select(File.ReadAllText)
            .ToArray();

        AssertForbidden(sources, "project.migrateLegacyStorage");
        AssertForbidden(sources, "projectMigrateLegacyStorage");
        AssertForbidden(sources, "storageMigration");
        AssertForbidden(sources, "storage-migration");
        AssertForbidden(sources, "picker-migrate-legacy");
        AssertForbidden(sources, "picker-migration-dismiss");
        AssertForbidden(sources, "安全複製舊版案件");
    }

    private static void AssertForbidden(IEnumerable<string> sources, string value)
        => Assert.DoesNotContain(sources, source => source.Contains(value, StringComparison.Ordinal));

    private static string RepoRoot()
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
