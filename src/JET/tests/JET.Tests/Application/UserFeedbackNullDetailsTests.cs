using System.Text.Json;
using Xunit;

namespace JET.Tests.Application;

public sealed class UserFeedbackNullDetailsTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task SeparateNullDetails_KeepOverlapSortSearchAndIndependentCursors(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow(null, "2025-03-01", "1101", "現金", "A-document-only", "1.00", 1)
            .AddRow("JV-B", "2025-03-01", null, null, "B-account-only", "1.00", 1)
            .AddRow(null, "2025-03-01", null, null, "C-both", "1.00", 1)
            .AddRow("JV-D", "2025-03-01", "1101", "現金", "D-normal", "1.00", 1),
            databaseProvider: provider, validateForDownstream: true);
        foreach (var category in new[] { "nullDocument", "nullAccount" })
        {
            Task<JsonElement> Page(string? cursor = null, string? search = null, string direction = "asc") =>
                host.DispatchAsync("query.nullRecordsPage", JsonSerializer.Serialize(new
                {
                    category, cursor, pageSize = 1, search, sort = new { key = "description", direction }
                }));
            var first = await Page();
            Assert.Equal(category == "nullDocument" ? "A-document-only" : "B-account-only",
                Assert.Single(first.GetProperty("rows").EnumerateArray()).GetProperty("description").GetString());
            var second = await Page(first.GetProperty("nextCursor").GetString());
            Assert.Equal("C-both", Assert.Single(second.GetProperty("rows").EnumerateArray()).GetProperty("description").GetString());
            Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
            var searched = await Page(search: "C-both");
            Assert.Equal("C-both", Assert.Single(searched.GetProperty("rows").EnumerateArray()).GetProperty("description").GetString());
            Assert.Empty((await Page(search: "not-found")).GetProperty("rows").EnumerateArray());
            Assert.Equal("C-both", Assert.Single((await Page(direction: "desc")).GetProperty("rows").EnumerateArray())
                .GetProperty("description").GetString());
        }
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        var counts = loaded.GetProperty("latestRuns").GetProperty("validate").GetProperty("nullRecordsTest");
        Assert.Equal(2, counts.GetProperty("nullDocumentCount").GetInt64());
        Assert.Equal(2, counts.GetProperty("nullAccountCount").GetInt64());
    }
}
