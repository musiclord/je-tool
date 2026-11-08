using System.Text.Json;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class ProjectHandlersTests
{
    private const string CreatePayload =
        """
        {
          "projectCode": "ENG-2024-001",
          "clientName": "範例股份有限公司",
          "operatorId": "auditor01",
          "periodStart": "2024-01-01",
          "periodEnd": "2024-12-31",
          "prepStartDate": "2024-12-31"
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
    public async Task Create_MissingOptionalClientName_Succeeds()
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
                { "projectCode": "X", "clientName": "E", "operatorId": "op",
                  "periodStart": "2024/01/01", "periodEnd": "2024-12-31" }
                """));

        Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
        // 前端不露出系統用詞（裁定 2026-10-19-04）：日期欄只送得出 yyyy-MM-dd，格式不對是程式送錯資料，訊息改為非預期錯誤那一句。
        // 首次失敗：20261019-111133190-cc5d8f23cf204a278811de85e6b1e15d；改為整句比對，並核對支援日誌記下的檢查代號。
        Assert.Equal(JetPayloadRejection.UnexpectedErrorMessage, ex.Message);
        Assert.Equal("payload_date_invalid", JetPayloadRejection.CheckOf(ex));
        Assert.Null(ex.Field);
    }

    // ---- 案件名稱(projectName)→ projectId/資料夾名(2026-06-22);選填,未提供回退 GUID ----

    private const string CreateWithProjectName =
        """
        {
          "projectName": "2025年度甲公司查核",
          "projectCode": "ENG-2024-001",
          "clientName": "範例股份有限公司",
          "operatorId": "auditor01",
          "periodStart": "2024-01-01",
          "periodEnd": "2024-12-31"
        }
        """;

    [Fact]
    public async Task Create_WithProjectName_UsesItAsProjectIdAndFolderName()
    {
        using var host = new HandlerTestHost();

        var data = await host.DispatchAsync("project.create", CreateWithProjectName);

        Assert.Equal("2025年度甲公司查核", data.GetProperty("projectId").GetString());
        Assert.True(File.Exists(
            Path.Combine(host.ProjectsRoot, "2025年度甲公司查核", "project.json")));
    }

    [Fact]
    public async Task Create_WithoutProjectName_FallsBackToGuidProjectId()
    {
        using var host = new HandlerTestHost();

        var data = await host.DispatchAsync("project.create", CreatePayload);

        Assert.Matches("^[0-9a-f]{32}$", data.GetProperty("projectId").GetString());
    }

    [Fact]
    public async Task Create_WithIllegalProjectName_ThrowsInvalidPayload()
    {
        using var host = new HandlerTestHost();

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync(
                "project.create", CreateWithProjectName.Replace("2025年度甲公司查核", "壞/名稱")));

        Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
        Assert.Equal(JetErrorFields.ProjectName, ex.Field);
    }

    [Fact]
    public async Task Create_DuplicateProjectName_ThrowsInvalidPayload()
    {
        using var host = new HandlerTestHost();

        await host.DispatchAsync("project.create", CreateWithProjectName);
        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("project.create", CreateWithProjectName));

        Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
        Assert.Contains("已存在", ex.Message);
        Assert.Equal(JetErrorFields.ProjectName, ex.Field);
    }

    // ---- 孤兒 schema 回歸（2026-07-07 實機回歸案例）：本機 projects/ 登記遺失、
    // SQL Server 單庫留下前一個 session 建的專案 schema 時,同名重建的兩種路徑 ----

    /// <summary>組 projectName＋provider 的建案 payload(孤兒 schema 回歸兩測共用)。</summary>
    private static string ProjectNamePayload(string projectName, string provider) =>
        $$"""
        { "projectName": "{{projectName}}", "projectCode": "P", "clientName": "E", "operatorId": "op",
          "periodStart": "2024-01-01", "periodEnd": "2024-12-31", "databaseProvider": "{{provider}}" }
        """;

    [Fact]
    public async Task List_ReturnsCreatedProjectsNewestFirst()
    {
        using var host = new HandlerTestHost();

        await host.DispatchAsync("project.create", CreatePayload);
        await host.DispatchAsync(
            "project.create",
            CreatePayload.Replace("ENG-2024-001", "ENG-2024-002"));

        var data = await host.DispatchAsync("project.listLocal");
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
              "clientName": "舊版專案",
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
        // 這句的用字依裁定 2026-10-14-01（第一次失敗：收據 20261014-012448805-68a8e8ea71bc4d6392585a586ab6b91e）。不寫檔名與缺了哪個設定。
        // 裁定 2026-10-30-23：舊版案件改由設定檔版本（這裡是第 1 版）判斷，訊息寫明刪除後重建。
        // 第一次失敗收據 20261030-070630463-47f67e5e918c4bab8e82fd87416d8e9c。
        Assert.Equal(
            $"案件「{projectId}」是舊版 JET 建立的，目前版本無法開啟。請在案件清單刪除這個案件，再用目前版本重新建立並匯入資料。",
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
        var list = await host.DispatchAsync("project.listLocal");
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

        var project = (await host.DispatchAsync("project.listLocal")).GetProperty("projects")[0];

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

        var project = (await host.DispatchAsync("project.listLocal")).GetProperty("projects")[0];
        // 載入後 lastOpenedUtc 應被戳記為可解析的時間戳(非 null)。
        Assert.Equal(JsonValueKind.String, project.GetProperty("lastOpenedUtc").ValueKind);
        Assert.True(DateTimeOffset.TryParse(project.GetProperty("lastOpenedUtc").GetString(), out _));
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
        var before = await host.DispatchAsync("project.listLocal");
        var row = SingleProject(before, projectId);
        Assert.Equal("sqlite", row.GetProperty("databaseProvider").GetString());

        // 刪除 → 立即從清單移除、資料夾消失。
        await host.DispatchAsync("project.delete", $$"""{ "projectId": "{{projectId}}" }""");
        Assert.False(Directory.Exists(Path.Combine(host.ProjectsRoot, projectId)));
        Assert.False(ProjectInList(await host.DispatchAsync("project.listLocal"), projectId));
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
        // 這句的用字依裁定 2026-10-14-01（第一次失敗：收據 20261014-012448805-68a8e8ea71bc4d6392585a586ab6b91e）。畫面只送三種資料庫，
        // 其他值只有程式送錯資料才會出現：顯示非預期錯誤那一句，送出的值不出現。
        Assert.Equal(JetPayloadRejection.UnexpectedErrorMessage, ex.Message);
        Assert.Equal("database_provider_unsupported", JetPayloadRejection.CheckOf(ex));
        Assert.DoesNotContain("postgres", ex.Message);
        Assert.Null(ex.Field);
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
}
