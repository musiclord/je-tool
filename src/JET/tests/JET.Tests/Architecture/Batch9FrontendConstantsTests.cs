using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Architecture;

public sealed class Batch9FrontendConstantsTests
{
    [Fact]
    public void PrescreenThresholds_HaveSeparateFixedDomainAndFrontendMirrors()
    {
        Assert.Equal(11, PreparerFrequency.DefaultMaxEntries);
        Assert.Equal(11, AccountFrequency.DefaultMaxEntries);
        Assert.Equal(6, TrailingZeroThreshold.DefaultZerosThreshold);
        var core = Read("JET", "wwwroot", "js", "ui-core.js");
        var constants = Regex.Match(core, @"var\s+PRESCREEN_DEFAULTS\s*=\s*\{(?<body>.*?)\};", RegexOptions.Singleline);
        Assert.True(constants.Success, "ui-core.js須集中列出各自的既定預篩選門檻。");
        Assert.Equal(11, NumberProperty(constants.Groups["body"].Value, "preparerMaxEntries"));
        Assert.Equal(11, NumberProperty(constants.Groups["body"].Value, "accountMaxEntries"));
        Assert.Equal(6, NumberProperty(constants.Groups["body"].Value, "trailingZeroDigits"));
        Assert.Contains("PRESCREEN_DEFAULTS: PRESCREEN_DEFAULTS", core, StringComparison.Ordinal);
        foreach (var key in new[] { "preparerMaxEntries", "accountMaxEntries", "trailingZeroDigits" })
            Assert.Contains("PRESCREEN_DEFAULTS." + key, core, StringComparison.Ordinal);
    }

