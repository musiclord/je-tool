using System.Diagnostics;
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
/// 第 22 步盤點的第 3 項：寫到一半被強制結束（工作管理員結束程式或斷電）後重開，要確認四件事：
/// 強制結束前的資料完整、資料庫完整性檢查通過、能重新取得案件鎖、重試會成功。SQLite 與 DuckDB 都測。這是新增的測試。
/// 子程式就是測試程式本身，以環境變數指定角色，只執行這個測試方法，寫法同 <see cref="SupportCaseHistoryProcessTests"/>。
/// 子程式在寫入交易進行中送出的進度事件裡寫下「已開始」再停住，父程式這時強制結束它，所以結束的時間點是確定的，不靠等待時間：
/// 匯入每兩萬列送一次進度，確認 GL 配對在投影寫完、交易提交之前一定送一次。
/// </summary>
public sealed class InterruptedWriteProcessTests
{
    private const string RoleVariable = "JET_TESTS_INTERRUPTED_WRITE_ROLE";
    private const string ProjectsVariable = "JET_TESTS_INTERRUPTED_WRITE_PROJECTS";
    private const string CaseVariable = "JET_TESTS_INTERRUPTED_WRITE_CASE";
    private const string SourceVariable = "JET_TESTS_INTERRUPTED_WRITE_SOURCE";
    private const string ReadyVariable = "JET_TESTS_INTERRUPTED_WRITE_READY";

    // 比第一次送出匯入進度的兩萬列多，送出時交易一定還在寫。
    private const int LargeGlRows = 30_000;
    private static readonly TimeSpan ChildLimit = TimeSpan.FromMinutes(3);

