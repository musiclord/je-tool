using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using JET.Application;
using Microsoft.Web.WebView2.Core;

namespace JET.Bridge;

/// <summary>
/// IJetEventPublisher 的 WebView2 實作。事件的格式是 { event, requestId, data }，前端看有沒有 event 和 action 的回應區分。
/// requestId 是送出這個事件的畫面請求，前端只把還在等回應的請求的進度交給畫面；不在任何請求裡送出的事件不帶這個鍵。
/// Publish 可能從背景執行緒呼叫，例如匯入是在 Task.Run 裡執行；PostWebMessageAsJson 必須在 UI 執行緒呼叫，
/// 所以用 Bind 時傳入的 SynchronizationContext 轉回 UI 執行緒。
/// Bind 之前 WebView 還沒準備好，事件直接丟棄；事件只是給畫面的提示，正式結果不靠它傳遞。
/// </summary>
public sealed class WebViewEventPublisher : IJetEventPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private CoreWebView2? _webView;
    private SynchronizationContext? _uiContext;

    /// <summary>Form1 於 WebView 就緒後、在 UI 執行緒上呼叫。</summary>
    public void Bind(CoreWebView2 webView, SynchronizationContext uiContext)
    {
        _webView = webView;
        _uiContext = uiContext;
    }

    public void Publish(string eventName, object payload)
    {
        var webView = _webView;
        var uiContext = _uiContext;

        if (webView is null || uiContext is null)
        {
            return;
        }

        var json = SerializeEnvelope(eventName, payload);
        uiContext.Post(_ =>
        {
            try
            {
                webView.PostWebMessageAsJson(json);
            }
            catch (Exception ex) when (ex is COMException or ObjectDisposedException or InvalidOperationException)
            {
                // 視窗關閉途中 WebView 已銷毀：fire-and-forget 事件直接丟棄
            }
        }, null);
    }

    /// <summary>
    /// 信封序列化抽成純靜態，讓 wire shape 可單元測試。請求編號在送出事件的當下讀，
    /// 所以要在事件所屬請求的非同步流程裡呼叫，不能延到切回 UI 執行緒之後。
    /// </summary>
    public static string SerializeEnvelope(string eventName, object payload)
    {
        return JsonSerializer.Serialize(
            new JetEventEnvelope(eventName, RequestCancellationRegistry.CurrentRequestId, payload),
            JsonOptions);
    }
}

internal sealed record JetEventEnvelope(
    string Event,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RequestId,
    object Data);
