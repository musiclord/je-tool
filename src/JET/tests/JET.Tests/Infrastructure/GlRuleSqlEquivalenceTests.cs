using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// GL 規則述詞的等價測試基底。
/// oracle：手算 ≤20 列固定 fixture（fixture 註解列出每條述詞的預期命中傳票）；
/// 斷言鎖「值＋身分」（命中哪幾張傳票），不是只鎖筆數。
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
    ///   D06 2025-05-14(三) 核准 05-15       摘要「整數金額」      |2,000,000.00|（6 個尾數 0，條件環境帶入 6 個 0）
    ///   D07 2026-01-05(一·期外) 核准 2025-12-22(一) 摘要「期外分錄」 |3.33|
    ///   D08 日期 NULL                        摘要「無日期」        |1.11|
    ///   D09 2025-07-09(三) 核准 07-10        摘要「零元測試」      借方 0 元＋貸方 |0.50|
    ///   D10 2025-09-10(三) 核准 09-11        摘要「領料出庫」      借 5101 銷貨成本＋貸 4101 銷貨收入 |333.33|
    /// 科目配對（fixture 對照）：1101→Cash、2201→Receivables、4101→自訂分類（semantic role=revenue）、
    /// 5101→Others。4101 的 transitional standardized_category 刻意寫 Others；若述詞仍比較顯示文字，
    /// 所有收入規則都會失敗，因此這套 golden 會鎖住 role-based 行為。
    /// 預篩選尾數規則的 0 的個數正式流程由 GL 算出，這組固定答案的條件環境帶入 6：先取主單位整數，再以 10⁶ 取模；
    /// 只有 D06（|2,000,000.00| 的主單位整數為 2,000,000）命中。
    /// 預期命中（傳票集合）：
    ///   prepPeriodApproval → {D02}；suspiciousKeywords → {D02}（調整）；
    ///   trailingZeros → {D06}（帶入 6 個 0；D02 的 1,000 僅 3 個 0 不命中）；weekendPosting → {D03, D05}；
    ///   weekendApproval → {D04, D05}（週末規則皆納入補班日）；holidayPosting → {D04}；
    ///   holidayApproval → {D03}；blankDescription → {D03}（NULL 列）；
    ///   unexpectedAccountPair（否定面）→ {D10}（貸 Revenue 且同傳票無「金額 > 0 的
    ///     Cash/Receivables/ReceiptInAdvance 借方」，但至少有一筆金額 > 0 的分錄；D01/D02/D04–D06 有正值 Cash 借方對方 → 不命中；
    ///     D09 的 Cash 借方是 0 元，不算對方，但整張傳票沒有金額 > 0 的分錄 → 不命中；D03 無 Revenue 貸方 → 不命中；只標貸方列）。
    /// </summary>
    protected const string FixtureSql =
        """
        INSERT INTO gl
            (batch_id, source_row_number, voucher_number, line_item, gl_date, approval_date,
             account_number, account_name, description, source_module, created_by, approved_by,
             is_manual, is_excluded, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
        VALUES
            ('b1', 1,  'D01', '1', '2025-03-05', '2025-03-06', '1101', '現金',     '進貨',       NULL, '王一', NULL, 0,    0, 10000,  10000, 0,      'DEBIT'),
            ('b1', 2,  'D01', '2', '2025-03-05', '2025-03-06', '4101', '銷貨收入', '進貨',       NULL, '王一', NULL, 0,    0, -10000, 0,     10000,  'CREDIT'),
            ('b1', 3,  'D02', '1', '2025-04-03', '2026-01-12', '1101', '現金',     '調整分錄',   NULL, '李二', NULL, 1,    0, 100000, 100000, 0,     'DEBIT'),
            ('b1', 4,  'D02', '2', '2025-04-03', '2026-01-12', '4101', '銷貨收入', '調整分錄',   NULL, '李二', NULL, 1,    0, -100000, 0,    100000, 'CREDIT'),
            ('b1', 5,  'D03', '1', '2025-06-07', '2025-10-10', '2201', '應付帳款', NULL,         NULL, '王一', NULL, NULL, 0, 12345,  12345, 0,      'DEBIT'),
            ('b1', 6,  'D03', '2', '2025-06-07', '2025-10-10', '1101', '現金',     '沖回',       NULL, '王一', NULL, NULL, 0, -12345, 0,     12345,  'CREDIT'),
            ('b1', 7,  'D04', '1', '2025-10-10', '2025-10-11', '1101', '現金',     '例假日入帳', NULL, '李二', NULL, 0,    0, 777,    777,   0,      'DEBIT'),
            ('b1', 8,  'D04', '2', '2025-10-10', '2025-10-11', '4101', '銷貨收入', '例假日入帳', NULL, '李二', NULL, 0,    0, -777,   0,     777,    'CREDIT'),
            ('b1', 9,  'D05', '1', '2025-02-08', '2025-02-08', '1101', '現金',     '補班日傳票', NULL, '王一', NULL, 1,    0, 500,    500,   0,      'DEBIT'),
            ('b1', 10, 'D05', '2', '2025-02-08', '2025-02-08', '4101', '銷貨收入', '補班日傳票', NULL, '王一', NULL, 1,    0, -500,   0,     500,    'CREDIT'),
            ('b1', 11, 'D06', '1', '2025-05-14', '2025-05-15', '1101', '現金',     '整數金額',   NULL, '李二', NULL, 0,    0, 200000000, 200000000, 0,     'DEBIT'),
            ('b1', 12, 'D06', '2', '2025-05-14', '2025-05-15', '4101', '銷貨收入', '整數金額',   NULL, '李二', NULL, 0,    0, -200000000, 0,    200000000, 'CREDIT'),
            ('b1', 13, 'D07', '1', '2026-01-05', '2025-12-22', '1101', '現金',     '期外分錄',   NULL, '王一', NULL, 0,    1, 333,    333,   0,      'DEBIT'),
            ('b1', 14, 'D07', '2', '2026-01-05', '2025-12-22', '4101', '銷貨收入', '期外分錄',   NULL, '王一', NULL, 0,    1, -333,   0,     333,    'CREDIT'),
            ('b1', 15, 'D08', '1', NULL,         NULL,         '1101', '現金',     '無日期',     NULL, '王一', NULL, 0,    1, 111,    111,   0,      'DEBIT'),
            ('b1', 16, 'D08', '2', NULL,         NULL,         '4101', '銷貨收入', '無日期',     NULL, '王一', NULL, 0,    1, -111,   0,     111,    'CREDIT'),
            ('b1', 17, 'D09', '1', '2025-07-09', '2025-07-10', '1101', '現金',     '零元測試',   NULL, '王一', NULL, 0,    0, 0,      0,     0,      'DEBIT'),
            ('b1', 18, 'D09', '2', '2025-07-09', '2025-07-10', '4101', '銷貨收入', '零元測試',   NULL, '王一', NULL, 0,    0, -50,    0,     50,     'CREDIT'),
            ('b1', 19, 'D10', '1', '2025-09-10', '2025-09-11', '5101', '銷貨成本', '領料出庫',   NULL, '王一', NULL, 0,    0, 33333,  33333, 0,      'DEBIT'),
            ('b1', 20, 'D10', '2', '2025-09-10', '2025-09-11', '4101', '銷貨收入', '領料出庫',   NULL, '王一', NULL, 0,    0, -33333, 0,     33333,  'CREDIT');

        INSERT INTO calendar_day (day_type, date) VALUES
            ('holiday', '2025-10-10'),
            ('makeup',  '2025-02-08');

        INSERT INTO account_category
            (category_id, label, ordinal, semantic_role, is_builtin, revision)
        VALUES
            ('custom.0123456789abcdef0123456789abcdef', 'Custom revenue', 5, 'revenue', 0, 1);

        -- 科目配對一律以 category_id 連到分類表，每一列都帶內建分類。
        INSERT INTO account_mapping
            (batch_id, source_row_number, account_number, account_name, standardized_category, category_id)
        VALUES
            ('am1', 1, '1101', '現金',     'Cash',        'builtin.cash'),
            ('am1', 2, '2201', '應付帳款', 'Receivables', 'builtin.receivables'),
            ('am1', 3, '4101', '銷貨收入', 'Others',      'custom.0123456789abcdef0123456789abcdef'),
            ('am1', 4, '5101', '銷貨成本', 'Others',      'builtin.others');
        """;

    protected const int MoneyScale = 100;

    protected abstract IFilterHitRepository Repository { get; }
    protected abstract string ProjectId { get; }

    // 所有規則共用同一個「只看納入測試的分錄」述詞；D07、D08 保留在 fixture，專門防止未納入測試的列混進來。
    private static FilterConditionContext Context => new(
        MoneyScale,
        "2025-12-31",
        "2025-01-01",
        "2025-12-31",
        PopulationScope: GlPopulationScope.AuditPeriod)
    {
        // 預篩選尾數規則的 0 的個數由 GL 算出，再由條件環境帶入；這組固定答案照 6 個 0 設計，所以帶 6。
        ZerosThreshold = 6
    };

    private static FilterScenarioSpec SingleRule(FilterConditionSpec rule) =>
        new("等價測試", "固定 fixture 的述詞等價斷言", [new FilterGroupSpec(FilterJoin.And, [rule])]);

    // 用畫面送出的條件寫法建立規則，走正式的解析。
    private static FilterConditionSpec Parsed(string rule) => FilterScenarioPayloadParser.Parse(
        JsonDocument.Parse($$"""{"groups":[{"conditions":[{{rule}}]}]}""").RootElement, MoneyScale).Groups[0].Conditions[0];

    private static FilterConditionSpec Prescreen(string key) =>
        new(FilterJoin.And, FilterConditionType.Prescreen, key, null, [], TextMatchMode.Contains,
            null, null, null, null, null, null);

    private async Task<IReadOnlyList<string>> HitDocumentsAsync(FilterConditionSpec rule)
    {
        var result = await Repository.PreviewAsync(ProjectId, SingleRule(rule), Context, CancellationToken.None);
        return result.PreviewRows
            .Select(r => r.VoucherNumber!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();
    }

    [Theory]
    [InlineData(PrescreenRuleKeys.PrepPeriodApproval, new[] { "D02" })]
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
        // 否定面與零元邊界：貸 Revenue 且同傳票
        // 無任何「金額 > 0 的 Cash/Receivables/ReceiptInAdvance 借方」才命中，只標貸方列。
        // 照舊版 IDEA R3：傳票裡還要至少有一筆金額 > 0 的分錄。
        // D01/D02/D04–D06 有正值 Cash（1101）借方對方 → 不命中；D03 無 Revenue 貸方 → 不命中；
        // D09 的 Cash 借方是 0 元，不算對方，但整張傳票沒有正金額分錄 → 不命中；
        // D10（借 5101 Others）無正常對方，借方 333.33 是正金額 → 命中。
        Assert.Equal(
            ["D10"],
            await HitDocumentsAsync(Prescreen(PrescreenRuleKeys.UnexpectedAccountPair)));
    }

    [Fact]
    public async Task NonAuthorizedPreparer_EmptyAuthorizedList_HitsNothing()
    {
        // 非授權編製人員述詞以 EXISTS 自保（繞過 Application validator 直接跑述詞）：fixture 無 authorized_preparer
        // 任何列。少了 EXISTS 前綴時 `created_by NOT IN (空集合)` 會反轉成全命中(8 張有編製者的傳票);
        // 有前綴 → 整體述詞 FALSE → 0 命中(與 prescreen.run 的 na 語意對齊)。鎖「無命中」而非筆數巧合。
        Assert.Empty(await HitDocumentsAsync(Prescreen(PrescreenRuleKeys.NonAuthorizedPreparer)));
    }

    [Fact]
    public async Task TextContains_FixedFixture_HitsExpectedDocuments()
    {
        var rule = new FilterConditionSpec(FilterJoin.And, FilterConditionType.Text, null, "description",
            ["進貨"], TextMatchMode.Contains, null, null, null, null, null, null);

        Assert.Equal(["D01"], await HitDocumentsAsync(rule));
    }

    [Fact]
    public async Task TextNotContains_FixedFixture_TreatsNullAsEmptyAndHitsComplement()
    {
        // NULL 摘要視為空字串 → notContains「進貨」成立；只排除 D01 兩列。
        var rule = new FilterConditionSpec(FilterJoin.And, FilterConditionType.Text, null, "description",
            ["進貨"], TextMatchMode.NotContains, null, null, null, null, null, null);

        Assert.Equal(["D02", "D03", "D04", "D05", "D06", "D09", "D10"], await HitDocumentsAsync(rule));
    }

    [Fact]
    public async Task DateRange_FixedFixture_BoundsAreInclusive()
    {
        // BVA：邊界含入——from=2025-03-05 恰為 D01 的過帳日；NULL 過帳日（D08）不命中。
        var rule = new FilterConditionSpec(FilterJoin.And, FilterConditionType.DateRange, null, "glDate",
            [], TextMatchMode.Contains, "2025-03-05", "2025-04-03", null, null, null, null);

        Assert.Equal(["D01", "D02"], await HitDocumentsAsync(rule));
    }

    [Theory]
    [InlineData("1000.00", new[] { "D02", "D06" })] // 邊界值：|1,000.00| 恰等於下限 → 含入
    [InlineData("1000.01", new[] { "D06" })]        // 上鄰：剛超過 D02 金額 → 只剩 D06
    public async Task AmountRange_FixedFixture_AbsoluteScaledBoundary(string from, string[] expectedDocs)
    {
        var fromScaled = (long)(decimal.Parse(from) * MoneyScale);
        var rule = new FilterConditionSpec(FilterJoin.And, FilterConditionType.NumRange, null, "amount",
            [], TextMatchMode.Contains, null, null, fromScaled, null, null, null);

        Assert.Equal(expectedDocs, await HitDocumentsAsync(rule));
    }

    // 特定金額尾數比對＝整數化金額末 k 位與樣態相等（＝legacy @Right(@Str(@int(x),1,0),k)）；k≥2 須整數 ≥ 10^(k-1)。
    // 000000（k=6，下界 10^5）：D06=2,000,000 整數末 6 位 = 000000 → 命中；D09 的次單位金額（0.50／0，整數 0）
    //   位數 < 6、字串短於樣態、長度不符不相等 → 不命中。
    // 000333：D10 整數 333 僅 3 位 < 6 → 不相等 → 空集合。
    [Theory]
    [InlineData("000000", new[] { "D06" })]
    [InlineData("000333", new string[0])]
    public async Task TrailingDigits_FixedFixture_MajorUnitTailRequiresEnoughDigits(string pattern, string[] expectedDocs)
    {
        // 條件用「金額」的「整數尾數符合」。
        var rule = Parsed($$"""{"type":"fieldValue","field":"amount","operator":"endsWithDigits","values":["{{pattern}}"],"amountBasis":"absolute"}""");

        Assert.Equal(expectedDocs, await HitDocumentsAsync(rule));
    }

    [Fact]
    public async Task DrCrOnly_FixedFixture_CreditSideOnly()
    {
        var rule = new FilterConditionSpec(FilterJoin.And, FilterConditionType.DrCrOnly, null, null,
            [], TextMatchMode.Contains, null, null, null, null, "credit", null);

        var result = await Repository.PreviewAsync(ProjectId, SingleRule(rule), Context, CancellationToken.None);

        // 查核期間內每張傳票恰一列貸方：8 列、8 張傳票（D09 的 0 元列屬借方側，不在貸方）。
        Assert.Equal(8, result.Count);
        Assert.All(result.PreviewRows, r => Assert.Equal("CREDIT", r.DrCr));
    }

    [Fact]
    public async Task ManualAuto_FixedFixture_NullFlagNeverMatches()
    {
        var rule = new FilterConditionSpec(FilterJoin.And, FilterConditionType.ManualAuto, null, null,
            [], TextMatchMode.Contains, null, null, null, null, null, true);

        // is_manual=1 → D02、D05；NULL（D03）不匹配 true 也不匹配 false。
        Assert.Equal(["D02", "D05"], await HitDocumentsAsync(rule));
    }

    [Fact]
    public async Task AccountPairDebitAnchor_FixedFixture_OutputsAnchorAndCounterRows()
    {
        // 借方錨定 Receivables（=2201）：只有 D03 含 Receivables 借方 → 輸出錨定列＋同傳票貸方列。
        var rule = new FilterConditionSpec(FilterJoin.And, FilterConditionType.AccountPair, null, null,
            [], TextMatchMode.Contains, null, null, null, null, null, null,
            PairMode: AccountPairModes.DebitAnchor)
        {
            // 用分類身分陣列表達這一組分類。
            DebitCategoryIds = [AccountCategoryBuiltIns.ReceivablesId]
        };

        var result = await Repository.PreviewAsync(ProjectId, SingleRule(rule), Context, CancellationToken.None);

        Assert.Equal(["D03"], result.PreviewRows.Select(r => r.VoucherNumber).Distinct().ToList());
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task AccountPairExact_FixedFixture_ZeroAmountDebitCountsAsDebitSide()
    {
        // 精確配對 Cash 借＋Revenue 貸：除 D03 外全部命中；
        // D09 的 0 元 Cash 借方屬借方側（借方側的判定是 `>= 0`），改 `>` 即漏 D09。
        var rule = new FilterConditionSpec(FilterJoin.And, FilterConditionType.AccountPair, null, null,
            [], TextMatchMode.Contains, null, null, null, null, null, null,
            PairMode: AccountPairModes.Exact)
        {
            // 用分類身分陣列表達這一組分類。
            DebitCategoryIds = [AccountCategoryBuiltIns.CashId],
            CreditCategoryIds = [AccountCategoryBuiltIns.RevenueId]
        };

        Assert.Equal(
            ["D01", "D02", "D04", "D05", "D06", "D09"],
            await HitDocumentsAsync(rule));
    }

    [Fact]
    public async Task AccountPairExact_FixedFixture_NoQualifyingDocReturnsEmpty()
    {
        // Receivables 借＋Revenue 貸：D03 有 Receivables 借方但無 Revenue 貸方 → 空集合。
        var rule = new FilterConditionSpec(FilterJoin.And, FilterConditionType.AccountPair, null, null,
            [], TextMatchMode.Contains, null, null, null, null, null, null,
            PairMode: AccountPairModes.Exact)
        {
            // 用分類身分陣列表達這一組分類。
            DebitCategoryIds = [AccountCategoryBuiltIns.ReceivablesId],
            CreditCategoryIds = [AccountCategoryBuiltIns.RevenueId]
        };

        var result = await Repository.PreviewAsync(ProjectId, SingleRule(rule), Context, CancellationToken.None);

        Assert.Equal(0, result.Count);
    }

    [Theory]
    [InlineData(2, new[] { "D01", "D02", "D06" })] // 10^2 主單位整數倍：100.00／1,000.00／2,000,000.00
    [InlineData(3, new[] { "D02", "D06" })]        // 10^3 主單位整數倍：1,000.00／2,000,000.00
    [InlineData(6, new[] { "D06" })]               // 固定預設 6：只剩 D06（|2,000,000.00| 為 10^6 整數倍）
    public async Task CustomTrailingZeros_FixedFixture_FixedDigitsBoundaries(int digits, string[] expectedDocs)
    {
        // 條件寫成同一個子條件組裡的「整數尾數符合」N 個 0
        // 與「大於或等於 1」。
        var tail = new string('0', digits);
        var rule = Parsed($$"""{"type":"group","conditions":[{"type":"fieldValue","field":"amount","operator":"endsWithDigits","values":["{{tail}}"],"amountBasis":"absolute"},{"type":"fieldValue","field":"amount","operator":"greaterThanOrEqual","value":"1","amountBasis":"absolute"}]}""");

        Assert.Equal(expectedDocs, await HitDocumentsAsync(rule));
    }

    [Fact]
    public async Task LeftFoldJoin_FixedFixture_OrThenAndGroups()
    {
        // ((suspiciousKeywords OR trailingZeros)) AND (|金額| >= 1,500.00)
        // suspicious={D02}、trailingZeros={D06}（帶入 6 個 0）→ OR={D02,D06}；
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
                new FilterConditionSpec(FilterJoin.And, FilterConditionType.NumRange, null, "amount",
                    [], TextMatchMode.Contains, null, null, 150_000L, null, null, null)
            ])
        ]);

        var result = await Repository.PreviewAsync(ProjectId, scenario, Context, CancellationToken.None);

        Assert.Equal(["D06"], result.PreviewRows.Select(r => r.VoucherNumber).Distinct().ToList());
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

        Repository = new LocalFilterHitRepository(database);
    }

    protected override IFilterHitRepository Repository { get; }
    protected override string ProjectId { get; }

    public void Dispose() => _root.Dispose();
}
