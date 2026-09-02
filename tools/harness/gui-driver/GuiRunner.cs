using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace Jet.GuiDriver;

internal static class GuiRunner
{
    private const int MaximumDevToolsFileBytes = 1024;
    private const int MaximumDiscoveryBytes = 65_536;
    private const string ApplicationUrl = "https://app.jet.local/index.html";

    private const string BridgeProbeScript = """
        (async function () {
          if (!window.JetApi || typeof window.JetApi.isReady !== 'function' || !window.JetApi.isReady()) {
            return false;
          }
          try {
            await window.JetApi.systemPing({});
            return true;
          } catch (_) {
            return false;
          }
        })()
        """;

    private const string UiProbeScript = """
        (function () {
          function visible(element) {
            if (!element || element.hidden) { return false; }
            var style = window.getComputedStyle(element);
            var rect = element.getBoundingClientRect();
            return style.display !== 'none' && style.visibility !== 'hidden'
              && Number(style.opacity || '1') > 0 && rect.width > 0 && rect.height > 0;
          }
          var picker = document.querySelector('[data-bind="project-picker"]');
          var newProjectButton = document.querySelector('[data-action="picker-new"]');
          var exitButton = document.querySelector('[data-action="app-exit"]');
          var newProjectRect = newProjectButton ? newProjectButton.getBoundingClientRect() : null;
          var exitRect = exitButton ? exitButton.getBoundingClientRect() : null;
          return {
            documentLoaded: document.readyState === 'complete',
            bridgeReady: !!(window.JetApi && window.JetApi.isReady && window.JetApi.isReady()),
            projectPickerVisible: visible(picker),
            newProjectButtonVisible: visible(newProjectButton),
            newProjectX: newProjectRect ? newProjectRect.left + (newProjectRect.width / 2) : 0,
            newProjectY: newProjectRect ? newProjectRect.top + (newProjectRect.height / 2) : 0,
            exitButtonVisible: visible(exitButton),
            exitX: exitRect ? exitRect.left + (exitRect.width / 2) : 0,
            exitY: exitRect ? exitRect.top + (exitRect.height / 2) : 0
          };
        })()
        """;

    internal static async Task<GuiRunOutcome> ExecuteAsync(DriverOptions options)
    {
        var outcome = new GuiRunOutcome(options.Scenario);
        OwnedGuiRun? ownedRun = null;
        try
        {
            ownedRun = OwnedGuiRun.Create(
                options.Timeout,
                options.Scenario.ActionLimit,
                options.Scenario.Fixtures);
            using var deadline = new CancellationTokenSource(options.Timeout);
            var process = ownedRun.StartApplication(options.ApplicationPath);
            outcome.Process.ProcessId = process.Id;
            outcome.Process.StartedUtc = ReadProcessStartTime(process);

            var port = await WaitForDevToolsPortAsync(ownedRun, process, deadline.Token).ConfigureAwait(false);
            var endpoint = await WaitForApplicationPageAsync(port, process, deadline.Token).ConfigureAwait(false);
            await using var cdp = await CdpSession.ConnectAsync(endpoint, deadline.Token).ConfigureAwait(false);

            var ui = await WaitForUiAsync(cdp, process, outcome.Assertions, deadline.Token).ConfigureAwait(false);
            var bridgeProbe = await cdp.EvaluateAsync(BridgeProbeScript, deadline.Token).ConfigureAwait(false);
            outcome.Assertions.SystemPingSucceeded = bridgeProbe.ValueKind == JsonValueKind.True;
            if (!outcome.Assertions.SystemPingSucceeded)
            {
                throw new GuiCheckException("system_ping_failed");
            }

            await GuiScenarios.ExecuteAsync(
                options.Scenario,
                cdp,
                ownedRun,
                process,
                ui,
                outcome,
                deadline.Token).ConfigureAwait(false);

            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            outcome.Process.Exited = true;
            outcome.Process.ExitCode = process.ExitCode;
            outcome.Assertions.ProcessExited = true;
            if (process.ExitCode != 0)
            {
                throw new GuiCheckException("application_exit_code_nonzero");
            }

            outcome.Status = "passed";
            outcome.ExitCode = 0;
        }
        catch (GuiCheckException exception)
        {
            outcome.Status = "failed";
            outcome.ExitCode = 1;
            outcome.ErrorCode = exception.Code;
        }
        catch (OperationCanceledException)
        {
            outcome.Status = "failed";
            outcome.ExitCode = 1;
            outcome.ErrorCode = "gui_scenario_timeout";
        }
        catch (GuiInfrastructureException exception)
        {
            outcome.Status = "infrastructure_error";
            outcome.ExitCode = 4;
            outcome.ErrorCode = exception.Code;
        }
        catch (Exception exception)
        {
            outcome.Status = "infrastructure_error";
            outcome.ExitCode = 4;
            outcome.ErrorCode = "unexpected_driver_error";
            outcome.ErrorType = exception.GetType().Name;
        }
        finally
        {
            if (ownedRun is not null)
            {
                var process = ownedRun.Process;
                var stopped = await ownedRun.StopApplicationAsync(outcome.Process).ConfigureAwait(false);
                if (process is not null && process.HasExited)
                {
                    outcome.Process.Exited = true;
                    outcome.Process.ExitCode = process.ExitCode;
                    outcome.Assertions.ProcessExited = true;
                }

                string? cleanupError = null;
                var removed = stopped && ownedRun.TryRemoveRoot(out cleanupError);
                outcome.Cleanup.RootRemoved = removed;
                if (!removed)
                {
                    outcome.Status = "infrastructure_error";
                    outcome.ExitCode = 4;
                    outcome.ErrorCode = cleanupError ?? "owned_cleanup_failed";
                    outcome.ErrorType = null;
                }

                ownedRun.Dispose();
            }

            outcome.CompletedUtc = DateTimeOffset.UtcNow;
        }

        return outcome;
    }

