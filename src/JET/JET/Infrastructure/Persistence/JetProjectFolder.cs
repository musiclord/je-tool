using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 案件資料夾的位置：根目錄下每個案件一個資料夾，資料夾名就是 projectId，裡面放 project.json 與 jet.db。
/// 根目錄只在 composition root 決定，再傳進來；正式預設為 %USERPROFILE%\JET，
/// 開發環境與可攜（portable）環境可以用環境變數 JET_PROJECTS_ROOT 改到別的位置。
/// </summary>
public sealed class JetProjectFolder(string rootPath) : IProjectExportLocator
{
    public const string DatabaseFileName = "jet.db";
    public const string ProjectJsonFileName = "project.json";

    public string RootPath { get; } = rootPath;

    /// <summary>
    /// projectId、案件名稱與資料夾名是同一個字串。命名規則在 <see cref="ProjectNameRules"/>，
    /// 其中也擋掉會跳出根目錄的寫法（例如 "..\.."）。
    /// </summary>
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
    /// 建案中的標記檔。建案時和 project.json 一起寫進暫存資料夾再發布，資料庫建好、建案完成後才刪掉。
    /// 開案時標記還在，表示上一次建案在完成前中斷，審計員沒有拿到這個案件，
    /// 資料庫裡不會有資料，所以開案時把初始化做完；標記不在而資料庫不見或沒有內容，才是資料遺失。
    /// </summary>
    public const string CreationMarkerFileName = ".project-creating";

    public string GetCreationMarkerPath(string projectId)
        => Path.Combine(GetProjectDirectory(projectId), CreationMarkerFileName);

    /// <summary>
    /// 案件鎖檔的位置：放在案件根目錄、案件資料夾旁邊，檔名是用途前綴加案件資料夾名。
    /// 同一個根目錄裡資料夾名不會重複，鎖檔名也就不會重複；案件名稱不能含「.」，所以鎖檔也不會和案件資料夾同名。
    /// 鎖檔放在案件資料夾外，持鎖時仍能刪掉整個案件資料夾。
    /// <paramref name="normalizedRootPath"/> 與 <paramref name="projectDirectory"/> 由呼叫端正規化並確認後者是前者的直接子資料夾。
    /// </summary>
    public static string GetLockPathBesideProject(string normalizedRootPath, string projectDirectory, string purposePrefix)
        => Path.Combine(normalizedRootPath, purposePrefix + Path.GetFileName(projectDirectory) + ".lock");

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
