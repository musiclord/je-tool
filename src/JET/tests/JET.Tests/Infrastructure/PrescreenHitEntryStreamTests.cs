using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 預篩選報告一個查詢讀完一條規則的全部明細時，讀出的分錄與順序，要和畫面用的分頁查詢逐頁讀完相同。
/// 合成資料同一張傳票的分錄散在各處，另有一列無效分錄、一列傳票號碼空白的收入貸方，以及摘要空白的分錄。
/// 預期值由下面的資料直接數出：
///
///   未預期借貸組合：PAIR 與 NONE 的收入貸方沒有正常的對方科目，列出整張傳票的有效分錄
///     → PAIR 的 entry 1、4、7、10（entry 9 無效），NONE 的 entry 2、8；
///     CS 有應收帳款作對方科目，不列；傳票號碼空白的 entry 5 不屬於任何傳票，不列。
///   摘要空白：只列直接符合的有效分錄 → entry 3（entry 9 摘要也空白，但無效）。
/// </summary>
public sealed class PrescreenHitEntryStreamTests
{
    private const string FixtureSql =
        """
        INSERT INTO account_mapping
            (batch_id, source_row_number, account_number, account_name, standardized_category, category_id)
        VALUES
            ('am1', 1, '1131', '應收帳款', 'Receivables', 'builtin.receivables'),
            ('am1', 2, '5101', '營業成本', 'Others', 'builtin.others'),
            ('am1', 3, '4101', '銷貨收入', 'Revenue', 'builtin.revenue');
        INSERT INTO gl
            (batch_id, source_row_number, voucher_number, line_item, gl_date, approval_date, account_number, account_name,
             description, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr, is_excluded)
        VALUES
            ('b1', 1, 'PAIR', '1', '2025-03-05', '2025-03-06', '4101', '銷貨收入', '合成', -10000, 0, 10000, 'CREDIT', 0),
            ('b1', 2, 'NONE', '1', '2025-03-05', '2025-03-06', '5101', '營業成本', '合成', 30000, 30000, 0, 'DEBIT', 0),
            ('b1', 3, 'CS', '1', '2025-03-05', '2025-03-06', '1131', '應收帳款', NULL, 10000, 10000, 0, 'DEBIT', 0),
            ('b1', 4, 'PAIR', '2', '2025-03-05', '2025-03-06', '5101', '營業成本', '合成', 5000, 5000, 0, 'DEBIT', 0),
            ('b1', 5, NULL, '1', '2025-03-05', '2025-03-06', '4101', '銷貨收入', '合成', -10000, 0, 10000, 'CREDIT', 0),
            ('b1', 6, 'CS', '2', '2025-03-05', '2025-03-06', '4101', '銷貨收入', '合成', -10000, 0, 10000, 'CREDIT', 0),
            ('b1', 7, 'PAIR', '3', '2025-03-05', '2025-03-06', '4101', '銷貨收入', '合成', -10000, 0, 10000, 'CREDIT', 0),
            ('b1', 8, 'NONE', '2', '2025-03-05', '2025-03-06', '4101', '銷貨收入', '合成', -30000, 0, 30000, 'CREDIT', 0),
            ('b1', 9, 'PAIR', '4', '2025-03-05', '2025-03-06', '5101', '營業成本', NULL, 7000, 7000, 0, 'DEBIT', 1),
            ('b1', 10, 'PAIR', '5', '2025-03-05', '2025-03-06', '5101', '營業成本', '合成', 15000, 15000, 0, 'DEBIT', 0);
        """;

    private static readonly FilterConditionContext Context = new(100, "2025-12-31", "2025-01-01", "2025-12-31");

    [Theory]
    [InlineData(PrescreenRuleKeys.UnexpectedAccountPair, new long[] { 1, 2, 4, 7, 8, 10 })]
    [InlineData(PrescreenRuleKeys.BlankDescription, new long[] { 3 })]
    public async Task Stream_ReadsTheRuleRowsInEntryOrder_SameAsWalkingEveryPage(string ruleKey, long[] expectedEntryIds)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await ExecuteAsync(database, projectId, FixtureSql);
        var repository = new LocalPrescreenPageRepository(database);

        var streamed = new List<long>();
        await foreach (var entryId in ((IPrescreenHitEntryStreamRepository)repository).StreamEntryIdsAsync(
                           projectId,
                           ruleKey,
                           Context,
                           CancellationToken.None))
        {
            streamed.Add(entryId);
        }

        Assert.Equal(expectedEntryIds, streamed);
        Assert.Equal(await WalkEveryPageAsync(repository, projectId, ruleKey), streamed);
    }

    /// <summary>畫面的分頁查詢沒有指定排序，每頁 2 筆逐頁讀到底。</summary>
    private static async Task<List<long>> WalkEveryPageAsync(IPrescreenPageRepository pages, string projectId, string ruleKey)
    {
        var walked = new List<long>();
        string? cursor = null;
        do
        {
            var page = await pages.GetPageAsync(projectId, ruleKey, Context, new PageRequest(cursor, 2), CancellationToken.None);
            walked.AddRange(page.Rows.Select(row => row.EntryId));
            cursor = page.NextCursor;
        } while (cursor is not null);
        return walked;
    }

    private static async Task ExecuteAsync(ILocalProjectDatabase database, string projectId, string sql)
    {
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
