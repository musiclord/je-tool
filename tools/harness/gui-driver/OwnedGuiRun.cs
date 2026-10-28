using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jet.GuiDriver;

/// <summary>
/// 一次 Gui 情境擁有的暫存目錄與 JET 程序。JET 開在這次執行另建的 Windows 桌面上：使用者的螢幕看不到這個視窗，
/// 也不會被搶走前景或滑鼠。使用者 2026-10-05 原話是測試「時常會發生跟我搶滑鼠的問題」，2026-10-15 對 Gui 的整理方式選
/// 「全部改到另一個桌面」。驅動程式自己留在使用者的桌面，只經 DevTools 協定操作頁面；要動視窗本身時（例如調整大小），
/// 用 <see cref="OnApplicationWindow"/> 在掛到那個桌面的執行緒上做，因為 Windows 只讓同一個桌面上的執行緒找到與移動那裡的視窗。
/// </summary>
internal sealed partial class OwnedGuiRun : IDisposable
{
    private const string DirectoryPrefix = "jet-agent-gui-";
    private const string MarkerFileName = ".jet-agent-gui-run.json";
    private const int MaximumCleanupEntries = 20_000;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly string[] EnvironmentVariablesToRemove =
    [
        "JET_ACCOUNT_MAPPING_EVIDENCE_PATH",
        "JET_AGENT_GUI_CHILD",
        "JET_AGENT_GUI_ROOT",
        "JET_LEGACY_PARITY_KEEP_OUTPUTS",
        "JET_LEGACY_PARITY_PROVIDER",
        "JET_LEGACY_PARITY_STOP_AFTER",
        "JET_PRESCREEN_MONTHLY_BENCHMARK",
        "JET_PRESCREEN_MONTHLY_BENCHMARK_OUTPUT",
        "JET_PRESCREEN_POSTPERIOD_PROBE",
        "JET_PRESCREEN_POSTPERIOD_PROBE_OUTPUT",
        "JET_PROJECTS_ROOT",
        "JET_SIX_REPORT_EVIDENCE_DIR",
        "JET_SQLSERVER_CONNECTION",
        "JET_STEP41_BASELINE_ROOT",
        "JET_STEP41_PARITY_EVIDENCE_ROOT",
        "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
        "WEBVIEW2_USER_DATA_FOLDER"
    ];

    private Process? _process;
    private IntPtr _desktop;
    private IntPtr _processHandle;

    private OwnedGuiRun(string runId, string rootPath)
    {
        RunId = runId;
        RootPath = rootPath;
    }

    /// <summary>這個驅動程式程序裡正在執行的那一次；驅動程式一次只跑一個情境。</summary>
    internal static OwnedGuiRun? Active { get; private set; }

    internal string RunId { get; }
    internal string RootPath { get; }
    internal Process? Process => _process;
    internal string DesktopName => "JetAgentGui-" + RunId;
    internal string ProjectsRootPath => Path.Combine(RootPath, "projects");
    internal string DevToolsActivePortPath => Path.Combine(
        RootPath,
        "children",
        "primary",
        "webview2",
        "EBWebView",
        "DevToolsActivePort");
    internal string FixtureTracePath => Path.Combine(
        RootPath,
        "children",
        "primary",
        "logs",
        "agent-gui-fixtures.ndjson");

