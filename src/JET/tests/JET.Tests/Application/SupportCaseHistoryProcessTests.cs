using System.Diagnostics;
using System.Text;
using System.Text.Json;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;
using static JET.Tests.Application.SupportCaseHistoryTests;

namespace JET.Tests.Application;

/// <summary>
/// 使用者 2026-10-21 要求支援日誌在程式結束、重新啟動後仍保有案件一開始的事件。這個測試由三個獨立程式依序操作同一個案件：
/// 第一個建立案件、操作超過舊版記憶體保留的筆數、失敗後重試、取消、切到另一個案件再回來，正常離開；第二個開啟案件後
/// 被強制結束；第三個重新開啟並匯出。匯出檔逐行與事先寫好的完整事件順序比對。
/// 子程式就是測試程式本身，以環境變數指定角色，只執行這個測試方法。
/// </summary>
public sealed class SupportCaseHistoryProcessTests
{
    private const string RoleVariable = "JET_TESTS_SUPPORT_HISTORY_ROLE";
    private const string ProjectsVariable = "JET_TESTS_SUPPORT_HISTORY_PROJECTS";
    private const string CaseVariable = "JET_TESTS_SUPPORT_HISTORY_CASE";
    private const string ResultVariable = "JET_TESTS_SUPPORT_HISTORY_RESULT";

    // 記憶體裡的支援日誌最多保留 2,000 筆；第一個程式記下的事件超過這個數量。
    private const int RecentCalls = 1_050;

    private static readonly TimeSpan ChildLimit = TimeSpan.FromMinutes(3);

    [Fact]
    public async Task CaseHistory_SurvivesRestartsAndAForcedEnd_InTheExactOrderOfEvents()
    {
        switch (Environment.GetEnvironmentVariable(RoleVariable))
        {
            case null or "":
                await RunParentAsync();
                break;
            case "first":
                await RunFirstAsync();
                break;
            case "second":
                await RunSecondAsync();
                break;
            case "third":
                await RunThirdAsync();
                break;
            default:
                Assert.Fail("Unknown support history child role.");
                break;
        }
    }

