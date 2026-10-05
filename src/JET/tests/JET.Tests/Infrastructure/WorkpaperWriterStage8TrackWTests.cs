using System.Globalization;
using System.Text.Json;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Application;
using Xunit;
using static JET.Tests.Infrastructure.WorkpaperWriterTestSupport;

namespace JET.Tests.Infrastructure;

/// <summary>底稿寫出器：Stage 8 Track W 已登記的 43 項外觀逐格比對與空狀態條件。</summary>
public sealed class WorkpaperWriterStage8TrackWTests
{
    [Fact]
    public void Stage8TrackW_Registry_IsTheExactClosedFortyThreeIdSet()
    {
        var actual = TrackWEntries().Select(entry => entry.Id).ToArray();

        Assert.Equal(43, actual.Length);
        Assert.Equal(Stage8TrackWIds, actual);
    }

    [Fact]
    public async Task Stage8TrackW_AllFortyThreeRegisteredAppearances_HaveNoCellOrColumnMismatch()
    {
        using var positiveHost = new HandlerTestHost();
        var positiveProject = await SetupContinuationMatrixAsync(positiveHost, voucherCount: 3);
        var differences = DifferenceRows(1);
        var positive = await WriteStage8TrackWAsync(
            positiveHost,
            positiveProject,
            differences,
            new FixedStage8CalendarRepository(
                [new CalendarDayEntry("2025-01-01", "Synthetic holiday")],
                [new CalendarDayEntry("2025-02-08", "Synthetic make-up day")]),
            new FixedStage8AccountMappingRepository(
                [new AccountMappingExportRow("SYN-001", "Synthetic account", "Cash", false)]));

        using var missingPreparerHost = new HandlerTestHost();
        var missingPreparerProject = await SetupStage8MissingPreparerMappingAsync(missingPreparerHost);
        var missingPreparer = await WriteStage8TrackWAsync(
            missingPreparerHost,
            missingPreparerProject,
            DifferenceRows(0),
            FixedStage8CalendarRepository.Empty,
            FixedStage8AccountMappingRepository.Empty);

        using var positiveDocument = OpenStage8Document(positive.Bytes);
        using var missingPreparerDocument = OpenStage8Document(missingPreparer.Bytes);
        var glHeaderRow = checked((uint)positive.Plan.FieldInfo!.TbRows.Count + 7U);
        var targets = Stage8TrackWTargets(glHeaderRow);
        var entries = TrackWEntries();
        var mismatches = new List<string>();

        foreach (var entry in entries)
        {
            var target = targets[entry.Id];
            var document = target.Output == Stage8TrackWOutput.Positive
                ? positiveDocument
                : missingPreparerDocument;
            mismatches.AddRange(Stage8AppearanceMismatches(document, entry, target));
        }

        Assert.Empty(mismatches);
        AssertStage8GeneratedCellMatchesBaseline(
            positiveDocument,
            entries,
            Stage8TrackWIds.Skip(10).Take(4),
            Step12Sheet,
            targetReference: "B13",
            baselineReference: "B12");
    }

    [Fact]
    public async Task Stage8TrackW_CompletenessAndUnbalancedConditions_CoverAllFourStates()
    {
        using var host = new HandlerTestHost();
        var balancedProject = await SetupBalancedAsync(host);
        var none = await WriteStage8TrackWAsync(
            host,
            balancedProject,
            DifferenceRows(0),
            FixedStage8CalendarRepository.Empty,
            FixedStage8AccountMappingRepository.Empty);
        var completenessOnly = await WriteStage8TrackWAsync(
            host,
            balancedProject,
            DifferenceRows(1),
            FixedStage8CalendarRepository.Empty,
            FixedStage8AccountMappingRepository.Empty);

        var unbalancedProject = await SetupContinuationMatrixAsync(host, voucherCount: 3);
        var unbalancedOnly = await WriteStage8TrackWAsync(
            host,
            unbalancedProject,
            DifferenceRows(0),
            FixedStage8CalendarRepository.Empty,
            FixedStage8AccountMappingRepository.Empty);
        var both = await WriteStage8TrackWAsync(
            host,
            unbalancedProject,
            DifferenceRows(1),
            FixedStage8CalendarRepository.Empty,
            FixedStage8AccountMappingRepository.Empty);

        using var noneDocument = OpenStage8Document(none.Bytes);
        using var completenessDocument = OpenStage8Document(completenessOnly.Bytes);
        using var unbalancedDocument = OpenStage8Document(unbalancedOnly.Bytes);
        using var bothDocument = OpenStage8Document(both.Bytes);
        using var templateDocument = SpreadsheetDocument.Open(
            Path.Combine(AppContext.BaseDirectory, "Templates", "WorkingPaper.xlsx"),
            false);
        var entries = TrackWEntries();
        var targets = Stage8TrackWTargets(1);
        var completenessIds = Stage8TrackWIds.Take(4).ToArray();
        var unbalancedIds = Stage8TrackWIds.Skip(4).Take(6).ToArray();

        AssertStage8Condition(noneDocument, templateDocument, entries, targets, completenessIds, expectedApplied: false);
        AssertStage8Condition(noneDocument, templateDocument, entries, targets, unbalancedIds, expectedApplied: false);
        AssertStage8Condition(completenessDocument, templateDocument, entries, targets, completenessIds, expectedApplied: true);
        AssertStage8Condition(completenessDocument, templateDocument, entries, targets, unbalancedIds, expectedApplied: false);
        AssertStage8Condition(unbalancedDocument, templateDocument, entries, targets, completenessIds, expectedApplied: false);
        AssertStage8Condition(unbalancedDocument, templateDocument, entries, targets, unbalancedIds, expectedApplied: true);
        AssertStage8Condition(bothDocument, templateDocument, entries, targets, completenessIds, expectedApplied: true);
        AssertStage8Condition(bothDocument, templateDocument, entries, targets, unbalancedIds, expectedApplied: true);
    }

