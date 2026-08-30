using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// revision-level populationScope 的跨 provider golden fixture。
/// Oracle 是依 manifest Filter / Criteria 與投影後有效分錄母體手算的 21 列固定資料；
/// 每條斷言都鎖定 document_number + line_item 身分，不只比較筆數。
/// </summary>
public abstract class GlPopulationScopeGoldenTests
{
    protected const int MoneyScale = 10_000;

    /// <summary>
    /// 固定 fixture（21 列，MoneyScale=10,000，查核期間 2025-01-01～2025-12-31）：
    /// C01：Revenue 貸方有效、正常 Receivables 對方分別為期外與 status-excluded，否證 counterpart 子查詢繞過有效母體。
    /// F01/F02：同科目各一筆有效／期外，另有一筆 status-excluded；maxEntries=1 鎖 frequency 子查詢 scope。
    /// N01：post_date=NULL，固定不進查核期間母體。
    /// ZP/ZN/ZSUB：1,000.99、-1,000.99、0.99；Z6/Z6N：1,000,000.1234／999,999.9999，
    /// 鎖主單位整數尾零、固定六零邊界，並排除次單位與零值。
    /// DIG/SHORT：-12,345.67 與 45.67，鎖 trailingDigits 先截主單位且 k 位數不足不匹配。
    /// T1：有效命中列＋期外／NULL／status-excluded 伴隨列；T2：有效伴隨列＋期外唯一命中列，
    /// 鎖 tag matrix 外層、hit 與 voucherTotal 同 scope。
    /// </summary>
    protected const string FixtureSql =
        """
        INSERT INTO target_gl_entry
            (batch_id, source_row_number, document_number, line_item, post_date, approval_date,
             account_code, account_name, document_description, source_module, created_by, approved_by,
             is_manual, is_effective, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
        VALUES
            ('scope', 1,  'C01',   '1', '2025-06-30', '2025-06-30', '4100', '收入',     '跨期收入',             NULL, 'CrossPreparer', NULL, 1, 1, -2500000,    0,           2500000,   'CREDIT'),
            ('scope', 2,  'C01',   '2', '2026-01-01', '2026-01-01', '1200', '應收款',   '跨期正常對方',         NULL, 'CrossPreparer', NULL, 1, 0,  2500000,    2500000,     0,         'DEBIT'),
            ('scope', 3,  'F01',   '1', '2025-02-01', '2025-02-01', '6001', '低頻科目', '期內低頻科目',         NULL, 'FreqIn',        NULL, 0, 1,  777700,     777700,      0,         'DEBIT'),
            ('scope', 4,  'F02',   '1', '2024-12-31', '2024-12-31', '6001', '低頻科目', '期外同科目',           NULL, 'FreqOut',       NULL, 0, 0,  888800,     888800,      0,         'DEBIT'),
            ('scope', 5,  'N01',   '1', NULL,         NULL,         '7000', '無日期',   NULL,                   NULL, 'NullDate',      NULL, 0, 0,  12300,      12300,       0,         'DEBIT'),
            ('scope', 6,  'ZP',    '1', '2025-03-01', '2025-03-01', '7101', '尾零正數', '主單位一千點九九',     NULL, 'ZeroPositive',  NULL, 0, 1,  10009900,   10009900,    0,         'DEBIT'),
            ('scope', 7,  'ZN',    '1', '2025-03-02', '2025-03-02', '7102', '尾零負數', '主單位負一千點九九',   NULL, 'ZeroNegative',  NULL, 0, 1, -10009900,   0,           10009900,  'CREDIT'),
            ('scope', 8,  'ZSUB',  '1', '2025-03-03', '2025-03-03', '7103', '次單位',   '零點九九',             NULL, 'SubUnit',       NULL, 0, 1,  9900,       9900,        0,         'DEBIT'),
            ('scope', 9,  'Z6',    '1', '2025-03-04', '2025-03-04', '7104', '六零命中', '一百萬點一二三四',     NULL, 'SixZerosHit',   NULL, 0, 1,  10000001234,10000001234, 0,         'DEBIT'),
            ('scope', 10, 'Z6N',   '1', '2025-03-05', '2025-03-05', '7105', '六零邊界', '九十九萬九千九百九九', NULL, 'SixZerosMiss',  NULL, 0, 1,  9999999999, 9999999999,  0,         'DEBIT'),
            ('scope', 11, 'DIG',   '1', '2025-04-01', '2025-04-01', '7201', '尾碼',     '負一萬二千三百四十五', NULL, 'DigitsLong',    NULL, 0, 1, -123456700,  0,           123456700, 'CREDIT'),
            ('scope', 12, 'SHORT', '1', '2025-04-02', '2025-04-02', '7202', '短尾碼',   '四十五點六七',         NULL, 'DigitsShort',   NULL, 0, 1,  456700,     456700,      0,         'DEBIT'),
            ('scope', 13, 'ZERO',  '1', '2025-04-03', '2025-04-03', '7203', '零值',     '零值不算尾零',         NULL, 'ZeroValue',     NULL, 0, 1,  0,          0,           0,         'DEBIT'),
            ('scope', 14, 'T1',    '1', '2025-05-01', '2025-05-01', '7301', '矩陣',     'matrix-hit-in',         NULL, 'TagOne',        NULL, 0, 1,  1000000,    1000000,     0,         'DEBIT'),
            ('scope', 15, 'T1',    '2', '2026-05-01', '2026-05-01', '7302', '矩陣',     'matrix-companion-out',  NULL, 'TagOne',        NULL, 0, 0,  2000000,    2000000,     0,         'DEBIT'),
            ('scope', 16, 'T2',    '1', '2025-05-02', '2025-05-02', '7303', '矩陣',     'matrix-companion-in',   NULL, 'TagTwo',        NULL, 0, 1,  3000000,    3000000,     0,         'DEBIT'),
            ('scope', 17, 'T2',    '2', '2026-05-02', '2026-05-02', '7304', '矩陣',     'matrix-hit-out',        NULL, 'TagTwo',        NULL, 0, 0,  4000000,    4000000,     0,         'DEBIT'),
            ('scope', 18, 'T1',    '3', NULL,         NULL,         '7305', '矩陣',     'matrix-companion-null', NULL, 'TagOne',        NULL, 0, 0,  12300,      12300,       0,         'DEBIT'),
            ('scope', 19, 'C01',   '3', '2025-07-01', '2026-01-15', '1200', '應收款',   '狀態排除正常對方',     NULL, 'CrossPreparer', NULL, 1, 0,  2500000,    2500000,     0,         'DEBIT'),
            ('scope', 20, 'F01',   '2', '2025-02-02', '2025-02-02', '6001', '低頻科目', '狀態排除頻率列',       NULL, 'CrossPreparer', NULL, 0, 0,  999900,     999900,      0,         'DEBIT'),
            ('scope', 21, 'T1',    '4', '2025-05-03', '2025-05-03', '7306', '矩陣',     'matrix-hit-in',         NULL, 'TagOne',        NULL, 0, 0,  500000,     500000,      0,         'DEBIT');

        INSERT INTO target_account_mapping
            (batch_id, source_row_number, account_code, account_name, standardized_category)
        VALUES
            ('scope-map', 1, '4100', '收入',   'Revenue'),
            ('scope-map', 2, '1200', '應收款', 'Receivables'),
            ('scope-map', 3, '6001', '低頻科目', 'Others');
        """;

