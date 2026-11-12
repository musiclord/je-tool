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
using static JET.Tests.Infrastructure.WorkingPaperWriterTestSupport;

namespace JET.Tests.Infrastructure;

/// <summary>底稿寫出器：已登記的 43 項外觀逐格比對與空狀態條件。其中欄位資訊頁 A1 的版本列五項，
/// JET 不寫，登記成沒有對應，不比對外觀。</summary>
public sealed class WorkingPaperWriterAppearanceTests
{
    [Fact]
    public void Registry_IsTheExactClosedFortyThreeIdSet()
    {
        var actual = WorkingPaperAppearanceEntries().Select(entry => entry.Id).ToArray();

        Assert.Equal(43, actual.Length);
        Assert.Equal(WorkingPaperAppearanceIds, actual);
    }

    [Fact]
    public async Task RegisteredAppearances_HaveNoCellOrColumnMismatch()
    {
        using var positiveHost = new HandlerTestHost();
        var positiveProject = await SetupContinuationProjectAsync(positiveHost, voucherCount: 3);
        var differences = DifferenceRows(1);
        var positive = await WriteAppearanceWorkingPaperAsync(
            positiveHost,
            positiveProject,
            differences,
            new FixedAppearanceCalendarRepository(
                [new CalendarDayEntry("2025-01-01", "Synthetic holiday")],
                [new CalendarDayEntry("2025-02-08", "Synthetic make-up day")]),
            new FixedAppearanceAccountMappingRepository(
                [new AccountMappingExportRow("SYN-001", "Synthetic account", "Cash", false)]));

        using var missingPreparerHost = new HandlerTestHost();
        var missingPreparerProject = await SetupMissingPreparerMappingAsync(missingPreparerHost);
        var missingPreparer = await WriteAppearanceWorkingPaperAsync(
            missingPreparerHost,
            missingPreparerProject,
            DifferenceRows(0),
            FixedAppearanceCalendarRepository.Empty,
            FixedAppearanceAccountMappingRepository.Empty);

        using var positiveDocument = OpenWorkingPaper(positive.Bytes);
        using var missingPreparerDocument = OpenWorkingPaper(missingPreparer.Bytes);
        var glHeaderRow = checked((uint)positive.Plan.FieldInfo!.TbRows.Count + 7U);
        var targets = WorkingPaperAppearanceTargets(glHeaderRow);
        var entries = WorkingPaperAppearanceEntries();
        var mismatches = new List<string>();

        // 底稿欄位資訊頁不寫版本字樣與藍底紅字的版本列，那五項在登記檔記成沒有對應。
        // 這裡只比對其餘 38 項，並確認沒有對應的恰好是那五項。A1 不寫任何東西另由
        // ReportWorkbookPresentationTests 與 WorkingPaperWriterCoverAndReferenceSheetTests 確認。
        Assert.Equal(
            [
                "workingpaper-field-info-version-bold", "workingpaper-field-info-version-font", "workingpaper-field-info-version-size",
                "workingpaper-field-info-version-color", "workingpaper-field-info-version-fill"
            ],
            entries.Where(entry => entry.NoCorrespondenceReason is not null).Select(entry => entry.Id));
        foreach (var entry in entries.Where(entry => entry.NoCorrespondenceReason is null))
        {
            var target = targets[entry.Id];
            var document = target.Output == WorkingPaperAppearanceOutput.Positive
                ? positiveDocument
                : missingPreparerDocument;
            mismatches.AddRange(WorkingPaperAppearanceMismatches(document, entry, target));
        }

        Assert.Empty(mismatches);
        AssertGeneratedCellMatchesBaseline(
            positiveDocument,
            entries,
            WorkingPaperAppearanceIds.Skip(10).Take(4),
            Step12Sheet,
            targetReference: "B13",
            baselineReference: "B12");
    }

    // 國定假日填滿第一頁、沒有補班日時，補班日的提示句寫在續頁第 1 列，照舊版不加粗。
    [Fact]
    public async Task CalendarContinuation_WithoutMakeupDays_WritesNoticeOnContinuationPage()
    {
        const uint continuationRowLimit = 25;
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);
        var output = await WriteAppearanceWorkingPaperAsync(
            host,
            projectId,
            DifferenceRows(0),
            new FixedAppearanceCalendarRepository(
                Enumerable.Range(1, 14)
                    .Select(index => new CalendarDayEntry($"2025-07-{index:00}", "Synthetic holiday"))
                    .ToArray(),
                []),
            FixedAppearanceAccountMappingRepository.Empty,
            new WorkingPaperWriterOptions(continuationRowLimit));

