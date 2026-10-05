using System.Globalization;

namespace JET.Domain;

internal static class ProjectDocumentPeriodIntegrity
{
    internal static void Validate(ProjectDocument document, string sourceDescription)
    {
        if (DateOnly.TryParseExact(document.PeriodStart, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var start)
            && DateOnly.TryParseExact(document.PeriodEnd, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var end)
            && start <= end) return;

        throw new JetActionException(JetErrorCodes.InvalidProjectSchema,
            $"{sourceDescription} 查核期間不合法；"
            + "起日與迄日須為 yyyy-MM-dd，且起日不得晚於迄日。請從備份復原，或另建案件重新匯入。");
    }
}
