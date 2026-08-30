using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Application;
using JET.Domain;

namespace JET.Tests.Infrastructure;

internal sealed class LegacyAuditParityObservationException(string fieldId)
    : InvalidOperationException($"legacy-audit-parity-observation:{fieldId}")
{
    public string FieldId { get; } = fieldId;
}

internal enum LegacyAuditParityReportSource
{
    JetGenerated,
    LegacySystem,
}

internal sealed record LegacyAuditParityLegacyWorkingPaperReadResult(
    IReadOnlyList<KeyValuePair<string, long>> DataRows,
    long DistinctUnbalancedVoucherCount);

internal static class LegacyAuditParityObservationFactory
{
    internal static LegacyAuditParityObservation FromResponses(
        LegacyParityCase @case,
        LegacyAuditParityProvider provider,
        JsonElement validation,
        JsonElement prescreen,
        JsonElement tagMatrix,
        IReadOnlyList<JsonElement> infSampleRows,
        IReadOnlyList<LegacyAuditParityJourneyArtifact> artifacts,
        LegacyRowVoucherCounts weekendUnion,
        IReadOnlyList<LegacyFilterScenarioId> legacyFilterScenarioIds)
    {
        try
        {
            var completeness = ReadCompleteness(validation);
            var unbalancedVoucherCount = RequiredInt64(
                RequiredObject(validation, "docBalanceTest", "validation.doc-balance"),
                "unbalancedDocumentCount",
                "validation.doc-balance.count");
            var inf = ReadInf(validation, infSampleRows);
            var prescreenRules = ReadPrescreen(prescreen);
            var filterScenarios = ReadFilterScenarios(tagMatrix, legacyFilterScenarioIds);
            var reports = ReadReports(artifacts).ToDictionary(pair => pair.Key, pair => pair.Value);
            reports[LegacyReportKind.CriteriaSelectionReport] = reports[
                LegacyReportKind.CriteriaSelectionReport]
                .RebindCompactCriteriaScenarioPositions(legacyFilterScenarioIds);
            var metrics = new LegacyAuditParityMetrics(
                completeness,
                unbalancedVoucherCount,
                inf,
                prescreenRules,
                filterScenarios,
                reports,
                weekendUnion);
            return LegacyAuditParityObservation.FromProvider(@case, provider, metrics);
        }
        catch (LegacyAuditParityObservationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Error("read-failed");
        }
    }

    private static LegacyCompletenessObservation ReadCompleteness(JsonElement validation)
    {
        // Parity capture is a consumer of the same persisted/current validation-v4 carrier as
        // resume and formal reports. Keep the production strict guard in the path so a partial
        // synthetic summary cannot be accepted by this observation-only adapter.
        ValidationSummaryShapeValidator.Require(validation);
        var completeness = RequiredObject(
            validation,
            "completenessTest",
            "validation.completeness");
        var partA = RequiredObject(completeness, "partA", "validation.completeness.part-a");
        var eligibleSource = partA.GetProperty("eligibleSource");
        var effectiveTarget = partA.GetProperty("effectiveTarget");
        var partAUnavailable = eligibleSource.ValueKind == JsonValueKind.Null;
        return new LegacyCompletenessObservation(
            RequiredInt64(
                completeness,
                "diffAccountCount",
                "validation.completeness.diff-account-count"),
            partAUnavailable
                ? null
                : RequiredInt64(eligibleSource, "rowCount", "validation.completeness.part-a.source"),
            partAUnavailable
                ? null
                : RequiredInt64(effectiveTarget, "rowCount", "validation.completeness.part-a.target"),
            partAUnavailable
                ? null
                : NullableDecimal(effectiveTarget, "totalDebit", "validation.completeness.part-a.debit"),
            partAUnavailable
                ? null
                : NullableDecimal(effectiveTarget, "totalCredit", "validation.completeness.part-a.credit"),
            NullableBoolean(
                partA,
                "rowCountMatch",
                "validation.completeness.part-a.row-match"),
            NullableBoolean(
                partA,
                "amountMatch",
                "validation.completeness.part-a.amount-match"));
    }

    private static LegacyInfObservation ReadInf(
        JsonElement validation,
        IReadOnlyList<JsonElement> rows)
    {
        var inf = RequiredObject(validation, "infSamplingTest", "validation.inf");
        var sampleSize = RequiredInt64(inf, "sampleSize", "validation.inf.sample-size");
        var keys = rows.Select(row => LegacyInfMemberKey.Create(
            NullableString(row, "documentNumber", "validation.inf.member.document"),
            NullableString(row, "accountCode", "validation.inf.member.account-code"),
            NullableString(row, "accountName", "validation.inf.member.account-name"),
            NullableNumberText(row, "debit", "validation.inf.member.debit"),
            NullableNumberText(row, "credit", "validation.inf.member.credit"),
            NullableString(row, "postDate", "validation.inf.member.post-date"),
            NullableString(row, "approvalDate", "validation.inf.member.approval-date"),
            NullableString(row, "createdBy", "validation.inf.member.created-by"),
            NullableString(row, "approvedBy", "validation.inf.member.approved-by"),
            NullableString(row, "description", "validation.inf.member.description")));
        return new LegacyInfObservation(sampleSize, keys);
    }

