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

/// <summary>底稿寫出器：封面、說明、step5 與自動化工具參考工作表。</summary>
public sealed class WorkpaperWriterCoverAndReferenceSheetTests
{
    // ================= Task 2:封面 / 固定文字三表(母體不影響,沿用既有逐字 oracle)=================

    [Fact]
    public async Task WriteAsync_EmitsCoverIntroAndStep5Sheets()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var names = workbook.Worksheets.Select(ws => ws.Name).ToList();
        Assert.Contains(CoverSheet, names);
        Assert.Contains(IntroSheet, names);
        Assert.Contains(Step5Sheet, names);
    }

    [Fact]
    public async Task WriteAsync_CoverSheet_WritesCompanyPeriodAndCaatsFileName()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var cover = workbook.Worksheet(CoverSheet);
        Assert.Equal("公司名稱 : 示範科技股份有限公司", cover.Cell("A1").GetString());
        Assert.Equal("測試資料期間 :  2025-01-01 ~ 2025-12-31", cover.Cell("A2").GetString());
        Assert.Equal("篩選測試母體 : 查核期間", cover.Cell("A3").GetString());
        // A6:CAATs 文件檔名,yyyymmdd 取 PeriodEnd 去非數字字元(全形冒號逐字對齊樣本)
        Assert.Equal("請詳：示範科技股份有限公司_CAATS_JE_WP_20251231.docx", cover.Cell("A6").GetString());
    }

    [Fact]
    public async Task WriteAsync_CoverSheet_RemovesTemplateOleAttachmentClosure()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);
        var outputBytes = await WriteToBytesAsync(host, projectId);
        using var output = SpreadsheetDocument.Open(
            new MemoryStream(outputBytes, writable: false),
            false);
        var workbookPart = Assert.IsType<WorkbookPart>(output.WorkbookPart);
        var sheet = workbookPart.Workbook.Sheets!.Elements<Sheet>()
            .Single(item => string.Equals(
                item.Name?.Value,
                CoverSheet,
                StringComparison.Ordinal));
        var worksheetPart = Assert.IsType<WorksheetPart>(
            workbookPart.GetPartById(sheet.Id!.Value!));

        Assert.Empty(worksheetPart.Worksheet.Elements<Drawing>());
        Assert.Empty(worksheetPart.Worksheet.Elements<LegacyDrawing>());
        Assert.Empty(worksheetPart.Worksheet.Elements<OleObjects>());
        Assert.DoesNotContain(
            worksheetPart.Parts,
            relationship => relationship.OpenXmlPart is DrawingsPart
                or VmlDrawingPart
                or EmbeddedPackagePart
                or ImagePart);
    }

    [Fact]
    public async Task WriteAsync_CoverAndStep3_AuditPeriodScope_AreExplicitAndConsistent()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);
        await using var stream = new MemoryStream();

        await BuildWriter(host).WriteAsync(
            stream,
            ContextFor(projectId),
            CancellationToken.None);

        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        Assert.Equal(
            "篩選測試母體 : 查核期間",
            workbook.Worksheet(CoverSheet).Cell("A3").GetString());
        var step3 = workbook.Worksheet(Step3Sheet);
        Assert.Contains("2025-01-01 ~ 2025-12-31", step3.Cell("B7").GetString(), StringComparison.Ordinal);
        Assert.Contains("查核期間外與無有效總帳入帳日", step3.Cell("B9").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_IntroSheet_HasLabelAndMergedBoilerplate()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var intro = workbook.Worksheet(IntroSheet);
        Assert.Equal("說明：", intro.Cell("A1").GetString());
        Assert.Contains("JE Testing Tool", intro.Cell("B1").GetString());
        Assert.Contains("B1:O1", intro.MergedRanges.Select(r => r.RangeAddress.ToString()));
    }

    [Fact]
    public async Task WriteAsync_Step5Sheet_HasYellowBannerMergedAcrossA1R1()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var step5 = workbook.Worksheet(Step5Sheet);
        Assert.Contains("Post-closing entries", step5.Cell("A1").GetString());
        Assert.Contains("A1:R1", step5.MergedRanges.Select(r => r.RangeAddress.ToString()));
    }

    // ================= Task 5:自動化工具-檔案欄位資訊(欄位配對正準名)=================

    [Fact]
    public async Task FieldInfo_TypedPath_UsesCanonicalProjectionAndDynamicTwoRowGap()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        var context = await CurrentContextForAsync(host, ctx.ProjectId);
        var plan = await CurrentPlanForAsync(host, context);
        var projection = Assert.IsType<FieldInfoProjection>(plan.FieldInfo);
        await using var stream = new MemoryStream();
        await ((IWorkpaperPlanWriter)BuildWriter(host)).WriteAsync(
            stream,
            context,
            plan,
            CancellationToken.None);

        var firstBlankRow = checked((uint)(4 + projection.TbRows.Count));
        var secondBlankRow = checked(firstBlankRow + 1);
        stream.Position = 0;
        using (var package = SpreadsheetDocument.Open(stream, false))
        {
            var workbookPart = package.WorkbookPart!;
            var sheetRef = workbookPart.Workbook.Sheets!
                .Elements<Sheet>()
                .Single(sheet => string.Equals(
                    sheet.Name?.Value,
                    FieldInfoSheet,
                    StringComparison.Ordinal));
            var worksheet = ((WorksheetPart)workbookPart.GetPartById(sheetRef.Id!))
                .Worksheet;
            var rows = worksheet.GetFirstChild<SheetData>()!
                .Elements<Row>()
                .ToDictionary(row => row.RowIndex!.Value);
            Assert.Empty(rows[firstBlankRow].Elements<Cell>());
            Assert.Empty(rows[secondBlankRow].Elements<Cell>());
        }

        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(FieldInfoSheet);
        Assert.Equal("V.2019", sheet.Cell("A1").GetString());
        Assert.Equal("TB檔案配對前後欄位對照表", sheet.Cell("A2").GetString());

        AssertProjectionRows(sheet, 4, projection.TbRows);
        var glTitleRow = checked((int)secondBlankRow + 1);
        Assert.Equal("GL檔案配對前後欄位對照表", sheet.Cell(glTitleRow, 1).GetString());
        AssertProjectionRows(sheet, glTitleRow + 2, projection.GlRows);

        Assert.All(
            Enumerable.Range(1, 5),
            column => Assert.InRange(sheet.Column(column).Width, 8d, 255d));
        Assert.All(
            Enumerable.Range(6, 3),
            column => Assert.True(sheet.Column(column).IsHidden));
        Assert.DoesNotContain("K_R條件", ReadEntireColumn(sheet, "A"));
    }

    // ================= Task 5:自動化工具-假期假日資訊(週末固定 + 假日/補班)=================

    [Fact]
    public async Task CalendarInfo_FixedWeekendTable_AndHolidayMakeupCountsMatchStore()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host); // demo 匯入 holiday(13)+ makeup(1)
        var longHolidayName = string.Concat(Enumerable.Repeat("跨年度長假日名稱", 10));
        var holidayFile = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 2).Value = "Holiday Table";
            ws.Cell(2, 1).Value = "Date_of_Holiday";
            ws.Cell(2, 2).Value = "Holiday_Name";
            ws.Cell(2, 3).Value = "IS_Holiday";
            ws.Cell(3, 1).Value = new DateTime(2025, 8, 8);
            ws.Cell(3, 2).Value = longHolidayName;
            ws.Cell(3, 3).Value = "Y";
        });
        try
        {
            await host.DispatchAsync(
                "import.holiday.fromFile",
                JsonSerializer.Serialize(new { filePath = holidayFile }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(holidayFile);
        }

        using var workbook = await WriteDemoAndReadAsync(host, ctx.ProjectId);
        var sheet = workbook.Worksheet(CalendarInfoSheet);

        // 週末表固定(資料化常數,非逐列特判):A1/B1 標頭 + 7 列;Mon-Fri=N、Sat/Sun=Y。
        Assert.Equal("DAYOFWEEK", sheet.Cell("A1").GetString());
        Assert.Equal("WORKDAY", sheet.Cell("B1").GetString());
        Assert.Equal("Monday", sheet.Cell("A2").GetString());
        Assert.Equal("N", sheet.Cell("B2").GetString());
        Assert.Equal("Friday", sheet.Cell("A6").GetString());
        Assert.Equal("N", sheet.Cell("B6").GetString());
        Assert.Equal("Saturday", sheet.Cell("A7").GetString());
        Assert.Equal("Y", sheet.Cell("B7").GetString());
        Assert.Equal("Sunday", sheet.Cell("A8").GetString());
        Assert.Equal("Y", sheet.Cell("B8").GetString());

        // 假日表標頭存在(逐字對齊樣本);假日筆數 == calendar store recount(獨立查 staging 表)。
        var holidayHeaderRow = FindRowByColumnValue(sheet, "A", "DATE_OF_HOLIDAY", 1);
        Assert.True(holidayHeaderRow > 0, "應有假日表標頭 DATE_OF_HOLIDAY");
        Assert.Equal("HOLIDAY_NAME", sheet.Cell($"B{holidayHeaderRow}").GetString());
        Assert.Equal("IS_HOLIDAY", sheet.Cell($"C{holidayHeaderRow}").GetString());

        var holidayCount = await DemoProjectPipeline.QueryScalarAsync(
            host, ctx.ProjectId,
            "SELECT COUNT(*) FROM staging_calendar_raw_day WHERE day_type = 'holiday';");
        Assert.True(holidayCount > 0, "demo 應有假日");
        var holidayRows = CountColumnFrom(sheet, "A", holidayHeaderRow + 1);
        // 假日列含補班標頭以下會中斷(補班標頭在空列之後),故先到首個空白即止——此處假日段連續。
        var isHolidayMarks = CountColumnFrom(sheet, "C", holidayHeaderRow + 1);
        Assert.Equal(holidayCount, (long)isHolidayMarks);
        Assert.True(holidayRows >= holidayCount);

        // 補班段標頭 + 筆數 == makeup recount。
        var makeupHeaderRow = FindRowByColumnValue(sheet, "A", "DATE_OF_MAKEUPDAY", 1);
        Assert.True(makeupHeaderRow > 0, "應有補班段標頭 DATE_OF_MAKEUPDAY");
        Assert.Equal("MAKEUPDAY_DESC", sheet.Cell($"B{makeupHeaderRow}").GetString());

        var makeupCount = await DemoProjectPipeline.QueryScalarAsync(
            host, ctx.ProjectId,
            "SELECT COUNT(*) FROM staging_calendar_raw_day WHERE day_type = 'makeup';");
        Assert.Equal(makeupCount, (long)CountColumnFrom(sheet, "A", makeupHeaderRow + 1));

        // 假日 IS_HOLIDAY 一律 Y(對齊樣本)。
        Assert.Equal("Y", sheet.Cell($"C{holidayHeaderRow + 1}").GetString());
        Assert.Equal(longHolidayName, sheet.Cell(holidayHeaderRow + 1, 2).GetString());
        var expectedNameWidth = ExcelDisplayWidth.ToColumnWidth(
            ExcelDisplayWidth.Measure(longHolidayName));
        Assert.InRange(sheet.Column(2).Width, expectedNameWidth - 1.5D, expectedNameWidth + 1.5D);
        AssertSingleLine(sheet.Cell(holidayHeaderRow + 1, 2));
    }

    // ================= Task 5:自動化工具-科目配對資訊(Not-in-TB 字面值)=================

    [Fact]
    public async Task AccountMapping_NotInTbAccount_WritesLiteral_OthersWriteAccountName()
    {
        using var host = new HandlerTestHost();
        var longAccountName = string.Concat(Enumerable.Repeat("長科目名稱", 12));

        // 母體:GL 含 1101(TB 有)與 5501(TB 無 → Not in TB);TB 只列 1101。
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            gl =>
            {
                // 過帳日對齊本測試 WorkpaperContext 的期間(2025)——完整性 GL 彙總（含 not-in-tb 判定）
                // 母體限本期 post_date（§2），資料須落在 context.PeriodStart/End 內方可入母體。
                gl.WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "建立人員", "金額", "借方旗標");
                gl.AddRow("JV1", "2025-03-01", "1101", longAccountName, "說明", "甲", "100.00", 1);
                gl.AddRow("JV1", "2025-03-01", "1101", longAccountName, "說明", "甲", "100.00", 0);
                gl.AddRow("JV2", "2025-03-02", "5501", "管理費用", "說明", "乙", "50.00", 1);
                gl.AddRow("JV2", "2025-03-02", "1101", longAccountName, "說明", "乙", "50.00", 0);
            },
            configureTb: tb =>
            {
                tb.AddRow("1101", longAccountName, 0);
                // 5501 刻意不入 TB → 完整性 not_in_tb 集合應含 5501
            });

        // 匯入科目配對檔(含 1101 與 5501,皆給標準分類)。
        var mappingFile = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 1).Value = "GL_NUMBER";
            ws.Cell(1, 2).Value = "GL_NAME";
            ws.Cell(1, 3).Value = "STANDARDIZED_ACCOUNT_NAME";
            ws.Cell(2, 1).Value = "1101"; ws.Cell(2, 2).Value = longAccountName; ws.Cell(2, 3).Value = "Cash";
            ws.Cell(3, 1).Value = "2101"; ws.Cell(3, 2).Value = "收入"; ws.Cell(3, 3).Value = "Revenue";
            ws.Cell(4, 1).Value = "2201"; ws.Cell(4, 2).Value = "應收款"; ws.Cell(4, 3).Value = "Receivables";
            ws.Cell(5, 1).Value = "2301"; ws.Cell(5, 2).Value = "預收款"; ws.Cell(5, 3).Value = "Receipt in advance";
            ws.Cell(6, 1).Value = "5501"; ws.Cell(6, 2).Value = "管理費用"; ws.Cell(6, 3).Value = "Others";
        });
        try
        {
            await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new
            {
                filePath = mappingFile,
                fileName = "inline-account-mapping.xlsx"
            }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(mappingFile);
        }

        using var workbook = await WriteAndReadAsync(host, projectId);
        var sheet = workbook.Worksheet(AccountMappingSheet);

        // 第 1 列標頭(逐字對齊樣本)。
        Assert.Equal("GL_NUMBER", sheet.Cell("A1").GetString());
        Assert.Equal("GL_NAME", sheet.Cell("B1").GetString());
        Assert.Equal("STANDARDIZED_ACCOUNT_NAME", sheet.Cell("C1").GetString());

        // 1101 在 TB → GL_NAME 寫 account_name。
        var row1101 = FindRowByColumnValue(sheet, "A", "1101", 2);
        Assert.True(row1101 > 0, "科目配對應含 1101");
        Assert.Equal(longAccountName, sheet.Cell($"B{row1101}").GetString());
        Assert.Equal("Cash", sheet.Cell($"C{row1101}").GetString());
        var expectedAccountNameWidth = ExcelDisplayWidth.ToColumnWidth(
            ExcelDisplayWidth.Measure(longAccountName));
        Assert.InRange(
            sheet.Column(2).Width,
            expectedAccountNameWidth - 1.5D,
            expectedAccountNameWidth + 1.5D);
        AssertSingleLine(sheet.Cell(row1101, 2));

        // 5501 在 GL 不在 TB → GL_NAME 寫字面「Not in TB」(非 account_name);分類仍寫。
        var row5501 = FindRowByColumnValue(sheet, "A", "5501", 2);
        Assert.True(row5501 > 0, "科目配對應含 5501");
        Assert.Equal("Not in TB", sheet.Cell($"B{row5501}").GetString());
        Assert.Equal("Others", sheet.Cell($"C{row5501}").GetString());
        Assert.Equal(
            AccountMappingCategories.All.Order(StringComparer.Ordinal),
            ReadColumnFrom(sheet, "C", 2).Order(StringComparer.Ordinal));

        // 獨立 recount:5501 確實落在完整性 not-in-tb 集合(GL 有 TB 無)。
        var notInTb = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId,
            "SELECT COUNT(*) FROM (SELECT account_code FROM target_gl_entry " +
            "EXCEPT SELECT account_code FROM target_tb_balance) x WHERE account_code = '5501';");
        Assert.Equal(1, notInTb);
    }


    [Fact]
    public async Task WriteAsync_FullWorkbook_InitializesFirstWorksheetPartWithSheetData()
    {
        using var host = new HandlerTestHost();
        var project = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("validate.run");
        await host.DispatchAsync("prescreen.run");
        using var stream = new MemoryStream();
        var context = await CurrentContextForAsync(host, project.ProjectId);

        var stats = await BuildWriter(host).WriteAsync(stream, context, CancellationToken.None);

        var stat = Assert.IsType<SheetStat>(stats.SheetStats[0]);
        Assert.Equal(CoverSheet, stat.SheetName);
        Assert.Equal(0, stat.RowsWritten);

        stream.Position = 0;
        using var document = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(stream, false);
        Assert.NotNull(document.WorkbookPart);
        var workbookPart = document.WorkbookPart;
        Assert.NotNull(workbookPart.Workbook.Sheets);
        var sheet = workbookPart.Workbook.Sheets
            .Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>()
            .First();
        Assert.Equal(CoverSheet, sheet.Name?.Value);

        var relationshipId = sheet.Id?.Value;
        Assert.False(string.IsNullOrWhiteSpace(relationshipId));
        var worksheetPart = Assert.IsType<DocumentFormat.OpenXml.Packaging.WorksheetPart>(
            workbookPart.GetPartById(relationshipId!));
        Assert.Single(worksheetPart.Worksheet.Elements<DocumentFormat.OpenXml.Spreadsheet.SheetData>());
    }

}
