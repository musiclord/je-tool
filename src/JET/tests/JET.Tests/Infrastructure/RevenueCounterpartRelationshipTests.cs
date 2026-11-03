using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// R3（unexpected_account_pair，否定面）與「收入無一般對方科目」（revenue_without_normal_counterpart）的
/// 重疊關係不變式（2026-07-08 spec §1.3）。兩者都挑「貸 Revenue 且同傳票無正常對方借方」，
/// 差別只在對方集合：R3 含 Cash、C 不含。故 R3 條件較嚴 → 命中集 ⊆ C。
///
/// 2026-08-14 零元邊界後，兩者的對方認定另有一項差異：R3 的正常對方借方採
/// `amount_scaled > 0`（0 元不算已收到對價），C 維持 `>= 0`。因此包含關係只在
/// 「沒有零元正常對方借方」時成立，ZERO 傳票是唯一且刻意保留的分歧點。
///
/// 裁定 2026-10-29-29 改成照舊版 IDEA R3：R3 另要求同傳票至少有一筆金額大於 0 的分錄。這只會讓 R3 變小，
/// 包含關係不受影響。
///
/// fixture 五型傳票：
///   CS   賒銷：借 1131 Receivables ／貸 4101 Revenue  → R3 與 C 皆不命中（有 Receivables 對方）
///   CASH 現銷：借 1101 Cash        ／貸 4101 Revenue  → C 命中、R3 不命中（Cash 只在 R3 對方集合內）
///   NONE 無對方：借 5101 Others     ／貸 4101 Revenue  → R3 與 C 皆命中（無任何正常對方）
///   ZERO 零元對方：借 1131 Receivables 0 元、借 5101 Others 正金額／貸 4101 Revenue
///        → R3 命中（0 元不算對方，且有正金額分錄）、C 不命中
///   ZNP  只有零元對方：借 1131 Receivables 0 元／貸 4101 Revenue → 沒有正金額分錄，R3 與 C 皆不命中
/// </summary>
public sealed class RevenueCounterpartRelationshipTests : IDisposable
{
    private const int MoneyScale = 100;

    private const string FixtureSql =
        """
        INSERT INTO gl
            (batch_id, source_row_number, voucher_number, line_item, gl_date, approval_date,
             account_number, account_name, description, source_module, created_by, approved_by,
             is_manual, is_excluded, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
        VALUES
            ('b1', 1, 'CS',   '1', '2025-03-05', '2025-03-06', '1131', '應收帳款', '賒銷',   NULL, '王一', NULL, 0, 0, 10000,  10000, 0,     'DEBIT'),
            ('b1', 2, 'CS',   '2', '2025-03-05', '2025-03-06', '4101', '銷貨收入', '賒銷',   NULL, '王一', NULL, 0, 0, -10000, 0,     10000, 'CREDIT'),
            ('b1', 3, 'CASH', '1', '2025-03-05', '2025-03-06', '1101', '現金',     '現銷',   NULL, '王一', NULL, 0, 0, 20000,  20000, 0,     'DEBIT'),
            ('b1', 4, 'CASH', '2', '2025-03-05', '2025-03-06', '4101', '銷貨收入', '現銷',   NULL, '王一', NULL, 0, 0, -20000, 0,     20000, 'CREDIT'),
            ('b1', 5, 'NONE', '1', '2025-03-05', '2025-03-06', '5101', '銷貨成本', '無對方', NULL, '王一', NULL, 0, 0, 30000,  30000, 0,     'DEBIT'),
            ('b1', 6, 'NONE', '2', '2025-03-05', '2025-03-06', '4101', '銷貨收入', '無對方', NULL, '王一', NULL, 0, 0, -30000, 0,     30000, 'CREDIT'),
            ('b1', 7, 'ZERO', '1', '2025-03-05', '2025-03-06', '1131', '應收帳款', '零元對方', NULL, '王一', NULL, 0, 0, 0,      0,     0,     'DEBIT'),
            ('b1', 8, 'ZERO', '2', '2025-03-05', '2025-03-06', '4101', '銷貨收入', '零元對方', NULL, '王一', NULL, 0, 0, -40000, 0,     40000, 'CREDIT'),
            -- 裁定 2026-10-29-29：R3 要求傳票裡有金額大於 0 的分錄；ZERO 原本只有 0 元借方，改後不再命中 R3
            -- （第一次失敗：Focused 收據 20261030-163342206-c1b391e5be2e46bfa36b85fdbe6b59ad）。補一筆正金額的其他科目借方，保留這張傳票作為零元邊界的分歧點。
            ('b1', 9, 'ZERO', '3', '2025-03-05', '2025-03-06', '5101', '銷貨成本', '零元對方', NULL, '王一', NULL, 0, 0, 40000,  40000, 0,     'DEBIT'),
            ('b1', 10, 'ZNP', '1', '2025-03-05', '2025-03-06', '1131', '應收帳款', '只有零元對方', NULL, '王一', NULL, 0, 0, 0,      0,     0,     'DEBIT'),
            ('b1', 11, 'ZNP', '2', '2025-03-05', '2025-03-06', '4101', '銷貨收入', '只有零元對方', NULL, '王一', NULL, 0, 0, -50000, 0,     50000, 'CREDIT');

        -- 科目配對一律以 category_id 連到分類表（2026-10-02 起刪除依分類名稱對應的退路），測試資料補上 category_id。
        INSERT INTO account_mapping
            (batch_id, source_row_number, account_number, account_name, standardized_category, category_id)
        VALUES
            ('am1', 1, '1131', '應收帳款', 'Receivables', 'builtin.receivables'),
            ('am1', 2, '1101', '現金',     'Cash', 'builtin.cash'),
            ('am1', 3, '5101', '銷貨成本', 'Others', 'builtin.others'),
            ('am1', 4, '4101', '銷貨收入', 'Revenue', 'builtin.revenue');
        """;

