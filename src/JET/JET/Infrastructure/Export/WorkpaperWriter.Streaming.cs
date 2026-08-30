using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;

namespace JET.Infrastructure;

public sealed partial class WorkpaperWriter
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
        Action<WorkpaperProgress>? progress,
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

    /// <summary>
    /// A1-A4 共同表頭(五張 step1 表逐字相同):公司名 / 測試期間 / 財報準備開始日 / 斜體說明。
    /// A3 對應 ideascript 的 LastPeriod，只取 <see cref="WorkpaperContext.LastPeriodStart"/>；缺值時明示 N/A，不得改用 PeriodEnd。
    /// </summary>
    private static async Task<long> EmitTableSheetAsync(
        SheetWriter sheet, uint headerRow, IReadOnlyList<Cell> headerCells, uint firstDataRow,
        IAsyncEnumerable<Func<uint, IReadOnlyList<Cell>>> rowFactories, CancellationToken cancellationToken,
        Action<long>? reportRows,
        bool writeHeader = true)
    {
        if (writeHeader && headerCells.Count > 0)
        {
            sheet.WriteFixedRow(headerRow, headerCells);
        }

        long count = 0;
        var rowIndex = firstDataRow;
        await foreach (var factory in rowFactories.WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            sheet.WriteRow(rowIndex, factory(rowIndex));
            rowIndex++;
            count++;
            reportRows?.Invoke(count);
        }

        return count;
    }

    /// <summary>keyset 分頁 repo → 列工廠序列(逐頁取、不全載入,直到 nextCursor==null)。</summary>
    private static async IAsyncEnumerable<Func<uint, IReadOnlyList<Cell>>> StreamRowsAsync<T>(
        Func<string?, CancellationToken, Task<PageResult<T>>> fetchPage,
        Func<T, Func<uint, IReadOnlyList<Cell>>> rowFactory,
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
                yield return rowFactory(item);
            }

            cursor = page.NextCursor;
        } while (cursor is not null);
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

    private static async IAsyncEnumerable<T> StreamItemsFromAsync<T>(
        PageResult<T> firstPage,
        Func<string?, Task<PageResult<T>>> fetchNext,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var item in firstPage.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }

        var cursor = firstPage.NextCursor;
        while (cursor is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await fetchNext(cursor);
            foreach (var item in page.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }

            cursor = page.NextCursor;
        }
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
