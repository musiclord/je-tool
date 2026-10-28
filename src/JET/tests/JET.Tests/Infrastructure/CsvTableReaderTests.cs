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

    // ---- 2026-10-04 第二遍回饋審閱第 1 批（C2）：文字檔讀法 ----

    [Fact]
    public async Task UnquotedFieldWithEmbeddedQuote_IsLiteralAndFollowingRowsStayIntact()
    {
        // 審閱 U02：沒有用引號包住的欄位裡出現一個雙引號（例如英吋符號），以前後面的列會被併成一列。
        // 一般 CSV 讀法（Excel、Python csv）把它當一般字元；資料列數不得減少。
        var path = TestCsvBuilder.WriteFile("doc,desc,amount\nV1,3/4\" pipe,10\nV2,plain,20\nV3,\"quoted, ok\",30\n", TestCsvBuilder.Utf8NoBom);
        try
        {
            var (_, rows) = await ReadAllAsync(path);

            Assert.Equal(3, rows.Count);
            Assert.Equal("3/4\" pipe", rows[0].Values["desc"]);
            Assert.Equal("plain", rows[1].Values["desc"]);
            Assert.Equal("quoted, ok", rows[2].Values["desc"]);
            Assert.Equal("30", rows[2].Values["amount"]);
        }
        finally
        {
            TestCsvBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task QuotedFieldFollowedByText_KeepsTrailingTextLikeExcel()
    {
        var path = TestCsvBuilder.WriteFile("a,b\n1,\"x\"y\n", TestCsvBuilder.Utf8NoBom);
        try
        {
            var (_, rows) = await ReadAllAsync(path);
            Assert.Equal("xy", Assert.Single(rows).Values["b"]);
        }
        finally
        {
            TestCsvBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task UnterminatedQuote_FailsAndNamesTheRow()
    {
        // 引號沒有成對、結構讀不通：明確失敗並寫出第幾列，不默默併列。
        var path = TestCsvBuilder.WriteFile("a,b\n1,ok\n2,\"open\n3,z\n", TestCsvBuilder.Utf8NoBom);
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
            Assert.Contains("第 3 列", ex.Message, StringComparison.Ordinal);
            Assert.Contains("引號", ex.Message, StringComparison.Ordinal);
            Assert.Contains(Path.GetFileName(path), ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            TestCsvBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task LeadingBlankLines_AreSkippedBeforeHeader_AndRowNumbersCountThem()
    {
        // 審閱 U15：第一行是空白行時以前找不到標頭；改成和 Excel 檔一樣略過開頭空白行。列號仍照檔案實際列數。
        var path = TestCsvBuilder.WriteFile("\n   \n傳票號碼,金額\nV1,10\n", TestCsvBuilder.Utf8NoBom);
        try
        {
            var (columns, rows) = await ReadAllAsync(path);

            Assert.Equal(["傳票號碼", "金額"], columns);
            var row = Assert.Single(rows);
            Assert.Equal(4, row.SourceRowNumber);
            Assert.Equal("V1", row.Values["傳票號碼"]);

            var inspection = await new CsvTableReader().InspectAsync(path, CancellationToken.None);
            Assert.Equal(["傳票號碼", "金額"], inspection.Columns);
        }
        finally
        {
            TestCsvBuilder.Delete(path);
        }
    }

    // 預篩選收斂計畫階段六：使用者 2026-10-22 裁定「欄名在第幾列」開放給 GL 與 TB 的一般匯入，含文字檔。
    // 文字檔和 Excel 讀取器同一規則：先略過指定的列數（以記錄計，空白列也算），再取第一個有內容的列當欄名；列號照檔案實際列數。
    // 第一次失敗：Focused 收據 20261023-193734239-f6249c3be1e046cfa0c55b4181d2cabd（略過列數被忽略，標題列被當成欄名）。
    [Fact]
    public async Task HeaderRowSkip_SkipsTitleRows_ThenFirstContentRowIsHeader_AndRowNumbersCountThem()
    {
        var path = TestCsvBuilder.WriteFile("合成公司 總帳明細\n\n傳票號碼,金額\nV1,10\n,20\n", TestCsvBuilder.Utf8NoBom);
        try
        {
            var reader = new CsvTableReader();
            foreach (var skip in new[] { 1, 2 })
            {
                // 略過 1 列後第 2 列是空白列，仍往下找到第 3 列當欄名；略過 2 列直接落在欄名列。
                var request = new TabularSourceRequest(path, LeadingRowsToSkip: skip);
                Assert.Equal(["傳票號碼", "金額"], await reader.ReadColumnsAsync(request, CancellationToken.None));

                var rows = new List<StagingRow>();
                await foreach (var row in reader.ReadRowsAsync(request, CancellationToken.None))
                {
                    rows.Add(row);
                }

                Assert.Equal([4, 5], rows.Select(row => row.SourceRowNumber).ToArray());
                Assert.Equal("V1", rows[0].Values["傳票號碼"]);
                Assert.False(rows[1].Values.ContainsKey("傳票號碼"));
                Assert.Equal("20", rows[1].Values["金額"]);
            }

            // 略過到檔尾就是找不到欄名列，和整檔空白同一個錯誤代碼。
            var tooFar = await Assert.ThrowsAsync<JetActionException>(() =>
                reader.ReadColumnsAsync(new TabularSourceRequest(path, LeadingRowsToSkip: 5), CancellationToken.None));
            Assert.Equal(JetErrorCodes.EmptyWorkbook, tooFar.Code);
            // 2026-10-28 複審新增的斷言：訊息寫那一列起沒有內容，不說成檔案找不到標頭列。改程式之後才補，沒有第一次失敗的收據。
            Assert.Equal($"檔案「{Path.GetFileName(path)}」從第 6 列起沒有內容，找不到欄名。請確認欄名所在的列。", tooFar.Message);
        }
        finally
        {
            TestCsvBuilder.Delete(path);
        }
    }

    // 低嚴重度事項 A3（使用者 2026-10-19 要求處理）：有 BOM 的 UTF-8 與 UTF-16 檔，StreamReader 認得 BOM 後
    // 換成 .NET 預設的編碼，壞位元組被默默換成替代字元匯入。改成嚴格編碼後，和沒有 BOM 的 UTF-8 一樣以讀檔錯誤回報。
    public static TheoryData<string, byte[]> BomFilesWithBadBytes() => new()
    {
        { "utf-8 bom, 0xFF", [0xEF, 0xBB, 0xBF, .. "a,b\n1,"u8, 0xFF, .. ",2\n"u8] },
        { "utf-16 le bom, lone high surrogate", [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("a,b\n1,"), 0x00, 0xD8, .. Encoding.Unicode.GetBytes("x\n")] },
        { "utf-16 be bom, lone high surrogate", [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("a,b\n1,"), 0xD8, 0x00, .. Encoding.BigEndianUnicode.GetBytes("x\n")] }
    };

    [Theory]
    [MemberData(nameof(BomFilesWithBadBytes))]
    public async Task BomFile_WithUndecodableBytes_ThrowsFileReadError(string label, byte[] bytes)
    {
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

            Assert.True(ex.Code == JetErrorCodes.FileReadError, label + ": " + ex.Code);
            // 使用者 2026-10-19 要求前端不露出系統用詞：訊息裡的編碼和匯入清單的選項一樣寫 UTF-8、Big5、UTF-16。
            Assert.Contains("改選編碼（UTF-8、Big5 或 UTF-16）", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("utf-", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            TestCsvBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task Utf16Override_OnBigEndianFile_ReadsByTheBom()
    {
        // 指定「utf-16」時照 BOM 選大端或小端；以前一律用小端開檔，讀到大端的 BOM 才換成不嚴格的編碼。
        var path = TestCsvBuilder.WriteFile(ChineseContent, new UnicodeEncoding(bigEndian: true, byteOrderMark: true));
        try
        {
            var reader = new CsvTableReader();
            var request = new TabularSourceRequest(path, EncodingName: "utf-16");
            var columns = await reader.ReadColumnsAsync(request, CancellationToken.None);

            Assert.Equal(["傳票號碼", "總帳日期", "金額", "摘要"], columns);
            var rows = new List<StagingRow>();
            await foreach (var row in reader.ReadRowsAsync(request, CancellationToken.None))
            {
                rows.Add(row);
            }

            Assert.Equal(2, rows.Count);
            Assert.Equal("期初轉入", rows[0].Values["摘要"]);
        }
        finally
        {
            TestCsvBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task LeadingWhitespaceLine_TabFileStillSplitsIntoColumns()
    {
        // 低嚴重度事項 A1（使用者 2026-10-19 要求處理）：第一行只有空白字元時，分隔符偵測以前把它當成標頭，
        // 改用預設的逗號，Tab 檔整個讀成一欄、無法配對。
        var path = TestCsvBuilder.WriteFile("   \r\n傳票號碼\t金額\r\nV1\t10\r\n", TestCsvBuilder.Utf8NoBom);
        try
        {
            var (columns, rows) = await ReadAllAsync(path);

            Assert.Equal(["傳票號碼", "金額"], columns);
            var row = Assert.Single(rows);
            Assert.Equal("V1", row.Values["傳票號碼"]);
            Assert.Equal("10", row.Values["金額"]);

            var inspection = await new CsvTableReader().InspectAsync(path, CancellationToken.None);
            Assert.Equal(["傳票號碼", "金額"], inspection.Columns);
        }
        finally
        {
            TestCsvBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task ExtraCellsBeyondHeader_BecomePlaceholderColumns_LikeXlsx()
    {
        // guide 3.1.5 的規則：有資料的欄絕不靜默丟棄，標頭範圍外的資料合成 COL_n 佔位欄；缺欄視為空。
        var path = TestCsvBuilder.WriteFile("a,b\n1,2,3\n4\n", TestCsvBuilder.Utf8NoBom);
        try
        {
            var (_, rows) = await ReadAllAsync(path);

            Assert.Equal(2, rows.Count);
            Assert.Equal("3", rows[0].Values["COL_3"]);
            Assert.Equal("4", rows[1].Values["a"]);
            Assert.False(rows[1].Values.ContainsKey("b"));
        }
        finally
        {
            TestCsvBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task InspectAsync_SingleColumn_ReportsPossibleReportLayout()
    {
        // 審閱 U54：報表格式的文字檔（上方有公司名稱或標題列）以前匯成單一欄而不提示。
        var path = TestCsvBuilder.WriteFile("合成公司 總帳明細表\n2025/01/01 V001 1000\n2025/01/02 V002 2000\n", TestCsvBuilder.Utf8NoBom);
        try
        {
            var inspection = await new CsvTableReader().InspectAsync(path, CancellationToken.None);

            Assert.Single(inspection.Columns!);
            var notice = Assert.Single(inspection.Notices ?? []);
            Assert.Contains("報表", notice, StringComparison.Ordinal);
            Assert.Contains("Excel", notice, StringComparison.Ordinal);
            // 預篩選收斂計畫階段六：待匯入清單有了「欄名在第幾列」，標題列在欄名前面時提醒要指到那一格，不再只叫審計員去 Excel 整理。
            // 第一次失敗收據 20261024-035009182-d65b0b7be7854c92a2263a9c18ab810f。
            Assert.Contains("欄名在第幾列", notice, StringComparison.Ordinal);
        }
        finally
        {
            TestCsvBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task InspectAsync_MultipleColumns_HasNoNotice()
    {
        var path = TestCsvBuilder.WriteFile(ChineseContent, TestCsvBuilder.Utf8NoBom);
        try
        {
            var inspection = await new CsvTableReader().InspectAsync(path, CancellationToken.None);
            Assert.True(inspection.Notices is null || inspection.Notices.Count == 0);
        }
        finally
        {
            TestCsvBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task FileOpenedByAnotherProgram_MessageAsksToCloseIt_WithoutPath()
    {
        // 審閱 U14：檔案被 Excel 開著時以前顯示英文例外與完整路徑。
        var path = TestCsvBuilder.WriteFile(ChineseContent, TestCsvBuilder.Utf8NoBom);
        using var locked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var reader = new CsvTableReader();
            var ex = await Assert.ThrowsAsync<JetActionException>(
                () => reader.InspectAsync(path, CancellationToken.None));

            Assert.Equal(JetErrorCodes.FileReadError, ex.Code);
            Assert.Contains(Path.GetFileName(path), ex.Message, StringComparison.Ordinal);
            Assert.Contains("關閉", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Path.GetDirectoryName(path)!, ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("process", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            locked.Dispose();
            TestCsvBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task DecodeFailure_MessageIsPlainChinese_AndPointsToEncodingChoice()
    {
        // 審閱 U13、U52：解碼失敗以前把 .NET 英文例外文字放進訊息，並要審計員「改以匯入參數指定編碼」。
        var path = TestCsvBuilder.WriteFile(ChineseContent, TestCsvBuilder.Big5);
        try
        {
            var reader = new CsvTableReader();
            var ex = await Assert.ThrowsAsync<JetActionException>(
                () => reader.ReadColumnsAsync(new TabularSourceRequest(path, EncodingName: "utf-8"), CancellationToken.None));

            Assert.Equal(JetErrorCodes.FileReadError, ex.Code);
            // 使用者 2026-10-19 要求前端不露出系統用詞：編碼改寫成審計員常見的 UTF-8，不再顯示程式內部的小寫代號。
            // 首次失敗：20261019-111133190-cc5d8f23cf204a278811de85e6b1e15d；仍檢查訊息說出讀取時用的編碼。
            Assert.Contains("UTF-8", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("utf-8", ex.Message, StringComparison.Ordinal);
            Assert.Contains("改選編碼", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Unable", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("translate", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("匯入參數", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            TestCsvBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task QuoteInsideUnquotedField_DoesNotConfuseDelimiterDetection()
    {
        // 分隔符偵測也要用同一套引號規則，否則一個英吋符號會讓後面的列都被當成引號內。
        var text = "doc;desc;amount\nV1;3/4\" pipe;10\nV2;x;20\nV3;y;30\nV4;z;40\n";
        var path = TestCsvBuilder.WriteFile(text, TestCsvBuilder.Utf8NoBom);
        try
        {
            var inspection = await new CsvTableReader().InspectAsync(path, CancellationToken.None);
            Assert.Equal(";", inspection.Delimiter);
            var (_, rows) = await ReadAllAsync(path);
            Assert.Equal(4, rows.Count);
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
