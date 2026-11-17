using System.Data.Common;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 判斷「同一張傳票裡有沒有某種分錄」的查詢，產品程式先依傳票號碼取一次集合，再接回每一筆分錄。
/// 測試裡另寫一份讓每一筆分錄各自回頭掃同一張傳票的查詢當對照：
/// 兩種寫法在同一份合成資料上選到的分錄、順序、筆數與情境標記都要相同，而且都要等於手算的答案。
/// 合成資料有一張三十多筆分錄的大傳票，裡面同時有符合與不符合的分錄，也有排除在母體外的分錄；
/// 另有借貸不平的傳票、傳票號碼是 NULL 與空字串的分錄、整張排除在母體外的傳票，以及沒有科目配對的分錄。
/// 金額以元寫，寫進資料庫時乘以 100。
/// </summary>
public sealed class SameVoucherQueryEquivalenceTests : IDisposable
{
    private const string Revenue = "4100";
    private const string Cash = "1100";
    private const string Receivables = "1200";
    private const string ReceiptInAdvance = "2300";
    private const string Others = "5100";
    private const string Unmapped = "9999";

    private static readonly FilterConditionContext Context = new(100, "2025-12-31", "2025-01-01", "2025-12-31");
    private static readonly GlPopulationContext Population = new(GlPopulationScope.AuditPeriod, "2025-01-01", "2025-12-31");

    /// <summary>大傳票 BIG 後段的三十筆收入貸方，entry_id 25 到 54。</summary>
    private static readonly long[] BigTail = Enumerable.Range(25, 30).Select(static id => (long)id).ToArray();

    private sealed record Entry(
        long Id,
        string? Voucher,
        string Line,
        string Account,
        long Amount,
        bool Excluded = false,
        string Date = "2025-06-30",
        string CreatedBy = "P9");

    private static readonly IReadOnlyList<Entry> Entries =
    [
        // 大傳票：收入貸方、現金貸方、沒有科目配對的借方、其他科目借方，以及一筆排除在母體外的應收借方。
        new(1, "BIG", "01", Revenue, -600, CreatedBy: "P3"),
        new(2, "BIG", "02", Cash, -400, Date: "2025-06-29", CreatedBy: "P2"),
        new(3, "BIG", "03", Revenue, -300, CreatedBy: "P3"),
        new(4, "BIG", "04", Unmapped, 100, CreatedBy: "P5"),
        new(5, "BIG", "05", Others, 1200, Date: "2025-07-01", CreatedBy: "P4"),
        new(6, "BIG", "06", Receivables, 500, Excluded: true, Date: "2025-06-01", CreatedBy: "P0"),
        // 借貸不平：收入貸方 1,000，0 元的應收借方，其他科目借方 700。
        new(7, "UNB", "01", Revenue, -1000),
        new(8, "UNB", "02", Receivables, 0),
        new(9, "UNB", "03", Others, 700),
        new(10, "CS", "01", Receivables, 800),
        new(11, "CS", "02", Revenue, -800),
        new(12, "CASH", "01", Cash, 900),
        new(13, "CASH", "02", Revenue, -900),
        new(14, "RIA", "01", ReceiptInAdvance, 300),
        new(15, "RIA", "02", Revenue, -300),
        // 沒有金額大於 0 的分錄。
        new(16, "NEG", "01", Revenue, -200),
        new(17, "NEG", "02", Receivables, 0),
        new(18, null, "01", Revenue, -500),
        new(19, null, "02", Others, 500),
        // 其他科目借方與應收貸方。
        new(20, "RCV", "01", Others, 400),
        new(21, "EXC", "01", Revenue, -100, Excluded: true),
        new(22, "EXC", "02", Others, 100, Excluded: true),
        // 唯一的借方排除在母體外。
        new(23, "OUT", "01", Revenue, -250),
        new(24, "OUT", "02", Others, 250, Excluded: true),
        .. BigTail.Select(static id => new Entry(id, "BIG", (id - 18).ToString("00"), Revenue, -10, CreatedBy: "P6")),
        new(55, "RCV", "02", Receivables, -400),
        new(56, "", "01", Revenue, -70),
        new(57, "", "02", Others, 70),
    ];