    protected abstract IFilterRunRepository FilterRepository { get; }

    protected abstract IFilterRunMaterializer Materializer { get; }

    protected abstract ITagMatrixRowPageRepository RowMatrixRepository { get; }

    protected abstract ITagMatrixVoucherPageRepository VoucherMatrixRepository { get; }

    private protected abstract IValidationFactsPort ValidationFactsPort { get; }

    private protected abstract IPrescreenFactsPort PrescreenFactsPort { get; }

    protected abstract INullRecordsPageRepository NullRecordsRepository { get; }

    protected abstract ITagMatrixScenariosRepository MatrixCountsRepository { get; }

    protected abstract string ProjectId { get; }

    [Fact]
    public async Task BlankPostDateDetection_IsSourceQualityOnly()
    {
        var plan = JetAuditProgram.Plan(new ValidationRequest(
            ProjectId,
            HasGlMapping: true,
            HasTbMapping: false,
            PeriodStart: "2025-01-01",
            PeriodEnd: "2025-12-31",
            MoneyScale: MoneyScale,
            SampleSeed: 48271,
            RunId: "blank-date-run",
            GeneratedUtc: DateTimeOffset.UnixEpoch,
            SampleSize: 0));
        var result = await ValidationFactsPort.ExecuteAsync(plan, CancellationToken.None);

        Assert.Equal(2, result.SourceQualityFindingCount);
        Assert.Equal(0, result.NullAccountCount);
        Assert.Equal(0, result.NullDocumentCount);
        Assert.Equal(0, result.NullDescriptionCount);
        Assert.Equal(0, result.OutOfRangeDateCount);

    }

