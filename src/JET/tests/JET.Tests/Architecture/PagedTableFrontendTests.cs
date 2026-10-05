using System.Text.RegularExpressions;
using JET.AuditCore;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 明細表排序與搜尋的前端守衛（2026-09-07 工作包 G）：畫面上每個可排序表頭的鍵名都要在後端該查詢的白名單裡；
/// 排序與搜尋一律送回後端從第一頁重載，前端不自己排序、不自己過濾。
/// </summary>
public sealed class PagedTableFrontendTests
{
    private static readonly IReadOnlyDictionary<string, PageSortCatalog> Catalogs = new Dictionary<string, PageSortCatalog>(StringComparer.Ordinal)
    {
        ["query.completenessDiffPage"] = ResultPageSorting.CompletenessDiff,
        ["query.docBalancePage"] = ResultPageSorting.DocBalance,
        ["query.nullRecordsPage"] = ResultPageSorting.NullRecords,
        ["query.sourceQualityPage"] = ResultPageSorting.SourceQuality,
        ["query.infSamplePage"] = ResultPageSorting.InfSample,
        ["query.prescreenPage"] = ResultPageSorting.Prescreen,
        ["query.filterHitsPage"] = ResultPageSorting.GlEntryRows,
        ["query.tagMatrixVoucherPage"] = ResultPageSorting.TagMatrixVoucher,
        ["query.tagMatrixRowPage"] = ResultPageSorting.TagMatrixRow,
        ["query.filterVoucherPage"] = ResultPageSorting.FilterVoucher
    };

    private static string Read(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JET.slnx")))
        {
            directory = directory.Parent;
        }
        var root = directory?.FullName ?? throw new InvalidOperationException("向上尋不到 JET.slnx，無法定位 repo 根。");
        return File.ReadAllText(Path.Combine(new[] { root, "JET", "wwwroot" }.Concat(parts).ToArray()));
    }

    private static string AllFrontend() => string.Join("\n",
        Read("js", "ui-core.js"), Read("js", "filter-vouchers.js"),
        Read("js", "steps", "validate-step.js"), Read("js", "steps", "filter-step.js"));

    [Fact]
    public void EverySortableHeader_UsesAKeyFromTheBackendWhitelist()
    {
        var source = AllFrontend();
        var seen = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        // 兩種寫法：sortableHeadCellsHtml('query.x', [{ key: 'a', ... }]) 與 sortAction: 'query.x', sortKeys: [...]。
        foreach (Match match in Regex.Matches(source, @"sortableHeadCellsHtml\('(?<action>query\.\w+)',\s*\[(?<body>.*?)\]\)", RegexOptions.Singleline))
        {
            Collect(seen, match.Groups["action"].Value, Regex.Matches(match.Groups["body"].Value, @"key:\s*'(?<key>\w+)'").Select(m => m.Groups["key"].Value));
        }
        foreach (Match match in Regex.Matches(source, @"sortAction:\s*'(?<action>query\.\w+)',\s*sortKeys:\s*\[(?<body>[^\]]*)\]"))
        {
            Collect(seen, match.Groups["action"].Value, Regex.Matches(match.Groups["body"].Value, @"'(?<key>\w+)'").Select(m => m.Groups["key"].Value));
        }

        Assert.True(seen.Count >= 8, "至少八個分頁查詢要有可排序表頭：" + string.Join("、", seen.Keys));
        foreach (var (action, keys) in seen)
        {
            Assert.True(Catalogs.TryGetValue(action, out var catalog), $"前端用了沒有排序目錄的查詢 {action}");
            foreach (var key in keys)
            {
                Assert.True(catalog!.Find(key) is not null, $"{action} 的表頭鍵「{key}」不在後端白名單：{string.Join("、", catalog!.Keys)}");
            }
        }
    }

    [Fact]
    public void SortAndSearch_ReloadFromTheBackendInsteadOfSortingInTheBrowser()
    {
        var core = Read("js", "ui-core.js").Replace("\r\n", "\n", StringComparison.Ordinal);
        var bind = core[core.IndexOf("function bindPagedTable(", StringComparison.Ordinal)..];
        bind = bind[..bind.IndexOf("\n  }\n", StringComparison.Ordinal)];

        // 9/23：重試保留原請求快照，不受失敗後退回的可見狀態影響；後端排序契約不變。
        Assert.Contains("sort: state.sort, search: state.search", bind, StringComparison.Ordinal);
        Assert.Contains("opts.fetchPage(reset ? null : state.cursor, requested.sort, requested.search)", bind, StringComparison.Ordinal);
        Assert.Contains("if (!current()) { return; }", bind, StringComparison.Ordinal);
        Assert.Contains("retryRequest = requested", bind, StringComparison.Ordinal);
        Assert.DoesNotContain("setBusy(", bind, StringComparison.Ordinal);
        Assert.Contains("aria-sort", bind, StringComparison.Ordinal);
        // 換排序或搜尋一律帶上一次成功的狀態，重載失敗就退回，表頭與輸入框不會宣稱表格沒有的排序。
        Assert.Contains("fetch(true, '重新排序明細', previous)", bind, StringComparison.Ordinal);
        Assert.Contains("fetch(true, '依號碼查看明細', previous)", bind, StringComparison.Ordinal);
        Assert.Contains("if (previous) { state.sort = previous.sort; state.search = previous.search;", bind, StringComparison.Ordinal);
        Assert.Contains("!table.contains(button) || state.busy) { return; }", bind, StringComparison.Ordinal);
        Assert.DoesNotContain(".sort(", bind, StringComparison.Ordinal);
        Assert.DoesNotContain(".filter(", bind, StringComparison.Ordinal);

        // 每個 query.*Page 的呼叫都把 sort 與 search 送出去；載入更多沿用同一個排序。
        var steps = Read("js", "steps", "validate-step.js") + Read("js", "steps", "filter-step.js") + Read("js", "filter-vouchers.js");
        foreach (var api in new[] { "queryCompletenessDiffPage", "queryDocBalancePage", "queryNullRecordsPage", "querySourceQualityPage",
                     "queryInfSamplePage", "queryPrescreenPage({ ruleKey: key, cursor: cursor", "queryFilterHitsPage", "queryTagMatrixVoucherPage",
                     "queryTagMatrixRowPage", "Api.queryFilterVoucherPage, Object.assign" })
        {
            var index = steps.IndexOf(api, StringComparison.Ordinal);
            Assert.True(index >= 0, $"找不到 {api} 的呼叫");
            var call = steps.Substring(index, Math.Min(260, steps.Length - index));
            Assert.Contains("sort:", call, StringComparison.Ordinal);
            Assert.Contains("search:", call, StringComparison.Ordinal);
        }
    }

    private static void Collect(Dictionary<string, HashSet<string>> seen, string action, IEnumerable<string> keys)
    {
        if (!seen.TryGetValue(action, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            seen[action] = set;
        }
        foreach (var key in keys) set.Add(key);
    }
}
