using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 使用者 2026-10-15 在待決事項勾選頁裁定 B1，原文：「後端儲存時只保留該條件類型用得到的欄位，存檔格式只在一個地方決定；
/// 已存的舊情境在下次儲存時整理。前端送出的格式不變。」
/// 這裡核對 <see cref="FilterRuleWireFields"/>：每種條件只留 type、join 與該類型用得到的屬性，情境層與條件組層原樣保留；
/// 整理前後的驗證結果、三種資料庫方言編譯出的 SQL 與參數、讀回文字完全相同；表上的類型和解析器認得的類型一一對應。
/// 「完整版」照前端 ui-core.js newFilterRule 給多數條件類型的共同預設值補上用不到的屬性，模擬畫面實際送出的內容。
/// </summary>
public sealed class FilterRuleWireFieldsTests
{
    private const string TextFieldId = "rde.b1000000b1000000b1000000b1000001";
    private const string MoneyFieldId = "rde.b1000000b1000000b1000000b1000002";

    private static readonly IReadOnlyList<GlRdeFieldMetadata> RdeFields =
    [
        new(TextFieldId, "合成文字欄", "合成附註", RdeFieldValueTypeNames.Text),
        new(MoneyFieldId, "合成金額欄", "合成稅額", RdeFieldValueTypeNames.Money)
    ];

    private static readonly FilterRuleContext RuleContext =
        new(100, "2025-12-01", "2025-01-01", "2025-12-31") { RdeFields = RdeFields };

    // ui-core.js newFilterRule 的共同預設值（2026-10-15 讀取）。categorySelection 只給借貸組合，那兩種本來就保留它。
    private const string GenericDefaults = """
        {"prescreenKey":"suspiciousKeywords","field":"description","keywords":"","values":[],"mode":"contains",
         "normalization":"preserve","from":"","to":"","drCr":"debit","isManual":"true","pairMode":"exact",
         "debitCategoryIds":["builtin.receivables"],"creditCategoryIds":["builtin.revenue"],"fieldId":"","operator":"",
         "amountBasis":"absolute","value":"","digits":"3","maxEntries":"","windowDays":""}
        """;

    // 每種目前可儲存的條件一條合格的精簡寫法，只帶 type、join 與該類型用得到的屬性；整理完整版後應該剛好得到它。
    public static TheoryData<string> CompactValidRules() => new()
    {
        """{"join":"AND","type":"prescreen","prescreenKey":"blankDescription"}""",
        """{"join":"AND","type":"text","field":"description","keywords":"合成","mode":"contains"}""",
        """{"join":"OR","type":"textSet","field":"description","values":["甲","乙 丙"],"mode":"exact","normalization":"removeAsciiSpaces"}""",
        """{"join":"AND","type":"dateRange","field":"postDate","from":"2025-03-01","to":"2025-03-31"}""",
        """{"join":"AND","type":"numRange","field":"amount","from":"100","to":"5000"}""",
        """{"join":"AND","type":"drCrOnly","drCr":"credit"}""",
        """{"join":"AND","type":"manualAuto","isManual":"false"}""",
        """{"join":"AND","type":"accountPair","pairMode":"debitAnchor","categorySelection":"subtree","debitCategoryIds":["builtin.cash"],"creditCategoryIds":["builtin.revenue"]}""",
        """{"join":"AND","type":"specialAccountCategoryPair","pairMode":"drNotCr","categorySelection":"subtree","debitCategoryIds":["builtin.cash"],"creditCategoryIds":["builtin.cash"]}""",
        // 預篩選收斂計畫階段九把兩種舊式筆數條件列為停用寫法，不再是可儲存的條件；精簡寫法的檢查仍涵蓋「科目與人員統計」。
        // 第一次失敗：Focused 收據 20261024-195604731-a54ac30c8a994b46878d21f5b82967bf（驗證回「已停用」）。
        """{"join":"AND","type":"entityFrequency","field":"accNum","countUnit":"entries","countOperator":"lessThanOrEqual","countFrom":"11","countTo":""}""",
        """{"join":"AND","type":"revenueDebitNearQuarterEnd","windowDays":"10"}""",
        """{"join":"AND","type":"revenueWithoutNormalCounterpart"}""",
        """{"join":"AND","type":"manualRevenueEntry"}""",
        """{"join":"AND","type":"preparerEqualsApprover"}""",
        $$"""{"join":"AND","type":"typed","fieldId":"{{TextFieldId}}","operator":"equals","value":"  Alpha "}""",
        $$"""{"join":"AND","type":"typed","fieldId":"{{MoneyFieldId}}","operator":"greaterThan","value":"100","amountBasis":"absolute"}""",
        """{"join":"AND","type":"fieldValue","field":"description","operator":"contains","values":["合成"],"includeBlank":false}""",
        """{"join":"AND","type":"fieldValue","field":"amount","operator":"between","from":"100","to":"500","amountBasis":"absolute","includeBlank":false}""",
        """{"join":"AND","type":"accountSide","categoryMode":"is","categoryIds":["builtin.cash"],"categorySelection":"subtree"}""",
        """{"join":"AND","type":"group","rules":[{"join":"AND","type":"drCrOnly","drCr":"debit"},{"join":"OR","type":"manualAuto","isManual":"true"}]}""",
        """{"join":"AND","type":"voucher","quantifier":"any","rules":[{"join":"AND","type":"drCrOnly","drCr":"debit"},{"join":"AND","type":"fieldValue","field":"accNum","operator":"in","values":["1000"]}]}"""
    };

