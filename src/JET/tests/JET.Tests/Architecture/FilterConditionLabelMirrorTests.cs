using System.Text.RegularExpressions;
using JET.Domain;
using Xunit;

namespace JET.Tests.Architecture;

// filter-step.js 依職責拆成五個檔案後，讀第五步前端的斷言改用 ProductSources.FilterStepFrontend()，
// 把五個檔案合起來讀，範圍和拆檔前讀單一檔案相同；斷言本身不變。第一次失敗：Focused 收據 20261025-053438454-333c59b548b548008f977941c5502503。

/// <summary>
/// 條件中文標籤的漂移守衛（比照 <see cref="SupportedActionsParityTests"/> 的樣式）：Domain 的
/// <see cref="FilterConditionLabels"/> 為正本，前端 <c>ui-core.js</c> 的標籤常數必須與之逐鍵相等。
/// 前端沒有自動測試——標籤只改一邊時，Criteria summary 與前端藍色 read-back
/// 會漂移；這條守衛把該缺口升級為 CI 可擋。<c>PRESCREEN_RULES</c> 為雙向比對，故新增 row-tag 鍵卻
/// 漏接前端（如 <c>nonAuthorizedPreparer</c>）會立即紅燈。
/// </summary>
public sealed class FilterConditionLabelMirrorTests
{
    [Fact]
    public void FilterConditionTypes_MirrorDomainLabels_Bidirectional()
    {
        // 已停用的五種型別連同前端的 RETIRED_RULE_TYPES 一起拿掉（裁定 2026-10-30-25：已儲存情境的舊寫法不再讀回），
        // 前後端型別表恢復逐鍵相等，Domain 標籤表仍涵蓋全部型別。第一次失敗：Build 收據 20261030-080858642-d5c1719481744042bbfc70329c5fcceb（停用寫法的常數已不存在，無法編譯）。
        var frontend = ExtractValueLabelMap("FILTER_RULE_TYPES");
        Assert.Equal(FilterConditionLabels.RuleTypes, frontend);
        Assert.Equal(Enum.GetValues<FilterConditionType>().Length, FilterConditionLabels.RuleTypes.Count);
        Assert.DoesNotContain("RETIRED_RULE_TYPES", ReadUiCore(), StringComparison.Ordinal);
    }

    [Fact]
    public void PrescreenKeyOptions_MirrorDomainLabels_Bidirectional()
    {
        // 共用 11 與 6 的門檻常數後，literal-only regex 會把串接文字截斷，並非可見標籤變更。
        // 首次失敗：20261004-105344715-0a3f9684995a4a20bcdcb2fb954649f6。
        // 從JS自己的常數求出完整文字；Domain只作另一側比對，全部鍵仍逐一核對。
        // 一條規則只有一個定義，前端只留一份 PRESCREEN_RULES。第一次失敗：Focused 收據 20261024-195604731-a54ac30c8a994b46878d21f5b82967bf。
        var frontend = FrontendConstantTextReader.ValueLabels(ReadUiCore(), "PRESCREEN_RULES");
        Assert.Equal(FilterConditionLabels.PrescreenKeys, frontend);
        Assert.Equal("金額尾數連續 6 個 0", frontend["trailingZeros"]);
        // 第四步照舊版拿掉編製分錄較少的人員與使用較少的科目（裁定 2026-10-29-27），兩條規則的標籤跟著拿掉；第一次失敗：Focused 收據 20261030-163342206-c1b391e5be2e46bfa36b85fdbe6b59ad。
        Assert.False(frontend.ContainsKey("rarePreparer"));
        Assert.False(frontend.ContainsKey("rareAccount"));
    }

