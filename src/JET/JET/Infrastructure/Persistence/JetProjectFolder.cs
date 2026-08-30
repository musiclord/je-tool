using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 專案資料夾路徑規約：{root}/{projectId}/project.json + jet.db。
/// root 由 composition root 單點注入；正式預設為 %USERPROFILE%\JET，
/// 開發與 portable 環境可以用 JET_PROJECTS_ROOT 明示覆寫。
/// </summary>
public sealed class JetProjectFolder(string rootPath) : IProjectExportLocator
{
    public const string DatabaseFileName = "jet.db";
    public const string ProjectJsonFileName = "project.json";

    public string RootPath { get; } = rootPath;

    /// <summary>projectId 即案件名稱即資料夾名；規則(含 path-traversal 守衛)見 <see cref="ProjectNameRules"/>。</summary>
    public static bool IsValidProjectId(string? projectId)
        => ProjectNameRules.IsValid(projectId);

    public string GetProjectDirectory(string projectId)
    {
        if (!IsValidProjectId(projectId))
        {
            // 同時阻擋 path traversal（"..\.." 等）與格式錯誤的 id
            throw new JetActionException(
                JetErrorCodes.ProjectNotFound,
                $"專案識別碼 '{projectId}' 無效。");
        }

        return Path.Combine(RootPath, projectId);
    }

    public string GetDatabasePath(string projectId)
        => Path.Combine(GetProjectDirectory(projectId), DatabaseFileName);

    public string GetProjectJsonPath(string projectId)
        => Path.Combine(GetProjectDirectory(projectId), ProjectJsonFileName);

    public IEnumerable<string> EnumerateProjectIds()
    {
        if (!Directory.Exists(RootPath))
        {
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(RootPath))
        {
            var id = Path.GetFileName(directory);
            if (IsValidProjectId(id) && File.Exists(Path.Combine(directory, ProjectJsonFileName)))
            {
                yield return id;
            }
        }
    }
}
