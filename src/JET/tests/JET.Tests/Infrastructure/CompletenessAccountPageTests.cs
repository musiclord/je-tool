using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Application;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 完整性「全科目」keyset 分頁(<see cref="ICompletenessAccountPageRepository"/>,匯出底稿 step1 資料源)。
/// 與既有 <see cref="ICompletenessDiffPageRepository"/> 唯一差別:不過濾 diff≠0,故必含差異為 0 的科目。
///
/// oracle:獨立 SQL recount(<see cref="DemoProjectPipeline.QueryScalarAsync"/>),
/// 全科目數 = TB 科目 ∪ (GL 有 TB 無) 的基數;以小 pageSize 走訪驗「聯集==全集、無缺漏無重複、序穩」。
/// 直接建 SQLite repo(比照 <see cref="CreatorSummaryExportTests"/>);此 query 無 action,不走 dispatcher。
/// SQL Server parity 隨 Task 6 端到端匯出的 [SqlServerFact] 一併涵蓋(該 repo 無獨立 action 可走訪)。
/// </summary>
public sealed class CompletenessAccountPageTests
{
    private static ICompletenessAccountPageRepository SqliteRepo(HandlerTestHost host) =>
        new LocalCompletenessAccountPageRepository(new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot)));

    /// <summary>小 pageSize 走訪全科目:聯集 == 全集、無缺漏無重複、account_code 升冪穩定。</summary>
    [Fact]
    public async Task WalkAllPages_EqualsEveryAccount_IncludingZeroDiff()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        var all = new List<string>();
        string? cursor = null;
        do
        {
            var page = await SqliteRepo(host).GetPageAsync(
                context.ProjectId, moneyScale: 100, "2025-01-01", "2025-12-31",
                new PageRequest(cursor, PageSize: 50), CancellationToken.None);
            all.AddRange(page.Rows.Select(r => r.AccountCode));
            cursor = page.NextCursor;
        } while (cursor is not null);

        // 全科目 recount:TB 全科目 ∪ (GL 有、TB 無);不加 diff 過濾(對比 diff repo 的 COALESCE<>)。
        var total = await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId,
            "WITH gl AS (SELECT account_code, SUM(amount_scaled) s FROM target_gl_entry GROUP BY account_code), " +
            "tb AS (SELECT account_code, SUM(change_amount_scaled) s FROM target_tb_balance GROUP BY account_code) " +
            "SELECT COUNT(*) FROM (" +
            " SELECT t.account_code FROM tb t " +
            " UNION ALL " +
            " SELECT g.account_code FROM gl g LEFT JOIN tb ON tb.account_code=g.account_code " +
            "   WHERE tb.account_code IS NULL) x;");

        Assert.Equal(total, all.Count);
        Assert.Equal(all.Count, all.Distinct().Count()); // 無重複
        Assert.True(all.SequenceEqual(all.OrderBy(x => x, StringComparer.Ordinal))); // account_code 升冪穩定
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

        var allCodes = allRows.Select(r => r.AccountCode).ToHashSet(StringComparer.Ordinal);
        var diffCodes = diffRows.Select(r => r.AccountCode).ToHashSet(StringComparer.Ordinal);

        Assert.ProperSuperset(diffCodes, allCodes); // 全科目嚴格涵蓋差異科目(demo 含 diff=0 科目)
        // 多出來的(全科目 - 差異)必為 diff=0。
        var extra = allRows.Where(r => !diffCodes.Contains(r.AccountCode));
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
