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
    }

    [Fact]
    public void PagingConsumers_UseTheMatchingSharedUiConstant()
    {
        foreach (var path in new[] { new[] { "steps", "filter-step.js" }, new[] { "filter-vouchers.js" }, new[] { "steps", "validate-step.js" } })
        {
            var source = Read(new[] { "JET", "wwwroot", "js" }.Concat(path).ToArray());
            Assert.Contains("pageSize: Ui.RESULT_PREVIEW_PAGE_SIZE", source, StringComparison.Ordinal);
            Assert.DoesNotMatch(@"pageSize\s*:\s*50\b", source);
        }
        Assert.Contains("HISTORY_PAGE_SIZE = Ui.REPORT_HISTORY_PAGE_SIZE", Read("JET", "wwwroot", "js", "steps", "export-step.js"), StringComparison.Ordinal);
        Assert.Contains("limit: Ui.DEV_TABLE_PAGE_SIZE", Read("JET", "wwwroot", "js", "dev-panel.js"), StringComparison.Ordinal);
        Assert.Contains("limit: Ui.VALUE_PROFILE_LIMIT", Read("JET", "wwwroot", "js", "steps", "mapping-step.js"), StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyFixedCatalogue_RemainsJsonAndKeepsItsSixZeroExplanation()
    {
        var source = Read("JET", "wwwroot", "js", "filter-legacy.js");
        const string startMarker = "/* legacy-catalog:start */";
        var start = source.IndexOf(startMarker, StringComparison.Ordinal) + startMarker.Length;
        var end = source.IndexOf("/* legacy-catalog:end */", start, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(source[start..end]);
        var condition = json.RootElement.GetProperty("conditions").EnumerateArray().Single(item => item.GetProperty("letter").GetString() == "D");
        Assert.Equal("金額整數部分以至少 6 個零結尾，整數為零不選取。可另用「金額尾數連續 0 的位數」調整位數。", condition.GetProperty("help").GetString());
        Assert.Equal(6, TrailingZeroThreshold.DefaultZerosThreshold);
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
