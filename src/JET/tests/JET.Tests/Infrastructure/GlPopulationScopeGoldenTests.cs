using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// revision-level populationScope 的 golden fixture，由 SQLite 的子類別執行。
/// Oracle 是依篩選條件的規則與投影後有效分錄母體手算的 21 列固定資料；
/// 每條斷言都鎖定 voucher_number + line_item 身分，不只比較筆數。
/// </summary>
public abstract class GlPopulationScopeGoldenTests
{
    protected const int MoneyScale = 10_000;

    /// <summary>
    /// 固定 fixture（21 列，MoneyScale=10,000，查核期間 2025-01-01～2025-12-31）：
    /// C01：Revenue 貸方有效、正常 Receivables 對方分別為期外與 status-excluded，否證 counterpart 子查詢繞過「只看納入測試的分錄」。
    ///      未預期借貸組合另要求同傳票在 GL 中有金額大於 0 的分錄（照舊版 IDEA R3），
    ///      C01 的正金額列都在母體外，所以它對未預期借貸組合不命中，由它鎖住正金額子查詢也只看納入測試的分錄。
    /// F01/F02：同科目各一筆有效／期外，另有一筆 status-excluded；maxEntries=1 鎖 frequency 子查詢 scope。
    /// N01：gl_date=NULL，固定不進查核期間母體。
    /// ZP/ZN/ZSUB：1,000.99、-1,000.99、0.99；Z6/Z6N：1,000,000.1234／999,999.9999，
    /// 鎖主單位整數尾零、固定六零邊界，並排除次單位與零值。
    /// DIG/SHORT：-12,345.67 與 45.67，鎖 trailingDigits 先截主單位且 k 位數不足不匹配。
    /// T1：有效命中列＋期外／NULL／status-excluded 伴隨列；T2：有效伴隨列＋期外唯一命中列，
    /// 鎖高風險條件彙總外層、hit 與 voucherTotal 同 scope。
    /// </summary>
    protected const string FixtureSql =
        """
        INSERT INTO gl
            (batch_id, source_row_number, voucher_number, line_item, gl_date, approval_date,
             account_number, account_name, description, source_module, created_by, approved_by,
             is_manual, is_excluded, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
        VALUES
            ('scope', 1,  'C01',   '1', '2025-06-30', '2025-06-30', '4100', '收入',     '跨期收入',             NULL, 'CrossPreparer', NULL, 1, 0, -2500000,    0,           2500000,   'CREDIT'),
            ('scope', 2,  'C01',   '2', '2026-01-01', '2026-01-01', '1200', '應收款',   '跨期正常對方',         NULL, 'CrossPreparer', NULL, 1, 1,  2500000,    2500000,     0,         'DEBIT'),
            ('scope', 3,  'F01',   '1', '2025-02-01', '2025-02-01', '6001', '較少使用科目', '期內較少使用科目',         NULL, 'FreqIn',        NULL, 0, 0,  777700,     777700,      0,         'DEBIT'),
            ('scope', 4,  'F02',   '1', '2024-12-31', '2024-12-31', '6001', '較少使用科目', '期外同科目',           NULL, 'FreqOut',       NULL, 0, 1,  888800,     888800,      0,         'DEBIT'),
            ('scope', 5,  'N01',   '1', NULL,         NULL,         '7000', '無日期',   NULL,                   NULL, 'NullDate',      NULL, 0, 1,  12300,      12300,       0,         'DEBIT'),
            ('scope', 6,  'ZP',    '1', '2025-03-01', '2025-03-01', '7101', '尾零正數', '主單位一千點九九',     NULL, 'ZeroPositive',  NULL, 0, 0,  10009900,   10009900,    0,         'DEBIT'),
            ('scope', 7,  'ZN',    '1', '2025-03-02', '2025-03-02', '7102', '尾零負數', '主單位負一千點九九',   NULL, 'ZeroNegative',  NULL, 0, 0, -10009900,   0,           10009900,  'CREDIT'),
            ('scope', 8,  'ZSUB',  '1', '2025-03-03', '2025-03-03', '7103', '次單位',   '零點九九',             NULL, 'SubUnit',       NULL, 0, 0,  9900,       9900,        0,         'DEBIT'),
            ('scope', 9,  'Z6',    '1', '2025-03-04', '2025-03-04', '7104', '六零命中', '一百萬點一二三四',     NULL, 'SixZerosHit',   NULL, 0, 0,  10000001234,10000001234, 0,         'DEBIT'),
            ('scope', 10, 'Z6N',   '1', '2025-03-05', '2025-03-05', '7105', '六零邊界', '九十九萬九千九百九九', NULL, 'SixZerosMiss',  NULL, 0, 0,  9999999999, 9999999999,  0,         'DEBIT'),
            ('scope', 11, 'DIG',   '1', '2025-04-01', '2025-04-01', '7201', '尾碼',     '負一萬二千三百四十五', NULL, 'DigitsLong',    NULL, 0, 0, -123456700,  0,           123456700, 'CREDIT'),
            ('scope', 12, 'SHORT', '1', '2025-04-02', '2025-04-02', '7202', '短尾碼',   '四十五點六七',         NULL, 'DigitsShort',   NULL, 0, 0,  456700,     456700,      0,         'DEBIT'),
            ('scope', 13, 'ZERO',  '1', '2025-04-03', '2025-04-03', '7203', '零值',     '零值不算尾零',         NULL, 'ZeroValue',     NULL, 0, 0,  0,          0,           0,         'DEBIT'),
            ('scope', 14, 'T1',    '1', '2025-05-01', '2025-05-01', '7301', '彙總',     'summary-hit-in',         NULL, 'TagOne',        NULL, 0, 0,  1000000,    1000000,     0,         'DEBIT'),
            ('scope', 15, 'T1',    '2', '2026-05-01', '2026-05-01', '7302', '彙總',     'summary-companion-out',  NULL, 'TagOne',        NULL, 0, 1,  2000000,    2000000,     0,         'DEBIT'),
            ('scope', 16, 'T2',    '1', '2025-05-02', '2025-05-02', '7303', '彙總',     'summary-companion-in',   NULL, 'TagTwo',        NULL, 0, 0,  3000000,    3000000,     0,         'DEBIT'),
            ('scope', 17, 'T2',    '2', '2026-05-02', '2026-05-02', '7304', '彙總',     'summary-hit-out',        NULL, 'TagTwo',        NULL, 0, 1,  4000000,    4000000,     0,         'DEBIT'),
            ('scope', 18, 'T1',    '3', NULL,         NULL,         '7305', '彙總',     'summary-companion-null', NULL, 'TagOne',        NULL, 0, 1,  12300,      12300,       0,         'DEBIT'),
            ('scope', 19, 'C01',   '3', '2025-07-01', '2026-01-15', '1200', '應收款',   '狀態排除正常對方',     NULL, 'CrossPreparer', NULL, 1, 1,  2500000,    2500000,     0,         'DEBIT'),
            ('scope', 20, 'F01',   '2', '2025-02-02', '2025-02-02', '6001', '較少使用科目', '狀態排除頻率列',       NULL, 'CrossPreparer', NULL, 0, 1,  999900,     999900,      0,         'DEBIT'),
            ('scope', 21, 'T1',    '4', '2025-05-03', '2025-05-03', '7306', '彙總',     'summary-hit-in',         NULL, 'TagOne',        NULL, 0, 1,  500000,     500000,      0,         'DEBIT');

        -- 科目配對一律以 category_id 連到分類表，測試資料帶 category_id。
        INSERT INTO account_mapping
            (batch_id, source_row_number, account_number, account_name, standardized_category, category_id)
        VALUES
            ('scope-map', 1, '4100', '收入',   'Revenue', 'builtin.revenue'),
            ('scope-map', 2, '1200', '應收款', 'Receivables', 'builtin.receivables'),
            ('scope-map', 3, '6001', '較少使用科目', 'Others', 'builtin.others');

        -- 條件組結果依條件組編號儲存，高風險條件彙總要從條件組定義表找顯示順序，彙總測試用的兩個條件組先登記定義。
        INSERT INTO filter_scenario (scenario_id, position, name, rationale, definition_json, saved_utc)
        VALUES
            (1, 1, 'summary-in',  'synthetic', '{}', '2026-10-31T00:00:00Z'),
            (2, 2, 'summary-out', 'synthetic', '{}', '2026-10-31T00:00:00Z');
        """;

