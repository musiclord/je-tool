using System.Text.Json;
using JET.Application;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 每個流程的輸出筆數都寫進日誌，審計員交回日誌時，不必打開輸出檔就能核對筆數：
/// 操作結束時記下回應裡的每一種筆數，情境、匯入來源與預篩選規則逐項記；
/// 匯出時每寫完一張工作表，記下那張表的資料列數，條件篩選報告的明細表另記標了「Y」的列數。
/// 支援日誌照舊只記筆數與程式代號，不記工作表名稱、檔名或資料。
/// </summary>
public sealed class OutputCountLogTests(ITestOutputHelper testOutput)
{
    private static readonly string[] OriginalHeaders =
    [
        "傳票號碼",
        "傳票日期",
        "核准日期",
        "科目代號",
        "科目名稱",
        "摘要",
        "金額",
        "借方旗標"
    ];

    [Fact]
    public void ResultRefs_RecordsEveryCountWithItsPath_AndEachScenarioSourceAndRuleByItsKey()
    {
        var response = new
        {
            resultRef = new { resultId = "r1" },
            populationSummary = new
            {
                gl = new { rowCount = 10, voucherCount = 4, totalDebit = 999L },
                excluded = new { rowCount = 2, byPeriodCount = 2 },
            },
            voucherBalanceTest = new { status = "V", unbalancedVoucherCount = 1 },
            infSamplingTest = new { sampleSize = 3, requestedSampleSize = 5, seed = 42 },
            rulePeriod = new
            {
                population = 10,
                rules = new[]
                {
                    new { key = "trailingZeros", hitLines = (long?)6, hitVouchers = (long?)2, ratePct = (double?)60 },
                    new { key = "PRIVATE KEY", hitLines = (long?)1, hitVouchers = (long?)1, ratePct = (double?)10 },
                },
            },
            scenarios = new[]
            {
                new { position = 2, name = "PRIVATE", voucherHitCount = 67046L, rowHitCount = 133774L, summaryRowCount = 133774L },
            },
            sources = new[] { new { sourceNo = 1, fileName = "PRIVATE.xlsx", sheetName = "PRIVATE", rowCount = 7 } },
        };

        Assert.Equal(
            "resultRef.resultId=r1;"
            + "populationSummary.gl.rowCount=10;populationSummary.gl.voucherCount=4;"
            + "populationSummary.excluded.rowCount=2;populationSummary.excluded.byPeriodCount=2;"
            + "voucherBalanceTest.status=V;voucherBalanceTest.unbalancedVoucherCount=1;"
            + "infSamplingTest.sampleSize=3;infSamplingTest.requestedSampleSize=5;"
            + "rulePeriod.population=10;rulePeriod.rules[trailingZeros].hitLines=6;rulePeriod.rules[trailingZeros].hitVouchers=2;"
            + "scenarios=1;scenarios[2].voucherHitCount=67046;scenarios[2].rowHitCount=133774;scenarios[2].summaryRowCount=133774;"
            + "sources[1].rowCount=7",
            SupportResultRefs.Describe(response));
    }

