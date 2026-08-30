using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// GL 規則述詞的 provider 等價測試基底（guide §13 golden test 的落地形狀）。
/// oracle：手算 ≤20 列固定 fixture（fixture 註解列出每條述詞的預期命中傳票）；
/// 斷言鎖「值＋身分」（命中哪幾張傳票），不是只鎖筆數。
/// 未來 SqlServer*/DuckDb* provider 只需新增子類實作 <see cref="Repository"/>
/// 與 <see cref="ProjectId"/>，即可跑完全同一套等價斷言。
/// </summary>
public abstract class GlRuleSqlEquivalenceTests
{
    /// <summary>
    /// 固定 fixture（MoneyScale=100、查核期間 2025-01-01～2025-12-31、期末財報準備日 2025-12-31；
    /// 假日 2025-10-10、補班日 2025-02-08）。每張傳票兩列借貸對沖：
    ///   D01 2025-03-05(三) 核准 03-06       摘要「進貨」          |100.00|
    ///   D02 2025-04-03(四) 核准 2026-01-12  摘要「調整分錄」      |1,000.00| 人工
    ///   D03 2025-06-07(六) 核准 2025-10-10  摘要 NULL／「沖回」   |123.45|  旗標 NULL
    ///   D04 2025-10-10(五·假日) 核准 10-11(六) 摘要「例假日入帳」 |7.77|
    ///   D05 2025-02-08(六·補班) 核准 02-08  摘要「補班日傳票」    |5.00|   人工
    ///   D06 2025-05-14(三) 核准 05-15       摘要「整數金額」      |2,000,000.00|（6 個尾數 0 = 固定預設門檻）
    ///   D07 2026-01-05(一·期外) 核准 2025-12-22(一) 摘要「期外分錄」 |3.33|
    ///   D08 日期 NULL                        摘要「無日期」        |1.11|
    ///   D09 2025-07-09(三) 核准 07-10        摘要「零元測試」      借方 0 元＋貸方 |0.50|
    ///   D10 2025-09-10(三) 核准 09-11        摘要「領料出庫」      借 5101 銷貨成本＋貸 4101 銷貨收入 |333.33|
    /// 科目配對（fixture 對照）：1101→Cash、2201→Receivables、4101→自訂分類（semantic role=revenue）、
    /// 5101→Others。4101 的 transitional standardized_category 刻意寫 Others；若述詞仍比較顯示文字，
    /// 所有收入規則都會失敗，因此同一套 SQLite／SQL Server golden 會鎖住 role-based 行為。
    /// 連續零尾數門檻為方法學固定預設 6：先取主單位整數，再以 10⁶ 取模；
    /// 只有 D06（|2,000,000.00| 的主單位整數為 2,000,000）命中。
    /// 預期命中（傳票集合）：
    ///   postPeriodApproval → {D02}；suspiciousKeywords → {D02}（調整）；
    ///   trailingZeros → {D06}（固定預設 6；D02 的 1,000 僅 3 個 0 不再命中）；weekendPosting → {D03, D05}；
    ///   weekendApproval → {D04, D05}（週末規則皆納入補班日）；holidayPosting → {D04}；
    ///   holidayApproval → {D03}；blankDescription → {D03}（NULL 列）；
    ///   unexpectedAccountPair（否定面）→ {D09, D10}（貸 Revenue 且同傳票無「金額 > 0 的
    ///     Cash/Receivables/ReceiptInAdvance 借方」；D01/D02/D04–D06 有正值 Cash 借方對方 → 不命中；
    ///     D09 的 Cash 借方是 0 元，依 2026-08-14 零元邊界不算對方 → 命中；D03 無 Revenue 貸方 → 不命中；只標貸方列）。
    /// </summary>
    protected const string FixtureSql =
        """
        INSERT INTO target_gl_entry
            (batch_id, source_row_number, document_number, line_item, post_date, approval_date,
             account_code, account_name, document_description, source_module, created_by, approved_by,
             is_manual, is_effective, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
        VALUES
            ('b1', 1,  'D01', '1', '2025-03-05', '2025-03-06', '1101', '現金',     '進貨',       NULL, '王一', NULL, 0,    1, 10000,  10000, 0,      'DEBIT'),
            ('b1', 2,  'D01', '2', '2025-03-05', '2025-03-06', '4101', '銷貨收入', '進貨',       NULL, '王一', NULL, 0,    1, -10000, 0,     10000,  'CREDIT'),
            ('b1', 3,  'D02', '1', '2025-04-03', '2026-01-12', '1101', '現金',     '調整分錄',   NULL, '李二', NULL, 1,    1, 100000, 100000, 0,     'DEBIT'),
            ('b1', 4,  'D02', '2', '2025-04-03', '2026-01-12', '4101', '銷貨收入', '調整分錄',   NULL, '李二', NULL, 1,    1, -100000, 0,    100000, 'CREDIT'),
            ('b1', 5,  'D03', '1', '2025-06-07', '2025-10-10', '2201', '應付帳款', NULL,         NULL, '王一', NULL, NULL, 1, 12345,  12345, 0,      'DEBIT'),
            ('b1', 6,  'D03', '2', '2025-06-07', '2025-10-10', '1101', '現金',     '沖回',       NULL, '王一', NULL, NULL, 1, -12345, 0,     12345,  'CREDIT'),
            ('b1', 7,  'D04', '1', '2025-10-10', '2025-10-11', '1101', '現金',     '例假日入帳', NULL, '李二', NULL, 0,    1, 777,    777,   0,      'DEBIT'),
            ('b1', 8,  'D04', '2', '2025-10-10', '2025-10-11', '4101', '銷貨收入', '例假日入帳', NULL, '李二', NULL, 0,    1, -777,   0,     777,    'CREDIT'),
            ('b1', 9,  'D05', '1', '2025-02-08', '2025-02-08', '1101', '現金',     '補班日傳票', NULL, '王一', NULL, 1,    1, 500,    500,   0,      'DEBIT'),
            ('b1', 10, 'D05', '2', '2025-02-08', '2025-02-08', '4101', '銷貨收入', '補班日傳票', NULL, '王一', NULL, 1,    1, -500,   0,     500,    'CREDIT'),
            ('b1', 11, 'D06', '1', '2025-05-14', '2025-05-15', '1101', '現金',     '整數金額',   NULL, '李二', NULL, 0,    1, 200000000, 200000000, 0,     'DEBIT'),
            ('b1', 12, 'D06', '2', '2025-05-14', '2025-05-15', '4101', '銷貨收入', '整數金額',   NULL, '李二', NULL, 0,    1, -200000000, 0,    200000000, 'CREDIT'),
            ('b1', 13, 'D07', '1', '2026-01-05', '2025-12-22', '1101', '現金',     '期外分錄',   NULL, '王一', NULL, 0,    0, 333,    333,   0,      'DEBIT'),
            ('b1', 14, 'D07', '2', '2026-01-05', '2025-12-22', '4101', '銷貨收入', '期外分錄',   NULL, '王一', NULL, 0,    0, -333,   0,     333,    'CREDIT'),
            ('b1', 15, 'D08', '1', NULL,         NULL,         '1101', '現金',     '無日期',     NULL, '王一', NULL, 0,    0, 111,    111,   0,      'DEBIT'),
            ('b1', 16, 'D08', '2', NULL,         NULL,         '4101', '銷貨收入', '無日期',     NULL, '王一', NULL, 0,    0, -111,   0,     111,    'CREDIT'),
            ('b1', 17, 'D09', '1', '2025-07-09', '2025-07-10', '1101', '現金',     '零元測試',   NULL, '王一', NULL, 0,    1, 0,      0,     0,      'DEBIT'),
            ('b1', 18, 'D09', '2', '2025-07-09', '2025-07-10', '4101', '銷貨收入', '零元測試',   NULL, '王一', NULL, 0,    1, -50,    0,     50,     'CREDIT'),
            ('b1', 19, 'D10', '1', '2025-09-10', '2025-09-11', '5101', '銷貨成本', '領料出庫',   NULL, '王一', NULL, 0,    1, 33333,  33333, 0,      'DEBIT'),
            ('b1', 20, 'D10', '2', '2025-09-10', '2025-09-11', '4101', '銷貨收入', '領料出庫',   NULL, '王一', NULL, 0,    1, -33333, 0,     33333,  'CREDIT');

        INSERT INTO staging_calendar_raw_day (day_type, date) VALUES
            ('holiday', '2025-10-10'),
            ('makeup',  '2025-02-08');

        INSERT INTO config_account_taxonomy
            (category_id, label, ordinal, semantic_role, is_builtin, revision)
        VALUES
            ('custom.0123456789abcdef0123456789abcdef', 'Custom revenue', 5, 'revenue', 0, 1);

        INSERT INTO target_account_mapping
            (batch_id, source_row_number, account_code, account_name, standardized_category, category_id)
        VALUES
            ('am1', 1, '1101', '現金',     'Cash',        NULL),
            ('am1', 2, '2201', '應付帳款', 'Receivables', NULL),
            ('am1', 3, '4101', '銷貨收入', 'Others',      'custom.0123456789abcdef0123456789abcdef'),
            ('am1', 4, '5101', '銷貨成本', 'Others',      NULL);
        """;