    private readonly TempProjectRoot _root = new();
    private readonly IFilterHitRepository _repository;
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

        _repository = new LocalFilterHitRepository(database);
    }

    private static FilterConditionContext Context => new(MoneyScale, "2025-12-31", "2025-01-01", "2025-12-31");

    private async Task<IReadOnlyList<string>> HitDocumentsAsync(FilterConditionType type, string? prescreenKey)
    {
        var rule = new FilterConditionSpec(FilterJoin.And, type, prescreenKey, null, [],
            TextMatchMode.Contains, null, null, null, null, null, null);
        var scenario = new FilterScenarioSpec("關係測試", "R3⊆C",
            [new FilterGroupSpec(FilterJoin.And, [rule])]);
        var result = await _repository.PreviewAsync(_projectId, scenario, Context, CancellationToken.None);
        return result.PreviewRows
            .Select(r => r.VoucherNumber!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();
    }

    [Fact]
    public async Task R3_IsSubsetOfC_ExceptZeroAmountCounterpart_AndCashSaleDiscriminates()
    {
        var r3 = await HitDocumentsAsync(FilterConditionType.Prescreen, PrescreenRuleKeys.UnexpectedAccountPair);
        var c = await HitDocumentsAsync(FilterConditionType.RevenueWithoutNormalCounterpart, null);

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
    public async Task ZeroAmountNormalCounterpart_OnlyBreaksR3_NotRevenueCounterpartRule()
    {
        var r3 = await HitDocumentsAsync(FilterConditionType.Prescreen, PrescreenRuleKeys.UnexpectedAccountPair);
        var c = await HitDocumentsAsync(FilterConditionType.RevenueWithoutNormalCounterpart, null);

        // 零元邊界只屬於 unexpected_account_pair：0 元應收借方不足以消解「收入貸記缺正常對方」，
        // 但「收入無一般對方科目」維持 `>= 0` 的既有借方判定，因此同一張傳票不命中它。
        Assert.Contains("ZERO", r3);
        Assert.DoesNotContain("ZERO", c);
        // 同樣只有 0 元應收借方、但整張傳票沒有金額大於 0 的分錄時，照舊版 R3 不命中。
        Assert.DoesNotContain("ZNP", r3);
        Assert.DoesNotContain("ZNP", c);
    }

    public void Dispose() => _root.Dispose();
}
