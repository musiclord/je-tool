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

/// <summary>底稿寫出器：封面、說明、step5 與自動化工具參考工作表。</summary>
// 封面 A3 留白。
public sealed class WorkingPaperWriterCoverAndReferenceSheetTests
{
    // ================= 封面、JE WorkingPaper說明與 step5 三張固定文字的表;另有一則核對封面與 step3 的查核期間 =================

    [Fact]
    public async Task WriteAsync_EmitsCoverIntroAndPostClosingEntriesSheets()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var names = workbook.Worksheets.Select(ws => ws.Name).ToList();
        Assert.Contains(CoverSheet, names);
        Assert.Contains(IntroSheet, names);
        Assert.Contains(PostClosingEntriesSheet, names);
    }

    [Fact]
    public async Task WriteAsync_CoverSheet_WritesCompanyPeriodAndLeavesA6Blank()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var cover = workbook.Worksheet(CoverSheet);
        Assert.Equal("公司名稱 : 示範科技股份有限公司", cover.Cell("A1").GetString());
        Assert.Equal("測試資料期間 :  2025-01-01 ~ 2025-12-31", cover.Cell("A2").GetString());
        Assert.Equal(string.Empty, cover.Cell("A3").GetString());
        // A6 只留格式、不寫字，也不寫含 CAATS 的檔名；兩份舊版 IDEA 程式都沒有寫。
        Assert.Equal(string.Empty, cover.Cell("A6").GetString());
        Assert.DoesNotContain("CAATS", cover.Cell("A6").GetString(), StringComparison.OrdinalIgnoreCase);
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
        var sheet = workbookPart.RequiredWorkbook().Sheets!.Elements<Sheet>()
            .Single(item => string.Equals(
                item.Name?.Value,
                CoverSheet,
                StringComparison.Ordinal));
        var worksheetPart = Assert.IsType<WorksheetPart>(
            workbookPart.GetPartById(sheet.Id!.Value!));

        var worksheet = worksheetPart.RequiredWorksheet();
        Assert.Empty(worksheet.Elements<Drawing>());
        Assert.Empty(worksheet.Elements<LegacyDrawing>());
        Assert.Empty(worksheet.Elements<OleObjects>());
        Assert.DoesNotContain(
            worksheetPart.Parts,
            relationship => relationship.OpenXmlPart is DrawingsPart
                or VmlDrawingPart
                or EmbeddedPackagePart
                or ImagePart);
    }

    [Fact]
    public async Task WriteAsync_CoverAndHighRiskSummary_AuditPeriodScope_AreExplicitAndConsistent()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);
        await using var stream = new MemoryStream();

        await WriteWithCurrentPlanAsync(host, BuildWriter(host), stream, ContextFor(projectId), CancellationToken.None);

        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        Assert.Equal(
            string.Empty,
            workbook.Worksheet(CoverSheet).Cell("A3").GetString());
        var highRiskSummary = workbook.Worksheet(HighRiskSummarySheet);
        Assert.Contains("2025-01-01 ~ 2025-12-31", highRiskSummary.Cell("B7").GetString(), StringComparison.Ordinal);
        // 這句用未納入原因的名稱「總帳日期空白」；底稿句子裡的欄位名稱照原樣寫，不加任何符號。
        Assert.Contains("查核期間外與總帳日期空白的分錄", highRiskSummary.Cell("B9").GetString(), StringComparison.Ordinal);
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
    public async Task WriteAsync_PostClosingEntriesSheet_HasYellowBannerMergedAcrossA1R1()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupBalancedAsync(host);
        using var workbook = await WriteAndReadAsync(host, projectId);

        var postClosingEntries = workbook.Worksheet(PostClosingEntriesSheet);
        Assert.Contains("Post-closing entries", postClosingEntries.Cell("A1").GetString());
        Assert.Contains("A1:R1", postClosingEntries.MergedRanges.Select(r => r.RangeAddress.ToString()));
    }

    // ================= 自動化工具-檔案欄位資訊(配對後的欄位名稱)=================

    [Fact]
    public async Task FieldInfo_TypedPath_UsesCanonicalProjectionAndDynamicTwoRowGap()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        var context = await CurrentContextForAsync(host, ctx.ProjectId);
        var plan = await CurrentPlanForAsync(host, context);
        var projection = Assert.IsType<FieldInfoProjection>(plan.FieldInfo);
        await using var stream = new MemoryStream();
        await ((IWorkingPaperPlanWriter)BuildWriter(host)).WriteAsync(
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
            var sheetRef = workbookPart.RequiredWorkbook().Sheets!
                .Elements<Sheet>()
                .Single(sheet => string.Equals(
                    sheet.Name?.Value,
                    WorkingPaperWriterTestSupport.FieldInfoSheet,
                    StringComparison.Ordinal));
            var worksheet = ((WorksheetPart)workbookPart.GetPartById(sheetRef.Id!))
                .RequiredWorksheet();
            var rows = worksheet.GetFirstChild<SheetData>()!
                .Elements<Row>()
                .ToDictionary(row => row.RowIndex!.Value);
            Assert.Empty(rows[firstBlankRow].Elements<Cell>());
            Assert.Empty(rows[secondBlankRow].Elements<Cell>());
        }

        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        // JET.Domain 也有名為 FieldInfoSheet 的記錄型別，這裡寫明工作表名稱常數來自 WorkingPaperWriterTestSupport，避免撞名。
        var sheet = workbook.Worksheet(WorkingPaperWriterTestSupport.FieldInfoSheet);
        // 第一列不寫版本字樣，A1 是空白。
        Assert.True(sheet.Cell("A1").IsEmpty());
        Assert.Equal("TB檔案配對前後欄位對照表", sheet.Cell("A2").GetString());
        // 照舊版保護整張表，標題列不合併。
        Assert.True(sheet.Protection.IsProtected);
        Assert.Empty(sheet.MergedRanges);

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

    // ================= 自動化工具-假期假日資訊(週末表照案件的每週休息日,加上假日與補班)=================

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
        // 照舊版保護整張表。
        Assert.True(sheet.Protection.IsProtected);

        // 示範案件沒有設定每週休息日，週末表照預設：A1、B1 是標頭，下面 7 列週一到週五寫 N，週六、週日寫 Y。
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

        // 假日表標頭存在；假日筆數和直接查 calendar_day 表得到的假日筆數相同。
        var holidayHeaderRow = FindRowByColumnValue(sheet, "A", "DATE_OF_HOLIDAY", 1);
        Assert.True(holidayHeaderRow > 0, "應有假日表標頭 DATE_OF_HOLIDAY");
        Assert.Equal("HOLIDAY_NAME", sheet.Cell($"B{holidayHeaderRow}").GetString());
        Assert.Equal("IS_HOLIDAY", sheet.Cell($"C{holidayHeaderRow}").GetString());

        var holidayCount = await DemoProjectPipeline.QueryScalarAsync(
            host, ctx.ProjectId,
            "SELECT COUNT(*) FROM calendar_day WHERE day_type = 'holiday';");
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
            "SELECT COUNT(*) FROM calendar_day WHERE day_type = 'makeup';");
        Assert.Equal(makeupCount, (long)CountColumnFrom(sheet, "A", makeupHeaderRow + 1));

        // 假日 IS_HOLIDAY 一律 Y。
        Assert.Equal("Y", sheet.Cell($"C{holidayHeaderRow + 1}").GetString());
        Assert.Equal(longHolidayName, sheet.Cell(holidayHeaderRow + 1, 2).GetString());
        var expectedNameWidth = ExcelDisplayWidth.ToColumnWidth(
            ExcelDisplayWidth.Measure(longHolidayName));
        Assert.InRange(sheet.Column(2).Width, expectedNameWidth - 1.5D, expectedNameWidth + 1.5D);
        AssertSingleLine(sheet.Cell(holidayHeaderRow + 1, 2));
    }

    // 週末表由週一排到週日，案件設定的每週休息日寫 Y；
    // 沒有設定時照預設寫週六、週日，設定成空集合時七天都寫 N。
    [Theory]
    [InlineData(null, "NNNNNYY")]
    [InlineData(new int[] { }, "NNNNNNN")]
    [InlineData(new[] { 5, 0 }, "NNNNYNY")]
    [InlineData(new[] { 1, 2, 3, 4, 5 }, "YYYYYNN")]
    public void CalendarInfo_WeekendRows_FollowNonWorkingDays(int[]? nonWorkingDays, string expected)
    {
        var rows = WorkingPaperWriter.WeekendRows(nonWorkingDays);
        Assert.Equal(
            new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" },
            rows.Select(row => row.DayName).ToArray());
        Assert.Equal(expected, string.Concat(rows.Select(row => row.Workday)));
    }

    // ================= 自動化工具-科目配對資訊(名稱照上傳的檔案，沒有名稱寫 Not in TB)=================

    /// <summary>底稿照審計員上傳的科目配對檔寫名稱，和舊版 JET 照上傳的檔案原樣寫相同；只在 GL 的 5501 也寫檔案裡的名稱。</summary>
    [Fact]
    public async Task AccountMapping_WritesTheUploadedNames_AlsoForAGlOnlyAccount()
    {
        using var host = new HandlerTestHost();
        var longAccountName = string.Concat(Enumerable.Repeat("長科目名稱", 12));

        // 母體:GL 含 1101(TB 有)與 5501(TB 無);TB 只列 1101。
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            gl =>
            {
                // 過帳日對齊本測試 WorkingPaperContext 的期間(2025)——完整性 GL 彙總（含 not-in-tb 判定）
                // 母體限本期 gl_date，資料須落在 context.PeriodStart/End 內方可入母體。
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

        // 第 1 列標頭。
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

        // 5501 在 GL 不在 TB，上傳的檔案寫了名稱「管理費用」→ GL_NAME 寫這個名稱;分類仍寫。
        var row5501 = FindRowByColumnValue(sheet, "A", "5501", 2);
        Assert.True(row5501 > 0, "科目配對應含 5501");
        Assert.Equal("管理費用", sheet.Cell($"B{row5501}").GetString());
        Assert.Equal("Others", sheet.Cell($"C{row5501}").GetString());
        Assert.Equal(
            AccountMappingCategories.All.Order(StringComparer.Ordinal),
            ReadColumnFrom(sheet, "C", 2).Order(StringComparer.Ordinal));

        // 獨立 recount:5501 確實落在完整性 not-in-tb 集合(GL 有 TB 無)。
        var notInTb = await DemoProjectPipeline.QueryScalarAsync(
            host, projectId,
            "SELECT COUNT(*) FROM (SELECT account_number FROM gl " +
            "EXCEPT SELECT account_number FROM tb) x WHERE account_number = '5501';");
        Assert.Equal(1, notInTb);
    }

    /// <summary>
    /// 已儲存的科目配對沒有科目名稱時，GL_NAME 寫「Not in TB」，不留白；科目在試算表裡也一樣。
    /// 1201 在科目配對檔的名稱欄是空的；1301 的名稱欄是「Not in TB」，匯入時當成沒有科目名稱。
    /// </summary>
    [Fact]
    public async Task AccountMapping_AccountWithoutASavedName_WritesNotInTb()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            gl =>
            {
                gl.WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "建立人員", "金額", "借方旗標");
                gl.AddRow("JV1", "2025-03-01", "1101", "現金", "說明", "甲", "100.00", 1);
                gl.AddRow("JV1", "2025-03-01", "1201", "應收帳款", "說明", "甲", "60.00", 0);
                gl.AddRow("JV1", "2025-03-01", "1301", "存貨", "說明", "甲", "40.00", 0);
            },
            configureTb: tb =>
            {
                tb.AddRow("1101", "現金", 0);
                tb.AddRow("1201", "應收帳款", 0);
                tb.AddRow("1301", "存貨", 0);
            });

        var mappingFile = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 1).Value = "GL_NUMBER";
            ws.Cell(1, 2).Value = "GL_NAME";
            ws.Cell(1, 3).Value = "STANDARDIZED_ACCOUNT_NAME";
            ws.Cell(2, 1).Value = "1101"; ws.Cell(2, 2).Value = "現金"; ws.Cell(2, 3).Value = "Cash";
            ws.Cell(3, 1).Value = "1201"; ws.Cell(3, 3).Value = "Receivables";
            ws.Cell(4, 1).Value = "1301"; ws.Cell(4, 2).Value = "Not in TB"; ws.Cell(4, 3).Value = "Others";
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

        Assert.Equal(
            new[] { ("1101", "現金"), ("1201", "Not in TB"), ("1301", "Not in TB") },
            new[] { "1101", "1201", "1301" }.Select(accountNumber =>
            {
                var row = FindRowByColumnValue(sheet, "A", accountNumber, 2);
                Assert.True(row > 0, $"科目配對應含 {accountNumber}");
                return (accountNumber, sheet.Cell($"B{row}").GetString());
            }));
    }

    /// <summary>
    /// 同一份資料在畫面分類，或用沒有改過名稱的科目配對範本匯回，底稿「自動化工具-科目配對資訊」寫的名稱相同。
    /// 1102 在試算表沒有名稱、總帳是「銀行存款」，5101 只在總帳；兩條路都和範本一樣寫「Not in TB」。
    /// </summary>
    [Fact]
    public async Task AccountMapping_ScreenClassificationAndUnchangedTemplate_WriteTheSameNames()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            gl =>
            {
                gl.WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "建立人員", "金額", "借方旗標");
                gl.AddRow("JV1", "2025-03-01", "1101", "現金", "說明", "甲", "100.00", 1);
                gl.AddRow("JV1", "2025-03-01", "1102", "銀行存款", "說明", "甲", "60.00", 0);
                gl.AddRow("JV1", "2025-03-01", "5101", "薪資", "說明", "甲", "40.00", 0);
            },
            configureTb: tb =>
            {
                tb.AddRow("1101", "現金", 0);
                tb.AddRow("1102", null, 0);
            });
        var expected = new[] { ("1101", "現金"), ("1102", "Not in TB"), ("5101", "Not in TB") };

        await host.DispatchAsync("accountMapping.save", JsonSerializer.Serialize(new
        {
            changes = expected.Select(account => new { accountNumber = account.Item1, categoryId = "builtin.cash" }).ToArray()
        }));
        var onScreen = await AccountMappingNamesAsync(host, projectId, expected.Select(account => account.Item1));

        var export = await host.DispatchAsync("export.accountMappingTemplate", "{}");
        var templatePath = export.GetProperty("filePath").GetString()!;
        var filledPath = Path.Combine(host.ProjectsRoot, "synthetic-filled-account-mapping.xlsx");
        using (var template = new XLWorkbook(templatePath))
        {
            var sheet = template.Worksheet("AccountMapping");
            for (var row = 4; row <= sheet.LastRowUsed()!.RowNumber(); row++)
            {
                sheet.Cell(row, 3).Value = AccountMappingCategories.Cash;
            }

            template.SaveAs(filledPath);
        }

        await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new
        {
            filePath = filledPath,
            fileName = "synthetic-filled-account-mapping.xlsx"
        }));
        var fromTemplate = await AccountMappingNamesAsync(host, projectId, expected.Select(account => account.Item1));

        Assert.Equal(expected, onScreen);
        Assert.Equal(expected, fromTemplate);
    }

    private static async Task<(string, string)[]> AccountMappingNamesAsync(
        HandlerTestHost host, string projectId, IEnumerable<string> accountNumbers)
    {
        using var workbook = await WriteAndReadAsync(host, projectId);
        var sheet = workbook.Worksheet(AccountMappingSheet);
        return accountNumbers.Select(accountNumber =>
        {
            var row = FindRowByColumnValue(sheet, "A", accountNumber, 2);
            Assert.True(row > 0, $"科目配對應含 {accountNumber}");
            return (accountNumber, sheet.Cell($"B{row}").GetString());
        }).ToArray();
    }

    // ================= 整本活頁簿 =================

    [Fact]
    public async Task WriteAsync_FullWorkbook_InitializesFirstWorksheetPartWithSheetData()
    {
        using var host = new HandlerTestHost();
        var project = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("validate.run");
        await host.DispatchAsync("prescreen.run");
        using var stream = new MemoryStream();
        var context = await CurrentContextForAsync(host, project.ProjectId);

        var stats = await WriteWithCurrentPlanAsync(host, BuildWriter(host), stream, context, CancellationToken.None);

        var stat = Assert.IsType<SheetStat>(stats.SheetStats[0]);
        Assert.Equal(CoverSheet, stat.SheetName);
        Assert.Equal(0, stat.RowsWritten);

        stream.Position = 0;
        using var document = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(stream, false);
        Assert.NotNull(document.WorkbookPart);
        var workbookPart = document.WorkbookPart;
        var sheets = workbookPart.RequiredWorkbook().Sheets;
        Assert.NotNull(sheets);
        var sheet = sheets
            .Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>()
            .First();
        Assert.Equal(CoverSheet, sheet.Name?.Value);

        var relationshipId = sheet.Id?.Value;
        Assert.False(string.IsNullOrWhiteSpace(relationshipId));
        var worksheetPart = Assert.IsType<DocumentFormat.OpenXml.Packaging.WorksheetPart>(
            workbookPart.GetPartById(relationshipId!));
        Assert.Single(worksheetPart.RequiredWorksheet().Elements<DocumentFormat.OpenXml.Spreadsheet.SheetData>());
    }

}
