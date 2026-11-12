using System.Data.Common;
using JET.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// SQLite 的取消接在每一步執行上（見 CancellableSqliteCommand 與 CancellableSqliteDataReader）。這裡核對幾個時間點剛好落在
/// 一步結束時的情形：取消在這一步算完、列已經出來時才送達，以及讀到後面某一列時發生錯誤。
/// 用一個在 SQL 裡執行時就按下取消的函式，讓時間點固定，不靠等待。
/// </summary>
public sealed class SqliteCancellationBoundaryTests
{
    // 一到二的兩列；第二列算出來時才呼叫指定的函式，第一列不呼叫。函式放在 ELSE：THEN 分支結束時有一個跳躍，
    // SQLite 在跳躍時檢查中斷，放在 THEN 會在交出這一列之前就中止，測不到「列已經出來才收到取消」。
    private const string TwoRowsCallingOnTheSecond =
        "WITH RECURSIVE r(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM r WHERE x < 2) "
        + "SELECT CASE WHEN x = 1 THEN 0 ELSE {0} END FROM r;";

    [Fact]
    public async Task CancelArrivingAsTheFirstRowIsReady_ReportsCancellation_AndTheConnectionStaysUsable()
    {
        using var root = new TempProjectRoot();
        await using var connection = await OpenAsync(root);
        using var cancellation = new CancellationTokenSource();
        connection.CreateFunction("cancel_now", () => { cancellation.Cancel(); return 1L; });
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT cancel_now();";
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => ((DbCommand)command).ExecuteReaderAsync(cancellation.Token));
        }

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT CAST(1 AS BIGINT);"));
    }

    [Fact]
    public async Task CancelArrivingAsALaterRowIsReady_ReportsCancellation_AndTheConnectionStaysUsable()
    {
        using var root = new TempProjectRoot();
        await using var connection = await OpenAsync(root);
        using var cancellation = new CancellationTokenSource();
        connection.CreateFunction("cancel_now", () => { cancellation.Cancel(); return 2L; });
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = string.Format(TwoRowsCallingOnTheSecond, "cancel_now()");
            await using var reader = await ((DbCommand)command).ExecuteReaderAsync(cancellation.Token);
            Assert.True(await reader.ReadAsync(cancellation.Token));
            Assert.Equal(0L, reader.GetInt64(0));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(cancellation.Token));
        }

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT CAST(1 AS BIGINT);"));
    }

    [Fact]
    public async Task ErrorWhileReadingALaterRow_ReachesTheCaller_AndTheConnectionStaysUsable()
    {
        using var root = new TempProjectRoot();
        await using var connection = await OpenAsync(root);
        await using (var command = connection.CreateCommand())
        {
            // abs 的參數是最小的 64 位元整數時，SQLite 回報整數溢位；參數跟著 x 變，SQLite 不會提前算，只在第二列發生。
            command.CommandText = string.Format(TwoRowsCallingOnTheSecond, "abs(-9223372036854775806 - x)");
            await using var reader = await ((DbCommand)command).ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await reader.ReadAsync(CancellationToken.None));

            var error = await Assert.ThrowsAsync<SqliteException>(() => reader.ReadAsync(CancellationToken.None));
            Assert.Equal(1, error.SqliteErrorCode);
        }

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT CAST(1 AS BIGINT);"));
    }

    [Fact]
    public async Task ReadingSeveralResults_MovesToTheNextResult_ThroughTheCancellableReader()
    {
        using var root = new TempProjectRoot();
        await using var connection = await OpenAsync(root);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(1 AS BIGINT); SELECT CAST(2 AS BIGINT);";
        await using var reader = await ((DbCommand)command).ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.True(await reader.NextResultAsync(CancellationToken.None));
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(2L, reader.GetInt64(0));
        Assert.False(await reader.NextResultAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ScalarWithAnAlreadyCancelledToken_DoesNotRun()
    {
        using var root = new TempProjectRoot();
        await using var connection = await OpenAsync(root);
        var calls = 0;
        connection.CreateFunction("count_call", () => ++calls);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count_call();";
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ((DbCommand)command).ExecuteScalarAsync(cancellation.Token));
        Assert.Equal(0, calls);
    }

    private static async Task<SqliteConnection> OpenAsync(TempProjectRoot root)
    {
        var folder = new JetProjectFolder(root.Path);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        var connection = (SqliteConnection)new SqliteProjectDatabase(folder).CreateConnection(projectId);
        await connection.OpenAsync(CancellationToken.None);
        return connection;
    }

    private static async Task<object?> ScalarAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(CancellationToken.None);
    }
}