    protected const int MoneyScale = 100;

    protected abstract IFilterRunRepository Repository { get; }
    protected abstract string ProjectId { get; }

    // 所有規則共用唯一有效母體；D07／D08 保留在 fixture，專門防止非有效列偷渡。
    private static FilterRuleContext Context => new(
        MoneyScale,
        "2025-12-31",
        "2025-01-01",
        "2025-12-31",
        PopulationScope: GlPopulationScope.AuditPeriod);

    private static FilterScenarioSpec SingleRule(FilterRuleSpec rule) =>
        new("等價測試", "固定 fixture 的述詞等價斷言", [new FilterGroupSpec(FilterJoin.And, [rule])]);

    private static FilterRuleSpec Prescreen(string key) =>
        new(FilterJoin.And, FilterRuleType.Prescreen, key, null, [], TextMatchMode.Contains,
            null, null, null, null, null, null);

    private async Task<IReadOnlyList<string>> HitDocumentsAsync(FilterRuleSpec rule)
    {
        var result = await Repository.PreviewAsync(ProjectId, SingleRule(rule), Context, CancellationToken.None);
        return result.PreviewRows
            .Select(r => r.DocumentNumber!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();
    }

    [Theory]
    [InlineData(PrescreenRuleKeys.PostPeriodApproval, new[] { "D02" })]
    [InlineData(PrescreenRuleKeys.SuspiciousKeywords, new[] { "D02" })]
    [InlineData(PrescreenRuleKeys.TrailingZeros, new[] { "D06" })]
    [InlineData(PrescreenRuleKeys.WeekendPosting, new[] { "D03", "D05" })]
    [InlineData(PrescreenRuleKeys.WeekendApproval, new[] { "D04", "D05" })]
    [InlineData(PrescreenRuleKeys.HolidayPosting, new[] { "D04" })]
    [InlineData(PrescreenRuleKeys.HolidayApproval, new[] { "D03" })]
    [InlineData(PrescreenRuleKeys.BlankDescription, new[] { "D03" })]
    public async Task PrescreenPredicate_FixedFixture_HitsExpectedDocuments(string key, string[] expectedDocs)
    {
        Assert.Equal(expectedDocs, await HitDocumentsAsync(Prescreen(key)));
    }

    [Fact]
    public async Task UnexpectedAccountPair_FixedFixture_HitsRevenueCreditWithoutCounterpart()
    {
        // guide §5 否定面（2026-07-08 spec §1）＋零元邊界（2026-08-14）：貸 Revenue 且同傳票
        // 無任何「金額 > 0 的 Cash/Receivables/ReceiptInAdvance 借方」才命中，只標貸方列。
        // D01/D02/D04–D06 有正值 Cash（1101）借方對方 → 不命中；D03 無 Revenue 貸方 → 不命中；
        // D09 的 Cash 借方是 0 元，不算已收到對價 → 命中；D10（借 5101 Others）無正常對方 → 命中。
        Assert.Equal(
            ["D09", "D10"],
            await HitDocumentsAsync(Prescreen(PrescreenRuleKeys.UnexpectedAccountPair)));
    }

    [Fact]
    public async Task NonAuthorizedPreparer_EmptyAuthorizedList_HitsNothing()
    {
        // 非授權編製人員述詞以 EXISTS 自保（繞過 Application validator 直接跑述詞）：fixture 無 target_authorized_preparer
        // 任何列。少了 EXISTS 前綴時 `created_by NOT IN (空集合)` 會反轉成全命中(8 張有編製者的傳票);
        // 有前綴 → 整體述詞 FALSE → 0 命中(與 prescreen.run 的 na 語意對齊)。鎖「無命中」而非筆數巧合。
        Assert.Empty(await HitDocumentsAsync(Prescreen(PrescreenRuleKeys.NonAuthorizedPreparer)));
    }

    [Fact]
    public async Task TextContains_FixedFixture_HitsExpectedDocuments()
    {
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.Text, null, "description",
            ["進貨"], TextMatchMode.Contains, null, null, null, null, null, null);

        Assert.Equal(["D01"], await HitDocumentsAsync(rule));
    }