    protected abstract IFilterHitRepository FilterRepository { get; }

    protected abstract IFilterHitMaterializer Materializer { get; }

    protected abstract IHighRiskSummaryRowPageRepository HighRiskSummaryRowRepository { get; }

    protected abstract IHighRiskSummaryVoucherPageRepository HighRiskSummaryVoucherRepository { get; }

    private protected abstract IValidationFactsPort ValidationFactsPort { get; }

    private protected abstract IPrescreenFactsPort PrescreenFactsPort { get; }

    protected abstract IBlankRecordsPageRepository BlankRecordsRepository { get; }

    protected abstract IHighRiskSummaryScenariosRepository HighRiskSummaryCountsRepository { get; }

    protected abstract string ProjectId { get; }

    [Fact]
    public async Task BlankGlDateDetection_IsSourceQualityOnly()
    {
        var plan = JetAuditProgram.Plan(new ValidationRequest(
            ProjectId,
            HasGlMapping: true,
            HasTbMapping: false,
            PeriodStart: "2025-01-01",
            PeriodEnd: "2025-12-31",
            MoneyScale: MoneyScale,
            SampleSeed: 48271,
            ResultId: "blank-date-run",
            GeneratedUtc: DateTimeOffset.UnixEpoch,
            SampleSize: 0));
        var result = await ValidationExecutionTestData.ExecuteForFactsAsync(ValidationFactsPort, plan, CancellationToken.None);

        Assert.Equal(2, result.SourceQualityFindingCount);
        Assert.Equal(0, result.BlankAccountNumberCount);
        Assert.Equal(0, result.BlankVoucherNumberCount);
        Assert.Equal(0, result.BlankDescriptionCount);
        Assert.Equal(0, result.ApprovalDateNotInPeriodCount);

    }

