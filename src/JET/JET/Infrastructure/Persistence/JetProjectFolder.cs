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
            throw JetPayloadRejection.Create(JetErrorCodes.ProjectNotFound, "project_id_invalid");
        }

        return Path.Combine(RootPath, projectId);
    }

    public string GetDatabasePath(string projectId)
        => Path.Combine(GetProjectDirectory(projectId), DatabaseFileName);

    public string GetProjectJsonPath(string projectId)
        => Path.Combine(GetProjectDirectory(projectId), ProjectJsonFileName);

    /// <summary>
    /// 列出名稱合法的案件資料夾。資料夾裡有沒有 project.json，由 <see cref="JsonFileProjectStore"/> 在讀寫共用的鎖裡判斷：
    /// 儲存換檔的一瞬間正式檔名會暫時不存在，在這裡判斷會讓正在儲存的案件從案件清單上消失。
    /// </summary>
    public IEnumerable<string> EnumerateProjectIds()
    {
        if (!Directory.Exists(RootPath))
        {
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(RootPath))
        {
            var id = Path.GetFileName(directory);
            if (IsValidProjectId(id))
            {
                yield return id;
            }
        }
    }
}
