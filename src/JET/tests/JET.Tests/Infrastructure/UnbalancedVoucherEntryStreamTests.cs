using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// V_Report 6 一個查詢讀完借貸不平傳票的全部有效分錄，依 entry_id 升冪。
/// 合成資料同一張傳票的分錄散在各處，另有無效分錄與傳票號碼空白的分錄。預期值由下面的資料直接算出：
///
///   U：借 100、貸 90，不平 → entry 1、9
///   B：借 100、貸 100，平衡，不列
///   E：有效分錄借 100、貸 100，平衡；另一列無效的 50 不算，不列
///   X：有效分錄借 100、貸 80，不平 → entry 4、10；無效的 entry 11 不列
///   傳票號碼空白的 entry 5 與 12 合計不為 0，但不屬於任何傳票，不列
/// </summary>
public sealed class UnbalancedVoucherEntryStreamTests
{
    private const string FixtureSql =
        """
        INSERT INTO gl
            (batch_id, source_row_number, voucher_number, gl_date, account_number, account_name,
             amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr, is_excluded)
        VALUES
            ('b1', 1, 'U', '2025-03-05', '1101', '現金', 100, 100, 0, 'DEBIT', 0),
            ('b1', 2, 'B', '2025-03-05', '1101', '現金', 100, 100, 0, 'DEBIT', 0),
            ('b1', 3, 'E', '2025-03-05', '1101', '現金', 100, 100, 0, 'DEBIT', 0),
            ('b1', 4, 'X', '2025-03-05', '1101', '現金', 100, 100, 0, 'DEBIT', 0),
            ('b1', 5, NULL, '2025-03-05', '1101', '現金', 100, 100, 0, 'DEBIT', 0),
            ('b1', 6, 'B', '2025-03-05', '4101', '收入', -100, 0, 100, 'CREDIT', 0),
            ('b1', 7, 'E', '2025-03-05', '4101', '收入', -100, 0, 100, 'CREDIT', 0),
            ('b1', 8, 'E', '2025-03-05', '1101', '現金', 50, 50, 0, 'DEBIT', 1),
            ('b1', 9, 'U', '2025-03-05', '4101', '收入', -90, 0, 90, 'CREDIT', 0),
            ('b1', 10, 'X', '2025-03-05', '4101', '收入', -80, 0, 80, 'CREDIT', 0),
            ('b1', 11, 'X', '2025-03-05', '4101', '收入', -20, 0, 20, 'CREDIT', 1),
            ('b1', 12, NULL, '2025-03-05', '4101', '收入', -10, 0, 10, 'CREDIT', 0);
        """;

    [Fact]
    public async Task Stream_ReadsEveryEffectiveRowOfUnbalancedVouchersInEntryOrder()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await using (var connection = database.CreateConnection(projectId))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = FixtureSql;
            await command.ExecuteNonQueryAsync();
        }

        var repository = new LocalUnbalancedVoucherEntryStreamRepository(database);
        var streamed = new List<long>();
        await foreach (var entryId in ((IUnbalancedVoucherEntryStreamRepository)repository).StreamEntryIdsAsync(
                           projectId,
                           CancellationToken.None))
        {
            streamed.Add(entryId);
        }

        Assert.Equal(new long[] { 1, 4, 9, 10 }, streamed);
    }
}
