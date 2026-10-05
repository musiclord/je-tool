using System.Text.Json;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace JET.Tests.Application;

public sealed class ProjectHandlersTests
{
    private const string CreatePayload =
        """
        {
          "projectCode": "ENG-2024-001",
          "entityName": "範例股份有限公司",
          "operatorId": "auditor01",
          "periodStart": "2024-01-01",
          "periodEnd": "2024-12-31",
          "lastPeriodStart": "2024-12-31"
        }
        """;

    [Fact]
    public async Task Create_WritesFolderJsonDbAndReturnsProjectId()
    {
        using var host = new HandlerTestHost();

        var data = await host.DispatchAsync("project.create", CreatePayload);

        Assert.True(data.GetProperty("ok").GetBoolean());
        var projectId = data.GetProperty("projectId").GetString();
        Assert.NotNull(projectId);
        Assert.Matches("^[0-9a-f]{32}$", projectId);

        var projectDir = Path.Combine(host.ProjectsRoot, projectId);
        Assert.True(File.Exists(Path.Combine(projectDir, "project.json")));
        Assert.True(File.Exists(Path.Combine(projectDir, "jet.db")));

        // session 已設定：dev.db.overview 不需先 load 即可使用
        var overview = await host.DispatchAsync("dev.db.overview");
        Assert.True(overview.GetProperty("tables").GetArrayLength() >= 7);
    }

