using System.Text.Json;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 第四步「分類留白 N 筆，視為 Others」提醒（2026-09-07 裁定的輕量版科目配對完善）：
/// 留白列在投影時已落到 Others，篩選不受影響；狀態只回計數，清單走有界分頁。
/// </summary>
public sealed class AccountMappingBlankCategoryTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task BlankCategories_AreCountedAndListed_ButProjectedAsOthers(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("V-1", "2025-01-01", "c", "Cash", "Synthetic", 100, 1)
            .AddRow("V-1", "2025-01-01", "x", "Blank one", "Synthetic", 100, 0)
            .AddRow("V-2", "2025-01-02", "c", "Cash", "Synthetic", 50, 1)
            .AddRow("V-2", "2025-01-02", "y", "Blank two", "Synthetic", 50, 0), databaseProvider: provider,
            validateForDownstream: true);
        var path = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Cell(1, 1).Value = "科目代號"; sheet.Cell(1, 2).Value = "科目名稱"; sheet.Cell(1, 3).Value = "標準化分類";
            sheet.Cell(2, 1).Value = "c"; sheet.Cell(2, 2).Value = "Cash"; sheet.Cell(2, 3).Value = "Cash";
            sheet.Cell(3, 1).Value = "x"; sheet.Cell(3, 2).Value = "Blank one"; sheet.Cell(3, 3).Value = "";
            sheet.Cell(4, 1).Value = "y"; sheet.Cell(4, 2).Value = "Blank two"; sheet.Cell(4, 3).Value = "  ";
        });
        JsonElement imported;
        try { imported = await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new { filePath = path })); }
        finally { TestWorkbookBuilder.Delete(path); }
        Assert.Equal(2, imported.GetProperty("blankCategoryCount").GetInt32());

        var page = await host.DispatchAsync("query.accountMappingBlankPage", JsonSerializer.Serialize(new { pageSize = 1 }));
        Assert.Equal("x", Assert.Single(page.GetProperty("rows").EnumerateArray()).GetProperty("accountCode").GetString());
        var next = await host.DispatchAsync("query.accountMappingBlankPage",
            JsonSerializer.Serialize(new { pageSize = 1, cursor = page.GetProperty("nextCursor").GetString() }));
        var last = Assert.Single(next.GetProperty("rows").EnumerateArray());
        Assert.Equal("y", last.GetProperty("accountCode").GetString());
        Assert.Equal("Blank two", last.GetProperty("accountName").GetString());
        Assert.Equal(JsonValueKind.Null, next.GetProperty("nextCursor").ValueKind);

        // 留白視為 Others：貸方「不屬於 Cash」兩張都命中，沒有待判定。
        var scenario = JsonDocument.Parse("""
          {"name":"Blank as others","rationale":"Synthetic","groups":[{"matchScope":"sameVoucher","rules":[
            {"type":"accountSide","drCr":"debit","categoryMode":"is","categoryIds":["builtin.cash"]},
            {"type":"accountSide","drCr":"credit","categoryMode":"isNot","categoryIds":["builtin.cash"]}]}]}
          """).RootElement;
        var preview = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new { scenario }));
        Assert.Equal(2, preview.GetProperty("scenario").GetProperty("voucherCount").GetInt64());

        await host.DispatchAsync("project.releaseLock");
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        Assert.Equal(2, loaded.GetProperty("importState").GetProperty("accountMapping").GetProperty("blankCategoryCount").GetInt32());
    }
}
