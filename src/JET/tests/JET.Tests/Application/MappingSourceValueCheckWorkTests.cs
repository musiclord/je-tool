using System.Text.Json;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 「讀取來源值」核對審計員填的值有沒有出現在來源欄時，整份來源只能掃一次：
/// 要核對的值從 1 個變成 50 個，資料庫工作量不得超過 1.5 倍。
/// 每核對一個值就把來源重掃一遍的話，50 個值約是 50 倍，這裡就會失敗。
/// </summary>
public sealed class MappingSourceValueCheckWorkTests(ITestOutputHelper testOutput)
{
    private const int RowCount = 2_000;
    private const double AllowedGrowth = 1.5;

    [Fact]
    public async Task CheckingManyValues_ScansTheSourceOnce()
    {
        // 核對的那一句 SQL 用 requested 這個 CTE；統計值概況的那一句沒有。
        var meter = new SqliteStatementWorkMeter(static sql =>
            sql.Contains("requested AS (", StringComparison.Ordinal) ? "check" : null);
        using var host = new HandlerTestHost(sqlStatementObserver: meter);
        await InlineWorkbookProject.SetupAsync(host, gl =>
        {
            gl.WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "建立人員", "金額", "借方旗標");
            for (var index = 1; index <= RowCount; index++)
            {
                gl.AddRow($"JV-{index:00000}", "2025-06-30", "1101", "現金", index % 2 == 0 ? "甲" : "乙",
                    "人員1", "100.00", index % 2);
            }
        });

        async Task<long> CheckAsync(int count)
        {
            var values = Enumerable.Range(1, count).Select(static index => $"代碼{index:000}").ToArray();
            var before = meter.Steps("check");
            var response = await host.DispatchAsync("mapping.valueProfile", JsonSerializer.Serialize(new
            {
                dataset = "gl",
                sourceColumn = "摘要",
                comparisonValues = values,
                checkSourceValues = true,
            }));
            Assert.Equal(count, response.GetProperty("missingComparisonValues").GetArrayLength());
            return meter.Steps("check") - before;
        }

        var one = await CheckAsync(1);
        var many = await CheckAsync(50);
        var line = $"{RowCount} 列來源，核對 1 個值 {one}，核對 50 個值 {many}，{(double)many / one:F2} 倍。";
        testOutput.WriteLine(line);
        Assert.True(one > 0, $"量不到核對的資料庫工作量。{line}");
        Assert.True(many <= one * AllowedGrowth, $"核對的值變多時，來源不應重掃。{line}");
    }

    /// <summary>
    /// 按一次「讀取來源值」同時要值概況與核對時，整份來源只讀一次：常見值、空白數與缺少的值由同一句 SQL 算出，
    /// 不分成兩句各掃一次。往下填補開與關都一樣。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadingSourceValuesWithCheck_ReadsTheSourceOnce(bool fillDown)
    {
        var meter = new SqliteStatementWorkMeter(static sql =>
            sql.Contains("FROM pbc_gl", StringComparison.Ordinal) ? "source" : null);
        using var host = new HandlerTestHost(sqlStatementObserver: meter);
        await InlineWorkbookProject.SetupAsync(host, gl =>
        {
            gl.WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "建立人員", "金額", "借方旗標");
            for (var index = 1; index <= 20; index++)
            {
                gl.AddRow($"JV-{index:00000}", "2025-06-30", "1101", "現金", index % 2 == 0 ? "甲" : "乙",
                    "人員1", "100.00", index % 2);
            }
        });

        var before = meter.Statements("source");
        var response = await host.DispatchAsync("mapping.valueProfile", JsonSerializer.Serialize(new
        {
            dataset = "gl",
            sourceColumn = "摘要",
            comparisonValues = new[] { "甲", "丙" },
            checkSourceValues = true,
            fillDown,
        }));

        Assert.Equal(
            new[] { "丙" },
            response.GetProperty("missingComparisonValues").EnumerateArray().Select(static item => item.GetString()!).ToArray());
        Assert.Equal(2, response.GetProperty("distinctCount").GetInt64());
        Assert.Equal(1, meter.Statements("source") - before);
    }
}
