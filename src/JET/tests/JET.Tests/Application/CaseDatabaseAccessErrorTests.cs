using System.Globalization;
using System.Text;
using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 第 22 步盤點的第 4 項：唯讀、防毒軟體這類程式占用檔案與磁碟已滿時，原本畫面只寫「發生非預期的錯誤」，重試也不會成功。
/// 這裡用合成資料在真正的檔案上重現這幾種情形，核對審計員看到的原因與下一步，以及案件資料保持原狀、排除後可以繼續。
/// 這是新增的測試。
/// </summary>
public sealed class CaseDatabaseAccessErrorTests
{
    [Fact]
    public async Task ReadOnlySqliteDatabase_RejectsTheWrite_KeepsTheData_AndWorksAfterReopening()
    {
        using var workspace = new TempProjectRoot();
        var projects = Projects(workspace);
        const string name = "read-only-sqlite";
        var small = WriteGl(workspace.Path, "small.csv", 3);
        var next = WriteGl(workspace.Path, "next.csv", 8);

        using var host = new HandlerTestHost(projectsRootPath: projects);
        await host.DispatchAsync("project.create", CreatePayload(name, ProjectDocument.DefaultDatabaseProvider));
        await ImportAsync(host, small);
        await host.DispatchAsync("project.releaseLock");

        var databasePath = DatabasePath(projects, name, ProjectDocument.DefaultDatabaseProvider);
        File.SetAttributes(databasePath, File.GetAttributes(databasePath) | FileAttributes.ReadOnly);
        try
        {
            await host.DispatchAsync("project.load", LoadPayload(name));
            var rejected = await Assert.ThrowsAsync<JetActionException>(() => ImportAsync(host, next));
            Assert.Equal(JetErrorCodes.DatabaseReadOnly, rejected.Code);
            Assert.Equal(
                "案件資料庫是唯讀的，資料無法寫入。請確認案件資料夾和裡面的檔案不是唯讀，再重新開啟案件。",
                rejected.Message);
            await host.DispatchAsync("project.releaseLock");
        }
        finally
        {
            File.SetAttributes(databasePath, File.GetAttributes(databasePath) & ~FileAttributes.ReadOnly);
        }

        // 取消唯讀、重新開啟案件後照常可以寫入，被擋下的那次沒有留下任何資料。
        Assert.Equal(3, await CountAsync(databasePath, "pbc_gl"));
        await host.DispatchAsync("project.load", LoadPayload(name));
        await ImportAsync(host, next);
        Assert.Equal(8, await CountAsync(databasePath, "pbc_gl"));
    }

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task ReadOnlyCaseFolder_OpeningTheCaseExplainsTheReason_AndTheFilesStayUnchanged(string provider)
    {
        using var workspace = new TempProjectRoot();
        var projects = Projects(workspace);
        var name = $"read-only-folder-{provider}";
        var gl = WriteGl(workspace.Path, "gl.csv", 3);

        using var host = new HandlerTestHost(projectsRootPath: projects);
        await host.DispatchAsync("project.create", CreatePayload(name, provider));
        await ImportAsync(host, gl);
        await host.DispatchAsync("project.releaseLock");

        // 從唯讀的位置複製回來的案件資料夾，裡面每個檔案都是唯讀。
        var caseDirectory = Path.Combine(projects, name);
        var files = Directory.GetFiles(caseDirectory, "*", SearchOption.AllDirectories);
        // 支援紀錄是 JET 自己的操作紀錄，開案失敗時照常追加一筆，不列入比對。
        var before = files
            .Where(path => !IsSupportLog(caseDirectory, path))
            .ToDictionary(path => path, ReadShared, StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        }

        try
        {
            var rejected = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("project.load", LoadPayload(name)));
            // SQLite 遇到唯讀的資料庫檔仍然可以讀，開案到寫入案件設定時才失敗；DuckDB 開資料庫檔就要寫入，開檔時就失敗。
            Assert.Equal(
                provider == ProjectDocument.DuckDbDatabaseProvider ? JetErrorCodes.DatabaseUnavailable : JetErrorCodes.FileReadError,
                rejected.Code);
            Assert.Contains("唯讀", rejected.Message, StringComparison.Ordinal);
            foreach (var (path, bytes) in before)
            {
                Assert.True(bytes.AsSpan().SequenceEqual(ReadShared(path)), $"{Path.GetFileName(path)} 不應被改動");
            }
        }
        finally
        {
            foreach (var file in Directory.GetFiles(caseDirectory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            }
        }

        await host.DispatchAsync("project.load", LoadPayload(name));
    }

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task DatabaseFileHeldByAnotherProgram_OpeningTheCaseExplainsTheReason_AndWorksAfterItIsReleased(string provider)
    {
        using var workspace = new TempProjectRoot();
        var projects = Projects(workspace);
        var name = $"held-{provider}";
        var gl = WriteGl(workspace.Path, "gl.csv", 3);

        using var host = new HandlerTestHost(projectsRootPath: projects);
        await host.DispatchAsync("project.create", CreatePayload(name, provider));
        await ImportAsync(host, gl);
        await host.DispatchAsync("project.releaseLock");

        // 防毒軟體或備份程式獨占資料庫檔時，其他程式都打不開它。
        var databasePath = DatabasePath(projects, name, provider);
        using (new FileStream(databasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var rejected = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("project.load", LoadPayload(name)));
            Assert.Equal(JetErrorCodes.DatabaseUnavailable, rejected.Code);
            Assert.Equal(
                "JET 無法讀寫案件資料庫。請確認案件資料夾所在的磁碟仍然連接，資料夾和裡面的檔案不是唯讀，而且目前的 Windows 帳號可以寫入。"
                + "防毒軟體或雲端同步正在處理這個資料夾時，請等處理完成後再試一次。",
                rejected.Message);
        }

        await host.DispatchAsync("project.load", LoadPayload(name));
        await host.DispatchAsync("project.releaseLock");
        Assert.Equal(3, await CountAsync(databasePath, "pbc_gl", provider));
    }

