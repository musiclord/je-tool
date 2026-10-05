using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 分類多選的呼叫必須與定義的參數個數一致。2026-10-02 刪掉單選分類退路時，定義少了一個參數，
/// 借貸分類組合的兩個呼叫卻還傳三個參數，欄位標題因此顯示成內部鍵名，「整張傳票不得有這些貸方分類」消失；
/// 公開測試沒有抓到，是桌面情境 filter-auditor-journey 抓到的（收據 20261002-122456569）。
/// </summary>
public sealed class FilterCategorySelectFrontendTests
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
    public void CategoryMultiSelectCalls_PassExactlyTheDeclaredArguments()
    {
        var filter = Read("js", "steps", "filter-step.js");

        var definition = Regex.Match(filter, @"function categoryMultiSelect\((?<parameters>[^)]*)\)");
        Assert.True(definition.Success, "filter-step.js 內找不到 categoryMultiSelect 的定義。");
        var declared = definition.Groups["parameters"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Length;

        var calls = Regex.Matches(filter, @"(?<!function )categoryMultiSelect\(")
            .Select(match => CountTopLevelArguments(filter, match.Index + match.Length))
            .ToArray();
        Assert.NotEmpty(calls);
        Assert.All(calls, count => Assert.Equal(declared, count));
    }

    [Fact]
    public void AccountPairCard_UsesPlainLanguageLegends()
    {
        var filter = Read("js", "steps", "filter-step.js");

        Assert.Contains(
            "categoryMultiSelect('debitCategoryIds', rule.pairMode === 'notDrCr' ? '整張傳票不得有這些借方分類' : '借方分類')",
            filter,
            StringComparison.Ordinal);
        Assert.Contains(
            "categoryMultiSelect('creditCategoryIds', rule.pairMode === 'drNotCr' ? '整張傳票不得有這些貸方分類' : '貸方分類')",
            filter,
            StringComparison.Ordinal);
    }

    // 從左括號後面開始，數到對應的右括號為止的最外層參數個數；略過字串與巢狀括號裡的逗號。
    private static int CountTopLevelArguments(string source, int start)
    {
        var depth = 0;
        var arguments = 0;
        var sawToken = false;
        char? quote = null;
        for (var i = start; i < source.Length; i++)
        {
            var c = source[i];
            if (quote is not null)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = null;
                }

                continue;
            }

            switch (c)
            {
                case '\'' or '"' or '`':
                    quote = c;
                    sawToken = true;
                    break;
                case '(' or '[' or '{':
                    depth++;
                    sawToken = true;
                    break;
                case ')' or ']' or '}' when depth > 0:
                    depth--;
                    break;
                case ')':
                    return sawToken ? arguments + 1 : 0;
                case ',' when depth == 0:
                    arguments++;
                    break;
                default:
                    if (!char.IsWhiteSpace(c))
                    {
                        sawToken = true;
                    }

                    break;
            }
        }

        throw new InvalidOperationException("categoryMultiSelect 呼叫沒有對應的右括號。");
    }
}