    [Fact]
    public void TheTableCoversExactlyTheRuleTypesTheParserAccepts()
    {
        var parsed = FilterRuleWireFields.UsedByType.Keys
            .Select(type => FilterScenarioPayloadParser.Parse(Scenario($$"""{"type":"{{type}}"}""").RootElement, 100)
                .Groups[0].Rules[0].Type)
            .Order()
            .ToArray();

        Assert.Equal(Enum.GetValues<FilterRuleType>().Order().ToArray(), parsed);
        Assert.Throws<JetActionException>(() =>
            FilterScenarioPayloadParser.Parse(Scenario("""{"type":"noSuchCondition"}""").RootElement, 100));
    }

    [Theory]
    [MemberData(nameof(CompactValidRules))]
    public void TrimmingTheFullRuleLeavesExactlyTheCompactRule(string compact)
    {
        using var full = Scenario(WithUnusedDefaults(compact));
        var trimmed = FilterRuleWireFields.KeepUsedRuleFields(full.RootElement);
        var rule = FirstRule(trimmed);
        var expected = JsonNode.Parse(compact)!;

        Assert.True(JsonNode.DeepEquals(expected, rule), rule.ToJsonString());
        Assert.Equal(PropertyNames(expected), PropertyNames(rule));
        using var again = JsonDocument.Parse(trimmed);
        Assert.Equal(trimmed, FilterRuleWireFields.KeepUsedRuleFields(again.RootElement));
    }

    [Theory]
    [MemberData(nameof(CompactValidRules))]
    public void TrimmedRulesValidateCompileAndReadBackExactlyLikeTheFullRules(string compact)
    {
        using var full = Scenario(WithUnusedDefaults(compact));
        using var trimmed = JsonDocument.Parse(FilterRuleWireFields.KeepUsedRuleFields(full.RootElement));
        var fullSpec = FilterScenarioPayloadParser.Parse(full.RootElement, 100);
        var trimmedSpec = FilterScenarioPayloadParser.Parse(trimmed.RootElement, 100);

        // 完整版本身合格，整理只會碰到合格的條件；沒有科目配對與分類時，兩邊列出相同的原因。
        Assert.Empty(FilterScenarioValidator.Validate(fullSpec, Validation(mapped: true), forSave: true));
        foreach (var mapped in new[] { true, false })
        {
            foreach (var forSave in new[] { true, false })
            {
                Assert.Equal(
                    FilterScenarioValidator.Validate(fullSpec, Validation(mapped), forSave),
                    FilterScenarioValidator.Validate(trimmedSpec, Validation(mapped), forSave));
            }
        }

        Assert.Equal(FilterSinglePurposeRules.Reason(fullSpec.Groups[0].Rules[0]), FilterSinglePurposeRules.Reason(trimmedSpec.Groups[0].Rules[0]));
        foreach (IProviderSqlDialect dialect in new IProviderSqlDialect[] { SqliteDialect.Instance, DuckDbDialect.Instance, SqlServerDialect.Instance })
        {
            var expected = Compile(dialect, fullSpec);
            var actual = Compile(dialect, trimmedSpec);
            Assert.Equal(expected.Sql, actual.Sql);
            Assert.Equal(Describe(expected.Parameters), Describe(actual.Parameters));
            Assert.Equal(expected.EvidencePredicates, actual.EvidencePredicates);
        }

        Assert.Equal(Render(full.RootElement), Render(trimmed.RootElement));
    }