    /// <summary>已儲存的三個情境，依序在位置 1、2、3。</summary>
    private static readonly IReadOnlyList<(int ScenarioId, long[] EntryIds)> SavedHits =
    [
        (1, [1, 3, 7, .. BigTail]),
        // 一筆傳票號碼是 NULL，一筆排除在母體外。
        (2, [19, 24]),
        (3, [5, 13, 57]),
    ];

    private readonly TempProjectRoot _root = new();
    private readonly SqliteProjectDatabase _database;
    private readonly string _projectId = Guid.NewGuid().ToString("N");

    public SameVoucherQueryEquivalenceTests()
    {
        var folder = new JetProjectFolder(_root.Path);
        _database = new SqliteProjectDatabase(folder);
        Directory.CreateDirectory(folder.GetProjectDirectory(_projectId));
        _database.EnsureCreatedAsync(_projectId, CancellationToken.None).GetAwaiter().GetResult();
        Seed();
    }

    public void Dispose() => _root.Dispose();

    private static long[] Ids(params IEnumerable<long>[] parts) =>
        parts.SelectMany(static part => part).Order().ToArray();

    public static TheoryData<string, long[]> PredicateCases => new()
    {
        { "unexpectedAccountPair", Ids([1, 3, 7, 56], BigTail) },
        { "revenueWithoutNormalCounterpart", Ids([1, 3, 13, 23, 56], BigTail) },
        { "accountPair exact Others Revenue", Ids([1, 3, 5, 7, 9, 56, 57], BigTail) },
        { "accountPair debitAnchor Others Revenue", Ids([1, 2, 3, 5, 7, 9, 20, 55, 56, 57], BigTail) },
        { "accountPair creditAnchor Others Cash", [2, 4, 5] },
        { "specialAccountCategoryPair drAndCr Receivables Revenue", [7, 8, 10, 11, 16, 17] },
        { "specialAccountCategoryPair drNotCr Others Receivables", Ids([1, 2, 3, 5, 7, 9, 56, 57], BigTail) },
        { "specialAccountCategoryPair notDrCr Receivables Revenue", Ids([1, 3, 4, 5, 12, 13, 14, 15, 23, 56, 57], BigTail) },
    };

    [Theory]
    [MemberData(nameof(PredicateCases))]
    public async Task VoucherWideRule_SelectsTheSameEntriesAsScanningTheVoucherForEachEntry(string rule, long[] expected)
    {
        var (productSql, productParameters) = ProductPredicate(rule);
        var (scanSql, scanParameters) = PerEntryScanPredicate(rule);

        var product = await ReadEntryIdsAsync(productSql, productParameters);
        var scan = await ReadEntryIdsAsync(scanSql, scanParameters);

        Assert.Equal(expected, scan);
        Assert.Equal(expected, product);
    }

    [Fact]
    public async Task HighRiskSummaryVoucherPage_ListsTheSameVouchersAsScanningEachVoucher()
    {
        var expected = new[]
        {
            new VoucherTagRow("", "2025-06-30", "P9", 7000),
            new VoucherTagRow("BIG", "2025-06-29", "P2", 130000),
            new VoucherTagRow("CASH", "2025-06-30", "P9", 90000),
            new VoucherTagRow("UNB", "2025-06-30", "P9", 70000),
        };
        var expectedPositions = new Dictionary<string, int[]>
        {
            [""] = [3],
            ["BIG"] = [1, 3],
            ["CASH"] = [3],
            ["UNB"] = [1],
        };

        await using var connection = await OpenAsync();
        var (page, positions) = await HighRiskSummaryVoucherPageReader.ReadAsync(
            connection, SqliteDialect.Instance, Population, new PageRequest(null, 200, IncludeTotalCount: true), [1, 2, 3],
            CancellationToken.None);
        var scan = await ReadPerEntryScanHighRiskVouchersAsync([1, 2, 3]);

        Assert.Equal(expected, scan);
        Assert.Equal(expected, page.Rows);
        Assert.Equal(4, page.TotalCount);
        Assert.Null(page.NextCursor);
        Assert.Equal(expectedPositions.Keys.Order(), positions.Keys.Order());
        foreach (var (voucher, voucherPositions) in expectedPositions)
        {
            Assert.Equal(voucherPositions, positions[voucher]);
        }

        // 位置 2 的命中一筆傳票號碼是 NULL、一筆排除在母體外，沒有任何命中傳票。
        var (onlySecond, _) = await HighRiskSummaryVoucherPageReader.ReadAsync(
            connection, SqliteDialect.Instance, Population, new PageRequest(null, 200, IncludeTotalCount: true), [2],
            CancellationToken.None);
        Assert.Empty(await ReadPerEntryScanHighRiskVouchersAsync([2]));
        Assert.Empty(onlySecond.Rows);
        Assert.Equal(0, onlySecond.TotalCount);

        // 一頁兩張時，第二頁接著第一頁。
        var firstHalf = await HighRiskSummaryVoucherPageReader.ReadAsync(
            connection, SqliteDialect.Instance, Population, new PageRequest(null, 2), [1, 2, 3], CancellationToken.None);
        var secondHalf = await HighRiskSummaryVoucherPageReader.ReadAsync(
            connection, SqliteDialect.Instance, Population, new PageRequest(firstHalf.Page.NextCursor, 2), [1, 2, 3],
            CancellationToken.None);
        Assert.Equal(expected, firstHalf.Page.Rows.Concat(secondHalf.Page.Rows));
        Assert.Null(secondHalf.Page.NextCursor);
    }

