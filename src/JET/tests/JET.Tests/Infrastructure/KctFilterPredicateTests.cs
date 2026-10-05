using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// KCT 小組進階篩選述詞（清單 A/C/D/H/J）的 SQLite 正確性測試。
/// oracle：手算固定 7 傳票 fixture（每傳票借貸兩列）；斷言鎖命中傳票身分（doc_num），非僅筆數。
/// 述詞純 ANSI（EXISTS / NOT EXISTS / 整數 ÷ %、TRIM、UPPER、ISO 日期比較），雙 provider 等價由構造保證；
/// SQL Server LocalDB 等價驗證列入 windows-handoff（本機無 LocalDB 時不在此跑）。
/// </summary>
public sealed class KctFilterPredicateTests : IDisposable
{
    // MoneyScale=100、查核期間 2025 全年、期末財報準備日 2025-12-31。
    // 科目分類：4101 Revenue、1131 Receivables、2251 Receipt in advance、1101 Cash、5101 Others。
    //   K01 2025-03-30  4101 借 500（甲/核甲，自動）   | 1101 貸   ← 季末前借記收入(Q1,X≥2)、編製＝核准
    //   K02 2025-05-15  4101 借 600（乙/核丙，人工）   | 1101 貸   ← 收入人工分錄；遠離季底
    //   K03 2025-07-01  1131 借 700（甲/核NULL）       | 4101 貸   ← 收入貸＋應收借（C 不命中）
    //   K04 2025-08-01  1101 借 800（乙/核乙）         | 4101 貸   ← 收入貸＋現金借（C 命中）、編製＝核准
    //   K05 2025-09-01  2251 借 900（丙/核NULL）       | 4101 貸   ← 收入貸＋預收借（C 不命中）
    //   K06 2025-11-11  5101 借 1,999,999             | 1101 貸   ← 尾數 999999
    //   K07 2025-12-01  5101 借 3,000,000             | 1101 貸   ← 尾數 000000
    //   K08 2025-06-15  5101 借 0.39（主單位整數 0）   | 1101 貸   ← 次單位金額，不得命中 000000（字尾語意）
    //   K09 2025-04-10  5101 借 39.00（主單位整數 39） | 1101 貸   ← 位數不足，不得命中 6 位樣態 000039
    // 預期命中（傳票集合）：
    //   revenueDebitNearQuarterEnd(X=2/3) → {K01}（K02 遠離季底；K03–K05 的 4101 在貸方）
    //   revenueWithoutNormalCounterpart   → {K04}（K03 有應收借、K05 有預收借 → 排除）
    //   manualRevenueEntry                → {K02}（唯一 is_manual=1 的 4101 列）
    //   trailingDigits 999999 → {K06}；000000 → {K07}（K08 主單位整數 0 不得命中）；000039 → {}（K09 位數不足）
    //   preparerEqualsApprover            → {K01, K04}（核准人員與建立人員同名且非空）
    private const string FixtureSql =
        """
        INSERT INTO target_gl_entry
            (batch_id, source_row_number, document_number, line_item, post_date, approval_date,
             account_code, account_name, document_description, source_module, created_by, approved_by,
             is_manual, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
        VALUES
            ('b1', 1,  'K01', '1', '2025-03-30', '2025-03-31', '4101', '銷貨收入', '季末借收入', NULL, '甲', '甲',  0, 50000,      50000,     0,         'DEBIT'),
            ('b1', 2,  'K01', '2', '2025-03-30', '2025-03-31', '1101', '現金',     '季末借收入', NULL, '甲', '甲',  0, -50000,     0,         50000,     'CREDIT'),
            ('b1', 3,  'K02', '1', '2025-05-15', '2025-05-16', '4101', '銷貨收入', '收入人工',   NULL, '乙', '丙',  1, 60000,      60000,     0,         'DEBIT'),
            ('b1', 4,  'K02', '2', '2025-05-15', '2025-05-16', '1101', '現金',     '收入人工',   NULL, '乙', '丙',  1, -60000,     0,         60000,     'CREDIT'),
            ('b1', 5,  'K03', '1', '2025-07-01', '2025-07-02', '1131', '應收帳款', '收入對應收', NULL, '甲', NULL,  0, 70000,      70000,     0,         'DEBIT'),
            ('b1', 6,  'K03', '2', '2025-07-01', '2025-07-02', '4101', '銷貨收入', '收入對應收', NULL, '甲', NULL,  0, -70000,     0,         70000,     'CREDIT'),
            ('b1', 7,  'K04', '1', '2025-08-01', '2025-08-02', '1101', '現金',     '收入對現金', NULL, '乙', '乙',  0, 80000,      80000,     0,         'DEBIT'),
            ('b1', 8,  'K04', '2', '2025-08-01', '2025-08-02', '4101', '銷貨收入', '收入對現金', NULL, '乙', '乙',  0, -80000,     0,         80000,     'CREDIT'),
            ('b1', 9,  'K05', '1', '2025-09-01', '2025-09-02', '2251', '預收貨款', '收入對預收', NULL, '丙', NULL,  0, 90000,      90000,     0,         'DEBIT'),
            ('b1', 10, 'K05', '2', '2025-09-01', '2025-09-02', '4101', '銷貨收入', '收入對預收', NULL, '丙', NULL,  0, -90000,     0,         90000,     'CREDIT'),
            ('b1', 11, 'K06', '1', '2025-11-11', '2025-11-12', '5101', '其他費用', '尾數九',     NULL, '甲', NULL,  0, 199999900,  199999900, 0,         'DEBIT'),
            ('b1', 12, 'K06', '2', '2025-11-11', '2025-11-12', '1101', '現金',     '尾數九',     NULL, '甲', NULL,  0, -199999900, 0,         199999900, 'CREDIT'),
            ('b1', 13, 'K07', '1', '2025-12-01', '2025-12-02', '5101', '其他費用', '尾數零',     NULL, '甲', NULL,  0, 300000000,  300000000, 0,         'DEBIT'),
            ('b1', 14, 'K07', '2', '2025-12-01', '2025-12-02', '1101', '現金',     '尾數零',     NULL, '甲', NULL,  0, -300000000, 0,         300000000, 'CREDIT'),
            -- K08：0.39（scale 100 → amount_scaled 39），主單位整數 0；買錯的取模會讓 0 % 10^6 = 0 誤命中 000000。
            ('b1', 15, 'K08', '1', '2025-06-15', '2025-06-16', '5101', '其他費用', '次單位金額', NULL, '甲', NULL,  0, 39,         39,        0,         'DEBIT'),
            ('b1', 16, 'K08', '2', '2025-06-15', '2025-06-16', '1101', '現金',     '次單位金額', NULL, '甲', NULL,  0, -39,        0,         39,        'CREDIT'),
            -- K09：39.00（amount_scaled 3900），主單位整數 39（2 位）；字尾語意下不得命中 6 位樣態 000039。
            ('b1', 17, 'K09', '1', '2025-04-10', '2025-04-11', '5101', '其他費用', '位數不足',   NULL, '甲', NULL,  0, 3900,       3900,      0,         'DEBIT'),
            ('b1', 18, 'K09', '2', '2025-04-10', '2025-04-11', '1101', '現金',     '位數不足',   NULL, '甲', NULL,  0, -3900,      0,         3900,      'CREDIT');

        UPDATE target_gl_entry SET is_effective = 1;

        -- 科目配對一律以 category_id 連到分類表（2026-10-02 起刪除依分類名稱對應的退路），測試資料補上 category_id。
        INSERT INTO target_account_mapping
            (batch_id, source_row_number, account_code, account_name, standardized_category, category_id)
        VALUES
            ('am1', 1, '4101', '銷貨收入', 'Revenue', 'builtin.revenue'),
            ('am1', 2, '1131', '應收帳款', 'Receivables', 'builtin.receivables'),
            ('am1', 3, '2251', '預收貨款', 'Receipt in advance', 'builtin.receipt_in_advance'),
            ('am1', 4, '1101', '現金',     'Cash', 'builtin.cash'),
            ('am1', 5, '5101', '其他費用', 'Others', 'builtin.others');
        """;

