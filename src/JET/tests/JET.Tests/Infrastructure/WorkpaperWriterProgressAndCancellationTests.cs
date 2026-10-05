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

/// <summary>底稿寫出器：進度事件、取消、分段寫出與工作表限制。</summary>
public sealed class WorkpaperWriterProgressAndCancellationTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(29)]
    public void LegacyNumberStyles_RejectDecimalScaleOutsideDecimalRange(int decimalPlaces)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WorkpaperStyles.Number(decimalPlaces));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WorkpaperStyles.GroupedNumber(decimalPlaces));
    }

    [Fact]
    public async Task FullWorkbook_ReportsMonotonicProgressAfterEachCompletedSheet()
    {
        using var host = new HandlerTestHost();
        var project = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("validate.run");
        await host.DispatchAsync("prescreen.run");
        await using var stream = new MemoryStream();
        var progress = new List<WorkpaperProgress>();
        var context = await CurrentContextForAsync(host, project.ProjectId);

        var stats = await BuildWriter(host).WriteAsync(
            stream, context, CancellationToken.None, progress.Add);

        for (var index = 0; index < stats.SheetStats.Count; index++)
        {
            var stat = stats.SheetStats[index];
            Assert.Single(progress, update =>
                update.SheetName == stat.SheetName
                && update.SheetsCompleted == index + 1
                && update.RowsWritten == stat.RowsWritten);
        }
        Assert.Equal(
            progress.Select(update => update.SheetsCompleted).Order(),
            progress.Select(update => update.SheetsCompleted));
    }

    [Fact]
    public async Task WriteAsync_LongDataSheets_ReportRowsBeforeTheirCompletionEvent()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupContinuationMatrixAsync(host, voucherCount: 3);
        await ImportContinuationReferencesAsync(host, voucherCount: 3);
        await host.DispatchAsync("filter.commit", ContinuationScenarioPayload());
        var progress = new List<WorkpaperProgress>();
        await using var stream = new MemoryStream();

        var stats = await BuildWriter(
                host,
                new WorkpaperWriterOptions(),
                progressRowInterval: 2)
            .WriteAsync(
                stream,
                await CurrentContextForAsync(host, projectId),
                CancellationToken.None,
                progress.Add);

        AssertIntermediateBeforeCompletion(progress, stats, Step1Sheet, checkpointRows: 2);
        AssertIntermediateBeforeCompletion(progress, stats, Step4Sheet, checkpointRows: 2);
        AssertIntermediateBeforeCompletion(progress, stats, Step41Sheet, checkpointRows: 2);
    }

    [Fact]
    public async Task WriteAsync_Step4ReportsSheetStartBeforeFirstRowOrCompletion()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupContinuationMatrixAsync(host, voucherCount: 3);
        await ImportContinuationReferencesAsync(host, voucherCount: 3);
        await host.DispatchAsync("filter.commit", ContinuationScenarioPayload());
        var progress = new List<WorkpaperProgress>();
        await using var stream = new MemoryStream();

        await ((IWorkpaperPlanWriter)BuildWriter(host)).WriteAsync(
            stream,
            await CurrentContextForAsync(host, projectId),
            await CurrentPlanForAsync(
                host,
                await CurrentContextForAsync(host, projectId)),
            CancellationToken.None,
            progress.Add);

        var step3Completion = Assert.Single(
            progress,
            update => update.SheetName == Step3Sheet);
        var step4Updates = progress
            .Where(update => update.SheetName == Step4Sheet)
            .ToArray();
        var start = Assert.Single(
            step4Updates,
            update => update.SheetsCompleted == step3Completion.SheetsCompleted
                      && update.RowsWritten == 0);
        var completion = Assert.Single(
            step4Updates,
            update => update.SheetsCompleted == step3Completion.SheetsCompleted + 1);
        Assert.True(
            progress.IndexOf(start) < progress.IndexOf(completion),
            "Step4 sheet-start checkpoint must precede its first completion event.");
    }

    [Fact]
    public async Task FinalizedStep4_StreamsOnceAndLateLongTextUsesFullDataWidths()
    {
        const int voucherCount = 757;
        using var host = new HandlerTestHost();
        var longScenarioName = string.Concat(Enumerable.Repeat("晚頁高風險條件", 8));
        var longScenarioRationale = "LATE-RATIONALE-" + new string('R', 72);
        var longDocumentNumber = "ZZZ-VOUCHER-" + new string('9', 70);
        var longCreator = "ZZZ-" + string.Concat(Enumerable.Repeat("晚頁編製人員", 9));
        var projectId = await SetupContinuationMatrixAsync(host, voucherCount);
        await ImportContinuationReferencesAsync(host, voucherCount);
        await host.DispatchAsync(
            "filter.commit",
            ContinuationScenarioPayload(longScenarioName, longScenarioRationale));
        await ExecuteProjectSqlAsync(
            host,
            projectId,
            """
            UPDATE target_gl_entry
            SET document_number = @documentNumber,
                created_by = @creator
            WHERE document_number = @originalDocumentNumber;
            """,
            ("@documentNumber", longDocumentNumber),
            ("@creator", longCreator),
            ("@originalDocumentNumber", $"PAGE-{voucherCount:000}"));
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        var captured = new CapturingStep4StreamRepository(
            new LocalTagMatrixVoucherPageRepository(database));
        var context = await CurrentContextForAsync(host, projectId);
        var plan = await CurrentPlanForAsync(host, context);
        await using var stream = new MemoryStream();

        var stats = await ((IWorkpaperPlanWriter)BuildWriter(
                host,
                tagMatrixVouchersOverride: captured))
            .WriteAsync(
                stream,
                context,
                plan,
                CancellationToken.None);

        Assert.Equal(0, captured.PublicPageCalls);
        Assert.Equal(1, captured.StreamCalls);
        Assert.Equal(
            voucherCount,
            Assert.Single(
                stats.SheetStats,
                item => item.SheetName == Step4Sheet).RowsWritten);

        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var step3 = workbook.Worksheet(Step3Sheet);
        Assert.Equal(longScenarioName, step3.Cell("C19").GetString());
        Assert.Equal(longScenarioRationale, step3.Cell("D19").GetString());
        AssertFullDataWidth(step3, 3, longScenarioName);
        AssertFullDataWidth(step3, 4, longScenarioRationale);
        AssertSafeCountColumn(step3, 5);
        AssertSingleLine(step3.Cell("C19"));
        AssertSingleLine(step3.Cell("D19"));
        AssertSingleLine(step3.Cell("E19"));

        var step4 = workbook.Worksheet(Step4Sheet);
        Assert.Equal("PAGE-001", step4.Cell("B13").GetString());
        Assert.Equal(longDocumentNumber, step4.Cell(12 + voucherCount, 2).GetString());
        Assert.Equal(longCreator, step4.Cell(12 + voucherCount, 4).GetString());
        Assert.Equal(voucherCount, step4.Cell(12 + voucherCount, 1).GetValue<int>());
        Assert.Equal("Y", step4.Cell("F13").GetString());
        AssertFullDataWidth(step4, 2, longDocumentNumber);
        AssertFullDataWidth(step4, 4, longCreator);
        AssertSafeAmountColumn(step4, 5);
        AssertSingleLine(step4.Cell(12 + voucherCount, 2));
        AssertSingleLine(step4.Cell(12 + voucherCount, 4));
        AssertSingleLine(step4.Cell(12 + voucherCount, 5));
        AssertSingleLine(step4.Cell(12 + voucherCount, 16));
    }

    [Theory]
    [InlineData(Step12Sheet)]
    [InlineData(Step4Sheet)]
    [InlineData(Step41Sheet)]
    [InlineData(CalendarInfoSheet)]
    public async Task WriteAsync_CancelDuringCollectionBackedSheet_StopsBeforeSheetCompletion(string targetSheet)
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupContinuationMatrixAsync(host, voucherCount: 3);
        await ImportContinuationReferencesAsync(host, voucherCount: 3);
        await host.DispatchAsync("filter.commit", ContinuationScenarioPayload());
        using var cancellation = new CancellationTokenSource();
        var progress = new List<WorkpaperProgress>();
        await using var stream = new MemoryStream();
        var context = await CurrentContextForAsync(host, projectId);

        void OnProgress(WorkpaperProgress update)
        {
            progress.Add(update);
            if (update.SheetName == targetSheet && update.RowsWritten == 1)
            {
                cancellation.Cancel();
            }
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BuildWriter(
                host,
                new WorkpaperWriterOptions(),
                progressRowInterval: 1)
            .WriteAsync(
                stream,
                context,
                cancellation.Token,
                OnProgress));

        var intermediate = Assert.Single(progress, update =>
            update.SheetName == targetSheet && update.RowsWritten == 1);
        Assert.DoesNotContain(progress, update =>
            update.SheetName == targetSheet && update.SheetsCompleted > intermediate.SheetsCompleted);
    }

    [Fact]
    public async Task WriteAsync_TypedPlan_CancellationPropagatesBeforeSheetCompletion()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupContinuationMatrixAsync(host, voucherCount: 3);
        await ImportContinuationReferencesAsync(host, voucherCount: 3);
        await host.DispatchAsync("filter.commit", ContinuationScenarioPayload());
        using var cancellation = new CancellationTokenSource();
        var progress = new List<WorkpaperProgress>();
        await using var stream = new MemoryStream();
        var context = await CurrentContextForAsync(host, projectId);
        var plan = await CurrentPlanForAsync(host, context);
        var writer = (IWorkpaperPlanWriter)BuildWriter(
            host,
            new WorkpaperWriterOptions(),
            progressRowInterval: 1);

        void OnProgress(WorkpaperProgress update)
        {
            progress.Add(update);
            if (update.SheetName == Step4Sheet && update.RowsWritten == 1)
            {
                cancellation.Cancel();
            }
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteAsync(
            stream,
            context,
            plan,
            cancellation.Token,
            OnProgress));

        var intermediate = Assert.Single(progress, update =>
            update.SheetName == Step4Sheet && update.RowsWritten == 1);
        Assert.DoesNotContain(progress, update =>
            update.SheetName == Step4Sheet
            && update.SheetsCompleted > intermediate.SheetsCompleted);
    }

    [Fact]
    public async Task DataDrivenSheets_ContinueBeforeConfiguredRowLimit_WithoutLossOrJetAddedPhysicalSettings()
    {
        const uint continuationRowLimit = 25;
        using var host = new HandlerTestHost();
        var projectId = await SetupContinuationMatrixAsync(host);
        await ImportContinuationReferencesAsync(host);
        await host.DispatchAsync("filter.commit", ContinuationScenarioPayload());

        var completenessDiffs = new FixedCompletenessDiffPageRepository(
            Enumerable.Range(1, 26)
                .Select(index => new CompletenessDiffAccount(
                    AccountCode: $"D{index:000}",
                    AccountName: $"借方科目{index:000}",
                    TbAmountScaled: 110_000,
                    GlAmountScaled: 100_000,
                    DiffScaled: 10_000,
                    NotInTb: false))
                .ToArray());

        await using var stream = new MemoryStream();
        var context = await CurrentContextForAsync(host, projectId);
        var plan = await CurrentPlanForAsync(host, context, completenessDiffs);
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        var capturedStep41 = new CapturingStep41PreparedRepository(
            new LocalTagMatrixRowPageRepository(database));
        var writer = (IWorkpaperPlanWriter)BuildWriter(
            host,
            new WorkpaperWriterOptions(continuationRowLimit),
            tagMatrixRowsOverride: capturedStep41,
            completenessDiffsOverride: completenessDiffs);
        var stats = await writer.WriteAsync(
            stream,
            context,
            plan,
            CancellationToken.None);

        var step4Stats = stats.SheetStats.Where(stat => IsSheetSeries(stat.SheetName, Step4Sheet)).ToList();
        var step41Stats = stats.SheetStats.Where(stat => IsSheetSeries(stat.SheetName, Step41Sheet)).ToList();
        Assert.True(step4Stats.Count > 1, "縮小列上限後 step4 應建立續頁");
        Assert.True(step41Stats.Count > 1, "縮小列上限後 step4-1 應建立續頁");
        Assert.Equal(26L, step4Stats.Sum(stat => stat.RowsWritten));
        Assert.Equal(52L, step41Stats.Sum(stat => stat.RowsWritten));
        Assert.All(step4Stats, stat => Assert.InRange(stat.RowsWritten, 1L, 13L));
        Assert.All(step41Stats, stat => Assert.InRange(stat.RowsWritten, 1L, 20L));
        Assert.Equal(step4Stats.Count, step4Stats.Select(stat => stat.SheetName).Distinct().Count());
        Assert.Equal(step41Stats.Count, step41Stats.Select(stat => stat.SheetName).Distinct().Count());
        Assert.Equal(0, capturedStep41.TypedPageCalls);
        var preparedMetrics = Assert.IsType<WorkpaperStep41PreparedSessionMetrics>(
            capturedStep41.Metrics);
        Assert.Equal(1, preparedMetrics.SchemaReadinessCommands);
        Assert.Equal(1, preparedMetrics.Connections);
        Assert.Equal(1, preparedMetrics.Transactions);
        Assert.Equal(1, preparedMetrics.TemporaryTableInitializationCommands);
        Assert.Equal(1, preparedMetrics.HitVoucherMaterializationCommands);
        Assert.Equal(1, preparedMetrics.RowTagMaterializationCommands);
        Assert.Equal(1, preparedMetrics.WidthAggregations);
        Assert.Equal(1, preparedMetrics.OrderedReaderCommands);
        Assert.Equal(1, preparedMetrics.CleanupCommands);
        Assert.Equal(52, preparedMetrics.RowsRead);
        Assert.True(preparedMetrics.ReaderCompleted);
        Assert.True(preparedMetrics.CleanupCommandSucceeded);
        Assert.True(preparedMetrics.TransactionCommitted);
        Assert.False(preparedMetrics.TransactionRolledBack);
        Assert.True(preparedMetrics.DedicatedConnection);
        Assert.True(preparedMetrics.TransactionDisposed);
        Assert.True(preparedMetrics.ConnectionDisposed);
        Assert.True(preparedMetrics.Disposed);
        Assert.True(preparedMetrics.TemporaryObjectsCleared);
        Assert.Equal(
            [
                "schemaReadiness",
                "initializeTemporaryTables",
                "materializeHitVouchers",
                "materializeRowTags",
                "orderedRows",
                "cleanup"
            ],
            preparedMetrics.Commands.Select(command => command.Operation));
        Assert.All(step4Stats.Concat(step41Stats), stat =>
            Assert.InRange(stat.SheetName.Length, 1, ExcelWorksheetConstraints.MaxSheetNameLength));

        var expectedContinuedSeries = new[]
        {
            Step1Sheet,
            Step11Sheet,
            Step12Sheet,
            Step13Sheet,
            Step4Sheet,
            Step41Sheet,
            CalendarInfoSheet,
            AccountMappingSheet
        };
        foreach (var baseName in expectedContinuedSeries)
        {
            Assert.True(stats.SheetStats.Count(stat => IsSheetSeries(stat.SheetName, baseName)) > 1,
                $"{baseName} 的合成資料超過本測試列容量，應建立續頁");
        }
        Assert.Equal(27L, SumSeriesRows(stats, Step1Sheet));
        Assert.Equal(26L, SumSeriesRows(stats, Step11Sheet));
        Assert.Equal(26L, SumSeriesRows(stats, Step12Sheet));
        Assert.Equal(26L, SumSeriesRows(stats, Step13Sheet));
        Assert.Equal(36L, SumSeriesRows(stats, CalendarInfoSheet));
        Assert.Equal(27L, SumSeriesRows(stats, AccountMappingSheet));

        stream.Position = 0;
        using (var workbook = new XLWorkbook(stream))
        {
            var step4Ordinals = new List<long>();
            foreach (var stat in step4Stats)
            {
                var sheet = workbook.Worksheet(stat.SheetName);
                Assert.Equal("傳票號碼", sheet.Cell("B11").GetString());
                Assert.Contains("母體#2", sheet.Cell("A12").GetString(), StringComparison.Ordinal);
                if (!string.Equals(stat.SheetName, Step4Sheet, StringComparison.Ordinal))
                {
                    Assert.True((sheet.LastRowUsed()?.RowNumber() ?? 0) <= continuationRowLimit);
                }
                for (var row = 13; row < 13 + stat.RowsWritten; row++)
                {
                    step4Ordinals.Add(sheet.Cell(row, 1).GetValue<long>());
                }
            }
            Assert.Equal(Enumerable.Range(1, 26).Select(value => (long)value), step4Ordinals);

            var step41Keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var stat in step41Stats)
            {
                var sheet = workbook.Worksheet(stat.SheetName);
                Assert.Equal("傳票號碼_JE", sheet.Cell("A5").GetString());
                if (!string.Equals(stat.SheetName, Step41Sheet, StringComparison.Ordinal))
                {
                    Assert.True((sheet.LastRowUsed()?.RowNumber() ?? 0) <= continuationRowLimit);
                }
                for (var row = 6; row < 6 + stat.RowsWritten; row++)
                {
                    Assert.True(step41Keys.Add($"{sheet.Cell(row, 1).GetString()}\u001f{sheet.Cell(row, 2).GetString()}"),
                        "step4-1 續頁不得重複傳票行");
                }
            }
            Assert.Equal(52, step41Keys.Count);

            foreach (var baseName in new[] { CalendarInfoSheet, AccountMappingSheet })
            {
                var seriesWidths = stats.SheetStats
                    .Where(stat => IsSheetSeries(stat.SheetName, baseName))
                    .Select(stat => Enumerable.Range(1, 3)
                        .Select(column => workbook.Worksheet(stat.SheetName).Column(column).Width)
                        .ToArray())
                    .ToArray();
                var expectedWidths = seriesWidths[0];
                Assert.All(
                    seriesWidths.Skip(1),
                    actualWidths => Assert.Equal(expectedWidths, actualWidths));
            }
        }

        stream.Position = 0;
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbookPart = Assert.IsType<WorkbookPart>(document.WorkbookPart);
        foreach (var stat in step4Stats.Concat(step41Stats))
        {
            var sheet = workbookPart.Workbook.Sheets!.Elements<Sheet>()
                .Single(item => item.Name?.Value == stat.SheetName);
            var part = Assert.IsType<WorksheetPart>(workbookPart.GetPartById(sheet.Id!.Value!));
            var worksheet = part.Worksheet;
            Assert.NotNull(worksheet.Elements<Columns>().SingleOrDefault());
            if (!string.Equals(stat.SheetName, Step4Sheet, StringComparison.Ordinal)
                && !string.Equals(stat.SheetName, Step41Sheet, StringComparison.Ordinal))
            {
                Assert.Empty(worksheet.Elements<SheetViews>());
                Assert.Empty(worksheet.Descendants<Pane>());
                Assert.Empty(worksheet.Elements<PageMargins>());
                Assert.Empty(worksheet.Elements<PageSetup>());
            }
            Assert.NotNull(worksheet.Elements<SheetProtection>().SingleOrDefault());

            var validations = worksheet.Descendants<DataValidation>().ToArray();
            if (IsSheetSeries(stat.SheetName, Step4Sheet))
            {
                var validation = Assert.Single(validations);
                Assert.Equal(
                    $"P13:T{12 + stat.RowsWritten}",
                    validation.SequenceOfReferences?.InnerText);
                Assert.Equal("\"Y,N,N/A\"", validation.Formula1?.Text);
            }
            else
            {
                Assert.Empty(validations);
            }

            if (IsSheetSeries(stat.SheetName, Step4Sheet))
            {
                if (!string.Equals(stat.SheetName, Step4Sheet, StringComparison.Ordinal))
                {
                    AssertCellUnlocked(workbookPart, worksheet, "P13");
                    AssertCellNumberFormat(workbookPart, worksheet, "E13", "#,##0.0000");
                }
            }
            else
            {
                if (!string.Equals(stat.SheetName, Step41Sheet, StringComparison.Ordinal))
                {
                    var amountColumn = plan.Step41Columns
                        .Select((column, index) => (column, index))
                        .Single(item =>
                            item.column.ValueSource
                            == WorkpaperStep41ValueSource.SignedAmount)
                        .index + 1;
                    var amountReference = $"{ColumnLetters(amountColumn)}6";
                    AssertCellNumberFormat(
                        workbookPart,
                        worksheet,
                        amountReference,
                        "#,##0.0000");
                }
            }

            var headerRow = IsSheetSeries(stat.SheetName, Step4Sheet) ? 11U : 5U;
            var row = worksheet.GetFirstChild<SheetData>()!.Elements<Row>()
                .First(item => item.RowIndex?.Value == headerRow);
            if (!string.Equals(stat.SheetName, Step4Sheet, StringComparison.Ordinal)
                && !string.Equals(stat.SheetName, Step41Sheet, StringComparison.Ordinal))
            {
                Assert.Equal(42d, row.Height!.Value);
            }
            else
            {
                Assert.NotNull(row.Height);
            }
        }

        AssertEditableCells(workbookPart, Step12Sheet, ["C12", "F12", "G12", "H12"]);
        AssertEditableCells(workbookPart, Step13Sheet, ["E17", "F17", "G17"]);
    }

    [Fact]
    public void ExcelWorksheetConstraints_RejectOutOfRangeCells_AndKeepContinuationNamesSafe()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ExcelWorksheetConstraints.EnsureCell(ExcelWorksheetConstraints.MaxRows + 1, 1));
        Assert.Throws<InvalidOperationException>(() =>
            ExcelWorksheetConstraints.EnsureCell(1, ExcelWorksheetConstraints.MaxColumns + 1));

        var name = ExcelWorksheetConstraints.ContinuationSheetName(
            "step4-1 符合高風險條件傳票明細/不合法字元與過長名稱",
            12);
        Assert.InRange(name.Length, 1, ExcelWorksheetConstraints.MaxSheetNameLength);
        Assert.DoesNotContain('/', name);
        Assert.EndsWith(" (續12)", name, StringComparison.Ordinal);
        Assert.Equal(Step4Sheet, ExcelWorksheetConstraints.ContinuationSheetName(Step4Sheet, 1));
    }

}