    [Fact]
    public async Task ValidationRules_UseGl_AndExposePartitionSummary()
    {
        var plan = JetAuditProgram.Plan(new ValidationRequest(
            ProjectId,
            HasGlMapping: true,
            HasTbMapping: false,
            PeriodStart: "2025-01-01",
            PeriodEnd: "2025-12-31",
            MoneyScale: MoneyScale,
            SampleSeed: 48271,
            ResultId: "effective-population-run",
            GeneratedUtc: DateTimeOffset.UnixEpoch,
            SampleSize: 100));

        var result = await ValidationExecutionTestData.ExecuteForFactsAsync(ValidationFactsPort, plan, CancellationToken.None);

        Assert.Equal(21, result.PopulationSummary.Raw.RowCount);
        Assert.Equal(12, result.PopulationSummary.Gl.RowCount);
        Assert.Equal(9, result.PopulationSummary.Excluded.RowCount);
        Assert.Equal(
            result.PopulationSummary.Raw.RowCount,
            result.PopulationSummary.Gl.RowCount + result.PopulationSummary.Excluded.RowCount);
        Assert.Equal(12, result.AmountBinCounts.Sum(bin => bin.Count));
        Assert.Equal(11, result.UnbalancedVoucherCount);
        Assert.Equal(12, result.InfSampleCount);
        Assert.Equal(2, result.SourceQualityFindingCount);
    }

