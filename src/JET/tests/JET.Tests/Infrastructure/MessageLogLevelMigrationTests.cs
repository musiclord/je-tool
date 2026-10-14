using System.Data.Common;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 使用者 2026-10-13 裁定訊息分三級（錯誤、需要注意、一般），訊息紀錄要能存 <c>error</c>。
/// 情境：審計員開啟改版前建立的案件（本機 schema 第 11 版，訊息紀錄只收 info 與 warn，裡面已有兩則訊息），
/// 開案後資料庫升到第 12 版，原本的訊息與等級都還在，之後的操作失敗可以寫成 error 並讀回。
/// 第 11 版的形狀由目前的建表結果降回去：只把訊息紀錄表換回當時的限制，其他表與現行相同。
/// </summary>
public sealed class MessageLogLevelMigrationTests
{
    [Fact]
    public Task Sqlite_V11CaseWithMessages_UpgradesToV12_KeepsMessages_AndAcceptsError() =>
        AssertUpgradeAsync("sqlite", "message_id INTEGER PRIMARY KEY AUTOINCREMENT");

    [Fact]
    public Task DuckDb_V11CaseWithMessages_UpgradesToV12_KeepsMessages_AndAcceptsError() =>
        AssertUpgradeAsync("duckdb", "message_id BIGINT PRIMARY KEY DEFAULT nextval('seq_app_message_log')");

    private static async Task AssertUpgradeAsync(string provider, string idColumn)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await Create(provider, folder).EnsureCreatedAsync(projectId, CancellationToken.None);

        // 降回第 11 版：訊息紀錄表回到只收 info 與 warn 的限制，再照改版前的方式寫入兩則訊息。
        var opened = Create(provider, folder);
        await using (var connection = opened.CreateConnection(projectId))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, "DROP TABLE app_message_log");
            await ExecuteAsync(connection, "CREATE TABLE app_message_log (" + idColumn + ", occurred_utc TEXT NOT NULL, "
                + "level TEXT NOT NULL CHECK (level IN ('info','warn')), text TEXT NOT NULL)");
            await ExecuteAsync(connection, "INSERT INTO app_message_log (occurred_utc, level, text) "
                + "VALUES ('2026-10-01T01:00:00.0000000+00:00', 'info', '已載入專案「合成案件」。')");
            await ExecuteAsync(connection, "INSERT INTO app_message_log (occurred_utc, level, text) "
                + "VALUES ('2026-10-01T01:01:00.0000000+00:00', 'warn', '匯入 GL失敗：合成原因，耗時 1,200 毫秒。')");
            await ExecuteAsync(connection, "UPDATE schema_info SET value = '11' WHERE key = 'schema_version'");

            // 確認這份案件真的是改版前的樣子：寫不進 error。
            await Assert.ThrowsAnyAsync<DbException>(() => ExecuteAsync(connection,
                "INSERT INTO app_message_log (occurred_utc, level, text) VALUES ('2026-10-01T01:02:00.0000000+00:00', 'error', 'x')"));
        }

        // 審計員開啟案件。
        var database = Create(provider, folder);
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);

        await using (var connection = database.CreateConnection(projectId))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM schema_info WHERE key = 'schema_version'";
            Assert.Equal("12", await command.ExecuteScalarAsync() as string);
        }

        var store = new LocalMessageLogStore(database);
        var before = await store.GetRecentAsync(projectId, 30, CancellationToken.None);
        Assert.Equal(
            new[] { ("warn", "匯入 GL失敗：合成原因，耗時 1,200 毫秒。"), ("info", "已載入專案「合成案件」。") },
            before.Select(entry => (entry.Level, entry.Text)).ToArray());

        await store.AppendAsync(projectId, "error", "產生工作底稿失敗：合成原因，耗時 4.2 秒。", CancellationToken.None);
        var after = await store.GetRecentAsync(projectId, 30, CancellationToken.None);
        Assert.Equal(
            new[]
            {
                ("error", "產生工作底稿失敗：合成原因，耗時 4.2 秒。"),
                ("warn", "匯入 GL失敗：合成原因，耗時 1,200 毫秒。"),
                ("info", "已載入專案「合成案件」。"),
            },
            after.Select(entry => (entry.Level, entry.Text)).ToArray());

        // 再開一次不重做升版，訊息不重複。
        await Create(provider, folder).EnsureCreatedAsync(projectId, CancellationToken.None);
        Assert.Equal(3, (await store.GetRecentAsync(projectId, 30, CancellationToken.None)).Count);
    }

    private static ILocalProjectDatabase Create(string provider, JetProjectFolder folder) =>
        provider == "sqlite" ? new SqliteProjectDatabase(folder) : new DuckDbProjectDatabase(folder);

    private static async Task ExecuteAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