    private static async Task RunParentAsync()
    {
        using var workspace = new TempProjectRoot();
        var projects = Path.Combine(workspace.Path, "projects");
        Directory.CreateDirectory(projects);

        var first = await RunChildToEndAsync(workspace.Path, projects, "first", caseId: null);
        var id = first.GetProperty("projectId").GetString()!;
        var otherId = first.GetProperty("otherProjectId").GetString()!;

        using (var second = StartChild(workspace.Path, projects, "second", id))
        {
            await WaitForResultAsync(second, workspace.Path, "second");
            // 強制結束：程式來不及離開案件，也不會執行任何結束程序。
            second.Process.Kill(entireProcessTree: true);
            await second.Process.WaitForExitAsync();
        }

        var third = await RunChildToEndAsync(workspace.Path, projects, "third", id);
        var log = await ReadExportAsync(third.GetProperty("exportPath").GetString()!);

        string[] expected =
        [
            "P1 support.history_started", "P1 support.history_process",
            "P1 support.case_opened project.create first-create",
            .. Enumerable.Range(1, RecentCalls).SelectMany(i => Recent("P1", $"first-recent-{i}")),
            "P1 action.start project.saveProgress first-fail", "P1 action.error project.saveProgress first-fail",
            "P1 action.payload_rejected project.saveProgress first-fail",
            .. Pair("P1", "project.saveProgress", "first-retry"),
            "P1 action.start log.recent first-cancel", "P1 action.cancelled log.recent first-cancel",
            .. Pair("P1", "project.releaseLock", "first-leave"),
            .. Load("P1", "first-return"), .. Pair("P1", "project.releaseLock", "first-leave-again"),
            "P2 support.history_process", .. Load("P2", "second-load"),
            .. Enumerable.Range(1, 5).SelectMany(i => Recent("P2", $"second-recent-{i}")),
            "P3 support.history_process", .. Load("P3", "third-load"), .. Recent("P3", "third-recent"),
        ];
        Assert.Equal(expected, Story(log));

        var processes = Events(log, "support.history_process")
            .Select(process => process.GetProperty("processInstance").GetString()!)
            .ToArray();
        Assert.Equal(3, processes.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(processes[2], log.Metadata.GetProperty("fields").GetProperty("processInstance").GetString());
        var retry = Assert.Single(log.Entries, entry => entry.GetProperty("eventName").GetString() == "action.start"
            && entry.TryGetProperty("correlationId", out var value) && value.GetString() == "first-retry");
        Assert.Equal("first-fail", Field(retry, "retry_of"));
        Assert.Equal("true", Field(Single(log, "action.cancelled"), "cancellation_requested"));

        // 每個程式自己的事件序號遞增、時間不倒退。紀錄檔自己寫的行序號為 0，時間是寫入當下，不列入。
        foreach (var process in processes)
        {
            var own = log.Entries.Where(entry => entry.GetProperty("processInstance").GetString() == process
                && entry.GetProperty("sequence").GetInt64() > 0).ToArray();
            var sequences = own.Select(entry => entry.GetProperty("sequence").GetInt64()).ToArray();
            Assert.Equal(sequences.Order(), sequences);
            Assert.Equal(sequences.Distinct().Count(), sequences.Length);
            var times = own.Select(entry => entry.GetProperty("timestamp").GetDateTimeOffset()).ToArray();
            Assert.Equal(times.Order(), times);
        }

        var history = log.History;
        Assert.Equal("saved", Text(history, "status"));
        Assert.Equal("case_created", Text(history, "start"));
        Assert.Equal(expected.Length, Number(history, "lines"));
        Assert.True(expected.Length > 2_000);
        Assert.Equal(4, Number(history, "openings"));
        Assert.Equal(3, Number(history, "processes"));
        // 第一個程式正常離開，第二個被強制結束，第三個是匯出的程式本身。
        Assert.Equal([processes[1]], history.GetProperty("processesNotClosed").EnumerateArray().Select(process => process.GetString()));
        Assert.Equal(0, Number(history, "unsavedEvents"));
        Assert.Equal(0, Number(history, "gapMarkers"));
        Assert.Equal(0, Number(history, "unreadableLines"));
        Assert.Equal(0, Number(history, "incompleteTailBytes"));
        AssertSafe(log.Text, projects, id);

        // 另一個案件的紀錄只有它自己的事件。
        var other = (await File.ReadAllLinesAsync(HistoryPath(projects, otherId)))
            .Select(line => JsonDocument.Parse(line).RootElement)
            .Select(entry => entry.TryGetProperty("correlationId", out var value) ? value.GetString() : null)
            .Where(correlation => correlation is not null)
            .Distinct()
            .ToArray();
        Assert.Equal(["other-create", "other-recent", "other-leave"], other);
    }

    private static async Task RunFirstAsync()
    {
        using var host = new HandlerTestHost(enableDevTools: false, projectsRootPath: Projects());
        var id = await CreateAsync(host, "sqlite", "Synthetic restart case", "first-create");
        for (var i = 1; i <= RecentCalls; i++)
        {
            await host.DispatchAsync("log.recent", correlationId: $"first-recent-{i}");
        }

        await Assert.ThrowsAsync<JetActionException>(() =>
            host.DispatchAsync("project.saveProgress", """{"currentStep":99}""", correlationId: "first-fail"));
        await host.DispatchAsync("project.saveProgress", """{"currentStep":1}""", correlationId: "first-retry");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.DispatchAsync("log.recent", "{}", new CancellationToken(canceled: true), "first-cancel"));
        await host.DispatchAsync("project.releaseLock", correlationId: "first-leave");

        var otherId = await CreateAsync(host, "sqlite", "Synthetic restart other case", "other-create");
        await host.DispatchAsync("log.recent", correlationId: "other-recent");
        await host.DispatchAsync("project.releaseLock", correlationId: "other-leave");

        await host.DispatchAsync("project.load", Json(new { projectId = id }), correlationId: "first-return");
        await host.DispatchAsync("project.releaseLock", correlationId: "first-leave-again");
        await WriteResultAsync(new { projectId = id, otherProjectId = otherId });
    }

