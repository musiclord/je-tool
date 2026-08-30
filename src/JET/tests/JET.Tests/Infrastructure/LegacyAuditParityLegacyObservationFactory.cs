using System.Globalization;

namespace JET.Tests.Infrastructure;

internal static class LegacyAuditParityLegacyObservationFactory
{
    private const int InfFirstDataRow = 53;

    internal static LegacyAuditParityObservation FromProfile(
        LegacyParityCase @case,
        LegacyAuditParityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        try
        {
            var validation = LegacyWorkbookSnapshot.Load(
                RequiredReport(profile, LegacyReportKind.ValidationReport));
            var validationSummary = validation.FindSingleSheet(
                sheet => sheet.Name.Equals("ValidationReport", StringComparison.Ordinal),
                "legacy-validation-summary");
            var completeness = new LegacyCompletenessObservation(
                ReadLong(validationSummary.Get(24, 4), "legacy-validation-difference-count"),
                partASourceRowCount: null,
                ReadLong(validationSummary.Get(13, 5), "legacy-validation-target-row-count"),
                ReadDecimal(validationSummary.Get(11, 5), "legacy-validation-total-debit"),
                ReadDecimal(validationSummary.Get(12, 5), "legacy-validation-total-credit"),
                partARowCountMatch: null,
                partAAmountMatch: null);
            var workingPaper = ReadUnbalancedVoucherObservation(
                validationSummary,
                RequiredReport(profile, LegacyReportKind.WorkingPaper));
            var unbalanced = workingPaper.DistinctUnbalancedVoucherCount;

            var infWorkbook = LegacyWorkbookSnapshot.Load(
                RequiredReport(profile, LegacyReportKind.InfReport));
            var inf = ReadInf(infWorkbook);
            var prescreen = ReadPrescreen(
                LegacyWorkbookSnapshot.Load(
                    RequiredReport(profile, LegacyReportKind.PrescreenReport)));
            var filters = ReadFilterScenarios(profile.LegacyScenarioCounts);
            var reports = profile.LegacyReports.ToDictionary(
                pair => pair.Key,
                pair => new LegacyReportObservation(
                    pair.Key == LegacyReportKind.WorkingPaper
                        ? workingPaper.DataRows
                        : LegacyAuditParityObservationFactory.ReadReportDataRows(
                            pair.Key,
                            pair.Value,
                            LegacyAuditParityReportSource.LegacySystem)));
            var metrics = new LegacyAuditParityMetrics(
                completeness,
                unbalanced,
                inf,
                prescreen.Rules,
                filters,
                reports,
                weekendUnion: new LegacyRowVoucherCounts(
                    LegacyObservedCount.NotExecuted,
                    LegacyObservedCount.NotExecuted),
                creatorSummaryLegacyApplicability: prescreen.CreatorSummaryApplicability,
                rareAccountsLegacyApplicability: prescreen.RareAccountsApplicability);
            return LegacyAuditParityObservation.FromLegacy(@case, metrics);
        }
        catch (LegacyAuditParityObservationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Error("legacy-read-failed");
        }
    }

