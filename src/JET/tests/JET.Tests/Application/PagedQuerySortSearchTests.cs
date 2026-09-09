using System.Text.Json;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 各明細表分頁查詢的排序與搜尋走訪（2026-09-07 工作包 G）。每個查詢用同一套檢查：
/// 不排序、升冪、降冪三種走訪筆數相同；排序後相鄰兩列依鍵單調、空值一律在最後；
/// 依傳票號碼或科目編號搜尋時，每一列都含該文字，且筆數等於不搜尋時符合的筆數。
/// 排序鍵用 wire row 的欄位名，錯的鍵要被擋下並列出允許值。
/// </summary>
public sealed class PagedQuerySortSearchTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string ScenarioPayload() =>
        JsonSerializer.Serialize(new
        {
            scenarios = new[] { new {
                name = "提前過帳", rationale = "test",
                groups = new[] { new { join = "and", rules = new[] {
                    new { join = "and", type = "prescreen", prescreenKey = "backdatedPosting" } } } } } }
        });

    private static async Task<List<JsonElement>> WalkAsync(
        HandlerTestHost host, string action, Dictionary<string, object?> payload, int pageSize,
        (string Key, string Direction)? sort = null, string? search = null)
    {
        var rows = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var request = new Dictionary<string, object?>(payload) { ["cursor"] = cursor, ["pageSize"] = pageSize };
            if (sort is { } s) request["sort"] = new { key = s.Key, direction = s.Direction };
            if (search is not null) request["search"] = search;
            var page = await host.DispatchAsync(action, JsonSerializer.Serialize(request, Web));
            rows.AddRange(page.GetProperty("rows").EnumerateArray().Select(static row => row.Clone()));
            var next = page.GetProperty("nextCursor");
            cursor = next.ValueKind == JsonValueKind.Null ? null : next.GetString();
        } while (cursor is not null);

        return rows;
    }

    private static IComparable? Value(JsonElement row, string key)
    {
        var element = row.GetProperty(key);
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetDecimal(),
            JsonValueKind.True => 1m,
            JsonValueKind.False => 0m,
            _ => throw new InvalidOperationException($"排序鍵 {key} 的值型別不可比較：{element.ValueKind}")
        };
    }

    private static void AssertMonotonic(IReadOnlyList<JsonElement> rows, string key, bool descending)
    {
        for (var index = 1; index < rows.Count; index++)
        {
            var previous = Value(rows[index - 1], key);
            var current = Value(rows[index], key);
            if (previous is null)
            {
                Assert.Null(current); // 空值在最後，後面不能再出現有值的列
                continue;
            }

            if (current is null) continue;
            var comparison = previous is string left
                ? string.CompareOrdinal(left, (string)current)
                : ((decimal)previous).CompareTo((decimal)current);
            Assert.True(descending ? comparison >= 0 : comparison <= 0,
                $"{key} 第 {index} 列順序錯誤：{previous} 之後是 {current}");
        }
    }

    private static async Task AssertSortAndSearchAsync(
        HandlerTestHost host, string action, Dictionary<string, object?> payload, string sortKey, string searchField, int pageSize)
    {
        var plain = await WalkAsync(host, action, payload, pageSize);
        var ascending = await WalkAsync(host, action, payload, pageSize, (sortKey, "asc"));
        var descending = await WalkAsync(host, action, payload, pageSize, (sortKey, "desc"));

        Assert.Equal(plain.Count, ascending.Count);
        Assert.Equal(plain.Count, descending.Count);
        AssertMonotonic(ascending, sortKey, descending: false);
        AssertMonotonic(descending, sortKey, descending: true);

        if (plain.Count == 0) return;
        var sample = plain.Select(row => row.GetProperty(searchField).GetString()).First(static value => !string.IsNullOrEmpty(value))!;
        var needle = sample.Length > 3 ? sample[1..3] : sample;
        var expected = plain.Count(row =>
            (row.GetProperty(searchField).GetString() ?? string.Empty).Contains(needle, StringComparison.OrdinalIgnoreCase));
        var searched = await WalkAsync(host, action, payload, pageSize, search: needle.ToLowerInvariant());
        Assert.Equal(expected, searched.Count);
        Assert.All(searched, row => Assert.Contains(needle, row.GetProperty(searchField).GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        Assert.True(searched.Count > 0);
    }

    [Fact]
    public async Task ValidationTables_SortAndSearch()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);

        await AssertSortAndSearchAsync(host, "query.docBalancePage", [], "diff", "documentNumber", 5);
        await AssertSortAndSearchAsync(host, "query.docBalancePage", [], "documentNumber", "documentNumber", 5);
        await AssertSortAndSearchAsync(host, "query.completenessDiffPage", [], "diff", "accountCode", 5);
        await AssertSortAndSearchAsync(host, "query.completenessDiffPage", [], "accountName", "accountCode", 5);
        await AssertSortAndSearchAsync(host, "query.nullRecordsPage", new() { ["category"] = "nullDescription" }, "postDate", "documentNumber", 5);
        await AssertSortAndSearchAsync(host, "query.infSamplePage", [], "approvedBy", "documentNumber", 7);
        await AssertSortAndSearchAsync(host, "query.infSamplePage", [], "debit", "documentNumber", 7);
        await AssertSortAndSearchAsync(host, "query.prescreenPage", new() { ["ruleKey"] = "backdatedPosting" }, "amount", "documentNumber", 3);
        await AssertSortAndSearchAsync(host, "query.prescreenPage", new() { ["ruleKey"] = "backdatedPosting" }, "documentDescription", "documentNumber", 3);

        // 來源品質在 demo 母體可能沒有列；只驗證排序參數被接受且走訪一致。
        var plain = await WalkAsync(host, "query.sourceQualityPage", [], 5);
        var sorted = await WalkAsync(host, "query.sourceQualityPage", [], 5, ("sourceRowNumber", "desc"));
        Assert.Equal(plain.Count, sorted.Count);
    }

    [Fact]
    public async Task FilterTables_SortAndSearch()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);
        var committed = await host.DispatchAsync("filter.commit", ScenarioPayload());
        var revision = committed.GetProperty("resultRef").GetProperty("revision").GetString();

        await AssertSortAndSearchAsync(host, "query.filterHitsPage", new() { ["scenarioPosition"] = 1 }, "amount", "documentNumber", 3);
        await AssertSortAndSearchAsync(host, "query.filterHitsPage", new() { ["scenarioPosition"] = 1 }, "postDate", "documentNumber", 3);
        await AssertSortAndSearchAsync(host, "query.tagMatrixVoucherPage", [], "voucherTotal", "documentNumber", 3);
        await AssertSortAndSearchAsync(host, "query.tagMatrixVoucherPage", [], "createdBy", "documentNumber", 3);
        await AssertSortAndSearchAsync(host, "query.tagMatrixRowPage", [], "approvedBy", "documentNumber", 4);
        await AssertSortAndSearchAsync(host, "query.tagMatrixRowPage", [], "amount", "documentNumber", 4);

        var voucherPayload = new Dictionary<string, object?>
        {
            ["scenarioPosition"] = 1, ["scenarioRevision"] = revision, ["populationScope"] = "auditPeriod"
        };
        await AssertSortAndSearchAsync(host, "query.filterVoucherPage", voucherPayload, "totalRowCount", "documentNumber", 3);
        await AssertSortAndSearchAsync(host, "query.filterVoucherPage", voucherPayload, "postDate", "documentNumber", 3);
    }

    [Fact]
    public async Task InvalidSortOrSearch_AreRejectedWithGuidance()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);

        var badKey = await Assert.ThrowsAsync<JET.Domain.JetActionException>(() => host.DispatchAsync("query.docBalancePage",
            JsonSerializer.Serialize(new { sort = new { key = "document_number; DROP TABLE x", direction = "asc" } })));
        Assert.Equal("invalid_payload", badKey.Code);
        Assert.Contains("documentNumber、debit、credit、diff", badKey.Message, StringComparison.Ordinal);

        var badDirection = await Assert.ThrowsAsync<JET.Domain.JetActionException>(() => host.DispatchAsync("query.docBalancePage",
            JsonSerializer.Serialize(new { sort = new { key = "diff", direction = "sideways" } })));
        Assert.Equal("invalid_payload", badDirection.Code);

        var tooLong = await Assert.ThrowsAsync<JET.Domain.JetActionException>(() => host.DispatchAsync("query.docBalancePage",
            JsonSerializer.Serialize(new { search = new string('x', 201) })));
        Assert.Equal("invalid_payload", tooLong.Code);

        // 換了排序卻沿用舊游標：明確擋下，不靜默回到第一頁。demo 的提前過帳命中超過一頁，游標一定存在。
        var first = await host.DispatchAsync("query.prescreenPage",
            JsonSerializer.Serialize(new { ruleKey = "backdatedPosting", pageSize = 1, sort = new { key = "amount", direction = "asc" } }));
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));
        var mismatch = await Assert.ThrowsAsync<JET.Domain.JetActionException>(() => host.DispatchAsync("query.prescreenPage",
            JsonSerializer.Serialize(new { ruleKey = "backdatedPosting", pageSize = 1, cursor, sort = new { key = "postDate", direction = "asc" } })));
        Assert.Equal("invalid_payload", mismatch.Code);
        Assert.Contains("從第一頁重新載入", mismatch.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DynamicColumns_MarkSortableFixedColumns()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("filter.commit", ScenarioPayload());

        var page = await host.DispatchAsync("query.filterHitsPage", JsonSerializer.Serialize(new { scenarioPosition = 1, pageSize = 1 }));
        var columns = page.GetProperty("columns").EnumerateArray().ToArray();
        Assert.All(columns.Where(static column => !column.GetProperty("isCustom").GetBoolean()),
            column => Assert.True(column.GetProperty("sortable").GetBoolean(), column.GetProperty("key").GetString()));

        var inf = await host.DispatchAsync("query.infSamplePage", JsonSerializer.Serialize(new { pageSize = 1 }));
        Assert.All(inf.GetProperty("columns").EnumerateArray().Where(static column => !column.GetProperty("isCustom").GetBoolean()),
            column => Assert.True(column.GetProperty("sortable").GetBoolean(), column.GetProperty("key").GetString()));
    }
}