    private static async Task<int> WaitForDevToolsPortAsync(
        OwnedGuiRun ownedRun,
        Process process,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfExited(process, "application_exited_before_devtools");
            var path = ownedRun.DevToolsActivePortPath;
            if (File.Exists(path))
            {
                OwnedGuiRun.RejectReparsePoint(path);
                var info = new FileInfo(path);
                if (info.Length is > 0 and <= MaximumDevToolsFileBytes)
                {
                    string[] lines;
                    try
                    {
                        lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (lines.Length >= 2
                        && int.TryParse(lines[0], out var port)
                        && port is > 0 and <= 65535
                        && lines[1].StartsWith("/devtools/browser/", StringComparison.Ordinal))
                    {
                        return port;
                    }
                }
            }

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<Uri> WaitForApplicationPageAsync(
        int port,
        Process process,
        CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(2)
        };

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfExited(process, "application_exited_before_page");
            try
            {
                using var response = await client.GetAsync("json/list", cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                    if (bytes.Length > MaximumDiscoveryBytes)
                    {
                        throw new GuiInfrastructureException("cdp_discovery_too_large");
                    }

                    using var document = JsonDocument.Parse(bytes);
                    var matches = document.RootElement
                        .EnumerateArray()
                        .Where(item => item.TryGetProperty("type", out var type)
                            && type.GetString() == "page"
                            && item.TryGetProperty("url", out var url)
                            && url.GetString() == ApplicationUrl
                            && item.TryGetProperty("webSocketDebuggerUrl", out var socket)
                            && socket.ValueKind == JsonValueKind.String)
                        .ToArray();
                    if (matches.Length > 1)
                    {
                        throw new GuiInfrastructureException("multiple_application_pages");
                    }

                    if (matches.Length == 1
                        && Uri.TryCreate(
                            matches[0].GetProperty("webSocketDebuggerUrl").GetString(),
                            UriKind.Absolute,
                            out var endpoint))
                    {
                        return CdpSession.NormalizeLoopbackEndpoint(endpoint, port);
                    }
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<UiProbe> WaitForUiAsync(
        CdpSession cdp,
        Process process,
        GuiAssertions assertions,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfExited(process, "application_exited_before_ui_ready");
            var value = await cdp.EvaluateAsync(UiProbeScript, cancellationToken).ConfigureAwait(false);
            var probe = new UiProbe(
                ReadBoolean(value, "documentLoaded"),
                ReadBoolean(value, "bridgeReady"),
                ReadBoolean(value, "projectPickerVisible"),
                ReadBoolean(value, "newProjectButtonVisible"),
                ReadDouble(value, "newProjectX"),
                ReadDouble(value, "newProjectY"),
                ReadBoolean(value, "exitButtonVisible"),
                ReadDouble(value, "exitX"),
                ReadDouble(value, "exitY"));
            assertions.DocumentLoaded = probe.DocumentLoaded;
            assertions.BridgeReady = probe.BridgeReady;
            assertions.ProjectPickerVisible = probe.ProjectPickerVisible;
            assertions.NewProjectButtonVisible = probe.NewProjectButtonVisible;
            assertions.ExitButtonVisible = probe.ExitButtonVisible;
            if (probe.DocumentLoaded
                && probe.BridgeReady
                && probe.ProjectPickerVisible
                && probe.NewProjectButtonVisible
                && double.IsFinite(probe.NewProjectX)
                && double.IsFinite(probe.NewProjectY)
                && probe.NewProjectX > 0
                && probe.NewProjectY > 0
                && probe.ExitButtonVisible
                && double.IsFinite(probe.ExitX)
                && double.IsFinite(probe.ExitY)
                && probe.ExitX > 0
                && probe.ExitY > 0)
            {
                return probe;
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool ReadBoolean(JsonElement value, string name)
    {
        return value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.True;
    }

    private static double ReadDouble(JsonElement value, string name)
    {
        return value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(name, out var property)
            && property.TryGetDouble(out var number)
                ? number
                : double.NaN;
    }

    private static void ThrowIfExited(Process process, string code)
    {
        if (process.HasExited)
        {
            throw new GuiCheckException(code);
        }
    }

    private static DateTimeOffset ReadProcessStartTime(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return DateTimeOffset.UtcNow;
        }
    }

    internal sealed record UiProbe(
        bool DocumentLoaded,
        bool BridgeReady,
        bool ProjectPickerVisible,
        bool NewProjectButtonVisible,
        double NewProjectX,
        double NewProjectY,
        bool ExitButtonVisible,
        double ExitX,
        double ExitY);
}
