using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JET.Tests.Infrastructure;

internal enum PrivateCaseLegacyScenarioEvidenceFailure
{
    InvalidInput,
    WorkbookUnreadable,
    ScenarioCountMismatch,
    CountMismatch,
    ScenarioDefinitionMismatch,
}

internal sealed class PrivateCaseLegacyScenarioEvidenceException : InvalidOperationException
{
    internal PrivateCaseLegacyScenarioEvidenceException(
        PrivateCaseLegacyScenarioEvidenceFailure failure)
        : base($"私人案件的舊版篩選依據無法核對（{failure}）。")
    {
        Failure = failure;
    }

    internal PrivateCaseLegacyScenarioEvidenceFailure Failure { get; }
}

internal sealed class PrivateCaseLegacyScenarioEvidenceResult
{
    internal PrivateCaseLegacyScenarioEvidenceResult(
        int scenarioCount,
        IReadOnlyDictionary<int, IReadOnlySet<string>>? selectedVoucherNumbersByPosition = null)
    {
        ScenarioCount = scenarioCount;
        SelectedVoucherNumbersByPosition = selectedVoucherNumbersByPosition
            ?? new Dictionary<int, IReadOnlySet<string>>();
    }

    public int ScenarioCount { get; }

    [JsonIgnore]
    internal IReadOnlyDictionary<int, IReadOnlySet<string>> SelectedVoucherNumbersByPosition { get; }

    public bool Passed => true;

    public override string ToString() =>
        $"private case legacy scenario evidence ({ScenarioCount} scenarios)";
}

internal static class PrivateCaseLegacyScenarioEvidenceVerifier
{
    // idea-tool.bas 在匯入後把來源傳票欄統一改成這個名稱，Criteria 明細也沿用它。
    private const string LegacyDocumentNumberHeader = "傳票號碼_JE";

    internal static PrivateCaseLegacyScenarioEvidenceResult Verify(
        PrivateCaseManifest manifest,
        PrivateCaseExpectedReportSet expectedReports)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(expectedReports);

        var criteriaReports = expectedReports.Reports
            .Where(static report => report.Kind == LegacyReportKind.CriteriaSelectionReport)
            .Take(2)
            .ToArray();
        if (criteriaReports.Length != 1)
        {
            throw Error(PrivateCaseLegacyScenarioEvidenceFailure.InvalidInput);
        }

