using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 2026-10-02 使用者裁定移除「情境層級」概念：KCT I 是加入目前條件組的條件括號，不再有獨立區塊、
/// 黃色預設卡片狀態或「適用整個情境」的說明。行為另由 tools/tests/frontend-workflow.test.cjs 直接執行前端腳本核對。
/// </summary>
public sealed class FilterKctNonBusinessDayFrontendTests
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
    public void ScenarioLevelPresetGroup_IsGoneFromScreenAndWire()
    {
        var filter = Read("js", "steps", "filter-step.js");
        var core = Read("js", "ui-core.js");
        var css = Read("css", "app.css");

        foreach (var removed in new[] { "情境層級", "適用整個情境", "__kctPresetGroup", "remove-preset-group", "scenario-preset", "picker-card--preset", "isPresetNewGroup" })
        {
            Assert.DoesNotContain(removed, filter, StringComparison.Ordinal);
            Assert.DoesNotContain(removed, css, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("情境層級", core, StringComparison.Ordinal);
        Assert.DoesNotContain("newGroup", core, StringComparison.Ordinal);
        // 送出的編輯來源只記每組字母，不再寫 presetGroup。
        Assert.Contains("return { letters: g.rules.map(function (r) { return r[KCT_LETTER_KEY] || null; }) };", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("presetGroup:", filter, StringComparison.Ordinal);
        // I 的預設定義是一條條件括號，加入時整份深拷貝。
        Assert.Contains("overrides: { type: 'group', rules: [", core, StringComparison.Ordinal);
        Assert.Contains("var copy = JSON.parse(JSON.stringify(spec));", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingValueNotice_NamesGroupConditionAndField()
    {
        var filter = Read("js", "steps", "filter-step.js");

        Assert.Contains("var text = '第 ' + (gi + 1) + ' 組第 ' + (path[0] + 1) + ' 條';", filter, StringComparison.Ordinal);
        Assert.Contains("text += '的第 ' + (path[i] + 1) + ' 項';", filter, StringComparison.Ordinal);
        Assert.Contains("var problems = draftRuleProblems(draft).map(function (item) { return item.text; });", filter, StringComparison.Ordinal);
        // 使用者 2026-10-07 要求重名不得影響唯一性：同一則提示接著寫重名原因，缺值清單本身不變（第一次失敗收據 20261007-035823939-41943206b5f747f1b7fb8dfe79f987d7）。
        Assert.Contains("'尚需補齊：' + problems.join('、')", filter, StringComparison.Ordinal);
        Assert.Contains("notice.textContent = (problems.length ? '尚需補齊：' + problems.join('、')", filter, StringComparison.Ordinal);
    }
}
