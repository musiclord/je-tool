using System.Globalization;
using System.Text.Json;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// GL 與 GL(未納入測試) 的分法，只從畫面看得到的結果核對：資料驗證的 GL 範圍摘要，以及資料預覽兩頁的筆數、
/// 傳票與未納入原因。資料庫怎麼標記一列沒有納入測試（欄名與值的方向）不在這裡出現，
/// 換欄名或反轉值以後，這裡的每一個數字都要照舊。
/// 合成資料：查核期間 2025-01-01 到 2025-12-31，過帳狀態只收 POSTED。
/// G1、G2 期間內已過帳，進 GL；B1 早於查核期間、A1 晚於查核期間、N1 總帳日期空白、D1 過帳狀態是 DRAFT，
/// 各兩列，進 GL(未納入測試)。D2 日期晚於查核期間且過帳狀態也不符，日期先判斷，記為晚於查核期間。
/// 預期值逐列手算。
/// </summary>
public sealed class GlExcludedPopulationPartitionTests
{
    [Theory]
    [InlineData("sqlite")]
    public async Task GlAndGlExcluded_KeepCountsTotalsAndReasons(string provider)
    {
        using var host = new HandlerTestHost();
        var builder = new InlineGlWorkbookBuilder()
            .WithColumns("傳票號碼", "傳票項次", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標", "過帳狀態")
            .AddRow("G1", "1", "2025-03-01", "1101", "現金", "合成一", 1000, 1, "POSTED")
            .AddRow("G1", "2", "2025-03-01", "4101", "銷貨收入", "合成一", 1000, 0, "POSTED")
            .AddRow("G2", "1", "2025-12-31", "6112", "租金支出", "合成二", 250.5m, 1, "POSTED")
            .AddRow("G2", "2", "2025-12-31", "2171", "應付費用", "合成二", 250.5m, 0, "POSTED")
            .AddRow("B1", "1", "2024-12-31", "1101", "現金", "早於期間", 300, 1, "POSTED")
            .AddRow("B1", "2", "2024-12-31", "4101", "銷貨收入", "早於期間", 300, 0, "POSTED")
            .AddRow("A1", "1", "2026-01-01", "1101", "現金", "晚於期間", 40, 1, "POSTED")
            .AddRow("A1", "2", "2026-01-01", "4101", "銷貨收入", "晚於期間", 40, 0, "POSTED")
            .AddRow("N1", "1", null, "1101", "現金", "日期空白", 7, 1, "POSTED")
            .AddRow("N1", "2", null, "4101", "銷貨收入", "日期空白", 5, 0, "POSTED")
            .AddRow("D1", "1", "2025-06-30", "1101", "現金", "草稿", 60, 1, "DRAFT")
            .AddRow("D1", "2", "2025-06-30", "4101", "銷貨收入", "草稿", 60, 0, "DRAFT")
            .AddRow("D2", "1", "2026-02-01", "1101", "現金", "期後草稿", 9, 1, "DRAFT");
        await CreateAndImportAsync(host, builder, provider);

        var mapping = builder.BuildFlagModeMapping();
        mapping["postingStatus"] = "過帳狀態";
        await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping,
            amountMode = "flag",
            manualAutoPolicy = new { manualValues = new[] { "1" }, automaticValues = new[] { "0" } },
            postingStatusPolicy = new { acceptedValues = new[] { "POSTED" }, includeBlank = false }
        }));

        var validation = await host.DispatchAsync("validate.run");
        var population = validation.GetProperty("populationSummary");
        var raw = population.GetProperty("raw");
        // 匯入 13 列，借方 1000 + 250.5 + 300 + 40 + 7 + 60 + 9 = 1666.5，貸方 1000 + 250.5 + 300 + 40 + 5 + 60 = 1655.5。
        Assert.Equal(13, raw.GetProperty("rowCount").GetInt64());
        Assert.Equal(1666.5m, raw.GetProperty("totalDebit").GetDecimal());
        Assert.Equal(1655.5m, raw.GetProperty("totalCredit").GetDecimal());
        var gl = population.GetProperty("gl");
        // GL 只有 G1、G2：4 列、2 張傳票，借貸各 1250.5。
        Assert.Equal(4, gl.GetProperty("rowCount").GetInt64());
        Assert.Equal(2, gl.GetProperty("voucherCount").GetInt64());
        Assert.Equal(1250.5m, gl.GetProperty("totalDebit").GetDecimal());
        Assert.Equal(1250.5m, gl.GetProperty("totalCredit").GetDecimal());
        Assert.Equal(0m, gl.GetProperty("net").GetDecimal());
        var excluded = population.GetProperty("excluded");
        // GL(未納入測試) 9 列：日期不符 B1、A1、N1 各兩列加 D2 一列共 7 列，過帳狀態不符只有 D1 兩列。
        Assert.Equal(9, excluded.GetProperty("rowCount").GetInt64());
        Assert.Equal(7, excluded.GetProperty("byPeriodCount").GetInt64());
        Assert.Equal(2, excluded.GetProperty("byPostingStatusCount").GetInt64());

        var glPage = await host.DispatchAsync("query.dataPreview", """{ "dataset": "gl" }""");
        Assert.Equal(4, glPage.GetProperty("totalCount").GetInt64());
        Assert.Equal(new[] { "G1", "G1", "G2", "G2" }, Vouchers(glPage));

        var excludedPage = await host.DispatchAsync("query.dataPreview", """{ "dataset": "glExcluded" }""");
        Assert.Equal(9, excludedPage.GetProperty("totalCount").GetInt64());
        // 每一列最後一欄是未納入原因。
        Assert.Equal(
            new[]
            {
                "A1:afterPeriod", "A1:afterPeriod", "B1:beforePeriod", "B1:beforePeriod",
                "D1:postingStatus", "D1:postingStatus", "D2:afterPeriod", "N1:blankGlDate", "N1:blankGlDate"
            },
            excludedPage.GetProperty("rows").EnumerateArray()
                .Select(row => row[0].GetString() + ":" + row[row.GetArrayLength() - 1].GetString())
                .Order(StringComparer.Ordinal));

        foreach (var (reason, vouchers) in new (string, string[])[]
                 {
                     ("blankGlDate", ["N1", "N1"]),
                     ("beforePeriod", ["B1", "B1"]),
                     ("afterPeriod", ["A1", "A1", "D2"]),
                     ("postingStatus", ["D1", "D1"])
                 })
        {
            var page = await host.DispatchAsync("query.dataPreview", JsonSerializer.Serialize(new
            {
                dataset = "glExcluded",
                exclusionReason = reason
            }));
            Assert.Equal(vouchers.Length, page.GetProperty("totalCount").GetInt64());
            Assert.Equal(vouchers, Vouchers(page));
        }

        // GL(未納入測試) 的借貸合計 = 匯入合計 − GL 合計：借方 416，貸方 405。
        Assert.Equal(416m, raw.GetProperty("totalDebit").GetDecimal() - gl.GetProperty("totalDebit").GetDecimal());
        Assert.Equal(405m, raw.GetProperty("totalCredit").GetDecimal() - gl.GetProperty("totalCredit").GetDecimal());
        Assert.Equal(
            416m,
            AmountSum(excludedPage, debit: true));
        Assert.Equal(
            405m,
            AmountSum(excludedPage, debit: false));
    }

    private static string[] Vouchers(JsonElement page) =>
        page.GetProperty("rows").EnumerateArray()
            .Select(row => row[0].GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>資料預覽第 7 欄是有號金額（借方正、貸方負），第 8 欄是借貸別。</summary>
    private static decimal AmountSum(JsonElement page, bool debit) =>
        page.GetProperty("rows").EnumerateArray()
            .Where(row => row[7].GetString() == (debit ? "DEBIT" : "CREDIT"))
            .Sum(row => Math.Abs(decimal.Parse(row[6].GetString()!, CultureInfo.InvariantCulture)));

    private static async Task CreateAndImportAsync(HandlerTestHost host, InlineGlWorkbookBuilder builder, string provider)
    {
        await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectCode = "PARTITION-2025-001",
            clientName = "合成案件",
            operatorId = "tester",
            periodStart = "2025-01-01",
            periodEnd = "2025-12-31",
            databaseProvider = provider
        }));
        var filePath = builder.WriteWorkbook();
        try
        {
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(filePath);
        }
    }
}
