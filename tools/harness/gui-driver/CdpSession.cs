using System.Net;
using System.Net.WebSockets;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Jet.GuiDriver;

internal sealed class CdpSession : IAsyncDisposable
{
    private const int MaximumMessageBytes = 262_144;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly ClientWebSocket _socket = new();
    private int _nextId;

    private CdpSession()
    {
        _socket.Options.Proxy = null;
    }

    internal static async Task<CdpSession> ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        ValidateLoopbackEndpoint(endpoint);
        var session = new CdpSession();
        try
        {
            await session._socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async Task<JsonElement> EvaluateAsync(string expression, CancellationToken cancellationToken)
    {
        var result = await SendAsync(
            "Runtime.evaluate",
            new
            {
                expression,
                awaitPromise = true,
                returnByValue = true,
                userGesture = false
            },
            cancellationToken).ConfigureAwait(false);

        if (result.TryGetProperty("exceptionDetails", out _)
            || !result.TryGetProperty("result", out var remoteObject)
            || remoteObject.TryGetProperty("subtype", out var subtype)
                && subtype.ValueKind == JsonValueKind.String
                && subtype.GetString() == "error"
            || !remoteObject.TryGetProperty("value", out var value))
        {
            throw new GuiInfrastructureException("cdp_evaluation_failed");
        }

        return value.Clone();
    }

    internal async Task ClickAsync(double x, double y, CancellationToken cancellationToken)
    {
        await DispatchMouseAsync("mouseMoved", x, y, "none", 0, 0, cancellationToken).ConfigureAwait(false);
        await DispatchMouseAsync("mousePressed", x, y, "left", 1, 1, cancellationToken).ConfigureAwait(false);
        await DispatchMouseAsync("mouseReleased", x, y, "left", 0, 1, cancellationToken).ConfigureAwait(false);
    }

    internal Task ScrollAsync(double x, double y, double deltaX, double deltaY, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x <= 0 || y <= 0
            || !double.IsFinite(deltaX) || !double.IsFinite(deltaY)
            || Math.Abs(deltaX) > 4000 || Math.Abs(deltaY) > 4000)
        {
            throw new GuiInfrastructureException("scroll_parameters_invalid");
        }
        return SendAndDiscardAsync("Input.dispatchMouseEvent",
            new { type = "mouseWheel", x, y, deltaX, deltaY }, cancellationToken);
    }

