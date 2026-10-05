using System.Text.Json;
using System.Text.RegularExpressions;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 前端為了就近提示，持有幾份後端規則的副本。這裡逐份比對副本與後端權威值，任何一邊改了另一邊沒跟上就失敗。
/// 後端仍是權威；本檔只讀前端原始碼並用正則擷取，不執行 JavaScript。所有資料都是合成值。
/// </summary>
public sealed class FilterBackendRuleMirrorFrontendTests
{
    [Fact]
    public void SuspiciousKeywordDefaults_MatchBackendWordForWordAndInOrder()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var match = Regex.Match(
            core,
            @"var\s+SUSPICIOUS_KEYWORD_DEFAULTS\s*=\s*\[(?<body>[^\]]*)\];",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, "ui-core.js 內找不到 SUSPICIOUS_KEYWORD_DEFAULTS。");

        var frontend = Regex.Matches(match.Groups["body"].Value, @"'(?<word>[^']*)'")
            .Select(item => item.Groups["word"].Value)
            .ToArray();

        // 連順序一起比：自訂關鍵字條件的預設值是把這份清單依序以逗號串成輸入框內容（ui-core.js 的
        // SUSPICIOUS_KEYWORD_DEFAULTS.join(',')），審計員在畫面上看到的就是這個順序。
        Assert.Equal(SuspiciousKeywordDefaults.Defaults, frontend);
        Assert.Contains("SUSPICIOUS_KEYWORD_DEFAULTS.join(',')", core, StringComparison.Ordinal);
    }

    [Fact]
    public void TypedSetMaxValues_MatchesBackendTypedInLimit()
    {
        var core = ReadFrontend("js", "ui-core.js");

        Assert.Equal(FilterScenarioLimits.MaxTypedInValuesPerRule, ReadIntVar(core, "TYPED_SET_MAX_VALUES"));
        Assert.Contains("TYPED_SET_MAX_VALUES: TYPED_SET_MAX_VALUES", core, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterValues_UsesSharedListLimitInsteadOfHardcodedNumbers()
    {
        var values = ReadFrontend("js", "filter-values.js");

        // fieldValue 的 in／notIn 與文字包含比對，後端都以 MaxTypedInValuesPerRule 驗證，
        // 所以本檔的值清單上限必須引用 Ui.TYPED_SET_MAX_VALUES，不能寫死數字。
        Assert.DoesNotMatch(
            new Regex(@"\b" + FilterScenarioLimits.MaxTypedInValuesPerRule + @"\b", RegexOptions.CultureInvariant),
            values);
        Assert.DoesNotContain("TEXT_SET_MAX_VALUES", values, StringComparison.Ordinal);

        Assert.Contains("最多 ' + Ui.TYPED_SET_MAX_VALUES + ' 個不同值，空行不列入。'", values, StringComparison.Ordinal);
        Assert.Contains("selected.length < Ui.TYPED_SET_MAX_VALUES", values, StringComparison.Ordinal);
        Assert.Contains("'最多選取 ' + Ui.TYPED_SET_MAX_VALUES + ' 個不同日期，請先移除不需要的日期。'", values, StringComparison.Ordinal);
        // L81 counts the exact distinct strings that wire() sends, not a second uppercase-derived set.
        // First failure: 20261004-084834579-2436b90e840c4c53bec70aa81bf5a84b.
        // The Node behavior case also fixes the 100/101 mixed-case wire counts; keep this source-to-limit guard.
        var problem = ExtractFunctionBody(values, "problem(rule, state)");
        Assert.Contains("var value = wire(rule, state), mode = carrier(rule.operator, selected.type);", problem, StringComparison.Ordinal);
        Assert.Contains("var inputs = mode === 'set' ? value.values", problem, StringComparison.Ordinal);
        Assert.Contains("inputs.length > Ui.TYPED_SET_MAX_VALUES", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("toUpperCase", problem, StringComparison.Ordinal);
        Assert.Contains("'請填入 1 到 ' + Ui.TYPED_SET_MAX_VALUES + ' 個不同值'", values, StringComparison.Ordinal);
    }

    [Fact]
    public void SavedScenarioLimit_IsOneBackendConstantMirroredByTheFrontend()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var filter = ReadFrontend("js", "steps", "filter-step.js");

        Assert.Equal(FilterScenarioLimits.MaxSavedScenarios, ReadIntVar(core, "FILTER_MAX_SAVED_SCENARIOS"));
        Assert.Contains("FILTER_MAX_SAVED_SCENARIOS: FILTER_MAX_SAVED_SCENARIOS", core, StringComparison.Ordinal);
        Assert.Contains("scenarios.length > Ui.FILTER_MAX_SAVED_SCENARIOS", filter, StringComparison.Ordinal);
        // 2026-10-03 用語統一 W10：前後端訊息一起改成「儲存」（第一次失敗：收據 20261003-023349721-0ccefea0a80c412aa8460624eaae563a）。
        Assert.Contains(
            "'最多儲存 ' + Ui.FILTER_MAX_SAVED_SCENARIOS + ' 個篩選情境；請先移除既有情境。'",
            filter,
            StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"scenarios\.length\s*>\s*\d", RegexOptions.CultureInvariant), filter);

        // 後端 filter.commit 的 handler 與 AuditCore 規劃都引用同一個常數，不各自寫數字。
        var handler = ReadSource("Application", "Handlers", "FilterHandlers.cs");
        var program = ReadSource("AuditCore", "FilterProgram.cs");
        Assert.Contains("MaxScenarios = FilterScenarioLimits.MaxSavedScenarios;", handler, StringComparison.Ordinal);
        Assert.Contains("MaxFilterScenarios = FilterScenarioLimits.MaxSavedScenarios;", program, StringComparison.Ordinal);
    }

    /// <summary>
    /// 前端依 accountMappingRequirement 停用規則；後端驗證器是權威。逐一把前端規則清單裡的每個型別交給
    /// 後端驗證器，在每一種科目配對資格下比對「前端可用」與「後端不因科目配對擋下」是否一致。
    /// </summary>
    [Fact]
    public void AccountMappingRequirements_AgreeWithBackendValidatorForEveryRuleType()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var filter = ReadFrontend("js", "steps", "filter-step.js");
        AssertFrontendEligibilityExpression(filter);

        var requirements = ReadRuleTypeRequirements(core);
        Assert.Equal(
            FilterConditionLabels.RuleTypes.Keys.OrderBy(key => key, StringComparer.Ordinal),
            requirements.Keys.OrderBy(key => key, StringComparer.Ordinal));

        var mismatches = new List<string>();
        foreach (var (type, requirement) in requirements)
        {
            var rule = new Dictionary<string, object?> { ["type"] = type };
            foreach (var facts in MappingFactStates)
            {
                var frontendAvailable = FrontendEligible(requirement, facts);
                var backendBlocks = BackendBlocksBecauseOfMapping(rule, facts);
                if (frontendAvailable == backendBlocks)
                {
                    mismatches.Add($"{type}（前端需求：{requirement ?? "無"}；科目配對狀態：{facts}；"
                        + $"前端{(frontendAvailable ? "可用" : "停用")}，後端{(backendBlocks ? "擋下" : "放行")}）");
                }
            }
        }

        Assert.True(mismatches.Count == 0, "前後端科目配對需求不一致：\n" + string.Join("\n", mismatches));
    }

    /// <summary>標了需求的型別，在最低需求成立時，一條完整合成規則要能通過後端驗證。</summary>
    [Theory]
    [InlineData("accountSide")]
    [InlineData("accountPair")]
    [InlineData("specialAccountCategoryPair")]
    [InlineData("revenueDebitNearQuarterEnd")]
    [InlineData("revenueWithoutNormalCounterpart")]
    [InlineData("manualRevenueEntry")]
    public void RequiredAccountMappingFacts_AreSufficientForTheBackend(string type)
    {
        var requirements = ReadRuleTypeRequirements(ReadFrontend("js", "ui-core.js"));
        var requirement = requirements[type];
        Assert.NotNull(requirement);

        var minimal = requirement switch
        {
            "any" => new MappingFacts(true, true, false, false),
            "revenue" => new MappingFacts(true, true, true, false),
            "revenueAndCounterpart" => new MappingFacts(true, true, true, true),
            _ => throw new InvalidOperationException($"未知的科目配對需求：{requirement}")
        };
        Assert.True(FrontendEligible(requirement, minimal));
        Assert.Empty(Validate(ValidSample(type), minimal));
    }

    /// <summary>一般預篩選鍵仍看匯入狀態；未預期組合另外需要收入與至少一種對方分類，與第四步相同。</summary>
    [Fact]
    public void PrescreenKeyAccountMappingRequirements_AgreeWithBackendValidator()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var filter = ReadFrontend("js", "steps", "filter-step.js");
        AssertFrontendPrescreenEligibilityExpression(filter);
        var body = ExtractArrayBody(core, "PRESCREEN_KEY_OPTIONS");
        var keys = Regex.Matches(body, @"\{\s*value:\s*'(?<key>\w+)'(?<rest>[^{}]*)\}")
            .ToDictionary(
                item => item.Groups["key"].Value,
                item => Regex.IsMatch(item.Groups["rest"].Value, @"requiresAccountMapping:\s*true"));
        Assert.NotEmpty(keys);

        var mismatches = new List<string>();
        foreach (var (key, requiresMapping) in keys)
        {
            var rule = new Dictionary<string, object?> { ["type"] = "prescreen", ["prescreenKey"] = key };
            foreach (var facts in MappingFactStates)
            {
                // R7 changed the actual frontend helper, not this metadata flag. The old test reproduced
                // only requiresAccountMapping and therefore missed the new two-summary-flag requirement.
                // First failures: 20261004-084834579-2436b90e840c4c53bec70aa81bf5a84b,
                // 20261004-085118314-9c43f4a2a69c4f718bab4f9f3d6aaa11. Keep every snapshot and every key.
                var frontendAvailable = (!requiresMapping || facts.Imported)
                    && (key != "unexpectedAccountPair" || facts.Imported && facts.HasRevenue && facts.HasCounterpart);
                if (frontendAvailable == BackendBlocksBecauseOfMapping(rule, facts))
                {
                    mismatches.Add($"{key}（科目配對狀態：{facts}）");
                }
            }
        }

        Assert.True(mismatches.Count == 0, "預篩選鍵的科目配對需求不一致：\n" + string.Join("\n", mismatches));
    }

    private static void AssertFrontendPrescreenEligibilityExpression(string filter)
    {
        var available = ExtractFunctionBody(filter, "availablePrescreenKeys()");
        Assert.Contains("return !prescreenRequirementNote(o.value);", available, StringComparison.Ordinal);
        var note = ExtractFunctionBody(filter, "prescreenRequirementNote(key)");
        Assert.Contains("if (option.requiresAccountMapping && !mapping)", note, StringComparison.Ordinal);
        Assert.Contains("if (key === 'unexpectedAccountPair')", note, StringComparison.Ordinal);
        Assert.Contains("if (!mapping || !mapping.hasRevenue)", note, StringComparison.Ordinal);
        Assert.Contains("if (!mapping.hasCounterpart)", note, StringComparison.Ordinal);
        Assert.Contains("return '科目配對缺少收入（Revenue）分類';", note, StringComparison.Ordinal);
        Assert.Contains("return '科目配對缺少對方分類（應收、現金或預收）';", note, StringComparison.Ordinal);
        Assert.Contains("return '';", note, StringComparison.Ordinal);
        // A newly introduced special-case key cannot silently evade the complete matrix above.
        Assert.Equal(["unexpectedAccountPair"], Regex.Matches(note, @"if \(key === '(?<key>\w+)'\)")
            .Select(match => match.Groups["key"].Value).ToArray());
    }

    private static string ExtractFunctionBody(string source, string signature)
    {
        var start = source.IndexOf("function " + signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"前端找不到 function {signature}。");
        var end = source.IndexOf("\n  function ", start + 1, StringComparison.Ordinal);
        Assert.True(end > start, $"前端找不到 function {signature} 的結尾。");
        return source[start..end];
    }

    private sealed record MappingFacts(bool Imported, bool HasAnyCategory, bool HasRevenue, bool HasCounterpart)
    {
        public override string ToString() => Imported
            ? $"已匯入，任一分類={HasAnyCategory}，Revenue={HasRevenue}，一般對方分類={HasCounterpart}"
            : "未匯入";
    }

    // 可能出現的科目配對狀態：未匯入，或已匯入且各分類有無的組合（有 Revenue 或對方分類必然有任一分類）。
    private static readonly MappingFacts[] MappingFactStates =
    [
        new(false, false, false, false),
        new(true, false, false, false),
        new(true, true, false, false),
        new(true, true, true, false),
        new(true, true, false, true),
        new(true, true, true, true)
    ];

    private static readonly MappingFacts FullFacts = new(true, true, true, true);

    // filter-step.js availableRuleTypes 的判斷式在 C# 端的對應；AssertFrontendEligibilityExpression 鎖住原文。
    private static bool FrontendEligible(string? requirement, MappingFacts facts) => requirement switch
    {
        null => true,
        "any" => facts.Imported && facts.HasAnyCategory,
        "revenue" => facts.Imported && facts.HasRevenue,
        "revenueAndCounterpart" => facts.Imported && facts.HasRevenue && facts.HasCounterpart,
        _ => throw new InvalidOperationException($"未知的科目配對需求：{requirement}")
    };

    private static void AssertFrontendEligibilityExpression(string filter)
    {
        var start = filter.IndexOf("function availableRuleTypes()", StringComparison.Ordinal);
        var end = filter.IndexOf("\n  function ", start + 1, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "filter-step.js 找不到 availableRuleTypes。");
        var body = filter[start..end];

        Assert.Contains("var mappingEligible = !requirement || (!!mapping && (", body, StringComparison.Ordinal);
        Assert.Contains("(requirement === 'any' && mapping.hasAnyCategory) ||", body, StringComparison.Ordinal);
        Assert.Contains("(requirement === 'revenue' && mapping.hasRevenue) ||", body, StringComparison.Ordinal);
        Assert.Contains(
            "(requirement === 'revenueAndCounterpart' && mapping.hasRevenue && mapping.hasCounterpart)",
            body,
            StringComparison.Ordinal);
    }

    // 後端是否「因為科目配對」擋下：同一條規則在完整資格下沒有、在此狀態下才出現的錯誤。
    private static bool BackendBlocksBecauseOfMapping(Dictionary<string, object?> rule, MappingFacts facts)
    {
        var withFacts = Validate(rule, facts);
        var withFullFacts = Validate(rule, FullFacts);
        return withFacts.Except(withFullFacts, StringComparer.Ordinal).Any();
    }

    private static IReadOnlyList<string> Validate(Dictionary<string, object?> rule, MappingFacts facts)
    {
        var scenario = new
        {
            name = "synthetic scenario",
            rationale = "synthetic rationale",
            groups = new[] { new { join = "AND", matchScope = "row", rules = new object[] { rule } } }
        };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(scenario));
        var spec = FilterScenarioPayloadParser.Parse(document.RootElement, moneyScale: 100);
        var context = new FilterValidationContext(
            HasLastPeriodStart: true,
            HasAccountMapping: facts.Imported,
            HasAuthorizedPreparers: true,
            HasAnyAccountCategory: facts.HasAnyCategory,
            HasRevenueCategory: facts.HasRevenue,
            HasCounterpartCategory: facts.HasCounterpart)
        {
            HasManualFlag = true,
            HasCreatedBy = true,
            HasApprovedBy = true,
            HasDescription = true
        };
        return FilterScenarioValidator.Validate(spec, context);
    }

    private static Dictionary<string, object?> ValidSample(string type) => type switch
    {
        "accountSide" => new()
        {
            ["type"] = type,
            ["drCr"] = "debit",
            ["categoryMode"] = "is",
            ["categorySelection"] = "node",
            ["categoryIds"] = new[] { AccountTaxonomyBuiltIns.CashId }
        },
        "accountPair" => new()
        {
            ["type"] = type,
            ["pairMode"] = AccountPairModes.Exact,
            ["debitCategoryIds"] = new[] { AccountTaxonomyBuiltIns.CashId },
            ["creditCategoryIds"] = new[] { AccountTaxonomyBuiltIns.RevenueId }
        },
        "specialAccountCategoryPair" => new()
        {
            ["type"] = type,
            ["pairMode"] = SpecialAccountCategoryPairModes.DrAndCr,
            ["debitCategoryIds"] = new[] { AccountTaxonomyBuiltIns.CashId },
            ["creditCategoryIds"] = new[] { AccountTaxonomyBuiltIns.RevenueId }
        },
        "revenueDebitNearQuarterEnd" => new() { ["type"] = type, ["windowDays"] = QuarterEndWindows.MinWindowDays },
        _ => new() { ["type"] = type }
    };

    private static Dictionary<string, string?> ReadRuleTypeRequirements(string core)
    {
        var body = ExtractArrayBody(core, "FILTER_RULE_TYPES");
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (Match item in Regex.Matches(body, @"\{\s*value:\s*'(?<type>\w+)'(?<rest>[^{}]*)\}"))
        {
            var requirement = Regex.Match(item.Groups["rest"].Value, @"accountMappingRequirement:\s*'(?<value>\w+)'");
            result.Add(item.Groups["type"].Value, requirement.Success ? requirement.Groups["value"].Value : null);
        }

        Assert.NotEmpty(result);
        return result;
    }

    private static string ExtractArrayBody(string source, string arrayName)
    {
        var match = Regex.Match(
            source,
            @"var\s+" + arrayName + @"\s*=\s*\[(?<body>.*?)\n  \];",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"ui-core.js 內找不到 {arrayName} 陣列。");
        return match.Groups["body"].Value;
    }

    private static int ReadIntVar(string source, string name)
    {
        var match = Regex.Match(
            source,
            @"var\s+" + name + @"\s*=\s*(?<value>\d+)\s*;",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"ui-core.js 內找不到 {name}。");
        return int.Parse(match.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ReadFrontend(params string[] segments) => ReadSource(["wwwroot", .. segments]);

    private static string ReadSource(params string[] segments)
    {
        var path = Path.Combine(new[] { RepoRoot(), "JET" }.Concat(segments).ToArray());
        Assert.True(File.Exists(path), $"找不到原始檔：{path}");
        return File.ReadAllText(path);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JET.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("向上尋不到 JET.slnx，無法定位 repo 根。");
    }
}
