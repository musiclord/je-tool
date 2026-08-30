using System.Text.Json.Serialization;

namespace JET.Tests.Infrastructure;

internal enum PrivateCaseActualReportFailure
{
    InvalidInput,
    GeneratedFileUnavailable,
    WorkspaceRejected,
}

internal sealed class PrivateCaseActualReportException : InvalidOperationException
{
    internal PrivateCaseActualReportException(PrivateCaseActualReportFailure failure)
        : base($"無法準備私人案件的 JET 輸出副本（{failure}）。")
    {
        Failure = failure;
    }

    internal PrivateCaseActualReportFailure Failure { get; }
}

internal sealed record PrivateCaseActualReport(
    LegacyReportKind Kind,
    [property: JsonIgnore] string FileName,
    [property: JsonIgnore] string FullPath,
    [property: JsonIgnore] long Length)
{
    public override string ToString() => $"private case actual report ({Kind})";
}

/// <summary>
/// Copies the six JET outputs from the owned project directory into the private-case workspace.
/// Later comparison code receives only these fixed-name copies and never reopens an artifact path.
/// </summary>
internal sealed class PrivateCaseActualReportSet
{
    private PrivateCaseActualReportSet(IReadOnlyList<PrivateCaseActualReport> reports)
    {
        Reports = reports;
        ReportKinds = reports.Select(static report => report.Kind).ToArray();
    }

    public int ReportCount => Reports.Count;

    public IReadOnlyList<LegacyReportKind> ReportKinds { get; }

    [JsonIgnore]
    internal IReadOnlyList<PrivateCaseActualReport> Reports { get; }

    public override string ToString() =>
        $"private case actual report set ({ReportCount} reports)";

    internal static PrivateCaseActualReportSet Create(
        IReadOnlyList<LegacyAuditParityJourneyArtifact> artifacts,
        string generatedRootPath,
        PrivateCaseInputWorkspace workspace,
        PrivateCaseFileAccess? fileAccess = null)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentException.ThrowIfNullOrWhiteSpace(generatedRootPath);
        ArgumentNullException.ThrowIfNull(workspace);
        fileAccess ??= new PrivateCaseFileAccess();

        var requiredKinds = Enum.GetValues<LegacyReportKind>();
        if (!TryIndexArtifacts(artifacts, generatedRootPath, requiredKinds, out var indexed))
        {
            throw Error(PrivateCaseActualReportFailure.InvalidInput);
        }

        try
        {
            var reports = new List<PrivateCaseActualReport>(requiredKinds.Length);
            for (var index = 0; index < requiredKinds.Length; index++)
            {
                var kind = requiredKinds[index];
                var artifact = indexed[kind];
                using var opened = fileAccess.OpenRead(generatedRootPath, artifact.FileName);
                var copy = workspace.CopyInput(
                    opened,
                    PrivateCaseInputRole.ActualReport,
                    index + 1,
                    ".xlsx");
                if (copy.Length <= 0)
                {
                    throw Error(PrivateCaseActualReportFailure.GeneratedFileUnavailable);
                }

                reports.Add(new PrivateCaseActualReport(
                    kind,
                    copy.FileName,
                    copy.FullPath,
                    copy.Length));
            }
            return new PrivateCaseActualReportSet(reports);
        }
        catch (PrivateCaseActualReportException)
        {
            throw;
        }
        catch (PrivateCaseFileAccessException)
        {
            throw Error(PrivateCaseActualReportFailure.GeneratedFileUnavailable);
        }
        catch (PrivateCaseInputWorkspaceException)
        {
            throw Error(PrivateCaseActualReportFailure.WorkspaceRejected);
        }
        catch (ObjectDisposedException)
        {
            throw Error(PrivateCaseActualReportFailure.WorkspaceRejected);
        }
    }

    private static bool TryIndexArtifacts(
        IReadOnlyList<LegacyAuditParityJourneyArtifact> artifacts,
        string generatedRootPath,
        IReadOnlyList<LegacyReportKind> requiredKinds,
        out IReadOnlyDictionary<LegacyReportKind, LegacyAuditParityJourneyArtifact> indexed)
    {
        var result = new Dictionary<LegacyReportKind, LegacyAuditParityJourneyArtifact>();
        if (artifacts.Count != requiredKinds.Count)
        {
            indexed = result;
            return false;
        }

        foreach (var artifact in artifacts)
        {
            if (artifact is null
                || !Enum.IsDefined(artifact.Kind)
                || !IsSafeLeafWorkbookName(artifact.FileName)
                || !MatchesGeneratedPath(generatedRootPath, artifact.FileName, artifact.FullPath)
                || !result.TryAdd(artifact.Kind, artifact))
            {
                indexed = result;
                return false;
            }
        }

        indexed = result;
        return requiredKinds.All(result.ContainsKey);
    }

    private static bool IsSafeLeafWorkbookName(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Any(char.IsControl)
        && string.Equals(value, System.IO.Path.GetFileName(value), StringComparison.Ordinal)
        && string.Equals(
            System.IO.Path.GetExtension(value),
            ".xlsx",
            StringComparison.OrdinalIgnoreCase);

    private static bool MatchesGeneratedPath(
        string rootPath,
        string fileName,
        string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)
            || fullPath.Any(char.IsControl)
            || !System.IO.Path.IsPathFullyQualified(rootPath)
            || !System.IO.Path.IsPathFullyQualified(fullPath))
        {
            return false;
        }

        try
        {
            var root = System.IO.Path.TrimEndingDirectorySeparator(
                System.IO.Path.GetFullPath(rootPath));
            var expected = System.IO.Path.GetFullPath(fileName, root);
            var actual = System.IO.Path.GetFullPath(fullPath);
            return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or System.Security.SecurityException
            or PathTooLongException)
        {
            return false;
        }
    }

    private static PrivateCaseActualReportException Error(
        PrivateCaseActualReportFailure failure) => new(failure);
}