    [Fact]
    public async Task FullDiskDuringALargeImport_IsReported_AndTheEarlierDataStays()
    {
        using var workspace = new TempProjectRoot();
        var projects = Projects(workspace);
        const string name = "full-disk-sqlite";
        var small = WriteGl(workspace.Path, "small.csv", 3);
        var large = WriteGl(workspace.Path, "large.csv", 30_000);

        using var host = new HandlerTestHost(projectsRootPath: projects);
        await host.DispatchAsync("project.create", CreatePayload(name, ProjectDocument.DefaultDatabaseProvider));
        await ImportAsync(host, small);
        var databasePath = DatabasePath(projects, name, ProjectDocument.DefaultDatabaseProvider);

        // 磁碟已滿用 SQLite 的頁數上限重現：上限只作用在設定它的連線上，所以先把 JET 的連線池換成設好上限的連線，
        // 匯入就在真正的寫入途中遇到空間不足。連線字串必須和 SqliteProjectDatabase 的讀寫連線相同，才會進到同一個連線池。
        var pooled = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
        }.ToString();
        SeedPoolWithPageLimit(pooled, headroomPages: 16);

        var rejected = await Assert.ThrowsAsync<JetActionException>(() => ImportAsync(host, large));
        Assert.Equal(JetErrorCodes.DatabaseStorageFull, rejected.Code);
        Assert.Equal("案件所在磁碟的空間不足，資料庫無法繼續寫入。請釋放空間後再試一次。", rejected.Message);
        Assert.Equal(3, await CountAsync(databasePath, "pbc_gl"));
        Assert.Equal("ok", await IntegrityAsync(databasePath));

        // 空間釋出後重試會成功：換回沒有上限的連線。
        using (var connection = new SqliteConnection(pooled))
        {
            SqliteConnection.ClearPool(connection);
        }

        await ImportAsync(host, large);
        Assert.Equal(30_000, await CountAsync(databasePath, "pbc_gl"));
    }

    /// <summary>同時開幾條連線，各自設好頁數上限再放回連線池，之後 JET 取到的都是有上限的連線。</summary>
    private static void SeedPoolWithPageLimit(string connectionString, int headroomPages)
    {
        using (var connection = new SqliteConnection(connectionString))
        {
            SqliteConnection.ClearPool(connection);
        }

        var connections = new List<SqliteConnection>();
        try
        {
            for (var index = 0; index < 8; index++)
            {
                var connection = new SqliteConnection(connectionString);
                connection.Open();
                connections.Add(connection);
                using var count = connection.CreateCommand();
                count.CommandText = "PRAGMA page_count;";
                var pages = Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture);
                using var limit = connection.CreateCommand();
                limit.CommandText = $"PRAGMA max_page_count = {pages + headroomPages};";
                limit.ExecuteNonQuery();
            }
        }
        finally
        {
            foreach (var connection in connections)
            {
                connection.Dispose();
            }
        }
    }

    private static bool IsSupportLog(string caseDirectory, string path) =>
        Path.GetRelativePath(caseDirectory, path)
            .StartsWith(SupportCaseHistoryStore.DirectoryName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>JET 可能還開著這個檔案，讀取時允許其他程式同時讀寫。</summary>
    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static string Projects(TempProjectRoot workspace)
    {
        var projects = Path.Combine(workspace.Path, "projects");
        Directory.CreateDirectory(projects);
        return projects;
    }

    private static string CreatePayload(string name, string provider) =>
        JsonSerializer.Serialize(new { projectName = name, periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider });

    private static string LoadPayload(string name) => JsonSerializer.Serialize(new { projectId = name });

    private static Task<JsonElement> ImportAsync(HandlerTestHost host, string path) =>
        host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new
        {
            mode = "replace",
            sources = new[] { new { filePath = path, fileName = Path.GetFileName(path), encoding = "utf-8", delimiter = "," } },
        }));

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

    private static string DatabasePath(string projects, string name, string provider)
    {
        var folder = new JetProjectFolder(projects);
        ILocalProjectDatabase database = provider == ProjectDocument.DuckDbDatabaseProvider
            ? new DuckDbProjectDatabase(folder)
            : new SqliteProjectDatabase(folder);
        return database.GetDatabasePath(name);
    }

    /// <summary>不經 JET 的連線池，用不進連線池的連線讀一個表的列數，讀完就關閉。</summary>
    private static async Task<long> CountAsync(string databasePath, string table, string provider = ProjectDocument.DefaultDatabaseProvider)
    {
        await using System.Data.Common.DbConnection connection = provider == ProjectDocument.DuckDbDatabaseProvider
            ? new DuckDB.NET.Data.DuckDBConnection($"DataSource={databasePath};ACCESS_MODE=READ_ONLY")
            : new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        // 表名都是上面固定的幾個，不來自外部輸入。
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<string> IntegrityAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? "";
    }
}
