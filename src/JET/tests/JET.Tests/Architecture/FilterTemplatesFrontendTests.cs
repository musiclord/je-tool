using System.Text.RegularExpressions;
using JET.Domain;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 常用情境範本（FILTER_SCENARIO_TEMPLATES）的守衛：每筆範本的規則型別都是後端認得的 wire 型別、模式在 Domain
/// 標籤表內、需要科目配對的範本有標記。範本的套用入口與組合卡，2026-10-14 起改由
/// <c>tools/tests/frontend-journey-filter-build.test.cjs</c> 的行為測試核對；這裡只留前後端型別與模式的原始碼比對。
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
        // 使用者 2026-10-08 要求重新設計自訂篩選條件：原有六個範例沿用，另由舊目錄範例轉入四個，共十個。
        Assert.Equal(10, keys.Length);
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        AssertKnownRuleShapes(body, "範本");
        // 借現金貸非現金與貸現金借非現金一定要標需要科目配對，否則沒有配對時套用會在預覽才失敗。
        Assert.Matches(new Regex(@"key:\s*'cashDebitNonCashCredit'[^}]*requires:\s*'accountMapping'"), body);
        Assert.Matches(new Regex(@"key:\s*'cashCreditNonCashDebit'[^}]*requires:\s*'accountMapping'"), body);
        // 後端對未配對核心欄位的空白比較會擋，財報準備日起核准沒有準備日也會擋，所以這兩個範例要標前置條件。
        Assert.Matches(new Regex(@"key:\s*'blankDescriptionOrApprover'[^}]*requires:\s*'approveByMapped'"), body);
        Assert.Matches(new Regex(@"key:\s*'preparationManualHoliday'[^}]*requires:\s*'preparationDate'"), body);
        // 只有部門範例要選文字型攸關資料元素欄位。
        Assert.Equal(new[] { "text" }, Regex.Matches(body, @"pickField:\s*'(?<field>[^']+)'").Select(m => m.Groups["field"].Value).ToArray());
        Assert.Matches(new Regex(@"key:\s*'manualDepartment'[^}]*pickField:\s*'text'"), body);
        AssertClosedRequirements(core, body);
    }

    [Fact]
    public void CommonChecks_UseKnownRuleTypesOperatorsAndRequirements()
    {
        // 使用者 2026-10-08 要求重新設計自訂篩選條件：常用查核條件與範例做同樣的型別與比較方式檢查，不新增運算子或規則型別。
        var core = Read("js", "ui-core.js");
        var match = Regex.Match(core, @"FILTER_COMMON_CHECK_GROUPS\s*=\s*\[(?<body>.*?)\n  \];", RegexOptions.Singleline);
        Assert.True(match.Success, "ui-core.js 找不到 FILTER_COMMON_CHECK_GROUPS");
        var body = match.Groups["body"].Value;
        AssertKnownRuleShapes(body, "常用查核條件");
        AssertClosedRequirements(core, body);
        foreach (Match key in Regex.Matches(body, @"prescreenKey:\s*'(?<key>[^']+)'"))
        {
            Assert.True(FilterConditionLabels.PrescreenKeys.ContainsKey(key.Groups["key"].Value), key.Groups["key"].Value);
        }
        Assert.Equal(new[] { "日期與時點", "金額", "摘要", "科目與借貸", "人員與分錄性質", "預篩選結果" },
            Regex.Matches(body, @"\{ key: '[a-z]+', label: '(?<label>[^']+)', checks:").Select(m => m.Groups["label"].Value).ToArray());
    }

    private static void AssertKnownRuleShapes(string body, string owner)
    {
        foreach (Match type in Regex.Matches(body, @"type:\s*'(?<type>[^']+)'"))
        {
            Assert.True(FilterConditionLabels.RuleTypes.ContainsKey(type.Groups["type"].Value),
                $"{owner}用了後端不認得的型別「{type.Groups["type"].Value}」");
        }
        foreach (Match mode in Regex.Matches(body, @"pairMode:\s*'(?<mode>[^']+)'"))
        {
            Assert.True(FilterConditionLabels.SpecialPairModes.ContainsKey(mode.Groups["mode"].Value)
                || FilterConditionLabels.AccountPairModes.ContainsKey(mode.Groups["mode"].Value));
        }
        foreach (Match op in Regex.Matches(body, @"operator:\s*'(?<op>[^']+)'"))
        {
            Assert.True(FieldValueConditions.Labels.ContainsKey(op.Groups["op"].Value),
                $"{owner}用了後端不認得的比較方式「{op.Groups["op"].Value}」");
        }
        foreach (Match op in Regex.Matches(body, @"countOperator:\s*'(?<op>[^']+)'"))
        {
            Assert.True(EntityFrequencyConditions.Operators.ContainsKey(op.Groups["op"].Value), op.Groups["op"].Value);
        }
    }

    // requires 只能是 ui-core.js 的 FILTER_CONDITION_REQUIREMENTS 列出的幾種，filter-step 對每一種都有停用原因。
    private static void AssertClosedRequirements(string core, string body)
    {
        var list = Regex.Match(core, @"var FILTER_CONDITION_REQUIREMENTS = \[(?<body>[^\]]*)\];").Groups["body"].Value;
        var allowed = Regex.Matches(list, @"'(?<value>[^']+)'").Select(m => m.Groups["value"].Value).ToHashSet(StringComparer.Ordinal);
        // 使用者 2026-10-14 第三輪勾選頁確認 第 2 節刪除沒有作用的程式（第一次失敗：收據 20261014-012448805-68a8e8ea71bc4d6392585a586ab6b91e）。
        // 查核截止日在建立案件時必填，「案件沒有查核截止日」的分支與 periodEnd 前置條件一起刪除。
        Assert.Equal(new[] { "accountMapping", "approveByMapped", "createByMapped", "preparationDate", "unexpectedAccountPair" },
            allowed.Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("'periodEnd'", list, StringComparison.Ordinal);
        var filter = Read("js", "steps", "filter-step.js");
        foreach (var value in allowed)
        {
            Assert.Contains("if (requires === '" + value + "')", filter, StringComparison.Ordinal);
        }
        foreach (Match requires in Regex.Matches(body, @"requires:\s*'(?<value>[^']+)'"))
        {
            Assert.Contains(requires.Groups["value"].Value, allowed);
        }
    }
}
