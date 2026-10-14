using System.Diagnostics;
using System.Net;
using System.Text.Json;
using JET;
using JET.Bridge;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;

namespace Jet.BrowserHost;

/// <summary>
/// 開發用瀏覽器主機：正式 wwwroot 在一般瀏覽器執行，action 經 HTTP 交給正式 ActionDispatcher，
/// host→web 事件改走 Server-Sent Events。審計運算、資料庫與報表全部是正式程式碼；
/// 這裡只替換 WebView2 傳輸與原生視窗能力，不能取代 AgentGuiTest 的真實 WebView2 驗證。
/// </summary>
internal static class Program
{
    private const int DefaultPort = 4244;
    private const string Principal = @"JET-BROWSER\auditor";
    private const string JetApiScript = "<script defer src=\"./js/jet-api.js\"></script>";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> Main(string[] args)
    {
        var repositoryRoot = FindRepositoryRoot();
        var port = ReadPort();
        var seed = ReadSeed();
        var paths = HostPaths.Create(repositoryRoot, ReadDataFolder());
        if (args.Contains("--reset", StringComparer.Ordinal))
        {
            paths.ResetProjects();
            Console.WriteLine("[host] 已清除先前的合成案件，重新建立。");
        }

        paths.EnsureCreated();
        var events = new EventBroker();
        using var runtime = AppCompositionRoot.CreateRuntime(
            new BrowserHostShell(),
            paths.Projects,
            enableDevTools: true,
            eventPublisher: events,
            // 空字串讓 composition 不讀 JET_SQLSERVER_CONNECTION；此主機永遠不連 SQL Server。
            sqlServerConnectionString: string.Empty,
            diagnosticLogDirectory: paths.Logs,
            principalName: Principal,
            userProfileDirectory: paths.Profile);
        var dispatcher = runtime.Dispatcher;
        var seeded = true;
        if (seed == TaiwanSmeSeed)
        {
            seeded = await Jet.Harness.TaiwanSme.TaiwanSmeSeeder.EnsureSeededAsync(dispatcher, paths.Projects, paths.Run).ConfigureAwait(false);
        }
        else
        {
            await DemoSeeder.EnsureSeededAsync(dispatcher, paths.Projects).ConfigureAwait(false);
        }

        if (args.Contains("--seed-only", StringComparer.Ordinal))
        {
            // 只建立案件與比對報告，不啟動網站；預覽前先跑一次，避免啟動等候過久。
            return seeded ? 0 : 1;
        }

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = paths.Run,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
        var app = builder.Build();
        var allowedHosts = new[] { $"127.0.0.1:{port}", $"localhost:{port}" };
        var contentTypes = new FileExtensionContentTypeProvider();

        app.Use(async (context, next) =>
        {
            if (!allowedHosts.Contains(context.Request.Host.Value, StringComparer.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            await next(context).ConfigureAwait(false);
        });

        app.MapPost("/__jet/action", context => HandleActionAsync(context, dispatcher, allowedHosts));
        app.MapGet("/__jet/events", context => HandleEventsAsync(context, events));
        app.MapGet("/__jet/bridge.js", context => SendFileAsync(
            context, Path.Combine(paths.HostSource, "browser-bridge.js"), "text/javascript; charset=utf-8"));
        app.MapGet("/", context => SendIndexAsync(context, paths.WebRoot));
        app.MapGet("/index.html", context => SendIndexAsync(context, paths.WebRoot));
        app.MapGet("/{**asset}", context => SendAssetAsync(context, paths.WebRoot, contentTypes));

        _ = ParentProcessWatch.TryWatch()?.ContinueWith(_ =>
        {
            Console.WriteLine("[host] 啟動主機的程序已結束，瀏覽器主機一併停止。");
            app.Lifetime.StopApplication();
        }, TaskScheduler.Default);

        Console.WriteLine($"[host] JET browser host: http://127.0.0.1:{port}/");
        Console.WriteLine($"[host] 合成案件目錄：{Path.GetRelativePath(repositoryRoot, paths.Projects)}");
        Console.WriteLine($"[host] 診斷日誌目錄：{Path.GetRelativePath(repositoryRoot, paths.Logs)}");
        await app.RunAsync().ConfigureAwait(false);
        return 0;
    }

    private static async Task HandleActionAsync(
        HttpContext context,
        ActionDispatcher dispatcher,
        IReadOnlyCollection<string> allowedHosts)
    {
        // 只接受同源的 JSON POST：其他網站無法用簡單表單或 text/plain 請求觸發本機 action。
        var origin = context.Request.Headers.Origin.ToString();
        var sameOrigin = allowedHosts.Any(host =>
            string.Equals(origin, "http://" + host, StringComparison.OrdinalIgnoreCase));
        if (!sameOrigin || context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var requestId = string.Empty;
        var action = "(unknown)";
        var correlationId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();
        BrowserResponseEnvelope response;
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body).ConfigureAwait(false);
            var root = document.RootElement;
            requestId = ReadString(root, "requestId") ?? string.Empty;
            action = ReadString(root, "action") ?? "(unknown)";
            if (string.IsNullOrWhiteSpace(requestId))
            {
                throw new InvalidOperationException("JET bridge requestId is required.");
            }

            if (action == "(unknown)" || string.IsNullOrWhiteSpace(action))
            {
                throw new InvalidOperationException("JET bridge action is required.");
            }

            var payload = root.TryGetProperty("payload", out var value)
                ? value.Clone()
                : JsonDocument.Parse("{}").RootElement.Clone();
            // 與 JetWebMessageBridge 相同：頁面離開不取消後端作業，取消只走 operation.cancel。
            var data = await dispatcher.CancellationRegistry.RunAsync(
                requestId,
                cancellationToken => dispatcher.DispatchAsync(action, payload, cancellationToken, correlationId))
                .ConfigureAwait(false);
            response = new BrowserResponseEnvelope(requestId, true, data, null, correlationId);
            Console.WriteLine($"[action] {action} ok {stopwatch.ElapsedMilliseconds} ms");
        }
        catch (Exception exception)
        {
            var error = JetWebMessageBridge.ToErrorDto(exception);
            response = new BrowserResponseEnvelope(requestId, false, null, error, correlationId);
            Console.WriteLine(
                $"[action] {action} failed {stopwatch.ElapsedMilliseconds} ms code={error.Code} correlation={correlationId}: {error.Message}");
        }

        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(JsonSerializer.Serialize(response, JsonOptions)).ConfigureAwait(false);
    }

    private static async Task HandleEventsAsync(HttpContext context, EventBroker events)
    {
        var cancellationToken = context.RequestAborted;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        var (id, reader) = events.Subscribe();
        try
        {
            await context.Response.WriteAsync(": connected\n\n", cancellationToken).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                wait.CancelAfter(TimeSpan.FromSeconds(15));
                string line;
                try
                {
                    line = "data: " + await reader.ReadAsync(wait.Token).ConfigureAwait(false) + "\n\n";
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    line = ": keep-alive\n\n";
                }

                await context.Response.WriteAsync(line, cancellationToken).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 頁面關閉或重新整理時 SSE 連線結束。
        }
        finally
        {
            events.Unsubscribe(id);
        }
    }

    private static async Task SendIndexAsync(HttpContext context, string webRoot)
    {
        var html = await File.ReadAllTextAsync(Path.Combine(webRoot, "index.html")).ConfigureAwait(false);
        if (!html.Contains(JetApiScript, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsync("index.html 找不到 jet-api.js 的載入位置，瀏覽器主機無法接上 bridge。")
                .ConfigureAwait(false);
            return;
        }

        html = html
            .Replace(JetApiScript, "<script defer src=\"/__jet/bridge.js\"></script>\n  " + JetApiScript, StringComparison.Ordinal)
            .Replace("<title>JE Tool</title>", "<title>JE Tool（瀏覽器開發主機）</title>", StringComparison.Ordinal);
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html).ConfigureAwait(false);
    }

    private static Task SendAssetAsync(HttpContext context, string webRoot, FileExtensionContentTypeProvider contentTypes)
    {
        var relative = context.Request.RouteValues["asset"] as string ?? string.Empty;
        var segments = relative.Split('/', '\\');
        if (Path.IsPathRooted(relative) || segments.Any(part => part is ".." or "."))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        var root = Path.GetFullPath(webRoot) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(webRoot, relative));
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(target))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        var contentType = contentTypes.TryGetContentType(target, out var known) ? known : "application/octet-stream";
        return SendFileAsync(context, target, contentType);
    }

    private static async Task SendFileAsync(HttpContext context, string path, string contentType)
    {
        if (!File.Exists(path))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = contentType;
        await context.Response.SendFileAsync(path).ConfigureAwait(false);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private const string DemoSeed = "demo";
    private const string TaiwanSmeSeed = "taiwan-sme";

    /// <summary>
    /// JET_BROWSER_HOST_SEED 決定案件目錄空白時建立哪一組合成案件：demo（預設，兩個示範案件）
    /// 或 taiwan-sme（台灣中小企業一整年的總帳與試算表）。
    /// </summary>
    private static string ReadSeed()
    {
        var value = Environment.GetEnvironmentVariable("JET_BROWSER_HOST_SEED");
        if (string.IsNullOrWhiteSpace(value))
        {
            return DemoSeed;
        }

        return value.Trim() switch
        {
            DemoSeed => DemoSeed,
            TaiwanSmeSeed => TaiwanSmeSeed,
            _ => throw new InvalidOperationException("JET_BROWSER_HOST_SEED 只接受 demo 或 taiwan-sme。"),
        };
    }

    /// <summary>
    /// JET_BROWSER_HOST_DATA 是 artifacts 底下的資料夾名稱，預設 browser-host。另開一台主機時給不同名稱，
    /// 兩台主機的案件、日誌與設定就不會互相覆蓋。只接受單一層的資料夾名稱，確保資料一定留在 artifacts 底下。
    /// </summary>
    private static string ReadDataFolder()
    {
        var value = Environment.GetEnvironmentVariable("JET_BROWSER_HOST_DATA");
        if (string.IsNullOrWhiteSpace(value))
        {
            return "browser-host";
        }

        var name = value.Trim();
        if (name is "." or ".." || name.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')))
        {
            throw new InvalidOperationException("JET_BROWSER_HOST_DATA 只接受英數字、連字號、底線與點組成的單一資料夾名稱。");
        }

        return name;
    }

    private static int ReadPort()
    {
        var value = Environment.GetEnvironmentVariable("PORT");
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultPort;
        }

        if (!int.TryParse(value, out var port) || port is < 1 or > 65535)
        {
            throw new InvalidOperationException("PORT must be an integer from 1 to 65535.");
        }

        return port;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "tools", "verify.ps1")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("找不到 je-tool 儲存庫根目錄（tools/verify.ps1）。");
    }
}