    internal static OwnedGuiRun Create(
        TimeSpan timeout,
        int actionBudget,
        IReadOnlyList<string> fixtures,
        int screenshotBudget = 0)
    {
        // 驅動程式自己的安全上限，要不小於 lanes.json 裡最大的情境預算；JET 的 AgentGuiTestProfile 另有同值上限。
        if (actionBudget is < 1 or > 120)
        {
            throw new GuiInfrastructureException("action_budget_invalid");
        }
        if (screenshotBudget is < 0 or > 2)
        {
            throw new GuiInfrastructureException("screenshot_budget_invalid");
        }

        var runId = Guid.NewGuid().ToString("N");
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var rootPath = Path.Combine(tempRoot, DirectoryPrefix + runId);
        if (!Path.GetDirectoryName(rootPath)!.Equals(tempRoot, StringComparison.OrdinalIgnoreCase)
            || !OwnedDirectoryName().IsMatch(Path.GetFileName(rootPath)))
        {
            throw new GuiInfrastructureException("owned_root_invalid");
        }

        Directory.CreateDirectory(rootPath);
        RejectReparsePoint(rootPath);
        var ownedRun = new OwnedGuiRun(runId, rootPath);
        try
        {
            var marker = new
            {
                schemaVersion = 1,
                runId,
                deadlineUtc = DateTimeOffset.UtcNow.Add(timeout).ToString("O"),
                actionBudget,
                screenshotBudget,
                childCount = 1,
                fixtures
            };
            var markerPath = Path.Combine(rootPath, MarkerFileName);
            using var stream = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, Utf8);
            writer.Write(JsonSerializer.Serialize(marker));
            return ownedRun;
        }
        catch
        {
            ownedRun.TryRemoveRoot(out _);
            throw;
        }
    }

    internal Process StartApplication(string applicationPath)
    {
        if (_process is not null)
        {
            throw new InvalidOperationException("Application process has already started.");
        }

        var workingDirectory = Path.GetDirectoryName(applicationPath)
            ?? throw new GuiInfrastructureException("application_directory_missing");
        var environment = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            environment[(string)entry.Key] = (string?)entry.Value ?? string.Empty;
        }
        foreach (var variableName in EnvironmentVariablesToRemove)
        {
            environment.Remove(variableName);
        }

        environment["JET_AGENT_GUI_ROOT"] = RootPath;
        environment["JET_AGENT_GUI_CHILD"] = "primary";
        environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        environment["NO_COLOR"] = "1";

        _desktop = NativeMethods.CreateDesktopW(DesktopName, IntPtr.Zero, IntPtr.Zero, 0, NativeMethods.GenericAll, IntPtr.Zero);
        if (_desktop == IntPtr.Zero)
        {
            throw new GuiInfrastructureException("gui_desktop_create_failed");
        }

        // 先暫停建立，拿到 Process 物件再讓它開始跑；原本的程序控制代碼留到 Dispose，程序結束後仍讀得到結束碼。
        var startup = new NativeMethods.StartupInfo
        {
            cb = Marshal.SizeOf<NativeMethods.StartupInfo>(),
            lpDesktop = @"WinSta0\" + DesktopName
        };
        var block = Marshal.StringToHGlobalUni(
            string.Concat(environment.Select(pair => pair.Key + "=" + pair.Value + "\0")) + "\0");
        try
        {
            var commandLine = new StringBuilder("\"" + applicationPath + "\"");
            if (!NativeMethods.CreateProcessW(applicationPath, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    NativeMethods.CreateSuspended | NativeMethods.CreateUnicodeEnvironment, block, workingDirectory,
                    ref startup, out var information))
            {
                throw new GuiInfrastructureException("application_start_failed");
            }

            _processHandle = information.hProcess;
            try
            {
                _process = Process.GetProcessById(information.dwProcessId);
                _process.EnableRaisingEvents = true;
                if (NativeMethods.ResumeThread(information.hThread) == uint.MaxValue)
                {
                    throw new GuiInfrastructureException("application_start_failed");
                }
            }
            catch
            {
                // 還停在暫停狀態的程序不會自己結束，先收掉再回報。
                NativeMethods.TerminateProcess(information.hProcess, 1);
                throw;
            }
            finally
            {
                NativeMethods.CloseHandle(information.hThread);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }

        Active = this;
        return _process;
    }

    /// <summary>
    /// 在掛到 JET 那個桌面的執行緒上找到 JET 主視窗，再交給 action 操作，例如調整大小。主視窗是 JET 行程在那個桌面上
    /// 唯一看得見、沒有擁有者的 WinForms 頂層視窗。不用 <c>Process.MainWindowHandle</c>：它取 Z 順序上第一個看得見的
    /// 頂層視窗，而另一個桌面上 Windows 的輸入指示器（<c>UAC_InputIndicatorOverlayWnd</c>）也屬於 JET 行程、排在前面。
    /// 2026-10-15 第五步旅程因此放大了那個 90×90 的小視窗，JET 本身沒變（收據 20261015-034259534-f56aa0b0204a401ba3e9c469a2cd18a9）。
    /// </summary>
    internal void OnApplicationWindow(Action<IntPtr> action)
    {
        var processId = _process?.Id ?? throw new InvalidOperationException("Application has not been started.");
        OnDesktop(() =>
        {
            var windows = new List<IntPtr>();
            var className = new StringBuilder(256);
            NativeMethods.EnumWindows((window, _) =>
            {
                className.Clear();
                if (NativeMethods.GetWindowThreadProcessId(window, out var owner) != 0 && owner == processId
                    && NativeMethods.IsWindowVisible(window)
                    && NativeMethods.GetWindow(window, NativeMethods.GwOwner) == IntPtr.Zero
                    && NativeMethods.GetClassNameW(window, className, className.Capacity) > 0
                    && className.ToString().StartsWith("WindowsForms", StringComparison.Ordinal))
                {
                    windows.Add(window);
                }

                return true;
            }, IntPtr.Zero);
            if (windows.Count != 1)
            {
                throw new GuiInfrastructureException(windows.Count == 0 ? "gui_window_missing" : "gui_window_ambiguous");
            }

            action(windows[0]);
        });
    }

    /// <summary>
    /// 在掛到 JET 那個桌面的執行緒上執行 action；在驅動程式自己的桌面上，Windows 列舉不到也移不動那裡的視窗。
    /// </summary>
    internal void OnDesktop(Action action)
    {
        if (_desktop == IntPtr.Zero)
        {
            throw new InvalidOperationException("Application desktop has not been created.");
        }

        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (!NativeMethods.SetThreadDesktop(_desktop))
                {
                    throw new GuiInfrastructureException("gui_desktop_attach_failed");
                }

                action();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        });
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    internal async Task<bool> StopApplicationAsync(GuiProcessEvidence evidence)
    {
        if (_process is null)
        {
            return true;
        }

        if (_process.HasExited)
        {
            return true;
        }

        evidence.KillAttempted = true;
        try
        {
            _process.Kill(entireProcessTree: true);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
            evidence.KillSucceeded = _process.HasExited;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            evidence.KillSucceeded = _process.HasExited;
        }

        return _process.HasExited;
    }

    internal bool TryRemoveRoot(out string? errorCode)
    {
        errorCode = null;
        if (!Directory.Exists(RootPath))
        {
            return true;
        }

        var validationDeadline = DateTime.UtcNow.AddSeconds(3);
        while (true)
        {
            try
            {
                ValidateOwnedRootBeforeDelete();
                break;
            }
            catch (IOException) when (DateTime.UtcNow < validationDeadline)
            {
                Thread.Sleep(150);
            }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < validationDeadline)
            {
                Thread.Sleep(150);
            }
            catch
            {
                errorCode = "owned_root_validation_failed";
                return false;
            }
        }

        var deadline = DateTime.UtcNow.AddSeconds(8);
        do
        {
            try
            {
                Directory.Delete(RootPath, recursive: true);
                return !Directory.Exists(RootPath);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(200);
            }
            catch
            {
                errorCode = "owned_root_delete_failed";
                return false;
            }
        }
        while (DateTime.UtcNow < deadline);

        errorCode = "owned_root_delete_failed";
        return false;
    }

    private void ValidateOwnedRootBeforeDelete()
    {
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootPath));
        if (!Path.GetDirectoryName(rootFull)!.Equals(tempRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(rootFull).Equals(DirectoryPrefix + RunId, StringComparison.Ordinal)
            || !OwnedDirectoryName().IsMatch(Path.GetFileName(rootFull)))
        {
            throw new InvalidOperationException("Owned root escaped the process temp directory.");
        }

        RejectReparsePoint(rootFull);
        var markerPath = Path.Combine(rootFull, MarkerFileName);
        RejectReparsePoint(markerPath);
        using (var marker = JsonDocument.Parse(File.ReadAllText(markerPath)))
        {
            if (!marker.RootElement.TryGetProperty("schemaVersion", out var schemaVersion)
                || schemaVersion.GetInt32() != 1
                || !marker.RootElement.TryGetProperty("runId", out var runId)
                || !string.Equals(runId.GetString(), RunId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Owned root marker does not match this run.");
            }
        }

        var directories = new Stack<string>();
        directories.Push(rootFull);
        var entryCount = 0;
        while (directories.Count > 0)
        {
            var directory = directories.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                entryCount++;
                if (entryCount > MaximumCleanupEntries)
                {
                    throw new InvalidOperationException("Owned root exceeded the cleanup entry limit.");
                }

                RejectReparsePoint(entry);
                if (Directory.Exists(entry))
                {
                    directories.Push(entry);
                }
            }
        }
    }

    internal static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new GuiInfrastructureException("reparse_point_rejected");
        }
    }

    [GeneratedRegex("^jet-agent-gui-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex OwnedDirectoryName();

    public void Dispose()
    {
        _process?.Dispose();
        if (_processHandle != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_processHandle);
            _processHandle = IntPtr.Zero;
        }

        // 桌面在最後一個控制代碼關閉、上面也沒有程序時才會消失；JET 已由 StopApplicationAsync 結束。
        if (_desktop != IntPtr.Zero)
        {
            NativeMethods.CloseDesktop(_desktop);
            _desktop = IntPtr.Zero;
        }

        if (ReferenceEquals(Active, this))
        {
            Active = null;
        }
    }

    private static class NativeMethods
    {
        internal const uint GenericAll = 0x10000000;
        internal const uint CreateSuspended = 0x00000004;
        internal const uint CreateUnicodeEnvironment = 0x00000400;

#pragma warning disable CS0649 // 欄位由 Windows 填入或保留預設值。
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct StartupInfo
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessInformation
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }
#pragma warning restore CS0649

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateDesktopW(string desktop, IntPtr device, IntPtr deviceMode, uint flags,
            uint desiredAccess, IntPtr securityAttributes);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseDesktop(IntPtr desktop);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetThreadDesktop(IntPtr desktop);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcessW(string applicationName, StringBuilder commandLine,
            IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        internal const uint GwOwner = 4;

        internal delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetWindow(IntPtr window, uint command);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetClassNameW(IntPtr window, StringBuilder className, int maximumCount);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}
