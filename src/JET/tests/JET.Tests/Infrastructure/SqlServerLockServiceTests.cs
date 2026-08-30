using System.Data;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 專案租約鎖 <see cref="SqlServerLockService"/> 的 Infrastructure 層驗收。
/// 現行語意見 docs/jet-guide.md 的「專案鎖」章節。
/// 全部 [SqlServerFact] 閘控（連 JET_Test，非 Express、≥ 2022）。隔離：每測試唯一 projectId（GUID）＋唯一 principal，
/// finally 直接 DELETE 該 project_id 的鎖列（`dbo.project_lock` 跨測試殘留互不干擾）。
/// oracle：租約鎖語意規格（空表插入／新鮮他人 Held／過期接管／自己續租／HOLDLOCK 原子性／續租只動自己／釋放只刪自己）。
/// 逾時以 stub app_config 注入（不污染共用 JET_Test 的 dbo.app_config）；「過期」以直接回填 heartbeat_utc 確定性製造（免 sleep）。
/// </summary>
public sealed class SqlServerLockServiceTests
{
    private const string SingleDb = "JET_Test";

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    /// <summary>逾時秒數可注入的 app_config stub（回 null＝缺鍵，交由 ProjectLockDefaults 落程式常數）。</summary>
    private sealed class StubAppConfig(int? timeoutSeconds = null, int? heartbeatSeconds = null) : IAppConfigStore
    {
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken)
        {
            string? value = key switch
            {
                ProjectLockDefaults.TimeoutSecondsKey => timeoutSeconds?.ToString(),
                ProjectLockDefaults.HeartbeatSecondsKey => heartbeatSeconds?.ToString(),
                _ => null,
            };
            return Task.FromResult(value);
        }

        public Task SetAsync(string key, string valueJson, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>取指向 JET_Test 的 lock service；先確保單庫＋控制面表（含 project_lock）就位。</summary>
    private static async Task<(SqlServerConnectionOptions Options, SqlServerLockService Lock, string Conn)?> TryArrangeAsync(
        int? timeoutSeconds = null)
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null)
        {
            return null;
        }

