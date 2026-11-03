using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// SQLite 與 DuckDB 的排序分頁必須給出同一個順序：空值一律最後、同值時穩定鍵接續、換頁不漏不重。
/// 兩個引擎對 NULL 的預設排序不同（SQLite 在前、DuckDB 在後），這裡用固定答案證明 CASE 形式把差異消掉了。
/// </summary>
public sealed class KeysetPagingProviderParityTests
{
    private const string Seed = """
        INSERT INTO gl (batch_id,source_row_number,voucher_number,line_item,gl_date,approval_date,
          account_number,description,amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_excluded,approved_by)
        VALUES
          ('f',1,'V1','1','2025-01-01','2025-01-01','001','a',10000,10000,0,'DEBIT',0,'bob'),
          ('f',2,'V1','2','2025-01-01','2025-01-01','002','b',-10000,0,10000,'CREDIT',0,NULL),
          ('f',3,'V2','1','2025-02-01','2025-02-01','001','c',5000,5000,0,'DEBIT',0,'alice'),
          ('f',4,'V2','2','2025-02-01','2025-02-01','002','d',-5000,0,5000,'CREDIT',0,'bob'),
          ('f',5,'V3','1','2025-03-01','2025-03-01','001','e',7000,7000,0,'DEBIT',0,NULL),
          ('f',6,'V3','2','2025-03-01','2025-03-01','002','f',-7000,0,7000,'CREDIT',0,'alice');
        INSERT INTO filter_scenario (scenario_id, position, name, rationale, definition_json, saved_utc) VALUES (1, 1, 'synthetic', 'synthetic', '{}', '2026-10-31T00:00:00Z');
        INSERT INTO filter_hit (scenario_id, entry_id) SELECT 1, entry_id FROM gl;
        """;

    private static readonly GlPopulationContext Context = new(GlPopulationScope.AuditPeriod, "2025-01-01", "2025-12-31");

    private static async Task<List<string>> WalkAsync(ILocalProjectDatabase db, string projectId, PageSort? sort, string? search)
    {
        var repository = new LocalHighRiskSummaryRowPageRepository(db);
        var order = new List<string>();
        string? cursor = null;
        do
        {
            var (page, _, _) = await repository.GetPageAsync(projectId, Context, new PageRequest(cursor, 2, sort, search), null, CancellationToken.None);
            order.AddRange(page.Rows.Select(static row => $"{row.VoucherNumber}|{row.LineItem}|{row.ApprovedBy ?? "∅"}"));
            cursor = page.NextCursor;
        } while (cursor is not null);

        return order;
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task SortedWalk_NullsLastAndStableKeyTieBreak_OnBothProviders(string provider)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase db = provider == "sqlite" ? new SqliteProjectDatabase(folder) : new DuckDbProjectDatabase(folder);
        const string id = "keyset-paging-parity";
        Directory.CreateDirectory(folder.GetProjectDirectory(id));
        await db.EnsureCreatedAsync(id, CancellationToken.None);
        await using (var connection = db.CreateConnection(id))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText = Seed;
            await seed.ExecuteNonQueryAsync();
        }

        // 升冪：alice(V2|1、V3|2 依 entry_id)、bob(V1|1、V2|2)、空值最後（V1|2、V3|1）。
        Assert.Equal(
            ["V2|1|alice", "V3|2|alice", "V1|1|bob", "V2|2|bob", "V1|2|∅", "V3|1|∅"],
            await WalkAsync(db, id, new PageSort("approvedBy", PageSortDirection.Ascending), null));

        // 降冪：bob 在前且同值時 entry_id 也降冪，空值仍在最後。
        Assert.Equal(
            ["V2|2|bob", "V1|1|bob", "V3|2|alice", "V2|1|alice", "V3|1|∅", "V1|2|∅"],
            await WalkAsync(db, id, new PageSort("approvedBy", PageSortDirection.Descending), null));

        // 不排序維持 entry_id 升冪；搜尋不分大小寫，只留 V2。
        Assert.Equal(
            ["V1|1|bob", "V1|2|∅", "V2|1|alice", "V2|2|bob", "V3|1|∅", "V3|2|alice"],
            await WalkAsync(db, id, null, null));
        Assert.Equal(["V2|1|alice", "V2|2|bob"], await WalkAsync(db, id, null, "v2"));
        Assert.Equal(["V2|2|bob", "V2|1|alice"], await WalkAsync(db, id, new PageSort("approvedBy", PageSortDirection.Descending), "V2"));
    }
}
