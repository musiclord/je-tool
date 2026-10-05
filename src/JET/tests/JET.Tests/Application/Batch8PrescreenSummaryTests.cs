using System.Text.Json;
using ClosedXML.Excel;
using Xunit;

namespace JET.Tests.Application;

public sealed class Batch8PrescreenSummaryTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task LowFrequencyAccountCount_UsesElevenEntryBoundaryAndAuditPeriod(string provider)
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
        Assert.Equal(12, result.GetProperty("lowFrequencyAccount").GetProperty("count").GetInt64());
        Assert.Equal(2, result.GetProperty("rareAccounts").GetProperty("lowFrequencyAccountCount").GetInt64());

        var reopened = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        Assert.Equal(2, reopened.GetProperty("latestRuns").GetProperty("prescreen")
            .GetProperty("rareAccounts").GetProperty("lowFrequencyAccountCount").GetInt64());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task LowFrequencyAccountCount_UsesWholePopulationBeyondFiftyRowSummary(string provider)
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
        Assert.Equal(61, result.GetProperty("lowFrequencyAccount").GetProperty("count").GetInt64());
        Assert.Equal(61, rareAccounts.GetProperty("lowFrequencyAccountCount").GetInt64());
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
        var creators = result.GetProperty("creatorSummary").GetProperty("creators").EnumerateArray().ToArray();
        Assert.Equal(new[] { "", "U1", "（空白）" }, creators.Select(row => row.GetProperty("createdBy").GetString()));
        Assert.Equal(new long[] { 3, 2, 1 }, creators.Select(row => row.GetProperty("entryCount").GetInt64()));
        Assert.Equal(new decimal[] { 30, 20, 10 }, creators.Select(row => row.GetProperty("debitTotal").GetDecimal()));
        Assert.Equal(3, result.GetProperty("concentration").GetProperty("preparers").GetProperty("totalPreparerCount").GetInt64());
        var runId = result.GetProperty("resultRef").GetProperty("runId").GetString();
        var exported = await host.DispatchAsync("export.prescreenReport", JsonSerializer.Serialize(new { runId }));
        Assert.True(exported.GetProperty("ok").GetBoolean());
        using var workbook = new XLWorkbook(exported.GetProperty("artifact").GetProperty("fullPath").GetString()!);
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