    internal static IReadOnlyDictionary<LegacyFilterScenarioId, LegacyRowVoucherCounts>
        ReadFilterScenarios(IReadOnlyList<LegacyScenarioCount> scenarios)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        return scenarios.ToDictionary(
                scenario => LegacyFilterScenarioId.FromOrdinal(scenario.Position),
                scenario => new LegacyRowVoucherCounts(
                    LegacyObservedCount.Executed(scenario.RowCount),
                    scenario.VoucherCount.HasValue
                        ? LegacyObservedCount.Executed(scenario.VoucherCount.Value)
                        : LegacyObservedCount.ExecutedUnavailable));
    }

    internal static LegacyAuditParityLegacyWorkingPaperReadResult ReadUnbalancedVoucherObservation(
        WorksheetSnapshot validationSummary,
        string workingPaperPath)
    {
        // Legacy Validation!D25 is the raw GL detail-row count used by the V6
        // export gate. Parse it only as a shape cross-check; the distinct voucher
        // oracle comes from the step1-1 voucher column in the Working Paper.
        _ = ReadLong(
            validationSummary.Get(25, 4),
            "legacy-validation-unbalanced-detail-row-count");
        return LegacyAuditParityObservationFactory.ReadLegacyWorkingPaper(workingPaperPath);
    }

    internal static LegacyInfObservation ReadInf(LegacyWorkbookSnapshot workbook)
    {
        var sheet = workbook.FindSingleSheet(
            candidate => candidate.Name.Equals("INF Testing 可靠性測試", StringComparison.Ordinal),
            "legacy-inf-sheet");
        var keys = new List<LegacyInfMemberKey>();
        var encounteredGap = false;
        // The two repository legacy writers disagree by one sample: idea-tool.bas
        // writes 59 rows (53-111), while idea-script.bas writes 60 (53-112).
        // Both are valid legacy observations and remain distinguishable by the
        // captured sample-size/member metrics.
        const int lastDataRow = 112;
        for (var row = InfFirstDataRow; row <= lastDataRow; row++)
        {
            var hasData = Enumerable.Range(2, 11).Any(column => sheet.Get(row, column).Length > 0);
            if (!hasData)
            {
                encounteredGap = keys.Count > 0;
                continue;
            }
            if (encounteredGap)
            {
                throw Error("legacy-inf-contiguous-rows");
            }

            var amount = ReadDecimal(sheet.Get(row, 6), "legacy-inf-amount");
            var debit = amount >= 0 ? CanonicalAmount(amount) : CanonicalAmount(0);
            var credit = amount < 0 ? CanonicalAmount(decimal.Negate(amount)) : CanonicalAmount(0);
            keys.Add(LegacyInfMemberKey.Create(
                NullIfBlank(sheet.Get(row, 2)),
                NullIfBlank(sheet.Get(row, 3)),
                NullIfBlank(sheet.Get(row, 4)),
                debit,
                credit,
                CanonicalDate(sheet.Get(row, 7), "legacy-inf-post-date"),
                CanonicalDate(sheet.Get(row, 8), "legacy-inf-approval-date"),
                NullIfBlank(sheet.Get(row, 9)),
                NullIfBlank(sheet.Get(row, 12)),
                NullIfBlank(sheet.Get(row, 11))));
        }

        for (var row = lastDataRow + 1; row <= sheet.MaxRow; row++)
        {
            if (Enumerable.Range(2, 11).Any(column => sheet.Get(row, column).Length > 0))
            {
                throw Error("legacy-inf-outside-writer-area");
            }
        }

        return new LegacyInfObservation(keys.Count, keys);
    }

    internal static LegacyPrescreenObservation ReadPrescreen(
        LegacyWorkbookSnapshot workbook)
    {
        var sheet = workbook.FindSingleSheet(
            candidate => candidate.Name.Equals("Pre-screening_Report", StringComparison.Ordinal),
            "legacy-prescreen-summary");
        var result = LegacyPrescreenRuleCatalog.All.ToDictionary(
            rule => rule,
            _ => new LegacyRowVoucherCounts(
                LegacyObservedCount.NotExecuted,
                LegacyObservedCount.NotExecuted));
        var direct = new[]
        {
            (LegacyPrescreenRuleId.PostPeriodApproval, 8),
            (LegacyPrescreenRuleId.SuspiciousKeywords, 9),
            (LegacyPrescreenRuleId.UnexpectedAccountPair, 10),
            (LegacyPrescreenRuleId.TrailingZeros, 11),
            (LegacyPrescreenRuleId.BlankDescription, 17),
        };
        foreach (var (rule, row) in direct)
        {
            result[rule] = ReadRowVoucherPair(
                sheet.Get(row, 6),
                sheet.Get(row, 5),
                $"legacy-prescreen-{rule}");
        }
        var creatorSummary = ReadRowVoucherPair(
            sheet.Get(12, 6),
            sheet.Get(12, 5),
            "legacy-prescreen-creator-summary");
        var rareAccounts = ReadRowVoucherPair(
            sheet.Get(13, 6),
            sheet.Get(13, 5),
            "legacy-prescreen-rare-accounts");
        if (creatorSummary.Applicability != LegacyMetricApplicability.NotApplicable
            || rareAccounts.Applicability != LegacyMetricApplicability.NotApplicable)
        {
            throw Error("legacy-prescreen-aggregate-applicability");
        }
        return new LegacyPrescreenObservation(
            result,
            creatorSummary.Applicability,
            rareAccounts.Applicability);
    }

    private static LegacyRowVoucherCounts ReadRowVoucherPair(
        string rowValue,
        string voucherValue,
        string fieldId)
    {
        var rowBlank = string.IsNullOrWhiteSpace(rowValue);
        var voucherBlank = string.IsNullOrWhiteSpace(voucherValue);
        if (rowBlank || voucherBlank)
        {
            if (rowBlank != voucherBlank)
            {
                throw Error(fieldId);
            }
            return new LegacyRowVoucherCounts(
                LegacyObservedCount.ExecutedUnavailable,
                LegacyObservedCount.ExecutedUnavailable);
        }

        var rowNa = IsNotApplicable(rowValue);
        var voucherNa = IsNotApplicable(voucherValue);
        if (rowNa || voucherNa)
        {
            if (rowNa != voucherNa)
            {
                throw Error(fieldId);
            }
            return LegacyRowVoucherCounts.NotApplicable();
        }
        return new LegacyRowVoucherCounts(
            ReadCountState(rowValue, fieldId),
            ReadCountState(voucherValue, fieldId));
    }

    private static LegacyObservedCount ReadCountState(string value, string fieldId)
    {
        var normalized = value.Trim();
        if (long.TryParse(
                normalized.Replace(",", string.Empty, StringComparison.Ordinal),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var count)
            && count >= 0)
        {
            return LegacyObservedCount.Executed(count);
        }
        if (normalized.StartsWith("Over ", StringComparison.Ordinal)
            && long.TryParse(
                normalized[5..].Replace(",", string.Empty, StringComparison.Ordinal),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var threshold)
            && threshold > 0)
        {
            return LegacyObservedCount.ExecutedUnavailable;
        }
        throw Error(fieldId);
    }

    private static string RequiredReport(
        LegacyAuditParityProfile profile,
        LegacyReportKind report) =>
        profile.LegacyReports.TryGetValue(report, out var path) && File.Exists(path)
            ? path
            : throw Error($"legacy-report-{report}");

    private static long ReadLong(string value, string fieldId) =>
        long.TryParse(
            value.Replace(",", string.Empty, StringComparison.Ordinal),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var result)
            && result >= 0
                ? result
                : throw Error(fieldId);

    private static decimal ReadDecimal(string value, string fieldId) =>
        decimal.TryParse(
            value.Replace(",", string.Empty, StringComparison.Ordinal),
            NumberStyles.Number | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture,
            out var result)
                ? result
                : throw Error(fieldId);

    private static string? CanonicalDate(string value, string fieldId)
    {
        var normalized = value.Trim().TrimStart('\'');
        if (normalized.Length == 0)
        {
            return null;
        }
        string[] exactFormats = ["yyyy/M/d", "yyyy/MM/dd", "yyyy-M-d", "yyyy-MM-dd", "yyyyMMdd"];
        if (DateOnly.TryParseExact(
                normalized,
                exactFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var exactDate))
        {
            return exactDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        if (double.TryParse(
                normalized,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var serial)
            && serial is >= 1 and <= 2_958_465)
        {
            try
            {
                return DateOnly.FromDateTime(DateTime.FromOADate(serial))
                    .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            catch (ArgumentException)
            {
                throw Error(fieldId);
            }
        }
        if (DateTime.TryParse(
                normalized,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            return DateOnly.FromDateTime(date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        throw Error(fieldId);
    }

    private static string CanonicalAmount(decimal value) =>
        value.ToString("G29", CultureInfo.InvariantCulture);

    private static string? NullIfBlank(string value) => value.Length == 0 ? null : value;

    private static bool IsNotApplicable(string value) =>
        value.Equals("N/A", StringComparison.OrdinalIgnoreCase);

    private static LegacyAuditParityObservationException Error(string fieldId) => new(fieldId);

    internal sealed record LegacyPrescreenObservation(
        IReadOnlyDictionary<LegacyPrescreenRuleId, LegacyRowVoucherCounts> Rules,
        LegacyMetricApplicability CreatorSummaryApplicability,
        LegacyMetricApplicability RareAccountsApplicability);
}