    [Fact]
    public async Task ValidationRules_UseEffectivePopulation_AndExposePartitionSummary()
    {
        var plan = JetAuditProgram.Plan(new ValidationRequest(
            ProjectId,
            HasGlMapping: true,
            HasTbMapping: false,
            PeriodStart: "2025-01-01",
            PeriodEnd: "2025-12-31",
            MoneyScale: MoneyScale,
            SampleSeed: 48271,
            RunId: "effective-population-run",
            GeneratedUtc: DateTimeOffset.UnixEpoch,
            SampleSize: 100));

        var result = await ValidationFactsPort.ExecuteAsync(plan, CancellationToken.None);

        Assert.Equal(21, result.PopulationSummary.Raw.RowCount);
        Assert.Equal(12, result.PopulationSummary.Effective.RowCount);
        Assert.Equal(9, result.PopulationSummary.Excluded.RowCount);
        Assert.Equal(
            result.PopulationSummary.Raw.RowCount,
            result.PopulationSummary.Effective.RowCount + result.PopulationSummary.Excluded.RowCount);
        Assert.Equal(12, result.AmountBinCounts.Sum(bin => bin.Count));
        Assert.Equal(11, result.UnbalancedDocumentCount);
        Assert.Equal(12, result.InfSampleCount);
        Assert.Equal(2, result.SourceQualityFindingCount);
    }

    [Fact]
    public async Task PrescreenRulesAndSummaries_UseEffectivePopulation()
    {
        var plan = JetAuditProgram.Plan(new PrescreenRequest(
            ProjectId: ProjectId,
            HasGlMapping: true,
            PeriodStart: "2025-01-01",
            PeriodEnd: "2025-12-31",
            MoneyScale: MoneyScale,
            SampleSeed: 48_271,
            RunId: "effective-prescreen-run",
            GeneratedUtc: DateTimeOffset.UnixEpoch,
            LastPeriodStart: "2025-12-31",
            HasApprovalDate: true,
            HasCreatedBy: true,
            HasHolidays: false,
            HasAccountMapping: true,
            HasRevenue: true,
            HasCounterpart: true,
            HasAuthorizedPreparers: false,
            NonWorkingDays: [0, 6]));

        var result = await PrescreenFactsPort.ExecuteAsync(plan, CancellationToken.None);

        Assert.Equal(0, result.PostPeriodApprovalCount);
        Assert.Equal(1, result.UnexpectedAccountPairCount);
        Assert.Equal(12, result.TotalEntryCount);
        Assert.Equal(12, result.TotalPreparerCount);
        Assert.Equal(12, result.Creators.Sum(row => row.EntryCount));
        Assert.Equal(12, result.DistinctAccountCount);
        Assert.Equal(12, result.Accounts.Sum(row => row.EntryCount));
    }

    private static FilterRuleContext RuleContext(GlPopulationScope scope) =>
        new(MoneyScale, null, "2025-01-01", "2025-12-31", PopulationScope: scope);

    private static GlPopulationContext PopulationContext(GlPopulationScope scope) =>
        new(scope, "2025-01-01", "2025-12-31");

    private static FilterRuleSpec Rule(FilterRuleType type) =>
        new(FilterJoin.And, type, null, null, [], TextMatchMode.Contains,
            null, null, null, null, null, null);

    private static FilterRuleSpec TextRule(string field, string value, TextMatchMode mode = TextMatchMode.Exact) =>
        Rule(FilterRuleType.Text) with { Field = field, Keywords = [value], Mode = mode };

    private static readonly IReadOnlyDictionary<FilterRuleType, FilterRuleSpec> CounterpartRules =
        new Dictionary<FilterRuleType, FilterRuleSpec>
        {
            [FilterRuleType.Prescreen] = Rule(FilterRuleType.Prescreen) with
            {
                PrescreenKey = PrescreenRuleKeys.UnexpectedAccountPair
            },
            [FilterRuleType.RevenueWithoutNormalCounterpart] =
                Rule(FilterRuleType.RevenueWithoutNormalCounterpart)
        };

