using System.Text.Json;
using ClosedXML.Excel;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 2026-10-05 最後獨立複審 V9：第四步「依分錄編製者彙總」的人數原本用最多 50 列的清單長度，超過 50 位時顯示「50 位人員」；
/// 預篩選報告的摘要列也一樣。改成後端的完整人數，和流程總覽的「全部 N 位編製人員」同一個數字。
/// 依 R6，空白人員列成一組，所以也算一位。人員比對去頭尾空白、不分大小寫（C3）。
/// </summary>
public sealed class PreparerCountTests
{
    private static void Columns(InlineGlWorkbookBuilder builder) => builder.WithColumns(
        "傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "建立人員", "金額", "借方旗標");

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task MoreThanFiftyPreparers_ScreenAndReportUseTheFullCount_IncludingTheBlankGroup(string provider)
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder =>
        {
            Columns(builder);
            for (var i = 0; i < 60; i++)
            {
                builder.AddRow($"V{i:00}", "2025-03-05", "A", "合成科目", "一般分錄", $"P{i:00}", "10.00", 1);
            }

            // 空白人員一組；「 p00 」和 P00 是同一位。所以 60 位具名人員加空白一組，共 61。
            builder.AddRow("VB", "2025-03-05", "A", "合成科目", "一般分錄", null, "10.00", 1)
                .AddRow("VC", "2025-03-05", "A", "合成科目", "一般分錄", " p00 ", "10.00", 1);
        }, databaseProvider: provider, validateForDownstream: true);

        var result = await host.DispatchAsync("prescreen.run");
        var summary = result.GetProperty("creatorSummary");
        Assert.Equal(50, summary.GetProperty("creators").GetArrayLength());
        Assert.Equal(61, summary.GetProperty("totalPreparerCount").GetInt64());
        Assert.Equal(61, result.GetProperty("concentration").GetProperty("preparers").GetProperty("totalPreparerCount").GetInt64());

        var runId = result.GetProperty("resultRef").GetProperty("runId").GetString();
        var exported = await host.DispatchAsync("export.prescreenReport", JsonSerializer.Serialize(new { runId }));
        using var workbook = new XLWorkbook(exported.GetProperty("artifact").GetProperty("fullPath").GetString()!);
        Assert.Equal("彙總 61 項；詳 [R5] 工作表", workbook.Worksheet("Pre-screening_Report").Cell("D12").GetString());
        Assert.Equal(61, workbook.Worksheet("R5").RowsUsed().Skip(1).Count());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task PreparerFieldNotMapped_FullCountIsNullNotZero(string provider)
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("V1", "2025-03-05", "A", "合成科目", "一般分錄", "10.00", 1),
            databaseProvider: provider, validateForDownstream: true);

        var summary = (await host.DispatchAsync("prescreen.run")).GetProperty("creatorSummary");
        Assert.Equal(JsonValueKind.String, summary.GetProperty("naReason").ValueKind);
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("totalPreparerCount").ValueKind);
    }
}
