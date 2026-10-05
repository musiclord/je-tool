using System.Text.RegularExpressions;
using JET.Domain;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 常用情境範本（FILTER_SCENARIO_TEMPLATES）的守衛：每筆範本的規則型別都是後端認得的 wire 型別、模式在 Domain
/// 標籤表內、需要科目配對的範本有標記；套用入口與範本列存在於篩選步驟。前端沒有自動測試，這裡只用原始碼比對。
/// </summary>
public sealed class FilterTemplatesFrontendTests
{
    private static string Read(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JET.slnx")))
        {
            directory = directory.Parent;
        }
        var root = directory?.FullName ?? throw new InvalidOperationException("向上尋不到 JET.slnx，無法定位 repo 根。");
        var path = Path.Combine(new[] { root, "JET", "wwwroot" }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), $"找不到前端檔：{path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void Templates_UseKnownRuleTypesAndModes()
    {
        var core = Read("js", "ui-core.js");
        var arrayMatch = Regex.Match(core, @"FILTER_SCENARIO_TEMPLATES\s*=\s*\[(?<body>.*?)\n  \];", RegexOptions.Singleline);
        Assert.True(arrayMatch.Success, "ui-core.js 找不到 FILTER_SCENARIO_TEMPLATES");
        var body = arrayMatch.Groups["body"].Value;
        var keys = Regex.Matches(body, @"key:\s*'(?<key>[^']+)'").Select(m => m.Groups["key"].Value).ToArray();
        Assert.Equal(6, keys.Length);
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        foreach (Match type in Regex.Matches(body, @"type:\s*'(?<type>[^']+)'"))
        {
            Assert.True(FilterConditionLabels.RuleTypes.ContainsKey(type.Groups["type"].Value),
                $"範本用了後端不認得的型別「{type.Groups["type"].Value}」");
        }
        foreach (Match mode in Regex.Matches(body, @"pairMode:\s*'(?<mode>[^']+)'"))
        {
            Assert.True(FilterConditionLabels.SpecialPairModes.ContainsKey(mode.Groups["mode"].Value)
                || FilterConditionLabels.AccountPairModes.ContainsKey(mode.Groups["mode"].Value));
        }
        foreach (Match op in Regex.Matches(body, @"operator:\s*'(?<op>[^']+)'"))
        {
            Assert.True(FieldValueConditions.Labels.ContainsKey(op.Groups["op"].Value),
                $"範本用了後端不認得的比較方式「{op.Groups["op"].Value}」");
        }
        // 借現金貸非現金與貸現金借非現金一定要標需要科目配對，否則沒有配對時套用會在預覽才失敗。
        Assert.Matches(new Regex(@"key:\s*'cashDebitNonCashCredit'[^}]*requires:\s*'accountMapping'"), body);
        Assert.Matches(new Regex(@"key:\s*'cashCreditNonCashDebit'[^}]*requires:\s*'accountMapping'"), body);
    }

    [Fact]
    public void InvalidScenarioDetails_AreMarkedOnTheRuleRow()
    {
        var api = Read("js", "jet-api.js");
        var filter = Read("js", "steps", "filter-step.js");
        Assert.Contains("err.details = message.error && Array.isArray(message.error.details)", api, StringComparison.Ordinal);
        Assert.Contains("function markRuleErrors(root, error)", filter, StringComparison.Ordinal);
        Assert.Contains("rule-row--invalid", filter, StringComparison.Ordinal);
        Assert.Contains("data-rule-error", filter, StringComparison.Ordinal);
        // 9/23：仍有兩條錯誤接線，但先拒絕舊案件或舊草稿回應，不能標紅新條件。
        Assert.Equal(2, Regex.Matches(filter, Regex.Escape("markRuleErrors(container, error);")).Count);
        Assert.Contains("if (!acceptResponse()) { return; } markRuleErrors", filter, StringComparison.Ordinal);
        Assert.Contains("if (!acceptsFilterCommit(saveSnapshot)) { return; }", filter, StringComparison.Ordinal);
        Assert.Contains("Store.getFilterDraftRev() === saveSnapshot.revision", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterStep_OffersTemplatesAndTheCombinationCard()
    {
        var filter = Read("js", "steps", "filter-step.js");
        Assert.Contains("data-action=\"apply-template\"", filter, StringComparison.Ordinal);
        Assert.Contains("function applyTemplate(key)", filter, StringComparison.Ordinal);
        Assert.Contains("data-pair-selection", filter, StringComparison.Ordinal);
        Assert.Contains("customPickerOpen: true", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("customDestination", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("排除區域", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("待判定", filter, StringComparison.Ordinal);
    }
}
