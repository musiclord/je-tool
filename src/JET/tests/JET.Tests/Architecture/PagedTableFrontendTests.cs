using System.Text.RegularExpressions;
using JET.AuditCore;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 明細表排序與搜尋的前端守衛：畫面上每個可排序表頭的鍵名都要在後端該查詢的白名單裡。
/// 排序與搜尋一律送回後端從第一頁重載、前端不自己排序也不自己過濾，
/// 由前端測試以實際送出的請求核對。
/// </summary>
public sealed class PagedTableFrontendTests
{
    private static readonly IReadOnlyDictionary<string, PageSortCatalog> Catalogs = new Dictionary<string, PageSortCatalog>(StringComparer.Ordinal)
    {
        ["query.completenessDiffPage"] = ResultPageSorting.CompletenessDiff,
        ["query.voucherBalancePage"] = ResultPageSorting.VoucherBalance,
        ["query.blankRecordsPage"] = ResultPageSorting.BlankRecords,
        ["query.sourceQualityPage"] = ResultPageSorting.SourceQuality,
        ["query.infSamplePage"] = ResultPageSorting.InfSample,
        ["query.prescreenPage"] = ResultPageSorting.Prescreen,
        ["query.filterHitsPage"] = ResultPageSorting.GlEntryRows,
        ["query.highRiskSummaryVoucherPage"] = ResultPageSorting.HighRiskSummaryVoucher,
        ["query.highRiskSummaryRowPage"] = ResultPageSorting.HighRiskSummaryRow,
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
        Read("js", "steps", "validate-step.js"), ProductSources.FilterStepFrontend());

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

        // 矩陣沒有分錄明細頁；其餘固定表頭逐項保留，不能只降低數量門檻。
        var expectedActions = new[]
        {
            "query.completenessDiffPage", "query.voucherBalancePage", "query.blankRecordsPage",
            "query.sourceQualityPage", "query.prescreenPage", "query.highRiskSummaryVoucherPage", "query.filterVoucherPage"
        };
        Assert.Equal(expectedActions.Order(StringComparer.Ordinal), seen.Keys.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("query.highRiskSummaryRowPage", seen.Keys);
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
