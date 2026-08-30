namespace JET;

internal sealed record ProjectStorageConfiguration(string ProjectsRootPath);

/// <summary>專案根目錄的單一解析點；不使用 process working directory。</summary>
internal static class ProjectStoragePathResolver
{
    internal const string ProjectsRootEnvironmentVariable = "JET_PROJECTS_ROOT";
    public static ProjectStorageConfiguration ResolveEnvironment()
        => Resolve(
            AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetEnvironmentVariable(ProjectsRootEnvironmentVariable));

    internal static ProjectStorageConfiguration Resolve(
        string appBaseDirectory,
        string userProfileDirectory,
        string? projectsRootOverride,
        Func<string, DriveType>? driveTypeResolver = null)
    {
        string projectsRoot;
        if (!string.IsNullOrWhiteSpace(projectsRootOverride))
        {
            projectsRoot = NormalizeLocalPath(
                projectsRootOverride!,
                appBaseDirectory,
                ProjectsRootEnvironmentVariable,
                driveTypeResolver);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(userProfileDirectory))
            {
                throw new InvalidOperationException("無法取得使用者設定檔目錄，不能建立正式專案根目錄。");
            }

            var userProfileRoot = NormalizeLocalPath(
                userProfileDirectory,
                appBaseDirectory,
                "UserProfile",
                driveTypeResolver);
            projectsRoot = Path.GetFullPath(Path.Combine(userProfileRoot, "JET"));
        }

        return new ProjectStorageConfiguration(projectsRoot);
    }

    private static string NormalizeLocalPath(
        string value,
        string relativeBase,
        string label,
        Func<string, DriveType>? driveTypeResolver)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException($"{label} 不可為空。");
        }

        var combined = Path.IsPathFullyQualified(trimmed)
            ? trimmed
            : Path.Combine(relativeBase, trimmed);
        var fullPath = Path.GetFullPath(combined);
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{label} 不支援 UNC 網路路徑。");
        }

        var root = Path.GetPathRoot(fullPath);
        var driveType = string.IsNullOrEmpty(root)
            ? DriveType.NoRootDirectory
            : driveTypeResolver?.Invoke(root) ?? new DriveInfo(root).DriveType;
        if (driveType is not (DriveType.Fixed or DriveType.Removable))
        {
            throw new InvalidOperationException($"{label} 只支援本機固定磁碟或可移除磁碟。");
        }

        return Path.TrimEndingDirectorySeparator(fullPath);
    }
}
