using System.Text.RegularExpressions;
using JET.Domain;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 常用情境範本（FILTER_SCENARIO_TEMPLATES）的守衛：每筆範本的規則型別都是後端認得的 wire 型別、模式在 Domain
/// 標籤表內，範本與依審計目的條件不另寫前置條件。範本的套用入口與組合卡，2026-10-14 起改由
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
        // 原本這裡核對四個範例標了前置條件：兩個現金範例要科目配對，空白核准人員要傳票核准人員，財報準備日起核准要準備日，
        // 否則缺這些時套用後要到預覽才被後端擋下。預篩選收斂計畫階段四（2026-10-23）拿掉範例上的 requires，範例的前置條件改由
        // 條件內容查和後端同一張需求表；表和後端驗證器的一致由 FilterBackendRuleMirrorFrontendTests 核對，範例停用並寫原因由
        // frontend-journey-filter-build.test.cjs 的階段四測試（兩個現金範例另有一則）與 frontend-workflow.test.cjs 的範例測試操作核對。
        // 第一次失敗：Focused 收據 20261023-045313479-2a121804a0e04b518609f80bf108ad65（範例上已經沒有 requires）。
        Assert.DoesNotContain("requires:", body, StringComparison.Ordinal);
        // 只有部門範例要選文字型攸關資料元素欄位。
        Assert.Equal(new[] { "text" }, Regex.Matches(body, @"pickField:\s*'(?<field>[^']+)'").Select(m => m.Groups["field"].Value).ToArray());
        Assert.Matches(new Regex(@"key:\s*'manualDepartment'[^}]*pickField:\s*'text'"), body);
        // 前端也不再匯出另一份前置條件詞彙；每一種前置條件都有判斷，改由 FilterBackendRuleMirrorFrontendTests 核對。
        // 使用者 2026-10-14 第三輪勾選頁確認刪除的「案件沒有查核截止日」分支與 periodEnd 前置條件維持刪除
        // （第一次失敗：收據 20261014-012448805-68a8e8ea71bc4d6392585a586ab6b91e）：後端需求表沒有這一種，
        // 範例不因此停用由 frontend-workflow.test.cjs 的範例測試核對。
        Assert.DoesNotContain("FILTER_CONDITION_REQUIREMENTS", core, StringComparison.Ordinal);
    }

    [Fact]
    public void PurposeChecks_UseKnownRuleTypesOperatorsAndRequirements()
    {
        // 使用者 2026-10-08 要求重新設計自訂篩選條件：依審計目的條件（當時叫常用查核條件）與範例做同樣的型別與比較方式檢查，不新增運算子或規則型別。
        var core = Read("js", "ui-core.js");
        // 2026-10-29 常用查核條件改稱依審計目的，常數跟著改名，比對的內容不變。第一次失敗：Focused 收據 20261029-062123410-6be7ae8a47f140af98c112c94ca35612。
        var match = Regex.Match(core, @"FILTER_PURPOSE_CHECK_GROUPS\s*=\s*\[(?<body>.*?)\n  \];", RegexOptions.Singleline);
        Assert.True(match.Success, "ui-core.js 找不到 FILTER_PURPOSE_CHECK_GROUPS");
        var body = match.Groups["body"].Value;
        AssertKnownRuleShapes(body, "依審計目的");
        // 同一階段拿掉依審計目的條件上的 requires，前置條件改由條件內容查同一張需求表（第一次失敗：同上收據）。
        Assert.DoesNotContain("requires:", body, StringComparison.Ordinal);
        foreach (Match key in Regex.Matches(body, @"prescreenKey:\s*'(?<key>[^']+)'"))
        {
            Assert.True(FilterConditionLabels.PrescreenKeys.ContainsKey(key.Groups["key"].Value), key.Groups["key"].Value);
        }
        // 最後一類「預篩選結果」只有「沿用第四步預篩選規則」，使用者 2026-10-16 要求縮短套用預篩選條件的操作後，
        // 改成條件總覽的「預篩選」分頁直接列出每一條規則（第一次失敗：收據 20261016-050025227-3ad1807939a146e6b55712279046deb1）。
        Assert.Equal(new[] { "日期與時點", "金額", "摘要", "科目與借貸", "人員與分錄性質" },
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
}
