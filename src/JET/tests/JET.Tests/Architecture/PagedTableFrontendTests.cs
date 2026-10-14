using System.Text.RegularExpressions;
using JET.AuditCore;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 明細表排序與搜尋的前端守衛（2026-09-07 工作包 G）：畫面上每個可排序表頭的鍵名都要在後端該查詢的白名單裡。
/// 排序與搜尋一律送回後端從第一頁重載、前端不自己排序也不自己過濾，2026-10-14 起改由
/// <c>tools/tests/frontend-journey-tables.test.cjs</c> 以實際送出的請求核對。
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
