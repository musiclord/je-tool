using System.Globalization;

namespace JET.Domain;

internal static class ProjectDocumentPeriodIntegrity
{
    internal static void Validate(ProjectDocument document, string caseName)
    {
        if (DateOnly.TryParseExact(document.PeriodStart, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var start)
            && DateOnly.TryParseExact(document.PeriodEnd, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var end)
            && start <= end) return;

        // 使用者 2026-10-14 第三輪勾選頁確認 R3-51：不寫檔名與格式代碼。
        throw new JetActionException(JetErrorCodes.InvalidProjectSchema,
            $"案件「{caseName}」設定檔中的查核期間無效。請從備份還原案件資料夾，或另建案件重新匯入。");
    }
}