    [Theory]
    [InlineData("customKeywords", "keywords")]
    [InlineData("customTrailingZeros", "digits")]
    [InlineData("trailingDigits", "keywords")]
    public void RetiredTypesKeepOnlyTheirOwnField(string type, string field)
    {
        // 已停用的寫法在儲存時一律被驗證擋下，不會走到整理；表上仍列出它們讀的屬性，和解析器一一對應。
        using var full = Scenario(WithUnusedDefaults($$"""{"join":"AND","type":"{{type}}","{{field}}":"000"}"""));
        var rule = FirstRule(FilterRuleWireFields.KeepUsedRuleFields(full.RootElement));

        Assert.Equal(["join", "type", field], PropertyNames(rule));
    }

    [Fact]
    public void ScenarioAndGroupPropertiesStayAsTheyAre_AndKeptValuesKeepTheirRawText()
    {
        using var source = JsonDocument.Parse($$"""
            {"name":"合成","unknownTop":1e2,"groups":[{"join":"AND","unknownGroup":1.2300,"matchScope":"row","rules":[
              {"join":"AND","type":"typed","fieldId":"{{TextFieldId}}","operator":"equals","value":"  Alpha ","unknownRule":1E+02},
              {"join":"AND","type":"customPreparerEntryCount","maxEntries":5.0,"digits":"3"}]}],
             "editorOrigins":{"version":1,"groups":[{"letters":[null,null]}]},"source":"kct"}
            """);

        using var trimmed = JsonDocument.Parse(FilterRuleWireFields.KeepUsedRuleFields(source.RootElement));
        var root = trimmed.RootElement;
        Assert.Equal(["name", "unknownTop", "groups", "editorOrigins", "source"], root.EnumerateObject().Select(property => property.Name));
        Assert.Equal("1e2", root.GetProperty("unknownTop").GetRawText());
        Assert.Equal("""{"version":1,"groups":[{"letters":[null,null]}]}""", root.GetProperty("editorOrigins").GetRawText());
        var group = root.GetProperty("groups")[0];
        Assert.Equal(["join", "unknownGroup", "matchScope", "rules"], group.EnumerateObject().Select(property => property.Name));
        Assert.Equal("1.2300", group.GetProperty("unknownGroup").GetRawText());
        var rules = group.GetProperty("rules");
        Assert.Equal(["join", "type", "fieldId", "operator", "value"], rules[0].EnumerateObject().Select(property => property.Name));
        Assert.Equal("  Alpha ", rules[0].GetProperty("value").GetString());
        Assert.Equal("5.0", rules[1].GetProperty("maxEntries").GetRawText());
        Assert.False(rules[1].TryGetProperty("digits", out _));
    }

    [Fact]
    public void ARuleWhoseTypeIsNotInTheTableIsKeptAsItIs()
    {
        // 驗證過的條件不會走到這裡；認不得的類型不猜要留哪些屬性。
        using var source = Scenario("""{"join":"AND","type":"noSuchCondition","digits":"3"}""");

        var rule = FirstRule(FilterRuleWireFields.KeepUsedRuleFields(source.RootElement));

        Assert.Equal(["join", "type", "digits"], PropertyNames(rule));
    }