    [Fact]
    public async Task TextNotContains_FixedFixture_TreatsNullAsEmptyAndHitsComplement()
    {
        // NULL 摘要視為空字串 → notContains「進貨」成立；只排除 D01 兩列。
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.Text, null, "description",
            ["進貨"], TextMatchMode.NotContains, null, null, null, null, null, null);

        Assert.Equal(["D02", "D03", "D04", "D05", "D06", "D09", "D10"], await HitDocumentsAsync(rule));
    }

    [Fact]
    public async Task DateRange_FixedFixture_BoundsAreInclusive()
    {
        // BVA：邊界含入——from=2025-03-05 恰為 D01 的過帳日；NULL 過帳日（D08）不命中。
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.DateRange, null, "postDate",
            [], TextMatchMode.Contains, "2025-03-05", "2025-04-03", null, null, null, null);

        Assert.Equal(["D01", "D02"], await HitDocumentsAsync(rule));
    }

    [Theory]
    [InlineData("1000.00", new[] { "D02", "D06" })] // 邊界值：|1,000.00| 恰等於下限 → 含入
    [InlineData("1000.01", new[] { "D06" })]        // 上鄰：剛超過 D02 金額 → 只剩 D06
    public async Task AmountRange_FixedFixture_AbsoluteScaledBoundary(string from, string[] expectedDocs)
    {
        var fromScaled = (long)(decimal.Parse(from) * MoneyScale);
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.NumRange, null, "amount",
            [], TextMatchMode.Contains, null, null, fromScaled, null, null, null);

        Assert.Equal(expectedDocs, await HitDocumentsAsync(rule));
    }

    // KCT 清單 H 尾數比對＝整數化金額末 k 位與樣態相等（＝legacy @Right(@Str(@int(x),1,0),k)）；k≥2 須整數 ≥ 10^(k-1)。
    // 000000（k=6，下界 10^5）：D06=2,000,000 整數末 6 位 = 000000 → 命中；D09 的次單位金額（0.50／0，整數 0）
    //   位數 < 6、字串短於樣態、長度不符不相等 → 不命中。
    // 000333：D10 整數 333 僅 3 位 < 6 → 不相等 → 空集合。跨 provider 由 [SqlServerTheory] 包裝在真 SQL Server 上同步驗證。
    [Theory]
    [InlineData("000000", new[] { "D06" })]
    [InlineData("000333", new string[0])]
    public async Task TrailingDigits_FixedFixture_MajorUnitTailRequiresEnoughDigits(string pattern, string[] expectedDocs)
    {
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.TrailingDigits, null, null,
            [pattern], TextMatchMode.Contains, null, null, null, null, null, null);

        Assert.Equal(expectedDocs, await HitDocumentsAsync(rule));
    }

    [Fact]
    public async Task DrCrOnly_FixedFixture_CreditSideOnly()
    {
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.DrCrOnly, null, null,
            [], TextMatchMode.Contains, null, null, null, null, "credit", null);

        var result = await Repository.PreviewAsync(ProjectId, SingleRule(rule), Context, CancellationToken.None);

        // 查核期間內每張傳票恰一列貸方：8 列、8 張傳票（D09 的 0 元列屬借方側，不在貸方）。
        Assert.Equal(8, result.Count);
        Assert.All(result.PreviewRows, r => Assert.Equal("CREDIT", r.DrCr));
    }

    [Fact]
    public async Task ManualAuto_FixedFixture_NullFlagNeverMatches()
    {
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.ManualAuto, null, null,
            [], TextMatchMode.Contains, null, null, null, null, null, true);

        // is_manual=1 → D02、D05；NULL（D03）不匹配 true 也不匹配 false。
        Assert.Equal(["D02", "D05"], await HitDocumentsAsync(rule));
    }

    [Fact]
    public async Task AccountPairDebitAnchor_FixedFixture_OutputsAnchorAndCounterRows()
    {
        // 借方錨定 Receivables（=2201）：只有 D03 含 Receivables 借方 → 輸出錨定列＋同傳票貸方列。
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.AccountPair, null, null,
            [], TextMatchMode.Contains, null, null, null, null, null, null,
            PairMode: AccountPairModes.DebitAnchor, DebitCategory: "Receivables");

        var result = await Repository.PreviewAsync(ProjectId, SingleRule(rule), Context, CancellationToken.None);

        Assert.Equal(["D03"], result.PreviewRows.Select(r => r.DocumentNumber).Distinct().ToList());
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task AccountPairExact_FixedFixture_ZeroAmountDebitCountsAsDebitSide()
    {
        // 精確配對 Cash 借＋Revenue 貸：除 D03 外全部命中；
        // D09 的 0 元 Cash 借方屬借方側（`>= 0` 裁決），改 `>` 即漏 D09。
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.AccountPair, null, null,
            [], TextMatchMode.Contains, null, null, null, null, null, null,
            PairMode: AccountPairModes.Exact, DebitCategory: "Cash", CreditCategory: "Revenue");

        Assert.Equal(
            ["D01", "D02", "D04", "D05", "D06", "D09"],
            await HitDocumentsAsync(rule));
    }

    [Fact]
    public async Task AccountPairExact_FixedFixture_NoQualifyingDocReturnsEmpty()
    {
        // Receivables 借＋Revenue 貸：D03 有 Receivables 借方但無 Revenue 貸方 → 空集合。
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.AccountPair, null, null,
            [], TextMatchMode.Contains, null, null, null, null, null, null,
            PairMode: AccountPairModes.Exact, DebitCategory: "Receivables", CreditCategory: "Revenue");

        var result = await Repository.PreviewAsync(ProjectId, SingleRule(rule), Context, CancellationToken.None);

        Assert.Equal(0, result.Count);
    }

    [Theory]
    [InlineData(2, new[] { "D01", "D02", "D06" })] // 10^2 主單位整數倍：100.00／1,000.00／2,000,000.00
    [InlineData(3, new[] { "D02", "D06" })]        // 10^3 主單位整數倍：1,000.00／2,000,000.00
    [InlineData(6, new[] { "D06" })]               // 固定預設 6：只剩 D06（|2,000,000.00| 為 10^6 整數倍）
    public async Task CustomTrailingZeros_FixedFixture_FixedDigitsBoundaries(int digits, string[] expectedDocs)
    {
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.CustomTrailingZeros, null, null,
            [], TextMatchMode.Contains, null, null, null, null, null, null, Digits: digits);

        Assert.Equal(expectedDocs, await HitDocumentsAsync(rule));
    }

    [Fact]
    public async Task CustomKeywords_FixedFixture_EquivalentToDescriptionContains()
    {
        // metamorphic：自訂關鍵字 ≡ text contains（description 欄、contains-any 語意）。
        var custom = new FilterRuleSpec(FilterJoin.And, FilterRuleType.CustomKeywords, null, null,
            ["進貨", "調整"], TextMatchMode.Contains, null, null, null, null, null, null);
        var text = new FilterRuleSpec(FilterJoin.And, FilterRuleType.Text, null, "description",
            ["進貨", "調整"], TextMatchMode.Contains, null, null, null, null, null, null);

        var customDocs = await HitDocumentsAsync(custom);

        Assert.Equal(["D01", "D02"], customDocs);
        Assert.Equal(await HitDocumentsAsync(text), customDocs);
    }

    [Fact]
    public async Task LeftFoldJoin_FixedFixture_OrThenAndGroups()
    {
        // ((suspiciousKeywords OR trailingZeros)) AND (|金額| >= 1,500.00)
        // suspicious={D02}、trailingZeros={D06}（固定預設 6）→ OR={D02,D06}；
        // ∩ (|金額|>=1,500.00 → {D06}) = {D06}（鎖左折疊與群組 AND 的優先序）。
        var scenario = new FilterScenarioSpec("組合", "左折疊驗證",
        [
            new FilterGroupSpec(FilterJoin.And,
            [
                Prescreen(PrescreenRuleKeys.SuspiciousKeywords),
                Prescreen(PrescreenRuleKeys.TrailingZeros) with { Join = FilterJoin.Or }
            ]),
            new FilterGroupSpec(FilterJoin.And,
            [
                new FilterRuleSpec(FilterJoin.And, FilterRuleType.NumRange, null, "amount",
                    [], TextMatchMode.Contains, null, null, 150_000L, null, null, null)
            ])
        ]);

        var result = await Repository.PreviewAsync(ProjectId, scenario, Context, CancellationToken.None);

        Assert.Equal(["D06"], result.PreviewRows.Select(r => r.DocumentNumber).Distinct().ToList());
    }
}

