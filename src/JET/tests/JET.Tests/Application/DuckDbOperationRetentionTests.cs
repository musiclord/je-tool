using System.Text.Json;
using JET.Application;
using JET.Bridge;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// DuckDB 案件在一次操作期間保持資料庫開啟（2026-10-02 核准的提速方向）：dispatcher 只替持有清單內的
/// action 持有當前案件資料庫，操作結束（含失敗與取消）就釋放；刪除仍在使用中的案件回 operation_in_progress；
/// project.load 由 handler 自己持有並在離開時釋放。斷言一律看檔案能否獨占開啟，不看內部計數。
/// </summary>
public sealed class DuckDbOperationRetentionTests
{
    private sealed class ProbeHandler(string action, string databasePath) : IApplicationActionHandler
    {
        public string Action { get; } = action;

        public bool? ExclusiveOpenDuringHandler { get; private set; }

        public Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
        {
            ExclusiveOpenDuringHandler = DuckDbFileProbe.CanOpenExclusively(databasePath);
            return Task.FromResult<object?>(new { ok = true });
        }
    }

    private sealed class ThrowingHandler(string action, string databasePath) : IApplicationActionHandler
    {
        public string Action { get; } = action;

        public bool? ExclusiveOpenDuringHandler { get; private set; }

        public Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
        {
            ExclusiveOpenDuringHandler = DuckDbFileProbe.CanOpenExclusively(databasePath);
            throw new InvalidOperationException("handler failed");
        }
    }

    private sealed class CancellableHandler(string action, string databasePath) : IApplicationActionHandler
    {
        public string Action { get; } = action;

        public bool? ExclusiveOpenDuringHandler { get; private set; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
        {
            ExclusiveOpenDuringHandler = DuckDbFileProbe.CanOpenExclusively(databasePath);
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }
    }

