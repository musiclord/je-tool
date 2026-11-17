using System.Text.Json;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 傳票清單的第一頁要帶資料庫條件式，草稿與已儲存情境都一樣；換頁與展開分錄不重送。資料是合成的。
/// </summary>
public sealed class FilterConditionSqlActionTests
{
    [Fact]
    public async Task VoucherPage_ReturnsConditionSqlForDraftAndSavedScenario()
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票項次", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("V1", "1", "2025-03-03", "A", "合成 A", "甲部門費用報銷", 10, 1)
            .AddRow("V1", "2", "2025-03-03", "B", "合成 B", "其他合成摘要", 10, 0)
            .AddRow("V2", "1", "2025-03-04", "A", "合成 A", null, 20, 1)
            .AddRow("V2", "2", "2025-03-04", "B", "合成 B", "乙部門費用報銷", 20, 0),
            validateForDownstream: true);
        var scenario = JsonElement.Parse("""
            {"name":"摘要排除指定文字","rationale":"合成資料","groups":[{"conditions":[
            {"type":"fieldValue","field":"description","operator":"notIn","values":["甲部門費用報銷","乙部門費用報銷"],"includeBlank":true}]}]}
            """);

        var draft = await host.DispatchAsync("query.filterVoucherPage", JsonSerializer.Serialize(new { scenario }));
        var draftSql = draft.GetProperty("conditionSql").GetString()!;
        Assert.StartsWith("FROM gl g\nWHERE g.is_excluded = 0\n  AND (", draftSql, StringComparison.Ordinal);
        Assert.Contains("UPPER(TRIM([摘要])) NOT IN ('甲部門費用報銷', '乙部門費用報銷')", draftSql, StringComparison.Ordinal);
        Assert.Equal(["V1", "V2"], draft.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("voucherNumber").GetString()).Order());

        // 展開分錄的頁面不重送條件式。
        var detail = await host.DispatchAsync("query.filterVoucherRowsPage", JsonSerializer.Serialize(new
        { scenario, voucherNumber = "V1", queryRevision = draft.GetProperty("queryRevision").GetString() }));
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("conditionSql").ValueKind);

        var committed = await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new { scenarios = new[] { scenario } }));
        var saved = await host.DispatchAsync("query.filterVoucherPage", JsonSerializer.Serialize(new
        { scenarioPosition = 1, scenarioRevision = committed.GetProperty("resultRef").GetProperty("revision").GetString() }));
        Assert.Equal(draftSql, saved.GetProperty("conditionSql").GetString());
    }
}