/// <summary>SQLite provider 的等價測試子類：temp 專案 DB + 固定 fixture。</summary>
public sealed class SqliteGlRuleSqlEquivalenceTests : GlRuleSqlEquivalenceTests, IDisposable
{
    private readonly TempProjectRoot _root = new();

    public SqliteGlRuleSqlEquivalenceTests()
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

        Repository = new LocalFilterRunRepository(database);
    }

    protected override IFilterRunRepository Repository { get; }
    protected override string ProjectId { get; }

    public void Dispose() => _root.Dispose();
}

/// <summary>
/// SQL Server provider 的等價測試（基底類註解承諾的第三個 provider；design §2.4）。
/// 基底測試方法掛的是無條件 <c>[Fact]</c>/<c>[Theory]</c>，xUnit 2.x 無法對「繼承來的測試」按子類條件略過——
/// 直接繼承會讓無 SQL Server 的機器紅燈（違反誠實 skip 紀律）。故本類不繼承，改以顯式包裝：
/// 每個基底測試對應一個 <c>[SqlServerFact]</c>/<c>[SqlServerTheory]</c> 包裝方法（缺 SQL Server 時具名略過），
/// 委派給私有引擎子類（私有巢狀類不被 xUnit 探索）在每測試獨立 schema 上執行同一套斷言。
/// 包裝與基底的漂移由 <see cref="WrapperMethods_CoverEveryBaseEquivalenceTest"/> 鎖住（方法名＋InlineData 逐列比對）。
/// </summary>
public sealed class SqlServerGlRuleSqlEquivalenceTests
{
    [SqlServerTheory]
    [InlineData(PrescreenRuleKeys.PostPeriodApproval, new[] { "D02" })]
    [InlineData(PrescreenRuleKeys.SuspiciousKeywords, new[] { "D02" })]
    [InlineData(PrescreenRuleKeys.TrailingZeros, new[] { "D06" })]
    [InlineData(PrescreenRuleKeys.WeekendPosting, new[] { "D03", "D05" })]
    [InlineData(PrescreenRuleKeys.WeekendApproval, new[] { "D04", "D05" })]
    [InlineData(PrescreenRuleKeys.HolidayPosting, new[] { "D04" })]
    [InlineData(PrescreenRuleKeys.HolidayApproval, new[] { "D03" })]
    [InlineData(PrescreenRuleKeys.BlankDescription, new[] { "D03" })]
    public Task PrescreenPredicate_FixedFixture_HitsExpectedDocuments(string key, string[] expectedDocs) =>
        RunAsync(e => e.PrescreenPredicate_FixedFixture_HitsExpectedDocuments(key, expectedDocs));