    /// <summary>模擬 dev.db.overview 與寫入作業重疊：持有中開唯讀連線讀取，之後的一般寫入不得失敗。</summary>
    private sealed class ReadOnlyThenWriteHandler(string action, DuckDbProjectDatabase database, string projectId)
        : IApplicationActionHandler
    {
        public string Action { get; } = action;

        public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
        {
            await using (var readOnly = database.CreateReadOnlyConnection(projectId))
            {
                await readOnly.OpenAsync(cancellationToken);
                await using var count = readOnly.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM app_message_log;";
                _ = await count.ExecuteScalarAsync(cancellationToken);
            }

            await using var connection = database.CreateConnection(projectId);
            await connection.OpenAsync(cancellationToken);
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO app_message_log (occurred_utc, level, text) VALUES ('2024-01-01T00:00:00Z', 'info', 'after-read-only');";
            return await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<(DuckDbProjectDatabase Database, ProjectSession Session, string Path)> CreateLoadedCaseAsync(
        TempProjectRoot root,
        string projectId)
    {
        var database = new DuckDbProjectDatabase(new JetProjectFolder(root.Path));
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        // 2026-10-02 資料庫分流簡化：只設案件編號的舊 Enter(string) 已移除，改帶資料庫組進入。
        // 這裡的探測 handler 自己開連線、不經資料庫組，所以放一組全部維持 null 的 DuckDB 資料庫組。
        var session = new ProjectSession();
        session.Enter(projectId, TestProjectRepositories.Unconfigured(ProjectDocument.DuckDbDatabaseProvider));
        return (database, session, database.GetDatabasePath(projectId));
    }

    private static ActionDispatcher Dispatcher(
        DuckDbProjectDatabase database,
        ProjectSession session,
        params IApplicationActionHandler[] handlers)
        => new(
            handlers,
            NullLogger<ActionDispatcher>.Instance,
            session,
            engineErrorTranslator: null,
            cancellationRegistry: null,
            executionGate: null,
            databaseRetention: database);

    [Theory]
    [InlineData("validate.run")]       // 變更型
    [InlineData("project.update")]
    [InlineData("query.dataPreview")]  // 併行
    public async Task RetainingAction_HoldsDatabaseDuringHandler_AndReleasesAfterDispatch(string action)
    {
        using var root = new TempProjectRoot();
        var (database, session, path) = await CreateLoadedCaseAsync(root, "分派持有");
        var handler = new ProbeHandler(action, path);
        var dispatcher = Dispatcher(database, session, handler);
        using var payload = JsonDocument.Parse("{}");

        await dispatcher.DispatchAsync(action, payload.RootElement, CancellationToken.None);

        Assert.False(handler.ExclusiveOpenDuringHandler, "持有清單內的 action 執行中，資料庫檔應被占用");
        Assert.True(DuckDbFileProbe.CanOpenExclusively(path), "dispatch 結束後檔案應可獨占開啟");
    }

    [Theory]
    [InlineData("project.saveProgress")]
    [InlineData("log.append")]
    public async Task NonRetainingAction_DoesNotHoldDatabase(string action)
    {
        using var root = new TempProjectRoot();
        var (database, session, path) = await CreateLoadedCaseAsync(root, "分派不持有");
        var handler = new ProbeHandler(action, path);
        var dispatcher = Dispatcher(database, session, handler);
        using var payload = JsonDocument.Parse("{}");

        await dispatcher.DispatchAsync(action, payload.RootElement, CancellationToken.None);

        Assert.True(handler.ExclusiveOpenDuringHandler, "不在持有清單內的 action 不得持有資料庫");
    }

    [Fact]
    public async Task RetainingAction_WithoutCurrentProject_DoesNotHoldDatabase()
    {
        using var root = new TempProjectRoot();
        var (database, _, path) = await CreateLoadedCaseAsync(root, "未載入案件");
        var handler = new ProbeHandler("validate.run", path);
        var dispatcher = Dispatcher(database, new ProjectSession(), handler);
        using var payload = JsonDocument.Parse("{}");

        await dispatcher.DispatchAsync("validate.run", payload.RootElement, CancellationToken.None);

        Assert.True(handler.ExclusiveOpenDuringHandler, "沒有當前案件時不得持有任何資料庫");
    }

    [Fact]
    public async Task HandlerThrows_DatabaseIsReleasedAfterDispatch()
    {
        using var root = new TempProjectRoot();
        var (database, session, path) = await CreateLoadedCaseAsync(root, "失敗釋放");
        var handler = new ThrowingHandler("filter.commit", path);
        var dispatcher = Dispatcher(database, session, handler);
        using var payload = JsonDocument.Parse("{}");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => dispatcher.DispatchAsync("filter.commit", payload.RootElement, CancellationToken.None));

        Assert.False(handler.ExclusiveOpenDuringHandler, "handler 執行中資料庫應被持有");
        Assert.True(DuckDbFileProbe.CanOpenExclusively(path), "handler 失敗後資料庫應已釋放");
    }

    [Fact]
    public async Task HandlerCancelled_DatabaseIsReleasedAfterDispatch()
    {
        using var root = new TempProjectRoot();
        var (database, session, path) = await CreateLoadedCaseAsync(root, "取消釋放");
        var handler = new CancellableHandler("prescreen.run", path);
        var dispatcher = Dispatcher(database, session, handler);
        using var payload = JsonDocument.Parse("{}");
        using var cancellation = new CancellationTokenSource();

        var dispatch = dispatcher.DispatchAsync("prescreen.run", payload.RootElement, cancellation.Token);
        await handler.Entered.Task;
        Assert.False(DuckDbFileProbe.CanOpenExclusively(path), "取消前資料庫應被持有");

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);

        Assert.False(handler.ExclusiveOpenDuringHandler);
        Assert.True(DuckDbFileProbe.CanOpenExclusively(path), "取消後資料庫應已釋放");
    }

    [Fact]
    public async Task ReadOnlyConnectionDuringRetainedWrite_DoesNotBreakLaterWrites()
    {
        using var root = new TempProjectRoot();
        const string projectId = "唯讀重疊";
        var (database, session, path) = await CreateLoadedCaseAsync(root, projectId);
        var handler = new ReadOnlyThenWriteHandler("validate.run", database, projectId);
        var dispatcher = Dispatcher(database, session, handler);
        using var payload = JsonDocument.Parse("{}");

        var written = await dispatcher.DispatchAsync("validate.run", payload.RootElement, CancellationToken.None);

        Assert.Equal(1, written);
        Assert.True(DuckDbFileProbe.CanOpenExclusively(path));
    }

