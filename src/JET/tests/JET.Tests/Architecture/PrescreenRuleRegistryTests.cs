using System.Text.RegularExpressions;
using JET.Domain;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 預篩選收斂計畫階段九（使用者 2026-10-22 裁定「一條規則一個定義」）：規則的身分只登錄一份、述詞每條只有一個方法、
/// 第四步與第五步三個入口都從同一張對應表取述詞，前端只留一份鏡像。這裡讀原始碼確認各處不再各自列規則；
/// 行為由 PrescreenRuleFixedAnswerTests 的固定答案鎖住。第一次失敗：收據 20261024-190913880-a45e823f31e745139f977c5c3481bdb3。
/// </summary>
public sealed class PrescreenRuleRegistryTests
{
    private static readonly string[] Slugs =
    [
        "post_period_approval", "suspicious_keywords", "unexpected_account_pair", "trailing_zeros",
        "creator_summary", "rare_accounts", "weekend_posting", "weekend_approval", "holiday_posting", "holiday_approval",
        "blank_description", "non_authorized_preparer", "low_frequency_preparer", "low_frequency_account"
    ];

    private static readonly string[] RowTagConstants =
    [
        "PostPeriodApproval", "SuspiciousKeywords", "UnexpectedAccountPair", "TrailingZeros",
        "WeekendPosting", "WeekendApproval", "HolidayPosting", "HolidayApproval",
        "BlankDescription", "NonAuthorizedPreparer", "LowFrequencyPreparer", "LowFrequencyAccount"
    ];

    private static readonly string[] RetiredTypes =
    [
        "customKeywords", "customTrailingZeros", "trailingDigits", "customPreparerEntryCount", "customAccountEntryCount"
    ];

    [Fact]
    public void RuleIdentity_IsRegisteredOnceInTheDomainDefinitions()
    {
        var definitions = ReadProduct("Domain", "Rules", "PrescreenRuleDefinitions.cs");
        foreach (var slug in Slugs)
        {
            Assert.Contains($"\"{slug}\"", definitions, StringComparison.Ordinal);
        }

        // 其他四處只引用定義，不再各自列一份規則。
        var catalog = ReadProduct("AuditCore", "RuleCatalog.cs");
        var program = ReadProduct("AuditCore", "JetAuditProgram.cs");
        var labels = ReadProduct("Domain", "Rules", "FilterConditionLabels.cs");
        var requirements = ReadProduct("Domain", "Rules", "JetRuleRequirements.cs");
        var keys = ReadProduct("Domain", "Rules", "PrescreenRules.cs");
        foreach (var slug in Slugs)
        {
            Assert.DoesNotContain($"\"{slug}\"", catalog, StringComparison.Ordinal);
            Assert.DoesNotContain($"PrescreenDefinition(\"{slug}\")", program, StringComparison.Ordinal);
        }
        Assert.Contains("PrescreenRuleDefinitions", catalog, StringComparison.Ordinal);
        Assert.Contains("PrescreenRuleDefinitions", program, StringComparison.Ordinal);
        Assert.DoesNotContain("[PrescreenRuleKeys.", labels, StringComparison.Ordinal);
        Assert.Contains("PrescreenRuleDefinitions", labels, StringComparison.Ordinal);
        Assert.DoesNotContain("[PrescreenRuleKeys.", requirements, StringComparison.Ordinal);
        Assert.Contains("PrescreenRuleDefinitions", requirements, StringComparison.Ordinal);
        Assert.DoesNotContain("new HashSet<string>", keys, StringComparison.Ordinal);
        Assert.Contains("PrescreenRuleDefinitions", keys, StringComparison.Ordinal);
    }

    [Fact]
    public void Predicates_HaveOneMethodPerRule_AndTheFieldConditionsCallTheSameMethods()
    {
        var predicates = ReadProduct("AuditCore", "GlRulePredicates.cs");
        var values = ReadProduct("AuditCore", "GlRulePredicates.Values.cs");
        var frequency = ReadProduct("AuditCore", "GlRulePredicates.Frequency.cs");

        // 尾零併入尾數樣態；低頻人員與低頻科目改由統計條件同一個方法產生；自訂關鍵字與摘要特定描述共用同一個包含比對。
        Assert.DoesNotContain("string TrailingZeros(", predicates, StringComparison.Ordinal);
        Assert.DoesNotContain("string LowFrequencyPreparer(", predicates, StringComparison.Ordinal);
        Assert.DoesNotContain("string LowFrequencyAccount(", predicates, StringComparison.Ordinal);
        Assert.DoesNotContain("string CustomKeywords(", predicates, StringComparison.Ordinal);
        Assert.Contains("string ContainsAnyKeyword(", predicates, StringComparison.Ordinal);
        Assert.Contains("string BlankText(", predicates, StringComparison.Ordinal);
        Assert.Contains("string EntityFrequency(", frequency, StringComparison.Ordinal);

        // 欄位條件的摘要包含、空白、週末、假日、補班日都呼叫同一個方法，不自己拼 SQL。
        Assert.Contains("ContainsAnyKeyword(", values, StringComparison.Ordinal);
        Assert.Contains("BlankText(", values, StringComparison.Ordinal);
        Assert.Contains("Weekend(", values, StringComparison.Ordinal);
        Assert.Contains("Holiday(", values, StringComparison.Ordinal);
        Assert.Contains("MakeupDay(", values, StringComparison.Ordinal);
        Assert.DoesNotContain("staging_calendar_raw_day", values, StringComparison.Ordinal);
        Assert.DoesNotContain("WeekendPredicate(", values, StringComparison.Ordinal);
    }