    internal async Task<byte[]> CaptureScreenshotAsync(CancellationToken cancellationToken)
    {
        var result = await SendAsync("Page.captureScreenshot",
            new { format = "png", captureBeyondViewport = false }, cancellationToken,
            maximumResponseBytes: 3 * 1024 * 1024).ConfigureAwait(false);
        if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String
            || data.GetString() is not { Length: > 0 and <= 2_800_000 } encoded)
        {
            throw new GuiInfrastructureException("screenshot_response_invalid");
        }
        var bytes = Convert.FromBase64String(encoded);
        if (bytes.Length > 2 * 1024 * 1024)
        {
            throw new GuiInfrastructureException("screenshot_too_large");
        }
        return bytes;
    }

    internal async Task TypeTextAsync(string text, CancellationToken cancellationToken)
    {
        if (text.Length is < 1 or > 128 || text.Any(character => !IsClosedInputCharacter(character)))
        {
            throw new GuiInfrastructureException("keyboard_text_invalid");
        }

        foreach (var character in text)
        {
            await DispatchCharacterAsync(character, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task TypeDateAsync(string isoDate, CancellationToken cancellationToken)
    {
        if (!DateOnly.TryParseExact(
                isoDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            throw new GuiInfrastructureException("keyboard_date_invalid");
        }

        await TypeTextAsync(date.Year.ToString("0000", CultureInfo.InvariantCulture), cancellationToken)
            .ConfigureAwait(false);
        await DispatchNavigationKeyAsync("ArrowRight", "ArrowRight", 0x27, cancellationToken)
            .ConfigureAwait(false);
        await TypeTextAsync(date.Month.ToString("00", CultureInfo.InvariantCulture), cancellationToken)
            .ConfigureAwait(false);
        await DispatchNavigationKeyAsync("ArrowRight", "ArrowRight", 0x27, cancellationToken)
            .ConfigureAwait(false);
        await TypeTextAsync(date.Day.ToString("00", CultureInfo.InvariantCulture), cancellationToken)
            .ConfigureAwait(false);
    }

    // Closed synthetic IME updates. The scenario then clicks Save and checks persisted text.
    // This exercises WebView composition events without changing the desktop input method.
    internal async Task TypeSyntheticCompositionAsync(CancellationToken cancellationToken)
    {
        await SendAndDiscardAsync("Input.imeSetComposition",
            new { text = "銀", selectionStart = 1, selectionEnd = 1 }, cancellationToken);
        await SendAndDiscardAsync("Input.imeSetComposition",
            new { text = "銀行", selectionStart = 2, selectionEnd = 2 }, cancellationToken);
    }

    internal Task PressKeyAsync(string key, CancellationToken cancellationToken)
    {
        var keyDefinition = key switch
        {
            "ArrowDown" => (Code: "ArrowDown", VirtualKey: 0x28),
            "ArrowUp" => (Code: "ArrowUp", VirtualKey: 0x26),
            "ArrowLeft" => (Code: "ArrowLeft", VirtualKey: 0x25),
            "ArrowRight" => (Code: "ArrowRight", VirtualKey: 0x27),
            "PageUp" => (Code: "PageUp", VirtualKey: 0x21),
            "PageDown" => (Code: "PageDown", VirtualKey: 0x22),
            "Space" => (Code: "Space", VirtualKey: 0x20),
            "Escape" => (Code: "Escape", VirtualKey: 0x1B),
            "Home" => (Code: "Home", VirtualKey: 0x24),
            "End" => (Code: "End", VirtualKey: 0x23),
            "Enter" => (Code: "Enter", VirtualKey: 0x0D),
            "Tab" => (Code: "Tab", VirtualKey: 0x09),
            "Backspace" => (Code: "Backspace", VirtualKey: 0x08),
            _ => throw new GuiInfrastructureException("keyboard_key_invalid")
        };
        return DispatchNavigationKeyAsync(
            key == "Space" ? " " : key,
            keyDefinition.Code,
            keyDefinition.VirtualKey,
            cancellationToken);
    }

    private async Task DispatchCharacterAsync(char character, CancellationToken cancellationToken)
    {
        var uppercase = char.IsAsciiLetterUpper(character);
        var modifiers = uppercase ? 8 : 0;
        var key = character.ToString();
        var code = character switch
        {
            >= 'a' and <= 'z' => "Key" + char.ToUpperInvariant(character),
            >= 'A' and <= 'Z' => "Key" + character,
            >= '0' and <= '9' => "Digit" + character,
            '-' => "Minus",
            ',' => "Comma",
            _ => throw new GuiInfrastructureException("keyboard_character_invalid")
        };
        var virtualKey = character switch
        {
            >= 'a' and <= 'z' => char.ToUpperInvariant(character),
            >= 'A' and <= 'Z' => character,
            >= '0' and <= '9' => character,
            '-' => (char)0xBD,
            ',' => (char)0xBC,
            _ => throw new GuiInfrastructureException("keyboard_character_invalid")
        };

        await SendAndDiscardAsync(
            "Input.dispatchKeyEvent",
            new
            {
                type = "rawKeyDown",
                modifiers,
                key,
                code,
                windowsVirtualKeyCode = (int)virtualKey,
                nativeVirtualKeyCode = (int)virtualKey
            },
            cancellationToken).ConfigureAwait(false);
        await SendAndDiscardAsync(
            "Input.dispatchKeyEvent",
            new
            {
                type = "char",
                modifiers,
                text = key,
                unmodifiedText = uppercase ? char.ToLowerInvariant(character).ToString() : key,
                key,
                code,
                windowsVirtualKeyCode = (int)virtualKey,
                nativeVirtualKeyCode = (int)virtualKey
            },
            cancellationToken).ConfigureAwait(false);
        await SendAndDiscardAsync(
            "Input.dispatchKeyEvent",
            new
            {
                type = "keyUp",
                modifiers,
                key,
                code,
                windowsVirtualKeyCode = (int)virtualKey,
                nativeVirtualKeyCode = (int)virtualKey
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task DispatchNavigationKeyAsync(
        string key,
        string code,
        int virtualKey,
        CancellationToken cancellationToken)
    {
        await SendAndDiscardAsync(
            "Input.dispatchKeyEvent",
            new
            {
                type = "rawKeyDown",
                key,
                code,
                windowsVirtualKeyCode = virtualKey,
                nativeVirtualKeyCode = virtualKey
            },
            cancellationToken).ConfigureAwait(false);
        await SendAndDiscardAsync(
            "Input.dispatchKeyEvent",
            new
            {
                type = "keyUp",
                key,
                code,
                windowsVirtualKeyCode = virtualKey,
                nativeVirtualKeyCode = virtualKey
            },
            cancellationToken).ConfigureAwait(false);
    }

    // 逗號只用來輸入「每月幾日」這類逗號分隔的清單（例如 28,31）；仍然不接受引號、括號與其他標點。
    private static bool IsClosedInputCharacter(char character) =>
        character is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '-'
            or ',';

    private Task DispatchMouseAsync(
        string type,
        double x,
        double y,
        string button,
        int buttons,
        int clickCount,
        CancellationToken cancellationToken)
    {
        return SendAndDiscardAsync(
            "Input.dispatchMouseEvent",
            new
            {
                type,
                x,
                y,
                button,
                buttons,
                clickCount
            },
            cancellationToken);
    }

    private async Task SendAndDiscardAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        _ = await SendAsync(method, parameters, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement> SendAsync(string method, object parameters, CancellationToken cancellationToken,
        int maximumResponseBytes = MaximumMessageBytes)
    {
        var id = Interlocked.Increment(ref _nextId);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
        if (payload.Length > MaximumMessageBytes)
        {
            throw new GuiInfrastructureException("cdp_request_too_large");
        }

        await _socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            using var message = new MemoryStream();
            var buffer = new byte[16 * 1024];
            WebSocketReceiveResult received;
            do
            {
                received = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    throw new GuiInfrastructureException("cdp_connection_closed");
                }

                if (received.MessageType != WebSocketMessageType.Text
                    || message.Length + received.Count > maximumResponseBytes)
                {
                    throw new GuiInfrastructureException("cdp_response_invalid");
                }

                message.Write(buffer, 0, received.Count);
            }
            while (!received.EndOfMessage);

            using var document = JsonDocument.Parse(message.ToArray());
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var responseId)
                || !responseId.TryGetInt32(out var responseIdValue)
                || responseIdValue != id)
            {
                continue;
            }

            if (root.TryGetProperty("error", out _)
                || !root.TryGetProperty("result", out var result))
            {
                throw new GuiInfrastructureException("cdp_command_failed");
            }

            return result.Clone();
        }
    }

    internal static Uri NormalizeLoopbackEndpoint(Uri endpoint, int expectedPort)
    {
        ValidateLoopbackEndpoint(endpoint);
        if (endpoint.Port != expectedPort
            || !endpoint.AbsolutePath.StartsWith("/devtools/page/", StringComparison.Ordinal))
        {
            throw new GuiInfrastructureException("cdp_page_endpoint_invalid");
        }

        var builder = new UriBuilder(endpoint)
        {
            Host = IPAddress.Loopback.ToString(),
            Port = expectedPort
        };
        return builder.Uri;
    }

    private static void ValidateLoopbackEndpoint(Uri endpoint)
    {
        if (!endpoint.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase)
            || endpoint.UserInfo.Length != 0
            || !IsLoopbackHost(endpoint.Host))
        {
            throw new GuiInfrastructureException("cdp_page_endpoint_not_loopback");
        }
    }

    private static bool IsLoopbackHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            using var closeCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await _socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    string.Empty,
                    closeCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (WebSocketException)
            {
            }
        }

        _socket.Dispose();
    }
}