    internal static IReadOnlyDictionary<LegacyPrescreenRuleId, LegacyRowVoucherCounts> ReadPrescreen(
        JsonElement prescreen)
    {
        var rulePeriod = RequiredObject(prescreen, "rulePeriod", "prescreen.rule-period");
        var rules = RequiredArray(rulePeriod, "rules", "prescreen.rule-period.rules");
        var result = new Dictionary<LegacyPrescreenRuleId, LegacyRowVoucherCounts>();
        foreach (var rule in rules.EnumerateArray())
        {
            var id = LegacyPrescreenRuleCatalog.FromWireKey(
                RequiredString(rule, "key", "prescreen.rule-period.key"));
            var naReason = NullableString(
                rule,
                "naReason",
                "prescreen.rule-period.na-reason");
            var counts = naReason is null
                ? new LegacyRowVoucherCounts(
                    RequiredInt64(rule, "hitLines", "prescreen.rule-period.hit-lines"),
                    RequiredInt64(rule, "hitVouchers", "prescreen.rule-period.hit-vouchers"))
                : ReadNotApplicableRule(rule);
            if (!result.TryAdd(
                    id,
                    counts))
            {
                throw Error("prescreen.rule-period.duplicate-key");
            }
        }
        return result;
    }

    private static LegacyRowVoucherCounts ReadNotApplicableRule(JsonElement rule)
    {
        if (NullableInt64(rule, "hitLines", "prescreen.rule-period.hit-lines") is not null
            || NullableInt64(rule, "hitVouchers", "prescreen.rule-period.hit-vouchers") is not null)
        {
            throw Error("prescreen.rule-period.na-counts");
        }

        return LegacyRowVoucherCounts.NotApplicable();
    }

    internal static IReadOnlyDictionary<LegacyFilterScenarioId, LegacyRowVoucherCounts>
        ReadFilterScenarios(
            JsonElement tagMatrix,
            IReadOnlyList<LegacyFilterScenarioId> legacyFilterScenarioIds)
    {
        ArgumentNullException.ThrowIfNull(legacyFilterScenarioIds);
        var scenarios = RequiredArray(tagMatrix, "scenarios", "filter.tag-matrix.scenarios");
        if (legacyFilterScenarioIds.Count != scenarios.GetArrayLength()
            || legacyFilterScenarioIds.Any(id => !id.IsValid)
            || legacyFilterScenarioIds.Distinct().Count() != legacyFilterScenarioIds.Count)
        {
            throw Error("filter.tag-matrix.scenario-identities");
        }
        var result = new Dictionary<LegacyFilterScenarioId, LegacyRowVoucherCounts>();
        foreach (var scenario in scenarios.EnumerateArray())
        {
            var position = checked((int)RequiredInt64(
                scenario,
                "position",
                "filter.tag-matrix.position"));
            var runtimeId = LegacyFilterScenarioId.FromOrdinal(position);
            if (position > scenarios.GetArrayLength())
            {
                throw Error("filter.tag-matrix.position");
            }
            var id = legacyFilterScenarioIds[position - 1];
            if (!result.TryAdd(
                    id,
                    new LegacyRowVoucherCounts(
                        RequiredInt64(
                            scenario,
                            "rowHitCount",
                            "filter.tag-matrix.row-count"),
                        RequiredInt64(
                            scenario,
                            "voucherHitCount",
                            "filter.tag-matrix.voucher-count"))))
            {
                throw Error("filter.tag-matrix.duplicate-position");
            }
        }
        return result;
    }

    private static IReadOnlyDictionary<LegacyReportKind, LegacyReportObservation> ReadReports(
        IReadOnlyList<LegacyAuditParityJourneyArtifact> artifacts)
    {
        var reports = new Dictionary<LegacyReportKind, LegacyReportObservation>();
        foreach (var artifact in artifacts)
        {
            if (!reports.TryAdd(artifact.Kind, ReadReport(artifact.Kind, artifact.FullPath)))
            {
                throw Error("report.duplicate-kind");
            }
        }
        return reports;
    }

    private static LegacyReportObservation ReadReport(LegacyReportKind report, string path) =>
        new(ReadReportDataRows(report, path));

    internal static IReadOnlyList<KeyValuePair<string, long>> ReadReportDataRows(
        LegacyReportKind report,
        string path) => ReadReportDataRows(
            report,
            path,
            LegacyAuditParityReportSource.JetGenerated);

    internal static IReadOnlyList<KeyValuePair<string, long>> ReadReportDataRows(
        LegacyReportKind report,
        string path,
        LegacyAuditParityReportSource source) => ReadReportDataRowsCore(
            report,
            path,
            source).DataRows;

    internal static LegacyAuditParityLegacyWorkingPaperReadResult ReadLegacyWorkingPaper(
        string path)
    {
        var result = ReadReportDataRowsCore(
            LegacyReportKind.WorkingPaper,
            path,
            LegacyAuditParityReportSource.LegacySystem);
        return new LegacyAuditParityLegacyWorkingPaperReadResult(
            result.DataRows,
            result.DistinctUnbalancedVoucherCount
                ?? throw Error("report.unbalanced-voucher-count"));
    }