    [SqlServerFact]
    public Task UnexpectedAccountPair_FixedFixture_HitsRevenueCreditWithoutCounterpart() =>
        RunAsync(e => e.UnexpectedAccountPair_FixedFixture_HitsRevenueCreditWithoutCounterpart());

    [SqlServerFact]
    public Task NonAuthorizedPreparer_EmptyAuthorizedList_HitsNothing() =>
        RunAsync(e => e.NonAuthorizedPreparer_EmptyAuthorizedList_HitsNothing());

    [SqlServerFact]
    public Task TextContains_FixedFixture_HitsExpectedDocuments() =>
        RunAsync(e => e.TextContains_FixedFixture_HitsExpectedDocuments());

    [SqlServerFact]
    public Task TextNotContains_FixedFixture_TreatsNullAsEmptyAndHitsComplement() =>
        RunAsync(e => e.TextNotContains_FixedFixture_TreatsNullAsEmptyAndHitsComplement());

    [SqlServerFact]
    public Task DateRange_FixedFixture_BoundsAreInclusive() =>
        RunAsync(e => e.DateRange_FixedFixture_BoundsAreInclusive());

    [SqlServerTheory]
    [InlineData("1000.00", new[] { "D02", "D06" })]
    [InlineData("1000.01", new[] { "D06" })]
    public Task AmountRange_FixedFixture_AbsoluteScaledBoundary(string from, string[] expectedDocs) =>
        RunAsync(e => e.AmountRange_FixedFixture_AbsoluteScaledBoundary(from, expectedDocs));

