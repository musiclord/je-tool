using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 唯讀、防毒軟體這類程式占用檔案與磁碟已滿時，重試也不會成功，畫面不能只寫「發生非預期的錯誤」。
/// 這裡用合成資料在真正的檔案上重現這幾種情形，核對使用者看到的原因與下一步，以及案件資料保持原狀、排除後可以繼續。
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
    public async Task ReadOnlyCaseFolder_TheFirstWriteExplainsTheReason_AndTheCaseDataStayUnchanged(string provider)
    {
        using var workspace = new TempProjectRoot();
        var projects = Projects(workspace);
        var name = $"read-only-folder-{provider}";
        var gl = WriteGl(workspace.Path, "gl.csv", 3);
        var next = WriteGl(workspace.Path, "next.csv", 8);

        using var host = new HandlerTestHost(projectsRootPath: projects);
        await host.DispatchAsync("project.create", CreatePayload(name, provider));
        await ImportAsync(host, gl);
        await host.DispatchAsync("project.releaseLock");

        // 從唯讀的位置複製回來的案件資料夾，裡面每個檔案都是唯讀。
        var caseDirectory = Path.Combine(projects, name);
        var files = Directory.GetFiles(caseDirectory, "*", SearchOption.AllDirectories);
        // 支援紀錄是 JET 自己的操作紀錄，照常追加，不列入比對。案件設定檔平常就由 JET 設成唯讀，開案時照常寫回最近開啟時間，
        // 所以另外比對最近開啟時間以外的內容。
        var projectJson = Path.Combine(caseDirectory, JetProjectFolder.ProjectJsonFileName);
        var before = files
            .Where(path => !IsSupportLog(caseDirectory, path)
                && !string.Equals(path, projectJson, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => path, ReadShared, StringComparer.OrdinalIgnoreCase);
        var settingsBefore = SettingsOtherThanLastOpened(projectJson);
        foreach (var file in files)
        {
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        }

        try
        {
            // SQLite 遇到唯讀的資料庫檔仍然可以讀，所以照樣開得了案；第一次寫入資料庫時才擋下並說明原因。
            await host.DispatchAsync("project.load", LoadPayload(name));
            var rejected = await Assert.ThrowsAsync<JetActionException>(() => ImportAsync(host, next));
            Assert.Equal(JetErrorCodes.DatabaseReadOnly, rejected.Code);
            Assert.Contains("唯讀", rejected.Message, StringComparison.Ordinal);
            foreach (var (path, bytes) in before)
            {
                Assert.True(bytes.AsSpan().SequenceEqual(ReadShared(path)), $"{Path.GetFileName(path)} 不應被改動");
            }

            Assert.Equal(settingsBefore, SettingsOtherThanLastOpened(projectJson));
            await host.DispatchAsync("project.releaseLock");
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

        var pageLimit = new PageLimitObserver(headroomPages: 16);
        using var host = new HandlerTestHost(projectsRootPath: projects, sqlStatementObserver: pageLimit);
        await host.DispatchAsync("project.create", CreatePayload(name, ProjectDocument.DefaultDatabaseProvider));
        await ImportAsync(host, small);
        var databasePath = DatabasePath(projects, name, ProjectDocument.DefaultDatabaseProvider);

        // 磁碟已滿用 SQLite 的頁數上限重現，匯入就在真正的寫入途中遇到空間不足。
        pageLimit.Enabled = true;

        var rejected = await Assert.ThrowsAsync<JetActionException>(() => ImportAsync(host, large));
        Assert.Equal(JetErrorCodes.DatabaseStorageFull, rejected.Code);
        Assert.Equal("案件所在磁碟的空間不足，資料庫無法繼續寫入。請釋放空間後再試一次。", rejected.Message);
        Assert.Equal(3, await CountAsync(databasePath, "pbc_gl"));
        Assert.Equal("ok", await IntegrityAsync(databasePath));

        // 空間釋出後重試會成功：不再設上限，並關掉連線池裡設過上限的連線。
        // 連線字串必須和 SqliteProjectDatabase 的讀寫連線相同，清的才是同一個連線池。
        pageLimit.Enabled = false;
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
        }.ToString()))
        {
            SqliteConnection.ClearPool(connection);
        }

        await ImportAsync(host, large);
        Assert.Equal(30_000, await CountAsync(databasePath, "pbc_gl"));
    }

    /// <summary>
    /// <see cref="Enabled"/> 為 true 時，在 JET 的每條連線執行語句前設好頁數上限：目前的頁數再加 headroomPages 頁。
    /// 頁數上限只作用在設定它的連線上，匯入用的連線又不進連線池，所以在連線第一次執行語句時設定。
    /// 直接呼叫 SQLite 執行，不經過 JET 的命令，也就不會再通知這個監看物件。
    /// </summary>
    private sealed class PageLimitObserver(int headroomPages) : ISqlStatementObserver
    {
        private readonly ConditionalWeakTable<SqliteConnection, object> _limited = new();

        public volatile bool Enabled;

        public SqlStatementWatch? Begin(DbCommand command)
        {
            if (!Enabled || command.Connection is not SqliteConnection { Handle: { } handle } connection
                || _limited.TryGetValue(connection, out _))
            {
                return null;
            }

            _limited.Add(connection, new object());
            var pages = Pragma(handle, "PRAGMA page_count;");
            Pragma(handle, $"PRAGMA max_page_count = {pages + headroomPages};");
            return null;
        }

        private static long Pragma(SQLitePCL.sqlite3 handle, string sql)
        {
            Assert.Equal(SQLitePCL.raw.SQLITE_OK, SQLitePCL.raw.sqlite3_prepare_v2(handle, sql, out var statement));
            using (statement)
            {
                Assert.Equal(SQLitePCL.raw.SQLITE_ROW, SQLitePCL.raw.sqlite3_step(statement));
                return SQLitePCL.raw.sqlite3_column_int64(statement, 0);
            }
        }
    }

    private static bool IsSupportLog(string caseDirectory, string path) =>
        Path.GetRelativePath(caseDirectory, path)
            .StartsWith(SupportCaseHistoryStore.DirectoryName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>案件設定檔裡最近開啟時間以外的內容，包括抽樣種子；開案時寫回最近開啟時間是正常的。</summary>
    private static string SettingsOtherThanLastOpened(string projectJson)
    {
        var settings = JsonNode.Parse(Encoding.UTF8.GetString(ReadShared(projectJson)).TrimStart('\uFEFF'))!.AsObject();
        settings.Remove("lastOpenedUtc");
        return settings.ToJsonString();
    }

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
        ILocalProjectDatabase database = new SqliteProjectDatabase(folder);
        return database.GetDatabasePath(name);
    }

    /// <summary>不經 JET 的連線池，用不進連線池的連線讀一個表的列數，讀完就關閉。</summary>
    private static async Task<long> CountAsync(string databasePath, string table, string provider = ProjectDocument.DefaultDatabaseProvider)
    {
        await using System.Data.Common.DbConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder
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
