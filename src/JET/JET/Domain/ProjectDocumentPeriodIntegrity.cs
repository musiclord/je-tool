using System.Globalization;

namespace JET.Domain;

internal static class ProjectDocumentPeriodIntegrity
{
    internal static void Validate(ProjectDocument document, string projectName)
    {
        if (DateOnly.TryParseExact(document.PeriodStart, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var start)
            && DateOnly.TryParseExact(document.PeriodEnd, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var end)
            && start <= end) return;

        // 裁定 2026-10-14-01 確認了用字：不寫檔名與格式代碼。
        throw new JetActionException(JetErrorCodes.InvalidProjectSchema,
            $"案件「{projectName}」設定檔中的查核期間無效。請從備份還原案件資料夾，或另建案件重新匯入。");
    }
}
