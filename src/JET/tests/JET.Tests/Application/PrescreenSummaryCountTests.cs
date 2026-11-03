using System.Text.Json;
using ClosedXML.Excel;
using Xunit;

namespace JET.Tests.Application;

public sealed class PrescreenSummaryCountTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    // 裁定 2026-10-29-27：第四步照舊版拿掉編製分錄較少的人員與使用較少的科目，第四步不再數 11 筆以下的科目與分錄，原本的 rareAccount.count 與 rareAccountCount 兩項斷言跟著拿掉，
    // 測試改名；保留較少使用之科目彙總只看查核期間的科目數，並改在重新開啟後核對同一個科目數。
    // 第一次失敗：Focused 收據 20261030-163342206-c1b391e5be2e46bfa36b85fdbe6b59ad。
    public async Task DistinctAccountCount_UsesAuditPeriodAndSurvivesReopen(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(host, builder =>
        {
            Columns(builder);
            AddAccount(builder, "A", 11);
            AddAccount(builder, "B", 12);
            AddAccount(builder, "C", 1);
            AddAccount(builder, "A", 5, "2026-01-05");
        }, databaseProvider: provider, validateForDownstream: true);

        var result = await host.DispatchAsync("prescreen.run");
        Assert.Equal(3, result.GetProperty("rareAccounts").GetProperty("distinctAccountCount").GetInt64());
        Assert.False(result.TryGetProperty("rareAccount", out _));

        var reopened = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        var reopenedAccounts = reopened.GetProperty("prescreenResult").GetProperty("rareAccounts");
        Assert.Equal(3, reopenedAccounts.GetProperty("distinctAccountCount").GetInt64());
        Assert.False(reopenedAccounts.TryGetProperty("rareAccountCount", out _));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    // 裁定 2026-10-29-27：第四步照舊版拿掉編製分錄較少的人員與使用較少的科目，原本的 rareAccount.count 與 rareAccountCount 兩項斷言跟著拿掉，測試改名；第一次失敗：Focused 收據 20261030-163342206-c1b391e5be2e46bfa36b85fdbe6b59ad。
    public async Task DistinctAccountCount_UsesWholePopulationBeyondFiftyRowSummary(string provider)
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder =>
        {
            Columns(builder);
            for (var i = 0; i < 61; i++) AddAccount(builder, $"A{i:00}", 1);
        }, databaseProvider: provider, validateForDownstream: true);

        var result = await host.DispatchAsync("prescreen.run");
        var rareAccounts = result.GetProperty("rareAccounts");
        Assert.Equal(50, rareAccounts.GetProperty("accounts").GetArrayLength());
        Assert.Equal(61, rareAccounts.GetProperty("distinctAccountCount").GetInt64());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task BlankPreparerGroup_ReportShowsLabelWithoutMergingLiteralNameOrChangingTotals(string provider)
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder =>
        {
            Columns(builder);
            string?[] preparers = [null, "", "\u3000\u00a0", "U1", " u1 ", "（空白）"];
            for (var i = 0; i < preparers.Length; i++)
                builder.AddRow($"V{i}", "2025-03-05", "A", "合成科目", "一般分錄", preparers[i], "10.00", 1);
        }, databaseProvider: provider, validateForDownstream: true);

        var result = await host.DispatchAsync("prescreen.run");
        var preparerRows = result.GetProperty("preparerSummary").GetProperty("preparers").EnumerateArray().ToArray();
        Assert.Equal(new[] { "", "U1", "（空白）" }, preparerRows.Select(row => row.GetProperty("createdBy").GetString()));
        Assert.Equal(new long[] { 3, 2, 1 }, preparerRows.Select(row => row.GetProperty("entryCount").GetInt64()));
        Assert.Equal(new decimal[] { 30, 20, 10 }, preparerRows.Select(row => row.GetProperty("debitTotal").GetDecimal()));
        Assert.Equal(3, result.GetProperty("concentration").GetProperty("preparers").GetProperty("totalPreparerCount").GetInt64());
        var resultId = result.GetProperty("resultRef").GetProperty("resultId").GetString();
        var exported = await host.DispatchAsync("export.prescreenReport", JsonSerializer.Serialize(new { resultId }));
        Assert.True(exported.GetProperty("ok").GetBoolean());
        using var workbook = new XLWorkbook(exported.GetProperty("publishedReport").GetProperty("fullPath").GetString()!);
        var rows = workbook.Worksheet("R5").RowsUsed().Skip(1).ToArray();
        Assert.Equal(3, rows.Length);
        Assert.Equal(new[] { "（空白）", "U1", "（空白）" }, rows.Select(row => row.Cell(1).GetString()));
        Assert.Equal(new long[] { 3, 2, 1 }, rows.Select(row => row.Cell(2).GetValue<long>()));
        Assert.Equal(new decimal[] { 30, 20, 10 }, rows.Select(row => row.Cell(3).GetValue<decimal>()));
        Assert.All(rows, row => Assert.Equal(0m, row.Cell(4).GetValue<decimal>()));
    }

    private static void Columns(InlineGlWorkbookBuilder builder) => builder.WithColumns(
        "傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "建立人員", "金額", "借方旗標");

    private static void AddAccount(InlineGlWorkbookBuilder builder, string account, int count, string date = "2025-03-05")
    {
        for (var i = 0; i < count; i++)
            builder.AddRow($"{account}-{date}-{i}", date, account, $"合成{account}", "一般分錄", "U1", "10.00", 1);
    }
}