    [SqlServerTheory]
    [InlineData("000000", new[] { "D06" })]
    [InlineData("000333", new string[0])]
    public Task TrailingDigits_FixedFixture_MajorUnitTailRequiresEnoughDigits(string pattern, string[] expectedDocs) =>
        RunAsync(e => e.TrailingDigits_FixedFixture_MajorUnitTailRequiresEnoughDigits(pattern, expectedDocs));

    [SqlServerFact]
    public Task DrCrOnly_FixedFixture_CreditSideOnly() =>
        RunAsync(e => e.DrCrOnly_FixedFixture_CreditSideOnly());

    [SqlServerFact]
    public Task ManualAuto_FixedFixture_NullFlagNeverMatches() =>
        RunAsync(e => e.ManualAuto_FixedFixture_NullFlagNeverMatches());

    [SqlServerFact]
    public Task AccountPairDebitAnchor_FixedFixture_OutputsAnchorAndCounterRows() =>
        RunAsync(e => e.AccountPairDebitAnchor_FixedFixture_OutputsAnchorAndCounterRows());

    [SqlServerFact]
    public Task AccountPairExact_FixedFixture_ZeroAmountDebitCountsAsDebitSide() =>
        RunAsync(e => e.AccountPairExact_FixedFixture_ZeroAmountDebitCountsAsDebitSide());

