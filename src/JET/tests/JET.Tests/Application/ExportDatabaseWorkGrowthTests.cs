using System.Text.Json;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 驗證、預篩選、條件保存與四份匯出各自的資料庫工作量，要跟著資料量成比例成長。
/// 這裡用正式的動作走完整條路，不挑個別的查詢：資料量多一倍，工作量不得超過 2.5 倍。
/// 匯出程式若一頁一頁呼叫每頁都重算整個範圍的查詢，工作量會隨資料量平方成長，多一倍資料約是 4 倍，這裡就會失敗。
/// 合成資料讓每一種明細都跟著資料量變多：傳票借貸不平、分錄符合篩選條件、日期在週末，
/// 一半的借方分錄沒有摘要，每兩筆分錄用一個新科目，而且每個科目的試算表金額都和總帳不同。
/// 資料有兩種形狀：很多張各兩筆分錄的傳票，以及全部分錄都在同一張傳票。
/// </summary>
public sealed class ExportDatabaseWorkGrowthTests(ITestOutputHelper testOutput)
{
    private const int SmallVoucherCount = 600;
    private const double AllowedGrowth = 2.5;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryExport_DatabaseWorkGrowsInProportionToTheData(bool singleVoucher)
    {
        var small = await MeasureAsync(SmallVoucherCount, singleVoucher);
        var large = await MeasureAsync(SmallVoucherCount * 2, singleVoucher);

        var failures = new List<string>();
        foreach (var (export, smallSteps) in small)
        {
            var largeSteps = large[export];
            var line = $"{export}：{SmallVoucherCount * 2} 筆分錄 {smallSteps}，{SmallVoucherCount * 4} 筆分錄 {largeSteps}，"
                + $"{(double)largeSteps / smallSteps:F2} 倍。";
            testOutput.WriteLine(line);
            Assert.True(smallSteps > 0, $"量不到資料庫的工作量。{line}");
            if (largeSteps > smallSteps * AllowedGrowth)
            {
                failures.Add(line);
            }
        }

        Assert.True(
            failures.Count == 0,
            $"資料量多一倍時，匯出的資料庫工作量不應超過 {AllowedGrowth} 倍。" + string.Join(" ", failures));
    }

    private static async Task<IReadOnlyDictionary<string, long>> MeasureAsync(int vouchers, bool singleVoucher)
    {
        var meter = new SqliteStatementWorkMeter(static _ => null);
        using var host = new HandlerTestHost(sqlStatementObserver: meter);
        await InlineWorkbookProject.SetupAsync(
            host,
            gl =>
            {
                gl.WithColumns(
                    "傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "建立人員", "金額", "借方旗標");
                for (var index = 1; index <= vouchers; index++)
                {
                    var voucher = singleVoucher ? "JV-BIG" : $"JV-{index:00000}";
                    var account = $"D{index:00000}";
                    var preparer = $"人員{index % 7}";
                    // 2025-06-28 是星期六。借方 1,000、貸方 900，每張傳票都不平。
                    gl.AddRow(voucher, "2025-06-28", account, $"借方科目{index:00000}",
                        index % 2 == 0 ? "調整分錄" : string.Empty, preparer, "1000.00", 1);
                    gl.AddRow(voucher, "2025-06-28", "C999", "貸方科目",
                        "結轉", preparer, "900.00", 0);
                }
            },
            configureTb: tb =>
            {
                // 每個借方科目的試算表金額都和總帳的 1,000 不同，完整性差異的科目數跟著傳票數成長。
                for (var index = 1; index <= vouchers; index++)
                {
                    tb.AddRow($"D{index:00000}", $"借方科目{index:00000}", 10);
                }
                tb.AddRow("C999", "貸方科目", vouchers * -900);
            });

        var measured = new Dictionary<string, long>(StringComparer.Ordinal);
        var beforeRuns = meter.TotalSteps;
        var validation = await host.DispatchAsync("validate.run");
        measured.Add("驗證", meter.TotalSteps - beforeRuns);
        beforeRuns = meter.TotalSteps;
        var prescreen = await host.DispatchAsync("prescreen.run");
        measured.Add("預篩選", meter.TotalSteps - beforeRuns);
        beforeRuns = meter.TotalSteps;
        var committed = await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            scenarios = new[]
            {
                new
                {
                    name = "所有借方",
                    rationale = "每張傳票都符合",
                    groups = new[]
                    {
                        new
                        {
                            join = "and",
                            conditions = new[] { new { join = "and", type = "drCrOnly", drCr = "debit" } }
                        }
                    }
                }
            }
        }));
        measured.Add("條件保存", meter.TotalSteps - beforeRuns);
        var validationResultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString()!;
        var prescreenResultId = prescreen.GetProperty("resultRef").GetProperty("resultId").GetString()!;
        var revision = committed.GetProperty("resultRef").GetProperty("revision").GetString()!;

        async Task ExportAsync(string name, string action, object payload)
        {
            var before = meter.TotalSteps;
            var response = await host.DispatchAsync(action, JsonSerializer.Serialize(payload));
            Assert.True(response.GetProperty("ok").GetBoolean(), $"{name}沒有匯出成功。");
            measured.Add(name, meter.TotalSteps - before);
        }

        await ExportAsync("資料驗證報告", "export.validationReports", new { resultId = validationResultId });
        await ExportAsync("預篩選報告", "export.prescreenReport", new { resultId = prescreenResultId });
        await ExportAsync(
            "條件篩選報告",
            "export.criteriaSelectionReport",
            new { validationResultId, prescreenResultId, revision });
        await ExportAsync(
            "工作底稿",
            "export.workingPaperStream",
            new { validationResultId, prescreenResultId, scenarioRevision = revision, scenarioPositions = new[] { 1 } });
        return measured;
    }
}
