namespace JET.Domain;

/// <summary>
/// 案件資料夾內由 JET 命名的檔案。前綴由 projectId 去掉檔名不允許的字元而來，最長 48 字；
/// 報告與工作檔共用同一個前綴，審計員在資料夾裡一眼就能看出屬於哪個案件。
/// </summary>
public static class ProjectFileNames
{
    public const string AccountMappingTemplateSuffix = "AccountMapping";

    public static string SafePrefix(string projectId)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        var safeProjectId = string.Concat(projectId.Select(character =>
            char.IsControl(character) || character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*'
                ? '_'
                : character));
        safeProjectId = safeProjectId.Trim(' ', '.');
        if (safeProjectId.Length == 0)
        {
            safeProjectId = "JET";
        }

        return safeProjectId.Length <= 48 ? safeProjectId : safeProjectId[..48];
    }

    /// <summary>給審計員填寫分類再匯回的帳戶對應範本；固定檔名，每次產生都覆蓋。</summary>
    public static string AccountMappingTemplate(string projectId)
        => $"{SafePrefix(projectId)}_{AccountMappingTemplateSuffix}.xlsx";
}