    private static ReportReadResult ReadReportDataRowsCore(
        LegacyReportKind report,
        string path,
        LegacyAuditParityReportSource source)
    {
        try
        {
            if (!Enum.IsDefined(report)
                || !Enum.IsDefined(source)
                || string.IsNullOrWhiteSpace(path))
            {
                throw Error("report.identity");
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var document = SpreadsheetDocument.Open(stream, isEditable: false);
            var workbookPart = document.WorkbookPart ?? throw Error("report.workbook");
            var unbalancedVouchers = report == LegacyReportKind.WorkingPaper
                && source == LegacyAuditParityReportSource.LegacySystem
                    ? new LegacyUnbalancedVoucherCollector()
                    : null;
            var voucherKeys = unbalancedVouchers is null
                ? VoucherKeyResolver.Empty
                : VoucherKeyResolver.Create(workbookPart);
            var sheets = new List<KeyValuePair<string, long>>();
            foreach (var sheet in workbookPart.Workbook.Sheets?.Elements<Sheet>() ?? [])
            {
                if (sheet.Id?.Value is not { } relationshipId
                    || !workbookPart.TryGetPartById(relationshipId, out var part)
                    || part is not WorksheetPart worksheetPart
                    || string.IsNullOrWhiteSpace(sheet.Name?.Value))
                {
                    throw Error("report.sheet");
                }

                var sheetName = sheet.Name.Value;
                if (source == LegacyAuditParityReportSource.JetGenerated
                    && string.Equals(
                        sheetName,
                        ReportWorkbookMetadataFormat.WorksheetName,
                        StringComparison.Ordinal))
                {
                    if (sheet.State?.Value != SheetStateValues.VeryHidden)
                    {
                        throw Error("report.metadata-sheet-state");
                    }
                    continue;
                }

                sheets.Add(new KeyValuePair<string, long>(
                    sheetName,
                    CountDataRows(
                        report,
                        sheetName,
                        worksheetPart,
                        source,
                        voucherKeys,
                        unbalancedVouchers)));
            }
            return new ReportReadResult(
                Array.AsReadOnly(sheets.ToArray()),
                unbalancedVouchers?.Complete());
        }
        catch (LegacyAuditParityObservationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Error("report.read-failed");
        }
    }

    private static long CountDataRows(
        LegacyReportKind report,
        string sheetName,
        WorksheetPart worksheetPart,
        LegacyAuditParityReportSource source,
        VoucherKeyResolver voucherKeys,
        LegacyUnbalancedVoucherCollector? unbalancedVouchers)
    {
        // `SheetStat.RowsWritten` is the production contract: fixed copy, titles,
        // headers, formulas and formatted empty template rows are not data rows.
        // Keep this test-side registry closed over the six writer families from
        // jet-guide §7; an unknown layout must stop parity instead of falling back
        // to populated-row counting.
        var series = ParseSeries(sheetName);
        using var rows = new WorksheetRowCursor(ReadRows(worksheetPart, voucherKeys));
        var count = report switch
        {
            LegacyReportKind.ValidationReport => CountValidationRows(series, rows, source),
            LegacyReportKind.AccountMapping => CountAccountMappingRows(series, rows),
            LegacyReportKind.InfReport => CountInfRows(series, rows),
            LegacyReportKind.PrescreenReport => CountPrescreenRows(series, rows),
            LegacyReportKind.CriteriaSelectionReport => CountCriteriaRows(series, rows),
            LegacyReportKind.WorkingPaper => CountWorkingPaperRows(
                series,
                rows,
                source,
                unbalancedVouchers),
            _ => throw Error("report.data-row-boundary"),
        };
        rows.Drain();
        return count;
    }

    private static long CountValidationRows(
        SheetSeries series,
        WorksheetRowCursor rows,
        LegacyAuditParityReportSource source)
    {
        if (series.Part == 1
            && series.BaseName is "ValidationReport" or "完整性測試出現差異時之指引")
        {
            return 0;
        }
        if (series.Part == 1
            && string.Equals(
                series.BaseName,
                MappingMetadataFormat.WorksheetName,
                StringComparison.Ordinal))
        {
            return CountFieldInfoRows(rows, FieldInfoBlankRowCount(source));
        }
        if (string.Equals(
                series.BaseName,
                WorkpaperSheetCatalog.Step13,
                StringComparison.Ordinal))
        {
            return CountContiguousRows(rows, 17, row => HasStoredValue(row, 2, 4));
        }
        if (source == LegacyAuditParityReportSource.JetGenerated
            && string.Equals(series.BaseName, "Source_Quality", StringComparison.Ordinal))
        {
            return CountContiguousRows(
                rows,
                2,
                row => HasAllStoredValues(row, 1, 3),
                isInvalidRow: row => HasStoredValue(row, 1, 3)
                    && !HasAllStoredValues(row, 1, 3));
        }
        if (TryParseOrdinal(
                series.BaseName,
                "V_Report ",
                1,
                source == LegacyAuditParityReportSource.LegacySystem ? 7 : 6))
        {
            return CountContiguousRows(rows, 2, HasAnyStoredValue);
        }
        throw Error("report.data-row-boundary");
    }

    private static long CountAccountMappingRows(
        SheetSeries series,
        WorksheetRowCursor rows)
    {
        if (series.Part != 1)
        {
            throw Error("report.data-row-boundary");
        }
        return series.BaseName switch
        {
            "AccountMapping" => CountContiguousRows(
                rows,
                4,
                row => HasAllStoredValues(row, 1, 2),
                isInvalidRow: row => HasStoredValue(row, 1, 2)
                    && !HasAllStoredValues(row, 1, 2)),
            "List" => CountContiguousRows(
                rows,
                2,
                row => HasStoredValue(row, 1, 1)),
            _ => throw Error("report.data-row-boundary"),
        };
    }

    private static long CountInfRows(
        SheetSeries series,
        WorksheetRowCursor rows)
    {
        if (series.Part != 1)
        {
            throw Error("report.data-row-boundary");
        }
        return series.BaseName switch
        {
            "INF Testing 可靠性測試" => CountContiguousRows(
                rows,
                53,
                row => HasStoredValue(row, 2, 12),
                lastDataRow: 112),
            "可靠性樣本_所有欄位" => CountContiguousRows(rows, 2, HasAnyStoredValue),
            _ => throw Error("report.data-row-boundary"),
        };
    }

    private static long CountPrescreenRows(
        SheetSeries series,
        WorksheetRowCursor rows)
    {
        if (series.Part == 1
            && string.Equals(series.BaseName, "Pre-screening_Report", StringComparison.Ordinal))
        {
            return 0;
        }
        if (series.BaseName.Length == 2
            && series.BaseName[0] == 'R'
            && series.BaseName[1] is >= '1' and <= '7')
        {
            return CountContiguousRows(rows, 2, HasAnyStoredValue);
        }
        throw Error("report.data-row-boundary");
    }

    private static long CountCriteriaRows(
        SheetSeries series,
        WorksheetRowCursor rows)
    {
        if (series.Part == 1
            && string.Equals(series.BaseName, "Summary Inforamtion", StringComparison.Ordinal))
        {
            return 0;
        }
        if (TryParseOrdinal(series.BaseName, "#Criteria Select ", 1, 10))
        {
            return CountContiguousRows(rows, 2, HasAnyStoredValue);
        }
        throw Error("report.data-row-boundary");
    }

    private static long CountWorkingPaperRows(
        SheetSeries series,
        WorksheetRowCursor rows,
        LegacyAuditParityReportSource source,
        LegacyUnbalancedVoucherCollector? unbalancedVouchers)
    {
        if (series.Part == 1
            && series.BaseName is WorkpaperSheetCatalog.Cover
                or WorkpaperSheetCatalog.Intro
                or WorkpaperSheetCatalog.Step5)
        {
            return 0;
        }
        if (string.Equals(series.BaseName, WorkpaperSheetCatalog.Step1, StringComparison.Ordinal))
        {
            return CountContiguousRows(rows, 20, row => HasStoredValue(row, 2, 6));
        }
        if (string.Equals(series.BaseName, WorkpaperSheetCatalog.Step11, StringComparison.Ordinal))
        {
            if (source == LegacyAuditParityReportSource.LegacySystem)
            {
                var collector = unbalancedVouchers
                    ?? throw Error("report.unbalanced-voucher-count");
                collector.ObserveSheet();
                return CountLegacyStep11Rows(rows, collector);
            }
            return CountContiguousRows(rows, 15, row => HasStoredValue(row, 2, 5));
        }
        if (string.Equals(series.BaseName, WorkpaperSheetCatalog.Step12, StringComparison.Ordinal))
        {
            return CountContiguousRows(rows, 12, row => HasStoredValue(row, 2, 5));
        }
        if (string.Equals(series.BaseName, WorkpaperSheetCatalog.Step13, StringComparison.Ordinal))
        {
            return CountContiguousRows(rows, 17, row => HasStoredValue(row, 2, 4));
        }
        if (string.Equals(series.BaseName, WorkpaperSheetCatalog.Step2, StringComparison.Ordinal))
        {
            if (source == LegacyAuditParityReportSource.LegacySystem)
            {
                return 0;
            }
            return CountContiguousRows(
                rows,
                53,
                row => HasStoredValue(row, 1, 1),
                lastDataRow: 111);
        }
        if (string.Equals(series.BaseName, WorkpaperSheetCatalog.Step3, StringComparison.Ordinal))
        {
            return CountContiguousRows(
                rows,
                19,
                row => HasAllStoredValues(row, 3, 5),
                isInvalidRow: row => HasStoredValue(row, 3, 5)
                    && !HasAllStoredValues(row, 3, 5));
        }
        if (string.Equals(series.BaseName, WorkpaperSheetCatalog.Step4, StringComparison.Ordinal))
        {
            return CountContiguousRows(rows, 13, row => HasStoredValue(row, 1, 5));
        }
        if (string.Equals(series.BaseName, WorkpaperSheetCatalog.Step41, StringComparison.Ordinal))
        {
            return CountContiguousRows(rows, 6, row => HasStoredValue(row, 1, 16));
        }
        if (series.Part == 1
            && string.Equals(
                series.BaseName,
                WorkpaperSheetCatalog.FieldInfo,
                StringComparison.Ordinal))
        {
            return CountFieldInfoRows(rows, FieldInfoBlankRowCount(source));
        }
        if (string.Equals(
                series.BaseName,
                WorkpaperSheetCatalog.CalendarInfo,
                StringComparison.Ordinal))
        {
            return CountCalendarRows(rows, series.Part, source);
        }
        if (string.Equals(
                series.BaseName,
                WorkpaperSheetCatalog.AccountMapping,
                StringComparison.Ordinal))
        {
            return CountContiguousRows(rows, 2, row => HasStoredValue(row, 1, 1));
        }
        throw Error("report.data-row-boundary");
    }

    private static long CountLegacyStep11Rows(
        WorksheetRowCursor rows,
        LegacyUnbalancedVoucherCollector collector)
    {
        if (HasStoredValue(rows.TakeExact(15), 2, 5))
        {
            throw Error("report.data-row-shape");
        }
        var header = rows.TakeExact(16);
        var count = CountContiguousRows(
            rows,
            17,
            row => HasStoredValue(row, 2, 5),
            isInvalidRow: row => HasStoredValue(row, 2, 5)
                && row.ColumnBVoucherKey is null,
            observeDataRow: collector.ObserveVoucher);
        if (count > 0 && !HasAllStoredValues(header, 2, 3, 4, 5, 6))
        {
            throw Error("report.data-row-shape");
        }
        return count;
    }

    private static int FieldInfoBlankRowCount(LegacyAuditParityReportSource source) => source switch
    {
        LegacyAuditParityReportSource.JetGenerated => 2,
        LegacyAuditParityReportSource.LegacySystem => 1,
        _ => throw Error("report.identity"),
    };

    private static long CountFieldInfoRows(WorksheetRowCursor rows, int blankRowCount)
    {
        if (!HasAllStoredValues(rows.TakeExact(3U), 1, 2))
        {
            throw Error("report.data-row-shape");
        }

        var rowIndex = 4U;
        var tbRows = CountContiguousRowsFrom(
            rows,
            ref rowIndex,
            row => HasStoredValue(row, 1, 1));
        for (var blank = 0; blank < blankRowCount; blank++)
        {
            RequireBlankRow(rows, rowIndex++);
        }
        var glTitle = rows.TakeExact(rowIndex++);
        var glHeader = rows.TakeExact(rowIndex++);
        if (!HasStoredValue(glTitle, 1, 1)
            || HasStoredValue(glTitle, 2, 5)
            || !HasAllStoredValues(glHeader, 1, 2))
        {
            throw Error("report.data-row-shape");
        }

        var glRows = CountContiguousRowsFrom(
            rows,
            ref rowIndex,
            row => HasStoredValue(row, 1, 1));
        EnsureNoLaterDataRows(rows, rowIndex, row => HasStoredValue(row, 1, 1));
        return checked(tbRows + glRows);
    }

    private static long CountCalendarRows(
        WorksheetRowCursor rows,
        int part,
        LegacyAuditParityReportSource source)
    {
        var headerRow = part == 1 ? 10U : 1U;
        var header = rows.TakeExact(headerRow);
        if (!HasAllStoredValues(header, 1, 2))
        {
            throw Error("report.data-row-shape");
        }

        var holidaySection = HasStoredValue(header, 3, 3);
        var rowIndex = checked(headerRow + 1);
        if (!holidaySection)
        {
            var makeupRows = CountContiguousRowsFrom(
                rows,
                ref rowIndex,
                row => HasStoredValue(row, 1, 1));
            EnsureNoLaterDataRows(rows, rowIndex, row => HasStoredValue(row, 1, 1));
            return makeupRows;
        }

        var holidayRow = source == LegacyAuditParityReportSource.LegacySystem
            ? (Func<WorksheetRowSnapshot, bool>)(row => HasAllStoredValues(row, 1, 3))
            : row => HasAllStoredValues(row, 1, 2, 3);
        var invalidHolidayRow = source == LegacyAuditParityReportSource.LegacySystem
            ? (Func<WorksheetRowSnapshot, bool>)(row => HasStoredValue(row, 1, 1)
                != HasStoredValue(row, 3, 3))
            : row => HasStoredValue(row, 1, 3)
                && !HasAllStoredValues(row, 1, 2, 3);
        var holidayRows = CountContiguousRowsFrom(
            rows,
            ref rowIndex,
            holidayRow,
            isInvalidRow: invalidHolidayRow);
        var separator = rows.TakeExact(rowIndex++);
        var makeupHeader = rows.TakeExact(rowIndex++);
        var hasMakeupSection = HasStoredValue(separator, 1, 2)
            || HasStoredValue(makeupHeader, 1, 2);
        while (!hasMakeupSection && rows.TryPeek(out var laterRow))
        {
            if (HasStoredValue(laterRow, 1, 2))
            {
                hasMakeupSection = true;
                break;
            }
            rows.Consume();
        }

        if (!hasMakeupSection)
        {
            return holidayRows;
        }
        RequireEmptyRowPosition(separator);
        if (!HasAllStoredValues(makeupHeader, 1, 2)
            || HasStoredValue(makeupHeader, 3, 3))
        {
            throw Error("report.data-row-shape");
        }
        var makeupRowsAfterHoliday = CountContiguousRowsFrom(
            rows,
            ref rowIndex,
            row => HasStoredValue(row, 1, 1));
        EnsureNoLaterDataRows(rows, rowIndex, row => HasStoredValue(row, 1, 1));
        return checked(holidayRows + makeupRowsAfterHoliday);
    }

    private static long CountContiguousRows(
        WorksheetRowCursor rows,
        uint firstDataRow,
        Func<WorksheetRowSnapshot, bool> isDataRow,
        uint? lastDataRow = null,
        Func<WorksheetRowSnapshot, bool>? isInvalidRow = null,
        Action<WorksheetRowSnapshot>? observeDataRow = null)
    {
        var rowIndex = firstDataRow;
        var count = CountContiguousRowsFrom(
            rows,
            ref rowIndex,
            isDataRow,
            lastDataRow,
            isInvalidRow,
            observeDataRow);
        EnsureNoLaterDataRows(rows, rowIndex, isDataRow, lastDataRow, isInvalidRow);
        return count;
    }

    private static long CountContiguousRowsFrom(
        WorksheetRowCursor rows,
        ref uint rowIndex,
        Func<WorksheetRowSnapshot, bool> isDataRow,
        uint? lastDataRow = null,
        Func<WorksheetRowSnapshot, bool>? isInvalidRow = null,
        Action<WorksheetRowSnapshot>? observeDataRow = null)
    {
        rows.SkipBefore(rowIndex);
        long count = 0;
        while ((!lastDataRow.HasValue || rowIndex <= lastDataRow.Value)
               && rows.TryPeek(out var row)
               && row.RowIndex == rowIndex)
        {
            if (isInvalidRow?.Invoke(row) == true)
            {
                throw Error("report.data-row-shape");
            }
            if (!isDataRow(row))
            {
                break;
            }
            observeDataRow?.Invoke(row);
            rows.Consume();
            count = checked(count + 1);
            rowIndex = checked(rowIndex + 1);
        }
        return count;
    }

    private static void EnsureNoLaterDataRows(
        WorksheetRowCursor rows,
        uint firstUncheckedRow,
        Func<WorksheetRowSnapshot, bool> isDataRow,
        uint? lastDataRow = null,
        Func<WorksheetRowSnapshot, bool>? isInvalidRow = null)
    {
        while (rows.TryTake(out var row))
        {
            var isUncheckedWriterRow = row.RowIndex >= firstUncheckedRow
                && (!lastDataRow.HasValue || row.RowIndex <= lastDataRow.Value);
            var isOutsideFixedWriterArea = lastDataRow.HasValue
                && row.RowIndex > lastDataRow.Value;
            if (!isUncheckedWriterRow && !isOutsideFixedWriterArea)
            {
                continue;
            }
            if (isInvalidRow?.Invoke(row) == true)
            {
                throw Error("report.data-row-shape");
            }
            if (isDataRow(row))
            {
                throw Error("report.data-row-contiguity");
            }
        }
    }

    private static void RequireBlankRow(WorksheetRowCursor rows, uint rowIndex)
    {
        var row = rows.TakeExact(rowIndex);
        if (HasStoredValue(row, 1, 5))
        {
            throw Error("report.data-row-shape");
        }
    }

    private static void RequireEmptyRowPosition(WorksheetRowSnapshot? row)
    {
        if (HasStoredValue(row, 1, 3))
        {
            throw Error("report.data-row-shape");
        }
    }

    private static IEnumerable<WorksheetRowSnapshot> ReadRows(
        WorksheetPart worksheetPart,
        VoucherKeyResolver voucherKeys)
    {
        using var reader = OpenXmlReader.Create(worksheetPart);
        var sawSheetData = false;
        WorksheetRowSnapshot? pendingRow = null;
        while (reader.Read())
        {
            if (reader.IsStartElement && reader.ElementType == typeof(SheetData))
            {
                if (sawSheetData)
                {
                    throw Error("report.sheet-data");
                }
                sawSheetData = true;
                continue;
            }
            if (!reader.IsStartElement || reader.ElementType != typeof(Row))
            {
                continue;
            }
            if (!sawSheetData)
            {
                throw Error("report.sheet-data");
            }

            var row = reader.LoadCurrentElement() as Row
                ?? throw Error("report.sheet-data");
            var snapshot = CaptureRow(row, voucherKeys);
            if (snapshot is null)
            {
                continue;
            }
            if (pendingRow is null)
            {
                pendingRow = snapshot;
                continue;
            }
            if (snapshot.Value.RowIndex < pendingRow.Value.RowIndex)
            {
                throw Error("report.row-index-order");
            }
            if (snapshot.Value.RowIndex == pendingRow.Value.RowIndex)
            {
                pendingRow = MergeComplementaryRowFragments(
                    pendingRow.Value,
                    snapshot.Value);
                continue;
            }

            yield return pendingRow.Value;
            pendingRow = snapshot;
        }
        if (pendingRow is { } finalRow)
        {
            yield return finalRow;
        }
        if (!sawSheetData)
        {
            throw Error("report.sheet-data");
        }
    }

    private static WorksheetRowSnapshot MergeComplementaryRowFragments(
        WorksheetRowSnapshot first,
        WorksheetRowSnapshot second)
    {
        // Direct template overlays can emit adjacent row fragments when fixed
        // cells on one logical row are written separately. Merge only fragments
        // whose stored cells are provably disjoint; repeated data remains a
        // fail-closed duplicate instead of being silently overwritten.
        if ((first.StoredColumns & second.StoredColumns) != 0
            || first.HasStoredValueAbove64 && second.HasAnyStoredValue
            || second.HasStoredValueAbove64 && first.HasAnyStoredValue)
        {
            throw Error("report.row-index-duplicate");
        }

        return new WorksheetRowSnapshot(
            first.RowIndex,
            first.StoredColumns | second.StoredColumns,
            first.HasAnyStoredValue || second.HasAnyStoredValue,
            first.HasStoredValueAbove64 || second.HasStoredValueAbove64,
            first.ColumnBVoucherKey ?? second.ColumnBVoucherKey);
    }

    private static WorksheetRowSnapshot? CaptureRow(Row row, VoucherKeyResolver voucherKeys)
    {
        uint? referencedRowIndex = null;
        var multipleReferencedRows = false;
        var seenColumns = new HashSet<uint>();
        ulong storedColumns = 0;
        var hasAnyStoredValue = false;
        var hasStoredValueAbove64 = false;
        VoucherKeyFingerprint? columnBVoucherKey = null;
        foreach (var cell in row.Elements<Cell>())
        {
            var rowIndex = RowIndex(cell.CellReference?.Value);
            if (referencedRowIndex.HasValue && referencedRowIndex.Value != rowIndex)
            {
                multipleReferencedRows = true;
            }
            else
            {
                referencedRowIndex = rowIndex;
            }

            var column = ColumnIndex(cell);
            if (!seenColumns.Add(column))
            {
                throw Error("report.cell-reference-duplicate");
            }
            if (!HasStoredValue(cell))
            {
                continue;
            }
            hasAnyStoredValue = true;
            if (column <= 64)
            {
                storedColumns |= 1UL << checked((int)column - 1);
            }
            else
            {
                hasStoredValueAbove64 = true;
            }
            if (column == 2 && voucherKeys.IsEnabled)
            {
                columnBVoucherKey = voucherKeys.Resolve(cell);
            }
        }

        if (row.RowIndex?.Value is null && referencedRowIndex is null)
        {
            // Some template producers leave an index-less, cell-less formatting row.
            // It cannot carry a data value and therefore has no role in the boundary count.
            return null;
        }
        if (row.RowIndex?.Value is null && multipleReferencedRows)
        {
            throw Error("report.row-index-missing");
        }

        var resolvedRowIndex = row.RowIndex?.Value
            ?? referencedRowIndex
            ?? throw Error("report.row-index-missing");
        if (resolvedRowIndex == 0)
        {
            throw Error("report.row-index-missing");
        }
        if (multipleReferencedRows
            || referencedRowIndex.HasValue && referencedRowIndex.Value != resolvedRowIndex)
        {
            throw Error("report.row-index-cell-mismatch");
        }
        return new WorksheetRowSnapshot(
            resolvedRowIndex,
            storedColumns,
            hasAnyStoredValue,
            hasStoredValueAbove64,
            columnBVoucherKey);
    }

    private static uint RowIndex(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            throw Error("report.cell-reference");
        }
        var position = 0;
        while (position < reference.Length
               && reference[position] is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
        {
            position++;
        }
        var digits = reference.AsSpan(position);
        return uint.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var rowIndex)
            && rowIndex > 0
                ? rowIndex
                : throw Error("report.cell-reference");
    }