    [Fact]
    public async Task PrescreenRulesAndSummaries_UseGl()
    {
        var plan = JetAuditProgram.Plan(new PrescreenRequest(
            ProjectId: ProjectId,
            HasGlMapping: true,
            PeriodStart: "2025-01-01",
            PeriodEnd: "2025-12-31",
            MoneyScale: MoneyScale,
            SampleSeed: 48_271,
            ResultId: "effective-prescreen-run",
            GeneratedUtc: DateTimeOffset.UnixEpoch,
            PrepStartDate: "2025-12-31",
            // 不帶可用欄位清單就不檢查欄位，等同核准日與建立人員兩欄都可用。
            AvailableGlFields: null,
            HasHolidays: false,
            HasAccountMapping: true,
            HasRevenue: true,
            HasCounterpart: true,
            HasAuthorizedPreparers: false,
            NonWorkingDays: [0, 6])
        {
            // 0 的個數照 IDEA 版由 GL 算出，請求要帶 0 的個數。這則測試直接建立請求，帶入 6 個；由 GL 算出個數另由 PrescreenRunHandlerTests 核對。
            ZerosThreshold = 6
        });

        var result = await PrescreenFactsPort.ExecuteAsync(plan, CancellationToken.None);

        Assert.Equal(0, result.PrepPeriodApprovalCount);
        // C01 的正金額列都不在 GL 中，照舊版 IDEA R3 不命中未預期借貸組合。
        Assert.Equal(0, result.UnexpectedAccountPairCount);
        Assert.Equal(12, result.TotalEntryCount);
        Assert.Equal(12, result.TotalPreparerCount);
        Assert.Equal(12, result.Preparers.Sum(row => row.EntryCount));
        Assert.Equal(12, result.DistinctAccountCount);
        Assert.Equal(12, result.Accounts.Sum(row => row.EntryCount));
    }

    // 0 的個數照 IDEA 版由 GL 算出、跟著條件環境帶入。這組合成資料直接組條件環境，固定帶 6 個，
    // 用來測尾零規則的六個 0 邊界。沒有個數時會明確丟出例外。
    private static FilterConditionContext RuleContext(GlPopulationScope scope) =>
        new(MoneyScale, null, "2025-01-01", "2025-12-31", PopulationScope: scope) { ZerosThreshold = 6 };

    private static GlPopulationContext PopulationContext(GlPopulationScope scope) =>
        new(scope, "2025-01-01", "2025-12-31");

    private static FilterConditionSpec Rule(FilterConditionType type) =>
        new(FilterJoin.And, type, null, null, [], TextMatchMode.Contains,
            null, null, null, null, null, null);

    private static FilterConditionSpec TextRule(string field, string value, TextMatchMode mode = TextMatchMode.Exact) =>
        Rule(FilterConditionType.Text) with { Field = field, Keywords = [value], Mode = mode };

    // 用畫面送出的條件寫法建立規則，走正式的解析。
    private static FilterConditionSpec Parsed(string rule) => FilterScenarioPayloadParser.Parse(
        JsonDocument.Parse($$"""{"groups":[{"conditions":[{{rule}}]}]}""").RootElement, MoneyScale).Groups[0].Conditions[0];

    private static readonly IReadOnlyDictionary<FilterConditionType, FilterConditionSpec> CounterpartRules =
        new Dictionary<FilterConditionType, FilterConditionSpec>
        {
            [FilterConditionType.Prescreen] = Rule(FilterConditionType.Prescreen) with
            {
                PrescreenKey = PrescreenRuleKeys.UnexpectedAccountPair
            },
            [FilterConditionType.RevenueWithoutNormalCounterpart] =
                Rule(FilterConditionType.RevenueWithoutNormalCounterpart)
        };