    private static async Task RunSecondAsync()
    {
        var id = Environment.GetEnvironmentVariable(CaseVariable)!;
        // 刻意不釋放：這個程式要在開著案件時被強制結束。
        var host = new HandlerTestHost(enableDevTools: false, projectsRootPath: Projects());
        await host.DispatchAsync("project.load", Json(new { projectId = id }), correlationId: "second-load");
        for (var i = 1; i <= 5; i++)
        {
            await host.DispatchAsync("log.recent", correlationId: $"second-recent-{i}");
        }

        // 匯出會先等排隊中的事件寫進案件紀錄；之後等著被結束，不釋放 host。
        await host.DispatchAsync("support.log.export", Json(new { projectId = id }));
        await WriteResultAsync(new { ready = true });
        await Task.Delay(ChildLimit);
        Assert.Fail("The parent test did not end this program.");
    }

    private static async Task RunThirdAsync()
    {
        var id = Environment.GetEnvironmentVariable(CaseVariable)!;
        using var host = new HandlerTestHost(enableDevTools: false, projectsRootPath: Projects());
        await host.DispatchAsync("project.load", Json(new { projectId = id }), correlationId: "third-load");
        await host.DispatchAsync("log.recent", correlationId: "third-recent");
        var exported = await host.DispatchAsync("support.log.export", Json(new { projectId = id }));
        await WriteResultAsync(new { exportPath = exported.GetProperty("filePath").GetString() });
    }

    private static string Projects() => Environment.GetEnvironmentVariable(ProjectsVariable)!;

    private static async Task WriteResultAsync(object result)
    {
        // 先寫暫存檔再改名，父程式不會讀到寫到一半的結果。
        var path = Environment.GetEnvironmentVariable(ResultVariable)!;
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(result));
        File.Move(path + ".tmp", path);
    }

    private static string ResultPath(string workspace, string role) => Path.Combine(workspace, $"result-{role}.json");

    private static async Task<JsonElement> RunChildToEndAsync(string workspace, string projects, string role, string? caseId)
    {
        using var child = StartChild(workspace, projects, role, caseId);
        using var timeout = new CancellationTokenSource(ChildLimit);
        try
        {
            await child.Process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            child.Process.Kill(entireProcessTree: true);
            Assert.Fail($"Child '{role}' did not finish.\n{await child.OutputAsync()}");
        }

        Assert.True(child.Process.ExitCode == 0, $"Child '{role}' exited with {child.Process.ExitCode}.\n{await child.OutputAsync()}");
        return JsonDocument.Parse(await File.ReadAllTextAsync(ResultPath(workspace, role))).RootElement.Clone();
    }

    private static async Task WaitForResultAsync(Child child, string workspace, string role)
    {
        var path = ResultPath(workspace, role);
        var deadline = DateTime.UtcNow + ChildLimit;
        while (!File.Exists(path))
        {
            if (child.Process.HasExited)
            {
                Assert.Fail($"Child '{role}' exited with {child.Process.ExitCode} before it was ready.\n{await child.OutputAsync()}");
            }

            if (DateTime.UtcNow > deadline)
            {
                child.Process.Kill(entireProcessTree: true);
                Assert.Fail($"Child '{role}' was not ready in time.\n{await child.OutputAsync()}");
            }

            await Task.Delay(50);
        }
    }

    private static Child StartChild(string workspace, string projects, string role, string? caseId)
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
            start.ArgumentList.Add(typeof(SupportCaseHistoryProcessTests).Assembly.Location);
        }

        foreach (var argument in new[]
        {
            "--filter-method",
            $"{typeof(SupportCaseHistoryProcessTests).FullName}.{nameof(CaseHistory_SurvivesRestartsAndAForcedEnd_InTheExactOrderOfEvents)}",
            "--results-directory", Path.Combine(workspace, $"results-{role}"),
            "--no-ansi", "--progress", "off",
        })
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment[RoleVariable] = role;
        start.Environment[ProjectsVariable] = projects;
        start.Environment[ResultVariable] = ResultPath(workspace, role);
        if (caseId is not null)
        {
            start.Environment[CaseVariable] = caseId;
        }

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
