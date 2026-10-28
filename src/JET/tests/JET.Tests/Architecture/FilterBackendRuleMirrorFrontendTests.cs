using System.Text.Json;
using System.Text.RegularExpressions;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Architecture;

// 預篩選收斂計畫階段十（2026-10-25）：filter-step.js 依職責拆成五個檔案。讀第五步前端的斷言改用 ProductSources.FilterStepFrontend()，
// 把五個檔案合起來讀，範圍和拆檔前讀單一檔案相同；斷言本身不變。第一次失敗：Focused 收據 20261025-053438454-333c59b548b548008f977941c5502503。

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

    // 低嚴重度事項 F8（使用者 2026-10-19 要求處理）：尾數比對每組的位數與尾數連續 0 的位數，上限都是後端同一個常數，
    // 前端兩處都引用 Ui.TAIL_DIGITS_MAX，不寫數字。
    [Fact]
    public void TailDigitsMax_MatchesBackendTrailingZeroLimit()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var values = ReadFrontend("js", "filter-values.js");
        var filter = ProductSources.FilterStepFrontend();

        Assert.Equal(TrailingZeroThreshold.MaxCustomDigits, ReadIntVar(core, "TAIL_DIGITS_MAX"));
        Assert.Contains("TAIL_DIGITS_MAX: TAIL_DIGITS_MAX", core, StringComparison.Ordinal);
        Assert.Contains("item.length > Ui.TAIL_DIGITS_MAX", values, StringComparison.Ordinal);
        Assert.Contains("' 組純數字，每組 1 到 ' + Ui.TAIL_DIGITS_MAX + ' 位'", values, StringComparison.Ordinal);
        Assert.Contains("max=\"' + Ui.TAIL_DIGITS_MAX + '\" step=\"1\" aria-label=\"尾數連續 0 的位數\"", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("{1," + TrailingZeroThreshold.MaxCustomDigits + "}", values, StringComparison.Ordinal);
        Assert.DoesNotContain("max=\"" + TrailingZeroThreshold.MaxCustomDigits + "\"", filter, StringComparison.Ordinal);
    }

    // 使用者 2026-10-19 裁定第五步檢查句寫出欄位目前的設定；後端的名稱要和第三步下拉選單的選項文字相同。
    [Theory]
    [InlineData(RdeFieldValueTypeNames.Text)]
    [InlineData(RdeFieldValueTypeNames.Date)]
    [InlineData(RdeFieldValueTypeNames.Money)]
    public void RdeValueTypeDisplayName_MatchesTheMappingDropdown(string valueType)
    {
        var core = ReadFrontend("js", "ui-core.js");

        Assert.Contains(
            "{ value: '" + valueType + "', label: '" + RdeFieldValueTypeNames.DisplayName(valueType) + "' }",
            core,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 文字值清單（舊式條件 textSet）每條的值數上限，前端副本必須等於後端 <c>FilterScenarioLimits.MaxTextSetValuesPerRule</c>。
    /// 2026-10-14 原樣搬自 <c>FilterAstFrontendContractTests.TextSet_UsesLineDelimitedTextareaAndClosedModeOptions</c>
    /// （收尾四項第一項第二、三波整併）。前端行為測試讀不到後端常數，只能在這裡比對原始碼；
    /// 101 個值時預覽與儲存都擋下的行為，由 <c>tools/tests/frontend-journey-filter-saved.test.cjs</c> 的 journey 15 核對。
    /// </summary>
    [Fact]
    public void TextSetMaxValues_MatchesBackendTextSetLimit()
    {
        var core = ReadFrontend("js", "ui-core.js");

        var limitMatch = Regex.Match(
            core,
            @"var\s+TEXT_SET_MAX_VALUES\s*=\s*(?<value>\d+)\s*;",
            RegexOptions.CultureInvariant);
        Assert.True(limitMatch.Success, "ui-core.js 內找不到 TEXT_SET_MAX_VALUES。 ");
        Assert.Equal(
            FilterScenarioLimits.MaxTextSetValuesPerRule,
            int.Parse(limitMatch.Groups["value"].Value));
        Assert.Contains("TEXT_SET_MAX_VALUES: TEXT_SET_MAX_VALUES", core, StringComparison.Ordinal);
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
        // 第九節 E 移除自製月曆，改在加入日期前擋下已滿清單，仍引用同一上限。
        // 首次失敗：20261014-130014323-112f3ca2d352414ba9de606d2b6e0e09；100 筆與重複日期由 frontend-shared-controls.test.cjs 操作驗證。
        Assert.Contains("!duplicate && selected.length >= Ui.TYPED_SET_MAX_VALUES", values, StringComparison.Ordinal);
        Assert.Contains("'最多選取 ' + Ui.TYPED_SET_MAX_VALUES + ' 個不同日期，請先移除不需要的日期。'", values, StringComparison.Ordinal);
        // L81 counts the exact distinct strings that wire() sends, not a second uppercase-derived set.
        // First failure: 20261004-084834579-2436b90e840c4c53bec70aa81bf5a84b.
        // The Node behavior case also fixes the 100/101 mixed-case wire counts; keep this source-to-limit guard.
        var problem = ExtractFunctionBody(values, "problem(rule, state)");
        Assert.Contains("var value = wire(rule, state), mode = carrier(rule.operator, selected.type);", problem, StringComparison.Ordinal);
        Assert.Contains("var inputs = mode === 'set' ? value.values", problem, StringComparison.Ordinal);
        // 第九節 G2 讓尾數與一般清單共用超限指引，計數仍用 wire 的值；首次失敗：20261014-133317716-754ea282ab3842268400b7daca79b593。
        Assert.Contains("count > Ui.TYPED_SET_MAX_VALUES", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("toUpperCase", problem, StringComparison.Ordinal);
        // 使用者 2026-10-14 第三輪勾選頁確認 R3-08（第一次失敗：收據 20261014-012448805-68a8e8ea71bc4d6392585a586ab6b91e）。
        Assert.Contains("'請填 1 到 ' + Ui.TYPED_SET_MAX_VALUES + ' 個值'", values, StringComparison.Ordinal);
    }

    [Fact]
    public void SavedScenarioLimit_IsOneBackendConstantMirroredByTheFrontend()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var filter = ProductSources.FilterStepFrontend();

        Assert.Equal(FilterScenarioLimits.MaxSavedScenarios, ReadIntVar(core, "FILTER_MAX_SAVED_SCENARIOS"));
        Assert.Contains("FILTER_MAX_SAVED_SCENARIOS: FILTER_MAX_SAVED_SCENARIOS", core, StringComparison.Ordinal);
        Assert.Contains("scenarios.length > Ui.FILTER_MAX_SAVED_SCENARIOS", filter, StringComparison.Ordinal);
        // 情境名稱上限：前端輸入框鏡像後端的三種資料庫共用上限（使用者 2026-10-19 選「三種共用 400 字上限」）。
        Assert.Equal(FilterScenarioLimits.MaxNameLength, ReadIntVar(core, "FILTER_MAX_SCENARIO_NAME_LENGTH"));
        Assert.Contains("FILTER_MAX_SCENARIO_NAME_LENGTH: FILTER_MAX_SCENARIO_NAME_LENGTH", core, StringComparison.Ordinal);
        Assert.Contains("data-bind=\"scenario-name\" maxlength=\"' + Ui.FILTER_MAX_SCENARIO_NAME_LENGTH + '\"", filter, StringComparison.Ordinal);
        // 2026-10-03 用語統一 W10：前後端訊息一起改成「儲存」（第一次失敗：收據 20261003-023349721-0ccefea0a80c412aa8460624eaae563a）。
        Assert.Contains(
            // 使用者 2026-10-13 勾選頁確認 60（第一次失敗：Focused 收據 20261013-141351990-49476cfa2fd741ed9b2b96ce5334e3f0）。
            "'最多可儲存 ' + Ui.FILTER_MAX_SAVED_SCENARIOS + ' 個篩選情境，請先移除不需要的情境。'",
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
    /// 預篩選收斂計畫階段四（2026-10-23）：第五步各入口的停用原因讀 ui-core.js 的規則需求表，這張表逐項鏡像後端
    /// <see cref="JetRuleRequirements"/>，順序也相同，因為原因依表列的順序寫。預篩選比對每一個可篩選的鍵；條件型別比對每一種型別，
    /// 需要條件自己所選 GL 欄位的型別另列一份清單；GL 語意欄位的清單比對欄位目錄。
    /// 第一次失敗：Focused 收據 20261023-043250303-106f37c3021445109eac5f58b7560bbc（前端還沒有這張表）。
    /// </summary>
    [Fact]
    public void RulePrerequisiteTables_MirrorTheBackendRequirementTable()
    {
        var core = ReadFrontend("js", "ui-core.js");

        var prescreen = ReadPrescreenRuleTable(core);
        Assert.Equal(
            PrescreenRuleKeys.FilterableKeys.Order(StringComparer.Ordinal),
            prescreen.Keys.Order(StringComparer.Ordinal));
        foreach (var key in PrescreenRuleKeys.FilterableKeys)
        {
            Assert.Equal(Wire(JetRuleRequirements.PrescreenRules[key]), prescreen[key]);
        }

        var types = ReadPrerequisiteTable(core, "FILTER_RULE_PREREQUISITES");
        var fieldTypes = ReadStringArray(core, "FILTER_FIELD_RULE_TYPES");
        Assert.Empty(types.Keys.Concat(fieldTypes).Except(FilterConditionLabels.RuleTypes.Keys, StringComparer.Ordinal));
        foreach (var type in FilterConditionLabels.RuleTypes.Keys.Where(type => type != "prescreen"))
        {
            var withoutField = Wire(BackendPrerequisites(new() { ["type"] = type }));
            var withField = Wire(BackendPrerequisites(new() { ["type"] = type, ["field"] = JetFieldCatalog.GlCreateBy }));
            if (fieldTypes.Contains(type, StringComparer.Ordinal))
            {
                Assert.False(types.ContainsKey(type), type + " 由條件自己選的欄位決定，不另列需求");
                Assert.Empty(withoutField);
                Assert.Equal(["gl:" + JetFieldCatalog.GlCreateBy], withField);
            }
            else
            {
                Assert.True(withoutField.SequenceEqual(withField), type + " 的需求和條件選的欄位無關");
                Assert.Equal(withoutField, types.TryGetValue(type, out var frontend) ? frontend : []);
            }
        }

        // 欄位值比較用攸關資料元素欄位時不要求 GL 欄位；前端同樣跳過帶 fieldId 的條件。
        Assert.Empty(BackendPrerequisites(new() { ["type"] = "fieldValue", ["fieldId"] = "rde.synthetic", ["operator"] = "isBlank" }));
        Assert.Equal(
            JetFieldCatalog.GlFields.Select(field => field.SemanticIdentity),
            ReadStringArray(core, "GL_SEMANTIC_FIELDS"));
    }

    /// <summary>
    /// 同一階段：停用時的文字由後端的原因句決定，前端不另造句（計畫做法第 5 點）。不帶分類名稱的句子逐字出現在前端；
    /// 帶分類名稱的句子用合成名稱算出後端的句子，名稱前後的文字也逐字出現在前端。GL 欄位的句子加上前端那句下一步再加句號，
    /// 就是第四步同一條規則的句子；欄位名稱和第三步欄位配對的名稱相同。
    /// 第一次失敗：同上收據。
    /// </summary>
    [Fact]
    public void RulePrerequisiteReasons_AreTheBackendSentences()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var reasons = ExtractFunctionBody(core, "rulePrerequisiteReason(state, prerequisite)");
        foreach (var kind in new[]
                 {
                     JetRulePrerequisiteKind.LastPeriodStart, JetRulePrerequisiteKind.AccountMapping,
                     JetRulePrerequisiteKind.AnyAccountCategory, JetRulePrerequisiteKind.AuthorizedPreparers,
                     JetRulePrerequisiteKind.HolidayCalendar
                 })
        {
            Assert.Contains(
                "'" + JetRuleRequirements.Reason(new JetRulePrerequisite(kind), AccountCategoryLabels.BuiltIn) + "'",
                reasons,
                StringComparison.Ordinal);
        }

        // 使用者 2026-10-14 第三輪勾選頁確認 R3-15（第一次失敗：收據 20261014-012448805-68a8e8ea71bc4d6392585a586ab6b91e）：
        // 前端和後端同一句，分類名稱取第四步分類設定裡目前的名稱。原本由 AssertFrontendPrescreenEligibilityExpression 核對 filter-step.js。
        Assert.Contains("function label(role) { return taxonomyCategoryLabel(state, 'builtin.' + role); }", reasons, StringComparison.Ordinal);

        char[] sentinels = ['\u0001', '\u0002', '\u0003', '\u0004'];
        var labels = new AccountCategoryLabels("\u0001", "\u0002", "\u0003", "\u0004");
        foreach (var kind in new[] { JetRulePrerequisiteKind.RevenueCategory, JetRulePrerequisiteKind.CounterpartCategory })
        {
            foreach (var piece in JetRuleRequirements.Reason(new JetRulePrerequisite(kind), labels)
                         .Split(sentinels, StringSplitOptions.RemoveEmptyEntries))
            {
                Assert.Contains("'" + piece + "'", reasons, StringComparison.Ordinal);
            }
        }

        var unmapped = ReadStringVar(core, "GL_FIELD_UNMAPPED");
        var nextStep = ReadStringVar(core, "GL_FIELD_NEXT_STEP");
        foreach (var field in JetFieldCatalog.GlFields)
        {
            var label = JetFieldCatalog.GlSemanticFieldMappingLabel(field.SemanticIdentity);
            Assert.Contains("{ key: '" + field.SemanticIdentity + "', label: '" + label + "'", core, StringComparison.Ordinal);
            var prerequisite = new JetRulePrerequisite(JetRulePrerequisiteKind.GlField, field.SemanticIdentity);
            var reason = JetRuleRequirements.Reason(prerequisite, AccountCategoryLabels.BuiltIn);
            Assert.Equal("「" + label + "」" + unmapped, reason);
            Assert.Equal(reason + nextStep + "。", JetRuleRequirements.PrescreenSentence(prerequisite, AccountCategoryLabels.BuiltIn));
        }
    }

    /// <summary>
    /// 預篩選收斂計畫階段四（2026-10-23）：第五步編輯器把舊情境的排除區畫成唯讀的「舊排除區」，原因照抄後端擋下它的那一句，
    /// 前端不另造句。第一次失敗：Focused 收據 20261023-052228467-29884e1d40b84f99a4423752f808d8fb（前端還沒有這一句）。
    /// </summary>
    [Fact]
    public void LegacyExclusionsReason_IsTheBackendSentence()
    {
        var filter = ProductSources.FilterStepFrontend();

        Assert.Equal(FilterScenarioPayloadParser.RemovedExclusionsMessage, ReadStringVar(filter, "LEGACY_EXCLUSIONS_REASON"));
    }

    /// <summary>
    /// 第五步依 ui-core.js 的規則需求表與 rulePrerequisiteMet 停用條件；後端驗證器是權威。逐一把每個條件型別交給
    /// 後端驗證器，在每一種科目配對狀態下比對「前端可用」與「後端不因科目配對擋下」是否一致。
    /// 預篩選收斂計畫階段四（2026-10-23）拿掉型別上的 accountMappingRequirement 與 filter-step.js 的 availableRuleTypes，
    /// 前端可用改由需求表算；比對的型別與科目配對狀態不變。
    /// 第一次失敗：Focused 收據 20261023-045253734-62b9cd9c0604461dbc4f70ec63236833（filter-step.js 找不到 availableRuleTypes）。
    /// </summary>
    [Fact]
    public void AccountMappingRequirements_AgreeWithBackendValidatorForEveryRuleType()
    {
        var core = ReadFrontend("js", "ui-core.js");
        AssertFrontendPrerequisiteExpression(core);
        var types = ReadPrerequisiteTable(core, "FILTER_RULE_PREREQUISITES");

        var mismatches = new List<string>();
        foreach (var type in FilterConditionLabels.RuleTypes.Keys)
        {
            var prerequisites = types.TryGetValue(type, out var listed) ? listed : [];
            var rule = new Dictionary<string, object?> { ["type"] = type };
            foreach (var facts in MappingFactStates)
            {
                var frontendAvailable = FrontendMet(prerequisites, facts);
                var backendBlocks = BackendBlocksBecauseOfMapping(rule, facts);
                if (frontendAvailable == backendBlocks)
                {
                    mismatches.Add($"{type}（前端需求：{(prerequisites.Length == 0 ? "無" : string.Join("、", prerequisites))}；"
                        + $"科目配對狀態：{facts}；前端{(frontendAvailable ? "可用" : "停用")}，後端{(backendBlocks ? "擋下" : "放行")}）");
                }
            }
        }

        Assert.True(mismatches.Count == 0, "前後端科目配對需求不一致：\n" + string.Join("\n", mismatches));
    }

    /// <summary>
    /// 需求表上要科目分類的型別，在表上列的分類剛好都有時，一條完整合成規則要能通過後端驗證。
    /// 同一階段把最低需求改由需求表上的分類算，型別不變。第一次失敗：同上收據（型別上已經沒有 accountMappingRequirement）。
    /// </summary>
    [Theory]
    [InlineData("accountSide")]
    [InlineData("accountPair")]
    [InlineData("specialAccountCategoryPair")]
    [InlineData("revenueDebitNearQuarterEnd")]
    [InlineData("revenueWithoutNormalCounterpart")]
    [InlineData("manualRevenueEntry")]
    public void RequiredAccountMappingFacts_AreSufficientForTheBackend(string type)
    {
        var table = ReadPrerequisiteTable(ReadFrontend("js", "ui-core.js"), "FILTER_RULE_PREREQUISITES");
        var prerequisites = table.TryGetValue(type, out var listed) ? listed : [];
        var revenue = prerequisites.Contains("revenueCategory");
        var counterpart = prerequisites.Contains("counterpartCategory");
        Assert.True(revenue || counterpart || prerequisites.Contains("anyAccountCategory"), type + " 在需求表上要科目分類");

        // 有收入或對方分類時，一定也有任一分類。
        var minimal = new MappingFacts(true, true, revenue, counterpart);
        Assert.True(FrontendMet(prerequisites, minimal));
        Assert.Empty(Validate(ValidSample(type), minimal));
    }

    /// <summary>
    /// 預篩選鍵的科目配對需求：前端依同一張需求表停用，每個鍵在每一種科目配對狀態下和後端驗證器比對。
    /// 同一階段拿掉 PRESCREEN_KEY_OPTIONS 的 requiresAccountMapping 與 prescreenRequirementNote 裡未預期借貸組合的特例，
    /// 改由需求表算；比對的鍵與科目配對狀態不變。第一次失敗：同上收據（prescreenRequirementNote 裡找不到 requiresAccountMapping 的判斷）。
    /// </summary>
    [Fact]
    public void PrescreenKeyAccountMappingRequirements_AgreeWithBackendValidator()
    {
        var core = ReadFrontend("js", "ui-core.js");
        AssertFrontendPrerequisiteExpression(core);
        AssertFrontendPrescreenEligibilityExpression(ProductSources.FilterStepFrontend());
        var table = ReadPrescreenRuleTable(core);

        var mismatches = new List<string>();
        foreach (var key in PrescreenRuleKeys.FilterableKeys)
        {
            var prerequisites = table.TryGetValue(key, out var listed) ? listed : [];
            var rule = new Dictionary<string, object?> { ["type"] = "prescreen", ["prescreenKey"] = key };
            foreach (var facts in MappingFactStates)
            {
                // R7 changed the actual frontend helper, not this metadata flag. The old test reproduced
                // only requiresAccountMapping and therefore missed the new two-summary-flag requirement.
                // First failures: 20261004-084834579-2436b90e840c4c53bec70aa81bf5a84b,
                // 20261004-085118314-9c43f4a2a69c4f718bab4f9f3d6aaa11. Keep every snapshot and every key.
                if (FrontendMet(prerequisites, facts) == BackendBlocksBecauseOfMapping(rule, facts))
                {
                    mismatches.Add($"{key}（科目配對狀態：{facts}）");
                }
            }
        }

        Assert.True(mismatches.Count == 0, "預篩選鍵的科目配對需求不一致：\n" + string.Join("\n", mismatches));
    }

    /// <summary>
    /// 預篩選鍵與條件型別需要的 GL 欄位：前端需求表和後端驗證器一致。每個鍵、每個型別都逐一拿掉一個 GL 欄位，
    /// 看後端是否因此擋下；條件自己選欄位的型別，每個 GL 欄位各比對一次。
    /// 2026-10-16 複審找到第五步「預篩選」分頁在沒有傳票建立人員時沒有停用「編製分錄較少的人員」與「非授權編製人員」，
    /// 按下後一預覽就被後端擋下，所以原本這裡逐一核對每個預篩選鍵的傳票建立人員需求（PrescreenKeyCreatedByRequirements_AgreeWithBackendValidator）。
    /// 預篩選收斂計畫階段四（2026-10-23）拿掉 requiresCreateBy，第五步不能用任何 GL 沒有配對的欄位（使用者 2026-10-22 裁定），
    /// 這裡改成核對每個 GL 欄位，條件型別也一起核對。
    /// 第一次失敗：同上收據（prescreenRequirementNote 裡找不到 requiresCreateBy 的判斷）。
    /// </summary>
    [Fact]
    public void GlFieldRequirements_AgreeWithBackendValidator()
    {
        var core = ReadFrontend("js", "ui-core.js");
        AssertFrontendPrerequisiteExpression(core);
        AssertFrontendPrescreenEligibilityExpression(ProductSources.FilterStepFrontend());
        var prescreen = ReadPrescreenRuleTable(core);
        var types = ReadPrerequisiteTable(core, "FILTER_RULE_PREREQUISITES");
        var fieldTypes = ReadStringArray(core, "FILTER_FIELD_RULE_TYPES");
        var allFields = JetFieldCatalog.GlFields.Select(field => field.SemanticIdentity).ToArray();

        var samples = new List<(string Name, Dictionary<string, object?> Rule, string[] Frontend)>();
        foreach (var key in PrescreenRuleKeys.FilterableKeys)
        {
            samples.Add((key, new Dictionary<string, object?> { ["type"] = "prescreen", ["prescreenKey"] = key },
                prescreen.TryGetValue(key, out var listed) ? listed : []));
        }

        foreach (var type in FilterConditionLabels.RuleTypes.Keys.Where(type => type != "prescreen"))
        {
            if (!fieldTypes.Contains(type, StringComparer.Ordinal))
            {
                samples.Add((type, new Dictionary<string, object?> { ["type"] = type }, types.TryGetValue(type, out var listed) ? listed : []));
                continue;
            }

            // 前端 filterRulePrerequisites：這幾種型別需要的是條件自己選的 GL 欄位。
            foreach (var field in allFields)
            {
                samples.Add((type + "（" + field + "）", new Dictionary<string, object?> { ["type"] = type, ["field"] = field }, ["gl:" + field]));
            }
        }

        var mismatches = new List<string>();
        foreach (var (name, rule, frontend) in samples)
        {
            var withEveryField = Validate(rule, FullFacts, allFields);
            var backend = allFields
                .Where(field => Validate(rule, FullFacts, allFields.Where(other => other != field).ToArray())
                    .Except(withEveryField, StringComparer.Ordinal).Any())
                .Select(field => "gl:" + field)
                .ToArray();
            var frontendFields = frontend.Where(prerequisite => prerequisite.StartsWith("gl:", StringComparison.Ordinal)).ToArray();
            if (!backend.Order(StringComparer.Ordinal).SequenceEqual(frontendFields.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            {
                mismatches.Add($"{name}（前端：{string.Join("、", frontendFields)}；後端：{string.Join("、", backend)}）");
            }
        }

        Assert.True(mismatches.Count == 0, "前後端 GL 欄位需求不一致：\n" + string.Join("\n", mismatches));
    }

    // ui-core.js rulePrerequisiteMet 的原文；FrontendMet 是它對科目配對的 C# 對應。第五步各入口都經過 ruleRequirementNote，
    // 它依需求表與這個判斷查每條條件。
    private static void AssertFrontendPrerequisiteExpression(string core)
    {
        var met = ExtractFunctionBody(core, "rulePrerequisiteMet(state, prerequisite)");
        Assert.Contains("var imp = (state && state.importState) || {}, mapping = imp.accountMapping;", met, StringComparison.Ordinal);
        Assert.Contains("if (prerequisite.indexOf('gl:') === 0) { return glFieldAvailable(state, prerequisite.slice(3)); }", met, StringComparison.Ordinal);
        Assert.Contains("case 'accountMapping': return !!mapping;", met, StringComparison.Ordinal);
        Assert.Contains("case 'anyAccountCategory': return !!(mapping && mapping.hasAnyCategory);", met, StringComparison.Ordinal);
        Assert.Contains("case 'revenueCategory': return !!(mapping && mapping.hasRevenue);", met, StringComparison.Ordinal);
        Assert.Contains("case 'counterpartCategory': return !!(mapping && mapping.hasCounterpart);", met, StringComparison.Ordinal);
        // 每一種前置條件都有自己的判斷：後端新增種類時這裡失敗，前端不會把它當成已成立。原本由
        // FilterTemplatesFrontendTests.AssertClosedRequirements 核對範例的 requires 只用畫面認得的幾種
        // （第一次失敗：Focused 收據 20261023-045313479-2a121804a0e04b518609f80bf108ad65）。
        Assert.Equal(
            Enum.GetValues<JetRulePrerequisiteKind>()
                .Where(kind => kind != JetRulePrerequisiteKind.GlField)
                .SelectMany(kind => Wire([new JetRulePrerequisite(kind)]))
                .Order(StringComparer.Ordinal),
            Regex.Matches(met, @"case '(?<kind>\w+)':").Select(match => match.Groups["kind"].Value).Order(StringComparer.Ordinal));

        var note = ExtractFunctionBody(core, "ruleRequirementNote(state, rules, ownOnly)");
        Assert.Contains("filterRulePrerequisites(rule).forEach(function (prerequisite) {", note, StringComparison.Ordinal);
        Assert.Contains("rulePrerequisiteMet(state, prerequisite)", note, StringComparison.Ordinal);
    }

    // 預篩選分頁與預篩選條件卡都依 prescreenRequirementNote 停用；它把鍵交給同一張需求表，沒有任何鍵的特例。
    // 原本這裡另外鎖住 R3-15 的句子，現在句子和分類名稱的來源由 RulePrerequisiteReasons_AreTheBackendSentences 核對。
    private static void AssertFrontendPrescreenEligibilityExpression(string filter)
    {
        var available = ExtractFunctionBody(filter, "availablePrescreenKeys()");
        Assert.Contains("return !prescreenRequirementNote(o.value);", available, StringComparison.Ordinal);
        var note = ExtractFunctionBody(filter, "prescreenRequirementNote(key)");
        Assert.Contains("return known ? requirementNote([{ type: 'prescreen', prescreenKey: key }]) : RETIRED_TYPE_NOTE;", note, StringComparison.Ordinal);
        Assert.Contains(
            "return Ui.ruleRequirementNote(Store.getState(), rules);",
            ExtractFunctionBody(filter, "requirementNote(rules)"),
            StringComparison.Ordinal);
        // A newly introduced special-case key cannot silently evade the complete matrix above.
        Assert.DoesNotContain("key === '", note, StringComparison.Ordinal);
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

    // rulePrerequisiteMet 對科目配對的判斷在 C# 端的對應；AssertFrontendPrerequisiteExpression 鎖住原文。其他前置條件在
    // Validate 裡都成立：有準備日、有授權編製人員清單、不帶可用欄位清單，第五步也不檢查假日檔。
    private static bool FrontendMet(IEnumerable<string> prerequisites, MappingFacts facts) => prerequisites.All(prerequisite => prerequisite switch
    {
        "accountMapping" => facts.Imported,
        "anyAccountCategory" => facts.Imported && facts.HasAnyCategory,
        "revenueCategory" => facts.Imported && facts.HasRevenue,
        "counterpartCategory" => facts.Imported && facts.HasCounterpart,
        _ => true
    });

    // 後端是否「因為科目配對」擋下：同一條規則在完整資格下沒有、在此狀態下才出現的錯誤。
    private static bool BackendBlocksBecauseOfMapping(Dictionary<string, object?> rule, MappingFacts facts)
    {
        var withFacts = Validate(rule, facts);
        var withFullFacts = Validate(rule, FullFacts);
        return withFacts.Except(withFullFacts, StringComparer.Ordinal).Any();
    }

    private static IReadOnlyList<string> Validate(Dictionary<string, object?> rule, MappingFacts facts,
        IReadOnlyList<string>? availableGlFields = null)
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
            // 預篩選收斂計畫階段四拿掉四個欄位旗標，改由規則需求表讀可用欄位清單（2026-10-23）。
            // 第一次失敗：收據 20261023-031856475-04f811a69e3346428de91091f0d02655（編譯失敗）。
            AvailableGlFields = availableGlFields
        };
        return FilterScenarioValidator.Validate(spec, context);
    }

    private static Dictionary<string, object?> ValidSample(string type) => type switch
    {
        "accountSide" => new()
        {
            ["type"] = type,
            // 第九節 G1、G5：科目分類只判斷分類；首次失敗：20261014-135448833-0915d3416a0e48208774011f8499f528。
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

    // 後端規則需求表對一條合成條件的答案：條件先經過正式的 payload 解析，再交給 JetRuleRequirements.ForFilterRule。
    private static IReadOnlyList<JetRulePrerequisite> BackendPrerequisites(Dictionary<string, object?> rule)
    {
        var scenario = new
        {
            name = "synthetic scenario",
            rationale = "synthetic rationale",
            groups = new[] { new { join = "AND", matchScope = "row", rules = new object[] { rule } } }
        };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(scenario));
        var spec = FilterScenarioPayloadParser.Parse(document.RootElement, moneyScale: 100);
        return JetRuleRequirements.ForFilterRule(spec.Groups[0].Rules[0]);
    }

    // 前端表的寫法：GL 欄位寫成「gl:語意欄位」，其他前置條件寫種類名稱，首字母小寫。
    private static string[] Wire(IEnumerable<JetRulePrerequisite> prerequisites) => prerequisites
        .Select(prerequisite => prerequisite.Kind == JetRulePrerequisiteKind.GlField
            ? "gl:" + prerequisite.GlField
            : char.ToLowerInvariant(prerequisite.Kind.ToString()[0]) + prerequisite.Kind.ToString()[1..])
        .ToArray();

    // 預篩選收斂計畫階段九：前端只留一份 PRESCREEN_RULES，每條規則的 prerequisites 寫在同一筆。
    // 第一次失敗：Focused 收據 20261024-195604731-a54ac30c8a994b46878d21f5b82967bf。
    private static Dictionary<string, string[]> ReadPrescreenRuleTable(string source)
    {
        var match = Regex.Match(
            source,
            @"var\s+PRESCREEN_RULES\s*=\s*\[(?<body>.*?)\n  \];",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        Assert.True(match.Success, "ui-core.js 內找不到 PRESCREEN_RULES。");
        return Regex.Matches(
                match.Groups["body"].Value,
                @"value:\s*'(?<key>\w+)'[^{}]*?prerequisites:\s*\[(?<items>[^\]]*)\]",
                RegexOptions.Singleline | RegexOptions.CultureInvariant)
            .ToDictionary(
                item => item.Groups["key"].Value,
                item => Regex.Matches(item.Groups["items"].Value, @"'(?<value>[^']+)'")
                    .Select(value => value.Groups["value"].Value)
                    .ToArray(),
                StringComparer.Ordinal);
    }

    private static Dictionary<string, string[]> ReadPrerequisiteTable(string source, string name)
    {
        var match = Regex.Match(
            source,
            @"var\s+" + name + @"\s*=\s*\{(?<body>.*?)\n  \};",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"ui-core.js 內找不到 {name}。");
        return Regex.Matches(match.Groups["body"].Value, @"(?<key>\w+):\s*\[(?<items>[^\]]*)\]")
            .ToDictionary(
                item => item.Groups["key"].Value,
                item => Regex.Matches(item.Groups["items"].Value, @"'(?<value>[^']+)'")
                    .Select(value => value.Groups["value"].Value)
                    .ToArray(),
                StringComparer.Ordinal);
    }

    private static string[] ReadStringArray(string source, string name)
    {
        var match = Regex.Match(source, @"var\s+" + name + @"\s*=\s*\[(?<body>[^\]]*)\];", RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"ui-core.js 內找不到 {name}。");
        return Regex.Matches(match.Groups["body"].Value, @"'(?<value>[^']+)'").Select(value => value.Groups["value"].Value).ToArray();
    }

    private static string ReadStringVar(string source, string name)
    {
        var match = Regex.Match(source, @"var\s+" + name + @"\s*=\s*'(?<value>[^']*)';", RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"前端原始碼內找不到 {name}。");
        return match.Groups["value"].Value;
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