    [Fact]
    public void TheSavedDefinitionStampTrimsRulesWhileTheVerbatimStampKeepsThem()
    {
        using var full = Scenario(WithUnusedDefaults("""{"join":"AND","type":"manualAuto","isManual":"true"}"""));

        using var saved = JsonDocument.Parse(RuleLogicVersions.StampSavedFilterDefinition(full.RootElement));
        using var verbatim = JsonDocument.Parse(RuleLogicVersions.StampFilterDefinition(full.RootElement));

        Assert.Equal(RuleLogicVersions.Filter, saved.RootElement.GetProperty("logicVersion").GetString());
        Assert.Equal(["join", "type", "isManual"], PropertyNames(FirstRule(saved.RootElement.GetRawText())));
        Assert.True(FirstRule(verbatim.RootElement.GetRawText()).AsObject().ContainsKey("digits"));
    }

    private static JsonDocument Scenario(string rule) =>
        JsonDocument.Parse("""{"name":"合成情境","rationale":"B1 整理","groups":[{"join":"AND","matchScope":"row","rules":[""" + rule + "]}]}");

    // 只補這個類型沒有保留的共同預設值，所以補上的屬性都不會改變判定；group 與 voucher 的子條件也照補。
    private static string WithUnusedDefaults(string compact)
    {
        var rule = JsonNode.Parse(compact)!.AsObject();
        var type = rule["type"]!.GetValue<string>();
        var kept = FilterRuleWireFields.UsedByType[type];
        foreach (var (name, value) in JsonNode.Parse(GenericDefaults)!.AsObject())
        {
            if (!kept.Contains(name) && !rule.ContainsKey(name))
            {
                rule[name] = value!.DeepClone();
            }
        }

        if (rule["rules"] is JsonArray children)
        {
            for (var index = 0; index < children.Count; index++)
            {
                children[index] = JsonNode.Parse(WithUnusedDefaults(children[index]!.ToJsonString()));
            }
        }

        return rule.ToJsonString();
    }

    private static JsonNode FirstRule(string scenario) => JsonNode.Parse(scenario)!["groups"]![0]!["rules"]![0]!;

    private static string[] PropertyNames(JsonNode node) => node.AsObject().Select(property => property.Key).ToArray();

    private static FilterValidationContext Validation(bool mapped) => new(
        HasLastPeriodStart: true,
        HasAccountMapping: mapped,
        HasAuthorizedPreparers: true,
        PopulationScope: GlPopulationScope.AuditPeriod,
        HasAnyAccountCategory: mapped,
        HasRevenueCategory: mapped,
        HasCounterpartCategory: mapped)
    {
        // 預篩選收斂計畫階段四拿掉四個欄位旗標，改由規則需求表讀可用欄位清單（2026-10-23）；這裡不帶清單就不檢查欄位，
        // 等同原本四個旗標都是 true。第一次失敗：收據 20261023-031856475-04f811a69e3346428de91091f0d02655（編譯失敗）。
        RdeFields = RdeFields,
        MoneyScale = 100
    };

    private static FilterSqlFragmentPlan Compile(IProviderSqlDialect dialect, FilterScenarioSpec spec) =>
        new GlFilterWhereBuilder(dialect, new GlRulePredicates(dialect, GlPopulationScopeSql.Predicate))
            .BuildPlan(spec, RuleContext, includeEvidence: true);

    private static string[] Describe(IReadOnlyList<FilterSqlParameter> parameters) =>
        parameters.Select(parameter => parameter.Name + "=" + JsonSerializer.Serialize(parameter.Value)).ToArray();

    private static string Render(JsonElement scenario) => FilterConditionRenderer.Render(
        scenario,
        AccountTaxonomyBuiltIns.All.ToDictionary(category => category.CategoryId, category => category.Label),
        RdeFields.ToDictionary(field => field.FieldId, field => field.Label),
        AccountTaxonomyBuiltIns.All,
        preparationDate: "2025-12-01",
        periodStart: "2025-01-01",
        periodEnd: "2025-12-31");
}
