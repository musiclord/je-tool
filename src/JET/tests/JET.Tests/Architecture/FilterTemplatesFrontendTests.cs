using System.Text.RegularExpressions;
using JET.Domain;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 常用情境範本（FILTER_SCENARIO_TEMPLATES）的守衛：每筆範本的規則型別都是後端認得的 wire 型別、模式在 Domain
/// 標籤表內，範本與依審計目的條件不另寫前置條件。範本的套用入口與組合卡由前端的行為測試核對；
/// 這裡只比對前後端型別與模式的原始碼。
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
        // 共十一個範例；「借方與貸方科目編號組合」是第十一個。
        Assert.Equal(11, keys.Length);
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        AssertKnownRuleShapes(body, "範本");
        // 範例上不寫 requires：範例的前置條件由條件內容查和後端同一張需求表；
        // 表和後端驗證器的一致由 FilterBackendRuleMirrorFrontendTests 核對，範例停用並寫原因由前端測試核對。
        Assert.DoesNotContain("requires:", body, StringComparison.Ordinal);
        // 只有部門範例要選文字型攸關資料元素欄位。
        Assert.Equal(new[] { "text" }, Regex.Matches(body, @"pickField:\s*'(?<field>[^']+)'").Select(m => m.Groups["field"].Value).ToArray());
        Assert.Matches(new Regex(@"key:\s*'manualDepartment'[^}]*pickField:\s*'text'"), body);
        // 前端不匯出另一份前置條件詞彙；每一種前置條件都有判斷，由 FilterBackendRuleMirrorFrontendTests 核對。
        // 前端沒有「案件沒有查核截止日」分支與 periodEnd 前置條件：後端需求表沒有這一種，
        // 範例不因此停用由前端測試核對。
        Assert.DoesNotContain("FILTER_CONDITION_REQUIREMENTS", core, StringComparison.Ordinal);
    }

    [Fact]
    public void FieldShortcuts_UseKnownRuleTypesOperatorsAndRequirements()
    {
        // 欄位底下的常用查法與範例做同樣的型別與比較方式檢查，不新增運算子或規則型別。
        var core = Read("js", "ui-core.js");
        var match = Regex.Match(core, @"FIELD_SHORTCUT_GROUPS\s*=\s*\[(?<body>.*?)\n  \];", RegexOptions.Singleline);
        Assert.True(match.Success, "ui-core.js 找不到 FIELD_SHORTCUT_GROUPS");
        var body = match.Groups["body"].Value;
        AssertKnownRuleShapes(body, "常用查法");
        // 常用查法上不寫 requires，前置條件由條件內容查同一張需求表。
        Assert.DoesNotContain("requires:", body, StringComparison.Ordinal);
        foreach (Match key in Regex.Matches(body, @"prescreenKey:\s*'(?<key>[^']+)'"))
        {
            Assert.True(FilterConditionLabels.PrescreenKeys.ContainsKey(key.Groups["key"].Value), key.Groups["key"].Value);
        }
        // 預篩選規則由條件總覽的「預篩選」分頁直接列出每一條。
        // 常用查法掛在欄位條件分頁各自的欄位底下；
        // 這裡核對依欄位分組的順序，每組的 key 就是欄位識別。
        Assert.Equal(new[] { "glDate", "approvalDate", "amount", "description", "accountNumber", "createdBy", "accountCategory" },
            Regex.Matches(body, @"\{ key: '(?<key>[A-Za-z]+)', field: '\k<key>', checks:").Select(m => m.Groups["key"].Value).ToArray());
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
}
