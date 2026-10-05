using System.Collections.Concurrent;
using System.Threading.Channels;
using JET.Application;
using JET.Bridge;

namespace Jet.BrowserHost;

/// <summary>
/// host→web 事件改由 Server-Sent Events 送到每個開著的頁面。信封沿用
/// <see cref="WebViewEventPublisher.SerializeEnvelope"/>，前端收到的形狀與 WebView2 相同。
/// 事件只是 UX 提示；頁面沒有連線時直接丟棄，與 WebView 尚未就緒時一致。
/// </summary>
internal sealed class EventBroker : IJetEventPublisher
{
    private readonly ConcurrentDictionary<Guid, Channel<string>> _clients = new();

    public void Publish(string eventName, object payload)
    {
        if (_clients.IsEmpty)
        {
            return;
        }

        var json = WebViewEventPublisher.SerializeEnvelope(eventName, payload);
        foreach (var client in _clients.Values)
        {
            client.Writer.TryWrite(json);
        }
    }

    public (Guid Id, ChannelReader<string> Reader) Subscribe()
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        var id = Guid.NewGuid();
        _clients[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_clients.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }
}