    // 強制結束前後逐列比對的資料表：匯入與確認配對會寫到的表。訊息與操作紀錄不在交易裡，不列入。
    private static readonly string[] ComparedTables =
    [
        "import_batch", "import_batch_source", "pbc_gl", "gl", "field_info", "field_mapping", "field_mapping_previous",
        "gl_control_total", "gl_rde_field", "gl_rde_value", "result_stale_state", "filter_scenario", "filter_hit",
    ];

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider, "import")]
    [InlineData(ProjectDocument.DefaultDatabaseProvider, "mapping")]
    public async Task ForcedEndInTheMiddleOfAWrite_KeepsTheEarlierData_AndTheRetrySucceeds(string provider, string operation)
    {
        var role = Environment.GetEnvironmentVariable(RoleVariable);
        if (string.IsNullOrEmpty(role))
        {
            await RunParentAsync(provider, operation);
        }
        else if (role == Role(provider, operation))
        {
            await RunChildAsync(operation);
        }

        // 子程式只跑指定的那一組，其他組不做事。
    }

    private static async Task RunParentAsync(string provider, string operation)
    {
        using var workspace = new TempProjectRoot();
        var projects = Path.Combine(workspace.Path, "projects");
        Directory.CreateDirectory(projects);
        var name = $"interrupted-{provider}-{operation}";
        var smallGl = WriteGl(workspace.Path, "small.csv", 3);
        var largeGl = WriteGl(workspace.Path, "large.csv", LargeGlRows);

        // 先建好已匯入 GL、已確認配對的案件，離開案件後記下強制結束前的資料。
        using (var host = new HandlerTestHost(projectsRootPath: projects))
        {
            await host.DispatchAsync("project.create", CreatePayload(name, provider));
            await ImportAsync(host, smallGl);
            await CommitGlAsync(host, Mapping(description: "Description"));
            await host.DispatchAsync("project.releaseLock");
        }

        SqliteConnection.ClearAllPools();
        var before = await SnapshotAsync(projects, name, provider);
        Assert.Equal(3, before["gl"].Length);

        // 子程式開案後重新匯入大檔，或改用另一欄當摘要重新確認配對；寫到一半時被強制結束，來不及離開案件或做任何結束程序。
        var ready = Path.Combine(workspace.Path, "ready.json");
        using (var child = StartChild(workspace.Path, projects, Role(provider, operation), name, largeGl, ready))
        {
            await WaitForReadyAsync(child, ready);
            child.Process.Kill(entireProcessTree: true);
            await child.Process.WaitForExitAsync();
        }

        SqliteConnection.ClearAllPools();
        var after = await SnapshotAsync(projects, name, provider);
        foreach (var table in ComparedTables)
        {
            Assert.True(before[table].SequenceEqual(after[table]), $"強制結束後資料表 {table} 和結束前不同");
        }

        await AssertIntegrityAsync(projects, name, provider);

        // 能重新取得案件鎖，重試同一項作業會成功，結果是重試後的資料。
        using (var host = new HandlerTestHost(projectsRootPath: projects))
        {
            await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = name }));
            if (operation == "import")
            {
                await ImportAsync(host, largeGl);
                await CommitGlAsync(host, Mapping(description: "Description"));
            }
            else
            {
                await CommitGlAsync(host, Mapping(description: "Name"));
            }

            await host.DispatchAsync("project.releaseLock");
        }

        SqliteConnection.ClearAllPools();
        var retried = await SnapshotAsync(projects, name, provider);
        Assert.Equal(operation == "import" ? LargeGlRows : 3, retried["gl"].Length);
        if (operation == "mapping")
        {
            Assert.False(before["gl"].SequenceEqual(retried["gl"]), "重新確認配對後，GL 的摘要應改成另一欄的內容");
        }

        await AssertIntegrityAsync(projects, name, provider);
    }

    private static async Task RunChildAsync(string operation)
    {
        var projects = Environment.GetEnvironmentVariable(ProjectsVariable)!;
        var name = Environment.GetEnvironmentVariable(CaseVariable)!;
        var ready = Environment.GetEnvironmentVariable(ReadyVariable)!;
        var progressEvent = operation == "import" ? "import.progress" : "mapping.progress";

        // 刻意不釋放 host：這個程式要在寫入交易進行中被強制結束。
        var host = new HandlerTestHost(
            projectsRootPath: projects,
            eventPublisher: new StopAtFirstEvent(progressEvent, ready));
        await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = name }));
        if (operation == "import")
        {
            await ImportAsync(host, Environment.GetEnvironmentVariable(SourceVariable)!);
        }
        else
        {
            await CommitGlAsync(host, Mapping(description: "Name"));
        }

        Assert.Fail("寫入作業在送出進度事件前就結束了，父程式沒有機會在寫到一半時結束這個程式。");
    }

    /// <summary>第一次收到指定的進度事件時寫下「已開始」，然後停住，等父程式強制結束。</summary>
    private sealed class StopAtFirstEvent(string eventName, string readyPath) : IJetEventPublisher
    {
        public void Publish(string name, object payload)
        {
            if (name != eventName)
            {
                return;
            }

            // 先寫暫存檔再改名，父程式不會讀到寫到一半的檔案。
            File.WriteAllText(readyPath + ".tmp", JsonSerializer.Serialize(new { name }));
            File.Move(readyPath + ".tmp", readyPath);
            Thread.Sleep(ChildLimit);
            Environment.FailFast("父程式沒有結束這個程式。");
        }
    }

    private static string Role(string provider, string operation) => $"{provider}-{operation}";

    private static string CreatePayload(string name, string provider) =>
        JsonSerializer.Serialize(new { projectName = name, periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider });

    private static Dictionary<string, string> Mapping(string description) => new()
    {
        ["voucherNumber"] = "Document", ["glDate"] = "Date", ["accountNumber"] = "Account", ["accountName"] = "Name",
        ["description"] = description, ["amount"] = "Amount",
    };

    private static Task<JsonElement> CommitGlAsync(HandlerTestHost host, Dictionary<string, string> mapping) =>
        host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new { mapping, amountMode = "signed", rdeFields = Array.Empty<object>() }));

    private static Task<JsonElement> ImportAsync(HandlerTestHost host, string path) =>
        host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new
        {
            mode = "replace",
            sources = new[] { new { filePath = path, fileName = Path.GetFileName(path), encoding = "utf-8", delimiter = "," } },
        }));

    /// <summary>合成的 GL：每張傳票一借一貸，摘要與科目名稱不同，改用另一欄當摘要時看得出差別。</summary>
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
        return new SqliteProjectDatabase(folder);
    }

    private static async Task<Dictionary<string, string[]>> SnapshotAsync(string projects, string name, string provider)
    {
        var snapshot = new Dictionary<string, string[]>(StringComparer.Ordinal);
        await using var connection = Database(projects, provider).CreateConnection(name);
        await connection.OpenAsync();
        foreach (var table in ComparedTables)
        {
            await using var command = connection.CreateCommand();
            // 表名都是上面固定的清單，不來自外部輸入。
            command.CommandText = $"SELECT * FROM {table};";
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<string>();
            while (await reader.ReadAsync())
            {
                rows.Add(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount)
                    .Select(index => reader.IsDBNull(index) ? null : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture))
                    .ToArray()));
            }

            snapshot[table] = rows.Order(StringComparer.Ordinal).ToArray();
        }

        return snapshot;
    }

    /// <summary>SQLite 用 integrity_check；DuckDB 沒有對應的檢查，改成讀完每一張比對的表，讀不到就失敗。</summary>
    private static async Task AssertIntegrityAsync(string projects, string name, string provider)
    {
        await using var connection = Database(projects, provider).CreateConnection(name);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture));

        await connection.CloseAsync();
        SqliteConnection.ClearAllPools();
    }

    private static async Task WaitForReadyAsync(Child child, string ready)
    {
        var deadline = DateTime.UtcNow + ChildLimit;
        while (!File.Exists(ready))
        {
            if (child.Process.HasExited)
            {
                Assert.Fail($"子程式在送出進度事件前就結束了，結束碼 {child.Process.ExitCode}。\n{await child.OutputAsync()}");
            }

            if (DateTime.UtcNow > deadline)
            {
                child.Process.Kill(entireProcessTree: true);
                Assert.Fail($"子程式沒有在時限內送出進度事件。\n{await child.OutputAsync()}");
            }

            await Task.Delay(50);
        }
    }

    private static Child StartChild(string workspace, string projects, string role, string name, string source, string ready)
    {
        // 子程式的暫存資料夾放在父程式的工作區裡，被強制結束時留下的檔案跟著父程式一起刪除。
        var temp = Path.Combine(workspace, $"temp-{role}");
        Directory.CreateDirectory(temp);
        var executable = Environment.ProcessPath!;
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(typeof(InterruptedWriteProcessTests).Assembly.Location);
        }

        foreach (var argument in new[]
        {
            "--filter-method",
            $"{typeof(InterruptedWriteProcessTests).FullName}.{nameof(ForcedEndInTheMiddleOfAWrite_KeepsTheEarlierData_AndTheRetrySucceeds)}",
            "--results-directory", Path.Combine(workspace, $"results-{role}"),
            "--no-ansi", "--progress", "off",
        })
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment[RoleVariable] = role;
        start.Environment[ProjectsVariable] = projects;
        start.Environment[CaseVariable] = name;
        start.Environment[SourceVariable] = source;
        start.Environment[ReadyVariable] = ready;
        start.Environment["TMP"] = temp;
        start.Environment["TEMP"] = temp;
        return new Child(Process.Start(start)!);
    }

    /// <summary>子程式與它的輸出；輸出邊跑邊讀，避免輸出緩衝區滿了卡住子程式。</summary>
    private sealed class Child : IDisposable
    {
        private readonly Task<string> _output;
        private readonly Task<string> _error;

        public Child(Process process)
        {
            Process = process;
            _output = process.StandardOutput.ReadToEndAsync();
            _error = process.StandardError.ReadToEndAsync();
        }

        public Process Process { get; }

        public async Task<string> OutputAsync()
        {
            var text = new StringBuilder();
            foreach (var stream in new[] { _output, _error })
            {
                if (await Task.WhenAny(stream, Task.Delay(TimeSpan.FromSeconds(10))) == stream)
                {
                    text.AppendLine(await stream);
                }
            }

            return text.ToString();
        }

        public void Dispose()
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                Process.WaitForExit();
            }

            Process.Dispose();
        }
    }
}