    [SqlServerFact]
    public Task AccountPairExact_FixedFixture_NoQualifyingDocReturnsEmpty() =>
        RunAsync(e => e.AccountPairExact_FixedFixture_NoQualifyingDocReturnsEmpty());

    [SqlServerTheory]
    [InlineData(2, new[] { "D01", "D02", "D06" })]
    [InlineData(3, new[] { "D02", "D06" })]
    [InlineData(6, new[] { "D06" })]
    public Task CustomTrailingZeros_FixedFixture_FixedDigitsBoundaries(int digits, string[] expectedDocs) =>
        RunAsync(e => e.CustomTrailingZeros_FixedFixture_FixedDigitsBoundaries(digits, expectedDocs));

    [SqlServerFact]
    public Task CustomKeywords_FixedFixture_EquivalentToDescriptionContains() =>
        RunAsync(e => e.CustomKeywords_FixedFixture_EquivalentToDescriptionContains());

    [SqlServerFact]
    public Task LeftFoldJoin_FixedFixture_OrThenAndGroups() =>
        RunAsync(e => e.LeftFoldJoin_FixedFixture_OrThenAndGroups());

    /// <summary>
    /// 漂移守衛：包裝方法必須逐一對應基底的全部測試（方法名＋InlineData 逐列）。
    /// 基底新增測試/資料列而包裝漏補時，此測試紅燈——顯式重複的維護成本由機器守住。
    /// </summary>
    [Fact]
    public async Task WrapperMethods_CoverEveryBaseEquivalenceTest()
    {
        var baseSurface = await TestSurfaceAsync(typeof(GlRuleSqlEquivalenceTests));
        var wrapperSurface = await TestSurfaceAsync(
            typeof(SqlServerGlRuleSqlEquivalenceTests), exclude: nameof(WrapperMethods_CoverEveryBaseEquivalenceTest));

        Assert.NotEmpty(baseSurface); // 空集合守門：反射掃不到基底測試時不得真空通過
        Assert.Equal(baseSurface, wrapperSurface);
    }

