using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 控制面第四輪 §1 遷移驗收：一次性移除冗餘反查表 <c>dbo.project_schema_map</c>。
/// 全部 [SqlServerFact] 閘控（連 JET_Test，非 Express、≥ 2022）。
/// oracle：遷移規格——bootstrap 後 map 已 DROP、registry 完好、且刻意不把 map 資料回填 registry（無聲吞）；
/// 建案不再寫 map。<b>本測試是全套件唯一建立 project_schema_map 的地方</b>（生產碼已不建），
/// 故其他測試的 bootstrap 只會 DROP-if-exists（冪等 no-op），不會與本測試搶建。
/// </summary>
public sealed class SqlServerControlPlaneMigrationTests
{
    private const string SingleDb = "JET_Test";

    private static ProjectDocument Doc(string projectId) =>
        new(projectId, "MIG", "遷移測試", "op", "2024-01-01", "2024-12-31", null,
            ProjectDocument.DefaultMoneyScale, ProjectDocument.DefaultRoundingMode,
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            CurrentStep: 1, ProjectDocument.CurrentSchemaVersion, ProjectDocument.SqlServerDatabaseProvider);

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    [SqlServerFact]
    public async Task Bootstrap_WithPopulatedSchemaMap_DropsMap_KeepsRegistry_NoBackfill()
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null) { return; }

        var options = new SqlServerConnectionOptions(conn, SingleDb);
        var registry = new SqlServerProjectRegistry(options);

        var registeredId = Unique("已登記案");
        // map 的一列指向一個「未登記」的專案 id → 用來證明遷移不把 map 靜默回填 registry。
        var unregisteredMapId = Unique("map孤兒");
        var mapSchema = SqlServerProjectSchema.For(unregisteredMapId);

        try
        {
            // 先登記一個真專案（bootstrap 控制面表 + 一列 registry）。
            await registry.RegisterAsync(Doc(registeredId), Unique("user"), CancellationToken.None);

            // 手動建含資料的舊反查表（模擬遷移前殘留）。
            await PopulateSchemaMapAsync(conn, mapSchema, unregisteredMapId);

            // 觸發一次全新 bootstrap（新實例）→ EnsureControlPlaneSchema 應 DROP map。
            await new SqlServerProjectDatabase(options).EnsureDatabaseReadyAsync(CancellationToken.None);

            // map 表已被 DROP。
            Assert.Equal(0, await ScalarAsync(conn, "SELECT CASE WHEN OBJECT_ID(N'dbo.project_schema_map','U') IS NULL THEN 0 ELSE 1 END;"));
            // registry 完好：先前登記的真專案仍在。
            Assert.True(await registry.ExistsAsync(registeredId, CancellationToken.None));
            // 刻意不回填：map 裡那個未登記的 project_id 不因遷移而被寫進 registry（無聲吞）。
            Assert.False(await registry.ExistsAsync(unregisteredMapId, CancellationToken.None));
        }
        finally
        {
            await registry.UnregisterAsync(registeredId, CancellationToken.None);
        }
    }

    [SqlServerFact]
    public async Task EnsureCreated_AfterMigration_DoesNotRecreateSchemaMap()
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null) { return; }

        var options = new SqlServerConnectionOptions(conn, SingleDb);
        var database = new SqlServerProjectDatabase(options);
        var projectId = Unique("建案不寫map");

        try
        {
            await database.EnsureCreatedAsync(projectId, CancellationToken.None); // 建 schema
            Assert.Equal(1, await ScalarAsync(conn,
                $"SELECT CASE WHEN SCHEMA_ID(@s) IS NULL THEN 0 ELSE 1 END;",
                ("@s", SqlServerProjectSchema.For(projectId))));

            // 建案完成後,反查表不得被重建（schema → 專案反查改讀 registry.schema_name）。
            Assert.Equal(0, await ScalarAsync(conn,
                "SELECT CASE WHEN OBJECT_ID(N'dbo.project_schema_map','U') IS NULL THEN 0 ELSE 1 END;"));
        }
        finally
        {
            await database.DeleteAsync(projectId, CancellationToken.None);
        }
    }

    /// <summary>
    /// 建立含一列的舊反查表 <c>dbo.project_schema_map</c>。CREATE／INSERT 以 dynamic SQL 放在同一 transaction，
    /// 既避開「同 batch 編譯時表尚不存在」，也讓其他平行測試的 bootstrap DROP 等到 seed commit 後才執行；
    /// 有限次重試只吸收 deadlock／瞬時 SQL 競賽。
    /// </summary>
    private static async Task PopulateSchemaMapAsync(string baseConnectionString, string schema, string projectId)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await ExecuteAsync(baseConnectionString,
                    """
                    SET XACT_ABORT ON;
                    BEGIN TRANSACTION;
                    BEGIN TRY
                        IF OBJECT_ID(N'dbo.project_schema_map','U') IS NULL
                            EXEC(N'CREATE TABLE dbo.project_schema_map (
                                schema_name NVARCHAR(64) COLLATE Latin1_General_BIN2 PRIMARY KEY,
                                project_id  NVARCHAR(100) COLLATE Latin1_General_BIN2 NOT NULL
                            );');

                        EXEC sys.sp_executesql
                            N'INSERT INTO dbo.project_schema_map (schema_name, project_id)
                              VALUES (@schema, @project);',
                            N'@schema NVARCHAR(64), @project NVARCHAR(100)',
                            @schema = @s,
                            @project = @p;
                        COMMIT TRANSACTION;
                    END TRY
                    BEGIN CATCH
                        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
                        THROW;
                    END CATCH;
                    """,
                    ("@s", schema), ("@p", projectId));
                return;
            }
            catch (SqlException) when (attempt < 5)
            {
                await Task.Delay(50);
            }
        }
    }

    // ---- 對 JET_Test 的最小 raw helper（本檔驗的是 dbo 控制面表形狀,故直接下 SQL 而非經埠） ----

    private static string SingleDbConnectionString(string baseConnectionString) =>
        new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = SingleDb }.ConnectionString;

    private static async Task ExecuteAsync(
        string baseConnectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(SingleDbConnectionString(baseConnectionString));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(
        string baseConnectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(SingleDbConnectionString(baseConnectionString));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }
}
