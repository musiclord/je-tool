using System.Text.RegularExpressions;
using JET.Domain;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 攸關資料元素（typed）條件建構器的前端守衛。operator 集合、operand carrier 與 blank 語意的
/// 正本都在 Domain；本組只鎖「畫面選單逐鍵鏡像」「輸入形狀跟著欄位型別」與「送出形狀不帶
/// 不適用 carrier」。命中判定仍全在後端 SQL。
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

    [Fact]
    public void OperatorCarrier_FollowsTheFrozenContract()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var carrier = ExtractFunction(core, "typedOperatorCarrier", "ACCOUNT_TAXONOMY_ROLES");

        Assert.Contains("'isBlank' || op === 'isNotBlank'", carrier, StringComparison.Ordinal);
        Assert.Contains("return 'none'", carrier, StringComparison.Ordinal);
        Assert.Contains("op === 'between'", carrier, StringComparison.Ordinal);
        Assert.Contains("return 'range'", carrier, StringComparison.Ordinal);
        Assert.Contains("op === 'in' || op === 'notIn'", carrier, StringComparison.Ordinal);
        Assert.Contains("return 'set'", carrier, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownValueType_FailsClosedWithNoOperators()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var resolver = ExtractFunction(core, "typedOperatorsForValueType", "typedOperatorLabel");

        Assert.Contains("valueType === 'text'", resolver, StringComparison.Ordinal);
        Assert.Contains("valueType === 'date'", resolver, StringComparison.Ordinal);
        Assert.Contains("valueType === 'money'", resolver, StringComparison.Ordinal);
        Assert.Contains("return [];", resolver, StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_DerivesInputShapeFromTheCommittedFieldType()
    {
        var filter = ReadFrontend("js", "steps", "filter-step.js");
        // L58 now calls ruleSummaryLabel inside controls; delimit by the following declaration, not an earlier call.
        // First failure: 20261004-093913331-b4376ac7ce5a4ffa8603dbedf49b0cc2.
        var controls = ExtractFunction(filter, "ruleControlsHtml", "function ruleSummaryLabel");
        var typedCase = ExtractSwitchCase(controls, "typed");

        // 欄位清單只來自已提交的定義；型別決定 operator 選單與輸入框種類。
        Assert.Contains("rdeFieldOptions()", typedCase, StringComparison.Ordinal);
        Assert.Contains("rdeFieldValueType(rule.fieldId)", typedCase, StringComparison.Ordinal);
        Assert.Contains("Ui.typedOperatorsForValueType(valueType)", typedCase, StringComparison.Ordinal);
        Assert.Contains(
            "valueType === 'date' ? 'date' : (valueType === 'money' ? 'number' : 'text')",
            typedCase,
            StringComparison.Ordinal);

        // 三種 carrier 的輸入形狀；blank 家族不渲染任何 operand。
        Assert.Contains("data-rule-bind=\"value\"", typedCase, StringComparison.Ordinal);
        Assert.Contains("data-rule-bind=\"from\"", typedCase, StringComparison.Ordinal);
        Assert.Contains("data-rule-bind=\"to\"", typedCase, StringComparison.Ordinal);
        Assert.Contains("data-rule-bind=\"values\"", typedCase, StringComparison.Ordinal);
        Assert.Contains("Ui.TYPED_SET_MAX_VALUES", typedCase, StringComparison.Ordinal);
        Assert.Contains("aria-describedby", typedCase, StringComparison.Ordinal);

        // amountBasis 只在金額型欄位、且有 operand 時出現。
        Assert.Contains("valueType === 'money' && carrier !== 'none'", typedCase, StringComparison.Ordinal);
    }

    [Fact]
    public void WireProjection_KeepsOnlyTheCarrierThatTheOperatorUses()
    {
        var filter = ReadFrontend("js", "steps", "filter-step.js");
        var projection = ExtractFunction(filter, "typedWireRule", "groupCombinator");

        Assert.Contains("Ui.typedOperatorCarrier(clean.operator)", projection, StringComparison.Ordinal);
        Assert.Contains("if (carrier === 'value') { wire.value = clean.value; }", projection, StringComparison.Ordinal);
        Assert.Contains("if (carrier === 'range') { wire.from = clean.from; wire.to = clean.to; }", projection, StringComparison.Ordinal);
        Assert.Contains("if (carrier === 'set') { wire.values = (clean.values || []).slice(); }", projection, StringComparison.Ordinal);
        Assert.Contains(
            "if (rdeFieldValueType(clean.fieldId) === 'money')",
            projection,
            StringComparison.Ordinal);

        // typed 規則走同一個共用送出投影，不另開第二條路徑。
        Assert.Contains("clean.type === 'typed' ? typedWireRule(clean) : clean", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void Gate_BlocksIncompleteOrOversizedTypedRulesBeforeSending()
    {
        var filter = ReadFrontend("js", "steps", "filter-step.js");

        Assert.Contains("function hasIncompleteTypedRule(draft)", filter, StringComparison.Ordinal);
        Assert.Contains("rule.type === 'typed' && (!rule.fieldId || !rule.operator)", filter, StringComparison.Ordinal);
        Assert.Contains("rule.values.length > Ui.TYPED_SET_MAX_VALUES", filter, StringComparison.Ordinal);
        // 2026-10-03 用語統一 T2：「額外欄位」改為「攸關資料元素欄位」（第一次失敗：收據 20261003-023349721-0ccefea0a80c412aa8460624eaae563a）。
        Assert.Contains("'攸關資料元素欄位條件需選定欄位與比較方式'", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("'額外欄位條件需選定欄位與比較方式'", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void TypedConditionType_IsOfferedOnlyWhenTheCaseHasCommittedFields()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var filter = ReadFrontend("js", "steps", "filter-step.js");

        Assert.Contains("requiresRdeFields: true", core, StringComparison.Ordinal);
        Assert.Contains("(!t.requiresRdeFields || hasRdeFields)", filter, StringComparison.Ordinal);
        // 2026-10-03 用語統一 T2：「額外欄位」改為「攸關資料元素欄位」（第一次失敗：收據 20261003-023349721-0ccefea0a80c412aa8460624eaae563a）。
        Assert.Contains("'需先在欄位配對勾選攸關資料元素欄位'", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("'需先在欄位配對勾選額外欄位'", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadBack_UsesCurrentFieldMetadataAndBackendOperatorLabels()
    {
        var filter = ReadFrontend("js", "steps", "filter-step.js");
        var core = ReadFrontend("js", "ui-core.js");

        // 僅 label 改名時自動跟進；欄位被移除時退回原識別字（與後端 renderer 同一 fallback）。
        Assert.Contains("Ui.rdeFieldLabel(Store.getState(), rule.fieldId)", filter, StringComparison.Ordinal);
        var labelHelper = ExtractFunction(core, "rdeFieldLabel", "dynamicColumnHeadHtml");
        Assert.Contains("return hit ? hit.label : fieldId;", labelHelper, StringComparison.Ordinal);
        Assert.Contains("Ui.typedOperatorLabel(rule.operator)", filter, StringComparison.Ordinal);
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

    private static string ExtractSwitchCase(string switchBody, string caseName)
    {
        var match = Regex.Match(
            switchBody,
            @"case\s+'" + Regex.Escape(caseName) + @"':(?<body>[\s\S]*?)(?=\n\s*case\s+'|\n\s*default:)");
        Assert.True(match.Success, $"filter-step.js 找不到 {caseName} 控制項 case。");
        return match.Groups["body"].Value;
    }

    private static string ExtractFunction(string source, string functionName, string nextSymbol)
    {
        var start = source.IndexOf("function " + functionName, StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到 {functionName}。");
        var end = source.IndexOf(nextSymbol, start + functionName.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"找不到 {functionName} 的邊界（下一個符號 {nextSymbol}）。");
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