    [Fact]
    public void ResultRefs_TenScenariosOfCounts_AreKeptWholeInTheSupportLog()
    {
        var response = new
        {
            scenarios = Enumerable.Range(1, 10)
                .Select(position => new
                {
                    position,
                    voucherHitCount = 1_000_000L + position,
                    rowHitCount = 2_000_000L + position,
                    summaryRowCount = 3_000_000L + position,
                })
                .ToArray(),
        };

        var text = SupportResultRefs.Describe(response)!;

        Assert.True(text.Length > 1024, $"十個情境的筆數要比原本的長度上限長，才測得出有沒有被截斷：{text.Length} 字。");
        foreach (var position in Enumerable.Range(1, 10))
        {
            Assert.Contains($"scenarios[{position}].summaryRowCount={3_000_000 + position}", text, StringComparison.Ordinal);
        }

        using var support = new SupportRingBufferLoggerProvider(10);
        var logger = support.CreateLogger("test");
        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["action"] = "query.highRiskSummaryScenarios",
            ["result_refs"] = text,
        }))
        {
            logger.Log(LogLevel.Information, new EventId(1001, "action.end"), "action end");
        }

        Assert.Equal(text, Assert.Single(support.Snapshot()).Fields["result_refs"]?.ToString());
    }

    /// <summary>
    /// 合成資料每張傳票一借一貸。情境 1 命中前一半傳票的借方，情境 2 命中全部傳票的貸方；
    /// 明細表列出命中傳票的全部分錄，所以明細列數是命中分錄的兩倍，標「Y」的列數等於命中分錄數。
    /// </summary>
    [Fact]
    public async Task CriteriaExport_LogsEachSheetsRowsAndMarkedRows_AndTheScenarioCountsOnTheSummary()
    {
        const int vouchers = 300;
        const int half = vouchers / 2;
        using var host = new HandlerTestHost(enableDevTools: true);
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            builder =>
            {
                builder.WithColumns(OriginalHeaders);
                for (var v = 1; v <= vouchers; v++)
                {
                    var description = v <= half ? $"調整 借方 {v}" : $"借方 {v}";
                    builder.AddRow($"JV-{v:0000}", "2025-03-05", "2025-03-06", "1101", "現金", description, "100.00", 1);
                    builder.AddRow($"JV-{v:0000}", "2025-03-05", "2025-03-06", "4101", "收入", $"結轉 貸方 {v}", "100.00", 0);
                }
            },
            validateForDownstream: true);
        var committed = await host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new { scenarios = new[] { KeywordScenario("調整情境", "調整"), KeywordScenario("結轉情境", "結轉") } }));
        var revision = committed.GetProperty("resultRef").GetProperty("revision").GetString()!;
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        var validationResultId = loaded.GetProperty("validationResult").GetProperty("resultRef").GetProperty("resultId").GetString()!;

        await host.DispatchAsync("query.highRiskSummaryScenarios", correlationId: "summary-scenarios");
        await host.DispatchAsync(
            "export.criteriaSelectionReport",
            JsonSerializer.Serialize(new { validationResultId, revision }),
            correlationId: "criteria-export");

        // 診斷日誌：每張工作表寫完時的那一筆進度帶工作表名稱、那張表的資料列數，明細表另帶標了「Y」的列數。
        var completed = host.DiagnosticLog!.Snapshot()
            .Where(e => e.CorrelationId == "criteria-export" && e.EventName == "export.progress" && e.Fields.ContainsKey("sheet_rows"))
            .Select(e => (
                Sheet: e.Fields["sheet_name"]?.ToString(),
                Rows: Convert.ToInt64(e.Fields["sheet_rows"]),
                Marked: e.Fields.TryGetValue("marked_rows", out var marked) && marked is not null ? Convert.ToInt64(marked) : (long?)null))
            .ToArray();
        Assert.Equal(
            [("Summary Inforamtion", 0L, (long?)null), ("#Criteria Select 1", (long)vouchers, (long?)half), ("#Criteria Select 2", vouchers * 2L, (long?)vouchers)],
            completed);

        var exported = await host.DispatchAsync("support.log.export", JsonSerializer.Serialize(new { projectId }));
        var lines = (await File.ReadAllLinesAsync(exported.GetProperty("filePath").GetString()!))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();

        // 支援日誌：同樣的列數，不帶工作表名稱，靠完成第幾張表對照。
        var supportCompleted = lines
            .Where(line => Text(line, "eventName") == "export.progress" && Text(line, "correlationId") == "criteria-export")
            .Select(line => line.GetProperty("fields"))
            .Where(fields => fields.TryGetProperty("sheet_rows", out _))
            .ToArray();
        Assert.Equal([1L, 2L, 3L], supportCompleted.Select(fields => fields.GetProperty("sheets_completed").GetInt64()));
        Assert.Equal([0L, vouchers, vouchers * 2L], supportCompleted.Select(fields => fields.GetProperty("sheet_rows").GetInt64()));
        Assert.Equal(
            [null, (long?)half, (long?)vouchers],
            supportCompleted.Select(fields => fields.TryGetProperty("marked_rows", out var marked) && marked.ValueKind == JsonValueKind.Number
                ? marked.GetInt64()
                : (long?)null));
        Assert.All(supportCompleted, fields => Assert.False(fields.TryGetProperty("sheet_name", out _)));

        // 摘要頁的傳票數與分錄筆數，在第五步讀情境彙總時記下：情境 1 是 150 張、150 筆，情境 2 是 300 張、300 筆。
        var refs = ResultRefs(lines, "query.highRiskSummaryScenarios");
        Assert.Contains($"scenarios[1].voucherHitCount={half};scenarios[1].rowHitCount={half};scenarios[1].summaryRowCount={half}", refs, StringComparison.Ordinal);
        Assert.Contains($"scenarios[2].voucherHitCount={vouchers};scenarios[2].rowHitCount={vouchers};scenarios[2].summaryRowCount={vouchers}", refs, StringComparison.Ordinal);

        // 匯入與驗證的筆數也在：匯入來源逐一記列數，驗證記 GL 的分錄數與傳票數。
        Assert.Contains($"sources[1].rowCount={vouchers * 2}", ResultRefs(lines, "import.gl.fromFile"), StringComparison.Ordinal);
        var validation = ResultRefs(lines, "validate.run");
        Assert.Contains($"populationSummary.gl.rowCount={vouchers * 2}", validation, StringComparison.Ordinal);
        Assert.Contains($"populationSummary.gl.voucherCount={vouchers}", validation, StringComparison.Ordinal);

        foreach (var line in lines.Where(line => Text(line, "eventName") == "action.end"))
        {
            var fields = line.GetProperty("fields");
            if (fields.TryGetProperty("result_refs", out var value))
            {
                testOutput.WriteLine($"{Text(fields, "action")} ({value.GetString()!.Length} 字)：{value.GetString()}");
            }
        }
    }

    private static string ResultRefs(IEnumerable<JsonElement> lines, string action) =>
        lines.Last(line => Text(line, "eventName") == "action.end" && Text(line.GetProperty("fields"), "action") == action)
            .GetProperty("fields").GetProperty("result_refs").GetString()!;

    private static string? Text(JsonElement line, string name) =>
        line.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static object KeywordScenario(string name, string keyword) => new
    {
        name,
        rationale = $"測試 {name}",
        groups = new[]
        {
            new
            {
                join = "AND",
                conditions = new[]
                {
                    new
                    {
                        join = "AND",
                        type = "fieldValue",
                        field = "description",
                        @operator = "contains",
                        values = new[] { keyword }
                    }
                }
            }
        }
    };
}