    [Fact]
    public void DomainPrescreenLabelKeys_EqualFilterableRowTagKeys()
    {
        // 標籤表的鍵集合不得與可篩選 row-tag 集合分岔：任一 row-tag 都要有中文標籤（否則渲染回退原鍵）。
        Assert.Equal(
            PrescreenRuleKeys.FilterableKeys.OrderBy(k => k, StringComparer.Ordinal),
            FilterConditionLabels.PrescreenKeys.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void TextModeOptions_MirrorDomainLabels_Bidirectional()
    {
        var frontend = ExtractValueLabelMap("TEXT_MODE_OPTIONS");
        Assert.Equal(FilterConditionLabels.TextModes, frontend);
    }

    [Fact]
    public void AccountPairModeOptions_MirrorDomainLabels_Bidirectional()
    {
        var frontend = ExtractValueLabelMap("ACCOUNT_PAIR_MODE_OPTIONS");
        Assert.Equal(FilterConditionLabels.AccountPairModes, frontend);
    }

    [Fact]
    public void SpecialPairModeOptions_MirrorDomainLabels_Bidirectional()
    {
        var frontend = ExtractValueLabelMap("SPECIAL_PAIR_MODE_OPTIONS");
        Assert.Equal(FilterConditionLabels.SpecialPairModes, frontend);
    }

    [Fact]
    public void AccountSideModeOptions_MirrorDomainLabels_Bidirectional()
    {
        var frontend = ExtractValueLabelMap("ACCOUNT_SIDE_MODE_OPTIONS");
        Assert.Equal(FilterConditionLabels.AccountSideModes, frontend);
    }

    [Fact]
    public void AccountCombinationOptions_ReuseTheDomainPairLabels()
    {
        // 借貸科目組合卡的每個模式標籤必須與對應 wire 型別在 Domain 的標籤逐字相同（舊格式 exact 除外，它只給舊情境讀回）。
        var source = ReadUiCore();
        var arrayMatch = Regex.Match(source, @"ACCOUNT_COMBINATION_OPTIONS\s*=\s*\[(?<body>.*?)\];", RegexOptions.Singleline);
        Assert.True(arrayMatch.Success, "ui-core.js 找不到 ACCOUNT_COMBINATION_OPTIONS");
        var items = Regex.Matches(arrayMatch.Groups["body"].Value,
            @"\{\s*type:\s*'(?<type>[^']+)'\s*,\s*mode:\s*'(?<mode>[^']+)'\s*,\s*label:\s*'(?<label>[^']+)'");
        Assert.Equal(6, items.Count);
        foreach (Match item in items)
        {
            var labels = item.Groups["type"].Value == "accountPair"
                ? FilterConditionLabels.AccountPairModes
                : FilterConditionLabels.SpecialPairModes;
            var expected = labels[item.Groups["mode"].Value];
            Assert.StartsWith(expected, item.Groups["label"].Value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GlFieldLabels_MirrorDomainSubset()
    {
        // GL_FIELDS 是 mapping 步驟也用的超集；只要求 Domain 條件欄子集的每一鍵在 GL_FIELDS 內且 label 相符。
        var frontend = ExtractKeyLabelMap("GL_FIELDS");
        foreach (var (key, label) in FilterConditionLabels.GlFields)
        {
            Assert.True(frontend.TryGetValue(key, out var frontendLabel),
                $"GL_FIELDS 缺少條件欄鍵「{key}」");
            Assert.Equal(label, frontendLabel);
        }
    }

    [Fact]
    public void CategoryListSeparator_MirrorsDomainLabel()
    {
        // 借貸組合多選讀回的分隔字元：後端 renderer 是唯一權威輸出，前端只能鏡射同一字元。
        var source = ReadUiCore();
        var match = Regex.Match(source, @"FILTER_CATEGORY_LIST_SEPARATOR\s*=\s*'(?<separator>[^']+)'");
        Assert.True(match.Success, "ui-core.js 找不到 FILTER_CATEGORY_LIST_SEPARATOR");
        Assert.Equal(FilterConditionLabels.CategoryListSeparator, match.Groups["separator"].Value);
    }

    [Fact]
    public void RuleFamilies_CoverEveryBackendRuleType()
    {
        // 2026-10-14 從 FilterBuilderFrontendTests.EveryRuleRow_IsOneSentenceWithoutATypeDropdown 原樣搬來。
        // 家族只是畫面分類：三個家族合起來正好涵蓋後端認得的全部型別，wire 型別鍵不變。
        // 前端行為測試讀不到後端的 FilterConditionLabels.RuleTypes，後端新增型別而前端漏接時只有這裡看得出來。
        var filter = ProductSources.FilterStepFrontend();
        var families = Regex.Match(filter, @"var RULE_FAMILIES = \{(?<body>.*?)\n  \};", RegexOptions.Singleline).Groups["body"].Value;
        var covered = Regex.Matches(families, @"'(?<type>[A-Za-z]+)'").Select(m => m.Groups["type"].Value).ToHashSet(StringComparer.Ordinal);
        foreach (var type in JET.Domain.FilterConditionLabels.RuleTypes.Keys)
        {
            Assert.True(covered.Contains(type), $"型別 {type} 沒有歸到任何家族");
        }
    }

    /// <summary>抽 <c>var NAME = [ { value: 'x', label: 'y' ... }, ... ]</c> 的 value→label 映射。</summary>
    private static Dictionary<string, string> ExtractValueLabelMap(string arrayName) =>
        ExtractPairs(arrayName, @"\{\s*value:\s*'(?<key>[^']+)'\s*,\s*label:\s*'(?<label>[^']+)'");

    /// <summary>抽 <c>var NAME = [ { key: 'x', label: 'y' ... }, ... ]</c> 的 key→label 映射。</summary>
    private static Dictionary<string, string> ExtractKeyLabelMap(string arrayName) =>
        ExtractPairs(arrayName, @"\{\s*key:\s*'(?<key>[^']+)'\s*,\s*label:\s*'(?<label>[^']+)'");

    private static Dictionary<string, string> ExtractPairs(string arrayName, string itemPattern)
    {
        var source = ReadUiCore();
        // 非貪婪擷取到「]; 」為止，避免吃進後續陣列。
        var arrayMatch = Regex.Match(
            source, arrayName + @"\s*=\s*\[(?<body>.*?)\];", RegexOptions.Singleline);
        Assert.True(arrayMatch.Success, $"ui-core.js 內找不到 {arrayName} 陣列（宣告形狀改了就同步更新本守衛）。");

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(arrayMatch.Groups["body"].Value, itemPattern))
        {
            map[m.Groups["key"].Value] = m.Groups["label"].Value;
        }

        Assert.NotEmpty(map); // 空集合守門：解析失敗不得真空通過
        return map;
    }

    private static string ReadUiCore()
    {
        var path = Path.Combine(RepoRoot(), "JET", "wwwroot", "js", "ui-core.js");
        Assert.True(File.Exists(path), $"找不到前端核心檔：{path}");
        return File.ReadAllText(path);
    }

    /// <summary>由測試組件位置向上尋 <c>JET.slnx</c>，定位 repo 根（同 SupportedActionsParityTests 的慣例）。</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "JET.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("向上尋不到 JET.slnx，無法定位 repo 根。");
    }
}