    public static TheoryData<int[], (long EntryId, int[] Positions)[]> Step41Cases => new()
    {
        {
            [1],
            [
                (56, []), (19, [2]), (57, [3]),
                (1, [1]), (2, [1]), (3, [1]), (4, [1]), (5, [1, 3]), .. BigTail.Select(static id => (id, new[] { 1 })),
                (12, []), (13, [3]),
                (7, [1]), (8, [1]), (9, [1]),
            ]
        },
        {
            [1, 3],
            [
                (56, [3]), (19, [2]), (57, [3]),
                (1, [1, 3]), (2, [1, 3]), (3, [1, 3]), (4, [1, 3]), (5, [1, 3]),
                .. BigTail.Select(static id => (id, new[] { 1, 3 })),
                (12, [3]), (13, [3]),
                (7, [1]), (8, [1]), (9, [1]),
            ]
        },
    };

    [Theory]
    [MemberData(nameof(Step41Cases))]
    public async Task WorkingPaperStep41_TagsTheSameEntriesAsExpandingEachHitAcrossItsVoucher(
        int[] wholeVoucherPositions,
        (long EntryId, int[] Positions)[] expected)
    {
        var factory = (IWorkingPaperStep41PreparedSessionFactory)new LocalHighRiskSummaryRowPageRepository(_database);
        var rows = new List<WorkingPaperStep41SourceRow>();
        await using (var session = await factory.PrepareAsync(
                         _projectId, Population, [1, 2, 3], LegacyFieldKind.Text, CancellationToken.None, wholeVoucherPositions))
        {
            await foreach (var row in session.ReadRowsAsync(CancellationToken.None))
            {
                rows.Add(row);
            }
        }

        var scanTags = await ReadPerEntryScanStep41TagsAsync([1, 2, 3], wholeVoucherPositions);

        Assert.Equal(expected.Select(static row => row.EntryId), rows.Select(static row => row.EntryId));
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.True(
                expected[index].Positions.SequenceEqual(rows[index].MatchedPositions),
                $"entry_id {expected[index].EntryId} 的情境應是 [{string.Join(", ", expected[index].Positions)}]，"
                + $"實際是 [{string.Join(", ", rows[index].MatchedPositions)}]。");
        }

