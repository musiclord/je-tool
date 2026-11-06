using System.Globalization;
using System.Text;
using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 第 22 步盤點的第 6 項：SQLite 先把資料寫進記錄檔再併回資料庫檔，原本離開案件時不清空連線池，產品也不併回，
/// 一百萬筆的匯入後記錄檔一直有幾百 MB，JET 開著時只複製資料庫檔也會拿到不完整的副本。現在每項改資料的作業結束後併回並清空記錄檔，
/// 離開案件、開案失敗與結束 JET 時關閉這個案件的連線（見 IProjectDatabaseCheckpoint）。這是新增的測試。
/// DuckDB 每項作業結束時關閉資料庫，關閉時就會併回，所以同樣的情境也測 DuckDB，證明它不需要另外處理。
/// </summary>
/// <remarks>
/// SQLite 的情境另外開一條不進連線池的連線守在旁邊：別的測試清空全部連線池時，最後一條連線關閉也會併回並刪掉記錄檔，
/// 有這條連線在，記錄檔清空只可能來自作業結束後的併回。這條連線沒有開交易，不擋併回。
/// </remarks>
public sealed class ProjectDatabaseCheckpointTests
{
    private const int GlRows = 5_000;

    // 比第一次送出匯入進度的兩萬列多，取消時交易一定還在寫。
    private const int CancelledGlRows = 30_000;

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task AfterEachWrite_TheLogIsEmpty_AndTheDatabaseFileAloneHasTheData(string provider)
    {
        using var workspace = new TempProjectRoot();
        var projects = Path.Combine(workspace.Path, "projects");
        Directory.CreateDirectory(projects);
        var name = $"checkpoint-write-{provider}";
        var gl = WriteGl(workspace.Path, "gl.csv", GlRows);

        using var host = new HandlerTestHost(projectsRootPath: projects);
        await host.DispatchAsync("project.create", CreatePayload(name, provider));
        var databasePath = DatabasePath(projects, name, provider);
        using var guard = OpenGuard(databasePath, provider);

        await ImportAsync(host, gl);
        Assert.Equal(0, LogLength(databasePath, provider));
        Assert.Equal(GlRows, await CountInCopyAsync(databasePath, workspace.Path, "after-import", name, provider, "pbc_gl"));

        await CommitGlAsync(host);
        Assert.Equal(0, LogLength(databasePath, provider));
        Assert.Equal(GlRows, await CountInCopyAsync(databasePath, workspace.Path, "after-mapping", name, provider, "gl"));
    }

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task AfterACancelledLargeImport_TheLogIsEmpty_AndTheEarlierDataStays(string provider)
    {
        using var workspace = new TempProjectRoot();
        var projects = Path.Combine(workspace.Path, "projects");
        Directory.CreateDirectory(projects);
        var name = $"checkpoint-cancel-{provider}";
        var small = WriteGl(workspace.Path, "small.csv", 3);
        var large = WriteGl(workspace.Path, "large.csv", CancelledGlRows);

        using var cancellation = new CancellationTokenSource();
        var canceller = new CancelAtFirstEvent("import.progress", cancellation);
        using var host = new HandlerTestHost(projectsRootPath: projects, eventPublisher: canceller);
        await host.DispatchAsync("project.create", CreatePayload(name, provider));
        var databasePath = DatabasePath(projects, name, provider);
        using var guard = OpenGuard(databasePath, provider);
        await ImportAsync(host, small);

        // 匯入大檔寫到兩萬列時取消：沒提交的資料原本會一直留在記錄檔裡。
        canceller.Armed = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImportAsync(host, large, cancellation.Token));
        Assert.True(canceller.Fired, "前置條件：取消應發生在匯入寫到一半時");

