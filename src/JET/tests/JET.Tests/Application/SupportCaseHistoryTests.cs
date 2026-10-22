using System.Text;
using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 使用者 2026-10-21 要求離開案件、之後再回到同一案件時，匯出的支援日誌不缺一開始的事件。每個情境先寫出完整的預期事件順序，
/// 再只讀匯出檔核對：重新開啟、沒有正常離開與寫到一半的行、紀錄檔被占用或唯讀、另一個程式開不了案件、切換案件、
/// 刪除後重建同名案件、舊案件第一次開啟，以及寫不進去的事件超過保留上限。程式真的重新啟動與被強制結束的情境見
/// <see cref="SupportCaseHistoryProcessTests"/>。
/// </summary>
public sealed class SupportCaseHistoryTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ReopenedByAnotherProgram_ExportHasTheWholeHistoryInOrder(string provider)
    {
        using var root = new TempProjectRoot();
        string id;
        using (var first = new HandlerTestHost(enableDevTools: false, projectsRootPath: root.Path))
        {
            id = await CreateAsync(first, provider, "Synthetic history reopen", "create");
            for (var i = 1; i <= 3; i++)
            {
                await first.DispatchAsync("log.recent", correlationId: $"first-{i}");
            }

            await first.DispatchAsync("project.releaseLock", correlationId: "leave");
        }

        using var second = new HandlerTestHost(enableDevTools: false, projectsRootPath: root.Path);
        await second.DispatchAsync("project.load", Json(new { projectId = id }), correlationId: "reopen");
        await second.DispatchAsync("log.recent", correlationId: "second-1");
        var log = await ExportAsync(second, id);

        string[] expected =
        [
            "P1 support.history_started", "P1 support.history_process", "P1 support.case_opened project.create create",
            .. Recent("P1", "first-1"), .. Recent("P1", "first-2"), .. Recent("P1", "first-3"),
            .. Pair("P1", "project.releaseLock", "leave"),
            "P2 support.history_process", .. Load("P2", "reopen"), .. Recent("P2", "second-1"),
        ];
        Assert.Equal(expected, Story(log));
        Assert.Equal("case_created", Field(Single(log, "support.history_started"), "history_start"));
        Assert.All(Events(log, "support.case_opened"), opened => Assert.Equal(provider, Field(opened, "database_provider")));
        Assert.Equal(["create", "load"], Events(log, "support.case_opened").Select(opened => Field(opened, "opened_by")));
        Assert.All(Events(log, "support.history_process"), process =>
        {
            Assert.False(string.IsNullOrEmpty(Field(process, "app_version")));
            Assert.Matches("^[0-9a-f]{32}$", Field(process, "module_version_id")!);
        });

        var history = log.History;
        Assert.Equal("saved", Text(history, "status"));
        Assert.Equal("case_created", Text(history, "start"));
        Assert.Equal(expected.Length, Number(history, "lines"));
        Assert.Equal(2, Number(history, "openings"));
        Assert.Equal(2, Number(history, "processes"));
        // 第一個程式記到了正常離開案件；匯出的程式本身不列入。
        Assert.Empty(history.GetProperty("processesNotClosed").EnumerateArray());
        Assert.Equal(0, Number(history, "unsavedEvents"));
        Assert.Equal(0, Number(history, "gapMarkers"));
        Assert.Equal(0, Number(history, "unreadableLines"));
        AssertSafe(log.Text, root.Path, id);
        AssertSafe(await File.ReadAllTextAsync(HistoryPath(root.Path, id)), root.Path, id);
    }

    [Fact]
    public async Task ProgramEndedWithoutLeavingTheCase_IsListedAndAHalfWrittenLineDoesNotHideLaterEvents()
    {
        using var root = new TempProjectRoot();
        string id;
        using (var first = new HandlerTestHost(enableDevTools: false, projectsRootPath: root.Path))
        {
            id = await CreateAsync(first, "sqlite", "Synthetic history crash", "create");
            await first.DispatchAsync("log.recent", correlationId: "before-end");
        }

        // 程式寫到一半就結束：最後一行沒有換行。下一個程式附加前先補換行，這一行在匯出時算成無法讀取。
        await File.AppendAllTextAsync(HistoryPath(root.Path, id), "{\"timestamp\":\"2026-10-21T00:00:00Z\",\"eventName\":\"action.st");
        using var second = new HandlerTestHost(enableDevTools: false, projectsRootPath: root.Path);
        await second.DispatchAsync("project.load", Json(new { projectId = id }), correlationId: "reopen");
        await second.DispatchAsync("log.recent", correlationId: "after-reopen");
        await ExportAsync(second, id);
        // 匯出時正在寫的行：還沒有換行的部分不讀。
        const string writing = "{\"timestamp\":\"2026-10-21T00:00:01Z\"";
        await File.AppendAllTextAsync(HistoryPath(root.Path, id), writing);
        var log = await ExportAsync(second, id);

        string[] expected =
        [
            "P1 support.history_started", "P1 support.history_process", "P1 support.case_opened project.create create",
            .. Recent("P1", "before-end"),
            "P2 support.history_process", .. Load("P2", "reopen"), .. Recent("P2", "after-reopen"),
        ];
        Assert.Equal(expected, Story(log));
        var history = log.History;
        Assert.Equal(1, Number(history, "unreadableLines"));
        Assert.Equal([6L], history.GetProperty("unreadableLineNumbers").EnumerateArray().Select(number => number.GetInt64()));
        Assert.Equal(Encoding.UTF8.GetByteCount(writing), Number(history, "incompleteTailBytes"));
        var ended = Events(log, "support.history_process")[0].GetProperty("processInstance").GetString();
        Assert.Equal([ended], history.GetProperty("processesNotClosed").EnumerateArray().Select(process => process.GetString()));
    }

    [Theory]
    [InlineData("held")]
    [InlineData("readOnly")]
    public async Task HistoryFileCannotBeWritten_AuditWorkContinuesAndTheExportShowsUnsavedEventsUntilWrittenLate(string condition)
    {
        using var host = new HandlerTestHost(enableDevTools: false);
        var id = await CreateAsync(host, "sqlite", "Synthetic history blocked", "create");
        await ExportAsync(host, id);
        var path = HistoryPath(host.ProjectsRoot, id);
        string[] saved =
        [
            "P1 support.history_started", "P1 support.history_process", "P1 support.case_opened project.create create",
        ];
        string[] during = [.. Pair("P1", "project.saveProgress", "during")];

        ExportedLog blocked;
        var hold = condition == "held" ? new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        try
        {
            if (hold is null)
            {
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            }

            // 紀錄寫不進去不影響審計操作。
            var progress = await host.DispatchAsync("project.saveProgress", """{"currentStep":2}""", correlationId: "during");
            Assert.Equal(2, progress.GetProperty("currentStep").GetInt32());
            blocked = await ExportAsync(host, id);
        }
        finally
        {
            hold?.Dispose();
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }

        var history = blocked.History;
        Assert.Equal(2, Number(history, "unsavedEvents"));
        Assert.Equal(0, Number(history, "droppedEvents"));
        var failure = history.GetProperty("lastWriteFailure");
        if (condition == "held")
        {
            // 別的程式獨占紀錄檔時連讀都讀不到：只匯出寫不進去的事件，並寫出讀取失敗的原因。
            Assert.Equal(during, Story(blocked));
            Assert.Equal("read_failed", Text(history, "status"));
            Assert.Equal("System.IO.IOException", Text(history.GetProperty("readFailure"), "error_type"));
            Assert.Equal(32, Number(history.GetProperty("readFailure"), "win32_error"));
            Assert.Equal("file_in_use", Text(failure, "io_category"));
            Assert.Equal(32, Number(failure, "win32_error"));
        }
        else
        {
            Assert.Equal([.. saved, .. during], Story(blocked));
            Assert.Equal("saved", Text(history, "status"));
            Assert.Equal("access_denied", Text(failure, "io_category"));
            Assert.Equal(5, Number(failure, "win32_error"));
            Assert.Equal("System.UnauthorizedAccessException", Text(failure, "error_type"));
        }

        // 恢復之後，匯出前先補寫，事件依原順序進到紀錄檔，不算缺口。
        var log = await ExportAsync(host, id);
        Assert.Equal([.. saved, .. during], Story(log));
        Assert.Equal("saved", Text(log.History, "status"));
        Assert.Equal(0, Number(log.History, "unsavedEvents"));
        Assert.Equal(0, Number(log.History, "gapMarkers"));
        Assert.False(log.History.TryGetProperty("lastWriteFailure", out _));
        Assert.Equal(saved.Length + during.Length, File.ReadAllLines(path).Length);

        await host.DispatchAsync("project.releaseLock");
        var reloaded = await host.DispatchAsync("project.load", Json(new { projectId = id }));
        Assert.Equal(2, reloaded.GetProperty("project").GetProperty("currentStep").GetInt32());
    }

    [Fact]
    public async Task SecondProgramFailingToOpenTheCase_IsRecordedInTheSameHistory()
    {
        using var root = new TempProjectRoot();
        using var first = new HandlerTestHost(enableDevTools: false, projectsRootPath: root.Path);
        var id = await CreateAsync(first, "sqlite", "Synthetic history shared", "create");
        await first.DispatchAsync("log.recent", correlationId: "first-1");
        await ExportAsync(first, id);

        using var second = new HandlerTestHost(enableDevTools: false, projectsRootPath: root.Path);
        await Assert.ThrowsAsync<JetActionException>(() =>
            second.DispatchAsync("project.load", Json(new { projectId = id }), correlationId: "second-load"));
        var fromSecond = await ExportAsync(second, id, "second-load");
        Assert.Equal(
            ["P1 action.start project.load second-load", "P1 action.error project.load second-load"],
            Story(fromSecond));

        var log = await ExportAsync(first, id);
        string[] expected =
        [
            "P1 support.history_started", "P1 support.history_process", "P1 support.case_opened project.create create",
            .. Recent("P1", "first-1"),
            "P2 support.history_process", "P2 action.start project.load second-load", "P2 action.error project.load second-load",
        ];
        Assert.Equal(expected, Story(log));
        Assert.Equal(1, Number(log.History, "openings"));
        Assert.Equal(2, Number(log.History, "processes"));
        Assert.Empty(log.History.GetProperty("processesNotClosed").EnumerateArray());
    }

    [Fact]
    public async Task SwitchingCases_KeepsEachCaseHistorySeparate()
    {
        using var host = new HandlerTestHost(enableDevTools: false);
        var first = await CreateAsync(host, "sqlite", "Synthetic history first", "first-create");
        await host.DispatchAsync("log.recent", correlationId: "first-1");
        await host.DispatchAsync("project.releaseLock", correlationId: "first-leave");
        var second = await CreateAsync(host, "duckdb", "Synthetic history second", "second-create");
        await host.DispatchAsync("log.recent", correlationId: "second-1");
        await host.DispatchAsync("project.releaseLock", correlationId: "second-leave");
        await host.DispatchAsync("project.load", Json(new { projectId = first }), correlationId: "first-reopen");
        await host.DispatchAsync("log.recent", correlationId: "first-2");

        string[] firstExpected =
        [
            "P1 support.history_started", "P1 support.history_process",
            "P1 support.case_opened project.create first-create",
            .. Recent("P1", "first-1"), .. Pair("P1", "project.releaseLock", "first-leave"),
            .. Load("P1", "first-reopen"), .. Recent("P1", "first-2"),
        ];
        Assert.Equal(firstExpected, Story(await ExportAsync(host, first)));
        string[] secondExpected =
        [
            "P1 support.history_started", "P1 support.history_process",
            "P1 support.case_opened project.create second-create",
            .. Recent("P1", "second-1"), .. Pair("P1", "project.releaseLock", "second-leave"),
        ];
        Assert.Equal(secondExpected, Story(await ExportAsync(host, second)));
    }

    [Fact]
    public async Task DeletedCaseRecreatedWithTheSameName_StartsANewHistoryAndNothingRecreatesTheDeletedFolder()
    {
        using var host = new HandlerTestHost(enableDevTools: false);
        var id = await CreateAsync(host, "sqlite", "Synthetic history recreated", "old-create");
        await host.DispatchAsync("log.recent", correlationId: "old-1");
        await host.DispatchAsync("project.releaseLock", correlationId: "old-leave");
        await host.DispatchAsync("project.delete", Json(new { projectId = id }), correlationId: "old-delete");

        // 匯出另一個案件會先等寫入器處理完刪除那次操作的事件。
        var witness = await CreateAsync(host, "sqlite", "Synthetic history witness", "witness-create");
        await ExportAsync(host, witness);
        Assert.False(Directory.Exists(Path.Combine(host.ProjectsRoot, id)));
        await host.DispatchAsync("project.releaseLock", correlationId: "witness-leave");

        Assert.Equal(id, await CreateAsync(host, "sqlite", "Synthetic history recreated", "new-create"));
        await host.DispatchAsync("log.recent", correlationId: "new-1");
        var log = await ExportAsync(host, id);
        string[] expected =
        [
            "P1 support.history_started", "P1 support.history_process", "P1 support.case_opened project.create new-create",
            .. Recent("P1", "new-1"),
        ];
        Assert.Equal(expected, Story(log));
        Assert.Equal("case_created", Text(log.History, "start"));
        Assert.Equal(1, Number(log.History, "openings"));
    }

    [Fact]
    public async Task OldCaseFirstOpening_StartsFromThatLoadAndIsMarkedAsHavingNoEarlierRecord()
    {
        using var root = new TempProjectRoot();
        var id = await CreateOldCaseAsync(root.Path, "Synthetic history old case");

        using var host = new HandlerTestHost(enableDevTools: false, projectsRootPath: root.Path);
        await host.DispatchAsync("project.load", Json(new { projectId = id }), correlationId: "first-open");
        await host.DispatchAsync("log.recent", correlationId: "after-open");
        var log = await ExportAsync(host, id);

        // 載入開始時還沒有紀錄資料夾：那一筆先保留，開啟案件後依原順序補寫。
        string[] expected =
        [
            "P1 support.history_started", "P1 support.history_process", .. Load("P1", "first-open"),
            .. Recent("P1", "after-open"),
        ];
        Assert.Equal(expected, Story(log));
        Assert.Equal("no_earlier_record", Text(log.History, "start"));
        Assert.Equal(1, Number(log.History, "openings"));
    }

    [Fact]
    public async Task FailedLoadOfACaseWithoutHistory_IsExportedWithoutCreatingTheHistoryFolder()
    {
        using var root = new TempProjectRoot();
        var id = await CreateOldCaseAsync(root.Path, "Synthetic history broken old case");
        await File.WriteAllTextAsync(Path.Combine(root.Path, id, "project.json"), "{\"entityName\":");
        var directory = Path.Combine(root.Path, id, SupportCaseHistoryStore.DirectoryName);

        using (var host = new HandlerTestHost(enableDevTools: false, projectsRootPath: root.Path))
        {
            await Assert.ThrowsAsync<JetActionException>(() =>
                host.DispatchAsync("project.load", Json(new { projectId = id }), correlationId: "broken-load"));
            var log = await ExportAsync(host, id, "broken-load");
            Assert.Equal(
                ["P1 action.start project.load broken-load", "P1 action.error project.load broken-load"],
                Story(log));
            Assert.Equal("not_started", Text(log.History, "status"));
            Assert.Equal(2, Number(log.History, "unsavedEvents"));
            // 案件沒有開啟成功，不算寫入失敗。
            Assert.False(log.History.TryGetProperty("lastWriteFailure", out _));
            Assert.False(Directory.Exists(directory));
        }

        // 程式結束時再補寫一次，也只開啟既有紀錄檔，不建立資料夾。
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task UnsavedEventsOverTheLimit_AreWrittenLaterAfterAGapLineWithTheCause()
    {
        using var root = new TempProjectRoot();
        const string id = "Synthetic history gap";
        Directory.CreateDirectory(Path.Combine(root.Path, id));
        using var store = new SupportCaseHistoryStore(new JetProjectFolder(root.Path), maxUnsaved: 3);
        store.Prepare(id, createdNow: true);
        await store.FlushAsync(CancellationToken.None);
        var path = HistoryPath(root.Path, id);
        await File.WriteAllTextAsync(path, "");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            for (var sequence = 1; sequence <= 5; sequence++)
            {
                store.Enqueue(Entry(id, sequence));
            }

            await store.FlushAsync(CancellationToken.None);
            var state = store.WriteState(id);
            Assert.Equal([3L, 4L, 5L], state.Unsaved.Select(entry => entry.Sequence));
            Assert.Equal(2, state.Dropped);
            Assert.Equal("access_denied", state.LastFailure!["io_category"]);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        await store.FlushAsync(CancellationToken.None);
        Assert.Same(SupportCaseHistoryWriteState.Empty, store.WriteState(id));
        var lines = (await File.ReadAllLinesAsync(path)).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        Assert.Equal(
            ["support.history_started", "support.history_process", "support.history_gap", "action.start", "action.start", "action.start"],
            lines.Select(line => line.GetProperty("eventName").GetString()));
        Assert.Equal([0L, 0L, 0L, 3L, 4L, 5L], lines.Select(line => line.GetProperty("sequence").GetInt64()));
        Assert.Equal("2", Field(lines[2], "missing_count"));
        Assert.Equal("access_denied", Field(lines[2], "io_category"));
        Assert.Equal("System.UnauthorizedAccessException", Field(lines[2], "error_type"));
    }

    /// <summary>用目前版本建立案件後刪掉支援紀錄資料夾，等同舊版建立、從來沒有支援紀錄的案件。</summary>
    private static async Task<string> CreateOldCaseAsync(string projectsRoot, string name)
    {
        string id;
        using (var host = new HandlerTestHost(enableDevTools: false, projectsRootPath: projectsRoot))
        {
            id = await CreateAsync(host, "sqlite", name, "old-version-create");
            await host.DispatchAsync("project.releaseLock");
        }

        Directory.Delete(Path.Combine(projectsRoot, id, SupportCaseHistoryStore.DirectoryName), recursive: true);
        return id;
    }

    [Fact]
    public async Task EventThatCannotBeWrittenAsJson_IsDroppedWithAGapLineAndTheOthersAreWritten()
    {
        using var root = new TempProjectRoot();
        const string id = "Synthetic history unserializable";
        Directory.CreateDirectory(Path.Combine(root.Path, id));
        using var store = new SupportCaseHistoryStore(new JetProjectFolder(root.Path));
        store.Prepare(id, createdNow: true);
        store.Enqueue(Entry(id, 1));
        // 自己包含自己的清單寫不成 JSON；模擬日後某個欄位放進這種物件，寫入器不能因此停下。
        var cycle = new List<object>();
        cycle.Add(cycle);
        store.Enqueue(Entry(id, 2) with
        {
            Fields = new Dictionary<string, object?>(StringComparer.Ordinal) { ["bad"] = cycle },
        });
        store.Enqueue(Entry(id, 3));
        await store.FlushAsync(CancellationToken.None);

        Assert.Same(SupportCaseHistoryWriteState.Empty, store.WriteState(id));
        var lines = (await File.ReadAllLinesAsync(HistoryPath(root.Path, id)))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        var names = lines.Select(line => line.GetProperty("eventName").GetString()).ToArray();
        Assert.Equal(["support.history_started", "support.history_process"], names.Take(2));
        // 寫入器分幾批處理這三筆事件由時機決定：缺口行寫在放棄之後的下一次寫入，所以一定在第 3 筆之前，可能在第 1 筆之後。
        Assert.Equal(5, lines.Length);
        Assert.Equal([1L, 3L], lines.Where(line => line.GetProperty("eventName").GetString() == "action.start")
            .Select(line => line.GetProperty("sequence").GetInt64()));
        var gap = Array.FindIndex(names, name => name == "support.history_gap");
        Assert.InRange(gap, 2, 3);
        Assert.Equal(3L, lines[4].GetProperty("sequence").GetInt64());
        Assert.Equal("1", Field(lines[gap], "missing_count"));
        Assert.Equal("System.Text.Json.JsonException", Field(lines[gap], "error_type"));
    }

    private static SupportDiagnosticLogEntry Entry(string projectId, long sequence) =>
        new(DateTimeOffset.UtcNow, "Information", "JET.Tests", "action.start", "synthetic", $"c{sequence}", projectId,
            new Dictionary<string, object?>(StringComparer.Ordinal), null)
        {
            ProcessInstance = "synthetic0001",
            Sequence = sequence,
        };

    internal static string[] Pair(string process, string action, string correlationId) =>
        [$"{process} action.start {action} {correlationId}", $"{process} action.end {action} {correlationId}"];

    internal static string[] Recent(string process, string correlationId) => Pair(process, "log.recent", correlationId);

    internal static string[] Load(string process, string correlationId) =>
    [
        $"{process} action.start project.load {correlationId}",
        $"{process} support.case_opened project.load {correlationId}",
        $"{process} action.end project.load {correlationId}",
    ];

    /// <summary>每一行寫成「第幾個程式、事件、操作、操作識別碼」；程式依第一次出現的順序編號。</summary>
    internal static string[] Story(ExportedLog log)
    {
        var processes = new Dictionary<string, int>(StringComparer.Ordinal);
        return log.Entries.Select(entry =>
        {
            var process = entry.GetProperty("processInstance").GetString()!;
            if (!processes.TryGetValue(process, out var number))
            {
                processes[process] = number = processes.Count + 1;
            }

            var correlation = entry.TryGetProperty("correlationId", out var value) ? value.GetString() : null;
            return string.Join(' ', new[] { $"P{number}", entry.GetProperty("eventName").GetString(), Field(entry, "action"), correlation }
                .Where(part => part is not null));
        }).ToArray();
    }

    internal static async Task<ExportedLog> ExportAsync(HandlerTestHost host, string projectId, string? correlationId = null)
    {
        var exported = await host.DispatchAsync("support.log.export", Json(new { projectId, correlationId }));
        return await ReadExportAsync(exported.GetProperty("filePath").GetString()!);
    }

    internal static async Task<ExportedLog> ReadExportAsync(string path)
    {
        var lines = (await File.ReadAllLinesAsync(path)).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        var parsed = lines.Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        return new ExportedLog(parsed[0], parsed[1..], string.Join('\n', lines));
    }

    internal static async Task<string> CreateAsync(HandlerTestHost host, string provider, string name, string correlationId)
    {
        var result = await host.DispatchAsync("project.create", Json(new
        { caseName = name, periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider }),
            correlationId: correlationId);
        return result.GetProperty("projectId").GetString()!;
    }

    internal static string HistoryPath(string projectsRoot, string projectId) =>
        Path.Combine(projectsRoot, projectId, SupportCaseHistoryStore.DirectoryName, SupportCaseHistoryStore.FileName);

    internal static string Json(object value) => JsonSerializer.Serialize(value);

    internal static JsonElement Single(ExportedLog log, string eventName) =>
        Assert.Single(log.Entries, entry => entry.GetProperty("eventName").GetString() == eventName);

    internal static JsonElement[] Events(ExportedLog log, string eventName) =>
        log.Entries.Where(entry => entry.GetProperty("eventName").GetString() == eventName).ToArray();

    internal static string? Field(JsonElement entry, string name) =>
        entry.GetProperty("fields").TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
            : null;

    internal static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static long Number(JsonElement element, string name) => element.GetProperty(name).GetInt64();

    internal static void AssertSafe(string text, string projectsRoot, string projectId)
    {
        Assert.DoesNotContain(projectsRoot, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(projectId, text, StringComparison.Ordinal);
    }

    internal sealed record ExportedLog(JsonElement Metadata, JsonElement[] Entries, string Text)
    {
        public JsonElement History => Metadata.GetProperty("fields").GetProperty("history");
    }
}
