using System.Text.RegularExpressions;

namespace Jet.ExcelDriver;

internal static class DriverExitCodes
{
    internal const int Passed = 0;
    internal const int Failed = 1;
    internal const int Blocked = 2;
    internal const int InfrastructureError = 4;
}

internal static class ExcelDriverContract
{
    internal const string Scenario = "synthetic-report-roundtrip";
    internal const int MinimumTimeoutSeconds = 30;
    internal const int MaximumTimeoutSeconds = 300;
    internal const long MaximumWorkbookBytes = 512L * 1024L * 1024L;

    internal static readonly string[] ReportKinds =
    [
        "accountMapping",
        "criteriaSelectionReport",
        "infReport",
        "prescreenReport",
        "validationReport",
        "workingPaper"
    ];

    private static readonly IReadOnlySet<string> ReportKindSet =
        new HashSet<string>(ReportKinds, StringComparer.Ordinal);
    private static readonly Regex RunIdPattern = new(
        "^[0-9]{8}-[0-9]{9}-[0-9a-f]{32}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static bool IsReportKind(string value) => ReportKindSet.Contains(value);

    internal static bool IsRunId(string value) => RunIdPattern.IsMatch(value);
}

internal sealed record DriverOptions(
    string OwnedRoot,
    string RunId,
    string ManifestPath,
    string ReportKind,
    int TimeoutSeconds)
{
    internal static DriverOptions Parse(string[] args)
    {
        if (args.Length != 10)
        {
            throw new InvalidDataException("Excel driver arguments were rejected.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            var name = args[index];
            var value = args[index + 1];
            if (name is not ("--owned-root" or "--run-id" or "--manifest" or "--report-kind" or "--timeout-seconds")
                || !values.TryAdd(name, value))
            {
                throw new InvalidDataException("Excel driver arguments were rejected.");
            }
        }

        if (!values.TryGetValue("--owned-root", out var ownedRoot)
            || !values.TryGetValue("--run-id", out var runId)
            || !values.TryGetValue("--manifest", out var manifestPath)
            || !values.TryGetValue("--report-kind", out var reportKind)
            || !values.TryGetValue("--timeout-seconds", out var timeoutText)
            || !Path.IsPathFullyQualified(ownedRoot)
            || !Path.IsPathFullyQualified(manifestPath)
            || !ExcelDriverContract.IsRunId(runId)
            || !ExcelDriverContract.IsReportKind(reportKind)
            || !int.TryParse(timeoutText, out var timeoutSeconds)
            || timeoutSeconds is < ExcelDriverContract.MinimumTimeoutSeconds
                or > ExcelDriverContract.MaximumTimeoutSeconds)
        {
            throw new InvalidDataException("Excel driver arguments were rejected.");
        }

        return new DriverOptions(
            Path.GetFullPath(ownedRoot),
            runId,
            Path.GetFullPath(manifestPath),
            reportKind,
            timeoutSeconds);
    }
}

internal readonly record struct ExcelProcessIdentity(int ProcessId, long StartTimeUtcTicks);

internal sealed record ExcelProcessSnapshot(IReadOnlyDictionary<int, long?> Processes)
{
    internal int Count => Processes.Count;
}

internal readonly record struct WorkbookOpenPolicy(
    int UpdateLinks,
    bool ReadOnly,
    bool IgnoreReadOnlyRecommended,
    bool Editable,
    bool Notify,
    bool AddToMru,
    bool Local,
    int CorruptLoad,
    int AutomationSecurity)
{
    internal static WorkbookOpenPolicy SafeReadOnly => new(
        UpdateLinks: 0,
        ReadOnly: true,
        IgnoreReadOnlyRecommended: true,
        Editable: false,
        Notify: false,
        AddToMru: false,
        Local: false,
        CorruptLoad: 0,
        AutomationSecurity: 3);
}

internal interface IExcelProcessCatalog
{
    ExcelProcessSnapshot CaptureSnapshot();

    ExcelProcessIdentity ResolveOwnedProcess(
        long applicationHwnd,
        DateTimeOffset activationStartedUtc,
        ExcelProcessSnapshot baseline);

    bool WaitForExitExact(ExcelProcessIdentity identity, TimeSpan timeout);

    bool TryKillExact(ExcelProcessIdentity identity);
}

internal interface IExcelAutomationSession : IDisposable
{
    long ApplicationHwnd { get; }

    string? OfficeVersion { get; }

    void OpenWorkbook(string path, WorkbookOpenPolicy policy);

    bool IsWorkbookReadOnly { get; }

    int ExternalLinkCount { get; }

    long CountFormulaErrors();

    void CalculateFullRebuild();

    void SaveCopyAs(string path);

    void ExportFirstPagePdf(string path);

    void CloseWorkbook();

    void Quit();
}

internal interface IExcelDeadline : IDisposable
{
    bool TimedOut { get; }

    bool ProcessTerminated { get; }
}

internal readonly record struct PdfInspection(bool Parseable, long ByteCount);

internal interface IPdfInspector
{
    PdfInspection Inspect(string path);
}

internal sealed class ExcelDriverBlockedException(string code) : Exception
{
    internal string Code { get; } = code;
}

internal sealed class ExcelDriverAssertionException(string code) : Exception
{
    internal string Code { get; } = code;
}

internal sealed class ExcelUnavailableException : Exception
{
    internal ExcelUnavailableException()
    {
    }

    internal ExcelUnavailableException(Exception innerException)
        : base("Excel automation is unavailable.", innerException)
    {
    }
}

internal sealed class ExcelWorkbookOpenException(Exception innerException)
    : Exception("Excel rejected the workbook open request.", innerException);

internal sealed class ExcelProcessOwnershipException : Exception
{
    internal ExcelProcessOwnershipException()
    {
    }

    internal ExcelProcessOwnershipException(Exception innerException)
        : base("Excel process ownership could not be verified.", innerException)
    {
    }
}
