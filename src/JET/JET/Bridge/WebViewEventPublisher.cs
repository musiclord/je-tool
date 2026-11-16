using System.Runtime.InteropServices;
using System.Text.Json;
using JET.Application;
using Microsoft.Web.WebView2.Core;

namespace JET.Bridge;

/// <summary>
/// IJetEventPublisher 的 WebView2 實作。事件的格式是 { event, data }，沒有 requestId，
/// 藉此和 action 的回應區分；不認得這個格式的前端會直接忽略。
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

    /// <summary>信封序列化抽成純靜態，讓 wire shape 可單元測試。</summary>
    public static string SerializeEnvelope(string eventName, object payload)
    {
        return JsonSerializer.Serialize(new JetEventEnvelope(eventName, payload), JsonOptions);
    }
}

internal sealed record JetEventEnvelope(
    string Event,
    object Data);