    [Fact]
    public async Task Stage8TrackW_ReferenceSourceConditions_KeepHeadersAndProveEachEmptyState()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);
        var empty = await WriteStage8TrackWAsync(
            host,
            projectId,
            DifferenceRows(0),
            FixedStage8CalendarRepository.Empty,
            FixedStage8AccountMappingRepository.Empty);
        var importedEmptyAccount = await WriteStage8TrackWAsync(
            host,
            projectId,
            DifferenceRows(0),
            FixedStage8CalendarRepository.Empty,
            FixedStage8AccountMappingRepository.ImportedEmpty);
        var holidayOnly = await WriteStage8TrackWAsync(
            host,
            projectId,
            DifferenceRows(0),
            new FixedStage8CalendarRepository(
                [new CalendarDayEntry("2025-01-01", "Synthetic holiday")],
                []),
            FixedStage8AccountMappingRepository.Empty);
        var makeupOnly = await WriteStage8TrackWAsync(
            host,
            projectId,
            DifferenceRows(0),
            new FixedStage8CalendarRepository(
                [],
                [new CalendarDayEntry("2025-02-08", "Synthetic make-up day")]),
            FixedStage8AccountMappingRepository.Empty);
        var accountOnly = await WriteStage8TrackWAsync(
            host,
            projectId,
            DifferenceRows(0),
            FixedStage8CalendarRepository.Empty,
            new FixedStage8AccountMappingRepository(
                [new AccountMappingExportRow("SYN-001", "Synthetic account", "Cash", false)]));

        using var emptyDocument = OpenStage8Document(empty.Bytes);
        using var importedEmptyAccountDocument = OpenStage8Document(importedEmptyAccount.Bytes);
        using var holidayDocument = OpenStage8Document(holidayOnly.Bytes);
        using var makeupDocument = OpenStage8Document(makeupOnly.Bytes);
        using var accountDocument = OpenStage8Document(accountOnly.Bytes);
        var entries = TrackWEntries();
        var normalTargets = Stage8TrackWTargets(1);
        var makeupTargets = normalTargets.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var id in Stage8TrackWIds.Skip(35).Take(3))
        {
            makeupTargets[id] = Stage8AppearanceTarget.Cells(
                Stage8TrackWOutput.Positive,
                CalendarInfoSheet,
                Cells(12, 12, 1, 2));
        }
        var holidayWithoutMakeupTargets = normalTargets.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        foreach (var id in Stage8TrackWIds.Skip(35).Take(3))
        {
            holidayWithoutMakeupTargets[id] = Stage8AppearanceTarget.Cells(
                Stage8TrackWOutput.Positive,
                CalendarInfoSheet,
                Cells(13, 13, 1, 2));
        }

        AssertStage8Applied(emptyDocument, entries, normalTargets, Stage8TrackWIds.Skip(28).Take(3));
        AssertStage8Applied(holidayDocument, entries, normalTargets, Stage8TrackWIds.Skip(31).Take(4));
        AssertStage8Applied(makeupDocument, entries, makeupTargets, Stage8TrackWIds.Skip(35).Take(4));
        AssertStage8Applied(accountDocument, entries, normalTargets, Stage8TrackWIds.Skip(39).Take(4));
        AssertStage8Applied(
            importedEmptyAccountDocument,
            entries,
            normalTargets,
            Stage8TrackWIds.Skip(39).Take(4));

        AssertReferenceBaseline(
            emptyDocument,
            entries,
            normalTargets,
            Stage8TrackWIds.Skip(31).Take(4));
        AssertReferenceBaseline(
            emptyDocument,
            entries,
            makeupTargets,
            Stage8TrackWIds.Skip(35).Take(4));
        AssertReferenceBaseline(
            emptyDocument,
            entries,
            normalTargets,
            Stage8TrackWIds.Skip(39).Take(4));

        // A sibling source may be present without activating this source's header patch.
        // AutoFit is compared only where the sibling source does not share the same columns.
        AssertReferenceConditionMatchesBaseline(
            makeupDocument,
            emptyDocument,
            entries,
            normalTargets,
            Stage8TrackWIds.Skip(31).Take(3));
        AssertReferenceConditionMatchesBaseline(
            holidayDocument,
            emptyDocument,
            entries,
            holidayWithoutMakeupTargets,
            Stage8TrackWIds.Skip(35).Take(3),
            makeupTargets);
        AssertReferenceConditionMatchesBaseline(
            holidayDocument,
            emptyDocument,
            entries,
            normalTargets,
            Stage8TrackWIds.Skip(39).Take(4));
        AssertReferenceConditionMatchesBaseline(
            accountDocument,
            emptyDocument,
            entries,
            normalTargets,
            Stage8TrackWIds.Skip(31).Take(4));
        AssertReferenceConditionMatchesBaseline(
            accountDocument,
            emptyDocument,
            entries,
            makeupTargets,
            Stage8TrackWIds.Skip(35).Take(4));

        Assert.Equal("DAYOFWEEK", ReadStage8CellText(emptyDocument, CalendarInfoSheet, "A1"));
        Assert.Equal("DATE_OF_HOLIDAY", ReadStage8CellText(emptyDocument, CalendarInfoSheet, "A10"));
        Assert.Equal("DATE_OF_MAKEUPDAY", ReadStage8CellText(emptyDocument, CalendarInfoSheet, "A12"));
        Assert.Equal("GL_NUMBER", ReadStage8CellText(emptyDocument, AccountMappingSheet, "A1"));
        Assert.Equal(
            "GL_NUMBER",
            ReadStage8CellText(importedEmptyAccountDocument, AccountMappingSheet, "A1"));
    }

    [Fact]
    public async Task Stage8TrackW_JetPhysicalSettingsAreAbsent_AndBaseTemplateSettingsSurvive()
    {
        const uint continuationRowLimit = 25;
        using var host = new HandlerTestHost();
        var projectId = await SetupContinuationMatrixAsync(host);
        await ImportContinuationReferencesAsync(host);
        await host.DispatchAsync("filter.commit", ContinuationScenarioPayload());
        var output = await WriteStage8TrackWAsync(
            host,
            projectId,
            DifferenceRows(26),
            new FixedStage8CalendarRepository(
                Enumerable.Range(1, 26)
                    .Select(index => new CalendarDayEntry($"2025-07-{index:00}", "Synthetic holiday"))
                    .ToArray(),
                [new CalendarDayEntry("2025-08-02", "Synthetic make-up day")]),
            new FixedStage8AccountMappingRepository(
                Enumerable.Range(1, 26)
                    .Select(index => new AccountMappingExportRow(
                        $"SYN-{index:000}",
                        "Synthetic account",
                        "Others",
                        false))
                    .ToArray()),
            new WorkpaperWriterOptions(continuationRowLimit));

        var templatePath = Path.Combine(AppContext.BaseDirectory, "Templates", "WorkingPaper.xlsx");
        using var template = SpreadsheetDocument.Open(templatePath, false);
        using var actual = OpenStage8Document(output.Bytes);
        var templateSheets = WorksheetsByName(template);
        var actualSheets = WorksheetsByName(actual);
        Assert.Contains(actualSheets.Keys, name => !templateSheets.ContainsKey(name));

        foreach (var (name, worksheet) in actualSheets)
        {
            if (templateSheets.TryGetValue(name, out var templateWorksheet))
            {
                Assert.Equal(
                    CanonicalElements(templateWorksheet.Elements<SheetViews>()),
                    CanonicalElements(worksheet.Elements<SheetViews>()));
                Assert.Equal(
                    CanonicalElements(templateWorksheet.Descendants<Pane>()),
                    CanonicalElements(worksheet.Descendants<Pane>()));
                Assert.Equal(
                    CanonicalElements(templateWorksheet.Elements<PageMargins>()),
                    CanonicalElements(worksheet.Elements<PageMargins>()));
                Assert.Equal(
                    CanonicalElements(templateWorksheet.Elements<PageSetup>()),
                    CanonicalElements(worksheet.Elements<PageSetup>()));
            }
            else
            {
                Assert.Empty(worksheet.Elements<SheetViews>());
                Assert.Empty(worksheet.Descendants<Pane>());
                Assert.Empty(worksheet.Elements<PageMargins>());
                Assert.Empty(worksheet.Elements<PageSetup>());
            }
        }
    }

}
