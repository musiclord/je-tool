using System.Text.Json;

namespace JET
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
#if JET_AGENT_GUI_TEST
            var agentGuiProfile = AgentGuiTestProfile.LoadRequired();
            using var agentGuiMutex = new Mutex(
                initiallyOwned: true,
                name: agentGuiProfile.MutexName,
                createdNew: out var agentGuiMutexCreated);
            if (!agentGuiMutexCreated)
            {
                throw new InvalidOperationException(
                    $"Another JET AgentGuiTest '{agentGuiProfile.ChildId}' child is already running for this run id.");
            }

            var fallbackLog = new GlobalExceptionFallbackLog(agentGuiProfile.DiagnosticLogDirectory);
#else
            var fallbackLog = GlobalExceptionFallbackLog.CreateDefault();
#endif
            System.Windows.Forms.Application.ThreadException += (_, eventArgs) =>
                fallbackLog.Write("Application.ThreadException", eventArgs.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
                fallbackLog.Write(
                    "AppDomain.UnhandledException",
                    eventArgs.ExceptionObject as Exception
                        ?? new InvalidOperationException($"Unhandled non-exception object: {eventArgs.ExceptionObject}"),
                    eventArgs.IsTerminating);
            TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
            {
                fallbackLog.Write("TaskScheduler.UnobservedTaskException", eventArgs.Exception);
                eventArgs.SetObserved();
            };

            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();
#if JET_AGENT_GUI_TEST
            System.Windows.Forms.Application.Run(new Form1(agentGuiProfile));
#else
            System.Windows.Forms.Application.Run(new Form1());
#endif
        }
    }

    /// <summary>
    /// Release 也啟用的最後一道例外後盾。它不依賴 dev-only logger，寫入失敗時自行吞納，
    /// 避免診斷動作在程序已不穩定時造成第二次崩潰。
    /// </summary>
    internal sealed class GlobalExceptionFallbackLog
    {
        private readonly Lock _writeGate = new();

        internal GlobalExceptionFallbackLog(string directory)
        {
            FilePath = Path.Combine(
                directory,
                $"jet-unhandled-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.ndjson");
        }

        internal string FilePath { get; }

        internal static GlobalExceptionFallbackLog CreateDefault() =>
            new(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JET",
                "logs"));

        internal void Write(string source, Exception exception, bool isTerminating = false)
        {
            try
            {
                var entry = new GlobalExceptionFallbackEntry(
                    DateTimeOffset.UtcNow,
                    source,
                    isTerminating,
                    exception.GetType().FullName ?? exception.GetType().Name,
                    exception.Message,
                    exception.ToString());
                var line = JsonSerializer.Serialize(entry, GlobalExceptionFallbackJsonContext.Default.GlobalExceptionFallbackEntry);

                lock (_writeGate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    File.AppendAllText(FilePath, line + '\n');
                }
            }
            catch
            {
                // 例外後盾不得再影響主程式；磁碟滿、ACL 或 shutdown I/O 失敗時放棄該行。
            }
        }
    }

    internal sealed record GlobalExceptionFallbackEntry(
        DateTimeOffset TimestampUtc,
        string Source,
        bool IsTerminating,
        string ExceptionType,
        string Message,
        string Exception);

    [System.Text.Json.Serialization.JsonSerializable(typeof(GlobalExceptionFallbackEntry))]
    internal sealed partial class GlobalExceptionFallbackJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
}
