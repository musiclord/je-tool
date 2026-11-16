using System.Text.Json.Serialization;

namespace JET.Tests.Infrastructure;

internal enum PrivateCaseExpectedReportFailure
{
    InvalidInput,
    PrivateFileUnavailable,
    WorkspaceRejected,
}

internal sealed class PrivateCaseExpectedReportException : InvalidOperationException
{
    internal PrivateCaseExpectedReportException(PrivateCaseExpectedReportFailure failure)
        : base($"無法準備私人案件的標準底稿副本（{failure}）。")
    {
        Failure = failure;
    }

    internal PrivateCaseExpectedReportFailure Failure { get; }
}

internal sealed record PrivateCaseExpectedReport(
    LegacyReportKind Kind,
    [property: JsonIgnore] string FileName,
    [property: JsonIgnore] string FullPath,
    [property: JsonIgnore] long Length)
{
    public override string ToString() => $"private case expected report ({Kind})";
}

internal sealed class PrivateCaseExpectedReportSet
{
    private PrivateCaseExpectedReportSet(IReadOnlyList<PrivateCaseExpectedReport> reports)
    {
        Reports = reports;
        ReportKinds = reports.Select(static report => report.Kind).ToArray();
    }

    public int ReportCount => Reports.Count;

    public IReadOnlyList<LegacyReportKind> ReportKinds { get; }

    [JsonIgnore]
    internal IReadOnlyList<PrivateCaseExpectedReport> Reports { get; }

    public override string ToString() =>
        $"private case expected report set ({ReportCount} reports)";

    internal static PrivateCaseExpectedReportSet Create(
        PrivateCaseManifest manifest,
        string authorizedRootPath,
        PrivateCaseInputWorkspace workspace,
        PrivateCaseFileAccess? fileAccess = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizedRootPath);
        ArgumentNullException.ThrowIfNull(workspace);
        fileAccess ??= new PrivateCaseFileAccess();

        var requiredKinds = Enum.GetValues<LegacyReportKind>();
        var configured = manifest.Legacy.Reports;
        if (configured.Count != requiredKinds.Length
            || requiredKinds.Any(kind =>
                !configured.TryGetValue(kind, out var path)
                || string.IsNullOrWhiteSpace(path)
                || !string.Equals(
                    System.IO.Path.GetExtension(path),
                    ".xlsx",
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw Error(PrivateCaseExpectedReportFailure.InvalidInput);
        }

        try
        {
            var reports = new List<PrivateCaseExpectedReport>(requiredKinds.Length);
            for (var index = 0; index < requiredKinds.Length; index++)
            {
                var kind = requiredKinds[index];
                using var opened = fileAccess.OpenRead(
                    authorizedRootPath,
                    configured[kind]);
                var copy = workspace.CopyInput(
                    opened,
                    PrivateCaseInputRole.ExpectedReport,
                    index + 1,
                    ".xlsx");
                reports.Add(new PrivateCaseExpectedReport(
                    kind,
                    copy.FileName,
                    copy.FullPath,
                    copy.Length));
            }
            return new PrivateCaseExpectedReportSet(reports);
        }
        catch (PrivateCaseExpectedReportException)
        {
            throw;
        }
        catch (PrivateCaseFileAccessException)
        {
            throw Error(PrivateCaseExpectedReportFailure.PrivateFileUnavailable);
        }
        catch (PrivateCaseInputWorkspaceException)
        {
            throw Error(PrivateCaseExpectedReportFailure.WorkspaceRejected);
        }
        catch (ObjectDisposedException)
        {
            throw Error(PrivateCaseExpectedReportFailure.WorkspaceRejected);
        }
    }

    private static PrivateCaseExpectedReportException Error(
        PrivateCaseExpectedReportFailure failure) => new(failure);
}