        try
        {
            var workbook = LegacyWorkbookSnapshot.Load(criteriaReports[0].FullPath);
            var summary = workbook.FindSingleSheet(
                sheet => sheet.Name.Equals("Summary Inforamtion", StringComparison.Ordinal)
                    || Enumerable.Range(5, 10).Any(row => sheet.Get(row, 1).StartsWith(
                        "Criteria Selection ",
                        StringComparison.Ordinal)),
                "private-case-criteria-summary");
            var verified = Verify(
                manifest.Scenarios,
                manifest.Legacy.ScenarioCounts,
                summary);
            return new PrivateCaseLegacyScenarioEvidenceResult(
                verified.ScenarioCount,
                ReadSelectedVoucherNumbers(manifest, workbook));
        }
        catch (PrivateCaseLegacyScenarioEvidenceException)
        {
            throw;
        }
        catch
        {
            throw Error(PrivateCaseLegacyScenarioEvidenceFailure.WorkbookUnreadable);
        }
    }

    internal static PrivateCaseLegacyScenarioEvidenceResult Verify(
        IReadOnlyList<JsonElement> scenarios,
        IReadOnlyList<PrivateCaseScenarioCount> expectedCounts,
        WorksheetSnapshot summary)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        ArgumentNullException.ThrowIfNull(expectedCounts);
        ArgumentNullException.ThrowIfNull(summary);
        if (scenarios.Count is < 1 or > 10
            || expectedCounts.Count != scenarios.Count
            || scenarios.Any(static scenario => scenario.ValueKind != JsonValueKind.Object)
            || !expectedCounts.Select(static count => count.Position)
                .SequenceEqual(Enumerable.Range(1, scenarios.Count)))
        {
            throw Error(PrivateCaseLegacyScenarioEvidenceFailure.InvalidInput);
        }

        var found = 0;
        for (var row = 5; row <= 14; row++)
        {
            var log = summary.Get(row, 2);
            if (log.Length == 0)
            {
                continue;
            }

            var position = row - 4;
            if (position > scenarios.Count)
            {
                throw Error(PrivateCaseLegacyScenarioEvidenceFailure.ScenarioCountMismatch);
            }
            found++;

            var expected = expectedCounts[position - 1];
            var voucherCount = ParseCount(summary.Get(row, 3), allowOverflowMarker: true);
            var rowCount = ParseCount(summary.Get(row, 4), allowOverflowMarker: false);
            if (voucherCount != expected.VoucherCount || rowCount != expected.RowCount)
            {
                throw Error(PrivateCaseLegacyScenarioEvidenceFailure.CountMismatch);
            }

            var parsed = LegacyCriteriaLogParser.TryParseUsingExpectedShape(
                log,
                $"private-scenario-{position:D2}",
                "private case comparison",
                $"private-scenario-{position:D2}",
                scenarios[position - 1]);
            if (parsed.Scenario is not JsonElement parsedScenario
                || !TryGroups(parsedScenario, out var parsedGroups)
                || !TryGroups(scenarios[position - 1], out var expectedGroups)
                || !JsonElement.DeepEquals(parsedGroups, expectedGroups))
            {
                throw Error(PrivateCaseLegacyScenarioEvidenceFailure.ScenarioDefinitionMismatch);
            }
        }

        if (found != scenarios.Count)
        {
            throw Error(PrivateCaseLegacyScenarioEvidenceFailure.ScenarioCountMismatch);
        }

        return new PrivateCaseLegacyScenarioEvidenceResult(found);
    }

    private static IReadOnlyDictionary<int, IReadOnlySet<string>> ReadSelectedVoucherNumbers(
        PrivateCaseManifest manifest,
        LegacyWorkbookSnapshot workbook)
    {
        var result = new Dictionary<int, IReadOnlySet<string>>();
        foreach (var expected in manifest.Legacy.ScenarioCounts)
        {
            var sheet = workbook.FindSingleSheet(
                candidate => candidate.Name.Equals(
                    $"#Criteria Select {expected.Position}",
                    StringComparison.Ordinal),
                $"private-case-criteria-detail-{expected.Position:D2}");
            var headerCells = sheet.FindCells(LegacyDocumentNumberHeader)
                .Where(static cell => cell.Row is >= 1 and <= 10)
                .ToArray();
            if (headerCells.Length != 1)
            {
                throw Error(PrivateCaseLegacyScenarioEvidenceFailure.WorkbookUnreadable);
            }

            var firstDataRow = headerCells[0].Row + 1;
            var documentNumbers = Enumerable.Range(
                    firstDataRow,
                    Math.Max(0, sheet.MaxRow - firstDataRow + 1))
                .Select(row => sheet.Get(row, headerCells[0].Column).Trim())
                .Where(static value => value.Length > 0)
                .ToHashSet(StringComparer.Ordinal);
            if (expected.VoucherCount.HasValue
                && documentNumbers.Count != expected.VoucherCount.Value)
            {
                throw Error(PrivateCaseLegacyScenarioEvidenceFailure.CountMismatch);
            }
            result.Add(expected.Position, documentNumbers);
        }

        return result;
    }

    private static long? ParseCount(string value, bool allowOverflowMarker)
    {
        if (long.TryParse(
                value.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var count)
            && count >= 0)
        {
            return count;
        }

        if (allowOverflowMarker && value.StartsWith("Over ", StringComparison.Ordinal))
        {
            return null;
        }

        throw Error(PrivateCaseLegacyScenarioEvidenceFailure.WorkbookUnreadable);
    }

    private static bool TryGroups(JsonElement scenario, out JsonElement groups) =>
        scenario.TryGetProperty("groups", out groups)
        && groups.ValueKind == JsonValueKind.Array;

    private static PrivateCaseLegacyScenarioEvidenceException Error(
        PrivateCaseLegacyScenarioEvidenceFailure failure) => new(failure);
}