    private static FilterScenarioSpec Scenario(params FilterRuleSpec[] rules) =>
        new("母體 scope golden", "21 列手算 fixture", [new FilterGroupSpec(FilterJoin.And, rules)]);

    private static string Identity(FilterPreviewRow row) =>
        $"{row.DocumentNumber}|{row.LineItem}";

    private static string Identity(RowTagRow row) =>
        $"{row.DocumentNumber}|{row.LineItem}";

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

    // 唯一母體：auditPeriod 只消費 is_effective；期外、NULL 與 status-excluded 均排除。
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

    // C01 的正常對方只存在於期外或 status-excluded 列，兩條規則都必須命中 Revenue 貸方。
    [Theory]
    [InlineData(FilterRuleType.Prescreen, GlPopulationScope.AuditPeriod, new[] { "C01|1" })]
    [InlineData(FilterRuleType.RevenueWithoutNormalCounterpart, GlPopulationScope.AuditPeriod, new[] { "C01|1" })]
    public async Task CounterpartSubquery_CrossPeriodVoucher_UsesRevisionScope(
        FilterRuleType type,
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
        var exactPair = Rule(FilterRuleType.AccountPair) with
        {
            PairMode = AccountPairModes.Exact,
            DebitCategory = "Receivables",
            CreditCategory = "Revenue"
        };

        Assert.Equal(expectedIdentities, await HitIdentitiesAsync(Scenario(exactPair), scope));
    }

    // 同一分組鍵在有效母體只有 1 列；期外與 status-excluded 不能拉高 frequency count。
    [Theory]
    [InlineData(FilterRuleType.CustomPreparerEntryCount, "createBy", "CrossPreparer", GlPopulationScope.AuditPeriod, new[] { "C01|1" })]
    [InlineData(FilterRuleType.CustomAccountEntryCount, "accNum", "6001", GlPopulationScope.AuditPeriod, new[] { "F01|1" })]
    public async Task FrequencySubquery_CrossPeriodGroup_UsesRevisionScope(
        FilterRuleType type,
        string field,
        string value,
        GlPopulationScope scope,
        string[] expectedIdentities)
    {
        var frequency = Rule(type) with { MaxEntries = 1 };
        var scenario = Scenario(TextRule(field, value), frequency);

        Assert.Equal(expectedIdentities, await HitIdentitiesAsync(scenario, scope));
    }

    [Fact]
    public async Task CustomTrailingZeros_DecimalAndNegativeAmounts_UsesMajorUnitInteger()
    {
        // Legacy @int oracle：±1,000.99 的主單位整數都是 1,000，故命中 3 個尾零；0.99 與零值不命中。
        var rule = Rule(FilterRuleType.CustomTrailingZeros) with { Digits = 3 };

        Assert.Equal(
            ["Z6|1", "ZN|1", "ZP|1"],
            await HitIdentitiesAsync(Scenario(rule), GlPopulationScope.AuditPeriod));
    }

    [Fact]
    public async Task PrescreenTrailingZeros_FractionalAmount_UsesSixZeroMajorUnitBoundary()
    {
        // BVA：1,000,000.1234 的整數部分在六零邊界上，999,999.9999 是下鄰；只有前者命中。
        var rule = Rule(FilterRuleType.Prescreen) with
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
        var rule = Rule(FilterRuleType.TrailingDigits) with { Keywords = ["345"] };

        Assert.Equal(
            ["DIG|1"],
            await HitIdentitiesAsync(Scenario(rule), GlPopulationScope.AuditPeriod));
    }

