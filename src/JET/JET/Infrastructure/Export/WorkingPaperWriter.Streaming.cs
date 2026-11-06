using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;

namespace JET.Infrastructure;

public sealed partial class WorkingPaperWriter
{
    private async Task<long> EmitContinuedRowsAsync<T>(
        List<SheetStat> stats,
        string baseSheetName,
        uint firstDataRow,
        int reservedRowsAfterData,
        IAsyncEnumerable<T> items,
        Func<string, bool, SheetWriter> createPage,
        Func<SheetWriter, uint, T, IReadOnlyList<Cell>> createCells,
        Func<SheetWriter, long, bool, CancellationToken, SheetStat> closePage,
        bool omitWhenEmpty,
        Action<WorkingPaperProgress>? progress,
        CancellationToken cancellationToken)
    {
        var lastDataRow = (long)_continuationRowLimit - reservedRowsAfterData;
        var pageCapacity = lastDataRow - firstDataRow + 1;
        if (pageCapacity <= 0)
        {
            throw new InvalidOperationException("Configured continuation row limit leaves no room for data rows.");
        }

        await using var enumerator = items.GetAsyncEnumerator(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!await enumerator.MoveNextAsync())
        {
            if (!omitWhenEmpty)
            {
                using var emptySheet = createPage(
                    ExcelWorksheetConstraints.ContinuationSheetName(baseSheetName, 1),
                    false);
                AddStat(stats, closePage(emptySheet, 0, true, cancellationToken), progress);
            }

            return 0;
        }

        var pageNumber = 1;
        var pageRows = 0L;
        var totalRows = 0L;
        var sheet = createPage(
            ExcelWorksheetConstraints.ContinuationSheetName(baseSheetName, pageNumber),
            true);
        try
        {
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pageRows == pageCapacity)
                {
                    AddStat(stats, closePage(sheet, pageRows, false, cancellationToken), progress);
                    sheet.Dispose();
                    pageNumber = checked(pageNumber + 1);
                    sheet = createPage(
                        ExcelWorksheetConstraints.ContinuationSheetName(baseSheetName, pageNumber),
                        true);
                    pageRows = 0;
                }

                var row = checked((uint)(firstDataRow + pageRows));
                sheet.WriteRow(row, createCells(sheet, row, enumerator.Current));
                pageRows++;
                totalRows++;
                ReportIntermediateRows(stats, progress, sheet.Name, pageRows);
            } while (await enumerator.MoveNextAsync());

            AddStat(stats, closePage(sheet, pageRows, true, cancellationToken), progress);
            return totalRows;
        }
        finally
        {
            sheet.Dispose();
        }
    }

    private static async IAsyncEnumerable<T> StreamItemsAsync<T>(
        Func<string?, CancellationToken, Task<PageResult<T>>> fetchPage,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await fetchPage(cursor, cancellationToken);
            foreach (var item in page.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }

            cursor = page.NextCursor;
        } while (cursor is not null);
    }

    private static async IAsyncEnumerable<T> ToItems<T>(
        IReadOnlyList<T> items,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }

        await Task.CompletedTask;
    }

    // ---- 列 cell 工廠(各表欄序;手填欄一律 BlankCell)----

    /// <summary>step1 全科目列:B 編號 / C 名稱 / D TB 變動(A) / E GL 彙總(C) / F 差異(B)-(A)。</summary>
}
