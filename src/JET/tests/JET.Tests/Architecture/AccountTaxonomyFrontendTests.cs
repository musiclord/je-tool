using System.Text.RegularExpressions;
using JET.Domain;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 科目分類編輯器的前端守衛。身分、semantic role 與「使用中不可刪」的最終裁定都在後端；
/// 本組只鎖內建保護、role 封閉選單鏡像、revision 樂觀鎖與失效說明。
/// </summary>
public sealed class AccountTaxonomyFrontendTests
{
    [Fact]
    public void BuiltInCategories_MirrorDomainIdentityAndRole()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var builtIns = ExtractBuiltIns(core);

        Assert.Equal(AccountTaxonomyBuiltIns.All.Count, builtIns.Count);
        foreach (var expected in AccountTaxonomyBuiltIns.All)
        {
            Assert.True(builtIns.TryGetValue(expected.CategoryId, out var actual),
                $"ui-core.js 缺少內建分類 {expected.CategoryId}");
            Assert.Equal(expected.Label, actual.Label);
            Assert.Equal(expected.SemanticRole, actual.SemanticRole);
            Assert.Equal(expected.Ordinal, actual.Ordinal);
        }
    }

    [Fact]
    public void SemanticRoleOptions_CoverEveryBuiltInRoleExactly()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var roles = ExtractValueLabelMap(core, "ACCOUNT_TAXONOMY_ROLES");

        Assert.Equal(
            AccountTaxonomyBuiltIns.All.Select(item => item.SemanticRole).OrderBy(r => r, StringComparer.Ordinal),
            roles.Keys.OrderBy(r => r, StringComparer.Ordinal));
    }

    [Fact]
    public void Editor_ProtectsBuiltInsAndCategoriesUsedBySavedScenarios()
    {
        var validate = ReadFrontend("js", "steps", "validate-step.js");

        // 內建分類不可刪、role 不可改；被已存情境引用的自訂分類先在畫面擋下並說明原因。
        Assert.Contains("'內建分類不可刪除'", validate, StringComparison.Ordinal);
        Assert.Contains("'已被篩選情境使用，不可刪除'", validate, StringComparison.Ordinal);
        Assert.Contains("(row.isBuiltIn ? ' disabled aria-disabled=\"true\"' : '')", validate, StringComparison.Ordinal);
        Assert.Contains("if (row.rowId === rowId && !row.isBuiltIn)", validate, StringComparison.Ordinal);

        // 引用偵測讀的是已存情境的分類身分陣列，不是顯示名稱。
        Assert.Contains("['debitCategoryIds', 'creditCategoryIds', 'categoryIds']", validate, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_UsesRevisionOptimisticConcurrencyAndNeverMintsCustomIds()
    {
        var validate = ReadFrontend("js", "steps", "validate-step.js");

        Assert.Contains("global.JetApi.accountTaxonomySave({", validate, StringComparison.Ordinal);
        Assert.Contains("revision: state.taxonomy ? state.taxonomy.revision : 1", validate, StringComparison.Ordinal);
        Assert.Contains("if (row.categoryId) { item.categoryId = row.categoryId; }", validate, StringComparison.Ordinal);
        Assert.DoesNotContain("'custom.'", validate, StringComparison.Ordinal);

        // ordinal 由清單順序決定；顯示名稱必填且不可重複由前端就近提示。
        Assert.Contains("ordinal: index", validate, StringComparison.Ordinal);
        Assert.Contains("'每個分類都需要顯示名稱'", validate, StringComparison.Ordinal);
        Assert.Contains("'顯示名稱不可重複'", validate, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_MirrorsTheDownstreamInvalidationScope()
    {
        var validate = ReadFrontend("js", "steps", "validate-step.js");
        var state = ReadFrontend("js", "state.js");

        // 第二遍第9批：失效範圍改由後端唯一政策回傳，不能要求前端保留第二份矩陣。
        // 首次失敗：20261004-100752118-a195c091201e444d858ec7d7bee3d291。
        // 原本「taxonomy不使驗證失效」仍鎖在權威政策，前端只消費本次回應。
        var impact = AuditDependencyPolicy.For(AuditMutation.AccountTaxonomy);
        Assert.False(impact.InvalidateValidation);
        Assert.True(impact.InvalidatePrescreen);
        Assert.True(impact.InvalidateFilterHits);
        Assert.False(impact.InvalidateFilterScenarioDefinitions);
        Assert.Contains("Store.setTaxonomyAfterSave(data)", validate, StringComparison.Ordinal);
        Assert.Matches(@"Store\.setTaxonomyAfterSave\(data\);\s*Store\.applyMutationEffects\(data\);", validate);
        var setter = ExtractBlock(state, "setTaxonomyAfterSave");
        Assert.Contains("state.taxonomy = snapshot || null", setter, StringComparison.Ordinal);
        Assert.DoesNotContain("invalidateDerivedResults", setter, StringComparison.Ordinal);
        Assert.DoesNotContain("validation: true", setter, StringComparison.Ordinal);
        var effects = ExtractBlock(state, "applyMutationEffects");
        Assert.Contains("var invalidated = result.invalidatedResults || {}", effects, StringComparison.Ordinal);
        Assert.Contains("invalidateDerivedResults(invalidated)", effects, StringComparison.Ordinal);
        Assert.Contains("validation: !!result.staleState.validation", effects, StringComparison.Ordinal);
        Assert.Contains("prescreen: !!result.staleState.prescreen", effects, StringComparison.Ordinal);
        Assert.Contains("filter: !!result.staleState.filter", effects, StringComparison.Ordinal);
        Assert.Contains("風險預篩選與篩選命中需要重新執行", validate, StringComparison.Ordinal);
    }

    [Fact]
    public void RuleControls_UseTaxonomyIdentitiesInsteadOfLegacyCategoryLabels()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var filter = ReadFrontend("js", "steps", "filter-step.js");

        // 舊的五類單選白名單已退場：分類身分一律來自目前專案 taxonomy。
        Assert.DoesNotContain("ACCOUNT_CATEGORY_OPTIONS", core, StringComparison.Ordinal);
        // 第二遍第 5 批把分類樹呈現抽到 ui-core.js。首次失敗收據：
        // 20261004-075340533-87179f5019724ec89d7c19b4243fdb09；改查共同函式與呼叫端，不放寬 stable ID 契約。
        Assert.Contains("Ui.taxonomyPickerHtml(state, selected, idsKey, legend, view)", filter, StringComparison.Ordinal);
        Assert.Contains("Ui.bindTaxonomyPicker(picker, Store.getState()", filter, StringComparison.Ordinal);
        Assert.Contains("var categories = taxonomyCategories(state)", core, StringComparison.Ordinal);
        var pickerStart = core.IndexOf("function taxonomyPickerHtml(", StringComparison.Ordinal);
        var pickerEnd = core.IndexOf("function bindTaxonomyPicker(", StringComparison.Ordinal);
        Assert.True(pickerStart >= 0 && pickerEnd > pickerStart, "找不到共同分類樹的呈現函式。");
        var picker = core[pickerStart..pickerEnd];
        Assert.Contains("var tree = taxonomyTree(state)", picker, StringComparison.Ordinal);
        Assert.Contains("data-category-bind=\"' + esc(key) + '\"", picker, StringComparison.Ordinal);
        Assert.Contains("value=\"' + esc(category.categoryId) + '\"", picker, StringComparison.Ordinal);
        Assert.Contains("indexOf(category.categoryId) >= 0 ? ' checked'", picker, StringComparison.Ordinal);
        Assert.Contains("esc(category.label)", picker, StringComparison.Ordinal);

        // 新規則只帶陣列。2026-10-02 起刪除回放舊單選定義的退路，原本鎖住退路的兩行斷言
        // 改成確認讀取只認陣列、舊名稱換算函式已移除。
        Assert.Contains("debitCategoryIds: ['builtin.receivables']", core, StringComparison.Ordinal);
        Assert.Contains("creditCategoryIds: ['builtin.revenue']", core, StringComparison.Ordinal);
        Assert.Contains("function ruleCategoryIds(rule, idsKey)", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("builtInCategoryIdForLegacyLabel", core + filter, StringComparison.Ordinal);

        // 多選去重：同一側的勾選集合不得重複同一身分。
        Assert.Contains("if (item.checked && selected.indexOf(item.value) < 0)", filter, StringComparison.Ordinal);
    }

    private static Dictionary<string, (string Label, string SemanticRole, int Ordinal)> ExtractBuiltIns(string source)
    {
        var arrayMatch = Regex.Match(
            source,
            @"ACCOUNT_TAXONOMY_BUILT_INS\s*=\s*\[(?<body>.*?)\];",
            RegexOptions.Singleline);
        Assert.True(arrayMatch.Success, "ui-core.js 內找不到 ACCOUNT_TAXONOMY_BUILT_INS 陣列。");

        var result = new Dictionary<string, (string, string, int)>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(
            arrayMatch.Groups["body"].Value,
            @"\{\s*categoryId:\s*'(?<id>[^']+)'\s*,\s*label:\s*'(?<label>[^']+)'\s*,\s*ordinal:\s*(?<ordinal>\d+)\s*,\s*semanticRole:\s*'(?<role>[^']+)'"))
        {
            result[match.Groups["id"].Value] = (
                match.Groups["label"].Value,
                match.Groups["role"].Value,
                int.Parse(match.Groups["ordinal"].Value));
        }

        Assert.NotEmpty(result);
        return result;
    }

    private static Dictionary<string, string> ExtractValueLabelMap(string source, string arrayName)
    {
        var arrayMatch = Regex.Match(
            source,
            arrayName + @"\s*=\s*\[(?<body>.*?)\];",
            RegexOptions.Singleline);
        Assert.True(arrayMatch.Success, $"ui-core.js 內找不到 {arrayName} 陣列。");

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(
            arrayMatch.Groups["body"].Value,
            @"\{\s*value:\s*'(?<value>[^']+)'\s*,\s*label:\s*'(?<label>[^']+)'"))
        {
            result[match.Groups["value"].Value] = match.Groups["label"].Value;
        }

        Assert.NotEmpty(result);
        return result;
    }

    private static string ExtractBlock(string source, string memberName)
    {
        var start = source.IndexOf(memberName + ": function", StringComparison.Ordinal);
        Assert.True(start >= 0, $"state.js 找不到 {memberName}。");
        var end = source.IndexOf("\n    },", start, StringComparison.Ordinal);
        Assert.True(end > start, $"state.js 的 {memberName} 邊界解析失敗。");
        return source[start..end];
    }

    private static string ReadFrontend(params string[] segments)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot(), "JET", "wwwroot" }.Concat(segments).ToArray()));

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JET.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("向上尋不到 JET.slnx。");
    }
}