    [Fact]
    public async Task TagMatrix_InScopeHitWithOutOfScopeCompanion_ScopesRowsPositionsAndVoucherTotal()
    {
        await MaterializeTagScenariosAsync();
        var request = new PageRequest(null, 50);

        var auditRows = await RowMatrixRepository.GetPageAsync(
            ProjectId, PopulationContext(GlPopulationScope.AuditPeriod), request, [1], CancellationToken.None);
        Assert.Equal(["T1|1"], auditRows.Page.Rows.Select(Identity).ToList());
        Assert.Equal([1], auditRows.PositionsByEntry[auditRows.EntryIds[0]]);

        var auditVouchers = await VoucherMatrixRepository.GetPageAsync(
            ProjectId, PopulationContext(GlPopulationScope.AuditPeriod), request, [1], CancellationToken.None);
        var auditVoucher = Assert.Single(auditVouchers.Page.Rows);
        Assert.Equal("T1", auditVoucher.DocumentNumber);
        Assert.Equal(1_000_000L, auditVoucher.VoucherTotalScaled);
        Assert.Equal([1], auditVouchers.PositionsByDoc["T1"]);

        var scenarioCounts = await MatrixCountsRepository.GetCountsAsync(
            ProjectId,
            CancellationToken.None);
        Assert.Equal((1L, 1L), scenarioCounts[1]);
        Assert.DoesNotContain(2, scenarioCounts.Keys);

    }

    [Fact]
    public async Task TagMatrix_OutOfScopeOnlyHit_AuditScopeDoesNotAdmitVoucher()
    {
        await MaterializeTagScenariosAsync();
        var request = new PageRequest(null, 50);

        var auditRows = await RowMatrixRepository.GetPageAsync(
            ProjectId, PopulationContext(GlPopulationScope.AuditPeriod), request, [2], CancellationToken.None);
        var auditVouchers = await VoucherMatrixRepository.GetPageAsync(
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
                new MaterializableScenario(1, Scenario(TextRule("description", "matrix-hit-in"))),
                new MaterializableScenario(2, Scenario(TextRule("description", "matrix-hit-out")))
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

        FilterRepository = new LocalFilterRunRepository(database);
        Materializer = new LocalFilterRunMaterializer(database);
        RowMatrixRepository = new LocalTagMatrixRowPageRepository(database);
        VoucherMatrixRepository = new LocalTagMatrixVoucherPageRepository(database);
        ValidationFactsPort = new LocalValidationRunRepository(database);
        PrescreenFactsPort = new LocalPrescreenRunRepository(database);
        NullRecordsRepository = new LocalNullRecordsPageRepository(database);
        MatrixCountsRepository = new LocalTagMatrixScenariosRepository(database);
    }

    protected override IFilterRunRepository FilterRepository { get; }

    protected override IFilterRunMaterializer Materializer { get; }

    protected override ITagMatrixRowPageRepository RowMatrixRepository { get; }

    protected override ITagMatrixVoucherPageRepository VoucherMatrixRepository { get; }

    private protected override IValidationFactsPort ValidationFactsPort { get; }

    private protected override IPrescreenFactsPort PrescreenFactsPort { get; }

    protected override INullRecordsPageRepository NullRecordsRepository { get; }

    protected override ITagMatrixScenariosRepository MatrixCountsRepository { get; }

    protected override string ProjectId { get; }

    public void Dispose() => _root.Dispose();
}

public sealed class DuckDbGlPopulationScopeGoldenTests : GlPopulationScopeGoldenTests, IDisposable
{
    private readonly TempProjectRoot _root = new();

    public DuckDbGlPopulationScopeGoldenTests()
    {
        var folder = new JetProjectFolder(_root.Path);
        var database = new DuckDbProjectDatabase(folder);
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

        FilterRepository = new LocalFilterRunRepository(database);
        Materializer = new LocalFilterRunMaterializer(database);
        RowMatrixRepository = new LocalTagMatrixRowPageRepository(database);
        VoucherMatrixRepository = new LocalTagMatrixVoucherPageRepository(database);
        ValidationFactsPort = new LocalValidationRunRepository(database);
        PrescreenFactsPort = new LocalPrescreenRunRepository(database);
        NullRecordsRepository = new LocalNullRecordsPageRepository(database);
        MatrixCountsRepository = new LocalTagMatrixScenariosRepository(database);
    }

    protected override IFilterRunRepository FilterRepository { get; }

    protected override IFilterRunMaterializer Materializer { get; }

    protected override ITagMatrixRowPageRepository RowMatrixRepository { get; }

    protected override ITagMatrixVoucherPageRepository VoucherMatrixRepository { get; }

    private protected override IValidationFactsPort ValidationFactsPort { get; }

    private protected override IPrescreenFactsPort PrescreenFactsPort { get; }

    protected override INullRecordsPageRepository NullRecordsRepository { get; }

    protected override ITagMatrixScenariosRepository MatrixCountsRepository { get; }

    protected override string ProjectId { get; }