    [Fact]
    public async Task ProjectDelete_WhileDatabaseRetained_IsRejectedWithoutDeletingAnything()
    {
        using var root = new TempProjectRoot();
        var projectDir = Path.Combine(root.Path, "刪除中使用");
        using (var host = new HandlerTestHost(projectsRootPath: root.Path))
        {
            await host.DispatchAsync("project.create", CreatePayload("刪除中使用"));
        }

        var folder = new JetProjectFolder(root.Path);
        var database = new DuckDbProjectDatabase(folder);
        using var lockService = new LocalFileLockService(folder);
        var session = new ProjectSession();
        var handler = new ProjectDeleteHandler(
            new JsonFileProjectStore(folder),
            TestProjectRepositories.CatalogWithSameObjects(
                TestProjectRepositories.Unconfigured(ProjectDocument.DuckDbDatabaseProvider) with
                {
                    DatabaseDeleter = database,
                    ReportArtifactStore = new ProjectReportArtifactStore(folder),
                    DeletionLockService = lockService
                }),
            registry: null!, // 本地 duckdb 案件不查線上登記簿
            new CurrentPrincipal("retention-test"),
            session);
        var dbPath = database.GetDatabasePath("刪除中使用");
        using var payload = JsonDocument.Parse("""{ "projectId": "刪除中使用" }""");

        var retention = database.TryRetain("刪除中使用");
        Assert.NotNull(retention);
        var rejected = await Assert.ThrowsAsync<JetActionException>(
            () => handler.HandleAsync(payload.RootElement, CancellationToken.None));
        Assert.Equal(JetErrorCodes.OperationInProgress, rejected.Code);
        Assert.True(File.Exists(dbPath), "被擋下的刪除不得刪掉資料庫檔");
        Assert.True(File.Exists(Path.Combine(projectDir, JetProjectFolder.ProjectJsonFileName)),
            "被擋下的刪除不得刪掉 project.json");

        retention!.Dispose();
        await handler.HandleAsync(payload.RootElement, CancellationToken.None);
        Assert.False(Directory.Exists(projectDir), "釋放後刪除應移除整個案件資料夾");
    }

    [Fact]
    public async Task DeleteThenRecreateSameName_NewCaseStartsEmpty()
    {
        using var host = new HandlerTestHost();
        const string name = "同名重建";
        await host.DispatchAsync("project.create", CreatePayload(name));
        var glFile = WriteGlWorkbook();
        try
        {
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = glFile }));
            Assert.Equal(2L, await TableRowCountAsync(host, "staging_gl_raw_row"));

            await host.DispatchAsync("project.delete", $$"""{ "projectId": "{{name}}" }""");
            var dbPath = Path.Combine(host.ProjectsRoot, name, DuckDbProjectDatabase.DatabaseFileName);
            Assert.False(File.Exists(dbPath));

