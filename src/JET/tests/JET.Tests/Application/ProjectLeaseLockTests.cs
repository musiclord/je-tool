using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 專案租約鎖的 Application／端到端驗收。現行語意見 docs/jet-guide.md 的「專案鎖」章節。
/// SQL 相關全 [SqlServerFact] 閘控（連 JET_Test）。
/// 隔離：每測試唯一 caseName；finally 以 <see cref="TempSqlServerProject.DropDatabaseAsync"/>（drop schema＋清 registry；
/// schema 刪除交易一併刪 project_lock 列）清理。oracle：manifest 契約（project.load 取鎖／被鎖 project_locked／
/// heartbeatSeconds；project.list 顯示 lock；project.delete 清鎖；折疊修正）＋直接 SQL 物證。
/// </summary>
public sealed class ProjectLeaseLockTests
{
    private const string SingleDb = "JET_Test";

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    private static string SqlServerPayload(string caseName) =>
        $$"""
        { "caseName": "{{caseName}}", "projectCode": "SRV-LOCK", "entityName": "線上實體", "operatorId": "op",
          "periodStart": "2024-01-01", "periodEnd": "2024-12-31", "databaseProvider": "sqlServer" }
        """;

    private static SqlServerLockService LockServiceFor(string conn) =>
        new(new SqlServerConnectionOptions(conn, SingleDb),
            new SqlServerAppConfigStore(new SqlServerConnectionOptions(conn, SingleDb)));

    // ---- ⑨ handler：load 取鎖 / 被鎖拋 project_locked / delete 清鎖 ----

