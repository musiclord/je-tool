using System.Text.Json;
using Xunit;

namespace JET.Tests.Application;

public sealed class DetailPaginationContractTests
{
    [Theory]
    [InlineData("sqlite", 201)]
    [InlineData("sqlite", 401)]
    [InlineData("duckdb", 201)]
    [InlineData("duckdb", 401)]
    public async Task PreviewAndResultPages_AreBoundedAndCountTheWholeQuery(string provider, int count)
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder =>
        {
            builder.WithColumns("傳票號碼", "傳票日期", "核准日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標");
            for (var i = 1; i <= count; i++)
                builder.AddRow(Document(i), "2025-01-01", "2026-01-01", $"A{i:0000}",
                    i <= 201 ? "GroupOne" : "GroupTwo", "Synthetic", i, 1);
        }, databaseProvider: provider);

        // The independent fixture order is also the expected order for offset previews.
        foreach (var dataset in new[] { "pbcGl", "gl" })
        {
            for (var offset = 0; offset < count; offset += 200)
            {
                var preview = await host.DispatchAsync("query.dataPreview", JsonSerializer.Serialize(new { dataset, offset, limit = 9999 }));
                var rows = preview.GetProperty("rows").EnumerateArray().ToArray();
                // 總筆數與統計只在第一頁（offset 0）計算，換頁沿用第一頁的值，不再重掃完整資料（使用者 2026-10-16 要求處理每頁重算總筆數）。
                if (offset == 0)
                {
                    Assert.Equal(count, preview.GetProperty("totalCount").GetInt64());
                    if (dataset == "gl") Assert.Equal(JsonValueKind.Object, preview.GetProperty("stats").ValueKind);
                }
                else
                {
                    Assert.Equal(JsonValueKind.Null, preview.GetProperty("totalCount").ValueKind);
                    Assert.Equal(JsonValueKind.Null, preview.GetProperty("stats").ValueKind);
                }
                Assert.Equal(Math.Min(200, count - offset), rows.Length);
                Assert.Equal(Enumerable.Range(offset + 1, rows.Length).Select(Document), rows.Select(row => row[0].GetString()));
            }
            var beyond = await host.DispatchAsync("query.dataPreview", JsonSerializer.Serialize(new { dataset, offset = count }));
            Assert.Equal(JsonValueKind.Null, beyond.GetProperty("totalCount").ValueKind);
            Assert.Empty(beyond.GetProperty("rows").EnumerateArray());
        }
        var defaults = await host.DispatchAsync("query.dataPreview", """{"dataset":"pbcGl"}""");
        Assert.Equal(200, defaults.GetProperty("rows").GetArrayLength());

        var unsorted = await WalkAsync(host, "query.nullRecordsPage", new() { ["category"] = "outOfRangeDate" }, count);
        Assert.Equal(Enumerable.Range(1, count).Select(Document), unsorted.Select(row => row.GetProperty("documentNumber").GetString()));
        var sorted = await WalkAsync(host, "query.docBalancePage", new()
        {
            ["sort"] = new { key = "diff", direction = "desc" }
        }, count);
        Assert.Equal(Enumerable.Range(1, count).Reverse().Select(i => (decimal)i), sorted.Select(row => row.GetProperty("diff").GetDecimal()));
        var searched = await WalkAsync(host, "query.docBalancePage", new()
        {
            ["sort"] = new { key = "diff", direction = "desc" }, ["search"] = "keep"
        }, 201);
        Assert.Equal(Enumerable.Range(1, 201).Reverse().Select(Document), searched.Select(row => row.GetProperty("documentNumber").GetString()));
        var noMatches = await WalkAsync(host, "query.docBalancePage", new() { ["search"] = "absent" }, 0);
        Assert.Empty(noMatches);

        var accounts = await WalkAsync(host, "query.accountMappingPage", new() { ["search"] = "groupone" }, 201);
        Assert.Equal(Enumerable.Range(1, 201).Select(i => $"A{i:0000}"), accounts.Select(row => row.GetProperty("accountCode").GetString()));
        await host.DispatchAsync("accountMapping.save", """{"changes":[{"accountCode":"A0001","categoryId":"builtin.cash"},{"accountCode":"A0002","categoryId":"builtin.cash"}]}""");
        var category = await WalkAsync(host, "query.accountMappingPage", new() { ["categoryId"] = "builtin.cash", ["search"] = "groupone", ["pageSize"] = 1 }, 2);
        Assert.Equal(new[] { "A0001", "A0002" }, category.Select(row => row.GetProperty("accountCode").GetString()));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task EveryResultPage_CountsOnItsFirstPageAndWalksTheWholeQuery(string provider)
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        var committed = await host.DispatchAsync("filter.commit", """
            {"scenarios":[{"name":"Synthetic","rationale":"Synthetic","groups":[{"join":"and","rules":[{"join":"and","type":"prescreen","prescreenKey":"nonAuthorizedPreparer"}]}]}]}
            """);
        var revision = committed.GetProperty("resultRef").GetProperty("revision").GetString();
        var queries = new (string Action, Dictionary<string, object?> Payload)[]
        {
            ("query.completenessDiffPage", new()),
            ("query.sourceQualityPage", new()),
            ("query.infSamplePage", new()),
            ("query.prescreenPage", new() { ["ruleKey"] = "nonAuthorizedPreparer" }),
            ("query.filterHitsPage", new() { ["scenarioPosition"] = 1 }),
            ("query.tagMatrixVoucherPage", new()),
            ("query.tagMatrixRowPage", new()),
            ("query.accountMappingBlankPage", new()),
            ("query.accountMappingDifferencePage", new() { ["kind"] = "unmapped" }),
            ("query.filterVoucherPage", new() { ["scenarioPosition"] = 1, ["scenarioRevision"] = revision, ["populationScope"] = "auditPeriod" })
        };
        foreach (var (action, payload) in queries)
        {
            payload["pageSize"] = 7;
            var first = await host.DispatchAsync(action, JsonSerializer.Serialize(payload));
            var expected = first.GetProperty("totalCount").GetInt64();
            var all = await WalkAsync(host, action, payload, expected);
            Assert.Equal(expected, all.Count);
        }
        var vouchers = await host.DispatchAsync("query.filterVoucherPage", JsonSerializer.Serialize(new
        {
            scenarioPosition = 1, scenarioRevision = revision, populationScope = "auditPeriod", pageSize = 1
        }));
        var voucher = Assert.Single(vouchers.GetProperty("rows").EnumerateArray());
        var details = await WalkAsync(host, "query.filterVoucherRowsPage", new()
        {
            ["scenarioPosition"] = 1, ["scenarioRevision"] = revision, ["populationScope"] = "auditPeriod",
            ["queryRevision"] = vouchers.GetProperty("queryRevision").GetString(),
            ["documentNumber"] = voucher.GetProperty("documentNumber").GetString(), ["pageSize"] = 1
        }, voucher.GetProperty("totalRowCount").GetInt64());
        Assert.NotEmpty(details);
    }

    private static string Document(int i) => $"{(i <= 201 ? "KEEP" : "OTHER")}-{i:0000}";

    private static async Task<List<JsonElement>> WalkAsync(HandlerTestHost host, string action,
        Dictionary<string, object?> payload, long expectedCount)
    {
        var result = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var request = new Dictionary<string, object?>(payload) { ["cursor"] = cursor };
            if (!request.ContainsKey("pageSize")) request["pageSize"] = 9999;
            var page = await host.DispatchAsync(action, JsonSerializer.Serialize(request));
            var rows = page.GetProperty("rows").EnumerateArray().ToArray();
            Assert.InRange(rows.Length, 0, 200);
            // 第一頁回完整查詢範圍的總筆數；帶游標的續頁不再計數，回 null。
            if (cursor is null) Assert.Equal(expectedCount, page.GetProperty("totalCount").GetInt64());
            else Assert.Equal(JsonValueKind.Null, page.GetProperty("totalCount").ValueKind);
            result.AddRange(rows);
            cursor = page.GetProperty("nextCursor").GetString();
            Assert.True(cursor is null || rows.Length > 0);
            Assert.True(result.Count <= expectedCount);
        } while (cursor is not null);
        Assert.Equal(expectedCount, result.Count);
        return result;
    }
}
