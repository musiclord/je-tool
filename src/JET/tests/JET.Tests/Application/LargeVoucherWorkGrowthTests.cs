using System.Collections.Concurrent;
using System.Text.Json;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 分錄數加倍時，各動作的資料庫工作量不得超過 2.5 倍。
/// 規則若對每一筆分錄都重新掃一次同一張傳票的其他分錄，工作量會隨單張傳票的分錄數平方成長，約是 4 倍，這裡就會失敗。
/// 每一單位是一筆收入貸方 600、一筆現金貸方 400 與一筆其他科目借方 1,000。資料有三種形狀：
/// 全部單位放在同一張傳票、借方合成最後一筆，存五個要判斷「同一張傳票有沒有某種分錄」的情境；
/// 同一張大傳票，只存一個挑借方的情境，命中的只有最後一筆；
/// 每個單位各自一張傳票，存和第一種相同的五個情境，確認小傳票很多時也一樣按比例成長。
/// 「同一張傳票有沒有借方」這類判斷要掃到最後一筆才找得到，「同一張傳票沒有應收借方」這類判斷要掃完整張傳票。
/// </summary>
public sealed class LargeVoucherWorkGrowthTests(ITestOutputHelper testOutput)
{
    private const int SmallUnitCount = 600;
    private const double AllowedGrowth = 2.5;
    private const string OneVoucher = "oneVoucher";
    private const string OneVoucherLastEntryHits = "oneVoucherLastEntryHits";
    private const string ManyVouchers = "manyVouchers";

