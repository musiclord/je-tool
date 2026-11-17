using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 條件篩選報告一個情境用一個查詢讀完整張傳票分錄，讀出的分錄、順序與命中標記，
/// 要和畫面用的分頁查詢只指定這個情境、逐頁讀完相同。合成資料的 entry_id 順序和傳票號碼順序不同，
/// 另有傳票號碼空白的命中、不在母體裡的命中與分錄，以及情境編號和畫面位置不同的情境。預期值由下面的資料直接數出：
///
///   位置 1（情境編號 11）：命中 entry 2（傳票 B）、entry 8（傳票號碼空白）、entry 6（傳票 C，但不在母體裡）
///     → 只有傳票 B 是命中傳票，它的分錄是 entry 1、2、11，命中的是 entry 2
///   位置 2（情境編號 12）：命中 entry 3（傳票 A）、entry 7（傳票 C）
///     → 傳票 A 的 entry 3、4、9 與傳票 C 的 entry 5、7（entry 6 不在母體裡），命中的是 entry 3 與 7
///   位置 3（情境編號 13）：沒有命中 → 沒有分錄
/// </summary>
public sealed class HighRiskSummaryRowStreamTests
{
    private const string FixtureSql =
        """
        INSERT INTO gl
            (batch_id, source_row_number, voucher_number, line_item, gl_date, approval_date, account_number, account_name,
             description, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr, is_excluded)
        VALUES
            ('b1', 1, 'B', '1', '2025-03-05', '2025-03-06', '1101', '現金', '合成', 100, 100, 0, 'DEBIT', 0),
            ('b1', 2, 'B', '2', '2025-03-05', '2025-03-06', '4101', '收入', '合成', -100, 0, 100, 'CREDIT', 0),
            ('b1', 3, 'A', '1', '2025-03-05', '2025-03-06', '1101', '現金', '合成', 200, 200, 0, 'DEBIT', 0),
            ('b1', 4, 'A', '2', '2025-03-05', '2025-03-06', '4101', '收入', '合成', -100, 0, 100, 'CREDIT', 0),
            ('b1', 5, 'C', '1', '2025-03-05', '2025-03-06', '1101', '現金', '合成', 300, 300, 0, 'DEBIT', 0),
            ('b1', 6, 'C', '2', '2025-03-05', '2025-03-06', '4101', '收入', '合成', -50, 0, 50, 'CREDIT', 1),
            ('b1', 7, 'C', '3', '2025-03-05', '2025-03-06', '4101', '收入', '合成', -300, 0, 300, 'CREDIT', 0),
            ('b1', 8, NULL, '1', '2025-03-05', '2025-03-06', '1101', '現金', '合成', 400, 400, 0, 'DEBIT', 0),
            ('b1', 9, 'A', '3', '2025-03-05', '2025-03-06', '4101', '收入', '合成', -100, 0, 100, 'CREDIT', 0),
            ('b1', 10, 'D', '1', '2025-03-05', '2025-03-06', '1101', '現金', '合成', 500, 500, 0, 'DEBIT', 0),
            ('b1', 11, 'B', '3', '2025-03-05', '2025-03-06', '1101', '現金', '合成', 0, 0, 0, 'DEBIT', 0);
        INSERT INTO filter_scenario (scenario_id, position, name, rationale, definition_json, saved_utc) VALUES
            (11, 1, 'synthetic 1', 'synthetic', '{}', '2026-11-17T00:00:00Z'),
            (12, 2, 'synthetic 2', 'synthetic', '{}', '2026-11-17T00:00:00Z'),
            (13, 3, 'synthetic 3', 'synthetic', '{}', '2026-11-17T00:00:00Z');
        INSERT INTO filter_hit (scenario_id, entry_id) VALUES
            (11, 2), (11, 8), (11, 6),
            (12, 3), (12, 7);
        """;

    private static readonly GlPopulationContext Population = new(GlPopulationScope.AuditPeriod, "2025-01-01", "2025-12-31");

    [Theory]
    [InlineData(1, new long[] { 1, 2, 11 }, new long[] { 2 })]
    [InlineData(2, new long[] { 3, 4, 5, 7, 9 }, new long[] { 3, 7 })]
    [InlineData(3, new long[0], new long[0])]
    public async Task Stream_ReadsTheHitVoucherRowsInEntryOrder_SameAsWalkingEveryPage(
        int scenarioPosition,
        long[] expectedEntryIds,
        long[] expectedHits)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await ExecuteAsync(database, projectId, FixtureSql);
        var repository = new LocalHighRiskSummaryRowPageRepository(database);

        var streamed = new List<(long EntryId, bool IsHit)>();
        await foreach (var row in ((IHighRiskSummaryRowStreamRepository)repository).StreamAsync(
                           projectId,
                           Population,
                           scenarioPosition,
                           CancellationToken.None))
        {
            streamed.Add(row);
        }

        Assert.Equal(expectedEntryIds, streamed.Select(row => row.EntryId).ToArray());
        Assert.Equal(expectedHits, streamed.Where(row => row.IsHit).Select(row => row.EntryId).ToArray());
        Assert.Equal(await WalkEveryPageAsync(repository, projectId, scenarioPosition), streamed);
    }

    /// <summary>畫面的分頁查詢只指定一個情境，每頁 2 筆逐頁讀到底；命中標記是這筆分錄有沒有這個情境的命中位置。</summary>
    private static async Task<List<(long EntryId, bool IsHit)>> WalkEveryPageAsync(
        IHighRiskSummaryRowPageRepository rows,
        string projectId,
        int scenarioPosition)
    {
        var walked = new List<(long EntryId, bool IsHit)>();
        string? cursor = null;
        do
        {
            var page = await rows.GetPageAsync(
                projectId,
                Population,
                new PageRequest(cursor, 2),
                [scenarioPosition],
                CancellationToken.None);
            walked.AddRange(page.EntryIds.Select(entryId => (entryId, page.PositionsByEntry.ContainsKey(entryId))));
            cursor = page.Page.NextCursor;
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