    [SqlServerFact]
    public async Task Load_SqlServer_AcquiresLockAndReturnsHeartbeatSeconds()
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null) { return; }

        var name = $"取鎖載入-{Tag()}";
        using var host = new HandlerTestHost(sqlServerConnectionString: conn);
        try
        {
            await host.DispatchAsync("project.create", SqlServerPayload(name));
            await host.DispatchAsync("project.releaseLock");
            Assert.Equal(0, await LockCountAsync(conn, name));

            var loaded = await host.DispatchAsync("project.load", $$"""{ "projectId": "{{name}}" }""");

            // 回應帶 heartbeatSeconds（無 app_config 覆寫 → 程式常數 30）。
            Assert.Equal(JsonValueKind.Number, loaded.GetProperty("heartbeatSeconds").ValueKind);
            Assert.Equal(ProjectLockDefaults.HeartbeatSeconds, loaded.GetProperty("heartbeatSeconds").GetInt32());
            // 取鎖物證：租約表有一列，持鎖者＝當前 host principal。
            Assert.Equal(host.Principal, await LockedByAsync(conn, name));
        }
        finally
        {
            await TempSqlServerProject.DropDatabaseAsync(conn, name);
        }
    }

    [SqlServerFact]
    public async Task Load_SqlServer_HeldByOther_ThrowsProjectLocked()
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null) { return; }

        var name = $"被鎖載入-{Tag()}";
        using var host = new HandlerTestHost(sqlServerConnectionString: conn);
        var otherHolder = $"其他使用者-{Tag()}";
        try
        {
            await host.DispatchAsync("project.create", SqlServerPayload(name));
            await host.DispatchAsync("project.releaseLock");
            // 另一使用者先持有未過期租約。
            await LockServiceFor(conn).AcquireAsync(name, otherHolder, CancellationToken.None);

            var ex = await Assert.ThrowsAsync<JetActionException>(
                () => host.DispatchAsync("project.load", $$"""{ "projectId": "{{name}}" }"""));

            Assert.Equal(JetErrorCodes.ProjectLocked, ex.Code);
            Assert.Contains(otherHolder, ex.Message); // 訊息含持鎖者，供使用者辨識
            // 被鎖時不奪鎖：持鎖者仍是他人。
            Assert.Equal(otherHolder, await LockedByAsync(conn, name));
        }
        finally
        {
            await TempSqlServerProject.DropDatabaseAsync(conn, name);
        }
    }

    [SqlServerFact]
    public async Task Delete_SqlServer_ClearsLockRow()
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null) { return; }

        var name = $"刪案清鎖-{Tag()}";
        using var host = new HandlerTestHost(sqlServerConnectionString: conn);
        try
        {
            await host.DispatchAsync("project.create", SqlServerPayload(name));
            await host.DispatchAsync("project.load", $$"""{ "projectId": "{{name}}" }"""); // 取鎖
            Assert.Equal(1, await LockCountAsync(conn, name));

            await host.DispatchAsync("project.delete", $$"""{ "projectId": "{{name}}" }""");

            // 刪案交易一併清鎖（避免幽靈鎖）。
            Assert.Equal(0, await LockCountAsync(conn, name));
        }
        finally
        {
            await TempSqlServerProject.DropDatabaseAsync(conn, name);
        }
    }

    // ---- ⑦ project.list 顯示鎖 ----

    [SqlServerFact]
    public async Task List_SqlServer_ShowsLockHeldByOther()
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null) { return; }

        var name = $"清單顯鎖-{Tag()}";
        using var host = new HandlerTestHost(sqlServerConnectionString: conn);
        var otherHolder = $"其他使用者-{Tag()}";
        try
        {
            await host.DispatchAsync("project.create", SqlServerPayload(name)); // 建立者＝host（可見）
            await host.DispatchAsync("project.releaseLock");
            await LockServiceFor(conn).AcquireAsync(name, otherHolder, CancellationToken.None); // 他人持鎖

            var list = await host.DispatchAsync("project.list");
            var entry = SingleProject(list, name);

            var lockInfo = entry.GetProperty("lock");
            Assert.Equal(JsonValueKind.Object, lockInfo.ValueKind);
            Assert.Equal(otherHolder, lockInfo.GetProperty("lockedBy").GetString());
            Assert.False(string.IsNullOrEmpty(lockInfo.GetProperty("machineName").GetString()));
        }
        finally
        {
            await TempSqlServerProject.DropDatabaseAsync(conn, name);
        }
    }

    [SqlServerFact]
    public async Task List_SqlServer_Unlocked_LockIsNull()
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null) { return; }

        var name = $"清單無鎖-{Tag()}";
        using var host = new HandlerTestHost(sqlServerConnectionString: conn);
        try
        {
            await host.DispatchAsync("project.create", SqlServerPayload(name));
            await host.DispatchAsync("project.releaseLock"); // 明示離場後才是無鎖狀態

            var entry = SingleProject(await host.DispatchAsync("project.list"), name);

            // sqlServer 條目恆帶 lock 鍵，未鎖時為 null。
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("lock").ValueKind);
        }
        finally
        {
            await TempSqlServerProject.DropDatabaseAsync(conn, name);
        }
    }

    // ---- ⑩ 折疊修正：folder-cleanup 失敗不使 delete 回報失敗（sqlite，無需 SQL） ----

    [Fact]
    public async Task Delete_FolderCleanupFails_StillReportsSuccessWithMessage()
    {
        using var host = new HandlerTestHost();

        var created = await host.DispatchAsync(
            "project.create",
            """
            { "projectCode": "LOCAL-DEL", "entityName": "E", "operatorId": "op",
              "periodStart": "2024-01-01", "periodEnd": "2024-12-31" }
            """);
        var projectId = created.GetProperty("projectId").GetString()!;
        var projectDir = Path.Combine(host.ProjectsRoot, projectId);

        // 在專案資料夾內開一個檔並保持開啟（FileShare.None）→ Directory.Delete(recursive) 必拋 →
        // 模擬「DB 刪除已成功、本機資料夾清理失敗」。
        var blockerPath = Path.Combine(projectDir, "cleanup-blocker.tmp");
        using (var blocker = new FileStream(blockerPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await blocker.WriteAsync(new byte[] { 1, 2, 3 });
            await blocker.FlushAsync();

            var deleted = await host.DispatchAsync("project.delete", $$"""{ "projectId": "{{projectId}}" }""");

            // 折疊修正：資料夾清理失敗仍回 ok（DB 刪除已 commit），並附 message 供前端寫訊息面板。
            Assert.True(deleted.GetProperty("ok").GetBoolean());
            Assert.Equal(projectId, deleted.GetProperty("projectId").GetString());
            Assert.Equal(JsonValueKind.String, deleted.GetProperty("message").ValueKind);
            Assert.Equal(
                "案件已刪除；本機快取資料夾清理失敗，可稍後手動移除。",
                deleted.GetProperty("message").GetString());
            // DB 刪除確實已發生（jet.db 於 folder-cleanup 之前刪除、交易已 commit）。
            Assert.False(File.Exists(Path.Combine(projectDir, "jet.db")));
        }
    }

    // ---- 直接 SQL 物證縫 ----

    private static SqlConnection Open(string baseConnectionString) =>
        new(new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = SingleDb }.ConnectionString);

    private static async Task<int> LockCountAsync(string conn, string projectId)
    {
        await using var connection = Open(conn);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM dbo.project_lock WHERE project_id = @id;";
        cmd.Parameters.AddWithValue("@id", projectId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task<string?> LockedByAsync(string conn, string projectId)
    {
        await using var connection = Open(conn);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT locked_by FROM dbo.project_lock WHERE project_id = @id;";
        cmd.Parameters.AddWithValue("@id", projectId);
        return (await cmd.ExecuteScalarAsync()) as string;
    }

    private static JsonElement SingleProject(JsonElement listResponse, string projectId)
    {
        var match = listResponse.GetProperty("projects").EnumerateArray()
            .Where(p => p.GetProperty("projectId").GetString() == projectId)
            .ToList();
        Assert.Single(match);
        return match[0];
    }
}
