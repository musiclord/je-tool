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
        // 第四步照舊版拿掉 11 筆以下的人員與科目兩條規則後，後端沒有這兩個門檻（裁定 2026-10-29-27）；
        // 前端的 preparerMaxEntries 與 accountMaxEntries 只剩第五步「依審計目的」兩項的預設，仍是 11。第一次失敗是建置失敗：收據 20261030-163008036-bb2e987946cc4c72a351610e661109c5。
        // 「分錄金額中有連續 0 的尾數」的 0 的個數改照 IDEA 版由 GL 算出（裁定 2026-11-05-25），前後端都拿掉固定 6 個 0 的常數；
        // 前端改讀已確認 GL 配對帶回的個數（trailingZeroDigits）。第一次失敗是建置失敗：收據 20261105-082826731-60a7ec59299d423aa561526e307e604b。
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
        // 0 的個數改由 GL 算出（裁定 2026-11-05-25）：不知道個數時寫規則名稱，知道時寫「金額尾數連續 N 個 0」。
        // 第一次失敗是建置失敗：收據 20261105-082826731-60a7ec59299d423aa561526e307e604b。
        Assert.Equal("分錄金額中有連續 0 的尾數", FilterConditionLabels.PrescreenKeys[PrescreenRuleKeys.TrailingZeros]);
        Assert.Equal("金額尾數連續 4 個 0", FilterConditionLabels.PrescreenLabel(PrescreenRuleKeys.TrailingZeros, 4));
        Assert.Equal("空白摘要", FilterConditionLabels.PrescreenLabel(PrescreenRuleKeys.BlankDescription, 4));
        // 編製分錄較少的人員與使用較少的科目兩條規則照舊版拿掉，標籤與門檻常數跟著不在登錄裡（裁定 2026-10-29-27）；第一次失敗是建置失敗：收據 20261030-163008036-bb2e987946cc4c72a351610e661109c5。
        Assert.DoesNotContain(FilterConditionLabels.PrescreenKeys.Values, label => label.Contains("11 筆以下", StringComparison.Ordinal));
        // 一條規則只有一個定義，所以標籤正本搬到 Domain PrescreenRuleDefinitions，FilterConditionLabels 由它衍生。第一次失敗：Focused 收據 20261024-195604731-a54ac30c8a994b46878d21f5b82967bf。
        var source = Read("JET", "Domain", "Rules", "PrescreenRuleDefinitions.cs");
        Assert.DoesNotContain("DefaultZerosThreshold", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DefaultMaxEntries", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PagingLimits_KeepSummaryLimitsSeparateFromTwoHundredRowTables()
    {
        var core = Read("JET", "wwwroot", "js", "ui-core.js");
        // 工作底稿版本紀錄改用具名的 REPORT_HISTORY_PAGE_SIZE，仍固定 50 份；首次失敗：20261004-105344715-0a3f9684995a4a20bcdcb2fb954649f6
        // （原寫在已刪除的 WorkingPaperSheetMirrorTests.WorkingPaperHistory_DoesNotReplaceCurrentCompletionEvidence，2026-10-14 搬來）。
        // 第六步實際一頁 50 份由 tools/tests/frontend-journey-tables.test.cjs 與 frontend-workflow.test.cjs 的報告版本紀錄測試核對。
        foreach (var key in new[] { "RESULT_PREVIEW_PAGE_SIZE", "REPORT_HISTORY_PAGE_SIZE", "VALUE_PROFILE_LIMIT" })
        {
            Assert.Equal(50, NumericConstant(core, key));
            Assert.Contains(key + ": " + key, core, StringComparison.Ordinal);
        }
        // These are actual endpoint defaults, not inferred from a matching-looking frontend literal.
        Assert.Equal(50, HandlerDefault(typeof(MappingValueProfileHandler)));
        // 裁定 2026-10-16-03：明細與欄位配對每頁最多 200 筆；驗證摘要及欄位值取樣的 50 筆設定不變。
        // 第一次失敗：20261016-093839202-e342e301c3f14aa2ae0b43abe98a30ed。
        Assert.Equal(200, HandlerDefault(typeof(QueryDataPreviewHandler)));
        Assert.Equal(200, NumericConstant(core, "TABLE_PAGE_SIZE"));
        Assert.Contains("TABLE_PAGE_SIZE: TABLE_PAGE_SIZE", core, StringComparison.Ordinal);
        Assert.Equal(50, HandlerDefault(typeof(DevDbTableDataHandler)));
        Assert.Equal(200, PageRequest.DefaultPageSize);
        Assert.Equal(500, PageRequest.MaxPageSize);
        // 開發者表格也明示每頁 200 筆，省略 limit 的後端相容預設仍為 50。
        // 第一次失敗：20261016-094045942-99a1634b315f4241bf92408a1a7da5d6。
        Assert.Contains("limit: Ui.TABLE_PAGE_SIZE", Read("JET", "wwwroot", "js", "dev-panel.js"), StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyFixedCatalogue_RemainsJsonAndKeepsItsSixZeroExplanation()
    {
        // 裁定 2026-10-08-06 重新設計自訂篩選條件：目錄從正式前端移到測試夾具，內容仍是 JSON 且原文不變；
        // 新選單的「金額尾數連續多個 0」預設位數直接取同一個六個零的門檻常數。
        using var json = JsonDocument.Parse(Read("tests", "JET.Tests", "Fixtures", "legacy-form-2024-1210.json"));
        var condition = json.RootElement.GetProperty("conditions").EnumerateArray().Single(item => item.GetProperty("letter").GetString() == "D");
        Assert.Equal("金額整數部分以至少 6 個零結尾，整數為零不選取。可另用「金額尾數連續 0 的位數」調整位數。", condition.GetProperty("help").GetString());
        // 尾數條件改用欄位尾數與至少一元的子條件組；六位預設仍來自同一常數。
        // 首次失敗：20261014-133317716-754ea282ab3842268400b7daca79b593。
        // 2026-11-03 改成一條條件，新增「尾數連續多個 0」比較方式（裁定 2026-11-03-07）：原本核對尾數樣態與至少一元兩條子條件，
        // 改核對新的比較方式；預設個數仍取同一個六個零的門檻常數。第一次失敗：Focused 收據 20261103-100726697-97e6c3f1045d4beda092d17c7bec9ecb。
        // 0 的個數改照 IDEA 版由 GL 算出，加入時預設這個個數（裁定 2026-11-05-25、2026-11-05-26）：常用查法不寫個數，
        // 加入時由 FilterValues.zeroRunDefault 帶入；這份舊目錄是夾具原文，仍寫 6 個零。第一次失敗是建置失敗：收據 20261105-082826731-60a7ec59299d423aa561526e307e604b。
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
