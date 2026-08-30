using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// <see cref="SqlServerUserDirectory"/>（單庫使用者目錄 dbo.app_user）的 Infrastructure 層測試。
/// 除「未設定連線」一條為純 [Fact]（不需 live SQL）外，其餘皆 [SqlServerFact] 閘控（連 JET_Test，非 Express、≥ 2022）。
/// 隔離：每測試用帶 GUID 的唯一 principal／projectId（勿用真帳號名），故共用 dbo.app_user／dbo.project_access
/// 的跨測試殘留互不相干；finally 以原生 SQL 清除本測試種下的列。
/// oracle：spec §2（EnsureUser 冪等、發唯一編號、雛形裸名→合格名一次性 heal 並防撞主鍵）。
/// </summary>
public sealed class SqlServerUserDirectoryTests
{
    private const string SingleDb = "JET_Test";

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    /// <summary>取得指向 JET_Test 的使用者目錄；並先確保單庫存在（目錄於其上 bootstrap 自表）。</summary>
    private static async Task<Arranged?> TryArrangeAsync()
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null)
        {
            return null;
        }

        var options = new SqlServerConnectionOptions(conn, SingleDb);
        await new SqlServerProjectDatabase(options).EnsureDatabaseReadyAsync(CancellationToken.None);
        return new Arranged(new SqlServerUserDirectory(options), conn, options);
    }

    private sealed record Arranged(SqlServerUserDirectory Directory, string Conn, SqlServerConnectionOptions Options);

    // ① 同 principal 兩次 EnsureUser → 同一 user_id 且 app_user 僅一列（冪等）。
    [SqlServerFact]
    public async Task EnsureUser_SamePrincipalTwice_ReturnsSameIdAndSingleRow()
    {
        var a = await TryArrangeAsync();
        if (a is null) { return; }

        var principal = $"TESTDOM\\{Unique("same")}";
        try
        {
            var first = await a.Directory.EnsureUserAsync(principal, "same", CancellationToken.None);
            var second = await a.Directory.EnsureUserAsync(principal, "same", CancellationToken.None);

            Assert.Equal(first.UserId, second.UserId);
            Assert.Equal(principal, first.Principal);
            Assert.Equal(1, await CountAppUserAsync(a.Conn, principal)); // 冪等：只一列
        }
        finally
        {
            await DeleteAppUserAsync(a.Conn, principal);
        }
    }

    // ② 不同 principal → 編號互異。
    [SqlServerFact]
    public async Task EnsureUser_DifferentPrincipals_GetDistinctNumbers()
    {
        var a = await TryArrangeAsync();
        if (a is null) { return; }

        var alice = $"TESTDOM\\{Unique("alice")}";
        var bob = $"TESTDOM\\{Unique("bob")}";
        try
        {
            var ra = await a.Directory.EnsureUserAsync(alice, "alice", CancellationToken.None);
            var rb = await a.Directory.EnsureUserAsync(bob, "bob", CancellationToken.None);

            Assert.NotEqual(ra.UserId, rb.UserId);
        }
        finally
        {
            await DeleteAppUserAsync(a.Conn, alice);
            await DeleteAppUserAsync(a.Conn, bob);
        }
    }

    // ③ heal：先種一列 bare-principal 的 project_access，EnsureUser 合格名後該列升級為合格名。
    [SqlServerFact]
    public async Task EnsureUser_HealsBarePrincipalAccessRow_ToQualifiedName()
    {
        var a = await TryArrangeAsync();
        if (a is null) { return; }

        var bare = Unique("healbare");
        var qualified = $"TESTDOM\\{bare}";
        var projectId = Unique("healproj");
        await EnsureAccessTableAsync(a.Options);
        await SeedAccessAsync(a.Conn, projectId, bare);
        try
        {
            await a.Directory.EnsureUserAsync(qualified, bare, CancellationToken.None);

            var principals = await ReadAccessPrincipalsAsync(a.Conn, projectId);
            Assert.Single(principals);
            Assert.Equal(qualified, principals[0]); // 裸名列已被一次性收斂為合格名
        }
        finally
        {
            await DeleteAccessAsync(a.Conn, projectId);
            await DeleteAppUserAsync(a.Conn, qualified);
        }
    }

    // ④ heal 防撞：bare 與 qualified 兩列都在時不炸主鍵、bare 列保留（NOT EXISTS 令 UPDATE 跳過）。
    [SqlServerFact]
    public async Task EnsureUser_HealWithExistingQualifiedRow_DoesNotThrowAndKeepsBothRows()
    {
        var a = await TryArrangeAsync();
        if (a is null) { return; }

        var bare = Unique("collbare");
        var qualified = $"TESTDOM\\{bare}";
        var projectId = Unique("collproj");
        await EnsureAccessTableAsync(a.Options);
        await SeedAccessAsync(a.Conn, projectId, bare);      // 裸名列
        await SeedAccessAsync(a.Conn, projectId, qualified); // 同專案已有合格名列 → heal 須跳過以免撞 PK
        try
        {
            // 不得丟例外（輔助收斂不阻斷註冊；且 NOT EXISTS 防主鍵衝突）。
            await a.Directory.EnsureUserAsync(qualified, bare, CancellationToken.None);

            var principals = await ReadAccessPrincipalsAsync(a.Conn, projectId);
            Assert.Equal(2, principals.Count);
            Assert.Contains(bare, principals);      // 裸名列保留（未被誤 UPDATE）
            Assert.Contains(qualified, principals);
        }
        finally
        {
            await DeleteAccessAsync(a.Conn, projectId);
            await DeleteAppUserAsync(a.Conn, qualified);
        }
    }

    // ⑤ 連線未設定 → JetActionException code sql_server_not_configured（不需 live SQL，恆跑）。
    [Fact]
    public async Task EnsureUser_ConnectionNotConfigured_ThrowsSqlServerNotConfigured()
    {
        var directory = new SqlServerUserDirectory(new SqlServerConnectionOptions(null, SingleDb));

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => directory.EnsureUserAsync("TESTDOM\\nobody", "nobody", CancellationToken.None));

        Assert.Equal("sql_server_not_configured", ex.Code);
    }

    // ---- 原生 SQL 隔離／驗證 helpers（連 JET_Test；測試自管種列與清列）----

    private static async Task<SqlConnection> OpenAsync(string conn)
    {
        var connection = new SqlConnection(
            new SqlConnectionStringBuilder(conn) { InitialCatalog = SingleDb }.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<int> CountAppUserAsync(string conn, string principal)
    {
        await using var c = await OpenAsync(conn);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM dbo.app_user WHERE principal = @p;";
        cmd.Parameters.AddWithValue("@p", principal);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task DeleteAppUserAsync(string conn, string principal)
    {
        await using var c = await OpenAsync(conn);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM dbo.app_user WHERE principal = @p;";
        cmd.Parameters.AddWithValue("@p", principal);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>確保 dbo.project_access 已存在（由 registry bootstrap；heal 測試種列前必要）。</summary>
    private static async Task EnsureAccessTableAsync(SqlServerConnectionOptions options)
    {
        await new SqlServerProjectRegistry(options).ExistsAsync(Unique("noop"), CancellationToken.None);
    }

    private static async Task SeedAccessAsync(string conn, string projectId, string principal)
    {
        await using var c = await OpenAsync(conn);
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            "INSERT INTO dbo.project_access (project_id, principal, granted_utc) VALUES (@id, @p, SYSUTCDATETIME());";
        cmd.Parameters.AddWithValue("@id", projectId);
        cmd.Parameters.AddWithValue("@p", principal);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> ReadAccessPrincipalsAsync(string conn, string projectId)
    {
        await using var c = await OpenAsync(conn);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT principal FROM dbo.project_access WHERE project_id = @id;";
        cmd.Parameters.AddWithValue("@id", projectId);
        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    private static async Task DeleteAccessAsync(string conn, string projectId)
    {
        await using var c = await OpenAsync(conn);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM dbo.project_access WHERE project_id = @id;";
        cmd.Parameters.AddWithValue("@id", projectId);
        await cmd.ExecuteNonQueryAsync();
    }
}
