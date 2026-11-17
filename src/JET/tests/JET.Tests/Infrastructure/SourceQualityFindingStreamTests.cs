using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 資料驗證報告一個查詢讀完來源資料品質的全部明細時，讀出的分錄與順序，要和畫面用的分頁查詢逐頁讀完相同。
/// 合成資料有兩個來源：第一個是活頁簿的工作表，第二個是文字檔，各兩列。預期值由下面的資料直接數出：
///
///   entry 1：有總帳日期，不列
///   entry 2：總帳日期是空值，未納入測試也照列 → 第一個來源的第 3 列，來源名稱帶工作表名稱
///   entry 3：總帳日期是空值 → 第二個來源的第 2 列，來源名稱只有檔名
///   entry 4：有總帳日期，不列
/// </summary>
public sealed class SourceQualityFindingStreamTests
{
    private const string FixtureSql =
        """
        INSERT INTO import_batch_source
            (batch_id, source_no, source_file_path, source_file_name, sheet_name, row_count, imported_utc)
        VALUES
            ('b1', 1, 'synthetic/gl-a.xlsx', 'gl-a.xlsx', '總帳', 2, '2025-01-01T00:00:00Z'),
            ('b1', 2, 'synthetic/gl-b.csv', 'gl-b.csv', NULL, 2, '2025-01-01T00:00:00Z');
        INSERT INTO pbc_gl (batch_id, row_number, source_no, source_row_number, row_json)
        VALUES
            ('b1', 1, 1, 2, '{}'),
            ('b1', 2, 1, 3, '{}'),
            ('b1', 3, 2, 2, '{}'),
            ('b1', 4, 2, 3, '{}');
        INSERT INTO gl
            (batch_id, source_row_number, voucher_number, gl_date, account_number, account_name, description,
             amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr, is_excluded)
        VALUES
            ('b1', 1, 'V1', '2025-03-05', '1101', '現金', '合成一', 100, 100, 0, 'DEBIT', 0),
            ('b1', 2, 'V1', NULL, '4101', '收入', '合成二', -100, 0, 100, 'CREDIT', 1),
            ('b1', 3, 'V2', NULL, '1101', '現金', NULL, 200, 200, 0, 'DEBIT', 0),
            ('b1', 4, 'V2', '2025-04-05', '4101', '收入', '合成四', -200, 0, 200, 'CREDIT', 0);
        """;

    [Fact]
    public async Task Stream_ReadsEveryBlankGlDateRowInEntryOrder_SameAsWalkingEveryPage()
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

        var repository = new LocalSourceQualityPageRepository(database);
        var streamed = new List<SourceQualityFindingRow>();
        await foreach (var finding in ((ISourceQualityFindingStreamRepository)repository).StreamAsync(
                           projectId,
                           CancellationToken.None))
        {
            streamed.Add(finding);
        }

        SourceQualityFindingRow[] expected =
        [
            new("blankGlDate", 3, "gl-a.xlsx [總帳]", "V1", "4101", null, "合成二", 2),
            new("blankGlDate", 2, "gl-b.csv", "V2", "1101", null, null, 3)
        ];
        Assert.Equal(expected, streamed);

        var walked = new List<SourceQualityFindingRow>();
        string? cursor = null;
        do
        {
            var page = await repository.GetPageAsync(projectId, new PageRequest(cursor, 1), CancellationToken.None);
            walked.AddRange(page.Rows);
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(walked, streamed);
    }
}
