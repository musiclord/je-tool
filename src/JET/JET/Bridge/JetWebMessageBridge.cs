using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using JET.Application;
using JET.Domain;
using Microsoft.Web.WebView2.Core;

namespace JET.Bridge;

public sealed class JetWebMessageBridge(CoreWebView2 webView, ActionDispatcher dispatcher)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private bool _attached;

    public void Attach()
    {
        if (_attached)
        {
            return;
        }

        webView.WebMessageReceived += OnWebMessageReceived;
        _attached = true;
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JetRequestEnvelope? request = null;
        var requestId = string.Empty;
        var correlationId = Guid.NewGuid().ToString("N");

        try
        {
            request = JsonSerializer.Deserialize<JetRequestEnvelope>(e.WebMessageAsJson, JsonOptions);

            if (request is null)
            {
                throw new InvalidOperationException("JET bridge request is empty.");
            }

            requestId = request.RequestId ?? string.Empty;

            if (string.IsNullOrWhiteSpace(request.RequestId))
            {
                throw new InvalidOperationException("JET bridge requestId is required.");
            }

            if (string.IsNullOrWhiteSpace(request.Action))
            {
                throw new InvalidOperationException("JET bridge action is required.");
            }

            var data = await dispatcher.CancellationRegistry.RunAsync(
                request.RequestId,
                cancellationToken => dispatcher.DispatchAsync(
                    request.Action,
                    request.Payload,
                    cancellationToken,
                    correlationId));
            Post(new JetResponseEnvelope(request.RequestId, true, data, null, correlationId));
        }
        catch (Exception ex)
        {
            Post(new JetResponseEnvelope(requestId, false, null, ToErrorDto(ex), correlationId));
        }
    }

    /// <summary>
    /// 非預期例外回給畫面的固定訊息。例外原文可能是英文，也可能含本機路徑，因此不送到畫面；
    /// ActionDispatcher 已在丟出前把例外寫進日誌，支援人員從支援日誌與本機診斷日誌判斷原因。
    /// </summary>
    public const string UnexpectedErrorMessage =
        "發生非預期的錯誤，這個動作沒有完成。請按畫面上方的「輸出支援日誌」，把檔案交給支援人員。";

    /// <summary>JetActionException 與取消有穩定 wire code；其餘例外一律 bridge_error，訊息固定，不帶例外原文。</summary>
    public static JetErrorDto ToErrorDto(Exception exception)
    {
        return exception switch
        {
            JetActionException actionException => new JetErrorDto(
                actionException.Code,
                actionException.Message,
                actionException.Field,
                actionException.Details),
            OperationCanceledException => new JetErrorDto(JetErrorCodes.OperationCancelled, "作業已取消。"),
            _ => new JetErrorDto(JetErrorCodes.BridgeError, UnexpectedErrorMessage)
        };
    }

    private void Post(JetResponseEnvelope response)
    {
        var json = JsonSerializer.Serialize(response, JsonOptions);
        PostSafely(json, webView.PostWebMessageAsJson);
    }

    internal static void PostSafely(string json, Action<string> post)
    {
        try
        {
            post(json);
        }
        catch (Exception exception) when (
            exception is COMException or ObjectDisposedException or InvalidOperationException)
        {
            // 關窗途中 WebView 已拆除，遲到 response 已無接收者；靜默丟棄，避免 async void 二次 Post 崩潰。
        }
    }
}

internal sealed record JetRequestEnvelope(
    string? RequestId,
    string? Action,
    JsonElement Payload);

internal sealed record JetResponseEnvelope(
    string RequestId,
    bool Ok,
    object? Data,
    JetErrorDto? Error,
    string CorrelationId);

public sealed record JetErrorDto(
    string Code,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Field = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<JetErrorDetail>? Details = null);
