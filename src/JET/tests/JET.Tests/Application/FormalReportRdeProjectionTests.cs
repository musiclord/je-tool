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

    // 報告與底稿的表頭照舊版在攸關資料元素的顯示名稱後加 _JE（裁定 2026-11-03-14），上面三個是配對時填的顯示名稱。
    // 第一次失敗：Public 收據 20261103-111153760-c5c19c4dc5b94ecaacaacbb58eab4608，可靠性樣本的表頭已是「RDE 金額_JE」。
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
                Assert.Equal(
                    new[] { MoneyHeader, TextHeader, DateHeader },
                    ReadHeader(workbook.Worksheet($"#Criteria Select {position}"), 1).TakeLast(3));
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
        var step2 = workingPaper.Worksheet("step2 可靠性測試");
        var step2Headers = ReadHeader(
                step2,
                51)
            .Skip(20)
            .ToArray();
        Assert.Equal(new[] { TextHeader, DateHeader }, step2Headers);
        Assert.DoesNotContain(MoneyHeader, step2Headers);
        var step2DocumentColumn = FindHeaderColumn(step2, 51, "傳票號碼\n(分錄編號)");
        var step2TextColumn = FindHeaderColumn(step2, 51, TextHeader);
        var step2DateColumn = FindHeaderColumn(step2, 51, DateHeader);
        var step2ValuedRow = FindRow(step2, step2DocumentColumn, "RDE-001", 53);
        Assert.Equal("alpha", step2.Cell(step2ValuedRow, step2TextColumn).GetString());
        Assert.Equal(
            new DateTime(2025, 4, 2),
            step2.Cell(step2ValuedRow, step2DateColumn).GetDateTime());
        var step2BlankRow = FindRow(step2, step2DocumentColumn, "RDE-002", 53);
        Assert.True(step2.Cell(step2BlankRow, step2TextColumn).IsEmpty());
        Assert.True(step2.Cell(step2BlankRow, step2DateColumn).IsEmpty());

        var step41Headers = ReadHeader(
            workingPaper.Worksheet("step4-1 符合高風險條件傳票明細"),
            5);
        var valueHeaders = step41Headers
            .TakeWhile(header => !header.EndsWith("_TAG", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(new[] { TextHeader, DateHeader }, valueHeaders.TakeLast(2));
        Assert.DoesNotContain(MoneyHeader, valueHeaders);
    }

    // 2026-10-30 結案前的獨立複審查到：2026-10-29 改的列高，只有 V_Report 5、驗證報告的欄位資訊頁與底稿 step3 有測試。
    // 這則補上其他 JET 自己寫的工作表。使用者選了「統一 15 點」（裁定 2026-10-29-08）：10 點的明細頁，例如 R1 到 R7、各張 #Criteria Select
    // 與 Source_Quality，列高 15 點；12 點的「可靠性樣本_所有欄位」是 18 點。程式用字級的 1.5 倍算出這些值，這個算法是 Claude 的歸納。
    // 這是新增的測試。
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

    private static async Task<RdeSetup> SetupAsync(HandlerTestHost host)
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
                        12.34m, "alpha", "2025-04-02")
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
            ids[DateLabel]);
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
                                join = "AND", type = "typed", fieldId = setup.DateFieldId,
                                @operator = "isNotBlank"
                            },
                            new
                            {
                                join = "OR", type = "typed", fieldId = setup.TextFieldId,
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
                                join = "AND", type = "typed", fieldId = setup.MoneyFieldId,
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
        string DateFieldId);
}
