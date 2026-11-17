using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 資料可靠性抽樣清單與底稿 step2 一個查詢讀完一次驗證的全部樣本時，讀出的樣本與順序，要和畫面用的分頁查詢逐頁讀完相同。
/// 預期值由下面的資料直接數出：
///
///   第一次驗證抽了 entry 5、2、4：entry 4 未納入測試，不列 → 依 entry_id 是 entry 2、5
///   第二次驗證抽了 entry 1、3，不屬於第一次驗證，不列
/// </summary>
public sealed class InfSampleStreamTests
{
    private const string FirstResultId = "result-first";
    private const string SecondResultId = "result-second";

    private const string FixtureSql =
        """
        INSERT INTO gl
            (batch_id, source_row_number, voucher_number, line_item, gl_date, approval_date, account_number, account_name,
             description, source_module, created_by, approved_by, amount_scaled, debit_amount_scaled, credit_amount_scaled,
             dr_cr, is_excluded)
        VALUES
            ('b1', 1, 'V1', '1', '2025-03-05', '2025-03-06', '1101', '現金', '合成一', 'GL', '甲', '乙', 100, 100, 0, 'DEBIT', 0),
            ('b1', 2, 'V1', '2', '2025-03-05', '2025-03-06', '4101', '收入', '合成二', 'GL', '甲', '乙', -100, 0, 100, 'CREDIT', 0),
            ('b1', 3, 'V2', '1', '2025-04-05', '2025-04-06', '1101', '現金', '合成三', 'AP', '丙', NULL, 200, 200, 0, 'DEBIT', 0),
            ('b1', 4, 'V2', '2', '2025-04-05', '2025-04-06', '4101', '收入', '合成四', 'AP', '丙', NULL, -200, 0, 200, 'CREDIT', 1),
            ('b1', 5, 'V3', '1', '2025-05-05', NULL, '1101', '現金', NULL, NULL, '丁', NULL, 300, 300, 0, 'DEBIT', 0);
        INSERT INTO inf_sample (result_id, entry_id, voucher_number, line_item)
        VALUES
            ('result-first', 5, 'V3', '1'),
            ('result-first', 2, 'V1', '2'),
            ('result-first', 4, 'V2', '2'),
            ('result-second', 1, 'V1', '1'),
            ('result-second', 3, 'V2', '1');
        """;

    [Fact]
    public async Task Stream_ReadsTheSamplesOfOneValidationInEntryOrder_SameAsWalkingEveryPage()
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

        var repository = new LocalInfSamplePageRepository(database);
        var streamed = new List<InfSampleRow>();
        await foreach (var sample in ((IInfSampleStreamRepository)repository).StreamAsync(
                           projectId,
                           FirstResultId,
                           CancellationToken.None))
        {
            streamed.Add(sample);
        }

        InfSampleRow[] expected =
        [
            new("V1", "4101", "收入", 0, 100, "2025-03-05", "2025-03-06", "甲", "乙", "合成二", 2, "GL", "CREDIT"),
            new("V3", "1101", "現金", 300, 0, "2025-05-05", null, "丁", null, null, 5, null, "DEBIT")
        ];
        Assert.Equal(expected, streamed);

        var walked = new List<InfSampleRow>();
        string? cursor = null;
        do
        {
            var page = await repository.GetPageAsync(
                projectId, FirstResultId, 10_000, new PageRequest(cursor, 1), CancellationToken.None);
            walked.AddRange(page.Rows);
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(walked, streamed);

        var other = new List<long>();
        await foreach (var sample in ((IInfSampleStreamRepository)repository).StreamAsync(
                           projectId,
                           SecondResultId,
                           CancellationToken.None))
        {
            other.Add(sample.EntryId);
        }
        Assert.Equal(new long[] { 1, 3 }, other);
    }
}