    public void Dispose() => _root.Dispose();
}

/// <summary>
/// SQL Server 無法讓繼承而來的無條件 Fact/Theory 在探索期誠實略過，因此用具名 wrapper 委派到私有 engine；
/// wrapper 表面由反射守衛，新增 golden test 或 InlineData 時漏包裝會直接紅燈。
/// </summary>
public sealed class SqlServerGlPopulationScopeGoldenTests
{
    [SqlServerFact]
    public Task BlankPostDateDetection_IsSourceQualityOnly() =>
        RunAsync(engine => engine.BlankPostDateDetection_IsSourceQualityOnly());

    [SqlServerFact]
    public Task ValidationRules_UseEffectivePopulation_AndExposePartitionSummary() =>
        RunAsync(engine => engine.ValidationRules_UseEffectivePopulation_AndExposePartitionSummary());

    [SqlServerFact]
    public Task PrescreenRulesAndSummaries_UseEffectivePopulation() =>
        RunAsync(engine => engine.PrescreenRulesAndSummaries_UseEffectivePopulation());

    [SqlServerTheory]
    [InlineData(GlPopulationScope.AuditPeriod, new[]
    {
        "C01|1", "DIG|1", "F01|1", "SHORT|1", "T1|1",
        "T2|1", "Z6N|1", "Z6|1", "ZERO|1", "ZN|1", "ZP|1", "ZSUB|1"
    })]
    public Task OuterPopulationScope_FixedFixture_ReturnsExactRows(
        GlPopulationScope scope,
        string[] expectedIdentities) =>
        RunAsync(engine => engine.OuterPopulationScope_FixedFixture_ReturnsExactRows(scope, expectedIdentities));

    [SqlServerTheory]
    [InlineData(FilterRuleType.Prescreen, GlPopulationScope.AuditPeriod, new[] { "C01|1" })]
    [InlineData(FilterRuleType.RevenueWithoutNormalCounterpart, GlPopulationScope.AuditPeriod, new[] { "C01|1" })]
    public Task CounterpartSubquery_CrossPeriodVoucher_UsesRevisionScope(
        FilterRuleType type,
        GlPopulationScope scope,
        string[] expectedIdentities) =>
        RunAsync(engine => engine.CounterpartSubquery_CrossPeriodVoucher_UsesRevisionScope(type, scope, expectedIdentities));

    [SqlServerTheory]
    [InlineData(GlPopulationScope.AuditPeriod, new string[0])]
    public Task AccountPairSubquery_CrossPeriodVoucher_UsesRevisionScope(
        GlPopulationScope scope,
        string[] expectedIdentities) =>
        RunAsync(engine => engine.AccountPairSubquery_CrossPeriodVoucher_UsesRevisionScope(scope, expectedIdentities));

    [SqlServerTheory]
    [InlineData(FilterRuleType.CustomPreparerEntryCount, "createBy", "CrossPreparer", GlPopulationScope.AuditPeriod, new[] { "C01|1" })]
    [InlineData(FilterRuleType.CustomAccountEntryCount, "accNum", "6001", GlPopulationScope.AuditPeriod, new[] { "F01|1" })]
    public Task FrequencySubquery_CrossPeriodGroup_UsesRevisionScope(
        FilterRuleType type,
        string field,
        string value,
        GlPopulationScope scope,
        string[] expectedIdentities) =>
        RunAsync(engine => engine.FrequencySubquery_CrossPeriodGroup_UsesRevisionScope(
            type, field, value, scope, expectedIdentities));

    [SqlServerFact]
    public Task CustomTrailingZeros_DecimalAndNegativeAmounts_UsesMajorUnitInteger() =>
        RunAsync(engine => engine.CustomTrailingZeros_DecimalAndNegativeAmounts_UsesMajorUnitInteger());

    [SqlServerFact]
    public Task PrescreenTrailingZeros_FractionalAmount_UsesSixZeroMajorUnitBoundary() =>
        RunAsync(engine => engine.PrescreenTrailingZeros_FractionalAmount_UsesSixZeroMajorUnitBoundary());

    [SqlServerFact]
    public Task TrailingDigits_DecimalAndNegativeAmount_UsesMajorUnitIntegerAndLengthGuard() =>
        RunAsync(engine => engine.TrailingDigits_DecimalAndNegativeAmount_UsesMajorUnitIntegerAndLengthGuard());

