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

    [Fact]
    public async Task FormalReports_UseAllOrScenarioScopedRdeUnionInCommittedOrdinal()
    {
        using var host = new HandlerTestHost();
        var setup = await SetupAsync(host);

        var validationArtifacts = await host.DispatchAsync(
            "export.validationArtifacts",
            JsonSerializer.Serialize(new { runId = setup.ValidationRunId }));
        var infPath = ArtifactPath(
            host,
            setup.ProjectId,
            validationArtifacts.GetProperty("artifacts"),
            "infReport");

        using (var inf = new XLWorkbook(infPath))
        {
            var sheet = inf.Worksheet("可靠性樣本_所有欄位");
            Assert.Equal(
                new[] { MoneyLabel, TextLabel, DateLabel },
                ReadHeader(sheet, 1).TakeLast(3));

            var documentColumn = FindHeaderColumn(sheet, 1, "傳票號碼");
            var moneyColumn = FindHeaderColumn(sheet, 1, MoneyLabel);
            var textColumn = FindHeaderColumn(sheet, 1, TextLabel);
            var dateColumn = FindHeaderColumn(sheet, 1, DateLabel);
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
        var prescreenRunId = prescreen.GetProperty("resultRef").GetProperty("runId").GetString()!;
        var committed = await host.DispatchAsync(
            "filter.commit",
            ScenarioPayload(setup));
        var revision = committed.GetProperty("resultRef").GetProperty("revision").GetString()!;

        var criteria = await host.DispatchAsync(
            "export.criteriaSelectionReport",
            JsonSerializer.Serialize(new
            {
                validationRunId = setup.ValidationRunId,
                prescreenRunId,
                revision
            }));
        var criteriaPath = Path.Combine(
            host.ProjectsRoot,
            setup.ProjectId,
            criteria.GetProperty("artifact").GetProperty("fileName").GetString()!);
        using (var workbook = new XLWorkbook(criteriaPath))
        {
            foreach (var position in new[] { 1, 2 })
            {
                Assert.Equal(
                    new[] { MoneyLabel, TextLabel, DateLabel },
                    ReadHeader(workbook.Worksheet($"#Criteria Select {position}"), 1).TakeLast(3));
            }
        }

        var workpaper = await host.DispatchAsync(
            "export.workpaperStream",
            JsonSerializer.Serialize(new
            {
                validationRunId = setup.ValidationRunId,
                prescreenRunId,
                scenarioRevision = revision,
                scenarioPositions = new[] { 1 }
            }));
        var workpaperPath = Path.Combine(
            host.ProjectsRoot,
            setup.ProjectId,
            workpaper.GetProperty("artifact").GetProperty("fileName").GetString()!);
        using var workingPaper = new XLWorkbook(workpaperPath);
        var step2 = workingPaper.Worksheet("step2 可靠性測試");
        var step2Headers = ReadHeader(
                step2,
                51)
            .Skip(20)
            .ToArray();
        Assert.Equal(new[] { TextLabel, DateLabel }, step2Headers);
        Assert.DoesNotContain(MoneyLabel, step2Headers);
        var step2DocumentColumn = FindHeaderColumn(step2, 51, "傳票號碼\n(分錄編號)");
        var step2TextColumn = FindHeaderColumn(step2, 51, TextLabel);
        var step2DateColumn = FindHeaderColumn(step2, 51, DateLabel);
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
        Assert.Equal(new[] { TextLabel, DateLabel }, valueHeaders.TakeLast(2));
        Assert.DoesNotContain(MoneyLabel, valueHeaders);
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
                // mapping ordinal: money, text, date. Scenario 1 deliberately references text/date only.
                rdeFields = new object[]
                {
                    new { sourceColumn = "RDE 金額來源", label = MoneyLabel, valueType = "money" },
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
            validation.GetProperty("resultRef").GetProperty("runId").GetString()!,
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
                        rules = new object[]
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
                        rules = new object[]
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

    private static string ArtifactPath(
        HandlerTestHost host,
        string projectId,
        JsonElement artifacts,
        string kind)
    {
        var artifact = Assert.Single(
            artifacts.EnumerateArray(),
            item => string.Equals(
                item.GetProperty("kind").GetString(),
                kind,
                StringComparison.Ordinal));
        return Path.Combine(
            host.ProjectsRoot,
            projectId,
            artifact.GetProperty("fileName").GetString()!);
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
        string ValidationRunId,
        string MoneyFieldId,
        string TextFieldId,
        string DateFieldId);
}
