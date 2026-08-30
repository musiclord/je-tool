using System.Text;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// CsvTableReader 測試（guide §3.1.1）。
/// oracle：手寫 bytes fixture（編碼與內容皆可人工驗證）。
/// 設計技術：決策表——編碼來源（BOM UTF-8 / BOM UTF-16 / 無 BOM UTF-8 / 無 BOM Big5）×
/// 引號形態（含分隔符 / 含換行 / "" 跳脫）；負向含不可解碼 bytes 與空檔。
/// </summary>
public sealed class CsvTableReaderTests
{
    private const string ChineseContent =
        "傳票號碼,總帳日期,金額,摘要\n" +
        "V001,2025-01-31,\"1,234.50\",期初轉入\n" +
        "V002,2025-02-28,99,\"含逗號, 與「引號」的\"\"摘要\"\"\"\n";

    [Fact]
    public async Task Utf8WithBom_ReadsNormalizedColumnsAndRows()
    {
        var path = TestCsvBuilder.WriteFile(ChineseContent, TestCsvBuilder.Utf8Bom);
        try
        {
            var (columns, rows) = await ReadAllAsync(path);

            Assert.Equal(["傳票號碼", "總帳日期", "金額", "摘要"], columns);
            Assert.Equal(2, rows.Count);

            // 列號語意：標頭 = 1，資料列由 2 起算
            Assert.Equal(2, rows[0].SourceRowNumber);
            Assert.Equal("V001", rows[0].Values["傳票號碼"]);
            // RFC 4180：引號內的千分位逗號不切欄
            Assert.Equal("1,234.50", rows[0].Values["金額"]);

            Assert.Equal(3, rows[1].SourceRowNumber);
            // RFC 4180：引號內逗號保留、"" 跳脫為 "
            Assert.Equal("含逗號, 與「引號」的\"摘要\"", rows[1].Values["摘要"]);
        }
        finally
        {
            TestCsvBuilder.Delete(path);
        }
    }