    [Fact]
    public void Compilation_AndBothPrescreenRepositories_TakePredicatesFromTheRegisteredMap()
    {
        var map = ReadProduct("AuditCore", "PrescreenRulePredicates.cs");
        foreach (var constant in RowTagConstants)
        {
            Assert.Contains("PrescreenRuleKeys." + constant, map, StringComparison.Ordinal);
        }

        var compilation = ReadProduct("AuditCore", "FilterCompilation.cs");
        Assert.DoesNotContain("PrescreenRuleKeys.PostPeriodApproval =>", compilation, StringComparison.Ordinal);
        Assert.Contains("PrescreenRulePredicates", compilation, StringComparison.Ordinal);

        var local = ReadProduct("Infrastructure", "Persistence", "Local", "LocalPrescreenRunRepository.cs");
        var sqlServer = ReadProduct("Infrastructure", "Persistence", "SqlServer", "SqlServerPrescreenRunRepository.cs");
        foreach (var repository in new[] { local, sqlServer })
        {
            Assert.Contains("PrescreenRulePredicates", repository, StringComparison.Ordinal);
            foreach (var call in new[]
            {
                "Predicates.PostPeriodApproval(", "Predicates.SuspiciousKeywords(", "Predicates.UnexpectedAccountPair(",
                "Predicates.TrailingZeros(", "Predicates.Weekend(", "Predicates.Holiday(", "Predicates.BlankDescription(",
                "Predicates.NonAuthorizedPreparer(", "Predicates.LowFrequencyPreparer(", "Predicates.LowFrequencyAccount("
            })
            {
                Assert.DoesNotContain(call, repository, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Frontend_KeepsOneMirrorTable_AndTheTypeTableDropsRetiredTypes()
    {
        var core = ReadFrontend("js", "ui-core.js");
        Assert.Contains("var PRESCREEN_RULES = [", core, StringComparison.Ordinal);
        Assert.DoesNotContain("PRESCREEN_KEY_OPTIONS = [", core, StringComparison.Ordinal);
        Assert.DoesNotContain("PRESCREEN_RULE_PREREQUISITES = {", core, StringComparison.Ordinal);
        Assert.DoesNotContain("PRESCREEN_DESCRIPTIONS = {", core, StringComparison.Ordinal);

        var types = FrontendConstantTextReader.ValueLabels(core, "FILTER_RULE_TYPES");
        foreach (var type in RetiredTypes)
        {
            Assert.False(types.ContainsKey(type), $"型別表仍列出已停用的 {type}");
        }
        Assert.Equal(
            FilterConditionLabels.RuleTypes.Keys.Except(RetiredTypes, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal),
            types.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (key, label) in types)
        {
            Assert.Equal(FilterConditionLabels.RuleTypes[key], label);
        }

        // 已停用的型別只留一份清單給舊情境讀回用。
        var retired = Regex.Match(core, @"var RETIRED_RULE_TYPES = \[(?<body>[^\]]*)\];");
        Assert.True(retired.Success, "ui-core.js 找不到 RETIRED_RULE_TYPES");
        var listed = Regex.Matches(retired.Groups["body"].Value, @"'(?<type>[A-Za-z]+)'").Select(m => m.Groups["type"].Value).ToArray();
        Assert.Equal(RetiredTypes.OrderBy(k => k, StringComparer.Ordinal), listed.OrderBy(k => k, StringComparer.Ordinal));
    }

    private static string ReadProduct(params string[] segments) =>
        File.ReadAllText(Path.Combine([Root(), "JET", .. segments]));

    private static string ReadFrontend(params string[] segments) =>
        File.ReadAllText(Path.Combine([Root(), "JET", "wwwroot", .. segments]));

    /// <summary>由測試組件位置向上尋 JET.slnx（同 FilterConditionLabelMirrorTests 的慣例）。</summary>
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "JET.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("向上尋不到 JET.slnx，無法定位 repo 根。");
    }
}
