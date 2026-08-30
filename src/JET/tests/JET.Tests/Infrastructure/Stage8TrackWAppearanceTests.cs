using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Application;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed partial class WorkpaperWriterTests
{
    private static readonly string[] Stage8TrackWIds =
    [
        "workingpaper-step1-difference-bold",
        "workingpaper-step1-difference-font",
        "workingpaper-step1-difference-size",
        "workingpaper-step1-difference-font-color",
        "workingpaper-step1-balance-block-bold",
        "workingpaper-step1-balance-block-font",
        "workingpaper-step1-balance-block-size",
        "workingpaper-step1-balance-message-font-color",
        "workingpaper-step1-balance-header-font-color",
        "workingpaper-step1-balance-header-fill",
        "workingpaper-step1-preparer-warning-bold",
        "workingpaper-step1-preparer-warning-font",
        "workingpaper-step1-preparer-warning-size",
        "workingpaper-step1-preparer-warning-color",
        "workingpaper-field-info-version-bold",
        "workingpaper-field-info-version-font",
        "workingpaper-field-info-version-size",
        "workingpaper-field-info-version-color",
        "workingpaper-field-info-version-fill",
        "workingpaper-field-info-tb-header-bold",
        "workingpaper-field-info-tb-header-font",
        "workingpaper-field-info-tb-header-size",
        "workingpaper-field-info-tb-header-fill",
        "workingpaper-field-info-gl-header-bold",
        "workingpaper-field-info-gl-header-font",
        "workingpaper-field-info-gl-header-size",
        "workingpaper-field-info-gl-header-fill",
        "workingpaper-field-info-autofit",
        "workingpaper-weekend-header-bold",
        "workingpaper-weekend-header-font",
        "workingpaper-weekend-header-size",
        "workingpaper-holiday-header-bold",
        "workingpaper-holiday-header-font",
        "workingpaper-holiday-header-size",
        "workingpaper-holiday-autofit",
        "workingpaper-makeup-header-bold",
        "workingpaper-makeup-header-font",
        "workingpaper-makeup-header-size",
        "workingpaper-makeup-autofit",
        "workingpaper-account-mapping-header-bold",
        "workingpaper-account-mapping-header-font",
        "workingpaper-account-mapping-header-size",
        "workingpaper-account-mapping-autofit"
    ];

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

    private static string[] CanonicalElements<T>(IEnumerable<T> elements)
        where T : OpenXmlElement =>
        elements.Select(CanonicalElement).ToArray();

    private static string CanonicalElement(OpenXmlElement element)
    {
        var attributes = element.GetAttributes()
            .OrderBy(attribute => attribute.NamespaceUri, StringComparer.Ordinal)
            .ThenBy(attribute => attribute.LocalName, StringComparer.Ordinal)
            .Select(attribute =>
                $"{attribute.NamespaceUri}|{attribute.LocalName}={attribute.Value}");
        var children = element.ChildElements.Select(CanonicalElement);
        var attributeText = string.Join(";", attributes);
        var childText = string.Join(";", children);
        return $"{element.NamespaceUri}|{element.LocalName}[{attributeText}]({childText})";
    }

    private static IReadOnlyList<LegacyAppearanceRegistryEntry> TrackWEntries() =>
        LegacyAppearanceRegistry.Load().Entries
            .Where(entry => string.Equals(
                entry.Procedure,
                "Step5_Export_Excel_TW",
                StringComparison.Ordinal))
            .ToArray();

    private static FixedCompletenessDiffPageRepository DifferenceRows(int count) => new(
        Enumerable.Range(1, count)
            .Select(index => new CompletenessDiffAccount(
                $"SYN-{index:000}",
                "Synthetic account",
                110_000,
                100_000,
                10_000,
                false))
            .ToArray());

    private static async Task<Stage8TrackWWriteResult> WriteStage8TrackWAsync(
        HandlerTestHost host,
        string projectId,
        ICompletenessDiffPageRepository completenessDiffs,
        ICalendarExportRepository calendar,
        IAccountMappingExportRepository accountMappings,
        WorkpaperWriterOptions? options = null)
    {
        var context = await CurrentContextForAsync(host, projectId);
        var plan = await CurrentPlanForAsync(host, context, completenessDiffs);
        var accountMappingStateStore = Assert.IsAssignableFrom<IAccountMappingStore>(accountMappings);
        await using var stream = new MemoryStream();
        var stats = await ((IWorkpaperPlanWriter)BuildWriter(
            host,
            options,
            completenessDiffsOverride: completenessDiffs,
            calendarDaysOverride: calendar,
            accountMappingsOverride: accountMappings,
            accountMappingStateStoreOverride: accountMappingStateStore)).WriteAsync(
                stream,
                context,
                plan,
                CancellationToken.None);
        Assert.Equal(stream.Length, stats.BytesWritten);
        return new Stage8TrackWWriteResult(stream.ToArray(), plan);
    }

    private static Task<string> SetupStage8MissingPreparerMappingAsync(HandlerTestHost host) =>
        InlineWorkbookProject.SetupAsync(
            host,
            gl =>
            {
                gl.WithColumns(
                    "傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標");
                gl.AddRow("SYN-DOC", "2025-03-01", "SYN-D", "Synthetic debit", "Synthetic", 100m, 1);
                gl.AddRow("SYN-DOC", "2025-03-01", "SYN-C", "Synthetic credit", "Synthetic", 100m, 0);
            },
            validateForDownstream: true);

    private static SpreadsheetDocument OpenStage8Document(byte[] bytes) =>
        SpreadsheetDocument.Open(new MemoryStream(bytes, writable: false), false);

    private static Dictionary<string, Worksheet> WorksheetsByName(SpreadsheetDocument document)
    {
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        return workbookPart.Workbook.Sheets!.Elements<Sheet>().ToDictionary(
            sheet => sheet.Name!.Value!,
            sheet => Assert.IsType<WorksheetPart>(workbookPart.GetPartById(sheet.Id!.Value!)).Worksheet,
            StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, Stage8AppearanceTarget> Stage8TrackWTargets(uint glHeaderRow)
    {
        var targets = new Dictionary<string, Stage8AppearanceTarget>(StringComparer.Ordinal);
        AddTargets(targets, Stage8TrackWIds.Take(4), Stage8TrackWOutput.Positive, Step1Sheet, Cells(15, 17, 2, 2));
        AddTargets(targets, Stage8TrackWIds.Skip(4).Take(3), Stage8TrackWOutput.Positive, Step11Sheet, Cells(12, 16, 2, 6));
        AddTargets(targets, [Stage8TrackWIds[7]], Stage8TrackWOutput.Positive, Step11Sheet, Cells(12, 15, 2, 2));
        AddTargets(targets, Stage8TrackWIds.Skip(8).Take(2), Stage8TrackWOutput.Positive, Step11Sheet, Cells(16, 16, 2, 6));
        AddTargets(targets, Stage8TrackWIds.Skip(10).Take(4), Stage8TrackWOutput.MissingPreparer, Step12Sheet, ["B13"]);
        AddTargets(targets, Stage8TrackWIds.Skip(14).Take(5), Stage8TrackWOutput.Positive, FieldInfoSheet, ["A1"]);
        AddTargets(targets, Stage8TrackWIds.Skip(19).Take(4), Stage8TrackWOutput.Positive, FieldInfoSheet, Cells(3, 3, 1, 5));
        AddTargets(targets, Stage8TrackWIds.Skip(23).Take(4), Stage8TrackWOutput.Positive, FieldInfoSheet, Cells(glHeaderRow, glHeaderRow, 1, 5));
        targets.Add(Stage8TrackWIds[27], Stage8AppearanceTarget.Columns(Stage8TrackWOutput.Positive, FieldInfoSheet, 1, 5));
        AddTargets(targets, Stage8TrackWIds.Skip(28).Take(3), Stage8TrackWOutput.Positive, CalendarInfoSheet, Cells(1, 1, 1, 2));
        AddTargets(targets, Stage8TrackWIds.Skip(31).Take(3), Stage8TrackWOutput.Positive, CalendarInfoSheet, Cells(10, 10, 1, 3));
        targets.Add(Stage8TrackWIds[34], Stage8AppearanceTarget.Columns(Stage8TrackWOutput.Positive, CalendarInfoSheet, 1, 3));
        AddTargets(targets, Stage8TrackWIds.Skip(35).Take(3), Stage8TrackWOutput.Positive, CalendarInfoSheet, Cells(13, 13, 1, 2));
        targets.Add(Stage8TrackWIds[38], Stage8AppearanceTarget.Columns(Stage8TrackWOutput.Positive, CalendarInfoSheet, 1, 3));
        AddTargets(targets, Stage8TrackWIds.Skip(39).Take(3), Stage8TrackWOutput.Positive, AccountMappingSheet, Cells(1, 1, 1, 3));
        targets.Add(Stage8TrackWIds[42], Stage8AppearanceTarget.Columns(Stage8TrackWOutput.Positive, AccountMappingSheet, 1, 3));
        Assert.Equal(43, targets.Count);
        return targets;
    }

    private static void AddTargets(
        IDictionary<string, Stage8AppearanceTarget> targets,
        IEnumerable<string> ids,
        Stage8TrackWOutput output,
        string sheet,
        IReadOnlyList<string> cells)
    {
        foreach (var id in ids)
        {
            targets.Add(id, Stage8AppearanceTarget.Cells(output, sheet, cells));
        }
    }

    private static string[] Cells(uint firstRow, uint lastRow, uint firstColumn, uint lastColumn) =>
        Enumerable.Range(checked((int)firstRow), checked((int)(lastRow - firstRow + 1)))
            .SelectMany(row => Enumerable.Range(
                checked((int)firstColumn),
                checked((int)(lastColumn - firstColumn + 1))),
                (row, column) => $"{ColumnLetters(column)}{row}")
            .ToArray();

    private static IReadOnlyList<string> Stage8AppearanceMismatches(
        SpreadsheetDocument document,
        LegacyAppearanceRegistryEntry entry,
        Stage8AppearanceTarget target)
    {
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        var worksheet = WorksheetsByName(document)[target.Sheet];
        var mismatches = new List<string>();
        foreach (var reference in target.CellReferences)
        {
            var actual = ReadCellAppearance(workbookPart, worksheet, reference, entry.Property);
            var expected = entry.Property == "font.name"
                ? "微軟正黑體"
                : entry.NormalizedValue;
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                mismatches.Add($"{entry.Id}|{target.Sheet}!{reference}|{entry.Property}|expected={expected}|actual={actual}");
            }
        }
        foreach (var column in target.ColumnIndexes)
        {
            var actual = ReadColumnAutoFit(worksheet, column);
            if (!string.Equals(entry.NormalizedValue, actual, StringComparison.Ordinal))
            {
                mismatches.Add($"{entry.Id}|{target.Sheet}!column:{column}|{entry.Property}|expected={entry.NormalizedValue}|actual={actual}");
            }
        }
        return mismatches;
    }

    private static void AssertStage8Condition(
        SpreadsheetDocument document,
        SpreadsheetDocument template,
        IReadOnlyList<LegacyAppearanceRegistryEntry> entries,
        IReadOnlyDictionary<string, Stage8AppearanceTarget> targets,
        IReadOnlyList<string> ids,
        bool expectedApplied)
    {
        var mismatches = ids
            .Select(id => entries.Single(entry => entry.Id == id))
            .SelectMany(entry => Stage8AppearanceMismatches(document, entry, targets[entry.Id]))
            .ToArray();
        if (expectedApplied)
        {
            Assert.Empty(mismatches);
        }
        else
        {
            Assert.NotEmpty(mismatches);
            var actualWorkbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
            var templateWorkbookPart = Assert.IsType<WorkbookPart>(template.WorkbookPart);
            var actualSheets = WorksheetsByName(document);
            var templateSheets = WorksheetsByName(template);
            foreach (var id in ids)
            {
                var entry = entries.Single(item => item.Id == id);
                var target = targets[id];
                foreach (var reference in target.CellReferences)
                {
                    var actual = ReadCellAppearance(
                        actualWorkbookPart,
                        actualSheets[target.Sheet],
                        reference,
                        entry.Property);
                    if (entry.Property == "font.name")
                    {
                        Assert.Equal("微軟正黑體", actual);
                    }
                    else
                    {
                        Assert.Equal(
                            ReadCellAppearance(
                                templateWorkbookPart,
                                templateSheets[target.Sheet],
                                reference,
                                entry.Property),
                            actual);
                    }
                }
                foreach (var column in target.ColumnIndexes)
                {
                    Assert.Equal(
                        ReadColumnAutoFit(templateSheets[target.Sheet], column),
                        ReadColumnAutoFit(actualSheets[target.Sheet], column));
                }
            }
        }
    }

    private static void AssertStage8Applied(
        SpreadsheetDocument document,
        IReadOnlyList<LegacyAppearanceRegistryEntry> entries,
        IReadOnlyDictionary<string, Stage8AppearanceTarget> targets,
        IEnumerable<string> ids) =>
        Assert.Empty(ids
            .Select(id => entries.Single(entry => entry.Id == id))
            .SelectMany(entry => Stage8AppearanceMismatches(document, entry, targets[entry.Id])));

    private static void AssertStage8GeneratedCellMatchesBaseline(
        SpreadsheetDocument document,
        IReadOnlyList<LegacyAppearanceRegistryEntry> entries,
        IEnumerable<string> ids,
        string sheetName,
        string targetReference,
        string baselineReference)
    {
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        var sheet = WorksheetsByName(document)[sheetName];
        foreach (var id in ids)
        {
            var entry = entries.Single(item => item.Id == id);
            Assert.NotEqual(
                entry.NormalizedValue,
                ReadCellAppearance(workbookPart, sheet, targetReference, entry.Property));
            Assert.Equal(
                ReadCellAppearance(workbookPart, sheet, baselineReference, entry.Property),
                ReadCellAppearance(workbookPart, sheet, targetReference, entry.Property));
        }
    }

    private static void AssertReferenceBaseline(
        SpreadsheetDocument document,
        IReadOnlyList<LegacyAppearanceRegistryEntry> entries,
        IReadOnlyDictionary<string, Stage8AppearanceTarget> targets,
        IEnumerable<string> ids)
    {
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        var sheets = WorksheetsByName(document);
        foreach (var id in ids)
        {
            var entry = entries.Single(item => item.Id == id);
            var target = targets[id];
            foreach (var reference in target.CellReferences)
            {
                var expected = entry.Property switch
                {
                    "font.bold" => "true",
                    "font.name" => "微軟正黑體",
                    "font.size" => "11",
                    _ => throw new InvalidOperationException(
                        $"Unsupported condition-off property '{entry.Property}'.")
                };
                Assert.Equal(
                    expected,
                    ReadCellAppearance(workbookPart, sheets[target.Sheet], reference, entry.Property));
            }
            foreach (var column in target.ColumnIndexes)
            {
                Assert.Equal("false", ReadColumnAutoFit(sheets[target.Sheet], column));
            }
        }
    }

    private static void AssertReferenceConditionMatchesBaseline(
        SpreadsheetDocument document,
        SpreadsheetDocument baseline,
        IReadOnlyList<LegacyAppearanceRegistryEntry> entries,
        IReadOnlyDictionary<string, Stage8AppearanceTarget> targets,
        IEnumerable<string> ids,
        IReadOnlyDictionary<string, Stage8AppearanceTarget>? baselineTargets = null)
    {
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        var baselineWorkbookPart = Assert.IsType<WorkbookPart>(baseline.WorkbookPart);
        var sheets = WorksheetsByName(document);
        var baselineSheets = WorksheetsByName(baseline);
        foreach (var id in ids)
        {
            var entry = entries.Single(item => item.Id == id);
            var target = targets[id];
            var baselineTarget = (baselineTargets ?? targets)[id];
            for (var referenceIndex = 0; referenceIndex < target.CellReferences.Count; referenceIndex++)
            {
                var reference = target.CellReferences[referenceIndex];
                var baselineReference = baselineTarget.CellReferences[referenceIndex];
                Assert.Equal(
                    ReadCellAppearance(
                        baselineWorkbookPart,
                        baselineSheets[baselineTarget.Sheet],
                        baselineReference,
                        entry.Property),
                    ReadCellAppearance(
                        workbookPart,
                        sheets[target.Sheet],
                        reference,
                        entry.Property));
            }
            for (var columnIndex = 0; columnIndex < target.ColumnIndexes.Count; columnIndex++)
            {
                var column = target.ColumnIndexes[columnIndex];
                var baselineColumn = baselineTarget.ColumnIndexes[columnIndex];
                Assert.Equal(
                    ReadColumnAutoFit(baselineSheets[baselineTarget.Sheet], baselineColumn),
                    ReadColumnAutoFit(sheets[target.Sheet], column));
            }
        }
    }

    private static string ReadStage8CellText(
        SpreadsheetDocument document,
        string sheetName,
        string reference)
    {
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        var cell = WorksheetsByName(document)[sheetName].Descendants<Cell>()
            .Single(item => string.Equals(item.CellReference?.Value, reference, StringComparison.Ordinal));
        if (cell.DataType?.Value == CellValues.InlineString)
        {
            return cell.InlineString?.InnerText ?? string.Empty;
        }
        if (cell.DataType?.Value == CellValues.SharedString
            && int.TryParse(cell.CellValue?.Text, out var sharedIndex))
        {
            return workbookPart.SharedStringTablePart!.SharedStringTable!
                .Elements<SharedStringItem>()
                .ElementAt(sharedIndex)
                .InnerText;
        }
        return cell.CellValue?.Text ?? string.Empty;
    }

    private static string ReadCellAppearance(
        WorkbookPart workbookPart,
        Worksheet worksheet,
        string reference,
        string property)
    {
        var cell = worksheet.Descendants<Cell>()
            .SingleOrDefault(item => string.Equals(item.CellReference?.Value, reference, StringComparison.Ordinal));
        if (cell is null)
        {
            return "<missing-cell>";
        }
        var stylesheet = workbookPart.WorkbookStylesPart!.Stylesheet;
        var format = stylesheet.CellFormats!.Elements<CellFormat>()
            .ElementAt(checked((int)(cell.StyleIndex?.Value ?? 0U)));
        var font = stylesheet.Fonts!.Elements<DocumentFormat.OpenXml.Spreadsheet.Font>()
            .ElementAt(checked((int)(format.FontId?.Value ?? 0U)));
        return property switch
        {
            "font.bold" => (font.Bold is not null && (font.Bold.Val?.Value ?? true)) ? "true" : "false",
            "font.name" => font.FontName?.Val?.Value ?? "<missing-font-name>",
            "font.size" => font.FontSize?.Val?.Value.ToString("0.################", CultureInfo.InvariantCulture)
                ?? "<missing-font-size>",
            "font.colorArgb" => font.Color?.Rgb?.Value?.ToUpperInvariant() ?? "<missing-font-rgb>",
            "fill.foregroundArgb" => stylesheet.Fills!.Elements<Fill>()
                .ElementAt(checked((int)(format.FillId?.Value ?? 0U)))
                .PatternFill?.ForegroundColor?.Rgb?.Value?.ToUpperInvariant() ?? "<missing-fill-rgb>",
            _ => throw new InvalidOperationException($"Unsupported Stage 8 Track W property '{property}'.")
        };
    }

    private static string ReadColumnAutoFit(Worksheet worksheet, uint index)
    {
        var column = worksheet.GetFirstChild<Columns>()?.Elements<Column>()
            .LastOrDefault(item => item.Min?.Value <= index && item.Max?.Value >= index);
        return column?.BestFit?.Value == true && column.CustomWidth?.Value == true
            ? "true"
            : "false";
    }

    private sealed record Stage8TrackWWriteResult(byte[] Bytes, WorkpaperPlan Plan);

    private enum Stage8TrackWOutput
    {
        Positive,
        MissingPreparer
    }

    private sealed record Stage8AppearanceTarget(
        Stage8TrackWOutput Output,
        string Sheet,
        IReadOnlyList<string> CellReferences,
        IReadOnlyList<uint> ColumnIndexes)
    {
        internal static Stage8AppearanceTarget Cells(
            Stage8TrackWOutput output,
            string sheet,
            IReadOnlyList<string> cells) => new(output, sheet, cells, []);

        internal static Stage8AppearanceTarget Columns(
            Stage8TrackWOutput output,
            string sheet,
            uint first,
            uint last) => new(
                output,
                sheet,
                [],
                Enumerable.Range(checked((int)first), checked((int)(last - first + 1)))
                    .Select(value => checked((uint)value))
                    .ToArray());
    }

    private sealed class FixedStage8CalendarRepository(
        IReadOnlyList<CalendarDayEntry> holidays,
        IReadOnlyList<CalendarDayEntry> makeups) : ICalendarExportRepository
    {
        internal static FixedStage8CalendarRepository Empty { get; } = new([], []);

        public Task<IReadOnlyList<CalendarDayEntry>> FetchDaysAsync(
            string projectId,
            CalendarDayType type,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(type == CalendarDayType.Holiday ? holidays : makeups);
        }
    }

    private sealed class FixedStage8AccountMappingRepository(
        IReadOnlyList<AccountMappingExportRow> rows,
        bool sourceImported = true) : IAccountMappingExportRepository, IAccountMappingStore
    {
        internal static FixedStage8AccountMappingRepository Empty { get; } = new([], false);
        internal static FixedStage8AccountMappingRepository ImportedEmpty { get; } = new([], true);

        public Task<IReadOnlyList<AccountMappingExportRow>> FetchAllAsync(
            string projectId,
            string periodStart,
            string periodEnd,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(rows);
        }

        public Task<IReadOnlyList<AccountMappingTemplateRow>> FetchTemplateRowsAsync(
            string projectId,
            string periodStart,
            string periodEnd,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<AccountMappingTemplateRow> projected = rows
                .Select(row => new AccountMappingTemplateRow(row.AccountCode, row.AccountName))
                .ToArray();
            return Task.FromResult(projected);
        }

        public Task<AccountMappingImportResult> ImportAsync(
            string projectId,
            ImportSourceDescriptor source,
            IReadOnlyList<string> columns,
            IAsyncEnumerable<StagingRow> stagingRows,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Stage 8 fixed account-mapping source is read-only.");

        public Task<AccountMappingState?> FindStateAsync(
            string projectId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<AccountMappingState?>(sourceImported
                ? new AccountMappingState(
                    BatchId: "stage8-synthetic-account-mapping",
                    RowCount: rows.Count,
                    FileName: "stage8-synthetic-account-mapping.xlsx",
                    ImportedUtc: DateTimeOffset.UnixEpoch,
                    HasAnyCategory: rows.Count > 0,
                    HasRevenue: rows.Any(row => string.Equals(
                        row.Category,
                        AccountMappingCategories.Revenue,
                        StringComparison.Ordinal)),
                    HasCounterpart: rows.Any(row => AccountMappingCategories.CounterpartCategories.Contains(
                        row.Category)))
                : null);
        }
    }
}