    [Fact]
    public void PrescreenLabels_UseTheAuthorityConstantsAndRetainExactApprovedSentences()
    {
        Assert.Equal("金額尾數連續 6 個 0", FilterConditionLabels.PrescreenKeys[PrescreenRuleKeys.TrailingZeros]);
        Assert.Equal("編製分錄較少的人員（11 筆以下）", FilterConditionLabels.PrescreenKeys[PrescreenRuleKeys.LowFrequencyPreparer]);
        Assert.Equal("使用較少的科目（11 筆以下）", FilterConditionLabels.PrescreenKeys[PrescreenRuleKeys.LowFrequencyAccount]);
        var source = Read("JET", "Domain", "Rules", "FilterConditionLabels.cs");
        Assert.Contains("{TrailingZeroThreshold.DefaultZerosThreshold}", source, StringComparison.Ordinal);
        Assert.Contains("{PreparerFrequency.DefaultMaxEntries}", source, StringComparison.Ordinal);
        Assert.Contains("{AccountFrequency.DefaultMaxEntries}", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PagingFifties_AreNamedByPurposeWithoutChangingTheTwoHundredRowQueryDefault()
    {
        var core = Read("JET", "wwwroot", "js", "ui-core.js");
        // 工作底稿版本紀錄改用具名的 REPORT_HISTORY_PAGE_SIZE，仍固定 50 份；首次失敗：20261004-105344715-0a3f9684995a4a20bcdcb2fb954649f6
        // （原寫在已刪除的 WorkpaperSheetMirrorTests.WorkpaperHistory_DoesNotReplaceCurrentCompletionEvidence，2026-10-14 搬來）。
        // 第六步實際一頁 50 份由 tools/tests/frontend-journey-tables.test.cjs 與 frontend-workflow.test.cjs 的 batch9 report history 測試核對。
        foreach (var key in new[] { "RESULT_PREVIEW_PAGE_SIZE", "REPORT_HISTORY_PAGE_SIZE", "DEV_TABLE_PAGE_SIZE", "VALUE_PROFILE_LIMIT" })
        {
            Assert.Equal(50, NumericConstant(core, key));
            Assert.Contains(key + ": " + key, core, StringComparison.Ordinal);
        }
        // These are actual endpoint defaults, not inferred from a matching-looking frontend literal.
        Assert.Equal(50, HandlerDefault(typeof(MappingValueProfileHandler)));
        Assert.Equal(50, HandlerDefault(typeof(QueryDataPreviewHandler)));
        Assert.Equal(50, HandlerDefault(typeof(DevDbTableDataHandler)));
        Assert.Equal(200, PageRequest.DefaultPageSize);
        Assert.Equal(500, PageRequest.MaxPageSize);
        // 2026-10-14 從 PagingConsumers_UseTheMatchingSharedUiConstant 原樣搬來。開發者面板不是審計員畫面，不寫情境行為測試；
        // 其他畫面的一頁 50 筆已由 tools/tests/frontend-journey-tables.test.cjs 以實際送出的請求測過。
        Assert.Contains("limit: Ui.DEV_TABLE_PAGE_SIZE", Read("JET", "wwwroot", "js", "dev-panel.js"), StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyFixedCatalogue_RemainsJsonAndKeepsItsSixZeroExplanation()
    {
        // 使用者 2026-10-08 要求重新設計自訂篩選條件：目錄從正式前端移到測試夾具，內容仍是 JSON 且原文不變；
        // 新選單的「金額尾數連續多個 0」預設位數直接取同一個六個零的門檻常數。
        using var json = JsonDocument.Parse(Read("tests", "JET.Tests", "Fixtures", "legacy-form-2024-1210.json"));
        var condition = json.RootElement.GetProperty("conditions").EnumerateArray().Single(item => item.GetProperty("letter").GetString() == "D");
        Assert.Equal("金額整數部分以至少 6 個零結尾，整數為零不選取。可另用「金額尾數連續 0 的位數」調整位數。", condition.GetProperty("help").GetString());
        Assert.Equal(6, TrailingZeroThreshold.DefaultZerosThreshold);
        // 第九節 G2 改用欄位尾數與至少一元的子條件組；六位預設仍來自同一常數。
        // 首次失敗：20261014-133317716-754ea282ab3842268400b7daca79b593。
        var core = Read("JET", "wwwroot", "js", "ui-core.js");
        Assert.Contains("operator: 'endsWithDigits', value: '0'.repeat(PRESCREEN_DEFAULTS.trailingZeroDigits)", core, StringComparison.Ordinal);
        Assert.Contains("operator: 'greaterThanOrEqual', value: '1', amountBasis: 'absolute'", core, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidationSummaryPreviewRows_UseTheSameDisplayOnlyLimitForAllProviders()
    {
        // Reflection preserves a compilable first-failure test before introducing this display-only constant.
        var type = typeof(PageRequest).Assembly.GetType("JET.Domain.ResultPreviewLimits");
        Assert.NotNull(type);
        Assert.Equal(50, type.GetField("SummaryRows", BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue());
        Assert.Equal(50, NumericConstant(Read("JET", "wwwroot", "js", "ui-core.js"), "RESULT_PREVIEW_PAGE_SIZE"));
        foreach (var provider in new[] { "Local", "SqlServer" })
        {
            var source = Read("JET", "Infrastructure", "Persistence", provider, provider + "ValidationRunRepository.cs");
            Assert.Contains("new PageRequest(null, ResultPreviewLimits.SummaryRows)", source, StringComparison.Ordinal);
            // Existing completeness, unbalanced-voucher and null-row summaries each have one bounded first page.
            Assert.Equal(4, Regex.Matches(source, @"\bResultPreviewLimits\.SummaryRows\b").Count);
            Assert.DoesNotMatch(@"LIMIT\s+50\b|TOP\s*\(50\)", source);
        }
        Assert.Equal(200, PageRequest.DefaultPageSize);
    }

    private static int HandlerDefault(Type handler) => (int)(handler.GetField("DefaultLimit", BindingFlags.NonPublic | BindingFlags.Static)?.GetRawConstantValue()
        ?? throw new InvalidOperationException(handler.Name + "沒有DefaultLimit常數。"));

    private static int NumericConstant(string source, string name)
    {
        var match = Regex.Match(source, @"\bvar\s+" + Regex.Escape(name) + @"\s*=\s*(?<value>\d+)\s*;");
        Assert.True(match.Success, "找不到共用常數" + name);
        return int.Parse(match.Groups["value"].Value);
    }

    private static int NumberProperty(string source, string name)
    {
        var match = Regex.Match(source, @"\b" + Regex.Escape(name) + @"\s*:\s*(?<value>\d+)\b");
        Assert.True(match.Success, "找不到門檻" + name);
        return int.Parse(match.Groups["value"].Value);
    }

    private static string Read(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JET.slnx"))) directory = directory.Parent;
        return File.ReadAllText(Path.Combine(new[] { directory?.FullName ?? throw new InvalidOperationException("找不到JET.slnx") }.Concat(parts).ToArray()));
    }
}
