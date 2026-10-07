using JET.Domain;

namespace JET.Infrastructure;

public sealed class CalendarTemplateSource : ICalendarTemplateSource
{
    public IReadOnlyList<string> FileNames { get; } = Array.AsReadOnly(new[]
    {
        ReportTemplateCatalog.Holiday, ReportTemplateCatalog.MakeUpDay
    });

    public Task CopyToAsync(string fileName, Stream output, CancellationToken cancellationToken)
    {
        if (!FileNames.Contains(fileName, StringComparer.Ordinal))
            throw new ArgumentException("未登錄的行事曆範本。", nameof(fileName));
        return ReportTemplateCatalog.Default.CopyToAsync(fileName, output, cancellationToken);
    }
}