    private static bool HasAnyStoredValue(WorksheetRowSnapshot row) => row.HasAnyStoredValue;

    private static bool HasStoredValue(
        WorksheetRowSnapshot? row,
        uint firstColumn,
        uint lastColumn) => row?.HasStoredValue(firstColumn, lastColumn) == true;

    private static bool HasAllStoredValues(
        WorksheetRowSnapshot? row,
        params uint[] columns) => row is { } captured
        && columns.All(column => captured.HasStoredValue(column, column));

    private static uint ColumnIndex(Cell cell)
    {
        var reference = cell.CellReference?.Value;
        if (string.IsNullOrWhiteSpace(reference))
        {
            throw Error("report.cell-reference");
        }

        uint result = 0;
        var letterCount = 0;
        foreach (var character in reference)
        {
            if (character is >= 'A' and <= 'Z')
            {
                result = checked(result * 26 + (uint)(character - 'A' + 1));
                letterCount++;
                continue;
            }
            if (character is >= 'a' and <= 'z')
            {
                result = checked(result * 26 + (uint)(character - 'a' + 1));
                letterCount++;
                continue;
            }
            break;
        }
        if (letterCount == 0
            || letterCount == reference.Length
            || reference[letterCount..].Any(character => character is < '0' or > '9'))
        {
            throw Error("report.cell-reference");
        }
        return result;
    }