        Assert.Equal(0, LogLength(databasePath, provider));
        Assert.Equal(3, await CountInCopyAsync(databasePath, workspace.Path, "after-cancel", name, provider, "pbc_gl"));
    }

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task AfterLeavingTheCase_JetNoLongerHoldsTheDatabaseFile(string provider)
    {
        using var workspace = new TempProjectRoot();
        var projects = Path.Combine(workspace.Path, "projects");
        Directory.CreateDirectory(projects);
        var name = $"checkpoint-leave-{provider}";
        var gl = WriteGl(workspace.Path, "gl.csv", GlRows);

        using var host = new HandlerTestHost(projectsRootPath: projects);
        await host.DispatchAsync("project.create", CreatePayload(name, provider));
        await ImportAsync(host, gl);
        await CommitGlAsync(host);
        await host.DispatchAsync("project.releaseLock");

        // JET 還開著，只是回到案件清單：記錄檔與共用記憶體檔都已刪掉，資料庫檔沒有被任何程式開著。
        var databasePath = DatabasePath(projects, name, provider);
        AssertNoSideFiles(databasePath, provider);
        Assert.True(DuckDbFileProbe.CanOpenExclusively(databasePath), "離開案件後 JET 不應再開著案件資料庫");

        // 再開同一個案件照常可以讀寫。
        await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = name }));
        await CommitGlAsync(host);
        Assert.Equal(GlRows, await CountInCopyAsync(databasePath, workspace.Path, "after-reload", name, provider, "gl"));
    }

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task AFailedLoad_LeavesTheDatabaseFileClosed(string provider)
    {
        using var workspace = new TempProjectRoot();
        var projects = Path.Combine(workspace.Path, "projects");
        Directory.CreateDirectory(projects);
        var name = $"checkpoint-failed-load-{provider}";
        using (var creator = new HandlerTestHost(projectsRootPath: projects))
        {
            await creator.DispatchAsync("project.create", CreatePayload(name, provider));
            await creator.DispatchAsync("project.releaseLock");
        }

        // 把版本改成舊版：開案會在讀過資料庫之後被拒絕。
        var databasePath = DatabasePath(projects, name, provider);
        await ExecuteDirectAsync(databasePath, provider, "UPDATE schema_info SET value = '19' WHERE key = 'schema_version';");

        using var host = new HandlerTestHost(projectsRootPath: projects);
        var rejected = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = name })));
        Assert.Equal(JetErrorCodes.InvalidProjectSchema, rejected.Code);

        // 沒有進入這個案件，開案途中開過的連線也要關掉，審計員才能在 JET 開著時搬移或刪除這個資料夾。
        AssertNoSideFiles(databasePath, provider);
        Assert.True(DuckDbFileProbe.CanOpenExclusively(databasePath), "開案被拒後 JET 不應再開著案件資料庫");
    }

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task EndingJetWithoutLeavingTheCase_ClosesTheDatabaseFile(string provider)
    {
        using var workspace = new TempProjectRoot();
        var projects = Path.Combine(workspace.Path, "projects");
        Directory.CreateDirectory(projects);
        var name = $"checkpoint-exit-{provider}";
        var gl = WriteGl(workspace.Path, "gl.csv", GlRows);

        // 畫面已經無法使用時，主機不經過離開案件就直接關窗；runtime 釋放時要關閉目前案件的資料庫。
        using (var host = new HandlerTestHost(projectsRootPath: projects))
        {
            await host.DispatchAsync("project.create", CreatePayload(name, provider));
            await ImportAsync(host, gl);
        }

        var databasePath = DatabasePath(projects, name, provider);
        AssertNoSideFiles(databasePath, provider);
        Assert.True(DuckDbFileProbe.CanOpenExclusively(databasePath), "結束 JET 後不應再開著案件資料庫");
        Assert.Equal(GlRows, await CountInCopyAsync(databasePath, workspace.Path, "after-exit", name, provider, "pbc_gl"));
    }

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task RecalculatingFilterHitsInAQuery_AlsoEmptiesTheLog(string provider)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        var databasePath = DatabasePath(host.ProjectsRoot, context.ProjectId, provider);
        using var guard = OpenGuard(databasePath, provider);
        await host.DispatchAsync("filter.commit", ScenarioPayload());

        // 用開案規則升版時的整批取代把結果標成過期，這次寫入不經過 dispatcher，記錄檔裡留著沒併回的資料。
        await SavedScenarioReplay.ReplaceLikeRuleUpgradeAsync(host, context.ProjectId, provider);
        if (provider == ProjectDocument.DefaultDatabaseProvider)
        {
            Assert.True(LogLength(databasePath, provider) > 0, "前置條件：記錄檔裡應有還沒併回的資料");
        }

        // 查詢先補算過期的命中結果再回傳；補算是查詢裡的寫入，結束時同樣要併回。
        var scenarios = await host.DispatchAsync(
            "query.highRiskSummaryScenarios", JsonSerializer.Serialize(new { scenarioPosition = (int?)null, pageSize = 20, search = (string?)null }));
        Assert.True(Assert.Single(scenarios.GetProperty("scenarios").EnumerateArray()).GetProperty("rowHitCount").GetInt64() > 0);
        Assert.Equal(0, await ScalarDirectAsync(databasePath, provider, "SELECT filter_stale FROM result_stale_state WHERE singleton = 1;"));
        Assert.Equal(0, LogLength(databasePath, provider));
    }

    [Fact]
    public async Task CheckpointAndClose_DoNotCreateAMissingDatabase()
    {
        using var workspace = new TempProjectRoot();
        var database = new SqliteProjectDatabase(new JetProjectFolder(workspace.Path));

        await database.CheckpointAsync("missing-case", CancellationToken.None);
        await database.CloseAsync("missing-case", CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "missing-case")), "收尾不應替不存在的案件建資料夾或資料庫");
    }

    /// <summary>開始等待後，第一次收到指定的進度事件時要求取消；交易這時還在寫。</summary>
    private sealed class CancelAtFirstEvent(string eventName, CancellationTokenSource cancellation) : IJetEventPublisher
    {
        public volatile bool Armed;
        public volatile bool Fired;

        public void Publish(string name, object payload)
        {
            if (Armed && !Fired && name == eventName)
            {
                Fired = true;
                cancellation.Cancel();
            }
        }
    }

    private static string CreatePayload(string name, string provider) =>
        JsonSerializer.Serialize(new { projectName = name, periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider });

    private static Task<JsonElement> ImportAsync(HandlerTestHost host, string path, CancellationToken cancellationToken = default) =>
        host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new
        {
            mode = "replace",
            sources = new[] { new { filePath = path, fileName = Path.GetFileName(path), encoding = "utf-8", delimiter = "," } },
        }), cancellationToken);

    private static Task<JsonElement> CommitGlAsync(HandlerTestHost host) =>
        host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping = new Dictionary<string, string>
            {
                ["voucherNumber"] = "Document", ["glDate"] = "Date", ["accountNumber"] = "Account", ["accountName"] = "Name",
                ["description"] = "Description", ["amount"] = "Amount",
            },
            amountMode = "signed",
            rdeFields = Array.Empty<object>(),
        }));

    private static string ScenarioPayload() => JsonSerializer.Serialize(new
    {
        scenarios = new[] { new
        {
            name = "併回記錄檔", rationale = "合成資料的補算測試",
            groups = new[] { new { conditions = new[] { new { type = "numRange", field = "amount", from = "1" } } } }
        } }
    });

    /// <summary>合成的 GL：每張傳票一借一貸。</summary>
    private static string WriteGl(string directory, string fileName, int rows)
    {
        var text = new StringBuilder("Document,Date,Account,Name,Description,Amount\n");
        for (var index = 0; index < rows; index++)
        {
            var debit = index % 2 == 0;
            text.Append(CultureInfo.InvariantCulture, $"V{index / 2:000000},2025-03-05,{(debit ? "1101" : "4101")},")
                .Append(debit ? "Synthetic cash" : "Synthetic revenue")
                .Append(CultureInfo.InvariantCulture, $",Synthetic entry {index},{(debit ? "10.00" : "-10.00")}\n");
        }

        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static ILocalProjectDatabase Database(string projects, string provider)
    {
        var folder = new JetProjectFolder(projects);
        return provider == ProjectDocument.DuckDbDatabaseProvider ? new DuckDbProjectDatabase(folder) : new SqliteProjectDatabase(folder);
    }

    private static string DatabasePath(string projects, string name, string provider) =>
        Database(projects, provider).GetDatabasePath(name);

    /// <summary>SQLite 的記錄檔是 jet.db-wal，DuckDB 的是 jet.duckdb.wal；檔案不在時算 0。</summary>
    private static long LogLength(string databasePath, string provider)
    {
        var path = databasePath + (provider == ProjectDocument.DuckDbDatabaseProvider ? ".wal" : "-wal");
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    private static void AssertNoSideFiles(string databasePath, string provider)
    {
        var suffixes = provider == ProjectDocument.DuckDbDatabaseProvider ? new[] { ".wal" } : new[] { "-wal", "-shm" };
        foreach (var suffix in suffixes)
        {
            Assert.False(File.Exists(databasePath + suffix), $"關閉後不應留下 {Path.GetFileName(databasePath + suffix)}");
        }
    }

    /// <summary>SQLite 才需要守著的連線，見類別說明；DuckDB 回 null，開著連線會讓資料庫實體一直不關閉。</summary>
    private static SqliteConnection? OpenGuard(string databasePath, string provider)
    {
        if (provider == ProjectDocument.DuckDbDatabaseProvider)
        {
            return null;
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    /// <summary>
    /// 只複製資料庫檔，不帶記錄檔，再數副本裡的列數：JET 還開著案件時複製資料夾，拿到的副本要和 JET 看到的相同。
    /// </summary>
    private static async Task<long> CountInCopyAsync(
        string databasePath, string workspace, string copyName, string name, string provider, string table)
    {
        var copyRoot = Path.Combine(workspace, copyName);
        var copyPath = Database(copyRoot, provider).GetDatabasePath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(copyPath)!);
        await using (var source = new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        await using (var target = new FileStream(copyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await source.CopyToAsync(target);
        }

        // 表名都是上面固定的幾個，不來自外部輸入。
        return await ScalarDirectAsync(copyPath, provider, $"SELECT COUNT(*) FROM {table};");
    }

    /// <summary>不經 JET 的連線池直接讀一個值，讀完就關閉，不留下開著的檔案。</summary>
    private static async Task<long> ScalarDirectAsync(string databasePath, string provider, string sql)
    {
        await using var connection = DirectConnection(databasePath, provider);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteDirectAsync(string databasePath, string provider, string sql)
    {
        await using var connection = DirectConnection(databasePath, provider);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static System.Data.Common.DbConnection DirectConnection(string databasePath, string provider) =>
        provider == ProjectDocument.DuckDbDatabaseProvider
            ? new DuckDB.NET.Data.DuckDBConnection($"DataSource={databasePath}")
            : new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
}