    [Theory]
    [InlineData(OneVoucher)]
    [InlineData(OneVoucherLastEntryHits)]
    [InlineData(ManyVouchers)]
    public async Task EveryAction_DatabaseWorkGrowsInProportionToTheEntries(string shape)
    {
        var small = await MeasureAsync(SmallUnitCount, shape);
        var large = await MeasureAsync(SmallUnitCount * 2, shape);

        var failures = new List<string>();
        foreach (var (action, smallSteps) in small.Totals)
        {
            var largeSteps = large.Totals[action];
            var line = $"{action}：{EntryCount(SmallUnitCount, shape)} 筆分錄 {smallSteps}，"
                + $"{EntryCount(SmallUnitCount * 2, shape)} 筆分錄 {largeSteps}，"
                + $"{(double)largeSteps / smallSteps:F2} 倍。";
            testOutput.WriteLine(line);
            Assert.True(smallSteps > 0, $"量不到資料庫的工作量。{line}");
            if (largeSteps > smallSteps * AllowedGrowth)
            {
                failures.Add(line);
                foreach (var (sql, smallSql) in small.BySql[action]
                             .OrderByDescending(item => large.BySql[action].GetValueOrDefault(item.Key) - item.Value)
                             .Where(item => item.Value > 1000)
                             .Take(8))
                {
                    var largeSql = large.BySql[action].GetValueOrDefault(sql);
                    testOutput.WriteLine(
                        $"    {smallSql} → {largeSql}（{(smallSql == 0 ? 0 : (double)largeSql / smallSql):F2} 倍）：{Shorten(sql)}");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            $"分錄數多一倍時，資料庫工作量不應超過 {AllowedGrowth} 倍。" + string.Join(" ", failures));
    }

    private static int EntryCount(int units, string shape) => shape == ManyVouchers ? units * 3 : units * 2 + 1;

    private static string Shorten(string sql)
    {
        var flat = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= 600 ? flat : flat[..600] + "…";
    }

    private sealed record Measurement(
        IReadOnlyDictionary<string, long> Totals,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> BySql);

    private static async Task<Measurement> MeasureAsync(int units, string shape)
    {
        var oneVoucher = shape != ManyVouchers;
        var firstVoucher = oneVoucher ? "JV-BIG" : "JV-00001";
        var seen = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var meter = new SqliteStatementWorkMeter(sql =>
        {
            seen.TryAdd(sql, 0);
            return sql;
        });
        using var host = new HandlerTestHost(sqlStatementObserver: meter);
        await InlineWorkbookProject.SetupAsync(
            host,
            gl =>
            {
                gl.WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "建立人員", "金額", "借方旗標");
                for (var index = 1; index <= units; index++)
                {
                    var voucher = oneVoucher ? "JV-BIG" : $"JV-{index:00000}";
                    gl.AddRow(voucher, "2025-06-30", "R100", "銷貨收入", $"銷貨{index:00000}", "人員1", "600.00", 0);
                    gl.AddRow(voucher, "2025-06-30", "C100", "現金", $"付現{index:00000}", "人員1", "400.00", 0);
                    if (!oneVoucher)
                    {
                        gl.AddRow(voucher, "2025-06-30", "O100", "其他科目", $"借方{index:00000}", "人員1", "1000.00", 1);
                    }
                }

                if (oneVoucher)
                {
                    gl.AddRow("JV-BIG", "2025-06-30", "O100", "其他科目", "彙總借方", "人員1", $"{units * 1000}.00", 1);
                }
            },
            configureTb: tb =>
            {
                tb.AddRow("R100", "銷貨收入", units * -600);
                tb.AddRow("C100", "現金", units * -400);
                tb.AddRow("O100", "其他科目", units * 1000);
            });
        await ImportAccountMappingAsync(host);

        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        var bySql = new Dictionary<string, IReadOnlyDictionary<string, long>>(StringComparer.Ordinal);

        async Task<JsonElement> MeasureActionAsync(string name, string action, object? payload = null)
        {
            var beforeTotal = meter.TotalSteps;
            var before = seen.Keys.ToDictionary(sql => sql, meter.Steps, StringComparer.Ordinal);
            var response = await host.DispatchAsync(action, payload is null ? "{}" : JsonSerializer.Serialize(payload));
            totals.Add(name, meter.TotalSteps - beforeTotal);
            bySql.Add(name, seen.Keys.ToDictionary(
                sql => sql,
                sql => meter.Steps(sql) - before.GetValueOrDefault(sql),
                StringComparer.Ordinal));
            return response;
        }

        var validation = await host.DispatchAsync("validate.run");
        var prescreen = await MeasureActionAsync("預篩選", "prescreen.run");
        Assert.True(
            prescreen.GetProperty("unexpectedAccountPair").GetProperty("count").GetInt64() == EntryCount(units, shape),
            "未預期借貸組合單獨使用時列整張命中傳票。" + prescreen.GetProperty("unexpectedAccountPair").GetRawText());
        var scenarios = shape == OneVoucherLastEntryHits
            ? new object[] { Scenario("最後一筆借方", new { join = "and", type = "drCrOnly", drCr = "debit" }) }
            : new object[]
            {
                Scenario("未預期借貸組合", new { join = "and", type = "prescreen", prescreenKey = "unexpectedAccountPair" }),
                Scenario("收入無一般對方科目", new { join = "and", type = "revenueWithoutNormalCounterpart" }),
                Scenario("科目配對分析", new
                {
                    join = "and", type = "accountPair", pairMode = "exact",
                    debitCategoryIds = new[] { "builtin.others" }, creditCategoryIds = new[] { "builtin.revenue" }
                }),
                Scenario("借方其他、貸方沒有應收", new
                {
                    join = "and", type = "specialAccountCategoryPair", pairMode = "drNotCr",
                    debitCategoryIds = new[] { "builtin.others" }, creditCategoryIds = new[] { "builtin.receivables" }
                }),
                Scenario("貸方收入、借方沒有應收", new
                {
                    join = "and", type = "specialAccountCategoryPair", pairMode = "notDrCr",
                    debitCategoryIds = new[] { "builtin.receivables" }, creditCategoryIds = new[] { "builtin.revenue" }
                }),
            };
        var committed = await MeasureActionAsync("條件保存", "filter.commit", new { scenarios });
        var revision = committed.GetProperty("resultRef").GetProperty("revision").GetString()!;
        var validationResultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString()!;
        var prescreenResultId = prescreen.GetProperty("resultRef").GetProperty("resultId").GetString()!;

        await MeasureActionAsync("高風險條件彙總情境", "query.highRiskSummaryScenarios");
        await MeasureActionAsync("高風險條件彙總傳票頁", "query.highRiskSummaryVoucherPage", new { pageSize = 200 });
        await MeasureActionAsync("高風險條件彙總分錄頁", "query.highRiskSummaryRowPage", new { pageSize = 200 });
        await MeasureActionAsync("預篩選分頁", "query.prescreenPage", new { ruleKey = "unexpectedAccountPair", pageSize = 200 });
        var positions = Enumerable.Range(1, scenarios.Length).ToArray();
        foreach (var position in positions)
        {
            await MeasureActionAsync($"命中分錄頁{position}", "query.filterHitsPage", new { scenarioPosition = position, pageSize = 200 });
            var vouchers = await MeasureActionAsync(
                $"命中傳票頁{position}",
                "query.filterVoucherPage",
                new { scenarioPosition = position, scenarioRevision = revision, pageSize = 200 });
            await MeasureActionAsync(
                $"命中傳票分錄頁{position}",
                "query.filterVoucherRowsPage",
                new
                {
                    scenarioPosition = position, scenarioRevision = revision, voucherNumber = firstVoucher, pageSize = 200,
                    queryRevision = vouchers.GetProperty("queryRevision").GetString()
                });
        }

        await MeasureActionAsync("預篩選報告", "export.prescreenReport", new { resultId = prescreenResultId });
        await MeasureActionAsync(
            "條件篩選報告",
            "export.criteriaSelectionReport",
            new { validationResultId, prescreenResultId, revision });
        await MeasureActionAsync(
            "工作底稿",
            "export.workingPaperStream",
            new { validationResultId, prescreenResultId, scenarioRevision = revision, scenarioPositions = positions });
        return new Measurement(totals, bySql);
    }

    private static object Scenario(string name, object condition) => new
    {
        name,
        rationale = "大傳票工作量",
        groups = new[] { new { join = "and", conditions = new[] { condition } } }
    };

    private static async Task ImportAccountMappingAsync(HandlerTestHost host)
    {
        var path = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Cell(1, 1).Value = "科目代號";
            sheet.Cell(1, 2).Value = "科目名稱";
            sheet.Cell(1, 3).Value = "分類";
            sheet.Cell(2, 1).Value = "R100";
            sheet.Cell(2, 2).Value = "銷貨收入";
            sheet.Cell(2, 3).Value = "Revenue";
            sheet.Cell(3, 1).Value = "C100";
            sheet.Cell(3, 2).Value = "現金";
            sheet.Cell(3, 3).Value = "Cash";
            sheet.Cell(4, 1).Value = "O100";
            sheet.Cell(4, 2).Value = "其他科目";
            sheet.Cell(4, 3).Value = "Others";
        });
        try
        {
            await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new { filePath = path }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }
}