    private static FilterScenarioSpec Scenario(params FilterConditionSpec[] rules) =>
        new("母體 scope golden", "21 列手算 fixture", [new FilterGroupSpec(FilterJoin.And, rules)]);

    private static string Identity(FilterPreviewRow row) =>
        $"{row.VoucherNumber}|{row.LineItem}";

    private static string Identity(RowTagRow row) =>
        $"{row.VoucherNumber}|{row.LineItem}";

    private async Task<IReadOnlyList<string>> HitIdentitiesAsync(
        FilterScenarioSpec scenario,
        GlPopulationScope scope)
    {
        var result = await FilterRepository.PreviewAsync(
            ProjectId, scenario, RuleContext(scope), CancellationToken.None);

        Assert.Equal(result.Count, (long)result.PreviewRows.Count);
        return result.PreviewRows
            .Select(Identity)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
    }

    // 唯一母體：auditPeriod 只消費 is_excluded；期外、NULL 與 status-excluded 均排除。
    [Theory]
    [InlineData(GlPopulationScope.AuditPeriod, new[]
    {
        "C01|1", "DIG|1", "F01|1", "SHORT|1", "T1|1",
        "T2|1", "Z6N|1", "Z6|1", "ZERO|1", "ZN|1", "ZP|1", "ZSUB|1"
    })]
    public async Task OuterPopulationScope_FixedFixture_ReturnsExactRows(
        GlPopulationScope scope,
        string[] expectedIdentities)
    {
        var matchEverything = Scenario(TextRule("description", "__fixture_never_contains__", TextMatchMode.NotContains));

        Assert.Equal(expectedIdentities, await HitIdentitiesAsync(matchEverything, scope));
    }

    // C01 的正常對方只存在於期外或 status-excluded 列，收入無一般對方科目必須命中 Revenue 貸方。
    // 未預期借貸組合照舊版 IDEA R3 還要求同傳票在 GL 中有金額大於 0 的分錄（取自同一個 #GL# 母體）；
    // C01 的正金額列都在母體外，所以不命中。
    // 若正金額子查詢繞過「只看納入測試的分錄」，C01 會被期外的應收借方湊成命中，這一列因此仍有否證力。
    // 正常對方與正金額兩個子查詢共用同一個母體述詞，正常對方的母體範圍由收入無一般對方科目這一列一起鎖住。
    [Theory]
    [InlineData(FilterConditionType.Prescreen, GlPopulationScope.AuditPeriod, new string[0])]
    [InlineData(FilterConditionType.RevenueWithoutNormalCounterpart, GlPopulationScope.AuditPeriod, new[] { "C01|1" })]
    public async Task CounterpartSubquery_CrossPeriodVoucher_UsesRevisionScope(
        FilterConditionType type,
        GlPopulationScope scope,
        string[] expectedIdentities)
    {
        Assert.Equal(
            expectedIdentities,
            await HitIdentitiesAsync(Scenario(CounterpartRules[type]), scope));
    }

    [Theory]
    [InlineData(GlPopulationScope.AuditPeriod, new string[0])]
    public async Task AccountPairSubquery_CrossPeriodVoucher_UsesRevisionScope(
        GlPopulationScope scope,
        string[] expectedIdentities)
    {
        var exactPair = Rule(FilterConditionType.AccountPair) with
        {
            PairMode = AccountPairModes.Exact,
            // 用分類身分陣列表達這一組分類。
            DebitCategoryIds = [AccountCategoryBuiltIns.ReceivablesId],
            CreditCategoryIds = [AccountCategoryBuiltIns.RevenueId]
        };

        Assert.Equal(expectedIdentities, await HitIdentitiesAsync(Scenario(exactPair), scope));
    }

