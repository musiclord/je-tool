using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 完整性全科目表與差異表一個查詢讀完時，讀出的科目、名稱、金額與順序，要和畫面用的分頁查詢逐頁讀完相同。
/// 合成資料的寫入順序和科目編號順序不同，另有試算表與總帳各一列空白科目、只在總帳的科目、只在試算表的科目、
/// 差異為 0 的科目、試算表名稱空白的科目，以及一列無效分錄。預期值由下面的資料直接算出：
///
///   試算表空白科目：TB 100、GL 0，差異 100      總帳空白科目：TB 0、GL 50，差異 -50，只在總帳
///   A100：TB 500、GL 200 + 100 = 300（無效分錄 999 不算），差異 200
///   A200：TB 300、GL 300，差異 0                 A300：TB 0、GL 0，差異 0，試算表沒有名稱
///   B500：TB 0、GL 900，差異 -900，只在總帳     Z900：TB 700、GL 0，差異 700，只在試算表
///
/// 全科目表的名稱只取試算表；差異表在試算表沒有名稱時用總帳的名稱。
/// </summary>
public sealed class CompletenessAccountStreamTests
{
    private const string FixtureSql =
        """
        INSERT INTO tb (batch_id, source_row_number, account_number, account_name, change_amount_scaled)
        VALUES
            ('tb1', 1, NULL, 'TB 空白科目', 100),
            ('tb1', 2, 'Z900', 'TB 獨有', 700),
            ('tb1', 3, 'A200', '零差異', 300),
            ('tb1', 4, 'A100', '有差異', 500),
            ('tb1', 5, 'A300', NULL, 0);
        INSERT INTO gl
            (batch_id, source_row_number, voucher_number, gl_date, account_number, account_name,
             amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr, is_excluded)
        VALUES
            ('gl1', 1, 'V1', '2025-03-05', 'B500', 'GL 獨有', 900, 900, 0, 'DEBIT', 0),
            ('gl1', 2, 'V1', '2025-03-05', 'A100', '有差異', 200, 200, 0, 'DEBIT', 0),
            ('gl1', 3, 'V2', '2025-03-05', NULL, 'GL 空白科目', 50, 50, 0, 'DEBIT', 0),
            ('gl1', 4, 'V2', '2025-03-05', 'A200', '零差異', 300, 300, 0, 'DEBIT', 0),
            ('gl1', 5, 'V3', '2025-03-05', 'A100', '有差異', 100, 100, 0, 'DEBIT', 0),
            ('gl1', 6, 'V3', '2025-03-05', 'A100', '有差異', 999, 999, 0, 'DEBIT', 1),
            ('gl1', 7, 'V4', '2025-03-05', 'A300', 'GL 名稱', 0, 0, 0, 'DEBIT', 0);
        """;

    private static readonly CompletenessDiffAccount[] ExpectedAccounts =
    [
        new(string.Empty, "TB 空白科目", 100, 0, 100, false),
        new(string.Empty, null, 0, 50, -50, true),
        new("A100", "有差異", 500, 300, 200, false),
        new("A200", "零差異", 300, 300, 0, false),
        new("A300", null, 0, 0, 0, false),
        new("B500", null, 0, 900, -900, true),
        new("Z900", "TB 獨有", 700, 0, 700, false)
    ];

    private static readonly CompletenessDiffAccount[] ExpectedDifferences =
    [
        new(string.Empty, "TB 空白科目", 100, 0, 100, false),
        new(string.Empty, "GL 空白科目", 0, 50, -50, true),
        new("A100", "有差異", 500, 300, 200, false),
        new("B500", "GL 獨有", 0, 900, -900, true),
        new("Z900", "TB 獨有", 700, 0, 700, false)
    ];

    [Fact]
    public async Task AccountStream_ReadsEveryAccountInTheFixedOrder_SameAsWalkingEveryPage()
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await SetupAsync(root);
        var repository = new LocalCompletenessAccountPageRepository(database);

        var streamed = await ReadAllAsync(
            ((ICompletenessAccountStreamRepository)repository).StreamAsync(projectId, CancellationToken.None));

        Assert.Equal(ExpectedAccounts, streamed);
        Assert.Equal(
            await WalkEveryPageAsync(
                (request, ct) => repository.GetPageAsync(projectId, 10_000, "2025-01-01", "2025-12-31", request, ct)),
            streamed);
    }

    [Fact]
    public async Task DifferenceStream_ReadsOnlyAccountsWithDifferences_SameAsWalkingEveryPage()
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await SetupAsync(root);
        var repository = new LocalCompletenessDiffPageRepository(database);

        var streamed = await ReadAllAsync(
            ((ICompletenessDiffStreamRepository)repository).StreamAsync(projectId, CancellationToken.None));

        Assert.Equal(ExpectedDifferences, streamed);
        Assert.Equal(
            await WalkEveryPageAsync(
                (request, ct) => repository.GetPageAsync(projectId, 10_000, "2025-01-01", "2025-12-31", request, ct)),
            streamed);
    }

    private static async Task<(ILocalProjectDatabase Database, string ProjectId)> SetupAsync(TempProjectRoot root)
    {
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = FixtureSql;
        await command.ExecuteNonQueryAsync();
        return (database, projectId);
    }

    private static async Task<List<CompletenessDiffAccount>> ReadAllAsync(IAsyncEnumerable<CompletenessDiffAccount> rows)
    {
        var all = new List<CompletenessDiffAccount>();
        await foreach (var row in rows)
        {
            all.Add(row);
        }

        return all;
    }

    /// <summary>畫面的分頁查詢沒有指定排序與搜尋，每頁 2 筆逐頁讀到底。</summary>
    private static async Task<List<CompletenessDiffAccount>> WalkEveryPageAsync(
        Func<PageRequest, CancellationToken, Task<PageResult<CompletenessDiffAccount>>> readPage)
    {
        var walked = new List<CompletenessDiffAccount>();
        string? cursor = null;
        do
        {
            var page = await readPage(new PageRequest(cursor, 2), CancellationToken.None);
            walked.AddRange(page.Rows);
            cursor = page.NextCursor;
        } while (cursor is not null);
        return walked;
    }
}
