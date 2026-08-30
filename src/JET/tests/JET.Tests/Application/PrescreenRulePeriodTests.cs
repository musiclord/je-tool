using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Architecture;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// prescreen.run 的全期規則命中分布（rulePeriod）wire 契約。
/// Oracle 來自四列手算母體；前置不足與「已執行但零命中」刻意分開，
/// 不依賴既有 count DTO 反推預期值。
/// </summary>
public sealed class PrescreenRulePeriodTests
{
    private static readonly string[] ExpectedRuleKeys =
    [
        "postPeriodApproval",
        "suspiciousKeywords",
        "unexpectedAccountPair",
        "trailingZeros",
        "weekendPosting",
        "weekendApproval",
        "holidayPosting",
        "holidayApproval",
        "blankDescription",
        "backdatedPosting",
        "nonAuthorizedPreparer",
        "lowFrequencyPreparer",
        "lowFrequencyAccount"
    ];

    private static readonly IReadOnlyDictionary<string, RuleOracle> FourRowOracle =
        new Dictionary<string, RuleOracle>(StringComparer.Ordinal)
        {
            ["postPeriodApproval"] = new(2, 1, 50.0m),
            ["suspiciousKeywords"] = new(2, 1, 50.0m),
            ["trailingZeros"] = new(2, 1, 50.0m),
            ["weekendPosting"] = new(2, 1, 50.0m),
            ["weekendApproval"] = new(2, 1, 50.0m),
            ["holidayPosting"] = new(2, 1, 50.0m),
            ["holidayApproval"] = new(4, 2, 100.0m),
            ["blankDescription"] = new(1, 1, 25.0m),
            ["backdatedPosting"] = new(2, 1, 50.0m),
            ["lowFrequencyPreparer"] = new(4, 2, 100.0m),
            ["lowFrequencyAccount"] = new(4, 2, 100.0m)
        };

    [Fact]
    public async Task PrescreenRun_RulePeriod_Sqlite_MatchesFourRowHandComputedOracle()
    {
        using var host = new HandlerTestHost();
        await SetupFourRowProjectAsync(host, "sqlite");

        var data = await host.DispatchAsync("prescreen.run");

        AssertFourRowOracle(data);
    }

    [Fact]
    public async Task PrescreenRun_RulePeriod_DuckDb_MatchesFourRowHandComputedOracle()
    {
        using var host = new HandlerTestHost();
        await SetupFourRowProjectAsync(host, "duckdb");

        var data = await host.DispatchAsync("prescreen.run");

        AssertFourRowOracle(data);
    }