    private const int MoneyScale = 100;
    private static FilterRuleContext Context => new(MoneyScale, "2025-12-31", "2025-01-01", "2025-12-31");

    private readonly TempProjectRoot _root = new();
    private readonly IFilterRunRepository _repository;
    private readonly string _projectId;

    public KctFilterPredicateTests()
    {
        var folder = new JetProjectFolder(_root.Path);
        var database = new SqliteProjectDatabase(folder);
        _projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(_projectId));
        database.EnsureCreatedAsync(_projectId, CancellationToken.None).GetAwaiter().GetResult();

        using (var connection = database.CreateConnection(_projectId))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = FixtureSql;
            command.ExecuteNonQuery();
        }

        _repository = new LocalFilterRunRepository(database);
    }

    private static FilterScenarioSpec SingleRule(FilterRuleSpec rule) =>
        new("KCT 述詞", "固定 fixture 的命中身分斷言", [new FilterGroupSpec(FilterJoin.And, [rule])]);

    private static FilterRuleSpec Rule(
        FilterRuleType type, IReadOnlyList<string>? keywords = null, int? windowDays = null) =>
        new(FilterJoin.And, type, null, null, keywords ?? [], TextMatchMode.Contains,
            null, null, null, null, null, null, WindowDays: windowDays);

    private async Task<IReadOnlyList<string>> HitDocumentsAsync(FilterRuleSpec rule)
    {
        var result = await _repository.PreviewAsync(_projectId, SingleRule(rule), Context, CancellationToken.None);
        return result.PreviewRows
            .Select(r => r.DocumentNumber!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();
    }

    [Theory]
    [InlineData(1, new string[0])]    // BVA：X=1（僅季底當日 03-31）→ K01(03-30) 不命中
    [InlineData(2, new[] { "K01" })]  // 邊界：X=2（[03-30,03-31]）→ 含 K01
    [InlineData(3, new[] { "K01" })]  // K02(05-15) 遠離任何季底 → 不命中；只剩 K01
    public async Task RevenueDebitNearQuarterEnd_WindowBoundary_HitsExpectedDocuments(
        int windowDays, string[] expected)
    {
        Assert.Equal(expected,
            await HitDocumentsAsync(Rule(FilterRuleType.RevenueDebitNearQuarterEnd, windowDays: windowDays)));
    }

    [Fact]
    public async Task RevenueWithoutNormalCounterpart_ExcludesVouchersWithReceivableOrAdvanceDebit()
    {
        // K03（應收借）、K05（預收借）有一般對方科目 → 排除；K04（現金借）無 → 命中。
        Assert.Equal(["K04"],
            await HitDocumentsAsync(Rule(FilterRuleType.RevenueWithoutNormalCounterpart)));
    }

    [Fact]
    public async Task ManualRevenueEntry_HitsOnlyManualRevenueRows()
    {
        // 唯一 is_manual=1 的 Revenue 列在 K02；其餘 4101 列 is_manual=0。
        Assert.Equal(["K02"],
            await HitDocumentsAsync(Rule(FilterRuleType.ManualRevenueEntry)));
    }

    // 尾數比對＝整數化金額(捨小數)末 k 位與樣態相等,等價 legacy @Right(@Str(@int(x),1,0),k)=pattern。
    // 等價分割（整數位數 vs 樣態長度 k）：位數 ≥ k 才可能以 k 位樣態結尾；位數 < k 時字串短於樣態、長度不符不相等。
    [Theory]
    [InlineData("999999", new[] { "K06" })]                // 1,999,999 整數末 6 位 = 999999
    [InlineData("000000", new[] { "K07" })]                // 3,000,000 命中；K08(整數 0) 位數 < 6、不相等
    [InlineData("999999,000000", new[] { "K06", "K07" })]  // 多樣態任一相等即命中
    [InlineData("000039", new string[0])]                  // K09(整數 39) 僅 2 位 < 6、不相等
    public async Task TrailingDigits_MajorUnitTail_HitsExpectedDocuments(string patterns, string[] expected)
    {
        Assert.Equal(expected,
            await HitDocumentsAsync(Rule(FilterRuleType.TrailingDigits, keywords: patterns.Split(','))));
    }

    // k=1 邊界（字尾長度 1、無長度下界，等價 @Right(@Str(@int(x),1,0),1)）：整數末位與樣態相等。
    // K08(0.39，整數 0) 對樣態 "0" 命中（@Right("0",1)="0"）——純機械比對，不因金額小而排除；由審計員判斷是否採用。
    [Theory]
    [InlineData("0", new[] { "K01", "K02", "K03", "K04", "K05", "K07", "K08" })]  // 整數末位 0（含整數 0）
    [InlineData("9", new[] { "K06", "K09" })]                                     // 整數末位 9
    public async Task TrailingDigits_SingleDigitTail_HitsByLastDigit(string pattern, string[] expected)
    {
        Assert.Equal(expected,
            await HitDocumentsAsync(Rule(FilterRuleType.TrailingDigits, keywords: [pattern])));
    }

    [Fact]
    public async Task PreparerEqualsApprover_HitsRowsWithSameNonBlankCreatorAndApprover()
    {
        // K01（甲=甲）、K04（乙=乙）命中；K02（乙≠丙）、K03/K05/K06/K07（核准 NULL）不命中。
        Assert.Equal(["K01", "K04"],
            await HitDocumentsAsync(Rule(FilterRuleType.PreparerEqualsApprover)));
    }

    public void Dispose() => _root.Dispose();

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task AutonomousAudit_KctBoundariesExecuteOnBothLocalDatabases(string provider)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, FixtureSql);
        (FilterRuleSpec Rule, string[] Documents)[] cases =
        [
            (Rule(FilterRuleType.RevenueDebitNearQuarterEnd, windowDays: 1), []),
            (Rule(FilterRuleType.RevenueDebitNearQuarterEnd, windowDays: 2), ["K01"]),
            (Rule(FilterRuleType.RevenueDebitNearQuarterEnd, windowDays: 3), ["K01"]),
            (Rule(FilterRuleType.RevenueWithoutNormalCounterpart), ["K04"]),
            (Rule(FilterRuleType.ManualRevenueEntry), ["K02"]),
            (Rule(FilterRuleType.TrailingDigits, keywords: ["999999"]), ["K06"]),
            (Rule(FilterRuleType.TrailingDigits, keywords: ["000000"]), ["K07"]),
            (Rule(FilterRuleType.TrailingDigits, keywords: ["999999","000000"]), ["K06","K07"]),
            (Rule(FilterRuleType.TrailingDigits, keywords: ["000039"]), []),
            (Rule(FilterRuleType.TrailingDigits, keywords: ["0"]), ["K01","K02","K03","K04","K05","K07","K08"]),
            (Rule(FilterRuleType.TrailingDigits, keywords: ["9"]), ["K06","K09"]),
            (Rule(FilterRuleType.PreparerEqualsApprover), ["K01","K04"])
        ];
        foreach (var c in cases)
        {
            var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, SingleRule(c.Rule), Context, CancellationToken.None);
            Assert.Equal(c.Documents, result.PreviewRows.Select(r=>r.DocumentNumber).Distinct().Order());
            Assert.Equal(c.Documents.Length, result.VoucherCount);
        }
    }
}
