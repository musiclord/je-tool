using System.Runtime.InteropServices;
using System.Text.Json;
using JET.Application;
using Microsoft.Web.WebView2.Core;

namespace JET.Bridge;

/// <summary>
/// IJetEventPublisher 的 WebView2 實作：事件信封 { event, data }（無 requestId，
/// 與 response 信封區隔；不認識此形狀的前端會安全忽略）。
/// Publish 可能來自背景緒（匯入在 Task.Run 內），PostWebMessageAsJson 必須在
/// UI 執行緒呼叫——以建構 WebView 的 SynchronizationContext marshal。
/// 未 Bind 前（WebView 尚未就緒）事件靜默丟棄：事件是 UX 提示，不承載權威。
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

    /// <summary>信封序列化抽成純靜態，讓 wire shape 可單元測試（WebView2 marshal 走 GUI 手動驗證）。</summary>
    public static string SerializeEnvelope(string eventName, object payload)
    {
        return JsonSerializer.Serialize(new JetEventEnvelope(eventName, payload), JsonOptions);
    }
}

internal sealed record JetEventEnvelope(
    string Event,
    object Data);
