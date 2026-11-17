using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Application;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 資料驗證報告一個查詢讀完一類空值或期外日期紀錄時，讀出的分錄與順序，要和畫面用的分頁查詢逐頁讀完相同。
/// 查核期間是 2025-01-01 到 2025-12-31。預期值由下面的資料直接數出：
///
///   entry 1：四欄都有值，核准日在期間內，不列
///   entry 2：傳票號碼是空值 → 傳票號碼空白
///   entry 3：科目編號是空值 → 科目編號空白
///   entry 4：摘要是空值 → 摘要空白
///   entry 5：核准日是空值 → 核准日不在期間
///   entry 6：傳票號碼、科目編號與摘要都只有空白字元，核准日在期間之前 → 四類都列
///   entry 7：四欄都是空值，但未納入測試，四類都不列
///   entry 8：核准日在期間之後 → 核准日不在期間
///   entry 9：傳票號碼與摘要是空字串，核准日是期間最後一天 → 傳票號碼空白、摘要空白
/// </summary>
public sealed class BlankRecordEntryStreamTests
{
    private const string PeriodStart = "2025-01-01";
    private const string PeriodEnd = "2025-12-31";

    private const string FixtureSql =
        """
        INSERT INTO gl
            (batch_id, source_row_number, voucher_number, gl_date, approval_date, account_number, account_name,
             description, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr, is_excluded)
        VALUES
            ('b1', 1, 'A', '2025-03-05', '2025-03-06', '1101', '現金', '合成', 100, 100, 0, 'DEBIT', 0),
            ('b1', 2, NULL, '2025-03-05', '2025-03-06', '1101', '現金', '合成', 100, 100, 0, 'DEBIT', 0),
            ('b1', 3, 'B', '2025-03-05', '2025-03-06', NULL, '現金', '合成', 100, 100, 0, 'DEBIT', 0),
            ('b1', 4, 'C', '2025-03-05', '2025-03-06', '1101', '現金', NULL, 100, 100, 0, 'DEBIT', 0),
            ('b1', 5, 'D', '2025-03-05', NULL, '1101', '現金', '合成', 100, 100, 0, 'DEBIT', 0),
            ('b1', 6, '  ', '2025-03-05', '2024-12-31', '   ', '現金', '  ', 100, 100, 0, 'DEBIT', 0),
            ('b1', 7, NULL, '2025-03-05', NULL, NULL, '現金', NULL, 100, 100, 0, 'DEBIT', 1),
            ('b1', 8, 'E', '2025-03-05', '2026-01-01', '1101', '現金', '合成', 100, 100, 0, 'DEBIT', 0),
            ('b1', 9, '', '2025-03-05', '2025-12-31', '1101', '現金', '', 100, 100, 0, 'DEBIT', 0);
        """;

    [Theory]
    [InlineData(BlankRecordCategory.BlankAccountNumber, new long[] { 3, 6 })]
    [InlineData(BlankRecordCategory.BlankVoucherNumber, new long[] { 2, 6, 9 })]
    [InlineData(BlankRecordCategory.BlankDescription, new long[] { 4, 6, 9 })]
    [InlineData(BlankRecordCategory.ApprovalDateNotInPeriod, new long[] { 5, 6, 8 })]
    public async Task Stream_ReadsTheCategoryRowsInEntryOrder_SameAsWalkingEveryPage(
        BlankRecordCategory category,
        long[] expectedEntryIds)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var statements = new SqliteStatementWorkMeter(static sql =>
            sql.StartsWith("SELECT entry_id FROM gl", StringComparison.Ordinal) ? "stream" : null);
        ILocalProjectDatabase database = new SqliteProjectDatabase(folder, statements);
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

        var repository = new LocalBlankRecordsPageRepository(database);
        var streamed = new List<long>();
        await foreach (var entryId in ((IBlankRecordEntryStreamRepository)repository).StreamEntryIdsAsync(
                           projectId,
                           category,
                           PeriodStart,
                           PeriodEnd,
                           CancellationToken.None))
        {
            streamed.Add(entryId);
        }

        Assert.Equal(expectedEntryIds, streamed);
        Assert.Equal(1, statements.Statements("stream"));

        var walked = new List<long>();
        string? cursor = null;
        do
        {
            var page = await repository.GetPageAsync(
                projectId, category, PeriodStart, PeriodEnd, new PageRequest(cursor, 2), CancellationToken.None);
            walked.AddRange(page.Rows.Select(static row => row.EntryId));
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(walked, streamed);
    }
}