internal sealed record BrowserResponseEnvelope(
    string RequestId,
    bool Ok,
    object? Data,
    JetErrorDto? Error,
    string CorrelationId);

/// <summary>主機的所有讀寫位置；案件與日誌都在 Git 忽略的 artifacts 之下，預設資料夾是 browser-host。</summary>
internal sealed record HostPaths(string Run, string Projects, string Logs, string Profile, string WebRoot, string HostSource)
{
    public static HostPaths Create(string repositoryRoot, string dataFolder)
    {
        var run = Path.Combine(repositoryRoot, "artifacts", dataFolder);
        return new HostPaths(
            run,
            Path.Combine(run, "projects"),
            Path.Combine(run, "logs"),
            Path.Combine(run, "profile"),
            Path.Combine(repositoryRoot, "src", "JET", "JET", "wwwroot"),
            Path.Combine(repositoryRoot, "tools", "harness", "browser-host"));
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Projects);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Profile);
    }

    public void ResetProjects()
    {
        var projects = Path.GetFullPath(Projects);
        if (!projects.StartsWith(Path.GetFullPath(Run) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to reset a directory outside the browser host data folder.");
        }

        if (Directory.Exists(projects))
        {
            Directory.Delete(projects, recursive: true);
        }

        // 台灣中小企業案件的比對報告跟著案件重建，舊報告一併移除。
        var report = Path.Combine(Run, "seed-report");
        if (Directory.Exists(report))
        {
            Directory.Delete(report, recursive: true);
        }
    }
}
