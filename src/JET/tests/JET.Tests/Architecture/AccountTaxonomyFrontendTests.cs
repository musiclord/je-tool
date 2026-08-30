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
        Assert.Contains("['debitCategoryIds', 'creditCategoryIds']", validate, StringComparison.Ordinal);
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

        // taxonomy mutation 只使預篩選與篩選命中失效，資料驗證不受影響（與後端同一份依賴範圍）。
        Assert.Contains("Store.setTaxonomyAfterSave(data)", validate, StringComparison.Ordinal);
        var setter = ExtractBlock(state, "setTaxonomyAfterSave");
        Assert.Contains("invalidateDerivedResults({ prescreen: true, filter: true })", setter, StringComparison.Ordinal);
        Assert.DoesNotContain("validation: true", setter, StringComparison.Ordinal);
        Assert.Contains("保存分類會使既有的風險預篩選與篩選命中失效", validate, StringComparison.Ordinal);
    }

    [Fact]
    public void RuleControls_UseTaxonomyIdentitiesInsteadOfLegacyCategoryLabels()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var filter = ReadFrontend("js", "steps", "filter-step.js");

        // 舊的五類單選白名單已退場：分類身分一律來自目前專案 taxonomy。
        Assert.DoesNotContain("ACCOUNT_CATEGORY_OPTIONS", core, StringComparison.Ordinal);
        Assert.Contains("Ui.taxonomyCategories(state)", filter, StringComparison.Ordinal);
        Assert.Contains("data-category-bind=\"' + idsKey + '\"", filter, StringComparison.Ordinal);

        // 新規則只帶陣列；legacy scalar 只在回放舊定義時讀取。
        Assert.Contains("debitCategoryIds: ['builtin.receivables']", core, StringComparison.Ordinal);
        Assert.Contains("creditCategoryIds: ['builtin.revenue']", core, StringComparison.Ordinal);
        Assert.Contains("function ruleCategoryIds(rule, idsKey, legacyKey)", filter, StringComparison.Ordinal);
        Assert.Contains("Ui.builtInCategoryIdForLegacyLabel(rule[legacyKey])", filter, StringComparison.Ordinal);

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
