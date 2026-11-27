using System.Text.Json;
using ClosedXML.Excel;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 正式報表 RDE 投影的 synthetic regression。Oracle 只來自本測試宣告的
/// committed ordinal 與各報表所涵蓋的情境集合，不讀取外部或真實案件資料。
/// </summary>
public sealed class FormalReportRdeProjectionTests
{
    private const string MoneyLabel = "RDE 金額";
    private const string TextLabel = "RDE 文字";
    private const string DateLabel = "RDE 日期";

    // 報告與底稿的表頭照舊版在攸關資料元素的顯示名稱後加 _JE，上面三個是配對時填的顯示名稱。
    // 可靠性樣本的表頭已是「RDE 金額_JE」。
    private const string MoneyHeader = "RDE 金額_JE";
    private const string TextHeader = "RDE 文字_JE";
    private const string DateHeader = "RDE 日期_JE";

    [Fact]
    public async Task FormalReports_UseAllOrScenarioScopedRdeUnionInCommittedOrdinal()
    {
        using var host = new HandlerTestHost();
        var setup = await SetupAsync(host);

        var validationReports = await host.DispatchAsync(
            "export.validationReports",
            JsonSerializer.Serialize(new { resultId = setup.ValidationResultId }));
        var infPath = ReportPath(
            host,
            setup.ProjectId,
            validationReports.GetProperty("publishedReports"),
            "infReport");

        using (var inf = new XLWorkbook(infPath))
        {
            var sheet = inf.Worksheet("可靠性樣本_所有欄位");
            Assert.Equal(
                new[] { MoneyHeader, TextHeader, DateHeader },
                ReadHeader(sheet, 1).TakeLast(3));

            var documentColumn = FindHeaderColumn(sheet, 1, "傳票號碼");
            var moneyColumn = FindHeaderColumn(sheet, 1, MoneyHeader);
            var textColumn = FindHeaderColumn(sheet, 1, TextHeader);
            var dateColumn = FindHeaderColumn(sheet, 1, DateHeader);
            var valuedRow = FindRow(sheet, documentColumn, "RDE-001", 2);
            Assert.Equal("12.34", sheet.Cell(valuedRow, moneyColumn).GetString());
            Assert.Equal("alpha", sheet.Cell(valuedRow, textColumn).GetString());
            Assert.Equal("2025-04-02", sheet.Cell(valuedRow, dateColumn).GetString());

            var blankRow = FindRow(sheet, documentColumn, "RDE-002", 2);
            Assert.True(sheet.Cell(blankRow, moneyColumn).IsEmpty());
            Assert.True(sheet.Cell(blankRow, textColumn).IsEmpty());
            Assert.True(sheet.Cell(blankRow, dateColumn).IsEmpty());
        }

        var prescreen = await host.DispatchAsync("prescreen.run");
        var prescreenResultId = prescreen.GetProperty("resultRef").GetProperty("resultId").GetString()!;
        var committed = await host.DispatchAsync(
            "filter.commit",
            ScenarioPayload(setup));
        var revision = committed.GetProperty("resultRef").GetProperty("revision").GetString()!;

        var criteria = await host.DispatchAsync(
            "export.criteriaSelectionReport",
            JsonSerializer.Serialize(new
            {
                validationResultId = setup.ValidationResultId,
                prescreenResultId,
                revision
            }));
        var criteriaPath = Path.Combine(
            host.ProjectsRoot,
            setup.ProjectId,
            criteria.GetProperty("publishedReport").GetProperty("fileName").GetString()!);
        using (var workbook = new XLWorkbook(criteriaPath))
        {
            foreach (var position in new[] { 1, 2 })
            {
                // 攸關資料元素後面有一欄符合標記 Final_TAG，所以取最後四欄，核對三個攸關資料元素的順序。
                Assert.Equal(
                    new[] { MoneyHeader, TextHeader, DateHeader, "Final_TAG" },
                    ReadHeader(workbook.Worksheet($"#Criteria Select {position}"), 1).TakeLast(4));
            }
        }

        var workingPaperResponse = await host.DispatchAsync(
            "export.workingPaperStream",
            JsonSerializer.Serialize(new
            {
                validationResultId = setup.ValidationResultId,
                prescreenResultId,
                scenarioRevision = revision,
                scenarioPositions = new[] { 1 }
            }));
        var workingPaperPath = Path.Combine(
            host.ProjectsRoot,
            setup.ProjectId,
            workingPaperResponse.GetProperty("publishedReport").GetProperty("fileName").GetString()!);
        using var workingPaper = new XLWorkbook(workingPaperPath);
        var infSampling = workingPaper.Worksheet("step2 可靠性測試");
        var infSamplingHeaders = ReadHeader(
                infSampling,
                51)
            .Skip(20)
            .ToArray();
        Assert.Equal(new[] { TextHeader, DateHeader }, infSamplingHeaders);
        Assert.DoesNotContain(MoneyHeader, infSamplingHeaders);
        // 攸關資料元素欄從 U 欄起，第 49 列的標題由 JET 寫。
        Assert.Equal("所選高風險條件引用RDE", infSampling.Cell(49, 21).GetString());
        var infSamplingDocumentColumn = FindHeaderColumn(infSampling, 51, "傳票號碼\n(分錄編號)");
        var infSamplingTextColumn = FindHeaderColumn(infSampling, 51, TextHeader);
        var infSamplingDateColumn = FindHeaderColumn(infSampling, 51, DateHeader);
        var infSamplingValuedRow = FindRow(infSampling, infSamplingDocumentColumn, "RDE-001", 53);
        Assert.Equal("alpha", infSampling.Cell(infSamplingValuedRow, infSamplingTextColumn).GetString());
        Assert.Equal(
            new DateTime(2025, 4, 2),
            infSampling.Cell(infSamplingValuedRow, infSamplingDateColumn).GetDateTime());
        var infSamplingBlankRow = FindRow(infSampling, infSamplingDocumentColumn, "RDE-002", 53);
        Assert.True(infSampling.Cell(infSamplingBlankRow, infSamplingTextColumn).IsEmpty());
        Assert.True(infSampling.Cell(infSamplingBlankRow, infSamplingDateColumn).IsEmpty());

        var highRiskVoucherDetailHeaders = ReadHeader(
            workingPaper.Worksheet("step4-1 符合高風險條件傳票明細"),
            5);
        var valueHeaders = highRiskVoucherDetailHeaders
            .TakeWhile(header => !header.EndsWith("_TAG", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(new[] { TextHeader, DateHeader }, valueHeaders.TakeLast(2));
        Assert.DoesNotContain(MoneyHeader, valueHeaders);
    }

    // Excel 的日期從 1900-03-01 起才和實際日期一致，更早的日期換不成正確的序列值，西元 100 年以前還會換算失敗。
    // 這種日期在底稿照存下來的年月日寫成文字，匯出照常完成；確認配對後的日期提醒寫出欄位、值與筆數。
    // 年份早於 1900 的列為年份不尋常；1900-01-01 到 1900-02-28 年份不算不尋常，列為底稿寫成文字。
    [Theory]
    [InlineData("0025-04-02", "unusualYear")]
    [InlineData("1850-04-02", "unusualYear")]
    [InlineData("1900-02-28", "textInWorkingPaper")]
    public async Task WorkingPaper_DateBeforeExcelCanShowIt_IsWrittenAsTextAsStored(string rdeDate, string noticeKind)
    {
        using var host = new HandlerTestHost();
        var setup = await SetupAsync(host, rdeDate);
        var notice = Assert.Single(setup.DateNotices);
        Assert.Equal("RDE 日期", notice.GetProperty("label").GetString());
        Assert.Equal(noticeKind, notice.GetProperty("kind").GetString());
        Assert.Equal("kept", notice.GetProperty("handling").GetString());
        Assert.Equal(1, notice.GetProperty("count").GetInt64());
        Assert.Equal(rdeDate, Assert.Single(notice.GetProperty("values").EnumerateArray()).GetProperty("value").GetString());

        var prescreen = await host.DispatchAsync("prescreen.run");
        var prescreenResultId = prescreen.GetProperty("resultRef").GetProperty("resultId").GetString()!;
        var committed = await host.DispatchAsync("filter.commit", ScenarioPayload(setup));
        var revision = committed.GetProperty("resultRef").GetProperty("revision").GetString()!;
        var workingPaperResponse = await host.DispatchAsync(
            "export.workingPaperStream",
            JsonSerializer.Serialize(new
            {
                validationResultId = setup.ValidationResultId,
                prescreenResultId,
                scenarioRevision = revision,
                scenarioPositions = new[] { 1 }
            }));
        var workingPaperPath = Path.Combine(
            host.ProjectsRoot,
            setup.ProjectId,
            workingPaperResponse.GetProperty("publishedReport").GetProperty("fileName").GetString()!);
        using var workingPaper = new XLWorkbook(workingPaperPath);

        var infSampling = workingPaper.Worksheet("step2 可靠性測試");
        var infSamplingRow = FindRow(infSampling, FindHeaderColumn(infSampling, 51, "傳票號碼\n(分錄編號)"), "RDE-001", 53);
        var infSamplingCell = infSampling.Cell(infSamplingRow, FindHeaderColumn(infSampling, 51, DateHeader));
        Assert.Equal(XLDataType.Text, infSamplingCell.DataType);
        Assert.Equal(rdeDate, infSamplingCell.GetString());

        var highRiskVoucherDetail = workingPaper.Worksheet("step4-1 符合高風險條件傳票明細");
        var highRiskVoucherDetailDateColumn = FindHeaderColumn(highRiskVoucherDetail, 5, DateHeader);
        var highRiskVoucherDetailCells = Enumerable.Range(6, (highRiskVoucherDetail.LastRowUsed()?.RowNumber() ?? 5) - 5)
            .Select(row => highRiskVoucherDetail.Cell(row, highRiskVoucherDetailDateColumn))
            .Where(cell => !cell.IsEmpty())
            .ToArray();
        var highRiskVoucherDetailCell = Assert.Single(highRiskVoucherDetailCells);
        Assert.Equal(XLDataType.Text, highRiskVoucherDetailCell.DataType);
        Assert.Equal(rdeDate, highRiskVoucherDetailCell.GetString());
    }

    // 1900-03-01 是換出的序列值和 Excel 一致的第一天，序列值 61。底稿寫成日期儲存格，Excel 顯示的就是 1900-03-01。
    [Fact]
    public async Task WorkingPaper_FirstExcelFaithfulDate_IsWrittenAsDateCell()
    {
        using var host = new HandlerTestHost();
        var setup = await SetupAsync(host, "1900-03-01");
        Assert.Empty(setup.DateNotices);

        var prescreen = await host.DispatchAsync("prescreen.run");
        var prescreenResultId = prescreen.GetProperty("resultRef").GetProperty("resultId").GetString()!;
        var committed = await host.DispatchAsync("filter.commit", ScenarioPayload(setup));
        var revision = committed.GetProperty("resultRef").GetProperty("revision").GetString()!;
        var workingPaperResponse = await host.DispatchAsync(
            "export.workingPaperStream",
            JsonSerializer.Serialize(new
            {
                validationResultId = setup.ValidationResultId,
                prescreenResultId,
                scenarioRevision = revision,
                scenarioPositions = new[] { 1 }
            }));
        var workingPaperPath = Path.Combine(
            host.ProjectsRoot,
            setup.ProjectId,
            workingPaperResponse.GetProperty("publishedReport").GetProperty("fileName").GetString()!);
        using var workingPaper = new XLWorkbook(workingPaperPath);

        var infSampling = workingPaper.Worksheet("step2 可靠性測試");
        var infSamplingRow = FindRow(infSampling, FindHeaderColumn(infSampling, 51, "傳票號碼\n(分錄編號)"), "RDE-001", 53);
        var infSamplingCell = infSampling.Cell(infSamplingRow, FindHeaderColumn(infSampling, 51, DateHeader));
        Assert.Equal(XLDataType.DateTime, infSamplingCell.DataType);
        Assert.Equal(new DateTime(1900, 3, 1), infSamplingCell.GetDateTime());

        var highRiskVoucherDetail = workingPaper.Worksheet("step4-1 符合高風險條件傳票明細");
        var highRiskVoucherDetailDateColumn = FindHeaderColumn(highRiskVoucherDetail, 5, DateHeader);
        var highRiskVoucherDetailCell = Assert.Single(Enumerable.Range(6, (highRiskVoucherDetail.LastRowUsed()?.RowNumber() ?? 5) - 5)
            .Select(row => highRiskVoucherDetail.Cell(row, highRiskVoucherDetailDateColumn))
            .Where(cell => !cell.IsEmpty()));
        Assert.Equal(XLDataType.DateTime, highRiskVoucherDetailCell.DataType);
        Assert.Equal(new DateTime(1900, 3, 1), highRiskVoucherDetailCell.GetDateTime());
    }

    [Fact]
    public async Task MappingCommit_DatesWithinTheUsualYears_GiveNoDateNotice()
    {
        using var host = new HandlerTestHost();
        var setup = await SetupAsync(host);
        Assert.Empty(setup.DateNotices);
    }

    // 舊版的攸關資料元素欄是把來源欄改名加 _JE，
    // 條件篩選的明細頁再匯出整個資料庫的所有欄位，
    // 所以攸關資料元素欄的表頭與內容和它的來源欄外觀相同，沒有一般明細的框線。
    [Fact]
    public async Task CriteriaSheets_RdeColumnsUseTheAppearanceOfTheirSourceColumns()
    {
        using var host = new HandlerTestHost();
        var setup = await SetupAsync(host);
        var prescreen = await host.DispatchAsync("prescreen.run");
        var prescreenResultId = prescreen.GetProperty("resultRef").GetProperty("resultId").GetString()!;
        var committed = await host.DispatchAsync("filter.commit", ScenarioPayload(setup));
        var revision = committed.GetProperty("resultRef").GetProperty("revision").GetString()!;
        var criteria = await host.DispatchAsync(
            "export.criteriaSelectionReport",
            JsonSerializer.Serialize(new
            {
                validationResultId = setup.ValidationResultId,
                prescreenResultId,
                revision
            }));
        var criteriaPath = Path.Combine(
            host.ProjectsRoot,
            setup.ProjectId,
            criteria.GetProperty("publishedReport").GetProperty("fileName").GetString()!);

        using var workbook = new XLWorkbook(criteriaPath);
        foreach (var position in new[] { 1, 2 })
        {
            var sheet = workbook.Worksheet($"#Criteria Select {position}");
            var lastRow = sheet.LastRowUsed()!.RowNumber();
            foreach (var (header, sourceHeader) in new[]
                     {
                         (MoneyHeader, "RDE 金額來源"),
                         (TextHeader, "RDE 文字來源"),
                         (DateHeader, "RDE 日期來源")
                     })
            {
                var rdeColumn = FindHeaderColumn(sheet, 1, header);
                var sourceColumn = FindHeaderColumn(sheet, 1, sourceHeader);
                for (var row = 1; row <= lastRow; row++)
                {
                    Assert.Equal(Appearance(sheet.Cell(row, sourceColumn)), Appearance(sheet.Cell(row, rdeColumn)));
                    Assert.Equal(XLBorderStyleValues.None, sheet.Cell(row, rdeColumn).Style.Border.TopBorder);
                }
            }
        }
    }

    // 這則核對其他 JET 自己寫的工作表：10 點的明細頁，例如 R1 到 R7、各張 #Criteria Select
    // 與 Source_Quality，列高 15 點；12 點的「可靠性樣本_所有欄位」是 18 點。程式用字級的 1.5 倍算出這些值。
    [Fact]
    public async Task FormalReports_JetWrittenDetailSheets_UseTheRowHeightOfTheirFontSize()
    {
        using var host = new HandlerTestHost();
        var setup = await SetupAsync(host);
        var validationReports = await host.DispatchAsync(
            "export.validationReports",
            JsonSerializer.Serialize(new { resultId = setup.ValidationResultId }));
        var reports = validationReports.GetProperty("publishedReports");
        using (var inf = new XLWorkbook(ReportPath(host, setup.ProjectId, reports, "infReport")))
        {
            Assert.Equal(18D, inf.Worksheet("可靠性樣本_所有欄位").RowHeight);
        }

        using (var validation = new XLWorkbook(ReportPath(host, setup.ProjectId, reports, "validationReport")))
        {
            Assert.Equal(15D, validation.Worksheet("Source_Quality").RowHeight);
        }

        var prescreen = await host.DispatchAsync("prescreen.run");
        var prescreenResultId = prescreen.GetProperty("resultRef").GetProperty("resultId").GetString()!;
        var prescreenReport = await host.DispatchAsync(
            "export.prescreenReport",
            JsonSerializer.Serialize(new { resultId = prescreenResultId }));
        var prescreenPath = Path.Combine(
            host.ProjectsRoot,
            setup.ProjectId,
            prescreenReport.GetProperty("publishedReport").GetProperty("fileName").GetString()!);
        using (var report = new XLWorkbook(prescreenPath))
        {
            var detailNames = new[] { "R1", "R2", "R3", "R4", "R5", "R6", "R7" };
            // 這份合成案件只有部分預篩選有命中，沒有命中的明細頁不產生；出現的明細頁都要是 15 點。
            var detailSheets = report.Worksheets.Where(sheet => detailNames.Contains(sheet.Name)).ToArray();
            Assert.NotEmpty(detailSheets);
            Assert.All(detailSheets, sheet => Assert.Equal(15D, sheet.RowHeight));
        }

        var committed = await host.DispatchAsync("filter.commit", ScenarioPayload(setup));
        var revision = committed.GetProperty("resultRef").GetProperty("revision").GetString()!;
        var criteria = await host.DispatchAsync(
            "export.criteriaSelectionReport",
            JsonSerializer.Serialize(new { validationResultId = setup.ValidationResultId, prescreenResultId, revision }));
        var criteriaPath = Path.Combine(
            host.ProjectsRoot,
            setup.ProjectId,
            criteria.GetProperty("publishedReport").GetProperty("fileName").GetString()!);
        using var criteriaWorkbook = new XLWorkbook(criteriaPath);
        foreach (var position in new[] { 1, 2 })
        {
            Assert.Equal(15D, criteriaWorkbook.Worksheet($"#Criteria Select {position}").RowHeight);
        }
    }

    private static async Task<RdeSetup> SetupAsync(HandlerTestHost host, string rdeDate = "2025-04-02")
    {
        InlineGlWorkbookBuilder? builder = null;
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            configured =>
            {
                builder = configured
                    .WithColumns(
                        "傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標",
                        "RDE 金額來源", "RDE 文字來源", "RDE 日期來源")
                    .AddRow(
                        "RDE-001", "2025-04-01", "1100", "Synthetic debit", "valued row", 100m, "1",
                        12.34m, "alpha", rdeDate)
                    .AddRow(
                        "RDE-002", "2025-04-03", "2100", "Synthetic credit", "blank row", 100m, "0",
                        null, null, null);
            },
            validateForDownstream: true);

        var mapping = await host.DispatchAsync(
            "mapping.commit.gl",
            JsonSerializer.Serialize(new
            {
                mapping = builder!.BuildFlagModeMapping(),
                amountMode = "flag",
                manualAutoPolicy = new
                {
                    manualValues = new[] { "true", "1" },
                    automaticValues = new[] { "false", "0" }
                },
                // mapping ordinal: amount, text, date. Scenario 1 deliberately references text/date only.
                rdeFields = new object[]
                {
                    new { sourceColumn = "RDE 金額來源", label = MoneyLabel, valueType = "amount" },
                    new { sourceColumn = "RDE 文字來源", label = TextLabel, valueType = "text" },
                    new { sourceColumn = "RDE 日期來源", label = DateLabel, valueType = "date" }
                }
            }));
        var ids = mapping.GetProperty("rdeFields")
            .EnumerateArray()
            .ToDictionary(
                field => field.GetProperty("label").GetString()!,
                field => field.GetProperty("fieldId").GetString()!,
                StringComparer.Ordinal);
        var validation = await host.DispatchAsync("validate.run");
        Assert.True(
            validation.GetProperty("completenessTest")
                .GetProperty("eligibility")
                .GetProperty("isEligible")
                .GetBoolean());

        return new RdeSetup(
            projectId,
            validation.GetProperty("resultRef").GetProperty("resultId").GetString()!,
            ids[MoneyLabel],
            ids[TextLabel],
            ids[DateLabel],
            mapping.GetProperty("dateNotices").EnumerateArray().Select(item => item.Clone()).ToArray());
    }

    private static string ScenarioPayload(RdeSetup setup) => JsonSerializer.Serialize(new
    {
        scenarios = new object[]
        {
            new
            {
                name = "text and date RDE",
                rationale = "scenario-scoped union",
                groups = new[]
                {
                    new
                    {
                        join = "AND",
                        conditions = new object[]
                        {
                            new
                            {
                                join = "AND", type = "fieldValue", fieldId = setup.DateFieldId,
                                @operator = "isNotBlank"
                            },
                            new
                            {
                                join = "OR", type = "fieldValue", fieldId = setup.TextFieldId,
                                @operator = "isBlank"
                            }
                        }
                    }
                }
            },
            new
            {
                name = "money RDE",
                rationale = "all-scenario union",
                groups = new[]
                {
                    new
                    {
                        join = "AND",
                        conditions = new object[]
                        {
                            new
                            {
                                join = "AND", type = "fieldValue", fieldId = setup.MoneyFieldId,
                                @operator = "isNotBlank", amountBasis = "signed"
                            }
                        }
                    }
                }
            }
        }
    });

    private static string ReportPath(
        HandlerTestHost host,
        string projectId,
        JsonElement reports,
        string kind)
    {
        var report = Assert.Single(
            reports.EnumerateArray(),
            item => string.Equals(
                item.GetProperty("kind").GetString(),
                kind,
                StringComparison.Ordinal));
        return Path.Combine(
            host.ProjectsRoot,
            projectId,
            report.GetProperty("fileName").GetString()!);
    }

    private static string Appearance(IXLCell cell) => string.Join(
        "|",
        cell.Style.Font.FontName,
        cell.Style.Font.FontSize,
        cell.Style.Font.Bold,
        cell.Style.Fill.PatternType,
        cell.Style.Alignment.Horizontal,
        cell.Style.Alignment.Vertical,
        cell.Style.Border.LeftBorder,
        cell.Style.Border.RightBorder,
        cell.Style.Border.TopBorder,
        cell.Style.Border.BottomBorder,
        cell.Style.NumberFormat.NumberFormatId,
        cell.Style.NumberFormat.Format);

    private static string[] ReadHeader(IXLWorksheet sheet, int row)
    {
        var lastColumn = sheet.Row(row).LastCellUsed()?.Address.ColumnNumber ?? 0;
        return Enumerable.Range(1, lastColumn)
            .Select(column => sheet.Cell(row, column).GetString())
            .ToArray();
    }

    private static int FindHeaderColumn(IXLWorksheet sheet, int row, string header)
    {
        var matches = ReadHeader(sheet, row)
            .Select((value, index) => (value, column: index + 1))
            .Where(item => string.Equals(item.value, header, StringComparison.Ordinal))
            .Select(item => item.column)
            .ToArray();
        return Assert.Single(matches);
    }

    private static int FindRow(
        IXLWorksheet sheet,
        int column,
        string expected,
        int firstDataRow)
    {
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? firstDataRow - 1;
        return Assert.Single(
            Enumerable.Range(firstDataRow, lastRow - firstDataRow + 1),
            row => string.Equals(
                sheet.Cell(row, column).GetString(),
                expected,
                StringComparison.Ordinal));
    }

    private sealed record RdeSetup(
        string ProjectId,
        string ValidationResultId,
        string MoneyFieldId,
        string TextFieldId,
        string DateFieldId,
        IReadOnlyList<JsonElement> DateNotices);
}
