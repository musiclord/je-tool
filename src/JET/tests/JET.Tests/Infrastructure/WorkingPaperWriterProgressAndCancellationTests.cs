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

/// <summary>底稿寫出器：進度事件、取消、分段寫出與工作表限制。</summary>
public sealed class WorkingPaperWriterProgressAndCancellationTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(29)]
    public void LegacyNumberStyles_RejectDecimalScaleOutsideDecimalRange(int decimalPlaces)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WorkingPaperStyles.Number(decimalPlaces));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WorkingPaperStyles.GroupedNumber(decimalPlaces));
    }

    [Fact]
    public async Task FullWorkbook_ReportsMonotonicProgressAfterEachCompletedSheet()
    {
        using var host = new HandlerTestHost();
        var project = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("validate.run");
        await host.DispatchAsync("prescreen.run");
        await using var stream = new MemoryStream();
        var progress = new List<WorkingPaperProgress>();
        var context = await CurrentContextForAsync(host, project.ProjectId);

        var stats = await WriteWithCurrentPlanAsync(
            host, BuildWriter(host), stream, context, CancellationToken.None, progress.Add);

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
        var projectId = await SetupContinuationProjectAsync(host, voucherCount: 3);
        await ImportContinuationReferencesAsync(host, voucherCount: 3);
        await host.DispatchAsync("filter.commit", ContinuationScenarioPayload());
        var progress = new List<WorkingPaperProgress>();
        await using var stream = new MemoryStream();

        var stats = await WriteWithCurrentPlanAsync(
            host,
            BuildWriter(host, new WorkingPaperWriterOptions(), progressRowInterval: 2),
            stream,
            await CurrentContextForAsync(host, projectId),
            CancellationToken.None,
            progress.Add);

        AssertIntermediateBeforeCompletion(progress, stats, CompletenessSheet, checkpointRows: 2);
        AssertIntermediateBeforeCompletion(progress, stats, HighRiskVoucherSheet, checkpointRows: 2);
        AssertIntermediateBeforeCompletion(progress, stats, HighRiskVoucherDetailSheet, checkpointRows: 2);
    }

    [Fact]
    public async Task WriteAsync_HighRiskVoucherSheetReportsStartBeforeFirstRowOrCompletion()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupContinuationProjectAsync(host, voucherCount: 3);
        await ImportContinuationReferencesAsync(host, voucherCount: 3);
        await host.DispatchAsync("filter.commit", ContinuationScenarioPayload());
        var progress = new List<WorkingPaperProgress>();
        await using var stream = new MemoryStream();

        await ((IWorkingPaperPlanWriter)BuildWriter(host)).WriteAsync(
            stream,
            await CurrentContextForAsync(host, projectId),
            await CurrentPlanForAsync(
                host,
                await CurrentContextForAsync(host, projectId)),
            CancellationToken.None,
            progress.Add);

        var highRiskSummaryCompletion = Assert.Single(
            progress,
            update => update.SheetName == HighRiskSummarySheet);
        var highRiskVoucherUpdates = progress
            .Where(update => update.SheetName == HighRiskVoucherSheet)
            .ToArray();
        var start = Assert.Single(
            highRiskVoucherUpdates,
            update => update.SheetsCompleted == highRiskSummaryCompletion.SheetsCompleted
                      && update.RowsWritten == 0);
        var completion = Assert.Single(
            highRiskVoucherUpdates,
            update => update.SheetsCompleted == highRiskSummaryCompletion.SheetsCompleted + 1);
        Assert.True(
            progress.IndexOf(start) < progress.IndexOf(completion),
            "Step4 sheet-start checkpoint must precede its first completion event.");
    }

    [Fact]
    public async Task FinalizedHighRiskVoucher_StreamsOnceAndLateLongTextUsesFullDataWidths()
    {
        const int voucherCount = 757;
        using var host = new HandlerTestHost();
        var longScenarioName = string.Concat(Enumerable.Repeat("晚頁高風險條件", 8));
        var longScenarioRationale = "LATE-RATIONALE-" + new string('R', 72);
        var longVoucherNumber = "ZZZ-VOUCHER-" + new string('9', 70);
        var longPreparer = "ZZZ-" + string.Concat(Enumerable.Repeat("晚頁編製人員", 9));
        var projectId = await SetupContinuationProjectAsync(host, voucherCount);
        await ImportContinuationReferencesAsync(host, voucherCount);
        await host.DispatchAsync(
            "filter.commit",
            ContinuationScenarioPayload(longScenarioName, longScenarioRationale));
        await ExecuteProjectSqlAsync(
            host,
            projectId,
            """
            UPDATE gl
            SET voucher_number = @voucherNumber,
                created_by = @preparer
            WHERE voucher_number = @originalVoucherNumber;
            """,
            ("@voucherNumber", longVoucherNumber),
            ("@preparer", longPreparer),
            ("@originalVoucherNumber", $"PAGE-{voucherCount:000}"));
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        var captured = new CapturingHighRiskVoucherStreamRepository(
            new LocalHighRiskSummaryVoucherPageRepository(database));
        var context = await CurrentContextForAsync(host, projectId);
        var plan = await CurrentPlanForAsync(host, context);
        await using var stream = new MemoryStream();

        var stats = await ((IWorkingPaperPlanWriter)BuildWriter(
                host,
                highRiskSummaryVouchersOverride: captured))
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
                item => item.SheetName == HighRiskVoucherSheet).RowsWritten);

        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var highRiskSummary = workbook.Worksheet(HighRiskSummarySheet);
        Assert.Equal(longScenarioName, highRiskSummary.Cell("C19").GetString());
        Assert.Equal(longScenarioRationale, highRiskSummary.Cell("D19").GetString());
        // step3 的條件與原因欄分別以 60 與 50 為上限並換行：
        // 56 個全形字約 112 字寬，扣掉留白後每行約 50 字寬，換成 3 行，
        // 11 點字每行 16.5 點，所以第 19 列是 49.5 點。數量欄與 step4 的長文字仍依全部資料決定欄寬。
        Assert.InRange(highRiskSummary.Column(3).Width, 60D - 1.5D, 60D + 1.5D);
        Assert.InRange(highRiskSummary.Column(4).Width, 50D - 1.5D, 50D + 1.5D);
        Assert.True(highRiskSummary.Cell("C19").Style.Alignment.WrapText);
        Assert.True(highRiskSummary.Cell("D19").Style.Alignment.WrapText);
        Assert.Equal(49.5D, highRiskSummary.Row(19).Height);
        AssertSafeCountColumn(highRiskSummary, 5);
        AssertSingleLine(highRiskSummary.Cell("E19"));

        var highRiskVoucher = workbook.Worksheet(HighRiskVoucherSheet);
        Assert.Equal("PAGE-001", highRiskVoucher.Cell("B13").GetString());
        Assert.Equal(longVoucherNumber, highRiskVoucher.Cell(12 + voucherCount, 2).GetString());
        Assert.Equal(longPreparer, highRiskVoucher.Cell(12 + voucherCount, 4).GetString());
        Assert.Equal(voucherCount, highRiskVoucher.Cell(12 + voucherCount, 1).GetValue<int>());
        Assert.Equal("Y", highRiskVoucher.Cell("F13").GetString());
        AssertFullDataWidth(highRiskVoucher, 2, longVoucherNumber);
        AssertFullDataWidth(highRiskVoucher, 4, longPreparer);
        AssertSafeAmountColumn(highRiskVoucher, 5);
        AssertSingleLine(highRiskVoucher.Cell(12 + voucherCount, 2));
        AssertSingleLine(highRiskVoucher.Cell(12 + voucherCount, 4));
        AssertSingleLine(highRiskVoucher.Cell(12 + voucherCount, 5));
        AssertSingleLine(highRiskVoucher.Cell(12 + voucherCount, 16));
    }

    [Theory]
    [InlineData(PreparerSummarySheet)]
    [InlineData(HighRiskVoucherSheet)]
    [InlineData(HighRiskVoucherDetailSheet)]
    [InlineData(CalendarInfoSheet)]
    public async Task WriteAsync_CancelDuringCollectionBackedSheet_StopsBeforeSheetCompletion(string targetSheet)
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupContinuationProjectAsync(host, voucherCount: 3);
        await ImportContinuationReferencesAsync(host, voucherCount: 3);
        await host.DispatchAsync("filter.commit", ContinuationScenarioPayload());
        using var cancellation = new CancellationTokenSource();
        var progress = new List<WorkingPaperProgress>();
        await using var stream = new MemoryStream();
        var context = await CurrentContextForAsync(host, projectId);

        void OnProgress(WorkingPaperProgress update)
        {
            progress.Add(update);
            if (update.SheetName == targetSheet && update.RowsWritten == 1)
            {
                cancellation.Cancel();
            }
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WriteWithCurrentPlanAsync(
            host,
            BuildWriter(host, new WorkingPaperWriterOptions(), progressRowInterval: 1),
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
        var projectId = await SetupContinuationProjectAsync(host, voucherCount: 3);
        await ImportContinuationReferencesAsync(host, voucherCount: 3);
        await host.DispatchAsync("filter.commit", ContinuationScenarioPayload());
        using var cancellation = new CancellationTokenSource();
        var progress = new List<WorkingPaperProgress>();
        await using var stream = new MemoryStream();
        var context = await CurrentContextForAsync(host, projectId);
        var plan = await CurrentPlanForAsync(host, context);
        var writer = (IWorkingPaperPlanWriter)BuildWriter(
            host,
            new WorkingPaperWriterOptions(),
            progressRowInterval: 1);

        void OnProgress(WorkingPaperProgress update)
        {
            progress.Add(update);
            if (update.SheetName == HighRiskVoucherSheet && update.RowsWritten == 1)
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
            update.SheetName == HighRiskVoucherSheet && update.RowsWritten == 1);
        Assert.DoesNotContain(progress, update =>
            update.SheetName == HighRiskVoucherSheet
            && update.SheetsCompleted > intermediate.SheetsCompleted);
    }

    [Fact]
    public async Task DataDrivenSheets_ContinueBeforeConfiguredRowLimit_WithoutLossOrJetAddedPhysicalSettings()
    {
        const uint continuationRowLimit = 25;
        using var host = new HandlerTestHost();
        var projectId = await SetupContinuationProjectAsync(host);
        await ImportContinuationReferencesAsync(host);
        await host.DispatchAsync("filter.commit", ContinuationScenarioPayload());

        var completenessDiffs = new FixedCompletenessDiffPageRepository(
            Enumerable.Range(1, 26)
                .Select(index => new CompletenessDiffAccount(
                    AccountNumber: $"D{index:000}",
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
        var capturedHighRiskVoucherDetail = new CapturingHighRiskVoucherDetailPreparedRepository(
            new LocalHighRiskSummaryRowPageRepository(database));
        var writer = (IWorkingPaperPlanWriter)BuildWriter(
            host,
            new WorkingPaperWriterOptions(continuationRowLimit),
            highRiskSummaryRowsOverride: capturedHighRiskVoucherDetail,
            completenessDiffsOverride: completenessDiffs);
        var stats = await writer.WriteAsync(
            stream,
            context,
            plan,
            CancellationToken.None);

        var highRiskVoucherStats = stats.SheetStats.Where(stat => IsSheetSeries(stat.SheetName, HighRiskVoucherSheet)).ToList();
        var highRiskVoucherDetailStats = stats.SheetStats.Where(stat => IsSheetSeries(stat.SheetName, HighRiskVoucherDetailSheet)).ToList();
        Assert.True(highRiskVoucherStats.Count > 1, "縮小列上限後 step4 應建立續頁");
        Assert.True(highRiskVoucherDetailStats.Count > 1, "縮小列上限後 step4-1 應建立續頁");
        Assert.Equal(26L, highRiskVoucherStats.Sum(stat => stat.RowsWritten));
        Assert.Equal(52L, highRiskVoucherDetailStats.Sum(stat => stat.RowsWritten));
        Assert.All(highRiskVoucherStats, stat => Assert.InRange(stat.RowsWritten, 1L, 13L));
        Assert.All(highRiskVoucherDetailStats, stat => Assert.InRange(stat.RowsWritten, 1L, 20L));
        Assert.Equal(highRiskVoucherStats.Count, highRiskVoucherStats.Select(stat => stat.SheetName).Distinct().Count());
        Assert.Equal(highRiskVoucherDetailStats.Count, highRiskVoucherDetailStats.Select(stat => stat.SheetName).Distinct().Count());
        Assert.Equal(0, capturedHighRiskVoucherDetail.TypedPageCalls);
        var preparedMetrics = Assert.IsType<WorkingPaperHighRiskVoucherDetailPreparedSessionMetrics>(
            capturedHighRiskVoucherDetail.Metrics);
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
                "initializeTemporaryTables",
                "materializeHitVouchers",
                "materializeRowTags",
                "orderedRows",
                "cleanup"
            ],
            preparedMetrics.Commands.Select(command => command.Operation));
        Assert.All(highRiskVoucherStats.Concat(highRiskVoucherDetailStats), stat =>
            Assert.InRange(stat.SheetName.Length, 1, ExcelWorksheetConstraints.MaxSheetNameLength));

        var expectedContinuedSeries = new[]
        {
            CompletenessSheet,
            VoucherBalanceSheet,
            PreparerSummarySheet,
            CompletenessDifferenceSheet,
            HighRiskVoucherSheet,
            HighRiskVoucherDetailSheet,
            CalendarInfoSheet,
            AccountMappingSheet
        };
        foreach (var baseName in expectedContinuedSeries)
        {
            Assert.True(stats.SheetStats.Count(stat => IsSheetSeries(stat.SheetName, baseName)) > 1,
                $"{baseName} 的合成資料超過本測試列容量，應建立續頁");
        }
        Assert.Equal(27L, SumSeriesRows(stats, CompletenessSheet));
        Assert.Equal(26L, SumSeriesRows(stats, VoucherBalanceSheet));
        Assert.Equal(26L, SumSeriesRows(stats, PreparerSummarySheet));
        Assert.Equal(26L, SumSeriesRows(stats, CompletenessDifferenceSheet));
        Assert.Equal(36L, SumSeriesRows(stats, CalendarInfoSheet));
        Assert.Equal(27L, SumSeriesRows(stats, AccountMappingSheet));

        stream.Position = 0;
        using (var workbook = new XLWorkbook(stream))
        {
            var highRiskVoucherOrdinals = new List<long>();
            foreach (var stat in highRiskVoucherStats)
            {
                var sheet = workbook.Worksheet(stat.SheetName);
                Assert.Equal("傳票號碼", sheet.Cell("B11").GetString());
                // 第一頁照範本 A12 的整句，續頁由程式寫同一句；這裡整句比對。
                Assert.Equal(
                    "因為設定高風險範圍條件，從母體#2挑選之分錄傳票(執行重大性或其他固定金額不應作為挑選的門檻)"
                    + "；空白傳票號碼的命中分錄逐筆列出，不計傳票張數，金額為該筆借方金額。",
                    sheet.Cell("A12").GetString());
                if (!string.Equals(stat.SheetName, HighRiskVoucherSheet, StringComparison.Ordinal))
                {
                    Assert.True((sheet.LastRowUsed()?.RowNumber() ?? 0) <= continuationRowLimit);
                }
                for (var row = 13; row < 13 + stat.RowsWritten; row++)
                {
                    highRiskVoucherOrdinals.Add(sheet.Cell(row, 1).GetValue<long>());
                }
            }
            Assert.Equal(Enumerable.Range(1, 26).Select(value => (long)value), highRiskVoucherOrdinals);

            var highRiskVoucherDetailKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var stat in highRiskVoucherDetailStats)
            {
                var sheet = workbook.Worksheet(stat.SheetName);
                Assert.Equal("傳票號碼_JE", sheet.Cell("A5").GetString());
                if (!string.Equals(stat.SheetName, HighRiskVoucherDetailSheet, StringComparison.Ordinal))
                {
                    Assert.True((sheet.LastRowUsed()?.RowNumber() ?? 0) <= continuationRowLimit);
                }
                for (var row = 6; row < 6 + stat.RowsWritten; row++)
                {
                    Assert.True(highRiskVoucherDetailKeys.Add($"{sheet.Cell(row, 1).GetString()}\u001f{sheet.Cell(row, 2).GetString()}"),
                        "step4-1 續頁不得重複傳票行");
                }
            }
            Assert.Equal(52, highRiskVoucherDetailKeys.Count);

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
        foreach (var stat in highRiskVoucherStats.Concat(highRiskVoucherDetailStats))
        {
            var sheet = workbookPart.RequiredWorkbook().Sheets!.Elements<Sheet>()
                .Single(item => item.Name?.Value == stat.SheetName);
            var part = Assert.IsType<WorksheetPart>(workbookPart.GetPartById(sheet.Id!.Value!));
            var worksheet = part.RequiredWorksheet();
            Assert.NotNull(worksheet.Elements<Columns>().SingleOrDefault());
            if (!string.Equals(stat.SheetName, HighRiskVoucherSheet, StringComparison.Ordinal)
                && !string.Equals(stat.SheetName, HighRiskVoucherDetailSheet, StringComparison.Ordinal))
            {
                Assert.Empty(worksheet.Elements<SheetViews>());
                Assert.Empty(worksheet.Descendants<Pane>());
                Assert.Empty(worksheet.Elements<PageMargins>());
                Assert.Empty(worksheet.Elements<PageSetup>());
            }
            // 照舊版只保護 step4，step4-1 不保護。
            // step4 的下拉選單照範本的兩組選項。
            // 這一頁的傳票沒有超過範本第 27 列，所以範圍和範本相同。
            if (IsSheetSeries(stat.SheetName, HighRiskVoucherSheet))
            {
                Assert.NotNull(worksheet.Elements<SheetProtection>().SingleOrDefault());
            }
            else
            {
                Assert.Empty(worksheet.Elements<SheetProtection>());
            }

            var validations = worksheet.Descendants<DataValidation>().ToArray();
            if (IsSheetSeries(stat.SheetName, HighRiskVoucherSheet))
            {
                Assert.Equal(
                    new (string?, string?)[] { ("P12:P27 Q13:R27", "\"v,no,n/a\""), ("S12:S27", "\"Yes,No\"") },
                    validations.Select(validation => (
                        validation.SequenceOfReferences?.InnerText,
                        validation.Formula1?.Text)));
            }
            else
            {
                Assert.Empty(validations);
            }

            if (IsSheetSeries(stat.SheetName, HighRiskVoucherSheet))
            {
                if (!string.Equals(stat.SheetName, HighRiskVoucherSheet, StringComparison.Ordinal))
                {
                    AssertCellUnlocked(workbookPart, worksheet, "P13");
                    AssertCellNumberFormat(workbookPart, worksheet, "E13", "#,##0.0000");
                }
            }
            else
            {
                if (!string.Equals(stat.SheetName, HighRiskVoucherDetailSheet, StringComparison.Ordinal))
                {
                    var amountColumn = plan.HighRiskVoucherDetailColumns
                        .Select((column, index) => (column, index))
                        .Single(item =>
                            item.column.ValueSource
                            == WorkingPaperHighRiskVoucherDetailValueSource.SignedAmount)
                        .index + 1;
                    var amountReference = $"{ColumnLetters(amountColumn)}6";
                    AssertCellNumberFormat(
                        workbookPart,
                        worksheet,
                        amountReference,
                        "#,##0.0000");
                }
            }

            var headerRow = IsSheetSeries(stat.SheetName, HighRiskVoucherSheet) ? 11U : 5U;
            var row = worksheet.GetFirstChild<SheetData>()!.Elements<Row>()
                .First(item => item.RowIndex?.Value == headerRow);
            if (!string.Equals(stat.SheetName, HighRiskVoucherSheet, StringComparison.Ordinal)
                && !string.Equals(stat.SheetName, HighRiskVoucherDetailSheet, StringComparison.Ordinal))
            {
                Assert.Equal(42d, row.Height!.Value);
            }
            else
            {
                Assert.NotNull(row.Height);
            }
        }

        // 這個案件沒有配對人工傳票欄位，step1-2 的 C 欄留白，但照舊版和 B12:E 一起鎖定。
        // 這裡確認 C12 鎖定；F 到 H 欄仍可填。
        AssertEditableCells(workbookPart, PreparerSummarySheet, ["F12", "G12", "H12"]);
        var preparerSummary = workbookPart.RequiredWorkbook().Sheets!.Elements<Sheet>()
            .Single(item => item.Name?.Value == PreparerSummarySheet);
        var preparerSummaryWorksheet = Assert.IsType<WorksheetPart>(workbookPart.GetPartById(preparerSummary.Id!.Value!)).RequiredWorksheet();
        Assert.Null(CellFor(preparerSummaryWorksheet, "C12").CellValue);
        Assert.True(ResolveCellFormat(workbookPart, CellFor(preparerSummaryWorksheet, "C12")).Protection?.Locked?.Value ?? true);
        AssertEditableCells(workbookPart, CompletenessDifferenceSheet, ["E17", "F17", "G17"]);
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
        Assert.Equal(HighRiskVoucherSheet, ExcelWorksheetConstraints.ContinuationSheetName(HighRiskVoucherSheet, 1));
    }

}