    [SqlServerFact]
    public Task TagMatrix_InScopeHitWithOutOfScopeCompanion_ScopesRowsPositionsAndVoucherTotal() =>
        RunAsync(engine => engine.TagMatrix_InScopeHitWithOutOfScopeCompanion_ScopesRowsPositionsAndVoucherTotal());

    [SqlServerFact]
    public Task TagMatrix_OutOfScopeOnlyHit_AuditScopeDoesNotAdmitVoucher() =>
        RunAsync(engine => engine.TagMatrix_OutOfScopeOnlyHit_AuditScopeDoesNotAdmitVoucher());

    [Fact]
    public async Task WrapperMethods_CoverEveryBaseGoldenTest()
    {
        var baseSurface = await TestSurfaceAsync(typeof(GlPopulationScopeGoldenTests));
        var wrapperSurface = await TestSurfaceAsync(
            typeof(SqlServerGlPopulationScopeGoldenTests), nameof(WrapperMethods_CoverEveryBaseGoldenTest));

        Assert.NotEmpty(baseSurface);
        Assert.Equal(baseSurface, wrapperSurface);
    }

    private static async Task<IReadOnlyList<string>> TestSurfaceAsync(Type type, string? exclude = null)
    {
        var surface = new List<string>();
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                     .Where(method => method.GetCustomAttributes<FactAttribute>(inherit: true).Any()
                         && method.Name != exclude))
        {
            await using var disposals = new Xunit.Sdk.DisposalTracker();
            var rows = new List<string>();
            foreach (var attribute in method.GetCustomAttributes<InlineDataAttribute>())
            {
                rows.AddRange((await attribute.GetData(method, disposals))
                    .Select(row => $"{method.Name}({JsonSerializer.Serialize(row.GetData())})"));
            }
            surface.AddRange(rows.Count == 0 ? [method.Name] : rows);
        }

        return surface.OrderBy(value => value, StringComparer.Ordinal).ToList();
    }

    private static async Task RunAsync(Func<GlPopulationScopeGoldenTests, Task> assertion)
    {
        var sql = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(sql);

        await using (sql)
        {
            var engine = await SqlServerEngine.CreateAsync(sql);
            await assertion(engine);
        }
    }

    private sealed class SqlServerEngine : GlPopulationScopeGoldenTests
    {
        private SqlServerEngine(TempSqlServerProject sql)
        {
            ProjectId = sql.ProjectId;
            FilterRepository = new SqlServerFilterRunRepository(sql.Database);
            Materializer = new SqlServerFilterRunMaterializer(sql.Database);
            RowMatrixRepository = new SqlServerTagMatrixRowPageRepository(sql.Database);
            VoucherMatrixRepository = new SqlServerTagMatrixVoucherPageRepository(sql.Database);
            ValidationFactsPort = new SqlServerValidationRunRepository(sql.Database);
            PrescreenFactsPort = new SqlServerPrescreenRunRepository(sql.Database);
            NullRecordsRepository = new SqlServerNullRecordsPageRepository(sql.Database);
            MatrixCountsRepository = new SqlServerTagMatrixScenariosRepository(sql.Database);
        }

        protected override IFilterRunRepository FilterRepository { get; }

        protected override IFilterRunMaterializer Materializer { get; }

        protected override ITagMatrixRowPageRepository RowMatrixRepository { get; }

        protected override ITagMatrixVoucherPageRepository VoucherMatrixRepository { get; }

        private protected override IValidationFactsPort ValidationFactsPort { get; }

        private protected override IPrescreenFactsPort PrescreenFactsPort { get; }

        protected override INullRecordsPageRepository NullRecordsRepository { get; }

        protected override ITagMatrixScenariosRepository MatrixCountsRepository { get; }

        protected override string ProjectId { get; }

        public static async Task<SqlServerEngine> CreateAsync(TempSqlServerProject sql)
        {
            var transformed = Regex.Replace(FixtureSql, "'[^']*'", match => "N" + match.Value)
                .Replace("INSERT INTO ", "INSERT INTO {s}.");

            await using var connection = sql.Database.CreateConnection(sql.ProjectId);
            await connection.OpenAsync();
            await using var command = sql.Database.CreateCommand(connection, sql.ProjectId, transformed);
            await command.ExecuteNonQueryAsync();
            return new SqlServerEngine(sql);
        }
    }
}
