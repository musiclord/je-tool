using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 雙來源專案清單與線上登記的 Application 層驗收：
/// project.list 三態合併＋online、project.create 登記＋授權、project.load 物化/lazy-heal/
/// 幽靈擋下，以及 sqlite 可攜性不變式。SQL 相關全 [SqlServerFact] 閘控（連 JET_Test，非 Express、≥ 2022）。
/// 隔離：每測試唯一 caseName/principal；finally 以 <see cref="TempSqlServerProject.DropDatabaseAsync"/>
/// （drop schema ＋ 清 registry）清理。oracle：manifest project.* 契約 ＋ 檔案系統物證。
/// </summary>
public sealed class DualSourceProjectTests
{
    private const string SingleDb = "JET_Test";

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    private static string SqlServerPayload(string caseName, string code = "SRV-CODE") =>
        $$"""
        { "caseName": "{{caseName}}", "projectCode": "{{code}}", "entityName": "線上實體", "operatorId": "op",
          "periodStart": "2024-01-01", "periodEnd": "2024-12-31", "databaseProvider": "sqlServer" }
        """;

    private static ProjectDocument SqlServerDoc(string projectId, string code = "SRV-CODE") =>
        new(projectId, code, "線上實體", "op", "2024-01-01", "2024-12-31", null,
            ProjectDocument.DefaultMoneyScale, ProjectDocument.DefaultRoundingMode,
            new DateTimeOffset(2026, 3, 3, 0, 0, 0, TimeSpan.Zero),
            CurrentStep: 1, ProjectDocument.CurrentSchemaVersion, ProjectDocument.SqlServerDatabaseProvider);

    private static SqlServerProjectRegistry RegistryFor(string conn) =>
        new(new SqlServerConnectionOptions(conn, SingleDb));

    // ---- project.list：三態合併（synced / serverOnly / localOnly）＋ online 塊 ----

