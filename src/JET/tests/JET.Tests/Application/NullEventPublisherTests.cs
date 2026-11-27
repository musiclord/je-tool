using JET.Application;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// NullEventPublisher 是無 WebView 環境的 no-op event port；事件只承載 UX 進度，狀態不以事件為準。
/// </summary>
public sealed class NullEventPublisherTests
{
    [Fact]
    public void Publish_AnyEvent_DoesNotThrow()
    {
        var publisher = new NullEventPublisher();

        publisher.Publish("import.progress", new { rowsRead = 20_000 });
    }
}