        using var document = OpenWorkingPaper(output.Bytes);
        Assert.Equal("DATE_OF_HOLIDAY", ReadCellText(document, CalendarInfoSheet, "A10"));
        Assert.Equal("Synthetic holiday", ReadCellText(document, CalendarInfoSheet, "B24"));
        var continuation = ExcelWorksheetConstraints.ContinuationSheetName(CalendarInfoSheet, 2);
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        var sheet = WorksheetsByName(document)[continuation];
        Assert.Equal("未設定補班日/結帳日資訊", ReadCellText(document, continuation, "A1"));
        Assert.Equal("false", ReadCellAppearance(workbookPart, sheet, "A1", "font.bold"));
    }

    // 空白的那一段在原本標題的那一列寫舊版提示句，照舊版不加粗，B、C 欄不寫字。
    private static void AssertCalendarNoticeRow(SpreadsheetDocument document, uint row, string notice)
    {
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        var sheet = WorksheetsByName(document)[CalendarInfoSheet];
        Assert.Equal(notice, ReadCellText(document, CalendarInfoSheet, $"A{row}"));
        Assert.Equal("false", ReadCellAppearance(workbookPart, sheet, $"A{row}", "font.bold"));
        Assert.Equal("微軟正黑體", ReadCellAppearance(workbookPart, sheet, $"A{row}", "font.name"));
        Assert.Equal("11", ReadCellAppearance(workbookPart, sheet, $"A{row}", "font.size"));
        foreach (var column in new[] { "B", "C" })
        {
            var reference = $"{column}{row}";
            var cell = sheet.Descendants<Cell>()
                .SingleOrDefault(item => string.Equals(item.CellReference?.Value, reference, StringComparison.Ordinal));
            Assert.True(cell is null || string.IsNullOrEmpty(cell.InnerText), $"{reference} 不應該有文字");
        }
    }

    [Fact]
    public async Task CompletenessAndUnbalancedConditions_CoverAllFourStates()
    {
        using var host = new HandlerTestHost();
        var balancedProject = await SetupBalancedAsync(host);
        var none = await WriteAppearanceWorkingPaperAsync(
            host,
            balancedProject,
            DifferenceRows(0),
            FixedAppearanceCalendarRepository.Empty,
            FixedAppearanceAccountMappingRepository.Empty);
        var completenessOnly = await WriteAppearanceWorkingPaperAsync(
            host,
            balancedProject,
            DifferenceRows(1),
            FixedAppearanceCalendarRepository.Empty,
            FixedAppearanceAccountMappingRepository.Empty);

        var unbalancedProject = await SetupContinuationProjectAsync(host, voucherCount: 3);
        var unbalancedOnly = await WriteAppearanceWorkingPaperAsync(
            host,
            unbalancedProject,
            DifferenceRows(0),
            FixedAppearanceCalendarRepository.Empty,
            FixedAppearanceAccountMappingRepository.Empty);
        var both = await WriteAppearanceWorkingPaperAsync(
            host,
            unbalancedProject,
            DifferenceRows(1),
            FixedAppearanceCalendarRepository.Empty,
            FixedAppearanceAccountMappingRepository.Empty);

        using var noneDocument = OpenWorkingPaper(none.Bytes);
        using var completenessDocument = OpenWorkingPaper(completenessOnly.Bytes);
        using var unbalancedVoucher = OpenWorkingPaper(unbalancedOnly.Bytes);
        using var bothDocument = OpenWorkingPaper(both.Bytes);
        using var templateDocument = SpreadsheetDocument.Open(
            Path.Combine(AppContext.BaseDirectory, "Templates", "WorkingPaper.xlsx"),
            false);
        var entries = WorkingPaperAppearanceEntries();
        var targets = WorkingPaperAppearanceTargets(1);
        var completenessIds = WorkingPaperAppearanceIds.Take(4).ToArray();
        var unbalancedIds = WorkingPaperAppearanceIds.Skip(4).Take(6).ToArray();

        AssertAppearanceCondition(noneDocument, templateDocument, entries, targets, completenessIds, expectedApplied: false);
        AssertAppearanceCondition(noneDocument, templateDocument, entries, targets, unbalancedIds, expectedApplied: false);
        AssertAppearanceCondition(completenessDocument, templateDocument, entries, targets, completenessIds, expectedApplied: true);
        AssertAppearanceCondition(completenessDocument, templateDocument, entries, targets, unbalancedIds, expectedApplied: false);
        AssertAppearanceCondition(unbalancedVoucher, templateDocument, entries, targets, completenessIds, expectedApplied: false);
        AssertAppearanceCondition(unbalancedVoucher, templateDocument, entries, targets, unbalancedIds, expectedApplied: true);
        AssertAppearanceCondition(bothDocument, templateDocument, entries, targets, completenessIds, expectedApplied: true);
        AssertAppearanceCondition(bothDocument, templateDocument, entries, targets, unbalancedIds, expectedApplied: true);
    }

    [Fact]
    public async Task ReferenceSourceConditions_KeepHeadersAndProveEachEmptyState()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);
        var empty = await WriteAppearanceWorkingPaperAsync(
            host,
            projectId,
            DifferenceRows(0),
            FixedAppearanceCalendarRepository.Empty,
            FixedAppearanceAccountMappingRepository.Empty);
        var importedEmptyAccount = await WriteAppearanceWorkingPaperAsync(
            host,
            projectId,
            DifferenceRows(0),
            FixedAppearanceCalendarRepository.Empty,
            FixedAppearanceAccountMappingRepository.ImportedEmpty);
        var holidayOnly = await WriteAppearanceWorkingPaperAsync(
            host,
            projectId,
            DifferenceRows(0),
            new FixedAppearanceCalendarRepository(
                [new CalendarDayEntry("2025-01-01", "Synthetic holiday")],
                []),
            FixedAppearanceAccountMappingRepository.Empty);
        var makeupOnly = await WriteAppearanceWorkingPaperAsync(
            host,
            projectId,
            DifferenceRows(0),
            new FixedAppearanceCalendarRepository(
                [],
                [new CalendarDayEntry("2025-02-08", "Synthetic make-up day")]),
            FixedAppearanceAccountMappingRepository.Empty);
        var accountOnly = await WriteAppearanceWorkingPaperAsync(
            host,
            projectId,
            DifferenceRows(0),
            FixedAppearanceCalendarRepository.Empty,
            new FixedAppearanceAccountMappingRepository(
                [new AccountMappingExportRow("SYN-001", "Synthetic account", "Cash", false)]));

        using var emptyDocument = OpenWorkingPaper(empty.Bytes);
        using var importedEmptyAccountDocument = OpenWorkingPaper(importedEmptyAccount.Bytes);
        using var holidayDocument = OpenWorkingPaper(holidayOnly.Bytes);
        using var makeupDocument = OpenWorkingPaper(makeupOnly.Bytes);
        using var accountDocument = OpenWorkingPaper(accountOnly.Bytes);
        var entries = WorkingPaperAppearanceEntries();
        var normalTargets = WorkingPaperAppearanceTargets(1);
        var makeupTargets = normalTargets.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var id in WorkingPaperAppearanceIds.Skip(35).Take(3))
        {
            makeupTargets[id] = LegacyAppearanceTarget.Cells(
                WorkingPaperAppearanceOutput.Positive,
                CalendarInfoSheet,
                Cells(12, 12, 1, 2));
        }
        var holidayWithoutMakeupTargets = normalTargets.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        foreach (var id in WorkingPaperAppearanceIds.Skip(35).Take(3))
        {
            holidayWithoutMakeupTargets[id] = LegacyAppearanceTarget.Cells(
                WorkingPaperAppearanceOutput.Positive,
                CalendarInfoSheet,
                Cells(13, 13, 1, 2));
        }

        AssertAppearanceApplied(emptyDocument, entries, normalTargets, WorkingPaperAppearanceIds.Skip(28).Take(3));
        AssertAppearanceApplied(holidayDocument, entries, normalTargets, WorkingPaperAppearanceIds.Skip(31).Take(4));
        AssertAppearanceApplied(makeupDocument, entries, makeupTargets, WorkingPaperAppearanceIds.Skip(35).Take(4));
        AssertAppearanceApplied(accountDocument, entries, normalTargets, WorkingPaperAppearanceIds.Skip(39).Take(4));
        AssertAppearanceApplied(
            importedEmptyAccountDocument,
            entries,
            normalTargets,
            WorkingPaperAppearanceIds.Skip(39).Take(4));

        // 沒有國定假日與補班日時，該段標題的那一列寫舊版的提示句，照舊版不加粗，
        // 所以空白案件的這兩列不比對標題外觀，只確認提示句；欄寬不自動調整仍要比對。
        AssertReferenceBaseline(
            emptyDocument,
            entries,
            normalTargets,
            [WorkingPaperAppearanceIds[34]]);
        AssertCalendarNoticeRow(emptyDocument, 10, "未設定假期資訊");
        AssertReferenceBaseline(
            emptyDocument,
            entries,
            makeupTargets,
            [WorkingPaperAppearanceIds[38]]);
        AssertCalendarNoticeRow(emptyDocument, 12, "未設定補班日/結帳日資訊");
        AssertReferenceBaseline(
            emptyDocument,
            entries,
            normalTargets,
            WorkingPaperAppearanceIds.Skip(39).Take(4));

        // A sibling source may be present without activating this source's header patch.
        // AutoFit is compared only where the sibling source does not share the same columns.
        AssertReferenceConditionMatchesBaseline(
            makeupDocument,
            emptyDocument,
            entries,
            normalTargets,
            WorkingPaperAppearanceIds.Skip(31).Take(3));
        AssertReferenceConditionMatchesBaseline(
            holidayDocument,
            emptyDocument,
            entries,
            holidayWithoutMakeupTargets,
            WorkingPaperAppearanceIds.Skip(35).Take(3),
            makeupTargets);
        AssertReferenceConditionMatchesBaseline(
            holidayDocument,
            emptyDocument,
            entries,
            normalTargets,
            WorkingPaperAppearanceIds.Skip(39).Take(4));
        AssertReferenceConditionMatchesBaseline(
            accountDocument,
            emptyDocument,
            entries,
            normalTargets,
            WorkingPaperAppearanceIds.Skip(31).Take(4));
        AssertReferenceConditionMatchesBaseline(
            accountDocument,
            emptyDocument,
            entries,
            makeupTargets,
            WorkingPaperAppearanceIds.Skip(35).Take(4));

        Assert.Equal("DAYOFWEEK", ReadCellText(emptyDocument, CalendarInfoSheet, "A1"));
        // 空白案件的 A10、A12 是提示句（見上方 AssertCalendarNoticeRow），標題只在有日子的那一段出現。
        Assert.Equal("DATE_OF_HOLIDAY", ReadCellText(holidayDocument, CalendarInfoSheet, "A10"));
        Assert.Equal("未設定補班日/結帳日資訊", ReadCellText(holidayDocument, CalendarInfoSheet, "A13"));
        Assert.Equal("未設定假期資訊", ReadCellText(makeupDocument, CalendarInfoSheet, "A10"));
        Assert.Equal("DATE_OF_MAKEUPDAY", ReadCellText(makeupDocument, CalendarInfoSheet, "A12"));
        Assert.Equal("GL_NUMBER", ReadCellText(emptyDocument, AccountMappingSheet, "A1"));
        Assert.Equal(
            "GL_NUMBER",
            ReadCellText(importedEmptyAccountDocument, AccountMappingSheet, "A1"));
    }

    [Fact]
    public async Task JetPhysicalSettingsAreAbsent_AndBaseTemplateSettingsSurvive()
    {
        const uint continuationRowLimit = 25;
        using var host = new HandlerTestHost();
        var projectId = await SetupContinuationProjectAsync(host);
        await ImportContinuationReferencesAsync(host);
        await host.DispatchAsync("filter.commit", ContinuationScenarioPayload());
        var output = await WriteAppearanceWorkingPaperAsync(
            host,
            projectId,
            DifferenceRows(26),
            new FixedAppearanceCalendarRepository(
                Enumerable.Range(1, 26)
                    .Select(index => new CalendarDayEntry($"2025-07-{index:00}", "Synthetic holiday"))
                    .ToArray(),
                [new CalendarDayEntry("2025-08-02", "Synthetic make-up day")]),
            new FixedAppearanceAccountMappingRepository(
                Enumerable.Range(1, 26)
                    .Select(index => new AccountMappingExportRow(
                        $"SYN-{index:000}",
                        "Synthetic account",
                        "Others",
                        false))
                    .ToArray()),
            new WorkingPaperWriterOptions(continuationRowLimit));

        var templatePath = Path.Combine(AppContext.BaseDirectory, "Templates", "WorkingPaper.xlsx");
        using var template = SpreadsheetDocument.Open(templatePath, false);
        using var actual = OpenWorkingPaper(output.Bytes);
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
