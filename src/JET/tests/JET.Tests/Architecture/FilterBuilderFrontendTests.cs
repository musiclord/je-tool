using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 自訂入口先選對象再加入目前條件組；既有條件列仍採對象、比較、值的句型。
/// 分錄性質併進欄位下拉，wire 型別和後端規則不變。
/// </summary>
public sealed class FilterBuilderFrontendTests
{
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

    [Fact]
    public void CustomEntry_ChoosesOneSubjectAndAddsToTheActiveGroup()
    {
        var filter = Read("js", "steps", "filter-step.js");

        // 09-08 使用者要求分開條件入口與保存情境。第一次失敗保留於 20260908-024320083。
        Assert.Contains("function addRuleBarHtml()", filter, StringComparison.Ordinal);
        Assert.Contains("data-custom-subject", filter, StringComparison.Ordinal);
        Assert.Contains("option('type:accountSide', '借方或貸方分類', accountNote)", filter, StringComparison.Ordinal);
        Assert.Contains("option('type:prescreen', '預篩選條件', '')", filter, StringComparison.Ordinal);
        Assert.Contains("var newRule = customSubjectRule();", filter, StringComparison.Ordinal);
        // 加入目標仍須明確，組內原有 AND/OR 與同傳票限制保留。
        Assert.Contains("var gi = btn.hasAttribute('data-gi') ? Number(btn.getAttribute('data-gi')) : NaN;", filter, StringComparison.Ordinal);
        // 新草稿一開始就有一塊空的第 1 組帶「＋」列。2026-10-02 使用者裁定移除「情境層級」，非營業日不再有
        // 接在條件組後面的獨立區塊，所以這行不再串接 presetBlocks。第一次失敗：收據 20261003-031918754-fe1aa08447674c618228988b042764db。
        Assert.Contains("return setWellHtml(null, draft.groups.length, 1, false, true);", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("presetBlock", filter, StringComparison.Ordinal);

        Assert.DoesNotContain("FILTER_RULE_GROUPS", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("picker-card--custom", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("data-text-list-mode", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("teachingEmptyStateHtml", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRuleRow_IsOneSentenceWithoutATypeDropdown()
    {
        var filter = Read("js", "steps", "filter-step.js");
        var values = Read("js", "filter-values.js");

        Assert.DoesNotContain("data-rule-bind=\"type\"", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("rule-row__type", filter, StringComparison.Ordinal);
        // 09-08 第二輪：移除不幫助操作的家族小標，欄位名稱固定；第一次失敗為 20260908-064216425。
        Assert.DoesNotContain("<span class=\"rule-row__family\">", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("<select data-value-key=\"field\"", values, StringComparison.Ordinal);
        Assert.Contains("data-value-kind", values, StringComparison.Ordinal);
        Assert.Contains("data-value-polarity", values, StringComparison.Ordinal);
        Assert.Contains("data-action=\"remove-rule\" aria-label=\"移除這條條件\"", filter, StringComparison.Ordinal);

        // 三個家族各有自己的「對象」下拉；分錄性質（借貸別、人工／自動）是欄位下拉裡的一組選項。
        Assert.Contains("data-field-subject", filter, StringComparison.Ordinal);
        Assert.Contains("data-account-subject", filter, StringComparison.Ordinal);
        Assert.Contains("data-pattern-subject", filter, StringComparison.Ordinal);
        Assert.Contains("{ id: '__drCr', label: '借貸別' }, { id: '__isManual', label: '人工／自動' }", filter, StringComparison.Ordinal);
        Assert.Contains("if (!selected) { changed('pseudo', control.value); return; }", values, StringComparison.Ordinal);
        Assert.Contains("<optgroup label=\"分錄性質\">", values, StringComparison.Ordinal);

        // 家族只是畫面分類：三個家族合起來正好涵蓋後端認得的全部型別，wire 型別鍵不變。
        var families = Regex.Match(filter, @"var RULE_FAMILIES = \{(?<body>.*?)\n  \};", RegexOptions.Singleline).Groups["body"].Value;
        var covered = Regex.Matches(families, @"'(?<type>[A-Za-z]+)'").Select(m => m.Groups["type"].Value).ToHashSet(StringComparer.Ordinal);
        foreach (var type in JET.Domain.FilterConditionLabels.RuleTypes.Keys)
        {
            Assert.True(covered.Contains(type), $"型別 {type} 沒有歸到任何家族");
        }
    }

    [Fact]
    public void GroupHeader_ReadsAsOneSentence()
    {
        var filter = Read("js", "steps", "filter-step.js");
        // 單一條件不顯示布林組合；跨分錄設定另列，並保留原範圍與 AND 限制。
        Assert.Contains("AND（全部符合）", filter, StringComparison.Ordinal);
        Assert.Contains("OR（任一符合）", filter, StringComparison.Ordinal);
        Assert.Contains("data-set-combinator data-gi=", filter, StringComparison.Ordinal);
        Assert.Contains("<legend class=\"match-scope__legend\">比對範圍</legend>", filter, StringComparison.Ordinal);
        Assert.Contains("data-set-join data-set-join-index=", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("comboSegment(", filter, StringComparison.Ordinal);
        // 下拉的 change 事件沒有 checked，處理器不能再用 radio 的守門條件。
        Assert.DoesNotContain("if (!control.checked) { return; }", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("if (!radio.checked) { return; }", filter, StringComparison.Ordinal);
    }
}
