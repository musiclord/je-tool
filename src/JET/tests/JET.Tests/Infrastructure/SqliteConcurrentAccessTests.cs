using System.Diagnostics;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 2026-10-05 最後獨立複審 V6：第 9 批把長作業移到背景後，其他要求可以同時讀案件資料庫。
/// SQLite 案件資料庫用 WAL；連線若共用快取，一條連線的寫入還沒提交時，別的連線讀同一張表要等到逾時才失敗。
/// 這裡要求讀取不等寫入，讀到的是上一次提交的資料；兩條寫入仍然依序進行，前一條提交後下一條就完成。
/// </summary>
public sealed class SqliteConcurrentAccessTests
{
    private const string ProjectId = "sqlite-concurrent-access";

    private static async Task<SqliteProjectDatabase> CreateAsync(TempProjectRoot root)
    {
        var folder = new JetProjectFolder(root.Path);
        var db = new SqliteProjectDatabase(folder);
        Directory.CreateDirectory(folder.GetProjectDirectory(ProjectId));
        await db.EnsureCreatedAsync(ProjectId, CancellationToken.None);
        await using var connection = db.CreateConnection(ProjectId);
        await connection.OpenAsync();
        await using var seed = connection.CreateCommand();
        seed.CommandText =
            "INSERT INTO target_tb_balance (batch_id, source_row_number, account_code, account_name, change_amount_scaled) " +
            "VALUES ('committed', 1, '1101', '現金', 100);";
        await seed.ExecuteNonQueryAsync();
        return db;
    }

    [Fact]
    public async Task ReadOnAnotherConnection_DoesNotWaitForUncommittedWrite_AndSeesLastCommittedRows()
    {
        using var root = new TempProjectRoot();
        var db = await CreateAsync(root);

        await using var writer = db.CreateConnection(ProjectId);
        await writer.OpenAsync();
        await using var transaction = await writer.BeginTransactionAsync();
        await using (var insert = writer.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO target_tb_balance (batch_id, source_row_number, account_code, account_name, change_amount_scaled) " +
                "VALUES ('pending', 1, '2101', '應付帳款', -100);";
            await insert.ExecuteNonQueryAsync();
        }

        await using var reader = db.CreateConnection(ProjectId);
        await reader.OpenAsync();
        await using var count = reader.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM target_tb_balance;";
        count.CommandTimeout = 5;
        var watch = Stopwatch.StartNew();
        var rows = Convert.ToInt64(await count.ExecuteScalarAsync());
        watch.Stop();

        Assert.Equal(1L, rows);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"讀取等了 {watch.Elapsed.TotalSeconds:F1} 秒。");
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task SecondWrite_WaitsForTheFirstToCommit_ThenSucceeds()
    {
        using var root = new TempProjectRoot();
        var db = await CreateAsync(root);

        await using var first = db.CreateConnection(ProjectId);
        await first.OpenAsync();
        var transaction = await first.BeginTransactionAsync();
        await using (var insert = first.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO target_tb_balance (batch_id, source_row_number, account_code, account_name, change_amount_scaled) " +
                "VALUES ('first', 1, '2101', '應付帳款', -100);";
            await insert.ExecuteNonQueryAsync();
        }

        var second = Task.Run(async () =>
        {
            await using var connection = db.CreateConnection(ProjectId);
            await connection.OpenAsync();
            await using var append = connection.CreateCommand();
            append.CommandText =
                "INSERT INTO app_message_log (occurred_utc, level, text) VALUES ('2026-10-05T00:00:00Z', 'info', '合成訊息');";
            append.CommandTimeout = 10;
            await append.ExecuteNonQueryAsync();
        });

        await Task.Delay(300);
        Assert.False(second.IsCompleted);
        await transaction.CommitAsync();
        await transaction.DisposeAsync();
        await second.WaitAsync(TimeSpan.FromSeconds(10));

        await using var check = first.CreateCommand();
        check.CommandText = "SELECT (SELECT COUNT(*) FROM target_tb_balance) * 10 + (SELECT COUNT(*) FROM app_message_log);";
        Assert.Equal(21L, Convert.ToInt64(await check.ExecuteScalarAsync()));
    }
}