    // 同一分組鍵在 GL 中只有 1 列；期外與 status-excluded 不能拉高 frequency count。
    // 條件用「科目與人員統計」的分錄筆數小於或等於 1。
    [Theory]
    [InlineData("createdBy", "CrossPreparer", GlPopulationScope.AuditPeriod, new[] { "C01|1" })]
    [InlineData("accountNumber", "6001", GlPopulationScope.AuditPeriod, new[] { "F01|1" })]
    public async Task FrequencySubquery_CrossPeriodGroup_UsesRevisionScope(
        string field,
        string value,
        GlPopulationScope scope,
        string[] expectedIdentities)
    {
        var frequency = Rule(FilterConditionType.EntityFrequency) with
        {
            Field = field, CountUnit = "entries", CountOperator = "lessThanOrEqual", CountFrom = 1
        };
        var scenario = Scenario(TextRule(field, value), frequency);

        Assert.Equal(expectedIdentities, await HitIdentitiesAsync(scenario, scope));
    }

    [Fact]
    public async Task CustomTrailingZeros_DecimalAndNegativeAmounts_UsesMajorUnitInteger()
    {
        // Legacy @int oracle：±1,000.99 的主單位整數都是 1,000，故命中 3 個尾零；0.99 與零值不命中。
        // 條件寫成同一個子條件組裡的「整數尾數符合 000」與「大於或等於 1」。
        var rule = Parsed("""{"type":"group","conditions":[{"type":"fieldValue","field":"amount","operator":"endsWithDigits","values":["000"],"amountBasis":"absolute"},{"type":"fieldValue","field":"amount","operator":"greaterThanOrEqual","value":"1","amountBasis":"absolute"}]}""");

        Assert.Equal(
            ["Z6|1", "ZN|1", "ZP|1"],
            await HitIdentitiesAsync(Scenario(rule), GlPopulationScope.AuditPeriod));
    }

    [Fact]
    public async Task PrescreenTrailingZeros_FractionalAmount_UsesSixZeroMajorUnitBoundary()
    {
        // BVA：1,000,000.1234 的整數部分在六零邊界上，999,999.9999 是下鄰；只有前者命中。
        var rule = Rule(FilterConditionType.Prescreen) with
        {
            PrescreenKey = PrescreenRuleKeys.TrailingZeros
        };

        Assert.Equal(
            ["Z6|1"],
            await HitIdentitiesAsync(Scenario(rule), GlPopulationScope.AuditPeriod));
    }

    [Fact]
    public async Task TrailingDigits_DecimalAndNegativeAmount_UsesMajorUnitIntegerAndLengthGuard()
    {
        // Legacy @Right(@Str(@int(amount),1,0),3)：-12,345.67 → 345；45.67 因主單位只有兩位不匹配。
        // 條件用「金額」的「整數尾數符合」。
        var rule = Parsed("""{"type":"fieldValue","field":"amount","operator":"endsWithDigits","values":["345"],"amountBasis":"absolute"}""");

        Assert.Equal(
            ["DIG|1"],
            await HitIdentitiesAsync(Scenario(rule), GlPopulationScope.AuditPeriod));
    }

    [Fact]
    public async Task HighRiskSummary_InScopeHitWithOutOfScopeCompanion_ScopesRowsPositionsAndVoucherTotal()
    {
        await MaterializeTagScenariosAsync();
        var request = new PageRequest(null, 50);

        var auditRows = await HighRiskSummaryRowRepository.GetPageAsync(
            ProjectId, PopulationContext(GlPopulationScope.AuditPeriod), request, [1], CancellationToken.None);
        Assert.Equal(["T1|1"], auditRows.Page.Rows.Select(Identity).ToList());
        Assert.Equal([1], auditRows.PositionsByEntry[auditRows.EntryIds[0]]);

        var auditVouchers = await HighRiskSummaryVoucherRepository.GetPageAsync(
            ProjectId, PopulationContext(GlPopulationScope.AuditPeriod), request, [1], CancellationToken.None);
        var auditVoucher = Assert.Single(auditVouchers.Page.Rows);
        Assert.Equal("T1", auditVoucher.VoucherNumber);
        Assert.Equal(1_000_000L, auditVoucher.VoucherTotalScaled);
        Assert.Equal([1], auditVouchers.PositionsByDoc["T1"]);

        var scenarioCounts = await HighRiskSummaryCountsRepository.GetCountsAsync(
            ProjectId,
            CancellationToken.None);
        Assert.Equal((1L, 1L), scenarioCounts[1]);
        Assert.DoesNotContain(2, scenarioCounts.Keys);

    }

