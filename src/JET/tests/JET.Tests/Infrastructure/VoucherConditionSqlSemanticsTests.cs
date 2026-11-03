using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class VoucherConditionSqlSemanticsTests
{
    private static readonly FilterConditionContext Context = new(100, null, "2025-01-01", "2025-12-31");

    public static IEnumerable<object[]> VoucherCases()
    {
        foreach (var provider in new[] { "sqlite", "duckdb" })
        foreach (var quantifier in new[] { "any", "all", "none" })
        foreach (var value in new[] { "X", "NO-MATCH" })
            yield return [provider, quantifier, value];
    }

    [Theory]
    [MemberData(nameof(VoucherCases))]
    public async Task BlankVoucher_IsNeverAWholeVoucherMatch_RegardlessOfOtherMatches(string provider, string quantifier, string value)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, VoucherSeed);
        // 傳票條件的借貸範圍已拿掉（裁定 2026-10-30-25：已儲存情境的舊寫法不再讀回）；原本的 all 就是不限借貸，拿掉這個屬性，固定答案不變。
        // 第一次失敗：Focused 收據 20261030-081546753-58fab64421d94d9a9973dfa09cda1b98（舊寫法在組成 SQL 時被擋下）。
        var rule = new { type = "voucher", quantifier,
            conditions = new[] { new { type = "fieldValue", field = "description", @operator = "equals", value } } };
        string[] expected = (quantifier, value) switch
        {
            ("any", "X") => ["MATCH|1", "MATCH|2", "MIX|1", "MIX|2"],
            ("all", "X") => ["MATCH|1", "MATCH|2"],
            ("none", "X") => ["NONE|1", "NONE|2"],
            ("none", "NO-MATCH") => ["MATCH|1", "MATCH|2", "MIX|1", "MIX|2", "NONE|1", "NONE|2"],
            _ => []
        };
        Assert.Equal(expected, await HitsAsync(fixture, rule));
    }

    [Theory]
    [InlineData("sqlite", "any")]
    [InlineData("duckdb", "any")]
    [InlineData("sqlite", "debit")]
    [InlineData("duckdb", "debit")]
    [InlineData("sqlite", "credit")]
    [InlineData("duckdb", "credit")]
    public async Task CategoryAbsence_ExcludesBlankVoucherButPreservesNamedVoucherMeaning(string provider, string side)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, VoucherSeed);
        // 科目分類的「這一側都不屬於」與借貸方向已拿掉（裁定 2026-10-30-25：已儲存情境的舊寫法不再讀回），照它的重建方式改成「整張傳票條件」的「不存在符合」，
        // 子條件是借貸別與科目分類屬於，固定答案不變。第一次失敗：Focused 收據 20261030-081546753-58fab64421d94d9a9973dfa09cda1b98（舊寫法在組成 SQL 時被擋下）。
        var cash = new { type = "accountSide", categoryMode = "is", categorySelection = "node",
            categoryIds = new[] { AccountCategoryBuiltIns.CashId } };
        object rule = side == "any"
            ? new { type = "voucher", quantifier = "none", conditions = new object[] { cash } }
            : new { type = "voucher", quantifier = "none", conditions = new object[]
                { new { type = "group", conditions = new object[] { new { type = "drCrOnly", drCr = side }, cash } } } };
        string[] expected = side switch
        {
            "any" => ["NONE|1", "NONE|2"],
            "debit" => ["MIX|1", "MIX|2", "NONE|1", "NONE|2"],
            _ => ["MATCH|1", "MATCH|2", "NONE|1", "NONE|2"]
        };
        Assert.Equal(expected, await HitsAsync(fixture, rule));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task BlankVoucher_RemainsAvailableToRowLevelConditions(string provider)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, VoucherSeed);
        // 科目分類條件的借貸方向已拿掉（裁定 2026-10-30-25：已儲存情境的舊寫法不再讀回）；原本的 any 就是不限借貸，拿掉這個屬性，固定答案不變。
        // 第一次失敗：Focused 收據 20261030-081546753-58fab64421d94d9a9973dfa09cda1b98（舊寫法在組成 SQL 時被擋下）。
        Assert.Equal(new[] { "<null>|1", "MATCH|1", "MIX|2" }, await HitsAsync(fixture, new
        { type = "accountSide", categoryMode = "is", categorySelection = "node",
            categoryIds = new[] { AccountCategoryBuiltIns.CashId } }));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task UnexpectedPairAndRevenueWithoutCounterpart_ExcludeBlankRevenue_WithoutChangingCashOrZeroDebitRules(string provider)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, PairSeed);
        // 裁定 2026-10-29-29 改成照舊版 IDEA R3：未預期借貸組合要求傳票裡至少有一筆金額大於 0 的分錄。
        // ZERO-R 只有 0 元應收借方與收入貸方，沒有正金額分錄，改為不命中（原本命中；第一次失敗：Focused 收據 20261030-163342206-c1b391e5be2e46bfa36b85fdbe6b59ad）。
        // 另加 ZERO-P：0 元應收借方之外還有其他科目的正金額借方，0 元應收照樣不算正常對方科目 → 命中，零元邊界的斷言由它保留。
        Assert.Equal(new[] { "BAD|2", "ZERO-P|3" }, await HitsAsync(fixture,
            new { type = "prescreen", prescreenKey = "unexpectedAccountPair" }));
        Assert.Equal(new[] { "BAD|2", "CASH|2" }, await HitsAsync(fixture,
            new { type = "revenueWithoutNormalCounterpart" }));
        Assert.Equal(new[] { "<null>|1", "BAD|2", "CASH|2", "REC|2", "ZERO-P|3", "ZERO-R|2" }, await HitsAsync(fixture,
            new { type = "fieldValue", field = "accountNumber", @operator = "equals", value = "R" }));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task NewNonBusinessDayCard_ExcludesMakeup_WhileWeekendPrescreenKeepsIt(string provider)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, CalendarSeed);
        Assert.Equal(new[] { "HOLIDAY|1", "SAT|1" }, await HitsAsync(fixture,
            new { type = "fieldValue", field = "glDate", @operator = "isNonBusinessDay" }));
        Assert.Equal(new[] { "HOLIDAY|1", "SAT|1" }, await HitsAsync(fixture,
            new { type = "group", conditions = new[]
            { new { type = "fieldValue", field = "glDate", @operator = "isNonBusinessDay" } } }));
        Assert.Equal(new[] { "MAKEUP|1", "SAT|1" }, await HitsAsync(fixture,
            new { type = "prescreen", prescreenKey = "weekendPosting" }));
        Assert.Equal(new[] { "HOLIDAY|1" }, await HitsAsync(fixture,
            new { type = "prescreen", prescreenKey = "holidayPosting" }));
    }

    public static IEnumerable<object[]> FrequencyCases()
    {
        foreach (var provider in new[] { "sqlite", "duckdb" })
        foreach (var field in new[] { "accountNumber", "createdBy", "approvedBy" })
        foreach (var countUnit in new[] { "entries", "vouchers" })
        foreach (var countOperator in new[] { "lessThanOrEqual", "greaterThanOrEqual" })
            yield return [provider, field, countUnit, countOperator];
    }

    [Theory]
    [MemberData(nameof(FrequencyCases))]
    public async Task InclusiveFrequencyComparisons_HaveFixedBoundaryAndDistinctVoucherAnswers(
        string provider, string field, string countUnit, string countOperator)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, FrequencySeed);
        var countFrom = countUnit == "vouchers" && countOperator == "lessThanOrEqual" ? 1 : 2;
        string[] expected = countOperator == "lessThanOrEqual"
            ? ["B|1", "B|2", "C|1"]
            : countUnit == "entries" ? ["<null>|3", "A1|1", "A2|2", "B|1", "B|2"]
            : ["<null>|3", "A1|1", "A2|2"];
        Assert.Equal(expected, await HitsAsync(fixture, new { type = "entityFrequency", field, countUnit, countOperator, countFrom }));
    }

    [Fact]
    public void ChangedVoucherRules_InvalidatePreviouslyCurrentPrescreenAndFilterSnapshots()
    {
        var oldPrescreen = new ResultRecord("old", ResultKind.Prescreen, DateTimeOffset.UnixEpoch,
            """{"resultRef":{"logicVersion":"prescreen-2026-09-17-v8"}}""");
        var oldFilter = new SavedFilterScenario(1, "Synthetic", "Old rules",
            """{"logicVersion":"filter-2026-09-18-v16","populationScope":"auditPeriod"}""", DateTimeOffset.UnixEpoch);
        Assert.False(RuleLogicVersions.IsCurrent(oldPrescreen));
        Assert.False(RuleLogicVersions.IsCurrent(oldFilter));
    }

    internal static FilterScenarioSpec Scenario(object rule) => FilterScenarioPayloadParser.Parse(
        JsonSerializer.SerializeToElement(new { name = "Synthetic", rationale = "Fixed answers", groups = new[] { new { conditions = new[] { rule } } } }), 100);

    internal static async Task<string[]> HitsAsync(FilterPredicateProviderFixture fixture, object rule, FilterConditionContext? context = null)
    {
        var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, Scenario(rule), context ?? Context, CancellationToken.None);
        return result.PreviewRows.Select(row => $"{row.VoucherNumber ?? "<null>"}|{row.LineItem}")
            .Order(StringComparer.Ordinal).ToArray();
    }

    private const string MappingSeed = """
        INSERT INTO account_mapping(batch_id,source_row_number,account_number,account_name,standardized_category,category_id)
        VALUES ('b7',1,'C','Cash','Cash','builtin.cash'),('b7',2,'O','Other','Others','builtin.others'),
        ('b7',3,'R','Revenue','Revenue','builtin.revenue'),('b7',4,'AR','Receivable','Receivables','builtin.receivables');
        """;
    private const string VoucherSeed = MappingSeed + """
        INSERT INTO gl(batch_id,source_row_number,voucher_number,line_item,gl_date,account_number,description,
            amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_excluded) VALUES
        ('b7',1,NULL,'1','2025-03-01','C','X',100,100,0,'DEBIT',0),
        ('b7',2,NULL,'2','2025-03-01','O','Y',-100,0,100,'CREDIT',0),
        ('b7',3,'MATCH','1','2025-03-01','C','X',100,100,0,'DEBIT',0),
        ('b7',4,'MATCH','2','2025-03-01','O','X',-100,0,100,'CREDIT',0),
        ('b7',5,'MIX','1','2025-03-01','O','X',100,100,0,'DEBIT',0),
        ('b7',6,'MIX','2','2025-03-01','C','Y',-100,0,100,'CREDIT',0),
        ('b7',7,'NONE','1','2025-03-01','O','Y',100,100,0,'DEBIT',0),
        ('b7',8,'NONE','2','2025-03-01','O','Y',-100,0,100,'CREDIT',0),
        ('b7',9,'NONE','3','2024-03-01','C','X',100,100,0,'DEBIT',1);
        """;
    private const string PairSeed = MappingSeed + """
        INSERT INTO gl(batch_id,source_row_number,voucher_number,line_item,gl_date,account_number,
            amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_excluded) VALUES
        ('b7',1,NULL,'1','2025-03-01','R',-100,0,100,'CREDIT',0),
        ('b7',2,'BAD','1','2025-03-01','O',100,100,0,'DEBIT',0),
        ('b7',3,'BAD','2','2025-03-01','R',-100,0,100,'CREDIT',0),
        ('b7',4,'CASH','1','2025-03-01','C',100,100,0,'DEBIT',0),
        ('b7',5,'CASH','2','2025-03-01','R',-100,0,100,'CREDIT',0),
        ('b7',6,'REC','1','2025-03-01','AR',100,100,0,'DEBIT',0),
        ('b7',7,'REC','2','2025-03-01','R',-100,0,100,'CREDIT',0),
        ('b7',8,'ZERO-R','1','2025-03-01','AR',0,0,0,'DEBIT',0),
        ('b7',9,'ZERO-R','2','2025-03-01','R',-100,0,100,'CREDIT',0),
        ('b7',10,'ZERO-P','1','2025-03-01','AR',0,0,0,'DEBIT',0),
        ('b7',11,'ZERO-P','2','2025-03-01','O',100,100,0,'DEBIT',0),
        ('b7',12,'ZERO-P','3','2025-03-01','R',-100,0,100,'CREDIT',0);
        """;
    private const string CalendarSeed = """
        INSERT INTO gl(batch_id,source_row_number,voucher_number,line_item,gl_date,account_number,
            amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_excluded) VALUES
        ('b7',1,'SAT','1','2025-03-01','A',100,100,0,'DEBIT',0),
        ('b7',2,'MAKEUP','1','2025-03-08','A',100,100,0,'DEBIT',0),
        ('b7',3,'HOLIDAY','1','2025-03-03','A',100,100,0,'DEBIT',0),
        ('b7',4,'WEEKDAY','1','2025-03-04','A',100,100,0,'DEBIT',0);
        INSERT INTO calendar_day(day_type,date) VALUES ('holiday','2025-03-03'),('makeup','2025-03-08');
        """;
    private const string FrequencySeed = """
        INSERT INTO gl(batch_id,source_row_number,voucher_number,line_item,gl_date,account_number,created_by,approved_by,
            amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_excluded) VALUES
        ('b7',1,'A1','1','2025-03-01','A',' a ',' x ',100,100,0,'DEBIT',0),
        ('b7',2,'A2','2','2025-03-01','A','A','X',100,100,0,'DEBIT',0),
        ('b7',3,NULL,'3','2025-03-01','A','a','x',100,100,0,'DEBIT',0),
        ('b7',4,'B','1','2025-03-01','B','B','Y',100,100,0,'DEBIT',0),
        ('b7',5,'B','2','2025-03-01','B','b','y',100,100,0,'DEBIT',0),
        ('b7',6,'C','1','2025-03-01','C','C','Z',100,100,0,'DEBIT',0),
        ('b7',7,'EMPTY','1','2025-03-01','',NULL,NULL,100,100,0,'DEBIT',0),
        ('b7',8,'OUT','1','2024-03-01','A','A','X',100,100,0,'DEBIT',1);
        """;
}