    [Theory]
    [InlineData(".csv")]
    [InlineData(".txt")]
    public async Task Utf8NoBom_DetectionChainPicksUtf8_ForBothExtensions(string extension)
    {
        var path = TestCsvBuilder.WriteFile(ChineseContent, TestCsvBuilder.Utf8NoBom, extension);
        try
        {
            var (columns, rows) = await ReadAllAsync(path);

            Assert.Equal(["傳票號碼", "總帳日期", "金額", "摘要"], columns);
            Assert.Equal("期初轉入", rows[0].Values["摘要"]);
        }
        finally
        {
            TestCsvBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task Big5NoBom_DetectionChainFallsBackToBig5()
    {
        // Big5 編碼的中文標頭：嚴格 UTF-8 驗證必失敗 → 鏈落到 Big5，欄名須逐字還原
        var path = TestCsvBuilder.WriteFile(ChineseContent, TestCsvBuilder.Big5);
        try
        {
            var (columns, rows) = await ReadAllAsync(path);

            Assert.Equal(["傳票號碼", "總帳日期", "金額", "摘要"], columns);
            Assert.Equal("期初轉入", rows[0].Values["摘要"]);
        }
        finally
        {
            TestCsvBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task Utf16WithBom_DetectedByBom()
    {
        var path = TestCsvBuilder.WriteFile(ChineseContent, Encoding.Unicode);
        try
        {
            var (columns, _) = await ReadAllAsync(path);

            Assert.Equal(["傳票號碼", "總帳日期", "金額", "摘要"], columns);
        }
        finally
        {
            TestCsvBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task QuotedNewline_StaysInOneLogicalRow()
    {
        var path = TestCsvBuilder.WriteFile("a,b\n1,\"x\ny\"\n2,z\n", TestCsvBuilder.Utf8NoBom);
        try
        {
            var (_, rows) = await ReadAllAsync(path);

            Assert.Equal(2, rows.Count);
            Assert.Equal("x\ny", rows[0].Values["b"]);
            Assert.Equal("z", rows[1].Values["b"]);
        }
        finally
        {
            TestCsvBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task DelimiterOverride_BeatsDetection()
    {
        // 偵測會選一致的逗號（每列 1 個）；override 分號後欄名變成整段含逗號的字串
        var path = TestCsvBuilder.WriteFile("a,b;c\n1,2;3\n", TestCsvBuilder.Utf8NoBom);
        try
        {
            var reader = new CsvTableReader();
            var columns = await reader.ReadColumnsAsync(
                new TabularSourceRequest(path, Delimiter: ';'), CancellationToken.None);

            Assert.Equal(["a,b", "c"], columns);
        }
        finally
        {
            TestCsvBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task EncodingOverrideUtf8_OnBig5Bytes_ThrowsFileReadError()
    {
        var path = TestCsvBuilder.WriteFile(ChineseContent, TestCsvBuilder.Big5);
        try
        {
            var reader = new CsvTableReader();

            var ex = await Assert.ThrowsAsync<JetActionException>(
                () => reader.ReadColumnsAsync(
                    new TabularSourceRequest(path, EncodingName: "utf-8"), CancellationToken.None));

            Assert.Equal(JetErrorCodes.FileReadError, ex.Code);
        }
        finally
        {
            TestCsvBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task UndecodableBytes_ThrowFileReadError()
    {
        // 0x81 0x30：UTF-8 非法（孤立 continuation），Big5 非法尾位元組（0x30 不在 0x40–0x7E / 0xA1–0xFE）
        var bytes = "a,b\n"u8.ToArray().Concat(new byte[] { 0x81, 0x30, 0x2C, 0x31 }).ToArray();
        var path = TestCsvBuilder.WriteBytes(bytes);
        try
        {
            var reader = new CsvTableReader();

            var ex = await Assert.ThrowsAsync<JetActionException>(async () =>
            {
                await foreach (var _ in reader.ReadRowsAsync(new TabularSourceRequest(path), CancellationToken.None))
                {
                }
            });

            Assert.Equal(JetErrorCodes.FileReadError, ex.Code);
        }
        finally
        {
            TestCsvBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task EmptyFile_ThrowsEmptyWorkbook()
    {
        var path = TestCsvBuilder.WriteFile("", TestCsvBuilder.Utf8NoBom);
        try
        {
            var reader = new CsvTableReader();

            var ex = await Assert.ThrowsAsync<JetActionException>(
                () => reader.ReadColumnsAsync(new TabularSourceRequest(path), CancellationToken.None));

            Assert.Equal(JetErrorCodes.EmptyWorkbook, ex.Code);
        }
        finally
        {
            TestCsvBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task HeaderOnlyFile_ReturnsColumnsAndZeroRows()
    {
        var path = TestCsvBuilder.WriteFile("傳票號碼,金額\n", TestCsvBuilder.Utf8NoBom);
        try
        {
            var (columns, rows) = await ReadAllAsync(path);

            Assert.Equal(["傳票號碼", "金額"], columns);
            Assert.Empty(rows);
        }
        finally
        {
            TestCsvBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task DuplicateAndBlankHeaders_NormalizedSameAsXlsx()
    {
        // 標頭正規化與 xlsx 共用 TabularHeaderNormalizer（guide §3.1.1）
        var path = TestCsvBuilder.WriteFile("金額,,金額\n1,2,3\n", TestCsvBuilder.Utf8NoBom);
        try
        {
            var (columns, rows) = await ReadAllAsync(path);

            Assert.Equal(["金額", "COL_2", "金額_2"], columns);
            Assert.Equal("3", rows[0].Values["金額_2"]);
        }
        finally
        {
            TestCsvBuilder.Delete(path);
        }
    }

    [Fact]
    public void Supports_CsvAndTxtOnly()
    {
        var reader = new CsvTableReader();

        Assert.True(reader.Supports(@"C:\data\JE.csv"));
        Assert.True(reader.Supports(@"C:\data\JE.TXT"));
        Assert.False(reader.Supports(@"C:\data\journal-source.xlsx"));
    }


    [Fact]
    public async Task InspectAsync_WhitespaceOnlyFile_ThrowsEmptyWorkbook()
    {
        var path = TestCsvBuilder.WriteFile("   \r\n\t", TestCsvBuilder.Utf8NoBom);
        try
        {
            var reader = new CsvTableReader();

            var ex = await Assert.ThrowsAsync<JetActionException>(
                () => reader.InspectAsync(path, CancellationToken.None));

            Assert.Equal(JetErrorCodes.EmptyWorkbook, ex.Code);
            Assert.Contains(Path.GetFileName(path), ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            TestCsvBuilder.Delete(path);
        }
    }


    [Fact]
    public async Task ReadRowsAsync_AllEmptyDataRows_SkipsRowsAndPreservesSourceRowNumber()
    {
        var path = TestCsvBuilder.WriteFile("a,b\n,\n  , \n1,2\n", TestCsvBuilder.Utf8NoBom);
        try
        {
            var reader = new CsvTableReader();
            var rows = new List<StagingRow>();

            await foreach (var row in reader.ReadRowsAsync(new TabularSourceRequest(path), CancellationToken.None))
            {
                rows.Add(row);
            }

            var singleRow = Assert.Single(rows);
            Assert.Equal(4, singleRow.SourceRowNumber);
            Assert.Equal("1", singleRow.Values["a"]);
            Assert.Equal("2", singleRow.Values["b"]);
        }
        finally
        {
            TestCsvBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task ReadRowsAsync_DecoderFallbackAfterSample_ThrowsFileReadError()
    {
        var validRows = string.Concat(Enumerable.Repeat("1,2\n", 9000));
        var prefix = Encoding.ASCII.GetBytes("a,b\n" + validRows);
        var bytes = prefix.Concat(new byte[] { 0xFF, 0x0A }).ToArray();
        var path = TestCsvBuilder.WriteBytes(bytes);
        try
        {
            var reader = new CsvTableReader();
            var request = new TabularSourceRequest(path, EncodingName: "utf-8");

            var ex = await Assert.ThrowsAsync<JetActionException>(async () =>
            {
                await foreach (var _ in reader.ReadRowsAsync(request, CancellationToken.None))
                {
                }
            });

            Assert.Equal(JetErrorCodes.FileReadError, ex.Code);
            Assert.Contains(Path.GetFileName(path), ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            TestCsvBuilder.Delete(path);
        }
    }

    private static async Task<(IReadOnlyList<string> Columns, List<StagingRow> Rows)> ReadAllAsync(string path)
    {
        var reader = new CsvTableReader();
        var request = new TabularSourceRequest(path);

        var columns = await reader.ReadColumnsAsync(request, CancellationToken.None);

        var rows = new List<StagingRow>();
        await foreach (var row in reader.ReadRowsAsync(request, CancellationToken.None))
        {
            rows.Add(row);
        }

        return (columns, rows);
    }
}