            // 同名重建後若沿用舊的資料庫實體，會讀到上一個案件的兩列 GL。
            await host.DispatchAsync("project.create", CreatePayload(name));
            Assert.Equal(0L, await TableRowCountAsync(host, "staging_gl_raw_row"));
            Assert.Equal(0L, await TableRowCountAsync(host, "import_batch"));
            Assert.True(DuckDbFileProbe.CanOpenExclusively(dbPath), "操作之間資料庫檔應解除鎖定");
        }
        finally
        {
            TestWorkbookBuilder.Delete(glFile);
        }
    }

    [Fact]
    public async Task ProjectLoad_ReleasesDatabaseAfterSuccess_AndAfterLegacySchemaRejection()
    {
        using var root = new TempProjectRoot();
        const string name = "載入釋放";
        var dbPath = Path.Combine(root.Path, name, DuckDbProjectDatabase.DatabaseFileName);
        using (var creator = new HandlerTestHost(projectsRootPath: root.Path))
        {
            await creator.DispatchAsync("project.create", CreatePayload(name));
        }

        using (var loader = new HandlerTestHost(projectsRootPath: root.Path))
        {
            await loader.DispatchAsync("project.load", $$"""{ "projectId": "{{name}}" }""");
            Assert.True(DuckDbFileProbe.CanOpenExclusively(dbPath), "載入成功後資料庫應已釋放");
        }

        // 換成第 5 版的舊資料庫：載入會被拒絕。
        File.Delete(dbPath);
        var seedDatabase = new DuckDbProjectDatabase(new JetProjectFolder(root.Path));
        await using (var connection = seedDatabase.CreateConnection(name))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                CREATE TABLE schema_info (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO schema_info (key, value) VALUES ('schema_version', '5');
                CHECKPOINT;
                """;
            await seed.ExecuteNonQueryAsync();
        }

        using var rejectedLoader = new HandlerTestHost(projectsRootPath: root.Path);
        var rejected = await Assert.ThrowsAsync<JetActionException>(
            () => rejectedLoader.DispatchAsync("project.load", $$"""{ "projectId": "{{name}}" }"""));
        Assert.Equal(JetErrorCodes.InvalidProjectSchema, rejected.Code);
        Assert.True(DuckDbFileProbe.CanOpenExclusively(dbPath), "載入被拒後資料庫應已釋放");

        // 工作鎖也已釋放：另一個執行個體再載入，得到的仍是舊版 schema 錯誤，而不是案件被鎖定。
        using var otherInstance = new HandlerTestHost(projectsRootPath: root.Path);
        var again = await Assert.ThrowsAsync<JetActionException>(
            () => otherInstance.DispatchAsync("project.load", $$"""{ "projectId": "{{name}}" }"""));
        Assert.Equal(JetErrorCodes.InvalidProjectSchema, again.Code);
    }

    [Fact]
    public void RetainingActions_AreRegisteredAndClassified_AndCaseLifecycleActionsNeverRetain()
    {
        using var host = new HandlerTestHost(enableDevTools: true);
        var registered = host.Dispatcher.RegisteredActions.ToHashSet(StringComparer.Ordinal);

        foreach (var action in ActionExecutionPolicy.ProjectDatabaseRetainingActionNames)
        {
            Assert.True(registered.Contains(action), $"持有清單裡的 {action} 沒有在 dispatcher 註冊");
            Assert.True(ActionExecutionPolicy.IsClassified(action), $"持有清單裡的 {action} 沒有歸類");
            Assert.True(ActionExecutionPolicy.RetainsProjectDatabase(action));
        }

        foreach (var action in new[]
                 {
                     "project.create", "project.load", "project.delete", "project.releaseLock", "dev.db.reconcile",
                 })
        {
            Assert.False(ActionExecutionPolicy.RetainsProjectDatabase(action), $"{action} 不得由 dispatcher 持有資料庫");
        }

        Assert.False(ActionExecutionPolicy.RetainsProjectDatabase("not.a.real.action"));
    }

    private static string CreatePayload(string name) =>
        JsonSerializer.Serialize(new
        {
            caseName = name,
            projectCode = "RET",
            entityName = "持有實體",
            operatorId = "op",
            periodStart = "2024-01-01",
            periodEnd = "2024-12-31",
            databaseProvider = "duckdb",
        });

    private static string WriteGlWorkbook() =>
        TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 1).Value = "傳票號碼"; ws.Cell(1, 2).Value = "傳票日期"; ws.Cell(1, 3).Value = "科目代號"; ws.Cell(1, 4).Value = "金額";
            ws.Cell(2, 1).Value = "JV-1"; ws.Cell(2, 2).Value = "2024-03-01"; ws.Cell(2, 3).Value = "1101"; ws.Cell(2, 4).Value = "100";
            ws.Cell(3, 1).Value = "JV-2"; ws.Cell(3, 2).Value = "2024-03-02"; ws.Cell(3, 3).Value = "4101"; ws.Cell(3, 4).Value = "100";
        });

    private static async Task<long> TableRowCountAsync(HandlerTestHost host, string tableName)
    {
        var overview = await host.DispatchAsync("dev.db.overview");
        foreach (var table in overview.GetProperty("tables").EnumerateArray())
        {
            if (table.GetProperty("name").GetString() == tableName)
            {
                return table.GetProperty("rowCount").GetInt64();
            }
        }

        throw new Xunit.Sdk.XunitException($"dev.db.overview 沒有回報資料表 {tableName}");
    }
}
