using System.Runtime.CompilerServices;
using System.Text.Json;
using JET.Bridge;
using Microsoft.Web.WebView2.Core;
using Xunit;

namespace JET.Tests.Bridge;

/// <summary>
/// 事件信封的 wire shape（manifest「Host→Web 事件」：{ event, data }、無 requestId）。
/// 只測序列化——WebView2 marshal 屬 host chrome，走 GUI 手動驗證（jet-testing skill §1 硬邊界）。
/// </summary>
public sealed class WebViewEventPublisherTests
{
    [Fact]
    public void SerializeEnvelope_ProducesEventAndDataWithoutRequestId()
    {
        var json = WebViewEventPublisher.SerializeEnvelope(
            "import.progress",
            new { kind = "gl", fileName = "je.xlsx", sheetName = (string?)null, rowsRead = 20_000 });

        var root = JsonDocument.Parse(json).RootElement;

        Assert.Equal("import.progress", root.GetProperty("event").GetString());
        Assert.False(root.TryGetProperty("requestId", out _)); // 與 response 信封的區隔鍵

        var data = root.GetProperty("data");
        Assert.Equal("gl", data.GetProperty("kind").GetString());
        Assert.Equal("je.xlsx", data.GetProperty("fileName").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("sheetName").ValueKind);
        Assert.Equal(20_000, data.GetProperty("rowsRead").GetInt32());
    }

    [Fact]
    public void Publish_BeforeBind_IsSilentNoOp()
    {
        var publisher = new WebViewEventPublisher();

        // 未綁定（WebView 尚未就緒）：事件是 UX 提示，不得丟例外
        publisher.Publish("import.progress", new { rowsRead = 1 });
    }

    [Fact]
    public void Publish_AfterBind_PostsSerializedEnvelopeToUiContext()
    {
        var webView = CreateUninitializedWebView();
        var uiContext = new CapturingSynchronizationContext();
        var publisher = new WebViewEventPublisher();

        publisher.Bind(webView, uiContext);
        publisher.Publish("import.progress", new { rowsRead = 2 });

        Assert.NotNull(uiContext.Callback);
        Assert.Null(uiContext.State);
    }

    private static CoreWebView2 CreateUninitializedWebView()
    {
        // Bridge 層只需驗證 marshal 到 SynchronizationContext；不可啟動 WebView2 GUI。
        return (CoreWebView2)RuntimeHelpers.GetUninitializedObject(typeof(CoreWebView2));
    }



    private sealed class CapturingSynchronizationContext : SynchronizationContext
    {
        public SendOrPostCallback? Callback { get; private set; }

        public object? State { get; private set; }

        public override void Post(SendOrPostCallback d, object? state)
        {
            Callback = d;
            State = state;
        }
    }

}