    [SqlServerFact]
    public async Task List_MergesSyncedServerOnlyLocalOnly_WithOnlineBlock()
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null) { return; }

        var tag = Tag();
        var principal = $"p-{tag}";
        using var host = new HandlerTestHost(sqlServerConnectionString: conn, principalName: principal);
        var registry = RegistryFor(conn);

        var syncedName = $"同步案-{tag}";     // 本機 doc ＋ registry 列 → synced
        var serverOnlyName = $"僅伺服器-{tag}"; // 僅 registry 列（本機無資料夾）→ serverOnly
        var localOnlyName = $"僅本機-{tag}";   // 本機 sqlServer 快取、registry 無 → localOnly

        try
        {
            await host.DispatchAsync("project.create", SqlServerPayload(syncedName));
            await registry.RegisterAsync(SqlServerDoc(serverOnlyName), principal, CancellationToken.None);
            await WriteLocalSqlServerCacheAsync(host.ProjectsRoot, localOnlyName);

            var data = await host.DispatchAsync("project.list");

            Assert.Equal("synced", Entry(data, syncedName).GetProperty("syncStatus").GetString());
            Assert.Equal("serverOnly", Entry(data, serverOnlyName).GetProperty("syncStatus").GetString());
            Assert.Equal("localOnly", Entry(data, localOnlyName).GetProperty("syncStatus").GetString());

            var online = data.GetProperty("online");
            Assert.True(online.GetProperty("reachable").GetBoolean());
            Assert.Equal(principal, online.GetProperty("principal").GetString());
            // 可達時 message 為 null（無警示）。
            Assert.Equal(JsonValueKind.Null, online.GetProperty("message").ValueKind);
        }
        finally
        {
            await TempSqlServerProject.DropDatabaseAsync(conn, syncedName);
            await registry.UnregisterAsync(serverOnlyName, CancellationToken.None);
        }
    }

    // ---- project.create：登記＋授權建立者；registry 幽靈同名擋下 ----

    [SqlServerFact]
    public async Task Create_SqlServer_WritesRegistryRowAndGrantsCreator()
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null) { return; }

        var name = $"建案登記-{Tag()}";
        using var host = new HandlerTestHost(sqlServerConnectionString: conn);
        var registry = RegistryFor(conn);
        try
        {
            var created = await host.DispatchAsync("project.create", SqlServerPayload(name));
            Assert.True(created.GetProperty("ok").GetBoolean());

            // registry 有列。
            Assert.True(await registry.ExistsAsync(name, CancellationToken.None));
            // 建立者（host.Principal）被授權 → ListVisible 看得到。
            var visible = await registry.ListVisibleAsync(host.Principal, CancellationToken.None);
            Assert.Contains(visible, r => r.Document.ProjectId == name);
        }
        finally
        {
            await TempSqlServerProject.DropDatabaseAsync(conn, name);
        }
    }

    [SqlServerFact]
    public async Task Create_SqlServer_RegistryHasRowButSchemaAbsent_BlocksGhostName()
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null) { return; }

        var name = $"幽靈同名-{Tag()}";
        using var host = new HandlerTestHost(sqlServerConnectionString: conn);
        var registry = RegistryFor(conn);
        try
        {
            // 登記簿有列、但後端無 schema（他人在別台電腦建立、本機既無資料夾亦無 schema）。
            await registry.RegisterAsync(SqlServerDoc(name), host.Principal, CancellationToken.None);

            var ex = await Assert.ThrowsAsync<JetActionException>(
                () => host.DispatchAsync("project.create", SqlServerPayload(name)));

            Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
            Assert.Contains("本機沒有對應的案件登記", ex.Message);
            // 檢查先於寫入：失敗嘗試不得留下本機資料夾。
            Assert.False(Directory.Exists(Path.Combine(host.ProjectsRoot, name)));
        }
        finally
        {
            await registry.UnregisterAsync(name, CancellationToken.None);
        }
    }

    // ---- project.load：物化 serverOnly、幽靈擋下、lazy-heal、物化資料夾衝突 ----

    [SqlServerFact]
    public async Task Load_ServerOnly_MaterializesCacheAndLoads()
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null) { return; }

        var name = $"物化案-{Tag()}";
        var principal = $"p-{Tag()}";
        // source：建案（registry 列＋schema＋本機資料夾）。target：同 principal、同 SQL、但獨立空 projects 根。
        using var source = new HandlerTestHost(sqlServerConnectionString: conn, principalName: principal);
        try
        {
            await source.DispatchAsync("project.create", SqlServerPayload(name, "MAT-CODE"));
            await source.DispatchAsync("project.releaseLock");

            using var target = new HandlerTestHost(sqlServerConnectionString: conn, principalName: principal);
            Assert.False(Directory.Exists(Path.Combine(target.ProjectsRoot, name))); // 物化前本機無資料夾

            var loaded = await target.DispatchAsync("project.load", $$"""{ "projectId": "{{name}}" }""");

            // 物化：project.json 生成，內容 round-trip 等於入庫的 doc。
            Assert.True(File.Exists(Path.Combine(target.ProjectsRoot, name, "project.json")));
            Assert.Equal(name, loaded.GetProperty("project").GetProperty("projectId").GetString());
            Assert.Equal("MAT-CODE", loaded.GetProperty("project").GetProperty("projectCode").GetString());
            Assert.Equal("sqlServer", loaded.GetProperty("project").GetProperty("databaseProvider").GetString());
        }
        finally
        {
            await TempSqlServerProject.DropDatabaseAsync(conn, name);
        }
    }

    [SqlServerFact]
    public async Task Load_ServerOnlyWithCorruptSeed_FailsLoudlyWithoutMaterializingCache()
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null) { return; }

        var name = $"種子損壞-{Tag()}";
        var principal = $"p-{Tag()}";
        using var host = new HandlerTestHost(sqlServerConnectionString: conn, principalName: principal);
        var registry = RegistryFor(conn);
        try
        {
            // 模擬已損壞的控制面 JSON；正式 project.create 永遠寫合法 seed＋v2 marker。
            await registry.RegisterAsync(
                SqlServerDoc(name) with { SampleSeed = 0, SampleSeedVersion = 2 },
                principal,
                CancellationToken.None);

            var exception = await Assert.ThrowsAsync<JetActionException>(() =>
                host.DispatchAsync("project.load", $$"""{ "projectId": "{{name}}" }"""));

            Assert.Equal(JetErrorCodes.FileReadError, exception.Code);
            Assert.Contains("INF 抽樣種子", exception.Message, StringComparison.Ordinal);
            Assert.Contains("不會重新產生", exception.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(host.ProjectsRoot, name)));
        }
        finally
        {
            await registry.UnregisterAsync(name, CancellationToken.None);
        }
    }

    [SqlServerFact]
    public async Task Load_LocalSqlServerDoc_RegistryMissingAndSchemaAbsent_ThrowsProjectNotFound()
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null) { return; }

        var name = $"幽靈快取-{Tag()}";
        using var host = new HandlerTestHost(sqlServerConnectionString: conn);
        // 本機有 sqlServer 快取 doc、但 registry 無登記、後端亦無 schema（幽靈）。
        await WriteLocalSqlServerCacheAsync(host.ProjectsRoot, name);

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("project.load", $$"""{ "projectId": "{{name}}" }"""));

        Assert.Equal(JetErrorCodes.ProjectNotFound, ex.Code);
        Assert.Contains("已不在伺服器上", ex.Message);
    }

    [SqlServerFact]
    public async Task Load_LocalSqlServerDoc_SchemaExistsRegistryMissing_LazyHeals()
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null) { return; }

        var name = $"待補登記-{Tag()}";
        using var host = new HandlerTestHost(sqlServerConnectionString: conn);
        var registry = RegistryFor(conn);
        try
        {
            // 建案（registry 列＋schema＋本機 doc），再移除 registry 列 → 模擬「registry 問世前的既有線上案」。
            await host.DispatchAsync("project.create", SqlServerPayload(name));
            await registry.UnregisterAsync(name, CancellationToken.None);
            Assert.False(await registry.ExistsAsync(name, CancellationToken.None)); // 前置：registry 缺

            var loaded = await host.DispatchAsync("project.load", $$"""{ "projectId": "{{name}}" }""");
            Assert.Equal("sqlServer", loaded.GetProperty("project").GetProperty("databaseProvider").GetString());

            // lazy-heal：載入時以本機 doc 補登記＋授權當前 principal。
            Assert.True(await registry.ExistsAsync(name, CancellationToken.None));
            Assert.Contains(
                await registry.ListVisibleAsync(host.Principal, CancellationToken.None),
                r => r.Document.ProjectId == name);
        }
        finally
        {
            await TempSqlServerProject.DropDatabaseAsync(conn, name);
        }
    }

    [SqlServerFact]
    public async Task Load_ServerOnly_TargetFolderOccupied_ThrowsInvalidPayload()
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null) { return; }

        var name = $"占用衝突-{Tag()}";
        using var host = new HandlerTestHost(sqlServerConnectionString: conn);
        var registry = RegistryFor(conn);
        try
        {
            // registry 有 serverOnly 列（授權當前 principal）。
            await registry.RegisterAsync(SqlServerDoc(name), host.Principal, CancellationToken.None);
            // 本機資料夾已被占用但無有效 project.json（FindAsync 回 null → 走物化路徑，CreateAsync 撞資料夾）。
            Directory.CreateDirectory(Path.Combine(host.ProjectsRoot, name));

            var ex = await Assert.ThrowsAsync<JetActionException>(
                () => host.DispatchAsync("project.load", $$"""{ "projectId": "{{name}}" }"""));

            Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
            Assert.Contains("本地已有同名案件", ex.Message);
            Assert.Null(ex.Field);
        }
        finally
        {
            await registry.UnregisterAsync(name, CancellationToken.None);
        }
    }

    // ---- 逐使用者操作隔離（2026-07-07 spec §4）：load/delete 授權前置、list noAccess、存在性不洩漏 ----
    // 每測試唯一 caseName／owner／intruder principal（GUID 隔離）；owner 建案獲授權、intruder 為未授權的第二使用者。

    /// <summary>
    /// ①：B 持有 A 案的本機快取（A 建案＋B 複製資料夾）→ B project.load 回 not_authorized。
    /// 現行為（無授權前置）＝放行成功；本測試鎖住「登記存在且當前 principal 不可見即擋下」的操作層強制。
    /// </summary>
    [SqlServerFact]
    public async Task Load_OtherUsersProjectWithLocalCache_ThrowsNotAuthorized()
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null) { return; }

        var name = $"越權載入-{Tag()}";
        using var owner = new HandlerTestHost(sqlServerConnectionString: conn, principalName: $"owner-{Tag()}");
        using var intruder = new HandlerTestHost(sqlServerConnectionString: conn, principalName: $"intruder-{Tag()}");
        try
        {
            await owner.DispatchAsync("project.create", SqlServerPayload(name)); // registry 列＋schema＋僅授權 A
            CopyDirectory(Path.Combine(owner.ProjectsRoot, name), Path.Combine(intruder.ProjectsRoot, name)); // B 持 A 案快取

            var ex = await Assert.ThrowsAsync<JetActionException>(
                () => intruder.DispatchAsync("project.load", $$"""{ "projectId": "{{name}}" }"""));
            Assert.Equal(JetErrorCodes.NotAuthorized, ex.Code);
        }
        finally
        {
            await TempSqlServerProject.DropDatabaseAsync(conn, name);
        }
    }

    /// <summary>
    /// ②：B project.delete 他人案 → not_authorized，且三不變：A 的登記仍在（A 仍可見）、專案 schema 仍在、
    /// B 的本機快取資料夾仍在（避免「刪了快取卻誤以為刪了案件」）。現行為（無 gate）＝連 registry＋schema 一併刪除。
    /// </summary>
    [SqlServerFact]
    public async Task Delete_OtherUsersProjectWithLocalCache_ThrowsNotAuthorized_PreservesRegistrySchemaAndCache()
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null) { return; }

        var name = $"越權刪除-{Tag()}";
        using var owner = new HandlerTestHost(sqlServerConnectionString: conn, principalName: $"owner-{Tag()}");
        using var intruder = new HandlerTestHost(sqlServerConnectionString: conn, principalName: $"intruder-{Tag()}");
        var registry = RegistryFor(conn);
        var schemaDb = new SqlServerProjectDatabase(new SqlServerConnectionOptions(conn, SingleDb));
        try
        {
            await owner.DispatchAsync("project.create", SqlServerPayload(name));
            CopyDirectory(Path.Combine(owner.ProjectsRoot, name), Path.Combine(intruder.ProjectsRoot, name));

            var ex = await Assert.ThrowsAsync<JetActionException>(
                () => intruder.DispatchAsync("project.delete", $$"""{ "projectId": "{{name}}" }"""));
            Assert.Equal(JetErrorCodes.NotAuthorized, ex.Code);

            // 三不變（動任何資料前即擋下）。
            Assert.True(await registry.ExistsAsync(name, CancellationToken.None));                       // A 的登記仍在
            Assert.NotNull(await registry.FindVisibleAsync(name, owner.Principal, CancellationToken.None)); // A 的授權仍在（仍可見）
            Assert.True(await schemaDb.DatabaseExistsAsync(
                name, ProjectDocument.SqlServerDatabaseProvider, CancellationToken.None));                // 專案 schema 仍在
            Assert.True(Directory.Exists(Path.Combine(intruder.ProjectsRoot, name)));                    // B 的本機快取仍在
        }
        finally
        {
            await TempSqlServerProject.DropDatabaseAsync(conn, name);
        }
    }

    /// <summary>
    /// ③：B 持有他人案的本機快取、線上可達但不在可見清單 → 該條目 syncStatus = "noAccess"。
    /// 現行為（無第四態）＝localOnly。輕量安排：owner 登記＋B 寫本機快取（noAccess 判定只查登記存在性，不需真 schema）。
    /// </summary>
    [SqlServerFact]
    public async Task List_OtherUsersProjectWithLocalCache_MarksNoAccess()
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null) { return; }

        var tag = Tag();
        var name = $"無權清單-{tag}";
        var registry = RegistryFor(conn);
        using var intruder = new HandlerTestHost(sqlServerConnectionString: conn, principalName: $"intruder-{tag}");
        try
        {
            await registry.RegisterAsync(SqlServerDoc(name), $"owner-{tag}", CancellationToken.None);
            await WriteLocalSqlServerCacheAsync(intruder.ProjectsRoot, name);

            var data = await intruder.DispatchAsync("project.list");

            Assert.True(data.GetProperty("online").GetProperty("reachable").GetBoolean());
            Assert.Equal("noAccess", Entry(data, name).GetProperty("syncStatus").GetString());
        }
        finally
        {
            await registry.UnregisterAsync(name, CancellationToken.None);
        }
    }

    /// <summary>
    /// ④：B 對他人案（B 本機**無**快取）project.load → project_not_found（存在性不洩漏）。serverOnly 物化走
    /// principal-scoped 的 FindVisibleAsync，不可見即當作不存在——鎖住現行為，不因本輪授權前置退化成 not_authorized。
    /// </summary>
    [SqlServerFact]
    public async Task Load_OtherUsersProjectWithoutLocalCache_ThrowsProjectNotFound_DoesNotLeakExistence()
    {
        var conn = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (conn is null) { return; }

        var tag = Tag();
        var name = $"不洩漏存在-{tag}";
        var registry = RegistryFor(conn);
        using var intruder = new HandlerTestHost(sqlServerConnectionString: conn, principalName: $"intruder-{tag}");
        try
        {
            await registry.RegisterAsync(SqlServerDoc(name), $"owner-{tag}", CancellationToken.None); // 他人登記、B 本機無資料夾

            var ex = await Assert.ThrowsAsync<JetActionException>(
                () => intruder.DispatchAsync("project.load", $$"""{ "projectId": "{{name}}" }"""));
            Assert.Equal(JetErrorCodes.ProjectNotFound, ex.Code);
        }
        finally
        {
            await registry.UnregisterAsync(name, CancellationToken.None);
        }
    }

    // ---- sqlite 可攜性不變式：整夾複製到新 projects 根仍列出、載入、資料查得回 ----

    [Fact]
    public async Task Portability_SqliteFolderCopiedToNewRoot_ListsAndLoadsWithImportedData()
    {
        using var source = new HandlerTestHost();
        var name = $"可攜案-{Tag()}";
        var created = await source.DispatchAsync(
            "project.create",
            $$"""
            { "caseName": "{{name}}", "projectCode": "PORT-1", "entityName": "可攜實體", "operatorId": "op",
              "periodStart": "2024-01-01", "periodEnd": "2024-12-31" }
            """);
        var id = created.GetProperty("projectId").GetString()!;

        // 最小匯入：兩列 GL，證明 jet.db（含 staging）隨資料夾可攜。
        var glFile = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 1).Value = "傳票號碼"; ws.Cell(1, 2).Value = "傳票日期"; ws.Cell(1, 3).Value = "科目代號"; ws.Cell(1, 4).Value = "金額";
            ws.Cell(2, 1).Value = "JV-1"; ws.Cell(2, 2).Value = "2024-03-01"; ws.Cell(2, 3).Value = "1101"; ws.Cell(2, 4).Value = "100";
            ws.Cell(3, 1).Value = "JV-2"; ws.Cell(3, 2).Value = "2024-03-02"; ws.Cell(3, 3).Value = "4101"; ws.Cell(3, 4).Value = "100";
        });
        try
        {
            var imported = await source.DispatchAsync(
                "import.gl.fromFile", JsonSerializer.Serialize(new { filePath = glFile }));
            Assert.Equal(2, imported.GetProperty("rowCount").GetInt32());

            // 整夾複製到第二個 projects 根（新 store/composition）。
            using var target = new HandlerTestHost();
            SqliteTestPool.Clear(source.ProjectsRoot, id); // 只釋放 source jet.db 的 pool，確保檔案可被複製
            CopyDirectory(Path.Combine(source.ProjectsRoot, id), Path.Combine(target.ProjectsRoot, id));

            // 新根：列出。
            var list = await target.DispatchAsync("project.list");
            Assert.Contains(
                list.GetProperty("projects").EnumerateArray(),
                p => p.GetProperty("projectId").GetString() == id);

            // 新根：載入成功、資料查得回（匯入的兩列在 jet.db 內）。
            var loaded = await target.DispatchAsync("project.load", $$"""{ "projectId": "{{id}}" }""");
            Assert.Equal("sqlite", loaded.GetProperty("project").GetProperty("databaseProvider").GetString());
            Assert.Equal(2, loaded.GetProperty("importState").GetProperty("gl").GetProperty("rowCount").GetInt32());

            // INF per-project 種子隨 project.json 可攜：搬移前後 sampleSeed 不變（同一母體抽同一集合的前提）。
            var sourceSeed = ReadSampleSeed(source.ProjectsRoot, id);
            var targetSeed = ReadSampleSeed(target.ProjectsRoot, id);
            Assert.Equal(sourceSeed, targetSeed);
            Assert.InRange(targetSeed, 1L, ProjectDocument.SampleSeedExclusiveUpperBound - 1);
            Assert.Equal(
                ReadSampleSeedVersion(source.ProjectsRoot, id),
                ReadSampleSeedVersion(target.ProjectsRoot, id));
        }
        finally
        {
            TestWorkbookBuilder.Delete(glFile);
        }
    }

    private static long ReadSampleSeed(string projectsRoot, string projectId)
    {
        using var doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(projectsRoot, projectId, "project.json")));
        return doc.RootElement.GetProperty("sampleSeed").GetInt64();
    }

    private static int ReadSampleSeedVersion(string projectsRoot, string projectId)
    {
        using var doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(projectsRoot, projectId, "project.json")));
        return doc.RootElement.GetProperty("sampleSeedVersion").GetInt32();
    }

    // ---- duckdb 可攜性不變式：整夾複製到新 projects 根仍列出、載入、資料查得回（鏡射 sqlite 版） ----

    [Fact]
    public async Task Portability_DuckDbFolderCopiedToNewRoot_ListsAndLoadsWithImportedData()
    {
        using var source = new HandlerTestHost();
        var name = $"可攜案duck-{Tag()}";
        var created = await source.DispatchAsync(
            "project.create",
            $$"""
            { "caseName": "{{name}}", "projectCode": "PORT-D", "entityName": "可攜實體", "operatorId": "op",
              "periodStart": "2024-01-01", "periodEnd": "2024-12-31", "databaseProvider": "duckdb" }
            """);
        var id = created.GetProperty("projectId").GetString()!;

        // 最小匯入：兩列 GL，證明 jet.duckdb（含 staging）隨資料夾可攜。
        var glFile = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 1).Value = "傳票號碼"; ws.Cell(1, 2).Value = "傳票日期"; ws.Cell(1, 3).Value = "科目代號"; ws.Cell(1, 4).Value = "金額";
            ws.Cell(2, 1).Value = "JV-1"; ws.Cell(2, 2).Value = "2024-03-01"; ws.Cell(2, 3).Value = "1101"; ws.Cell(2, 4).Value = "100";
            ws.Cell(3, 1).Value = "JV-2"; ws.Cell(3, 2).Value = "2024-03-02"; ws.Cell(3, 3).Value = "4101"; ws.Cell(3, 4).Value = "100";
        });
        try
        {
            var imported = await source.DispatchAsync(
                "import.gl.fromFile", JsonSerializer.Serialize(new { filePath = glFile }));
            Assert.Equal(2, imported.GetProperty("rowCount").GetInt32());

            // 整夾複製到第二個 projects 根（新 store/composition）。
            using var target = new HandlerTestHost();
            // DuckDB.NET 無 SQLite 的 ClearAllPools；催動 finalizer 釋放 source jet.duckdb 的原生 handle，確保檔案可複製。
            GC.Collect();
            GC.WaitForPendingFinalizers();
            CopyDirectory(Path.Combine(source.ProjectsRoot, id), Path.Combine(target.ProjectsRoot, id));

            // 新根：列出。
            var list = await target.DispatchAsync("project.list");
            Assert.Contains(
                list.GetProperty("projects").EnumerateArray(),
                p => p.GetProperty("projectId").GetString() == id);

            // 新根：載入成功、資料查得回（匯入的兩列在 jet.duckdb 內）。
            var loaded = await target.DispatchAsync("project.load", $$"""{ "projectId": "{{id}}" }""");
            Assert.Equal("duckdb", loaded.GetProperty("project").GetProperty("databaseProvider").GetString());
            Assert.Equal(2, loaded.GetProperty("importState").GetProperty("gl").GetProperty("rowCount").GetInt32());
        }
        finally
        {
            TestWorkbookBuilder.Delete(glFile);
        }
    }

    // ---- helpers ----

    /// <summary>本機一份 sqlServer 快取 project.json（無真後端 schema）。</summary>
    private static async Task WriteLocalSqlServerCacheAsync(string projectsRoot, string projectId)
    {
        var dir = Path.Combine(projectsRoot, projectId);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "project.json"), $$"""
            {
              "projectId": "{{projectId}}",
              "projectCode": "CACHE-1",
              "entityName": "快取實體",
              "operatorId": "op",
              "periodStart": "2024-01-01",
              "periodEnd": "2024-12-31",
              "lastAccountingPeriodDate": null,
              "moneyScale": 10000,
              "roundingMode": "AwayFromZero",
              "createdUtc": "2026-06-01T00:00:00+00:00",
              "currentStep": 1,
              "schemaVersion": 1,
              "databaseProvider": "sqlServer"
            }
            """);
    }

    private static JsonElement Entry(JsonElement listResponse, string projectId)
    {
        var match = listResponse.GetProperty("projects").EnumerateArray()
            .Where(p => p.GetProperty("projectId").GetString() == projectId)
            .ToList();
        Assert.Single(match);
        return match[0];
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var sub in Directory.EnumerateDirectories(sourceDir))
        {
            CopyDirectory(sub, Path.Combine(targetDir, Path.GetFileName(sub)));
        }
    }
}
