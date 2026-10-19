using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class Batch5AccountTreeQueryTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task CategoryQuery_IncludesDescendants_NotRolePeers_AndCombinesSearchWithPaging(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("SYN-1", "2025-01-01", "A100", "現金", "合成", 1, 1)
            .AddRow("SYN-1", "2025-01-01", "A200", "銀行帳戶", "合成", 1, 0)
            .AddRow("SYN-2", "2025-01-01", "A210", "銀行次戶", "合成", 1, 1)
            .AddRow("SYN-2", "2025-01-01", "B300", "銀行樹外同用途", "合成", 1, 0)
            .AddRow("SYN-3", "2025-01-01", "C400", "其他科目", "合成", 1, 1)
            .AddRow("SYN-3", "2025-01-01", "U500", "尚未分類", "合成", 1, 0),
            databaseProvider: provider);

        // 使用既有兩次儲存建立 fixture，讓第一次失敗針對查詢篩選而不是新暫存 ID 契約。
        var rows = Batch5TaxonomyTestSupport.BuiltIns();
        rows.Add(Batch5TaxonomyTestSupport.Item(null, "合成銀行", 5, "cash", "builtin.cash"));
        rows.Add(Batch5TaxonomyTestSupport.Item(null, "合成下層其他用途", 6, "others"));
        rows.Add(Batch5TaxonomyTestSupport.Item(null, "樹外相同用途", 7, "cash"));
        var saved = await Batch5TaxonomyTestSupport.SaveAsync(host, 1, rows);
        var bank = Batch5TaxonomyTestSupport.Category(saved, "合成銀行").GetProperty("categoryId").GetString()!;
        var leaf = Batch5TaxonomyTestSupport.Category(saved, "合成下層其他用途").GetProperty("categoryId").GetString()!;
        rows = JsonNode.Parse(saved.GetProperty("categories").GetRawText())!.AsArray();
        rows[6]!["parentCategoryId"] = bank;
        await Batch5TaxonomyTestSupport.SaveAsync(host, 2, rows);
        await ImportMappingAsync(host,
        [
            ("A100", "現金", "Cash"),
            ("A200", "銀行帳戶", "合成銀行"),
            ("A210", "銀行次戶", "合成下層其他用途"),
            ("B300", "銀行樹外同用途", "樹外相同用途"),
            ("C400", "其他科目", "Others")
        ]);

        var first = await QueryAsync(host, "builtin.cash", pageSize: 2);
        Assert.Equal(new[] { "A100", "A200" }, Codes(first));
        Assert.False(string.IsNullOrEmpty(first.GetProperty("nextCursor").GetString()));
        var second = await QueryAsync(host, "builtin.cash", pageSize: 2,
            cursor: first.GetProperty("nextCursor").GetString());
        Assert.Equal(new[] { "A210" }, Codes(second));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
        Assert.Equal(leaf, second.GetProperty("rows")[0].GetProperty("categoryId").GetString());

        Assert.Equal(new[] { "A200", "A210" }, Codes(await QueryAsync(host, bank)));
        Assert.Equal(new[] { "A210" }, Codes(await QueryAsync(host, leaf)));
        var searchFirst = await QueryAsync(host, "builtin.cash", "銀行", 1);
        Assert.Equal(new[] { "A200" }, Codes(searchFirst));
        var searchSecond = await QueryAsync(host, "builtin.cash", "銀行", 1,
            searchFirst.GetProperty("nextCursor").GetString());
        Assert.Equal(new[] { "A210" }, Codes(searchSecond));
        Assert.Equal(JsonValueKind.Null, searchSecond.GetProperty("nextCursor").ValueKind);
        Assert.Empty(Codes(await QueryAsync(host, "builtin.cash", "樹外")));
        Assert.Equal(new[] { "A210" }, Codes(await QueryAsync(host, "builtin.cash", "a21")));

        // 省略分類時保留原完整科目清單，未配對的科目也不被隱藏。
        Assert.Equal(new[] { "A100", "A200", "A210", "B300", "C400", "U500" }, Codes(await QueryAsync(host, null)));
        await host.DispatchAsync("project.releaseLock");
        await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        Assert.Equal(new[] { "A200", "A210" }, Codes(await QueryAsync(host, bank)));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task CategoryQuery_ReportsMoreThan100_AndKeeps200RowPageLimit(string provider)
    {
        using var host = new HandlerTestHost();
        await Batch5TaxonomyTestSupport.CreateProjectAsync(host, provider);
        var accounts = Enumerable.Range(1, 501)
            .Select(number => (Code: $"C{number:D3}", Name: $"合成現金 {number:D3}", Category: "Cash"))
            .Append(("Z999", "樹外科目", "Others")).ToArray();
        await ImportMappingAsync(host, accounts);

        var selection = await QueryAsync(host, "builtin.cash", pageSize: 100);
        Assert.Equal(Enumerable.Range(1, 100).Select(number => $"C{number:D3}"), Codes(selection));
        Assert.False(string.IsNullOrEmpty(selection.GetProperty("nextCursor").GetString()));
        // 使用者 2026-10-16 統一每頁最多 200 筆；仍逐頁核對全部 501 筆，不縮小原有合成母體。
        // 第一次失敗：20261016-094952219-d2e1f780f92649c4a72a3f03c4b46396。
        var remaining = await QueryAsync(host, "builtin.cash", pageSize: 500,
            cursor: selection.GetProperty("nextCursor").GetString());
        Assert.Equal(Enumerable.Range(101, 200).Select(number => $"C{number:D3}"), Codes(remaining));
        Assert.False(string.IsNullOrEmpty(remaining.GetProperty("nextCursor").GetString()));
        var remainingSecond = await QueryAsync(host, "builtin.cash", pageSize: 500,
            cursor: remaining.GetProperty("nextCursor").GetString());
        Assert.Equal(Enumerable.Range(301, 200).Select(number => $"C{number:D3}"), Codes(remainingSecond));
        Assert.False(string.IsNullOrEmpty(remainingSecond.GetProperty("nextCursor").GetString()));
        var remainingLast = await QueryAsync(host, "builtin.cash", pageSize: 500,
            cursor: remainingSecond.GetProperty("nextCursor").GetString());
        Assert.Equal(new[] { "C501" }, Codes(remainingLast));
        Assert.Equal(JsonValueKind.Null, remainingLast.GetProperty("nextCursor").ValueKind);

        var bounded = await QueryAsync(host, "builtin.cash", pageSize: 5000);
        Assert.Equal(Enumerable.Range(1, 200).Select(number => $"C{number:D3}"), Codes(bounded));
        Assert.False(string.IsNullOrEmpty(bounded.GetProperty("nextCursor").GetString()));
        var middle = await QueryAsync(host, "builtin.cash", pageSize: 5000,
            cursor: bounded.GetProperty("nextCursor").GetString());
        Assert.Equal(Enumerable.Range(201, 200).Select(number => $"C{number:D3}"), Codes(middle));
        Assert.False(string.IsNullOrEmpty(middle.GetProperty("nextCursor").GetString()));
        var last = await QueryAsync(host, "builtin.cash", pageSize: 5000,
            cursor: middle.GetProperty("nextCursor").GetString());
        Assert.Equal(Enumerable.Range(401, 101).Select(number => $"C{number:D3}"), Codes(last));
        Assert.Equal(JsonValueKind.Null, last.GetProperty("nextCursor").ValueKind);
        // 總筆數只在第一頁計算，續頁回 null，畫面沿用第一頁的總頁數（使用者 2026-10-16 要求處理每頁重算總筆數）。
        // 第一次失敗：20261016-110107604-643d3eac8ee8476fafdf625540660e37。
        foreach (var page in new[] { selection, bounded })
            Assert.Equal(501, page.GetProperty("totalCount").GetInt64());
        foreach (var page in new[] { remaining, remainingSecond, remainingLast, middle, last })
            Assert.Equal(JsonValueKind.Null, page.GetProperty("totalCount").ValueKind);
        var outside = await QueryAsync(host, "builtin.others");
        Assert.Equal(new[] { "Z999" }, Codes(outside));
        Assert.Equal(1, outside.GetProperty("totalCount").GetInt64());
    }

    private static Task<JsonElement> QueryAsync(
        HandlerTestHost host, string? categoryId, string? search = null, int pageSize = 100, string? cursor = null) =>
        host.DispatchAsync("query.accountMappingPage", JsonSerializer.Serialize(new { categoryId, search, pageSize, cursor }));

    private static string[] Codes(JsonElement page) => page.GetProperty("rows").EnumerateArray()
        .Select(row => row.GetProperty("accountCode").GetString()!).ToArray();

    private static async Task ImportMappingAsync(
        HandlerTestHost host, IReadOnlyList<(string Code, string Name, string Category)> accounts)
    {
        var path = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Cell(1, 1).Value = "科目代號";
            sheet.Cell(1, 2).Value = "科目名稱";
            sheet.Cell(1, 3).Value = "分類";
            for (var index = 0; index < accounts.Count; index++)
            {
                sheet.Cell(index + 2, 1).Value = accounts[index].Code;
                sheet.Cell(index + 2, 2).Value = accounts[index].Name;
                sheet.Cell(index + 2, 3).Value = accounts[index].Category;
            }
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