    /// <summary>
    /// INF per-project 種子：建案時隨機生成一次並寫進 project.json 的 sampleSeed，
    /// 值落在 [1, 2147483646]（避開 0 與 SQL 模數 2147483647 的倍數），並寫入
    /// current sampleSeedVersion。缺少版本的舊案件在讀取 project.json 時就會被拒絕。
    /// oracle：規格（種子生命週期）＋ 直接讀 project.json 物證。以原始 JSON 斷言，不綁 ProjectDocument 型別。
    /// </summary>
    [Fact]
    public async Task Create_PersistsSampleSeedWithinRange()
    {
        using var host = new HandlerTestHost();

        var created = await host.DispatchAsync("project.create", CreatePayload);
        var projectId = created.GetProperty("projectId").GetString()!;

        var json = await File.ReadAllTextAsync(
            Path.Combine(host.ProjectsRoot, projectId, "project.json"));
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("sampleSeed", out var seed),
            "project.json 應含 sampleSeed 欄位");
        Assert.Equal(JsonValueKind.Number, seed.ValueKind);
        var value = seed.GetInt64();
        Assert.InRange(value, 1L, ProjectDocument.SampleSeedExclusiveUpperBound - 1);
        Assert.Equal(
            JetAuditProgram.CurrentInfSamplingAlgorithmVersion,
            doc.RootElement.GetProperty("sampleSeedVersion").GetInt32());
    }

    /// <summary>兩次獨立建案的種子不同（隨機生成，非全域常數）。極小機率碰撞可接受；重跑即過。</summary>
    [Fact]
    public async Task Create_TwoProjects_HaveDistinctSampleSeeds()
    {
        using var host = new HandlerTestHost();

        var a = (await host.DispatchAsync("project.create", CreatePayload)).GetProperty("projectId").GetString()!;
        var b = (await host.DispatchAsync(
            "project.create", CreatePayload.Replace("ENG-2024-001", "ENG-2024-002"))).GetProperty("projectId").GetString()!;

        var seedA = ReadSampleSeed(host.ProjectsRoot, a);
        var seedB = ReadSampleSeed(host.ProjectsRoot, b);
        Assert.NotEqual(seedA, seedB);
    }

    private static long ReadSampleSeed(string projectsRoot, string projectId)
    {
        using var doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(projectsRoot, projectId, "project.json")));
        return doc.RootElement.GetProperty("sampleSeed").GetInt64();
    }

    [Fact]
    public async Task Create_MissingOptionalEntityName_Succeeds()
    {
        using var host = new HandlerTestHost();

        var created = await host.DispatchAsync(
            "project.create",
            """{ "projectCode": "X", "periodStart": "2024-01-01", "periodEnd": "2024-12-31" }""");

        Assert.True(created.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Create_BadDateFormat_ThrowsInvalidPayload()
    {
        using var host = new HandlerTestHost();

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync(
                "project.create",
                """
                { "projectCode": "X", "entityName": "E", "operatorId": "op",
                  "periodStart": "2024/01/01", "periodEnd": "2024-12-31" }
                """));

        Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
        Assert.Contains("periodStart", ex.Message);
        Assert.Null(ex.Field);
    }

    // ---- 案件名稱(caseName)→ projectId/資料夾名(2026-06-22);選填,未提供回退 GUID ----

    private const string CreateWithCaseName =
        """
        {
          "caseName": "2025年度甲公司查核",
          "projectCode": "ENG-2024-001",
          "entityName": "範例股份有限公司",
          "operatorId": "auditor01",
          "periodStart": "2024-01-01",
          "periodEnd": "2024-12-31"
        }
        """;

    [Fact]
    public async Task Create_WithCaseName_UsesItAsProjectIdAndFolderName()
    {
        using var host = new HandlerTestHost();

        var data = await host.DispatchAsync("project.create", CreateWithCaseName);

        Assert.Equal("2025年度甲公司查核", data.GetProperty("projectId").GetString());
        Assert.True(File.Exists(
            Path.Combine(host.ProjectsRoot, "2025年度甲公司查核", "project.json")));
    }

    [Fact]
    public async Task Create_WithoutCaseName_FallsBackToGuidProjectId()
    {
        using var host = new HandlerTestHost();

        var data = await host.DispatchAsync("project.create", CreatePayload);

        Assert.Matches("^[0-9a-f]{32}$", data.GetProperty("projectId").GetString());
    }

    [Fact]
    public async Task Create_WithIllegalCaseName_ThrowsInvalidPayload()
    {
        using var host = new HandlerTestHost();

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync(
                "project.create", CreateWithCaseName.Replace("2025年度甲公司查核", "壞/名稱")));

        Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
        Assert.Equal(JetErrorFields.CaseName, ex.Field);
    }

    [Fact]
    public async Task Create_DuplicateCaseName_ThrowsInvalidPayload()
    {
        using var host = new HandlerTestHost();

        await host.DispatchAsync("project.create", CreateWithCaseName);
        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("project.create", CreateWithCaseName));

        Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
        Assert.Contains("已存在", ex.Message);
        Assert.Equal(JetErrorFields.CaseName, ex.Field);
    }

    /// <summary>
    /// schema-per-project 修掉了舊 DB-name 淨化碰撞 bug：schema = For(projectId) =
    /// prj_ + sanitize + hash8(projectId)，兩個不同案名 → 不同 hash8 → 不同 schema（碰撞機率極低 ~2⁻³²）。
    /// 故兩個「在舊單庫淨化模型下會撞同 JET_ 庫名」的案名（僅差一個空白），在新模型下兩案皆建立成功、
    /// 且落在不同 schema。oracle：SqlServerProjectSchema.For 衍生規格（兩 schema 必不相等）+ 兩 project.json 皆落地。
    /// </summary>
    [SqlServerFact]
    public async Task Create_SqlServer_PreviouslyCollidingCaseNames_BothSucceedInDistinctSchemas()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return; // 無 LocalDB → 跳過
        }

        using var host = new HandlerTestHost(sqlServerConnectionString: connectionString);

        static string PayloadFor(string caseName) =>
            $$"""
            { "caseName": "{{caseName}}", "projectCode": "P", "entityName": "E", "operatorId": "op",
              "periodStart": "2024-01-01", "periodEnd": "2024-12-31", "databaseProvider": "sqlServer" }
            """;

        // 唯一 tag → 每輪不同的 schema（免跨輪殘留汙染）；兩名僅差一個空白，舊模型淨化後相同 → 舊會碰撞。
        var tag = Guid.NewGuid().ToString("N")[..8];
        var nameA = $"案 {tag}";
        var nameB = $"案{tag}";

        // 新模型規格：兩不同案名（即 projectId）→ 不同 hash8 → 不同 schema（碰撞機率極低 ~2⁻³²）。
        Assert.NotEqual(SqlServerProjectSchema.For(nameA), SqlServerProjectSchema.For(nameB));

        try
        {
            var createdA = await host.DispatchAsync("project.create", PayloadFor(nameA));
            var createdB = await host.DispatchAsync("project.create", PayloadFor(nameB));

            // 兩案皆建立成功（不再有第二案被擋的碰撞）。
            Assert.True(createdA.GetProperty("ok").GetBoolean());
            Assert.True(createdB.GetProperty("ok").GetBoolean());

            // 兩 project.json 皆落地、各自獨立。
            Assert.True(File.Exists(Path.Combine(host.ProjectsRoot, nameA, "project.json")));
            Assert.True(File.Exists(Path.Combine(host.ProjectsRoot, nameB, "project.json")));
        }
        finally
        {
            // schema-per-project：清掉兩案在共用單庫（測試 host 釘住 JET_Test）留下的 prj_xxx schema。
            var db = new SqlServerProjectDatabase(new SqlServerConnectionOptions(connectionString));
            await db.DeleteAsync(nameA, CancellationToken.None);
            await db.DeleteAsync(nameB, CancellationToken.None);
        }
    }

    // ---- 孤兒 schema 回歸（2026-07-07 實機回歸案例）：本機 projects/ 登記遺失、
    // SQL Server 單庫留下前一個 session 建的專案 schema 時,同名重建的兩種路徑 ----

    /// <summary>組 caseName＋provider 的建案 payload(孤兒 schema 回歸兩測共用)。</summary>
    private static string CaseNamePayload(string caseName, string provider) =>
        $$"""
        { "caseName": "{{caseName}}", "projectCode": "P", "entityName": "E", "operatorId": "op",
          "periodStart": "2024-01-01", "periodEnd": "2024-12-31", "databaseProvider": "{{provider}}" }
        """;

    /// <summary>
    /// 症狀 A 回歸:同名 sqlServer 建案撞到孤兒 schema(後端有既有資料、本機無登記)時,
    /// 必須以 invalid_payload 擋下,且錯誤訊息說真話(孤兒語意),不再沿用 per-database 時代的
    /// 「資料庫已存在」措辭;失敗嘗試不得留下任何本機資料夾(檢查先於寫入,消滅「先寫再回滾」時序)。
    /// oracle:規格(manifest project.create 錯誤語意)。錯誤碼不變(wire 不變),只鎖訊息的孤兒關鍵詞。
    /// </summary>
    [SqlServerFact]
    public async Task Create_SqlServerCaseNameHitsOrphanSchema_ThrowsInvalidPayloadWithOrphanMessage()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return; // 無 SQL Server → 跳過
        }

        // 唯一案名(每輪不同 schema,免跨輪殘留);孤兒化前置:直接在共用單庫(JET_Test)建該案名
        // 衍生的 schema、不建任何本機資料夾——等價於「前一個 app session 建過同名 sqlServer 案,
        // 之後使用者手動刪了 projects/ 登記」(事故時間軸即跨 session:06-29 建案、07-07 重建)。
        var caseName = $"孤兒訊息 {Guid.NewGuid().ToString("N")[..8]}";
        var backend = new SqlServerProjectDatabase(new SqlServerConnectionOptions(connectionString));
        await backend.EnsureCreatedAsync(caseName, CancellationToken.None);

        try
        {
            using var host = new HandlerTestHost(sqlServerConnectionString: connectionString);

            var ex = await Assert.ThrowsAsync<JetActionException>(
                () => host.DispatchAsync("project.create", CaseNamePayload(caseName, "sqlServer")));

            Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
            // 孤兒語意關鍵詞:訊息必須指出「後端已有資料、本機沒有登記」的矛盾(使用者的專案清單是空的,
            // 「已存在」對他是謎語),而非謊稱單純撞名。
            Assert.Contains("本機沒有對應的案件登記", ex.Message);
            // 檢查先於寫入:失敗嘗試不得留下本機資料夾。
            Assert.False(Directory.Exists(Path.Combine(host.ProjectsRoot, caseName)));
        }
        finally
        {
            // teardown:清掉 JET_Test 內的孤兒 schema(drop 表 → DROP SCHEMA → 刪 map 列)。
            await backend.DeleteAsync(caseName, CancellationToken.None);
        }
    }

    [SqlServerFact]
    public async Task Create_SqlServerOrphanPreflight_WinsBeforeMissingRequiredPayload()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return;
        }

        var caseName = $"孤兒優先序 {Guid.NewGuid().ToString("N")[..8]}";
        var backend = new SqlServerProjectDatabase(new SqlServerConnectionOptions(connectionString));
        await backend.EnsureCreatedAsync(caseName, CancellationToken.None);

        try
        {
            using var host = new HandlerTestHost(sqlServerConnectionString: connectionString);
            var payload =
                $$"""
                {
                  "caseName": "{{caseName}}",
                  "projectCode": "P",
                  "periodStart": "2024-01-01",
                  "databaseProvider": "sqlServer"
                }
                """;

            var exception = await Assert.ThrowsAsync<JetActionException>(
                () => host.DispatchAsync("project.create", payload));

            Assert.Equal(JetErrorCodes.InvalidPayload, exception.Code);
            Assert.Contains("本機沒有對應的案件登記", exception.Message);
            Assert.DoesNotContain("periodEnd", exception.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(host.ProjectsRoot, caseName)));
        }
        finally
        {
            await backend.DeleteAsync(caseName, CancellationToken.None);
        }
    }

    /// <summary>
    /// 症狀 B 回歸(journey:同一 host=同一組 app 生命週期單例,provider 路由快取跨 action 存活):
    /// 失敗的 sqlServer 建案不得汙染 provider 路由。旅程——孤兒 schema 已存在 →
    /// 階段 1:sqlServer 同名建案被擋(invalid_payload)→ 階段 2:同 session 改以 sqlite 同名建案成功 →
    /// 階段 3(物證,修復前紅):資料夾內 jet.db 真的建立。修復前:階段 1 的撞名檢查經 resolver 把
    /// sqlServer 判定寫進 app 生命週期快取、回滾只刪 project.json 不清快取,階段 2 的 EnsureCreated
    /// 被劫持到 SQL Server(孤兒 schema 已存在 → no-op),jet.db 從未建立、該案所有讀寫接到舊 schema。
    /// oracle:檔案系統物證(jet.db)+ manifest 契約(project.load 形狀)。
    /// </summary>
    [SqlServerFact]
    public async Task Create_SqliteRetryAfterOrphanCollision_RoutesToSqliteAndCreatesJetDb()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return; // 無 SQL Server → 跳過
        }

        // 孤兒化前置同上:後端有 schema、本機無登記,且本 host 的路由快取對此案名全冷。
        var caseName = $"孤兒路由 {Guid.NewGuid().ToString("N")[..8]}";
        var backend = new SqlServerProjectDatabase(new SqlServerConnectionOptions(connectionString));
        await backend.EnsureCreatedAsync(caseName, CancellationToken.None);

        try
        {
            // 同一 host:失敗建案與 sqlite 重試共用同一組單例(關鍵——快取是 app 生命週期)。
            using var host = new HandlerTestHost(sqlServerConnectionString: connectionString);

            // 階段 1:sqlServer 同名建案 → 後端孤兒殘留 → invalid_payload(訊息語意由上一測單獨鎖定)。
            var ex = await Assert.ThrowsAsync<JetActionException>(
                () => host.DispatchAsync("project.create", CaseNamePayload(caseName, "sqlServer")));
            Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);

            // 階段 2:同 session 改以 sqlite 同名建案 → 必須成功。
            var created = await host.DispatchAsync("project.create", CaseNamePayload(caseName, "sqlite"));
            Assert.True(created.GetProperty("ok").GetBoolean());

            // 階段 3(物證,修復前紅):SQLite 初始化必須真的執行——jet.db 存在。
            Assert.True(
                File.Exists(Path.Combine(host.ProjectsRoot, caseName, "jet.db")),
                "sqlite 同名重建後 jet.db 必須存在——不存在代表初始化被失敗建案殘留的 provider 路由快取劫持到 SQL Server no-op。");

            // 階段 4:載入後 provider 為 sqlite 且為空案(無匯入批次——不得接到孤兒 schema 的資料)。
            var loaded = await host.DispatchAsync("project.load", $$"""{ "projectId": "{{caseName}}" }""");
            Assert.Equal("sqlite", loaded.GetProperty("project").GetProperty("databaseProvider").GetString());
            Assert.Equal(JsonValueKind.Null, loaded.GetProperty("importState").GetProperty("gl").ValueKind);
        }
        finally
        {
            await backend.DeleteAsync(caseName, CancellationToken.None);
        }
    }

    [Fact]
    public async Task List_ReturnsCreatedProjectsNewestFirst()
    {
        using var host = new HandlerTestHost();

        await host.DispatchAsync("project.create", CreatePayload);
        await host.DispatchAsync(
            "project.create",
            CreatePayload.Replace("ENG-2024-001", "ENG-2024-002"));

        var data = await host.DispatchAsync("project.list");
        var projects = data.GetProperty("projects");

        Assert.Equal(2, projects.GetArrayLength());
        Assert.Equal("ENG-2024-002", projects[0].GetProperty("projectCode").GetString());
    }

    [Fact]
    public async Task Create_RecordsSqliteDatabaseProvider()
    {
        using var host = new HandlerTestHost();

        var created = await host.DispatchAsync("project.create", CreatePayload);
        var projectId = created.GetProperty("projectId").GetString()!;

        var loaded = await host.DispatchAsync("project.load", $$"""{ "projectId": "{{projectId}}" }""");

        // manifest「資料庫 provider 歸屬」：本地專案固定 sqlite。
        Assert.Equal("sqlite", loaded.GetProperty("project").GetProperty("databaseProvider").GetString());
    }

    [Fact]
    public async Task Create_RecordsDuckDbDatabaseProvider()
    {
        using var host = new HandlerTestHost();

        // 契約：project.create 接受 duckdb（第二本地引擎），且完整落地 + resume 回讀該 provider。
        var created = await host.DispatchAsync(
            "project.create",
            CreatePayload.Insert(CreatePayload.LastIndexOf('}'), """, "databaseProvider": "duckdb" """));
        var projectId = created.GetProperty("projectId").GetString()!;

        var loaded = await host.DispatchAsync("project.load", $$"""{ "projectId": "{{projectId}}" }""");
        Assert.Equal("duckdb", loaded.GetProperty("project").GetProperty("databaseProvider").GetString());

        // jet.duckdb 檔實際落地（本地檔引擎、與 sqlite 同資料夾模型）。
        Assert.True(File.Exists(Path.Combine(host.ProjectsRoot, projectId, "jet.duckdb")));
    }

    [Fact]
    public async Task Load_LegacyProjectJsonWithoutProvider_RejectsAsOldProject()
    {
        using var host = new HandlerTestHost();

        // 舊版 project.json（databaseProvider 欄位出現前的形狀）→ 明確拒絕，不再猜成 sqlite。
        var projectId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var projectDir = Path.Combine(host.ProjectsRoot, projectId);
        Directory.CreateDirectory(projectDir);
        await File.WriteAllTextAsync(Path.Combine(projectDir, "project.json"), $$"""
            {
              "projectId": "{{projectId}}",
              "projectCode": "LEGACY-001",
              "entityName": "舊版專案",
              "operatorId": "op",
              "periodStart": "2025-01-01",
              "periodEnd": "2025-12-31",
              "lastAccountingPeriodDate": null,
              "moneyScale": 10000,
              "roundingMode": "AwayFromZero",
              "createdUtc": "2026-06-01T00:00:00+00:00",
              "currentStep": 1,
              "schemaVersion": 1
            }
            """);

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            host.DispatchAsync("project.load", $$"""{ "projectId": "{{projectId}}" }"""));

        Assert.Equal(JetErrorCodes.InvalidProjectSchema, exception.Code);
        Assert.Equal(
            $"專案『{projectId}』的 project.json 是舊版 JET 建立的案件（缺少 databaseProvider），目前版本無法讀取。"
            + "請用目前版本重新建立案件，再重新匯入資料。",
            exception.Message);
    }

    [Fact]
    public async Task Load_UnknownOrTraversalId_ThrowsProjectNotFound()
    {
        using var host = new HandlerTestHost();

        var unknown = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("project.load", $$"""{ "projectId": "{{new string('0', 32)}}" }"""));
        Assert.Equal(JetErrorCodes.ProjectNotFound, unknown.Code);

        var traversal = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("project.load", """{ "projectId": "..\\..\\evil" }"""));
        Assert.Equal(JetErrorCodes.ProjectNotFound, traversal.Code);
    }

    [Fact]
    public async Task Delete_RemovesFolderAndDatabase_AndDropsFromList()
    {
        using var host = new HandlerTestHost();

        var created = await host.DispatchAsync("project.create", CreatePayload);
        var projectId = created.GetProperty("projectId").GetString()!;
        var projectDir = Path.Combine(host.ProjectsRoot, projectId);
        Assert.True(File.Exists(Path.Combine(projectDir, "jet.db")));

        var deleted = await host.DispatchAsync("project.delete", $$"""{ "projectId": "{{projectId}}" }""");

        Assert.True(deleted.GetProperty("ok").GetBoolean());
        Assert.Equal(projectId, deleted.GetProperty("projectId").GetString());
        // 資料夾(含 jet.db)整個移除,且不再出現在清單。
        Assert.False(Directory.Exists(projectDir));
        var list = await host.DispatchAsync("project.list");
        Assert.Equal(0, list.GetProperty("projects").GetArrayLength());
    }

    [Fact]
    public async Task Delete_UnknownProjectId_ThrowsProjectNotFound()
    {
        using var host = new HandlerTestHost();

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("project.delete", $$"""{ "projectId": "{{new string('0', 32)}}" }"""));

        Assert.Equal(JetErrorCodes.ProjectNotFound, ex.Code);
    }

    [Fact]
    public async Task List_AfterCreate_IncludesProviderAndNullLastOpened()
    {
        using var host = new HandlerTestHost();

        await host.DispatchAsync("project.create", CreatePayload);

        var project = (await host.DispatchAsync("project.list")).GetProperty("projects")[0];

        Assert.Equal("sqlite", project.GetProperty("databaseProvider").GetString());
        // 從未開啟過 → lastOpenedUtc 為 null(前端據此 fallback 顯示建立時間)。
        Assert.Equal(JsonValueKind.Null, project.GetProperty("lastOpenedUtc").ValueKind);
    }

    [Fact]
    public async Task Load_StampsLastOpenedUtc_VisibleInList()
    {
        using var host = new HandlerTestHost();

        var created = await host.DispatchAsync("project.create", CreatePayload);
        var projectId = created.GetProperty("projectId").GetString()!;

        await host.DispatchAsync("project.load", $$"""{ "projectId": "{{projectId}}" }""");

        var project = (await host.DispatchAsync("project.list")).GetProperty("projects")[0];
        // 載入後 lastOpenedUtc 應被戳記為可解析的時間戳(非 null)。
        Assert.Equal(JsonValueKind.String, project.GetProperty("lastOpenedUtc").ValueKind);
        Assert.True(DateTimeOffset.TryParse(project.GetProperty("lastOpenedUtc").GetString(), out _));
    }

    [SqlServerFact]
    public async Task Delete_SqlServerProject_DropsDatabaseAndFolder()
    {
        // 連線閘控:無 LocalDB/Express 即跳過(對齊 ProviderParityJourneyTests)。
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return;
        }

        using var host = new HandlerTestHost(sqlServerConnectionString: connectionString);

        var created = await host.DispatchAsync(
            "project.create",
            CreatePayload.Insert(CreatePayload.LastIndexOf('}'), """, "databaseProvider": "sqlServer" """));
        var projectId = created.GetProperty("projectId").GetString()!;

        try
        {
            Assert.True(await SchemaExistsAsync(connectionString, projectId)); // 建案已建 schema

            await host.DispatchAsync("project.delete", $$"""{ "projectId": "{{projectId}}" }""");

            // schema 被 DROP、資料夾移除。
            Assert.False(await SchemaExistsAsync(connectionString, projectId));
            Assert.False(Directory.Exists(Path.Combine(host.ProjectsRoot, projectId)));
        }
        finally
        {
            // 測試失敗時(delete 未成功)兜底清理,避免殘留 JET_ 暫時庫。
            await TempSqlServerProject.DropDatabaseAsync(connectionString, projectId);
        }
    }

    // ---- A/B/C 端到端流程驗收（驅動 GUI 按鈕背後的同一組 project.* actions） ----

    /// <summary>A：SQLite 建立→載入(currentStep=1,前端據此自動進入匯入)→在清單→刪除→移除。</summary>
    [Fact]
    public async Task Flow_Sqlite_CreateLoadListDelete()
    {
        using var host = new HandlerTestHost();

        // 建立（GUI 預設選項即 sqlite）。
        var created = await host.DispatchAsync("project.create", CreatePayload);
        Assert.True(created.GetProperty("ok").GetBoolean());
        var projectId = created.GetProperty("projectId").GetString()!;
        Assert.True(File.Exists(Path.Combine(host.ProjectsRoot, projectId, "jet.db")));

        // 載入：currentStep=1 → 前端 applyLoadedProject 映射到「匯入」步驟（自動前進，不停在建立畫面）。
        var loaded = await host.DispatchAsync("project.load", $$"""{ "projectId": "{{projectId}}" }""");
        Assert.Equal(1, loaded.GetProperty("project").GetProperty("currentStep").GetInt32());
        Assert.Equal("sqlite", loaded.GetProperty("project").GetProperty("databaseProvider").GetString());

        // 在清單、provider 標籤正確。
        var before = await host.DispatchAsync("project.list");
        var row = SingleProject(before, projectId);
        Assert.Equal("sqlite", row.GetProperty("databaseProvider").GetString());

        // 刪除 → 立即從清單移除、資料夾消失。
        await host.DispatchAsync("project.delete", $$"""{ "projectId": "{{projectId}}" }""");
        Assert.False(Directory.Exists(Path.Combine(host.ProjectsRoot, projectId)));
        Assert.False(ProjectInList(await host.DispatchAsync("project.list"), projectId));
    }

    /// <summary>B：SQL Server 建立(建 schema)→載入(currentStep=1)→在清單→刪除(DROP SCHEMA + 移除)。</summary>
    [SqlServerFact]
    public async Task Flow_SqlServer_CreateLoadListDelete_DropsDatabase()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return; // 無 LocalDB → 跳過
        }

        using var host = new HandlerTestHost(sqlServerConnectionString: connectionString);

        var created = await host.DispatchAsync(
            "project.create",
            CreatePayload.Insert(CreatePayload.LastIndexOf('}'), """, "databaseProvider": "sqlServer" """));
        var projectId = created.GetProperty("projectId").GetString()!;

        try
        {
            // 建立即在共用單庫建該專案 schema（連線已設定 → EnsureCreated 成功，不再卡在建立步驟）。
            Assert.True(created.GetProperty("ok").GetBoolean());
            Assert.True(await SchemaExistsAsync(connectionString, projectId));

            var loaded = await host.DispatchAsync("project.load", $$"""{ "projectId": "{{projectId}}" }""");
            Assert.Equal(1, loaded.GetProperty("project").GetProperty("currentStep").GetInt32());
            Assert.Equal("sqlServer", loaded.GetProperty("project").GetProperty("databaseProvider").GetString());

            var row = SingleProject(await host.DispatchAsync("project.list"), projectId);
            Assert.Equal("sqlServer", row.GetProperty("databaseProvider").GetString());

            // 刪除 → 該專案 schema 從共用單庫消失、清單移除、資料夾消失。
            await host.DispatchAsync("project.delete", $$"""{ "projectId": "{{projectId}}" }""");
            Assert.False(await SchemaExistsAsync(connectionString, projectId));
            Assert.False(Directory.Exists(Path.Combine(host.ProjectsRoot, projectId)));
            Assert.False(ProjectInList(await host.DispatchAsync("project.list"), projectId));
        }
        finally
        {
            await TempSqlServerProject.DropDatabaseAsync(connectionString, projectId);
        }
    }

    /// <summary>C：SQLite 與 SQL Server 專案並存時，清單各自顯示正確 provider 標籤。</summary>
    [SqlServerFact]
    public async Task Flow_Mixed_ListShowsBothProviders()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return; // 無 LocalDB → 跳過
        }

        using var host = new HandlerTestHost(sqlServerConnectionString: connectionString);

        var sqlite = await host.DispatchAsync(
            "project.create", CreatePayload.Replace("ENG-2024-001", "SQLITE-001"));
        var sqliteId = sqlite.GetProperty("projectId").GetString()!;

        var sqlServer = await host.DispatchAsync(
            "project.create",
            CreatePayload.Replace("ENG-2024-001", "SQLSRV-001")
                .Insert(CreatePayload.Replace("ENG-2024-001", "SQLSRV-001").LastIndexOf('}'),
                    """, "databaseProvider": "sqlServer" """));
        var sqlServerId = sqlServer.GetProperty("projectId").GetString()!;

        try
        {
            var list = await host.DispatchAsync("project.list");
            Assert.Equal("sqlite", SingleProject(list, sqliteId).GetProperty("databaseProvider").GetString());
            Assert.Equal("sqlServer", SingleProject(list, sqlServerId).GetProperty("databaseProvider").GetString());
        }
        finally
        {
            await TempSqlServerProject.DropDatabaseAsync(connectionString, sqlServerId);
        }
    }


    [Fact]
    public async Task Create_UnsupportedDatabaseProvider_ThrowsInvalidPayload()
    {
        using var host = new HandlerTestHost();

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync(
                "project.create",
                CreatePayload.Insert(CreatePayload.LastIndexOf('}'), """, "databaseProvider": "postgres" """)));

        Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
        Assert.Contains("未支援的 databaseProvider 'postgres'", ex.Message);
        Assert.Null(ex.Field);
    }

    // ---- 雙來源清單雛形（2026-07-07）：失聯降級（不需真 SQL——壞連線即模擬失聯） ----

    /// <summary>本機一份 sqlServer 快取 project.json（無真後端）。</summary>
    private static async Task WriteLocalSqlServerCacheAsync(string projectsRoot, string projectId)
    {
        var dir = Path.Combine(projectsRoot, projectId);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "project.json"), $$"""
            {
              "projectId": "{{projectId}}",
              "projectCode": "OFFLINE-1",
              "entityName": "離線快取案",
              "operatorId": "op",
              "periodStart": "2025-01-01",
              "periodEnd": "2025-12-31",
              "lastAccountingPeriodDate": null,
              "moneyScale": 10000,
              "roundingMode": "AwayFromZero",
              "createdUtc": "2026-06-01T00:00:00+00:00",
              "currentStep": 1,
              "schemaVersion": 1,
              "databaseProvider": "sqlServer",
              "sampleSeed": 1234567,
              "sampleSeedVersion": 2
            }
            """);
        // 上面補上的種子欄位是目前版本建案一定會寫入的值；缺欄位會被當成舊版案件拒絕。
    }

    /// <summary>
    /// registry 不可達（壞連線字串，快速 refuse）→ 清單不整體失敗：online.reachable=false、
    /// 本機 sqlServer 快取全標 localOnly、online.principal 仍為當前身分。oracle：manifest project.list 降級語意。
    /// </summary>
    [Fact]
    public async Task List_RegistryUnreachable_DegradesToLocalOnlyWithoutFailing()
    {
        // 指向 localhost:1（幾乎必然 refuse、Connect Timeout=1 快速失敗），模擬「伺服器失聯」。
        const string deadConnection =
            "Server=localhost,1;Database=JET_Test;Connect Timeout=1;Encrypt=False;TrustServerCertificate=True;User ID=x;Password=y";
        using var host = new HandlerTestHost(sqlServerConnectionString: deadConnection);
        await WriteLocalSqlServerCacheAsync(host.ProjectsRoot, "離線快取案");

        var data = await host.DispatchAsync("project.list");

        // 清單本身成功（絕不因 registry 失聯整體失敗）。
        var offline = SingleProject(data, "離線快取案");
        Assert.Equal("sqlServer", offline.GetProperty("databaseProvider").GetString());
        Assert.Equal("localOnly", offline.GetProperty("syncStatus").GetString());

        var online = data.GetProperty("online");
        Assert.False(online.GetProperty("reachable").GetBoolean());
        Assert.False(string.IsNullOrEmpty(online.GetProperty("principal").GetString()));
        Assert.False(string.IsNullOrEmpty(online.GetProperty("message").GetString()));
    }

    private static JsonElement SingleProject(JsonElement listResponse, string projectId)
    {
        var match = listResponse.GetProperty("projects").EnumerateArray()
            .Where(p => p.GetProperty("projectId").GetString() == projectId)
            .ToList();
        Assert.Single(match);
        return match[0];
    }

    private static bool ProjectInList(JsonElement listResponse, string projectId)
    {
        return listResponse.GetProperty("projects").EnumerateArray()
            .Any(p => p.GetProperty("projectId").GetString() == projectId);
    }

    // schema-per-project 模型下,測試用共用單庫為隔離庫 JET_Test（jetapp 擁有）——HandlerTestHost 以
    // singleDatabaseNameOverride:"JET_Test" 釘住,與 app 正式使用的 JET 庫隔離。
    private const string SingleDatabaseName = "JET_Test";

    /// <summary>
    /// 該專案衍生的 schema 是否存在於共用單庫(SCHEMA_ID;schema 名以參數綁定)。
    /// schema-per-project 模型:建案建 schema、刪案刪 schema,故「存在性」改以 schema 維度斷言(取代 per-DB 的 DB_ID)。
    /// </summary>
    private static async Task<bool> SchemaExistsAsync(string baseConnectionString, string projectId)
    {
        var schema = SqlServerProjectSchema.For(projectId);
        await using var connection = new SqlConnection(
            new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = SingleDatabaseName }.ConnectionString);
        await connection.OpenAsync();
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT CASE WHEN SCHEMA_ID(@s) IS NULL THEN 0 ELSE 1 END;";
        query.Parameters.AddWithValue("@s", schema);
        return Convert.ToInt32(await query.ExecuteScalarAsync()) == 1;
    }
}
