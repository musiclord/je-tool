using JET.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// system.databaseInfo 容量欄（控制面第七輪 §2）的 live 驗收：<see cref="SqlServerHealthCheck.DescribeAsync"/>
/// （＝<c>ISqlServerBackendProbe</c> 的實作委派）回 <c>databaseSizeMb</c>／<c>schemaCount</c>。全部 [SqlServerFact] 閘控。
/// 為讓 <c>schemaCount</c> 的「隨建/刪案增減」可<b>確定性</b>斷言（不受共用 JET_Test 的並行測試干擾），本測試用一個
/// <b>專屬的一次性資料庫</b>（唯一命名、測試結束 DROP）——庫內 <c>prj_%</c> schema 數只受本測試控制，故建案 →＋1、
/// 刪案 →－1 可逐步精確斷言。
/// </summary>
public sealed class SqlServerDatabaseInfoCapacityTests
{
    [SqlServerFact]
    public async Task DatabaseInfo_CapacityFields_SchemaCountTracksProjectCreateAndDelete()
    {
        var baseConn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (baseConn is null) { return; }

        // 專屬一次性庫（唯一名、僅本測試碰）：確定性計數的關鍵。
        var dbName = $"JET_R7Cap_{Guid.NewGuid():N}";
        var options = new SqlServerConnectionOptions(baseConn, dbName);
        var database = new SqlServerProjectDatabase(options);
        var p1 = $"cap1-{Guid.NewGuid():N}";
        var p2 = $"cap2-{Guid.NewGuid():N}";

        try
        {
            // 庫尚未建立 → 容量欄為 0（探測連 master，reachable 仍 true）。
            var before = await SqlServerHealthCheck.DescribeAsync(baseConn, dbName, CancellationToken.None);
            Assert.True(before.Configured);
            Assert.True(before.Reachable);
            Assert.Equal(0, before.DatabaseSizeMb);
            Assert.Equal(0, before.SchemaCount);

            // 建第一案 → 順帶建庫＋控制面表＋prj_ schema。整庫大小 > 0、prj_ schema 計數 = 1（dbo 控制面表不計）。
            await database.EnsureCreatedAsync(p1, CancellationToken.None);
            var afterP1 = await SqlServerHealthCheck.DescribeAsync(baseConn, dbName, CancellationToken.None);
            Assert.True(afterP1.DatabaseSizeMb > 0);
            Assert.Equal(1, afterP1.SchemaCount);

            // 建第二案 → schemaCount ＋1 = 2。
            await database.EnsureCreatedAsync(p2, CancellationToken.None);
            var afterP2 = await SqlServerHealthCheck.DescribeAsync(baseConn, dbName, CancellationToken.None);
            Assert.Equal(2, afterP2.SchemaCount);

            // 刪第二案 → schemaCount －1 = 1。
            await database.DeleteAsync(p2, CancellationToken.None);
            var afterDelete = await SqlServerHealthCheck.DescribeAsync(baseConn, dbName, CancellationToken.None);
            Assert.Equal(1, afterDelete.SchemaCount);
        }
        finally
        {
            await DropDatabaseAsync(baseConn, dbName);
        }
    }

    /// <summary>
    /// 專屬一次性庫的兜底清理：先清連線池釋放對該庫的池化連線，再於 master 上以
    /// <c>SET SINGLE_USER WITH ROLLBACK IMMEDIATE</c> 踢掉殘留 session 後 <c>DROP DATABASE</c>。
    /// 庫名為本測試生成（固定前綴＋hex，非使用者輸入），內嵌識別字安全。清理失敗吞掉（唯一命名的殘庫可辨識、可手動清）。
    /// </summary>
    private static async Task DropDatabaseAsync(string baseConnectionString, string dbName)
    {
        try
        {
            SqlConnection.ClearAllPools();
            await using var master = new SqlConnection(
                new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = "master" }.ConnectionString);
            await master.OpenAsync();
            await using var cmd = master.CreateCommand();
            cmd.CommandText =
                $"IF DB_ID(N'{dbName}') IS NOT NULL " +
                "BEGIN " +
                $"  ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                $"  DROP DATABASE [{dbName}]; " +
                "END";
            await cmd.ExecuteNonQueryAsync();
        }
        catch (SqlException)
        {
            // 清理失敗不使測試失真：唯一命名的殘庫可手動清。
        }
    }
}
