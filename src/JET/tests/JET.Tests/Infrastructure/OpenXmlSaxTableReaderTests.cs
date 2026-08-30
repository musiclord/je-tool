using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// OpenXML SAX 串流讀取器（guide §3.1.5）。
/// 前五個測試自 ClosedXmlTableReaderTests 原樣移植（行為契約不變；fixture 由 ClosedXML
/// 寫出，兼作讀寫互通檢查）；其後的 raw-zip 案例覆蓋 ClosedXML writer 寫不出的形狀。
/// oracle：§3.1.5 規格手算（cell 型別表、數值 double→decimal 正規化、日期樣式判定）。
/// </summary>
public sealed class OpenXmlSaxTableReaderTests
{
    private static async Task<List<StagingRow>> ReadAllRowsAsync(string path, string? sheetName = null)
    {
        var reader = new OpenXmlSaxTableReader();
        var rows = new List<StagingRow>();
        await foreach (var row in reader.ReadRowsAsync(
            new TabularSourceRequest(path, SheetName: sheetName), CancellationToken.None))
        {
            rows.Add(row);
        }

        return rows;
    }

    // ---- 自 ClosedXmlTableReaderTests 移植（golden：行為契約跨讀取器不變）----

    [Fact]
    public async Task ReadsSparseAndTypedCellsWithNormalizedHeaders()
    {
        var path = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            // 標頭：日期、金額、(空白)、金額(重複)、" 摘要 "(待 trim)
            ws.Cell(1, 1).Value = "日期";
            ws.Cell(1, 2).Value = "金額";
            ws.Cell(1, 3).Value = "";
            ws.Cell(1, 4).Value = "金額";
            ws.Cell(1, 5).Value = " 摘要 ";

            // 列 2：日期 cell、數字 cell、文字金額 cell；第 3 欄缺 cell（稀疏）
            ws.Cell(2, 1).Value = new DateTime(2024, 1, 1);
            ws.Cell(2, 2).Value = 45292;
            ws.Cell(2, 4).Value = "100.50";
            ws.Cell(2, 5).Value = "  期初轉入  ";

            // 列 3：全空（會被跳過）；列 4 有值
            ws.Cell(4, 2).Value = 12.345;
        });

        try
        {
            var reader = new OpenXmlSaxTableReader();

            var columns = await reader.ReadColumnsAsync(new TabularSourceRequest(path), CancellationToken.None);
            Assert.Equal(["日期", "金額", "COL_3", "金額_2", "摘要"], columns);

            var rows = await ReadAllRowsAsync(path);
            Assert.Equal(2, rows.Count);

            var first = rows[0];
            Assert.Equal(2, first.SourceRowNumber);
            Assert.Equal("2024-01-01", first.Values["日期"]);
            Assert.Equal("45292", first.Values["金額"]);
            Assert.Equal("100.50", first.Values["金額_2"]);
            Assert.Equal("期初轉入", first.Values["摘要"]);
            Assert.False(first.Values.ContainsKey("COL_3")); // 缺 cell → key 不存在

            var second = rows[1];
            Assert.Equal(4, second.SourceRowNumber);
            Assert.Equal("12.345", second.Values["金額"]);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task EmptySheet_ThrowsEmptyWorkbook()
    {
        var path = TestWorkbookBuilder.WriteWorkbook(_ => { });

        try
        {
            var reader = new OpenXmlSaxTableReader();

            var ex = await Assert.ThrowsAsync<JetActionException>(
                () => reader.ReadColumnsAsync(new TabularSourceRequest(path), CancellationToken.None));

            Assert.Equal(JetErrorCodes.EmptyWorkbook, ex.Code);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public void Supports_OnlyXlsx()
    {
        var reader = new OpenXmlSaxTableReader();

        Assert.True(reader.Supports(@"C:\data\journal-source.xlsx"));
        Assert.True(reader.Supports(@"C:\data\JE.XLSX"));
        Assert.False(reader.Supports(@"C:\data\JE.csv"));
        Assert.False(reader.Supports(@"C:\data\JE.xls"));
    }

    [Fact]
    public async Task SheetName_SelectsNamedWorksheet()
    {
        // 第一個工作表是 Q1；request.SheetName 指到 Q2 必須讀 Q2 的標頭與資料
        var path = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Name = "Q1";
            ws.Cell(1, 1).Value = "A";
            ws.Cell(2, 1).Value = "q1-row";

            var q2 = ws.Workbook.Worksheets.Add("Q2");
            q2.Cell(1, 1).Value = "B";
            q2.Cell(2, 1).Value = "q2-row";
        });

        try
        {
            var reader = new OpenXmlSaxTableReader();

            var columns = await reader.ReadColumnsAsync(
                new TabularSourceRequest(path, SheetName: "Q2"), CancellationToken.None);
            Assert.Equal(["B"], columns);

            var rows = await ReadAllRowsAsync(path, sheetName: "Q2");
            Assert.Single(rows);
            Assert.Equal("q2-row", rows[0].Values["B"]);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task SheetName_NotFound_ThrowsSheetNotFound()
    {
        var path = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 1).Value = "A";
        });

        try
        {
            var reader = new OpenXmlSaxTableReader();

            var ex = await Assert.ThrowsAsync<JetActionException>(
                () => reader.ReadColumnsAsync(
                    new TabularSourceRequest(path, SheetName: "不存在"), CancellationToken.None));

            Assert.Equal(JetErrorCodes.SheetNotFound, ex.Code);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    // ---- raw-zip 案例（ClosedXML writer 寫不出的真實世界形狀）----

    [Fact]
    public async Task SharedString_PhoneticRunsExcluded()
    {
        // 真實 PBC 檔形狀：<si><t>科目名稱</t><phoneticPr/></si> 與 rPh 注音 run。
        // 注音文字（カモク）不得混入欄名或值（guide §3.1.5）
        var path = new RawXlsxBuilder()
            .WithSharedStrings(
                """
                <si><t>科目名稱</t><phoneticPr fontId="2" type="noConversion"/></si>
                <si><r><t>科目</t></r><r><t>代號</t></r><rPh sb="0" eb="2"><t>カモク</t></rPh><phoneticPr fontId="2"/></si>
                <si><t>現金</t></si>
                """)
            .AddSheet("Sheet1",
                """
                <sheetData>
                <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c></row>
                <row r="2"><c r="A2" t="s"><v>2</v></c><c r="B2"><v>99</v></c></row>
                </sheetData>
                """)
            .Save();

        try
        {
            var reader = new OpenXmlSaxTableReader();
            var columns = await reader.ReadColumnsAsync(new TabularSourceRequest(path), CancellationToken.None);
            Assert.Equal(["科目名稱", "科目代號"], columns);

            var rows = await ReadAllRowsAsync(path);
            Assert.Equal("現金", rows[0].Values["科目名稱"]);
            Assert.Equal("99", rows[0].Values["科目代號"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RowsAndCellsWithoutReferences_UseRunningCounters()
    {
        // OOXML 允許省略 row 的 r 與 cell 的 r 屬性：以連續計數遞補
        var path = new RawXlsxBuilder()
            .AddSheet("Sheet1",
                """
                <sheetData>
                <row><c t="inlineStr"><is><t>甲</t></is></c><c t="inlineStr"><is><t>乙</t></is></c></row>
                <row><c><v>1</v></c><c><v>2</v></c></row>
                <row><c><v>3</v></c></row>
                </sheetData>
                """)
            .Save();

        try
        {
            var rows = await ReadAllRowsAsync(path);

            Assert.Equal(2, rows.Count);
            Assert.Equal(2, rows[0].SourceRowNumber);
            Assert.Equal("1", rows[0].Values["甲"]);
            Assert.Equal("2", rows[0].Values["乙"]);
            Assert.Equal(3, rows[1].SourceRowNumber);
            Assert.Equal("3", rows[1].Values["甲"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task NoDimension_DataBeyondHeaderExtent_LazyPlaceholderKeepsValue()
    {
        // 無 <dimension> 且資料列在標頭範圍外（C 欄）有值：lazy 合成 COL_3，值不得靜默丟棄
        var path = new RawXlsxBuilder()
            .AddSheet("Sheet1",
                """
                <sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>doc</t></is></c><c r="B1" t="inlineStr"><is><t>amt</t></is></c></row>
                <row r="2"><c r="A2"><v>1</v></c><c r="B2"><v>100</v></c><c r="C2" t="inlineStr"><is><t>孤兒值</t></is></c></row>
                </sheetData>
                """)
            .Save();

        try
        {
            var rows = await ReadAllRowsAsync(path);

            Assert.Single(rows);
            Assert.Equal("孤兒值", rows[0].Values["COL_3"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CellTypes_NormalizeLikeSpec()
    {
        // 型別表（guide §3.1.5）：str=公式字串取快取值、b=true/false、e=錯誤原文、
        // inlineStr、無型別=數值
        var path = new RawXlsxBuilder()
            .AddSheet("Sheet1",
                """
                <sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>f</t></is></c><c r="B1" t="inlineStr"><is><t>b</t></is></c><c r="C1" t="inlineStr"><is><t>e</t></is></c><c r="D1" t="inlineStr"><is><t>n</t></is></c></row>
                <row r="2"><c r="A2" t="str"><f>CONCAT("a","b")</f><v>ab</v></c><c r="B2" t="b"><v>1</v></c><c r="C2" t="e"><v>#DIV/0!</v></c><c r="D2"><v>12.5</v></c></row>
                <row r="3"><c r="B3" t="b"><v>0</v></c></row>
                </sheetData>
                """)
            .Save();

        try
        {
            var rows = await ReadAllRowsAsync(path);

            Assert.Equal("ab", rows[0].Values["f"]);
            Assert.Equal("true", rows[0].Values["b"]);
            Assert.Equal("#DIV/0!", rows[0].Values["e"]);
            Assert.Equal("12.5", rows[0].Values["n"]);
            Assert.Equal("false", rows[1].Values["b"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task NumericNormalization_DoubleToDecimalAbsorbsFloatArtifacts()
    {
        // 數值一致性（guide §3.1.5）：必走 double→(decimal)→InvariantCulture。
        // 真實 PBC 檔實測值 535.04999999999995 必須輸出 "535.05"；
        // 科學記號原文可解析；超出 decimal 範圍退 "R" 格式
        var path = new RawXlsxBuilder()
            .AddSheet("Sheet1",
                """
                <sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>a</t></is></c><c r="B1" t="inlineStr"><is><t>b</t></is></c><c r="C1" t="inlineStr"><is><t>c</t></is></c></row>
                <row r="2"><c r="A2"><v>535.04999999999995</v></c><c r="B2"><v>1.5E+3</v></c><c r="C2"><v>1E+300</v></c></row>
                </sheetData>
                """)
            .Save();

        try
        {
            var rows = await ReadAllRowsAsync(path);

            Assert.Equal("535.05", rows[0].Values["a"]);
            Assert.Equal("1500", rows[0].Values["b"]);
            Assert.Equal("1E+300", rows[0].Values["c"]); // decimal 溢位 → double "R"
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DateStyles_NumericCellsConvertByNumFmt()
    {
        // 日期樣式判定（guide §3.1.5）。styles：xf0=General、xf1=14(日期)、xf2=43(會計)、
        // xf3=176(自訂非日期)、xf4=164(自訂日期 yyyy"年"m"月")、xf5=21(時間)。
        // 45292 = 2024-01-01；型別優先於樣式（t="s" + 日期樣式仍是字串）
        var path = new RawXlsxBuilder()
            .WithSharedStrings("""<si><t>備註文字</t></si>""")
            .WithStyles(
                """
                <numFmts count="2"><numFmt numFmtId="176" formatCode="0_);[Red]\(0\)"/><numFmt numFmtId="164" formatCode="yyyy&quot;年&quot;m&quot;月&quot;"/></numFmts>
                <cellXfs count="6"><xf numFmtId="0"/><xf numFmtId="14" applyNumberFormat="1"/><xf numFmtId="43" applyNumberFormat="1"/><xf numFmtId="176" applyNumberFormat="1"/><xf numFmtId="164" applyNumberFormat="1"/><xf numFmtId="21" applyNumberFormat="1"/></cellXfs>
                """)
            .AddSheet("Sheet1",
                """
                <sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>date</t></is></c><c r="B1" t="inlineStr"><is><t>acct</t></is></c><c r="C1" t="inlineStr"><is><t>custom</t></is></c><c r="D1" t="inlineStr"><is><t>cdate</t></is></c><c r="E1" t="inlineStr"><is><t>time</t></is></c><c r="F1" t="inlineStr"><is><t>styled-str</t></is></c></row>
                <row r="2"><c r="A2" s="1"><v>45292</v></c><c r="B2" s="2"><v>30000</v></c><c r="C2" s="3"><v>7</v></c><c r="D2" s="4"><v>45292</v></c><c r="E2" s="5"><v>0.5</v></c><c r="F2" s="1" t="s"><v>0</v></c></row>
                </sheetData>
                """)
            .Save();

        try
        {
            var rows = await ReadAllRowsAsync(path);
            var values = rows[0].Values;

            Assert.Equal("2024-01-01", values["date"]);     // 14 → 日期
            Assert.Equal("30000", values["acct"]);          // 43 會計 → 數值
            Assert.Equal("7", values["custom"]);            // 176 → 數值
            Assert.Equal("2024-01-01", values["cdate"]);    // 自訂日期格式 → 日期
            Assert.Equal("12:00:00", values["time"]);       // 21 → 時間（TimeSpan "c"）
            Assert.Equal("備註文字", values["styled-str"]); // 型別優先於樣式
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task StyledEmptyCellsAndStyleOnlyRows_AreSkipped()
    {
        // <c s="2"/>（有樣式無值）視為空；只有樣式 cell 的列不得當標頭列、也不產生資料列
        var path = new RawXlsxBuilder()
            .WithStyles("""<cellXfs count="2"><xf numFmtId="0"/><xf numFmtId="14" applyNumberFormat="1"/></cellXfs>""")
            .AddSheet("Sheet1",
                """
                <sheetData>
                <row r="1"><c r="A1" s="1"/><c r="B1" s="1"/></row>
                <row r="2"><c r="A2" t="inlineStr"><is><t>h1</t></is></c><c r="B2" t="inlineStr"><is><t>h2</t></is></c></row>
                <row r="3"><c r="A3"><v>1</v></c><c r="B3" s="1"/></row>
                <row r="4"><c r="A4" s="1"/></row>
                </sheetData>
                """)
            .Save();

        try
        {
            var reader = new OpenXmlSaxTableReader();
            var columns = await reader.ReadColumnsAsync(new TabularSourceRequest(path), CancellationToken.None);
            Assert.Equal(["h1", "h2"], columns); // 列 1 全 styled-empty → 列 2 才是標頭

            var rows = await ReadAllRowsAsync(path);
            var row = Assert.Single(rows); // 列 4 全空 → 跳過
            Assert.Equal(3, row.SourceRowNumber);
            Assert.Equal("1", row.Values["h1"]);
            Assert.False(row.Values.ContainsKey("h2"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Inspect_ListsWorksheetsWithEstimates_SkipsChartsheets()
    {
        // inspect：dimension 推估資料列數（末列 − 標頭列）；無 dimension → null；
        // chartsheet 不是資料工作表；空工作表 columns 為空陣列
        var path = new RawXlsxBuilder()
            .AddSheet("有量",
                """
                <dimension ref="A1:B3"/>
                <sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>x</t></is></c></row>
                <row r="2"><c r="A2"><v>1</v></c></row>
                <row r="3"><c r="A3"><v>2</v></c></row>
                </sheetData>
                """)
            .AddChartsheet("圖表")
            .AddSheet("無量",
                """
                <sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>y</t></is></c></row>
                </sheetData>
                """)
            .AddSheet("空表", "<sheetData/>")
            .Save();

        try
        {
            var reader = new OpenXmlSaxTableReader();
            var inspection = await reader.InspectAsync(path, CancellationToken.None);

            Assert.Equal("xlsx", inspection.FileType);
            Assert.NotNull(inspection.Worksheets);
            Assert.Equal(["有量", "無量", "空表"], inspection.Worksheets.Select(w => w.Name));

            Assert.Equal(["x"], inspection.Worksheets[0].Columns);
            Assert.Equal(2, inspection.Worksheets[0].RowCountEstimate);

            Assert.Null(inspection.Worksheets[1].RowCountEstimate); // 無 dimension

            Assert.Empty(inspection.Worksheets[2].Columns);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ZeroWorksheets_ThrowsEmptyWorkbook()
    {
        var path = new RawXlsxBuilder().Save();

        try
        {
            var reader = new OpenXmlSaxTableReader();

            var ex = await Assert.ThrowsAsync<JetActionException>(
                () => reader.InspectAsync(path, CancellationToken.None));
            Assert.Equal(JetErrorCodes.EmptyWorkbook, ex.Code);

            var ex2 = await Assert.ThrowsAsync<JetActionException>(
                () => reader.ReadColumnsAsync(new TabularSourceRequest(path), CancellationToken.None));
            Assert.Equal(JetErrorCodes.EmptyWorkbook, ex2.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CorruptedFile_ThrowsFileReadError()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jet-corrupt-{Guid.NewGuid():N}.xlsx");
        await File.WriteAllTextAsync(path, "這不是 zip 檔");

        try
        {
            var reader = new OpenXmlSaxTableReader();

            var ex = await Assert.ThrowsAsync<JetActionException>(
                () => reader.ReadColumnsAsync(new TabularSourceRequest(path), CancellationToken.None));

            Assert.Equal(JetErrorCodes.FileReadError, ex.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task HeaderGapPlaceholder_SharedWithNormalizer()
    {
        // 稀疏標頭合成形狀：B 欄無標頭 cell → COL_2；
        // 該欄資料 cell 的值落在 COL_2 之下
        var path = new RawXlsxBuilder()
            .AddSheet("Sheet1",
                """
                <sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>左</t></is></c><c r="C1" t="inlineStr"><is><t>右</t></is></c></row>
                <row r="2"><c r="A2"><v>1</v></c><c r="B2"><v>5</v></c><c r="C2"><v>9</v></c></row>
                </sheetData>
                """)
            .Save();

        try
        {
            var reader = new OpenXmlSaxTableReader();
            var columns = await reader.ReadColumnsAsync(new TabularSourceRequest(path), CancellationToken.None);
            Assert.Equal(["左", "COL_2", "右"], columns);

            var rows = await ReadAllRowsAsync(path);
            Assert.Equal("5", rows[0].Values["COL_2"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LeadingRowsToSkip_SkipsTitleRowSoSecondRowIsHeader()
    {
        // 事務所行事曆形狀:第 1 列樣式標題(只佔 B1)、第 2 列才是標頭、第 3 列起資料。
        var path = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 2).Value = "Holiday Table";
            ws.Cell(2, 1).Value = "Date_of_Holiday";
            ws.Cell(2, 2).Value = "Holiday_Name";
            ws.Cell(2, 3).Value = "IS_Holiday";
            ws.Cell(3, 1).Value = new DateTime(2025, 1, 1);
            ws.Cell(3, 2).Value = "元旦";
            ws.Cell(3, 3).Value = "Y";
        });

        try
        {
            var reader = new OpenXmlSaxTableReader();

            var columns = await reader.ReadColumnsAsync(
                new TabularSourceRequest(path, LeadingRowsToSkip: 1), CancellationToken.None);
            Assert.Equal(["Date_of_Holiday", "Holiday_Name", "IS_Holiday"], columns);

            var rows = new List<StagingRow>();
            await foreach (var row in reader.ReadRowsAsync(
                new TabularSourceRequest(path, LeadingRowsToSkip: 1), CancellationToken.None))
            {
                rows.Add(row);
            }

            var only = Assert.Single(rows);
            Assert.Equal(3, only.SourceRowNumber);
            Assert.Equal("2025-01-01", only.Values["Date_of_Holiday"]);
            Assert.Equal("元旦", only.Values["Holiday_Name"]);
            Assert.Equal("Y", only.Values["IS_Holiday"]);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task LeadingRowsToSkip_DefaultZero_TreatsFirstContentRowAsHeader()
    {
        // 回歸:預設 0 與現狀位元等價(GL/TB/科目配對熱路徑不受影響)。
        var path = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 1).Value = "h1";
            ws.Cell(1, 2).Value = "h2";
            ws.Cell(2, 1).Value = "v1";
            ws.Cell(2, 2).Value = "v2";
        });

        try
        {
            var reader = new OpenXmlSaxTableReader();
            var columns = await reader.ReadColumnsAsync(
                new TabularSourceRequest(path), CancellationToken.None);
            Assert.Equal(["h1", "h2"], columns);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task ReadRowsAsync_EmptySheet_ThrowsEmptyWorkbook()
    {
        // 空工作表沒有標頭列；ReadRowsAsync 必須在列舉結束時回報 empty_workbook。
        var path = new RawXlsxBuilder()
            .AddSheet("Sheet1", "<sheetData/>")
            .Save();

        try
        {
            var ex = await Assert.ThrowsAsync<JetActionException>(
                () => ReadAllRowsAsync(path));

            Assert.Equal(JetErrorCodes.EmptyWorkbook, ex.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

}
