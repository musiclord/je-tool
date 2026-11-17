using System.Data.Common;
using JET.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 授權編製人員清單有幾人出現在 GL 的建立人員（matchedPreparerCount）。開案、確認 GL 配對與匯入清單時都會算。
/// 比對前建立人員要去掉前後空白再轉大寫；轉大寫用 JET 換上的 upper 函式，每呼叫一次就從 SQLite 回到 .NET 一次。
/// GL 列數加倍、建立人員還是同幾種寫法時，轉大寫的次數不應跟著增加，比對結果也不變。
///
/// 合成資料的建立人員只有幾種寫法：前後有空白、大小寫不同、空值，另有一人只出現在 GL(未納入測試)。
/// 清單四人裡，只有兩人出現在納入測試的分錄。
/// </summary>
public sealed class MatchedPreparerCountReadTests(ITestOutputHelper testOutput)
{
    [Fact]
    public async Task MatchedPreparerCount_WhenGlRowsDouble_NormalizesEachDistinctPreparerOnce()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        var counter = new UpperCallCounter();
        var repository = new LocalAuthorizedPreparerRepository(
            new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot), counter));

        await ReplaceGlAndListAsync(host, context.ProjectId, glRows: 1_000);
        var smaller = await repository.FindStateAsync(context.ProjectId, CancellationToken.None);
        var smallerCalls = counter.TakeCalls();

        await ReplaceGlAndListAsync(host, context.ProjectId, glRows: 2_000);
        var larger = await repository.FindStateAsync(context.ProjectId, CancellationToken.None);
        var largerCalls = counter.TakeCalls();

        var measured = $"GL 1,000 列時轉大寫 {smallerCalls} 次，2,000 列時 {largerCalls} 次。";
        testOutput.WriteLine(measured);
        Assert.Equal(2, smaller!.MatchedPreparerCount);
        Assert.Equal(2, larger!.MatchedPreparerCount);
        Assert.True(smallerCalls > 0, "量不到比對時轉大寫的次數。");
        Assert.True(largerCalls == smallerCalls, $"GL 列數加倍、建立人員不變時，轉大寫的次數不應增加。{measured}");
    }

    /// <summary>GL 換成 <paramref name="glRows"/> 列合成分錄，授權編製人員清單換成固定的四人。</summary>
    private static async Task ReplaceGlAndListAsync(HandlerTestHost host, string projectId, int glRows)
    {
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            DELETE FROM gl;
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {glRows})
            INSERT INTO gl (batch_id, source_row_number, voucher_number, gl_date, account_number, created_by,
                            amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr, is_excluded)
            SELECT 'synthetic', i, 'JV-' || ((i + 1) / 2), '2025-03-05', '1101',
                   CASE i % 5 WHEN 0 THEN ' Alice ' WHEN 1 THEN 'ALICE' WHEN 2 THEN 'bob' WHEN 3 THEN NULL ELSE 'Carol' END,
                   100, CASE i % 2 WHEN 0 THEN 100 ELSE 0 END, CASE i % 2 WHEN 0 THEN 0 ELSE 100 END,
                   CASE i % 2 WHEN 0 THEN 'DEBIT' ELSE 'CREDIT' END,
                   CASE i % 5 WHEN 4 THEN 1 ELSE 0 END
            FROM n;
            DELETE FROM authorized_preparer;
            INSERT INTO authorized_preparer (name) VALUES ('alice'), (' Bob'), ('carol'), ('dave');
            """;
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    /// <summary>
    /// 比對授權編製人員的語句執行前，在那條連線換上會計數的 upper，轉法和正式程式相同（ToUpperInvariant）。
    /// </summary>
    private sealed class UpperCallCounter : ISqlStatementObserver
    {
        private long _calls;

        public long TakeCalls() => Interlocked.Exchange(ref _calls, 0);

        public SqlStatementWatch? Begin(DbCommand command)
        {
            if (command.CommandText?.Contains("FROM authorized_preparer ap", StringComparison.Ordinal) == true
                && command.Connection is SqliteConnection connection)
            {
                connection.CreateFunction<string?, string?>(
                    "upper",
                    value =>
                    {
                        Interlocked.Increment(ref _calls);
                        return value?.ToUpperInvariant();
                    },
                    isDeterministic: true);
            }

            return null;
        }
    }
}