    [SqlServerFact]
    public async Task PrescreenRun_RulePeriod_SqlServer_MatchesFourRowHandComputedOracle()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return;
        }

        using var host = new HandlerTestHost(sqlServerConnectionString: connectionString);
        try
        {
            await SetupFourRowProjectAsync(host, "sqlServer");

            var data = await host.DispatchAsync("prescreen.run");

            AssertFourRowOracle(data);
        }
        finally
        {
            foreach (var directory in Directory.GetDirectories(host.ProjectsRoot))
            {
                await TempSqlServerProject.DropDatabaseAsync(
                    connectionString,
                    Path.GetFileName(directory));
            }
        }
    }

    [Fact]
    public async Task MappingCommit_EmptyEffectivePeriod_RejectsBeforePrescreen()
    {
        using var host = new HandlerTestHost();
        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            InlineWorkbookProject.SetupAsync(
                host,
                BuildFourRowGl,
                lastPeriodStart: "2026-12-31",
                holidays: ["2026-03-08"],
                periodStart: "2026-01-01",
                periodEnd: "2026-12-31",
                validateForDownstream: true));

        Assert.Equal(JetErrorCodes.EmptyEffectivePopulation, exception.Code);
        Assert.Contains("rollback", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PrescreenRun_RulePeriod_CrossYearPeriod_IncludesBothBoundariesAndExcludesOutsideRows()
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(
            host,
            BuildCrossYearGl,
            lastPeriodStart: "2025-01-01",
            holidays: ["2024-12-31"],
            periodStart: "2024-12-31",
            periodEnd: "2025-01-01",
            validateForDownstream: true);

        var data = await host.DispatchAsync("prescreen.run");
        var rulePeriod = data.GetProperty("rulePeriod");
        var rules = ReadRules(rulePeriod);

        // 四列來源只有兩個閉區間邊界列屬母體；兩個界外列都刻意會改變下列 oracle。
        Assert.Equal(2, rulePeriod.GetProperty("population").GetInt64());
        AssertApplicable(rules["postPeriodApproval"], new RuleOracle(1, 1, 50.0m));
        AssertApplicable(rules["blankDescription"], new RuleOracle(1, 1, 50.0m));
        AssertApplicable(rules["lowFrequencyPreparer"], new RuleOracle(2, 2, 100.0m));
        AssertApplicable(rules["lowFrequencyAccount"], new RuleOracle(2, 2, 100.0m));
        AssertApplicable(rules["suspiciousKeywords"], new RuleOracle(0, 0, 0.0m));
        AssertApplicable(rules["weekendPosting"], new RuleOracle(0, 0, 0.0m));
    }

    [Fact]
    public async Task ProjectLoad_OldPrescreenSummary_ReplaysWithoutFabricatingRulePeriod()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(host, BuildFourRowGl);
        await SaveLegacyPrescreenSummaryAsync(host, projectId);

        var loaded = await host.DispatchAsync(
            "project.load",
            JsonSerializer.Serialize(new { projectId }));

        var resumed = loaded.GetProperty("latestRuns").GetProperty("prescreen");
        Assert.Equal(7, resumed.GetProperty("suspiciousKeywords").GetProperty("count").GetInt64());
        Assert.False(resumed.TryGetProperty("rulePeriod", out _));
    }

    [Fact]
    public async Task PrescreenRun_RulePeriod_EmitsExactlyTheArchitectureWireKeys()
    {
        using var host = new HandlerTestHost();
        await SetupFourRowProjectAsync(host, "sqlite");

        var data = await host.DispatchAsync("prescreen.run");
        var rulePeriod = data.GetProperty("rulePeriod");
        var rules = rulePeriod.GetProperty("rules").EnumerateArray().ToArray();

        AssertPropertyNames(RulePeriodWireKeys.Block, rulePeriod);
        Assert.All(rules, rule => AssertPropertyNames(RulePeriodWireKeys.RuleRow, rule));
    }

    private static Task<string> SetupFourRowProjectAsync(
        HandlerTestHost host,
        string databaseProvider) =>
        InlineWorkbookProject.SetupAsync(
            host,
            BuildFourRowGl,
            lastPeriodStart: "2025-12-31",
            holidays: ["2025-03-08", "2025-03-09", "2025-12-31"],
            databaseProvider: databaseProvider,
            validateForDownstream: true);

    /// <summary>
    /// 手算母體共四列、兩張傳票：
    /// A 兩列同時命中期末後核准、關鍵字、六位連續零、週末／假日過帳及回溯過帳；
    /// B 兩列命中週末核准，且只有第一列摘要空白；兩張傳票的核准日都是假日。
    /// 所有建立人與科目使用次數都不超過 11，故兩個低頻規則命中四列。
    /// </summary>
    private static void BuildFourRowGl(InlineGlWorkbookBuilder builder)
    {
        builder
            .WithColumns(
                "傳票號碼",
                "傳票項次",
                "傳票日期",
                "傳票日期_僅條件_JE",
                "核准日期",
                "科目代號",
                "科目名稱",
                "摘要",
                "建立人員",
                "金額",
                "借方旗標")
            .AddRow(
                "JV-A", "1", "2025-03-08", "2025-03-10", "2025-12-31",
                "A100", "科目 A100", "調整分錄", "C01", "1000000.00", 1)
            .AddRow(
                "JV-A", "2", "2025-03-08", "2025-03-10", "2025-12-31",
                "A200", "科目 A200", "調整分錄", "C01", "1000000.00", 0)
            .AddRow(
                "JV-B", "1", "2025-03-10", "2025-03-10", "2025-03-09",
                "A300", "科目 A300", null, "C02", "12.34", 1)
            .AddRow(
                "JV-B", "2", "2025-03-10", "2025-03-10", "2025-03-09",
                "A400", "科目 A400", "一般分錄", "C02", "12.34", 0);
    }

    private static void BuildCrossYearGl(InlineGlWorkbookBuilder builder)
    {
        builder
            .WithColumns(
                "傳票號碼",
                "傳票項次",
                "傳票日期",
                "傳票日期_僅條件_JE",
                "核准日期",
                "科目代號",
                "科目名稱",
                "摘要",
                "建立人員",
                "金額",
                "借方旗標")
            .AddRow(
                "JV-BEFORE", "1", "2024-12-30", "2024-12-31", "2025-01-02",
                "X100", "界外前", null, "X01", "12.34", 1)
            .AddRow(
                "JV-START", "1", "2024-12-31", "2024-12-31", "2024-12-31",
                "X200", "期初邊界", null, "X02", "12.34", 1)
            .AddRow(
                "JV-END", "1", "2025-01-01", "2025-01-01", "2025-01-01",
                "X300", "期末邊界", "一般分錄", "X03", "12.34", 1)
            .AddRow(
                "JV-AFTER", "1", "2025-01-02", "2025-01-02", "2025-01-02",
                "X400", "界外後", null, "X04", "12.34", 1);
    }

    private static void AssertFourRowOracle(JsonElement data)
    {
        var rulePeriod = data.GetProperty("rulePeriod");
        Assert.Equal(4, rulePeriod.GetProperty("population").GetInt64());

        var orderedRules = rulePeriod.GetProperty("rules").EnumerateArray().ToArray();
        Assert.Equal(
            ExpectedRuleKeys,
            orderedRules.Select(rule => rule.GetProperty("key").GetString()).ToArray());

        var rules = ReadRules(rulePeriod);
        foreach (var (key, expected) in FourRowOracle)
        {
            AssertApplicable(rules[key], expected);
        }

        AssertNotApplicable(
            rules["unexpectedAccountPair"],
            "需先匯入科目配對。");
        AssertNotApplicable(
            rules["nonAuthorizedPreparer"],
            "需先匯入授權編製人員清單。");
    }

    private static IReadOnlyDictionary<string, JsonElement> ReadRules(JsonElement rulePeriod) =>
        rulePeriod.GetProperty("rules")
            .EnumerateArray()
            .ToDictionary(
                rule => rule.GetProperty("key").GetString()!,
                rule => rule,
                StringComparer.Ordinal);

    private static void AssertApplicable(JsonElement rule, RuleOracle expected)
    {
        Assert.Equal(JsonValueKind.Null, rule.GetProperty("naReason").ValueKind);
        Assert.Equal(expected.HitLines, rule.GetProperty("hitLines").GetInt64());
        Assert.Equal(expected.HitVouchers, rule.GetProperty("hitVouchers").GetInt64());
        Assert.Equal(expected.RatePct, rule.GetProperty("ratePct").GetDecimal());
    }

    private static void AssertNotApplicable(JsonElement rule, string expectedReason)
    {
        Assert.Equal(expectedReason, rule.GetProperty("naReason").GetString());
        Assert.Equal(JsonValueKind.Null, rule.GetProperty("hitLines").ValueKind);
        Assert.Equal(JsonValueKind.Null, rule.GetProperty("hitVouchers").ValueKind);
        Assert.Equal(JsonValueKind.Null, rule.GetProperty("ratePct").ValueKind);
    }

    private static void AssertPropertyNames(
        IReadOnlyList<string> expected,
        JsonElement element)
    {
        Assert.Equal(
            expected.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            element.EnumerateObject()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());
    }

    private static async Task SaveLegacyPrescreenSummaryAsync(
        HandlerTestHost host,
        string projectId)
    {
        var runId = Guid.NewGuid().ToString("N");
        var generatedUtc = DateTimeOffset.UtcNow;
        var summaryJson = JsonSerializer.Serialize(new
        {
            suspiciousKeywords = new { status = "V", count = 7 },
            resultRef = new
            {
                runId,
                generatedUtc,
                logicVersion = RuleLogicVersions.Prescreen
            }
        });

        var runStore = new LocalRuleRunStore(
            new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot)));
        await runStore.SaveAsync(
            projectId,
            new RuleRunRecord(runId, RuleRunKinds.Prescreen, generatedUtc, summaryJson),
            CancellationToken.None);
    }

    private sealed record RuleOracle(
        long HitLines,
        long HitVouchers,
        decimal RatePct);
}