    /// <summary>類別的測試表面：每個 [Fact]/[Theory] 方法一列；Theory 每個 InlineData 資料列各一列（JSON 正規化）。</summary>
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

    /// <summary>每測試獨立 schema：建 temp 專案 → 灌 fixture → 跑基底斷言 → DisposeAsync drop schema。</summary>
    private static async Task RunAsync(Func<GlRuleSqlEquivalenceTests, Task> assertion)
    {
        var sql = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(sql); // [SqlServerFact] 已在探測失敗時略過；此處走到即應可連線

        await using (sql)
        {
            var engine = await SqlServerEngine.CreateAsync(sql);
            await assertion(engine);
        }
    }

    /// <summary>私有引擎子類（不被 xUnit 探索）：對 SQL Server 專案 schema 提供基底斷言所需的 Repository/ProjectId。</summary>
    private sealed class SqlServerEngine : GlRuleSqlEquivalenceTests
    {
        private SqlServerEngine(TempSqlServerProject sql)
        {
            ProjectId = sql.ProjectId;
            Repository = new SqlServerFilterRunRepository(sql.Database);
        }

        protected override IFilterRunRepository Repository { get; }
        protected override string ProjectId { get; }

        public static async Task<SqlServerEngine> CreateAsync(TempSqlServerProject sql)
        {
            // fixture 是 provider 中立的 SQL，T-SQL 需兩個轉換：
            // 1. 字串常值加 N 前綴（fixture 含中文；無 N 時 varchar 走庫層字碼頁轉換，BIN2 庫＝Latin1 會毀損）。
            //    fixture 常值不含跳脫引號，成對貪婪配對安全。
            // 2. 表名限定到專案 schema（{s} 哨兵由 CreateCommand 換成 [prj_xxx]；裸名會落到 dbo）。
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

/// <summary>
/// DuckDB provider 的等價測試子類：temp 專案 DB + 同一固定 fixture。DuckDB 是本地檔引擎、不需外部後端，
/// 一律 <c>[Fact]</c>（基底斷言全跑、不掛 SqlServerFact 閘控）。這證明整套 GL 規則述詞在 DuckDB 上與
/// SQLite oracle 逐條等價——JE Testing 邏輯在第二本地引擎上成立（spec §5）。
/// </summary>
public sealed class DuckDbGlRuleSqlEquivalenceTests : GlRuleSqlEquivalenceTests, IDisposable
{
    private readonly TempProjectRoot _root = new();

    public DuckDbGlRuleSqlEquivalenceTests()
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

        // DuckDB.NET 無 SQLite 的 ClearAllPools；連線 using 釋放後，repo 的新連線經共用原生實例看到已提交資料。
        Repository = new LocalFilterRunRepository(database);
    }

    protected override IFilterRunRepository Repository { get; }
    protected override string ProjectId { get; }

    public void Dispose() => _root.Dispose();
}
