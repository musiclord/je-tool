using System.Text.Json.Serialization;

namespace JET.Tests.Infrastructure;

internal enum PrivateCaseReportPairingFailure
{
    InvalidInput,
    MissingReport,
    DuplicateReport,
}

internal sealed class PrivateCaseReportPairingException : InvalidOperationException
{
    internal PrivateCaseReportPairingException(PrivateCaseReportPairingFailure failure)
        : base($"無法配對私人案件的六份底稿（{failure}）。")
    {
        Failure = failure;
    }

    internal PrivateCaseReportPairingFailure Failure { get; }
}

internal sealed record PrivateCaseReportPair(
    LegacyReportKind Kind,
    [property: JsonIgnore] string ExpectedFullPath,
    [property: JsonIgnore] string ActualFullPath)
{
    public override string ToString() => $"private case report pair ({Kind})";
}

internal sealed class PrivateCaseReportPairSet
{
    private PrivateCaseReportPairSet(IReadOnlyList<PrivateCaseReportPair> pairs)
    {
        Pairs = pairs;
        ReportKinds = pairs.Select(static pair => pair.Kind).ToArray();
    }

    public int ReportCount => Pairs.Count;

    public IReadOnlyList<LegacyReportKind> ReportKinds { get; }

    [JsonIgnore]
    internal IReadOnlyList<PrivateCaseReportPair> Pairs { get; }

    public override string ToString() =>
        $"private case report pair set ({ReportCount} reports)";

    internal static PrivateCaseReportPairSet Create(
        PrivateCaseExpectedReportSet expectedReports,
        PrivateCaseActualReportSet actualReports)
    {
        ArgumentNullException.ThrowIfNull(expectedReports);
        ArgumentNullException.ThrowIfNull(actualReports);

        var requiredKinds = Enum.GetValues<LegacyReportKind>();
        var expected = expectedReports.Reports;
        var actual = actualReports.Reports;
        if (expected.Count != requiredKinds.Length
            || requiredKinds.Any(kind =>
                expected.Count(report => report.Kind == kind) != 1
                || !IsSafeInternalPath(expected.Single(report => report.Kind == kind).FullPath)))
        {
            throw Error(PrivateCaseReportPairingFailure.MissingReport);
        }
        if (actual.Count != requiredKinds.Length
            || requiredKinds.Any(kind =>
                actual.Count(report => report.Kind == kind) != 1
                || !IsSafeInternalPath(actual.Single(report => report.Kind == kind).FullPath)))
        {
            throw Error(PrivateCaseReportPairingFailure.MissingReport);
        }

        var pairs = requiredKinds
            .Select(kind => new PrivateCaseReportPair(
                kind,
                expected.Single(report => report.Kind == kind).FullPath,
                actual.Single(report => report.Kind == kind).FullPath))
            .ToArray();
        return new PrivateCaseReportPairSet(pairs);
    }

    /// <summary>
    /// 合成資料沒有舊版標準底稿；後續操作改拿第一次 JET 匯出的副本當基準，用同一個比較器確認報告不變。
    /// </summary>
    internal static PrivateCaseReportPairSet CreateFromJetBaseline(
        PrivateCaseActualReportSet baselineReports,
        PrivateCaseActualReportSet actualReports)
    {
        ArgumentNullException.ThrowIfNull(baselineReports);
        ArgumentNullException.ThrowIfNull(actualReports);

        var requiredKinds = Enum.GetValues<LegacyReportKind>();
        foreach (var reports in new[] { baselineReports.Reports, actualReports.Reports })
        {
            if (reports.Count != requiredKinds.Length
                || requiredKinds.Any(kind =>
                    reports.Count(report => report.Kind == kind) != 1
                    || !IsSafeInternalPath(reports.Single(report => report.Kind == kind).FullPath)))
            {
                throw Error(PrivateCaseReportPairingFailure.MissingReport);
            }
        }

        var pairs = requiredKinds
            .Select(kind => new PrivateCaseReportPair(
                kind,
                baselineReports.Reports.Single(report => report.Kind == kind).FullPath,
                actualReports.Reports.Single(report => report.Kind == kind).FullPath))
            .ToArray();
        return new PrivateCaseReportPairSet(pairs);
    }

    private static bool IsSafeInternalPath(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Any(char.IsControl);

    private static PrivateCaseReportPairingException Error(
        PrivateCaseReportPairingFailure failure) => new(failure);
}