    private readonly record struct WorksheetRowSnapshot(
        uint RowIndex,
        ulong StoredColumns,
        bool HasAnyStoredValue,
        bool HasStoredValueAbove64,
        VoucherKeyFingerprint? ColumnBVoucherKey)
    {
        internal bool HasStoredValue(uint firstColumn, uint lastColumn)
        {
            if (firstColumn == 0 || lastColumn < firstColumn || lastColumn > 64)
            {
                throw Error("report.data-row-boundary");
            }
            for (var column = firstColumn; column <= lastColumn; column++)
            {
                if ((StoredColumns & (1UL << checked((int)column - 1))) != 0)
                {
                    return true;
                }
            }
            return false;
        }
    }

    private readonly record struct VoucherKeyFingerprint(
        ulong Part1,
        ulong Part2,
        ulong Part3,
        ulong Part4)
    {
        internal static VoucherKeyFingerprint? Create(string? value)
        {
            var normalized = value?.Trim();
            if (string.IsNullOrEmpty(normalized))
            {
                return null;
            }
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
            return new VoucherKeyFingerprint(
                BinaryPrimitives.ReadUInt64LittleEndian(hash.AsSpan(0, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(hash.AsSpan(8, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(hash.AsSpan(16, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(hash.AsSpan(24, 8)));
        }
    }

    private sealed class VoucherKeyResolver
    {
        private readonly IReadOnlyList<VoucherKeyFingerprint?> _sharedStrings;

        private VoucherKeyResolver(
            bool isEnabled,
            IReadOnlyList<VoucherKeyFingerprint?> sharedStrings)
        {
            IsEnabled = isEnabled;
            _sharedStrings = sharedStrings;
        }

        internal static VoucherKeyResolver Empty { get; } = new(false, []);

        internal bool IsEnabled { get; }

        internal static VoucherKeyResolver Create(WorkbookPart workbookPart)
        {
            var sharedStrings = new List<VoucherKeyFingerprint?>();
            if (workbookPart.SharedStringTablePart is { } part)
            {
                using var reader = OpenXmlReader.Create(part);
                while (reader.Read())
                {
                    if (!reader.IsStartElement || reader.ElementType != typeof(SharedStringItem))
                    {
                        continue;
                    }
                    var item = reader.LoadCurrentElement() as SharedStringItem
                        ?? throw Error("report.voucher-key");
                    sharedStrings.Add(VoucherKeyFingerprint.Create(item.InnerText));
                }
            }
            return new VoucherKeyResolver(
                true,
                Array.AsReadOnly(sharedStrings.ToArray()));
        }

        internal VoucherKeyFingerprint? Resolve(Cell cell)
        {
            if (!IsEnabled)
            {
                return null;
            }
            if (cell.DataType?.Value == CellValues.SharedString)
            {
                if (!int.TryParse(
                        cell.CellValue?.InnerText,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var index)
                    || index < 0
                    || index >= _sharedStrings.Count)
                {
                    throw Error("report.voucher-key");
                }
                return _sharedStrings[index];
            }
            if (cell.DataType?.Value == CellValues.InlineString)
            {
                return VoucherKeyFingerprint.Create(cell.InlineString?.InnerText);
            }
            return VoucherKeyFingerprint.Create(cell.CellValue?.InnerText);
        }
    }

    private sealed class LegacyUnbalancedVoucherCollector
    {
        private readonly HashSet<VoucherKeyFingerprint> _voucherKeys = [];
        private bool _sawSheet;

        internal void ObserveSheet()
        {
            _sawSheet = true;
        }

        internal void ObserveVoucher(WorksheetRowSnapshot row)
        {
            if (row.ColumnBVoucherKey is not { } key)
            {
                throw Error("report.voucher-key");
            }
            _voucherKeys.Add(key);
        }

        internal long? Complete() => _sawSheet ? _voucherKeys.Count : null;
    }

    private sealed record ReportReadResult(
        IReadOnlyList<KeyValuePair<string, long>> DataRows,
        long? DistinctUnbalancedVoucherCount);

    private sealed class WorksheetRowCursor : IDisposable
    {
        private readonly IEnumerator<WorksheetRowSnapshot> _rows;
        private WorksheetRowSnapshot _current;
        private bool _hasCurrent;
        private bool _completed;

        internal WorksheetRowCursor(IEnumerable<WorksheetRowSnapshot> rows)
        {
            _rows = rows.GetEnumerator();
        }

        internal bool TryPeek(out WorksheetRowSnapshot row)
        {
            if (!EnsureCurrent())
            {
                row = default;
                return false;
            }
            row = _current;
            return true;
        }

        internal bool TryTake(out WorksheetRowSnapshot row)
        {
            if (!TryPeek(out row))
            {
                return false;
            }
            _hasCurrent = false;
            return true;
        }

        internal void Consume()
        {
            if (!TryTake(out _))
            {
                throw Error("report.sheet-data");
            }
        }

        internal void SkipBefore(uint rowIndex)
        {
            while (TryPeek(out var row) && row.RowIndex < rowIndex)
            {
                Consume();
            }
        }

        internal WorksheetRowSnapshot? TakeExact(uint rowIndex)
        {
            SkipBefore(rowIndex);
            if (!TryPeek(out var row) || row.RowIndex != rowIndex)
            {
                return null;
            }
            Consume();
            return row;
        }

        internal void Drain()
        {
            while (TryTake(out _))
            {
            }
        }

        public void Dispose()
        {
            _rows.Dispose();
        }

        private bool EnsureCurrent()
        {
            if (_hasCurrent)
            {
                return true;
            }
            if (_completed || !_rows.MoveNext())
            {
                _completed = true;
                return false;
            }
            _current = _rows.Current;
            _hasCurrent = true;
            return true;
        }
    }

    private static bool TryParseOrdinal(
        string value,
        string prefix,
        int minimum,
        int maximum)
    {
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }
        var suffix = value[prefix.Length..];
        return int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal)
            && ordinal >= minimum
            && ordinal <= maximum
            && string.Equals(
                suffix,
                ordinal.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
    }

    private static SheetSeries ParseSeries(string sheetName)
    {
        var marker = sheetName.LastIndexOf(" (", StringComparison.Ordinal);
        if (marker < 0 || !sheetName.EndsWith(')'))
        {
            return new SheetSeries(sheetName, 1);
        }

        var rawSuffix = sheetName[(marker + 2)..^1];
        var suffix = rawSuffix.StartsWith('續') ? rawSuffix[1..] : rawSuffix;
        if (!int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var part)
            || part < 2
            || !string.Equals(
                suffix,
                part.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            return new SheetSeries(sheetName, 1);
        }
        return new SheetSeries(sheetName[..marker], part);
    }

    private readonly record struct SheetSeries(string BaseName, int Part);

    private static bool HasStoredValue(Cell cell) =>
        cell.CellValue is not null
        || cell.InlineString is not null
        || cell.CellFormula is not null;

    private static JsonElement RequiredObject(JsonElement element, string property, string fieldId)
    {
        if (!element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Object)
        {
            throw Error(fieldId);
        }
        return value;
    }

    private static JsonElement RequiredArray(JsonElement element, string property, string fieldId)
    {
        if (!element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            throw Error(fieldId);
        }
        return value;
    }

    private static string RequiredString(JsonElement element, string property, string fieldId)
    {
        if (!element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw Error(fieldId);
        }
        return value.GetString()!;
    }

    private static string? NullableString(JsonElement element, string property, string fieldId)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            throw Error(fieldId);
        }
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => throw Error(fieldId),
        };
    }

    private static string? NullableNumberText(JsonElement element, string property, string fieldId)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            throw Error(fieldId);
        }
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Number => value.GetRawText(),
            _ => throw Error(fieldId),
        };
    }

    private static long RequiredInt64(JsonElement element, string property, string fieldId)
    {
        var value = NullableInt64(element, property, fieldId);
        return value ?? throw Error(fieldId);
    }

    private static long? NullableInt64(JsonElement element, string property, string fieldId)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            throw Error(fieldId);
        }
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result))
        {
            throw Error(fieldId);
        }
        return result;
    }

    private static decimal? NullableDecimal(JsonElement element, string property, string fieldId)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            throw Error(fieldId);
        }
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var result))
        {
            throw Error(fieldId);
        }
        return result;
    }

    private static bool RequiredBoolean(JsonElement element, string property, string fieldId)
    {
        var value = NullableBoolean(element, property, fieldId);
        return value ?? throw Error(fieldId);
    }

    private static bool? NullableBoolean(JsonElement element, string property, string fieldId)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            throw Error(fieldId);
        }
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True or JsonValueKind.False => value.GetBoolean(),
            _ => throw Error(fieldId),
        };
    }

    private static LegacyAuditParityObservationException Error(string fieldId) => new(fieldId);
}
