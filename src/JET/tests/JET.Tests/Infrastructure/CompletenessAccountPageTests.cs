using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Application;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 完整性「全科目」keyset 分頁(<see cref="ICompletenessAccountPageRepository"/>,匯出底稿 step1 資料源)。
/// 與既有 <see cref="ICompletenessDiffPageRepository"/> 共用科目集合；全科目表不過濾差異，名稱只取 TB。
/// 差異表仍可由 GL 補上 TB 缺少的名稱。
///
/// oracle:獨立 SQL recount(<see cref="DemoProjectPipeline.QueryScalarAsync"/>),
/// 全科目數 = TB 科目 ∪ (GL 有 TB 無) 的基數;以小 pageSize 走訪驗「聯集==全集、無缺漏無重複、序穩」。
/// 直接建 SQLite repo(比照 <see cref="PreparerSummaryExportTests"/>);此 query 無 action,不走 dispatcher。
/// SQL Server parity 隨 Task 6 端到端匯出的 [SqlServerFact] 一併涵蓋(該 repo 無獨立 action 可走訪)。
/// </summary>
public sealed class CompletenessAccountPageTests
{
    private static ICompletenessAccountPageRepository SqliteRepo(HandlerTestHost host) =>
        new LocalCompletenessAccountPageRepository(new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot)));

    /// <summary>小 pageSize 走訪全科目:聯集 == 全集、無缺漏無重複、account_number 升冪穩定。</summary>
    [Fact]
    public async Task WalkAllPages_EqualsEveryAccount_IncludingZeroDiff()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        // V5 與底稿 step1 的名稱來自 TB；差異頁仍可由 GL 補上缺少的名稱。
        // 全部為合成資料，另外加入足夠的零差異科目，讓既有 50 筆分頁確實跨頁。
        // 首次失敗：Focused 20261110-061640554-bf7ece0a2a3348909ca7874bf8ab5a73；TB 名稱為 null 時原本帶入 GL 名稱。
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        await using (var connection = database.CreateConnection(context.ProjectId))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO gl
                    (batch_id, source_row_number, voucher_number, gl_date, account_number, account_name,
                     amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr, is_excluded)
                VALUES
                    ('name-source-fixture', 1, 'name-source-1', '2025-01-15', 'NAME-TB-FILLED', 'GL filled name', 1000, 1000, 0, 'DEBIT', 0),
                    ('name-source-fixture', 2, 'name-source-2', '2025-01-15', 'NAME-TB-NULL', 'GL null fallback', 2000, 2000, 0, 'DEBIT', 0),
                    ('name-source-fixture', 3, 'name-source-3', '2025-01-15', 'NAME-TB-EMPTY', 'GL empty fallback', 3000, 3000, 0, 'DEBIT', 0),
                    ('name-source-fixture', 4, 'name-source-4', '2025-01-15', 'NAME-GL-ONLY', 'GL only name', 4000, 4000, 0, 'DEBIT', 0);
                INSERT INTO tb (batch_id, source_row_number, account_number, account_name, change_amount_scaled)
                VALUES
                    ('name-source-fixture', 1, 'NAME-TB-FILLED', 'TB filled name', 800),
                    ('name-source-fixture', 2, 'NAME-TB-NULL', NULL, 0),
                    ('name-source-fixture', 3, 'NAME-TB-EMPTY', '', 0),
                    ('name-source-fixture', 4, NULL, 'TB blank code', 100);
                WITH RECURSIVE synthetic_accounts(n) AS (
                    SELECT 1 UNION ALL SELECT n + 1 FROM synthetic_accounts WHERE n < 60
                )
                INSERT INTO tb (batch_id, source_row_number, account_number, account_name, change_amount_scaled)
                SELECT 'name-source-fixture', n + 4, 'NAME-TB-SEED-' || printf('%03d', n), 'TB seed name', 0
                FROM synthetic_accounts;
                """;
            await seed.ExecuteNonQueryAsync();
        }

        var all = new List<string>();
        var allRows = new List<CompletenessDiffAccount>();
        string? cursor = null;
        do
        {
            var page = await SqliteRepo(host).GetPageAsync(
                context.ProjectId, moneyScale: 100, "2025-01-01", "2025-12-31",
                new PageRequest(cursor, PageSize: 50), CancellationToken.None);
            all.AddRange(page.Rows.Select(r => r.AccountNumber));
            allRows.AddRange(page.Rows);
            cursor = page.NextCursor;
        } while (cursor is not null);

        // 全科目 recount:TB 全科目 ∪ (GL 有、TB 無);不加 diff 過濾(對比 diff repo 的 COALESCE<>)。
        var total = await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId,
            "WITH gl_by_account AS (SELECT account_number, SUM(amount_scaled) s FROM gl GROUP BY account_number), " +
            "tb_by_account AS (SELECT account_number, SUM(change_amount_scaled) s FROM tb GROUP BY account_number) " +
            "SELECT COUNT(*) FROM (" +
            " SELECT t.account_number FROM tb_by_account t " +
            " UNION ALL " +
            " SELECT g.account_number FROM gl_by_account g LEFT JOIN tb_by_account t ON t.account_number=g.account_number " +
            "   WHERE t.account_number IS NULL) x;");

        Assert.Equal(total, all.Count);
        Assert.Equal(all.Count, all.Distinct().Count()); // 無重複
        Assert.True(all.SequenceEqual(all.OrderBy(x => x, StringComparer.Ordinal))); // account_number 升冪穩定

        Assert.True(all.Count > 50);
        Assert.Equal(string.Empty, all[0]);
        Assert.Equal("TB blank code", allRows[0].AccountName);
        Assert.Equal("TB filled name", allRows.Single(r => r.AccountNumber == "NAME-TB-FILLED").AccountName);
        Assert.Null(allRows.Single(r => r.AccountNumber == "NAME-TB-NULL").AccountName);
        Assert.Equal(string.Empty, allRows.Single(r => r.AccountNumber == "NAME-TB-EMPTY").AccountName);
        var glOnly = allRows.Single(r => r.AccountNumber == "NAME-GL-ONLY");
        Assert.True(glOnly.NotInTb);
        Assert.Null(glOnly.AccountName);

        var differences = await DrainDiffAsync(new LocalCompletenessDiffPageRepository(database), context.ProjectId);
        Assert.Equal("TB filled name", differences.Single(r => r.AccountNumber == "NAME-TB-FILLED").AccountName);
        Assert.Equal("GL null fallback", differences.Single(r => r.AccountNumber == "NAME-TB-NULL").AccountName);
        Assert.Equal(string.Empty, differences.Single(r => r.AccountNumber == "NAME-TB-EMPTY").AccountName);
        Assert.Equal("GL only name", differences.Single(r => r.AccountNumber == "NAME-GL-ONLY").AccountName);
    }

    /// <summary>
    /// 全科目集合 ⊋ 僅差異集合:全科目嚴格涵蓋 diff repo 的結果,且多出來的全是 diff=0 科目。
    /// 這鎖住「不過濾 diff」這個唯一差異(若誤加 tb_s<>gl_s 過濾,兩集合相等 → 紅)。
    /// </summary>
    [Fact]
    public async Task AllAccounts_StrictlySupersetOfDiffOnly_ExtraAreZeroDiff()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        var allRows = await DrainAsync(SqliteRepo(host), context.ProjectId);
        var diffRepo = new LocalCompletenessDiffPageRepository(
            new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot)));
        var diffRows = await DrainDiffAsync(diffRepo, context.ProjectId);

        var allCodes = allRows.Select(r => r.AccountNumber).ToHashSet(StringComparer.Ordinal);
        var diffCodes = diffRows.Select(r => r.AccountNumber).ToHashSet(StringComparer.Ordinal);

        Assert.ProperSuperset(diffCodes, allCodes); // 全科目嚴格涵蓋差異科目(demo 含 diff=0 科目)
        // 多出來的(全科目 - 差異)必為 diff=0。
        var extra = allRows.Where(r => !diffCodes.Contains(r.AccountNumber));
        Assert.All(extra, r => Assert.Equal(0, r.DiffScaled));
    }

    private static async Task<List<CompletenessDiffAccount>> DrainAsync(
        ICompletenessAccountPageRepository repo, string projectId)
    {
        var rows = new List<CompletenessDiffAccount>();
        string? cursor = null;
        do
        {
            var page = await repo.GetPageAsync(projectId, 100, "2025-01-01", "2025-12-31", new PageRequest(cursor, 200), CancellationToken.None);
            rows.AddRange(page.Rows);
            cursor = page.NextCursor;
        } while (cursor is not null);

        return rows;
    }

    private static async Task<List<CompletenessDiffAccount>> DrainDiffAsync(
        ICompletenessDiffPageRepository repo, string projectId)
    {
        var rows = new List<CompletenessDiffAccount>();
        string? cursor = null;
        do
        {
            var page = await repo.GetPageAsync(projectId, 100, "2025-01-01", "2025-12-31", new PageRequest(cursor, 200), CancellationToken.None);
            rows.AddRange(page.Rows);
            cursor = page.NextCursor;
        } while (cursor is not null);

        return rows;
    }
}