        var options = new SqlServerConnectionOptions(conn, SingleDb);
        await new SqlServerProjectDatabase(options).EnsureDatabaseReadyAsync(CancellationToken.None);
        return (options, new SqlServerLockService(options, new StubAppConfig(timeoutSeconds)), conn);
    }

    [SqlServerFact]
    public async Task Acquire_EmptyTable_InsertsAndReturnsAcquired()
    {
        var arranged = await TryArrangeAsync();
        if (arranged is null) { return; }
        var (_, lockService, conn) = arranged.Value;

        var projectId = Unique("空表取鎖");
        var principal = Unique("user");
        try
        {
            var outcome = await lockService.AcquireAsync(projectId, principal, CancellationToken.None);

            Assert.IsType<LockOutcome.Acquired>(outcome);
            Assert.Equal(1, await CountRowsAsync(conn, projectId)); // 插入一列
        }
        finally
        {
            await DeleteRowAsync(conn, projectId);
        }
    }

    [SqlServerFact]
    public async Task Acquire_OtherHoldsFresh_ReturnsHeldWithHolderInfo()
    {
        var arranged = await TryArrangeAsync();
        if (arranged is null) { return; }
        var (options, lockA, conn) = arranged.Value;

        var projectId = Unique("他人持鎖");
        var owner = Unique("owner");
        var stranger = Unique("stranger");
        var lockB = new SqlServerLockService(options, new StubAppConfig());
        try
        {
            Assert.IsType<LockOutcome.Acquired>(await lockA.AcquireAsync(projectId, owner, CancellationToken.None));

            // 新鮮的他人租約（heartbeat 剛寫）→ Held，附持鎖者資訊。
            var outcome = await lockB.AcquireAsync(projectId, stranger, CancellationToken.None);

            var held = Assert.IsType<LockOutcome.Held>(outcome);
            Assert.Equal(owner, held.LockedBy);
            Assert.False(string.IsNullOrEmpty(held.MachineName)); // HOST_NAME() 非空
            // 未奪鎖：資料表持鎖者仍是 owner。
            Assert.Equal(owner, await ReadLockedByAsync(conn, projectId));
        }
        finally
        {
            await DeleteRowAsync(conn, projectId);
        }
    }

    [SqlServerFact]
    public async Task Acquire_OtherHoldsExpired_TakesOverReturnsAcquired()
    {
        var arranged = await TryArrangeAsync();
        if (arranged is null) { return; }
        var (options, lockA, conn) = arranged.Value;

        var projectId = Unique("過期接管");
        var crashed = Unique("crashed");
        var newcomer = Unique("newcomer");
        var lockB = new SqlServerLockService(options, new StubAppConfig());
        try
        {
            await lockA.AcquireAsync(projectId, crashed, CancellationToken.None);
            // 確定性製造「過期」：把 heartbeat 回填到 300 秒前（> 預設逾時 120）——等價於持有人崩潰未心跳。
            await BackdateHeartbeatAsync(conn, projectId, 300);

            var outcome = await lockB.AcquireAsync(projectId, newcomer, CancellationToken.None);

            Assert.IsType<LockOutcome.Acquired>(outcome); // 接管成功
            Assert.Equal(newcomer, await ReadLockedByAsync(conn, projectId)); // 持鎖者已換人
        }
        finally
        {
            await DeleteRowAsync(conn, projectId);
        }
    }

    [SqlServerFact]
    public async Task Acquire_SelfHolds_RenewsReturnsAcquired()
    {
        var arranged = await TryArrangeAsync();
        if (arranged is null) { return; }
        var (_, lockService, conn) = arranged.Value;

        var projectId = Unique("自己續租");
        var principal = Unique("user");
        try
        {
            var first = Assert.IsType<LockOutcome.Acquired>(
                await lockService.AcquireAsync(projectId, principal, CancellationToken.None));
            // 自己再取一次 → 續租、仍 Acquired，不重複列。
            var renewed = Assert.IsType<LockOutcome.Acquired>(
                await lockService.AcquireAsync(projectId, principal, CancellationToken.None));
            Assert.True(first.NewlyAcquired);
            Assert.False(renewed.NewlyAcquired);
            Assert.Equal(1, await CountRowsAsync(conn, projectId));
        }
        finally
        {
            await DeleteRowAsync(conn, projectId);
        }
    }

    /// <summary>
    /// HOLDLOCK 原子性（§9 ⑤）：兩個不同 principal 對同一空鍵並行 AcquireAsync，恰一 Acquired、一 Held、無例外。
    /// 確定性來自不變式——HOLDLOCK 讓 MERGE 對該鍵範圍序列化，故「先寫者插入＝Acquired、後到者見新鮮他人鎖＝Held」
    /// 對任意交錯皆成立（若拿掉 HOLDLOCK，兩者可能都走 NOT MATCHED→INSERT 而撞 PK 2627）。以共同閘門讓兩者同時起跑、
    /// 各自獨立 SqlServerLockService（＝兩 app 實例）真並行，斷言與時序無關。
    /// </summary>
    [SqlServerFact]
    public async Task Acquire_TwoConcurrent_ExactlyOneAcquired()
    {
        var arranged = await TryArrangeAsync();
        if (arranged is null) { return; }
        var (options, _, conn) = arranged.Value;

        var projectId = Unique("原子取鎖");
        var userA = Unique("A");
        var userB = Unique("B");
        var lockA = new SqlServerLockService(options, new StubAppConfig());
        var lockB = new SqlServerLockService(options, new StubAppConfig());
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            async Task<LockOutcome> AcquireAfterGate(SqlServerLockService svc, string principal)
            {
                await gate.Task;
                return await svc.AcquireAsync(projectId, principal, CancellationToken.None);
            }

            var t1 = Task.Run(() => AcquireAfterGate(lockA, userA));
            var t2 = Task.Run(() => AcquireAfterGate(lockB, userB));
            gate.SetResult(); // 兩者同時起跑
            var results = await Task.WhenAll(t1, t2);

            Assert.Equal(1, results.Count(r => r is LockOutcome.Acquired));
            Assert.Equal(1, results.Count(r => r is LockOutcome.Held));
            Assert.Equal(1, await CountRowsAsync(conn, projectId)); // 恰一列（無 PK 衝突半寫）
        }
        finally
        {
            await DeleteRowAsync(conn, projectId);
        }
    }

    [SqlServerFact]
    public async Task Renew_OnlyUpdatesOwnRow()
    {
        var arranged = await TryArrangeAsync();
        if (arranged is null) { return; }
        var (_, lockService, conn) = arranged.Value;

        var projectId = Unique("續租");
        var owner = Unique("owner");
        var stranger = Unique("stranger");
        try
        {
            await lockService.AcquireAsync(projectId, owner, CancellationToken.None);
            await BackdateHeartbeatAsync(conn, projectId, 300); // 回填成舊

            // 非持有人續租 → no-op（heartbeat 不動，仍舊）。
            await lockService.RenewAsync(projectId, stranger, CancellationToken.None);
            Assert.True(await HeartbeatIsStaleAsync(conn, projectId), "非持有人續租不得更新 heartbeat");

            // 持有人續租 → heartbeat 更新為近期（不再 stale）。
            await lockService.RenewAsync(projectId, owner, CancellationToken.None);
            Assert.False(await HeartbeatIsStaleAsync(conn, projectId), "持有人續租後 heartbeat 應更新為近期");
        }
        finally
        {
            await DeleteRowAsync(conn, projectId);
        }
    }

    [SqlServerFact]
    public async Task Release_DeletesOnlyOwnRow()
    {
        var arranged = await TryArrangeAsync();
        if (arranged is null) { return; }
        var (_, lockService, conn) = arranged.Value;

        var projectId = Unique("釋放");
        var owner = Unique("owner");
        var stranger = Unique("stranger");
        try
        {
            await lockService.AcquireAsync(projectId, owner, CancellationToken.None);

            // 釋放他人的鎖 → no-op（列仍在）。
            await lockService.ReleaseAsync(projectId, stranger, CancellationToken.None);
            Assert.Equal(1, await CountRowsAsync(conn, projectId));

            // 釋放自己的鎖 → 刪列。
            await lockService.ReleaseAsync(projectId, owner, CancellationToken.None);
            Assert.Equal(0, await CountRowsAsync(conn, projectId));
        }
        finally
        {
            await DeleteRowAsync(conn, projectId);
        }
    }

    [SqlServerFact]
    public async Task ListActive_IncludesFresh_ExcludesExpired()
    {
        var arranged = await TryArrangeAsync();
        if (arranged is null) { return; }
        var (_, lockService, conn) = arranged.Value;

        var freshId = Unique("新鮮鎖");
        var expiredId = Unique("過期鎖");
        var principal = Unique("user");
        try
        {
            await lockService.AcquireAsync(freshId, principal, CancellationToken.None);
            await lockService.AcquireAsync(expiredId, principal, CancellationToken.None);
            await BackdateHeartbeatAsync(conn, expiredId, 300); // 過期

            var active = await lockService.ListActiveAsync(CancellationToken.None);

            // 只斷言自己這兩筆的可見性（JET_Test 共用，其他測試的鎖可能並存）。
            Assert.Contains(active, l => l.ProjectId == freshId && l.LockedBy == principal);
            Assert.DoesNotContain(active, l => l.ProjectId == expiredId);
        }
        finally
        {
            await DeleteRowAsync(conn, freshId);
            await DeleteRowAsync(conn, expiredId);
        }
    }

    // ---- 直接 SQL 縫（安排前置態與物證讀取；不經受測 service，避免自我印證） ----

    private static SqlConnection Open(string baseConnectionString) =>
        new(new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = SingleDb }.ConnectionString);

    private static async Task<int> CountRowsAsync(string conn, string projectId)
    {
        await using var connection = Open(conn);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM dbo.project_lock WHERE project_id = @id;";
        cmd.Parameters.AddWithValue("@id", projectId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task<string?> ReadLockedByAsync(string conn, string projectId)
    {
        await using var connection = Open(conn);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT locked_by FROM dbo.project_lock WHERE project_id = @id;";
        cmd.Parameters.AddWithValue("@id", projectId);
        return (await cmd.ExecuteScalarAsync()) as string;
    }

    /// <summary>把某鎖的 heartbeat_utc 回填到 <paramref name="seconds"/> 秒前（確定性製造「過期」/「舊」）。</summary>
    private static async Task BackdateHeartbeatAsync(string conn, string projectId, int seconds)
    {
        await using var connection = Open(conn);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "UPDATE dbo.project_lock SET heartbeat_utc = DATEADD(SECOND, -@s, SYSUTCDATETIME()) WHERE project_id = @id;";
        cmd.Parameters.Add("@s", SqlDbType.Int).Value = seconds;
        cmd.Parameters.AddWithValue("@id", projectId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>heartbeat 是否仍「舊」（早於 60 秒前）——續租測試用：no-op 應維持舊、有效續租應變近期。</summary>
    private static async Task<bool> HeartbeatIsStaleAsync(string conn, string projectId)
    {
        await using var connection = Open(conn);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT CASE WHEN heartbeat_utc < DATEADD(SECOND, -60, SYSUTCDATETIME()) THEN 1 ELSE 0 END " +
            "FROM dbo.project_lock WHERE project_id = @id;";
        cmd.Parameters.AddWithValue("@id", projectId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 1;
    }

    private static async Task DeleteRowAsync(string conn, string projectId)
    {
        try
        {
            await using var connection = Open(conn);
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM dbo.project_lock WHERE project_id = @id;";
            cmd.Parameters.AddWithValue("@id", projectId);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (SqlException)
        {
            // 清理失敗不影響測試結果（殘留唯一 project_id 的鎖列無害）。
        }
    }
}
