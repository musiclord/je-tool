using System.Text.RegularExpressions;
using JET.Domain;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 攸關資料元素（typed）條件建構器的前端守衛。operator 集合、operand carrier 與 blank 語意的
/// 正本都在 Domain；本組只剩「畫面選單逐鍵鏡像」與比較基準選項鏡像。輸入形狀跟著欄位型別、
/// 送出形狀不帶不適用的 carrier，以及新增條件不再提供舊式型別，2026-10-14 起改由
/// <c>tools/tests/frontend-journey-filter-saved.test.cjs</c> 的 journey 15 核對。命中判定仍全在後端 SQL。
/// </summary>
public sealed class TypedFilterFrontendTests
{
    [Fact]
    public void OperatorMenus_MirrorDomainSetsAndLabelsPerValueType()
    {
        var core = ReadFrontend("js", "ui-core.js");

        AssertOperatorSet(core, "TYPED_TEXT_OPERATORS", TypedFieldOperatorSets.Text);
        AssertOperatorSet(core, "TYPED_DATE_OPERATORS", TypedFieldOperatorSets.Date);
        AssertOperatorSet(core, "TYPED_MONEY_OPERATORS", TypedFieldOperatorSets.Money);
    }

    [Fact]
    public void AmountBasisOptions_MirrorDomainTokensAndLabels()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var options = ExtractValueLabelMap(core, "TYPED_AMOUNT_BASIS_OPTIONS");

        Assert.Equal(2, options.Count);
        Assert.Equal(FilterConditionLabels.TypedSignedAmountBasis, options[TypedAmountBasisNames.Signed]);
        Assert.Equal(FilterConditionLabels.TypedAbsoluteAmountBasis, options[TypedAmountBasisNames.Absolute]);
    }

    private static void AssertOperatorSet(
        string core,
        string arrayName,
        IReadOnlyList<string> expectedOperators)
    {
        var options = ExtractValueLabelMap(core, arrayName);
        Assert.Equal(expectedOperators, options.Keys.ToArray());
        foreach (var (op, label) in options)
        {
            Assert.Equal(FilterConditionLabels.TypedOperatorLabel(op), label);
        }
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
