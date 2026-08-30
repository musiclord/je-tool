using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// R3（unexpected_account_pair，否定面）與 KCT 條件 C（revenue_without_normal_counterpart）的
/// 重疊關係不變式（2026-07-08 spec §1.3）。兩者都挑「貸 Revenue 且同傳票無正常對方借方」，
/// 差別只在對方集合：R3 含 Cash、C 不含。故 R3 條件較嚴 → 命中集 ⊆ C。
///
/// 2026-08-14 零元邊界後，兩者的對方認定另有一項差異：R3 的正常對方借方採
/// `amount_scaled > 0`（0 元不算已收到對價），C 維持 `>= 0`。因此包含關係只在
/// 「沒有零元正常對方借方」時成立，ZERO 傳票是唯一且刻意保留的分歧點。
///
/// fixture 四型傳票（各 2 列借貸對沖）：
///   CS   賒銷：借 1131 Receivables ／貸 4101 Revenue  → R3 與 C 皆不命中（有 Receivables 對方）
///   CASH 現銷：借 1101 Cash        ／貸 4101 Revenue  → C 命中、R3 不命中（Cash 只在 R3 對方集合內）
///   NONE 無對方：借 5101 Others     ／貸 4101 Revenue  → R3 與 C 皆命中（無任何正常對方）
///   ZERO 零元對方：借 1131 Receivables 0 元／貸 4101 Revenue → R3 命中（0 元不算對方）、C 不命中
/// </summary>
public sealed class RevenueCounterpartRelationshipTests : IDisposable
{
    private const int MoneyScale = 100;

    private const string FixtureSql =
        """
        INSERT INTO target_gl_entry
            (batch_id, source_row_number, document_number, line_item, post_date, approval_date,
             account_code, account_name, document_description, source_module, created_by, approved_by,
             is_manual, is_effective, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
        VALUES
            ('b1', 1, 'CS',   '1', '2025-03-05', '2025-03-06', '1131', '應收帳款', '賒銷',   NULL, '王一', NULL, 0, 1, 10000,  10000, 0,     'DEBIT'),
            ('b1', 2, 'CS',   '2', '2025-03-05', '2025-03-06', '4101', '銷貨收入', '賒銷',   NULL, '王一', NULL, 0, 1, -10000, 0,     10000, 'CREDIT'),
            ('b1', 3, 'CASH', '1', '2025-03-05', '2025-03-06', '1101', '現金',     '現銷',   NULL, '王一', NULL, 0, 1, 20000,  20000, 0,     'DEBIT'),
            ('b1', 4, 'CASH', '2', '2025-03-05', '2025-03-06', '4101', '銷貨收入', '現銷',   NULL, '王一', NULL, 0, 1, -20000, 0,     20000, 'CREDIT'),
            ('b1', 5, 'NONE', '1', '2025-03-05', '2025-03-06', '5101', '銷貨成本', '無對方', NULL, '王一', NULL, 0, 1, 30000,  30000, 0,     'DEBIT'),
            ('b1', 6, 'NONE', '2', '2025-03-05', '2025-03-06', '4101', '銷貨收入', '無對方', NULL, '王一', NULL, 0, 1, -30000, 0,     30000, 'CREDIT'),
            ('b1', 7, 'ZERO', '1', '2025-03-05', '2025-03-06', '1131', '應收帳款', '零元對方', NULL, '王一', NULL, 0, 1, 0,      0,     0,     'DEBIT'),
            ('b1', 8, 'ZERO', '2', '2025-03-05', '2025-03-06', '4101', '銷貨收入', '零元對方', NULL, '王一', NULL, 0, 1, -40000, 0,     40000, 'CREDIT');

        INSERT INTO target_account_mapping
            (batch_id, source_row_number, account_code, account_name, standardized_category)
        VALUES
            ('am1', 1, '1131', '應收帳款', 'Receivables'),
            ('am1', 2, '1101', '現金',     'Cash'),
            ('am1', 3, '5101', '銷貨成本', 'Others'),
            ('am1', 4, '4101', '銷貨收入', 'Revenue');
        """;

    private readonly TempProjectRoot _root = new();
    private readonly IFilterRunRepository _repository;
    private readonly string _projectId;

    public RevenueCounterpartRelationshipTests()
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

    private static FilterRuleContext Context => new(MoneyScale, "2025-12-31", "2025-01-01", "2025-12-31");

    private async Task<IReadOnlyList<string>> HitDocumentsAsync(FilterRuleType type, string? prescreenKey)
    {
        var rule = new FilterRuleSpec(FilterJoin.And, type, prescreenKey, null, [],
            TextMatchMode.Contains, null, null, null, null, null, null);
        var scenario = new FilterScenarioSpec("關係測試", "R3⊆C",
            [new FilterGroupSpec(FilterJoin.And, [rule])]);
        var result = await _repository.PreviewAsync(_projectId, scenario, Context, CancellationToken.None);
        return result.PreviewRows
            .Select(r => r.DocumentNumber!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();
    }

    [Fact]
    public async Task R3_IsSubsetOfC_ExceptZeroAmountCounterpart_AndCashSaleDiscriminates()
    {
        var r3 = await HitDocumentsAsync(FilterRuleType.Prescreen, PrescreenRuleKeys.UnexpectedAccountPair);
        var c = await HitDocumentsAsync(FilterRuleType.RevenueWithoutNormalCounterpart, null);

        // R3 挑無任何「正常對方且金額 > 0」的傳票 → {NONE, ZERO}。
        Assert.Equal(["NONE", "ZERO"], r3);
        // C 不含 Cash、且 0 元借方仍算對方 → {CASH, NONE}。
        Assert.Equal(["CASH", "NONE"], c);

        // 包含關係：排除零元對方的傳票後仍是 R3 ⊆ C。
        Assert.Subset(
            c.ToHashSet(StringComparer.Ordinal),
            r3.Where(static document => document != "ZERO").ToHashSet(StringComparer.Ordinal));
        // 現銷是刻意的判別點：C 命中、R3 不命中（Cash 只在 R3 對方集合內）。
        Assert.Contains("CASH", c);
        Assert.DoesNotContain("CASH", r3);
        // 賒銷（有 Receivables 對方）兩者皆不命中。
        Assert.DoesNotContain("CS", c);
        Assert.DoesNotContain("CS", r3);
    }

    [Fact]
    public async Task ZeroAmountNormalCounterpart_OnlyBreaksR3_NotKctCounterpartRule()
    {
        var r3 = await HitDocumentsAsync(FilterRuleType.Prescreen, PrescreenRuleKeys.UnexpectedAccountPair);
        var c = await HitDocumentsAsync(FilterRuleType.RevenueWithoutNormalCounterpart, null);

        // 零元邊界只屬於 unexpected_account_pair：0 元應收借方不足以消解「收入貸記缺正常對方」，
        // 但 KCT 條件 C 維持 `>= 0` 的既有借方判定，因此同一張傳票不命中 C。
        Assert.Contains("ZERO", r3);
        Assert.DoesNotContain("ZERO", c);
    }

    public void Dispose() => _root.Dispose();
}
