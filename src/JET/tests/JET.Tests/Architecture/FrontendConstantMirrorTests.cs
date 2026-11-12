using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Architecture;

public sealed class FrontendConstantMirrorTests
{
    [Fact]
    public void PrescreenThresholds_HaveSeparateFixedDomainAndFrontendMirrors()
    {
        // 第四步照舊版沒有 11 筆以下的人員與科目兩條規則，後端沒有這兩個門檻；
        // 前端的 preparerMaxEntries 與 accountMaxEntries 只是第五步「依審計目的」兩項的預設，仍是 11。
        // 「分錄金額中有連續 0 的尾數」的 0 的個數照 IDEA 版由 GL 算出，前後端都沒有固定 6 個 0 的常數；
        // 前端讀已確認 GL 配對帶回的個數（trailingZeroDigits）。
        var core = Read("JET", "wwwroot", "js", "ui-core.js");
        var constants = Regex.Match(core, @"var\s+PRESCREEN_DEFAULTS\s*=\s*\{(?<body>.*?)\};", RegexOptions.Singleline);
        Assert.True(constants.Success, "ui-core.js須集中列出各自的既定預篩選門檻。");
        Assert.Equal(11, NumberProperty(constants.Groups["body"].Value, "preparerMaxEntries"));
        Assert.Equal(11, NumberProperty(constants.Groups["body"].Value, "accountMaxEntries"));
        Assert.DoesNotContain("trailingZeroDigits", constants.Groups["body"].Value, StringComparison.Ordinal);
        Assert.Contains("PRESCREEN_DEFAULTS: PRESCREEN_DEFAULTS", core, StringComparison.Ordinal);
        foreach (var key in new[] { "preparerMaxEntries", "accountMaxEntries" })
            Assert.Contains("PRESCREEN_DEFAULTS." + key, core, StringComparison.Ordinal);
        Assert.Contains("committed.zerosThreshold", core, StringComparison.Ordinal);
        Assert.Contains("trailingZeroDigits: trailingZeroDigits", core, StringComparison.Ordinal);
    }

    [Fact]
    public void PrescreenLabels_UseTheAuthorityConstantsAndRetainExactApprovedSentences()
    {
        // 0 的個數由 GL 算出：不知道個數時寫規則名稱，知道時寫「金額尾數連續 N 個 0」。
        Assert.Equal("分錄金額中有連續 0 的尾數", FilterConditionLabels.PrescreenKeys[PrescreenRuleKeys.TrailingZeros]);
        Assert.Equal("金額尾數連續 4 個 0", FilterConditionLabels.PrescreenLabel(PrescreenRuleKeys.TrailingZeros, 4));
        Assert.Equal("空白摘要", FilterConditionLabels.PrescreenLabel(PrescreenRuleKeys.BlankDescription, 4));
        // 編製分錄較少的人員與使用較少的科目兩條規則照舊版不設，標籤與門檻常數都不在登錄裡。
        Assert.DoesNotContain(FilterConditionLabels.PrescreenKeys.Values, label => label.Contains("11 筆以下", StringComparison.Ordinal));
        // 一條規則只有一個定義，所以標籤正本在 Domain PrescreenRuleDefinitions，FilterConditionLabels 由它衍生。
        var source = Read("JET", "Domain", "Rules", "PrescreenRuleDefinitions.cs");
        Assert.DoesNotContain("DefaultZerosThreshold", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DefaultMaxEntries", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PagingLimits_KeepSummaryLimitsSeparateFromTwoHundredRowTables()
    {
        var core = Read("JET", "wwwroot", "js", "ui-core.js");
        // 工作底稿版本紀錄用具名的 REPORT_HISTORY_PAGE_SIZE，固定 50 份；
        // 第六步實際一頁 50 份由前端的報告版本紀錄測試核對。
        foreach (var key in new[] { "RESULT_PREVIEW_PAGE_SIZE", "REPORT_HISTORY_PAGE_SIZE", "VALUE_PROFILE_LIMIT" })
        {
            Assert.Equal(50, NumericConstant(core, key));
            Assert.Contains(key + ": " + key, core, StringComparison.Ordinal);
        }
        // These are actual endpoint defaults, not inferred from a matching-looking frontend literal.
        Assert.Equal(50, HandlerDefault(typeof(MappingValueProfileHandler)));
        // 明細與欄位配對每頁最多 200 筆；驗證摘要及欄位值取樣的 50 筆設定不變。
        Assert.Equal(200, HandlerDefault(typeof(QueryDataPreviewHandler)));
        Assert.Equal(200, NumericConstant(core, "TABLE_PAGE_SIZE"));
        Assert.Contains("TABLE_PAGE_SIZE: TABLE_PAGE_SIZE", core, StringComparison.Ordinal);
        Assert.Equal(50, HandlerDefault(typeof(DevDbTableDataHandler)));
        Assert.Equal(200, PageRequest.DefaultPageSize);
        Assert.Equal(500, PageRequest.MaxPageSize);
        // 開發者表格也明示每頁 200 筆，省略 limit 的後端相容預設仍為 50。
        Assert.Contains("limit: Ui.TABLE_PAGE_SIZE", Read("JET", "wwwroot", "js", "dev-panel.js"), StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyFixedCatalogue_RemainsJsonAndKeepsItsSixZeroExplanation()
    {
        // 目錄放在測試夾具，內容是 JSON 且原文不變。
        using var json = JsonDocument.Parse(Read("tests", "JET.Tests", "Fixtures", "legacy-form-2024-1210.json"));
        var condition = json.RootElement.GetProperty("conditions").EnumerateArray().Single(item => item.GetProperty("letter").GetString() == "D");
        Assert.Equal("金額整數部分以至少 6 個零結尾，整數為零不選取。可另用「金額尾數連續 0 的位數」調整位數。", condition.GetProperty("help").GetString());
        // 尾數條件是一條條件，用「尾數連續多個 0」比較方式；這裡核對這個比較方式。
        // 0 的個數照 IDEA 版由 GL 算出，加入時預設這個個數：常用查法不寫個數，
        // 加入時由 FilterValues.zeroRunDefault 帶入；這份舊目錄是夾具原文，仍寫 6 個零。
        var core = Read("JET", "wwwroot", "js", "ui-core.js");
        Assert.Contains("operator: 'endsWithZeros', value: '', amountBasis: 'absolute'", core, StringComparison.Ordinal);
        Assert.Contains("Ui.FilterValues.zeroRunDefault()", Read("JET", "wwwroot", "js", "steps", "filter-step-sources.js"), StringComparison.Ordinal);
        Assert.Contains("rule.value = zeroRunDefault();", Read("JET", "wwwroot", "js", "filter-values.js"), StringComparison.Ordinal);
    }

    [Fact]
    public void ValidationSummaryPreviewRows_UseTheSameDisplayOnlyLimitForAllProviders()
    {
        // Reflection preserves a compilable first-failure test before introducing this display-only constant.
        var type = typeof(PageRequest).Assembly.GetType("JET.Domain.ResultPreviewLimits");
        Assert.NotNull(type);
        Assert.Equal(50, type.GetField("SummaryRows", BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue());
        Assert.Equal(50, NumericConstant(Read("JET", "wwwroot", "js", "ui-core.js"), "RESULT_PREVIEW_PAGE_SIZE"));
        foreach (var provider in new[] { "Local" })
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