        var expectedTags = expected.Where(static row => row.Positions.Length > 0)
            .OrderBy(static row => row.EntryId)
            .Select(static row => $"{row.EntryId}:{string.Join(",", row.Positions)}")
            .ToArray();
        Assert.Equal(expectedTags, scanTags);
    }

    public static TheoryData<string, long[], long[]> SavedDetailCases => new()
    {
        { "BIG", Ids([1, 2, 3, 4, 5], BigTail), Ids([1, 3], BigTail) },
        { "UNB", [7, 8, 9], [7] },
        { "OUT", [], [] },
        { "CS", [], [] },
    };

    [Theory]
    [MemberData(nameof(SavedDetailCases))]
    public async Task SavedScenarioVoucherRows_AreTheSameAsScanningTheVoucherForEachEntry(
        string voucher,
        long[] expectedRows,
        long[] expectedHits)
    {
        var scenario = new FilterScenarioSpec("對照", "對照",
            [new FilterGroupSpec(FilterJoin.And, [Rule(FilterConditionType.DrCrOnly)])]);
        await using var connection = await OpenAsync();
        var page = await FilterVoucherPageReader.ReadSavedAsync(connection, SqliteDialect.Instance, "", 1, scenario, Context,
            voucher, new PageRequest(null, 200, IncludeTotalCount: true), countWholeVoucherRows: false, CancellationToken.None);
        var scan = await ReadPerEntryScanVoucherRowsAsync(
            "CASE WHEN h.entry_id IS NULL THEN 0 ELSE 1 END",
            "LEFT JOIN filter_hit h ON h.scenario_id = 1 AND h.entry_id = g.entry_id",
            "AND g.voucher_number IN (SELECT hg.voucher_number FROM filter_hit hh JOIN gl hg ON hg.entry_id = hh.entry_id "
            + "WHERE hh.scenario_id = 1 AND hg.voucher_number IS NOT NULL AND hg.is_excluded = 0)",
            voucher,
            []);

        AssertVoucherRows(expectedRows, expectedHits, scan);
        AssertVoucherRows(expectedRows, expectedHits, page.Details.Select(static row => (row.EntryId, row.IsHit)).ToList());
        Assert.Equal(expectedRows.Length, page.TotalCount);
    }

    public static TheoryData<string, long[], long[]> DraftDetailCases => new()
    {
        { "BIG", Ids([1, 2, 3, 4, 5], BigTail), Ids([1, 3], BigTail) },
        { "CASH", [12, 13], [13] },
        { "", [56, 57], [56] },
        { "CS", [], [] },
    };

    [Theory]
    [MemberData(nameof(DraftDetailCases))]
    public async Task DraftScenarioVoucherRows_AreTheSameAsScanningTheVoucherForEachEntry(
        string voucher,
        long[] expectedRows,
        long[] expectedHits)
    {
        var scenario = new FilterScenarioSpec("對照", "對照",
            [new FilterGroupSpec(FilterJoin.And, [Rule(FilterConditionType.RevenueWithoutNormalCounterpart)])]);
        await using var connection = await OpenAsync();
        var page = await FilterVoucherPageReader.ReadAsync(connection, SqliteDialect.Instance, "", scenario, Context,
            voucher, new PageRequest(null, 200, IncludeTotalCount: true), countWholeVoucherRows: false, CancellationToken.None);
        var (scanRule, scanParameters) = PerEntryScanPredicate("revenueWithoutNormalCounterpart");
        var scan = await ReadPerEntryScanVoucherRowsAsync(
            $"CASE WHEN ({scanRule}) THEN 1 ELSE 0 END", "", "", voucher, scanParameters);

        AssertVoucherRows(expectedRows, expectedHits, scan);
        AssertVoucherRows(expectedRows, expectedHits, page.Details.Select(static row => (row.EntryId, row.IsHit)).ToList());
        Assert.Equal(expectedRows.Length, page.TotalCount);
    }

    private static void AssertVoucherRows(long[] expectedRows, long[] expectedHits, IReadOnlyList<(long EntryId, bool IsHit)> rows)
    {
        Assert.Equal(expectedRows, rows.Select(static row => row.EntryId));
        Assert.Equal(expectedHits, rows.Where(static row => row.IsHit).Select(static row => row.EntryId));
    }

    private static FilterConditionSpec Rule(FilterConditionType type) =>
        new(FilterJoin.And, type, null, null, [], TextMatchMode.Contains, null, null, null, null,
            type == FilterConditionType.DrCrOnly ? "debit" : null, null);

    /// <summary>產品程式組出的規則條件與參數。</summary>
    private static (string Sql, IReadOnlyList<(string Name, object Value)> Parameters) ProductPredicate(string rule)
    {
        var predicates = new GlRulePredicates(SqliteDialect.Instance, GlPopulationScopeSql.Predicate);
        var builder = new FilterSqlParameterPlanBuilder(SqliteDialect.Instance);
        var parts = rule.Split(' ');
        var sql = parts[0] switch
        {
            "unexpectedAccountPair" => predicates.UnexpectedAccountPair(builder, Context),
            "revenueWithoutNormalCounterpart" => predicates.RevenueWithoutNormalCounterpart(builder, Context),
            "accountPair" => predicates.AccountPair(builder, parts[1], [CategoryId(parts[2])], [CategoryId(parts[3])], Context),
            "specialAccountCategoryPair" =>
                predicates.SpecialAccountCategoryPair(builder, parts[1], [CategoryId(parts[2])], [CategoryId(parts[3])], Context),
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, null)
        };
        return (sql, builder.Parameters.Select(static parameter => (parameter.Name, parameter.Value)).ToArray());
    }

    private static string CategoryId(string name) => name switch
    {
        "Revenue" => AccountCategoryBuiltIns.RevenueId,
        "Cash" => AccountCategoryBuiltIns.CashId,
        "Receivables" => AccountCategoryBuiltIns.ReceivablesId,
        "Others" => AccountCategoryBuiltIns.OthersId,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null)
    };

    /// <summary>
    /// 逐筆掃傳票的規則條件：每一筆分錄 g 各自用 EXISTS 或 NOT EXISTS 回頭掃同一張傳票，判斷的內容和產品程式相同。
    /// 分類一律比對審計角色，和沒有指定選取方式的條件相同。
    /// </summary>
    private static (string Sql, IReadOnlyList<(string Name, object Value)> Parameters) PerEntryScanPredicate(string rule)
    {
        const string revenueRow =
            "EXISTS (SELECT 1 FROM account_mapping m JOIN account_category tm ON tm.category_id = m.category_id "
            + "WHERE m.account_number = g.account_number AND tm.semantic_role = @revenue)";
        var parts = rule.Split(' ');
        switch (parts[0])
        {
            case "unexpectedAccountPair":
                return ($"""
                    (g.voucher_number IS NOT NULL
                     AND {revenueRow}
                     AND g.amount_scaled < 0
                     AND EXISTS (SELECT 1 FROM gl p WHERE p.voucher_number = g.voucher_number AND p.is_excluded = 0 AND p.amount_scaled > 0)
                     AND NOT EXISTS (
                         SELECT 1 FROM gl d
                         JOIN account_mapping md ON md.account_number = d.account_number
                         JOIN account_category td ON td.category_id = md.category_id
                         WHERE d.voucher_number = g.voucher_number AND d.is_excluded = 0 AND d.amount_scaled > 0
                           AND td.semantic_role IN (@receivables, @cash, @receiptInAdvance)))
                    """,
                    [
                        ("@revenue", AccountCategoryBuiltIns.RevenueRole), ("@receivables", AccountCategoryBuiltIns.ReceivablesRole),
                        ("@cash", AccountCategoryBuiltIns.CashRole), ("@receiptInAdvance", AccountCategoryBuiltIns.ReceiptInAdvanceRole)
                    ]);
            case "revenueWithoutNormalCounterpart":
                return ($"""
                    (g.voucher_number IS NOT NULL
                     AND {revenueRow}
                     AND g.amount_scaled < 0
                     AND NOT EXISTS (
                         SELECT 1 FROM gl d
                         JOIN account_mapping md ON md.account_number = d.account_number
                         JOIN account_category td ON td.category_id = md.category_id
                         WHERE d.voucher_number = g.voucher_number AND d.is_excluded = 0 AND d.amount_scaled >= 0
                           AND td.semantic_role IN (@receivables, @receiptInAdvance)))
                    """,
                    [
                        ("@revenue", AccountCategoryBuiltIns.RevenueRole), ("@receivables", AccountCategoryBuiltIns.ReceivablesRole),
                        ("@receiptInAdvance", AccountCategoryBuiltIns.ReceiptInAdvanceRole)
                    ]);
        }

        const string debitRoles = "IN (SELECT ts.semantic_role FROM account_category ts WHERE ts.category_id IN (@debitCategory))";
        const string creditRoles = "IN (SELECT ts.semantic_role FROM account_category ts WHERE ts.category_id IN (@creditCategory))";
        const string rowIsDebit =
            "(EXISTS (SELECT 1 FROM account_mapping m JOIN account_category tm ON tm.category_id = m.category_id "
            + $"WHERE m.account_number = g.account_number AND tm.semantic_role {debitRoles}) AND g.amount_scaled >= 0)";
        const string rowIsCredit =
            "(EXISTS (SELECT 1 FROM account_mapping m JOIN account_category tm ON tm.category_id = m.category_id "
            + $"WHERE m.account_number = g.account_number AND tm.semantic_role {creditRoles}) AND g.amount_scaled < 0)";
        const string docHasDebit =
            "EXISTS (SELECT 1 FROM gl d JOIN account_mapping md ON md.account_number = d.account_number "
            + "JOIN account_category td ON td.category_id = md.category_id "
            + $"WHERE d.voucher_number = g.voucher_number AND d.is_excluded = 0 AND td.semantic_role {debitRoles} AND d.amount_scaled >= 0)";
        const string docHasCredit =
            "EXISTS (SELECT 1 FROM gl c JOIN account_mapping mc ON mc.account_number = c.account_number "
            + "JOIN account_category tc ON tc.category_id = mc.category_id "
            + $"WHERE c.voucher_number = g.voucher_number AND c.is_excluded = 0 AND tc.semantic_role {creditRoles} AND c.amount_scaled < 0)";
        var sql = parts[1] switch
        {
            "exact" or "drAndCr" => $"({docHasDebit} AND {docHasCredit} AND ({rowIsDebit} OR {rowIsCredit}))",
            "debitAnchor" => $"({docHasDebit} AND ({rowIsDebit} OR g.amount_scaled < 0))",
            "creditAnchor" => $"({docHasCredit} AND ({rowIsCredit} OR g.amount_scaled >= 0))",
            "drNotCr" => $"({docHasDebit} AND NOT {docHasCredit} AND ({rowIsDebit} OR g.amount_scaled < 0))",
            "notDrCr" => $"({docHasCredit} AND NOT {docHasDebit} AND ({rowIsCredit} OR g.amount_scaled >= 0))",
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, null)
        };
        return (sql, [("@debitCategory", CategoryId(parts[2])), ("@creditCategory", CategoryId(parts[3]))]);
    }

    private async Task<long[]> ReadEntryIdsAsync(string predicate, IReadOnlyList<(string Name, object Value)> parameters)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT g.entry_id FROM gl g WHERE g.is_excluded = 0 AND ({predicate}) ORDER BY g.entry_id;";
        Bind(command, parameters);
        var ids = new List<long>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids.ToArray();
    }

    /// <summary>逐筆掃傳票的高風險條件彙總傳票頁：每一筆分錄各自用 EXISTS 找同一張傳票有沒有命中的分錄。</summary>
    private async Task<VoucherTagRow[]> ReadPerEntryScanHighRiskVouchersAsync(int[] positions)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT g.voucher_number, MIN(g.gl_date), MIN(g.created_by), COALESCE(SUM(g.debit_amount_scaled), 0) "
            + "FROM gl g WHERE g.is_excluded = 0 AND g.voucher_number IS NOT NULL "
            + "AND EXISTS (SELECT 1 FROM filter_hit r JOIN filter_scenario fs ON fs.scenario_id = r.scenario_id "
            + "JOIN gl hit ON hit.entry_id = r.entry_id WHERE hit.voucher_number = g.voucher_number AND hit.is_excluded = 0 "
            + $"AND fs.position IN ({string.Join(", ", positions)})) "
            + "GROUP BY g.voucher_number ORDER BY g.voucher_number;";
        var rows = new List<VoucherTagRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new VoucherTagRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
        }

        return rows.ToArray();
    }

    /// <summary>逐筆展開的第 4-1 步標記：整張傳票的位置，每一筆命中分錄各自接回同一張傳票的全部分錄。</summary>
    private async Task<string[]> ReadPerEntryScanStep41TagsAsync(int[] positions, int[] wholeVoucherPositions)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT r.entry_id, fs.position FROM filter_hit r JOIN filter_scenario fs ON fs.scenario_id = r.scenario_id "
            + "JOIN gl g ON g.entry_id = r.entry_id "
            + $"WHERE g.is_excluded = 0 AND fs.position IN ({string.Join(", ", positions)}) "
            + "UNION "
            + "SELECT expanded.entry_id, fs.position FROM filter_hit r JOIN filter_scenario fs ON fs.scenario_id = r.scenario_id "
            + "JOIN gl hit ON hit.entry_id = r.entry_id JOIN gl expanded ON expanded.voucher_number = hit.voucher_number "
            + "WHERE hit.voucher_number IS NOT NULL AND hit.is_excluded = 0 AND expanded.is_excluded = 0 "
            + $"AND fs.position IN ({string.Join(", ", wholeVoucherPositions)}) "
            + "ORDER BY 1, 2;";
        var tags = new List<(long EntryId, int Position)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tags.Add((reader.GetInt64(0), reader.GetInt32(1)));
        }

        return tags.GroupBy(static tag => tag.EntryId)
            .Select(static group => $"{group.Key}:{string.Join(",", group.Select(static tag => tag.Position))}")
            .ToArray();
    }

    /// <summary>逐筆掃傳票的傳票明細：每一筆分錄各自用 EXISTS 找同一張傳票有沒有命中的分錄。</summary>
    private async Task<IReadOnlyList<(long EntryId, bool IsHit)>> ReadPerEntryScanVoucherRowsAsync(
        string isHit,
        string join,
        string voucherFilter,
        string voucher,
        IReadOnlyList<(string Name, object Value)> parameters)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"WITH evaluated AS (SELECT g.*, {isHit} AS is_hit FROM gl g {join} "
            + $"WHERE g.is_excluded = 0 {voucherFilter} AND g.voucher_number = @document) "
            + "SELECT e.entry_id, e.is_hit FROM evaluated e WHERE e.voucher_number = @document "
            + "AND EXISTS (SELECT 1 FROM evaluated hit WHERE hit.voucher_number = e.voucher_number AND hit.is_hit = 1) "
            + "ORDER BY e.entry_id;";
        Bind(command, [.. parameters, ("@document", voucher)]);
        var rows = new List<(long, bool)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetInt64(0), reader.GetInt64(1) == 1));
        }

        return rows;
    }

    private static void Bind(DbCommand command, IEnumerable<(string Name, object Value)> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }

    private async Task<DbConnection> OpenAsync()
    {
        var connection = _database.CreateConnection(_projectId);
        await connection.OpenAsync();
        return connection;
    }

    private void Seed()
    {
        using var connection = _database.CreateConnection(_projectId);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var entry in Entries)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO pbc_gl (batch_id, row_number, source_no, source_row_number, row_json)
                VALUES ('b', @id, 1, @id, '{}');
                INSERT INTO gl
                    (entry_id, batch_id, source_row_number, voucher_number, line_item, gl_date, account_number, account_name,
                     created_by, is_excluded, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
                VALUES
                    (@id, 'b', @id, @voucher, @line, @date, @account, @account,
                     @createdBy, @excluded, @amount, @debit, @credit, @drCr);
                """;
            var scaled = entry.Amount * 100;
            Bind(command,
            [
                ("@id", entry.Id), ("@voucher", (object?)entry.Voucher ?? DBNull.Value), ("@line", entry.Line),
                ("@date", entry.Date), ("@account", entry.Account), ("@createdBy", entry.CreatedBy),
                ("@excluded", entry.Excluded ? 1 : 0), ("@amount", scaled), ("@debit", Math.Max(scaled, 0)),
                ("@credit", Math.Max(-scaled, 0)), ("@drCr", scaled >= 0 ? "DEBIT" : "CREDIT")
            ]);
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                $"""
                INSERT INTO account_mapping (batch_id, source_row_number, account_number, account_name, standardized_category, category_id)
                VALUES
                    ('am', 1, '{Revenue}', '收入', 'Revenue', '{AccountCategoryBuiltIns.RevenueId}'),
                    ('am', 2, '{Cash}', '現金', 'Cash', '{AccountCategoryBuiltIns.CashId}'),
                    ('am', 3, '{Receivables}', '應收', 'Receivables', '{AccountCategoryBuiltIns.ReceivablesId}'),
                    ('am', 4, '{ReceiptInAdvance}', '預收', 'Receipt in advance', '{AccountCategoryBuiltIns.ReceiptInAdvanceId}'),
                    ('am', 5, '{Others}', '其他', 'Others', '{AccountCategoryBuiltIns.OthersId}');
                INSERT INTO filter_scenario (scenario_id, position, name, rationale, definition_json, saved_utc) VALUES
                    (1, 1, '對照', '對照', '{"{}"}', '2026-11-17T00:00:00Z'),
                    (2, 2, '對照', '對照', '{"{}"}', '2026-11-17T00:00:00Z'),
                    (3, 3, '對照', '對照', '{"{}"}', '2026-11-17T00:00:00Z');
                """;
            command.ExecuteNonQuery();
        }

        foreach (var (scenarioId, entryIds) in SavedHits)
        {
            foreach (var entryId in entryIds)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO filter_hit (scenario_id, entry_id) VALUES (@scenario, @entry);";
                Bind(command, [("@scenario", scenarioId), ("@entry", entryId)]);
                command.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }
}