    [Fact]
    public async Task HighRiskSummary_OutOfScopeOnlyHit_AuditScopeDoesNotAdmitVoucher()
    {
        await MaterializeTagScenariosAsync();
        var request = new PageRequest(null, 50);

        var auditRows = await HighRiskSummaryRowRepository.GetPageAsync(
            ProjectId, PopulationContext(GlPopulationScope.AuditPeriod), request, [2], CancellationToken.None);
        var auditVouchers = await HighRiskSummaryVoucherRepository.GetPageAsync(
            ProjectId, PopulationContext(GlPopulationScope.AuditPeriod), request, [2], CancellationToken.None);
        Assert.Empty(auditRows.Page.Rows);
        Assert.Empty(auditRows.PositionsByEntry);
        Assert.Empty(auditVouchers.Page.Rows);
        Assert.Empty(auditVouchers.PositionsByDoc);

    }

    private async Task MaterializeTagScenariosAsync()
    {
        await Materializer.MaterializeAsync(
            ProjectId,
            [
                new MaterializableScenario(1, Scenario(TextRule("description", "summary-hit-in"))),
                new MaterializableScenario(2, Scenario(TextRule("description", "summary-hit-out")))
            ],
            RuleContext(GlPopulationScope.AuditPeriod),
            CancellationToken.None);
    }
}

public sealed class SqliteGlPopulationScopeGoldenTests : GlPopulationScopeGoldenTests, IDisposable
{
    private readonly TempProjectRoot _root = new();

    public SqliteGlPopulationScopeGoldenTests()
    {
        var folder = new JetProjectFolder(_root.Path);
        var database = new SqliteProjectDatabase(folder);
        ProjectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(ProjectId));
        database.EnsureCreatedAsync(ProjectId, CancellationToken.None).GetAwaiter().GetResult();

        using (var connection = database.CreateConnection(ProjectId))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = FixtureSql;
            command.ExecuteNonQuery();
        }

        FilterRepository = new LocalFilterHitRepository(database);
        Materializer = new LocalFilterHitMaterializer(database);
        HighRiskSummaryRowRepository = new LocalHighRiskSummaryRowPageRepository(database);
        HighRiskSummaryVoucherRepository = new LocalHighRiskSummaryVoucherPageRepository(database);
        ValidationFactsPort = new LocalValidationRunRepository(database);
        PrescreenFactsPort = new LocalPrescreenRunRepository(database);
        BlankRecordsRepository = new LocalBlankRecordsPageRepository(database);
        HighRiskSummaryCountsRepository = new LocalHighRiskSummaryScenariosRepository(database);
    }

    protected override IFilterHitRepository FilterRepository { get; }

    protected override IFilterHitMaterializer Materializer { get; }

    protected override IHighRiskSummaryRowPageRepository HighRiskSummaryRowRepository { get; }

    protected override IHighRiskSummaryVoucherPageRepository HighRiskSummaryVoucherRepository { get; }

    private protected override IValidationFactsPort ValidationFactsPort { get; }

    private protected override IPrescreenFactsPort PrescreenFactsPort { get; }

    protected override IBlankRecordsPageRepository BlankRecordsRepository { get; }

    protected override IHighRiskSummaryScenariosRepository HighRiskSummaryCountsRepository { get; }

    protected override string ProjectId { get; }

    public void Dispose() => _root.Dispose();
}
