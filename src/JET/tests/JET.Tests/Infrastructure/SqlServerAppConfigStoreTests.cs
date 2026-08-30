using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 控制面第四輪 §3 <c>dbo.app_config</c> 跨專案系統設定 store 驗收（<see cref="SqlServerAppConfigStore"/>）。
/// 全部 [SqlServerFact] 閘控（連 JET_Test）。隔離:每測試唯一 key。
/// oracle：UPSERT 埠語意——不存在 key 回 null；Set 後 Get 取回同值；同 key 二次 Set 覆寫（不重複列）。
/// </summary>
public sealed class SqlServerAppConfigStoreTests
{
    private const string SingleDb = "JET_Test";

    private static string UniqueKey() => $"cfg-{Guid.NewGuid():N}";

    [SqlServerFact]
    public async Task Get_UnknownKey_ReturnsNull()
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null) { return; }

        var store = new SqlServerAppConfigStore(new SqlServerConnectionOptions(conn, SingleDb));

        Assert.Null(await store.GetAsync(UniqueKey(), CancellationToken.None));
    }

    [SqlServerFact]
    public async Task Set_ThenGet_ReturnsStoredValue()
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null) { return; }

        var store = new SqlServerAppConfigStore(new SqlServerConnectionOptions(conn, SingleDb));
        var key = UniqueKey();
        const string value = """{"leaseSeconds":30,"heartbeat":5}""";

        await store.SetAsync(key, value, CancellationToken.None);

        Assert.Equal(value, await store.GetAsync(key, CancellationToken.None));
    }

    [SqlServerFact]
    public async Task Set_SameKeyTwice_UpsertsWithoutDuplicating()
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null) { return; }

        var store = new SqlServerAppConfigStore(new SqlServerConnectionOptions(conn, SingleDb));
        var key = UniqueKey();

        await store.SetAsync(key, """{"v":1}""", CancellationToken.None);
        await store.SetAsync(key, """{"v":2}""", CancellationToken.None); // 覆寫,非新增

        // 第二值取回（MERGE MATCHED 分支覆寫）。
        Assert.Equal("""{"v":2}""", await store.GetAsync(key, CancellationToken.None));
        // PK 為 [key]：同 key 不可能有兩列，故 MERGE 若誤走 INSERT 分支會撞 PK 而拋——上面 Set 未拋即證明是 UPSERT。
    }
}
