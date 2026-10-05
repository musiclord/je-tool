using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// DuckDB 案件在一次操作期間保持資料庫開啟：持有期間經 CreateConnection 開的連線沿用同一個資料庫實體，
/// 關掉那條連線後檔案仍被占用；釋放後才寫回並解除鎖定。看的是檔案的實際狀態，不看內部計數。
/// </summary>
public sealed class DuckDbDatabaseRetentionTests
{
    [Fact]
    public async Task Retain_KeepsDatabaseOpenAcrossConnections_AndReleaseUnlocksTheFile()
    {
        using var root = new TempProjectRoot();
        var database = new DuckDbProjectDatabase(new JetProjectFolder(root.Path));
        var projectId = "持有測試";
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        var path = database.GetDatabasePath(projectId);
        Assert.True(DuckDbFileProbe.CanOpenExclusively(path), "持有前檔案應可獨占開啟");

        var retention = database.TryRetain(projectId);
        Assert.NotNull(retention);

        await using (var connection = database.CreateConnection(projectId))
        {
            await connection.OpenAsync();
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO app_message_log (occurred_utc, level, text) VALUES ('2024-01-01T00:00:00Z', 'info', 'retained');";
            await insert.ExecuteNonQueryAsync();
        }

        // 寫入的那條連線已關閉，但持有中的資料庫實體還開著：尚未寫回，檔案仍被占用。
        Assert.True(File.Exists(path + ".wal"), "持有期間 .wal 應仍存在");
        Assert.False(DuckDbFileProbe.CanOpenExclusively(path), "持有期間檔案不應能獨占開啟");

        retention!.Dispose();

        Assert.False(File.Exists(path + ".wal"), "釋放後應已寫回，.wal 消失");
        Assert.True(DuckDbFileProbe.CanOpenExclusively(path), "釋放後檔案應可獨占開啟");

        // 重複釋放不出錯，也不影響之後的開啟。
        retention.Dispose();

        await using var verify = database.CreateConnection(projectId);
        await verify.OpenAsync();
        await using var count = verify.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM app_message_log WHERE text = 'retained';";
        Assert.Equal(1L, Convert.ToInt64(await count.ExecuteScalarAsync()));
    }

    [Fact]
    public void Retain_MissingDatabaseFile_ReturnsNullAndDoesNotCreateIt()
    {
        using var root = new TempProjectRoot();
        var database = new DuckDbProjectDatabase(new JetProjectFolder(root.Path));
        var projectId = "缺檔案件";
        var path = database.GetDatabasePath(projectId);

        Assert.Null(database.TryRetain(projectId));
        Assert.False(File.Exists(path), "缺檔時不得替案件建出新的 jet.duckdb");

        // 只有資料夾、沒有資料庫檔時也一樣不持有（SQLite 案件的資料夾就是這個樣子）。
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Assert.Null(database.TryRetain(projectId));
        Assert.False(File.Exists(path), "缺檔時不得替案件建出新的 jet.duckdb");
    }

    [Fact]
    public void Retain_InvalidProjectId_ReturnsNull()
    {
        using var root = new TempProjectRoot();
        var database = new DuckDbProjectDatabase(new JetProjectFolder(root.Path));

        Assert.Null(database.TryRetain(@"..\outside"));
    }

    [Fact]
    public async Task Delete_WhileRetained_IsRejectedAndKeepsTheFile_UntilEveryHolderReleases()
    {
        using var root = new TempProjectRoot();
        var database = new DuckDbProjectDatabase(new JetProjectFolder(root.Path));
        var projectId = "DeleteGuard";
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        var path = database.GetDatabasePath(projectId);

        var first = database.TryRetain(projectId);
        // 大小寫不同的同一路徑也算同一個持有對象（Windows 路徑大小寫不敏感）。
        var second = database.TryRetain("deleteguard");
        Assert.NotNull(first);
        Assert.NotNull(second);

        var whileBoth = await Assert.ThrowsAsync<JetActionException>(
            () => database.DeleteAsync(projectId, CancellationToken.None));
        Assert.Equal(JetErrorCodes.OperationInProgress, whileBoth.Code);
        Assert.Equal("案件資料庫還有作業在使用，請等作業完成後再刪除案件。", whileBoth.Message);
        Assert.True(File.Exists(path), "被擋下的刪除不得刪掉資料庫檔");

        first!.Dispose();
        var whileOne = await Assert.ThrowsAsync<JetActionException>(
            () => database.DeleteAsync(projectId, CancellationToken.None));
        Assert.Equal(JetErrorCodes.OperationInProgress, whileOne.Code);
        Assert.True(File.Exists(path), "還有持有者時不得刪掉資料庫檔");

        second!.Dispose();
        await database.DeleteAsync(projectId, CancellationToken.None);
        Assert.False(File.Exists(path), "全部釋放後刪除應成功");
        Assert.False(File.Exists(path + ".wal"));
    }
}

/// <summary>以 FileShare.None 試開檔案，判斷是否還有程式（含本程序的 DuckDB 實體）占用它。</summary>
internal static class DuckDbFileProbe
{
    public static bool CanOpenExclusively(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
